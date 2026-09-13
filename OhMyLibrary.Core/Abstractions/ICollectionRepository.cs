using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Persistence for user-created collections, mirroring
/// <c>OhMyLibrary.Core.Services.ICollectionService</c> one-to-one.
/// </summary>
public interface ICollectionRepository
{
    /// <summary>
    /// Returns every collection with its members, ordered by sort order then name.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<GameCollection>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns one collection with its members, or <see langword="null"/> when the id is unknown.
    /// </summary>
    /// <param name="collectionId">Collection id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GameCollection?> GetAsync(long collectionId, CancellationToken ct = default);

    /// <summary>
    /// Inserts an empty collection after the existing ones.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="createdUtc">Creation timestamp, in UTC.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created collection, with the id SQLite assigned.</returns>
    Task<GameCollection> CreateAsync(string name, DateTimeOffset createdUtc, CancellationToken ct = default);

    /// <summary>Renames a collection; a no-op when the id does not exist.</summary>
    /// <param name="collectionId">Collection id.</param>
    /// <param name="name">New display name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RenameAsync(long collectionId, string name, CancellationToken ct = default);

    /// <summary>Deletes a collection together with its membership rows.</summary>
    /// <param name="collectionId">Collection id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(long collectionId, CancellationToken ct = default);

    /// <summary>
    /// Rewrites the sort order of the listed collections, in one transaction.
    /// </summary>
    /// <param name="orderedIds">Collection ids in their new order.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default);

    /// <summary>Adds a game to a collection; adding it twice is a no-op.</summary>
    /// <param name="collectionId">Collection id.</param>
    /// <param name="appId">Steam application id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddGameAsync(long collectionId, int appId, CancellationToken ct = default);

    /// <summary>Removes a game from a collection; removing an absent game is a no-op.</summary>
    /// <param name="collectionId">Collection id.</param>
    /// <param name="appId">Steam application id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveGameAsync(long collectionId, int appId, CancellationToken ct = default);
}
