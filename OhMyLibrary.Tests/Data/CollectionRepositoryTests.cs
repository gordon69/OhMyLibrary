using OhMyLibrary.Data.Repositories;

namespace OhMyLibrary.Tests.Data;

/// <summary>
/// <see cref="CollectionRepository"/> against a real SQLite database: the collections are ours
/// alone, so this is the only place their ordering and membership rules are enforced.
/// </summary>
public sealed class CollectionRepositoryTests : SqliteFixtureBase
{
    private static readonly DateTimeOffset Created = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public async Task Create_AppendsAfterTheExistingCollections()
    {
        var first = await Collections.CreateAsync("Favourites", Created);
        var second = await Collections.CreateAsync("Backlog", Created);

        Assert.Equal(0, first.SortOrder);
        Assert.Equal(1, second.SortOrder);
        Assert.NotEqual(first.CollectionId, second.CollectionId);
        Assert.Empty(first.AppIds);

        var all = await Collections.GetAllAsync();
        Assert.Equal(["Favourites", "Backlog"], all.Select(collection => collection.Name).ToArray());
    }

    [Fact]
    public async Task Create_RoundTripsTheTimestamp()
    {
        var created = await Collections.CreateAsync("Favourites", Created);

        var stored = await Collections.GetAsync(created.CollectionId);

        Assert.Equal(Created, stored?.CreatedUtc);
    }

    [Fact]
    public async Task Rename_ChangesOnlyTheName()
    {
        var collection = await Collections.CreateAsync("Favourites", Created);
        await Collections.AddGameAsync(collection.CollectionId, 570);

        await Collections.RenameAsync(collection.CollectionId, "Best of");

        var renamed = await Collections.GetAsync(collection.CollectionId);
        Assert.Equal("Best of", renamed?.Name);
        Assert.NotNull(renamed);
        Assert.Equal([570], renamed.AppIds.ToArray());
    }

    [Fact]
    public async Task RenameAndDelete_AreNoOpsForAnUnknownId()
    {
        await Collections.RenameAsync(4242, "Nothing");
        await Collections.DeleteAsync(4242);

        Assert.Empty(await Collections.GetAllAsync());
    }

    [Fact]
    public async Task Delete_RemovesTheMembershipRowsToo()
    {
        var collection = await Collections.CreateAsync("Favourites", Created);
        await Collections.AddGameAsync(collection.CollectionId, 570);

        await Collections.DeleteAsync(collection.CollectionId);

        Assert.Null(await Collections.GetAsync(collection.CollectionId));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM GameCollections"));
    }

    [Fact]
    public async Task Reorder_WritesADenseZeroBasedOrder()
    {
        var a = await Collections.CreateAsync("A", Created);
        var b = await Collections.CreateAsync("B", Created);
        var c = await Collections.CreateAsync("C", Created);

        await Collections.ReorderAsync([c.CollectionId, a.CollectionId, b.CollectionId]);

        var all = await Collections.GetAllAsync();
        Assert.Equal(["C", "A", "B"], all.Select(collection => collection.Name).ToArray());
        Assert.Equal([0, 1, 2], all.Select(collection => collection.SortOrder).ToArray());
    }

    [Fact]
    public async Task Reorder_IgnoresAnEmptyListAndUnknownIds()
    {
        var a = await Collections.CreateAsync("A", Created);
        var b = await Collections.CreateAsync("B", Created);

        await Collections.ReorderAsync([]);
        await Collections.ReorderAsync([4242, b.CollectionId, a.CollectionId]);

        var all = await Collections.GetAllAsync();
        Assert.Equal(["B", "A"], all.Select(collection => collection.Name).ToArray());
    }

    [Fact]
    public async Task AddGame_IsIdempotentAndKeepsTheCollectionsOwnOrder()
    {
        var collection = await Collections.CreateAsync("Favourites", Created);

        await Collections.AddGameAsync(collection.CollectionId, 570);
        await Collections.AddGameAsync(collection.CollectionId, 292030);
        await Collections.AddGameAsync(collection.CollectionId, 570);

        var stored = await Collections.GetAsync(collection.CollectionId);

        Assert.NotNull(stored);
        Assert.Equal([570, 292030], stored.AppIds.ToArray());
    }

    [Fact]
    public async Task AddGame_DoesNothingForAnUnknownCollection()
    {
        await Collections.AddGameAsync(4242, 570);

        // An orphaned membership row would resurface the moment a collection reused the id.
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM GameCollections"));
    }

    [Fact]
    public async Task RemoveGame_IsANoOpForAGameThatIsNotAMember()
    {
        var collection = await Collections.CreateAsync("Favourites", Created);
        await Collections.AddGameAsync(collection.CollectionId, 570);

        await Collections.RemoveGameAsync(collection.CollectionId, 292030);
        await Collections.RemoveGameAsync(collection.CollectionId, 570);

        var stored = await Collections.GetAsync(collection.CollectionId);
        Assert.Empty(stored?.AppIds ?? []);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForAnUnknownId()
    {
        Assert.Null(await Collections.GetAsync(4242));
    }
}
