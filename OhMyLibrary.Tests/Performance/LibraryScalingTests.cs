using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

using OhMyLibrary.App.Views.Pages;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Core.Vdf;
using OhMyLibrary.Data.Repositories;

using Xunit.Abstractions;

namespace OhMyLibrary.Tests.Performance;

/// <summary>
/// The regression net under the scaling work: the local pipeline must stay linear in the size of the
/// library, and neither the grid nor the cards may do work proportional to the whole library for a
/// change that concerns a handful of apps.
/// </summary>
/// <remarks>
/// <para>
/// These build real trees of thousands of manifests and lay a real page out, so they cost seconds
/// rather than milliseconds. They carry <c>Category=Slow</c>; a fast loop is
/// <c>dotnet test --filter "Category!=Slow"</c>.
/// </para>
/// <para>
/// The timing assertions are ratios, never millisecond budgets: what they have to catch is a change
/// of complexity class, and that is the only thing a ratio measures on hardware nobody has seen.
/// Quadratic growth over a fourfold input is 16x; the ceiling below sits well under that and well
/// over the 4x a linear stage costs plus the noise of a shared machine.
/// </para>
/// </remarks>
[Trait("Category", "Slow")]
public sealed class LibraryScalingTests(ITestOutputHelper output)
{
    /// <summary>
    /// Library sizes the scaling assertions compare. The larger is four times the smaller.
    /// </summary>
    /// <remarks>
    /// Sized so that the cheapest stage clears <see cref="MinimumComparableMilliseconds"/> with room
    /// to spare. At 1500 the repository upsert measured ~20 ms — right on the floor, so the run was a
    /// coin toss between a ratio and "too little to compare against". Raising the input is the only
    /// honest fix; lowering the floor would buy a green run by measuring noise.
    /// </remarks>
    private const int SmallLibrary = 3000;

    /// <summary>Four times <see cref="SmallLibrary"/>, which is what makes the ratios readable.</summary>
    private const int LargeLibrary = SmallLibrary * 4;

    /// <summary>
    /// Fourfold input, fourfold work: anything beyond this is a change of complexity class, not a
    /// slow machine. Quadratic would be 16x.
    /// </summary>
    private const double MaxGrowthForFourfoldInput = 10d;

    /// <summary>
    /// Below this a measurement is scheduler noise rather than work, and a ratio taken from it says
    /// nothing either way.
    /// </summary>
    private const double MinimumComparableMilliseconds = 20d;

    /// <summary>
    /// Every local stage has to stay proportional to the number of manifests. The stage that broke
    /// this is the one nobody notices on a machine with 86 installed games.
    /// </summary>
    [Fact]
    public async Task TheLocalPipelineStaysLinearInTheSizeOfTheLibrary()
    {
        // Warm the readers, Dapper's type map and the JIT, so the first measurement is not the one
        // paying for all three.
        _ = await MeasurePipelineAsync(200).ConfigureAwait(true);

        StageTimings small = await MeasurePipelineAsync(SmallLibrary).ConfigureAwait(true);
        StageTimings large = await MeasurePipelineAsync(LargeLibrary).ConfigureAwait(true);

        output.WriteLine($"{SmallLibrary} apps: {small}");
        output.WriteLine($"{LargeLibrary} apps: {large}");

        AssertLinear("acf scan", small.ScanMs, large.ScanMs);
        AssertLinear("repository upsert", small.UpsertMs, large.UpsertMs);
        AssertLinear("metadata refresh", small.MetadataMs, large.MetadataMs);
        AssertLinear("GetGamesAsync", small.GetGamesMs, large.GetGamesMs);
    }

