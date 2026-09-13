using Microsoft.Extensions.Logging;
using OhMyLibrary.Core.Abstractions;

namespace OhMyLibrary.Core.Services;

/// <summary>
/// Watches Steam's directories with one <see cref="FileSystemWatcher"/> per folder and raises a
/// debounced <see cref="Changed"/> event when install state, metadata or cached art may have moved.
/// </summary>
/// <remarks>
/// <para>
/// Steam rewrites <c>appmanifest_*.acf</c> in bursts during a download — dozens of writes a second —
/// so raw events are collected and flushed once after <see cref="DebounceInterval"/> of quiet,
/// coalesced into one event per <see cref="SteamFileChangeKind"/>. Watching is best-effort: an
/// unreachable folder is skipped rather than failing <see cref="Start"/>, and a watcher that reports
/// an error (its buffer overflows during large updates) is recreated in the background.
/// </para>
/// <para>
/// The watched set is not frozen at <see cref="Start"/>. A <c>libraryfolders.vdf</c> change re-runs
/// the evaluation before the event reaches subscribers, so a library added — or an unplugged drive
/// that Steam has re-registered — starts being watched without a restart; <see cref="Refresh"/> does
/// the same on demand for the one case no file change announces, a drive quietly plugged back in.
/// Re-arming is a diff: only the folders that actually appeared or vanished are touched, and a
/// folder set that could not be read at all retires nothing.
/// </para>
/// <para>
/// <b>Locking.</b> Two locks, always taken in the order <c>_armGate</c> then <c>_sync</c>, never the
/// other way round. <c>_sync</c> guards the mutable state and is held only for the handful of
/// instructions that read or swap it — never across the registry read, the <c>libraryfolders.vdf</c>
/// parse or the directory handle a watcher opens, any of which can block for seconds on a spun-down
/// drive. <c>_armGate</c> serialises the arming itself, which is what stops two concurrent arms from
/// each building a watcher for the same folder; a watcher that loses the swap anyway — the service
/// was stopped or restarted while it was being built — is disposed rather than dropped.
/// </para>
/// </remarks>
public sealed class SteamWatcherService : ISteamWatcherService
{
    /// <summary>Default quiet period, in milliseconds, before <see cref="Changed"/> is raised.</summary>
    public const int DebounceMilliseconds = 750;

    private const string ManifestPrefix = "appmanifest_";
    private const string ManifestFilter = ManifestPrefix + "*.acf";
    private const string AllFilesFilter = "*";

    private readonly ISteamPathResolver _paths;
    private readonly ILogger<SteamWatcherService> _logger;

    private readonly Lock _armGate = new();
    private readonly Lock _sync = new();
    private readonly Dictionary<WatchTarget, FileSystemWatcher> _watchers = [];
    private readonly Dictionary<SteamFileChangeKind, PendingChange> _pending = [];
    private readonly Timer _debounce;

