using Microsoft.Extensions.Logging;

using OhMyLibrary.App.Services;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Tests.Fakes;
using OhMyLibrary.Tests.Infrastructure;

namespace OhMyLibrary.Tests.Services;

/// <summary>
/// <see cref="LibrarySyncCoordinator"/> against hand-written fakes.
/// </summary>
/// <remarks>
/// <para>
/// This class owns the startup order, the swallow-and-continue guards, the rescan coalescing, the
/// watcher lifecycle and the shutdown wait — behaviour that used to be pinned only by launching the
/// executable and reading a log. The tests are written against the observable contract: which
/// collaborator was called, in what order, and with which <c>force</c> flag.
/// </para>
/// <para>
/// Every wait here is a wait for something the coordinator actually did (<see cref="CountingSignal"/>),
/// and every "in flight" state is a <see cref="TaskCompletionSource"/> the test completes
/// (<see cref="FakeGameLibraryService.LocalScanGate"/>, <see cref="FakeSteamWatcherService.StartGate"/>).
/// The one exception is <see cref="NotYetWindow"/>, used only where the claim itself is negative —
/// "this must <i>not</i> have happened yet". A correct implementation never completes inside that
/// window, so the guard cannot fail spuriously; it can only miss a regression on an absurdly slow
/// machine.
/// </para>
/// </remarks>
public sealed class LibrarySyncCoordinatorTests
{
    /// <summary>How long a "this must not have happened yet" assertion gives a wrong answer to appear.</summary>
    private static readonly TimeSpan NotYetWindow = TimeSpan.FromMilliseconds(500);

    private readonly CallLog _calls = new();
    private readonly RecordingLogger<LibrarySyncCoordinator> _logger = new();
    private readonly FakeGameLibraryService _library;
    private readonly FakeSteamWatcherService _watcher;
    private readonly FakeLibraryAssetResolver _assets = new();
    private FakeImageCacheService _images = new();

    /// <summary>Wires the fakes into one shared ordering journal.</summary>
    public LibrarySyncCoordinatorTests()
    {
        _library = new FakeGameLibraryService(_calls);
        _watcher = new FakeSteamWatcherService { Journal = _calls };
        _images = new FakeImageCacheService { Journal = _calls };
    }

