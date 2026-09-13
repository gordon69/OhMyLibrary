using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Core.Vdf;
using OhMyLibrary.Data.Repositories;
using OhMyLibrary.Tests.Fakes;
using OhMyLibrary.Tests.Infrastructure;

namespace OhMyLibrary.Tests.Services;

/// <summary>
/// <see cref="CollectionService"/>: thin over the repository, but it owns name validation and the
/// promise that a reorder always produces a complete, dense order.
/// </summary>
public sealed class CollectionServiceTests
{
    private readonly FakeCollectionRepository _repository = new();

    [Fact]
    public async Task Create_TrimsTheName()
    {
        var created = await Service().CreateAsync("  Favourites  ");

        Assert.Equal("Favourites", created.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_RejectsABlankName(string name)
    {
        // A caller mistake, not a degraded environment: this one is reported rather than swallowed.
        await Assert.ThrowsAsync<ArgumentException>(() => Service().CreateAsync(name));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().RenameAsync(1, name));
    }

    [Fact]
    public async Task Reorder_AppendsTheCollectionsTheCallerDidNotMention()
    {
        var service = Service();
        var a = await service.CreateAsync("A");
        var b = await service.CreateAsync("B");
        var c = await service.CreateAsync("C");

        await service.ReorderAsync([c.CollectionId, 4242]);

        var all = await service.GetAllAsync();

        Assert.Equal(["C", "A", "B"], all.Select(collection => collection.Name).ToArray());
        Assert.Equal([0, 1, 2], all.Select(collection => collection.SortOrder).ToArray());
        Assert.Equal([a.CollectionId, b.CollectionId], all.Skip(1).Select(collection => collection.CollectionId).ToArray());
    }

    [Fact]
    public async Task MembershipEditsPassStraightThrough()
    {
        var service = Service();
        var collection = await service.CreateAsync("Favourites");

        await service.AddGameAsync(collection.CollectionId, 570);
        await service.AddGameAsync(collection.CollectionId, 570);
        await service.RemoveGameAsync(collection.CollectionId, 292030);

        var stored = Assert.Single(await service.GetAllAsync());
        Assert.Equal([570], stored.AppIds.ToArray());
    }

    private CollectionService Service() => new(_repository, NullLogger<CollectionService>.Instance);
}

/// <summary>
/// <see cref="InstallStateService"/> reading state straight off disk, with the real
/// <see cref="AcfReader"/> over a throwaway Steam tree.
/// </summary>
public sealed class InstallStateServiceTests : IDisposable
{
    private readonly FakeSteam _steam = new("installstate");
    private readonly FakeSteamPathResolver _paths = new();
    private readonly FakeGameRepository _games = new();
    private readonly FakeSteamUriLauncher _launcher = new();
    private readonly string _library;

    /// <summary>Builds a library the service can scan.</summary>
    public InstallStateServiceTests()
    {
        _library = _steam.AddLibrary("SteamLibrary");
        _paths.SteamPath = _steam.Root;
        _paths.LibraryFolders.Add(new SteamLibraryFolder(_library, string.Empty, 0, new Dictionary<int, long>()));
    }

    /// <inheritdoc />
    public void Dispose() => _steam.Dispose();

    [Fact]
    public async Task GetState_ReadsTheManifestRatherThanTheStoredRow()
    {
        _steam.InstallManifest(_library, "appmanifest_900001.acf");
        _games.Rows.Add(Stored(900001, AppStateFlags.FullyInstalled));

        var state = await Service().GetStateAsync(900001);

        // The manifest says an update is required; the database has not caught up yet.
        Assert.Equal(AppStateFlags.FullyInstalled | AppStateFlags.UpdateRequired, state);
    }

    [Fact]
    public async Task GetState_FallsBackToTheStoredRowWhenNoManifestIsReachable()
    {
        _games.Rows.Add(Stored(570, AppStateFlags.FullyInstalled));

        Assert.Equal(AppStateFlags.FullyInstalled, await Service().GetStateAsync(570));
    }

    [Fact]
    public async Task GetState_IsNoneForAnAppNobodyKnows()
    {
        Assert.Equal(AppStateFlags.None, await Service().GetStateAsync(4242));
        Assert.Equal(AppStateFlags.None, await Service().GetStateAsync(0));
    }

    [Fact]
    public async Task GetState_SurvivesADatabaseFailure()
    {
        _games.Failure = new InvalidOperationException("the database is locked");

        Assert.Equal(AppStateFlags.None, await Service().GetStateAsync(570));
    }

