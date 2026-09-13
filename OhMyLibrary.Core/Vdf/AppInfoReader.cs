using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using Serilog;
using Serilog.Core;
using ValveKeyValue;

namespace OhMyLibrary.Core.Vdf;

/// <summary>
/// Reads the binary <c>appcache/appinfo.vdf</c> container: name, type, genres, store tags,
/// categories, localised names and release info for every app the client knows about, with no
/// network call and no API key.
/// </summary>
/// <remarks>
/// <para>
/// ValveKeyValue parses the per-app key-values blob but not the container around it, so the
/// container loop is hand-written. Layout, verified against a live install:
/// </para>
/// <code>
/// uint32   magic              0x07564429 (v29) or 0x07564428 (v28)
/// uint32   universe
/// int64    stringTableOffset  v29 only
/// // repeating until appid == 0:
/// uint32   appid
/// uint32   size               bytes of the record after this field
/// uint32   infoState
/// uint32   lastUpdated        unix seconds
/// uint64   picsToken
/// byte[20] sha1TextVdf
/// uint32   changeNumber
/// byte[20] sha1BinaryVdf
/// byte[]   binaryKeyValues    fills the rest of size
/// // at stringTableOffset (v29):
/// int32    count
/// count x  NUL-terminated UTF-8 strings
/// </code>
/// <para>
/// The string table is read first because the record bodies reference it for their keys, then the
/// stream seeks back to the first record. After every record the stream is positioned explicitly at
/// <c>recordStart + size</c> rather than wherever the key-values parser stopped, so one malformed
/// app cannot desync the remaining few thousand.
/// </para>
/// <para>
/// The file is around 8 MB and holds close to 3000 records, so it is streamed through a
/// <see cref="BufferedStream"/> and only the requested records are materialised: pass a filter to
/// <see cref="ReadApps"/> and every other body is skipped over without being parsed.
/// </para>
/// </remarks>
public sealed class AppInfoReader : IAppInfoReader
{
    private const uint MagicV29 = 0x07564429;
    private const uint MagicV28 = 0x07564428;

    private const int Sha1Length = 20;

    /// <summary>infoState, lastUpdated, picsToken, both SHA-1s and changeNumber.</summary>
    private const int RecordHeaderLength = sizeof(uint) + sizeof(uint) + sizeof(ulong) + Sha1Length + sizeof(uint) + Sha1Length;

    private const int StreamBufferSize = 64 * 1024;
    private const int StringBufferSize = 128;

    /// <summary>Sanity bound on the string table so a corrupt offset cannot allocate wildly.</summary>
    private const int MaxStringTableEntries = 1 << 22;

    private const string CategoryKeyPrefix = "category_";
    private const string CommonKey = "common";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly IReadOnlyDictionary<string, string> NoLocalizedNames = ReadOnlyDictionary<string, string>.Empty;

    private readonly ILogger _logger;

    /// <summary>Creates a reader.</summary>
    /// <param name="logger">
    /// Logger for skipped records. <see langword="null"/> is accepted and silences the reader.
    /// </param>
    public AppInfoReader(ILogger? logger = null) => _logger = (logger ?? Logger.None).ForContext<AppInfoReader>();

    /// <inheritdoc />
    public IReadOnlyList<AppInfoEntry> ReadAll(string appInfoVdfPath, CancellationToken ct = default) =>
        ReadCore(appInfoVdfPath, wanted: null, ct);

    /// <inheritdoc />
    public IReadOnlyDictionary<int, AppInfoEntry> ReadApps(
        string appInfoVdfPath,
        IReadOnlySet<int> appIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(appIds);

        if (appIds.Count == 0)
        {
            return ReadOnlyDictionary<int, AppInfoEntry>.Empty;
        }

        var entries = ReadCore(appInfoVdfPath, appIds, ct);

        var found = new Dictionary<int, AppInfoEntry>(entries.Count);
        foreach (var entry in entries)
        {
            found[entry.AppId] = entry;
        }

        return found;
    }