    [Fact]
    public async Task StartAsync_QueuesTheSyncAndReturnsWithoutWaitingForIt()
    {
        // The window is already up by the time this hosted service starts, so blocking here would
        // hold the shell behind a disk scan.
        var gate = NewGate();
        _library.LocalScanGate = gate;

        using var coordinator = Coordinator();

        var start = coordinator.StartAsync(CancellationToken.None);

        Assert.True(start.IsCompletedSuccessfully);

        await _library.LocalScanStarted.WaitAsync();
        Assert.Equal(0, _library.LocalScanCompleted.Count);

        gate.SetResult();
        await _library.RemoteStarted.WaitAsync();
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InitialSync_RunsTheStepsInTheDocumentedOrder()
    {
        using var coordinator = Coordinator();

        await coordinator.StartAsync(CancellationToken.None);
        await _library.RemoteStarted.WaitAsync();

        Assert.Equal(
            [
                FakeSteamWatcherService.SubscribeCall,
                FakeSteamWatcherService.StartCall,
                FakeSteamWatcherService.StartDoneCall,
                FakeSteamWatcherService.RefreshCall,
                FakeGameLibraryService.LocalScanCall(force: false),
                FakeGameLibraryService.LocalScanDoneCall,
                FakeGameLibraryService.MetadataCall,
                FakeGameLibraryService.RemoteCall,
            ],
            _calls.Entries);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InitialSync_ArmsTheWatcherBeforeEitherRefreshRuns()
    {
        // The A3 fix, and the exact scope of it: both of this sync's refreshes can sit on a stalled
        // network for well over a minute, and arming behind them left live updates dead for that
        // long on every launch. It is not a claim about the process — the window and the library
        // page's own load are already running by the time StartAsync is called — only about the
        // order inside this coordinator.
        var gate = NewGate();
        _library.LocalScanGate = gate;

        using var coordinator = Coordinator();

        await coordinator.StartAsync(CancellationToken.None);
        await _library.LocalScanStarted.WaitAsync();

        Assert.True(_watcher.IsWatching);
        Assert.True(_watcher.HasSubscribers);
        Assert.Equal(0, _library.MetadataStarted.Count);
        Assert.Equal(0, _library.RemoteStarted.Count);

        gate.SetResult();
        await _library.RemoteStarted.WaitAsync();
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InitialSync_SwallowsAFailingStepAndStillRunsTheRest()
    {
        var failure = new InvalidOperationException("appinfo.vdf is truncated");
        _library.MetadataFailure = failure;

        using var coordinator = Coordinator();

        await coordinator.StartAsync(CancellationToken.None);
        await _library.RemoteStarted.WaitAsync();

        Assert.Equal(1, _library.LocalScanCompleted.Count);
        Assert.Equal(1, _library.RemoteStarted.Count);

        // Swallowed, not ignored: a partial library is a supported state, a silent failure is not.
        Assert.Contains(
            _logger.Entries,
            entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure));

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InitialSync_SurvivesALocalScanThatThrows()
    {
        var failure = new IOException("the library folder went away mid-scan");
        _library.LocalScanFailure = failure;

        using var coordinator = Coordinator();

        await coordinator.StartAsync(CancellationToken.None);
        await _library.RemoteStarted.WaitAsync();

        Assert.Equal(1, _library.MetadataStarted.Count);
        Assert.Equal(1, _library.RemoteStarted.Count);
        Assert.Contains(
            _logger.Entries,
            entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure));

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InitialSync_SurvivesAWatcherThatCannotBeArmed()
    {
        // A locked-down or unreadable Steam folder degrades to manual refresh. It must not cost the
        // user the rest of the sync, and it must not leave a dead subscription behind.
        var failure = new IOException("access to the Steam folder was denied");
        _watcher.StartFailure = failure;

        using var coordinator = Coordinator();

        await coordinator.StartAsync(CancellationToken.None);
        await _library.RemoteStarted.WaitAsync();

        Assert.Equal(1, _library.LocalScanCompleted.Count);
        Assert.False(_watcher.HasSubscribers);
        Assert.False(_watcher.IsWatching);
        Assert.Equal(0, _watcher.RefreshCalls);
        Assert.Contains(
            _logger.Entries,
            entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure));

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARequestArrivingBeforeTheStartupScan_IsFoldedIntoIt()
    {
        // MainWindow.Activated fires inside ShowWindow(), which the hosted service registered before
        // this one runs, so the shell's first rescan request lands before StartAsync. Exactly one
        // scan must come out of that, and it must honour the force flag the request carried.
        using var coordinator = Coordinator();

        coordinator.RequestLocalRescan(force: true);
        Assert.Equal(0, _library.LocalScanStarted.Count);

        await coordinator.StartAsync(CancellationToken.None);
        await _library.RemoteStarted.WaitAsync();

        Assert.Equal([true], _library.LocalScanForceFlags);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AWatcherEvent_ForcesARescanAndReArmsTheWatcherFirst()
    {
        using var coordinator = Coordinator();
        await StartAndSettleAsync(coordinator);

        _watcher.Raise(SteamFileChangeKind.AppManifest, 570);

        await _library.LocalScanStarted.WaitAsync(2);

        // Forced, because Steam has just rewritten a manifest and the card must flip in seconds
        // instead of waiting the focus cooldown out.
        Assert.Equal([false, true], _library.LocalScanForceFlags);

        // Re-armed first: the cheap recovery for a library drive replugged without Steam rewriting
        // libraryfolders.vdf.
        Assert.Equal(2, _watcher.RefreshCalls);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AWindowActivation_AsksForAnUnforcedRescan()
    {
        using var coordinator = Coordinator();
        await StartAndSettleAsync(coordinator);

        // What MainWindow.OnWindowActivated does on every focus. Unforced, so the cooldown keeps
        // focus thrash off the disk.
        coordinator.RequestLocalRescan(force: false);

        await _library.LocalScanStarted.WaitAsync(2);

        Assert.Equal([false, false], _library.LocalScanForceFlags);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ALibraryCacheEvent_DropsEveryCacheBetweenTheFileAndTheScreen()
    {
        // The C4 chain. Steam rewrote 570's capsule where it stood and moved its header into a new
        // hash folder. Three caches sit between that and the pixel and every one of them has to go.
        _assets.Assets[570] = new GameAssets(570, @"C:\cache\570\library_capsule.jpg", @"C:\cache\570\a\library_header.jpg", null, null, null);
        _assets.AssetsAfterInvalidation[570] = new GameAssets(570, @"C:\cache\570\library_capsule.jpg", @"C:\cache\570\b\library_header.jpg", null, null, null);

        using var coordinator = Coordinator();
        await StartAndSettleAsync(coordinator);

        _watcher.Raise(SteamFileChangeKind.LibraryCache, 570);

        // 1. the path cache
        Assert.Equal([570], _assets.Invalidated.ToArray());

        // 2. the decoded bitmaps — the path that did not move above all, because a bitmap keyed by a
        //    path Steam overwrote in place is the one a corrected path cannot save.
        Assert.Contains(@"C:\cache\570\library_capsule.jpg", _images.Invalidated);
        Assert.Contains(@"C:\cache\570\a\library_header.jpg", _images.Invalidated);
        Assert.Contains(@"C:\cache\570\b\library_header.jpg", _images.Invalidated);

        // 3. the built rows, which carry the paths the grid binds to.
        Assert.Equal([570], Assert.Single(_library.AssetChangeNotifications));

        // And in that order: the library is told last, so a page reloading a card in response finds
        // both caches beneath it already clean.
        Assert.True(
            _calls.IndexOf(FakeImageCacheService.InvalidateCall) < _calls.IndexOf(FakeGameLibraryService.AssetsChangedCall),
            _calls.ToString());
    }

    [Fact]
    public async Task ALibraryCacheEventThatNamesNoApps_DropsEverything()
    {
        // An overflowed watcher buffer: no app id can be trusted, so nothing cached can be either.
        using var coordinator = Coordinator();
        await StartAndSettleAsync(coordinator);

        _watcher.Raise(SteamFileChangeKind.LibraryCache);

        Assert.Equal(1, _assets.InvalidateAllCalls);
        Assert.Equal(1, _images.ClearMemoryCalls);
        Assert.Empty(Assert.Single(_library.AssetChangeNotifications));
    }

    [Fact]
    public async Task StopAsync_CancelsTheWorkBeforeItTearsTheWatcherDown()
    {
        // The A4(c) fix. The watcher teardown blocks on the watcher's own lock, and the rescan loop
        // takes that lock to re-arm — so tearing down first parks shutdown behind a scan that has
        // not even been told to stop.
        var scanGate = NewGate();
        var stopGate = NewGate();
        _library.LocalScanGate = scanGate;
        _watcher.StopGate = stopGate;

        using var coordinator = Coordinator();

        await coordinator.StartAsync(CancellationToken.None);
        await _library.LocalScanStarted.WaitAsync();

        // On a thread of its own because the watcher teardown blocks, exactly as the real one does
        // on its own lock; StopAsync would otherwise park the test thread before it could look.
        var stop = Task.Run(() => coordinator.StopAsync(CancellationToken.None));

        // Shutdown is now parked inside the watcher teardown. The scan in flight must already have
        // been told to stop; with the old order its token would still be live.
        await _watcher.StopEntered.WaitAsync();
        Assert.True(_library.LastLocalScanToken.IsCancellationRequested);

        stopGate.SetResult();
        scanGate.SetResult();
        await stop;

        Assert.False(_watcher.IsWatching);
        Assert.True(_watcher.IsDisposed);
    }

    [Fact]
    public async Task ALibraryCacheEvent_DoesNotRescanTheManifests()
    {
        using var coordinator = Coordinator();
        await StartAndSettleAsync(coordinator);

        // Cached art has nothing to do with install state, and Core's asset resolver has already
        // invalidated the apps this event names.
        _watcher.Raise(SteamFileChangeKind.LibraryCache, 570);

        // Drive one unforced rescan behind it. Had the art event queued a rescan of its own, it
        // would show up either as this scan being forced or as a third scan.
        coordinator.RequestLocalRescan(force: false);

        await _library.LocalScanStarted.WaitAsync(2);
        await _library.LocalScanCompleted.WaitAsync(2);

        Assert.Equal([false, false], _library.LocalScanForceFlags);
        Assert.Equal(2, _watcher.RefreshCalls);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AForcedRequestDuringAScan_IsHonouredRatherThanDropped()
    {
        // The A2 fix. The scan in flight may have read the manifests before Steam finished rewriting
        // them, so a forced request landing mid-scan has to produce another pass.
        var gate = NewGate();
        _library.LocalScanGate = gate;

        using var coordinator = Coordinator();

        await coordinator.StartAsync(CancellationToken.None);
        await _library.LocalScanStarted.WaitAsync();

        coordinator.RequestLocalRescan(force: true);

        _library.LocalScanGate = null;
        gate.SetResult();

        await _library.LocalScanStarted.WaitAsync(2);

        Assert.Equal([false, true], _library.LocalScanForceFlags);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SeveralRequestsDuringAScan_CoalesceIntoOneRescan()
    {
        // Steam rewrites a manifest many times during a download. Every request must be honoured,
        // but as one further pass, not as one pass each.
        var gate = NewGate();
        _library.LocalScanGate = gate;

        using var coordinator = Coordinator();

        await coordinator.StartAsync(CancellationToken.None);
        await _library.LocalScanStarted.WaitAsync();

        coordinator.RequestLocalRescan(force: true);
        coordinator.RequestLocalRescan(force: false);
        coordinator.RequestLocalRescan(force: true);

        _library.LocalScanGate = null;
        gate.SetResult();

        await _library.RemoteStarted.WaitAsync();
        await _library.LocalScanCompleted.WaitAsync(2);

        Assert.Equal([false, true], _library.LocalScanForceFlags);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_WaitsForTheScanInFlightAndTakesTheWatcherDown()
    {
        // The A4 fix. Returning while a scan is still writing let the container dispose the SQLite
        // connection factory, and App.OnExit flush the log, underneath it.
        var gate = NewGate();
        _library.LocalScanGate = gate;

        using var coordinator = Coordinator();

        await coordinator.StartAsync(CancellationToken.None);
        await _library.LocalScanStarted.WaitAsync();

        var stop = coordinator.StopAsync(CancellationToken.None);

        var settled = await Task.WhenAny(stop, Task.Delay(NotYetWindow, CancellationToken.None));
        Assert.NotSame(stop, settled);
        Assert.Equal(0, _library.LocalScanCompleted.Count);

        gate.SetResult();
        await stop;

        Assert.Equal(1, _library.LocalScanCompleted.Count);
        Assert.Equal(0, _library.InFlightCalls);

        // Detached before stopped, so nothing can start work during the unwind.
        Assert.False(_watcher.HasSubscribers);
        Assert.False(_watcher.IsWatching);
        Assert.True(_watcher.IsDisposed);
    }

    [Fact]
    public async Task StopAsync_DuringTheInitialSyncSkipsTheStepsThatHaveNotRunYet()
    {
        // Waiting for the step in flight is the point; running the two after it is not. This is
        // fully determined: StopAsync waits for the whole initial sync, so by the time it returns,
        // the metadata and owned-games steps have had their chance and declined it.
        var gate = NewGate();
        _library.LocalScanGate = gate;

        using var coordinator = Coordinator();

        await coordinator.StartAsync(CancellationToken.None);
        await _library.LocalScanStarted.WaitAsync();

        var stop = coordinator.StopAsync(CancellationToken.None);
        gate.SetResult();
        await stop;

        Assert.Equal(1, _library.LocalScanCompleted.Count);
        Assert.Equal(0, _library.MetadataStarted.Count);
        Assert.Equal(0, _library.RemoteStarted.Count);
    }

    [Fact]
    public async Task AfterStopAsync_NeitherAWatcherEventNorARequestStartsWork()
    {
        using var coordinator = Coordinator();
        await StartAndSettleAsync(coordinator);

        await coordinator.StopAsync(CancellationToken.None);

        // Structural half: the handler is gone, so the watcher cannot reach the coordinator at all.
        Assert.False(_watcher.HasSubscribers);

        _watcher.Raise(SteamFileChangeKind.AppManifest, 570);
        coordinator.RequestLocalRescan(force: true);

        // Negative half: a rescan would start on a thread-pool thread, so give a wrong answer time
        // to appear before concluding that it did not.
        await Task.Delay(NotYetWindow, CancellationToken.None);

        Assert.Equal(1, _library.LocalScanStarted.Count);
        Assert.Equal(0, _library.InFlightCalls);
    }

    [Fact]
    public async Task StartAsync_IsIdempotentAndDoesNothingAfterStop()
    {
        using var coordinator = Coordinator();

        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);
        await _library.RemoteStarted.WaitAsync();

        Assert.Equal(1, _watcher.StartCalls);
        Assert.Equal(1, _library.LocalScanStarted.Count);
        Assert.Equal(1, _library.MetadataStarted.Count);

        await coordinator.StopAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(1, _library.LocalScanStarted.Count);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromTheWatcher()
    {
        var coordinator = Coordinator();

        try
        {
            await StartAndSettleAsync(coordinator);
            Assert.True(_watcher.HasSubscribers);
        }
        finally
        {
            coordinator.Dispose();
        }

        Assert.False(_watcher.HasSubscribers);
    }

    [Fact]
    public async Task RequestLocalRescan_FromTheDispatcherDoesNotWaitBehindTheWatcherArming()
    {
        // The A1 fix, asserted through its observable consequence. Arming probes every library
        // folder, which on a spun-down drive takes seconds, and the shell's Activated handler runs
        // on the dispatcher — it may never wait behind that.
        var arming = NewGate();
        _watcher.StartGate = arming;

        using var dispatcher = StaDispatcher.Start();
        using var coordinator = Coordinator();

        Assert.Equal(ApartmentState.STA, dispatcher.ApartmentState);

        await coordinator.StartAsync(CancellationToken.None);
        await _watcher.StartEntered.WaitAsync();

        var apartmentInsideHandler = ApartmentState.Unknown;

        var activated = dispatcher.InvokeAsync(() =>
        {
            apartmentInsideHandler = Thread.CurrentThread.GetApartmentState();
            coordinator.RequestLocalRescan(force: true);
        });

        // A lock held across Start() would park the dispatcher here until the gate opens. Nothing
        // opens it yet, so this either returns promptly or the test fails on the timeout.
        await activated.WaitAsync(CountingSignal.DefaultTimeout);

        Assert.Equal(ApartmentState.STA, apartmentInsideHandler);
        Assert.Equal(0, _library.LocalScanStarted.Count);

        arming.SetResult();
        await _library.RemoteStarted.WaitAsync();

        // And the request made while arming was still folded into the startup scan.
        Assert.Equal([true], _library.LocalScanForceFlags);

        await coordinator.StopAsync(CancellationToken.None);
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LibrarySyncCoordinator Coordinator() => new(_library, _watcher, _assets, _images, _logger);

    /// <summary>Starts the coordinator and waits until the whole initial sync has run.</summary>
    private async Task StartAndSettleAsync(LibrarySyncCoordinator coordinator)
    {
        await coordinator.StartAsync(CancellationToken.None);
        await _library.RemoteStarted.WaitAsync();
        await _library.LocalScanCompleted.WaitAsync();
    }
}
