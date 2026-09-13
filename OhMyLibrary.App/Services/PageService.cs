using System.Windows;

using Wpf.Ui.Abstractions;

namespace OhMyLibrary.App.Services;

/// <summary>
/// Resolves navigation targets out of the DI container, so pages get constructor injection instead
/// of a parameterless constructor and a service locator.
/// </summary>
/// <remarks>
/// This is WPF-UI 4.x's page provider — the interface is
/// <see cref="INavigationViewPageProvider"/> from <c>Wpf.Ui.Abstractions</c>. The 3.x name
/// <c>IPageService</c> no longer exists. Every page a <c>NavigationViewItem</c> targets must be
/// registered in the container in <c>App.ConfigureServices</c>, otherwise
/// <see cref="GetPage"/> returns <see langword="null"/> and the navigation is silently ignored.
/// </remarks>
/// <param name="serviceProvider">The application's root service provider.</param>
public sealed class PageService(IServiceProvider serviceProvider) : INavigationViewPageProvider
{
    /// <summary>
    /// Resolves a page instance for a navigation target.
    /// </summary>
    /// <param name="pageType">The page type declared by the navigation item.</param>
    /// <returns>The page, or <see langword="null"/> when it is not registered.</returns>
    /// <exception cref="InvalidOperationException">
    /// The requested type is not a <see cref="FrameworkElement"/>, which means a navigation item was
    /// pointed at a view model or a service by mistake.
    /// </exception>
    public object? GetPage(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);

        if (!typeof(FrameworkElement).IsAssignableFrom(pageType))
        {
            throw new InvalidOperationException(
                $"{pageType.FullName} is not a FrameworkElement and cannot be a navigation target.");
        }

        return serviceProvider.GetService(pageType);
    }
}
