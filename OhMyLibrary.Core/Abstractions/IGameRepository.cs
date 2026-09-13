using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Persistence for the merged game rows and their genre and tag links.
/// </summary>
/// <remarks>
/// The upserts are additive by source: the installed upsert writes only install columns, the owned
/// upsert only ownership and playtime columns, and the metadata upsert only type, genres and tags.
/// None of them clears the columns another source owns, so a game that stops being installed is
/// cleared by <see cref="MarkNotInstalledExceptAsync"/>, not by omission.
/// </remarks>
public interface IGameRepository
{
    /// <summary>
    /// Returns every row as a fully hydrated entry, including genres, tags, friend owners and
    /// collection membership.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<GameEntry>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the app id of every row and nothing else.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="GetAllAsync"/> on purpose. Hydrating the library is six queries, two
    /// name tables and a <see cref="GameEntry"/> per row; a caller that only needs to know which
    /// apps exist — the <c>appinfo.vdf</c> refresh, which turns the answer straight into a filter —
    /// was paying all of that for a list of integers, and paying it again on every refresh of a
    /// library that can hold several thousand rows.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<int>> GetAllAppIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns one hydrated entry, or <see langword="null"/> when the app id is unknown.
    /// </summary>
    /// <param name="appId">Steam application id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GameEntry?> GetAsync(int appId, CancellationToken ct = default);

    /// <summary>
    /// Writes install state from a local manifest scan, inserting rows for apps that are installed
    /// but not owned.
    /// </summary>
    /// <param name="apps">The apps found on disk.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertInstalledAsync(IReadOnlyList<InstalledApp> apps, CancellationToken ct = default);

    /// <summary>
    /// Writes ownership and playtime from the Web API, inserting rows for apps that are owned but
    /// not installed.
    /// </summary>
    /// <param name="games">The owned games.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertOwnedAsync(IReadOnlyList<OwnedGame> games, CancellationToken ct = default);

    /// <summary>
    /// Writes metadata parsed from <c>appinfo.vdf</c> and replaces the genre and tag links of each
    /// app it covers. Only apps that already have a row are updated.
    /// </summary>
    /// <param name="entries">The parsed metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertMetadataAsync(IReadOnlyList<AppInfoEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// Clears the install columns of every row whose app id is not in the set, which is how an
    /// uninstall or an unplugged library drive is reflected. Owned rows survive as not-installed.
    /// </summary>
    /// <param name="installedAppIds">App ids found by the scan that is being committed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The app ids whose install state was actually cleared by this call — the rows that were
    /// installed a moment ago and are not any more. It is the only place that delta is knowable, and
    /// without it a caller raising a partial-update event can only name the apps that are still
    /// installed, leaving an uninstalled game's card showing "Play" until the next full reload.
    /// Empty when nothing changed.
    /// </returns>
    Task<IReadOnlyList<int>> MarkNotInstalledExceptAsync(IReadOnlySet<int> installedAppIds, CancellationToken ct = default);
}
