using System.Data.Common;
using Dapper;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Data.Repositories;

/// <summary>
/// Dapper implementation of <see cref="ICollectionRepository"/>.
/// </summary>
/// <remarks>
/// Collections are ours alone and never written back to Steam. Membership carries its own
/// <c>sort_order</c> so a collection can be arranged by hand rather than only alphabetically.
/// </remarks>
/// <param name="connectionFactory">Source of database connections.</param>
public sealed class CollectionRepository(IDbConnectionFactory connectionFactory) : ICollectionRepository
{
    private const string CollectionColumns = """
        collection_id AS CollectionId,
        name          AS Name,
        sort_order    AS SortOrder,
        created_utc   AS CreatedUtc
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameCollection>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        return await HydrateAsync(connection, null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<GameCollection?> GetAsync(long collectionId, CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        var collections = await HydrateAsync(connection, collectionId, ct).ConfigureAwait(false);
        return collections.Count > 0 ? collections[0] : null;
    }

    /// <inheritdoc />
    public async Task<GameCollection> CreateAsync(
        string name,
        DateTimeOffset createdUtc,
        CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var sortOrder = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM Collections",
            transaction: transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        var collectionId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO Collections (name, sort_order, created_utc)
            VALUES (@Name, @SortOrder, @CreatedUtc)
            RETURNING collection_id
            """,
            new
            {
                Name = name,
                SortOrder = sortOrder,
                CreatedUtc = SqliteTypeHandlers.ToStorage(createdUtc),
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new GameCollection(collectionId, name, (int)sortOrder, createdUtc.ToUniversalTime(), []);
    }

    /// <inheritdoc />
    public async Task RenameAsync(long collectionId, string name, CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Collections SET name = @Name WHERE collection_id = @CollectionId",
            new { Name = name, CollectionId = collectionId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long collectionId, CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM GameCollections WHERE collection_id = @CollectionId;
            DELETE FROM Collections     WHERE collection_id = @CollectionId;
            """,
            new { CollectionId = collectionId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default)
    {
        if (orderedIds.Count == 0)
        {
            return;
        }

        var rows = orderedIds
            .Select((collectionId, index) => new { CollectionId = collectionId, SortOrder = index })
            .ToList();

        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Collections SET sort_order = @SortOrder WHERE collection_id = @CollectionId",
            rows,
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddGameAsync(long collectionId, int appId, CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);

        // Driving the insert off Collections means an unknown id inserts nothing instead of leaving
        // an orphaned membership row behind; the scalar subquery appends to the collection's order.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT OR IGNORE INTO GameCollections (app_id, collection_id, sort_order)
            SELECT @AppId,
                   c.collection_id,
                   (SELECT COALESCE(MAX(gc.sort_order), -1) + 1
                    FROM GameCollections gc
                    WHERE gc.collection_id = c.collection_id)
            FROM Collections c
            WHERE c.collection_id = @CollectionId
            """,
            new { AppId = appId, CollectionId = collectionId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveGameAsync(long collectionId, int appId, CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM GameCollections WHERE collection_id = @CollectionId AND app_id = @AppId",
            new { CollectionId = collectionId, AppId = appId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task<List<GameCollection>> HydrateAsync(
        DbConnection connection,
        long? collectionId,
        CancellationToken ct)
    {
        var filter = collectionId is null ? string.Empty : " WHERE collection_id = @collectionId";
        var args = new { collectionId };

        var rows = (await connection.QueryAsync<CollectionRow>(new CommandDefinition(
                $"""
                 SELECT {CollectionColumns}
                 FROM Collections{filter}
                 ORDER BY sort_order, name COLLATE NOCASE
                 """,
                args,
                cancellationToken: ct)).ConfigureAwait(false))
            .ToList();

        if (rows.Count == 0)
        {
            return [];
        }

        var members = await connection.QueryAsync<MemberRow>(new CommandDefinition(
            $"""
             SELECT collection_id AS CollectionId, app_id AS AppId
             FROM GameCollections{filter}
             ORDER BY collection_id, sort_order, app_id
             """,
            args,
            cancellationToken: ct)).ConfigureAwait(false);

        var byCollection = new Dictionary<long, List<int>>();
        foreach (var member in members)
        {
            if (!byCollection.TryGetValue(member.CollectionId, out var appIds))
            {
                appIds = [];
                byCollection[member.CollectionId] = appIds;
            }

            appIds.Add(member.AppId);
        }

        return rows
            .Select(row => new GameCollection(
                CollectionId: row.CollectionId,
                Name: row.Name,
                SortOrder: row.SortOrder,
                CreatedUtc: row.CreatedUtc,
                AppIds: byCollection.TryGetValue(row.CollectionId, out var appIds) ? appIds : []))
            .ToList();
    }

    private sealed class CollectionRow
    {
        public long CollectionId { get; set; }

        public string Name { get; set; } = string.Empty;

        public int SortOrder { get; set; }

        public DateTimeOffset CreatedUtc { get; set; }
    }

    private sealed class MemberRow
    {
        public long CollectionId { get; set; }

        public int AppId { get; set; }
    }
}
