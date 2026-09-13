namespace OhMyLibrary.Core.Models;

/// <summary>
/// A store tag id paired with its display name.
/// </summary>
/// <param name="TagId">Valve's store tag id.</param>
/// <param name="Name">Localised display name, or <c>#&lt;id&gt;</c> when unresolved.</param>
public sealed record TagRef(int TagId, string Name)
{
    /// <summary>
    /// A placeholder for a tag id that no name table could resolve. Unresolved ids must still
    /// render rather than being dropped.
    /// </summary>
    /// <param name="tagId">The tag id to wrap.</param>
    public static TagRef Unresolved(int tagId) => new(tagId, $"#{tagId}");

    /// <summary>True when this reference carries a real name rather than the <c>#id</c> placeholder.</summary>
    public bool IsResolved => Name.Length > 0 && Name[0] != '#';
}

/// <summary>
/// A genre id paired with its display name.
/// </summary>
/// <param name="GenreId">Valve's genre id.</param>
/// <param name="Name">Localised display name, or <c>#&lt;id&gt;</c> when unresolved.</param>
public sealed record GenreRef(int GenreId, string Name)
{
    /// <summary>A placeholder for a genre id that no name table could resolve.</summary>
    /// <param name="genreId">The genre id to wrap.</param>
    public static GenreRef Unresolved(int genreId) => new(genreId, $"#{genreId}");

    /// <summary>True when this reference carries a real name rather than the <c>#id</c> placeholder.</summary>
    public bool IsResolved => Name.Length > 0 && Name[0] != '#';
}
