namespace OhMyLibrary.Core.Models;

/// <summary>
/// The merged library row the UI binds to: the union of owned and installed apps, enriched with
/// metadata, art, friend ownership and collection membership.
/// </summary>
/// <remarks>
/// An entry exists when the app is owned, installed, or both. An installed-but-not-owned entry is
/// normal (family sharing, a second account on the machine, a tool), and so is an owned-but-not-installed
/// one. This type is deliberately free of WPF types so that <c>OhMyLibrary.Core</c> stays UI-agnostic.
/// </remarks>
/// <param name="AppId">Steam application id.</param>
/// <param name="Name">Best available display name.</param>
/// <param name="IsOwned">Whether the signed-in account's owned-games list contains the app.</param>
/// <param name="IsInstalled">Whether a manifest for the app was found in a reachable library.</param>
/// <param name="StateFlags">Install state from the manifest; <see cref="AppStateFlags.None"/> when not installed.</param>
/// <param name="InstallDir">Folder name under <c>steamapps/common</c>, never a full path.</param>
/// <param name="FullInstallPath">Absolute install path when it could be resolved.</param>
/// <param name="SizeBytes">Size on disk in bytes; <c>0</c> when not installed.</param>
/// <param name="BuildId">Installed build id.</param>
/// <param name="PlaytimeForeverMinutes">Lifetime playtime in minutes; <c>0</c> when unknown.</param>
/// <param name="LastPlayed">Most recent of the local manifest and Web API timestamps.</param>
/// <param name="Genres">Resolved genres, in <c>appinfo.vdf</c> order.</param>
/// <param name="Tags">Resolved store tags, most relevant first.</param>
/// <param name="Assets">Resolved local art, or <see langword="null"/> when it has not been looked up yet.</param>
/// <param name="AppType">Raw <c>common/type</c> string; compare case-insensitively.</param>
/// <param name="FriendOwnerIds">SteamID64s of friends known to own the app.</param>
/// <param name="CollectionIds">Ids of the user's collections this app belongs to.</param>
/// <param name="LastLocalScanUtc">When the local manifest data behind this entry was last refreshed.</param>
public sealed record GameEntry(
    int AppId,
    string Name,
    bool IsOwned,
    bool IsInstalled,
    AppStateFlags StateFlags,
    string? InstallDir,
    string? FullInstallPath,
    long SizeBytes,
    string? BuildId,
    int PlaytimeForeverMinutes,
    DateTimeOffset? LastPlayed,
    IReadOnlyList<GenreRef> Genres,
    IReadOnlyList<TagRef> Tags,
    GameAssets? Assets,
    string? AppType,
    IReadOnlyList<ulong> FriendOwnerIds,
    IReadOnlyList<long> CollectionIds,
    DateTimeOffset? LastLocalScanUtc)
{
    /// <summary>
    /// Bytes fetched so far for a transfer in flight. Live local-scan data only: it is not persisted,
    /// so entries read back from the database report <c>0</c> and therefore no progress.
    /// </summary>
    public long BytesDownloaded { get; init; }

    /// <summary>
    /// Total bytes of a transfer in flight. Live local-scan data only, like <see cref="BytesDownloaded"/>.
    /// </summary>
    public long BytesToDownload { get; init; }

    /// <summary>True when the content on disk is stale, missing or corrupt.</summary>
    public bool NeedsUpdate => StateFlags.NeedsUpdate();

    /// <summary>True while Steam is downloading, staging, validating or uninstalling the app.</summary>
    public bool IsBusy => StateFlags.IsBusy();

    /// <summary>
    /// Transfer progress in the range <c>0..1</c>, or <see langword="null"/> when the app is idle or
    /// no transfer size is known.
    /// </summary>
    public double? DownloadProgress =>
        IsBusy && BytesToDownload > 0
            ? Math.Clamp((double)BytesDownloaded / BytesToDownload, 0d, 1d)
            : null;
}
