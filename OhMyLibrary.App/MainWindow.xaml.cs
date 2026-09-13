using System.ComponentModel;
using System.Windows;

using Microsoft.Extensions.Logging;

using OhMyLibrary.App.Services;
using OhMyLibrary.App.ViewModels;

using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace OhMyLibrary.App;

/// <summary>
/// The shell: a Fluent window with a title bar, the left navigation and the page frame.
/// </summary>
/// <remarks>
/// It implements <see cref="INavigationWindow"/> so <c>NavigationService</c> and the page provider
/// can drive it, and it owns two pieces of cross-cutting behaviour that have nowhere better to live:
/// applying the user's theme, and remembering its own placement.
/// </remarks>
public partial class MainWindow : FluentWindow, INavigationWindow
{
    private readonly ISettingsService _settingsService;
    private readonly IWindowPlacementService _placementService;
    private readonly ILibrarySyncCoordinator _syncCoordinator;
    private readonly ILogger<MainWindow> _logger;

    private bool _themeApplied;

    /// <summary>Creates the shell and wires the WPF-UI services to their hosts in this window.</summary>
    /// <param name="viewModel">Shell state: navigation items and window title.</param>
    /// <param name="pageProvider">Resolves navigation targets from the DI container.</param>
    /// <param name="navigationService">WPF-UI navigation service, bound to this window's navigation view.</param>
    /// <param name="snackbarService">WPF-UI snackbar service, bound to the presenter in the content overlay.</param>
    /// <param name="contentDialogService">WPF-UI dialog service, bound to the dialog host presenter.</param>
    /// <param name="settingsService">Settings, watched so a theme change applies immediately.</param>
    /// <param name="placementService">Restores and persists the window's size and position.</param>
    /// <param name="syncCoordinator">Background library sync, asked for a rescan when the window is activated.</param>
    /// <param name="serviceProvider">Root provider, handed to the navigation view for page activation.</param>
    /// <param name="logger">Log sink.</param>
    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationViewPageProvider pageProvider,
        INavigationService navigationService,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        ISettingsService settingsService,
        IWindowPlacementService placementService,
        ILibrarySyncCoordinator syncCoordinator,
        IServiceProvider serviceProvider,
        ILogger<MainWindow> logger)
    {
        ArgumentNullException.ThrowIfNull(navigationService);
        ArgumentNullException.ThrowIfNull(snackbarService);
        ArgumentNullException.ThrowIfNull(contentDialogService);
        ArgumentNullException.ThrowIfNull(syncCoordinator);

        _settingsService = settingsService;
        _placementService = placementService;
        _syncCoordinator = syncCoordinator;
        _logger = logger;

        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();

        SetPageService(pageProvider);
        SetServiceProvider(serviceProvider);
        navigationService.SetNavigationControl(RootNavigation);
        snackbarService.SetSnackbarPresenter(RootSnackbar);
        contentDialogService.SetDialogHost(RootContentDialog);

        RootNavigation.Navigated += OnNavigated;
        _settingsService.Changed += OnSettingsChanged;
        Activated += OnWindowActivated;

        _placementService.Restore(this);
        Loaded += OnLoaded;
    }

    /// <summary>Shell state. Bindings in the XAML go through this, because <c>DataContext</c> is the window.</summary>
    public MainWindowViewModel ViewModel { get; }

    /// <inheritdoc />
    public INavigationView GetNavigation() => RootNavigation;

    /// <inheritdoc />
    public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);

    /// <inheritdoc />
    public void SetServiceProvider(IServiceProvider serviceProvider) =>
        RootNavigation.SetServiceProvider(serviceProvider);

    /// <inheritdoc />
    public void SetPageService(INavigationViewPageProvider navigationViewPageProvider) =>
        RootNavigation.SetPageProviderService(navigationViewPageProvider);

    /// <inheritdoc />
    public void ShowWindow() => Show();

    /// <inheritdoc />
    public void CloseWindow() => Close();

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // The backdrop needs a window handle, so this is the earliest safe point.
        ApplyTheme(_settingsService.Current.Theme);
        _themeApplied = true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The base call comes first because it is what raises <c>Closing</c>, and a handler there may
    /// veto the close. Tearing the handlers off before knowing that would leave a window that is
    /// still alive with its focus rescans and its theme updates switched off for the rest of the
    /// session, and nothing ever re-attaches them.
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel)
        {
            return;
        }

        _placementService.Persist(this);

        RootNavigation.Navigated -= OnNavigated;
        _settingsService.Changed -= OnSettingsChanged;
        Activated -= OnWindowActivated;
        SystemThemeWatcher.UnWatch(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (ViewModel.StartPageType is { } startPage && !Navigate(startPage))
        {
            _logger.LogWarning("Could not navigate to the start page {Page}", startPage.FullName);
        }
    }

    /// <summary>
    /// Steam may have installed, updated or removed something while the user was elsewhere, so
    /// coming back to the window rescans the manifests. Not throttled here: the request is coalesced
    /// with any rescan already running and the service's own cooldown keeps alt-tab thrash off the
    /// disk, so a second timer on top would only make the grid slower to tell the truth.
    /// </summary>
    /// <remarks>
    /// This fires the first time inside <c>ShowWindow()</c>, before the coordinator's own hosted
    /// service has started — the shell is shown by the hosted service registered ahead of it. That
    /// first request therefore does not scan anything itself; the coordinator folds it into the
    /// startup scan it is about to run, which is what keeps the first scan of a session single and
    /// deterministic.
    /// </remarks>
    private void OnWindowActivated(object? sender, EventArgs e) =>
        _syncCoordinator.RequestLocalRescan(force: false);

    private void OnNavigated(NavigationView sender, NavigatedEventArgs args)
    {
        // NavigationView.SelectedItem is still the previous entry while Navigated is raised, so the
        // title is resolved from the page that actually arrived.
        ViewModel.CurrentPageTitle = ViewModel.FindTitle(args.Page?.GetType());
    }

    private void OnSettingsChanged(object? sender, UserSettingsChangedEventArgs e)
    {
        if (!_themeApplied)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() => ApplyTheme(e.Settings.Theme));
    }

    private void ApplyTheme(string theme)
    {
        // Mica needs Windows 11. On anything older WPF-UI leaves the solid ApplicationBackgroundBrush
        // in place, which is the intended fallback rather than a failure.
        var backdrop = WindowBackdrop.IsSupported(WindowBackdropType.Mica)
            ? WindowBackdropType.Mica
            : WindowBackdropType.None;

        if (WindowBackdropType != backdrop)
        {
            WindowBackdropType = backdrop;
        }

        switch (theme)
        {
            case "Light":
                SystemThemeWatcher.UnWatch(this);
                ApplicationThemeManager.Apply(ApplicationTheme.Light, backdrop, true);
                break;

            case "Dark":
                SystemThemeWatcher.UnWatch(this);
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, backdrop, true);
                break;

            default:
                ApplicationThemeManager.ApplySystemTheme();
                SystemThemeWatcher.Watch(this, backdrop, true);
                break;
        }

        _logger.LogDebug("Theme {Theme} applied with backdrop {Backdrop}", theme, backdrop);
    }
}
