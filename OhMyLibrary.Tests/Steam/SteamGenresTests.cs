using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Steam;

namespace OhMyLibrary.Tests.Steam;

/// <summary>
/// The built-in genre id to name table. The modern client ships no id-to-name file and the store has
/// no keyless genre endpoint, so this table is the only source and an unknown id must still render.
/// </summary>
public sealed class SteamGenresTests
{
    [Theory]
    [InlineData(1, "Action")]
    [InlineData(2, "Strategy")]
    [InlineData(3, "RPG")]
    [InlineData(4, "Casual")]
    [InlineData(9, "Racing")]
    [InlineData(18, "Sports")]
    [InlineData(23, "Indie")]
    [InlineData(25, "Adventure")]
    [InlineData(28, "Simulation")]
    [InlineData(29, "Massively Multiplayer")]
    // The two entries the since-merged duplicate table disagreed on: docs/steam-formats.md spells
    // 37 "Free To Play", and 60 was missing from the Steam-side table altogether.
    [InlineData(37, "Free To Play")]
    [InlineData(60, "Game Development")]
    public void Resolve_NamesTheGenresValveActuallyUses(int genreId, string expected)
    {
        var genre = SteamGenres.Resolve(genreId);

        Assert.Equal(new GenreRef(genreId, expected), genre);
        Assert.Equal(genreId, genre.GenreId);
        Assert.Equal(expected, genre.Name);
        Assert.True(genre.IsResolved);
    }

    [Theory]
    [InlineData(9999)]
    [InlineData(4242)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Resolve_FallsBackToTheIdPlaceholder(int genreId)
    {
        var genre = SteamGenres.Resolve(genreId);

        Assert.Equal(GenreRef.Unresolved(genreId), genre);
        Assert.Equal($"#{genreId}", genre.Name);
        Assert.False(genre.IsResolved);
    }

    [Fact]
    public void Resolve_KeepsTheAppInfoOrderAndDropsDuplicates()
    {
        // appinfo.vdf lists genres in its own order, which the UI shows as-is.
        var genres = SteamGenres.Resolve([37, 1, 37, 9999]);

        Assert.Equal([37, 1, 9999], genres.Select(genre => genre.GenreId).ToArray());
        Assert.Equal("#9999", genres[2].Name);
    }

    [Fact]
    public void Names_AgreesWithResolve()
    {
        foreach (var (id, name) in SteamGenres.Names)
        {
            Assert.Equal(new GenreRef(id, name), SteamGenres.Resolve(id));
        }
    }

    /// <summary><c>TagService</c> seeds the database from <see cref="SteamGenres.All"/>.</summary>
    [Fact]
    public void All_IsAConsistentNonEmptyTable()
    {
        var all = SteamGenres.All;

        Assert.NotEmpty(all);
        Assert.Equal(all.Select(genre => genre.GenreId).Distinct().Count(), all.Count);
        Assert.All(all, genre => Assert.True(genre.IsResolved));
        Assert.All(all, genre => Assert.Equal(genre, SteamGenres.Resolve(genre.GenreId)));
    }

    [Fact]
    public void All_CoversExactlyTheNamedGenres()
    {
        Assert.Equal(
            SteamGenres.Names.Select(entry => new GenreRef(entry.Key, entry.Value)).OrderBy(genre => genre.GenreId),
            SteamGenres.All.OrderBy(genre => genre.GenreId));
    }
}
