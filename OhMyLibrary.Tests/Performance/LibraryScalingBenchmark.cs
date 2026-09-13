using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;

using Microsoft.Extensions.Logging.Abstractions;

using OhMyLibrary.App.Services;
using OhMyLibrary.App.ViewModels.Pages;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Data;
using OhMyLibrary.Data.Repositories;
using OhMyLibrary.Tests.Fakes;
using OhMyLibrary.Tests.Infrastructure;

using Xunit.Abstractions;

namespace OhMyLibrary.Tests.Performance;

/// <summary>
/// Opt-in measurement harness. Set OHMYLIBRARY_BENCH=1 to run it.
/// </summary>
public sealed class LibraryScalingBenchmark(ITestOutputHelper output)
{
    private static readonly int[] Sizes = ParseSizes();

    [BenchmarkFact]
    public async Task MeasureStages()
    {
        var table = new StringBuilder();
        table.AppendLine("| N | acf scan | appinfo resolve | repo upsert | GetGamesAsync | VM apply |");
        table.AppendLine("|---|---|---|---|---|---|");

        foreach (var n in Sizes)
        {
            using var harness = new ScalingHarness(n, withArt: true);

            var acfReader = new OhMyLibrary.Core.Vdf.AcfReader();
            IReadOnlyList<InstalledApp> apps = [];
            var acfMs = ScalingHarness.Time(() => apps = acfReader.ReadAll(harness.Folders));

            var appInfoReader = new OhMyLibrary.Core.Vdf.AppInfoReader();
            var wanted = harness.AppIds.ToHashSet();
            IReadOnlyDictionary<int, AppInfoEntry> parsed = new Dictionary<int, AppInfoEntry>();
            var appInfoMs = ScalingHarness.Time(() => parsed = appInfoReader.ReadApps(harness.AppInfoPath, wanted));

            using var factory = harness.NewDatabase();
            var repo = new GameRepository(factory);
            var upsertMs = await ScalingHarness.TimeAsync(async () =>
            {
                await repo.UpsertInstalledAsync(apps);
                _ = await repo.MarkNotInstalledExceptAsync(apps.Select(a => a.AppId).ToHashSet());
                await repo.UpsertMetadataAsync([.. parsed.Values]);
            });

            using var assets = harness.NewAssetResolver();
            var library = harness.NewLibrary(factory, assets);
            IReadOnlyList<GameEntry> games = [];
            var getGamesMs = await ScalingHarness.TimeAsync(async () => games = await library.GetGamesAsync());

            var (vmMs, vmCards) = await MeasureViewModelApplyAsync(harness.BuildEntries());

            table.AppendLine(CultureInfo.InvariantCulture,
                $"| {n} | {acfMs:F0} ms | {appInfoMs:F0} ms | {upsertMs:F0} ms | {getGamesMs:F0} ms | {vmMs:F0} ms |");

            output.WriteLine($"N={n} apps={apps.Count} parsed={parsed.Count} games={games.Count} cards={vmCards}");
            output.WriteLine($"  acf={acfMs:F0} appinfo={appInfoMs:F0} upsert={upsertMs:F0} getgames={getGamesMs:F0} vm={vmMs:F0}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine(table.ToString());
    }

    [BenchmarkFact]
    public async Task MeasureGridRealisation()
    {
        var sta = WpfApplicationFixture.Dispatcher;

        foreach (var n in Sizes)
        {
            var entries = SyntheticEntries(n);

            var realised = 0;
            var layoutMs = 0d;
            var applyMs = 0d;
            long managed = 0;

            await sta.InvokeAsync(() =>
            {

                var vm = NewViewModel(entries, out var libraryFake);
                applyMs = ScalingHarness.Time(() => vm.InitialiseAsync().GetAwaiter().GetResult());

                var before = GC.GetTotalMemory(true);
                var list = BuildGamesList(vm, out var page);
                layoutMs = ScalingHarness.Time(() =>
                {
                    page.Measure(new Size(1600, 900));
                    page.Arrange(new Rect(0, 0, 1600, 900));
                    page.UpdateLayout();
                });

                realised = CountRealisedContainers(list);
                managed = (GC.GetTotalMemory(true) - before) / (1024 * 1024);

                vm.Dispose();
                GC.KeepAlive(libraryFake);
            });

            output.WriteLine($"N={n}: vm apply {applyMs:F0} ms, first layout {layoutMs:F0} ms, containers realised {realised}, +{managed} MB");
        }
    }

    [BenchmarkFact]
    public async Task MeasurePipeline()
    {
        foreach (var n in Sizes)
        {
            using var harness = new ScalingHarness(n, withArt: true);
            using var factory = harness.NewFileDatabase();
            using var assets = harness.NewAssetResolver();
            var library = harness.NewLibrary(factory, assets);

            var scanMs = await ScalingHarness.TimeAsync(() => library.RefreshLocalAsync(true));
            var metaMs = await ScalingHarness.TimeAsync(() => library.RefreshMetadataAsync(true));

            IReadOnlyList<GameEntry> games = [];
            var getMs = await ScalingHarness.TimeAsync(async () => games = await library.GetGamesAsync());
            var rescanMs = await ScalingHarness.TimeAsync(() => library.RefreshLocalAsync(true));
            var getMs2 = await ScalingHarness.TimeAsync(async () => games = await library.GetGamesAsync());

            output.WriteLine(
                $"N={n} rows={games.Count}: scan {scanMs:F0} ms, metadata {metaMs:F0} ms, "
                + $"GetGames(cold) {getMs:F0} ms, rescan {rescanMs:F0} ms, GetGames(warm-db) {getMs2:F0} ms");
        }
    }

    [BenchmarkFact]
    public async Task MeasurePartialUpdate()
    {
        var sta = WpfApplicationFixture.Dispatcher;

        foreach (var n in Sizes)
        {
            var entries = SyntheticEntries(n);
            var ids = entries.Select(e => e.AppId).ToArray();

            var loadMs = 0d;
            var partialMs = 0d;
            var covers = 0;

            await sta.InvokeAsync(() =>
            {
                var vm = NewViewModelWithCountingImages(entries, out var library, out var images);

                loadMs = ScalingHarness.Time(() => vm.InitialiseAsync().GetAwaiter().GetResult());
                var before = images.Requests;

                partialMs = ScalingHarness.Time(() =>
                {
                    library.RaiseChanged(LibraryChangeKind.Metadata, ids);
                    Drain();
                });

                covers = images.Requests - before;
                vm.Dispose();
            });

            output.WriteLine($"N={n}: first load {loadMs:F0} ms, partial update {partialMs:F0} ms, cover requests during partial update {covers}");
        }
    }

    /// <summary>
    /// The whole cold start, as the shipping app runs it: scan, metadata, grid load, and the
    /// library-changed events those raise, with the real image cache behind the cards.
    /// </summary>
    [BenchmarkFact]
    public async Task MeasureEndToEnd()
    {
        var sta = WpfApplicationFixture.Dispatcher;

        foreach (var n in Sizes)
        foreach (var withArt in new[] { true, false })
        {
            using var harness = new ScalingHarness(n, withArt);
            using var factory = harness.NewFileDatabase();
            using var assets = harness.NewAssetResolver();
            var http = new OfflineHttpClientFactory();
            using var realImages = new ImageCacheService(http, NullLogger<ImageCacheService>.Instance);
            var images = new TrackingImageCache(realImages);
            var library = harness.NewLibrary(factory, assets);

            LibraryViewModel? vm = null;
            await sta.InvokeAsync(() => vm = NewRealViewModel(library, assets, images)).ConfigureAwait(false);

            var totalMs = await ScalingHarness.TimeAsync(async () =>
            {
                await sta.InvokeAsync(() => vm!.InitialiseAsync().GetAwaiter().GetResult()).ConfigureAwait(false);
                await library.RefreshLocalAsync(true).ConfigureAwait(false);
                await library.RefreshMetadataAsync(true).ConfigureAwait(false);
                await SettleAsync(sta, images).ConfigureAwait(false);
            });

            var cards = 0;
            await sta.InvokeAsync(() =>
            {
                cards = vm!.Games.Count;
                vm.Dispose();
            }).ConfigureAwait(false);

            output.WriteLine(
                $"N={n} localArt={withArt}: cold start to settled grid {totalMs:F0} ms, cards {cards}, "
                + $"cover requests {images.Requests}, CDN requests {http.Requests}");
        }
    }

    internal static LibraryViewModel NewRealViewModel(
        IGameLibraryService library,
        ILibraryAssetResolver assets,
        IImageCacheService images)
    {
        var tags = new FakeTagService();
        for (var id = 1; id <= 12; id++)
        {
            tags.Genres.Add(new GenreRef(id, $"Genre {id}"));
        }

        return new LibraryViewModel(
            library,
            new NoopInstallStateService(),
            tags,
            new EmptyCollectionService(),
            assets,
            new FakeSteamUriLauncher(),
            images,
            new StaticOptionsMonitor<UiOptions>(new UiOptions()),
            NullLogger<LibraryViewModel>.Instance);
    }

    [BenchmarkFact]
    public async Task MeasureOwnedListArrival()
    {
        var sta = WpfApplicationFixture.Dispatcher;

        foreach (var n in Sizes)
        {
            var installed = SyntheticEntries(86);
            var owned = SyntheticEntries(n);
            var ids = owned.Select(e => e.AppId).ToArray();

            var arrivalMs = 0d;
            var cards = 0;

            await sta.InvokeAsync(() =>
            {
                var vm = NewViewModelWithCountingImages(installed, out var library, out _);
                vm.InitialiseAsync().GetAwaiter().GetResult();

                // A real page, laid out, so the arriving rows pay what they pay in the shell.
                var list = BuildGamesList(vm, out var page);
                page.Measure(new Size(1600, 900));
                page.Arrange(new Rect(0, 0, 1600, 900));
                page.UpdateLayout();

                library.Games.Clear();
                library.Games.AddRange(owned);

                arrivalMs = ScalingHarness.Time(() =>
                {
                    library.RaiseChanged(LibraryChangeKind.Remote, ids);
                    Drain();
                    page.UpdateLayout();
                });

                cards = vm.Games.Count;
                GC.KeepAlive(list);
                vm.Dispose();
            });

            output.WriteLine($"N={n}: owned list of {n} arrives over 86 installed: {arrivalMs:F0} ms, cards {cards}");
        }
    }

    /// <summary>Pumps the dispatcher until no cover decode is outstanding and the queue is empty.</summary>
    private static async Task SettleAsync(StaDispatcher sta, TrackingImageCache images)
    {
        var quiet = 0;
        var seen = -1;

        while (quiet < 3)
        {
            await sta.InvokeAsync(Drain).ConfigureAwait(false);

            if (images.InFlight == 0 && images.Requests == seen)
            {
                quiet++;
            }
            else
            {
                quiet = 0;
            }

            seen = images.Requests;
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private static void Drain()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.SystemIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    internal static List<GameEntry> SyntheticEntries(int count)
    {
        var entries = new List<GameEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var appId = 700000 + i;
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
                Assets: new GameAssets(appId, $@"C:\fake\librarycache\{appId}\library_capsule.jpg", null, null, null, null),
                AppType: "game",
                FriendOwnerIds: [],
                CollectionIds: [],
                LastLocalScanUtc: DateTimeOffset.UtcNow));
        }

        return entries;
    }

    internal static LibraryViewModel NewViewModelWithCountingImages(
        List<GameEntry> entries,
        out FakeGameLibraryService library,
        out CountingImageCache images)
    {
        images = new CountingImageCache();
        return NewViewModel(entries, out library, images);
    }

    internal static LibraryViewModel NewViewModel(List<GameEntry> entries, out FakeGameLibraryService library) =>
        NewViewModel(entries, out library, new CountingImageCache());

    internal static LibraryViewModel NewViewModel(
        List<GameEntry> entries,
        out FakeGameLibraryService library,
        CountingImageCache images)
    {
        library = new FakeGameLibraryService();
        library.Games.AddRange(entries);

        var tags = new FakeTagService();
        for (var id = 1; id <= 12; id++)
        {
            tags.Genres.Add(new GenreRef(id, $"Genre {id}"));
        }

        for (var id = 100; id < 340; id++)
        {
            tags.Tags.Add(new TagRef(id, $"Tag {id}"));
        }

        return new LibraryViewModel(
            library,
            new NoopInstallStateService(),
            tags,
            new EmptyCollectionService(),
            new FakeLibraryAssetResolver(),
            new FakeSteamUriLauncher(),
            images,
            new StaticOptionsMonitor<UiOptions>(new UiOptions()),
            NullLogger<LibraryViewModel>.Instance);
    }

    internal static ListBox BuildGamesList(LibraryViewModel vm, out FrameworkElement page)
    {
        var libraryPage = new OhMyLibrary.App.Views.Pages.LibraryPage(vm);
        page = libraryPage;
        return (ListBox)libraryPage.FindName("GamesList");
    }

    internal static int CountRealisedContainers(ListBox list)
    {
        var count = 0;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not null)
            {
                count++;
            }
        }

