using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OhMyLibrary.App.Services;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Services;

namespace OhMyLibrary.App.ViewModels.Pages;

/// <summary>
/// The library grid: every app that belongs in the library as a card, with search, facets, sorting
/// and the install and launch commands.
/// </summary>
/// <remarks>
/// <para>
/// Which apps those are is not decided here. <c>IGameLibraryService</c> answers it once, for the
/// bulk read and for a single app alike — <c>GetGamesAsync</c> and <c>GetGridEntryAsync</c> apply
/// the same type filter, the same redistributable exclusion and the same membership rule — so a
/// partial update and a full load can never put different apps on the grid.
/// </para>
/// <para>
/// The cards live in one <see cref="ObservableCollection{T}"/> that is never rebuilt for a filter.
/// Filtering and sorting run over a private <see cref="ListCollectionView"/>, so narrowing three
/// thousand games re-evaluates a predicate instead of tearing down and recreating containers, and
/// the grid keeps its scroll position and its selection across a refresh.
/// </para>
/// <para>
/// Nothing here loads in the constructor. <see cref="InitialiseAsync"/> runs on the first navigation
/// to the page, and a degraded environment — no Steam, no API key, an empty library — resolves to an
/// explained empty state rather than a spinner that never stops.
/// </para>
/// </remarks>
public sealed partial class LibraryViewModel : ViewModelBase, IDisposable
{
    /// <summary>A Steam capsule is 300x450, so a cover is one and a half times as tall as it is wide.</summary>
    public const double CoverAspectRatio = 1.5d;

    /// <summary>How long typing pauses before the filter re-runs.</summary>
    public const int SearchDebounceMilliseconds = 250;

    /// <summary>Narrowest card the grid will lay out, matching <c>SettingsService</c>.</summary>
    public const double MinCardWidth = 120d;

    /// <summary>Widest card the grid will lay out, matching <c>SettingsService</c>.</summary>
    public const double MaxCardWidth = 420d;

    /// <summary>How many store tags the facet list offers before it stops, longest tail first.</summary>
    public const int MaxTagFilters = 60;

    // Card chrome that the panel must budget for but the cover does not cover: OmlCardPadding
    // (10+10 / 8+10), a 1px border on each side, OmlCardMargin (12 right / 12 bottom) and the two
    // text lines under the cover (title 13pt + 8 margin, caption 11pt + 2 margin).
    private const double CardChromeWidth = 34d;
    private const double CardChromeHeight = 74d;

    private readonly IGameLibraryService _library;
    private readonly IInstallStateService _installState;
    private readonly ITagService _tags;
    private readonly ICollectionService _collections;
    private readonly ILibraryAssetResolver _assets;
    private readonly ISteamUriLauncher _launcher;
    private readonly IImageCacheService _images;

    private readonly Dictionary<int, GameCardViewModel> _cards = [];
    private readonly HashSet<int> _selectedGenreIds = [];
    private readonly HashSet<int> _selectedTagIds = [];
    private readonly CollectionViewSource _viewSource;
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _searchTimer;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IDisposable? _uiOptionsSubscription;

    private string _searchKey = string.Empty;
    private bool _suppressFacetRefresh;
    private bool _initialised;
    private bool _disposed;

    /// <summary>
    /// The ticket every writer to the grid claims before it starts reading, so a slower earlier read
    /// cannot apply its stale snapshot over a newer one. On a first run that is the difference
    /// between a populated grid and an empty one: the page's own load reads the database before the
    /// startup sync has written to it, the sync's <c>LibraryChanged</c> starts a second load that
    /// reads the real rows, and whichever of the two finishes last used to win.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The protocol is the same for all three writers — <see cref="LoadCoreAsync"/>,
    /// <see cref="ReloadAsync"/> and the partial branch of <see cref="ApplyLibraryChangeAsync"/>:
    /// claim with <c>++_loadGeneration</c>, then read, then apply only while the claimed number is
    /// still the newest. Claiming and reading are both one-way, so the newest claim is also the
    /// newest read, and exactly one writer — the last one to start — applies. A writer that only
    /// <i>read</i> the counter without claiming it, as the partial path used to, is ordered against
    /// loads that start after it but not against a load already in flight or against a second
    /// partial update, and either of those can then land its older rows last.
    /// </para>
    /// <para>
    /// Dispatcher-affine, and deliberately not interlocked: every writer either runs inside a command
    /// body (dispatcher thread, per <c>ViewModelBase</c>) or is marshalled onto the dispatcher by
    /// <see cref="OnLibraryChanged"/>, so claim and compare never interleave.
    /// </para>
    /// </remarks>
    private int _loadGeneration;

