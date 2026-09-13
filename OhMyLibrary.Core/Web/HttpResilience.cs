using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OhMyLibrary.Core.Web;

/// <summary>How a request ended, from the caller's point of view.</summary>
internal enum FetchOutcome
{
    /// <summary>The body was fetched and parsed.</summary>
    Success,

    /// <summary>Steam refused the request with 401/403 — typically a private profile or a rejected key.</summary>
    Denied,

    /// <summary>The request could not be completed; the caller degrades to an empty result.</summary>
    Failed,
}

/// <summary>Outcome plus payload of a single JSON GET.</summary>
/// <typeparam name="T">Deserialised body type.</typeparam>
/// <param name="Outcome">How the request ended.</param>
/// <param name="Value">The body, or <see langword="null"/> unless <paramref name="Outcome"/> is
/// <see cref="FetchOutcome.Success"/>.</param>
internal readonly record struct FetchResult<T>(FetchOutcome Outcome, T? Value);

/// <summary>
/// The one place where the Steam clients touch the network: a bounded, jittered retry around a
/// JSON GET that never throws for an expected failure.
/// </summary>
/// <remarks>
/// Only <see cref="OperationCanceledException"/> from the caller's own token escapes. HTTP 429 and
/// 5xx are retried up to <see cref="MaxAttempts"/> times, 401/403 are reported as
/// <see cref="FetchOutcome.Denied"/> without a retry, and everything else becomes
/// <see cref="FetchOutcome.Failed"/>. Nothing logged here can contain the API key: URIs are never
/// logged and exception text is passed through <see cref="Redact"/>.
/// </remarks>
internal static partial class HttpResilience
{
    /// <summary>Total attempts, including the first.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Per-attempt budget, covering headers and body.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// GETs <paramref name="uri"/> and deserialises the body, retrying transient failures.
    /// </summary>
    /// <typeparam name="T">Body type.</typeparam>
    /// <param name="http">Client to send on; <paramref name="uri"/> may be relative to its base address.</param>
    /// <param name="uri">Target, key included. It is never written to a log.</param>
    /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="logger">Sink for diagnostics.</param>
    /// <param name="operation">Key-free description used in log lines, for example <c>GetOwnedGames</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<FetchResult<T>> GetJsonAsync<T>(
        HttpClient http,
        Uri uri,
        JsonTypeInfo<T> typeInfo,
        ILogger logger,
        string operation,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            string reason;

            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(RequestTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    await using var body = await response.Content.ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);
                    var value = await JsonSerializer.DeserializeAsync(body, typeInfo, attemptCts.Token).ConfigureAwait(false);
                    return new FetchResult<T>(FetchOutcome.Success, value);
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    logger.LogDebug("{Operation}: Steam refused the request (HTTP {Status}).", operation, (int)response.StatusCode);
                    return new FetchResult<T>(FetchOutcome.Denied, default);
                }

                if (!IsTransient(response.StatusCode))
                {
                    logger.LogWarning("{Operation} failed with HTTP {Status}.", operation, (int)response.StatusCode);
                    return new FetchResult<T>(FetchOutcome.Failed, default);
                }

                retryAfter = GetRetryAfter(response);
                reason = $"HTTP {(int)response.StatusCode}";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                reason = $"timed out after {RequestTimeout.TotalSeconds:0}s";
            }
            catch (HttpRequestException ex)
            {
                reason = Redact(ex.Message);
            }
            catch (IOException ex)
            {
                reason = Redact(ex.Message);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("{Operation}: the response body could not be parsed ({Reason}).", operation, Redact(ex.Message));
                return new FetchResult<T>(FetchOutcome.Failed, default);
            }

            if (attempt >= MaxAttempts)
            {
                logger.LogWarning("{Operation} gave up after {Attempts} attempts ({Reason}).", operation, MaxAttempts, reason);
                return new FetchResult<T>(FetchOutcome.Failed, default);
            }

            var delay = retryAfter ?? Backoff(attempt);
            logger.LogDebug(
                "{Operation}: attempt {Attempt} failed ({Reason}); retrying in {DelayMs} ms.",
                operation,
                attempt,
                reason,
                (int)delay.TotalMilliseconds);

            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Replaces the value of any <c>key=</c> query parameter with <c>***</c> so that a message
    /// echoing a URI can never leak the API key.
    /// </summary>
    /// <param name="message">Text to sanitise.</param>
    public static string Redact(string? message) =>
        string.IsNullOrEmpty(message) ? string.Empty : ApiKeyPattern().Replace(message, "key=***");

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)status >= 500;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is not { } value || value <= TimeSpan.Zero)
        {
            return null;
        }

        return value > MaxDelay ? MaxDelay : value;
    }

    private static TimeSpan Backoff(int attempt)
    {
        var delay = BaseDelay * Math.Pow(2, attempt - 1) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        return delay > MaxDelay ? MaxDelay : delay;
    }

    [GeneratedRegex("""key=[^&\s"']*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyPattern();
}
