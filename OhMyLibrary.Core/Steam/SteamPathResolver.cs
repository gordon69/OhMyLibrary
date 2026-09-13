using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;

namespace OhMyLibrary.Core.Steam;

/// <summary>
/// Locates Steam through the configured override, then the registry, then the default install
/// locations, and hands the library and account files off to their readers.
/// </summary>
/// <remarks>
/// <para>
/// <c>HKCU\Software\Valve\Steam</c> stores lower-case forward-slash paths such as
/// <c>c:/program files (x86)/steam</c>, so every candidate is passed through
/// <see cref="Path.GetFullPath(string)"/> and an existence check before it is accepted.
/// </para>
/// <para>
/// A successfully resolved root is cached for the lifetime of the instance; a failed probe is not,
/// so installing Steam while the app is running is picked up on the next call.
/// </para>
/// </remarks>
public sealed class SteamPathResolver : ISteamPathResolver
{
    private const string RegistrySubKeyCurrentUser = @"Software\Valve\Steam";
    private const string RegistrySubKeyWow6432 = @"SOFTWARE\WOW6432Node\Valve\Steam";
    private const string RegistrySubKeyLocalMachine = @"SOFTWARE\Valve\Steam";
    private const string SteamExecutableName = "steam.exe";

    private readonly IOptions<SteamOptions> _options;
    private readonly ILibraryFoldersReader _libraryFoldersReader;
    private readonly ILoginUsersReader _loginUsersReader;
    private readonly ILogger<SteamPathResolver> _logger;

    private string? _cachedSteamPath;

    /// <summary>Creates a resolver.</summary>
    /// <param name="options">Steam settings; <see cref="SteamOptions.OverrideSteamPath"/> wins over detection.</param>
    /// <param name="libraryFoldersReader">Reader for <c>libraryfolders.vdf</c>.</param>
    /// <param name="loginUsersReader">Reader for <c>config/loginusers.vdf</c>.</param>
    /// <param name="logger">Logger.</param>
    public SteamPathResolver(
        IOptions<SteamOptions> options,
        ILibraryFoldersReader libraryFoldersReader,
        ILoginUsersReader loginUsersReader,
        ILogger<SteamPathResolver> logger)
    {
        _options = options;
        _libraryFoldersReader = libraryFoldersReader;
        _loginUsersReader = loginUsersReader;
        _logger = logger;
    }

    /// <inheritdoc />
    public string? FindSteamPath()
    {
        if (_cachedSteamPath is not null)
        {
            return _cachedSteamPath;
        }

        string? path = ProbeSteamPath();
        if (path is null)
        {
            _logger.LogInformation("Steam was not found: no override, registry key or default install location matched.");
            return null;
        }

        _cachedSteamPath = path;
        _logger.LogInformation("Steam install root resolved to {SteamPath}.", path);
        return path;
    }

    /// <inheritdoc />
    public string? FindSteamExecutable()
    {
        string? fromRegistry = NormalizeFile(ReadRegistryValue(RegistryHive.CurrentUser, RegistryView.Default, RegistrySubKeyCurrentUser, "SteamExe"));
        if (fromRegistry is not null)
        {
            return fromRegistry;
        }

        string? steamPath = FindSteamPath();
        return steamPath is null ? null : NormalizeFile(Path.Combine(steamPath, SteamExecutableName));
    }

    /// <inheritdoc />
    public IReadOnlyList<SteamLibraryFolder> GetLibraryFolders()
    {
        string? steamPath = FindSteamPath();
        if (steamPath is null)
        {
            return [];
        }

        IReadOnlyList<SteamLibraryFolder> declared;
        try
        {
            declared = _libraryFoldersReader.Read(steamPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading libraryfolders.vdf under {SteamPath} failed; treating the library list as empty.", steamPath);
            return [];
        }

        List<SteamLibraryFolder> reachable = new(declared.Count);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (SteamLibraryFolder folder in declared)
        {
            string? normalized = NormalizeDirectory(folder.Path);
            if (normalized is null)
            {
                _logger.LogDebug("Skipping library folder {LibraryPath}: it does not exist (unplugged drive?).", folder.Path);
                continue;
            }

            if (!seen.Add(normalized))
            {
                continue;
            }

            reachable.Add(folder with { Path = normalized });
        }

        return reachable;
    }

    /// <inheritdoc />
    public IReadOnlyList<SteamUser> GetLocalUsers()
    {
        string? steamPath = FindSteamPath();
        if (steamPath is null)
        {
            return [];
        }

        IReadOnlyList<SteamUser> users;
        try
        {
            users = _loginUsersReader.Read(steamPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading config/loginusers.vdf under {SteamPath} failed; treating the account list as empty.", steamPath);
            return [];
        }

        return users
            .OrderByDescending(u => u.MostRecent)
            .ThenByDescending(u => u.Timestamp ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    private string? ProbeSteamPath()
    {
        string? overridePath = NormalizeDirectory(_options.Value.OverrideSteamPath);
        if (overridePath is not null)
        {
            return overridePath;
        }

        if (!string.IsNullOrWhiteSpace(_options.Value.OverrideSteamPath))
        {
            _logger.LogWarning(
                "Configured Steam path {OverridePath} does not exist; falling back to detection.",
                _options.Value.OverrideSteamPath);
        }

        string?[] candidates =
        [
            ReadRegistryValue(RegistryHive.CurrentUser, RegistryView.Default, RegistrySubKeyCurrentUser, "SteamPath"),
            ReadRegistryValue(RegistryHive.LocalMachine, RegistryView.Registry64, RegistrySubKeyWow6432, "InstallPath"),
            ReadRegistryValue(RegistryHive.LocalMachine, RegistryView.Registry64, RegistrySubKeyLocalMachine, "InstallPath"),
            DefaultInstallPath(Environment.SpecialFolder.ProgramFilesX86),
            DefaultInstallPath(Environment.SpecialFolder.ProgramFiles),
        ];

        foreach (string? candidate in candidates)
        {
            string? normalized = NormalizeDirectory(candidate);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        return null;
    }

    private static string? DefaultInstallPath(Environment.SpecialFolder programFiles)
    {
        string root = Environment.GetFolderPath(programFiles);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Steam");
    }

    private string? ReadRegistryValue(RegistryHive hive, RegistryView view, string subKeyName, string valueName)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(subKeyName);
            return key?.GetValue(valueName) as string;
        }
        catch (SecurityException ex)
        {
            _logger.LogDebug(ex, "No permission to read {Hive}\\{SubKey}\\{Value}.", hive, subKeyName, valueName);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Access denied reading {Hive}\\{SubKey}\\{Value}.", hive, subKeyName, valueName);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Registry read of {Hive}\\{SubKey}\\{Value} failed.", hive, subKeyName, valueName);
        }

        return null;
    }

    /// <summary>
    /// Normalises a possibly lower-case, forward-slash registry path and returns it only when the
    /// directory exists.
    /// </summary>
    private static string? NormalizeDirectory(string? candidate)
    {
        string? full = NormalizeFullPath(candidate);
        return full is not null && Directory.Exists(full) ? full : null;
    }

    /// <summary>Normalises a path and returns it only when the file exists.</summary>
    private static string? NormalizeFile(string? candidate)
    {
        string? full = NormalizeFullPath(candidate);
        return full is not null && File.Exists(full) ? full : null;
    }

    private static string? NormalizeFullPath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(candidate.Trim());
            string root = Path.GetPathRoot(full) ?? string.Empty;

            // A trailing separator is only meaningful on a bare root such as "C:\".
            return full.Length > root.Length
                ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException or IOException)
        {
            return null;
        }
    }
}
