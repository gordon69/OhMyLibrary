namespace OhMyLibrary.Core.Models;

/// <summary>
/// A user-created collection of games. Ours, not Steam's — it lives only in our own database.
/// </summary>
/// <param name="CollectionId">Surrogate key from the local database.</param>
/// <param name="Name">Display name.</param>
/// <param name="SortOrder">Position in the sidebar; lower sorts first.</param>
/// <param name="CreatedUtc">Creation timestamp, in UTC.</param>
/// <param name="AppIds">Member app ids, in the collection's own order.</param>
public sealed record GameCollection(
    long CollectionId,
    string Name,
    int SortOrder,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<int> AppIds);
