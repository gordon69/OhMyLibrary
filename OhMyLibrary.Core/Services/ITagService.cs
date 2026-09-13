using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Services;

/// <summary>
/// Resolves store tag and genre ids to display names for the configured language.
/// </summary>
/// <remarks>
/// Tag names come from the public <c>populartags</c> endpoint; genre names come from a built-in
/// table because the client ships no id-to-name file. An id that resolves to nothing still renders,
/// as <c>#&lt;id&gt;</c>.
/// </remarks>
public interface ITagService
{
    /// <summary>
    /// Every tag name known for the configured language.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<TagRef>> GetAllTagsAsync(CancellationToken ct = default);

    /// <summary>
    /// Every genre name known for the configured language.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<GenreRef>> GetAllGenresAsync(CancellationToken ct = default);

    /// <summary>
    /// Re-downloads the tag name table. A failure leaves the previous names in place.
    /// </summary>
    /// <param name="force">Ignore the cache lifetime and fetch anyway.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RefreshNamesAsync(bool force, CancellationToken ct = default);
}
