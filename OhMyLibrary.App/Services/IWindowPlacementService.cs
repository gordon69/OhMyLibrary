using System.Windows;

namespace OhMyLibrary.App.Services;

/// <summary>
/// Remembers the main window's size, position and maximised state across runs in
/// <c>%LOCALAPPDATA%\OhMyLibrary\window.json</c>.
/// </summary>
/// <remarks>
/// Neither member throws. A missing, malformed or off-screen placement simply leaves the window at
/// its designed defaults — a monitor that was unplugged since the last run must not make the app
/// open outside the visible desktop.
/// </remarks>
public interface IWindowPlacementService
{
    /// <summary>
    /// Applies the stored placement. Call it before the window is shown; it is a no-op once the
    /// window is visible.
    /// </summary>
    /// <param name="window">The window to position.</param>
    void Restore(Window window);

    /// <summary>
    /// Writes the window's current placement, using the restore bounds when it is maximised so the
    /// next run remembers both the size and the maximised state.
    /// </summary>
    /// <param name="window">The window to record.</param>
    void Persist(Window window);
}
