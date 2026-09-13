using System.Data.Common;
using Dapper;
using OhMyLibrary.Data;
using OhMyLibrary.Data.Repositories;

namespace OhMyLibrary.Tests.Data;

/// <summary>
/// Base class for the repository tests: one isolated in-memory database per test class, with every
/// repository already built over it.
/// </summary>
/// <remarks>
/// Each <see cref="SqliteConnectionFactory"/> created with <see cref="SqliteConnectionFactory.InMemoryPath"/>
/// gets its own uniquely named shared-cache database, kept alive by a connection the factory holds,
/// so the tests are isolated from each other and disposing the factory drops the database.
/// </remarks>
public abstract class SqliteFixtureBase : IDisposable
{
    /// <summary>Creates the database and the repositories over it.</summary>
    protected SqliteFixtureBase()
    {
        Factory = new SqliteConnectionFactory(SqliteConnectionFactory.InMemoryPath);
        Games = new GameRepository(Factory);
        Tags = new TagRepository(Factory);
        Friends = new FriendRepository(Factory);
        Collections = new CollectionRepository(Factory);
        SyncMeta = new SyncMetaRepository(Factory);
    }

    /// <summary>The connection factory under test.</summary>
    protected SqliteConnectionFactory Factory { get; }

    /// <summary>Game rows.</summary>
    protected GameRepository Games { get; }

    /// <summary>Tag and genre names.</summary>
    protected TagRepository Tags { get; }

    /// <summary>Friends and friend ownership.</summary>
    protected FriendRepository Friends { get; }

    /// <summary>User collections.</summary>
    protected CollectionRepository Collections { get; }

    /// <summary>Sync timestamps.</summary>
    protected SyncMetaRepository SyncMeta { get; }

    /// <summary>Drops the database.</summary>
    public void Dispose()
    {
        Factory.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Runs a scalar query against the database, for assertions the repository interfaces do not
    /// expose — the columns an upsert must <i>not</i> have touched, mostly.
    /// </summary>
    /// <typeparam name="T">Scalar type.</typeparam>
    /// <param name="sql">Query text.</param>
    /// <param name="args">Optional parameters.</param>
    protected async Task<T?> ScalarAsync<T>(string sql, object? args = null)
    {
        await using DbConnection connection = await Factory.CreateOpenAsync(CancellationToken.None)
            .ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<T>(sql, args).ConfigureAwait(false);
    }
}