    private TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(DebounceMilliseconds);
    private int _generation;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the service. No watcher is created until <see cref="Start"/> is called.</summary>
    /// <param name="paths">Locates Steam and its reachable library folders.</param>
    /// <param name="logger">Log sink; watcher failures are logged, never thrown.</param>
    public SteamWatcherService(ISteamPathResolver paths, ILogger<SteamWatcherService> logger)
    {
        _paths = paths;
        _logger = logger;
        _debounce = new Timer(Flush, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public event EventHandler<SteamFilesChangedEventArgs>? Changed;

    /// <summary>
    /// How long the directories must be quiet before <see cref="Changed"/> is raised. Defaults to
    /// <see cref="DebounceMilliseconds"/>; a shorter interval is mostly useful to tests, which cannot
    /// afford to wait out a real download burst.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or longer than a minute.</exception>
    public TimeSpan DebounceInterval
    {
        get
        {
            lock (_sync)
            {
                return _debounceInterval;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromMinutes(1));

            lock (_sync)
            {
                _debounceInterval = value;
            }
        }
    }

    /// <summary>
    /// The paths currently armed, as <c>&lt;directory&gt;\&lt;filter&gt;</c>, for diagnostics and for
    /// the tests that pin the re-arming behaviour. Empty while stopped.
    /// </summary>
    public IReadOnlyList<string> WatchedPaths
    {
        get
        {
            lock (_sync)
            {
                return
                [
                    .. _watchers.Keys
                        .Select(static target => Path.Combine(target.Directory, target.Filter))
                        .Order(StringComparer.OrdinalIgnoreCase),
                ];
            }
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_started)
            {
                return;
            }

            _started = true;
        }

        ArmTargets();

        int armed;
        lock (_sync)
        {
            armed = _watchers.Count;
        }

        _logger.LogInformation("Watching {WatcherCount} Steam folders for changes.", armed);
    }

    /// <inheritdoc />
    public void Refresh() => ArmTargets();

    /// <inheritdoc />
    public void Stop()
    {
        List<FileSystemWatcher> stale;

        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            _started = false;

            // Any arm still running belongs to the session that just ended; the generation bump is
            // what tells its swap to dispose what it built instead of installing it.
            _generation++;
            _pending.Clear();

            if (!_disposed)
            {
                _ = _debounce.Change(Timeout.Infinite, Timeout.Infinite);
            }

            stale = [.. _watchers.Values];
            _watchers.Clear();
        }

        foreach (var watcher in stale)
        {
            DisposeWatcher(watcher);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _debounce.Dispose();
        Changed = null;
    }

    /// <summary>
    /// Pulls the app id out of <c>appmanifest_&lt;appid&gt;.acf</c>.
    /// </summary>
    /// <param name="fileName">File name only, not a path.</param>
    /// <returns>The id, or <see langword="null"/> when the name is not a manifest.</returns>
    internal static int? TryParseAppId(ReadOnlySpan<char> fileName)
    {
        if (!fileName.StartsWith(ManifestPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = fileName[ManifestPrefix.Length..];
        var dot = rest.IndexOf('.');
        if (dot >= 0)
        {
            rest = rest[..dot];
        }

        return int.TryParse(rest, out var appId) && appId > 0 ? appId : null;
    }

    /// <summary>
    /// Pulls the app id out of a path under <c>appcache/librarycache</c>, whose first segment below
    /// the cache root is the app id: <c>librarycache/570/&lt;hash&gt;/library_capsule.jpg</c>.
    /// </summary>
    /// <param name="cacheRoot">Absolute path of the <c>librarycache</c> folder.</param>
    /// <param name="fullPath">Absolute path reported by the watcher.</param>
    /// <returns>The id, or <see langword="null"/> when the path names no app.</returns>
    internal static int? TryParseLibraryCacheAppId(string cacheRoot, string fullPath)
    {
        if (fullPath.Length <= cacheRoot.Length
            || !fullPath.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = fullPath.AsSpan(cacheRoot.Length);
        if (rest[0] is not ('\\' or '/'))
        {
            // A sibling folder whose name merely starts with the cache root's, not a path inside it.
            return null;
        }

        rest = rest[1..];
        var separator = rest.IndexOfAny('\\', '/');
        if (separator >= 0)
        {
            rest = rest[..separator];
        }

        return int.TryParse(rest, out var appId) && appId > 0 ? appId : null;
    }

    /// <summary>
    /// Whether a change of this kind can name the apps it concerns. For the other kinds the changed
    /// file names no app at all, so an empty id list is their normal answer rather than a lost one.
    /// </summary>
    private static bool CarriesAppIds(SteamFileChangeKind kind) =>
        kind is SteamFileChangeKind.AppManifest or SteamFileChangeKind.LibraryCache;

    /// <summary>
    /// Brings the live watcher set in line with <see cref="PlanTargets"/>: folders that are gone are
    /// disposed and dropped, new ones are armed, and a folder that is in both sets keeps the watcher
    /// it already has rather than being torn down and rebuilt. A target whose watcher could not be
    /// created is simply absent, so the next call retries it — which is how a replugged drive
    /// recovers.
    /// </summary>
    /// <remarks>
    /// Must <b>not</b> be called while holding <c>_sync</c>: everything expensive here — the registry
    /// read and <c>libraryfolders.vdf</c> parse in <see cref="PlanTargets"/>, the existence check and
    /// directory handle in <see cref="CreateWatcher"/>, and the handle each disposal closes — happens
    /// outside it, and only the swap itself takes it.
    /// </remarks>
    private void ArmTargets()
    {
        var discarded = new List<FileSystemWatcher>();
        var retired = new List<WatchTarget>();
        var added = 0;

        lock (_armGate)
        {
            int generation;
            HashSet<WatchTarget> armed;

            lock (_sync)
            {
                if (!_started || _disposed)
                {
                    return;
                }

                generation = _generation;
                armed = [.. _watchers.Keys];
            }

            var plan = PlanTargets();

            var created = new List<KeyValuePair<WatchTarget, FileSystemWatcher>>();
            foreach (var target in plan.Targets)
            {
                if (armed.Contains(target))
                {
                    continue;
                }

                if (CreateWatcher(target) is { } watcher)
                {
                    created.Add(new KeyValuePair<WatchTarget, FileSystemWatcher>(target, watcher));
                }
            }

            lock (_sync)
            {
                if (!_started || _disposed || _generation != generation)
                {
                    // Stopped, disposed or restarted while this arm was building watchers: they
                    // belong to a session that is gone, so they are disposed instead of installed.
                    discarded.AddRange(created.Select(static entry => entry.Value));
                }
                else
                {
                    foreach (var target in _watchers.Keys.Where(plan.Retires).ToList())
                    {
                        if (_watchers.Remove(target, out var stale))
                        {
                            discarded.Add(stale);
                            retired.Add(target);
                        }
                    }

                    foreach (var (target, watcher) in created)
                    {
                        if (_watchers.TryAdd(target, watcher))
                        {
                            added++;
                        }
                        else
                        {
                            // Somebody armed this folder while ours was being built. Theirs is live,
                            // so ours is disposed rather than left running unreferenced.
                            discarded.Add(watcher);
                        }
                    }
                }
            }
        }

        foreach (var watcher in discarded)
        {
            DisposeWatcher(watcher);
        }

        foreach (var target in retired)
        {
            _logger.LogInformation("Stopped watching {Directory}: it is no longer a Steam folder.", target.Directory);
        }

        if (added > 0 || retired.Count > 0)
        {
            int total;
            lock (_sync)
            {
                total = _watchers.Count;
            }

            _logger.LogDebug(
                "Re-armed the Steam watchers: {Added} added, {Removed} dropped, {Total} active.",
                added,
                retired.Count,
                total);
        }
    }

    /// <summary>
    /// Every folder worth watching: the <c>steamapps</c> folder of each reachable library for the
    /// manifests, the three Steam-root files whose changes invalidate our own caches, and the library
    /// art cache — plus, per source, whether that source could be read at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-source flag is the difference between "the folder set no longer contains X" and "the
    /// folder set could not be determined". Only the first may retire a watcher: a registry read or a
    /// <c>libraryfolders.vdf</c> parse that fails is transient — a locked file, a drive still spinning
    /// up — and reading that as "every library vanished" would disarm manifest watching for the rest
    /// of the session, because nothing would ever announce their return.
    /// </para>
    /// <para>
    /// <c>appcache/librarycache</c> is watched recursively because the art of one app lives in an
    /// opaque hash subfolder below <c>librarycache/&lt;appid&gt;/</c>. Recursion costs one kernel
    /// subtree registration regardless of how big the tree is — measured at 1946 app folders, 3027
    /// directories and 652 MB on the reference install — and Steam only writes into it when it
    /// actually downloads new art, so the event volume is nothing like the manifest bursts.
    /// </para>
    /// </remarks>
    private TargetPlan PlanTargets()
    {
        var targets = new HashSet<WatchTarget>();

        var steamRootKnown = true;
        string? steamPath = null;
        try
        {
            steamPath = _paths.FindSteamPath();
        }
        catch (Exception ex)
        {
            // Unknown, not absent: whatever is already armed under the Steam root stays armed.
            steamRootKnown = false;
            _logger.LogWarning(ex, "Could not locate the Steam installation; the folders already watched there are kept.");
        }

        if (steamPath is not null)
        {
            var config = Path.Combine(steamPath, "config");
            _ = targets.Add(new WatchTarget(config, "loginusers.vdf", SteamFileChangeKind.LoginUsers, TargetSource.SteamRoot));
            _ = targets.Add(new WatchTarget(config, "libraryfolders.vdf", SteamFileChangeKind.LibraryFolders, TargetSource.SteamRoot));
            _ = targets.Add(new WatchTarget(
                Path.Combine(steamPath, "steamapps"),
                "libraryfolders.vdf",
                SteamFileChangeKind.LibraryFolders,
                TargetSource.SteamRoot));
            _ = targets.Add(new WatchTarget(
                Path.Combine(steamPath, "appcache"),
                "appinfo.vdf",
                SteamFileChangeKind.AppInfo,
                TargetSource.SteamRoot));
            _ = targets.Add(new WatchTarget(
                Path.Combine(steamPath, "appcache", "librarycache"),
                AllFilesFilter,
                SteamFileChangeKind.LibraryCache,
                TargetSource.SteamRoot,
                Recursive: true));
        }

        var librariesKnown = true;
        try
        {
            // Materialised first and merged only on success: half an enumeration is not a folder set,
            // and it must not be allowed to look like the whole of one.
            var libraries = _paths
                .GetLibraryFolders()
                .Select(folder => new WatchTarget(
                    folder.SteamAppsPath,
                    ManifestFilter,
                    SteamFileChangeKind.AppManifest,
                    TargetSource.Library))
                .ToList();

            targets.UnionWith(libraries);
        }
        catch (Exception ex)
        {
            librariesKnown = false;
            _logger.LogWarning(ex, "Could not enumerate the Steam library folders; the manifest watchers already armed are kept.");
        }

        return new TargetPlan(targets, steamRootKnown, librariesKnown);
    }

    private FileSystemWatcher? CreateWatcher(WatchTarget target)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            if (!Directory.Exists(target.Directory))
            {
                _logger.LogDebug("Not watching {Directory}: the folder is unreachable.", target.Directory);
                return null;
            }

            watcher = new FileSystemWatcher(target.Directory, target.Filter)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = target.Recursive,

                // Manifest bursts during a large update overflow the default 8 KB buffer, which costs
                // us the events entirely; 64 KB is the practical ceiling worth paying for.
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += (_, e) => OnFileEvent(target, e.FullPath);
            watcher.Changed += (_, e) => OnFileEvent(target, e.FullPath);
            watcher.Deleted += (_, e) => OnFileEvent(target, e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                OnFileEvent(target, e.OldFullPath);
                OnFileEvent(target, e.FullPath);
            };
            watcher.Error += (_, e) => OnWatcherError(target, e.GetException());

            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex)
        {
            // EnableRaisingEvents is the line that opens the directory handle, and the folder can go
            // away between the existence check above and it, so the half-built watcher is disposed
            // here instead of being left for a finaliser.
            if (watcher is not null)
            {
                DisposeWatcher(watcher);
            }

            _logger.LogWarning(ex, "Could not watch {Directory}; changes there will need a manual refresh.", target.Directory);
            return null;
        }
    }

    /// <summary>
    /// Rebuilds one watcher after its buffer overflowed or its drive went away. Runs off the
    /// watcher's own callback thread, because disposing a watcher from inside its error handler is
    /// not something the runtime promises to enjoy.
    /// </summary>
    private void OnWatcherError(WatchTarget target, Exception error)
    {
        _logger.LogWarning(error, "The watcher for {Directory} failed; recreating it.", target.Directory);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

                RearmOne(target);

                // The events lost while the buffer was full are unknowable, so treat the whole
                // category as dirty rather than pretending nothing happened. The null path is what
                // clears the collected ids, which is the documented "ids unknown" signal.
                OnFileEvent(target, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not recreate the watcher for {Directory}.", target.Directory);
            }
        });
    }

