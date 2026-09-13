using Microsoft.Extensions.Logging;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Services;

/// <summary>
/// User-created collections: validation on top of <see cref="ICollectionRepository"/>.
/// </summary>
/// <remarks>
/// Collections are ours alone and are never written back to Steam. Names are trimmed and must not be
/// blank; duplicates are deliberately allowed, because two collections called "Backlog" are the
/// user's business. Reordering always produces a dense <c>0..n-1</c> sort order.
/// </remarks>
public sealed class CollectionService : ICollectionService
{
    private readonly ICollectionRepository _collections;
    private readonly ILogger<CollectionService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="collections">Collection persistence.</param>
    /// <param name="logger">Log sink.</param>
    public CollectionService(ICollectionRepository collections, ILogger<CollectionService> logger)
    {
        _collections = collections;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameCollection>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            return await _collections.GetAllAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the collections.");
            return [];
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or whitespace. This is a caller mistake rather than a
    /// degraded environment, so it is reported rather than swallowed; the UI validates first.
    /// </exception>
    public Task<GameCollection> CreateAsync(string name, CancellationToken ct = default)
    {
        var trimmed = Validate(name);
        return _collections.CreateAsync(trimmed, DateTimeOffset.UtcNow, ct);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    public Task RenameAsync(long collectionId, string name, CancellationToken ct = default)
    {
        var trimmed = Validate(name);
        return _collections.RenameAsync(collectionId, trimmed, ct);
    }

    /// <inheritdoc />
    public Task DeleteAsync(long collectionId, CancellationToken ct = default) =>
        _collections.DeleteAsync(collectionId, ct);

    /// <inheritdoc />
    /// <remarks>
    /// The list handed to the repository is always the complete set: duplicates are dropped, ids that
    /// no longer exist are dropped, and collections the caller did not mention are appended in their
    /// current order. The result is a dense <c>0..n-1</c> sort order with no gaps.
    /// </remarks>
    public async Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        try
        {
            var existing = await _collections.GetAllAsync(ct).ConfigureAwait(false);
            var known = existing.Select(static c => c.CollectionId).ToHashSet();

            var dense = new List<long>(existing.Count);
            var placed = new HashSet<long>();

            foreach (var id in orderedIds)
            {
                if (known.Contains(id) && placed.Add(id))
                {
                    dense.Add(id);
                }
            }

            foreach (var collection in existing)
            {
                if (placed.Add(collection.CollectionId))
                {
                    dense.Add(collection.CollectionId);
                }
            }

            if (dense.Count > 0)
            {
                await _collections.ReorderAsync(dense, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not reorder the collections.");
        }
    }

    /// <inheritdoc />
    public async Task AddGameAsync(long collectionId, int appId, CancellationToken ct = default)
    {
        if (appId <= 0)
        {
            return;
        }

        try
        {
            await _collections.AddGameAsync(collectionId, appId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not add {AppId} to collection {CollectionId}.", appId, collectionId);
        }
    }

    /// <inheritdoc />
    public async Task RemoveGameAsync(long collectionId, int appId, CancellationToken ct = default)
    {
        if (appId <= 0)
        {
            return;
        }

        try
        {
            await _collections.RemoveGameAsync(collectionId, appId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not remove {AppId} from collection {CollectionId}.", appId, collectionId);
        }
    }

    private static string Validate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var trimmed = name.Trim();
        return trimmed.Length == 0
            ? throw new ArgumentException("A collection name cannot be empty or whitespace.", nameof(name))
            : trimmed;
    }
}
