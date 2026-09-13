namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Hands <c>steam://</c> URIs to the shell so the Steam client performs the action.
/// </summary>
/// <remarks>
/// Every member returns <see langword="false"/> instead of throwing when the shell refuses, Steam
/// is not installed, or the app id is not a positive integer. App ids are validated before being
/// concatenated into a URI even though they come from a trusted local parse.
/// </remarks>
public interface ISteamUriLauncher
{
    /// <summary>Launches the app via <c>steam://rungameid</c>, installing it first if needed.</summary>
    /// <param name="appId">Steam application id.</param>
    bool LaunchGame(int appId);

    /// <summary>Starts or resumes an install via <c>steam://install</c>.</summary>
    /// <param name="appId">Steam application id.</param>
    bool Install(int appId);

    /// <summary>Opens Steam's uninstall prompt via <c>steam://uninstall</c>.</summary>
    /// <param name="appId">Steam application id.</param>
    bool Uninstall(int appId);

    /// <summary>Starts an integrity check via <c>steam://validate</c>.</summary>
    /// <param name="appId">Steam application id.</param>
    bool Validate(int appId);

    /// <summary>Opens the store page via <c>steam://store</c>.</summary>
    /// <param name="appId">Steam application id.</param>
    bool OpenStorePage(int appId);

    /// <summary>Opens the app's page in the Steam library via <c>steam://nav/games/details</c>.</summary>
    /// <param name="appId">Steam application id.</param>
    bool OpenLibraryPage(int appId);

    /// <summary>Opens a friend's profile via <c>steam://friends/add</c>.</summary>
    /// <param name="steamId64">The friend's 64-bit Steam id.</param>
    bool OpenFriendProfile(ulong steamId64);

    /// <summary>
    /// Opens an arbitrary <c>steam://</c> URI. Returns <see langword="false"/> for anything that is
    /// not a well-formed <c>steam://</c> URI.
    /// </summary>
    /// <param name="steamUri">The URI to open.</param>
    bool OpenUri(string steamUri);
}
