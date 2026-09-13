using System.Collections.ObjectModel;
using System.Collections.Specialized;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using OhMyLibrary.App.Services;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Services;

using Wpf.Ui;
using Wpf.Ui.Controls;

namespace OhMyLibrary.App.ViewModels.Pages;

/// <summary>
/// The collections page: create, rename, reorder and delete collections, and move games in and out
/// of them.
/// </summary>
/// <remarks>
/// <para>
/// Collections are ours alone - deleting one drops the membership rows and never touches a game.
/// The confirmation dialog says so, because "delete" next to a wall of cover art reads as
/// destructive whether it is or not.
/// </para>
/// <para>
/// Membership comes from <see cref="ICollectionService"/> rather than from
/// <c>GameEntry.CollectionIds</c>: the library service caches its rows and has no invalidation hook
/// for a collection edit, so its copy of the membership goes stale the moment this page writes.
/// </para>
/// </remarks>
public partial class CollectionsViewModel : ViewModelBase
{
    /// <summary>Decode width of a cover in the strip under a collection name.</summary>
    public const int StripCoverWidth = 46;

    /// <summary>Decode width of a cover in the member grid.</summary>
    public const int MemberCoverWidth = 110;

    /// <summary>How many covers the strip under a collection name shows.</summary>
    public const int StripLength = 6;

    /// <summary>Upper bound on the rows the add-a-game search returns.</summary>
    public const int SearchResultLimit = 60;

    private readonly ICollectionService _collections;
    private readonly IGameLibraryService _library;
    private readonly ILibraryAssetResolver _assets;
    private readonly IImageCacheService _images;
    private readonly IContentDialogService _dialogs;

    private readonly Dictionary<int, GameEntry> _gamesById = [];

    private int _membersGeneration;

    /// <summary>Creates the page view model.</summary>
    /// <param name="collections">Collection storage.</param>
    /// <param name="library">Source of game names for the member and search lists.</param>
    /// <param name="assets">Resolver for locally cached cover art.</param>
    /// <param name="images">Decoder and cache for the cover thumbnails.</param>
    /// <param name="dialogs">Host for the delete confirmation.</param>
    /// <param name="logger">Log sink for guarded failures.</param>
    public CollectionsViewModel(
        ICollectionService collections,
        IGameLibraryService library,
        ILibraryAssetResolver assets,
        IImageCacheService images,
        IContentDialogService dialogs,
        ILogger<CollectionsViewModel> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(dialogs);

        _collections = collections;
        _library = library;
        _assets = assets;
        _images = images;
        _dialogs = dialogs;

        Collections.CollectionChanged += OnCollectionsChanged;
        Members.CollectionChanged += OnMembersChanged;

        // IsBusy lives on the base class, so its generated change hook is not ours to implement.
        PropertyChanged += OnSelfPropertyChanged;
    }

    /// <summary>Every collection, in sidebar order.</summary>
    public ObservableCollection<CollectionItemViewModel> Collections { get; } = [];

    /// <summary>The games in <see cref="SelectedCollection"/>, in collection order.</summary>
    public ObservableCollection<CollectionGameViewModel> Members { get; } = [];

    /// <summary>Library games matching <see cref="GameSearchText"/> that are not already members.</summary>
    public ObservableCollection<CollectionGameViewModel> SearchResults { get; } = [];

    /// <summary>The collection whose contents the right-hand pane shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionIsEmpty))]
    private CollectionItemViewModel? _selectedCollection;

    /// <summary>Name typed into the "new collection" box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCollectionCommand))]
    private string _newCollectionName = string.Empty;

    /// <summary>Filter for the add-a-game list.</summary>
    [ObservableProperty]
    private string _gameSearchText = string.Empty;

    /// <summary>True when at least one collection exists.</summary>
    public bool HasCollections => Collections.Count > 0;

    /// <summary>True when the empty-state panel should replace the list.</summary>
    public bool ShowEmptyState => Collections.Count == 0 && !IsBusy;

    /// <summary>True when a collection is selected and the detail pane has something to show.</summary>
    public bool HasSelection => SelectedCollection is not null;

    /// <summary>Inverse of <see cref="HasSelection"/>, for the "pick a collection" placeholder.</summary>
    public bool HasNoSelection => SelectedCollection is null;

    /// <summary>True when the selected collection holds no games yet.</summary>
    public bool SelectionIsEmpty => HasSelection && Members.Count == 0;

    /// <summary>
    /// Reloads collections and the library index. Called on every navigation, not just the first,
    /// so names and art for games added since the last visit are present. Selection is preserved by
    /// id across the reload.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task InitialiseAsync(CancellationToken ct = default) =>
        RunGuardedAsync(LoadAsync, "Loading collections", ct);

