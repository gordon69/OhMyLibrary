using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Tests.Fakes;

namespace OhMyLibrary.Tests.Services;

/// <summary>
/// <see cref="FriendsService"/> with hand-written fakes.
/// </summary>
/// <remarks>
/// The distinction this class exists for: a friend whose library is private is <b>hidden</b>, not
/// empty. Recording a hidden library as "owns no games" would be a lie the UI cannot recover from.
/// </remarks>
public sealed class FriendsServiceTests
{
    private const ulong Me = 76_561_198_000_000_000UL;
    private const ulong Alice = 76_561_198_000_000_001UL;
    private const ulong Bob = 76_561_198_000_000_002UL;

    private readonly FakeSteamWebApiClient _api = new();
    private readonly FakeSteamPathResolver _paths = new();
    private readonly FakeFriendRepository _friends = new();
    private readonly FakeSyncMetaRepository _syncMeta = new();
    private readonly SteamOptions _steamOptions = new() { SteamId64 = "76561198000000000" };
    private readonly SyncOptions _syncOptions = new();

    [Fact]
    public async Task Refresh_TreatsAHiddenLibraryAsHiddenRatherThanEmpty()
    {
        _api.Friends.Add(Friend(Alice));
        _api.Summaries[Alice] = Friend(Alice) with { PersonaName = "Alice" };
        _api.OwnedGames[Alice] = OwnedGamesResult.Hidden;

        await Service().RefreshAsync(force: true);

        var alice = _friends.Friends[Alice];
        Assert.False(alice.GameListVisible);
        Assert.NotNull(alice.LastSyncedUtc);

        // No game rows may be written for a library Steam refused to show us.
        Assert.DoesNotContain(Alice, _friends.GameReplacements);
        Assert.False(_friends.Games.ContainsKey(Alice));
    }

    [Fact]
    public async Task Refresh_StoresAVisibleLibraryEvenWhenItIsEmpty()
    {
        _api.Friends.Add(Friend(Bob));
        _api.OwnedGames[Bob] = OwnedGamesResult.Empty;

        await Service().RefreshAsync(force: true);

        // "Readable and owns nothing" is a different fact from "hidden", and both are worth storing.
        Assert.True(_friends.Friends[Bob].GameListVisible);
        Assert.Contains(Bob, _friends.GameReplacements);
        Assert.Empty(_friends.Games[Bob]);
    }

    [Fact]
    public async Task Refresh_FansOutOverEveryFriendAndKeepsTheirProfiles()
    {
        _api.Friends.AddRange([Friend(Alice), Friend(Bob)]);
        _api.Summaries[Alice] = Friend(Alice) with { PersonaName = "Alice", AvatarUrl = "https://avatars.example.invalid/a.jpg" };
        _api.Summaries[Bob] = Friend(Bob) with { PersonaName = "Bob" };
        _api.OwnedGames[Alice] = new OwnedGamesResult(true, [new OwnedGame(570, "Dota 2", 0, 0, null, null)]);
        _api.OwnedGames[Bob] = new OwnedGamesResult(true, [new OwnedGame(292030, "The Witcher 3", 0, 0, null, null)]);

        var progress = new List<string>();
        await Service().RefreshAsync(force: true, new Progress<string>(progress.Add));

        Assert.Equal([Alice, Bob], _api.OwnedGamesCalls.Order().ToArray());
        Assert.Equal([570], _friends.Games[Alice].ToArray());
        Assert.Equal([292030], _friends.Games[Bob].ToArray());
        Assert.Contains(SyncKeys.Friends, _syncMeta.Writes);
    }

    [Fact]
    public async Task Refresh_CarriesFriendSinceThroughTheSummariesCall()
    {
        var since = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _api.Friends.Add(Friend(Alice) with { FriendSince = since });
        _api.Summaries[Alice] = Friend(Alice) with { PersonaName = "Alice", FriendSince = null };
        _api.OwnedGames[Alice] = OwnedGamesResult.Empty;

        await Service().RefreshAsync(force: true);

        // GetPlayerSummaries does not report friend_since, so it has to come from the friend list.
        Assert.Contains(_friends.Upserts.SelectMany(batch => batch), friend => friend.FriendSince == since);
    }

    [Fact]
    public async Task Refresh_WithoutAnApiKeyDoesNothingAndSaysSo()
    {
        _api.IsConfigured = false;

        var progress = new List<string>();
        await Service().RefreshAsync(force: true, new Progress<string>(progress.Add));

        Assert.Empty(_friends.Friends);
        Assert.Empty(_syncMeta.Writes);
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task Refresh_WithoutASteamIdDoesNothing()
    {
        _steamOptions.SteamId64 = string.Empty;

        await Service().RefreshAsync(force: true);

        Assert.Empty(_friends.Friends);
        Assert.Empty(_syncMeta.Writes);
    }

    [Fact]
    public async Task Refresh_DoesNotStampTheSyncTimeWhenTheFriendListIsUnavailable()
    {
        // A private profile answers GetFriendList with 401/403, which the client reports as empty;
        // stamping that as a successful sync would hide the friends list for a whole TTL.
        await Service().RefreshAsync(force: true);

        Assert.Empty(_syncMeta.Writes);
    }

    [Fact]
    public async Task Refresh_HonoursTheCacheLifetimeUnlessForced()
    {
        _api.Friends.Add(Friend(Alice));
        _api.OwnedGames[Alice] = OwnedGamesResult.Empty;
        _syncMeta.Seed(SyncKeys.Friends, DateTimeOffset.UtcNow.AddHours(-1));
        _syncOptions.FriendsMaxAgeHours = 24;

        var service = Service();

        await service.RefreshAsync(force: false);
        Assert.Empty(_api.OwnedGamesCalls);

        await service.RefreshAsync(force: true);
        Assert.Single(_api.OwnedGamesCalls);
    }

    [Fact]
    public async Task Refresh_FallsBackToTheSignedInAccountWhenNoIdIsConfigured()
    {
        _steamOptions.SteamId64 = string.Empty;
        _paths.LocalUsers.Add(new SteamUser(Me, "fixture_account", "Fixture User", MostRecent: true, DateTimeOffset.UtcNow));
        _api.Friends.Add(Friend(Alice));
        _api.OwnedGames[Alice] = OwnedGamesResult.Empty;

        await Service().RefreshAsync(force: true);

        Assert.Single(_friends.Friends);
    }

    [Fact]
    public async Task GetFriends_AndGetOwnersOf_ReadFromTheDatabase()
    {
        await _friends.UpsertFriendsAsync([Friend(Alice) with { PersonaName = "Alice" }]);
        await _friends.ReplaceFriendGamesAsync(Alice, [570]);

        var service = Service();

        Assert.Single(await service.GetFriendsAsync());
        Assert.Single(await service.GetOwnersOfAsync(570));
        Assert.Empty(await service.GetOwnersOfAsync(292030));
        Assert.Empty(await service.GetOwnersOfAsync(0));
    }

    private FriendsService Service() =>
        new(
            _api,
            _paths,
            _friends,
            _syncMeta,
            Options.Create(_steamOptions),
            Options.Create(_syncOptions),
            NullLogger<FriendsService>.Instance);

    private static FriendSummary Friend(ulong steamId) =>
        new(steamId, string.Empty, null, null, PersonaState.Offline, null, GameListVisible: true, LastSyncedUtc: null);
}
