using OhMyLibrary.Core.Vdf;
using OhMyLibrary.Tests.Infrastructure;

namespace OhMyLibrary.Tests.Vdf;

/// <summary>
/// <see cref="LoginUsersReader"/> against a scrubbed copy of a real <c>config/loginusers.vdf</c>.
/// </summary>
/// <remarks>
/// The fixture is the shape the live client wrote on the reference machine, which notably carries
/// <b>no</b> <c>MostRecent</c> key at all — the case that breaks a naive "the account with
/// MostRecent = 1" lookup.
/// </remarks>
public sealed class LoginUsersReaderTests : IDisposable
{
    private readonly FakeSteam _steam = new("loginusers");
    private readonly LoginUsersReader _reader = new();

    /// <inheritdoc />
    public void Dispose() => _steam.Dispose();

    [Fact]
    public void Read_ParsesTheAccountTheClientWrote()
    {
        _steam.WriteLoginUsers("loginusers.vdf");

        var user = Assert.Single(_reader.Read(_steam.Root));

        Assert.Equal(76_561_198_000_000_001UL, user.SteamId64);
        Assert.Equal("fixture_account", user.AccountName);
        Assert.Equal("Fixture User", user.PersonaName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_789_056_195), user.Timestamp);
    }

    [Fact]
    public void Read_ReportsNobodyAsMostRecentWhenTheKeyIsAbsent()
    {
        _steam.WriteLoginUsers("loginusers.vdf");

        var users = _reader.Read(_steam.Root);

        // The live client omits MostRecent entirely, so auto-detection has to fall back to the
        // newest timestamp or to "the only account".
        Assert.All(users, user => Assert.False(user.MostRecent));
    }

    [Fact]
    public void Read_MarksTheMostRecentAccountWhenTheKeyIsPresent()
    {
        _steam.WriteLoginUsers("loginusers_multi.vdf");

        var users = _reader.Read(_steam.Root);

        Assert.Equal(2, users.Count);
        var mostRecent = Assert.Single(users, user => user.MostRecent);
        Assert.Equal(76_561_198_000_000_002UL, mostRecent.SteamId64);
        Assert.Equal("Second Fixture", mostRecent.PersonaName);
    }

    [Fact]
    public void Read_SkipsAnEntryWhoseKeyIsNotASteamId()
    {
        _steam.WriteLoginUsers("loginusers_multi.vdf");

        var users = _reader.Read(_steam.Root);

        Assert.DoesNotContain(users, user => user.AccountName == "broken");
    }

    [Fact]
    public void AccountId_IsTheLow32BitsOfTheSteamId()
    {
        _steam.WriteLoginUsers("loginusers.vdf");

        var user = Assert.Single(_reader.Read(_steam.Root));

        // userdata/<accountId>/ is named with the 32-bit id, i.e. steamId64 & 0xFFFFFFFF.
        Assert.Equal(39_734_273u, user.AccountId);
    }

    [Fact]
    public void Read_ReturnsNothingWhenTheFileIsMissing()
    {
        Assert.Empty(_reader.Read(_steam.Root));
    }

    [Fact]
    public void Read_ReturnsNothingForAGarbageFileInsteadOfThrowing()
    {
        _steam.WriteLoginUsers("loginusers_garbage.vdf");

        Assert.Empty(_reader.Read(_steam.Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_ReturnsNothingForAnEmptySteamPath(string steamPath)
    {
        Assert.Empty(_reader.Read(steamPath));
    }
}
