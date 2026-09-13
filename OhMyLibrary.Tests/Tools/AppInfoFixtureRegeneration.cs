using OhMyLibrary.Tests.Infrastructure;
using ValveKeyValue;

namespace OhMyLibrary.Tests.Tools;

/// <summary>
/// Mints the binary <c>appinfo.vdf</c> fixtures. Everything it writes is invented: the app ids, the
/// names, the genre, tag and category ids, the studios and the localised names are all fiction.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here reads a live client any more. A real <c>appcache/appinfo.vdf</c> is around 8 MB and
/// carries publisher and developer strings, EULA titles and store URLs, per-language store names,
/// branch marketing copy, depot and manifest ids and Valve's CEG signing keys — none of which can
/// ship in an MIT repository, however thoroughly the per-account fields are zeroed. The fixtures are
/// therefore synthesised to the *shape* the format notes describe rather than trimmed from real
/// bytes; the live file is only ever read by the opt-in drift check in
/// <c>AppInfoReaderTests</c>, which reads it and commits nothing.
/// </para>
/// <para>
/// This is a tool rather than a test: it is skipped unless
/// <see cref="RequiresFixtureOutputFactAttribute.VariableName"/> names the directory to write into.
/// Regenerate with:
/// <code>
/// set OHMYLIBRARY_FIXTURE_DIR=&lt;repo&gt;\OhMyLibrary.Tests\Fixtures\AppInfo
/// dotnet test --filter FullyQualifiedName~AppInfoFixtureRegeneration
/// </code>
/// </para>
/// </remarks>
public sealed class AppInfoFixtureRegeneration
{
    /// <summary>The apps the synthetic v29 fixture holds. Well above Steam's real id range, on purpose.</summary>
    public static readonly IReadOnlySet<int> V29AppIds = new HashSet<int> { 9_000_101, 9_000_202, 9_000_303, 9_000_404 };

    /// <summary>Name of the synthetic fixture in the format the current client writes.</summary>
    public const string V29FileName = "appinfo_v29.vdf";

    /// <summary>Name of the v29 fixture whose string table has been cut off.</summary>
    public const string V29TruncatedFileName = "appinfo_v29_truncated.vdf";

    /// <summary>Name of the synthetic fixture in the older, string-table-free format.</summary>
    public const string V28FileName = "appinfo_v28.vdf";

    /// <summary>Name of the v28 fixture that stops in the middle of the records.</summary>
    public const string V28TruncatedFileName = "appinfo_v28_truncated.vdf";

    /// <summary>Name of the fixture whose magic is not an appinfo container at all.</summary>
    public const string GarbageFileName = "appinfo_garbage.vdf";

    /// <summary>
    /// Languages the flagship record carries a localised name for. A real record carries a spread
    /// this wide, and every one of these is a key in the string table, which is what makes the
    /// table big enough to be worth resolving rather than a token handful.
    /// </summary>
    private static readonly string[] LocalizedLanguages =
    [
        "english", "german", "french", "italian", "spanish", "latam", "brazilian", "portuguese",
        "polish", "czech", "hungarian", "romanian", "bulgarian", "greek", "turkish", "danish",
        "dutch", "finnish", "norwegian", "swedish", "ukrainian", "vietnamese", "indonesian", "thai",
        "russian", "schinese", "tchinese", "japanese", "koreana",
    ];

    /// <summary>
    /// Invented names for the languages whose script is not Latin, so the fixture exercises
    /// multi-byte UTF-8 in record values as well as ASCII.
    /// </summary>
    private static readonly Dictionary<string, string> NonLatinNames = new(StringComparer.Ordinal)
    {
        ["russian"] = "Фикстур-Квест",
        ["ukrainian"] = "Фікстур-Квест",
        ["bulgarian"] = "Фикстур Куест",
        ["greek"] = "Φίξτσουρ Κουέστ",
        ["thai"] = "เควสต์ฟิกซ์เจอร์",
        ["schinese"] = "固定任务",
        ["tchinese"] = "固定任務",
        ["japanese"] = "フィクスチャクエスト",
        ["koreana"] = "픽스처 퀘스트",
    };

