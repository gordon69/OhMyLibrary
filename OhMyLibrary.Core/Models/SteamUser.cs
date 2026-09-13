namespace OhMyLibrary.Core.Models;

/// <summary>
/// One account from <c>config/loginusers.vdf</c>: someone who has signed in on this machine.
/// </summary>
/// <param name="SteamId64">The 64-bit Steam id.</param>
/// <param name="AccountName">Login name.</param>
/// <param name="PersonaName">Display name.</param>
/// <param name="MostRecent">Whether this is the account the client last signed in as.</param>
/// <param name="Timestamp">When that sign-in happened; <see langword="null"/> when absent or zero.</param>
public sealed record SteamUser(
    ulong SteamId64,
    string AccountName,
    string PersonaName,
    bool MostRecent,
    DateTimeOffset? Timestamp)
{
    /// <summary>
    /// The 32-bit account id used for <c>userdata/&lt;accountId&gt;</c> folder names.
    /// </summary>
    public uint AccountId => (uint)(SteamId64 & 0xFFFFFFFF);
}