    /// <summary>
    /// Moves a collection to a new position and persists the whole order.
    /// </summary>
    /// <remarks>
    /// Public because the page's drag-and-drop handler calls it with indices taken from the item
    /// containers. <c>ICollectionRepository.ReorderAsync</c> only rewrites the ids it is given, so
    /// the complete list is always sent.
    /// </remarks>
    /// <param name="fromIndex">Current index of the dragged collection.</param>
    /// <param name="toIndex">Index it should end up at.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task MoveAsync(int fromIndex, int toIndex, CancellationToken ct = default)
    {
        if (fromIndex == toIndex
            || fromIndex < 0 || fromIndex >= Collections.Count
            || toIndex < 0 || toIndex >= Collections.Count)
        {
            return Task.CompletedTask;
        }

        return RunGuardedAsync(
            async token =>
            {
                Collections.Move(fromIndex, toIndex);
                await PersistOrderAsync(token).ConfigureAwait(true);
            },
            "Reordering collections",
            ct);
    }

    [RelayCommand(CanExecute = nameof(CanCreateCollection))]
    private Task CreateCollectionAsync(CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                var name = NewCollectionName.Trim();
                if (name.Length == 0)
                {
                    return;
                }

                var created = await _collections.CreateAsync(name, token).ConfigureAwait(true);
                NewCollectionName = string.Empty;

                var item = new CollectionItemViewModel(created);
                Collections.Add(item);
                SelectedCollection = item;
            },
            "Creating collection",
            ct);

    private bool CanCreateCollection() => !string.IsNullOrWhiteSpace(NewCollectionName);

    [RelayCommand]
    private void BeginRename(CollectionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        ClearError();
        item.EditName = item.Name;
        item.IsEditing = true;
    }

    [RelayCommand]
    private void CancelRename(CollectionItemViewModel? item)
    {
        if (item is not null)
        {
            item.IsEditing = false;
        }
    }

    [RelayCommand]
    private Task CommitRenameAsync(CollectionItemViewModel? item, CancellationToken ct)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var name = item.EditName.Trim();
        item.IsEditing = false;

        if (name.Length == 0 || string.Equals(name, item.Name, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return RunGuardedAsync(
            async token =>
            {
                await _collections.RenameAsync(item.CollectionId, name, token).ConfigureAwait(true);
                item.Name = name;
            },
            "Renaming collection",
            ct);
    }

    [RelayCommand]
    private Task DeleteCollectionAsync(CollectionItemViewModel? item, CancellationToken ct)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        return RunGuardedAsync(
            async token =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Delete collection",
                    Content = $"\"{item.Name}\" will be removed from the sidebar. The games in it "
                        + "stay in your library - only the collection itself is deleted.",
                    PrimaryButtonText = "Delete",
                    PrimaryButtonAppearance = ControlAppearance.Danger,
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };

                var result = await _dialogs.ShowAsync(dialog, token).ConfigureAwait(true);
                if (result != ContentDialogResult.Primary)
                {
                    return;
                }

                await _collections.DeleteAsync(item.CollectionId, token).ConfigureAwait(true);

                var index = Collections.IndexOf(item);
                if (index >= 0)
                {
                    Collections.RemoveAt(index);
                }

                if (ReferenceEquals(SelectedCollection, item))
                {
                    SelectedCollection = Collections.Count == 0
                        ? null
                        : Collections[Math.Clamp(index, 0, Collections.Count - 1)];
                }
            },
            "Deleting collection",
            ct);
    }

    [RelayCommand]
    private Task MoveUpAsync(CollectionItemViewModel? item, CancellationToken ct)
    {
        var index = item is null ? -1 : Collections.IndexOf(item);
        return index <= 0 ? Task.CompletedTask : MoveAsync(index, index - 1, ct);
    }

    [RelayCommand]
    private Task MoveDownAsync(CollectionItemViewModel? item, CancellationToken ct)
    {
        var index = item is null ? -1 : Collections.IndexOf(item);
        return index < 0 || index >= Collections.Count - 1
            ? Task.CompletedTask
            : MoveAsync(index, index + 1, ct);
    }

    [RelayCommand]
    private Task AddGameAsync(CollectionGameViewModel? game, CancellationToken ct)
    {
        var target = SelectedCollection;
        if (game is null || target is null || target.AppIds.Contains(game.AppId))
        {
            return Task.CompletedTask;
        }

        return RunGuardedAsync(
            async token =>
            {
                await _collections.AddGameAsync(target.CollectionId, game.AppId, token).ConfigureAwait(true);
                target.SetMembers([.. target.AppIds, game.AppId]);
                await ReloadMembersAsync(target, token).ConfigureAwait(true);
            },
            "Adding to collection",
            ct);
    }

    [RelayCommand]
    private Task RemoveGameAsync(CollectionGameViewModel? game, CancellationToken ct)
    {
        var target = SelectedCollection;
        if (game is null || target is null)
        {
            return Task.CompletedTask;
        }

        return RunGuardedAsync(
            async token =>
            {
                await _collections.RemoveGameAsync(target.CollectionId, game.AppId, token).ConfigureAwait(true);
                target.SetMembers([.. target.AppIds.Where(id => id != game.AppId)]);
                await ReloadMembersAsync(target, token).ConfigureAwait(true);
            },
            "Removing from collection",
            ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var games = await _library.GetGamesAsync(ct).ConfigureAwait(true);

        _gamesById.Clear();
        foreach (var game in games)
        {
            _gamesById[game.AppId] = game;
        }

        var stored = await _collections.GetAllAsync(ct).ConfigureAwait(true);
        var previousId = SelectedCollection?.CollectionId;

        Collections.Clear();
        foreach (var collection in stored)
        {
            Collections.Add(new CollectionItemViewModel(collection));
        }

        SelectedCollection =
            Collections.FirstOrDefault(item => item.CollectionId == previousId)
            ?? Collections.FirstOrDefault();

        await LoadStripsAsync(ct).ConfigureAwait(true);
    }

    private async Task LoadStripsAsync(CancellationToken ct)
    {
        foreach (var item in Collections)
        {
            ct.ThrowIfCancellationRequested();

            var covers = await BuildGamesAsync([.. item.AppIds.Take(StripLength)], ct).ConfigureAwait(true);

            item.Covers.Clear();
            foreach (var cover in covers)
            {
                item.Covers.Add(cover);
            }

            await LoadCoversAsync(covers, StripCoverWidth, ct).ConfigureAwait(true);
        }
    }

    private async Task ReloadMembersAsync(CollectionItemViewModel item, CancellationToken ct)
    {
        var generation = ++_membersGeneration;

        var members = await BuildGamesAsync(item.AppIds, ct).ConfigureAwait(true);
        if (generation != _membersGeneration || !ReferenceEquals(SelectedCollection, item))
        {
            return;
        }

        Members.Clear();
        foreach (var member in members)
        {
            Members.Add(member);
        }

        UpdateSearchResults();

        var strip = members.Take(StripLength).ToArray();
        item.Covers.Clear();
        foreach (var cover in strip)
        {
            item.Covers.Add(cover);
        }

        await LoadCoversAsync(strip, StripCoverWidth, ct).ConfigureAwait(true);
        await LoadCoversAsync(members, MemberCoverWidth, ct).ConfigureAwait(true);
    }

    private async Task<IReadOnlyList<CollectionGameViewModel>> BuildGamesAsync(
        IReadOnlyList<int> appIds,
        CancellationToken ct)
    {
        if (appIds.Count == 0)
        {
            return [];
        }

        var ordered = appIds.Distinct().ToArray();

        // Resolve() walks the library cache directory, so keep it off the dispatcher.
        var art = await Task.Run(
            () => ordered.ToDictionary(
                id => id,
                id => (Local: _assets.Resolve(id).BestCover, Url: _assets.GetCoverUrlFallback(id))),
            ct).ConfigureAwait(true);

        return
        [
            .. ordered.Select(id => new CollectionGameViewModel(
                id,
                _gamesById.TryGetValue(id, out var entry) ? entry.Name : $"App {id}",
                art[id].Local,
                art[id].Url)),
        ];
    }

    private async Task LoadCoversAsync(
        IReadOnlyList<CollectionGameViewModel> items,
        int decodeWidth,
        CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            if (item.Cover is not null)
            {
                continue;
            }

            item.Cover =
                await _images.GetImageAsync(item.LocalCoverPath, decodeWidth, ct).ConfigureAwait(true)
                ?? await _images.GetImageAsync(item.CoverUrl, decodeWidth, ct).ConfigureAwait(true);
        }
    }

    private Task PersistOrderAsync(CancellationToken ct) =>
        _collections.ReorderAsync([.. Collections.Select(item => item.CollectionId)], ct);

    private void UpdateSearchResults()
    {
        SearchResults.Clear();

        var target = SelectedCollection;
        if (target is null)
        {
            return;
        }

        var term = GameSearchText.Trim();
        var members = target.AppIds.ToHashSet();

        var matches = _gamesById.Values
            .Where(entry => !members.Contains(entry.AppId))
            .Where(entry => term.Length == 0
                || entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(SearchResultLimit);

        foreach (var entry in matches)
        {
            SearchResults.Add(new CollectionGameViewModel(entry.AppId, entry.Name, null, null));
        }
    }

    partial void OnSelectedCollectionChanged(CollectionItemViewModel? value)
    {
        Members.Clear();
        SearchResults.Clear();

        if (value is null)
        {
            return;
        }

        _ = RunGuardedAsync(token => ReloadMembersAsync(value, token), "Loading collection");
    }

    partial void OnGameSearchTextChanged(string value) => UpdateSearchResults();

    private void OnSelfPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBusy))
        {
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private void OnCollectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasCollections));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(SelectionIsEmpty));
}
