using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Web.Dto;

namespace OhMyLibrary.Core.Web;

/// <summary>
/// Typed <see cref="HttpClient"/> over the public Steam Web API endpoints the launcher needs.
/// </summary>
/// <remarks>
/// <para>
/// No API key is a supported configuration, not a fault: <see cref="IsConfigured"/> is then
/// <see langword="false"/> and every call returns an empty result immediately, without a request and
/// without an error-level log line. Installed games keep working from local files.
/// </para>
/// <para>
/// The key is appended as the last query parameter and no request URI is ever logged; any text that
/// might echo one goes through <see cref="HttpResilience.Redact"/> first.
/// </para>
/// </remarks>
public sealed class SteamWebApiClient : ISteamWebApiClient
{
    /// <summary>Root of the Web API. Assigned to the typed client when none was configured.</summary>
    public static readonly Uri BaseAddress = new("https://api.steampowered.com/");

    /// <summary>Valve's hard limit on ids per <c>GetPlayerSummaries</c> call.</summary>
    public const int PlayerSummariesBatchSize = 100;

    private readonly HttpClient _http;
    private readonly ILogger<SteamWebApiClient> _logger;
    private readonly string _apiKey;

    /// <summary>Creates the client.</summary>
    /// <param name="httpClient">Typed client; its base address defaults to <see cref="BaseAddress"/>.</param>
    /// <param name="options">Steam settings, read once — the API key is not re-read per call.</param>
    /// <param name="logger">Sink for diagnostics.</param>
    public SteamWebApiClient(HttpClient httpClient, IOptions<SteamOptions> options, ILogger<SteamWebApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _http = httpClient;
        _logger = logger;
        _http.BaseAddress ??= BaseAddress;

        var key = options.Value.ApiKey;
        _apiKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();

        if (_apiKey.Length == 0)
        {
            _logger.LogInformation("No Steam Web API key is configured; owned games and friends stay empty.");
        }
    }

    /// <inheritdoc />
    public bool IsConfigured => _apiKey.Length != 0;

    /// <inheritdoc />
    public async Task<OwnedGamesResult> GetOwnedGamesAsync(ulong steamId64, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return OwnedGamesResult.Hidden;
        }

        var uri = BuildUri(
            "IPlayerService/GetOwnedGames/v1/",
            $"steamid={steamId64}&include_appinfo=1&include_played_free_games=1");

        var fetch = await HttpResilience
            .GetJsonAsync(_http, uri, SteamJsonContext.Default.OwnedGamesEnvelope, _logger, "GetOwnedGames", ct)
            .ConfigureAwait(false);

        if (fetch.Outcome != FetchOutcome.Success || fetch.Value?.Response is not { } response)
        {
            return OwnedGamesResult.Hidden;
        }

        // A private library answers with an empty "response" object rather than an error, so the
        // absence of both the list and the count — not an empty list — is what "hidden" looks like.
        if (response.Games is null && response.GameCount is null)
        {
            _logger.LogDebug("GetOwnedGames: account {SteamId} keeps its game list private.", steamId64);
            return OwnedGamesResult.Hidden;
        }

        if (response.Games is not { Count: > 0 } games)
        {
            return OwnedGamesResult.Empty;
        }

        var owned = new List<OwnedGame>(games.Count);
        foreach (var game in games)
        {
            if (game.AppId <= 0)
            {
                continue;
            }

            owned.Add(new OwnedGame(
                game.AppId,
                string.IsNullOrWhiteSpace(game.Name) ? null : game.Name,
                game.PlaytimeForever,
                game.Playtime2Weeks,
                FromUnixSeconds(game.RtimeLastPlayed),
                string.IsNullOrWhiteSpace(game.ImgIconUrl) ? null : game.ImgIconUrl));
        }

        return new OwnedGamesResult(true, owned);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FriendSummary>> GetFriendListAsync(ulong steamId64, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return [];
        }

        var uri = BuildUri("ISteamUser/GetFriendList/v1/", $"steamid={steamId64}&relationship=friend");

        var fetch = await HttpResilience
            .GetJsonAsync(_http, uri, SteamJsonContext.Default.FriendListEnvelope, _logger, "GetFriendList", ct)
            .ConfigureAwait(false);

