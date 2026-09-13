using System.Windows.Controls;

using OhMyLibrary.App.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace OhMyLibrary.App.Views.Pages;

/// <summary>
/// The settings page: credentials, appearance, manual refreshes and read-only diagnostics.
/// </summary>
/// <remarks>
/// The API key field is a masked <c>ui:PasswordBox</c> whose reveal button is the only way to see
/// the key, and the page never writes it anywhere but <c>ISettingsService</c>.
/// </remarks>
public partial class SettingsPage : Page, INavigableView<SettingsViewModel>, INavigationAware
{
    /// <summary>Creates the page with its injected view model.</summary>
    /// <param name="viewModel">The page's view model.</param>
    public SettingsPage(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    /// <inheritdoc />
    public SettingsViewModel ViewModel { get; }

    /// <inheritdoc />
    public Task OnNavigatedToAsync() => ViewModel.InitialiseAsync();

    /// <inheritdoc />
    public Task OnNavigatedFromAsync() => Task.CompletedTask;
}
