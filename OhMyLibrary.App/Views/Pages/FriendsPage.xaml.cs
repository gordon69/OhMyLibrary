using System.Windows.Controls;

using OhMyLibrary.App.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace OhMyLibrary.App.Views.Pages;

/// <summary>
/// The friends list. A friend with a private library is shown as hidden, never as owning nothing.
/// </summary>
public partial class FriendsPage : Page, INavigableView<FriendsViewModel>, INavigationAware
{
    /// <summary>Creates the page with its injected view model.</summary>
    /// <param name="viewModel">The page's view model.</param>
    public FriendsPage(FriendsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    /// <inheritdoc />
    public FriendsViewModel ViewModel { get; }

    /// <inheritdoc />
    public Task OnNavigatedToAsync() => ViewModel.InitialiseAsync();

    /// <inheritdoc />
    public Task OnNavigatedFromAsync() => Task.CompletedTask;
}
