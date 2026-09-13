namespace OhMyLibrary.Core.Options;

/// <summary>
/// Presentation preferences, bound from the <c>Ui</c> configuration section.
/// </summary>
public sealed class UiOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ui";

    /// <summary>Theme name: <c>System</c>, <c>Light</c> or <c>Dark</c>.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Width of a library grid card, in device-independent pixels.</summary>
    public int CardWidth { get; set; } = 200;
}
