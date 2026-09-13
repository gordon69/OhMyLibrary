using Microsoft.Extensions.Logging.Abstractions;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Core.Steam;
using OhMyLibrary.Tests.Fakes;
using OhMyLibrary.Tests.Infrastructure;

namespace OhMyLibrary.Tests.Steam;

/// <summary>
/// <see cref="LibraryAssetResolver"/> against a hand-built <c>appcache/librarycache</c> tree.
/// </summary>
/// <remarks>
/// Both layouts the client uses are covered: assets inside opaque hash subfolders, and the legacy
/// flat files that are still the majority on a real install.
/// </remarks>
public sealed class LibraryAssetResolverTests : IDisposable
{
    private const string HashFolder = "6843027380c3bfd0952449fd9174f492ef2e7b40";
    private const string SecondHashFolder = "06918d1009cc79ffdee853f1ef789cddf8014226";
    private const string IconName = "0bbb630d63262dd66d2fdd0f7d37e8661a410075.jpg";

    private readonly FakeSteam _steam = new("art");
    private readonly FakeSteamPathResolver _paths = new();
    private readonly FakeSteamWatcherService _watcher = new();
    private readonly LibraryAssetResolver _resolver;

    /// <summary>Points the resolver at the throwaway Steam tree.</summary>
    public LibraryAssetResolverTests()
    {
        _paths.SteamPath = _steam.Root;
        _resolver = new LibraryAssetResolver(_paths, _watcher, NullLogger<LibraryAssetResolver>.Instance);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _resolver.Dispose();
        _steam.Dispose();
    }

    [Fact]
    public void Resolve_FindsEveryAssetOfTheHashedLayout()
    {
        var capsule = _steam.WriteLibraryArt(570, Path.Combine(HashFolder, "library_capsule.jpg"));
        var header = _steam.WriteLibraryArt(570, Path.Combine(HashFolder, "library_header.jpg"));
        var hero = _steam.WriteLibraryArt(570, Path.Combine(SecondHashFolder, "library_hero.jpg"));
        var logo = _steam.WriteLibraryArt(570, "logo.png");
        var icon = _steam.WriteLibraryArt(570, IconName);

        var assets = _resolver.Resolve(570);

        Assert.Equal(capsule, assets.CapsulePath);
        Assert.Equal(header, assets.HeaderPath);
        Assert.Equal(hero, assets.HeroPath);
        Assert.Equal(logo, assets.LogoPath);
        Assert.Equal(icon, assets.IconPath);
        Assert.Equal(capsule, assets.BestCover);
        Assert.False(assets.IsEmpty);
    }

    [Fact]
    public void Resolve_AcceptsTheLegacyFlatNames()
    {
        var capsule = _steam.WriteLibraryArt(10, "library_600x900.jpg");
        var header = _steam.WriteLibraryArt(10, "header.jpg");

        var assets = _resolver.Resolve(10);

        Assert.Equal(capsule, assets.CapsulePath);
        Assert.Equal(header, assets.HeaderPath);
        Assert.Equal(capsule, assets.BestCover);
    }

    [Fact]
    public void Resolve_PrefersTheCurrentNamesOverTheLegacyOnes()
    {
        _steam.WriteLibraryArt(100, "library_600x900.jpg");
        _steam.WriteLibraryArt(100, "header.jpg");
        var capsule = _steam.WriteLibraryArt(100, Path.Combine(HashFolder, "library_capsule.jpg"));
        var header = _steam.WriteLibraryArt(100, Path.Combine(HashFolder, "library_header.jpg"));

        var assets = _resolver.Resolve(100);

        Assert.Equal(capsule, assets.CapsulePath);
        Assert.Equal(header, assets.HeaderPath);
    }

