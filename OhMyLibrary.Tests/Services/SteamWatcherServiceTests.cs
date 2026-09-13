using Microsoft.Extensions.Logging.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Tests.Fakes;
using OhMyLibrary.Tests.Infrastructure;

using Xunit.Sdk;

namespace OhMyLibrary.Tests.Services;

/// <summary>
/// <see cref="SteamWatcherService"/> against real <see cref="FileSystemWatcher"/> instances over a
/// throwaway Steam tree.
/// </summary>
/// <remarks>
/// <para>
/// These are deliberately not fakes. The two things worth pinning are exactly the two a fake cannot
/// prove: that every <see cref="SteamFileChangeKind"/> is raised for the file it claims to describe —
/// a subscriber branching on the kind is only as correct as that mapping — and that the watched
/// folder set follows <c>libraryfolders.vdf</c> instead of being frozen at <see cref="SteamWatcherService.Start"/>.
/// </para>
/// <para>
/// <see cref="SteamWatcherService.DebounceInterval"/> is shortened so the suite does not spend the
/// production 750 ms of quiet per assertion; the waits below still allow seconds, because the time
/// the OS takes to deliver a directory notification is nobody's to promise.
/// </para>
/// </remarks>
public sealed class SteamWatcherServiceTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeSteam _steam = new("watch");
    private readonly FakeSteamPathResolver _paths = new();
    private readonly SteamWatcherService _watcher;

    private readonly Lock _gate = new();
    private readonly List<SteamFilesChangedEventArgs> _events = [];

    /// <summary>Builds a Steam tree whose root is also its first library, and points the watcher at it.</summary>
    public SteamWatcherServiceTests()
    {
        Directory.CreateDirectory(_steam.LibraryCacheDirectory);

        _paths.SteamPath = _steam.Root;
        _paths.LibraryFolders.Add(Library(_steam.Root));

        _watcher = new SteamWatcherService(_paths, NullLogger<SteamWatcherService>.Instance)
        {
            DebounceInterval = Debounce,
        };

        _watcher.Changed += (_, e) =>
        {
            lock (_gate)
            {
                _events.Add(e);
            }
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _watcher.Dispose();
        _steam.Dispose();
    }

    [Fact]
    public void Start_ArmsTheSteamRootFilesTheLibraryManifestsAndTheArtCache()
    {
        _watcher.Start();

        Assert.Equal(
            [
                Path.Combine(_steam.AppCacheDirectory, "appinfo.vdf"),
                Path.Combine(_steam.LibraryCacheDirectory, "*"),
                Path.Combine(_steam.ConfigDirectory, "libraryfolders.vdf"),
                Path.Combine(_steam.ConfigDirectory, "loginusers.vdf"),
                Path.Combine(_steam.SteamAppsDirectory, "appmanifest_*.acf"),
                Path.Combine(_steam.SteamAppsDirectory, "libraryfolders.vdf"),
            ],
            _watcher.WatchedPaths.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void Start_IsIdempotentAndDoesNotDoubleTheWatchers()
    {
        _watcher.Start();
        var armed = _watcher.WatchedPaths;

        _watcher.Start();
        _watcher.Start();

        Assert.Equal(armed, _watcher.WatchedPaths);
    }

    [Fact]
    public void StartSurvivesAMachineWithNoSteam()
    {
        _paths.SteamPath = null;
        _paths.LibraryFolders.Clear();

        _watcher.Start();

        Assert.Empty(_watcher.WatchedPaths);
    }

    [Fact]
    public async Task AManifestChangeIsReportedAsAppManifestWithItsAppId()
    {
        _watcher.Start();

        File.WriteAllText(Path.Combine(_steam.SteamAppsDirectory, "appmanifest_570.acf"), "\"AppState\" {}");

        var change = await WaitForAsync(SteamFileChangeKind.AppManifest).ConfigureAwait(true);

        Assert.Equal([570], change.AppIds.ToArray());
    }

    [Fact]
    public async Task AManifestBurstIsCoalescedIntoOneEventNamingEveryApp()
    {
        _watcher.Start();

        foreach (var appId in new[] { 570, 292030, 1091500 })
        {
            File.WriteAllText(
                Path.Combine(_steam.SteamAppsDirectory, $"appmanifest_{appId}.acf"),
                "\"AppState\" {}");
        }

        var change = await WaitForAsync(
            SteamFileChangeKind.AppManifest,
            e => e.AppIds.Count == 3).ConfigureAwait(true);

        Assert.Equal([570, 292030, 1091500], change.AppIds.Order().ToArray());
    }

    [Fact]
    public async Task ADeletedManifestStillNamesItsApp()
    {
        var manifest = Path.Combine(_steam.SteamAppsDirectory, "appmanifest_570.acf");
        File.WriteAllText(manifest, "\"AppState\" {}");

        _watcher.Start();
        File.Delete(manifest);

        var change = await WaitForAsync(SteamFileChangeKind.AppManifest).ConfigureAwait(true);

        Assert.Equal([570], change.AppIds.ToArray());
    }

    [Fact]
    public async Task ALoginUsersChangeIsReportedAsLoginUsersWithNoAppIds()
    {
        _watcher.Start();

        File.WriteAllText(Path.Combine(_steam.ConfigDirectory, "loginusers.vdf"), "\"users\" {}");

        var change = await WaitForAsync(SteamFileChangeKind.LoginUsers).ConfigureAwait(true);

        Assert.Empty(change.AppIds);
    }

    [Fact]
    public async Task AnAppInfoChangeIsReportedAsAppInfoWithNoAppIds()
    {
        _watcher.Start();

        File.WriteAllBytes(Path.Combine(_steam.AppCacheDirectory, "appinfo.vdf"), [0x29, 0x44, 0x56, 0x07]);

        var change = await WaitForAsync(SteamFileChangeKind.AppInfo).ConfigureAwait(true);

        Assert.Empty(change.AppIds);
    }

    [Fact]
    public async Task ALibraryFoldersChangeIsReportedFromEitherOfTheTwoLocations()
    {
        _watcher.Start();

        File.WriteAllText(Path.Combine(_steam.SteamAppsDirectory, "libraryfolders.vdf"), "\"libraryfolders\" {}");
        Assert.Empty((await WaitForAsync(SteamFileChangeKind.LibraryFolders).ConfigureAwait(true)).AppIds);

        ClearEvents();

        File.WriteAllText(Path.Combine(_steam.ConfigDirectory, "libraryfolders.vdf"), "\"libraryfolders\" {}");
        Assert.Empty((await WaitForAsync(SteamFileChangeKind.LibraryFolders).ConfigureAwait(true)).AppIds);
    }

    [Fact]
    public async Task ArtWrittenIntoAHashSubfolderIsReportedWithTheAppIdAboveIt()
    {
        // The art of one app lives at librarycache/<appid>/<opaque hash>/library_capsule.jpg, so the
        // cache is watched recursively and the app id comes from the first segment below the root.
        _watcher.Start();

        _steam.WriteLibraryArt(570, Path.Combine("6843027380c3bfd0952449fd9174f492ef2e7b40", "library_capsule.jpg"));

        var change = await WaitForAsync(
            SteamFileChangeKind.LibraryCache,
            e => e.AppIds.Count > 0).ConfigureAwait(true);

        Assert.Equal([570], change.AppIds.ToArray());
    }

    [Fact]
    public async Task ALooseArtFileIsReportedWithItsAppIdToo()
    {
        _watcher.Start();

        _steam.WriteLibraryArt(292030, "library_600x900.jpg");

        var change = await WaitForAsync(
            SteamFileChangeKind.LibraryCache,
            e => e.AppIds.Count > 0).ConfigureAwait(true);

        Assert.Equal([292030], change.AppIds.ToArray());
    }

    [Fact]
    public async Task ALibraryAddedAfterStartStartsBeingWatched()
    {
        // C3: the folder set used to be computed once, so a library added while the app was running
        // was detected — libraryfolders.vdf is watched — and then never actually watched itself.
        _watcher.Start();

        var added = _steam.AddLibrary("SteamLibrary");
        var addedManifests = Path.Combine(added, "steamapps");
        Assert.DoesNotContain(
            Path.Combine(addedManifests, "appmanifest_*.acf"),
            _watcher.WatchedPaths,
            StringComparer.OrdinalIgnoreCase);

        _paths.LibraryFolders.Add(Library(added));
        File.WriteAllText(Path.Combine(_steam.SteamAppsDirectory, "libraryfolders.vdf"), "\"libraryfolders\" {}");

        await WaitForAsync(SteamFileChangeKind.LibraryFolders).ConfigureAwait(true);

        Assert.Contains(
            Path.Combine(addedManifests, "appmanifest_*.acf"),
            _watcher.WatchedPaths,
            StringComparer.OrdinalIgnoreCase);

        // And it is genuinely armed, not merely listed.
        ClearEvents();
        File.WriteAllText(Path.Combine(addedManifests, "appmanifest_1091500.acf"), "\"AppState\" {}");

        var change = await WaitForAsync(SteamFileChangeKind.AppManifest).ConfigureAwait(true);
        Assert.Equal([1091500], change.AppIds.ToArray());
    }

    [Fact]
    public async Task ALibraryRemovedAfterStartStopsBeingWatched()
    {
        var removable = _steam.AddLibrary("Removable");
        _paths.LibraryFolders.Add(Library(removable));

        _watcher.Start();
        var watchedPath = Path.Combine(removable, "steamapps", "appmanifest_*.acf");
        Assert.Contains(watchedPath, _watcher.WatchedPaths, StringComparer.OrdinalIgnoreCase);

        _paths.LibraryFolders.RemoveAll(folder =>
            string.Equals(folder.Path, removable, StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(Path.Combine(_steam.SteamAppsDirectory, "libraryfolders.vdf"), "\"libraryfolders\" {}");

        await WaitForAsync(SteamFileChangeKind.LibraryFolders).ConfigureAwait(true);

        Assert.DoesNotContain(watchedPath, _watcher.WatchedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshPicksUpADriveThatWasUnpluggedAtStart()
    {
        // The plan's graceful degradation: an unreachable library is skipped instead of failing
        // Start, and has to be able to come back without restarting the app.
        var unplugged = Path.Combine(_steam.UnreachablePath, "SteamLibrary");
        _paths.LibraryFolders.Add(Library(unplugged));

        _watcher.Start();
        var watchedPath = Path.Combine(unplugged, "steamapps", "appmanifest_*.acf");
        Assert.DoesNotContain(watchedPath, _watcher.WatchedPaths, StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(Path.Combine(unplugged, "steamapps"));
        _watcher.Refresh();

        Assert.Contains(watchedPath, _watcher.WatchedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshLeavesAnUnchangedFolderSetExactlyAsItWas()
    {
        _watcher.Start();
        var armed = _watcher.WatchedPaths;

        _watcher.Refresh();
        _watcher.Refresh();
        _watcher.Refresh();

        // Same set, same size: nothing was rebuilt and nothing was leaked alongside its replacement.
        Assert.Equal(armed, _watcher.WatchedPaths);
    }

    [Fact]
    public void RefreshWhileStoppedIsANoOp()
    {
        _watcher.Refresh();
        Assert.Empty(_watcher.WatchedPaths);

        _watcher.Start();
        _watcher.Stop();
        _watcher.Refresh();

        Assert.Empty(_watcher.WatchedPaths);
    }

    [Fact]
    public async Task StopSilencesTheEvents()
    {
        _watcher.Start();
        _watcher.Stop();

        File.WriteAllText(Path.Combine(_steam.SteamAppsDirectory, "appmanifest_570.acf"), "\"AppState\" {}");
        await Task.Delay(Debounce + TimeSpan.FromMilliseconds(400)).ConfigureAwait(true);

        lock (_gate)
        {
            Assert.Empty(_events);
        }
    }

    [Fact]
    public async Task ArmingDoesNotHoldTheStateLockWhileTheResolverBlocks()
    {
        // W1: planning the target set reads the registry and libraryfolders.vdf, and arming then opens a
        // handle on every library root — seconds, on a drive that has spun down. None of that may
        // happen under the lock the rest of the service takes, or a dead drive freezes live updates.
        _watcher.Start();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _paths.BeforeGetLibraryFolders = () =>
        {
            _ = entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        var refresh = Task.Run(_watcher.Refresh);
        try
        {
            await entered.Task.ConfigureAwait(true);

            // Reading the armed set takes the lock the arm is no longer holding.
            var probe = Task.Run(() => _watcher.WatchedPaths.Count);
            Assert.Same(probe, await Task.WhenAny(probe, Task.Delay(Timeout)).ConfigureAwait(true));
            Assert.True(await probe.ConfigureAwait(true) > 0);

            // And so does collecting a file event, so changes keep being reported meanwhile.
            File.WriteAllText(Path.Combine(_steam.SteamAppsDirectory, "appmanifest_570.acf"), "\"AppState\" {}");

            var change = await WaitForAsync(SteamFileChangeKind.AppManifest).ConfigureAwait(true);
            Assert.Equal([570], change.AppIds.ToArray());
        }
        finally
        {
            _ = release.TrySetResult();
            _paths.BeforeGetLibraryFolders = null;
        }

        await refresh.ConfigureAwait(true);
    }

    [Fact]
    public async Task AnArmStillRunningWhenTheWatcherStopsInstallsNothing()
    {
        // The flip side of arming outside the lock: the state can move while a watcher is being
        // built. One that belongs to a session that has ended is disposed, never installed.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _paths.BeforeGetLibraryFolders = () =>
        {
            _ = entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        var start = Task.Run(_watcher.Start);
        try
        {
            await entered.Task.ConfigureAwait(true);

            var stop = Task.Run(_watcher.Stop);
            Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(Timeout)).ConfigureAwait(true));
            await stop.ConfigureAwait(true);
        }
        finally
        {
            _ = release.TrySetResult();
            _paths.BeforeGetLibraryFolders = null;
        }

        await start.ConfigureAwait(true);

        Assert.Empty(_watcher.WatchedPaths);
    }

    [Fact]
    public async Task ATransientLibraryFolderReadFailureKeepsTheManifestWatchers()
    {
        // W2: a read that failed says nothing about whether the folders are still there. Retiring
        // them on it disarmed manifest watching for the rest of the session, because the only thing
        // that re-arms them is the folder set being read successfully again.
        _watcher.Start();
        var armed = _watcher.WatchedPaths;
        var manifests = Path.Combine(_steam.SteamAppsDirectory, "appmanifest_*.acf");
        Assert.Contains(manifests, armed, StringComparer.OrdinalIgnoreCase);

        _paths.BeforeGetLibraryFolders = () => throw new IOException("the drive hiccuped");
        _watcher.Refresh();

        Assert.Equal(armed, _watcher.WatchedPaths);

        // Still genuinely armed, not merely still listed.
        File.WriteAllText(Path.Combine(_steam.SteamAppsDirectory, "appmanifest_570.acf"), "\"AppState\" {}");
        var change = await WaitForAsync(SteamFileChangeKind.AppManifest).ConfigureAwait(true);
        Assert.Equal([570], change.AppIds.ToArray());

        // And the next successful read changes nothing either.
        _paths.BeforeGetLibraryFolders = null;
        _watcher.Refresh();
        Assert.Equal(armed, _watcher.WatchedPaths);
    }

    [Fact]
    public void ATransientFailureToLocateSteamKeepsTheSteamRootWatchers()
    {
        _watcher.Start();
        var armed = _watcher.WatchedPaths;

        _paths.BeforeFindSteamPath = () => throw new IOException("the registry hiccuped");
        _watcher.Refresh();

        Assert.Equal(armed, _watcher.WatchedPaths);
    }

    [Fact]
    public void SteamActuallyGoingAwayStillRetiresItsWatchers()
    {
        // The other half of the distinction: an answer of "there is no Steam here" is knowledge, and
        // it does retire the folders under the Steam root. The library's manifests come from the
        // other source, which still answered, so they stay.
        _watcher.Start();

        _paths.SteamPath = null;
        _watcher.Refresh();

        Assert.Equal(
            [Path.Combine(_steam.SteamAppsDirectory, "appmanifest_*.acf")],
            _watcher.WatchedPaths.ToArray());
    }

    [Fact]
    public async Task AnArtPathThatNamesNoAppLeavesTheWholeBurstWithoutIds()
    {
        // W4: an empty AppIds list is documented to mean "the ids are unknown, treat the category as
        // dirty". A path that carries no app id — assetcache.vdf sits beside the app folders, not
        // inside one — has to raise exactly that, instead of dropping out of the burst and leaving a
        // partial list that a subscriber cannot tell from a complete one.
        //
        // The debounce is widened so the two writes below are provably one burst: a flush needs that
        // much quiet after the last event, and both files are written in the same instant.
        _watcher.DebounceInterval = TimeSpan.FromMilliseconds(500);
        _watcher.Start();

        _steam.WriteLibraryArt(570, "library_600x900.jpg");
        File.WriteAllText(Path.Combine(_steam.LibraryCacheDirectory, "assetcache.vdf"), "\"assetcache\" {}");

        _ = await WaitForAsync(SteamFileChangeKind.LibraryCache).ConfigureAwait(true);

        Assert.All(EventsOf(SteamFileChangeKind.LibraryCache), change => Assert.Empty(change.AppIds));
    }

    [Fact]
    public async Task AManifestNameThatCarriesNoAppIdLeavesTheWholeBurstWithoutIds()
    {
        // The same contract on the other kind that can carry ids: appmanifest_*.acf is a file name
        // pattern, and a name matching it whose id will not parse is not evidence that only the
        // manifests that did parse moved.
        _watcher.DebounceInterval = TimeSpan.FromMilliseconds(500);
        _watcher.Start();

        File.WriteAllText(Path.Combine(_steam.SteamAppsDirectory, "appmanifest_570.acf"), "\"AppState\" {}");
        File.WriteAllText(Path.Combine(_steam.SteamAppsDirectory, "appmanifest_tmp.acf"), "\"AppState\" {}");

        _ = await WaitForAsync(SteamFileChangeKind.AppManifest).ConfigureAwait(true);

        Assert.All(EventsOf(SteamFileChangeKind.AppManifest), change => Assert.Empty(change.AppIds));
    }

    private static SteamLibraryFolder Library(string path) =>
        new(path, string.Empty, 0, new Dictionary<int, long>());

    private List<SteamFilesChangedEventArgs> EventsOf(SteamFileChangeKind kind)
    {
        lock (_gate)
        {
            return [.. _events.Where(e => e.Kind == kind)];
        }
    }

    private void ClearEvents()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }

    /// <summary>
    /// Waits for an event of one kind, optionally one that also satisfies a predicate — a burst can
    /// be flushed in more than one piece, so a test that needs the whole burst says so.
    /// </summary>
    private async Task<SteamFilesChangedEventArgs> WaitForAsync(
        SteamFileChangeKind kind,
        Func<SteamFilesChangedEventArgs, bool>? predicate = null)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                var match = _events.FindLast(e => e.Kind == kind && (predicate is null || predicate(e)));
                if (match is not null)
                {
                    return match;
                }
            }

            await Task.Delay(20).ConfigureAwait(true);
        }

        string seen;
        lock (_gate)
        {
            seen = _events.Count == 0
                ? "nothing"
                : string.Join(", ", _events.Select(e => $"{e.Kind}[{string.Join('/', e.AppIds)}]"));
        }

        throw new XunitException($"No {kind} event arrived within {Timeout.TotalSeconds:0}s; saw {seen}.");
    }
}

/// <summary>
/// The same watcher pointed at the machine's real Steam installation, read-only.
/// </summary>
/// <remarks>
/// The fake tree proves the mapping; this proves the four Steam-root paths the mapping names are the
/// paths a real client actually has, which is the assumption a fixture can never check. Nothing here
/// writes: it arms the watchers, reads back what was armed and disposes them.
/// </remarks>
public sealed class SteamWatcherServiceLiveTests
{
    [RequiresSteamFact]
    public void TheRealInstallHasEveryFileTheWatcherClaimsToWatch()
    {
        var steamPath = SteamInstall.Path!;

        Assert.True(File.Exists(Path.Combine(steamPath, "appcache", "appinfo.vdf")), "appinfo.vdf");
        Assert.True(File.Exists(Path.Combine(steamPath, "config", "loginusers.vdf")), "loginusers.vdf");
        Assert.True(
            File.Exists(Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"))
            || File.Exists(Path.Combine(steamPath, "config", "libraryfolders.vdf")),
            "libraryfolders.vdf");
        Assert.True(Directory.Exists(Path.Combine(steamPath, "appcache", "librarycache")), "librarycache");
    }

    [RequiresSteamFact]
    public void TheArtCacheOfARealInstallCanBeWatchedRecursively()
    {
        // C4 measured before committing: the reference install's librarycache is 652 MB over ~3000
        // directories, and a recursive watcher over it is still one subtree registration that arms
        // in the time it takes to check the folder exists.
        var paths = new FakeSteamPathResolver { SteamPath = SteamInstall.Path };

        using var watcher = new SteamWatcherService(paths, NullLogger<SteamWatcherService>.Instance);
        watcher.Start();

        Assert.Contains(
            Path.Combine(SteamInstall.Path!, "appcache", "librarycache", "*"),
            watcher.WatchedPaths,
            StringComparer.OrdinalIgnoreCase);
    }
}

