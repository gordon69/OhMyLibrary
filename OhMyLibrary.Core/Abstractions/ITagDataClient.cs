using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Downloads store tag names from <c>store.steampowered.com/tagdata/populartags/&lt;language&gt;</c>.
/// </summary>
/// <remarks>
/// No API key is required. The list changes rarely, so it is cached for days; on failure the caller
/// keeps the previous names and unresolved ids render as <c>#&lt;id&gt;</c>.
/// </remarks>
public interface ITagDataClient
{
    /// <summary>
    /// Fetches the popular tag list for a language.
    /// </summary>
    /// <param name="language">Steam language name, for example <c>english</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tags, or an empty list when the request fails.</returns>
    Task<IReadOnlyList<TagRef>> GetPopularTagsAsync(string language, CancellationToken ct = default);
}
