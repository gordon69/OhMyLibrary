namespace OhMyLibrary.Data;

/// <summary>
/// The complete SQLite schema, as one idempotent script.
/// </summary>
/// <remarks>
/// <para>
/// Every statement is <c>CREATE ... IF NOT EXISTS</c>, so running <see cref="CreateScript"/> on an
/// existing database is a no-op and it can be executed unconditionally at startup.
/// </para>
/// <para>Conventions the repositories rely on:</para>
/// <list type="bullet">
///   <item><description>
///     Timestamps are TEXT in round-trip ISO-8601 (<c>"O"</c>) and always UTC. SQLite has no date
///     type and ISO-8601 sorts correctly as text.
///   </description></item>
///   <item><description>
///     <c>steam_id64</c> is TEXT holding the decimal id. SQLite INTEGER is a signed 64-bit value,
///     and a SteamID64 is unsigned, so storing it as INTEGER would overflow in principle.
///   </description></item>
///   <item><description>
///     Booleans are INTEGER <c>0</c>/<c>1</c>.
///   </description></item>
/// </list>
/// </remarks>
public static class DatabaseSchema
{
    /// <summary>Version this build's schema describes. Bump it when the DDL changes shape.</summary>
    public const int Version = 1;

    /// <summary>
    /// <c>SyncMeta</c> key under which the schema version is recorded, so a future migration has
    /// something to read before it touches anything.
    /// </summary>
    public const string VersionKey = "schema_version";

    /// <summary>
    /// The full DDL. Execute it as a single script; every statement is idempotent.
    /// </summary>
    public const string CreateScript = """
        CREATE TABLE IF NOT EXISTS Games (
            app_id              INTEGER PRIMARY KEY,
            name                TEXT    NOT NULL,
            sort_name           TEXT,
            app_type            TEXT,
            is_owned            INTEGER NOT NULL DEFAULT 0,
            is_installed        INTEGER NOT NULL DEFAULT 0,
            install_dir         TEXT,
            install_path        TEXT,
            library_path        TEXT,
            size_bytes          INTEGER NOT NULL DEFAULT 0,
            state_flags         INTEGER NOT NULL DEFAULT 0,
            buildid             TEXT,
            playtime_forever    INTEGER NOT NULL DEFAULT 0,
            playtime_2weeks     INTEGER NOT NULL DEFAULT 0,
            last_played_utc     TEXT,
            last_local_scan_utc TEXT,
            last_meta_scan_utc  TEXT
        );

        CREATE TABLE IF NOT EXISTS Genres (
            genre_id INTEGER NOT NULL,
            lang     TEXT    NOT NULL,
            name     TEXT    NOT NULL,
            PRIMARY KEY (genre_id, lang)
        );

        CREATE TABLE IF NOT EXISTS GameGenres (
            app_id     INTEGER NOT NULL,
            genre_id   INTEGER NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (app_id, genre_id)
        );

        CREATE TABLE IF NOT EXISTS Tags (
            tag_id INTEGER NOT NULL,
            lang   TEXT    NOT NULL,
            name   TEXT    NOT NULL,
            PRIMARY KEY (tag_id, lang)
        );

        CREATE TABLE IF NOT EXISTS GameTags (
            app_id     INTEGER NOT NULL,
            tag_id     INTEGER NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (app_id, tag_id)
        );

        CREATE TABLE IF NOT EXISTS Friends (
            steam_id64        TEXT    PRIMARY KEY,
            persona_name      TEXT,
            avatar_url        TEXT,
            profile_url       TEXT,
            persona_state     INTEGER NOT NULL DEFAULT 0,
            friend_since_utc  TEXT,
            game_list_visible INTEGER NOT NULL DEFAULT 0,
            last_synced_utc   TEXT
        );

        CREATE TABLE IF NOT EXISTS FriendGames (
            steam_id64 TEXT    NOT NULL,
            app_id     INTEGER NOT NULL,
            PRIMARY KEY (steam_id64, app_id)
        );

        CREATE TABLE IF NOT EXISTS Collections (
            collection_id INTEGER PRIMARY KEY AUTOINCREMENT,
            name          TEXT    NOT NULL,
            sort_order    INTEGER NOT NULL DEFAULT 0,
            created_utc   TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS GameCollections (
            app_id        INTEGER NOT NULL,
            collection_id INTEGER NOT NULL,
            sort_order    INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (app_id, collection_id)
        );

        CREATE TABLE IF NOT EXISTS SyncMeta (
            key          TEXT PRIMARY KEY,
            last_run_utc TEXT NOT NULL,
            payload      TEXT
        );

        CREATE INDEX IF NOT EXISTS IX_Games_is_installed          ON Games (is_installed);
        CREATE INDEX IF NOT EXISTS IX_Games_name                  ON Games (name);
        CREATE INDEX IF NOT EXISTS IX_GameTags_tag_id             ON GameTags (tag_id);
        CREATE INDEX IF NOT EXISTS IX_GameGenres_genre_id         ON GameGenres (genre_id);
        CREATE INDEX IF NOT EXISTS IX_FriendGames_app_id          ON FriendGames (app_id);
        CREATE INDEX IF NOT EXISTS IX_GameCollections_collection  ON GameCollections (collection_id);

        INSERT OR IGNORE INTO SyncMeta (key, last_run_utc, payload)
        VALUES ('schema_version', '0001-01-01T00:00:00.0000000+00:00', '1');
        """;
}
