using OhMyLibrary.Core.Vdf;
using OhMyLibrary.Tests.Infrastructure;
using OhMyLibrary.Tests.Tools;

namespace OhMyLibrary.Tests.Vdf;

/// <summary>
/// <see cref="AppInfoReader"/> against the checked-in containers: a synthetic v29 file with a string
/// table, a synthetic v28 file without one, and the damaged variants of both.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures are invented end to end — app ids, names, genre, tag and category ids, studios and
/// localised names — because a real <c>appinfo.vdf</c> carries publisher strings, EULA titles and
/// URLs, marketing copy and Valve's CEG signing keys, which cannot ship here. They are minted by
/// <see cref="AppInfoFixtureRegeneration"/> to the shape recorded in <c>docs/steam-formats.md</c>, so
/// the expectations below are the shape of real data rather than real data.
/// </para>
/// <para>
/// What the fixtures therefore cannot prove is that the *format* still looks like this. That is what
/// <see cref="ReadAll_StillParsesTheLiveContainerOnThisMachine"/> is for: it reads this machine's own
/// client when there is one, and commits nothing.
/// </para>
/// </remarks>
public sealed class AppInfoReaderTests
{
    private readonly AppInfoReader _reader = new();

    [Fact]
    public void ReadApps_ObservesCancellationBetweenRecords()
    {
        // The parse is a multi-second pass over several megabytes, so the token has to be checked
        // per record: a caller that only wrapped the call in Task.Run stopped it from starting and
        // nothing more. The filter set is consulted once per record, so cancelling from inside it
        // puts the cancellation exactly where a real one lands — mid-walk, with records still to go.
        using var cts = new CancellationTokenSource();
        var wanted = new CancellingSet(cts, [9_000_101, 9_000_202, 9_000_303, 9_000_404]);

        _ = Assert.ThrowsAny<OperationCanceledException>(() => _reader.ReadApps(V29, wanted, cts.Token));

        // Cancelled after the first record was considered, not before the walk began.
        Assert.Equal(1, wanted.ContainsCalls);
    }

    [Fact]
    public void ReadAll_ThrowsRatherThanReturningTheRecordsReadSoFar()
    {
        // An abandoned parse must not be mistaken for a container that holds nothing: a caller that
        // wrote that answer through would wipe every genre and tag it had.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _ = Assert.ThrowsAny<OperationCanceledException>(() => _reader.ReadAll(V29, cts.Token));
    }

    private static string V29 => Fixture.Resolve("AppInfo", "appinfo_v29.vdf");

    private static string V28 => Fixture.Resolve("AppInfo", "appinfo_v28.vdf");

    [Fact]
    public void ReadAll_ParsesEveryRecordOfAV29Container()
    {
        var entries = _reader.ReadAll(V29);

        Assert.Equal([9_000_101, 9_000_202, 9_000_303, 9_000_404], entries.Select(entry => entry.AppId).Order().ToArray());
    }

