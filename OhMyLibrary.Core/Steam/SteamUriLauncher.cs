using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using OhMyLibrary.Core.Abstractions;

namespace OhMyLibrary.Core.Steam;

/// <summary>
/// Hands <c>steam://</c> URIs to the shell, which wakes the Steam client and performs the action.
/// </summary>
/// <remarks>
/// Every id is validated as a positive integer before it is concatenated into a URI, even though it
/// comes from a trusted local parse. A refusal by the shell — Steam not installed, the protocol
/// handler unregistered, a policy block — is logged and reported as <see langword="false"/>.
/// </remarks>
public sealed class SteamUriLauncher : ISteamUriLauncher
{
    private const string Scheme = "steam://";

    private readonly ILogger<SteamUriLauncher> _logger;

    /// <summary>Creates a launcher.</summary>
    /// <param name="logger">Logger.</param>
    public SteamUriLauncher(ILogger<SteamUriLauncher> logger) => _logger = logger;

    /// <inheritdoc />
    public bool LaunchGame(int appId) => OpenForApp("rungameid", appId);

    /// <inheritdoc />
    public bool Install(int appId) => OpenForApp("install", appId);

    /// <inheritdoc />
    public bool Uninstall(int appId) => OpenForApp("uninstall", appId);

    /// <inheritdoc />
    public bool Validate(int appId) => OpenForApp("validate", appId);

    /// <inheritdoc />
    public bool OpenStorePage(int appId) => OpenForApp("store", appId);

    /// <inheritdoc />
    public bool OpenLibraryPage(int appId) => OpenForApp("nav/games/details", appId);

    /// <inheritdoc />
    public bool OpenFriendProfile(ulong steamId64)
    {
        if (steamId64 == 0)
        {
            _logger.LogWarning("Refusing to open a friend profile for an unset Steam id.");
            return false;
        }

        return Start($"{Scheme}friends/add/{steamId64.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <inheritdoc />
    public bool OpenUri(string steamUri)
    {
        if (string.IsNullOrWhiteSpace(steamUri))
        {
            return false;
        }

        string trimmed = steamUri.Trim();
        if (!trimmed.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refusing to open {Uri}: only steam:// URIs are allowed.", trimmed);
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, "steam", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refusing to open {Uri}: it is not a well-formed steam:// URI.", trimmed);
            return false;
        }

        return Start(trimmed);
    }

    private bool OpenForApp(string action, int appId)
    {
        if (appId <= 0)
        {
            _logger.LogWarning("Refusing steam://{Action} for invalid app id {AppId}.", action, appId);
            return false;
        }

        return Start($"{Scheme}{action}/{appId.ToString(CultureInfo.InvariantCulture)}");
    }

    private bool Start(string uri)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            _logger.LogDebug("Handed {Uri} to the shell.", uri);
            return true;
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "The shell refused {Uri}; is the Steam client installed?", uri);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Starting {Uri} failed.", uri);
        }

        return false;
    }
}
