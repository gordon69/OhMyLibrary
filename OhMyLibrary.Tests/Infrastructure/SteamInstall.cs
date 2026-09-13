using Microsoft.Win32;

namespace OhMyLibrary.Tests.Infrastructure;

/// <summary>
/// Locates the Steam installation of the machine the tests happen to run on.
/// </summary>
/// <remarks>
/// Only the handful of tests that deliberately read the live client use this. Everything else runs
/// against the checked-in fixtures, so a CI machine without Steam stays green.
/// </remarks>
public static class SteamInstall
{
    /// <summary>Absolute Steam root, or <see langword="null"/> when Steam is not installed here.</summary>
    public static string? Path { get; } = FindSteamPath();

    /// <summary>True when this machine has a Steam installation the tests can read.</summary>
    public static bool IsAvailable => Path is not null;

    /// <summary>Absolute path of <c>appcache/appinfo.vdf</c>, when Steam is installed.</summary>
    public static string? AppInfoPath =>
        Path is null ? null : System.IO.Path.Combine(Path, "appcache", "appinfo.vdf");

    private static string? FindSteamPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var candidate in new[]
                 {
                     ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(candidate));
                if (Directory.Exists(full))
                {
                    return full;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static string? ReadRegistry(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself when the machine has no Steam installation.
/// </summary>
public sealed class RequiresSteamFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute, skipping the test when Steam is absent.</summary>
    public RequiresSteamFactAttribute()
    {
        if (!SteamInstall.IsAvailable)
        {
            Skip = "No Steam installation on this machine.";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that runs only when an environment variable names an output
/// directory, which is how the checked-in binary fixtures are regenerated on demand.
/// </summary>
/// <remarks>
/// Steam is deliberately <em>not</em> required: the fixtures are synthesised rather than trimmed out
/// of a live client, so they regenerate identically on a machine that has never seen Steam.
/// </remarks>
public sealed class RequiresFixtureOutputFactAttribute : FactAttribute
{
    /// <summary>Environment variable holding the directory to write regenerated fixtures into.</summary>
    public const string VariableName = "OHMYLIBRARY_FIXTURE_DIR";

    /// <summary>Creates the attribute, skipping the test unless the variable names a directory.</summary>
    public RequiresFixtureOutputFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VariableName)))
        {
            Skip = $"Set {VariableName} to a directory to regenerate the fixtures.";
        }
    }
}
