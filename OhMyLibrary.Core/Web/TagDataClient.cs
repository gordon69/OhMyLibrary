using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Web.Dto;

namespace OhMyLibrary.Core.Web;

/// <summary>
/// Typed <see cref="HttpClient"/> for <c>store.steampowered.com/tagdata/populartags/&lt;language&gt;</c>,
/// the only working source of store tag names now that <c>localization.vdf</c> is gone.
/// </summary>
/// <remarks>
/// No API key is involved. The endpoint answers with a bare JSON array of
/// <c>{"tagid":…,"name":…}</c> and changes rarely, so callers cache it for days. A language the
/// store does not know falls back to <see cref="DefaultLanguage"/> rather than returning nothing.
/// </remarks>
public sealed class TagDataClient : ITagDataClient
{
    /// <summary>Root of the store site. Assigned to the typed client when none was configured.</summary>
    public static readonly Uri BaseAddress = new("https://store.steampowered.com/");

    /// <summary>The language every other language falls back to.</summary>
    public const string DefaultLanguage = "english";

    private const int MaxLanguageLength = 32;

    private readonly HttpClient _http;
    private readonly ILogger<TagDataClient> _logger;
    private readonly string _configuredLanguage;

    /// <summary>Creates the client.</summary>
    /// <param name="httpClient">Typed client; its base address defaults to <see cref="BaseAddress"/>.</param>
    /// <param name="options">Steam settings; <see cref="SteamOptions.Language"/> is used when the
    /// caller passes no language of its own.</param>
    /// <param name="logger">Sink for diagnostics.</param>
    public TagDataClient(HttpClient httpClient, IOptions<SteamOptions> options, ILogger<TagDataClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _http = httpClient;
        _logger = logger;
        _http.BaseAddress ??= BaseAddress;
        _configuredLanguage = Sanitise(options.Value.Language);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagRef>> GetPopularTagsAsync(string language, CancellationToken ct = default)
    {
        var requested = string.IsNullOrWhiteSpace(language) ? _configuredLanguage : Sanitise(language);

        var tags = await FetchAsync(requested, ct).ConfigureAwait(false);
        if (tags.Count != 0 || string.Equals(requested, DefaultLanguage, StringComparison.Ordinal))
        {
            return tags;
        }

        _logger.LogDebug("No tag names came back for '{Language}'; falling back to {Fallback}.", requested, DefaultLanguage);
        return await FetchAsync(DefaultLanguage, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TagRef>> FetchAsync(string language, CancellationToken ct)
    {
        var uri = new Uri($"tagdata/populartags/{language}", UriKind.Relative);

        var fetch = await HttpResilience
            .GetJsonAsync(_http, uri, SteamJsonContext.Default.ListStoreTagDto, _logger, $"populartags/{language}", ct)
            .ConfigureAwait(false);

        if (fetch.Outcome != FetchOutcome.Success || fetch.Value is not { Count: > 0 } payload)
        {
            return [];
        }

        var tags = new List<TagRef>(payload.Count);
        var seen = new HashSet<int>(payload.Count);
        foreach (var tag in payload)
        {
            if (tag.TagId <= 0 || string.IsNullOrWhiteSpace(tag.Name) || !seen.Add(tag.TagId))
            {
                continue;
            }

            tags.Add(new TagRef(tag.TagId, tag.Name.Trim()));
        }

        _logger.LogDebug("Fetched {Count} tag names for {Language}.", tags.Count, language);
        return tags;
    }

    /// <summary>
    /// Reduces a language to something safe to concatenate into a URL path. Anything unexpected
    /// becomes <see cref="DefaultLanguage"/>, which also keeps a configured value from escaping
    /// the <c>tagdata</c> path.
    /// </summary>
    private static string Sanitise(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return DefaultLanguage;
        }

        var trimmed = language.Trim();
        if (trimmed.Length > MaxLanguageLength)
        {
            return DefaultLanguage;
        }

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetter(c) && c != '-' && c != '_')
            {
                return DefaultLanguage;
            }
        }

        return trimmed.ToLowerInvariant();
    }
}
