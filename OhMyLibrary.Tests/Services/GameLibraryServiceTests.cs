using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Tests.Fakes;

namespace OhMyLibrary.Tests.Services;

/// <summary>
/// <see cref="GameLibraryService"/> with hand-written fakes for everything it touches.
/// </summary>
/// <remarks>
/// The two things worth pinning here are the merge — which rows reach the grid, and with what
/// overlaid on them — and the promise that every degraded environment produces a status report
/// rather than an exception.
/// </remarks>
public sealed class GameLibraryServiceTests
{
    private readonly FakeSteamPathResolver _paths = new() { SteamPath = @"C:\Steam" };
    private readonly FakeLibraryFoldersReader _libraryFolders = new();
    private readonly FakeAcfReader _acf = new();
    private readonly FakeAppInfoReader _appInfo = new();
    private readonly FakeLibraryAssetResolver _assets = new();
    private readonly FakeSteamWebApiClient _api = new();
    private readonly FakeTagService _tags = new();
    private readonly FakeGameRepository _games = new();
    private readonly FakeSyncMetaRepository _syncMeta = new();

    private readonly SteamOptions _steamOptions = new();
    private readonly SyncOptions _syncOptions = new();

    [Fact]
    public async Task GetGames_ReturnsOwnedOnlyInstalledOnlyAndBothKindsOfRow()
    {
        _games.Rows.AddRange([
            Row(1, "Owned only", owned: true, installed: false, type: "game"),
            Row(2, "Installed only", owned: false, installed: true, type: "game"),
            Row(3, "Owned and installed", owned: true, installed: true, type: "game"),
        ]);

        var games = await Service().GetGamesAsync();

        Assert.Equal([1, 2, 3], games.Select(game => game.AppId).Order().ToArray());
    }

    [Fact]
    public async Task GetGames_DropsANonGameThatIsNotInstalled()
    {
        _games.Rows.AddRange([
            Row(1, "A game", owned: true, installed: false, type: "game"),
            Row(2, "Some DLC", owned: true, installed: false, type: "dlc"),
            Row(3, "A soundtrack", owned: true, installed: false, type: "Music"),
        ]);

        var games = await Service().GetGamesAsync();

        Assert.Equal([1], games.Select(game => game.AppId).ToArray());
    }

    [Fact]
    public async Task GetGames_KeepsANonGameThatIsInstalled()
    {
        // The user clearly has it on disk and may want to launch or remove it.
        _games.Rows.Add(Row(2, "An installed tool", owned: false, installed: true, type: "Tool"));

        var games = await Service().GetGamesAsync();

        Assert.Equal(2, Assert.Single(games).AppId);
    }

    [Fact]
    public async Task GetGames_KeepsARowWhoseTypeHasNotBeenParsedYet()
    {
        // Before the first appinfo.vdf pass nothing has a type, and an empty grid on first run would
        // look like a broken launcher.
        _games.Rows.Add(Row(1, "Unclassified", owned: true, installed: false, type: null));

        Assert.Single(await Service().GetGamesAsync());
    }

    [Fact]
    public async Task GetGames_AlwaysDropsTheRedistributables()
    {
        _games.Rows.AddRange([
            Row(228980, "Steamworks Common Redistributables", owned: false, installed: true, type: "Tool"),
            Row(1070560, "Steam Linux Runtime", owned: true, installed: true, type: "Tool"),
            Row(570, "Dota 2", owned: true, installed: true, type: "game"),
        ]);

        var games = await Service().GetGamesAsync();

        Assert.Equal([570], games.Select(game => game.AppId).ToArray());
    }

    [Fact]
    public async Task GetGames_ResolvesArtForRowsTheDatabaseCannotCarry()
    {
        _games.Rows.Add(Row(570, "Dota 2", owned: true, installed: true, type: "game"));
        _assets.Assets[570] = new GameAssets(570, @"C:\art\capsule.jpg", null, null, null, null);

        var game = Assert.Single(await Service().GetGamesAsync());

        Assert.Equal(@"C:\art\capsule.jpg", game.Assets?.CapsulePath);
    }

