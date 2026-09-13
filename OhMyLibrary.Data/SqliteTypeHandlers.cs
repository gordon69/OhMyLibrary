using System.Data;
using System.Globalization;
using Dapper;

namespace OhMyLibrary.Data;

/// <summary>
/// Global Dapper configuration for the storage conventions this assembly relies on.
/// </summary>
/// <remarks>
/// SQLite has no date type, so every timestamp column is TEXT in round-trip ISO-8601
/// (<c>"O"</c>) and always UTC — a format that sorts and compares correctly as text, which is what
/// lets the repositories do <c>MAX</c>-style timestamp merges in SQL. Dapper maps
/// <see cref="DateTimeOffset"/> natively by default, and <c>Microsoft.Data.Sqlite</c> would then
/// write it in a different (space-separated) shape, so the native mapping is removed and replaced
/// with <see cref="UtcDateTimeOffsetHandler"/>.
/// </remarks>
public static class SqliteTypeHandlers
{
    private static int _registered;

    /// <summary>
    /// The one storage format for timestamps: round-trip ISO-8601, always normalised to UTC.
    /// </summary>
    public const string TimestampFormat = "O";

    /// <summary>
    /// Installs the Dapper type handlers. Idempotent and thread-safe — only the first call does
    /// any work — so it is safe to call it from both the DI registration and a bare constructor.
    /// </summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        // Dapper's built-in type map wins over custom handlers, so it has to go first.
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));
        SqlMapper.AddTypeHandler(new UtcDateTimeOffsetHandler());
    }

    /// <summary>Formats a timestamp the way the database stores it.</summary>
    /// <param name="value">The value to format, or <see langword="null"/>.</param>
    /// <returns>An ISO-8601 UTC string, or <see langword="null"/> when the input was null.</returns>
    public static string? ToStorage(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>Parses a stored timestamp.</summary>
    /// <param name="value">The stored text, which may be null or unparseable.</param>
    /// <returns>The UTC value, or <see langword="null"/> when there was nothing usable to parse.</returns>
    public static DateTimeOffset? FromStorage(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    /// <summary>
    /// Reads and writes <see cref="DateTimeOffset"/> as an ISO-8601 UTC string.
    /// </summary>
    /// <remarks>
    /// Dapper never routes a <c>NULL</c> column through a handler when it is mapping onto a member,
    /// so <see cref="Parse"/> only sees real values in practice; an unexpected null degrades to
    /// <see langword="default"/> rather than throwing out of a query.
    /// </remarks>
    private sealed class UtcDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
        }

        public override DateTimeOffset Parse(object value) => value switch
        {
            string text => FromStorage(text) ?? default,
            DateTimeOffset stored => stored.ToUniversalTime(),
            DateTime stored => new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc)),
            _ => default,
        };
    }
}
