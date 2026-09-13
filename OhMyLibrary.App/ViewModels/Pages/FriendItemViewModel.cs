using System.Globalization;
using System.Windows.Media;

using CommunityToolkit.Mvvm.ComponentModel;

using OhMyLibrary.Core.Models;

namespace OhMyLibrary.App.ViewModels.Pages;

/// <summary>
/// One row of the friends list: avatar, persona name, online state and whether their library is
/// readable at all.
/// </summary>
/// <remarks>
/// The library caption is the part that matters. A friend whose game details are private is not a
/// friend who owns nothing, so <see cref="LibraryStatusText"/> says "Game list hidden" and never
/// "0 games". Three states are distinguished, in this order, because a plain friend-list refresh
/// reports <see cref="FriendSummary.GameListVisible"/> optimistically and only an owned-games fetch
/// settles it:
/// <list type="number">
///   <item><description>never fetched - <see cref="FriendSummary.LastSyncedUtc"/> is <see langword="null"/>;</description></item>
///   <item><description>fetched and hidden;</description></item>
///   <item><description>fetched and readable.</description></item>
/// </list>
/// </remarks>
public partial class FriendItemViewModel : ObservableObject
{
    /// <summary>Creates a row from a stored friend summary.</summary>
    /// <param name="summary">The friend this row shows.</param>
    public FriendItemViewModel(FriendSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        Summary = summary;
        DisplayName = string.IsNullOrWhiteSpace(summary.PersonaName)
            ? summary.SteamId64.ToString(CultureInfo.InvariantCulture)
            : summary.PersonaName;
    }

    /// <summary>The stored summary this row was built from.</summary>
    public FriendSummary Summary { get; }

    /// <summary>The friend's 64-bit Steam id.</summary>
    public ulong SteamId64 => Summary.SteamId64;

    /// <summary>Persona name, falling back to the Steam id when no summaries call has run yet.</summary>
    public string DisplayName { get; }

    /// <summary>Community profile URL, or <see langword="null"/> when it has not been fetched.</summary>
    public string? ProfileUrl => Summary.ProfileUrl;

    /// <summary>True when a profile link can be offered.</summary>
    public bool HasProfile => !string.IsNullOrWhiteSpace(Summary.ProfileUrl);

    /// <summary>Avatar URL handed to the image cache, or <see langword="null"/>.</summary>
    public string? AvatarUrl => Summary.AvatarUrl;

    /// <summary>Persona state as a word, for example <c>"Looking to play"</c>.</summary>
    public string StateText => Summary.State switch
    {
        PersonaState.Online => "Online",
        PersonaState.Busy => "Busy",
        PersonaState.Away => "Away",
        PersonaState.Snooze => "Snooze",
        PersonaState.LookingToTrade => "Looking to trade",
        PersonaState.LookingToPlay => "Looking to play",
        _ => "Offline",
    };

    /// <summary>
    /// True for anything other than <see cref="PersonaState.Offline"/>. Drives the state dot;
    /// note that a private profile also reports offline, which is why the dot is not the only cue.
    /// </summary>
    public bool IsOnline => Summary.State != PersonaState.Offline;

    /// <summary>
    /// True when this friend's library was fetched and turned out to be private. Never true for a
    /// friend who simply has not been synced yet.
    /// </summary>
    public bool IsLibraryHidden => Summary.LastSyncedUtc is not null && !Summary.GameListVisible;

    /// <summary>True when a library sync has been attempted for this friend.</summary>
    public bool IsSynced => Summary.LastSyncedUtc is not null;

    /// <summary>
    /// One caption explaining what we know about the friend's library: not synced, hidden, or the
    /// local time of the last successful read.
    /// </summary>
    public string LibraryStatusText => Summary.LastSyncedUtc switch
    {
        null => "Not synced yet",
        _ when !Summary.GameListVisible => "Game list hidden",
        { } synced => $"Library read {synced.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}",
    };

    /// <summary>When the friendship started, as a caption, or an empty string when unreported.</summary>
    public string FriendSinceText => Summary.FriendSince is { } since
        ? $"Friends since {since.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}"
        : string.Empty;

    /// <summary>The decoded avatar, or <see langword="null"/> while it is missing or still loading.</summary>
    [ObservableProperty]
    private ImageSource? _avatar;
}
