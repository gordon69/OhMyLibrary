using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Tests.Infrastructure;

namespace OhMyLibrary.Tests.Fakes;

/// <summary>
/// An <see cref="ISteamPathResolver"/> whose answers are set by the test.
/// </summary>
/// <remarks>
/// Set <see cref="SteamPath"/> to <see langword="null"/> to reproduce "Steam is not installed",
/// which every service is required to survive.
/// </remarks>
public sealed class FakeSteamPathResolver : ISteamPathResolver
{
    /// <summary>The install root to report; <see langword="null"/> means Steam is not installed.</summary>
    public string? SteamPath { get; set; }

    /// <summary>The executable to report.</summary>
    public string? SteamExecutable { get; set; }

    /// <summary>The <i>reachable</i> library folders, as the real resolver would report them.</summary>
    public List<SteamLibraryFolder> LibraryFolders { get; } = [];

    /// <summary>The accounts that have signed in on this machine.</summary>
    public List<SteamUser> LocalUsers { get; } = [];

    /// <summary>
    /// Runs before <see cref="FindSteamPath"/> answers. Throw from it to reproduce a registry read
    /// that fails transiently; block in it to reproduce one that takes its time.
    /// </summary>
    public Action? BeforeFindSteamPath { get; set; }

    /// <summary>
    /// Runs before <see cref="GetLibraryFolders"/> answers. Throw from it to reproduce a
    /// <c>libraryfolders.vdf</c> read that fails transiently — which is not the same thing as a
    /// library that went away; block in it to reproduce a drive that is still spinning up.
    /// </summary>
    public Action? BeforeGetLibraryFolders { get; set; }

    /// <inheritdoc />
    public string? FindSteamPath()
    {
        BeforeFindSteamPath?.Invoke();
        return SteamPath;
    }

    /// <inheritdoc />
    public string? FindSteamExecutable() => SteamExecutable;

    /// <inheritdoc />
    public IReadOnlyList<SteamLibraryFolder> GetLibraryFolders()
    {
        BeforeGetLibraryFolders?.Invoke();
        return LibraryFolders;
    }

    /// <inheritdoc />
    public IReadOnlyList<SteamUser> GetLocalUsers() => LocalUsers;
}

/// <summary>An <see cref="ILibraryFoldersReader"/> that returns the <i>declared</i> folders it was given.</summary>
public sealed class FakeLibraryFoldersReader : ILibraryFoldersReader
{
    /// <summary>Folders to report, unreachable ones included.</summary>
    public List<SteamLibraryFolder> Declared { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<SteamLibraryFolder> Read(string steamPath) => Declared;
}

/// <summary>An <see cref="IAcfReader"/> that replays a canned manifest scan.</summary>
public sealed class FakeAcfReader : IAcfReader
{
    /// <summary>Apps every scan reports.</summary>
    public List<InstalledApp> Apps { get; } = [];

    /// <summary>How many times <see cref="ReadAll"/> has run.</summary>
    public int ReadAllCalls { get; private set; }

    /// <summary>
    /// Raised as each scan starts, before it observes the token, so a test can cancel a scan that is
    /// genuinely in flight.
    /// </summary>
    public CountingSignal ReadAllStarted { get; } = new("AcfReader.ReadAll started");

    /// <summary>When set, every scan awaits it before it observes the token.</summary>
    public TaskCompletionSource? ReadAllGate { get; set; }

