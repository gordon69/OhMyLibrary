using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Services;

/// <summary>
/// User-created collections. These are ours alone and are never written back to Steam.
/// </summary>
public interface ICollectionService
{
    /// <summary>
    /// Returns every collection with its members, ordered by sort order then name.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<GameCollection>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates an empty collection, appended after the existing ones.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created collection, with its assigned id.</returns>
    Task<GameCollection> CreateAsync(string name, CancellationToken ct = default);

    /// <summary>Renames a collection; a no-op when the id does not exist.</summary>
    /// <param name="collectionId">Collection id.</param>
    /// <param name="name">New display name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RenameAsync(long collectionId, string name, CancellationToken ct = default);

    /// <summary>Deletes a collection and its membership rows; a no-op when the id does not exist.</summary>
    /// <param name="collectionId">Collection id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(long collectionId, CancellationToken ct = default);

    /// <summary>
    /// Rewrites the sidebar order. Ids not in the list keep their relative order after the ones that are.
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
