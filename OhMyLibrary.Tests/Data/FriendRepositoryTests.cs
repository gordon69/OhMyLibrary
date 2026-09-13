using OhMyLibrary.Core.Models;
using OhMyLibrary.Data.Repositories;

namespace OhMyLibrary.Tests.Data;

/// <summary>
/// <see cref="FriendRepository"/> against a real SQLite database.
/// </summary>
/// <remarks>
/// Three different calls fill in three different parts of a friend row — the friend list knows ids,
/// the summaries call knows names and avatars, the per-friend fan-out knows library visibility — so
/// the upsert has to be additive in exactly the way these tests describe.
/// </remarks>
public sealed class FriendRepositoryTests : SqliteFixtureBase
{
    private const ulong Alice = 76_561_198_000_000_001UL;
    private const ulong Bob = 76_561_198_000_000_002UL;

    private static readonly DateTimeOffset FriendSince = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Synced = new(2026, 5, 5, 5, 5, 5, TimeSpan.Zero);

    [Fact]
    public async Task Upsert_RoundTripsAFullSummary()
    {
        await Friends.UpsertFriendsAsync([Full(Alice, "Alice")]);

        var friend = Assert.Single(await Friends.GetAllAsync());

        // A SteamID64 is unsigned and is stored as text; it has to come back as the same number.
        Assert.Equal(Alice, friend.SteamId64);
        Assert.Equal("Alice", friend.PersonaName);
        Assert.Equal("https://avatars.example.invalid/alice.jpg", friend.AvatarUrl);
        Assert.Equal("https://steamcommunity.com/id/alice", friend.ProfileUrl);
        Assert.Equal(PersonaState.Online, friend.State);
        Assert.Equal(FriendSince, friend.FriendSince);
        Assert.True(friend.GameListVisible);
        Assert.Equal(Synced, friend.LastSyncedUtc);
    }

    [Fact]
    public async Task Upsert_KeepsWhatAnEarlierCallFilledIn()
    {
        await Friends.UpsertFriendsAsync([Full(Alice, "Alice")]);

        // What a bare GetFriendList refresh looks like: an id, no profile, no sync stamp.
        await Friends.UpsertFriendsAsync([
            new FriendSummary(Alice, string.Empty, null, null, PersonaState.Offline, null, false, null),
        ]);

        var friend = Assert.Single(await Friends.GetAllAsync());

        Assert.Equal("Alice", friend.PersonaName);
        Assert.Equal("https://avatars.example.invalid/alice.jpg", friend.AvatarUrl);
        Assert.Equal("https://steamcommunity.com/id/alice", friend.ProfileUrl);
        Assert.Equal(FriendSince, friend.FriendSince);
        Assert.Equal(Synced, friend.LastSyncedUtc);

        // GameListVisible cannot express "unknown", so it only moves when the writer also knows when
        // it last synced — otherwise a plain refresh would hide every friend.
        Assert.True(friend.GameListVisible);
    }

    [Fact]
    public async Task Upsert_RecordsAHiddenLibraryWhenTheFanOutSaysSo()
    {
        await Friends.UpsertFriendsAsync([Full(Alice, "Alice")]);

        await Friends.UpsertFriendsAsync([
            Full(Alice, "Alice") with { GameListVisible = false, LastSyncedUtc = Synced.AddHours(1) },
        ]);

        var friend = Assert.Single(await Friends.GetAllAsync());

        Assert.False(friend.GameListVisible);
        Assert.Equal(Synced.AddHours(1), friend.LastSyncedUtc);
    }

    [Fact]
    public async Task ReplaceFriendGames_ReplacesTheWholeSetAndMarksTheLibraryVisible()
    {
        await Friends.ReplaceFriendGamesAsync(Alice, [570, 292030]);
        await Friends.ReplaceFriendGamesAsync(Alice, [292030, 1091500]);

        var owners = await Friends.GetOwnersOfAsync(292030);
        Assert.Single(owners);

        // The row is created by the fan-out even when the friend list has not been stored yet, and a
        // library we could actually read is a visible one.
        Assert.True(owners[0].GameListVisible);
        Assert.NotNull(owners[0].LastSyncedUtc);

        Assert.Empty(await Friends.GetOwnersOfAsync(570));
        Assert.Single(await Friends.GetOwnersOfAsync(1091500));
    }

    [Fact]
    public async Task ReplaceFriendGames_WithAnEmptyListMeansOwnsNothing()
    {
        await Friends.ReplaceFriendGamesAsync(Alice, [570]);
        await Friends.ReplaceFriendGamesAsync(Alice, []);

        Assert.Empty(await Friends.GetOwnersOfAsync(570));
        Assert.Single(await Friends.GetAllAsync());
    }

    [Fact]
    public async Task GetOwnersOf_ListsEveryFriendWhoOwnsTheApp()
    {
        await Friends.UpsertFriendsAsync([Full(Alice, "Alice"), Full(Bob, "Bob")]);
        await Friends.ReplaceFriendGamesAsync(Alice, [570]);
        await Friends.ReplaceFriendGamesAsync(Bob, [570, 292030]);

        var owners = await Friends.GetOwnersOfAsync(570);

        Assert.Equal([Alice, Bob], owners.Select(owner => owner.SteamId64).Order().ToArray());
        Assert.Equal(["Alice", "Bob"], owners.Select(owner => owner.PersonaName).ToArray());
    }

    [Fact]
    public async Task GetAll_OrdersByPersonaName()
    {
        await Friends.UpsertFriendsAsync([Full(Bob, "zoe"), Full(Alice, "Alice")]);

        var friends = await Friends.GetAllAsync();

        Assert.Equal(["Alice", "zoe"], friends.Select(friend => friend.PersonaName).ToArray());
    }

    [Fact]
    public async Task EmptyBatchesAndUnknownAppsAreNoOps()
    {
        await Friends.UpsertFriendsAsync([]);

        Assert.Empty(await Friends.GetAllAsync());
        Assert.Empty(await Friends.GetOwnersOfAsync(570));
    }

    private static FriendSummary Full(ulong steamId, string personaName) =>
        new(
            steamId,
            personaName,
            AvatarUrl: $"https://avatars.example.invalid/{personaName.ToLowerInvariant()}.jpg",
            ProfileUrl: $"https://steamcommunity.com/id/{personaName.ToLowerInvariant()}",
            State: PersonaState.Online,
            FriendSince: FriendSince,
            GameListVisible: true,
            LastSyncedUtc: Synced);
}
