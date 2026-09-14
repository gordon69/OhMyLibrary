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
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        ContextMenuOpening += OnContextMenuOpening;
    }

    private GameCardViewModel? ViewModel => DataContext as GameCardViewModel;

    /// <summary>True once this container has told a view model it is showing it.</summary>
    /// <remarks>
    /// Realisation arrives twice for the same card — <c>Loaded</c> and <c>DataContextChanged</c> both
    /// fire when a container first appears — and the view model counts realisations, so the control
    /// has to report each transition once rather than once per event.
    /// </remarks>
    private GameCardViewModel? _realised;

    private void OnLoaded(object sender, RoutedEventArgs e) => Realise(ViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e) => Realise(null);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        // Recycling hands this container a different game. The card that was here stops being on
        // screen at exactly this moment, and if nobody says so it counts as realised for the rest of
        // the session — which is what made one art change re-decode every card the user had ever
        // scrolled past.
        Realise(IsLoaded ? ViewModel : null);

    private void Realise(GameCardViewModel? viewModel)
    {
        if (ReferenceEquals(_realised, viewModel))
        {
            return;
        }

        _realised?.OnUnrealised();
        _realised = viewModel;
        _realised?.OnRealised(viewModel?.Owner.PageLifetime ?? default);
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // The context commands act on the page's selection, so opening the menu is what selects the
        // card under the cursor. Without this a right-click would act on whatever was clicked last.
        if (ViewModel is { } viewModel)
        {
            viewModel.Owner.SelectedGame = viewModel;
        }
    }

}
