using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Services;

namespace OhMyLibrary.App.Services;

/// <summary>
/// Populates the library on startup and keeps it current afterwards.
/// </summary>
/// <remarks>
/// <para>
/// Without this the first run renders nothing: every read path — <c>GetGamesAsync</c>,
/// <c>GetStatusAsync</c>, the name tables — answers out of a SQLite database that no one has written
/// to yet. The pages only ever read, so somebody has to do the first write, and it has to happen
/// without making the user look at an empty window while it does.
/// </para>
/// <para>
/// <see cref="StartAsync"/> therefore returns as soon as the background sync is queued, and that sync
/// runs in a fixed order. The <see cref="ISteamWatcherService"/> is armed before <i>this sync's</i>
/// refresh steps: nothing about watching the file system depends on a refresh, and both refreshes
/// below can sit on a stalled network for a minute and a half, which would otherwise leave live
/// updates dead for that long on every launch. Then comes the local <c>.acf</c> scan, which puts the
/// user's installed games on screen in a few hundred milliseconds; then <c>appinfo.vdf</c>, which
/// fills in types, genres and tags; and last the Web API call, which no-ops entirely without a key.
/// Each step is independent: one that throws is logged and skipped, and the rest still run. A first
/// run must never take the window down.
/// </para>
/// <para>
/// The ordering above is this coordinator's, and it is <b>not</b> a claim that nothing in the process
/// touches the network before the watcher is armed. It is not even true that this sync goes first:
/// the shell's hosted service is registered ahead of this one, so the main window — and with it
/// <c>LibraryPage</c>'s first navigation, whose load asks <see cref="ITagService"/> for the tag names
/// and can download the popular-tags list — is already running when <see cref="StartAsync"/> is
/// called. What matters is that the page's load and this sync run on different threads, so a page
/// load parked on a stalled download delays neither the arming below nor the scan behind it.
/// </para>
/// <para>
/// The first local scan of a session is this coordinator's, deterministically. The main window is
/// shown by the hosted service registered before this one, so the window's <c>Activated</c> handler
/// can ask for a rescan before <see cref="StartAsync"/> has even run; such a request is folded into
/// the initial sync's scan instead of starting one of its own. That is what stops the two paths from
/// taking turns being first, with whichever lost the race quietly no-opped by the scan cooldown.
/// </para>
/// <para>
/// Afterwards the watcher's debounced <see cref="ISteamWatcherService.Changed"/> event drives a
/// forced rescan, so a finished install flips its card to "Play" without waiting out the focus
/// cooldown.
/// </para>
/// </remarks>
public sealed class LibrarySyncCoordinator : ILibrarySyncCoordinator, IHostedService, IDisposable
{
    /// <summary>
    /// How long <see cref="StopAsync"/> waits before it starts saying out loud that the background
    /// sync has not unwound yet.
    /// </summary>
    /// <remarks>
    /// It is a logging threshold, not a deadline. Shutdown does not continue past the wait, because
    /// returning early lets the host dispose the SQLite connection factory — and <c>App.OnExit</c>
    /// flush the log — underneath a scan that is still writing, and the resulting exception would be
    /// logged nowhere.
    /// </remarks>
    public static readonly TimeSpan SlowStopWarningThreshold = TimeSpan.FromSeconds(2);

    private readonly IGameLibraryService _library;
    private readonly ISteamWatcherService _watcher;
    private readonly ILibraryAssetResolver _assets;
    private readonly IImageCacheService _images;
    private readonly ILogger<LibrarySyncCoordinator> _logger;

    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _lifetime = new();

    private Task _initialSync = Task.CompletedTask;
    private Task _rescan = Task.CompletedTask;

    /// <summary>
    /// The most recent art refresh. Art refreshes are driven from the watcher's debounce thread
    /// rather than from a command, so without this they are tracked by nothing and
    /// <see cref="StopAsync"/> can return — and the host dispose the image cache — while one is
    /// still inside it. Waiting on the latest is enough: each one takes
    /// <see cref="_artGate"/> first, so it cannot finish before its predecessor released it.
    /// </summary>
    private Task _artRefresh = Task.CompletedTask;

    /// <summary>Serialises art refreshes so two bursts cannot interleave over the same app.</summary>
    private readonly SemaphoreSlim _artGate = new(1, 1);

