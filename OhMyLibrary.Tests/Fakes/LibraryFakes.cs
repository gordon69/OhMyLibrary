using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Tests.Infrastructure;

namespace OhMyLibrary.Tests.Fakes;

/// <summary>
/// An <see cref="IGameLibraryService"/> that records every refresh it was asked for and can be made
/// to stall or fail on demand.
/// </summary>
/// <remarks>
/// <para>
/// Written for the coordinator tests, which are about <i>when</i> and <i>with what arguments</i> the
/// library is refreshed, never about what the refresh produces. The read members answer out of plain
/// lists so a caller that reads is not forced to set anything up.
/// </para>
/// <para>
/// <see cref="LocalScanGate"/> is the whole reason the concurrency tests are deterministic: a scan
/// that is "in flight" is a scan awaiting a <see cref="TaskCompletionSource"/> the test completes,
/// not a scan racing a timer.
/// </para>
/// </remarks>
public sealed class FakeGameLibraryService : IGameLibraryService
{
    /// <summary>Journal label recorded when a local scan finishes.</summary>
    public const string LocalScanDoneCall = "library.local:done";

    /// <summary>Journal label recorded when a metadata refresh starts.</summary>
    public const string MetadataCall = "library.metadata";

    /// <summary>Journal label recorded when an owned-games refresh starts.</summary>
    public const string RemoteCall = "library.remote";

    /// <summary>Journal label recorded when the library is told its art paths are stale.</summary>
    public const string AssetsChangedCall = "library.assets";

    private readonly Lock _sync = new();
    private readonly List<bool> _localScanForceFlags = [];
    private readonly CallLog _calls;

    private int _inFlightCalls;
    private CancellationToken _lastLocalScanToken;

    /// <summary>Creates the fake.</summary>
    /// <param name="calls">Shared ordering journal; a private one is used when none is given.</param>
    public FakeGameLibraryService(CallLog? calls = null) => _calls = calls ?? new CallLog();

    /// <inheritdoc />
    public event EventHandler<LibraryChangedEventArgs>? LibraryChanged;

    /// <summary>Rows returned by <see cref="GetGamesAsync"/> and <see cref="GetGameAsync"/>.</summary>
    public List<GameEntry> Games { get; } = [];

    /// <summary>The app id lists passed to <see cref="NotifyAssetsChanged"/>, in order.</summary>
    public List<int[]> AssetChangeNotifications { get; } = [];

    /// <summary>What <see cref="GetStatusAsync"/> answers.</summary>
    public LibraryStatus Status { get; set; } =
        new(SteamFound: true, SteamPath: @"C:\Steam", LibraryFolderCount: 1, MissingLibraryFolderCount: 0,
            ApiKeyConfigured: false, OwnedListAvailable: false, SteamId64: null, LastError: null);

    /// <summary>Raised as each local scan starts, before it waits on <see cref="LocalScanGate"/>.</summary>
    public CountingSignal LocalScanStarted { get; } = new("RefreshLocalAsync started");

    /// <summary>Raised as each local scan returns normally.</summary>
    public CountingSignal LocalScanCompleted { get; } = new("RefreshLocalAsync completed");

    /// <summary>Raised as each metadata refresh starts.</summary>
    public CountingSignal MetadataStarted { get; } = new("RefreshMetadataAsync started");

    /// <summary>Raised as each owned-games refresh starts.</summary>
    public CountingSignal RemoteStarted { get; } = new("RefreshRemoteAsync started");

    /// <summary>
    /// Raised by each <see cref="NotifyAssetsChanged"/>, which is the last link of the art chain.
    /// Art refreshes run on a task the coordinator tracks rather than inline on the watcher thread,
    /// so a test that raises a LibraryCache event has to wait for this before asserting.
    /// </summary>
    public CountingSignal AssetsChanged { get; } = new("NotifyAssetsChanged");

    /// <summary>
    /// When set, every local scan awaits it before returning.
    /// </summary>
    /// <remarks>
    /// It deliberately ignores cancellation. It stands for work that is already past the last point
    /// where the token is checked — a Dapper write in flight, say — which is precisely the work
    /// shutdown must wait for rather than abandon.
    /// </remarks>
    public TaskCompletionSource? LocalScanGate { get; set; }