    [Fact]
    public void ReadAll_MapsTheWholeCommonBlockOfAGame()
    {
        var game = Assert.Single(_reader.ReadAll(V29), entry => entry.AppId == 9_000_101);

        Assert.Equal("Fixture Quest: Widgets of Wonder", game.Name);
        Assert.Equal("game", game.Type);
        Assert.True(game.IsGame);
        Assert.Equal("Quest, Fixture", game.SortAs);
        // File order, not sorted order: 901 comes second in the record and has to come second here.
        Assert.Equal([903, 901, 917], game.GenreIds.ToArray());
        // The record's first association is a franchise, so these two are not simply entries 0 and 1.
        Assert.Equal("Fixture Forge Studio", game.Developer);
        Assert.Equal("Fixture House Publishing", game.Publisher);
        Assert.Equal(73, game.MetacriticScore);
        Assert.Equal("windows,macos,linux", game.OsList);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_500_000_000), game.ReleaseDate);
        Assert.Equal(60_221_408u, game.ChangeNumber);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_772_000_000), game.LastUpdated);
    }

    [Fact]
    public void ReadAll_ResolvesLocalizedNamesThroughTheStringTable()
    {
        var game = Assert.Single(_reader.ReadAll(V29), entry => entry.AppId == 9_000_101);

        // Every language here is a key, so this is also the proof that the string table resolved.
        Assert.Equal(29, game.LocalizedNames.Count);
        Assert.Equal("Фикстур-Квест", game.LocalizedNames["russian"]);
        Assert.Equal("固定任务", game.LocalizedNames["schinese"]);
        Assert.Equal("Fixture Quest (german)", game.LocalizedNames["german"]);
    }

    [Fact]
    public void ReadAll_KeepsTheStoreTagOrderTheRecordDeclares()
    {
        var game = Assert.Single(_reader.ReadAll(V29), entry => entry.AppId == 9_000_101);

        // store_tags is ordered by relevance, and the UI shows the first few, so the order is data.
        // The ids are deliberately unsorted: a reader that sorted them would fail here.
        Assert.Equal([5501, 5417, 5992], game.StoreTagIds.Take(3).ToArray());
        Assert.Equal(20, game.StoreTagIds.Count);
    }

    [Fact]
    public void ReadAll_ReadsCategoryIdsOutOfTheKeySuffix()
    {
        var game = Assert.Single(_reader.ReadAll(V29), entry => entry.AppId == 9_000_101);

        // The id lives in the key — "category_715" is store category 715 — and the value is always
        // 1, so neither that 1 nor the child's index may be mistaken for an id.
        Assert.Equal([701, 702, 715, 728, 739, 744], game.CategoryIds.ToArray());
        Assert.DoesNotContain(1, game.CategoryIds);
        Assert.DoesNotContain(0, game.CategoryIds);
    }

    [Fact]
    public void ReadAll_ComparesTypeCaseInsensitivelyBecauseValveIsInconsistent()
    {
        var entries = _reader.ReadAll(V29);

        var lowerCase = Assert.Single(entries, entry => entry.AppId == 9_000_101);
        var upperCase = Assert.Single(entries, entry => entry.AppId == 9_000_202);
        var redistributable = Assert.Single(entries, entry => entry.AppId == 9_000_303);

        Assert.Equal("game", lowerCase.Type);
        Assert.Equal("Game", upperCase.Type);
        Assert.True(lowerCase.IsGame);
        Assert.True(upperCase.IsGame);

        // A redistributable is a Tool, like the real 228980, and must never reach the grid.
        Assert.Equal("Tool", redistributable.Type);
        Assert.False(redistributable.IsGame);
    }

    [Fact]
    public void ReadAll_LeavesTheOptionalCommonFieldsNullWhenAV29RecordOmitsThem()
    {
        var sparse = Assert.Single(_reader.ReadAll(V29), entry => entry.AppId == 9_000_404);

        Assert.Equal("Fixture Odyssey: Sample Sector", sparse.Name);
        Assert.Null(sparse.ReleaseDate);
        Assert.Null(sparse.MetacriticScore);
        Assert.Null(sparse.SortAs);
        Assert.Null(sparse.LastUpdated);
        Assert.Empty(sparse.LocalizedNames);
        Assert.Equal([917], sparse.GenreIds.ToArray());
    }

    [Fact]
    public void ReadAll_ParsesAV28ContainerThatHasNoStringTable()
    {
        var entries = _reader.ReadAll(V28);

        Assert.Equal(3, entries.Count);

        var game = Assert.Single(entries, entry => entry.AppId == 770);
        Assert.Equal("Fixture Game", game.Name);
        Assert.Equal("Fixture Game, The", game.SortAs);
        Assert.Equal([1, 2, 37], game.GenreIds.ToArray());
        Assert.Equal([4115, 1695, 6650], game.StoreTagIds.ToArray());
        Assert.Equal([2, 22, 28], game.CategoryIds.ToArray());
        Assert.Equal("Fixture Studios", game.Developer);
        Assert.Equal("Fixture Publishing", game.Publisher);
        Assert.Equal(86, game.MetacriticScore);
        Assert.Equal("Фикстура", game.LocalizedNames["russian"]);
        Assert.Equal("固定游戏", game.LocalizedNames["schinese"]);
    }

    [Fact]
    public void ReadAll_TreatsAZeroHeaderTimestampAsNever()
    {
        var dlc = Assert.Single(_reader.ReadAll(V28), entry => entry.AppId == 771);

        Assert.Null(dlc.LastUpdated);
        Assert.Equal(3_141_592u, dlc.ChangeNumber);
        Assert.Null(dlc.ReleaseDate);
        Assert.Null(dlc.MetacriticScore);
        Assert.Empty(dlc.GenreIds);
        Assert.Empty(dlc.LocalizedNames);
    }

    [Fact]
    public void ReadApps_ReturnsOnlyTheRequestedApps()
    {
        var entries = _reader.ReadApps(V29, new HashSet<int> { 9_000_101, 9_000_303 });

        Assert.Equal([9_000_101, 9_000_303], entries.Keys.Order().ToArray());
        Assert.Equal("Fixture Quest: Widgets of Wonder", entries[9_000_101].Name);
    }

    [Fact]
    public void ReadApps_SilentlySkipsIdsTheFileDoesNotHave()
    {
        var entries = _reader.ReadApps(V29, new HashSet<int> { 9_000_101, 4_242_424 });

        Assert.True(entries.ContainsKey(9_000_101));
        Assert.False(entries.ContainsKey(4_242_424));
    }

    [Fact]
    public void ReadApps_ReturnsNothingForAnEmptyIdSet()
    {
        Assert.Empty(_reader.ReadApps(V29, new HashSet<int>()));
    }

    [Fact]
    public void ReadAll_ReturnsNothingWhenAV29StringTableIsUnreachable()
    {
        // Without the table no record key resolves, so a partial parse would be nonsense.
        Assert.Empty(_reader.ReadAll(Fixture.Resolve("AppInfo", "appinfo_v29_truncated.vdf")));
    }

    [Fact]
    public void ReadAll_KeepsWhatItReadWhenTheRecordsAreTruncated()
    {
        var entries = _reader.ReadAll(Fixture.Resolve("AppInfo", "appinfo_v28_truncated.vdf"));

        Assert.Equal(2, entries.Count);
        Assert.Equal([770, 771], entries.Select(entry => entry.AppId).Order().ToArray());
    }

    [Fact]
    public void ReadAll_ReturnsNothingForAFileThatIsNotAContainer()
    {
        Assert.Empty(_reader.ReadAll(Fixture.Resolve("AppInfo", "appinfo_garbage.vdf")));
    }

    [Fact]
    public void ReadAll_ReturnsNothingForAMissingFile()
    {
        Assert.Empty(_reader.ReadAll(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.vdf")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadAll_ReturnsNothingForAnEmptyPath(string path)
    {
        Assert.Empty(_reader.ReadAll(path));
    }

    [Theory]
    [InlineData(AppInfoFixtureBuilder.MagicV29)]
    [InlineData(AppInfoFixtureBuilder.MagicV28)]
    public void ReadAll_ResynchronisesAfterARecordWhoseBodyIsCorrupt(uint magic)
    {
        // The size field of a record is trusted over whatever the key-values parser consumed, which
        // is what stops one damaged app from desyncing the thousands behind it.
        using var temp = new TempDirectory("appinfo");
        var file = temp.WriteFile(
            "appinfo.vdf",
            AppInfoFixtureBuilder.Build(magic, [
                Record(1, AppInfoFixtureBuilder.SampleBody("First", "Game")),
                Corrupt(2),
                Record(3, AppInfoFixtureBuilder.SampleBody("Third", "Game")),
            ]));

        var entries = _reader.ReadAll(file);

        Assert.Equal([1, 3], entries.Select(entry => entry.AppId).Order().ToArray());
        Assert.Equal("Third", Assert.Single(entries, entry => entry.AppId == 3).Name);
    }

    [Fact]
    public void ReadAll_StopsAtTheTerminatingAppId()
    {
        using var temp = new TempDirectory("appinfo");
        var container = AppInfoFixtureBuilder.Build(
            AppInfoFixtureBuilder.MagicV28,
            [Record(1, AppInfoFixtureBuilder.SampleBody("Only", "Game"))]);

        // Anything after the terminator is not a record and must not be parsed as one.
        var file = temp.WriteFile("appinfo.vdf", [.. container, .. new byte[64]]);

        var entry = Assert.Single(_reader.ReadAll(file));
        Assert.Equal(1, entry.AppId);
    }

    [RequiresSteamFact]
    public void ReadAll_StillParsesTheLiveContainerOnThisMachine()
    {
        // The fixtures are invented, so they can only prove the reader still reads *this* shape.
        // This is the test that proves the shape is still Valve's: it reads the client's own
        // appinfo.vdf — around 8 MB, a few thousand records, a string table with five figures of
        // entries — and asserts nothing about its contents beyond what the format guarantees.
        // Nothing it touches is written anywhere, which is why real data is allowed to live here.
        var path = SteamInstall.AppInfoPath!;
        var live = _reader.ReadAll(path);

        // A client knows thousands of apps. A handful would mean the container walk desynced and
        // gave up early, which is exactly the failure a new container version would cause.
        Assert.True(live.Count > 100, $"Only {live.Count} record(s) parsed out of {path}.");

        // Names come out of key-values whose keys are string table indices, so a table that no
        // longer resolves shows up here as a file full of nameless records.
        var named = live.Count(entry => !string.IsNullOrWhiteSpace(entry.Name));
        Assert.True(named > live.Count / 2, $"Only {named} of {live.Count} live records had a name.");

        // One live app per mapped field, so a field Valve moved or renamed cannot pass unnoticed.
        Assert.Contains(live, entry => entry.IsGame);
        Assert.Contains(live, entry => entry.GenreIds.Count > 0);
        Assert.Contains(live, entry => entry.StoreTagIds.Count > 0);
        Assert.Contains(live, entry => entry.CategoryIds.Count > 0 && !entry.CategoryIds.Contains(0));
        Assert.Contains(live, entry => entry.LocalizedNames.Count > 0);
        Assert.Contains(live, entry => entry.Developer is not null && entry.Publisher is not null);
        Assert.Contains(live, entry => entry.ReleaseDate is not null);
        Assert.Contains(live, entry => entry.MetacriticScore is > 0);
        Assert.Contains(live, entry => entry.OsList is not null);

        // The filtered walk skips over the bodies it does not want, so it is a different code path
        // from the full one above: on real bytes the two have to agree field for field.
        var expected = live.Where(entry => entry.GenreIds.Count > 0 && entry.CategoryIds.Count > 0)
            .Take(5)
            .ToDictionary(entry => entry.AppId);
        var filtered = _reader.ReadApps(path, expected.Keys.ToHashSet());

        Assert.Equal(expected.Count, filtered.Count);
        foreach (var (appId, one) in expected)
        {
            var other = filtered[appId];
            Assert.Equal(one.Name, other.Name);
            Assert.Equal(one.Type, other.Type);
            Assert.Equal(one.GenreIds, other.GenreIds);
            Assert.Equal(one.StoreTagIds, other.StoreTagIds);
            Assert.Equal(one.CategoryIds, other.CategoryIds);
            Assert.Equal(one.Developer, other.Developer);
            Assert.Equal(one.Publisher, other.Publisher);
            Assert.Equal(one.ChangeNumber, other.ChangeNumber);
        }

        // The fixture builder writes the committed files, so its own container walk has to keep
        // agreeing with the real format too — otherwise the fixtures drift without anything saying so.
        var viaBuilder = AppInfoFixtureBuilder.Read(path, expected.Keys.ToHashSet());
        Assert.Equal(expected.Keys.Order(), viaBuilder.Select(record => (int)record.AppId).Order());
    }

    private static AppInfoFixtureBuilder.AppInfoRecord Record(uint appId, ValveKeyValue.KVObject body) =>
        new(appId, InfoState: 2, LastUpdated: 1_700_000_000, PicsToken: 0, ChangeNumber: 42, body);

    private static AppInfoFixtureBuilder.AppInfoRecord Corrupt(uint appId) =>
        new(appId, InfoState: 2, LastUpdated: 0, PicsToken: 0, ChangeNumber: 0, Body: null, RawBody: [0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE]);

    /// <summary>
    /// The requested-id set, which cancels the walk the first time the reader consults it.
    /// </summary>
    /// <remarks>
    /// The reader asks this once per record, so it is the one place a test can stand in the middle
    /// of the walk without a timer: cancel on the first question and the answer to the second must
    /// never be asked for.
    /// </remarks>
    private sealed class CancellingSet(CancellationTokenSource cts, HashSet<int> inner) : IReadOnlySet<int>
    {
        public int ContainsCalls { get; private set; }

        public int Count => inner.Count;

        public bool Contains(int item)
        {
            ContainsCalls++;
            cts.Cancel();
            return inner.Contains(item);
        }

        public IEnumerator<int> GetEnumerator() => inner.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public bool IsProperSubsetOf(IEnumerable<int> other) => inner.IsProperSubsetOf(other);

        public bool IsProperSupersetOf(IEnumerable<int> other) => inner.IsProperSupersetOf(other);

        public bool IsSubsetOf(IEnumerable<int> other) => inner.IsSubsetOf(other);

        public bool IsSupersetOf(IEnumerable<int> other) => inner.IsSupersetOf(other);

        public bool Overlaps(IEnumerable<int> other) => inner.Overlaps(other);

        public bool SetEquals(IEnumerable<int> other) => inner.SetEquals(other);
    }
}
