using System.Globalization;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using Serilog;
using Serilog.Core;

namespace OhMyLibrary.Core.Vdf;

/// <summary>
/// Reads <c>config/loginusers.vdf</c>, the list of accounts that have signed in on this machine.
/// </summary>
/// <remarks>
/// It is how the app auto-detects a SteamID64 instead of asking the user to type one. The children
/// are keyed by the id itself; <c>MostRecent</c> marks the account the client last signed in as,
/// though older clients omit the key entirely, in which case nobody is marked.
/// </remarks>
public sealed class LoginUsersReader : ILoginUsersReader
{
    private const string RootKey = "users";
    private const string ConfigFolderName = "config";
    private const string FileName = "loginusers.vdf";
    private const string FileKind = "login users file";

    private readonly ILogger _logger;

    /// <summary>Creates a reader.</summary>
    /// <param name="logger">
    /// Logger for skipped accounts. <see langword="null"/> is accepted and silences the reader.
    /// </param>
    public LoginUsersReader(ILogger? logger = null) => _logger = (logger ?? Logger.None).ForContext<LoginUsersReader>();

    /// <inheritdoc />
    public IReadOnlyList<SteamUser> Read(string steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return [];
        }

        string path;
        try
        {
            path = Path.Combine(Path.GetFullPath(steamPath.Trim()), ConfigFolderName, FileName);
        }
        catch (Exception ex) when (VdfFile.IsExpected(ex))
        {
            _logger.Warning(ex, "Skipping {FileName}: {SteamPath} is not a usable path", FileName, steamPath);
            return [];
        }

        if (!File.Exists(path))
        {
            _logger.Warning("No {Path}; the signed-in account cannot be auto-detected", path);
            return [];
        }

        var document = VdfFile.TryLoadText(path, _logger, FileKind);
        if (document is null)
        {
            return [];
        }

        if (!string.Equals(document.Name, RootKey, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning("{Path} has root key {RootKey}, expected {Expected}; parsing it anyway", path, document.Name, RootKey);
        }

        var users = new List<SteamUser>(document.Root.Count);
        foreach (var entry in document.Root.Children)
        {
            if (entry.Key is null || !entry.Value.IsCollection)
            {
                continue;
            }

            if (!ulong.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var steamId64) || steamId64 == 0)
            {
                _logger.Warning("Skipping account {Key} in {Path}: not a SteamID64", entry.Key, path);
                continue;
            }

            users.Add(new SteamUser(
                SteamId64: steamId64,
                AccountName: entry.Value.GetString("AccountName") ?? string.Empty,
                PersonaName: entry.Value.GetString("PersonaName") ?? string.Empty,
                MostRecent: entry.Value.GetInt("MostRecent") != 0,
                Timestamp: entry.Value.GetUnixTime("Timestamp")));
        }

        return users;
    }
}