    /// <summary>
    /// The defect this replaces: every refresh asked the image cache for the cover of every card in
    /// the library, on screen or not, so one library event cost one file read, one JPEG decode and,
    /// for an app Steam had cached no art for, one CDN request per owned game.
    /// </summary>
    [Fact]
    public async Task ALibraryChangeDecodesNoCoverForCardsNoContainerHasRealised()
    {
        List<GameEntry> entries = LibraryScalingBenchmark.SyntheticEntries(SmallLibrary);
        var requests = -1;
        var cards = 0;

        await WpfApplicationFixture.Dispatcher.InvokeAsync(() =>
        {
            var viewModel = LibraryScalingBenchmark.NewViewModelWithCountingImages(entries, out var library, out var images);
            viewModel.InitialiseAsync().GetAwaiter().GetResult();

            library.RaiseChanged(LibraryChangeKind.Metadata, [.. entries.Select(entry => entry.AppId)]);
            Pump();

            cards = viewModel.Games.Count;
            requests = images.Requests;
            viewModel.Dispose();
        }).ConfigureAwait(true);

        Assert.Equal(SmallLibrary, cards);
        Assert.Equal(0, requests);
    }

    /// <summary>
    /// The other half of the same rule, so it cannot be satisfied by never decoding anything: a card
    /// a container has realised still re-decodes when Steam rewrites the art behind it.
    /// </summary>
    [Fact]
    public async Task ARealisedCardReDecodesWhenItsArtChanges()
    {
        List<GameEntry> before = LibraryScalingBenchmark.SyntheticEntries(1);
        GameEntry after = MoveArt(before[0]);

        IReadOnlyList<string?> sources = [];

        await WpfApplicationFixture.Dispatcher.InvokeAsync(() =>
        {
            var viewModel = LibraryScalingBenchmark.NewViewModelWithCountingImages(before, out var library, out var images);
            viewModel.InitialiseAsync().GetAwaiter().GetResult();

            // What a realised container does, and the only thing that marks a card as being on screen.
            viewModel.Games[0].EnsureCoverAsync().GetAwaiter().GetResult();

            library.Games[0] = after;
            library.RaiseChanged(LibraryChangeKind.Assets, after.AppId);
            Pump();

            sources = images.Sources;
            viewModel.Dispose();
        }).ConfigureAwait(true);

        Assert.Equal(before[0].Assets!.BestCover, sources[0]);
        Assert.Contains(after.Assets!.BestCover, sources);
    }

    /// <summary>
    /// A card no container realised must not merely be skipped: it has to pick up the <i>new</i> art
    /// when it is finally shown, or skipping the early decode would just be a dropped invalidation.
    /// </summary>
    [Fact]
    public async Task AnUnrealisedCardDecodesTheNewArtWhenItIsFinallyShown()
    {
        List<GameEntry> before = LibraryScalingBenchmark.SyntheticEntries(1);
        GameEntry after = MoveArt(before[0]);

        IReadOnlyList<string?> sources = [];

        await WpfApplicationFixture.Dispatcher.InvokeAsync(() =>
        {
            var viewModel = LibraryScalingBenchmark.NewViewModelWithCountingImages(before, out var library, out var images);
            viewModel.InitialiseAsync().GetAwaiter().GetResult();

            library.Games[0] = after;
            library.RaiseChanged(LibraryChangeKind.Assets, after.AppId);
            Pump();

            viewModel.Games[0].EnsureCoverAsync().GetAwaiter().GetResult();

            sources = images.Sources;
            viewModel.Dispose();
        }).ConfigureAwait(true);

        // The first thing asked for is the art the card carries now. The CDN fallback behind it is
        // only reached because the fake cache answers nothing for the local path.
        Assert.Equal(after.Assets!.BestCover, sources[0]);
        Assert.DoesNotContain(before[0].Assets!.BestCover, sources);
    }

    /// <summary>
    /// The grid must realise a viewport of containers, not a library of them. Every card carries a
    /// cover, a context menu and a dozen bindings, so a panel that stopped virtualising would take
    /// the process down long before it finished its first layout.
    /// </summary>
    [Fact]
    public async Task TheGridRealisesAViewportOfCardsRatherThanTheWholeLibrary()
    {
        var smallRealised = await CountRealisedAsync(SmallLibrary).ConfigureAwait(true);
        var largeRealised = await CountRealisedAsync(LargeLibrary).ConfigureAwait(true);

        output.WriteLine(
            $"realised containers: {SmallLibrary} items -> {smallRealised}, {LargeLibrary} items -> {largeRealised}");

        Assert.InRange(smallRealised, 1, SmallLibrary / 2);

        // Four times the items into the same viewport: what gets realised is a property of the
        // window, not of the library.
        Assert.Equal(smallRealised, largeRealised);
    }

