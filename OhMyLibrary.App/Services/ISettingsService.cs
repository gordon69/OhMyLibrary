namespace OhMyLibrary.App.Services;

/// <summary>
/// The slice of configuration the user may edit from the Settings page.
/// </summary>
/// <remarks>
/// This is a mutable POCO on purpose: the Settings page binds a working copy, mutates it, and hands
/// it back to <see cref="ISettingsService.SaveAsync"/>. Take that copy with
/// <see cref="Clone"/> — never bind directly to <see cref="ISettingsService.Current"/>, or a
/// half-typed API key becomes live configuration.
/// </remarks>
public sealed class UserSettings
{
    /// <summary>Steam Web API key. Empty means the Web API features stay switched off.</summary>
    /// <remarks>Never log this and never render it in full; use <see cref="ISettingsService.MaskedApiKey"/>.</remarks>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>SteamID64 as a decimal string. Empty means auto-detect from the local Steam client.</summary>
    public string SteamId64 { get; set; } = string.Empty;

    /// <summary>Language used for store tag names, for example <c>english</c>.</summary>
    public string Language { get; set; } = "english";

    /// <summary>Theme name: <c>System</c>, <c>Light</c> or <c>Dark</c>.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Width of a library grid card, in device-independent pixels.</summary>
    public int CardWidth { get; set; } = 200;

    /// <summary>Returns an independent copy, for a page to edit before saving.</summary>
    public UserSettings Clone() => (UserSettings)MemberwiseClone();
}

/// <summary>Carries the settings that are now in force.</summary>
/// <param name="settings">The saved settings.</param>
public sealed class UserSettingsChangedEventArgs(UserSettings settings) : EventArgs
{
    /// <summary>The settings that are now in force. Treat as read-only.</summary>
    public UserSettings Settings { get; } = settings;
}

/// <summary>
/// Reads and writes the user-editable settings at <c>%LOCALAPPDATA%\OhMyLibrary\user-settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The file uses the same section shape as <c>appsettings.json</c> (<c>Steam</c> and <c>Ui</c>) and
/// is registered as the last JSON configuration source, so a saved value also reaches
/// <c>IOptionsMonitor&lt;SteamOptions&gt;</c> and <c>IOptionsMonitor&lt;UiOptions&gt;</c> once the
/// file watcher fires. <see cref="Changed"/> fires immediately, which is what the UI should react to.
/// </para>
/// <para>
/// Nothing here throws for an unreadable or malformed file: the previous values stay in force and
/// the failure is logged.
/// </para>
/// </remarks>
public interface ISettingsService
{
    /// <summary>
    /// The settings currently in force. Never <see langword="null"/>; before <see cref="LoadAsync"/>
    /// runs it holds the values bound from <c>appsettings.json</c>.
    /// </summary>
    UserSettings Current { get; }

    /// <summary>
    /// The API key with all but its last four characters replaced, for display. Empty when no key
    /// is configured. This is the only form of the key that may reach the UI or a log.
    /// </summary>
    string MaskedApiKey { get; }

    /// <summary>Whether a non-empty API key is configured.</summary>
    bool HasApiKey { get; }

    /// <summary>Raised on the calling thread after <see cref="SaveAsync"/> or <see cref="LoadAsync"/> changes the values.</summary>
    event EventHandler<UserSettingsChangedEventArgs>? Changed;

    /// <summary>
    /// Loads the settings file, falling back to the bound configuration when it is missing or
    /// unreadable.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The settings now in force — the same instance as <see cref="Current"/>.</returns>
    Task<UserSettings> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Validates, normalises and persists the settings, then raises <see cref="Changed"/>.
    /// </summary>
    /// <param name="settings">The values to persist. The service stores a copy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The normalised settings now in force.</returns>
    Task<UserSettings> SaveAsync(UserSettings settings, CancellationToken ct = default);
}
