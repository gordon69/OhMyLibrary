using System.Diagnostics;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Core.Steam;
using OhMyLibrary.Core.Vdf;
using OhMyLibrary.Data;
using OhMyLibrary.Data.Repositories;
using OhMyLibrary.Tests.Fakes;
using OhMyLibrary.Tests.Infrastructure;
using OhMyLibrary.Tests.Tools;

namespace OhMyLibrary.Tests.Performance;

/// <summary>
/// Builds a throwaway Steam library of an arbitrary size and times the stages that turn it into
/// grid rows. Never touches a real Steam directory: everything lives under the system temp folder.
/// </summary>
public sealed class ScalingHarness : IDisposable
{
    private const int FirstAppId = 700000;

    /// <summary>A real 300x450 JPEG, so the image cache does the decoding work it does in the app.</summary>
    private static readonly Lazy<byte[]> Capsule = new(EncodeCapsule, isThreadSafe: true);

    private readonly TempDirectory _temp;
    private readonly string _steamRoot;
    private readonly string _libraryRoot;

    public ScalingHarness(int appCount, bool withArt)
    {
        AppCount = appCount;
        _temp = new TempDirectory("oml-scale");
        _steamRoot = _temp.CreateSubdirectory("Steam");
        _libraryRoot = _temp.CreateSubdirectory("Library");

        Directory.CreateDirectory(Path.Combine(_steamRoot, "appcache"));
        Directory.CreateDirectory(Path.Combine(_steamRoot, "steamapps"));
        var steamApps = Path.Combine(_libraryRoot, "steamapps");
        Directory.CreateDirectory(steamApps);
        Directory.CreateDirectory(Path.Combine(steamApps, "common"));

        AppIds = [.. Enumerable.Range(0, appCount).Select(i => FirstAppId + i)];

        foreach (var appId in AppIds)
        {
            File.WriteAllText(
                Path.Combine(steamApps, $"appmanifest_{appId}.acf"),
                Manifest(appId),
                Encoding.UTF8);
            Directory.CreateDirectory(Path.Combine(steamApps, "common", $"Game {appId}"));
        }

        if (withArt)
        {
            var cache = Path.Combine(_steamRoot, "appcache", "librarycache");
            foreach (var appId in AppIds)
            {
                var hashDir = Path.Combine(cache, appId.ToString(CultureInfo.InvariantCulture), "6843027380c3bfd0952449fd9174f492ef2e7b40");
                Directory.CreateDirectory(hashDir);
                File.WriteAllBytes(Path.Combine(hashDir, "library_capsule.jpg"), Capsule.Value);
            }
        }

        File.WriteAllBytes(AppInfoPath, BuildAppInfo(AppIds));

        Paths = new FakeSteamPathResolver { SteamPath = _steamRoot };
        Paths.LibraryFolders.Add(new SteamLibraryFolder(_libraryRoot, "test", 0, new Dictionary<int, long>()));

        Folders = Paths.LibraryFolders;
    }

    public int AppCount { get; }

    public IReadOnlyList<int> AppIds { get; }

    public FakeSteamPathResolver Paths { get; }

    public IReadOnlyList<SteamLibraryFolder> Folders { get; }

    public string AppInfoPath => Path.Combine(_steamRoot, "appcache", "appinfo.vdf");

    public SqliteConnectionFactory NewDatabase() => new(SqliteConnectionFactory.InMemoryPath);

    /// <summary>A file-backed database inside the throwaway tree, as the shipping app uses.</summary>
    public SqliteConnectionFactory NewFileDatabase() => new(_temp.Combine("library.db"));

