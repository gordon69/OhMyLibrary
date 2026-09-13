# UI conventions — OhMyLibrary.App

Everything the page stage needs in order to add `LibraryPage`, `CollectionsPage`, `FriendsPage`
and `SettingsPage` without touching the shell. The API names below were read out of
`~/.nuget/packages/wpf-ui/4.3.0` by reflection and exercised by actually running the shell, not
recalled from 3.x documentation — several of them changed between 3.x and 4.x.

---

## 1. What the shell already does

| Concern | Where it lives | You do not need to |
|---|---|---|
| Generic host, configuration, options binding | `App.xaml.cs` | build a host, read `appsettings.json` |
| Serilog to `%LOCALAPPDATA%\OhMyLibrary\logs\omnl-<date>.log` + Debug sink | `App.xaml.cs` | configure a logger; just inject `ILogger<T>` |
| DI container and all registrations | `App.ConfigureServices` | resolve anything from a static locator |
| Theme dictionaries, Mica backdrop, light/dark switching | `App.xaml`, `MainWindow.ApplyTheme` | call `ApplicationThemeManager` yourself |
| Navigation frame, snackbar host, dialog host | `MainWindow.xaml/.cs` | host any of those on a page |
| Window size/position memory | `WindowPlacementService` | — |
| Unhandled exception handling | `App.ConfigureExceptionHandling` | wrap `Main` in try/catch |

`dotnet run --project OhMyLibrary.App` brings up the window, navigates to the first navigation
entry, and logs to the file above. If the window does not appear, read that log first.

---

## 2. WPF-UI 4.3.0 — the names that actually exist

XAML namespace, on every view:

```xml
xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
```

That one URI covers `Wpf.Ui`, `Wpf.Ui.Controls`, `Wpf.Ui.Markup` and `Wpf.Ui.Converters`.

| Thing | 4.3.0 name | Notes |
|---|---|---|
| Page provider for DI | `Wpf.Ui.Abstractions.INavigationViewPageProvider` | **Replaces 3.x `Wpf.Ui.IPageService`, which no longer exists.** Lives in the separate `WPF-UI.Abstractions` assembly, pulled in transitively. |
| Its one member | `object? GetPage(Type pageType)` | Implemented by `OhMyLibrary.App.Services.PageService`. |
| Hand the provider to the view | `INavigationView.SetPageProviderService(...)` | On `INavigationWindow` the same thing is called `SetPageService(...)`. |
| Window contract | `Wpf.Ui.INavigationWindow` | `GetNavigation()`, `Navigate(Type)`, `SetServiceProvider`, `SetPageService`, `ShowWindow()`, `CloseWindow()`. |
| Navigation service | `Wpf.Ui.INavigationService` / `NavigationService` | `Navigate(Type)`, `Navigate(string tag)`, `GoBack()`. |
| Snackbar | `Wpf.Ui.ISnackbarService` / `SnackbarService`, presenter `Wpf.Ui.Controls.SnackbarPresenter` | `Show(title, message, ControlAppearance, IconElement, TimeSpan)`. |
| Dialogs | `Wpf.Ui.IContentDialogService` / `ContentDialogService`, host `Wpf.Ui.Controls.ContentDialogHost` | `SetDialogHost(ContentPresenter)` is **deprecated** in 4.3; the shell uses the `ContentDialogHost` overload. |
| Theme | `Wpf.Ui.Appearance.ApplicationThemeManager` | `Apply(ApplicationTheme, WindowBackdropType, bool updateAccent)`, `ApplySystemTheme()`. |
| Follow the OS theme | `Wpf.Ui.Appearance.SystemThemeWatcher.Watch(Window, WindowBackdropType, bool)` / `UnWatch(Window)` | |
| Theme dictionaries | `ui:ThemesDictionary` (has `Theme`) and `ui:ControlsDictionary` | Merged in `App.xaml`, in that order. |
| Window | `Wpf.Ui.Controls.FluentWindow` | `WindowBackdropType`, `WindowCornerPreference`, `ExtendsContentIntoTitleBar`. |
| Backdrop support probe | `Wpf.Ui.Controls.WindowBackdrop.IsSupported(WindowBackdropType)` | |
| Navigation item | `Wpf.Ui.Controls.NavigationViewItem` | Useful ctor: `(string content, SymbolRegular icon, Type targetPageType)`. |
| Per-page navigation hook | `Wpf.Ui.Abstractions.Controls.INavigationAware` | `OnNavigatedToAsync()` / `OnNavigatedFromAsync()`. |
| View/VM pairing marker | `Wpf.Ui.Abstractions.Controls.INavigableView<TViewModel>` | Exposes `TViewModel ViewModel { get; }`. |