    /// <summary>
    /// Twenty invented store tag ids in a deliberately unsorted order. <c>store_tags</c> is ordered
    /// by relevance and the UI shows the first few, so the order is data the fixture has to carry.
    /// </summary>
    private static readonly int[] FlagshipStoreTags =
    [
        5501, 5417, 5992, 5100, 5883, 5264, 5749, 5038, 5620, 5455,
        5911, 5177, 5326, 5708, 5064, 5842, 5230, 5597, 5483, 5955,
    ];

    [RequiresFixtureOutputFact]
    public void Regenerate()
    {
        var outputDirectory = Environment.GetEnvironmentVariable(RequiresFixtureOutputFactAttribute.VariableName)!;
        Directory.CreateDirectory(outputDirectory);

        var v29Records = SyntheticV29Records();
        Assert.Equal(V29AppIds.Order(), v29Records.Select(record => (int)record.AppId).Order());

        var v29 = AppInfoFixtureBuilder.Build(AppInfoFixtureBuilder.MagicV29, v29Records);
        File.WriteAllBytes(Path.Combine(outputDirectory, V29FileName), v29);

        // Cut the file off exactly where the string table starts, so the offset in the header now
        // points at the end of the file. Without the table no record key resolves, which is the one
        // damage mode that has nothing to salvage.
        var stringTableOffset = BitConverter.ToInt64(v29, sizeof(uint) + sizeof(uint));
        File.WriteAllBytes(
            Path.Combine(outputDirectory, V29TruncatedFileName),
            v29[..checked((int)stringTableOffset)]);

        var v28 = AppInfoFixtureBuilder.Build(AppInfoFixtureBuilder.MagicV28, SyntheticV28Records());
        File.WriteAllBytes(Path.Combine(outputDirectory, V28FileName), v28);

        // Cut inside the records instead, which must still yield everything read up to that point.
        File.WriteAllBytes(
            Path.Combine(outputDirectory, V28TruncatedFileName),
            v28[..(v28.Length - 40)]);

        var garbage = new byte[512];
        Random.Shared.NextBytes(garbage);
        garbage[0] = 0x7B; // '{', so it does not accidentally look like a container magic either.
        File.WriteAllBytes(Path.Combine(outputDirectory, GarbageFileName), garbage);
    }

