namespace OhMyLibrary.Core.Models;

/// <summary>Valve's <c>personastate</c> values from <c>ISteamUser/GetPlayerSummaries</c>.</summary>
public enum PersonaState
{
    /// <summary>Offline, or the profile is private.</summary>
    Offline = 0,

    /// <summary>Online.</summary>
    Online = 1,

    /// <summary>Busy.</summary>
    Busy = 2,

    /// <summary>Away.</summary>
    Away = 3,

    /// <summary>Snooze — away for a long while.</summary>
    Snooze = 4,

    /// <summary>Looking to trade.</summary>
    LookingToTrade = 5,

    /// <summary>Looking to play.</summary>
    LookingToPlay = 6,
}

/// <summary>
/// A friend of the signed-in account, merged from <c>GetFriendList</c> and <c>GetPlayerSummaries</c>.
/// </summary>
/// <param name="SteamId64">The friend's 64-bit Steam id.</param>
/// <param name="PersonaName">Display name; empty until a summaries call has filled it in.</param>
/// <param name="AvatarUrl">Full avatar URL, or <see langword="null"/> when not fetched yet.</param>
/// <param name="ProfileUrl">Community profile URL, or <see langword="null"/> when not fetched yet.</param>
/// <param name="State">Last known persona state.</param>
/// <param name="FriendSince">When the friendship was created; <see langword="null"/> when not reported.</param>
/// <param name="GameListVisible">
/// <see langword="false"/> means the friend's library is <b>private</b>. It does <b>not</b> mean they
/// own nothing, and the UI must say "hidden" rather than "no games".
/// </param>
/// <param name="LastSyncedUtc">When this friend's owned-games list was last fetched successfully.</param>
public sealed record FriendSummary(
    ulong SteamId64,
    string PersonaName,
    string? AvatarUrl,
    string? ProfileUrl,
    PersonaState State,
    DateTimeOffset? FriendSince,
    bool GameListVisible,
    DateTimeOffset? LastSyncedUtc);
