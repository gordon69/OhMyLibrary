using OhMyLibrary.Core.Vdf;
using OhMyLibrary.Tests.Infrastructure;

namespace OhMyLibrary.Tests.Vdf;

/// <summary>
/// <see cref="LibraryFoldersReader"/> against the real <c>libraryfolders.vdf</c> shape, both of the
/// paths Steam writes it to, and the degraded cases.
/// </summary>
/// <remarks>
/// The implementation deliberately deviates from its interface documentation: it drops folders whose
/// directory is gone and always includes the Steam folder itself. These tests pin the behaviour that
/// actually ships, because callers must not re-filter for existence.
/// </remarks>
public sealed class LibraryFoldersReaderTests : IDisposable
{
    private readonly FakeSteam _steam = new("libfolders");
    private readonly LibraryFoldersReader _reader = new();

    /// <inheritdoc />
    public void Dispose() => _steam.Dispose();

    [Fact]
    public void Read_ParsesTheCanonicalSteamAppsFile()
    {
        var (libraryA, libraryB) = WriteRealShapedFile(LibraryFoldersLocation.SteamApps);

        var folders = _reader.Read(_steam.Root);

        Assert.Equal(3, folders.Count);
        Assert.Equal(
            new[] { _steam.Root, libraryA, libraryB }.Order(StringComparer.OrdinalIgnoreCase),
            folders.Select(folder => folder.Path).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_UnescapesThePathsInsteadOfDoublingTheBackslashes()
    {
        var (libraryA, _) = WriteRealShapedFile(LibraryFoldersLocation.SteamApps);

        var folders = _reader.Read(_steam.Root);

        // Steam writes "A:\\SteamLibrary"; without HasEscapeSequences the parser hands back the
        // doubled form and every path below it is wrong.
        var folder = Assert.Single(folders, f => string.Equals(f.Path, libraryA, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(@"\\", folder.Path.TrimStart('\\'), StringComparison.Ordinal);
        Assert.True(Directory.Exists(folder.Path));
    }

    [Fact]
    public void Read_ExposesTheAppsIndexAsAHint()
    {
        var (libraryA, _) = WriteRealShapedFile(LibraryFoldersLocation.SteamApps);

        var folders = _reader.Read(_steam.Root);

        var folder = Assert.Single(folders, f => string.Equals(f.Path, libraryA, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(7, folder.Apps.Count);
        Assert.Equal(77_246_578_977L, folder.Apps[570]);
        Assert.Equal(98_765_432_100L, folder.Apps[9_000_404]);
        Assert.Equal(2_048_390_066_176L, folder.TotalSize);

        // The library root contains steamapps rather than being it.
        Assert.Equal(Path.Combine(libraryA, "steamapps"), folder.SteamAppsPath);
        Assert.Equal(Path.Combine(libraryA, "steamapps", "common"), folder.CommonPath);
    }

    [Fact]
    public void Read_FallsBackToTheConfigLocation()
    {
        var libraryB = _steam.AddLibrary("SecondLibrary");
        _steam.WriteLibraryFolders(
            "libraryfolders_config.vdf",
            new Dictionary<string, string>
            {
                ["__LIB1__"] = _steam.AddLibrary("ScalarLibrary"),
                ["__LIB2__"] = libraryB,
                ["__MISSING__"] = _steam.UnreachablePath,
            },
            LibraryFoldersLocation.Config);

        var folders = _reader.Read(_steam.Root);

        // The scalar "1" entry is not a folder block and the unplugged drive does not exist, so the
        // Steam root and the one real folder are all that survive.
        Assert.Equal(2, folders.Count);
        Assert.Contains(folders, folder => string.Equals(folder.Path, _steam.Root, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(folders, folder => string.Equals(folder.Path, libraryB, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_PrefersTheSteamAppsFileOverTheConfigOne()
    {
        var canonical = _steam.AddLibrary("CanonicalLibrary");
        var legacy = _steam.AddLibrary("LegacyLibrary");

        _steam.WriteLibraryFolders(
            "libraryfolders_config.vdf",
            new Dictionary<string, string>
            {
                ["__LIB1__"] = legacy,
                ["__LIB2__"] = legacy,
                ["__MISSING__"] = _steam.UnreachablePath,
            },
            LibraryFoldersLocation.Config);

        _steam.WriteLibraryFolders(
            "libraryfolders_config.vdf",
            new Dictionary<string, string>
            {
                ["__LIB1__"] = canonical,
                ["__LIB2__"] = canonical,
                ["__MISSING__"] = _steam.UnreachablePath,
            },
            LibraryFoldersLocation.SteamApps);

        var folders = _reader.Read(_steam.Root);

        Assert.Contains(folders, folder => string.Equals(folder.Path, canonical, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(folders, folder => string.Equals(folder.Path, legacy, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_SkipsAFolderWhoseDriveIsNotMounted()
    {
        var libraryA = _steam.AddLibrary("SteamLibrary");
        _steam.WriteLibraryFolders(
            "libraryfolders.vdf",
            new Dictionary<string, string>
            {
                ["__LIB0__"] = _steam.Root,
                ["__LIB1__"] = libraryA,
                ["__LIB2__"] = _steam.UnreachablePath,
            });

        var folders = _reader.Read(_steam.Root);

        Assert.Equal(2, folders.Count);
        Assert.DoesNotContain(folders, folder => folder.Path.Contains("Unplugged", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_StillReportsTheSteamFolderWhenTheFileIsMissing()
    {
        var folders = _reader.Read(_steam.Root);

        // A client that has never had a second library has no file at all, and its games still exist.
        var folder = Assert.Single(folders);
        Assert.Equal(_steam.Root, folder.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(folder.Apps);
    }

    [Theory]
    [InlineData("libraryfolders_garbage.vdf")]
    [InlineData("libraryfolders_truncated.vdf")]
    public void Read_StillReportsTheSteamFolderWhenTheFileIsUnparseable(string fixtureName)
    {
        _steam.WriteLibraryFolders(
            fixtureName,
            new Dictionary<string, string> { ["__LIB1__"] = _steam.Root });

        var folders = _reader.Read(_steam.Root);

        var folder = Assert.Single(folders);
        Assert.Equal(_steam.Root, folder.Path, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_ReturnsNothingWhenThereIsNoSteamFolderAtAll()
    {
        Assert.Empty(_reader.Read(_steam.UnreachablePath));
        Assert.Empty(_reader.Read(string.Empty));
    }

    [Fact]
    public void Read_DoesNotReportTheSameFolderTwice()
    {
        var libraryA = _steam.AddLibrary("SteamLibrary");
        _steam.WriteLibraryFolders(
            "libraryfolders.vdf",
            new Dictionary<string, string>
            {
                ["__LIB0__"] = _steam.Root,
                ["__LIB1__"] = libraryA,
                // The same folder again, written the way a hand-edited file might carry it.
                ["__LIB2__"] = libraryA + Path.DirectorySeparatorChar,
            });

        var folders = _reader.Read(_steam.Root);

        Assert.Equal(2, folders.Count);
    }

    private (string LibraryA, string LibraryB) WriteRealShapedFile(LibraryFoldersLocation location)
    {
        var libraryA = _steam.AddLibrary("SteamLibrary");
        var libraryB = _steam.AddLibrary("ThirdLibrary");

        _steam.WriteLibraryFolders(
            "libraryfolders.vdf",
            new Dictionary<string, string>
            {
                ["__LIB0__"] = _steam.Root,
                ["__LIB1__"] = libraryA,
                ["__LIB2__"] = libraryB,
            },
            location);

        return (libraryA, libraryB);
    }
}