        if (fetch.Outcome == FetchOutcome.Denied)
        {
            _logger.LogDebug("GetFriendList: account {SteamId} keeps its friend list private.", steamId64);
            return [];
        }

        if (fetch.Outcome != FetchOutcome.Success || fetch.Value?.FriendsList?.Friends is not { Count: > 0 } friends)
        {
            return [];
        }

        var summaries = new List<FriendSummary>(friends.Count);
        foreach (var friend in friends)
        {
            if (!ulong.TryParse(friend.SteamId, out var id) || id == 0)
            {
                continue;
            }

            summaries.Add(new FriendSummary(
                id,
                PersonaName: string.Empty,
                AvatarUrl: null,
                ProfileUrl: null,
                State: PersonaState.Offline,
                FriendSince: FromUnixSeconds(friend.FriendSince),
                GameListVisible: true,
                LastSyncedUtc: null));
        }

        return summaries;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FriendSummary>> GetPlayerSummariesAsync(
        IEnumerable<ulong> steamIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(steamIds);

        if (!IsConfigured)
        {
            return [];
        }

        var ids = new List<ulong>();
        var seen = new HashSet<ulong>();
        foreach (var id in steamIds)
        {
            if (id != 0 && seen.Add(id))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            return [];
        }

        var summaries = new List<FriendSummary>(ids.Count);
        for (var offset = 0; offset < ids.Count; offset += PlayerSummariesBatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = ids.GetRange(offset, Math.Min(PlayerSummariesBatchSize, ids.Count - offset));
            var uri = BuildUri("ISteamUser/GetPlayerSummaries/v2/", $"steamids={Join(batch)}");

            var fetch = await HttpResilience
                .GetJsonAsync(_http, uri, SteamJsonContext.Default.PlayerSummariesEnvelope, _logger, "GetPlayerSummaries", ct)
                .ConfigureAwait(false);

            // One bad batch must not lose the batches that did come back.
            if (fetch.Outcome != FetchOutcome.Success || fetch.Value?.Response?.Players is not { Count: > 0 } players)
            {
                continue;
            }

            foreach (var player in players)
            {
                if (!ulong.TryParse(player.SteamId, out var id) || id == 0)
                {
                    continue;
                }

                summaries.Add(new FriendSummary(
                    id,
                    PersonaName: player.PersonaName ?? string.Empty,
                    AvatarUrl: FirstNonEmpty(player.AvatarFull, player.AvatarMedium, player.Avatar),
                    ProfileUrl: FirstNonEmpty(player.ProfileUrl),
                    State: ToPersonaState(player.PersonaState),
                    FriendSince: null,
                    GameListVisible: player.CommunityVisibilityState is null or 3,
                    LastSyncedUtc: null));
            }
        }

        return summaries;
    }

    /// <inheritdoc />
    public async Task<ulong?> ResolveVanityUrlAsync(string vanityName, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(vanityName))
        {
            return null;
        }

        var uri = BuildUri("ISteamUser/ResolveVanityURL/v1/", $"vanityurl={Uri.EscapeDataString(vanityName.Trim())}");

        var fetch = await HttpResilience
            .GetJsonAsync(_http, uri, SteamJsonContext.Default.ResolveVanityUrlEnvelope, _logger, "ResolveVanityURL", ct)
            .ConfigureAwait(false);

        if (fetch.Outcome != FetchOutcome.Success || fetch.Value?.Response is not { Success: 1 } response)
        {
            return null;
        }

        return ulong.TryParse(response.SteamId, out var id) && id != 0 ? id : null;
    }

    /// <summary>
    /// Builds a request URI with the API key appended last, so a URI that is truncated anywhere
    /// loses the key before it loses anything else.
    /// </summary>
    private Uri BuildUri(string path, string query) =>
        new($"{path}?{query}&key={Uri.EscapeDataString(_apiKey)}", UriKind.Relative);

    private static string Join(List<ulong> ids)
    {
        var builder = new StringBuilder(ids.Count * 18);
        foreach (var id in ids)
        {
            if (builder.Length != 0)
            {
                builder.Append(',');
            }

            builder.Append(id);
        }

        return builder.ToString();
    }

    private static DateTimeOffset? FromUnixSeconds(long seconds) =>
        seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static PersonaState ToPersonaState(int value) =>
        Enum.IsDefined((PersonaState)value) ? (PersonaState)value : PersonaState.Offline;
}