    [Fact]
    public void TheRequestsAreHandedToTheSteamClient()
    {
        var service = Service();

        Assert.True(service.RequestInstall(570));
        Assert.True(service.RequestLaunch(570));
        Assert.True(service.RequestUninstall(570));
        Assert.True(service.RequestValidate(570));

        Assert.Equal(["install:570", "launch:570", "uninstall:570", "validate:570"], _launcher.Calls.ToArray());
    }

    [Fact]
    public void AnUnusableAppIdNeverReachesTheShell()
    {
        var service = Service();

        Assert.False(service.RequestInstall(0));
        Assert.False(service.RequestLaunch(-1));

        Assert.Empty(_launcher.Calls);
    }

    private InstallStateService Service() =>
        new(_paths, new AcfReader(), _games, _launcher, NullLogger<InstallStateService>.Instance);

    private static GameEntry Stored(int appId, AppStateFlags flags) =>
        new(
            AppId: appId,
            Name: $"App {appId}",
            IsOwned: true,
            IsInstalled: true,
            StateFlags: flags,
            InstallDir: null,
            FullInstallPath: null,
            SizeBytes: 0,
            BuildId: null,
            PlaytimeForeverMinutes: 0,
            LastPlayed: null,
            Genres: [],
            Tags: [],
            Assets: null,
            AppType: "game",
            FriendOwnerIds: [],
            CollectionIds: [],
            LastLocalScanUtc: null);
}

/// <summary>
/// <see cref="TagService"/>: where tag names come from, and the language stamp the game repository
/// reads back when it resolves names.
/// </summary>
public sealed class TagServiceTests
{
    private readonly FakeTagDataClient _tagData = new();
    private readonly FakeTagRepository _tagRepository = new();
    private readonly FakeSyncMetaRepository _syncMeta = new();
    private readonly SteamOptions _steamOptions = new() { Language = "russian" };
    private readonly SyncOptions _syncOptions = new();

    [Fact]
    public async Task RefreshNames_StoresTheLanguageAsTheSyncPayload()
    {
        _tagData.TagsByLanguage["russian"] = [new TagRef(492, "Инди")];

        await Service().RefreshNamesAsync(force: true);

        // GameRepository reads this payload to decide which language to hydrate names in; without it
        // the grid renders english while the tag list renders the configured language.
        Assert.Equal("russian", await _syncMeta.GetPayloadAsync(SyncKeys.TagNames));
        Assert.Equal(["russian"], _tagData.Requests.ToArray());
    }

    [Fact]
    public async Task RefreshNames_SeedsTheBuiltInGenresForTheConfiguredLanguage()
    {
        _tagData.TagsByLanguage["russian"] = [new TagRef(492, "Инди")];

        await Service().RefreshNamesAsync(force: true);

        Assert.NotEmpty(_tagRepository.Genres["russian"]);
        Assert.Contains(_tagRepository.Genres["russian"], genre => genre.GenreId == 1);
    }

    [Fact]
    public async Task RefreshNames_KeepsThePreviousNamesWhenTheDownloadComesBackEmpty()
    {
        await _tagRepository.UpsertTagNamesAsync([new TagRef(492, "Инди")], "russian");

        await Service().RefreshNamesAsync(force: true);

        Assert.Equal("Инди", Assert.Single(_tagRepository.Tags["russian"]).Name);
        Assert.Empty(_syncMeta.Writes);
    }

    [Fact]
    public async Task GetAllGenres_LetsStoredNamesWinOverTheBuiltInTable()
    {
        await _tagRepository.UpsertGenreNamesAsync([new GenreRef(1, "Экшен")], "russian");

        var genres = await Service().GetAllGenresAsync();

        Assert.Equal("Экшен", Assert.Single(genres, genre => genre.GenreId == 1).Name);
        Assert.Contains(genres, genre => genre.GenreId == 3);
    }

    [Fact]
    public async Task GetAllGenres_FallsBackToTheBuiltInTableWithNoDatabaseRows()
    {
        var genres = await Service().GetAllGenresAsync();

        Assert.NotEmpty(genres);
        Assert.All(genres, genre => Assert.True(genre.IsResolved));
    }

    [Fact]
    public async Task AnUnconfiguredLanguageFallsBackToEnglish()
    {
        _steamOptions.Language = "   ";
        _tagData.TagsByLanguage["english"] = [new TagRef(492, "Indie")];

        var service = Service();
        await service.RefreshNamesAsync(force: true);

        Assert.Equal("english", service.Language);
        Assert.Equal(["english"], _tagData.Requests.ToArray());
    }

    private TagService Service() =>
        new(
            _tagData,
            _tagRepository,
            _syncMeta,
            Options.Create(_steamOptions),
            Options.Create(_syncOptions),
            NullLogger<TagService>.Instance);
}