    /// <summary>
    /// Walks the container once.
    /// </summary>
    /// <param name="appInfoVdfPath">Absolute path of <c>appinfo.vdf</c>.</param>
    /// <param name="wanted">
    /// App ids to materialise, or <see langword="null"/> for all of them. When it is supplied the
    /// walk stops as soon as every id has been found.
    /// </param>
    /// <param name="ct">Cancellation token, observed once per record and while reading the string table.</param>
    private List<AppInfoEntry> ReadCore(string appInfoVdfPath, IReadOnlySet<int>? wanted, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entries = new List<AppInfoEntry>(wanted?.Count ?? 4096);

        if (string.IsNullOrWhiteSpace(appInfoVdfPath) || !File.Exists(appInfoVdfPath))
        {
            _logger.Warning("No appinfo.vdf at {Path}; local app metadata is unavailable", appInfoVdfPath);
            return entries;
        }

        try
        {
            using var file = new FileStream(
                appInfoVdfPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.SequentialScan);
            using var stream = new BufferedStream(file, StreamBufferSize);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            var magic = reader.ReadUInt32();
            if (magic is not (MagicV29 or MagicV28))
            {
                _logger.Warning("Unsupported appinfo.vdf magic 0x{Magic:X8} in {Path}", magic, appInfoVdfPath);
                return entries;
            }

            _ = reader.ReadUInt32(); // universe

            var recordsEnd = stream.Length;
            StringTable? stringTable = null;

            if (magic == MagicV29)
            {
                var stringTableOffset = reader.ReadInt64();
                var firstRecord = stream.Position;

                stringTable = ReadStringTable(stream, reader, stringTableOffset, appInfoVdfPath, ct);
                if (stringTable is null)
                {
                    // Without the table the record keys cannot be resolved, so there is nothing to salvage.
                    return entries;
                }

                recordsEnd = stringTableOffset;
                stream.Position = firstRecord;
            }

            ReadRecords(stream, reader, stringTable, recordsEnd, appInfoVdfPath, wanted, entries, ct);
        }
        catch (Exception ex) when (VdfFile.IsExpected(ex))
        {
            _logger.Warning(ex, "Abandoning appinfo.vdf {Path} after {Count} record(s): {Reason}", appInfoVdfPath, entries.Count, ex.Message);
        }

        return entries;
    }

    private void ReadRecords(
        Stream stream,
        BinaryReader reader,
        StringTable? stringTable,
        long recordsEnd,
        string path,
        IReadOnlySet<int>? wanted,
        List<AppInfoEntry> entries,
        CancellationToken ct)
    {
        var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Binary);
        var options = new KVSerializerOptions { StringTable = stringTable };

        while (true)
        {
            // Once per record. The container holds a few thousand of them and a full pass is
            // multi-second work, so a token checked only before the walk would leave shutdown
            // waiting the whole parse out.
            ct.ThrowIfCancellationRequested();

            if (stream.Position + sizeof(uint) > recordsEnd)
            {
                _logger.Warning("appinfo.vdf {Path} ends at offset {Offset} without a terminating app id", path, stream.Position);
                return;
            }

            var appId = reader.ReadUInt32();
            if (appId == 0)
            {
                return;
            }

            if (stream.Position + sizeof(uint) > recordsEnd)
            {
                _logger.Warning("appinfo.vdf {Path}: app {AppId} at offset {Offset} has no size field", path, appId, stream.Position);
                return;
            }

            var size = reader.ReadUInt32();
            var recordStart = stream.Position;
            var recordEnd = recordStart + size;

            if (size < RecordHeaderLength || recordEnd > recordsEnd)
            {
                _logger.Warning(
                    "appinfo.vdf {Path}: app {AppId} at offset {Offset} declares an unusable size {Size}; stopping",
                    path, appId, recordStart, size);
                return;
            }

            var lastUpdated = ReadRecordHeader(stream, reader, out var changeNumber);

            if (appId <= int.MaxValue && (wanted is null || wanted.Contains((int)appId)))
            {
                var entry = ReadRecordBody(stream, serializer, options, (int)appId, changeNumber, lastUpdated, (int)(recordEnd - stream.Position), path);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            // Never trust how much the key-values parser consumed: one bad app must not desync the file.
            stream.Position = recordEnd;

            if (wanted is not null && entries.Count >= wanted.Count)
            {
                return;
            }
        }
    }