    /// <summary>Moves an entry's art to another path, as Steam does when it re-caches a capsule.</summary>
    private static GameEntry MoveArt(GameEntry entry) => entry with
    {
        Assets = new GameAssets(
            entry.AppId,
            System.IO.Path.Combine(@"C:\fake\librarycache\moved", "library_capsule.jpg"),
            null,
            null,
            null,
            null),
    };

    /// <summary>Runs the dispatcher queue dry, the way an idle application does between frames.</summary>
    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.SystemIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static async Task<int> CountRealisedAsync(int itemCount)
    {
        List<GameEntry> entries = LibraryScalingBenchmark.SyntheticEntries(itemCount);
        var realised = 0;

        await WpfApplicationFixture.Dispatcher.InvokeAsync(() =>
        {
            var viewModel = LibraryScalingBenchmark.NewViewModelWithCountingImages(entries, out _, out _);
            viewModel.InitialiseAsync().GetAwaiter().GetResult();

            var page = new LibraryPage(viewModel);
            page.Measure(new Size(1600, 900));
            page.Arrange(new Rect(0, 0, 1600, 900));
            page.UpdateLayout();

            realised = LibraryScalingBenchmark.CountRealisedContainers((ListBox)page.FindName("GamesList"));
            viewModel.Dispose();
        }).ConfigureAwait(false);

        return realised;
    }

    private static async Task<StageTimings> MeasurePipelineAsync(int appCount)
    {
        using var harness = new ScalingHarness(appCount, withArt: true);
        using var factory = harness.NewDatabase();
        using var assets = harness.NewAssetResolver();

        var repository = new GameRepository(factory);
        var library = harness.NewLibrary(factory, assets);
        var acf = new AcfReader();

        IReadOnlyList<InstalledApp> apps = [];
        var scanMs = Measure(() => apps = acf.ReadAll(harness.Folders));

        var upsertMs = await MeasureAsync(async () =>
        {
            await repository.UpsertInstalledAsync(apps).ConfigureAwait(false);
            _ = await repository
                .MarkNotInstalledExceptAsync(apps.Select(app => app.AppId).ToHashSet())
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

        var metadataMs = await MeasureAsync(() => library.RefreshMetadataAsync(true)).ConfigureAwait(false);
        var getGamesMs = await MeasureAsync(() => library.GetGamesAsync()).ConfigureAwait(false);

        return new StageTimings(scanMs, upsertMs, metadataMs, getGamesMs);
    }

    private static double Measure(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static async Task<double> MeasureAsync(Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        await action().ConfigureAwait(false);
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private void AssertLinear(string stage, double smallMs, double largeMs)
    {
        Assert.True(
            smallMs >= MinimumComparableMilliseconds,
            $"The {stage} stage took {smallMs:F0} ms for {SmallLibrary} apps, too little to compare against; "
            + "raise SmallLibrary rather than lowering the bar.");

        var growth = largeMs / smallMs;
        output.WriteLine($"{stage}: {smallMs:F0} ms -> {largeMs:F0} ms ({growth:F1}x for 4x the input)");

        Assert.True(
            growth <= MaxGrowthForFourfoldInput,
            $"The {stage} stage grew {growth:F1}x for four times the input ({smallMs:F0} ms -> {largeMs:F0} ms); "
            + $"linear is 4x and the ceiling is {MaxGrowthForFourfoldInput:F0}x.");
    }

    /// <summary>What one pass over a library of a given size cost, stage by stage.</summary>
    private readonly record struct StageTimings(double ScanMs, double UpsertMs, double MetadataMs, double GetGamesMs)
    {
        /// <inheritdoc />
        public override string ToString() =>
            $"scan {ScanMs:F0} ms, upsert {UpsertMs:F0} ms, metadata {MetadataMs:F0} ms, GetGamesAsync {GetGamesMs:F0} ms";
    }
}
