namespace OhMyLibrary.Core.Services;

/// <summary>Which Steam file triggered a <see cref="SteamFilesChangedEventArgs"/>.</summary>
/// <remarks>
/// The kind is the discriminator subscribers branch on, so each value states exactly what is watched
/// and whether <see cref="SteamFilesChangedEventArgs.AppIds"/> can be populated for it. Every value
/// except <see cref="Unknown"/> is raised by <see cref="SteamWatcherService"/>; the pairing is pinned
/// by <c>SteamWatcherServiceTests</c> against a real directory tree.
/// </remarks>
public enum SteamFileChangeKind
{
    /// <summary>
    /// A change was detected but could not be attributed to a known file. Never raised by
    /// <see cref="SteamWatcherService"/>; it exists so a subscriber's <c>switch</c> has a safe
    /// default meaning "reload everything".
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// An <c>appmanifest_&lt;appid&gt;.acf</c> under some library's <c>steamapps</c> was created,
    /// changed, renamed or deleted — install state moved. <see cref="SteamFilesChangedEventArgs.AppIds"/>
    /// carries every app id parsed out of the file names, and is empty when the ids could not all be
    /// determined — the events were lost to a buffer overflow, or one of them named a file whose name
    /// carries no app id — which means "rescan everything".
    /// </summary>
    AppManifest = 1,

    /// <summary>
    /// <c>libraryfolders.vdf</c> changed — a library was added, moved or removed. Watched at both
    /// locations Steam uses (<c>steamapps/</c> and <c>config/</c>).
    /// <see cref="SteamFilesChangedEventArgs.AppIds"/> is always empty: the file names no app.
    /// The watcher has already re-armed its own folder set before this is raised.
    /// </summary>
    LibraryFolders = 2,

    /// <summary>
    /// <c>config/loginusers.vdf</c> changed — someone signed in or out, so the detected SteamID64 may
    /// have moved. <see cref="SteamFilesChangedEventArgs.AppIds"/> is always empty.
    /// </summary>
    LoginUsers = 3,

    /// <summary>
    /// <c>appcache/appinfo.vdf</c> changed — the client refreshed its metadata cache, so types,
    /// genres and store tags may have moved. <see cref="SteamFilesChangedEventArgs.AppIds"/> is
    /// always empty: the file is a single blob covering every app the client knows.
    /// </summary>
    AppInfo = 4,

    /// <summary>
    /// Something under <c>appcache/librarycache/&lt;appid&gt;/</c> changed — Steam rewrote cached
    /// library art. <see cref="SteamFilesChangedEventArgs.AppIds"/> carries the app ids taken from
    /// the folder names, and is empty when the ids could not all be determined — the events were lost
    /// to a buffer overflow, or one of them named a path that sits beside the app folders rather than
    /// inside one, such as <c>librarycache/assetcache.vdf</c> — which means "every app's art is
    /// suspect".
    /// </summary>
    LibraryCache = 5,
}

/// <summary>
/// Describes a change seen under Steam's directories.
/// </summary>
public sealed class SteamFilesChangedEventArgs : EventArgs
{
    /// <summary>Creates the event arguments.</summary>
    /// <param name="kind">Which file changed.</param>
    /// <param name="appIds">The app ids the change concerns, when they could be derived from the path.</param>
    /// <param name="path">Absolute path that changed, when known.</param>
    public SteamFilesChangedEventArgs(
        SteamFileChangeKind kind,
        IReadOnlyList<int>? appIds = null,
        string? path = null)
    {
        Kind = kind;
        AppIds = appIds ?? [];
        Path = path;
    }

    /// <summary>Which file changed.</summary>
    public SteamFileChangeKind Kind { get; }

    /// <summary>
    /// The affected app ids. Only <see cref="SteamFileChangeKind.AppManifest"/> and
    /// <see cref="SteamFileChangeKind.LibraryCache"/> can ever fill this in; for the other kinds the
    /// changed file names no app and the list is empty by definition. An <b>empty</b> list on one of
    /// those two kinds means the ids are unknown and the whole category has to be treated as dirty —
    /// either the watcher's buffer overflowed, or a path in the burst named no app at all.
    /// </summary>
    /// <remarks>
    /// The ids are all or nothing per event: one unattributable path in a coalesced burst empties the
    /// list rather than leaving behind the ids that did parse, because a partial list is
    /// indistinguishable from a complete one and would be read as "nothing else moved".
    /// </remarks>
    public IReadOnlyList<int> AppIds { get; }

    /// <summary>
    /// One absolute path from the coalesced burst, for logging. Never use it to decide what changed —
    /// a debounced event usually stands for many paths. <see langword="null"/> when the burst carried
    /// no path at all, which is what a buffer overflow looks like.
    /// </summary>
    public string? Path { get; }
}

/// <summary>
/// Watches Steam's directories and raises <see cref="Changed"/> when install state may have moved.
/// </summary>
/// <remarks>
/// Steam rewrites manifests repeatedly during a download, so implementations debounce and coalesce
/// before raising the event, one event per <see cref="SteamFileChangeKind"/>. Watching is
/// best-effort: an unwatchable or unplugged folder is skipped rather than failing
/// <see cref="Start"/>, and the app still works with manual refresh only.
/// </remarks>
public interface ISteamWatcherService : IDisposable
{
    /// <summary>
    /// Raised, already debounced, when a watched Steam file changed. Handlers run on a thread-pool
    /// thread, not on the UI thread, and one that throws is logged and does not stop the others.
    /// </summary>
    event EventHandler<SteamFilesChangedEventArgs>? Changed;

    /// <summary>Starts watching. Calling it while already started is a no-op.</summary>
    void Start();

    /// <summary>
    /// Re-evaluates which folders should be watched and re-arms the watchers accordingly: folders
    /// that are new since the last evaluation gain a watcher, folders that have gone away lose
    /// theirs, and the ones that did not move keep the watcher they already had.
    /// </summary>
    /// <remarks>
    /// A <c>libraryfolders.vdf</c> change triggers this on its own, so the common case needs no
    /// caller. It is exposed for the case a file change cannot announce: a library drive that was
    /// unplugged at <see cref="Start"/> and has since been plugged back in. Calling it while stopped
    /// is a no-op.
    /// </remarks>
    void Refresh();

    /// <summary>Stops watching. Calling it while already stopped is a no-op.</summary>
    void Stop();
}
