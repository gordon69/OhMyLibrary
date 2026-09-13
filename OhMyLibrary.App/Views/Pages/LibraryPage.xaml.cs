using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using OhMyLibrary.App.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace OhMyLibrary.App.Views.Pages;

/// <summary>
/// The library grid: every owned or installed game, searchable, filterable and launchable.
/// </summary>
/// <remarks>
/// The page owns only what is genuinely a view concern — the keyboard shortcuts and the
/// double-click gesture. Everything they do is a command on <see cref="ViewModel"/>.
/// </remarks>
public partial class LibraryPage : Page, INavigableView<LibraryViewModel>, INavigationAware
{
    /// <summary>Creates the page with its injected view model.</summary>
    /// <param name="viewModel">The page's view model.</param>
    public LibraryPage(LibraryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

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

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPreviewKeyDown(e);

        if (e.Handled)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F when (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control:
                _ = SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;

            case Key.Escape:
                ViewModel.ClearFiltersCommand.Execute(null);
                MoveFocusToGrid();
                e.Handled = true;
                break;

            // Enter in the search box means "I am done typing": move to the results.
            case Key.Enter when SearchBox.IsKeyboardFocusWithin:
                MoveFocusToGrid();
                e.Handled = true;
                break;

            case Key.Enter when ViewModel.SelectedGame is { } selected:
                if (ViewModel.PrimaryActionCommand.CanExecute(selected))
                {
                    ViewModel.PrimaryActionCommand.Execute(selected);
                }

                e.Handled = true;
                break;

            default:
                break;
        }
    }

    private void OnGamesMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // A double click on the empty space below the last row must not launch the selected game.
        if (e.OriginalSource is not DependencyObject source || FindContainer(source) is null)
        {
            return;
        }

        if (ViewModel.SelectedGame is not { } card || !ViewModel.PrimaryActionCommand.CanExecute(card))
        {
            return;
        }

        ViewModel.PrimaryActionCommand.Execute(card);
        e.Handled = true;
    }

    private void MoveFocusToGrid()
    {
        if (ViewModel.SelectedGame is null && GamesList.Items.Count > 0)
        {
            GamesList.SelectedIndex = 0;
        }

        _ = GamesList.Focus();
    }

    private static ListBoxItem? FindContainer(DependencyObject source)
    {
        var current = source;

        while (current is not null and not ListBoxItem)
        {
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return current as ListBoxItem;
    }
}
