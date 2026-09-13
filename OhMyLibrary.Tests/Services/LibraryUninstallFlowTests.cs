using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OhMyLibrary.App.Services;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Tests.Fakes;
using OhMyLibrary.Tests.Infrastructure;

namespace OhMyLibrary.Tests.Services;

/// <summary>
/// The uninstall path end to end: Steam deletes an <c>.acf</c>, the watcher reports it, the
/// coordinator forces a rescan, and a UI-side consumer is told which app stopped being installed.
/// </summary>
/// <remarks>
/// <para>
/// The pieces are pinned separately elsewhere — <see cref="GameLibraryServiceTests"/> covers the
/// change event naming cleared apps, <see cref="LibrarySyncCoordinatorTests"/> covers the forced
/// rescan — but nothing covered them joined up, and the joint is where an uninstall used to go
/// missing. So this uses the <i>real</i> <see cref="GameLibraryService"/> and the <i>real</i>
/// <see cref="LibrarySyncCoordinator"/>, with fakes only at the edges (disk, database, network).
/// </para>
/// <para>
/// It also pins the reason the watcher forces its rescan: the second scan happens well inside
/// <see cref="SyncOptions.LocalRescanCooldownSeconds"/>, so an unforced request would have been
/// skipped and the card would have kept offering to play a game that is gone.
/// </para>
/// </remarks>
public sealed class LibraryUninstallFlowTests
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
    private readonly FakeSteamWatcherService _watcher = new();

    private readonly Lock _sync = new();
    private readonly List<LibraryChangedEventArgs> _observed = [];
    private readonly CountingSignal _localChanges = new("LibraryChanged(Local)");

    [Fact]
    public async Task ADeletedManifestReachesAConsumerAsAChangeThatNamesTheUninstalledApp()
    {
        _api.IsConfigured = false;

        // Both games are installed and both manifests are on disk.
        _games.Rows.Add(Row(570, "Dota 2", installed: true));
        _games.Rows.Add(Row(292030, "The Witcher 3", installed: true));
        _acf.Apps.Add(Installed(570, "Dota 2"));
        _acf.Apps.Add(Installed(292030, "The Witcher 3"));

        var library = Library();
        library.LibraryChanged += OnLibraryChanged;

        using var coordinator = new LibrarySyncCoordinator(
            library,
            _watcher,
            _assets,
            new FakeImageCacheService(),
            NullLogger<LibrarySyncCoordinator>.Instance);

        await coordinator.StartAsync(CancellationToken.None);
        await _localChanges.WaitAsync();

        Assert.Equal([570, 292030], Change(0).AppIds.Order().ToArray());

        // Steam uninstalls The Witcher 3: its manifest is gone, and the watcher says so. The
        // database row still says installed, which is exactly the state the rescan has to clear.
        _ = _acf.Apps.RemoveAll(app => app.AppId == 292030);
        _watcher.Raise(SteamFileChangeKind.AppManifest, 292030);

        await _localChanges.WaitAsync(2);

        // The C1 fix: the app whose manifest vanished is named, so a consumer applying a partial
        // update knows to stop offering "Play" for it. Naming only what is still installed would
        // leave that card stale for the rest of the session.
        var uninstall = Change(1);
        Assert.Equal(LibraryChangeKind.Local, uninstall.Kind);
        Assert.Contains(292030, uninstall.AppIds);
        Assert.Contains(570, uninstall.AppIds);

        // ... and the row really was cleared, rather than the id being reported cosmetically.
        Assert.Equal([570], _games.NotInstalledExcept[^1].Order().ToArray());

        // The scan happened at all only because the watcher forces: the startup scan is seconds old
        // and LocalRescanCooldownSeconds is a minute.
        Assert.Equal(2, _acf.ReadAllCalls);

        library.LibraryChanged -= OnLibraryChanged;
        await coordinator.StopAsync(CancellationToken.None);
    }

    private static GameEntry Row(int appId, string name, bool installed) =>
        new(
            AppId: appId,
            Name: name,
            IsOwned: true,
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
            AppType: "game",
            FriendOwnerIds: [],
            CollectionIds: [],
            LastLocalScanUtc: null);

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

    private GameLibraryService Library() =>
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
            Options.Create(new SteamOptions()),
            Options.Create(new SyncOptions()),
            NullLogger<GameLibraryService>.Instance);

    private LibraryChangedEventArgs Change(int index)
    {
        lock (_sync)
        {
            return _observed[index];
        }
    }

    private void OnLibraryChanged(object? sender, LibraryChangedEventArgs e)
    {
        if (e.Kind != LibraryChangeKind.Local)
        {
            return;
        }

        lock (_sync)
        {
            _observed.Add(e);
        }

        _localChanges.Raise();
    }
}
