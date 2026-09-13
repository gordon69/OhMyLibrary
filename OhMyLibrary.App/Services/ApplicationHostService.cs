using System.Linq;
using System.Windows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Wpf.Ui;

namespace OhMyLibrary.App.Services;

/// <summary>
/// Brings the main window up once the generic host has started.
/// </summary>
/// <remarks>
/// The window is deliberately not created by <c>StartupUri</c>: it is resolved from the container so
/// it can take its view model and services through its constructor. Settings are loaded first so the
/// window paints in the right theme rather than flashing the default one.
/// </remarks>
/// <param name="serviceProvider">Root service provider used to resolve the window.</param>
/// <param name="settingsService">Settings, loaded before the window is shown.</param>
/// <param name="logger">Log sink.</param>
public sealed class ApplicationHostService(
    IServiceProvider serviceProvider,
    ISettingsService settingsService,
    ILogger<ApplicationHostService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Settings could not be loaded; starting with configuration defaults");
        }

        var application = Application.Current;
        if (application is null)
        {
            logger.LogWarning("No WPF application is running; the main window will not be shown");
            return;
        }

        await application.Dispatcher.InvokeAsync(ShowMainWindow);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void ShowMainWindow()
    {
        var application = Application.Current;
        if (application is null || application.Windows.OfType<MainWindow>().Any())
        {
            return;
        }

        INavigationWindow window = serviceProvider.GetRequiredService<MainWindow>();
        window.ShowWindow();
        logger.LogInformation("Main window shown");
    }
}
