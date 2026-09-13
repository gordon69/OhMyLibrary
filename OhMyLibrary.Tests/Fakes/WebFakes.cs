using System.Net;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Services;

namespace OhMyLibrary.Tests.Fakes;

/// <summary>
/// An <see cref="ISteamWebApiClient"/> whose every answer is scripted by the test.
/// </summary>
public sealed class FakeSteamWebApiClient : ISteamWebApiClient
{
    /// <summary>Owned-games answers, keyed by SteamID64; an id that is absent answers <see cref="OwnedGamesResult.Hidden"/>.</summary>
    public Dictionary<ulong, OwnedGamesResult> OwnedGames { get; } = [];

    /// <summary>The friend list to return.</summary>
    public List<FriendSummary> Friends { get; } = [];

    /// <summary>Player summaries, keyed by SteamID64.</summary>
    public Dictionary<ulong, FriendSummary> Summaries { get; } = [];

    /// <summary>SteamID64s <see cref="GetOwnedGamesAsync"/> was called for, in call order.</summary>
    public List<ulong> OwnedGamesCalls { get; } = [];

    /// <summary>Whether an API key is configured.</summary>
    public bool IsConfigured { get; set; } = true;

    /// <inheritdoc />
    public Task<OwnedGamesResult> GetOwnedGamesAsync(ulong steamId64, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (OwnedGamesCalls)
        {
            OwnedGamesCalls.Add(steamId64);
        }

        return Task.FromResult(
            !IsConfigured || !OwnedGames.TryGetValue(steamId64, out var result)
                ? OwnedGamesResult.Hidden
                : result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FriendSummary>> GetFriendListAsync(ulong steamId64, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<FriendSummary>>(IsConfigured ? [.. Friends] : []);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FriendSummary>> GetPlayerSummariesAsync(
        IEnumerable<ulong> steamIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var summaries = steamIds
            .Where(Summaries.ContainsKey)
            .Select(id => Summaries[id])
            .ToList();

        return Task.FromResult<IReadOnlyList<FriendSummary>>(summaries);
    }

    /// <inheritdoc />
    public Task<ulong?> ResolveVanityUrlAsync(string vanityName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<ulong?>(null);
    }
}

/// <summary>An <see cref="ITagService"/> that records refreshes and serves canned names.</summary>
public sealed class FakeTagService : ITagService
{
    /// <summary>Tags to serve.</summary>
    public List<TagRef> Tags { get; } = [];

    /// <summary>Genres to serve.</summary>
    public List<GenreRef> Genres { get; } = [];

    /// <summary>How many times <see cref="RefreshNamesAsync"/> has run.</summary>
    public int RefreshCalls { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<TagRef>> GetAllTagsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TagRef>>([.. Tags]);

    /// <inheritdoc />
    public Task<IReadOnlyList<GenreRef>> GetAllGenresAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GenreRef>>([.. Genres]);

    /// <inheritdoc />
    public Task RefreshNamesAsync(bool force, CancellationToken ct = default)
    {
        RefreshCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a script and records what was asked for.
/// </summary>
/// <remarks>
/// Responses are consumed in order; once the script runs out the last response repeats, so a retry
/// test can end with a success and a chunking test can queue one answer per batch.
/// </remarks>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();
    private Func<HttpRequestMessage, HttpResponseMessage>? _last;

    /// <summary>Absolute URIs of every request, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Queues a JSON response.</summary>
    /// <param name="json">Response body.</param>
    /// <param name="status">Status code to answer with.</param>
    public StubHttpMessageHandler RespondJson(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _script.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        return this;
    }

    /// <summary>Queues a bare status code, as Steam answers a refused or rate-limited request.</summary>
    /// <param name="status">Status code to answer with.</param>
    public StubHttpMessageHandler RespondStatus(HttpStatusCode status)
    {
        _script.Enqueue(_ => new HttpResponseMessage(status));
        return this;
    }

    /// <summary>Queues a transport failure.</summary>
    public StubHttpMessageHandler RespondTransportFailure()
    {
        _script.Enqueue(_ => throw new HttpRequestException("The stub refused to connect."));
        return this;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Requests.Add(request.RequestUri?.ToString() ?? string.Empty);

        if (_script.Count > 0)
        {
            _last = _script.Dequeue();
        }

        if (_last is null)
        {
            throw new InvalidOperationException("The stub handler was called without a scripted response.");
        }

        return Task.FromResult(_last(request));
    }
}

/// <summary>An <see cref="ITagDataClient"/> that answers from a canned table per language.</summary>
public sealed class FakeTagDataClient : ITagDataClient
{
    /// <summary>Tags to serve, keyed by language; a language that is absent answers empty.</summary>
    public Dictionary<string, List<TagRef>> TagsByLanguage { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The languages that were asked for, in call order.</summary>
    public List<string> Requests { get; } = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<TagRef>> GetPopularTagsAsync(string language, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Add(language);

        return Task.FromResult<IReadOnlyList<TagRef>>(
            TagsByLanguage.TryGetValue(language, out var tags) ? [.. tags] : []);
    }
}
