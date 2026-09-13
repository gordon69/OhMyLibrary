using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace OhMyLibrary.App.ViewModels.Pages;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can be refilled in one notification.
/// </summary>
/// <remarks>
/// <para>
/// Loading three thousand games one <c>Add</c> at a time makes a bound
/// <see cref="System.Windows.Data.ListCollectionView"/> re-filter and binary-search-insert on every
/// item. <see cref="ListCollectionView.DeferRefresh"/> is not the way out either: mutating the source
/// while a refresh is deferred throws <see cref="InvalidOperationException"/> out of
/// <c>CollectionView.VerifyRefreshNotDeferred</c> as soon as the view has to touch the current
/// position — verified, not assumed.
/// </para>
/// <para>
/// A single <see cref="NotifyCollectionChangedAction.Reset"/> is what a collection view is built to
/// handle cheaply: one filter pass and one sort. The cost is that the bound selector drops its
/// selection, so a caller that cares restores it afterwards.
/// </para>
/// </remarks>
/// <typeparam name="T">Element type.</typeparam>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private const string IndexerName = "Item[]";

    /// <summary>Replaces the whole contents and raises one reset notification.</summary>
    /// <param name="items">The new contents, in order.</param>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();

        Items.Clear();

        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs(IndexerName));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
