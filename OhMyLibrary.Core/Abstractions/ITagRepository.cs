using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Persistence for the tag and genre name tables, which are keyed by id <i>and</i> language.
/// </summary>
public interface ITagRepository
{
    /// <summary>
    /// Returns every tag name stored for a language, ordered by name.
    /// </summary>
    /// <param name="lang">Steam language name, for example <c>english</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<TagRef>> GetTagsAsync(string lang, CancellationToken ct = default);

    /// <summary>
    /// Returns every genre name stored for a language, ordered by name.
    /// </summary>
    /// <param name="lang">Steam language name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<GenreRef>> GetGenresAsync(string lang, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates tag names for a language. Names absent from the list are left alone, so a
    /// short or failed download cannot wipe the table.
    /// </summary>
    /// <param name="tags">The tags to write.</param>
    /// <param name="lang">Steam language name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertTagNamesAsync(IReadOnlyList<TagRef> tags, string lang, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates genre names for a language, with the same additive semantics as
    /// <see cref="UpsertTagNamesAsync"/>.
    /// </summary>
    /// <param name="genres">The genres to write.</param>
    /// <param name="lang">Steam language name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertGenreNamesAsync(IReadOnlyList<GenreRef> genres, string lang, CancellationToken ct = default);
}
