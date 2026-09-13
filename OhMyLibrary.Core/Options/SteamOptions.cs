namespace OhMyLibrary.Core.Options;

/// <summary>
/// Steam account and installation settings, bound from the <c>Steam</c> configuration section.
/// </summary>
/// <remarks>
/// Nothing here is required: every value has a working default of "not configured", and the app
/// degrades to local-files-only when the key or id is missing. <see cref="ApiKey"/> comes from
/// <c>appsettings.local.json</c> or user secrets and must never be logged or committed.
/// </remarks>
public sealed class SteamOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Steam";

    /// <summary>Steam Web API key. Empty means the Web API features stay switched off.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// SteamID64 as a decimal string. Empty means auto-detect from <c>config/loginusers.vdf</c>.
    /// It is text because a SteamID64 is unsigned and does not fit a signed 64-bit field everywhere.
    /// </summary>
    public string SteamId64 { get; set; } = string.Empty;

    /// <summary>Language used for store tag names, for example <c>english</c> or <c>russian</c>.</summary>
    public string Language { get; set; } = "english";

    /// <summary>
    /// Explicit Steam install path that wins over registry detection. Empty means detect it.
    /// </summary>
    public string OverrideSteamPath { get; set; } = string.Empty;
}
