using System.Data.Common;
using System.Globalization;
using Dapper;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Data.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IGameRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every bulk write runs as one transaction over one prepared statement: Dapper's multi-exec keeps
/// the same <c>DbCommand</c> and only rebinds parameters, so upserting a three thousand game
/// library is a single commit rather than three thousand of them.
/// </para>
/// <para>
/// Reads never issue a query per game. A hydrate is a fixed handful of queries — the rows, the
/// genre and tag links, the two name tables, friend ownership and collection membership — joined in
/// memory.
/// </para>
/// </remarks>
/// <param name="connectionFactory">Source of database connections.</param>
public sealed class GameRepository(IDbConnectionFactory connectionFactory) : IGameRepository
{
    /// <summary>Fallback language for tag and genre names when nothing else is recorded.</summary>
    private const string DefaultLanguage = "english";

    private const string GameColumns = """
        app_id              AS AppId,
        name                AS Name,
        app_type            AS AppType,
        is_owned            AS IsOwned,
        is_installed        AS IsInstalled,
        install_dir         AS InstallDir,
        install_path        AS InstallPath,
        size_bytes          AS SizeBytes,
        state_flags         AS StateFlags,
        buildid             AS BuildId,
        playtime_forever    AS PlaytimeForever,
        last_played_utc     AS LastPlayedUtc,
        last_local_scan_utc AS LastLocalScanUtc
        """;

    /// <summary>
    /// Timestamp merge used by both the installed and the owned upsert: the two sources report last
    /// played independently and the newer one wins. ISO-8601 UTC text compares chronologically.
    /// </summary>
    private const string LastPlayedMerge = """
        last_played_utc = CASE
            WHEN excluded.last_played_utc IS NOT NULL
             AND (Games.last_played_utc IS NULL OR excluded.last_played_utc > Games.last_played_utc)
            THEN excluded.last_played_utc
            ELSE Games.last_played_utc
        END
        """;