Theme brush keys used by the shared styles (all resolve in both light and dark):
`ApplicationBackgroundBrush`, `TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`,
`CardBackgroundFillColorDefaultBrush`, `CardBackgroundFillColorSecondaryBrush`,
`CardStrokeColorDefaultBrush`, `ControlStrokeColorSecondaryBrush`,
`SolidBackgroundFillColorBaseBrush`, `AccentTextFillColorPrimaryBrush`,
`SystemFillColorCriticalBrush`. Always reference them with `DynamicResource` — a `StaticResource`
snapshot goes stale the moment the user flips the theme.

---

## 3. Folder and namespace layout

```
OhMyLibrary.App/
  App.xaml(.cs)                      shell startup, DI, logging      OWNED BY THE SHELL
  MainWindow.xaml(.cs)               FluentWindow + NavigationView   OWNED BY THE SHELL
  AppPaths.cs                        %LOCALAPPDATA% paths            OWNED BY THE SHELL
  Services/                          namespace OhMyLibrary.App.Services
  Styles/Theme.xaml, Cards.xaml      shared resources
  ViewModels/ViewModelBase.cs        namespace OhMyLibrary.App.ViewModels
  ViewModels/MainWindowViewModel.cs

  Views/Pages/LibraryPage.xaml(.cs)      namespace OhMyLibrary.App.Views.Pages     <- yours
  Views/Pages/CollectionsPage.xaml(.cs)
  Views/Pages/FriendsPage.xaml(.cs)
  Views/Pages/SettingsPage.xaml(.cs)

  ViewModels/Pages/LibraryViewModel.cs   namespace OhMyLibrary.App.ViewModels.Pages <- yours
  ViewModels/Pages/CollectionsViewModel.cs
  ViewModels/Pages/FriendsViewModel.cs
  ViewModels/Pages/SettingsViewModel.cs
```

These exact type names are already referenced from `MainWindowViewModel` (navigation targets) and
from `App.ConfigureServices` (DI registrations). **Renaming or moving them breaks the build**;
adding item view models (`GameCardViewModel`, `FriendViewModel`, …) next to them is free.

Views also live under `Views/Controls/` if a page needs a reusable `UserControl`.

---

## 4. Adding a page

1. Create `Views/Pages/<Name>Page.xaml` + `.xaml.cs` and `ViewModels/Pages/<Name>ViewModel.cs`.
2. The XAML root is `System.Windows.Controls.Page`.
3. The code-behind sets `DataContext = this` and exposes `ViewModel`, so bindings read
   `{Binding ViewModel.Something}`. This matches `MainWindow`; do not set `DataContext = viewModel`.
4. Register both in `App.ConfigureServices` as singletons (the four pages above are already there).
5. Add a `NavigationViewItem` to `MainWindowViewModel` if it needs a sidebar entry.

The container is the only source of instances: `PageService.GetPage(type)` calls
`IServiceProvider.GetService(type)`. **A page that is not registered resolves to `null` and the
navigation is silently ignored** — that is the first thing to check when a menu item does nothing.

### Minimal working page

`ViewModels/Pages/LibraryViewModel.cs`

```csharp
using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using OhMyLibrary.App.ViewModels;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Services;

namespace OhMyLibrary.App.ViewModels.Pages;

/// <summary>The library grid.</summary>
public partial class LibraryViewModel(
    IGameLibraryService library,
    ILogger<LibraryViewModel> logger) : ViewModelBase(logger)
{
    private bool _initialised;

    /// <summary>Games currently shown in the grid.</summary>
    public ObservableCollection<GameEntry> Games { get; } = [];

    /// <summary>Loads the library once, the first time the page is shown.</summary>
    public Task InitialiseAsync(CancellationToken ct = default)
    {
        if (_initialised)
        {
            return Task.CompletedTask;
        }

        _initialised = true;
        return RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private Task RefreshAsync(CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                var games = await library.GetGamesAsync(token).ConfigureAwait(true);

                Games.Clear();
                foreach (var game in games)
                {
                    Games.Add(game);
                }
            },
            "Loading library",
            ct);
}
```

