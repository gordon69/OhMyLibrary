using System.Text;
using ValveKeyValue;

namespace OhMyLibrary.Tests.Tools;

/// <summary>
/// Writes — and, for inspection only, reads — the <c>appcache/appinfo.vdf</c> container, so the
/// checked-in fixtures can be minted from invented records instead of trimmed out of a live client.
/// </summary>
/// <remarks>
/// <para>
/// The container layout is the one documented in <c>docs/steam-formats.md</c>: a magic and universe
/// header, an <see cref="long"/> string table offset for v29 only, a run of records terminated by an
/// app id of zero, and — for v29 — a string table of NUL-terminated UTF-8 strings at that offset.
/// </para>
/// <para>
/// <see cref="Build"/> builds the string table from the keys of the bodies it is given, so a minted
/// file is self-consistent and a few kilobytes rather than the eight megabytes a client ships.
/// Bodies handed in as raw bytes are written verbatim, which is how a deliberately corrupt record is
/// produced for the resynchronisation tests.
/// </para>
/// <para>
/// <see cref="Read"/> exists to inspect a real container when a format drift is suspected. Nothing
/// it returns may be committed: a live <c>appinfo.vdf</c> carries publisher strings, EULA text and
/// URLs, marketing copy and Valve's CEG signing keys, none of which belong in this repository.
/// </para>
/// </remarks>
public static class AppInfoFixtureBuilder
{
    /// <summary>Container magic of the version the current client writes.</summary>
    public const uint MagicV29 = 0x07564429;

    /// <summary>Container magic of the previous version, which carries no string table.</summary>
    public const uint MagicV28 = 0x07564428;

    /// <summary>Universe the header declares; <c>1</c> is the public universe.</summary>
    public const uint PublicUniverse = 1;

    /// <summary>Root key name every record's key-values blob carries.</summary>
    public const string RootName = "appinfo";

    private const int Sha1Length = 20;

    /// <summary>infoState, lastUpdated, picsToken, both SHA-1s and changeNumber.</summary>
    private const int RecordHeaderLength =
        sizeof(uint) + sizeof(uint) + sizeof(ulong) + Sha1Length + sizeof(uint) + Sha1Length;

    /// <summary>
    /// One record of the container: the header fields plus either a parsed body or the raw bytes
    /// that should be written in its place.
    /// </summary>
    /// <param name="AppId">Steam application id; <c>0</c> is the file terminator and never a record.</param>
    /// <param name="InfoState">Valve's <c>infoState</c> field, carried through unchanged.</param>
    /// <param name="LastUpdated">Record timestamp in unix seconds; <c>0</c> means "never".</param>
    /// <param name="PicsToken">PICS access token, carried through unchanged.</param>
    /// <param name="ChangeNumber">PICS change number from the record header.</param>
    /// <param name="Body">Parsed key-values body, or <see langword="null"/> when <paramref name="RawBody"/> is used.</param>
    /// <param name="RawBody">Body bytes to write verbatim, for deliberately unparseable records.</param>
    public sealed record AppInfoRecord(
        uint AppId,
        uint InfoState,
        uint LastUpdated,
        ulong PicsToken,
        uint ChangeNumber,
        KVObject? Body,
        byte[]? RawBody = null);

    /// <summary>
    /// Walks a container and returns its records, deserialising each body against the file's own
    /// string table. For inspecting a live file only — see the remarks on the class.
    /// </summary>
    /// <param name="path">Absolute path of an <c>appinfo.vdf</c>.</param>
    /// <param name="appIds">Ids to keep, or <see langword="null"/> for every record in the file.</param>
    /// <exception cref="InvalidDataException">The magic is not a supported container version.</exception>
    public static IReadOnlyList<AppInfoRecord> Read(string path, IReadOnlySet<int>? appIds = null)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var magic = reader.ReadUInt32();
        if (magic is not (MagicV29 or MagicV28))
        {
            throw new InvalidDataException($"0x{magic:X8} is not an appinfo.vdf magic.");
        }

        _ = reader.ReadUInt32();

        var recordsEnd = stream.Length;
        StringTable? stringTable = null;