    /// <summary>A blank name never overwrites a good one, whichever source is writing.</summary>
    private const string NameMerge =
        "name = CASE WHEN excluded.name <> '' THEN excluded.name ELSE Games.name END";

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        return await HydrateAsync(connection, null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetAllAppIdsAsync(CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);

        return await QueryListAsync<int>(connection, "SELECT app_id FROM Games", null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<GameEntry?> GetAsync(int appId, CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        var entries = await HydrateAsync(connection, appId, ct).ConfigureAwait(false);
        return entries.Count > 0 ? entries[0] : null;
    }

    /// <inheritdoc />
    public async Task UpsertInstalledAsync(IReadOnlyList<InstalledApp> apps, CancellationToken ct = default)
    {
        if (apps.Count == 0)
        {
            return;
        }

        var scanUtc = DateTimeOffset.UtcNow;
        var rows = apps.Select(app => new
        {
            app.AppId,
            app.Name,
            app.InstallDir,
            InstallPath = app.FullInstallPath,
            app.LibraryPath,
            SizeBytes = app.SizeOnDisk,
            StateFlags = (int)app.StateFlags,
            app.BuildId,
            LastPlayedUtc = SqliteTypeHandlers.ToStorage(app.LastPlayed),
            ScanUtc = SqliteTypeHandlers.ToStorage(scanUtc),
        }).ToList();

        const string sql = $"""
            INSERT INTO Games (
                app_id, name, is_installed, install_dir, install_path, library_path,
                size_bytes, state_flags, buildid, last_played_utc, last_local_scan_utc)
            VALUES (
                @AppId, @Name, 1, @InstallDir, @InstallPath, @LibraryPath,
                @SizeBytes, @StateFlags, @BuildId, @LastPlayedUtc, @ScanUtc)
            ON CONFLICT(app_id) DO UPDATE SET
                {NameMerge},
                is_installed        = 1,
                install_dir         = excluded.install_dir,
                install_path        = excluded.install_path,
                library_path        = excluded.library_path,
                size_bytes          = excluded.size_bytes,
                state_flags         = excluded.state_flags,
                buildid             = excluded.buildid,
                {LastPlayedMerge},
                last_local_scan_utc = excluded.last_local_scan_utc
            """;

        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(sql, rows, transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertOwnedAsync(IReadOnlyList<OwnedGame> games, CancellationToken ct = default)
    {
        if (games.Count == 0)
        {
            return;
        }

        var rows = games.Select(game => new
        {
            game.AppId,
            Name = game.Name ?? string.Empty,
            PlaytimeForever = game.PlaytimeForeverMinutes,
            Playtime2Weeks = game.Playtime2WeeksMinutes,
            LastPlayedUtc = SqliteTypeHandlers.ToStorage(game.LastPlayed),
        }).ToList();

        const string sql = $"""
            INSERT INTO Games (app_id, name, is_owned, playtime_forever, playtime_2weeks, last_played_utc)
            VALUES (@AppId, @Name, 1, @PlaytimeForever, @Playtime2Weeks, @LastPlayedUtc)
            ON CONFLICT(app_id) DO UPDATE SET
                {NameMerge},
                is_owned         = 1,
                playtime_forever = excluded.playtime_forever,
                playtime_2weeks  = excluded.playtime_2weeks,
                {LastPlayedMerge}
            """;

        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(sql, rows, transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertMetadataAsync(IReadOnlyList<AppInfoEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // appinfo.vdf describes every app the client has ever heard of; only the ones we already
        // track get metadata, so the known ids are read once instead of guarded per statement.
        var known = (await connection
                .QueryAsync<int>(new CommandDefinition("SELECT app_id FROM Games", transaction: transaction, cancellationToken: ct))
                .ConfigureAwait(false))
            .ToHashSet();

        var covered = entries.Where(entry => known.Contains(entry.AppId)).ToList();
        if (covered.Count == 0)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        var scanUtc = DateTimeOffset.UtcNow;
        var updates = covered.Select(entry => new
        {
            entry.AppId,
            AppType = entry.Type,
            SortName = FirstNonEmpty(entry.SortAs, entry.Name),
            ScanUtc = SqliteTypeHandlers.ToStorage(scanUtc),
        }).ToList();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Games SET
                app_type           = COALESCE(@AppType, app_type),
                sort_name          = COALESCE(@SortName, sort_name),
                last_meta_scan_utc = @ScanUtc
            WHERE app_id = @AppId
            """,
            updates,
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        var appIds = covered.Select(entry => new { entry.AppId }).ToList();

        await ReplaceLinksAsync(
            connection,
            transaction,
            appIds,
            covered.SelectMany(entry => entry.GenreIds.Select((genreId, index) => new { entry.AppId, RefId = genreId, SortOrder = index })).ToList(),
            "GameGenres",
            "genre_id",
            ct).ConfigureAwait(false);

        await ReplaceLinksAsync(
            connection,
            transaction,
            appIds,
            covered.SelectMany(entry => entry.StoreTagIds.Select((tagId, index) => new { entry.AppId, RefId = tagId, SortOrder = index })).ToList(),
            "GameTags",
            "tag_id",
            ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> MarkNotInstalledExceptAsync(IReadOnlySet<int> installedAppIds, CancellationToken ct = default)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // A temp table keeps this one statement regardless of library size; a chunked NOT IN would
        // turn a 3000 game scan into dozens of statements and a NOT IN list into a parser stress test.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TEMP TABLE IF NOT EXISTS scanned_apps (app_id INTEGER PRIMARY KEY);
            DELETE FROM scanned_apps;
            """,
            transaction: transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        if (installedAppIds.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT OR IGNORE INTO scanned_apps (app_id) VALUES (@AppId)",
                installedAppIds.Select(appId => new { AppId = appId }).ToList(),
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        }

        // RETURNING makes the clear and the report of what it cleared the same statement, so the
        // delta is the rows SQLite actually touched rather than a guess reconstructed from a second
        // query that a concurrent scan could have moved underneath us.
        var cleared = (await connection.QueryAsync<int>(new CommandDefinition(
            """
            UPDATE Games SET
                is_installed = 0,
                install_dir  = NULL,
                install_path = NULL,
                library_path = NULL,
                size_bytes   = 0,
                state_flags  = 0,
                buildid      = NULL
            WHERE is_installed = 1
              AND app_id NOT IN (SELECT app_id FROM scanned_apps)
            RETURNING app_id
            """,
            transaction: transaction,
            cancellationToken: ct)).ConfigureAwait(false)).ToList();

        // Dropped in its own statement: the reader over the RETURNING rows has to be closed before
        // the schema the update read from can go away.
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TABLE IF EXISTS scanned_apps",
            transaction: transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return cleared;
    }

    private static async Task ReplaceLinksAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<object> appIds,
        IReadOnlyList<object> links,
        string table,
        string idColumn,
        CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM {table} WHERE app_id = @AppId",
            appIds,
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        if (links.Count == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            $"""
             INSERT INTO {table} (app_id, {idColumn}, sort_order)
             VALUES (@AppId, @RefId, @SortOrder)
             ON CONFLICT(app_id, {idColumn}) DO UPDATE SET sort_order = excluded.sort_order
             """,
            links,
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task<List<GameEntry>> HydrateAsync(DbConnection connection, int? appId, CancellationToken ct)
    {
        var filter = appId is null ? string.Empty : " WHERE app_id = @appId";
        var args = new { appId };

        var games = (await connection.QueryAsync<GameRow>(new CommandDefinition(
                $"""
                 SELECT {GameColumns}
                 FROM Games{filter}
                 ORDER BY COALESCE(NULLIF(sort_name, ''), name) COLLATE NOCASE, app_id
                 """,
                args,
                cancellationToken: ct)).ConfigureAwait(false))
            .ToList();

        if (games.Count == 0)
        {
            return [];
        }

        var language = await GetPreferredLanguageAsync(connection, ct).ConfigureAwait(false);

        var genreLinks = await QueryListAsync<LinkRow>(connection,
            $"SELECT app_id AS AppId, genre_id AS RefId FROM GameGenres{filter} ORDER BY app_id, sort_order", args, ct)
            .ConfigureAwait(false);
        var tagLinks = await QueryListAsync<LinkRow>(connection,
            $"SELECT app_id AS AppId, tag_id AS RefId FROM GameTags{filter} ORDER BY app_id, sort_order", args, ct)
            .ConfigureAwait(false);
        var friendLinks = await QueryListAsync<FriendLinkRow>(connection,
            $"SELECT app_id AS AppId, steam_id64 AS SteamId FROM FriendGames{filter} ORDER BY app_id, steam_id64", args, ct)
            .ConfigureAwait(false);
        var collectionLinks = await QueryListAsync<CollectionLinkRow>(connection,
            $"SELECT app_id AS AppId, collection_id AS CollectionId FROM GameCollections{filter} ORDER BY app_id, sort_order", args, ct)
            .ConfigureAwait(false);

        var genreNames = NamesByPreferredLanguage(
            await QueryListAsync<NameRow>(connection, "SELECT genre_id AS Id, lang AS Lang, name AS Name FROM Genres", null, ct).ConfigureAwait(false),
            language);
        var tagNames = NamesByPreferredLanguage(
            await QueryListAsync<NameRow>(connection, "SELECT tag_id AS Id, lang AS Lang, name AS Name FROM Tags", null, ct).ConfigureAwait(false),
            language);

        var genresByApp = Group(genreLinks, link => link.AppId, link => genreNames.TryGetValue(link.RefId, out var name)
            ? new GenreRef(link.RefId, name)
            : GenreRef.Unresolved(link.RefId));
        var tagsByApp = Group(tagLinks, link => link.AppId, link => tagNames.TryGetValue(link.RefId, out var name)
            ? new TagRef(link.RefId, name)
            : TagRef.Unresolved(link.RefId));
        var collectionsByApp = Group(collectionLinks, link => link.AppId, link => link.CollectionId);

        var ownersByApp = new Dictionary<int, List<ulong>>();
        foreach (var link in friendLinks)
        {
            if (!ulong.TryParse(link.SteamId, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId))
            {
                continue;
            }

            if (!ownersByApp.TryGetValue(link.AppId, out var owners))
            {
                owners = [];
                ownersByApp[link.AppId] = owners;
            }

            owners.Add(steamId);
        }

        var entries = new List<GameEntry>(games.Count);
        foreach (var row in games)
        {
            entries.Add(new GameEntry(
                AppId: row.AppId,
                Name: row.Name,
                IsOwned: row.IsOwned,
                IsInstalled: row.IsInstalled,
                StateFlags: (AppStateFlags)row.StateFlags,
                InstallDir: row.InstallDir,
                FullInstallPath: row.InstallPath,
                SizeBytes: row.SizeBytes,
                BuildId: row.BuildId,
                PlaytimeForeverMinutes: row.PlaytimeForever,
                LastPlayed: row.LastPlayedUtc,
                Genres: genresByApp.TryGetValue(row.AppId, out var genres) ? genres : [],
                Tags: tagsByApp.TryGetValue(row.AppId, out var tags) ? tags : [],
                Assets: null,
                AppType: row.AppType,
                FriendOwnerIds: ownersByApp.TryGetValue(row.AppId, out var owners) ? owners : [],
                CollectionIds: collectionsByApp.TryGetValue(row.AppId, out var collections) ? collections : [],
                LastLocalScanUtc: row.LastLocalScanUtc));
        }

        return entries;
    }

    private static async Task<List<T>> QueryListAsync<T>(DbConnection connection, string sql, object? args, CancellationToken ct) =>
        (await connection.QueryAsync<T>(new CommandDefinition(sql, args, cancellationToken: ct)).ConfigureAwait(false)).ToList();

    /// <summary>
    /// The language the name tables were last filled for, recorded as the <c>tag_names</c> payload.
    /// </summary>
    private static async Task<string> GetPreferredLanguageAsync(DbConnection connection, CancellationToken ct)
    {
        var language = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT payload FROM SyncMeta WHERE key = @key",
            new { key = SyncKeys.TagNames },
            cancellationToken: ct)).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language;
    }

    /// <summary>
    /// Collapses the id/language keyed name tables to one name per id: the preferred language when
    /// it is stored, otherwise whatever else is there, so an id never silently loses its name.
    /// </summary>
    private static Dictionary<int, string> NamesByPreferredLanguage(IReadOnlyList<NameRow> rows, string language)
    {
        var names = new Dictionary<int, string>();
        var exact = new HashSet<int>();

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
            {
                continue;
            }

            if (string.Equals(row.Lang, language, StringComparison.OrdinalIgnoreCase))
            {
                names[row.Id] = row.Name;
                exact.Add(row.Id);
            }
            else if (!exact.Contains(row.Id))
            {
                names.TryAdd(row.Id, row.Name);
            }
        }

        return names;
    }

    private static Dictionary<int, List<TValue>> Group<TRow, TValue>(
        IReadOnlyList<TRow> rows,
        Func<TRow, int> keySelector,
        Func<TRow, TValue> valueSelector)
    {
        var grouped = new Dictionary<int, List<TValue>>();
        foreach (var row in rows)
        {
            var key = keySelector(row);
            if (!grouped.TryGetValue(key, out var values))
            {
                values = [];
                grouped[key] = values;
            }

            values.Add(valueSelector(row));
        }

        return grouped;
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        Array.Find(candidates, candidate => !string.IsNullOrWhiteSpace(candidate));

    private sealed class GameRow
    {
        public int AppId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? AppType { get; set; }

        public bool IsOwned { get; set; }

        public bool IsInstalled { get; set; }

        public string? InstallDir { get; set; }

        public string? InstallPath { get; set; }

        public long SizeBytes { get; set; }

        public int StateFlags { get; set; }

        public string? BuildId { get; set; }

        public int PlaytimeForever { get; set; }

        public DateTimeOffset? LastPlayedUtc { get; set; }

        public DateTimeOffset? LastLocalScanUtc { get; set; }
    }

    private sealed class LinkRow
    {
        public int AppId { get; set; }

        public int RefId { get; set; }
    }

    private sealed class CollectionLinkRow
    {
        public int AppId { get; set; }

        public long CollectionId { get; set; }
    }

    private sealed class FriendLinkRow
    {
        public int AppId { get; set; }

        public string SteamId { get; set; } = string.Empty;
    }

    private sealed class NameRow
    {
        public int Id { get; set; }

        public string Lang { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }
}
