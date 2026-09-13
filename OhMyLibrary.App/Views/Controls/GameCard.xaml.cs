using System.Windows;
using System.Windows.Controls;

using OhMyLibrary.App.ViewModels.Pages;

namespace OhMyLibrary.App.Views.Controls;

/// <summary>
/// One game in the library grid: cover, name, state badges, a hover action and a context menu.
/// </summary>
/// <remarks>
/// The control is deliberately passive — every command it binds lives on the page view model,
/// reached through <see cref="GameCardViewModel.Owner"/>. The only work it does itself is asking its
/// view model for a cover when a container is realised, which is what makes lazy art loading follow
/// the virtualised viewport instead of the whole library.
/// </remarks>
public partial class GameCard : UserControl
{
    /// <summary>Creates the card.</summary>
    public GameCard()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        ContextMenuOpening += OnContextMenuOpening;
    }

    private GameCardViewModel? ViewModel => DataContext as GameCardViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e) => RequestCover();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => RequestCover();

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // The context commands act on the page's selection, so opening the menu is what selects the
        // card under the cursor. Without this a right-click would act on whatever was clicked last.
        if (ViewModel is { } viewModel)
        {
            viewModel.Owner.SelectedGame = viewModel;
        }
    }

    private void RequestCover()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        // Fire and forget on purpose: the call is idempotent, it never throws, and awaiting it here
        // would mean an async void handler for no gain. A cover simply appears when it is ready.
        _ = viewModel.EnsureCoverAsync(viewModel.Owner.PageLifetime);
    }
}