        if (magic == MagicV29)
        {
            var stringTableOffset = reader.ReadInt64();
            var firstRecord = stream.Position;

            stringTable = ReadStringTable(stream, reader, stringTableOffset);
            recordsEnd = stringTableOffset;
            stream.Position = firstRecord;
        }

        var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Binary);
        var options = new KVSerializerOptions { StringTable = stringTable };

        var records = new List<AppInfoRecord>();
        while (stream.Position + sizeof(uint) <= recordsEnd)
        {
            var appId = reader.ReadUInt32();
            if (appId == 0)
            {
                break;
            }

            var size = reader.ReadUInt32();
            var recordStart = stream.Position;

            var infoState = reader.ReadUInt32();
            var lastUpdated = reader.ReadUInt32();
            var picsToken = reader.ReadUInt64();
            stream.Position += Sha1Length;
            var changeNumber = reader.ReadUInt32();
            stream.Position += Sha1Length;

            if (appIds is null || appIds.Contains((int)appId))
            {
                var bodyLength = (int)(recordStart + size - stream.Position);
                var body = reader.ReadBytes(bodyLength);

                using var buffer = new MemoryStream(body, writable: false);
                records.Add(new AppInfoRecord(
                    appId,
                    infoState,
                    lastUpdated,
                    picsToken,
                    changeNumber,
                    serializer.Deserialize(buffer, options).Root));
            }

            stream.Position = recordStart + size;
        }

        return records;
    }

    /// <summary>
    /// Serialises records into a complete container.
    /// </summary>
    /// <param name="magic"><see cref="MagicV29"/> for a file with a string table, <see cref="MagicV28"/> for one without.</param>
    /// <param name="records">The records to write, in order.</param>
    /// <returns>The container bytes, terminator and string table included.</returns>
    public static byte[] Build(uint magic, IReadOnlyList<AppInfoRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var stringTable = magic == MagicV29 ? new StringTable() : null;
        var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Binary);
        var options = new KVSerializerOptions { StringTable = stringTable };

        // The bodies are serialised first because they are what fills the shared string table.
        var bodies = new List<byte[]>(records.Count);
        foreach (var record in records)
        {
            if (record.RawBody is not null)
            {
                bodies.Add(record.RawBody);
                continue;
            }

            if (record.Body is null)
            {
                throw new ArgumentException($"App {record.AppId} has neither a body nor raw bytes.", nameof(records));
            }

            using var buffer = new MemoryStream();
            serializer.Serialize(buffer, record.Body, RootName, options);
            bodies.Add(buffer.ToArray());
        }

        using var file = new MemoryStream();
        using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);

        writer.Write(magic);
        writer.Write(PublicUniverse);

        var offsetPlaceholder = file.Position;
        if (stringTable is not null)
        {
            writer.Write(0L);
        }

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            writer.Write(record.AppId);
            writer.Write((uint)(RecordHeaderLength + bodies[i].Length));
            writer.Write(record.InfoState);
            writer.Write(record.LastUpdated);
            writer.Write(record.PicsToken);
            writer.Write(new byte[Sha1Length]);
            writer.Write(record.ChangeNumber);
            writer.Write(new byte[Sha1Length]);
            writer.Write(bodies[i]);
        }

        writer.Write(0u);

        if (stringTable is not null)
        {
            var tableOffset = file.Position;
            var strings = stringTable.ToArray();

            writer.Write(strings.Length);
            foreach (var value in strings)
            {
                writer.Write(Encoding.UTF8.GetBytes(value));
                writer.Write((byte)0);
            }

            writer.Flush();
            file.Position = offsetPlaceholder;
            writer.Write(tableOffset);
        }

        writer.Flush();
        return file.ToArray();
    }

    /// <summary>
    /// Builds a record body shaped like a real one: a <c>common</c> block with the fields the
    /// launcher reads, plus the <c>associations</c> list developers and publishers come from.
    /// </summary>
    /// <param name="name">Value of <c>common/name</c>.</param>
    /// <param name="type">Value of <c>common/type</c>; casing is deliberately preserved.</param>
    /// <param name="genreIds">Ids for the ordered <c>common/genres</c> list.</param>
    /// <param name="storeTagIds">Ids for the ordered <c>common/store_tags</c> list.</param>
    /// <param name="categoryIds">Ids for <c>common/category</c>, whose keys carry the id as a suffix.</param>
    /// <param name="developer">First <c>developer</c> association.</param>
    /// <param name="publisher">First <c>publisher</c> association.</param>
    /// <param name="releaseDate">Value of <c>common/steam_release_date</c>, in unix seconds.</param>
    /// <param name="metacriticScore">Value of <c>common/metacritic_score</c>.</param>
    /// <param name="sortAs">Value of <c>common/sortas</c>.</param>
    /// <param name="localizedNames">Entries for <c>common/name_localized</c>.</param>
    /// <param name="franchise">
    /// Optional <c>franchise</c> association, written <em>before</em> the developer and publisher so
    /// the reader has to walk past a role it does not want instead of taking entry <c>0</c>.
    /// </param>
    /// <param name="osList">Value of <c>common/oslist</c>, comma separated as Valve writes it.</param>
    public static KVObject SampleBody(
        string name,
        string type,
        IReadOnlyList<int>? genreIds = null,
        IReadOnlyList<int>? storeTagIds = null,
        IReadOnlyList<int>? categoryIds = null,
        string? developer = null,
        string? publisher = null,
        long releaseDate = 0,
        int? metacriticScore = null,
        string? sortAs = null,
        IReadOnlyDictionary<string, string>? localizedNames = null,
        string? franchise = null,
        string osList = "windows")
    {
        var common = KVObject.Collection();
        common.Add("name", name);
        common.Add("type", type);
        common.Add("oslist", osList);

        if (sortAs is not null)
        {
            common.Add("sortas", sortAs);
        }

        if (genreIds is { Count: > 0 })
        {
            common.Add("genres", OrderedList(genreIds));
        }

        if (storeTagIds is { Count: > 0 })
        {
            common.Add("store_tags", OrderedList(storeTagIds));
        }

        if (categoryIds is { Count: > 0 })
        {
            var categories = KVObject.Collection();
            foreach (var categoryId in categoryIds)
            {
                categories.Add($"category_{categoryId}", 1);
            }

            common.Add("category", categories);
        }

        if (localizedNames is { Count: > 0 })
        {
            var localized = KVObject.Collection();
            foreach (var (language, localizedName) in localizedNames)
            {
                localized.Add(language, localizedName);
            }

            common.Add("name_localized", localized);
        }

        if (releaseDate > 0)
        {
            common.Add("steam_release_date", releaseDate);
        }

        if (metacriticScore is { } score)
        {
            common.Add("metacritic_score", score);
        }

        if (developer is not null || publisher is not null || franchise is not null)
        {
            var associations = KVObject.Collection();
            var index = 0;

            // Real records mix franchises and other roles into this list, so a fixture that only
            // ever held a developer and a publisher would let "take the first two" pass as correct.
            if (franchise is not null)
            {
                associations.Add(index++.ToString(), Association("franchise", franchise));
            }

            if (developer is not null)
            {
                associations.Add(index++.ToString(), Association("developer", developer));
            }

            if (publisher is not null)
            {
                associations.Add(index.ToString(), Association("publisher", publisher));
            }

            common.Add("associations", associations);
        }

        var body = KVObject.Collection();
        body.Add("common", common);
        return body;
    }

    private static KVObject Association(string type, string name)
    {
        var association = KVObject.Collection();
        association.Add("type", type);
        association.Add("name", name);
        return association;
    }

    private static KVObject OrderedList(IReadOnlyList<int> ids)
    {
        var list = KVObject.Collection();
        for (var i = 0; i < ids.Count; i++)
        {
            list.Add(i.ToString(), ids[i].ToString());
        }

        return list;
    }

    private static StringTable ReadStringTable(Stream stream, BinaryReader reader, long offset)
    {
        stream.Position = offset;

        var count = reader.ReadInt32();
        var strings = new List<string>(count);
        var buffer = new List<byte>(64);

        for (var i = 0; i < count; i++)
        {
            buffer.Clear();

            int next;
            while ((next = stream.ReadByte()) > 0)
            {
                buffer.Add((byte)next);
            }

            if (next < 0)
            {
                throw new InvalidDataException($"The string table ends after {strings.Count} of {count} entries.");
            }

            strings.Add(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        return new StringTable(strings);
    }
}
