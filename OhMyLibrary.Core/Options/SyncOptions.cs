namespace OhMyLibrary.Core.Options;

/// <summary>
/// Cache lifetimes, bound from the <c>Sync</c> configuration section.
/// </summary>
/// <remarks>
/// Local files are cheap to re-read and are only throttled; Web API calls are expensive and
/// rate-limited, so they get hours-long lifetimes and a manual refresh path.
/// </remarks>
public sealed class SyncOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Sync";

    /// <summary>How long the owned-games list stays fresh, in hours.</summary>
    public int OwnedGamesMaxAgeHours { get; set; } = 12;

    /// <summary>How long the friends list and its per-friend libraries stay fresh, in hours.</summary>
    public int FriendsMaxAgeHours { get; set; } = 24;

    /// <summary>How long parsed <c>appinfo.vdf</c> metadata stays fresh, in hours.</summary>
    public int AppInfoMaxAgeHours { get; set; } = 6;

    /// <summary>How long downloaded tag names stay fresh, in hours. They change rarely.</summary>
    public int TagNamesMaxAgeHours { get; set; } = 168;

    /// <summary>Minimum gap between local manifest rescans, in seconds.</summary>
    public int LocalRescanCooldownSeconds { get; set; } = 60;
}
