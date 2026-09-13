using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using OhMyLibrary.App.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace OhMyLibrary.App.Views.Pages;

/// <summary>
/// The collections page. Everything stateful lives on <see cref="CollectionsViewModel"/>; the
/// code-behind exists only for drag-to-reorder, which needs the item containers.
/// </summary>
public partial class CollectionsPage : Page, INavigableView<CollectionsViewModel>, INavigationAware
{
    private Point _dragOrigin;
    private CollectionItemViewModel? _dragged;

    /// <summary>Creates the page with its injected view model.</summary>
    /// <param name="viewModel">The page's view model.</param>
    public CollectionsPage(CollectionsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    /// <inheritdoc />
    public CollectionsViewModel ViewModel { get; }

    /// <inheritdoc />
    public Task OnNavigatedToAsync() => ViewModel.InitialiseAsync();

    /// <inheritdoc />
    public Task OnNavigatedFromAsync() => Task.CompletedTask;

    private static T? FindAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private void OnCollectionsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(null);
        _dragged = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext
            as CollectionItemViewModel;
    }

    private void OnCollectionsPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragged is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // A rename or a row button must not be hijacked by an accidental one-pixel drag.
        var travelled = e.GetPosition(null) - _dragOrigin;
        if (Math.Abs(travelled.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(travelled.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var payload = _dragged;
        _dragged = null;

        _ = DragDrop.DoDragDrop(CollectionsList, new DataObject(typeof(CollectionItemViewModel), payload), DragDropEffects.Move);
    }

    private void OnCollectionsDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CollectionItemViewModel)) is not CollectionItemViewModel source)
        {
            return;
        }

        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext
            as CollectionItemViewModel;

        var from = ViewModel.Collections.IndexOf(source);

        // Dropping past the last row means "put it at the end".
        var to = target is null
            ? ViewModel.Collections.Count - 1
            : ViewModel.Collections.IndexOf(target);

        e.Handled = true;

        if (from >= 0 && to >= 0)
        {
            _ = ViewModel.MoveAsync(from, to);
        }
    }
}