    /// <summary>
    /// Replaces the watcher of one target, by the same route as a full arm: the old handle is closed
    /// and the new one opened outside <c>_sync</c>, which is taken only to swap the entry.
    /// </summary>
    private void RearmOne(WatchTarget target)
    {
        FileSystemWatcher? orphan = null;

        lock (_armGate)
        {
            int generation;

            lock (_sync)
            {
                if (!_started || _disposed)
                {
                    return;
                }

                generation = _generation;
                _ = _watchers.Remove(target, out orphan);
            }

            if (orphan is not null)
            {
                DisposeWatcher(orphan);
                orphan = null;
            }

            var replacement = CreateWatcher(target);
            if (replacement is null)
            {
                return;
            }

            lock (_sync)
            {
                if (!_started || _disposed || _generation != generation || !_watchers.TryAdd(target, replacement))
                {
                    orphan = replacement;
                }
            }
        }

        if (orphan is not null)
        {
            DisposeWatcher(orphan);
        }
    }

    private void OnFileEvent(WatchTarget target, string? fullPath)
    {
        var appId = fullPath is null
            ? null
            : target.Kind switch
            {
                SteamFileChangeKind.AppManifest => TryParseAppId(Path.GetFileName(fullPath.AsSpan())),
                SteamFileChangeKind.LibraryCache => TryParseLibraryCacheAppId(target.Directory, fullPath),
                _ => null,
            };

        lock (_sync)
        {
            if (!_started || _disposed)
            {
                return;
            }

            if (!_pending.TryGetValue(target.Kind, out var pending))
            {
                pending = new PendingChange();
                _pending[target.Kind] = pending;
            }

            if (appId is { } id)
            {
                pending.AppIds.Add(id);
            }
            else if (CarriesAppIds(target.Kind))
            {
                // Either the events were lost to an overflow (no path at all), or a path arrived that
                // names no app — a rename to something unparseable, or librarycache/assetcache.vdf,
                // which sits beside the app folders rather than inside one. Both mean the same thing
                // to a subscriber: the ids collected so far are not the whole answer, which is
                // exactly what an empty AppIds list is documented to say.
                pending.IdsUnknown = true;
            }

            pending.Path = fullPath ?? pending.Path;

            _ = _debounce.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush(object? state)
    {
        List<SteamFilesChangedEventArgs> events;

        lock (_sync)
        {
            if (_disposed || !_started || _pending.Count == 0)
            {
                return;
            }

            events = new List<SteamFilesChangedEventArgs>(_pending.Count);
            foreach (var (kind, pending) in _pending)
            {
                events.Add(new SteamFilesChangedEventArgs(
                    kind,
                    pending.IdsUnknown ? [] : [.. pending.AppIds],
                    pending.Path));
            }

            _pending.Clear();
        }

        // The folder set is derived from libraryfolders.vdf, so a change to it means our own watcher
        // set is stale. Re-arm before subscribers see the event, so that by the time one reacts, a
        // library added a moment ago is already being watched — but outside the lock, because
        // re-arming reads the registry and opens directory handles.
        if (events.Exists(static e => e.Kind == SteamFileChangeKind.LibraryFolders))
        {
            ArmTargets();
        }

        var handler = Changed;
        if (handler is null)
        {
            return;
        }

        foreach (var args in events)
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A SteamWatcher subscriber threw while handling a {Kind} change.", args.Kind);
            }
        }
    }

