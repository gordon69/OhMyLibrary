using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OhMyLibrary.App.Services;
using OhMyLibrary.App.ViewModels;
using OhMyLibrary.App.ViewModels.Pages;
using OhMyLibrary.App.Views.Pages;
using OhMyLibrary.Core;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Web;
using OhMyLibrary.Data;

using Serilog;
using Serilog.Events;

using Wpf.Ui;
using Wpf.Ui.Abstractions;

namespace OhMyLibrary.App;

/// <summary>
/// Application entry point: builds the generic host, wires logging and DI, and makes sure a bug on
/// somebody's machine surfaces as a message rather than a vanished window.
/// </summary>
public partial class App : Application
{
    private const string ConsoleOutputTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private const string FileOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// The ceiling on how long <see cref="OnExit"/> waits for the host to stop.
    /// </summary>
    /// <remarks>
    /// It is not a budget the shutdown is expected to spend. Every hosted service cancels its work
    /// and waits for it, and the two long steps — the manifest scan and the <c>appinfo.vdf</c> parse
    /// — check their token per file and per record, so a real stop takes milliseconds. This exists
    /// only so a genuinely wedged service cannot leave a process behind with no window; reaching it
    /// is a bug worth the error line <see cref="OnExit"/> writes, and the host is deliberately
    /// <b>not</b> disposed on that path — see there.
    /// </remarks>
    private static readonly TimeSpan ShutdownCeiling = TimeSpan.FromSeconds(30);

    private readonly IHost _host;

    /// <summary>Builds the host. Nothing is started until <see cref="OnStartup"/> runs.</summary>
    public App()
    {
        Log.Logger = CreateLogger();
        _host = BuildHost();
    }

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        ConfigureExceptionHandling();
        base.OnStartup(e);