    /// <summary>Creates the library page's view model. Nothing is loaded until the page is shown.</summary>
    /// <param name="library">The merged library view.</param>
    /// <param name="installState">Install, launch, uninstall and validate requests.</param>
    /// <param name="tags">Tag and genre name tables, used to fix up unresolved facet names.</param>
    /// <param name="collections">User collections, for the collection filter and the context menu.</param>
    /// <param name="assets">Local art, and the CDN cover fallback for apps with none.</param>
    /// <param name="launcher">Store and library page links.</param>
    /// <param name="images">Decoding image cache the cards bind their covers to.</param>
    /// <param name="uiOptions">Live presentation options; the grid follows <c>Ui:CardWidth</c>.</param>
    /// <param name="logger">Log sink, shared with the cards this page creates.</param>
    public LibraryViewModel(
        IGameLibraryService library,
        IInstallStateService installState,
        ITagService tags,
        ICollectionService collections,
        ILibraryAssetResolver assets,
        ISteamUriLauncher launcher,
        IImageCacheService images,
        IOptionsMonitor<UiOptions> uiOptions,
        ILogger<LibraryViewModel> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(installState);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(uiOptions);

        _library = library;
        _installState = installState;
        _tags = tags;
        _collections = collections;
        _assets = assets;
        _launcher = launcher;
        _images = images;

        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _cardWidth = ClampCardWidth(uiOptions.CurrentValue.CardWidth);

        _viewSource = new CollectionViewSource { Source = Games };
        _view = (ListCollectionView)_viewSource.View;
        _view.Filter = IsVisible;
        _view.CustomSort = new CardComparer(LibrarySortOrder.Name);

        SortOptions =
        [
            new LibrarySortOption(LibrarySortOrder.Name, "Name"),
            new LibrarySortOption(LibrarySortOrder.LastPlayed, "Last played"),
            new LibrarySortOption(LibrarySortOrder.Playtime, "Playtime"),
            new LibrarySortOption(LibrarySortOrder.Size, "Size on disk"),
            new LibrarySortOption(LibrarySortOrder.InstallState, "Install state"),
        ];

        _selectedSort = SortOptions[0];
        _selectedCollection = AllGames;
        Collections.Add(AllGames);

        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(SearchDebounceMilliseconds),
        };
        _searchTimer.Tick += OnSearchTimerTick;

