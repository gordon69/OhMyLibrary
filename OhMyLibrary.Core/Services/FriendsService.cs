using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;

namespace OhMyLibrary.Core.Services;

/// <summary>
/// The friends list and which of them own a given game.
/// </summary>
/// <remarks>
/// The per-friend owned-games fan-out is the most rate-limit-sensitive thing the app does, so it runs
/// at most <see cref="MaxConcurrentFriendRequests"/> requests at a time, is gated by
/// <c>Sync:FriendsMaxAgeHours</c> and persists each friend as it finishes — cancelling halfway leaves
/// everything fetched so far in the database. A friend whose library is hidden is stored with
/// <see cref="FriendSummary.GameListVisible"/> <see langword="false"/> and <b>no</b> game rows, which
/// is not the same as owning nothing.
/// </remarks>
public sealed class FriendsService : IFriendsService
{
    /// <summary>How many friend libraries are fetched at once.</summary>
    public const int MaxConcurrentFriendRequests = 4;

    /// <summary>Valve's per-call limit for <c>GetPlayerSummaries</c>.</summary>
    private const int SummaryChunkSize = 100;

    private readonly ISteamWebApiClient _api;
    private readonly ISteamPathResolver _paths;
    private readonly IFriendRepository _friends;
    private readonly ISyncMetaRepository _syncMeta;
    private readonly SteamOptions _steamOptions;
    private readonly SyncOptions _syncOptions;
    private readonly ILogger<FriendsService> _logger;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>
    /// Serialises writes during the fan-out. The requests run concurrently; the SQLite writes behind
    /// them do not, which keeps the repositories free of any concurrency assumptions.
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Creates the service.</summary>
    /// <param name="api">Steam Web API client.</param>
    /// <param name="paths">Supplies the local accounts used to auto-detect the SteamID64.</param>
    /// <param name="friends">Friend and friend-game persistence.</param>
    /// <param name="syncMeta">Holds the friends last-run timestamp.</param>
    /// <param name="steamOptions">Steam account settings.</param>
    /// <param name="syncOptions">Cache lifetimes.</param>
    /// <param name="logger">Log sink; a per-friend failure is logged and the fan-out continues.</param>
    public FriendsService(
        ISteamWebApiClient api,
        ISteamPathResolver paths,
        IFriendRepository friends,
        ISyncMetaRepository syncMeta,
        IOptions<SteamOptions> steamOptions,
        IOptions<SyncOptions> syncOptions,
        ILogger<FriendsService> logger)
    {
        _api = api;
        _paths = paths;
        _friends = friends;
        _syncMeta = syncMeta;
        _steamOptions = steamOptions.Value;
        _syncOptions = syncOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FriendSummary>> GetFriendsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _friends.GetAllAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the stored friends list.");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FriendSummary>> GetOwnersOfAsync(int appId, CancellationToken ct = default)
    {
        if (appId <= 0)
        {
            return [];
        }

        try
        {
            return await _friends.GetOwnersOfAsync(appId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the friends who own {AppId}.", appId);
            return [];
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Cancelling is honoured between requests and lets <see cref="OperationCanceledException"/> out,
    /// which is the one exception this layer propagates. Everything fetched before the cancellation
    /// has already been written.
    /// </remarks>
    public async Task RefreshAsync(bool force, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!_api.IsConfigured)
        {
            progress?.Report("No Steam Web API key is configured, so the friends list stays empty.");
            return;
        }

        var steamId = ResolveSteamId64();
        if (steamId is null)
        {
            progress?.Report("No SteamID64 is configured and none could be detected.");
            return;
        }

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && await IsFreshAsync(ct).ConfigureAwait(false))
            {
                progress?.Report("The friends list is up to date.");
                return;
            }

            progress?.Report("Fetching the friends list...");
            var friends = await _api.GetFriendListAsync(steamId.Value, ct).ConfigureAwait(false);
            if (friends.Count == 0)
            {
                // A private profile answers GetFriendList with 401/403, which the client reports as
                // an empty list. Do not stamp the sync time, so the next attempt retries.
                progress?.Report("The friends list is unavailable: the profile may be private.");
                _logger.LogInformation("GetFriendList returned nothing for {SteamId}.", steamId.Value);
                return;
            }

            await WriteAsync(() => _friends.UpsertFriendsAsync(friends, ct), ct).ConfigureAwait(false);

            var withProfiles = await FetchSummariesAsync(friends, progress, ct).ConfigureAwait(false);
            await FanOutOwnedGamesAsync(withProfiles, progress, ct).ConfigureAwait(false);

            await _syncMeta
                .SetLastRunAsync(SyncKeys.Friends, DateTimeOffset.UtcNow, steamId.Value.ToString(), ct)
                .ConfigureAwait(false);

            progress?.Report($"Done: {withProfiles.Count} friends.");
        }
        catch (OperationCanceledException)
        {
            progress?.Report("Cancelled; the friends fetched so far were saved.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The friends refresh failed.");
            progress?.Report("The friends refresh failed; the stored data was kept.");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Fills in personas, avatars and profile URLs in chunks of
    /// <see cref="SummaryChunkSize"/>, carrying <c>friend_since</c> over from the friend list because
    /// <c>GetPlayerSummaries</c> does not report it.
    /// </summary>
    private async Task<IReadOnlyList<FriendSummary>> FetchSummariesAsync(
        IReadOnlyList<FriendSummary> friends,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var friendSince = friends
            .GroupBy(static f => f.SteamId64)
            .ToDictionary(static g => g.Key, static g => g.First().FriendSince);

        var merged = new List<FriendSummary>(friends.Count);
        var chunks = friends.Select(static f => f.SteamId64).Distinct().Chunk(SummaryChunkSize).ToArray();

        for (var i = 0; i < chunks.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Fetching profiles: batch {i + 1} of {chunks.Length}...");

            IReadOnlyList<FriendSummary> summaries;
            try
            {
                summaries = await _api.GetPlayerSummariesAsync(chunks[i], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A GetPlayerSummaries batch failed; those friends keep their stored profile.");
                continue;
            }

            var resolved = summaries
                .Select(s => friendSince.TryGetValue(s.SteamId64, out var since) && s.FriendSince is null
                    ? s with { FriendSince = since }
                    : s)
                .ToArray();

            if (resolved.Length > 0)
            {
                await WriteAsync(() => _friends.UpsertFriendsAsync(resolved, ct), ct).ConfigureAwait(false);
                merged.AddRange(resolved);
            }
        }

        // Friends the summaries call omitted are still friends; keep them so their library is fetched.
        var described = merged.Select(static f => f.SteamId64).ToHashSet();
        merged.AddRange(friends.Where(f => !described.Contains(f.SteamId64)));

        return merged;
    }

    /// <summary>
    /// One <c>GetOwnedGames</c> call per friend, at most
    /// <see cref="MaxConcurrentFriendRequests"/> in flight, persisting each result as it lands.
    /// </summary>
    private async Task FanOutOwnedGamesAsync(
        IReadOnlyList<FriendSummary> friends,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        using var throttle = new SemaphoreSlim(MaxConcurrentFriendRequests, MaxConcurrentFriendRequests);
        var total = friends.Count;
        var completed = 0;

        var tasks = friends.Select(async (FriendSummary friend) =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await SyncFriendLibraryAsync(friend, ct).ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
                progress?.Report($"{Interlocked.Increment(ref completed)} of {total} friends");
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task SyncFriendLibraryAsync(FriendSummary friend, CancellationToken ct)
    {
        OwnedGamesResult owned;
        try
        {
            owned = await _api.GetOwnedGamesAsync(friend.SteamId64, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch the library of {SteamId}.", friend.SteamId64);
            return;
        }

        // A hidden library must never be written as an empty one, so the game rows are replaced only
        // when Steam actually returned a list.
        if (owned.Visible)
        {
            var appIds = owned.Games.Select(static g => g.AppId).Distinct().ToArray();
            await WriteAsync(() => _friends.ReplaceFriendGamesAsync(friend.SteamId64, appIds, ct), ct)
                .ConfigureAwait(false);
        }

        // The visibility itself is a result worth remembering either way, so the UI can say "hidden"
        // instead of "no games" and the next refresh is not tempted to retry immediately.
        var stamped = friend with
        {
            AvatarUrl = null,
            ProfileUrl = null,
            GameListVisible = owned.Visible,
            LastSyncedUtc = DateTimeOffset.UtcNow,
        };

        await WriteAsync(() => _friends.UpsertFriendsAsync([stamped], ct), ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(Func<Task> write, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A friends write failed; the refresh continues with the rest.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private ulong? ResolveSteamId64()
    {
        try
        {
            return SteamIdentity.Resolve(_steamOptions, _paths.GetLocalUsers());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve the SteamID64 to use.");
            return null;
        }
    }

    private async Task<bool> IsFreshAsync(CancellationToken ct)
    {
        if (_syncOptions.FriendsMaxAgeHours <= 0)
        {
            return false;
        }

        try
        {
            var last = await _syncMeta.GetLastRunAsync(SyncKeys.Friends, ct).ConfigureAwait(false);
            return last is not null
                && DateTimeOffset.UtcNow - last.Value < TimeSpan.FromHours(_syncOptions.FriendsMaxAgeHours);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the friends sync state; treating it as stale.");
            return false;
        }
    }
}
