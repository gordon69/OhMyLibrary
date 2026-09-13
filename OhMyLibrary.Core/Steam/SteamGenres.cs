using System.Collections.Frozen;
using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Steam;

/// <summary>
/// Valve's built-in genre id to English name table, used to resolve the <c>common/genres</c> id list
/// found in <c>appinfo.vdf</c>.
/// </summary>
/// <remarks>
/// There is no <c>localization.vdf</c> in the modern client, and no endpoint that serves genre names
/// the way <c>tagdata/populartags</c> serves tag names, so this short and rarely-changing list ships
/// with the app. Localised names can later be lifted from
/// <c>steamui/localization/steamui_&lt;lang&gt;-json.js</c> keys <c>FilterElement_Genre&lt;Name&gt;</c>
/// and upserted over these. Ids that are absent here still render, as <c>#&lt;id&gt;</c>.
/// </remarks>
public static class SteamGenres
{
    /// <summary>Genre id to English display name.</summary>
    public static IReadOnlyDictionary<int, string> Names { get; } = new Dictionary<int, string>
    {
        [1] = "Action",
        [2] = "Strategy",
        [3] = "RPG",
        [4] = "Casual",
        [9] = "Racing",
        [18] = "Sports",
        [23] = "Indie",
        [25] = "Adventure",
        [28] = "Simulation",
        [29] = "Massively Multiplayer",
        [37] = "Free To Play",
        [50] = "Accounting",
        [51] = "Animation & Modeling",
        [52] = "Audio Production",
        [53] = "Design & Illustration",
        [54] = "Education",
        [55] = "Photo Editing",
        [56] = "Software Training",
        [57] = "Utilities",
        [58] = "Video Production",
        [59] = "Web Publishing",
        [60] = "Game Development",
        [70] = "Early Access",
        [71] = "Sexual Content",
        [72] = "Nudity",
        [73] = "Violent",
        [74] = "Gore",
        [81] = "Documentary",
        [84] = "Tutorial",
    }.ToFrozenDictionary();

    /// <summary>Every genre this build knows a name for, ordered by name for display.</summary>
    public static IReadOnlyList<GenreRef> All { get; } =
        Names
            .Select(static kvp => new GenreRef(kvp.Key, kvp.Value))
            .OrderBy(static genre => genre.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    /// <summary>
    /// Resolves a genre id to its name, falling back to <see cref="GenreRef.Unresolved(int)"/> so an
    /// unknown id is displayed as <c>#&lt;id&gt;</c> rather than dropped.
    /// </summary>
    /// <param name="id">Valve's genre id.</param>
    public static GenreRef Resolve(int id) =>
        Names.TryGetValue(id, out string? name) ? new GenreRef(id, name) : GenreRef.Unresolved(id);

    /// <summary>
    /// Resolves several genre ids in order, keeping duplicates out of the result.
    /// </summary>
    /// <param name="ids">Genre ids, in the order <c>appinfo.vdf</c> listed them.</param>
    public static IReadOnlyList<GenreRef> Resolve(IEnumerable<int> ids)
    {
        List<GenreRef> resolved = [];
        HashSet<int> seen = [];
        foreach (int id in ids)
        {
            if (seen.Add(id))
            {
                resolved.Add(Resolve(id));
            }
        }

        return resolved;
    }
}
