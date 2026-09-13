namespace OhMyLibrary.Core.Models;

/// <summary>
/// Why the library looks the way it does. Every degraded state Steam can put us in is a normal
/// state here, so the UI can explain itself instead of showing an unexplained empty grid.
/// </summary>
/// <param name="SteamFound">Whether a Steam installation was located at all.</param>
/// <param name="SteamPath">Normalised absolute Steam path, when found.</param>
/// <param name="LibraryFolderCount">Library folders declared in <c>libraryfolders.vdf</c>.</param>
/// <param name="MissingLibraryFolderCount">
/// How many of those were unreachable — an unplugged or unmounted drive. Their games are simply absent.
/// </param>
/// <param name="ApiKeyConfigured">Whether a Steam Web API key is available.</param>
/// <param name="OwnedListAvailable">
/// Whether the owned-games list was actually readable. <see langword="false"/> covers both a missing
/// key and a private profile, so owned-but-not-installed games stay hidden while installed games still work.
/// </param>
/// <param name="SteamId64">The account we are showing the library for, when known.</param>
/// <param name="LastError">Human-readable description of the last non-fatal failure, for the status bar.</param>
public sealed record LibraryStatus(
    bool SteamFound,
    string? SteamPath,
    int LibraryFolderCount,
    int MissingLibraryFolderCount,
    bool ApiKeyConfigured,
    bool OwnedListAvailable,
    ulong? SteamId64,
    string? LastError);