    [Fact]
    public async Task GetGame_DoesNotApplyTheNonGameFilter()
    {
        _games.Rows.Add(Row(2, "Some DLC", owned: true, installed: false, type: "dlc"));

        var service = Service();

        Assert.Empty(await service.GetGamesAsync());
        Assert.NotNull(await service.GetGameAsync(2));
    }

    [Fact]
    public async Task GetGame_ReturnsNullForAnAppThatIsNeitherOwnedNorInstalled()
    {
        Assert.Null(await Service().GetGameAsync(4242));
    }

    [Fact]
    public async Task RefreshLocal_WritesTheScanAndClearsWhatIsNoLongerInstalled()
    {
        _acf.Apps.Add(Installed(570, "Dota 2"));
        _acf.Apps.Add(Installed(292030, "The Witcher 3"));

        await Service().RefreshLocalAsync();

        Assert.Equal(2, Assert.Single(_games.InstalledUpserts).Count);
        Assert.Equal([570, 292030], Assert.Single(_games.NotInstalledExcept).Order().ToArray());
        Assert.Contains(SyncKeys.LocalScan, _syncMeta.Writes);
    }

    [Fact]
    public async Task RefreshLocal_IsThrottledByTheCooldown()
    {
        _syncMeta.Seed(SyncKeys.LocalScan, DateTimeOffset.UtcNow.AddSeconds(-5));
        _syncOptions.LocalRescanCooldownSeconds = 60;

        await Service().RefreshLocalAsync();

        Assert.Empty(_games.InstalledUpserts);
        Assert.Equal(0, _acf.ReadAllCalls);
    }

    [Fact]
    public async Task RefreshLocal_WithoutForceStillRespectsTheCooldown()
    {
        // The single-argument form is the focus-driven path and must delegate to force: false.
        _syncMeta.Seed(SyncKeys.LocalScan, DateTimeOffset.UtcNow.AddSeconds(-5));
        _syncOptions.LocalRescanCooldownSeconds = 60;

        await Service().RefreshLocalAsync(force: false);

        Assert.Empty(_games.InstalledUpserts);
        Assert.Equal(0, _acf.ReadAllCalls);
    }

    [Fact]
    public async Task RefreshLocal_ForcedIgnoresTheCooldown()
    {
        // A watcher event means Steam has just rewritten a manifest, so waiting out a 60 second
        // cooldown would leave a finished install showing the wrong button for the best part of a
        // minute.
        _syncMeta.Seed(SyncKeys.LocalScan, DateTimeOffset.UtcNow.AddSeconds(-5));
        _syncOptions.LocalRescanCooldownSeconds = 60;
        _acf.Apps.Add(Installed(570, "Dota 2"));

        await Service().RefreshLocalAsync(force: true);

        Assert.Equal(570, Assert.Single(Assert.Single(_games.InstalledUpserts)).AppId);
        Assert.Equal(1, _acf.ReadAllCalls);
        Assert.Contains(SyncKeys.LocalScan, _syncMeta.Writes);
    }

    [Fact]
    public async Task RefreshLocal_OverlaysLiveDownloadProgressOntoTheStoredRows()
    {
        // The database has no transfer columns, so progress can only come from the live scan.
        _games.Rows.Add(Row(570, "Dota 2", owned: true, installed: true, type: "game") with
        {
            StateFlags = AppStateFlags.FullyInstalled | AppStateFlags.Downloading,
        });

        _acf.Apps.Add(Installed(570, "Dota 2") with
        {
            StateFlags = AppStateFlags.FullyInstalled | AppStateFlags.Downloading,
            BytesDownloaded = 250,
            BytesToDownload = 1000,
        });

        var service = Service();
        await service.RefreshLocalAsync();

        var game = Assert.Single(await service.GetGamesAsync());
        Assert.Equal(0.25d, game.DownloadProgress);
    }

