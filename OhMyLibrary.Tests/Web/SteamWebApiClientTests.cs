using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Web;
using OhMyLibrary.Tests.Fakes;

namespace OhMyLibrary.Tests.Web;

/// <summary>
/// <see cref="SteamWebApiClient"/> against a stubbed transport.
/// </summary>
/// <remarks>
/// Everything Steam does to an unlucky caller — a private profile, a 401, a 429, a dead socket — has
/// to come back as an empty or hidden result. Only the caller's own cancellation escapes.
/// </remarks>
public sealed class SteamWebApiClientTests
{
    private const ulong SteamId = 76_561_198_000_000_001UL;
    private const string ApiKey = "0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task NoApiKeyShortCircuitsWithoutTouchingTheNetwork()
    {
        var stub = new StubHttpMessageHandler();
        var client = Client(stub, apiKey: string.Empty);

        Assert.False(client.IsConfigured);
        Assert.Equal(OwnedGamesResult.Hidden, await client.GetOwnedGamesAsync(SteamId));
        Assert.Empty(await client.GetFriendListAsync(SteamId));
        Assert.Empty(await client.GetPlayerSummariesAsync([SteamId]));
        Assert.Null(await client.ResolveVanityUrlAsync("someone"));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task GetOwnedGames_MapsAVisibleLibrary()
    {
        var stub = new StubHttpMessageHandler().RespondJson(
            """
            {"response":{"game_count":2,"games":[
              {"appid":570,"name":"Dota 2","playtime_forever":1200,"playtime_2weeks":30,"rtime_last_played":1786388287,"img_icon_url":"abc"},
              {"appid":292030,"name":"The Witcher 3","playtime_forever":0,"rtime_last_played":0}
            ]}}
            """);

        var result = await Client(stub).GetOwnedGamesAsync(SteamId);

        Assert.True(result.Visible);
        Assert.Equal(2, result.Games.Count);

        var dota = result.Games[0];
        Assert.Equal(570, dota.AppId);
        Assert.Equal("Dota 2", dota.Name);
        Assert.Equal(1200, dota.PlaytimeForeverMinutes);
        Assert.Equal(30, dota.Playtime2WeeksMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_786_388_287), dota.LastPlayed);
        Assert.Equal("abc", dota.ImgIconUrl);

        // A zero timestamp means never played, not 1970.
        Assert.Null(result.Games[1].LastPlayed);
    }

    [Fact]
    public async Task GetOwnedGames_ReadsNumbersValveQuoted()
    {
        // Valve is inconsistent about quoting numbers between endpoints.
        var stub = new StubHttpMessageHandler().RespondJson(
            """{"response":{"game_count":"1","games":[{"appid":"570","name":"Dota 2","playtime_forever":"1200"}]}}""");

        var result = await Client(stub).GetOwnedGamesAsync(SteamId);

        Assert.Equal(570, Assert.Single(result.Games).AppId);
        Assert.Equal(1200, result.Games[0].PlaytimeForeverMinutes);
    }

