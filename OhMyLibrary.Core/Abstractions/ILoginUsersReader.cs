using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Reads <c>config/loginusers.vdf</c> so the app can auto-detect the SteamID64 instead of asking
/// the user to type one.
/// </summary>
public interface ILoginUsersReader
{
    /// <summary>
    /// Reads the accounts that have signed in on this machine. Empty when the file is missing or
    /// unparseable.
    /// </summary>
    /// <param name="steamPath">Absolute Steam install root.</param>
    IReadOnlyList<SteamUser> Read(string steamPath);
}
