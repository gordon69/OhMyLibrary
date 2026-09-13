using System.Globalization;
using Dapper;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Data.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IFriendRepository"/>.
/// </summary>
/// <remarks>
/// A SteamID64 is unsigned and would overflow SQLite's signed INTEGER, so it is stored as decimal
/// text and converted here; callers only ever see <see cref="ulong"/>. A row whose stored id cannot
/// be parsed is skipped rather than thrown over — a corrupt cache row must not empty the friends list.
/// </remarks>
/// <param name="connectionFactory">Source of database connections.</param>
public sealed class FriendRepository(IDbConnectionFactory connectionFactory) : IFriendRepository
{
    private const string FriendColumns = """
        steam_id64        AS SteamId,
        persona_name      AS PersonaName,
        avatar_url        AS AvatarUrl,
        profile_url       AS ProfileUrl,
        persona_state     AS PersonaState,
        friend_since_utc  AS FriendSinceUtc,
        game_list_visible AS GameListVisible,
        last_synced_utc   AS LastSyncedUtc
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<FriendSummary>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);

        var rows = await connection.QueryAsync<FriendRow>(new CommandDefinition(
            $"""
             SELECT {FriendColumns}
             FROM Friends
             ORDER BY persona_name COLLATE NOCASE, steam_id64
             """,
            cancellationToken: ct)).ConfigureAwait(false);

        return Materialise(rows);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FriendSummary>> GetOwnersOfAsync(int appId, CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);

        var rows = await connection.QueryAsync<FriendRow>(new CommandDefinition(
            $"""
             SELECT {FriendColumns}
             FROM Friends
             WHERE steam_id64 IN (SELECT steam_id64 FROM FriendGames WHERE app_id = @appId)
             ORDER BY persona_name COLLATE NOCASE, steam_id64
             """,
            new { appId },
            cancellationToken: ct)).ConfigureAwait(false);

        return Materialise(rows);
    }

    /// <inheritdoc />
    public async Task UpsertFriendsAsync(IReadOnlyList<FriendSummary> friends, CancellationToken ct = default)
    {
        if (friends.Count == 0)
        {
            return;
        }

        var rows = friends.Select(friend => new
        {
            SteamId = friend.SteamId64.ToString(CultureInfo.InvariantCulture),
            friend.PersonaName,
            friend.AvatarUrl,
            friend.ProfileUrl,
            PersonaState = (int)friend.State,
            FriendSinceUtc = SqliteTypeHandlers.ToStorage(friend.FriendSince),
            GameListVisible = friend.GameListVisible ? 1 : 0,
            LastSyncedUtc = SqliteTypeHandlers.ToStorage(friend.LastSyncedUtc),
        }).ToList();

        // GetFriendList knows ids and nothing else; GetPlayerSummaries knows names and avatars;
        // the per-friend library fan-out knows visibility. Each fills in its own columns and leaves
        // the rest of the row alone, so whichever call runs last cannot erase the others' work.
        const string sql = """
            INSERT INTO Friends (
                steam_id64, persona_name, avatar_url, profile_url,
                persona_state, friend_since_utc, game_list_visible, last_synced_utc)
            VALUES (
                @SteamId, @PersonaName, @AvatarUrl, @ProfileUrl,
                @PersonaState, @FriendSinceUtc, @GameListVisible, @LastSyncedUtc)
            ON CONFLICT(steam_id64) DO UPDATE SET
                persona_name      = CASE WHEN excluded.persona_name <> '' THEN excluded.persona_name ELSE Friends.persona_name END,
                avatar_url        = COALESCE(excluded.avatar_url, Friends.avatar_url),
                profile_url       = COALESCE(excluded.profile_url, Friends.profile_url),
                persona_state     = excluded.persona_state,
                friend_since_utc  = COALESCE(excluded.friend_since_utc, Friends.friend_since_utc),
                game_list_visible = CASE
                                        WHEN excluded.last_synced_utc IS NOT NULL
                                        THEN excluded.game_list_visible
                                        ELSE Friends.game_list_visible
                                    END,
                last_synced_utc   = COALESCE(excluded.last_synced_utc, Friends.last_synced_utc)
            """;

        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(sql, rows, transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReplaceFriendGamesAsync(
        ulong steamId64,
        IReadOnlyList<int> appIds,
        CancellationToken ct = default)
    {
        var steamId = steamId64.ToString(CultureInfo.InvariantCulture);
        var syncedUtc = SqliteTypeHandlers.ToStorage(DateTimeOffset.UtcNow);

        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM FriendGames WHERE steam_id64 = @SteamId",
            new { SteamId = steamId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        if (appIds.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT OR IGNORE INTO FriendGames (steam_id64, app_id) VALUES (@SteamId, @AppId)",
                appIds.Select(appId => new { SteamId = steamId, AppId = appId }).ToList(),
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        }

        // Reaching here means the library really was readable, so the row is stamped visible. The
        // upsert also covers the case where the fan-out saw a friend the friend-list write missed.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Friends (steam_id64, persona_name, persona_state, game_list_visible, last_synced_utc)
            VALUES (@SteamId, '', 0, 1, @SyncedUtc)
            ON CONFLICT(steam_id64) DO UPDATE SET
                game_list_visible = 1,
                last_synced_utc   = excluded.last_synced_utc
            """,
            new { SteamId = steamId, SyncedUtc = syncedUtc },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static List<FriendSummary> Materialise(IEnumerable<FriendRow> rows)
    {
        var friends = new List<FriendSummary>();
        foreach (var row in rows)
        {
            if (!ulong.TryParse(row.SteamId, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId))
            {
                continue;
            }

            friends.Add(new FriendSummary(
                SteamId64: steamId,
                PersonaName: row.PersonaName ?? string.Empty,
                AvatarUrl: row.AvatarUrl,
                ProfileUrl: row.ProfileUrl,
                State: (PersonaState)row.PersonaState,
                FriendSince: row.FriendSinceUtc,
                GameListVisible: row.GameListVisible,
                LastSyncedUtc: row.LastSyncedUtc));
        }

        return friends;
    }

    private sealed class FriendRow
    {
        public string SteamId { get; set; } = string.Empty;

        public string? PersonaName { get; set; }

        public string? AvatarUrl { get; set; }

        public string? ProfileUrl { get; set; }

        public int PersonaState { get; set; }

        public DateTimeOffset? FriendSinceUtc { get; set; }

        public bool GameListVisible { get; set; }

        public DateTimeOffset? LastSyncedUtc { get; set; }
    }
}
