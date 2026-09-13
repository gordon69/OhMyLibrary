using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Data.Repositories;

namespace OhMyLibrary.Tests.Data;

/// <summary>
/// <see cref="GameRepository"/> against a real SQLite database.
/// </summary>
/// <remarks>
/// The three upserts write disjoint sets of columns on purpose — install state, ownership and
/// playtime, metadata — and each of them runs at a different cadence, so the interesting failure is
/// one source quietly clearing another's columns. That is what most of this class is about.
/// </remarks>
public sealed class GameRepositoryTests : SqliteFixtureBase
{
    private static readonly DateTimeOffset Older = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2026, 6, 7, 8, 9, 10, TimeSpan.Zero);

    [Fact]
    public async Task UpsertInstalled_WritesTheInstallColumns()
    {
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2")]);

        var entry = await Games.GetAsync(570);

        Assert.NotNull(entry);
        Assert.Equal("Dota 2", entry.Name);
        Assert.True(entry.IsInstalled);
        Assert.False(entry.IsOwned);
        Assert.Equal("dota 2 beta", entry.InstallDir);
        Assert.Equal(@"C:\Library\steamapps\common\dota 2 beta", entry.FullInstallPath);
        Assert.Equal(77_246_578_977L, entry.SizeBytes);
        Assert.Equal(AppStateFlags.FullyInstalled | AppStateFlags.UpdateRequired, entry.StateFlags);
        Assert.True(entry.NeedsUpdate);
        Assert.Equal("25219194", entry.BuildId);
        Assert.NotNull(entry.LastLocalScanUtc);
    }

    [Fact]
    public async Task UpsertOwned_DoesNotClearWhatTheInstallScanWrote()
    {
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2")]);
        await Games.UpsertOwnedAsync([new OwnedGame(570, "Dota 2", 1234, 56, Older, "icon")]);

        var entry = await Games.GetAsync(570);

        Assert.NotNull(entry);
        Assert.True(entry.IsInstalled);
        Assert.True(entry.IsOwned);
        Assert.Equal(1234, entry.PlaytimeForeverMinutes);
        Assert.Equal("dota 2 beta", entry.InstallDir);
        Assert.Equal(77_246_578_977L, entry.SizeBytes);
        Assert.Equal(AppStateFlags.FullyInstalled | AppStateFlags.UpdateRequired, entry.StateFlags);
    }

    [Fact]
    public async Task UpsertInstalled_DoesNotClearWhatTheOwnedRefreshWrote()
    {
        await Games.UpsertOwnedAsync([new OwnedGame(570, "Dota 2", 1234, 56, Older, "icon")]);
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2")]);

        var entry = await Games.GetAsync(570);

        Assert.NotNull(entry);
        Assert.True(entry.IsOwned);
        Assert.Equal(1234, entry.PlaytimeForeverMinutes);
        Assert.Equal(56, await ScalarAsync<int>("SELECT playtime_2weeks FROM Games WHERE app_id = 570"));
    }

    [Fact]
    public async Task UpsertMetadata_TouchesOnlyTypeSortNameAndTheLinks()
    {
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2")]);
        await Games.UpsertOwnedAsync([new OwnedGame(570, "Dota 2", 1234, 56, Older, "icon")]);

        await Games.UpsertMetadataAsync([Metadata(570, "Dota 2 (appinfo)", "game", [1, 2, 37], [113, 1718])]);

        var entry = await Games.GetAsync(570);

        Assert.NotNull(entry);
        Assert.Equal("game", entry.AppType);
        Assert.Equal([1, 2, 37], entry.Genres.Select(genre => genre.GenreId).ToArray());
        Assert.Equal([113, 1718], entry.Tags.Select(tag => tag.TagId).ToArray());

        // Everything the other two sources own survives, and the metadata pass does not rename the row.
        Assert.Equal("Dota 2", entry.Name);
        Assert.True(entry.IsInstalled);
        Assert.True(entry.IsOwned);
        Assert.Equal(1234, entry.PlaytimeForeverMinutes);
        Assert.Equal(77_246_578_977L, entry.SizeBytes);
        Assert.Equal("Dota 2 (appinfo)", await ScalarAsync<string>("SELECT sort_name FROM Games WHERE app_id = 570"));
    }

    [Fact]
    public async Task UpsertMetadata_ReplacesTheLinksItPreviouslyWrote()
    {
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2")]);
        await Games.UpsertMetadataAsync([Metadata(570, "Dota 2", "game", [1, 2, 37], [113, 1718, 3859])]);

        await Games.UpsertMetadataAsync([Metadata(570, "Dota 2", "game", [1], [3859])]);

        var entry = await Games.GetAsync(570);

        Assert.NotNull(entry);
        Assert.Equal([1], entry.Genres.Select(genre => genre.GenreId).ToArray());
        Assert.Equal([3859], entry.Tags.Select(tag => tag.TagId).ToArray());
    }

    [Fact]
    public async Task UpsertMetadata_IgnoresAppsThatHaveNoRow()
    {
        // appinfo.vdf describes thousands of apps the user does not own; none of them may create rows.
        await Games.UpsertMetadataAsync([Metadata(999_999, "Some Other App", "game", [1], [2])]);

        Assert.Empty(await Games.GetAllAsync());
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM GameGenres"));
    }

    [Fact]
    public async Task BlankNamesNeverOverwriteAGoodOne()
    {
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2")]);

        await Games.UpsertOwnedAsync([new OwnedGame(570, null, 10, 0, null, null)]);
        await Games.UpsertInstalledAsync([Installed(570, string.Empty)]);

        var entry = await Games.GetAsync(570);

        Assert.NotNull(entry);
        Assert.Equal("Dota 2", entry.Name);
    }

    [Fact]
    public async Task ARenameStillPropagates()
    {
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2")]);
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2 Renamed")]);

        var entry = await Games.GetAsync(570);

        Assert.NotNull(entry);
        Assert.Equal("Dota 2 Renamed", entry.Name);
    }

    [Fact]
    public async Task LastPlayedKeepsTheNewerOfTheTwoSources()
    {
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2") with { LastPlayed = Newer }]);
        await Games.UpsertOwnedAsync([new OwnedGame(570, "Dota 2", 1, 0, Older, null)]);

        var afterOlder = await Games.GetAsync(570);
        Assert.Equal(Newer, afterOlder?.LastPlayed);

        await Games.UpsertOwnedAsync([new OwnedGame(570, "Dota 2", 1, 0, Newer.AddDays(1), null)]);

        var afterNewer = await Games.GetAsync(570);
        Assert.Equal(Newer.AddDays(1), afterNewer?.LastPlayed);
    }

    [Fact]
    public async Task LastPlayedSurvivesASourceThatDoesNotKnowIt()
    {
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2") with { LastPlayed = Newer }]);
        await Games.UpsertOwnedAsync([new OwnedGame(570, "Dota 2", 1, 0, null, null)]);

        var entry = await Games.GetAsync(570);

        Assert.Equal(Newer, entry?.LastPlayed);
    }

    [Fact]
    public async Task MarkNotInstalledExcept_ClearsOnlyTheInstallColumns()
    {
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2"), Installed(292030, "The Witcher 3")]);
        await Games.UpsertOwnedAsync([new OwnedGame(292030, "The Witcher 3", 4321, 0, Older, null)]);

        await Games.MarkNotInstalledExceptAsync(new HashSet<int> { 570 });

        var uninstalled = await Games.GetAsync(292030);

        Assert.NotNull(uninstalled);
        Assert.False(uninstalled.IsInstalled);
        Assert.Null(uninstalled.InstallDir);
        Assert.Null(uninstalled.FullInstallPath);
        Assert.Equal(0, uninstalled.SizeBytes);
        Assert.Equal(AppStateFlags.None, uninstalled.StateFlags);
        Assert.Null(uninstalled.BuildId);

        // Owned-but-not-installed is a normal library row, so ownership and playtime must survive.
        Assert.True(uninstalled.IsOwned);
        Assert.Equal(4321, uninstalled.PlaytimeForeverMinutes);
        Assert.Equal(Older, uninstalled.LastPlayed);

        var stillInstalled = await Games.GetAsync(570);
        Assert.True(stillInstalled?.IsInstalled);
    }

    [Fact]
    public async Task MarkNotInstalledExcept_ReturnsTheIdsItActuallyFlipped()
    {
        // The caller turns this into the "these cards changed" event, so it must be the rows the
        // UPDATE really touched: not the whole library, and not the apps that are still installed.
        await Games.UpsertInstalledAsync([
            Installed(570, "Dota 2"),
            Installed(292030, "The Witcher 3"),
            Installed(1091500, "Cyberpunk 2077"),
        ]);
        await Games.UpsertOwnedAsync([new OwnedGame(9999, "Owned, never installed", 0, 0, null, null)]);

        var cleared = await Games.MarkNotInstalledExceptAsync(new HashSet<int> { 570 });

        Assert.Equal([292030, 1091500], cleared.Order().ToArray());
    }

    [Fact]
    public async Task MarkNotInstalledExcept_ReturnsNothingWhenNothingChanged()
    {
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2")]);

        Assert.Empty(await Games.MarkNotInstalledExceptAsync(new HashSet<int> { 570 }));

        // A second run over the same scan is a no-op too, not a repeat of the first delta.
        await Games.MarkNotInstalledExceptAsync(new HashSet<int>());
        Assert.Empty(await Games.MarkNotInstalledExceptAsync(new HashSet<int>()));
    }

    [Fact]
    public async Task MarkNotInstalledExcept_WithAnEmptySetClearsEverything()
    {
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2"), Installed(292030, "The Witcher 3")]);

        var cleared = await Games.MarkNotInstalledExceptAsync(new HashSet<int>());

        Assert.Equal([570, 292030], cleared.Order().ToArray());
        Assert.All(await Games.GetAllAsync(), entry => Assert.False(entry.IsInstalled));
    }

    [Fact]
    public async Task MarkNotInstalledExcept_CanRunTwiceOnTheSameConnectionPool()
    {
        // It builds a temp table; a pooled connection keeps its temp schema, so a second run on a
        // recycled connection has to still work.
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2")]);

        await Games.MarkNotInstalledExceptAsync(new HashSet<int> { 570 });
        await Games.MarkNotInstalledExceptAsync(new HashSet<int> { 570 });

        Assert.True((await Games.GetAsync(570))?.IsInstalled);
    }

    [Fact]
    public async Task GetAll_OrdersBySortNameAndFallsBackToTheDisplayName()
    {
        await Games.UpsertInstalledAsync([
            Installed(3, "Zebra"),
            Installed(1, "alpha"),
            Installed(2, "The Witcher 3"),
        ]);

        await Games.UpsertMetadataAsync([Metadata(2, "Witcher 3, The", "game", [], [])]);

        var names = (await Games.GetAllAsync()).Select(entry => entry.Name).ToArray();

        Assert.Equal(["alpha", "The Witcher 3", "Zebra"], names);
    }

    [Fact]
    public async Task GetAll_ResolvesTagAndGenreNamesInTheRecordedLanguage()
    {
        await Games.UpsertInstalledAsync([Installed(570, "Dota 2")]);
        await Games.UpsertMetadataAsync([Metadata(570, "Dota 2", "game", [1], [113, 4242])]);

        await Tags.UpsertGenreNamesAsync([new GenreRef(1, "Action")], "english");
        await Tags.UpsertTagNamesAsync([new TagRef(113, "Free to Play")], "english");
        await Tags.UpsertTagNamesAsync([new TagRef(113, "Бесплатная игра")], "russian");
        await SyncMeta.SetLastRunAsync(SyncKeys.TagNames, DateTimeOffset.UtcNow, "russian");

        var entry = await Games.GetAsync(570);

        Assert.NotNull(entry);
        Assert.Equal("Бесплатная игра", entry.Tags[0].Name);

        // A genre stored only in english still renders rather than losing its name...
        Assert.Equal("Action", entry.Genres[0].Name);

        // ...and an id with no name anywhere renders as the placeholder instead of disappearing.
        Assert.Equal("#4242", entry.Tags[1].Name);
    }

    [Fact]
    public async Task GetAll_CarriesFriendOwnershipAndCollectionMembership()
    {
        const ulong steamId = 76_561_198_000_000_001UL;

        await Games.UpsertInstalledAsync([Installed(570, "Dota 2")]);
        await Friends.ReplaceFriendGamesAsync(steamId, [570]);

        var collection = await Collections.CreateAsync("Favourites", DateTimeOffset.UtcNow);
        await Collections.AddGameAsync(collection.CollectionId, 570);

        var entry = await Games.GetAsync(570);

        Assert.NotNull(entry);

        // A SteamID64 does not fit a signed 64-bit column, so it round-trips through TEXT.
        Assert.Equal([steamId], entry.FriendOwnerIds.ToArray());
        Assert.Equal([collection.CollectionId], entry.CollectionIds.ToArray());
    }

    [Fact]
    public async Task GetAll_HydratesTheWholeLibraryWithOneConnection()
    {
        var counting = new CountingConnectionFactory(Factory);
        var repository = new GameRepository(counting);

        await Games.UpsertInstalledAsync([.. Enumerable.Range(1, 50).Select(id => Installed(id, $"Game {id}"))]);
        await Games.UpsertMetadataAsync([.. Enumerable.Range(1, 50).Select(id => Metadata(id, $"Game {id}", "game", [1], [113]))]);

        counting.Reset();
        var entries = await repository.GetAllAsync();

        Assert.Equal(50, entries.Count);

        // A query per game would make the grid unusable on a real library; one connection, a fixed
        // handful of queries, is the contract.
        Assert.Equal(1, counting.OpenedConnections);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForAnUnknownApp()
    {
        Assert.Null(await Games.GetAsync(4242));
    }

    [Fact]
    public async Task EmptyBatchesAreNoOps()
    {
        await Games.UpsertInstalledAsync([]);
        await Games.UpsertOwnedAsync([]);
        await Games.UpsertMetadataAsync([]);

        Assert.Empty(await Games.GetAllAsync());
    }

    [Fact]
    public async Task CancellationIsHonoured()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Games.GetAllAsync(cts.Token));
    }

    private static InstalledApp Installed(int appId, string name) =>
        new(
            AppId: appId,
            Name: name,
            StateFlags: AppStateFlags.FullyInstalled | AppStateFlags.UpdateRequired,
            InstallDir: "dota 2 beta",
            FullInstallPath: @"C:\Library\steamapps\common\dota 2 beta",
            ManifestPath: @"C:\Library\steamapps\appmanifest_570.acf",
            LibraryPath: @"C:\Library",
            SizeOnDisk: 77_246_578_977L,
            BytesDownloaded: 0,
            BytesToDownload: 0,
            StagingSize: 0,
            BuildId: "25219194",
            TargetBuildId: "25219194",
            LastOwner: 76_561_198_000_000_001UL,
            LastUpdated: Older,
            LastPlayed: null);

    private static AppInfoEntry Metadata(int appId, string name, string type, int[] genreIds, int[] tagIds) =>
        new(
            AppId: appId,
            Name: name,
            Type: type,
            SortAs: null,
            GenreIds: genreIds,
            StoreTagIds: tagIds,
            CategoryIds: [],
            LocalizedNames: new Dictionary<string, string>(),
            Developer: null,
            Publisher: null,
            ReleaseDate: null,
            MetacriticScore: null,
            OsList: null,
            ChangeNumber: 1,
            LastUpdated: null);
}