    [Fact]
    public async Task GetOwnedGames_TellsAPrivateLibraryApartFromAnEmptyOne()
    {
        // A private profile answers 200 with an empty response object rather than an error.
        var hidden = await Client(new StubHttpMessageHandler().RespondJson("""{"response":{}}""")).GetOwnedGamesAsync(SteamId);
        Assert.False(hidden.Visible);
        Assert.Empty(hidden.Games);

        var empty = await Client(new StubHttpMessageHandler().RespondJson("""{"response":{"game_count":0,"games":[]}}""")).GetOwnedGamesAsync(SteamId);
        Assert.True(empty.Visible);
        Assert.Empty(empty.Games);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusedRequestIsHiddenRatherThanAnError(HttpStatusCode status)
    {
        var stub = new StubHttpMessageHandler().RespondStatus(status);

        Assert.False((await Client(stub).GetOwnedGamesAsync(SteamId)).Visible);

        // 401 and 403 are answers, not transient failures: they must not be retried.
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ARateLimitedRequestIsRetriedAndThenSucceeds()
    {
        var stub = new StubHttpMessageHandler()
            .RespondStatus(HttpStatusCode.TooManyRequests)
            .RespondJson("""{"response":{"game_count":1,"games":[{"appid":570,"name":"Dota 2"}]}}""");

        var result = await Client(stub).GetOwnedGamesAsync(SteamId);

        Assert.True(result.Visible);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task ARequestThatKeepsFailingGivesUpAfterABoundedNumberOfAttempts()
    {
        var stub = new StubHttpMessageHandler()
            .RespondStatus(HttpStatusCode.ServiceUnavailable)
            .RespondStatus(HttpStatusCode.ServiceUnavailable)
            .RespondStatus(HttpStatusCode.ServiceUnavailable)
            .RespondStatus(HttpStatusCode.ServiceUnavailable);

        var result = await Client(stub).GetOwnedGamesAsync(SteamId);

        Assert.False(result.Visible);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task ATransportFailureIsHiddenRatherThanAnException()
    {
        var stub = new StubHttpMessageHandler()
            .RespondTransportFailure()
            .RespondTransportFailure()
            .RespondTransportFailure();

        Assert.False((await Client(stub).GetOwnedGamesAsync(SteamId)).Visible);
    }

    [Fact]
    public async Task AnUnparseableBodyIsHiddenRatherThanAnException()
    {
        var stub = new StubHttpMessageHandler().RespondJson("this is not json");

        Assert.False((await Client(stub).GetOwnedGamesAsync(SteamId)).Visible);

        // A body we cannot parse is not going to parse on a second attempt either.
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task GetPlayerSummaries_ChunksAtValvesLimitOfOneHundredIds()
    {
        var stub = new StubHttpMessageHandler()
            .RespondJson("""{"response":{"players":[]}}""")
            .RespondJson("""{"response":{"players":[]}}""")
            .RespondJson("""{"response":{"players":[]}}""");

        await Client(stub).GetPlayerSummariesAsync(Enumerable.Range(1, 250).Select(i => SteamId + (ulong)i));

        Assert.Equal(3, stub.Requests.Count);
        Assert.Equal([100, 100, 50], stub.Requests.Select(CountSteamIds).ToArray());
        Assert.Equal(100, SteamWebApiClient.PlayerSummariesBatchSize);
    }

    [Fact]
    public async Task GetPlayerSummaries_DedupesAndKeepsTheFirstSeenOrder()
    {
        var stub = new StubHttpMessageHandler().RespondJson("""{"response":{"players":[]}}""");

        await Client(stub).GetPlayerSummariesAsync([SteamId, SteamId + 1, SteamId, 0]);

        var request = Assert.Single(stub.Requests);
        Assert.Equal(2, CountSteamIds(request));
        Assert.Contains($"steamids={SteamId},{SteamId + 1}", Uri.UnescapeDataString(request), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPlayerSummaries_KeepsTheBatchesThatSucceeded()
    {
        var stub = new StubHttpMessageHandler()
            .RespondStatus(HttpStatusCode.Forbidden)
            .RespondJson(WithId("""{"response":{"players":[{"steamid":"%ID%","personaname":"Alice","personastate":1,"avatarfull":"https://avatars.example.invalid/a.jpg","communityvisibilitystate":3}]}}"""));

        var summaries = await Client(stub).GetPlayerSummariesAsync(
            Enumerable.Range(1, 150).Select(i => (ulong)i).Append(SteamId));

        // The result can be shorter than the input; losing everything because one batch failed
        // would throw away up to a hundred profiles.
        var alice = Assert.Single(summaries);
        Assert.Equal("Alice", alice.PersonaName);
        Assert.Equal(PersonaState.Online, alice.State);
        Assert.Equal("https://avatars.example.invalid/a.jpg", alice.AvatarUrl);
        Assert.True(alice.GameListVisible);
        Assert.Null(alice.FriendSince);
    }

    [Fact]
    public async Task GetPlayerSummaries_ReadsProfileVisibility()
    {
        var stub = new StubHttpMessageHandler().RespondJson(
            WithId("""{"response":{"players":[{"steamid":"%ID%","personaname":"Alice","communityvisibilitystate":1}]}}"""));

        Assert.False(Assert.Single(await Client(stub).GetPlayerSummariesAsync([SteamId])).GameListVisible);
    }

    [Fact]
    public async Task GetFriendList_MapsIdsAndFriendSince()
    {
        var stub = new StubHttpMessageHandler().RespondJson(
            WithId("""
            {"friendslist":{"friends":[
              {"steamid":"%ID%","relationship":"friend","friend_since":1600000000},
              {"steamid":"0","relationship":"friend","friend_since":0}
            ]}}
            """));

        var friends = await Client(stub).GetFriendListAsync(SteamId);

        var friend = Assert.Single(friends);
        Assert.Equal(SteamId, friend.SteamId64);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000), friend.FriendSince);
        Assert.Equal(string.Empty, friend.PersonaName);
        Assert.Null(friend.LastSyncedUtc);
    }

    [Fact]
    public async Task GetFriendList_IsEmptyForAPrivateProfile()
    {
        var stub = new StubHttpMessageHandler().RespondStatus(HttpStatusCode.Unauthorized);

        Assert.Empty(await Client(stub).GetFriendListAsync(SteamId));
    }

    [Fact]
    public async Task ResolveVanityUrl_ReturnsNullWhenItDoesNotResolve()
    {
        var found = new StubHttpMessageHandler().RespondJson(WithId("""{"response":{"success":1,"steamid":"%ID%"}}"""));
        var missing = new StubHttpMessageHandler().RespondJson("""{"response":{"success":42,"message":"No match"}}""");

        Assert.Equal(SteamId, await Client(found).ResolveVanityUrlAsync("alice"));
        Assert.Null(await Client(missing).ResolveVanityUrlAsync("nobody"));
        Assert.Null(await Client(new StubHttpMessageHandler()).ResolveVanityUrlAsync("   "));
    }

    [Fact]
    public async Task TheApiKeyIsSentAsTheLastQueryParameter()
    {
        var stub = new StubHttpMessageHandler().RespondJson("""{"response":{"game_count":0,"games":[]}}""");

        await Client(stub).GetOwnedGamesAsync(SteamId);

        // Truncate the URI anywhere and the key is the first thing lost.
        Assert.EndsWith($"key={ApiKey}", Assert.Single(stub.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationIsTheOneExceptionThatEscapes()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var stub = new StubHttpMessageHandler().RespondJson("""{"response":{}}""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(stub).GetOwnedGamesAsync(SteamId, cts.Token));
    }

    /// <summary>Fills the account id into a canned response body.</summary>
    private static string WithId(string json) => json.Replace("%ID%", SteamId.ToString(), StringComparison.Ordinal);

    private static SteamWebApiClient Client(StubHttpMessageHandler handler, string apiKey = ApiKey) =>
        new(
            new HttpClient(handler) { BaseAddress = SteamWebApiClient.BaseAddress },
            Options.Create(new SteamOptions { ApiKey = apiKey }),
            NullLogger<SteamWebApiClient>.Instance);

    private static int CountSteamIds(string requestUri)
    {
        var query = new Uri(requestUri).Query;
        var start = query.IndexOf("steamids=", StringComparison.Ordinal) + "steamids=".Length;
        var end = query.IndexOf('&', start);
        var value = end < 0 ? query[start..] : query[start..end];

        return Uri.UnescapeDataString(value).Split(',').Length;
    }
}
