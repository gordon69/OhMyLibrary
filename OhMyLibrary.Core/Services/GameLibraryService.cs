using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;

namespace OhMyLibrary.Core.Services;

/// <summary>
/// Folds local manifests, the owned-games list, <c>appinfo.vdf</c> metadata, cached art, collections
/// and friend ownership into the <see cref="GameEntry"/> rows the UI binds to.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here throws for a degraded environment. Missing Steam, an unplugged library drive, a
/// private profile and an absent API key all reduce what <see cref="GetGamesAsync"/> returns and are
/// explained by <see cref="GetStatusAsync"/>. Only <see cref="OperationCanceledException"/> escapes.
/// </para>
/// <para>
/// Which rows reach the grid is decided here and nowhere else, by <see cref="BelongsOnGrid"/>, for
/// the bulk read and the single-app read alike. A caller that filters the result again is a second
/// copy of the rule waiting to disagree with this one.
/// </para>
/// </remarks>
public sealed class GameLibraryService : IGameLibraryService
{
    /// <summary>
    /// <c>common/type</c> values that are never a playable library entry. Compared case-insensitively
    /// because Valve is inconsistent about the casing. An app of one of these types is still shown
    /// when it is installed, because the user clearly has it and may want to launch or remove it.
    /// </summary>
    private static readonly FrozenSet<string> NonGameTypes = new[]
    {
        "dlc", "tool", "config", "music", "demo", "application",
        "video", "series", "episode", "media", "hardware", "franchise",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runtimes and redistributables that appear as ordinary apps both on disk and in the owned list.
    /// These are dropped unconditionally — including while installed, which they almost always are.
    /// </summary>
    private static readonly FrozenSet<int> RedistributableAppIds = new[]
    {
        228980,  // Steamworks Common Redistributables
        1070560, // Steam Linux Runtime
        1391110, // Steam Linux Runtime - Soldier
        1493710, // Proton Experimental
        1628350, // Steam Linux Runtime 3.0 (sniper)
        1887720, // Proton 7.0
        2180100, // Proton Hotfix
        2348590, // Proton 8.0
        2805730, // Proton 9.0
    }.ToFrozenSet();

    /// <summary>
    /// How many app ids a single log line will name before it reports only how many there were.
    /// </summary>
    private const int MaxLoggedAppIds = 32;

    private readonly ISteamPathResolver _paths;
    private readonly ILibraryFoldersReader _libraryFolders;
    private readonly IAcfReader _acf;
    private readonly IAppInfoReader _appInfo;
    private readonly ILibraryAssetResolver _assets;
    private readonly ISteamWebApiClient _api;
    private readonly ITagService _tags;
    private readonly IGameRepository _games;
    private readonly ISyncMetaRepository _syncMeta;
    private readonly SteamOptions _steamOptions;
    private readonly SyncOptions _syncOptions;
    private readonly ILogger<GameLibraryService> _logger;

    private readonly SemaphoreSlim _localGate = new(1, 1);
    private readonly SemaphoreSlim _remoteGate = new(1, 1);
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    private volatile IReadOnlyList<GameEntry>? _cache;
    private volatile IReadOnlyDictionary<int, TransferProgress> _liveProgress =
        FrozenDictionary<int, TransferProgress>.Empty;
    private volatile string? _lastError;
    private volatile bool _ownedListAvailable;

    /// <summary>Creates the service.</summary>
    /// <param name="paths">Locates Steam, its reachable library folders and the local accounts.</param>
    /// <param name="libraryFolders">Reads the <i>declared</i> folders, unreachable ones included, for the status report.</param>
    /// <param name="acf">Parses <c>appmanifest_*.acf</c>.</param>
    /// <param name="appInfo">Parses <c>appcache/appinfo.vdf</c>.</param>
    /// <param name="assets">Finds cached library art.</param>
    /// <param name="api">Steam Web API client.</param>
    /// <param name="tags">Tag and genre name resolution.</param>
    /// <param name="games">Game row persistence.</param>
    /// <param name="syncMeta">Last-run timestamps that drive the cache lifetimes.</param>
    /// <param name="steamOptions">Steam account and installation settings.</param>
    /// <param name="syncOptions">Cache lifetimes.</param>
    /// <param name="logger">Log sink; every non-fatal failure is logged rather than thrown.</param>
    public GameLibraryService(
        ISteamPathResolver paths,
        ILibraryFoldersReader libraryFolders,
        IAcfReader acf,
        IAppInfoReader appInfo,
        ILibraryAssetResolver assets,
        ISteamWebApiClient api,
        ITagService tags,
        IGameRepository games,
        ISyncMetaRepository syncMeta,
        IOptions<SteamOptions> steamOptions,
        IOptions<SyncOptions> syncOptions,
        ILogger<GameLibraryService> logger)
    {
        _paths = paths;
        _libraryFolders = libraryFolders;
        _acf = acf;
        _appInfo = appInfo;
        _assets = assets;
        _api = api;
        _tags = tags;
        _games = games;
        _syncMeta = syncMeta;
        _steamOptions = steamOptions.Value;
        _syncOptions = syncOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<LibraryChangedEventArgs>? LibraryChanged;

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken ct = default)
    {
        var cached = _cache;
        if (cached is not null)
        {
            return cached;
        }

        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = _cache;
            if (cached is not null)
            {
                return cached;
            }

            var built = await BuildLibraryAsync(ct).ConfigureAwait(false);
            _cache = built;
            return built;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unlike <see cref="GetGamesAsync"/> this does <b>not</b> apply the non-game filter: a caller
    /// that asks for a specific app id already knows what it wants, even when that is a DLC or a tool.
    /// </remarks>
    public async Task<GameEntry?> GetGameAsync(int appId, CancellationToken ct = default)
    {
        var row = await ReadRowAsync(appId, ct).ConfigureAwait(false);
        return row is null ? null : Hydrate(row, _liveProgress);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the row itself, then runs it through <see cref="BelongsOnGrid"/> — the same predicate
    /// <see cref="GetGamesAsync"/> applies to every row it returns. There is deliberately no second
    /// copy of the rule here.
    /// </remarks>
    public async Task<GameEntry?> GetGridEntryAsync(int appId, CancellationToken ct = default)
    {
        var row = await ReadRowAsync(appId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var ownedListAvailable = await IsOwnedListAvailableAsync(ct).ConfigureAwait(false);
        return BelongsOnGrid(row, ownedListAvailable) ? Hydrate(row, _liveProgress) : null;
    }

    /// <inheritdoc />
    public async Task<LibraryStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var steamPath = SafeFindSteamPath();

        var declared = 0;
        var missing = 0;
        if (steamPath is not null)
        {
            try
            {
                declared = _libraryFolders.Read(steamPath).Count;
                missing = Math.Max(0, declared - _paths.GetLibraryFolders().Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not enumerate the Steam library folders.");
            }
        }

        var ownedAvailable = await IsOwnedListAvailableAsync(ct).ConfigureAwait(false);

        return new LibraryStatus(
            SteamFound: steamPath is not null,
            SteamPath: steamPath,
            LibraryFolderCount: declared,
            MissingLibraryFolderCount: missing,
            ApiKeyConfigured: _api.IsConfigured,
            OwnedListAvailable: ownedAvailable,
            SteamId64: ResolveSteamId64(),
            LastError: _lastError);
    }

    /// <inheritdoc />
    public Task RefreshLocalAsync(CancellationToken ct = default) => RefreshLocalAsync(force: false, ct);

    /// <inheritdoc />
    public async Task RefreshLocalAsync(bool force, CancellationToken ct = default)
    {
        await _localGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The cooldown keeps window-focus thrash off the disk. A watcher event means Steam has
            // just rewritten a manifest, so the caller may override it and get the card to flip
            // within seconds instead of waiting the cooldown out.
            if (!force)
            {
                var lastScan = await TryGetLastRunAsync(SyncKeys.LocalScan, ct).ConfigureAwait(false);
                var cooldown = TimeSpan.FromSeconds(Math.Max(0, _syncOptions.LocalRescanCooldownSeconds));
                if (lastScan is not null && DateTimeOffset.UtcNow - lastScan.Value < cooldown)
                {
                    _logger.LogDebug("Local rescan skipped; the last scan was at {LastScan:O}.", lastScan.Value);
                    return;
                }
            }

            var folders = _paths.GetLibraryFolders();

            // The token goes into ReadAll as well as into Task.Run: Task.Run only stops the scan from
            // starting, and the reader checks the token once per manifest, so a shutdown landing
            // mid-scan unwinds in milliseconds instead of after every manifest on the machine.
            var apps = await Task.Run(() => _acf.ReadAll(folders, ct), ct).ConfigureAwait(false);

            await _games.UpsertInstalledAsync(apps, ct).ConfigureAwait(false);

            var installedIds = apps.Select(static a => a.AppId).ToHashSet();
            var uninstalledIds = await _games.MarkNotInstalledExceptAsync(installedIds, ct).ConfigureAwait(false);
            await _syncMeta.SetLastRunAsync(SyncKeys.LocalScan, DateTimeOffset.UtcNow, null, ct).ConfigureAwait(false);

            _liveProgress = apps
                .Where(static a => a.BytesToDownload > 0 || a.BytesDownloaded > 0)
                .GroupBy(static a => a.AppId)
                .ToFrozenDictionary(
                    static g => g.Key,
                    static g => new TransferProgress(g.Last().BytesDownloaded, g.Last().BytesToDownload));

            _logger.LogInformation(
                "Local scan found {AppCount} installed apps across {FolderCount} library folders.",
                apps.Count,
                folders.Count);

            if (uninstalledIds.Count > 0)
            {
                // The count is always logged; the ids only while there are few enough to read. A
                // library of a few thousand rows can lose all of them at once — an unplugged drive
                // does exactly that — and rendering that list into one message costs more than the
                // scan that produced it and tells nobody anything.
                if (uninstalledIds.Count <= MaxLoggedAppIds)
                {
                    _logger.LogInformation(
                        "{AppCount} apps no longer have a manifest and were marked not installed: {AppIds}.",
                        uninstalledIds.Count,
                        uninstalledIds);
                }
                else
                {
                    _logger.LogInformation(
                        "{AppCount} apps no longer have a manifest and were marked not installed; the first {LoggedCount} are {AppIds}.",
                        uninstalledIds.Count,
                        MaxLoggedAppIds,
                        uninstalledIds.Take(MaxLoggedAppIds));
                }
            }

            // The event has to name every app whose state moved, not just the ones still on disk: a
            // consumer that applies a partial update learns about an uninstall only if it is named
            // here, and otherwise leaves the card offering to play a game that is gone.
            var changedIds = new HashSet<int>(installedIds);
            changedIds.UnionWith(uninstalledIds);

            RaiseChanged(LibraryChangeKind.Local, [.. changedIds]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The local manifest rescan failed.");
            RecordError("Could not rescan the local Steam manifests.");
        }
        finally
        {
            _localGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RefreshRemoteAsync(bool force, CancellationToken ct = default)
    {
        if (!_api.IsConfigured)
        {
            _ownedListAvailable = false;
            RecordError("No Steam Web API key is configured, so owned-but-not-installed games stay hidden.");
            return;
        }

        var steamId = ResolveSteamId64();
        if (steamId is null)
        {
            _ownedListAvailable = false;
            RecordError("No SteamID64 is configured and none could be detected from loginusers.vdf.");
            return;
        }

        await _remoteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && await IsFreshAsync(SyncKeys.OwnedGames, _syncOptions.OwnedGamesMaxAgeHours, ct).ConfigureAwait(false))
            {
                _ownedListAvailable = true;
                return;
            }

            var owned = await _api.GetOwnedGamesAsync(steamId.Value, ct).ConfigureAwait(false);
            if (!owned.Visible)
            {
                _ownedListAvailable = false;
                RecordError("The owned-games list is not readable: the profile is private, or the request failed.");
                return;
            }

            await _games.UpsertOwnedAsync(owned.Games, ct).ConfigureAwait(false);
            await _syncMeta
                .SetLastRunAsync(SyncKeys.OwnedGames, DateTimeOffset.UtcNow, steamId.Value.ToString(), ct)
                .ConfigureAwait(false);

            _ownedListAvailable = true;
            _lastError = null;
            _logger.LogInformation("The owned-games refresh stored {GameCount} entries.", owned.Games.Count);

            RaiseChanged(LibraryChangeKind.Remote, [.. owned.Games.Select(static g => g.AppId)]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The owned-games refresh failed.");
            RecordError("Could not refresh the owned-games list from the Steam Web API.");
        }
        finally
        {
            _remoteGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RefreshMetadataAsync(bool force, CancellationToken ct = default)
    {
        await _metadataGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && await IsFreshAsync(SyncKeys.AppInfo, _syncOptions.AppInfoMaxAgeHours, ct).ConfigureAwait(false))
            {
                return;
            }

            var steamPath = SafeFindSteamPath();
            if (steamPath is null)
            {
                RecordError("Steam is not installed on this machine, so no local metadata is available.");
                return;
            }

            var appInfoPath = Path.Combine(steamPath, "appcache", "appinfo.vdf");

            // Ids, not rows: all this needs is a filter for the appinfo.vdf walk, and hydrating the
            // whole library to build it made this step cost as much as rendering the grid.
            var wanted = (await _games.GetAllAppIdsAsync(ct).ConfigureAwait(false)).ToHashSet();

            if (wanted.Count > 0)
            {
                // Same reason as the manifest scan: the token has to reach the parse loop, which
                // checks it once per record, or cancelling only stops the parse from starting.
                var parsed = await Task
                    .Run(() => _appInfo.ReadApps(appInfoPath, wanted, ct), ct)
                    .ConfigureAwait(false);

                if (parsed.Count > 0)
                {
                    await _games.UpsertMetadataAsync([.. parsed.Values], ct).ConfigureAwait(false);
                }

                _logger.LogInformation(
                    "appinfo.vdf resolved metadata for {Found} of {Wanted} apps.",
                    parsed.Count,
                    wanted.Count);
            }

            await _tags.RefreshNamesAsync(force, ct).ConfigureAwait(false);
            await _syncMeta.SetLastRunAsync(SyncKeys.AppInfo, DateTimeOffset.UtcNow, null, ct).ConfigureAwait(false);

            RaiseChanged(LibraryChangeKind.Metadata, [.. wanted]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The metadata refresh failed.");
            RecordError("Could not refresh metadata from appinfo.vdf.");
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    /// <inheritdoc />
    public void NotifyAssetsChanged(IReadOnlyList<int> appIds)
    {
        ArgumentNullException.ThrowIfNull(appIds);

        _logger.LogDebug(
            "Cached library art moved for {AppCount} app(s); dropping the built rows so their art paths are resolved again.",
            appIds.Count);

        // RaiseChanged nulls the cache, which is the half that was missing: invalidating the art
        // resolver alone left the built rows handing out the old paths for the life of the process.
        RaiseChanged(LibraryChangeKind.Assets, [.. appIds]);
    }

    /// <summary>Reads one stored row, turning every non-cancellation failure into a status message.</summary>
    private async Task<GameEntry?> ReadRowAsync(int appId, CancellationToken ct)
    {
        try
        {
            return await _games.GetAsync(appId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read game {AppId} from the library database.", appId);
            RecordError($"Could not read app {appId} from the local database.");
            return null;
        }
    }

    private async Task<IReadOnlyList<GameEntry>> BuildLibraryAsync(CancellationToken ct)
    {
        IReadOnlyList<GameEntry> rows;
        try
        {
            rows = await _games.GetAllAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the library from the local database.");
            RecordError("Could not read the library from the local database.");
            return [];
        }

        // Read once for the whole build rather than per row: it is the same answer for every one of
        // them, and it can cost a database round trip.
        var ownedListAvailable = await IsOwnedListAvailableAsync(ct).ConfigureAwait(false);

        var live = _liveProgress;
        var result = new List<GameEntry>(rows.Count);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!BelongsOnGrid(row, ownedListAvailable))
            {
                continue;
            }

            result.Add(Hydrate(row, live));
        }

        return result;
    }

    /// <summary>
    /// The one predicate behind both <see cref="GetGamesAsync"/> and <see cref="GetGridEntryAsync"/>:
    /// whether a stored row belongs on the library grid.
    /// </summary>
    /// <remarks>
    /// It exists exactly once on purpose. A bulk read and a single-app refresh that each decided this
    /// for themselves is the defect this replaces: they disagreed, and a card could be dropped by one
    /// path and put back by the other.
    /// </remarks>
    /// <param name="entry">The row to judge.</param>
    /// <param name="ownedListAvailable">
    /// Whether the owned-games list is available and therefore authoritative about what is <i>not</i>
    /// owned. See <see cref="IsOwnedListAvailableAsync"/>.
    /// </param>
    private static bool BelongsOnGrid(GameEntry entry, bool ownedListAvailable) =>
        !RedistributableAppIds.Contains(entry.AppId)
        && IsPlayableType(entry)
        && IsLibraryMember(entry, ownedListAvailable);

    /// <summary>
    /// The <c>common/type</c> filter. A non-game type survives only while it is installed — the user
    /// clearly has it and may want to launch or remove it — and a row whose type has not been
    /// resolved yet is kept, so the grid is not empty before the first metadata refresh.
    /// </summary>
    private static bool IsPlayableType(GameEntry entry) =>
        entry.IsInstalled
        || string.IsNullOrWhiteSpace(entry.AppType)
        || !NonGameTypes.Contains(entry.AppType);

    /// <summary>
    /// Whether the library considers this app one of the user's at all.
    /// </summary>
    /// <remarks>
    /// Installed, or known to be owned, or — and this is the part that was wrong — the owned list is
    /// not available at all and the row came from a real manifest. Without an API key
    /// <see cref="RefreshRemoteAsync"/> returns before it can set <c>is_owned</c>, so on a default
    /// install <see cref="GameEntry.IsOwned"/> is <see langword="false"/> for every single row.
    /// Reading that as "not owned" deleted the card of any game the user uninstalled and left them
    /// unable to reinstall it from here, which is the whole point of the application.
    /// <see cref="GameEntry.LastLocalScanUtc"/> is the evidence that the row came from a manifest:
    /// the local scan stamps it and nothing clears it, so it outlives the uninstall that cleared
    /// <see cref="GameEntry.IsInstalled"/>. A row that only ever came from an owned list has no
    /// stamp, but it does not need one — it is owned.
    /// </remarks>
    private static bool IsLibraryMember(GameEntry entry, bool ownedListAvailable) =>
        entry.IsInstalled
        || entry.IsOwned
        || (!ownedListAvailable && entry.LastLocalScanUtc is not null);

    /// <summary>
    /// Whether the owned-games list is available, and therefore authoritative about what the user
    /// does <i>not</i> own.
    /// </summary>
    /// <remarks>
    /// True once a refresh in this process has stored a list, and otherwise whatever an earlier run
    /// left behind: a stored <see cref="SyncKeys.OwnedGames"/> timestamp means <c>is_owned</c> in the
    /// database was written from a real list at least once. Anything else — no API key, no SteamID64,
    /// a private profile, a failed request, a database that will not answer — is "we do not know",
    /// which is not the same answer as "not owned".
    /// </remarks>
    private async Task<bool> IsOwnedListAvailableAsync(CancellationToken ct)
    {
        if (_ownedListAvailable)
        {
            return true;
        }

        try
        {
            return await _syncMeta.GetLastRunAsync(SyncKeys.OwnedGames, ct).ConfigureAwait(false) is not null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the owned-games sync timestamp.");

            // The safe answer when we cannot tell: not authoritative, so no row loses its card.
            return false;
        }
    }

    private GameEntry Hydrate(GameEntry row, IReadOnlyDictionary<int, TransferProgress> live)
    {
        var entry = row.Assets is null ? row with { Assets = SafeResolveAssets(row.AppId) } : row;

        if (live.TryGetValue(row.AppId, out var progress))
        {
            entry = entry with
            {
                BytesDownloaded = progress.Downloaded,
                BytesToDownload = progress.Total,
            };
        }

        return entry;
    }

    private GameAssets SafeResolveAssets(int appId)
    {
        try
        {
            return _assets.Resolve(appId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve library art for {AppId}.", appId);
            return GameAssets.None(appId);
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

    private string? SafeFindSteamPath()
    {
        try
        {
            return _paths.FindSteamPath();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not locate the Steam installation.");
            return null;
        }
    }

    private async Task<DateTimeOffset?> TryGetLastRunAsync(string key, CancellationToken ct)
    {
        try
        {
            return await _syncMeta.GetLastRunAsync(key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the last-run timestamp for {SyncKey}.", key);
            return null;
        }
    }

    private async Task<bool> IsFreshAsync(string key, int maxAgeHours, CancellationToken ct)
    {
        if (maxAgeHours <= 0)
        {
            return false;
        }

        var last = await TryGetLastRunAsync(key, ct).ConfigureAwait(false);
        return last is not null && DateTimeOffset.UtcNow - last.Value < TimeSpan.FromHours(maxAgeHours);
    }

    private void RecordError(string message)
    {
        _lastError = message;
        _logger.LogWarning("Library degraded: {Reason}", message);
    }

    private void RaiseChanged(LibraryChangeKind kind, IReadOnlyList<int>? appIds)
    {
        _cache = null;

        var handler = LibraryChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new LibraryChangedEventArgs(kind, appIds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A LibraryChanged subscriber threw; the refresh itself succeeded.");
        }
    }

    /// <summary>Live transfer counters carried over from the most recent local scan.</summary>
    private readonly record struct TransferProgress(long Downloaded, long Total);
}
