namespace OhMyLibrary.Core.Models;

/// <summary>
/// Valve's <c>StateFlags</c> bitfield as it appears in <c>steamapps/appmanifest_&lt;appid&gt;.acf</c>.
/// </summary>
/// <remarks>
/// <c>4</c> alone is a healthy installed app; <c>6</c> is installed with an update pending.
/// Any bit in the <c>256..8388608</c> range means the Steam client is actively working on the app.
/// </remarks>
[Flags]
public enum AppStateFlags
{
    /// <summary>No state reported (or the manifest could not be parsed).</summary>
    None = 0,

    /// <summary>The app is known to the client but not installed.</summary>
    Uninstalled = 1,

    /// <summary>An update is available and must be applied before the app can run.</summary>
    UpdateRequired = 2,

    /// <summary>All content is present and verified.</summary>
    FullyInstalled = 4,

    /// <summary>Content on disk is still encrypted (pre-load).</summary>
    Encrypted = 8,

    /// <summary>The app is locked by the client and cannot be started.</summary>
    Locked = 16,

    /// <summary>Files that the manifest expects are missing from disk.</summary>
    FilesMissing = 32,

    /// <summary>The app is currently running.</summary>
    AppRunning = 64,

    /// <summary>Validation found corrupt files.</summary>
    FilesCorrupt = 128,

    /// <summary>An update is being applied.</summary>
    UpdateRunning = 256,

    /// <summary>An update was started and is currently paused.</summary>
    UpdatePaused = 512,

    /// <summary>An update has been queued and started.</summary>
    UpdateStarted = 1024,

    /// <summary>The app is being uninstalled.</summary>
    Uninstalling = 2048,

    /// <summary>A backup job is running for the app.</summary>
    BackupRunning = 4096,

    /// <summary>The install is being reconfigured (depot/language change).</summary>
    Reconfiguring = 65536,

    /// <summary>Files are being verified against the manifest.</summary>
    Validating = 131072,

    /// <summary>New files are being written.</summary>
    AddingFiles = 262144,

    /// <summary>Disk space is being pre-allocated.</summary>
    Preallocating = 524288,

    /// <summary>Content is downloading.</summary>
    Downloading = 1048576,

    /// <summary>Downloaded content is being staged.</summary>
    Staging = 2097152,

    /// <summary>Staged content is being committed into the install directory.</summary>
    Committing = 4194304,

    /// <summary>An update is being cancelled.</summary>
    UpdateStopping = 8388608,
}

/// <summary>Convenience predicates over <see cref="AppStateFlags"/>.</summary>
public static class AppStateFlagsExtensions
{
    /// <summary>Every bit that means "the Steam client is currently working on this app".</summary>
    public const AppStateFlags BusyMask =
        AppStateFlags.UpdateRunning
        | AppStateFlags.UpdatePaused
        | AppStateFlags.UpdateStarted
        | AppStateFlags.Uninstalling
        | AppStateFlags.BackupRunning
        | AppStateFlags.Reconfiguring
        | AppStateFlags.Validating
        | AppStateFlags.AddingFiles
        | AppStateFlags.Preallocating
        | AppStateFlags.Downloading
        | AppStateFlags.Staging
        | AppStateFlags.Committing
        | AppStateFlags.UpdateStopping;

    /// <summary>Every bit that means the installed content cannot be trusted as-is.</summary>
    public const AppStateFlags NeedsUpdateMask =
        AppStateFlags.UpdateRequired | AppStateFlags.FilesMissing | AppStateFlags.FilesCorrupt;

    /// <summary>
    /// True when the app is fully installed and not simultaneously marked uninstalled.
    /// </summary>
    public static bool IsFullyInstalled(this AppStateFlags flags) =>
        (flags & AppStateFlags.FullyInstalled) != 0 && (flags & AppStateFlags.Uninstalled) == 0;

    /// <summary>
    /// True when an update is required or the files on disk are missing or corrupt.
    /// </summary>
    public static bool NeedsUpdate(this AppStateFlags flags) => (flags & NeedsUpdateMask) != 0;

    /// <summary>
    /// True while Steam is downloading, staging, validating, uninstalling or otherwise
    /// mutating the app, i.e. the UI should show progress instead of a Play button.
    /// </summary>
    public static bool IsBusy(this AppStateFlags flags) => (flags & BusyMask) != 0;
}
