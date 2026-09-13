using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Data;
using OhMyLibrary.Data.Repositories;
using OhMyLibrary.Tests.Infrastructure;

namespace OhMyLibrary.Tests.Data;

/// <summary>
/// <see cref="SyncMetaRepository"/>: the timestamps that decide when an expensive refresh is due.
/// </summary>
public sealed class SyncMetaRepositoryTests : SqliteFixtureBase
{
    private static readonly DateTimeOffset When = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public async Task GetLastRun_IsNullUntilSomethingHasRun()
    {
        Assert.Null(await SyncMeta.GetLastRunAsync(SyncKeys.OwnedGames));
        Assert.Null(await SyncMeta.GetPayloadAsync(SyncKeys.OwnedGames));
    }

    [Fact]
    public async Task SetLastRun_RoundTripsTheTimestampAndPayload()
    {
        await SyncMeta.SetLastRunAsync(SyncKeys.TagNames, When, "russian");

        Assert.Equal(When, await SyncMeta.GetLastRunAsync(SyncKeys.TagNames));
        Assert.Equal("russian", await SyncMeta.GetPayloadAsync(SyncKeys.TagNames));
    }

    [Fact]
    public async Task SetLastRun_WithoutAPayloadKeepsTheStoredOne()
    {
        await SyncMeta.SetLastRunAsync(SyncKeys.TagNames, When, "russian");
        await SyncMeta.SetLastRunAsync(SyncKeys.TagNames, When.AddHours(1), null);

        // A null payload means "nothing to say", not "forget the language you fetched".
        Assert.Equal(When.AddHours(1), await SyncMeta.GetLastRunAsync(SyncKeys.TagNames));
        Assert.Equal("russian", await SyncMeta.GetPayloadAsync(SyncKeys.TagNames));
    }

    [Fact]
    public async Task TheSeededSchemaVersionRowReadsAsNeverRun()
    {
        // The schema seeds its own version row with a MinValue sentinel, which is not a sync.
        Assert.Null(await SyncMeta.GetLastRunAsync(DatabaseSchema.VersionKey));
        Assert.Equal("1", await SyncMeta.GetPayloadAsync(DatabaseSchema.VersionKey));
    }

    [Fact]
    public async Task TimestampsAreStoredAsSortableIsoText()
    {
        await SyncMeta.SetLastRunAsync(SyncKeys.LocalScan, When, null);

        var stored = await ScalarAsync<string>(
            "SELECT last_run_utc FROM SyncMeta WHERE key = @key",
            new { key = SyncKeys.LocalScan });

        Assert.Equal("2026-02-03T04:05:06.0000000+00:00", stored);
    }

    [Fact]
    public async Task ATimestampInAnotherOffsetComesBackAsTheSameInstant()
    {
        var local = new DateTimeOffset(2026, 2, 3, 7, 5, 6, TimeSpan.FromHours(3));

        await SyncMeta.SetLastRunAsync(SyncKeys.Friends, local, null);

        Assert.Equal(local.ToUniversalTime(), (await SyncMeta.GetLastRunAsync(SyncKeys.Friends))?.ToUniversalTime());
    }
}

