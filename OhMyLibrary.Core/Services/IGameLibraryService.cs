using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Services;

/// <summary>What kind of refresh produced a <see cref="LibraryChangedEventArgs"/>.</summary>
public enum LibraryChangeKind
{
    /// <summary>Unclassified change; treat the whole library as stale.</summary>
    Unknown = 0,

    /// <summary>
    /// A local manifest rescan changed install state. <see cref="LibraryChangedEventArgs.AppIds"/>
    /// names the apps whose state <i>moved</i> — everything found installed by the scan plus
    /// everything the scan cleared, so an uninstall reaches a partial-update consumer instead of
    /// being silently dropped for want of a manifest.
    /// </summary>
    Local = 1,

    /// <summary>A Web API refresh changed the owned list or playtimes.</summary>
    Remote = 2,

    /// <summary>An <c>appinfo.vdf</c> or tag-name refresh changed metadata.</summary>
    Metadata = 3,

    /// <summary>Collection membership changed.</summary>
    Collections = 4,

    /// <summary>Friend ownership changed.</summary>
    Friends = 5,

    /// <summary>
    /// Steam rewrote cached library art, so the <see cref="Models.GameAssets"/> paths carried by the
    /// named rows are stale. <see cref="LibraryChangedEventArgs.AppIds"/> names the apps whose art
    /// moved, and is empty when the change could not be narrowed to app ids.
    /// </summary>
    Assets = 6,
}

/// <summary>
/// Describes a library change so the UI can refresh a few cards instead of the whole grid.
/// </summary>
public sealed class LibraryChangedEventArgs : EventArgs
{
    /// <summary>Creates the event arguments.</summary>
    /// <param name="kind">What kind of refresh produced the change.</param>
    /// <param name="appIds">The affected app ids, or empty when the change is library-wide.</param>
    public LibraryChangedEventArgs(LibraryChangeKind kind, IReadOnlyList<int>? appIds = null)
    {
        Kind = kind;
        AppIds = appIds ?? [];
    }

    /// <summary>What kind of refresh produced the change.</summary>
    public LibraryChangeKind Kind { get; }

    /// <summary>
    /// The affected app ids. Empty means "unknown or library-wide" — reload everything.
    /// </summary>
    public IReadOnlyList<int> AppIds { get; }

    /// <summary>True when the change could not be narrowed to specific apps.</summary>
    public bool IsWholeLibrary => AppIds.Count == 0;
}

/// <summary>
/// The merged view of the library: local manifests, the owned list, metadata, art, collections and
/// friend ownership, folded into <see cref="GameEntry"/> rows.
/// </summary>
/// <remarks>
/// No method throws for a degraded environment. Missing Steam, an unplugged library drive, a private
/// profile or an absent API key all reduce what <see cref="GetGamesAsync"/> returns and are explained
/// by <see cref="GetStatusAsync"/>.
/// </remarks>
public interface IGameLibraryService
{
    /// <summary>Raised after any refresh that changed the library.</summary>
    event EventHandler<LibraryChangedEventArgs>? LibraryChanged;

    /// <summary>
    /// Returns every row that belongs on the library grid, from the local database, without hitting
    /// Steam or the network.
    /// </summary>
    /// <remarks>
    /// "Belongs on the grid" is the rule described on <see cref="GetGridEntryAsync"/>, and it is the
    /// same predicate: redistributables are dropped, a non-game type survives only while installed,
    /// and membership turns on install state, ownership, and whether the owned list is available at
    /// all. Nothing downstream needs to filter what comes back, and nothing downstream should.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns one entry straight from the database, unfiltered, or <see langword="null"/> when the
    /// app id is unknown.
    /// </summary>
    /// <remarks>
    /// This is the detail-view read: a caller that names an app id already knows what it wants, even
    /// when that is a DLC, a tool or a redistributable. It is <b>not</b> the read that decides
    /// whether a card belongs on the grid — that is <see cref="GetGridEntryAsync"/>, and using this
    /// one for it is how redistributables end up on screen.
    /// </remarks>
    /// <param name="appId">Steam application id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GameEntry?> GetGameAsync(int appId, CancellationToken ct = default);

    /// <summary>
    /// Returns one entry when that app belongs on the library grid, and <see langword="null"/> when
    /// it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single-app counterpart of <see cref="GetGamesAsync"/>, applying exactly the same rules:
    /// the same type filter, the same redistributable exclusion and the same membership rule, from
    /// one predicate. That is the point of it — with the decision made in two places, a single-app
    /// refresh and a bulk read disagreed, and the app whose manifest had just been deleted lost its
    /// card while the next full reload put it straight back.
    /// </para>
    /// <para>
    /// Membership is: installed, <i>or</i> known to be owned, <i>or</i> the owned list is not
    /// available at all — no API key, or it has never synced — and the row came from a real manifest.
    /// A row only loses its card when the owned list <i>is</i> available and says the app is not
    /// owned. "Not owned" and "we do not know whether it is owned" are different answers, and on a
    /// default install with no API key configured every row is the second one.
    /// </para>
    /// </remarks>
    /// <param name="appId">Steam application id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GameEntry?> GetGridEntryAsync(int appId, CancellationToken ct = default);

    /// <summary>
    /// Describes the current degraded states so the UI can explain an empty or partial library.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<LibraryStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Rescans the <c>.acf</c> manifests. Cheap enough to run on window focus, and throttled by
    /// <c>Sync:LocalRescanCooldownSeconds</c>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task RefreshLocalAsync(CancellationToken ct = default);

    /// <summary>
    /// Rescans the <c>.acf</c> manifests, optionally ignoring the rescan cooldown.
    /// </summary>
    /// <param name="force">
    /// <see langword="true"/> to rescan even when the last scan is younger than
    /// <c>Sync:LocalRescanCooldownSeconds</c>. The cooldown exists to stop window-focus thrash from
    /// hammering the disk, so only a caller that knows the manifests just changed — the file watcher,
    /// or the user pressing Rescan — should pass <see langword="true"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task RefreshLocalAsync(bool force, CancellationToken ct = default);

    /// <summary>
    /// Refreshes the owned-games list from the Steam Web API. Expensive and rate-limited.
    /// </summary>
    /// <param name="force">Ignore the cache lifetime and fetch anyway.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RefreshRemoteAsync(bool force, CancellationToken ct = default);

    /// <summary>
    /// Reparses <c>appinfo.vdf</c> and refreshes tag names.
    /// </summary>
    /// <param name="force">Ignore the cache lifetime and reparse anyway.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RefreshMetadataAsync(bool force, CancellationToken ct = default);

    /// <summary>
    /// Tells the library that Steam rewrote cached art for these apps: drops the rows held in memory
    /// so the next read re-resolves their <see cref="Models.GameAssets"/> paths, and raises
    /// <see cref="LibraryChanged"/> with <see cref="LibraryChangeKind.Assets"/>.
    /// </summary>
    /// <remarks>
    /// The library caches the built <see cref="GameEntry"/> list, and those rows carry the art paths
    /// that were current when they were built. Invalidating the art resolver alone therefore changes
    /// nothing on screen: the cached rows keep handing out the old paths until something drops them,
    /// and that something is this call.
    /// </remarks>
    /// <param name="appIds">
    /// The apps whose art moved, or empty when the change could not be narrowed to app ids, which
    /// means "every app's art is suspect".
    /// </param>
    void NotifyAssetsChanged(IReadOnlyList<int> appIds);
}
