using System.IO;
using System.Text.Json;
using System.Windows;

using Microsoft.Extensions.Logging;

namespace OhMyLibrary.App.Services;

/// <summary>
/// JSON-backed implementation of <see cref="IWindowPlacementService"/>.
/// </summary>
/// <param name="logger">Log sink.</param>
public sealed class WindowPlacementService(ILogger<WindowPlacementService> logger) : IWindowPlacementService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public void Restore(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var placement = Read();
        if (placement is null)
        {
            return;
        }

        var width = Math.Max(placement.Width, Coerce(window.MinWidth, 0));
        var height = Math.Max(placement.Height, Coerce(window.MinHeight, 0));

        if (!IsUsable(width) || !IsUsable(height))
        {
            return;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        width = Math.Min(width, SystemParameters.VirtualScreenWidth);
        height = Math.Min(height, SystemParameters.VirtualScreenHeight);

        var left = Math.Clamp(placement.Left, virtualLeft, Math.Max(virtualLeft, virtualRight - width));
        var top = Math.Clamp(placement.Top, virtualTop, Math.Max(virtualTop, virtualBottom - height));

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = left;
        window.Top = top;
        window.Width = width;
        window.Height = height;

        if (placement.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    /// <inheritdoc />
    public void Persist(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var isMaximized = window.WindowState == WindowState.Maximized;
        var bounds = isMaximized || window.WindowState == WindowState.Minimized
            ? window.RestoreBounds
            : new Rect(window.Left, window.Top, window.Width, window.Height);

        if (bounds.IsEmpty || !IsUsable(bounds.Width) || !IsUsable(bounds.Height))
        {
            return;
        }

        var placement = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, isMaximized);

        try
        {
            _ = Directory.CreateDirectory(AppPaths.Root);
            var temporaryPath = AppPaths.WindowStateFile + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(placement, SerializerOptions));
            File.Move(temporaryPath, AppPaths.WindowStateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogDebug(ex, "Could not save the window placement to {Path}", AppPaths.WindowStateFile);
        }
    }

    private WindowPlacement? Read()
    {
        try
        {
            if (!File.Exists(AppPaths.WindowStateFile))
            {
                return null;
            }

            return JsonSerializer.Deserialize<WindowPlacement>(
                File.ReadAllText(AppPaths.WindowStateFile),
                SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            logger.LogDebug(ex, "Could not read the window placement from {Path}", AppPaths.WindowStateFile);
            return null;
        }
    }

    private static bool IsUsable(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;

    private static double Coerce(double value, double fallback) => IsUsable(value) ? value : fallback;

    private sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool IsMaximized);
}