    /// <inheritdoc />
    public InstalledApp? Read(string manifestPath, string libraryPath) =>
        Apps.Find(app => string.Equals(app.ManifestPath, manifestPath, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IReadOnlyList<InstalledApp> ReadLibrary(SteamLibraryFolder folder, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Apps.Where(app => string.Equals(app.LibraryPath, folder.Path, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<InstalledApp> ReadAll(IEnumerable<SteamLibraryFolder> folders, CancellationToken ct = default)
    {
        ReadAllCalls++;
        ReadAllStarted.Raise();

        // The real reader checks the token once per manifest; this stands for the moment it does so
        // with the scan already under way, which is the only interesting case.
        ReadAllGate?.Task.GetAwaiter().GetResult();
        ct.ThrowIfCancellationRequested();

        return Apps;
    }
}

/// <summary>An <see cref="IAppInfoReader"/> backed by a dictionary the test fills in.</summary>
public sealed class FakeAppInfoReader : IAppInfoReader
{
    /// <summary>Entries to serve, keyed by app id.</summary>
    public Dictionary<int, AppInfoEntry> Entries { get; } = [];

    /// <summary>The id set of the most recent <see cref="ReadApps"/> call.</summary>
    public IReadOnlySet<int>? LastRequestedIds { get; private set; }

    /// <summary>
    /// The token of the most recent call. The real reader checks it once per record, so a caller
    /// that does not hand it over has left a multi-second parse uncancellable.
    /// </summary>
    public CancellationToken LastToken { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<AppInfoEntry> ReadAll(string appInfoVdfPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return [.. Entries.Values];
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<int, AppInfoEntry> ReadApps(
        string appInfoVdfPath,
        IReadOnlySet<int> appIds,
        CancellationToken ct = default)
    {
        LastRequestedIds = appIds;
        LastToken = ct;
        ct.ThrowIfCancellationRequested();
        return Entries.Where(entry => appIds.Contains(entry.Key)).ToDictionary(e => e.Key, e => e.Value);
    }
}

/// <summary>An <see cref="ILibraryAssetResolver"/> that reports art for the ids it was given.</summary>
/// <remarks>
/// <see cref="Invalidate"/> swaps in <see cref="AssetsAfterInvalidation"/> where the test supplied
/// one, which is how a rescan that finds the art somewhere else is reproduced without a disk.
/// </remarks>
public sealed class FakeLibraryAssetResolver : ILibraryAssetResolver
{
    /// <summary>Art to report, keyed by app id; ids that are absent resolve to nothing.</summary>
    public Dictionary<int, GameAssets> Assets { get; } = [];

    /// <summary>Art the next scan finds after an app has been invalidated, keyed by app id.</summary>
    public Dictionary<int, GameAssets> AssetsAfterInvalidation { get; } = [];

    /// <summary>Every app id passed to <see cref="Invalidate"/>, in order.</summary>
    public List<int> Invalidated { get; } = [];

    /// <summary>How many times <see cref="InvalidateAll"/> was called.</summary>
    public int InvalidateAllCalls { get; private set; }

    /// <inheritdoc />
    public GameAssets Resolve(int appId) =>
        Assets.TryGetValue(appId, out var assets) ? assets : GameAssets.None(appId);

    /// <inheritdoc />
    public string? GetCoverUrlFallback(int appId) =>
        appId <= 0 ? null : $"https://cdn.example.invalid/{appId}/library_600x900.jpg";

    /// <inheritdoc />
    public void Invalidate(int appId)
    {
        Invalidated.Add(appId);

        if (AssetsAfterInvalidation.TryGetValue(appId, out var rescanned))
        {
            Assets[appId] = rescanned;
        }
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        InvalidateAllCalls++;
        Assets.Clear();
    }
}

/// <summary>An <see cref="ISteamUriLauncher"/> that records the calls instead of starting a process.</summary>
public sealed class FakeSteamUriLauncher : ISteamUriLauncher
{
    /// <summary>Every call, as <c>&lt;action&gt;:&lt;id&gt;</c>, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>What every member returns.</summary>
    public bool Result { get; set; } = true;

    /// <inheritdoc />
    public bool LaunchGame(int appId) => Record("launch", appId);

    /// <inheritdoc />
    public bool Install(int appId) => Record("install", appId);

    /// <inheritdoc />
    public bool Uninstall(int appId) => Record("uninstall", appId);

    /// <inheritdoc />
    public bool Validate(int appId) => Record("validate", appId);

    /// <inheritdoc />
    public bool OpenStorePage(int appId) => Record("store", appId);

    /// <inheritdoc />
    public bool OpenLibraryPage(int appId) => Record("library", appId);

    /// <inheritdoc />
    public bool OpenFriendProfile(ulong steamId64) => Record("friend", (long)steamId64);

    /// <inheritdoc />
    public bool OpenUri(string steamUri)
    {
        Calls.Add($"uri:{steamUri}");
        return Result;
    }

    private bool Record(string action, long id)
    {
        Calls.Add($"{action}:{id}");
        return Result;
    }
}

/// <summary>
/// An <see cref="ISteamWatcherService"/> that raises whatever the test tells it to, without going
/// anywhere near a real <see cref="FileSystemWatcher"/>.
/// </summary>
/// <remarks>
/// <see cref="Start"/> can be made to block on a <see cref="TaskCompletionSource"/> the test owns,
/// which is how the coordinator tests reproduce the real thing's expensive step — arming a watcher
/// probes every library folder, and a spun-down drive makes that take seconds — deterministically
/// and without a timer.
/// </remarks>
public sealed class FakeSteamWatcherService : ISteamWatcherService
{
    /// <summary>Journal label recorded when <see cref="Start"/> is entered.</summary>
    public const string StartCall = "watcher.start";

    /// <summary>Journal label recorded when <see cref="Start"/> has finished arming.</summary>
    public const string StartDoneCall = "watcher.start:done";

    /// <summary>Journal label recorded when <see cref="Refresh"/> is called.</summary>
    public const string RefreshCall = "watcher.refresh";

    /// <summary>Journal label recorded when <see cref="Stop"/> is called.</summary>
    public const string StopCall = "watcher.stop";

    /// <summary>Journal label recorded when <see cref="Dispose"/> is called.</summary>
    public const string DisposeCall = "watcher.dispose";

    /// <summary>Journal label recorded when a handler subscribes to <see cref="Changed"/>.</summary>
    public const string SubscribeCall = "watcher.subscribe";

    /// <summary>Journal label recorded when a handler unsubscribes from <see cref="Changed"/>.</summary>
    public const string UnsubscribeCall = "watcher.unsubscribe";

    private readonly Lock _sync = new();

    private EventHandler<SteamFilesChangedEventArgs>? _changed;
    private int _startCalls;
    private int _refreshCalls;
    private int _stopCalls;
    private int _subscriberCount;
    private bool _watching;
    private bool _disposed;

    /// <summary>Shared ordering journal, when the test is asserting on call order.</summary>
    public CallLog? Journal { get; init; }

    /// <summary>Raised as <see cref="Start"/> is entered, before it blocks on <see cref="StartGate"/>.</summary>
    public CountingSignal StartEntered { get; } = new("SteamWatcher.Start entered");

    /// <summary>Raised as <see cref="Stop"/> is entered, before it blocks on <see cref="StopGate"/>.</summary>
    public CountingSignal StopEntered { get; } = new("SteamWatcher.Stop entered");

    /// <summary>
    /// When set, <see cref="Stop"/> blocks on it. The real watcher's teardown takes the lock its
    /// own callbacks hold, so a shutdown that tears down before it cancels can park there — this is
    /// how that ordering is made observable.
    /// </summary>
    public TaskCompletionSource? StopGate { get; set; }

    /// <summary>
    /// When set, <see cref="Start"/> blocks on it, exactly as the real one blocks on a slow drive.
    /// </summary>
    public TaskCompletionSource? StartGate { get; set; }

    /// <summary>When set, <see cref="Start"/> throws it instead of arming.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>How many times <see cref="Start"/> actually armed the watcher.</summary>
    public int StartCalls
    {
        get
        {
            lock (_sync)
            {
                return _startCalls;
            }
        }
    }

    /// <summary>How many times <see cref="Refresh"/> was called.</summary>
    public int RefreshCalls
    {
        get
        {
            lock (_sync)
            {
                return _refreshCalls;
            }
        }
    }

    /// <summary>How many times <see cref="Stop"/> was called.</summary>
    public int StopCalls
    {
        get
        {
            lock (_sync)
            {
                return _stopCalls;
            }
        }
    }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsWatching
    {
        get
        {
            lock (_sync)
            {
                return _watching;
            }
        }
    }

    /// <summary>True once <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _disposed;
            }
        }
    }

    /// <summary>How many handlers are subscribed to <see cref="Changed"/> right now.</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_sync)
            {
                return _subscriberCount;
            }
        }
    }

    /// <summary>True while at least one handler is subscribed to <see cref="Changed"/>.</summary>
    public bool HasSubscribers => SubscriberCount > 0;

    /// <inheritdoc />
    public event EventHandler<SteamFilesChangedEventArgs>? Changed
    {
        add
        {
            lock (_sync)
            {
                _changed += value;
                _subscriberCount++;
            }

            Journal?.Record(SubscribeCall);
        }

        remove
        {
            lock (_sync)
            {
                _changed -= value;
                _subscriberCount--;
            }

            Journal?.Record(UnsubscribeCall);
        }
    }

    /// <summary>Raises <see cref="Changed"/> as the real watcher would after its debounce.</summary>
    /// <param name="kind">Which file changed.</param>
    /// <param name="appIds">The app ids the change concerns, if any.</param>
    public void Raise(SteamFileChangeKind kind, params int[] appIds)
    {
        EventHandler<SteamFilesChangedEventArgs>? handlers;

        lock (_sync)
        {
            handlers = _changed;
        }

        handlers?.Invoke(this, new SteamFilesChangedEventArgs(kind, appIds));
    }

    /// <inheritdoc />
    public void Start()
    {
        Journal?.Record(StartCall);
        StartEntered.Raise();

        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        // Deliberately blocking, on the caller's thread: that is what makes "no lock is held across
        // Start()" an observable property rather than a claim.
        StartGate?.Task.GetAwaiter().GetResult();

        lock (_sync)
        {
            if (!_watching)
            {
                _watching = true;
                _startCalls++;
            }
        }

        Journal?.Record(StartDoneCall);
    }

    /// <inheritdoc />
    public void Refresh()
    {
        lock (_sync)
        {
            _refreshCalls++;
        }

        Journal?.Record(RefreshCall);
    }

    /// <inheritdoc />
    public void Stop()
    {
        StopEntered.Raise();

        // Blocking on the caller's thread, like Start(), so "cancelled before the teardown" is an
        // observable property rather than a claim.
        StopGate?.Task.GetAwaiter().GetResult();

        lock (_sync)
        {
            _watching = false;
            _stopCalls++;
        }

        Journal?.Record(StopCall);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();

        lock (_sync)
        {
            _disposed = true;
        }

        Journal?.Record(DisposeCall);
    }
}