        return count;
    }

    private static async Task<(double Ms, int Cards)> MeasureViewModelApplyAsync(List<GameEntry> entries)
    {
        var sta = WpfApplicationFixture.Dispatcher;
        var ms = 0d;
        var cards = 0;

        await sta.InvokeAsync(() =>
        {
            var vm = NewViewModel(entries, out _);
            ms = ScalingHarness.Time(() => vm.InitialiseAsync().GetAwaiter().GetResult());
            cards = vm.Games.Count;
            vm.Dispose();
        });

        return (ms, cards);
    }

    private static int[] ParseSizes()
    {
        var raw = Environment.GetEnvironmentVariable("OHMYLIBRARY_BENCH_SIZES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [100, 1000, 5000, 20000];
        }

        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.Parse(part, CultureInfo.InvariantCulture))];
    }
}

/// <summary>A fact that only runs when OHMYLIBRARY_BENCH names a value.</summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    /// <summary>Environment variable that enables the benchmarks.</summary>
    public const string VariableName = "OHMYLIBRARY_BENCH";

    /// <summary>Creates the attribute, skipping unless the variable is set.</summary>
    public BenchmarkFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VariableName)))
        {
            Skip = $"Set {VariableName}=1 to run the scaling benchmarks.";
        }
    }
}
