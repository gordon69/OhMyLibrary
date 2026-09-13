using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Tests.Models;

/// <summary>
/// The truth table of <see cref="AppStateFlagsExtensions"/>. These three predicates decide whether a
/// card shows Play, Update or a progress bar, so every interesting bit combination is pinned down.
/// </summary>
public sealed class AppStateFlagsTests
{
    [Theory]
    [InlineData(0, false)]                    // nothing known
    [InlineData(1, false)]                    // Uninstalled
    [InlineData(2, false)]                    // UpdateRequired alone, not installed
    [InlineData(4, true)]                     // the healthy installed app
    [InlineData(6, true)]                     // installed, update pending
    [InlineData(4 | 1, false)]                // FullyInstalled and Uninstalled together is not installed
    [InlineData(4 | 64, true)]                // installed and running
    [InlineData(4 | 1048576, true)]           // installed and downloading an update
    public void IsFullyInstalled_MatchesValvesBitfield(int flags, bool expected)
    {
        Assert.Equal(expected, ((AppStateFlags)flags).IsFullyInstalled());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]                    // healthy
    [InlineData(2, true)]                     // UpdateRequired
    [InlineData(6, true)]                     // installed, update required
    [InlineData(4 | 32, true)]                // FilesMissing
    [InlineData(4 | 128, true)]               // FilesCorrupt
    [InlineData(4 | 16, false)]               // Locked is not a content problem
    public void NeedsUpdate_CoversTheContentProblemBits(int flags, bool expected)
    {
        Assert.Equal(expected, ((AppStateFlags)flags).NeedsUpdate());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(6, false)]                    // an update is required but Steam is not working on it yet
    [InlineData(4 | 256, true)]               // UpdateRunning
    [InlineData(4 | 512, true)]               // UpdatePaused
    [InlineData(4 | 1024, true)]              // UpdateStarted
    [InlineData(2048, true)]                  // Uninstalling
    [InlineData(4 | 4096, true)]              // BackupRunning
    [InlineData(4 | 65536, true)]             // Reconfiguring
    [InlineData(4 | 131072, true)]            // Validating
    [InlineData(4 | 262144, true)]            // AddingFiles
    [InlineData(4 | 524288, true)]            // Preallocating
    [InlineData(4 | 1048576, true)]           // Downloading
    [InlineData(4 | 2097152, true)]           // Staging
    [InlineData(4 | 4194304, true)]           // Committing
    [InlineData(4 | 8388608, true)]           // UpdateStopping
    [InlineData(4 | 64, false)]               // AppRunning is the user's doing, not Steam's
    [InlineData(4 | 32, false)]               // FilesMissing needs an update, but nothing is in flight
    public void IsBusy_CoversEveryBitInValvesWorkingRange(int flags, bool expected)
    {
        Assert.Equal(expected, ((AppStateFlags)flags).IsBusy());
    }

    [Fact]
    public void TheRealWorldDownloadingFlagIsInstalledUpdatingAndBusyAtOnce()
    {
        // 1049606 = FullyInstalled | UpdateRequired | UpdateStarted | Downloading, which is what a
        // manifest looks like while Steam is patching a game that is already on disk.
        var flags = (AppStateFlags)1_049_606;

        Assert.True(flags.IsFullyInstalled());
        Assert.True(flags.NeedsUpdate());
        Assert.True(flags.IsBusy());
    }

    [Fact]
    public void MasksListEveryBitTheyClaimTo()
    {
        Assert.Equal(
            AppStateFlags.UpdateRequired | AppStateFlags.FilesMissing | AppStateFlags.FilesCorrupt,
            AppStateFlagsExtensions.NeedsUpdateMask);

        Assert.False(AppStateFlagsExtensions.BusyMask.HasFlag(AppStateFlags.AppRunning));
        Assert.False(AppStateFlagsExtensions.BusyMask.HasFlag(AppStateFlags.FullyInstalled));
        Assert.True(AppStateFlagsExtensions.BusyMask.HasFlag(AppStateFlags.Downloading));
    }

    [Fact]
    public void DownloadProgressIsOnlyReportedWhileATransferIsInFlight()
    {
        var idle = Installed(AppStateFlags.FullyInstalled, downloaded: 10, total: 20);
        var busy = Installed(AppStateFlags.FullyInstalled | AppStateFlags.Downloading, downloaded: 10, total: 20);
        var busyWithoutASize = Installed(AppStateFlags.FullyInstalled | AppStateFlags.Downloading, downloaded: 10, total: 0);

        Assert.Null(idle.DownloadProgress);
        Assert.Equal(0.5d, busy.DownloadProgress);
        Assert.Null(busyWithoutASize.DownloadProgress);
    }

    private static InstalledApp Installed(AppStateFlags flags, long downloaded, long total) =>
        new(
            AppId: 1,
            Name: "Fixture",
            StateFlags: flags,
            InstallDir: "Fixture",
            FullInstallPath: null,
            ManifestPath: "manifest.acf",
            LibraryPath: "library",
            SizeOnDisk: 0,
            BytesDownloaded: downloaded,
            BytesToDownload: total,
            StagingSize: 0,
            BuildId: null,
            TargetBuildId: null,
            LastOwner: 0,
            LastUpdated: null,
            LastPlayed: null);
}
