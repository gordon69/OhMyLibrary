namespace OhMyLibrary.Core.Models;

/// <summary>
/// One entry from <c>IPlayerService/GetOwnedGames</c>.
/// </summary>
/// <param name="AppId">Steam application id.</param>
/// <param name="Name">Display name; <see langword="null"/> when the call omitted <c>include_appinfo</c>.</param>
/// <param name="PlaytimeForeverMinutes">Lifetime playtime in minutes.</param>
/// <param name="Playtime2WeeksMinutes">Playtime in the last two weeks, in minutes.</param>
/// <param name="LastPlayed">When the account last played the app; <see langword="null"/> when never or not reported.</param>
/// <param name="ImgIconUrl">The bare icon hash Valve returns, not a full URL.</param>
public sealed record OwnedGame(
    int AppId,
    string? Name,
    int PlaytimeForeverMinutes,
    int Playtime2WeeksMinutes,
    DateTimeOffset? LastPlayed,
    string? ImgIconUrl);

/// <summary>
/// Result of an owned-games query, separating "the profile is private" from "the account owns nothing".
/// </summary>
/// <remarks>
/// A private profile answers <c>GetOwnedGames</c> with an empty <c>response</c> object rather than an
/// error, so an empty <see cref="Games"/> list alone is ambiguous. <see cref="Visible"/> is
/// <see langword="false"/> when the library was hidden or the call could not be made at all
/// (no API key, transport failure); it is <see langword="true"/> only when Steam actually returned a list.
/// </remarks>
/// <param name="Visible">Whether the account's game list was readable.</param>
/// <param name="Games">The games returned; always empty when <paramref name="Visible"/> is <see langword="false"/>.</param>
public sealed record OwnedGamesResult(bool Visible, IReadOnlyList<OwnedGame> Games)
{
    /// <summary>A hidden or unreadable library.</summary>
    public static OwnedGamesResult Hidden { get; } = new(false, []);

    /// <summary>A readable library that genuinely contains no games.</summary>
    public static OwnedGamesResult Empty { get; } = new(true, []);
}