        try
        {
            Log.Information("OhMyLibrary starting; data directory {Root}", AppPaths.Root);
            await _host.StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The application host failed to start");
            ShowFatal(ex);
            Shutdown(-1);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The host is stopped off the dispatcher, so a hosted service that marshals back to the UI
    /// thread cannot deadlock shutdown, and it is stopped with <see cref="CancellationToken.None"/>:
    /// the services cancel their own work and then wait for it, and handing them a token that fires
    /// on a deadline only makes them stop waiting — the work carries on regardless, which is the
    /// failure this is meant to prevent, not a cure for it.
    /// </para>
    /// <para>
    /// Disposing the host and closing the log are what actually hurt an unfinished background task:
    /// they pull the SQLite connection factory and the log file out from under it, and the exception
    /// that follows has nowhere to be reported. So they happen only once the host has really
    /// stopped. If it has not stopped by <see cref="ShutdownCeiling"/> — which means something is
    /// wedged, not merely slow — the honest thing left is to abandon it <i>without</i> tearing
    /// anything down: whatever is still running keeps a live database and a live log for the seconds
    /// it has before the process goes, and the file sink is unbuffered, so nothing already written is
    /// lost either way.
    /// </para>
    /// </remarks>
    protected override void OnExit(ExitEventArgs e)
    {
        var stopped = false;

        try
        {
            stopped = Task.Run(() => _host.StopAsync(CancellationToken.None)).Wait(ShutdownCeiling);
        }
        catch (Exception ex)
        {
            // A hosted service that threw out of StopAsync has still finished stopping.
            stopped = true;
            Log.Warning(ex, "The application host did not stop cleanly");
        }

        if (!stopped)
        {
            Log.Error(
                "The application host was still stopping after {Seconds}s; leaving it and the log alive rather than disposing them underneath whatever is still running",
                ShutdownCeiling.TotalSeconds);

            base.OnExit(e);
            return;
        }

        _host.Dispose();
        Log.Information("OhMyLibrary exiting with code {ExitCode}", e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static Serilog.ILogger CreateLogger()
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Debug(outputTemplate: ConsoleOutputTemplate);

        try
        {
            AppPaths.EnsureCreated();
            configuration = configuration.WriteTo.File(
                path: AppPaths.LogFileTemplate,
                outputTemplate: FileOutputTemplate,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                retainedFileTimeLimit: TimeSpan.FromDays(7),
                fileSizeLimitBytes: 32L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unwritable profile directory is not worth dying for: keep the Debug sink only.
            System.Diagnostics.Debug.WriteLine($"File logging disabled: {ex.Message}");
        }

        return configuration.CreateLogger();
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        ConfigureConfiguration(builder.Configuration);
        ConfigureServices(builder.Services, builder.Configuration);

        return builder.Build();
    }

    private static void ConfigureConfiguration(IConfigurationBuilder configuration)
    {
        _ = configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

        // The user-editable file wins over the shipped ones. Guarded because the whole
        // %LOCALAPPDATA% tree may be unwritable, in which case the app still runs read-only.
        if (Directory.Exists(AppPaths.Root))
        {
            _ = configuration.AddJsonFile(AppPaths.UserSettingsFile, optional: true, reloadOnChange: true);
        }

        _ = configuration
            .AddEnvironmentVariables()
            .AddEnvironmentVariables("OHMYLIBRARY_");
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        _ = services.AddSerilog(Log.Logger, false);

        _ = services.Configure<SteamOptions>(configuration.GetSection(SteamOptions.SectionName));
        _ = services.Configure<SyncOptions>(configuration.GetSection(SyncOptions.SectionName));
        _ = services.Configure<UiOptions>(configuration.GetSection(UiOptions.SectionName));

        // Domain layers. The extension methods live in OhMyLibrary.Core (ServiceCollectionExtensions),
        // OhMyLibrary.Core.Web (HttpClientRegistration) and OhMyLibrary.Data (DependencyInjection).
        // AddOhMyLibraryData takes an optional path; the default puts the database next to our logs.
        _ = services.AddOhMyLibraryCore();
        _ = services.AddOhMyLibraryHttp();
        _ = services.AddOhMyLibraryData();

        // Shell services.
        _ = services.AddHostedService<ApplicationHostService>();

        // The library sync runs after the window is up (hosted services start in registration order)
        // and stops before it (they stop in reverse). It is registered by concrete type as well so
        // MainWindow can take ILibrarySyncCoordinator and get the same instance the host drives.
        _ = services.AddSingleton<LibrarySyncCoordinator>();
        _ = services.AddSingleton<ILibrarySyncCoordinator>(sp => sp.GetRequiredService<LibrarySyncCoordinator>());
        _ = services.AddHostedService(sp => sp.GetRequiredService<LibrarySyncCoordinator>());
        _ = services.AddSingleton<INavigationViewPageProvider, PageService>();
        _ = services.AddSingleton<INavigationService, NavigationService>();
        _ = services.AddSingleton<ISnackbarService, SnackbarService>();
        _ = services.AddSingleton<IContentDialogService, ContentDialogService>();
        _ = services.AddSingleton<ISettingsService, SettingsService>();
        _ = services.AddSingleton<IWindowPlacementService, WindowPlacementService>();
        _ = services.AddSingleton<IImageCacheService, ImageCacheService>();

        _ = services.AddHttpClient(ImageCacheService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OhMyLibrary", "0.1"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        });

        // Shell window.
        _ = services.AddSingleton<MainWindow>();
        _ = services.AddSingleton<MainWindowViewModel>();

        // Pages and their view models. A navigation target that is missing here resolves to null and
        // the navigation is silently ignored, so every NavigationViewItem target belongs in this list.
        _ = services.AddSingleton<LibraryPage>();
        _ = services.AddSingleton<LibraryViewModel>();
        _ = services.AddSingleton<CollectionsPage>();
        _ = services.AddSingleton<CollectionsViewModel>();
        _ = services.AddSingleton<FriendsPage>();
        _ = services.AddSingleton<FriendsViewModel>();
        _ = services.AddSingleton<SettingsPage>();
        _ = services.AddSingleton<SettingsViewModel>();
    }

    private void ConfigureExceptionHandling()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception on the dispatcher");

        // Handled on purpose. A parse bug on one machine must not take the window with it; the user
        // gets a message, the details go to the log, and the rest of the app keeps working.
        e.Handled = true;
        ShowFatal(e.Exception, fatal: false);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "Unhandled exception on a background thread (terminating: {Terminating})", e.IsTerminating);
        }
        else
        {
            Log.Fatal("Unhandled non-exception throw on a background thread (terminating: {Terminating})", e.IsTerminating);
        }

        if (e.IsTerminating)
        {
            Log.CloseAndFlush();
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");

        // Observed here so the finalizer thread does not escalate it into a process kill.
        e.SetObserved();
    }

    private static void ShowFatal(Exception exception, bool fatal = true)
    {
        var headline = fatal
            ? "OhMyLibrary could not start."
            : "Something went wrong.";

        var text =
            $"{headline}\n\n{exception.GetType().Name}: {exception.Message}\n\n" +
            $"The details were written to:\n{AppPaths.LogsDirectory}";

        try
        {
            _ = MessageBox.Show(
                text,
                "OhMyLibrary",
                MessageBoxButton.OK,
                fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            // The UI is too far gone to report anything. The log entry above is what is left.
            Log.Error(ex, "Could not display the error dialog");
        }
    }
}
