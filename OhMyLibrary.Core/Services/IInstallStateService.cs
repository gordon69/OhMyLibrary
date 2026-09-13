using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Services;

/// <summary>
/// Reads install state and asks the Steam client to change it.
/// </summary>
/// <remarks>
/// The requests are fire-and-forget handoffs to Steam via <c>steam://</c> URIs: they return
/// <see langword="true"/> when the URI was accepted by the shell, which is not a promise that the
/// user confirmed the action. Watch for the resulting state change instead of awaiting a result.
/// </remarks>
public interface IInstallStateService
{
    /// <summary>
    /// Reads the current state flags for an app, freshly from its manifest.
    /// </summary>
    /// <param name="appId">Steam application id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="AppStateFlags.None"/> when the app is not installed.</returns>
    Task<AppStateFlags> GetStateAsync(int appId, CancellationToken ct = default);

    /// <summary>Asks Steam to install or resume installing the app.</summary>
    /// <param name="appId">Steam application id.</param>
    bool RequestInstall(int appId);

    /// <summary>Asks Steam to launch the app, installing it first if needed.</summary>
    /// <param name="appId">Steam application id.</param>
    bool RequestLaunch(int appId);

    /// <summary>Asks Steam to show its uninstall prompt for the app.</summary>
    /// <param name="appId">Steam application id.</param>
    bool RequestUninstall(int appId);

    /// <summary>Asks Steam to verify the app's files.</summary>
    /// <param name="appId">Steam application id.</param>
    bool RequestValidate(int appId);
}