    private bool _rescanRunning;
    private bool _pendingForce;
    private bool _startupScanReached;
    private bool _startupForce;
    private bool _started;
    private bool _watching;
    private bool _stopped;
    private bool _disposed;

    /// <summary>Creates the coordinator. Nothing runs until <see cref="StartAsync"/> is called.</summary>
    /// <param name="library">The merged library view; the only thing that writes to our database.</param>
    /// <param name="watcher">Debounced watcher over Steam's directories.</param>
    /// <param name="assets">
    /// Resolves cached library art. Held so that a <see cref="SteamFileChangeKind.LibraryCache"/>
    /// change can be turned into "these files are stale" in a determined order — see
    /// <see cref="RefreshArt"/>.
    /// </param>
    /// <param name="images">
    /// The decoded-bitmap cache. It is keyed by path, and Steam usually rewrites art at the path it
    /// used before, so the paths above have to be dropped from it by name.
    /// </param>
    /// <param name="logger">Log sink. Every sync failure is logged here and swallowed.</param>
    public LibrarySyncCoordinator(
        IGameLibraryService library,
        ISteamWatcherService watcher,
        ILibraryAssetResolver assets,
        IImageCacheService images,
        ILogger<LibrarySyncCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(logger);

        _library = library;
        _watcher = watcher;
        _assets = assets;
        _images = images;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns as soon as the sync is queued. Blocking here would hold the generic host — and
    /// therefore the shell, whose hosted service has already shown the window — behind a disk scan.
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_stopped || _disposed || _started)
            {
                return Task.CompletedTask;
            }

            _started = true;

