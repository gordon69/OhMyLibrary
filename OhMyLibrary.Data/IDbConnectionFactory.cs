using System.Data.Common;

namespace OhMyLibrary.Data;

/// <summary>
/// Creates connections to the local SQLite database under <c>%LOCALAPPDATA%/OhMyLibrary/</c>.
/// </summary>
/// <remarks>
/// The implementation is responsible for creating the folder, applying
/// <see cref="DatabaseSchema.CreateScript"/> once, and setting the connection pragmas
/// (WAL, foreign keys). Callers own the returned connection and must dispose it.
/// </remarks>
public interface IDbConnectionFactory
{
    /// <summary>Absolute path of the database file.</summary>
    string DatabasePath { get; }

    /// <summary>Creates a closed connection.</summary>
    DbConnection Create();

    /// <summary>Creates a connection and opens it.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<DbConnection> CreateOpenAsync(CancellationToken ct = default);
}