    [Fact]
    public async Task RefreshLocal_RaisesLibraryChangedAndDropsTheCache()
    {
        _games.Rows.Add(Row(570, "Dota 2", owned: true, installed: true, type: "game"));
        var service = Service();
        await service.GetGamesAsync();

        LibraryChangedEventArgs? observed = null;
        service.LibraryChanged += (_, args) => observed = args;

        _acf.Apps.Add(Installed(570, "Dota 2"));
        await service.RefreshLocalAsync();

        Assert.NotNull(observed);
        Assert.Equal(LibraryChangeKind.Local, observed.Kind);
        Assert.Equal([570], observed.AppIds.ToArray());
    }

    [Fact]
    public async Task RefreshLocal_NamesTheUninstalledAppsInTheChangeEventToo()
    {
        // The card of a game whose manifest Steam just deleted has to be told to stop offering
        // "Play". A consumer applying a partial update only ever learns that from this list, so an
        // event naming only what is still installed leaves the uninstalled card stale for good.
        _games.Rows.Add(Row(570, "Dota 2", owned: true, installed: true, type: "game"));
        _games.Rows.Add(Row(292030, "The Witcher 3", owned: true, installed: true, type: "game"));

        var service = Service();
        LibraryChangedEventArgs? observed = null;
        service.LibraryChanged += (_, args) => observed = args;

        // The Witcher 3 lost its manifest; only Dota 2 is still on disk.
        _acf.Apps.Add(Installed(570, "Dota 2"));
        await service.RefreshLocalAsync();

        Assert.NotNull(observed);
        Assert.Equal(LibraryChangeKind.Local, observed.Kind);
        Assert.Equal([570, 292030], observed.AppIds.Order().ToArray());
    }

    [Fact]
    public async Task RefreshLocal_DoesNotNameAppsThatWereAlreadyNotInstalled()
    {
        // Owned-but-never-installed rows do not change state during a scan, so naming them would
        // turn every rescan into a whole-library repaint.
        _games.Rows.Add(Row(570, "Dota 2", owned: true, installed: true, type: "game"));
        _games.Rows.Add(Row(1091500, "Cyberpunk 2077", owned: true, installed: false, type: "game"));

        var service = Service();
        LibraryChangedEventArgs? observed = null;
        service.LibraryChanged += (_, args) => observed = args;

        _acf.Apps.Add(Installed(570, "Dota 2"));
        await service.RefreshLocalAsync();

        Assert.NotNull(observed);
        Assert.Equal([570], observed.AppIds.ToArray());
    }

    [Fact]
    public async Task RefreshRemote_WithoutAnApiKeyExplainsItselfInsteadOfThrowing()
    {
        _api.IsConfigured = false;

        var service = Service();
        await service.RefreshRemoteAsync(force: true);

        var status = await service.GetStatusAsync();

        Assert.False(status.ApiKeyConfigured);
        Assert.False(status.OwnedListAvailable);
        Assert.NotNull(status.LastError);
        Assert.Empty(_games.OwnedUpserts);
    }

    [Fact]
    public async Task RefreshRemote_WithAPrivateProfileKeepsTheInstalledGames()
    {
        _paths.LocalUsers.Add(User(76_561_198_000_000_001UL, mostRecent: true));
        _api.OwnedGames[76_561_198_000_000_001UL] = OwnedGamesResult.Hidden;
        _games.Rows.Add(Row(570, "Dota 2", owned: false, installed: true, type: "game"));

        var service = Service();
        await service.RefreshRemoteAsync(force: true);

        var status = await service.GetStatusAsync();

        Assert.False(status.OwnedListAvailable);
        Assert.Equal(76_561_198_000_000_001UL, status.SteamId64);
        Assert.Empty(_games.OwnedUpserts);
        Assert.Single(await service.GetGamesAsync());
    }

