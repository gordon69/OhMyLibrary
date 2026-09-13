using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;

namespace OhMyLibrary.Data;

/// <summary>
/// The default <see cref="IDbConnectionFactory"/>: a SQLite file under
/// <c>%LOCALAPPDATA%/OhMyLibrary/</c>, created on first use.
/// </summary>
/// <remarks>
/// <para>
/// The folder is created, <see cref="DatabaseSchema.CreateScript"/> is applied once per factory
/// instance, and every connection handed out gets the connection-scoped pragmas applied the moment
/// it opens — including the ones returned from the connection pool, whose pragma state does not
/// survive being recycled.
/// </para>
/// <para>
/// Passing <see cref="InMemoryPath"/> gives an isolated in-memory database for tests. Each factory
/// gets its own uniquely named shared-cache database and keeps one connection open for as long as
/// the factory lives, because SQLite discards an in-memory database the moment its last connection
/// closes.
/// </para>
/// </remarks>
public sealed class SqliteConnectionFactory : IDbConnectionFactory, IDisposable
{
    /// <summary>Pass this as the database path to get a private in-memory database.</summary>
    public const string InMemoryPath = ":memory:";

    /// <summary>Folder created under <c>%LOCALAPPDATA%</c> for our own state.</summary>
    private const string AppFolderName = "OhMyLibrary";

    /// <summary>File name of the database inside that folder.</summary>
    private const string DatabaseFileName = "library.db";

    /// <summary>
    /// Pragmas that are per-connection (or cheap to re-assert) and therefore run on every open.
    /// <c>journal_mode</c> is persisted in the file itself and re-stating it is a no-op.
    /// </summary>
    private const string PragmaScript = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=NORMAL;
        PRAGMA foreign_keys=ON;
        PRAGMA busy_timeout=5000;
        """;

    private readonly string _connectionString;
    private readonly bool _inMemory;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    private volatile bool _initialized;
    private volatile bool _disposed;
    private SqliteConnection? _keepAlive;

    /// <summary>Creates a factory over the default database under <c>%LOCALAPPDATA%</c>.</summary>
    public SqliteConnectionFactory()
        : this(null)
    {
    }

    /// <summary>Creates a factory over an explicit database location.</summary>
    /// <param name="databasePath">
    /// Absolute or relative path of the database file, <see cref="InMemoryPath"/> for an isolated
    /// in-memory database, or <see langword="null"/>/empty for the default location.
    /// </param>
    public SqliteConnectionFactory(string? databasePath)
    {
        SqliteTypeHandlers.Register();

        var requested = string.IsNullOrWhiteSpace(databasePath) ? DefaultDatabasePath : databasePath.Trim();
        _inMemory = string.Equals(requested, InMemoryPath, StringComparison.OrdinalIgnoreCase);
        DatabasePath = _inMemory ? InMemoryPath : Path.GetFullPath(requested);

        _connectionString = new SqliteConnectionStringBuilder
        {
            // A unique name keeps parallel in-memory factories (i.e. parallel tests) isolated.
            DataSource = _inMemory ? $"ohmylibrary-{Guid.NewGuid():N}" : DatabasePath,
            Mode = _inMemory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    /// <summary>The database location this process uses when nothing else is configured.</summary>
    public static string DefaultDatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName,
        DatabaseFileName);

    /// <inheritdoc />
    public string DatabasePath { get; }

    /// <inheritdoc />
    public DbConnection Create()
    {
        // The DDL is local file work measured in milliseconds; blocking here keeps the sync half of
        // the interface honest instead of handing back a connection to an empty database.
        EnsureInitializedAsync(CancellationToken.None).GetAwaiter().GetResult();
        return NewConnection();
    }

    /// <inheritdoc />
    public async Task<DbConnection> CreateOpenAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var connection = NewConnection();
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    /// <summary>Closes the connection that keeps an in-memory database alive.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _keepAlive?.Dispose();
        _keepAlive = null;
        _initGate.Dispose();
    }

    private SqliteConnection NewConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connection = new SqliteConnection(_connectionString);
        connection.StateChange += ApplyPragmasOnOpen;
        return connection;
    }

    private static void ApplyPragmasOnOpen(object sender, StateChangeEventArgs e)
    {
        if (e.CurrentState != ConnectionState.Open || sender is not SqliteConnection connection)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = PragmaScript;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Applies the schema exactly once. The flag is only set after the DDL succeeds, so a failure
    /// (a locked file, a full disk) can be retried by the next caller instead of being cached.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            if (!_inMemory)
            {
                var directory = Path.GetDirectoryName(DatabasePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }

            var connection = new SqliteConnection(_connectionString);
            connection.StateChange += ApplyPragmasOnOpen;

            var keep = false;
            try
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await connection
                    .ExecuteAsync(new CommandDefinition(DatabaseSchema.CreateScript, cancellationToken: ct))
                    .ConfigureAwait(false);

                keep = _inMemory;
                if (keep)
                {
                    _keepAlive = connection;
                }

                _initialized = true;
            }
            finally
            {
                if (!keep)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _initGate.Release();
        }
    }
}