    private void DisposeWatcher(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing a Steam folder watcher failed.");
        }
    }

    /// <summary>Which enumeration produced a target, and therefore whose failure may retire it.</summary>
    private enum TargetSource
    {
        /// <summary>Derived from the Steam install root, so it lives or dies with the registry read.</summary>
        SteamRoot,

        /// <summary>Derived from the library folder list, so it lives or dies with that enumeration.</summary>
        Library,
    }

    /// <summary>One folder plus file pattern to watch, and what a change there means.</summary>
    /// <param name="Directory">Absolute path of the folder to watch.</param>
    /// <param name="Filter">File name pattern within it.</param>
    /// <param name="Kind">What a change there means to a subscriber.</param>
    /// <param name="Source">Which enumeration produced it.</param>
    /// <param name="Recursive">Whether subfolders are watched as well.</param>
    private sealed record WatchTarget(
        string Directory,
        string Filter,
        SteamFileChangeKind Kind,
        TargetSource Source,
        bool Recursive = false);

    /// <summary>The folders that should be watched, plus whether each source actually answered.</summary>
    /// <param name="Targets">Every folder the sources that answered say should be watched.</param>
    /// <param name="SteamRootKnown">Whether the Steam install root could be resolved at all.</param>
    /// <param name="LibrariesKnown">Whether the library folder list could be enumerated at all.</param>
    private sealed record TargetPlan(HashSet<WatchTarget> Targets, bool SteamRootKnown, bool LibrariesKnown)
    {
        /// <summary>
        /// Whether an armed target should be dropped: only when the source that produces it answered
        /// and no longer lists it. A source that could not be read leaves its watchers alone.
        /// </summary>
        /// <param name="armed">A target that currently has a live watcher.</param>
        /// <returns><see langword="true"/> when the watcher should be disposed and forgotten.</returns>
        public bool Retires(WatchTarget armed) =>
            !Targets.Contains(armed)
            && (armed.Source == TargetSource.SteamRoot ? SteamRootKnown : LibrariesKnown);
    }

    /// <summary>Events collected for one change kind since the last flush.</summary>
    private sealed class PendingChange
    {
        public HashSet<int> AppIds { get; } = [];

        /// <summary>
        /// True once at least one event in the burst could not be attributed to an app, which makes
        /// the ids collected an incomplete list rather than the answer.
        /// </summary>
        public bool IdsUnknown { get; set; }

        public string? Path { get; set; }
    }
}