    [Fact]
    public async Task RefreshRemote_StoresAVisibleOwnedList()
    {
        _steamOptions.SteamId64 = "76561198000000001";
        _api.OwnedGames[76_561_198_000_000_001UL] = new OwnedGamesResult(true, [
            new OwnedGame(570, "Dota 2", 120, 0, null, null),
        ]);

        var service = Service();
        await service.RefreshRemoteAsync(force: true);

        Assert.Equal(570, Assert.Single(Assert.Single(_games.OwnedUpserts)).AppId);
        Assert.Contains(SyncKeys.OwnedGames, _syncMeta.Writes);
        Assert.True((await service.GetStatusAsync()).OwnedListAvailable);
    }

    [Fact]
    public async Task RefreshRemote_HonoursTheCacheLifetimeUnlessForced()
    {
        _steamOptions.SteamId64 = "76561198000000001";
        _api.OwnedGames[76_561_198_000_000_001UL] = new OwnedGamesResult(true, []);
        _syncMeta.Seed(SyncKeys.OwnedGames, DateTimeOffset.UtcNow.AddMinutes(-5));
        _syncOptions.OwnedGamesMaxAgeHours = 12;

        var service = Service();

        await service.RefreshRemoteAsync(force: false);
        Assert.Empty(_api.OwnedGamesCalls);

        await service.RefreshRemoteAsync(force: true);
        Assert.Single(_api.OwnedGamesCalls);
    }

    [Fact]
    public async Task RefreshMetadata_AsksAppInfoOnlyForTheAppsWeTrack()
    {
        _games.Rows.AddRange([
            Row(570, "Dota 2", owned: true, installed: true, type: null),
            Row(292030, "The Witcher 3", owned: true, installed: false, type: null),
        ]);

        _appInfo.Entries[570] = Metadata(570, "Dota 2", "game");

        await Service().RefreshMetadataAsync(force: true);

        Assert.Equal([570, 292030], _appInfo.LastRequestedIds!.Order().ToArray());
        Assert.Equal(570, Assert.Single(Assert.Single(_games.MetadataUpserts)).AppId);
        Assert.Equal(1, _tags.RefreshCalls);
        Assert.Contains(SyncKeys.AppInfo, _syncMeta.Writes);
    }

    [Fact]
    public async Task RefreshMetadata_WithoutSteamDegradesToAStatusMessage()
    {
        _paths.SteamPath = null;

        var service = Service();
        await service.RefreshMetadataAsync(force: true);

        var status = await service.GetStatusAsync();

        Assert.False(status.SteamFound);
        Assert.Null(status.SteamPath);
        Assert.NotNull(status.LastError);
        Assert.Empty(_games.MetadataUpserts);
    }

    [Fact]
    public async Task GetStatus_CountsTheLibraryFoldersThatCouldNotBeReached()
    {
        _libraryFolders.Declared.AddRange([Folder(@"C:\Steam"), Folder(@"A:\SteamLibrary"), Folder(@"B:\SteamLibrary")]);
        _paths.LibraryFolders.AddRange([Folder(@"C:\Steam"), Folder(@"A:\SteamLibrary")]);

        var status = await Service().GetStatusAsync();

        Assert.True(status.SteamFound);
        Assert.Equal(3, status.LibraryFolderCount);
        Assert.Equal(1, status.MissingLibraryFolderCount);
    }

    [Fact]
    public async Task ADatabaseFailureLeavesAnEmptyLibraryAndAnExplanation()
    {
        _games.Failure = new InvalidOperationException("the database is locked");

        var service = Service();

        Assert.Empty(await service.GetGamesAsync());
        Assert.Null(await service.GetGameAsync(570));
        Assert.NotNull((await service.GetStatusAsync()).LastError);
    }