        _uiOptionsSubscription = uiOptions.OnChange(OnUiOptionsChanged);
    }

    /// <summary>
    /// Cancelled when the page view model is disposed. Cards pass it to the image cache so a cover
    /// still in flight at shutdown stops instead of resurrecting a disposed view model.
    /// </summary>
    public CancellationToken PageLifetime => _disposed ? new CancellationToken(canceled: true) : _lifetime.Token;

    /// <summary>The "no collection filter" entry, always first in <see cref="Collections"/>.</summary>
    public static CollectionOption AllGames { get; } = new(null, "All games");

    /// <summary>Every card, in load order. Bind to <see cref="GamesView"/>, not to this.</summary>
    public BulkObservableCollection<GameCardViewModel> Games { get; } = [];

    /// <summary>The filtered and sorted view the grid binds to.</summary>
    public ICollectionView GamesView => _view;

    /// <summary>Genres present in the library, most common first.</summary>
    public ObservableCollection<FilterOption> GenreFilters { get; } = [];

    /// <summary>The most common store tags in the library, capped at <see cref="MaxTagFilters"/>.</summary>
    public ObservableCollection<FilterOption> TagFilters { get; } = [];

    /// <summary>The collection selector: "all games" followed by the user's collections.</summary>
    public ObservableCollection<CollectionOption> Collections { get; } = [];

    /// <summary>The user's collections without the "all games" entry, for the "add to" menu.</summary>
    public ObservableCollection<CollectionOption> AssignableCollections { get; } = [];

    /// <summary>The available orderings.</summary>
    public IReadOnlyList<LibrarySortOption> SortOptions { get; }

    /// <summary>
    /// Search text. Typing does not filter immediately: the grid re-filters
    /// <see cref="SearchDebounceMilliseconds"/> after the last keystroke.
    /// </summary>
    [ObservableProperty]
    private string? _searchText;

    /// <summary>
    /// Whether the facet rail is open. View state, but it belongs to the page rather than to one
    /// control, and it is the only thing the toolbar's toggle button binds to.
    /// </summary>
    [ObservableProperty]
    private bool _isFilterPaneOpen;

    /// <summary>Show only apps with a manifest in a reachable library folder.</summary>
    [ObservableProperty]
    private bool _installedOnly;

    /// <summary>Show only apps whose content on disk is stale, missing or corrupt.</summary>
    [ObservableProperty]
    private bool _needsUpdateOnly;

    /// <summary>The collection being shown, or <see cref="AllGames"/>.</summary>
    [ObservableProperty]
    private CollectionOption _selectedCollection;

    /// <summary>The current ordering.</summary>
    [ObservableProperty]
    private LibrarySortOption _selectedSort;

    /// <summary>The card the keyboard and the context menu act on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToCollectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromCollectionCommand))]
    private GameCardViewModel? _selectedGame;

    /// <summary>Why the library looks the way it does; <see langword="null"/> before the first load.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    [NotifyPropertyChangedFor(nameof(HasStatusDetail))]
    private LibraryStatus? _status;

    /// <summary>How many cards pass the current filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    private int _visibleCount;

    /// <summary>How many cards the library holds in total.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    private int _totalCount;

    /// <summary>Cover width in device-independent pixels, from <c>Ui:CardWidth</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardSize))]
    private double _cardWidth;

    /// <summary>
    /// The cell the virtualising panel lays out, cover plus card chrome. It is authoritative: the
    /// panel does not measure individual cards, so a card must not try to grow past it.
    /// </summary>
    public Size CardSize => new(
        CardWidth + CardChromeWidth,
        Math.Round(CardWidth * CoverAspectRatio) + CardChromeHeight);

    /// <summary>"128 of 3,014 games", or just the total when nothing is filtered out.</summary>
    public string CountsText =>
        VisibleCount == TotalCount
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} games", TotalCount)
            : string.Format(CultureInfo.CurrentCulture, "{0:N0} of {1:N0} games", VisibleCount, TotalCount);

    /// <summary>True when at least one facet, toggle or search term is applied.</summary>
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || InstalledOnly
        || NeedsUpdateOnly
        || _selectedGenreIds.Count > 0
        || _selectedTagIds.Count > 0
        || SelectedCollection.CollectionId is not null;

    /// <summary>True when the grid has nothing to show and the empty state belongs on screen.</summary>
    public bool ShowEmptyState => !IsBusy && VisibleCount == 0;

    /// <summary>Headline of the empty state, chosen from the degraded state that explains it.</summary>
    public string EmptyStateTitle
    {
        get
        {
            if (Status is { SteamFound: false })
            {
                return "Steam not found";
            }

            if (TotalCount > 0)
            {
                return "No games match your filters";
            }

            return Status is { OwnedListAvailable: false } ? "Nothing to show yet" : "No games installed";
        }
    }

    /// <summary>The sentence under <see cref="EmptyStateTitle"/> that says what to do about it.</summary>
    public string EmptyStateMessage
    {
        get
        {
            if (Status is { SteamFound: false })
            {
                return "Install the Steam client, or point OhMyLibrary at it with Steam:OverrideSteamPath.";
            }

            if (TotalCount > 0)
            {
                return "Clear the search box and the filters to see the rest of the library.";
            }

            if (Status is { ApiKeyConfigured: false })
            {
                return "Add a Steam Web API key in Settings to see games you own but have not installed.";
            }

            if (Status is { OwnedListAvailable: false })
            {
                return "The owned-games list could not be read. A private profile hides it; installed games still appear.";
            }

            return "Install a game in Steam, or refresh to rescan the library folders.";
        }
    }

    /// <summary>A one-line note about a partially degraded library, or <see langword="null"/>.</summary>
    public string? StatusDetail
    {
        get
        {
            if (Status is not { } status)
            {
                return null;
            }

            if (!status.SteamFound)
            {
                return "Steam was not found on this machine.";
            }

            if (status.MissingLibraryFolderCount > 0)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} of {1} library folders are unreachable — their games are hidden.",
                    status.MissingLibraryFolderCount,
                    status.LibraryFolderCount);
            }

            if (!status.ApiKeyConfigured)
            {
                return "No Steam Web API key: only installed games are listed.";
            }

            if (!status.OwnedListAvailable)
            {
                return "The owned-games list is unavailable — the profile may be private.";
            }

            return status.LastError;
        }
    }

    /// <summary>True when <see cref="StatusDetail"/> is worth a line above the grid.</summary>
    public bool HasStatusDetail => !string.IsNullOrEmpty(StatusDetail);

    /// <summary>
    /// Loads the library the first time the page is shown, and rescans the local manifests on every
    /// later visit. The rescan is throttled inside the service, so revisiting the page is cheap.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task InitialiseAsync(CancellationToken ct = default)
    {
        if (_initialised)
        {
            // Revisiting the page is not the user asking for a scan, so it respects the cooldown.
            return RescanAsync(force: false, ct);
        }

        _initialised = true;
        _library.LibraryChanged += OnLibraryChanged;

        return LoadCommand.ExecuteAsync(null);
    }

    /// <summary>Unsubscribes from the library and cancels any cover still in flight.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _library.LibraryChanged -= OnLibraryChanged;
        _uiOptionsSubscription?.Dispose();
        _searchTimer.Stop();
        _searchTimer.Tick -= OnSearchTimerTick;
        DetachFacets(GenreFilters);
        DetachFacets(TagFilters);

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Reads the whole library, the collections and the name tables.</summary>
    /// <param name="ct">Cancellation token.</param>
    [RelayCommand]
    private Task LoadAsync(CancellationToken ct) => RunGuardedAsync(LoadCoreAsync, "Loading library", ct);

    /// <summary>
    /// The body of <see cref="LoadCommand"/> on its own, without the busy indicator and without the
    /// error reset that <see cref="ViewModelBase.RunGuardedAsync"/> performs.
    /// </summary>
    /// <remarks>
    /// A refresh the user asked for goes through the command and shows progress. A refresh a
    /// background watcher asked for calls this directly: it must not flash the page's progress ring,
    /// and it must not wipe an error message the user was shown a moment ago by an action of their
    /// own.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    private async Task LoadCoreAsync(CancellationToken ct)
    {
        var generation = ++_loadGeneration;

        var status = await _library.GetStatusAsync(ct).ConfigureAwait(true);
        var games = await _library.GetGamesAsync(ct).ConfigureAwait(true);
        var collections = await _collections.GetAllAsync(ct).ConfigureAwait(true);
        var genreNames = await _tags.GetAllGenresAsync(ct).ConfigureAwait(true);
        var tagNames = await _tags.GetAllTagsAsync(ct).ConfigureAwait(true);

        if (generation != _loadGeneration)
        {
            // A newer load started while this one was reading; its rows are the current ones.
            return;
        }

        Status = status;
        ApplyCollections(collections);
        ApplyGames(games);

        if (RebuildFacets(genreNames, tagNames))
        {
            // A checked facet just lost its last carrier and was dropped from the selection. The
            // only refresh this path performs is the reset ApplyGames raises, and that ran before
            // the selection was narrowed, so without this the grid stays filtered by a facet the
            // rail no longer offers and there is no checkbox left to clear it with.
            RefreshView();
        }
    }

    /// <summary>
    /// Rescans the <c>.acf</c> manifests and reloads the grid. Forced: the user pressed the toolbar
    /// button, and a Rescan that silently does nothing because the cooldown has not elapsed reads as
    /// a broken button.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [RelayCommand]
    private Task RefreshLocalAsync(CancellationToken ct) => RescanAsync(force: true, ct);

    /// <summary>Rescans the manifests and reloads the grid.</summary>
    /// <param name="force">Whether to bypass the service's rescan cooldown.</param>
    /// <param name="ct">Cancellation token.</param>
    private Task RescanAsync(bool force, CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                await _library.RefreshLocalAsync(force, token).ConfigureAwait(true);
                await ReloadAsync(token).ConfigureAwait(true);
            },
            "Rescanning installed games",
            ct);

    /// <summary>
    /// Forces a Steam Web API refresh of the owned list and a metadata reparse, then reloads. This
    /// is the expensive one and is only ever run from the toolbar button.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [RelayCommand]
    private Task RefreshRemoteAsync(CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                await _library.RefreshRemoteAsync(force: true, token).ConfigureAwait(true);
                await _library.RefreshMetadataAsync(force: false, token).ConfigureAwait(true);
                await ReloadAsync(token).ConfigureAwait(true);
            },
            "Refreshing from Steam",
            ct);

    /// <summary>Runs whatever the card's primary button offers for its current state.</summary>
    /// <param name="card">The card acted on; the selected card when the parameter is null.</param>
    [RelayCommand]
    private void PrimaryAction(GameCardViewModel? card)
    {
        var target = card ?? SelectedGame;

        if (target is null)
        {
            return;
        }

        switch (target.PrimaryAction)
        {
            case GameCardAction.Install:
            case GameCardAction.Update:
                Request(_installState.RequestInstall(target.AppId), target, "install");
                break;

            case GameCardAction.Play:
                Request(_installState.RequestLaunch(target.AppId), target, "launch");
                break;

            case GameCardAction.Working:
            default:
                Logger.LogDebug("Ignoring the primary action for {AppId}: Steam is already working on it", target.AppId);
                break;
        }
    }

    /// <summary>Asks Steam to launch the app, installing it first if it is not on disk.</summary>
    /// <param name="card">The card acted on; the selected card when the parameter is null.</param>
    [RelayCommand]
    private void Launch(GameCardViewModel? card)
    {
        if ((card ?? SelectedGame) is not { } target)
        {
            return;
        }

        Request(_installState.RequestLaunch(target.AppId), target, "launch");
    }

    /// <summary>Asks Steam to install the app, or to apply a pending update.</summary>
    /// <param name="card">The card acted on; the selected card when the parameter is null.</param>
    [RelayCommand]
    private void Install(GameCardViewModel? card)
    {
        if ((card ?? SelectedGame) is not { } target)
        {
            return;
        }

        Request(_installState.RequestInstall(target.AppId), target, "install");
    }

    /// <summary>Opens Steam's uninstall prompt for the app.</summary>
    /// <param name="card">The card acted on; the selected card when the parameter is null.</param>
    [RelayCommand]
    private void Uninstall(GameCardViewModel? card)
    {
        if ((card ?? SelectedGame) is not { } target)
        {
            return;
        }

        Request(_installState.RequestUninstall(target.AppId), target, "uninstall");
    }

    /// <summary>Asks Steam to verify the app's files.</summary>
    /// <param name="card">The card acted on; the selected card when the parameter is null.</param>
    [RelayCommand]
    private void Validate(GameCardViewModel? card)
    {
        if ((card ?? SelectedGame) is not { } target)
        {
            return;
        }

        Request(_installState.RequestValidate(target.AppId), target, "validate");
    }

    /// <summary>Opens the app's store page in the Steam client.</summary>
    /// <param name="card">The card acted on; the selected card when the parameter is null.</param>
    [RelayCommand]
    private void OpenStorePage(GameCardViewModel? card)
    {
        if ((card ?? SelectedGame) is not { } target)
        {
            return;
        }

        Request(_launcher.OpenStorePage(target.AppId), target, "open the store page for");
    }

    /// <summary>Shows the app on its page in the Steam client's own library.</summary>
    /// <param name="card">The card acted on; the selected card when the parameter is null.</param>
    [RelayCommand]
    private void OpenInSteam(GameCardViewModel? card)
    {
        if ((card ?? SelectedGame) is not { } target)
        {
            return;
        }

        Request(_launcher.OpenLibraryPage(target.AppId), target, "show in Steam");
    }

    /// <summary>Adds the selected game to a collection.</summary>
    /// <param name="option">The collection to add it to.</param>
    /// <param name="ct">Cancellation token.</param>
    [RelayCommand(CanExecute = nameof(CanChangeCollection))]
    private Task AddToCollectionAsync(CollectionOption? option, CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                if (SelectedGame is not { } card || option?.CollectionId is not { } collectionId)
                {
                    return;
                }

                await _collections.AddGameAsync(collectionId, card.AppId, token).ConfigureAwait(true);
                await RefreshEntryAsync(card.AppId, token).ConfigureAwait(true);
            },
            "Adding to collection",
            ct);

    /// <summary>Removes the selected game from a collection.</summary>
    /// <param name="option">The collection to remove it from.</param>
    /// <param name="ct">Cancellation token.</param>
    [RelayCommand(CanExecute = nameof(CanChangeCollection))]
    private Task RemoveFromCollectionAsync(CollectionOption? option, CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                if (SelectedGame is not { } card || option?.CollectionId is not { } collectionId)
                {
                    return;
                }

                await _collections.RemoveGameAsync(collectionId, card.AppId, token).ConfigureAwait(true);
                await RefreshEntryAsync(card.AppId, token).ConfigureAwait(true);
            },
            "Removing from collection",
            ct);

    /// <summary>Clears the search box, both toggles, every facet and the collection selector.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _searchTimer.Stop();

        SearchText = null;
        _searchKey = string.Empty;
        InstalledOnly = false;
        NeedsUpdateOnly = false;
        SelectedCollection = AllGames;

        ClearFacet(GenreFilters, _selectedGenreIds);
        ClearFacet(TagFilters, _selectedTagIds);

        RefreshView();
    }

    private bool CanChangeCollection(CollectionOption? option) =>
        SelectedGame is not null && option?.CollectionId is not null;

    partial void OnSearchTextChanged(string? value)
    {
        _searchTimer.Stop();

        if (string.IsNullOrWhiteSpace(value))
        {
            // Clearing the box is the one case that should not wait a quarter of a second.
            _searchKey = string.Empty;
            RefreshView();
            return;
        }

        _searchTimer.Start();
    }

    partial void OnInstalledOnlyChanged(bool value) => RefreshView();

    partial void OnNeedsUpdateOnlyChanged(bool value) => RefreshView();

    partial void OnSelectedCollectionChanged(CollectionOption value) => RefreshView();

    partial void OnSelectedSortChanged(LibrarySortOption value)
    {
        _view.CustomSort = new CardComparer(value.Order);
        UpdateCounts();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // IsBusy is declared on the base class, so there is no generated hook to hang this on: the
        // empty state must disappear while a load runs and reappear if the load found nothing.
        if (e.PropertyName == nameof(IsBusy))
        {
            NotifyEmptyState();
        }
    }

    private void OnSearchTimerTick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        _searchKey = GameCardViewModel.Fold(SearchText);
        RefreshView();
    }

    private void OnUiOptionsChanged(UiOptions options)
    {
        var width = ClampCardWidth(options.CardWidth);

        if (Math.Abs(width - CardWidth) < 0.5)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(
            () =>
            {
                CardWidth = width;

                foreach (var card in Games)
                {
                    card.Resize(width);
                }
            },
            DispatcherPriority.Background);
    }

    private bool IsVisible(object item)
    {
        if (item is not GameCardViewModel card)
        {
            return false;
        }

        if (InstalledOnly && !card.IsInstalled)
        {
            return false;
        }

        if (NeedsUpdateOnly && !card.NeedsUpdate)
        {
            return false;
        }

        if (SelectedCollection.CollectionId is { } collectionId && !card.IsInCollection(collectionId))
        {
            return false;
        }

        if (_selectedGenreIds.Count > 0 && !card.MatchesAnyGenre(_selectedGenreIds))
        {
            return false;
        }

        if (_selectedTagIds.Count > 0 && !card.MatchesAnyTag(_selectedTagIds))
        {
            return false;
        }

        return _searchKey.Length == 0 || card.SearchKey.Contains(_searchKey, StringComparison.Ordinal);
    }

    private void RefreshView()
    {
        _view.Refresh();
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        TotalCount = Games.Count;
        VisibleCount = _view.Count;

        OnPropertyChanged(nameof(HasActiveFilters));
        NotifyEmptyState();
    }

    private void NotifyEmptyState()
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        var generation = ++_loadGeneration;

        var status = await _library.GetStatusAsync(ct).ConfigureAwait(true);
        var games = await _library.GetGamesAsync(ct).ConfigureAwait(true);
        var genreNames = await _tags.GetAllGenresAsync(ct).ConfigureAwait(true);
        var tagNames = await _tags.GetAllTagsAsync(ct).ConfigureAwait(true);

        if (generation != _loadGeneration)
        {
            return;
        }

        Status = status;
        ApplyGames(games);

        if (RebuildFacets(genreNames, tagNames))
        {
            // See LoadCoreAsync: the selection is narrowed after ApplyGames' reset re-filtered the
            // view, so a dropped facet has to be followed by a refresh or the grid stays narrowed
            // by it.
            RefreshView();
        }
    }

    private void ApplyGames(IReadOnlyList<GameEntry> entries)
    {
        var selectedAppId = SelectedGame?.AppId;
        var next = new List<GameCardViewModel>(entries.Count);
        var seen = new HashSet<int>(entries.Count);

        // Every row handed in is already on the grid by definition: GetGamesAsync is the authority on
        // what belongs there, filter and membership rule together, and re-deciding that here is what
        // let a single-app refresh and a bulk read disagree. All this loop still owns is duplicates.
        //
        // Cards are reused for apps that are still there, so a refresh keeps their decoded covers
        // instead of re-reading three thousand files.
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.AppId))
            {
                continue;
            }

            if (_cards.TryGetValue(entry.AppId, out var existing))
            {
                existing.Update(entry);
                next.Add(existing);
            }
            else
            {
                var card = CreateCard(entry);
                _cards[entry.AppId] = card;
                next.Add(card);
            }
        }

        foreach (var appId in _cards.Keys.Where(appId => !seen.Contains(appId)).ToList())
        {
            _ = _cards.Remove(appId);
        }

        // One reset rather than three thousand adds: see BulkObservableCollection for why
        // ListCollectionView.DeferRefresh is not an option here.
        Games.ReplaceAll(next);

        SelectedGame = selectedAppId is { } appIdToRestore && _cards.TryGetValue(appIdToRestore, out var restored)
            ? restored
            : null;

        UpdateCounts();
    }

    private GameCardViewModel CreateCard(GameEntry entry) =>
        new(this, entry, SafeCoverUrl(entry.AppId), _images, CardWidth, Logger);

    private string? SafeCoverUrl(int appId)
    {
        try
        {
            return _assets.GetCoverUrlFallback(appId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not build a cover URL for {AppId}", appId);
            return null;
        }
    }

    private void ApplyCollections(IReadOnlyList<GameCollection> collections)
    {
        var selectedId = SelectedCollection.CollectionId;

        Collections.Clear();
        AssignableCollections.Clear();
        Collections.Add(AllGames);

        foreach (var collection in collections)
        {
            var option = new CollectionOption(collection.CollectionId, collection.Name);
            Collections.Add(option);
            AssignableCollections.Add(option);
        }

        SelectedCollection = Collections.FirstOrDefault(option => option.CollectionId == selectedId) ?? AllGames;
    }

    /// <summary>
    /// Rebuilds both facet lists from the ids the cards currently carry.
    /// </summary>
    /// <param name="genreNames">Genre id to display name.</param>
    /// <param name="tagNames">Tag id to display name.</param>
    /// <returns>
    /// <see langword="true"/> when at least one checked facet lost its last carrier and was dropped
    /// from the live selection. The filter is then wider than the one the view was last refreshed
    /// with, and the caller owes the view a <see cref="RefreshView"/>.
    /// </returns>
    private bool RebuildFacets(IReadOnlyList<GenreRef> genreNames, IReadOnlyList<TagRef> tagNames)
    {
        var genreLookup = genreNames
            .GroupBy(genre => genre.GenreId)
            .ToDictionary(group => group.Key, group => group.First().Name, EqualityComparer<int>.Default);

        var tagLookup = tagNames
            .GroupBy(tag => tag.TagId)
            .ToDictionary(group => group.Key, group => group.First().Name, EqualityComparer<int>.Default);

        var genreCounts = new Dictionary<int, int>();
        var tagCounts = new Dictionary<int, int>();

        foreach (var card in Games)
        {
            foreach (var genreId in card.GenreIds)
            {
                genreCounts[genreId] = genreCounts.GetValueOrDefault(genreId) + 1;
            }

            foreach (var tagId in card.TagIds)
            {
                tagCounts[tagId] = tagCounts.GetValueOrDefault(tagId) + 1;
            }
        }

        // Both, never short-circuited: each list has to be brought up to date whatever the other did.
        var genresDropped = RebuildFacet(GenreFilters, _selectedGenreIds, genreCounts, genreLookup, int.MaxValue);
        var tagsDropped = RebuildFacet(TagFilters, _selectedTagIds, tagCounts, tagLookup, MaxTagFilters);

        return genresDropped || tagsDropped;
    }

    /// <summary>
    /// Rebuilds one facet list from the ids the cards currently carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the user has <i>checked</i> lives in <paramref name="selectedIds"/>, not in the
    /// <see cref="FilterOption"/> objects, and this method never clears it. A rebuilt option is
    /// constructed with <see cref="FilterOption.IsSelected"/> seeded from that set, before it is
    /// subscribed to, so restoring a checkbox cannot re-enter <see cref="OnFilterOptionChanged"/>
    /// and cannot re-run the filter. The set is only ever narrowed at the end, to ids that no longer
    /// exist in the library at all — those must stop filtering it.
    /// </para>
    /// <para>
    /// When the resulting list is the same ids under the same names in the same order — the common
    /// case for a background refresh, where install state moved but no genre or tag did — the
    /// existing options are kept and only their counts are written. Nothing is detached, no
    /// collection reset reaches the rail, and the checkboxes are not even re-created.
    /// </para>
    /// </remarks>
    /// <param name="target">The facet list to bring up to date.</param>
    /// <param name="selectedIds">Ids the user has checked; the authority on checked state.</param>
    /// <param name="counts">How many cards carry each id.</param>
    /// <param name="names">Id to display name, from the Core name tables.</param>
    /// <param name="limit">How many entries the list may offer, checked ones aside.</param>
    /// <returns>
    /// <see langword="true"/> when narrowing removed something the user had checked, so the grid is
    /// now filtered more tightly than <paramref name="selectedIds"/> says it should be.
    /// </returns>
    private bool RebuildFacet(
        ObservableCollection<FilterOption> target,
        HashSet<int> selectedIds,
        Dictionary<int, int> counts,
        Dictionary<int, string> names,
        int limit)
    {
        // Most common first, and a facet the user has already applied is never dropped by the cap.
        var ordered = counts
            .OrderByDescending(entry => selectedIds.Contains(entry.Key))
            .ThenByDescending(entry => entry.Value)
            .ThenBy(entry => Name(entry.Key), StringComparer.CurrentCultureIgnoreCase)
            .Take(limit == int.MaxValue ? counts.Count : Math.Max(limit, selectedIds.Count))
            .ToList();

        if (!TryUpdateCountsInPlace())
        {
            DetachFacets(target);
            target.Clear();

            foreach (var (id, count) in ordered)
            {
                var option = new FilterOption(id, Name(id), count)
                {
                    IsSelected = selectedIds.Contains(id),
                };

                option.PropertyChanged += OnFilterOptionChanged;
                target.Add(option);
            }
        }

        // Ids that vanished from the library must not keep filtering it. IntersectWith only ever
        // removes, so a smaller set afterwards means a checked facet was dropped and the view is
        // still narrowed by it until somebody refreshes.
        var checkedBefore = selectedIds.Count;
        selectedIds.IntersectWith(target.Where(option => option.IsSelected).Select(option => option.Id));

        return selectedIds.Count != checkedBefore;

        bool TryUpdateCountsInPlace()
        {
            if (target.Count != ordered.Count)
            {
                return false;
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                if (target[i].Id != ordered[i].Key
                    || !string.Equals(target[i].Name, Name(ordered[i].Key), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                target[i].Count = ordered[i].Value;
            }

            return true;
        }

        string Name(int id) =>
            names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : string.Create(CultureInfo.InvariantCulture, $"#{id}");
    }

    private void DetachFacets(ObservableCollection<FilterOption> target)
    {
        foreach (var option in target)
        {
            option.PropertyChanged -= OnFilterOptionChanged;
        }
    }

    private void ClearFacet(ObservableCollection<FilterOption> target, HashSet<int> selectedIds)
    {
        selectedIds.Clear();
        _suppressFacetRefresh = true;

        try
        {
            foreach (var option in target)
            {
                option.IsSelected = false;
            }
        }
        finally
        {
            _suppressFacetRefresh = false;
        }
    }

    private void OnFilterOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not FilterOption option || e.PropertyName != nameof(FilterOption.IsSelected))
        {
            return;
        }

        if (_suppressFacetRefresh)
        {
            // A whole facet is being cleared at once; the caller refreshes the view when it is done.
            return;
        }

        var selectedIds = ReferenceEquals(FindFacet(option), GenreFilters) ? _selectedGenreIds : _selectedTagIds;

        if (option.IsSelected)
        {
            _ = selectedIds.Add(option.Id);
        }
        else
        {
            _ = selectedIds.Remove(option.Id);
        }

        RefreshView();
    }

    private ObservableCollection<FilterOption> FindFacet(FilterOption option) =>
        GenreFilters.Contains(option) ? GenreFilters : TagFilters;

    private void OnLibraryChanged(object? sender, LibraryChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // The event can arrive from a watcher thread or a background refresh; every card mutation
        // below has to happen on the dispatcher.
        _ = _dispatcher.InvokeAsync(() => _ = ApplyLibraryChangeAsync(e), DispatcherPriority.Background);
    }

    private async Task ApplyLibraryChangeAsync(LibraryChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // Deliberately neither RunGuardedAsync nor LoadCommand, on both branches: a watcher-driven
            // refresh must not flash the page's busy indicator and must not clear an error the user
            // was just shown by an action of their own. A failure here is a log line, not a red bar.
            //
            // A change that arrives while the grid is still empty is the first run finishing: the
            // startup sync has just written the whole library, so the per-app path below would walk
            // every app id it names to build a grid that is empty anyway, and it would leave the
            // collections list unread. One full reload is cheaper and complete.
            if (e.IsWholeLibrary || _cards.Count == 0)
            {
                await LoadCoreAsync(_lifetime.Token).ConfigureAwait(true);
                return;
            }

            // Claimed, not merely read: this is a writer to the grid like the two full loads, and it
            // has to be ordered against a load already in flight and against a second partial update
            // as well as against loads that start later. See _loadGeneration.
            //
            // Cutting a load that is already reading off costs nothing: this path reads the same
            // filtered list that load is reading, only later, and applies it to every app the change
            // event names — and Core names every app whose state moved.
            var generation = ++_loadGeneration;

            // Everything the partial path can invalidate is read in one go: the rows, the degraded
            // state behind the status line, and the genre and tag name tables the facet lists are
            // labelled from. All four are cheap — Core caches the library and the name tables, and
            // this path already had to read the rows.
            //
            // One filtered read instead of a database round trip per app, and it is the same read
            // the bulk path uses, so what lands here is exactly what a full load would have shown.
            var games = await _library.GetGamesAsync(_lifetime.Token).ConfigureAwait(true);
            var status = await _library.GetStatusAsync(_lifetime.Token).ConfigureAwait(true);
            var genreNames = await _tags.GetAllGenresAsync(_lifetime.Token).ConfigureAwait(true);
            var tagNames = await _tags.GetAllTagsAsync(_lifetime.Token).ConfigureAwait(true);

            if (_disposed || generation != _loadGeneration)
            {
                // A newer writer — a full load, or another change event — started while this was
                // reading, and it re-reads all of the above itself from rows that are at least as
                // new as these.
                return;
            }

            // GetGamesAsync already dropped everything that does not belong on the grid, so an app
            // id that is missing from this map is one whose card has to go.
            var byAppId = new Dictionary<int, GameEntry>(games.Count);
            foreach (var game in games)
            {
                _ = byAppId.TryAdd(game.AppId, game);
            }

            // A LibraryStatus that moved after the first load — an API key added, a library folder
            // that went missing — only reaches the status line if it is re-read here.
            Status = status;

            foreach (var appId in e.AppIds)
            {
                // The local scan names everything whose install state moved, uninstalls included, so
                // an app whose manifest Steam has just deleted arrives here too. Whether it keeps
                // its card is Core's call, not this loop's: it is still in the map when it is owned,
                // or when the owned list is unavailable and its row came from a real manifest, and
                // the card stays with the state flipped to "Install"; it is absent from the map only
                // when an authoritative owned list says it is gone, and then ApplyEntry drops it.
                _ = ApplyEntry(appId, byAppId.GetValueOrDefault(appId));
            }

            // The facet lists are counted off the cards, so they are only correct once the loop
            // above has run. Rebuilding them here is what makes the filters usable on a cold run:
            // the first Local event lands while the metadata step has not written a single genre or
            // tag row yet, and everything after it takes this path. Checked facets survive — see
            // RebuildFacet.
            //
            // The refresh is unconditional here whatever the rebuild reports: the loop above changed
            // install state, playtime and collection membership on cards that are already in the
            // view, and every one of those feeds the filter or the sort.
            _ = RebuildFacets(genreNames, tagNames);
            RefreshView();
        }
        catch (OperationCanceledException)
        {
            // The page is going away.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not apply a {Kind} library change to the grid", e.Kind);
        }
    }

    /// <summary>
    /// Re-reads one card the user just acted on.
    /// </summary>
    /// <remarks>
    /// Through <c>GetGridEntryAsync</c> and never through <c>GetGameAsync</c>: the per-app read the
    /// detail views use deliberately skips the non-game and redistributable filters, so anything
    /// admitted from it can land on a grid the bulk read excludes it from — app 228980, "Steamworks
    /// Common Redistributables", is installed, and installed is all any check made here would see.
    /// <c>GetGridEntryAsync</c> answers the same question as <c>GetGamesAsync</c>, one app at a time;
    /// <see langword="null"/> covers both "no such row" and "no longer belongs here", and either way
    /// the card goes.
    /// </remarks>
    /// <param name="appId">The app to re-read.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RefreshEntryAsync(int appId, CancellationToken ct)
    {
        var entry = await _library.GetGridEntryAsync(appId, ct).ConfigureAwait(true);

        if (ApplyEntry(appId, entry))
        {
            UpdateCounts();
        }
    }

    /// <summary>
    /// Adds, updates or removes one card. Must run on the dispatcher.
    /// </summary>
    /// <param name="appId">The app the change concerns.</param>
    /// <param name="entry">Its row, or <see langword="null"/> when it no longer belongs on the grid.</param>
    /// <returns><see langword="true"/> when a card was added or removed, so the counts moved.</returns>
    private bool ApplyEntry(int appId, GameEntry? entry)
    {
        if (entry is null)
        {
            if (!_cards.Remove(appId, out var removed))
            {
                return false;
            }

            _ = Games.Remove(removed);

            if (ReferenceEquals(SelectedGame, removed))
            {
                SelectedGame = null;
            }

            return true;
        }

        if (_cards.TryGetValue(appId, out var card))
        {
            card.Update(entry);
            return false;
        }

        var created = CreateCard(entry);
        _cards[appId] = created;
        Games.Add(created);
        return true;
    }

    private void Request(bool accepted, GameCardViewModel card, string what)
    {
        if (accepted)
        {
            Logger.LogInformation("Asked Steam to {Action} {AppId}", what, card.AppId);
            return;
        }

        Logger.LogWarning("Steam refused to {Action} {AppId}", what, card.AppId);
        ErrorMessage = $"Steam would not {what} {card.Name}. Is the Steam client installed and running?";
    }

    private static double ClampCardWidth(int cardWidth) =>
        Math.Clamp(cardWidth <= 0 ? 200d : cardWidth, MinCardWidth, MaxCardWidth);

    /// <summary>
    /// Orders cards for one <see cref="LibrarySortOrder"/>. Name comparison is natural, so
    /// "Game 2" comes before "Game 10", which plain text ordering in SQL cannot do.
    /// </summary>
    /// <param name="order">The ordering to apply.</param>
    private sealed class CardComparer(LibrarySortOrder order) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not GameCardViewModel left || y is not GameCardViewModel right)
            {
                return 0;
            }

            var result = order switch
            {
                LibrarySortOrder.LastPlayed => right.LastPlayedTicks.CompareTo(left.LastPlayedTicks),
                LibrarySortOrder.Playtime => right.PlaytimeMinutes.CompareTo(left.PlaytimeMinutes),
                LibrarySortOrder.Size => right.SizeBytes.CompareTo(left.SizeBytes),
                LibrarySortOrder.InstallState => left.InstallRank.CompareTo(right.InstallRank),
                _ => 0,
            };

            return result != 0 ? result : CompareNatural(left.SortKey, right.SortKey);
        }

        /// <summary>Compares two folded names, treating digit runs as numbers.</summary>
        /// <param name="left">First name.</param>
        /// <param name="right">Second name.</param>
        internal static int CompareNatural(string left, string right)
        {
            int i = 0, j = 0;

            while (i < left.Length && j < right.Length)
            {
                if (char.IsAsciiDigit(left[i]) && char.IsAsciiDigit(right[j]))
                {
                    var startLeft = i;
                    var startRight = j;

                    while (i < left.Length && char.IsAsciiDigit(left[i]))
                    {
                        i++;
                    }

                    while (j < right.Length && char.IsAsciiDigit(right[j]))
                    {
                        j++;
                    }

                    var digitsLeft = left.AsSpan(startLeft, i - startLeft).TrimStart('0');
                    var digitsRight = right.AsSpan(startRight, j - startRight).TrimStart('0');

                    if (digitsLeft.Length != digitsRight.Length)
                    {
                        return digitsLeft.Length - digitsRight.Length;
                    }

                    var digits = digitsLeft.SequenceCompareTo(digitsRight);

                    if (digits != 0)
                    {
                        return digits;
                    }

                    continue;
                }

                if (left[i] != right[j])
                {
                    return left[i].CompareTo(right[j]);
                }

                i++;
                j++;
            }

            return (left.Length - i) - (right.Length - j);
        }
    }
}
