using Microsoft.Extensions.Logging;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Services;

/// <summary>
/// Reads install state straight off disk and hands state changes to the Steam client.
/// </summary>
/// <remarks>
/// Deliberately thin: the <c>Request*</c> methods are one-line handoffs to
/// <see cref="ISteamUriLauncher"/>, and installing and updating are the <b>same</b>
/// <c>steam://install</c> call — Steam resumes, repairs or updates an app that already has a manifest.
/// </remarks>
public sealed class InstallStateService : IInstallStateService
{
    private readonly ISteamPathResolver _paths;
    private readonly IAcfReader _acf;
    private readonly IGameRepository _games;
    private readonly ISteamUriLauncher _launcher;
    private readonly ILogger<InstallStateService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="paths">Locates the reachable library folders holding the manifests.</param>
    /// <param name="acf">Parses a single manifest.</param>
    /// <param name="games">Fallback source of state when no manifest is on disk.</param>
    /// <param name="launcher">Sends <c>steam://</c> URIs to the shell.</param>
    /// <param name="logger">Log sink; a failed read degrades to <see cref="AppStateFlags.None"/>.</param>
    public InstallStateService(
        ISteamPathResolver paths,
        IAcfReader acf,
        IGameRepository games,
        ISteamUriLauncher launcher,
        ILogger<InstallStateService> logger)
    {
        _paths = paths;
        _acf = acf;
        _games = games;
        _launcher = launcher;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The manifest is re-read from disk so a download that started seconds ago is visible without
    /// waiting for a library rescan. When no manifest is reachable — an unplugged drive, or an app
    /// that is only owned — the stored row is used instead.
    /// </remarks>
    public async Task<AppStateFlags> GetStateAsync(int appId, CancellationToken ct = default)
    {
        if (appId <= 0)
        {
            return AppStateFlags.None;
        }

        var fromDisk = ReadStateFromDisk(appId);
        if (fromDisk is not null)
        {
            return fromDisk.Value;
        }

        try
        {
            var stored = await _games.GetAsync(appId, ct).ConfigureAwait(false);
            return stored?.StateFlags ?? AppStateFlags.None;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the stored install state for {AppId}.", appId);
            return AppStateFlags.None;
        }
    }

    /// <inheritdoc />
    public bool RequestInstall(int appId) => Request(appId, _launcher.Install, nameof(RequestInstall));

    /// <inheritdoc />
    public bool RequestLaunch(int appId) => Request(appId, _launcher.LaunchGame, nameof(RequestLaunch));

    /// <inheritdoc />
    public bool RequestUninstall(int appId) => Request(appId, _launcher.Uninstall, nameof(RequestUninstall));

    /// <inheritdoc />
    public bool RequestValidate(int appId) => Request(appId, _launcher.Validate, nameof(RequestValidate));

    private AppStateFlags? ReadStateFromDisk(int appId)
    {
        try
        {
            var manifestName = $"appmanifest_{appId}.acf";
            foreach (var folder in _paths.GetLibraryFolders())
            {
                var manifestPath = Path.Combine(folder.SteamAppsPath, manifestName);
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var app = _acf.Read(manifestPath, folder.Path);
                if (app is not null)
                {
                    return app.StateFlags;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the manifest for {AppId}.", appId);
        }

        return null;
    }

    private bool Request(int appId, Func<int, bool> action, string operation)
    {
        if (appId <= 0)
        {
            _logger.LogWarning("{Operation} refused: {AppId} is not a valid app id.", operation, appId);
            return false;
        }

        try
        {
            var accepted = action(appId);
            if (!accepted)
            {
                _logger.LogWarning("{Operation} for {AppId} was not accepted by the shell.", operation, appId);
            }

            return accepted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation} for {AppId} failed.", operation, appId);
            return false;
        }
    }
}