    [Fact]
    public void Resolve_FallsBackToTheHeaderWhenThereIsNoCapsule()
    {
        var header = _steam.WriteLibraryArt(220, Path.Combine(HashFolder, "library_header.jpg"));

        var assets = _resolver.Resolve(220);

        Assert.Null(assets.CapsulePath);
        Assert.Equal(header, assets.BestCover);
    }

    [Fact]
    public void Resolve_IgnoresTheBlurredAndLocalisedVariants()
    {
        _steam.WriteLibraryArt(340, Path.Combine(SecondHashFolder, "library_hero_blur.jpg"));
        _steam.WriteLibraryArt(340, Path.Combine(HashFolder, "library_capsule_russian.jpg"));
        _steam.WriteLibraryArt(340, Path.Combine(HashFolder, "markers.svg"));

        var assets = _resolver.Resolve(340);

        Assert.True(assets.IsEmpty);
    }

    [Fact]
    public void Resolve_OnlyTreatsALooseSha1FileAsTheIcon()
    {
        _steam.WriteLibraryArt(440, Path.Combine(HashFolder, IconName));
        _steam.WriteLibraryArt(440, "not-a-sha1.jpg");

        var assets = _resolver.Resolve(440);

        Assert.Null(assets.IconPath);
    }

    [Fact]
    public void Resolve_ReturnsAllNullsForAnAppWithNoCachedArt()
    {
        var assets = _resolver.Resolve(999_999);

        Assert.Equal(999_999, assets.AppId);
        Assert.True(assets.IsEmpty);
        Assert.Null(assets.BestCover);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Resolve_RejectsAnUnusableAppId(int appId)
    {
        Assert.True(_resolver.Resolve(appId).IsEmpty);
        Assert.Null(_resolver.GetCoverUrlFallback(appId));
    }

    [Fact]
    public void Resolve_DoesNotCacheAMissWhileSteamIsStillUnknown()
    {
        _paths.SteamPath = null;
        var capsule = _steam.WriteLibraryArt(570, Path.Combine(HashFolder, "library_capsule.jpg"));

        Assert.True(_resolver.Resolve(570).IsEmpty);

        // Steam turned up after the first call; the art has to appear with it.
        _paths.SteamPath = _steam.Root;
        Assert.Equal(capsule, _resolver.Resolve(570).CapsulePath);
    }

    [Fact]
    public void Invalidate_MakesTheNextResolveRescanTheFolder()
    {
        Assert.True(_resolver.Resolve(570).IsEmpty);

        var capsule = _steam.WriteLibraryArt(570, Path.Combine(HashFolder, "library_capsule.jpg"));
        Assert.True(_resolver.Resolve(570).IsEmpty);

        _resolver.Invalidate(570);
        Assert.Equal(capsule, _resolver.Resolve(570).CapsulePath);
    }

    [Fact]
    public void Invalidate_MakesARescanDistinguishableEvenWhenNothingMoved()
    {
        // Steam usually rewrites an app's art at the path it used before, so the paths a rescan
        // finds are identical to the ones it found last time. A consumer holding a bitmap decoded
        // from the old bytes has to be able to tell that apart from "no rescan happened", and the
        // revision stamp is the only thing that carries it.
        var capsule = _steam.WriteLibraryArt(570, Path.Combine(HashFolder, "library_capsule.jpg"));

        var before = _resolver.Resolve(570);
        Assert.Equal(capsule, before.CapsulePath);
        Assert.Equal(before, _resolver.Resolve(570));

        _resolver.Invalidate(570);
        var after = _resolver.Resolve(570);

        // Same file, same path, and still not the same answer.
        Assert.Equal(before.CapsulePath, after.CapsulePath);
        Assert.NotEqual(before, after);
        Assert.NotEqual(before.Revision, after.Revision);
    }

    [Fact]
    public void Invalidate_LeavesTheRevisionOfAnUntouchedAppAlone()
    {
        // The revision is per resolved set, not a global clock the whole grid re-decodes against.
        _ = _steam.WriteLibraryArt(570, Path.Combine(HashFolder, "library_capsule.jpg"));
        _ = _steam.WriteLibraryArt(10, Path.Combine(HashFolder, "library_capsule.jpg"));

        var otherBefore = _resolver.Resolve(10);
        _ = _resolver.Resolve(570);

        _resolver.Invalidate(570);
        _ = _resolver.Resolve(570);

        Assert.Equal(otherBefore, _resolver.Resolve(10));
    }

    [Fact]
    public void InvalidateAll_DropsEveryCachedResult()
    {
        Assert.True(_resolver.Resolve(570).IsEmpty);
        var capsule = _steam.WriteLibraryArt(570, Path.Combine(HashFolder, "library_capsule.jpg"));

        _resolver.InvalidateAll();

        Assert.Equal(capsule, _resolver.Resolve(570).CapsulePath);
    }

    [Fact]
    public void ALibraryCacheChangeDropsTheArtOfTheAppsItNames()
    {
        // The whole point of C4: without this the resolver serves the path it cached on first paint
        // for the rest of the process, however often Steam rewrites the file behind it.
        Assert.True(_resolver.Resolve(570).IsEmpty);
        Assert.True(_resolver.Resolve(10).IsEmpty);

        var capsule = _steam.WriteLibraryArt(570, Path.Combine(HashFolder, "library_capsule.jpg"));
        var otherCapsule = _steam.WriteLibraryArt(10, Path.Combine(HashFolder, "library_capsule.jpg"));

        _watcher.Raise(SteamFileChangeKind.LibraryCache, 570);

        Assert.Equal(capsule, _resolver.Resolve(570).CapsulePath);
        Assert.True(_resolver.Resolve(10).IsEmpty);

        _watcher.Raise(SteamFileChangeKind.LibraryCache, 10);

        Assert.Equal(otherCapsule, _resolver.Resolve(10).CapsulePath);
    }

    [Fact]
    public void ALibraryCacheChangeWithNoAppIdsDropsEverything()
    {
        // No ids means the watcher lost events, so no cached entry can be trusted.
        Assert.True(_resolver.Resolve(570).IsEmpty);
        var capsule = _steam.WriteLibraryArt(570, Path.Combine(HashFolder, "library_capsule.jpg"));

        _watcher.Raise(SteamFileChangeKind.LibraryCache);

        Assert.Equal(capsule, _resolver.Resolve(570).CapsulePath);
    }

    [Theory]
    [InlineData(SteamFileChangeKind.AppManifest)]
    [InlineData(SteamFileChangeKind.AppInfo)]
    [InlineData(SteamFileChangeKind.LoginUsers)]
    [InlineData(SteamFileChangeKind.LibraryFolders)]
    public void OtherChangeKindsLeaveTheArtCacheAlone(SteamFileChangeKind kind)
    {
        // appinfo.vdf in particular fires constantly and names no app; treating it as an art change
        // would throw the whole cache away many times an hour for changes that are not art.
        var capsule = _steam.WriteLibraryArt(570, Path.Combine(HashFolder, "library_capsule.jpg"));
        Assert.Equal(capsule, _resolver.Resolve(570).CapsulePath);

        File.Delete(capsule);
        _watcher.Raise(kind, 570);

        Assert.Equal(capsule, _resolver.Resolve(570).CapsulePath);
    }

    [Fact]
    public void DisposeUnsubscribesFromTheWatcher()
    {
        Assert.True(_watcher.HasSubscribers);

        _resolver.Dispose();

        Assert.False(_watcher.HasSubscribers);
    }

    [Fact]
    public void GetCoverUrlFallback_PointsAtTheKeylessCdn()
    {
        var url = _resolver.GetCoverUrlFallback(570);

        Assert.NotNull(url);
        Assert.Contains("/570/", url, StringComparison.Ordinal);
        Assert.StartsWith("https://", url, StringComparison.Ordinal);
    }
}
