using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Vdf;
using OhMyLibrary.Tests.Infrastructure;

namespace OhMyLibrary.Tests.Vdf;

/// <summary>
/// Field mapping and failure behaviour of <see cref="AcfReader"/> against real
/// <c>appmanifest_*.acf</c> files taken from a live client, plus hand-authored variants for the
/// states that install rarely reaches on a healthy machine.
/// </summary>
public sealed class AcfReaderTests : IDisposable
{
    private readonly FakeSteam _steam = new("acf");
    private readonly AcfReader _reader = new();
    private readonly string _library;

    /// <summary>Creates a library folder to install the fixtures into.</summary>
    public AcfReaderTests() => _library = _steam.AddLibrary("SteamLibrary");

    /// <inheritdoc />
    public void Dispose() => _steam.Dispose();

    [Fact]
    public void Read_MapsEveryFieldOfARealManifest()
    {
        var manifest = _steam.InstallManifest(_library, "appmanifest_570.acf");

        var app = _reader.Read(manifest, _library);

        Assert.NotNull(app);
        Assert.Equal(570, app.AppId);
        Assert.Equal("Dota 2", app.Name);
        Assert.Equal(AppStateFlags.FullyInstalled, app.StateFlags);
        Assert.Equal("dota 2 beta", app.InstallDir);
        Assert.Equal(77_246_578_977L, app.SizeOnDisk);
        Assert.Equal("25219194", app.BuildId);
        Assert.Equal("0", app.TargetBuildId);
        Assert.Equal(76_561_198_000_000_001UL, app.LastOwner);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_789_026_229), app.LastUpdated);
        Assert.Equal(0L, app.BytesToDownload);
        Assert.Equal(0L, app.StagingSize);
        Assert.Equal(Path.Combine(_library, "steamapps", "common", "dota 2 beta"), app.FullInstallPath);
        Assert.Equal(manifest, app.ManifestPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_library, app.LibraryPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_TreatsAZeroTimestampAsNever()
    {
        var manifest = _steam.InstallManifest(_library, "appmanifest_570.acf");

        var app = _reader.Read(manifest, _library);

        // "LastPlayed" "0" in the manifest means the app has never been played.
        Assert.NotNull(app);
        Assert.Null(app.LastPlayed);
    }

    [Fact]
    public void Read_KeepsANonZeroLastPlayed()
    {
        var manifest = _steam.InstallManifest(_library, "appmanifest_1091500.acf");

        var app = _reader.Read(manifest, _library);

        Assert.NotNull(app);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_786_388_287), app.LastPlayed);
    }

    [Fact]
    public void Read_DecodesAnUpdateRequiredStateFlag()
    {
        var manifest = _steam.InstallManifest(_library, "appmanifest_900001.acf");

        var app = _reader.Read(manifest, _library);

        Assert.NotNull(app);
        Assert.Equal(AppStateFlags.FullyInstalled | AppStateFlags.UpdateRequired, app.StateFlags);
        Assert.True(app.NeedsUpdate);
        Assert.False(app.IsBusy);
        Assert.Equal("1000", app.BuildId);
        Assert.Equal("2000", app.TargetBuildId);
    }

    [Fact]
    public void Read_DecodesABusyStateFlagAndItsTransferCounters()
    {
        var manifest = _steam.InstallManifest(_library, "appmanifest_900002.acf");

        var app = _reader.Read(manifest, _library);

        Assert.NotNull(app);
        Assert.True(app.IsBusy);
        Assert.True(app.StateFlags.HasFlag(AppStateFlags.Downloading));
        Assert.True(app.StateFlags.HasFlag(AppStateFlags.UpdateStarted));
        Assert.Equal(1_000_000L, app.BytesDownloaded);
        Assert.Equal(4_000_000L, app.BytesToDownload);
        Assert.Equal(250_000L, app.StagingSize);
        Assert.Equal(0.25d, app.DownloadProgress);
    }

    [Fact]
    public void Read_ReportsNoInstallPathWhenTheFolderIsNotOnDisk()
    {
        var manifest = _steam.InstallManifest(_library, "appmanifest_900003.acf", createInstallDirectory: false);

        var app = _reader.Read(manifest, _library);

        // A stale manifest, an interrupted install or an unmounted drive: the folder name survives,
        // the resolved path does not, and the app must not be reported as launchable from disk.
        Assert.NotNull(app);
        Assert.Equal("Fixture Never Installed", app.InstallDir);
        Assert.Null(app.FullInstallPath);
    }

    [Fact]
    public void Read_FallsBackToTheInstallDirectoryWhenTheManifestHasNoName()
    {
        var manifest = _steam.InstallManifest(_library, "appmanifest_900005.acf");

        var app = _reader.Read(manifest, _library);

        Assert.NotNull(app);
        Assert.Equal("Fixture Minimal", app.Name);
        Assert.Equal(AppStateFlags.None, app.StateFlags);
        Assert.Equal(0UL, app.LastOwner);
        Assert.Null(app.LastUpdated);
    }

    [Fact]
    public void Read_ReturnsNullForAMissingFile()
    {
        var app = _reader.Read(Path.Combine(_library, "steamapps", "appmanifest_404.acf"), _library);

        Assert.Null(app);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_ReturnsNullForAnEmptyPath(string path)
    {
        Assert.Null(_reader.Read(path, _library));
    }

    [Fact]
    public void Read_ReturnsNullForAGarbageFileInsteadOfThrowing()
    {
        var manifest = _steam.InstallManifest(_library, "appmanifest_900004.acf");

        var app = _reader.Read(manifest, _library);

        Assert.Null(app);
    }

    [Fact]
    public void ReadLibrary_ParsesEveryManifestAndSkipsTheUnparseableOne()
    {
        _steam.InstallManifest(_library, "appmanifest_570.acf");
        _steam.InstallManifest(_library, "appmanifest_1091500.acf");
        _steam.InstallManifest(_library, "appmanifest_900004.acf");

        var apps = _reader.ReadLibrary(new SteamLibraryFolder(_library, string.Empty, 0, new Dictionary<int, long>()));

        Assert.Equal([570, 1091500], apps.Select(app => app.AppId).Order());
    }

    [Fact]
    public void ReadLibrary_ReturnsNothingForAnUnreachableFolder()
    {
        var folder = new SteamLibraryFolder(_steam.UnreachablePath, string.Empty, 0, new Dictionary<int, long>());

        Assert.Empty(_reader.ReadLibrary(folder));
    }

    [Fact]
    public void ReadAll_MergesLibrariesAndLetsTheLastOneWin()
    {
        var second = _steam.AddLibrary("SecondLibrary");
        _steam.InstallManifest(_library, "appmanifest_570.acf");
        _steam.InstallManifest(_library, "appmanifest_1091500.acf");
        _steam.InstallManifest(second, "appmanifest_570.acf");

        var apps = _reader.ReadAll([
            new SteamLibraryFolder(_library, string.Empty, 0, new Dictionary<int, long>()),
            new SteamLibraryFolder(second, string.Empty, 0, new Dictionary<int, long>()),
        ]);

        Assert.Equal(2, apps.Count);

        // After a library move both folders can hold a manifest for the same app; the later folder wins.
        var dota = Assert.Single(apps, app => app.AppId == 570);
        Assert.Equal(second, dota.LibraryPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAll_StopsPartWayThroughWhenTheTokenIsCancelledMidWalk()
    {
        // A scan is thousands of file reads on a large install, and wrapping it in Task.Run only ever
        // stopped it from starting. The token has to be observed <i>inside</i> the walk, between
        // units of work, which is what this cancels into: the folders arrive lazily and the second
        // one is asked for only once the first has been read.
        var second = _steam.AddLibrary("SecondLibrary");
        _steam.InstallManifest(_library, "appmanifest_570.acf");
        _steam.InstallManifest(second, "appmanifest_1091500.acf");

        using var cts = new CancellationTokenSource();

        _ = Assert.ThrowsAny<OperationCanceledException>(
            () => _reader.ReadAll(CancelAfterFirst(cts, _library, second), cts.Token));
    }

    [Fact]
    public void ReadLibrary_ThrowsRatherThanReturningAPartialList()
    {
        // Cancelling must not look like "this folder holds nothing installed" — a caller that wrote
        // that answer to the database would uninstall the user's whole library on the way out.
        _steam.InstallManifest(_library, "appmanifest_570.acf");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _ = Assert.ThrowsAny<OperationCanceledException>(
            () => _reader.ReadLibrary(Folder(_library), cts.Token));
    }

    private static SteamLibraryFolder Folder(string path) =>
        new(path, string.Empty, 0, new Dictionary<int, long>());

    /// <summary>
    /// Yields the folders one at a time, cancelling once the reader comes back for the second — the
    /// moment the walk is genuinely under way rather than about to begin.
    /// </summary>
    private static IEnumerable<SteamLibraryFolder> CancelAfterFirst(
        CancellationTokenSource cts,
        params string[] paths)
    {
        for (var i = 0; i < paths.Length; i++)
        {
            if (i > 0)
            {
                cts.Cancel();
            }

            yield return Folder(paths[i]);
        }
    }
}