    /// <summary>Reads the fixed part of a record and returns its <c>lastUpdated</c> stamp.</summary>
    private static uint ReadRecordHeader(Stream stream, BinaryReader reader, out uint changeNumber)
    {
        _ = reader.ReadUInt32();                 // infoState
        var lastUpdated = reader.ReadUInt32();
        _ = reader.ReadUInt64();                 // picsToken
        stream.Position += Sha1Length;           // sha1 of the text vdf
        changeNumber = reader.ReadUInt32();
        stream.Position += Sha1Length;           // sha1 of the binary vdf (v28 and later)
        return lastUpdated;
    }

    /// <summary>
    /// Parses one app's key-values blob. A record that fails to parse is logged and skipped; the
    /// caller carries on with the next one.
    /// </summary>
    private AppInfoEntry? ReadRecordBody(
        Stream stream,
        KVSerializer serializer,
        KVSerializerOptions options,
        int appId,
        uint changeNumber,
        uint lastUpdated,
        int bodyLength,
        string path)
    {
        if (bodyLength <= 0)
        {
            return null;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(bodyLength);
        try
        {
            stream.ReadExactly(buffer, 0, bodyLength);

            using var body = new MemoryStream(buffer, 0, bodyLength, writable: false);
            var document = serializer.Deserialize(body, options);
            return Map(appId, document.Root, changeNumber, lastUpdated);
        }
        catch (Exception ex) when (VdfFile.IsExpected(ex))
        {
            _logger.Warning(ex, "Skipping app {AppId} in {Path}: {Reason}", appId, path, ex.Message);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static AppInfoEntry Map(int appId, KVObject root, uint changeNumber, uint lastUpdated)
    {
        var common = root.Child(CommonKey);
        var (developer, publisher) = ReadAssociations(common);

        return new AppInfoEntry(
            AppId: appId,
            Name: common.GetString("name"),
            Type: common.GetString("type"),
            SortAs: common.GetString("sortas"),
            GenreIds: common.GetOrderedIntList("genres"),
            StoreTagIds: common.GetOrderedIntList("store_tags"),
            CategoryIds: ReadCategoryIds(common),
            LocalizedNames: ReadLocalizedNames(common),
            Developer: developer,
            Publisher: publisher,
            ReleaseDate: common.GetUnixTime("steam_release_date"),
            MetacriticScore: common.GetIntOrNull("metacritic_score"),
            OsList: common.GetString("oslist"),
            ChangeNumber: changeNumber,
            LastUpdated: lastUpdated > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastUpdated) : null);
    }

    /// <summary>
    /// Reads <c>common/category</c>, whose ids live in the key suffix — <c>category_2</c> means
    /// store category 2 — rather than in the value, which is always <c>1</c>.
    /// </summary>
    private static IReadOnlyList<int> ReadCategoryIds(KVObject? common)
    {
        var categories = common.Child("category");
        if (categories is null || !categories.IsCollection)
        {
            return [];
        }

        List<int>? ids = null;
        foreach (var category in categories.Children)
        {
            if (category.Key is null || !category.Key.StartsWith(CategoryKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(category.Key.AsSpan(CategoryKeyPrefix.Length), NumberStyles.Integer, Invariant, out var id))
            {
                (ids ??= new List<int>(categories.Count)).Add(id);
            }
        }

        return ids ?? (IReadOnlyList<int>)[];
    }

    /// <summary>Reads <c>common/name_localized</c>, a language code to display name map.</summary>
    private static IReadOnlyDictionary<string, string> ReadLocalizedNames(KVObject? common)
    {
        var localized = common.Child("name_localized");
        if (localized is null || !localized.IsCollection)
        {
            return NoLocalizedNames;
        }

        Dictionary<string, string>? names = null;
        foreach (var localizedName in localized.Children)
        {
            if (localizedName.Key is null)
            {
                continue;
            }

            var name = localizedName.Value.AsScalarString();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            (names ??= new Dictionary<string, string>(localized.Count, StringComparer.OrdinalIgnoreCase))[localizedName.Key] = name;
        }

        return names ?? NoLocalizedNames;
    }

    /// <summary>
    /// Reads the first developer and publisher out of <c>common/associations</c>, an index-keyed
    /// list of <c>{ type, name }</c> pairs that also carries franchises and other roles.
    /// </summary>
    private static (string? Developer, string? Publisher) ReadAssociations(KVObject? common)
    {
        var associations = common.Child("associations");
        if (associations is null || !associations.IsCollection)
        {
            return (null, null);
        }

        string? developer = null;
        string? publisher = null;

        foreach (var association in associations.Children)
        {
            var entry = association.Value;
            if (entry is null || !entry.IsCollection)
            {
                continue;
            }

            var name = entry.GetString("name");
            var type = entry.GetString("type");
            if (string.IsNullOrWhiteSpace(name) || type is null)
            {
                continue;
            }

            if (developer is null && type.Equals("developer", StringComparison.OrdinalIgnoreCase))
            {
                developer = name;
            }
            else if (publisher is null && type.Equals("publisher", StringComparison.OrdinalIgnoreCase))
            {
                publisher = name;
            }

            if (developer is not null && publisher is not null)
            {
                break;
            }
        }

        return (developer, publisher);
    }

    /// <summary>
    /// Reads the string table the v29 records share for their keys, or <see langword="null"/> when
    /// the offset or the count is not usable.
    /// </summary>
    private StringTable? ReadStringTable(Stream stream, BinaryReader reader, long offset, string path, CancellationToken ct)
    {
        if (offset <= 0 || offset + sizeof(int) > stream.Length)
        {
            _logger.Warning("appinfo.vdf {Path} declares a string table at offset {Offset}, which is outside the file", path, offset);
            return null;
        }

        stream.Position = offset;

        var count = reader.ReadInt32();
        if (count < 0 || count > MaxStringTableEntries)
        {
            _logger.Warning("appinfo.vdf {Path} declares an implausible string table of {Count} entries", path, count);
            return null;
        }

        var strings = new List<string>(count);
        var buffer = ArrayPool<byte>.Shared.Rent(StringBufferSize);
        try
        {
            for (var i = 0; i < count; i++)
            {
                // 14 000 entries on the reference install, so the check is batched rather than
                // taken per string: often enough to abandon promptly, rare enough to be free.
                if ((i & 0x3FF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                strings.Add(ReadNullTerminatedString(stream, ref buffer));
            }
        }
        catch (EndOfStreamException)
        {
            _logger.Warning("appinfo.vdf {Path} has a truncated string table: {Read} of {Count} entries", path, strings.Count, count);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new StringTable(strings);
    }

    /// <summary>Reads one NUL-terminated UTF-8 string, growing the scratch buffer as needed.</summary>
    private static string ReadNullTerminatedString(Stream stream, ref byte[] buffer)
    {
        var length = 0;

        while (true)
        {
            var next = stream.ReadByte();
            if (next <= 0)
            {
                if (next < 0)
                {
                    throw new EndOfStreamException("The string table ends mid-string.");
                }

                return Encoding.UTF8.GetString(buffer, 0, length);
            }

            if (length == buffer.Length)
            {
                var grown = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                Array.Copy(buffer, grown, length);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = grown;
            }

            buffer[length++] = (byte)next;
        }
    }
}
