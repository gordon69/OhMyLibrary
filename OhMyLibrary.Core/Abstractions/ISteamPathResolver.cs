using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Locates the Steam installation and the things that hang off it.
/// </summary>
/// <remarks>
/// No member throws. "Steam is not installed" and "the library drive is unplugged" are normal
/// states that surface as <see langword="null"/> or an empty list.
/// </remarks>
public interface ISteamPathResolver
{
    /// <summary>
    /// The Steam install root, normalised to a full Windows path, or <see langword="null"/> when
    /// Steam is not installed on this machine.
    /// </summary>
    string? FindSteamPath();

    /// <summary>
    /// Absolute path of <c>steam.exe</c>, or <see langword="null"/> when it cannot be located.
    /// </summary>
    string? FindSteamExecutable();

    /// <summary>
    /// Every library folder declared by <c>libraryfolders.vdf</c> that actually exists.
    /// Unreachable drives are skipped rather than reported as errors.
    /// </summary>
    IReadOnlyList<SteamLibraryFolder> GetLibraryFolders();

    /// <summary>
    /// Accounts that have signed in on this machine, newest sign-in first. Empty when
    /// <c>config/loginusers.vdf</c> is missing or unreadable.
    /// </summary>
    IReadOnlyList<SteamUser> GetLocalUsers();
}
