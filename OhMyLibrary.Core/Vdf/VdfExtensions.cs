using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security;
using Serilog;
using ValveKeyValue;

namespace OhMyLibrary.Core.Vdf;

/// <summary>
/// Null-tolerant, exception-free accessors over <see cref="KVObject"/>.
/// </summary>
/// <remarks>
/// <para>
/// ValveKeyValue resolves child keys case-sensitively, while Valve's own writers are not consistent
/// about casing between client builds, so every lookup falls back to an ordinal-ignore-case scan of
/// the children.
/// </para>
/// <para>
/// Every accessor tolerates a <see langword="null"/> node, a missing key and a value of the wrong
/// kind — a collection where a number was expected, for instance. That is what keeps the readers
/// from being a wall of null checks: a field Steam stopped writing simply reads as absent.
/// </para>
/// </remarks>
internal static class VdfExtensions
{
    /// <summary>Largest value <see cref="DateTimeOffset.FromUnixTimeSeconds"/> accepts.</summary>
    private const long MaxUnixSeconds = 253402300799L;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Gets a child by key, or <see langword="null"/> when the node has no such child.</summary>
    internal static KVObject? Child(this KVObject? node, string key)
    {
        if (node is null || !node.IsCollection)
        {
            return null;
        }

        if (node.TryGetValue(key, out var child))
        {
            return child;
        }

        foreach (var candidate in node.Children)
        {
            if (candidate.Key is not null && string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the value is a single value rather than a collection, an array, a blob or null.
    /// </summary>
    internal static bool IsScalar([NotNullWhen(true)] this KVObject? value) =>
        value is not null
        && value.ValueType is not (KVValueType.Collection or KVValueType.Array or KVValueType.BinaryBlob or KVValueType.Null);

    /// <summary>
    /// Renders a scalar value as text, or <see langword="null"/> when it is not a scalar. Binary
    /// key-values carry typed numbers, so this is also how an <see cref="int"/> becomes parseable text.
    /// </summary>
    internal static string? AsScalarString(this KVObject? value)
    {
        if (!value.IsScalar())
        {
            return null;
        }

        try
        {
            return value.ToString(Invariant);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Reads a string field.</summary>
    internal static bool TryGetString(this KVObject? node, string key, [NotNullWhen(true)] out string? value)
    {
        value = node.Child(key).AsScalarString();
        return value is not null;
    }

    /// <summary>Reads a 32-bit integer field, whether it is stored as text or as a binary number.</summary>
    internal static bool TryGetInt(this KVObject? node, string key, out int value) =>
        int.TryParse(node.Child(key).AsScalarString(), NumberStyles.Integer, Invariant, out value);

    /// <summary>Reads a 64-bit integer field.</summary>
    internal static bool TryGetLong(this KVObject? node, string key, out long value) =>
        long.TryParse(node.Child(key).AsScalarString(), NumberStyles.Integer, Invariant, out value);

    /// <summary>Reads an unsigned 64-bit field such as a SteamID64.</summary>
    internal static bool TryGetULong(this KVObject? node, string key, out ulong value) =>
        ulong.TryParse(node.Child(key).AsScalarString(), NumberStyles.Integer, Invariant, out value);

    /// <summary>
    /// Reads a unix-seconds field. Steam writes <c>0</c> for "never", which is reported as absent
    /// rather than as 1970.
    /// </summary>
    internal static bool TryGetUnixTime(this KVObject? node, string key, out DateTimeOffset value)
    {
        value = default;

        if (!node.TryGetLong(key, out var seconds) || seconds <= 0 || seconds > MaxUnixSeconds)
        {
            return false;
        }

        value = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }

    /// <summary>
    /// Reads an index-keyed list such as <c>genres { "0" "3" }</c>, preserving file order — for
    /// <c>store_tags</c> that order is Valve's own relevance ranking. Elements that are not numbers
    /// are skipped.
    /// </summary>
    /// <returns><see langword="true"/> when the key exists and is a list, even when it is empty.</returns>
    internal static bool TryGetOrderedIntList(this KVObject? node, string key, out IReadOnlyList<int> values)
    {
        values = [];

        var list = node.Child(key);
        if (list is null || (!list.IsCollection && !list.IsArray))
        {
            return false;
        }

        List<int>? ids = null;
        foreach (var element in list.Children)
        {
            if (int.TryParse(element.Value.AsScalarString(), NumberStyles.Integer, Invariant, out var id))
            {
                (ids ??= new List<int>(list.Count)).Add(id);
            }
        }

        if (ids is not null)
        {
            values = ids;
        }

        return true;
    }

    /// <inheritdoc cref="TryGetString"/>
    internal static string? GetString(this KVObject? node, string key) => node.Child(key).AsScalarString();

    /// <inheritdoc cref="TryGetInt"/>
    internal static int GetInt(this KVObject? node, string key) => node.TryGetInt(key, out var value) ? value : 0;

    /// <inheritdoc cref="TryGetInt"/>
    internal static int? GetIntOrNull(this KVObject? node, string key) => node.TryGetInt(key, out var value) ? value : null;

    /// <inheritdoc cref="TryGetLong"/>
    internal static long GetLong(this KVObject? node, string key) => node.TryGetLong(key, out var value) ? value : 0L;

    /// <inheritdoc cref="TryGetULong"/>
    internal static ulong GetULong(this KVObject? node, string key) => node.TryGetULong(key, out var value) ? value : 0UL;

    /// <inheritdoc cref="TryGetUnixTime"/>
    internal static DateTimeOffset? GetUnixTime(this KVObject? node, string key) =>
        node.TryGetUnixTime(key, out var value) ? value : null;

    /// <inheritdoc cref="TryGetOrderedIntList"/>
    internal static IReadOnlyList<int> GetOrderedIntList(this KVObject? node, string key) =>
        node.TryGetOrderedIntList(key, out var values) ? values : [];
}

/// <summary>
/// Opens and parses Steam's <i>text</i> KeyValues files.
/// </summary>
/// <remarks>
/// <para>
/// Steam rewrites these files underneath us — a manifest is rewritten on every state change — so a
/// sharing violation is a normal outcome rather than a fault. Files are opened with
/// <see cref="FileShare.ReadWrite"/> and a read that fails transiently is retried a couple of times
/// with a short backoff before it is abandoned.
/// </para>
/// <para>
/// Escape sequences are enabled because Steam writes paths escaped
/// (<c>"C:\\Program Files (x86)\\Steam"</c>); without that the backslashes come back doubled.
/// Valve's truncate-on-bad-escape behaviour is enabled too, so one odd byte in one value cannot
/// fail the whole file.
/// </para>
/// </remarks>
internal static class VdfFile
{
    private const int MaxAttempts = 3;
    private const int RetryDelayMilliseconds = 30;
    private const int ReadBufferSize = 8192;

    private static readonly KVSerializerOptions TextOptions = new()
    {
        HasEscapeSequences = true,
        EnableValveNullByteBugBehavior = true,
    };

    /// <summary>
    /// Parses a text KeyValues file, returning <see langword="null"/> — never throwing — when it is
    /// missing, locked or malformed.
    /// </summary>
    /// <param name="path">Absolute path of the file.</param>
    /// <param name="logger">Logger that receives a warning describing anything that was skipped.</param>
    /// <param name="what">Human-readable name of the file kind, used in that warning.</param>
    internal static KVDocument? TryLoadText(string path, ILogger logger, string what)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    ReadBufferSize,
                    FileOptions.SequentialScan);

                return KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream, TextOptions);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                Thread.Sleep(RetryDelayMilliseconds * attempt);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Steam deletes manifests as it uninstalls; a vanished file needs no stack trace.
                logger.Warning("Skipping {What} {Path}: it no longer exists", what, path);
                return null;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                logger.Warning(ex, "Skipping {What} {Path}: {Reason}", what, path, ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// True for failures that are worth a second attempt: the file is there but somebody else is
    /// mid-write. A missing file or folder is not transient.
    /// </summary>
    private static bool IsTransient(Exception ex) =>
        ex is IOException and not (FileNotFoundException or DirectoryNotFoundException);

    /// <summary>
    /// True for every failure a reader is expected to absorb. Anything else — an
    /// <see cref="OutOfMemoryException"/>, say — is a real fault and is left to propagate.
    /// </summary>
    internal static bool IsExpected(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or FormatException
            or OverflowException
            or KeyValueException;
}