    /// <summary>
    /// The records of the synthetic v29 fixture: a flagship game with the full <c>common</c> block,
    /// a second game whose <c>type</c> is cased the other way, a tool that must never reach the grid,
    /// and a sparse game with no release date, no score and a zero header timestamp.
    /// </summary>
    public static IReadOnlyList<AppInfoFixtureBuilder.AppInfoRecord> SyntheticV29Records() =>
    [
        Record(
            9_000_101,
            lastUpdated: 1_772_000_000,
            changeNumber: 60_221_408,
            WithSiblingSections(
                AppInfoFixtureBuilder.SampleBody(
                    name: "Fixture Quest: Widgets of Wonder",
                    // Lower case, the way half of Valve's records spell it.
                    type: "game",
                    // Not ascending: the list is file order, and sorting it would be a bug.
                    genreIds: [903, 901, 917],
                    storeTagIds: FlagshipStoreTags,
                    categoryIds: [701, 702, 715, 728, 739, 744],
                    developer: "Fixture Forge Studio",
                    publisher: "Fixture House Publishing",
                    releaseDate: 1_500_000_000,
                    metacriticScore: 73,
                    sortAs: "Quest, Fixture",
                    localizedNames: FlagshipLocalizedNames(),
                    franchise: "Fixture Quest Anthology",
                    osList: "windows,macos,linux"),
                slug: "fixture_quest",
                installDirectory: "Fixture Quest")),
        Record(
            9_000_202,
            lastUpdated: 1_772_000_500,
            changeNumber: 60_221_409,
            WithSiblingSections(
                AppInfoFixtureBuilder.SampleBody(
                    name: "Fixture Tactics: Turnip Wars",
                    // Upper case, the way the other half spell it.
                    type: "Game",
                    genreIds: [902, 918],
                    storeTagIds: [5620, 5101, 5773, 5488],
                    categoryIds: [702, 751, 766],
                    developer: "Fixture Tiny Team",
                    publisher: "Fixture Tiny Team",
                    releaseDate: 1_520_000_000,
                    metacriticScore: 61,
                    localizedNames: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["russian"] = "Фикстур-Тактика",
                        ["schinese"] = "固定战术",
                    },
                    osList: "windows,macos"),
                slug: "fixture_tactics",
                installDirectory: "Fixture Tactics")),
        Record(
            9_000_303,
            lastUpdated: 1_772_001_000,
            changeNumber: 60_221_410,
            WithSiblingSections(
                AppInfoFixtureBuilder.SampleBody(
                    name: "Fixture Runtime Redistributables",
                    type: "Tool"),
                slug: "fixture_runtime",
                installDirectory: "Fixture Runtime")),
        Record(
            9_000_404,
            lastUpdated: 0,
            changeNumber: 60_221_411,
            AppInfoFixtureBuilder.SampleBody(
                name: "Fixture Odyssey: Sample Sector",
                type: "Game",
                genreIds: [917],
                storeTagIds: [5100],
                categoryIds: [703, 762],
                developer: "Fixture Forge Studio",
                publisher: "Fixture House Publishing")),
    ];

    /// <summary>
    /// The records of the synthetic v28 fixture. They deliberately mirror the shapes the format
    /// notes call out: a game with genres, ordered store tags and categories, a DLC, and a tool.
    /// </summary>
    public static IReadOnlyList<AppInfoFixtureBuilder.AppInfoRecord> SyntheticV28Records() =>
    [
        Record(
            770,
            lastUpdated: 1_700_000_000,
            changeNumber: 27_182_818,
            AppInfoFixtureBuilder.SampleBody(
                name: "Fixture Game",
                type: "Game",
                genreIds: [1, 2, 37],
                storeTagIds: [4115, 1695, 6650],
                categoryIds: [2, 22, 28],
                developer: "Fixture Studios",
                publisher: "Fixture Publishing",
                releaseDate: 1_600_000_000,
                metacriticScore: 86,
                sortAs: "Fixture Game, The",
                localizedNames: new Dictionary<string, string>
                {
                    ["russian"] = "Фикстура",
                    ["schinese"] = "固定游戏",
                })),
        Record(
            771,
            lastUpdated: 0,
            changeNumber: 3_141_592,
            AppInfoFixtureBuilder.SampleBody(
                name: "Fixture Game - Season Pass",
                type: "DLC")),
        Record(
            772,
            lastUpdated: 1_700_000_500,
            changeNumber: 1_618_033,
            AppInfoFixtureBuilder.SampleBody(
                name: "Fixture Redistributables",
                type: "Tool")),
    ];

    /// <summary>Invented localised names, one per language in <see cref="LocalizedLanguages"/>.</summary>
    private static IReadOnlyDictionary<string, string> FlagshipLocalizedNames()
    {
        var names = new Dictionary<string, string>(LocalizedLanguages.Length, StringComparer.Ordinal);
        foreach (var language in LocalizedLanguages)
        {
            names[language] = NonLatinNames.TryGetValue(language, out var localized)
                ? localized
                : $"Fixture Quest ({language})";
        }

        return names;
    }

    /// <summary>
    /// Adds the blocks a real record carries around the fields the launcher reads: more nested
    /// collections inside <c>common</c>, and whole sections beside it.
    /// </summary>
    /// <remarks>
    /// Two reasons they are here. Their keys are most of what a real string table is made of, so
    /// without them the fixture's table would be implausibly small; and a <c>common</c> block that
    /// held nothing but the keys the reader wants would let a reader that picked fields loosely —
    /// the first nested collection, a key matched by prefix — pass as correct.
    /// </remarks>
    /// <param name="body">Body returned by <see cref="AppInfoFixtureBuilder.SampleBody"/>.</param>
    /// <param name="slug">Lower-case invented folder-ish name the derived keys are built from.</param>
    /// <param name="installDirectory">Value of <c>config/installdir</c>.</param>
    private static KVObject WithSiblingSections(KVObject body, string slug, string installDirectory)
    {
        var common = body["common"];
        common.Add("library_assets_full", LibraryAssets(slug));
        common.Add("supported_languages", SupportedLanguages());

        var extended = KVObject.Collection();
        extended.Add("gamedir", slug);
        extended.Add("primarycache", $"{slug}_cache");
        extended.Add("order", 1);
        extended.Add("fixture_note", "Synthetic fixture record: every value in this file was invented for the tests.");
        body.Add("extended", extended);

        var launch = KVObject.Collection();
        launch.Add("0", LaunchEntry($"{slug}.exe", "--fixture", "Launch Fixture", "windows"));
        launch.Add("1", LaunchEntry($"{slug}.sh", string.Empty, "Launch Fixture", "linux"));

        var config = KVObject.Collection();
        config.Add("installdir", installDirectory);
        config.Add("contenttype", 3);
        config.Add("launch", launch);
        body.Add("config", config);

        body.Add("ufs", CloudSaves(slug));
        body.Add("localization", RichPresence(slug));

        return body;
    }

    /// <summary>Library art, nested inside <c>common</c> the way the real block is.</summary>
    private static KVObject LibraryAssets(string slug)
    {
        var assets = KVObject.Collection();
        assets.Add("library_capsule", Asset($"{slug}_capsule.jpg"));
        assets.Add("library_hero", Asset($"{slug}_hero.jpg"));
        assets.Add("library_logo", Asset($"{slug}_logo.png"));

        return assets;
    }

    private static KVObject Asset(string fileName)
    {
        var asset = KVObject.Collection();
        asset.Add("image", fileName);
        asset.Add("image2x", $"2x_{fileName}");

        return asset;
    }

    /// <summary>
    /// Per-language support flags. This one sits next to <c>name_localized</c> and is keyed by the
    /// same language codes, so a reader that grabbed the wrong one would read collections where it
    /// expected names.
    /// </summary>
    private static KVObject SupportedLanguages()
    {
        var languages = KVObject.Collection();
        foreach (var language in LocalizedLanguages.Take(6))
        {
            var support = KVObject.Collection();
            support.Add("supported", "true");
            support.Add("full_audio", language == "english" ? "true" : "false");
            support.Add("subtitles", "true");
            languages.Add(language, support);
        }

        return languages;
    }

    private static KVObject CloudSaves(string slug)
    {
        var save = KVObject.Collection();
        save.Add("root", "gameinstall");
        save.Add("path", $"saves/{slug}");
        save.Add("pattern", "*.sav");
        save.Add("recursive", 1);

        var files = KVObject.Collection();
        files.Add("0", save);

        var ufs = KVObject.Collection();
        ufs.Add("quota", 1_048_576);
        ufs.Add("maxnumfiles", 64);
        ufs.Add("savefiles", files);

        return ufs;
    }

    private static KVObject RichPresence(string slug)
    {
        var tokens = KVObject.Collection();
        tokens.Add($"#{slug}_status_menu", "In the fixture menu");
        tokens.Add($"#{slug}_status_playing", "Playing a fixture level");

        var english = KVObject.Collection();
        english.Add("tokens", tokens);

        var richPresence = KVObject.Collection();
        richPresence.Add("english", english);

        var localization = KVObject.Collection();
        localization.Add("richpresence", richPresence);

        return localization;
    }

    private static KVObject LaunchEntry(string executable, string arguments, string description, string osList)
    {
        var entry = KVObject.Collection();
        entry.Add("executable", executable);
        entry.Add("arguments", arguments);
        entry.Add("description", description);
        entry.Add("type", "default");

        var config = KVObject.Collection();
        config.Add("oslist", osList);
        entry.Add("config", config);

        return entry;
    }

    private static AppInfoFixtureBuilder.AppInfoRecord Record(uint appId, uint lastUpdated, uint changeNumber, KVObject body) =>
        new(appId, InfoState: 2, lastUpdated, PicsToken: 0, changeNumber, body);
}