`Views/Pages/LibraryPage.xaml`

```xml
<Page
    x:Class="OhMyLibrary.App.Views.Pages.LibraryPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    xmlns:pages="clr-namespace:OhMyLibrary.App.Views.Pages"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
    d:DataContext="{d:DesignInstance pages:LibraryPage, IsDesignTimeCreatable=False}"
    d:DesignHeight="600"
    d:DesignWidth="900"
    ui:Design.Background="{DynamicResource ApplicationBackgroundBrush}"
    mc:Ignorable="d">

    <Grid Style="{StaticResource OmlPageRoot}">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <TextBlock
            Grid.Row="0"
            Style="{StaticResource OmlPageTitleText}"
            Text="Library" />

        <TextBlock
            Grid.Row="1"
            Style="{StaticResource OmlErrorText}"
            Text="{Binding ViewModel.ErrorMessage, Mode=OneWay}"
            Visibility="{Binding ViewModel.HasError, Mode=OneWay, Converter={StaticResource BoolToVisibility}}" />

        <ui:ProgressRing
            Grid.Row="2"
            IsIndeterminate="True"
            Visibility="{Binding ViewModel.IsBusy, Mode=OneWay, Converter={StaticResource BoolToVisibility}}" />

        <ItemsControl Grid.Row="2" ItemsSource="{Binding ViewModel.Games, Mode=OneWay}">
            <ItemsControl.ItemsPanel>
                <ItemsPanelTemplate>
                    <ui:VirtualizingWrapPanel Orientation="Vertical" />
                </ItemsPanelTemplate>
            </ItemsControl.ItemsPanel>
        </ItemsControl>
    </Grid>
</Page>
```

`Views/Pages/LibraryPage.xaml.cs`

```csharp
using System.Windows.Controls;

using OhMyLibrary.App.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace OhMyLibrary.App.Views.Pages;

/// <summary>The library grid.</summary>
public partial class LibraryPage : Page, INavigableView<LibraryViewModel>, INavigationAware
{
    /// <summary>Creates the page with its injected view model.</summary>
    /// <param name="viewModel">The page's view model.</param>
    public LibraryPage(LibraryViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    /// <inheritdoc />
    public LibraryViewModel ViewModel { get; }

    /// <inheritdoc />
    public Task OnNavigatedToAsync() => ViewModel.InitialiseAsync();

    /// <inheritdoc />
    public Task OnNavigatedFromAsync() => Task.CompletedTask;
}
```

Registration, already present in `App.ConfigureServices`:

```csharp
_ = services.AddSingleton<LibraryPage>();
_ = services.AddSingleton<LibraryViewModel>();
```

> **Verified at runtime:** WPF-UI 4.3.0 raises `INavigationAware` on the **page**, not on the view
> model, even when the view model implements the interface. Implement it on the page and forward to
> the view model, exactly as above.

