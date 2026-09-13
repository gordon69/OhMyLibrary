using System.Data.Common;
using OhMyLibrary.Data;

namespace OhMyLibrary.Tests.Data;

/// <summary>
/// Wraps an <see cref="IDbConnectionFactory"/> and counts the connections handed out.
/// </summary>
/// <remarks>
/// A repository read that opens one connection cannot be issuing a query per row, which is the
/// cheapest way to pin down "no N+1" without instrumenting SQLite itself.
/// </remarks>
/// <param name="inner">The factory doing the real work.</param>
public sealed class CountingConnectionFactory(IDbConnectionFactory inner) : IDbConnectionFactory
{
    private int _opened;

    /// <summary>How many connections have been created since the last <see cref="Reset"/>.</summary>
    public int OpenedConnections => Volatile.Read(ref _opened);

    /// <inheritdoc />
    public string DatabasePath => inner.DatabasePath;

    /// <summary>Sets the counter back to zero, to exclude the arrange phase of a test.</summary>
    public void Reset() => Interlocked.Exchange(ref _opened, 0);

    /// <inheritdoc />
    public DbConnection Create()
    {
        Interlocked.Increment(ref _opened);
        return inner.Create();
    }

    /// <inheritdoc />
    public Task<DbConnection> CreateOpenAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _opened);
        return inner.CreateOpenAsync(ct);
    }
}