    [Fact]
    public async Task GetGridEntry_AndGetGames_BothRefuseAnInstalledRedistributable()
    {
        // 228980 is installed on practically every machine and is not a game by any route. Both
        // reads have to agree about it, or one of them puts "Steamworks Common Redistributables" on
        // the grid the moment the other is not looking.
        _games.Rows.Add(Row(228980, "Steamworks Common Redistributables", owned: true, installed: true, type: "Tool"));
        _games.Rows.Add(Row(570, "Dota 2", owned: true, installed: true, type: "game"));

        var service = Service();
        var onTheGrid = (await service.GetGamesAsync()).Select(game => game.AppId).ToArray();

        Assert.Equal([570], onTheGrid);
        Assert.Null(await service.GetGridEntryAsync(228980));

        // ... and it is the grid filter that refuses it, not the row being missing.
        Assert.NotNull(await service.GetGameAsync(228980));
    }

    [Fact]
    public async Task GetGridEntry_WithNoApiKey_KeepsAnAppThatIsUninstalledButWasInstalledBefore()
    {
        // The defect this pins: with no key RefreshRemoteAsync returns before it can set is_owned, so
        // IsOwned is false for every row on a default install. Reading that as "not owned" deleted
        // the card of any game the user uninstalled — and with it their way of reinstalling it.
        _api.IsConfigured = false;
        _games.Rows.Add(Row(
            292030,
            "The Witcher 3",
            owned: false,
            installed: false,
            type: "game",
            scanned: DateTimeOffset.UtcNow.AddMinutes(-1)));

        var service = Service();
        await service.RefreshRemoteAsync(force: true);

        var onTheGrid = (await service.GetGamesAsync()).Select(game => game.AppId).ToArray();

        Assert.False((await service.GetStatusAsync()).OwnedListAvailable);
        Assert.Equal([292030], onTheGrid);
        Assert.NotNull(await service.GetGridEntryAsync(292030));
    }

    [Fact]
    public async Task GetGridEntry_WithAnAuthoritativeOwnedList_DropsARowThatIsNeitherOwnedNorInstalled()
    {
        // The other half of the rule. Once the owned list has actually been read, "not owned" is an
        // answer rather than an absence, and a row that is neither owned nor installed has nothing
        // left to offer: it cannot be played and it cannot be installed.
        _steamOptions.SteamId64 = "76561198000000001";
        _api.OwnedGames[76_561_198_000_000_001UL] = new OwnedGamesResult(true, []);
        _games.Rows.Add(Row(
            292030,
            "A family-shared game that has gone",
            owned: false,
            installed: false,
            type: "game",
            scanned: DateTimeOffset.UtcNow.AddMinutes(-1)));

        var service = Service();
        await service.RefreshRemoteAsync(force: true);

        Assert.True((await service.GetStatusAsync()).OwnedListAvailable);
        Assert.Empty(await service.GetGamesAsync());
        Assert.Null(await service.GetGridEntryAsync(292030));
    }

    [Fact]
    public async Task GetGridEntry_TreatsAnEarlierRunsOwnedSyncAsAuthoritative()
    {
        // Nothing has synced in this process, but a previous run stored a list, so is_owned in the
        // database was written from a real answer. That is still "available".
        _syncMeta.Seed(SyncKeys.OwnedGames, DateTimeOffset.UtcNow.AddHours(-3));
        _games.Rows.Add(Row(
            292030,
            "The Witcher 3",
            owned: false,
            installed: false,
            type: "game",
            scanned: DateTimeOffset.UtcNow.AddHours(-4)));

        var service = Service();

        Assert.Empty(await service.GetGamesAsync());
        Assert.Null(await service.GetGridEntryAsync(292030));
    }

    [Fact]
    public async Task GetGridEntry_WithNoOwnedListStillDropsARowNoManifestEverProduced()
    {
        // "We do not know whether it is owned" only rescues a row that came from a real manifest.
        // A row with neither an owned flag nor a scan stamp is not evidence of anything.
        _games.Rows.Add(Row(292030, "Never seen on disk", owned: false, installed: false, type: "game"));

        var service = Service();

        Assert.Empty(await service.GetGamesAsync());
        Assert.Null(await service.GetGridEntryAsync(292030));
    }