/// <summary>
/// <see cref="TagRepository"/>: the id-and-language keyed name tables behind every tag chip.
/// </summary>
public sealed class TagRepositoryTests : SqliteFixtureBase
{
    [Fact]
    public async Task Upsert_KeepsOneRowPerIdAndLanguage()
    {
        await Tags.UpsertTagNamesAsync([new TagRef(492, "Indie"), new TagRef(113, "Free to Play")], "english");
        await Tags.UpsertTagNamesAsync([new TagRef(492, "Инди")], "russian");

        Assert.Equal(
            ["Free to Play", "Indie"],
            (await Tags.GetTagsAsync("english")).Select(tag => tag.Name).ToArray());

        Assert.Equal(["Инди"], (await Tags.GetTagsAsync("russian")).Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task Upsert_IsAdditiveSoAShortDownloadCannotWipeTheTable()
    {
        await Tags.UpsertTagNamesAsync([new TagRef(492, "Indie"), new TagRef(113, "Free to Play")], "english");
        await Tags.UpsertTagNamesAsync([new TagRef(492, "Indie Games")], "english");

        var tags = await Tags.GetTagsAsync("english");

        Assert.Equal(2, tags.Count);
        Assert.Equal("Indie Games", Assert.Single(tags, tag => tag.TagId == 492).Name);
    }

    [Fact]
    public async Task GenresBehaveTheSameWay()
    {
        await Tags.UpsertGenreNamesAsync([new GenreRef(1, "Action"), new GenreRef(3, "RPG")], "english");
        await Tags.UpsertGenreNamesAsync([new GenreRef(1, "Экшен")], "russian");

        Assert.Equal(["Action", "RPG"], (await Tags.GetGenresAsync("english")).Select(genre => genre.Name).ToArray());
        Assert.Equal(["Экшен"], (await Tags.GetGenresAsync("russian")).Select(genre => genre.Name).ToArray());
    }

    [Fact]
    public async Task AnUnknownLanguageIsEmptyRatherThanAFailure()
    {
        await Tags.UpsertTagNamesAsync([new TagRef(492, "Indie")], "english");

        Assert.Empty(await Tags.GetTagsAsync("klingon"));
        Assert.Empty(await Tags.GetGenresAsync("english"));
    }

    [Fact]
    public async Task EmptyBatchesAreNoOps()
    {
        await Tags.UpsertTagNamesAsync([], "english");
        await Tags.UpsertGenreNamesAsync([], "english");

        Assert.Empty(await Tags.GetTagsAsync("english"));
    }
}

/// <summary>
/// The schema and the connection factory: created on demand, idempotent, and pragma'd on every open.
/// </summary>
public sealed class DatabaseSchemaTests
{
    [Fact]
    public async Task TheScriptIsIdempotentAndTheDataSurvivesReopening()
    {
        using var temp = new TempDirectory("db");
        var path = Path.Combine(temp.Path, "nested", "library.db");

        using (var factory = new SqliteConnectionFactory(path))
        {
            var games = new GameRepository(factory);
            await games.UpsertOwnedAsync([new OwnedGame(570, "Dota 2", 10, 0, null, null)]);
        }

        // A second factory over the same file re-runs the DDL, which must not throw or wipe anything.
        using (var factory = new SqliteConnectionFactory(path))
        {
            var games = new GameRepository(factory);
            var entry = await games.GetAsync(570);

            Assert.Equal("Dota 2", entry?.Name);
        }

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task EveryConnectionGetsTheConnectionScopedPragmas()
    {
        using var factory = new SqliteConnectionFactory(SqliteConnectionFactory.InMemoryPath);

        await using var connection = await factory.CreateOpenAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        command.CommandText = "PRAGMA busy_timeout";
        Assert.Equal(5000L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task TwoInMemoryFactoriesDoNotShareADatabase()
    {
        using var first = new SqliteConnectionFactory(SqliteConnectionFactory.InMemoryPath);
        using var second = new SqliteConnectionFactory(SqliteConnectionFactory.InMemoryPath);

        await new GameRepository(first).UpsertOwnedAsync([new OwnedGame(570, "Dota 2", 0, 0, null, null)]);

        Assert.Empty(await new GameRepository(second).GetAllAsync());
        Assert.Equal(SqliteConnectionFactory.InMemoryPath, first.DatabasePath);
    }

    [Fact]
    public void TheDefaultDatabaseLivesUnderLocalAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OhMyLibrary",
            "library.db");

        Assert.Equal(expected, SqliteConnectionFactory.DefaultDatabasePath);
    }

    [Fact]
    public void TimestampsGoToStorageInRoundTripFormat()
    {
        var value = new DateTimeOffset(2026, 1, 7, 3, 4, 5, TimeSpan.Zero);

        Assert.Equal("2026-01-07T03:04:05.0000000+00:00", SqliteTypeHandlers.ToStorage(value));
        Assert.Equal(value, SqliteTypeHandlers.FromStorage("2026-01-07T03:04:05.0000000+00:00"));
        Assert.Null(SqliteTypeHandlers.ToStorage(null));
        Assert.Null(SqliteTypeHandlers.FromStorage(null));
        Assert.Null(SqliteTypeHandlers.FromStorage("not a timestamp"));
    }
}