There is no `BoolToVisibility` converter in the shared dictionary yet — add one to
`Styles/Theme.xaml` (or use WPF's `BooleanToVisibilityConverter`) as part of the page stage, and
keep it there so all four pages share it.

---

## 5. `ViewModelBase` — the rule for every command body

```csharp
public abstract partial class ViewModelBase(ILogger logger) : ObservableObject
{
    protected ILogger Logger { get; }

    public bool IsBusy { get; }            // [ObservableProperty]
    public string? BusyMessage { get; }    // [ObservableProperty]
    public string? ErrorMessage { get; }   // [ObservableProperty]
    public bool HasError { get; }          // derived from ErrorMessage
    public void ClearError();

    protected Task RunGuardedAsync(
        Func<CancellationToken, Task> operation,
        string busyMessage,
        CancellationToken ct = default);

    protected virtual string Describe(Exception exception);
}
```

Rules:

- **Every** `[RelayCommand]` body goes through `RunGuardedAsync`. That is the only reason a service
  bug reaches the user as one red line instead of a vanished window.
- Call it from the dispatcher thread; it writes observable properties. Inside the operation, use
  `ConfigureAwait(true)` for the same reason.
- A cancellation through the passed token is silent — no error message. Anything else is logged at
  Error and lands in `ErrorMessage`.
- Overlapping calls are reference-counted: `IsBusy` clears when the last one finishes.
- Derive every page view model, and every item view model that runs work, from `ViewModelBase`.
  Pure data holders can stay on `ObservableObject`.
- Use `[ObservableProperty]` and `[RelayCommand]` from `CommunityToolkit.Mvvm`. No hand-written
  `INotifyPropertyChanged`, no `Set(ref …)`.

---

## 6. Images

Never build a `BitmapImage` in a page. Ask `IImageCacheService`, which decodes at the requested
width, freezes the result, keeps an LRU of 768 bitmaps and mirrors CDN downloads under
`%LOCALAPPDATA%\OhMyLibrary\imagecache\`.

```csharp
public interface IImageCacheService
{
    Task<ImageSource?> GetImageAsync(string? source, int decodePixelWidth, CancellationToken ct = default);
    bool TryGetCached(string? source, int decodePixelWidth, out ImageSource? image);
    int Invalidate(IEnumerable<string?> sources);
    void ClearMemory();
    Task<long> ClearDiskAsync(CancellationToken ct = default);
}
```

The binding pattern — the item view model owns an `ImageSource?` property:

```csharp
public partial class GameCardViewModel(GameEntry entry, IImageCacheService images, ILogger<GameCardViewModel> logger)
    : ViewModelBase(logger)
{
    [ObservableProperty]
    private ImageSource? _cover;

    public GameEntry Entry { get; } = entry;

    public async Task LoadCoverAsync(int cardWidth, CancellationToken ct = default)
    {
        // Local capsule first, CDN second; null means "show the placeholder".
        var local = Entry.Assets?.BestCover;
        Cover = await images.GetImageAsync(local, cardWidth, ct).ConfigureAwait(true)
             ?? await images.GetImageAsync(CoverUrlFallback, cardWidth, ct).ConfigureAwait(true);
    }
}
```

```xml
<Border Style="{StaticResource OmlCardCover}" Width="{Binding CardWidth}">
    <Image Source="{Binding Cover, Mode=OneWay}" Style="{StaticResource OmlCardCoverImage}" />
</Border>
```

Rules:

- **Always pass a real `decodePixelWidth`** — the card width in pixels. `0` decodes at native size
  and is only for a single hero image. A 3000-game grid at native size will exhaust memory.
- `null` is the normal "no art" answer: a missing file, a 404, an undecodable JPEG, no network. The
  card shows `OmlPlaceholderCoverBrush`. The service never throws for those.
- A 404 or an undecodable file is remembered so it is not retried; a timeout or an offline network
  is **not** remembered, so it retries next time.
- The cache is keyed by **source**, not by content, and Steam rewrites an app's art *at the path it
  used before*. `Invalidate` is what drops the decoded bitmaps for a set of sources — every decode
  width of each — and returns how many it dropped. It walks the cache once for the whole batch, so
  handing it a burst that names thousands of apps costs the burst plus the cache rather than their
  product. A page never calls it: `LibrarySyncCoordinator` owns that chain, dropping the resolver's
  cached paths, then these bitmaps, then the library's built rows, in that order, whenever the
  watcher reports a `LibraryCache` change. A card re-decodes because `GameEntry.Assets` came back
  with a new `GameAssets.Revision`, which is how "Steam rewrote the same file" is told apart from
  "nothing about the art moved".
- **Only a card a container has realised ever decodes anything.** `GameCard`'s `Loaded` and
  `DataContextChanged` handlers call `EnsureCoverAsync`, which latches, so realisation is the one
  trigger. `GameCardViewModel.Update` re-decodes *only* when the art moved **and** that latch is
  already set; a card that was never on screen is left alone and reads the new path itself when it
  is finally realised. This is not an optimisation, it is the difference between a usable grid and a
  dead process: the grid virtualises (section 11), so a library of 3000 owned games has about 200
  realised containers, and a rule that decoded for every card instead turned one `LibraryChanged`
  event into 3000 file reads, 3000 JPEG decodes and — for every app Steam has cached no art for —
  3000 CDN requests, on a background refresh nobody asked for. The old condition also included
  `Cover is null`, which is permanently true for a card with no art, so those cards re-decoded on
  *every* event forever. Measured on a synthetic 20 000-game library: 20 000 decode requests per
  event before, 0 after.
- The only exception that escapes is `OperationCanceledException` from your own token — which
  `RunGuardedAsync` already swallows.
- `ILibraryAssetResolver.GetCoverUrlFallback(appId)` (Core) gives the CDN URL. Do not hardcode it.

---

## 7. Settings

`ISettingsService` owns the user-editable configuration and is the Settings page's whole model:

```csharp
UserSettings Current { get; }          // ApiKey, SteamId64, Language, Theme, CardWidth
string MaskedApiKey { get; }           // "••••••••••••6789"
bool HasApiKey { get; }
event EventHandler<UserSettingsChangedEventArgs>? Changed;
Task<UserSettings> LoadAsync(CancellationToken ct = default);
Task<UserSettings> SaveAsync(UserSettings settings, CancellationToken ct = default);
```

- Bind the page to `Current.Clone()`, never to `Current` itself — otherwise a half-typed API key
  becomes live configuration on every keystroke.
- `SaveAsync` normalises and returns the values that were actually stored: theme snaps to
  `System`/`Light`/`Dark`, language is lower-cased, `CardWidth` is clamped to 120–420, and a
  SteamID64 that is not a positive decimal number is dropped. Re-bind to the returned instance so
  the page shows what was really saved.
- **Never render the raw API key and never log it.** Show `MaskedApiKey`; bind the editor to a
  `PasswordBox` or a text box the user only ever overwrites.
- Saving the theme is enough — `MainWindow` listens to `Changed` and re-applies it. Do not call
  `ApplicationThemeManager` from a page.
- The file is `%LOCALAPPDATA%\OhMyLibrary\user-settings.json`, layered over `appsettings.json` and
  `appsettings.local.json` as the last JSON configuration source, so a save also reaches
  `IOptionsMonitor<SteamOptions>` / `IOptionsMonitor<UiOptions>` once the file watcher fires. If you
  need a live value in a page, take `IOptionsMonitor<T>`, not `IOptions<T>`.

Configuration precedence, lowest to highest:
`appsettings.json` → `appsettings.local.json` → `user-settings.json` → environment variables
(unprefixed, then `OHMYLIBRARY_`-prefixed).

---

## 8. Shared styles

Merged from `App.xaml`; reference the keys with `StaticResource` from a page.

`Styles/Theme.xaml`

| Key | Type | Use |
|---|---|---|
| `OmlGutterSmall` / `OmlGutter` / `OmlGutterLarge` | `Double` 4 / 8 / 16 | the spacing scale — everything is a multiple of 4 |
| `OmlPagePadding` | `Thickness` 24,12,24,24 | outer page margin |
| `OmlSectionSpacing` | `Thickness` | gap under a section |
| `OmlCardMargin`, `OmlCardPadding` | `Thickness` | card gaps |
| `OmlCardCornerRadius`, `OmlBadgeCornerRadius` | `CornerRadius` | 8 / 4 |
| `OmlCoverAspectRatio` | `Double` 1.5 | capsule height = width × 1.5 |
| `OmlPlaceholderCoverBrush` | `Brush` | stand-in for a game with no art |
| `OmlPageTitleText`, `OmlSectionHeaderText`, `OmlBodyText`, `OmlSubtleText`, `OmlErrorText` | `Style` for `TextBlock` | typography ramp |
| `OmlPageRoot` | `Style` for `Grid` | put it on the page's root grid so all four pages line up |

`Styles/Cards.xaml`

| Key | Target | Use |
|---|---|---|
| `OmlCardBorder` | `Border` | outer card chrome, with a hover state |
| `OmlCardCover` | `Border` | the cover box; set `Width` to `UiOptions.CardWidth` |
| `OmlCardCoverImage` | `Image` | `UniformToFill`, high-quality scaling |
| `OmlCardTitle` | `TextBlock` | the game name |
| `OmlCardCaption` | `TextBlock` | one muted metadata line |
| `OmlCardBadge` | `Border` | pill overlaid on the cover |
| `OmlCardProgress` | `ProgressBar` | download strip, `Minimum` 0 / `Maximum` 1 — bind straight to `GameEntry.DownloadProgress` |

New shared visuals go in these two files with the `Oml` prefix. Page-specific resources belong in
the page's own `<Page.Resources>`.

---

## 9. Snackbar and dialogs

Both are hosted by `MainWindow`; a page just injects the service.

```csharp
snackbarService.Show(
    "Steam not found",
    "Install Steam, or set Steam:OverrideSteamPath.",
    ControlAppearance.Caution,
    new SymbolIcon(SymbolRegular.Warning24),
    TimeSpan.FromSeconds(5));

var result = await contentDialogService.ShowAsync(
    new ContentDialog
    {
        Title = "Delete collection",
        Content = "This cannot be undone.",
        PrimaryButtonText = "Delete",
        CloseButtonText = "Cancel",
    },
    ct);
```

Use the snackbar for a degraded state that is informational (no API key, private profile, unplugged
library drive) and `ErrorMessage` for the failure of something the user just asked for.

---

## 10. Gotchas found while building the shell

- **`NavigationView.SelectedItem` lags.** During the `Navigated` event it still holds the *previous*
  entry. Resolve the current page from `NavigatedEventArgs.Page`, which is the page instance
  (`MainWindowViewModel.FindTitle(Type?)` does this).
- **`INavigationAware` fires on the page, not on the view model.** Verified by running it.
- **`IContentDialogService.SetDialogHost(ContentPresenter)` is deprecated** in 4.3; the
  `ContentDialogHost` overload is the live one, and that is what `MainWindow.xaml` declares.
- **The WPF SDK does not glob images.** A `.png` referenced by a `pack://` URI needs an explicit
  `<Resource Include="…" />` item in the `.csproj`. `Assets\logo.png` already has one.
- **Cross-dictionary keys need `DynamicResource`.** `Styles/Cards.xaml` cannot see
  `Styles/Theme.xaml` at parse time; from a *page*, both are already merged into
  `Application.Resources`, so `StaticResource` is fine there.
- **Mica is Windows 11 only.** `MainWindow` probes `WindowBackdrop.IsSupported` and falls back to
  `WindowBackdropType.None` plus the solid `ApplicationBackgroundBrush`. Do not assume a transparent
  window background in a page.
- **`Freeze()` every bitmap** you create yourself, and create it off the dispatcher. That is what
  makes it legal to hand between threads and cheap for WPF to render — `ImageCacheService` already
  does both.

---

## 11. Scaling the library grid

The plan's premise is the **whole** owned library, and a Steam account with 3000+ games is ordinary.
Two rules keep that from killing the process, and both were measured rather than assumed.

**The grid virtualises, and it must keep wrapping while it does.** `LibraryPage` binds a `ListBox`
(for selection and keyboard navigation) whose `ItemsPanel` is WPF-UI's
`Wpf.Ui.Controls.VirtualizingWrapPanel`. WPF's own `VirtualizingStackPanel` cannot wrap and a plain
`WrapPanel` realises every container, so neither is an option. What the panel needs from the page:

| Setting | Why |
|---|---|
| `ScrollViewer.CanContentScroll="True"` | without it the `ScrollViewer` scrolls a rendered surface and the panel never gets to virtualise |
| `VirtualizingPanel.IsVirtualizing="True"`, `VirtualizationMode="Recycling"` | containers are reused instead of rebuilt |
| `VirtualizingPanel.ScrollUnit="Pixel"`, `CacheLength="1,1"` / `CacheLengthUnit="Page"` | smooth pixel scrolling with a page of cards realised either side of the viewport |
| `ItemSize` bound to `LibraryViewModel.CardSize` | authoritative cell size, so the panel lays out cells instead of measuring every card |

Measured at 1600x900 with a real page: **224 containers realised for 1 500 items and the same 224
for 6 000** — what gets realised is a property of the window, not of the library.
`LibraryScalingTests.TheGridRealisesAViewportOfCardsRatherThanTheWholeLibrary` asserts exactly that.

**No per-row work proportional to the library on a library event.** A virtualising panel is worth
nothing if the view model does the per-card work itself. `GameCardViewModel.Update` runs for every
row of every refresh, so anything it does costs the whole library; the cover rule in section 6 is
the concrete case, and it is the one that made a 20 000-game grid permanently unresponsive. When you
add work to `Update`, ask what it costs times three thousand.

**The measurements.** `OhMyLibrary.Tests/Performance/` holds both halves:

- `LibraryScalingTests` — the guard, always run, `[Trait("Category", "Slow")]`. It builds real
  manifest trees at N and 4N and asserts a ratio, not a millisecond budget: a linear stage grows 4x,
  quadratic would be 16x, and the ceiling is 10x. Absolute budgets on unknown hardware are noise.
  A fast loop is `dotnet test --filter "Category!=Slow"`.
- `LibraryScalingBenchmark` — the harness the numbers came from. Skipped unless `OHMYLIBRARY_BENCH`
  is set, in the same spirit as `RequiresFixtureOutputFact`; `OHMYLIBRARY_BENCH_SIZES` picks the
  library sizes. It writes only into a temp tree, never into a real Steam directory.