    [Fact]
    public async Task GetGridEntry_AppliesTheSameTypeFilterAsTheBulkRead()
    {
        _games.Rows.Add(Row(2, "Some DLC", owned: true, installed: false, type: "dlc"));

        var service = Service();

        Assert.Empty(await service.GetGamesAsync());
        Assert.Null(await service.GetGridEntryAsync(2));

        // The detail read is the one that still answers, and that difference is deliberate.
        Assert.NotNull(await service.GetGameAsync(2));
    }

    [Fact]
    public async Task GetGridEntry_ReturnsNullForAnAppTheDatabaseHasNeverHeardOf()
    {
        Assert.Null(await Service().GetGridEntryAsync(4242));
    }

    [Fact]
    public async Task GetGridEntry_HydratesArtAndLiveProgressLikeTheBulkRead()
    {
        _games.Rows.Add(Row(570, "Dota 2", owned: true, installed: true, type: "game") with
        {
            StateFlags = AppStateFlags.FullyInstalled | AppStateFlags.Downloading,
        });

        _assets.Assets[570] = new GameAssets(570, @"C:\art\capsule.jpg", null, null, null, null);
        _acf.Apps.Add(Installed(570, "Dota 2") with
        {
            StateFlags = AppStateFlags.FullyInstalled | AppStateFlags.Downloading,
            BytesDownloaded = 250,
            BytesToDownload = 1000,
        });

        var service = Service();
        await service.RefreshLocalAsync();

        var entry = await service.GetGridEntryAsync(570);

        Assert.NotNull(entry);
        Assert.Equal(@"C:\art\capsule.jpg", entry.Assets?.CapsulePath);
        Assert.Equal(0.25d, entry.DownloadProgress);
    }

    [Fact]
    public async Task NotifyAssetsChanged_DropsTheBuiltRowsSoTheirArtPathsAreResolvedAgain()
    {
        // Break 1 of the cover-art chain: the library caches the rows it built, and each one carries
        // the art paths that were current when it was built. Invalidating the resolver alone leaves
        // those rows handing out the old path for the life of the process.
        _games.Rows.Add(Row(570, "Dota 2", owned: true, installed: true, type: "game"));
        _assets.Assets[570] = new GameAssets(570, @"C:\art\old.jpg", null, null, null, null);

        var service = Service();
        Assert.Equal(@"C:\art\old.jpg", Assert.Single(await service.GetGamesAsync()).Assets?.CapsulePath);

        LibraryChangedEventArgs? observed = null;
        service.LibraryChanged += (_, args) => observed = args;

        // Steam rewrote the art somewhere else and the resolver has been told.
        _assets.Assets[570] = new GameAssets(570, @"C:\art\new.jpg", null, null, null, null);

        service.NotifyAssetsChanged([570]);

        Assert.NotNull(observed);
        Assert.Equal(LibraryChangeKind.Assets, observed.Kind);
        Assert.Equal([570], observed.AppIds.ToArray());
        Assert.Equal(@"C:\art\new.jpg", Assert.Single(await service.GetGamesAsync()).Assets?.CapsulePath);
    }

    [Fact]
    public async Task RefreshLocal_HandsTheCancellationTokenToTheManifestScan()
    {
        // Wrapping the scan in Task.Run only ever stopped it from starting. The token has to reach
        // ReadAll, which checks it per manifest, or a shutdown landing mid-scan waits out every
        // manifest on the machine.
        using var cts = new CancellationTokenSource();
        _acf.ReadAllGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = Service();
        var scan = service.RefreshLocalAsync(force: true, cts.Token);

        await _acf.ReadAllStarted.WaitAsync();

        // Cancelled with the scan already running, which is the case Task.Run cannot help with.
        await cts.CancelAsync();
        _acf.ReadAllGate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.Empty(_games.InstalledUpserts);
    }

