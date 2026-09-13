using Dapper;
using OhMyLibrary.Core.Abstractions;

namespace OhMyLibrary.Data.Repositories;

/// <summary>
/// Dapper implementation of <see cref="ISyncMetaRepository"/>.
/// </summary>
/// <remarks>
/// The schema seeds its own version row with a sentinel timestamp of <c>DateTimeOffset.MinValue</c>,
/// and a caller asking "when did this last run" wants <see langword="null"/> for that, not the year
/// one — so the sentinel is translated back to "never" on the way out.
/// </remarks>
/// <param name="connectionFactory">Source of database connections.</param>
public sealed class SyncMetaRepository(IDbConnectionFactory connectionFactory) : ISyncMetaRepository
{
    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetLastRunAsync(string key, CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);

        var stored = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT last_run_utc FROM SyncMeta WHERE key = @key",
            new { key },
            cancellationToken: ct)).ConfigureAwait(false);

        var lastRun = SqliteTypeHandlers.FromStorage(stored);
        return lastRun == default(DateTimeOffset) ? null : lastRun;
    }

    /// <inheritdoc />
    public async Task SetLastRunAsync(
        string key,
        DateTimeOffset whenUtc,
        string? payload,
        CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);

        // A null payload means "I have nothing to say about it", not "erase what is there".
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO SyncMeta (key, last_run_utc, payload)
            VALUES (@Key, @WhenUtc, @Payload)
            ON CONFLICT(key) DO UPDATE SET
                last_run_utc = excluded.last_run_utc,
                payload      = COALESCE(excluded.payload, SyncMeta.payload)
            """,
            new
            {
                Key = key,
                WhenUtc = SqliteTypeHandlers.ToStorage(whenUtc),
                Payload = payload,
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> GetPayloadAsync(string key, CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT payload FROM SyncMeta WHERE key = @key",
            new { key },
            cancellationToken: ct)).ConfigureAwait(false);
    }
}
