namespace OhMyLibrary.Core.Models;

/// <summary>
/// One parsed <c>steamapps/appmanifest_&lt;appid&gt;.acf</c>: what Steam believes is on this disk.
/// </summary>
/// <param name="AppId">Steam application id.</param>
/// <param name="Name">Display name as recorded in the manifest.</param>
/// <param name="StateFlags">Valve's install state bitfield.</param>
/// <param name="InstallDir">Folder name under <c>steamapps/common</c>, never a full path.</param>
/// <param name="FullInstallPath">
/// Absolute path to the install directory, resolved against the owning library, or
/// <see langword="null"/> when it could not be resolved (unplugged drive, half-installed app).
/// </param>
/// <param name="ManifestPath">Absolute path of the <c>.acf</c> this was parsed from.</param>
/// <param name="LibraryPath">Absolute path of the library <i>root</i> that owns the manifest.</param>
/// <param name="SizeOnDisk">Bytes currently occupied on disk.</param>
/// <param name="BytesDownloaded">Bytes fetched so far for the pending transfer.</param>
/// <param name="BytesToDownload">Total bytes of the pending transfer, <c>0</c> when idle.</param>
/// <param name="StagingSize">Bytes currently held in the staging folder.</param>
/// <param name="BuildId">Installed build id, kept as text because Valve is inconsistent about its width.</param>
/// <param name="TargetBuildId">Build id Steam wants to reach; differs from <paramref name="BuildId"/> when an update is pending.</param>
/// <param name="LastOwner">SteamID64 of the account that installed the app; <c>0</c> when absent.</param>
/// <param name="LastUpdated">When Steam last updated the app; <see langword="null"/> when the manifest said <c>0</c>.</param>
/// <param name="LastPlayed">When the app was last played; <see langword="null"/> when never.</param>
public sealed record InstalledApp(
    int AppId,
    string Name,
    AppStateFlags StateFlags,
    string InstallDir,
    string? FullInstallPath,
    string ManifestPath,
    string LibraryPath,
    long SizeOnDisk,
    long BytesDownloaded,
    long BytesToDownload,
    long StagingSize,
    string? BuildId,
    string? TargetBuildId,
    ulong LastOwner,
    DateTimeOffset? LastUpdated,
    DateTimeOffset? LastPlayed)
{
    /// <summary>True when Steam is actively transferring or verifying this app.</summary>
    public bool IsBusy => StateFlags.IsBusy();

    /// <summary>True when the content on disk is stale, missing or corrupt.</summary>
    public bool NeedsUpdate => StateFlags.NeedsUpdate();

    /// <summary>
    /// Transfer progress in the range <c>0..1</c>, or <see langword="null"/> when the app is idle
    /// or Steam has not yet published a transfer size.
    /// </summary>
    public double? DownloadProgress =>
        IsBusy && BytesToDownload > 0
            ? Math.Clamp((double)BytesDownloaded / BytesToDownload, 0d, 1d)
            : null;
}