            var lifetime = _lifetime.Token;
            _initialSync = Task.Run(() => RunInitialSyncAsync(lifetime), CancellationToken.None);
        }

        _logger.LogInformation("The initial library sync was queued.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Cancellation comes <b>first</b>, before the watcher is touched. Taking the watcher down is a
    /// blocking teardown that contends for the watcher's own lock, and the rescan loop calls
    /// <see cref="ISteamWatcherService.Refresh"/> under that same lock — so tearing down before
    /// cancelling parks shutdown behind a scan that has not even been told to stop. Setting
    /// <c>_stopped</c> under the lock above has already closed the door on new work, so nothing is
    /// started by the reordering.
    /// </para>
    /// <para>
    /// Then the wait for what is in flight. <paramref name="cancellationToken"/> is deliberately not
    /// used to abandon that wait: abandoning it is the bug, not the remedy — the awaits in the sync
    /// honour the lifetime token cancelled here, and so do the manifest scan and the
    /// <c>appinfo.vdf</c> parse, per file and per record, so the unwind really is a matter of
    /// milliseconds rather than a whole scan.
    /// </para>
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task pending;

        lock (_sync)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _pendingForce = false;
            _startupForce = false;

            pending = Task.WhenAll(_initialSync, _rescan, _artRefresh);
        }

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed underneath us; there is nothing left to cancel.
        }

        DetachWatcher();
        StopWatcher();

        await AwaitUnwindAsync(pending).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void RequestLocalRescan(bool force)
    {
        lock (_sync)
        {
            if (_stopped || _disposed)
            {
                return;
            }

            if (!_startupScanReached)
            {
                // The window is up before this service starts, so its first activation lands here
                // ahead of the initial sync. Folding the request in keeps the startup order
                // deterministic: the initial sync always performs the first scan and honours the
                // force flag collected here, instead of the two racing and the loser being no-opped
                // by the scan cooldown.
                _startupForce |= force;
                return;
            }
        }

        _ = RunLocalRescanAsync(force);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        DetachWatcher();
        _lifetime.Dispose();
        _artGate.Dispose();
    }

    /// <summary>
    /// The watcher first, then cheapest source first, so the user sees their installed games before
    /// the network is touched. Each step is guarded on its own: a broken <c>appinfo.vdf</c> must not
    /// cost us the owned list.
    /// </summary>
    private async Task RunInitialSyncAsync(CancellationToken ct)
    {
        // Before this sync's own refresh steps. Watching depends on neither of them, and a stalled
        // Web API call would otherwise keep live updates dead for the first ~110 seconds of the
        // session. Arming ahead of the scan also closes the gap in which an install finishing
        // mid-scan would be seen by neither. It is not first in the process — the window, and the
        // library page's own load with it, is already up — but that load runs on another thread and
        // cannot delay this one.
        StartWatching();

        await RunStepAsync("local manifest scan", _ => RunStartupScanAsync(), ct).ConfigureAwait(false);
        await RunStepAsync("metadata refresh", token => _library.RefreshMetadataAsync(false, token), ct).ConfigureAwait(false);
        await RunStepAsync("owned-games refresh", token => _library.RefreshRemoteAsync(false, token), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The first local scan of the session, which is always this one. Any
    /// <see cref="RequestLocalRescan"/> that arrived while the window was coming up is folded in
    /// here, so its request is honoured without a second pass over the manifests.
    /// </summary>
    private Task RunStartupScanAsync()
    {
        bool force;

        lock (_sync)
        {
            force = _startupForce;
            _startupForce = false;

            // From this point on, requests take the normal path and coalesce with the scan below.
            _startupScanReached = true;
        }

        return RunLocalRescanAsync(force);
    }

    private async Task RunStepAsync(string step, Func<CancellationToken, Task> body, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await body(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("The startup {Step} stopped because the application is shutting down.", step);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed: the next step still runs, and an empty or partial library is a
            // supported state. The alternative is a first run that dies before the window is usable.
            _logger.LogError(ex, "The startup {Step} failed; the rest of the sync continues.", step);
        }
    }

    /// <summary>
    /// Starts a rescan, or folds the request into the one already running. Returns the task doing the
    /// work so the initial sync can await it and shutdown can wait on it.
    /// </summary>
    private Task RunLocalRescanAsync(bool force)
    {
        lock (_sync)
        {
            if (_stopped || _disposed)
            {
                return Task.CompletedTask;
            }

            if (_rescanRunning)
            {
                // A forced request that arrives mid-scan must not be dropped: the scan in flight may
                // have read the manifests before Steam finished rewriting them.
                _pendingForce |= force;
                return _rescan;
            }

            _rescanRunning = true;

            var lifetime = _lifetime.Token;
            _rescan = Task.Run(() => RescanLoopAsync(force, lifetime), CancellationToken.None);
            return _rescan;
        }
    }

    private async Task RescanLoopAsync(bool force, CancellationToken ct)
    {
        var handedBack = false;

        try
        {
            while (true)
            {
                // Re-arming probes every library folder and takes the watcher's lock, so shutdown
                // must not be made to wait behind it.
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                RefreshWatchTargets();

                try
                {
                    await _library.RefreshLocalAsync(force, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Shutting down. The exit check below sees the cancellation and unwinds cleanly.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "A local manifest rescan failed.");
                }

                lock (_sync)
                {
                    // Deciding to stop and clearing the flags is one acquisition on purpose. Split
                    // across two, a RequestLocalRescan landing in the gap would set _pendingForce,
                    // be handed this already-finishing task, and then have its flag wiped — a forced
                    // watcher rescan silently dropped.
                    if (!_pendingForce || _stopped || _disposed || ct.IsCancellationRequested)
                    {
                        _rescanRunning = false;
                        _pendingForce = false;
                        handedBack = true;
                        return;
                    }

                    _pendingForce = false;
                    force = true;
                }
            }
        }
        finally
        {
            if (!handedBack)
            {
                // The loop left by a route it is not supposed to have — only the logging call above
                // can throw out here. Put the state machine back so later rescans still run, and
                // honour a request that was waiting on the flag being cleared.
                bool outstanding;

                lock (_sync)
                {
                    outstanding = _pendingForce && !_stopped && !_disposed;
                    _rescanRunning = false;
                    _pendingForce = false;
                }

                if (outstanding)
                {
                    _ = RunLocalRescanAsync(force: true);
                }
            }
        }
    }

    /// <summary>
    /// Waits for the background sync to actually finish, and says so if it takes longer than
    /// <see cref="SlowStopWarningThreshold"/>.
    /// </summary>
    private async Task AwaitUnwindAsync(Task pending)
    {
        var settled = await Task
            .WhenAny(pending, Task.Delay(SlowStopWarningThreshold, CancellationToken.None))
            .ConfigureAwait(false);

        if (!ReferenceEquals(settled, pending))
        {
            _logger.LogWarning(
                "A library sync was still unwinding {Seconds}s after shutdown began; waiting for it rather than letting the services be disposed underneath it.",
                SlowStopWarningThreshold.TotalSeconds);
        }

        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The expected end once the lifetime token is cancelled.
        }
        catch (Exception ex)
        {
            // Every step already catches its own failures, so this is the sync scaffolding itself.
            _logger.LogWarning(ex, "The background library sync ended with an unobserved failure.");
        }
    }

    /// <summary>
    /// Subscribes and arms the watcher. The lock covers the state transition only — starting the
    /// watcher probes every library folder, and on a spun-down or network drive that takes seconds,
    /// which is not something the dispatcher thread in <see cref="RequestLocalRescan"/> may ever wait
    /// behind.
    /// </summary>
    private void StartWatching()
    {
        lock (_sync)
        {
            if (_stopped || _disposed || _watching)
            {
                return;
            }

            // Claimed before the file system is touched, so a second caller cannot arm it twice.
            // Subscribing is an interlocked field swap, not a blocking call, and it happens before
            // Start() so a manifest written while the folders are being armed is reported rather
            // than missed.
            _watching = true;
            _watcher.Changed += OnSteamFilesChanged;
        }

        try
        {
            _watcher.Start();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown disposed the watcher while this call was arming it. Expected on an exit that
            // lands during startup, and not worth an error line.
            DetachWatcher();
            _logger.LogDebug("The Steam file watcher was disposed while it was being started; the application is shutting down.");
            return;
        }
        catch (Exception ex)
        {
            DetachWatcher();
            _logger.LogError(ex, "The Steam file watcher could not be started; only manual refresh will update the library.");
            return;
        }

        // Shutdown can land while Start() is still probing the disk: it has already detached this
        // handler and asked the watcher to stop, so whatever Start() armed in the meantime has to be
        // taken down again.
        lock (_sync)
        {
            if (!_stopped && !_disposed)
            {
                return;
            }
        }

        StopWatcher();
    }

    /// <summary>
    /// Gives the watcher a chance to pick up a library folder that appeared without Steam rewriting
    /// <c>libraryfolders.vdf</c> — a drive that was unplugged when we armed and has since been
    /// plugged back in, which is the one case no file change announces.
    /// </summary>
    /// <remarks>
    /// Re-arming is a diff, so a folder set that has not moved costs only the existence checks, and
    /// this runs on the rescan's thread-pool thread because the caller asking for a rescan is usually
    /// the dispatcher.
    /// </remarks>
    private void RefreshWatchTargets()
    {
        lock (_sync)
        {
            if (!_watching || _stopped || _disposed)
            {
                return;
            }
        }

        try
        {
            _watcher.Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Re-arming the Steam file watcher failed; the watchers already armed stay in place.");
        }
    }

    private void DetachWatcher()
    {
        lock (_sync)
        {
            if (!_watching)
            {
                return;
            }

            _watching = false;
            _watcher.Changed -= OnSteamFilesChanged;
        }
    }

    private void StopWatcher()
    {
        try
        {
            _watcher.Stop();
            _watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The Steam file watcher did not stop cleanly.");
        }
    }

    /// <summary>
    /// Turns "Steam rewrote cached art for these apps" into new art on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three caches sit between the file and the pixel, and every one of them has to be dropped or
    /// the chain breaks silently:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <see cref="ILibraryAssetResolver"/> caches the folder scan, so it would keep returning the
    /// path it found last time — or keep saying there is no art for an app that has just been given
    /// some.
    /// </description></item>
    /// <item><description>
    /// The library caches the <see cref="GameEntry"/> rows it built, and each row carries the
    /// <see cref="GameAssets"/> paths that were current when it was built. Invalidating the resolver
    /// alone changes nothing on screen, because nothing re-reads those rows.
    /// </description></item>
    /// <item><description>
    /// <see cref="IImageCacheService"/> caches decoded bitmaps by path, and Steam usually rewrites an
    /// app's art at the path it used before — so even a correct path resolves to the old picture.
    /// </description></item>
    /// </list>
    /// <para>
    /// The order is the reason this lives here rather than in three independent event handlers: the
    /// old paths are read <i>before</i> the resolver is invalidated and the new ones after, so both
    /// are dropped from the image cache whether the art moved or was rewritten where it was; and the
    /// library is told last, so by the time a page reloads a card, both caches beneath it are already
    /// clean. Handlers hanging off the same event give no such guarantee — their order is undefined.
    /// </para>
    /// <para>
    /// Runs on the watcher's debounce thread, which is already off the dispatcher; the folder rescan
    /// it does is the same one a grid reload would have done anyway.
    /// </para>
    /// </remarks>
    /// <param name="appIds">
    /// The apps whose art moved. Empty means the watcher lost the events to a buffer overflow and no
    /// entry can be trusted, so everything goes.
    /// </param>
    /// <summary>
    /// Queues an art refresh on a task <see cref="StopAsync"/> can wait for, and drops it entirely
    /// once shutdown has begun.
    /// </summary>
    /// <param name="appIds">The apps whose art moved, or empty when the watcher lost the events.</param>
    private void RequestArtRefresh(IReadOnlyList<int> appIds)
    {
        lock (_sync)
        {
            if (_stopped || _disposed)
            {
                return;
            }

            _artRefresh = Task.Run(() => RunArtRefreshAsync(appIds), CancellationToken.None);
        }
    }

    private async Task RunArtRefreshAsync(IReadOnlyList<int> appIds)
    {
        try
        {
            await _artGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            // Shutdown may have started while this waited its turn; the caches it is about to touch
            // are the ones the host is about to dispose.
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            RefreshArt(appIds);
        }
        finally
        {
            try
            {
                _ = _artGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Torn down underneath us.
            }
        }
    }

    private void RefreshArt(IReadOnlyList<int> appIds)
    {
        try
        {
            if (appIds.Count == 0)
            {
                _logger.LogDebug("Steam rewrote library art for apps we could not identify; dropping every cached path and bitmap.");
                _assets.InvalidateAll();
                _images.ClearMemory();
                _library.NotifyAssetsChanged([]);
                return;
            }

            var stale = new List<string?>(appIds.Count * 4);

            foreach (var appId in appIds)
            {
                // Whatever the card is pointing at right now, then whatever it will point at next.
                // The first covers art rewritten in place, the second art that moved into one of the
                // client's hashed subfolders.
                Collect(stale, _assets.Resolve(appId));
                _assets.Invalidate(appId);
                Collect(stale, _assets.Resolve(appId));
            }

            var dropped = _images.Invalidate(stale);
            _library.NotifyAssetsChanged(appIds);

            _logger.LogInformation(
                "Steam rewrote cached library art for {AppCount} app(s); dropped {PathCount} cached art path(s) and {BitmapCount} decoded bitmap(s), and the rows carrying those paths.",
                appIds.Count,
                appIds.Count,
                dropped);
        }
        catch (Exception ex)
        {
            // A stale cover is not worth taking the watcher thread down for.
            _logger.LogError(ex, "Could not refresh the cached library art the watcher reported.");
        }

        static void Collect(List<string?> paths, GameAssets assets)
        {
            paths.Add(assets.CapsulePath);
            paths.Add(assets.HeaderPath);
            paths.Add(assets.HeroPath);
            paths.Add(assets.LogoPath);
            paths.Add(assets.IconPath);
        }
    }

    /// <summary>
    /// Raised on the watcher's debounce thread. Forced, because a manifest that changed on disk is
    /// exactly the case the focus cooldown must not delay.
    /// </summary>
    private void OnSteamFilesChanged(object? sender, SteamFilesChangedEventArgs e)
    {
        if (e.Kind == SteamFileChangeKind.LibraryCache)
        {
            // Cached art has nothing to do with install state, so re-reading the manifests would
            // find nothing new — but three caches downstream of the change do have to be told, in
            // this order, or the new art never reaches the screen. Every other kind can move install
            // state — libraryfolders.vdf brings a whole library's manifests with it, and the client
            // rewrites appinfo.vdf and loginusers.vdf around installs too — so those still rescan.
            RequestArtRefresh(e.AppIds);
            return;
        }

        _logger.LogInformation(
            "Steam reported a {Kind} change ({AppCount} apps); rescanning the local manifests.",
            e.Kind,
            e.AppIds.Count);

        RequestLocalRescan(force: true);
    }
}