    [Fact]
    public async Task RefreshMetadata_HandsTheCancellationTokenToTheAppInfoParse()
    {
        // The parse walks a few thousand records and checks the token on every one — but only if it
        // is given the token. Task.Run's copy stops the parse from starting and nothing else.
        using var cts = new CancellationTokenSource();
        _games.Rows.Add(Row(570, "Dota 2", owned: true, installed: true, type: null));

        await Service().RefreshMetadataAsync(force: true, cts.Token);

        Assert.Equal(cts.Token, _appInfo.LastToken);
    }

    [Fact]
    public async Task CancellationIsTheOneExceptionThatEscapes()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().GetGamesAsync(cts.Token));
    }

    private GameLibraryService Service() =>
        new(
            _paths,
            _libraryFolders,
            _acf,
            _appInfo,
            _assets,
            _api,
            _tags,
            _games,
            _syncMeta,
            Options.Create(_steamOptions),
            Options.Create(_syncOptions),
            NullLogger<GameLibraryService>.Instance);

    private static SteamLibraryFolder Folder(string path) =>
        new(path, string.Empty, 0, new Dictionary<int, long>());

    private static SteamUser User(ulong steamId, bool mostRecent) =>
        new(steamId, "fixture_account", "Fixture User", mostRecent, DateTimeOffset.UtcNow);

    /// <param name="appId">Steam application id.</param>
    /// <param name="name">Display name.</param>
    /// <param name="owned">Whether the owned-games list named it.</param>
    /// <param name="installed">Whether a manifest for it was found.</param>
    /// <param name="type">Raw <c>common/type</c>, or <see langword="null"/> before the first metadata pass.</param>
    /// <param name="scanned">
    /// When the local scan last wrote this row. Defaults to "installed rows are stamped, others are
    /// not", which is what the repository does; pass it explicitly for the interesting case — a row
    /// that came from a manifest and is not installed any more.
    /// </param>
    private static GameEntry Row(
        int appId,
        string name,
        bool owned,
        bool installed,
        string? type,
        DateTimeOffset? scanned = null) =>
        new(
            AppId: appId,
            Name: name,
            IsOwned: owned,
            IsInstalled: installed,
            StateFlags: installed ? AppStateFlags.FullyInstalled : AppStateFlags.None,
            InstallDir: installed ? name : null,
            FullInstallPath: null,
            SizeBytes: installed ? 1024 : 0,
            BuildId: null,
            PlaytimeForeverMinutes: 0,
            LastPlayed: null,
            Genres: [],
            Tags: [],
            Assets: null,
            AppType: type,
            FriendOwnerIds: [],
            CollectionIds: [],
            LastLocalScanUtc: scanned ?? (installed ? DateTimeOffset.UtcNow : null));

    private static InstalledApp Installed(int appId, string name) =>
        new(
            AppId: appId,
            Name: name,
            StateFlags: AppStateFlags.FullyInstalled,
            InstallDir: name,
            FullInstallPath: null,
            ManifestPath: $@"C:\Steam\steamapps\appmanifest_{appId}.acf",
            LibraryPath: @"C:\Steam",
            SizeOnDisk: 1024,
            BytesDownloaded: 0,
            BytesToDownload: 0,
            StagingSize: 0,
            BuildId: null,
            TargetBuildId: null,
            LastOwner: 0,
            LastUpdated: null,
            LastPlayed: null);

    private static AppInfoEntry Metadata(int appId, string name, string type) =>
        new(
            AppId: appId,
            Name: name,
            Type: type,
            SortAs: null,
            GenreIds: [],
            StoreTagIds: [],
            CategoryIds: [],
            LocalizedNames: new Dictionary<string, string>(),
            Developer: null,
            Publisher: null,
            ReleaseDate: null,
            MetacriticScore: null,
            OsList: null,
            ChangeNumber: 1,
            LastUpdated: null);
}