    /// <summary>When set, every local scan throws it.</summary>
    public Exception? LocalScanFailure { get; set; }

    /// <summary>When set, every metadata refresh throws it.</summary>
    public Exception? MetadataFailure { get; set; }

    /// <summary>When set, every owned-games refresh throws it.</summary>
    public Exception? RemoteFailure { get; set; }

    /// <summary>
    /// The token the most recent local scan was given, so a test can assert that shutdown cancelled
    /// it before doing something else.
    /// </summary>
    public CancellationToken LastLocalScanToken
    {
        get
        {
            lock (_sync)
            {
                return _lastLocalScanToken;
            }
        }
    }

    /// <summary>The <c>force</c> flag of every local scan, in order.</summary>
    public IReadOnlyList<bool> LocalScanForceFlags
    {
        get
        {
            lock (_sync)
            {
                return [.. _localScanForceFlags];
            }
        }
    }

    /// <summary>How many refreshes are running right now.</summary>
    public int InFlightCalls
    {
        get
        {
            lock (_sync)
            {
                return _inFlightCalls;
            }
        }
    }

    /// <summary>The journal label a local scan records, including its <c>force</c> flag.</summary>
    /// <param name="force">The flag the scan was asked for.</param>
    public static string LocalScanCall(bool force) => $"library.local(force={(force ? "true" : "false")})";

    /// <summary>Raises <see cref="LibraryChanged"/>, as a real refresh would.</summary>
    /// <param name="kind">What kind of refresh the change stands for.</param>
    /// <param name="appIds">The apps whose state moved.</param>
    public void RaiseChanged(LibraryChangeKind kind, params int[] appIds) =>
        LibraryChanged?.Invoke(this, new LibraryChangedEventArgs(kind, appIds));

    /// <inheritdoc />
    public Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<GameEntry>>([.. Games]);
    }

    /// <inheritdoc />
    public Task<GameEntry?> GetGameAsync(int appId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Games.Find(game => game.AppId == appId));
    }

    /// <inheritdoc />
    public Task<GameEntry?> GetGridEntryAsync(int appId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Games.Find(game => game.AppId == appId));
    }

    /// <inheritdoc />
    public void NotifyAssetsChanged(IReadOnlyList<int> appIds)
    {
        _calls.Record(AssetsChangedCall);
        AssetChangeNotifications.Add([.. appIds]);
        RaiseChanged(LibraryChangeKind.Assets, [.. appIds]);
        AssetsChanged.Raise();
    }

    /// <inheritdoc />
    public Task<LibraryStatus> GetStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Status);
    }

    /// <inheritdoc />
    public Task RefreshLocalAsync(CancellationToken ct = default) => RefreshLocalAsync(false, ct);

    /// <inheritdoc />
    public async Task RefreshLocalAsync(bool force, CancellationToken ct = default)
    {
        _calls.Record(LocalScanCall(force));

        lock (_sync)
        {
            _localScanForceFlags.Add(force);
            _lastLocalScanToken = ct;
            _inFlightCalls++;
        }

        LocalScanStarted.Raise();

        try
        {
            var gate = LocalScanGate;
            if (gate is not null)
            {
                await gate.Task.ConfigureAwait(false);
            }

            if (LocalScanFailure is not null)
            {
                throw LocalScanFailure;
            }
        }
        finally
        {
            lock (_sync)
            {
                _inFlightCalls--;
            }
        }

        _calls.Record(LocalScanDoneCall);
        LocalScanCompleted.Raise();
    }

    /// <inheritdoc />
    public Task RefreshRemoteAsync(bool force, CancellationToken ct = default)
    {
        _calls.Record(RemoteCall);
        RemoteStarted.Raise();

        return RemoteFailure is null ? Task.CompletedTask : Task.FromException(RemoteFailure);
    }

    /// <inheritdoc />
    public Task RefreshMetadataAsync(bool force, CancellationToken ct = default)
    {
        _calls.Record(MetadataCall);
        MetadataStarted.Raise();

        return MetadataFailure is null ? Task.CompletedTask : Task.FromException(MetadataFailure);
    }
}
