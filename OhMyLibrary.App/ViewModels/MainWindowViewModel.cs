using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using OhMyLibrary.App.Views.Pages;

using Wpf.Ui.Controls;

namespace OhMyLibrary.App.ViewModels;

/// <summary>
/// Shell state: the navigation items and the window title.
/// </summary>
/// <remarks>
/// The navigation items are the single place where a page type is named. Adding a page means adding
/// a <see cref="NavigationViewItem"/> here and a DI registration in <c>App.ConfigureServices</c> —
/// nothing else in the shell needs to change.
/// </remarks>
public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Product name shown in the title bar and used as the window-title suffix.</summary>
    public const string ApplicationTitle = "OhMyLibrary";

    /// <summary>Creates the shell view model with its four navigation targets.</summary>
    public MainWindowViewModel()
    {
        NavigationItems =
        [
            new NavigationViewItem("Library", SymbolRegular.Library24, typeof(LibraryPage)),
            new NavigationViewItem("Collections", SymbolRegular.BookmarkMultiple24, typeof(CollectionsPage)),
            new NavigationViewItem("Friends", SymbolRegular.People24, typeof(FriendsPage)),
        ];

        FooterNavigationItems =
        [
            new NavigationViewItem("Settings", SymbolRegular.Settings24, typeof(SettingsPage)),
        ];
    }

    /// <summary>
    /// The main navigation entries, bound to <c>NavigationView.MenuItemsSource</c>.
    /// </summary>
    public ObservableCollection<object> NavigationItems { get; }

    /// <summary>
    /// The pinned bottom entries, bound to <c>NavigationView.FooterMenuItemsSource</c>. Settings
    /// lives here, which is the Fluent convention.
    /// </summary>
    public ObservableCollection<object> FooterNavigationItems { get; }

    /// <summary>
    /// The page shown when the window opens: the first main navigation entry.
    /// </summary>
    public Type? StartPageType =>
        NavigationItems.OfType<NavigationViewItem>().FirstOrDefault()?.TargetPageType;

    /// <summary>
    /// The label of the navigation entry that targets a page type.
    /// </summary>
    /// <param name="pageType">The page that was navigated to, or <see langword="null"/>.</param>
    /// <returns>The entry's label, or <see langword="null"/> when nothing targets that page.</returns>
    public string? FindTitle(Type? pageType) =>
        pageType is null
            ? null
            : NavigationItems
                .Concat(FooterNavigationItems)
                .OfType<NavigationViewItem>()
                .FirstOrDefault(item => item.TargetPageType == pageType)?.Content as string;

    /// <summary>Title of the page currently shown, or <see langword="null"/> before the first navigation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string? _currentPageTitle;

    /// <summary>Title bar and taskbar text: the current page followed by the product name.</summary>
    public string WindowTitle =>
        string.IsNullOrWhiteSpace(CurrentPageTitle)
            ? ApplicationTitle
            : $"{CurrentPageTitle} — {ApplicationTitle}";
}
