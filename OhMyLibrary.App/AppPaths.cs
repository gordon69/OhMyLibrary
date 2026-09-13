using System.IO;

namespace OhMyLibrary.App;

/// <summary>
/// Every location the launcher writes to. All of it lives under
/// <c>%LOCALAPPDATA%\OhMyLibrary\</c>; nothing is ever written into Steam's own directories.
/// </summary>
/// <remarks>
/// The properties are pure string composition and never touch the disk, so they are safe to read
/// from a static initialiser. Call <see cref="EnsureCreated"/> once at startup — before the log
/// sink or any configuration file provider is created — to materialise the folders.
/// </remarks>
public static class AppPaths
{
    /// <summary>Folder name used under <c>%LOCALAPPDATA%</c>.</summary>
    public const string FolderName = "OhMyLibrary";

    /// <summary>Root of everything the app owns: <c>%LOCALAPPDATA%\OhMyLibrary</c>.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify),
        FolderName);

    /// <summary>Rolling Serilog log files.</summary>
    public static string LogsDirectory { get; } = Path.Combine(Root, "logs");

    /// <summary>
    /// Path template handed to the Serilog file sink. The sink inserts the date before the
    /// extension, producing <c>omnl-20260910.log</c>.
    /// </summary>
    public static string LogFileTemplate { get; } = Path.Combine(LogsDirectory, "omnl-.log");

    /// <summary>Disk cache for images downloaded from the Steam CDN.</summary>
    public static string ImageCacheDirectory { get; } = Path.Combine(Root, "imagecache");

    /// <summary>
    /// The user-editable settings written by <c>SettingsService</c>. It uses the same section
    /// shape as <c>appsettings.json</c> and is layered on top of it as a configuration source.
    /// </summary>
    public static string UserSettingsFile { get; } = Path.Combine(Root, "user-settings.json");

    /// <summary>Remembered main-window size, position and maximised state.</summary>
    public static string WindowStateFile { get; } = Path.Combine(Root, "window.json");

    /// <summary>
    /// Creates the folders the app writes to. Safe to call repeatedly.
    /// </summary>
    /// <exception cref="IOException">
    /// The profile directory is unwritable. That is fatal for logging and settings, so the caller
    /// reports it rather than continuing silently.
    /// </exception>
    public static void EnsureCreated()
    {
        _ = Directory.CreateDirectory(Root);
        _ = Directory.CreateDirectory(LogsDirectory);
        _ = Directory.CreateDirectory(ImageCacheDirectory);
    }
}