    /// <summary>A real <see cref="GameLibraryService"/> over the given database and this tree.</summary>
    public GameLibraryService NewLibrary(SqliteConnectionFactory factory, ILibraryAssetResolver assets)
    {
        var games = new GameRepository(factory);
        var syncMeta = new SyncMetaRepository(factory);

        return new GameLibraryService(
            Paths,
            new FakeLibraryFoldersReader(),
            new AcfReader(),
            new AppInfoReader(),
            assets,
            new FakeSteamWebApiClient(),
            new FakeTagService(),
            games,
            syncMeta,
            Options.Create(new SteamOptions()),
            Options.Create(new SyncOptions()),
            NullLogger<GameLibraryService>.Instance);
    }

    public LibraryAssetResolver NewAssetResolver() =>
        new(Paths, new FakeSteamWatcherService(), NullLogger<LibraryAssetResolver>.Instance);

    /// <summary>Synthetic grid rows, as Core would hand them to the view model.</summary>
    public List<GameEntry> BuildEntries()
    {
        var entries = new List<GameEntry>(AppCount);
        for (var i = 0; i < AppCount; i++)
        {
            var appId = AppIds[i];
            entries.Add(new GameEntry(
                AppId: appId,
                Name: $"Game {appId}",
                IsOwned: true,
                IsInstalled: true,
                StateFlags: AppStateFlags.FullyInstalled,
                InstallDir: $"Game {appId}",
                FullInstallPath: null,
                SizeBytes: 1000L * i,
                BuildId: "1",
                PlaytimeForeverMinutes: i,
                LastPlayed: null,
                Genres: [new GenreRef(1 + (i % 12), "Genre")],
                Tags: [new TagRef(100 + (i % 80), "Tag"), new TagRef(200 + (i % 40), "Tag")],
                Assets: GameAssets.None(appId),
                AppType: "game",
                FriendOwnerIds: [],
                CollectionIds: [],
                LastLocalScanUtc: DateTimeOffset.UtcNow));
        }

        return entries;
    }

    public void Dispose() => _temp.Dispose();

    public static double Time(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    public static async Task<double> TimeAsync(Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        await action().ConfigureAwait(false);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static byte[] EncodeCapsule()
    {
        const int width = 300;
        const int height = 450;
        var stride = width * 3;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 251);
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null, pixels, stride);

        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    private static byte[] BuildAppInfo(IReadOnlyList<int> appIds)
    {
        var records = new List<AppInfoFixtureBuilder.AppInfoRecord>(appIds.Count);
        for (var i = 0; i < appIds.Count; i++)
        {
            var body = AppInfoFixtureBuilder.SampleBody(
                $"Game {appIds[i]}",
                "game",
                genreIds: [1 + (i % 12)],
                storeTagIds: [100 + (i % 80), 200 + (i % 40), 300 + (i % 20)],
                categoryIds: [2, 22],
                developer: "Dev",
                publisher: "Pub");

            records.Add(new AppInfoFixtureBuilder.AppInfoRecord(
                (uint)appIds[i], 0u, 1700000000u, 0ul, (uint)i, body));
        }

        return AppInfoFixtureBuilder.Build(AppInfoFixtureBuilder.MagicV29, records);
    }

    private static string Manifest(int appId) => $$"""
        "AppState"
        {
        	"appid"		"{{appId}}"
        	"universe"		"1"
        	"name"		"Game {{appId}}"
        	"StateFlags"		"4"
        	"installdir"		"Game {{appId}}"
        	"lastupdated"		"1780776645"
        	"LastPlayed"		"1786388287"
        	"SizeOnDisk"		"91231172278"
        	"StagingSize"		"0"
        	"buildid"		"20383525"
        	"LastOwner"		"76561198000000001"
        	"BytesToDownload"		"42064"
        	"BytesDownloaded"		"42064"
        	"BytesToStage"		"45820"
        	"BytesStaged"		"45820"
        	"TargetBuildID"		"0"
        	"InstalledDepots"
        	{
        		"{{appId + 1}}"
        		{
        			"manifest"		"3389237510439361019"
        			"size"		"66366228927"
        		}
        	}
        	"UserConfig"
        	{
        		"language"		"english"
        	}
        }
        """;
}
