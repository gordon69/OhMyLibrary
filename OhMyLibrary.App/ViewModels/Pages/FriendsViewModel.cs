using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using OhMyLibrary.App.Services;
using OhMyLibrary.App.Views.Pages;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Services;

using Wpf.Ui;

namespace OhMyLibrary.App.ViewModels.Pages;

/// <summary>
/// The friends page: who is on the list, who is online, and whose library we are allowed to read.
/// </summary>
/// <remarks>
/// <para>
/// Opening the page never starts a fan-out. <see cref="IFriendsService.GetFriendsAsync"/> reads the
/// cached list; the network refresh is one owned-games call per friend and only happens when the
/// user asks for it, which is also why it reports progress and can be cancelled halfway.
/// </para>
/// <para>
/// With no API key there is nothing to fail: the page shows an explanation and a way to reach
/// Settings, not an error.
/// </para>
/// </remarks>
public partial class FriendsViewModel : ViewModelBase
{
    /// <summary>Decode width of an avatar, in pixels.</summary>
    public const int AvatarPixelWidth = 48;

    /// <summary>How many avatars are fetched at once.</summary>
    public const int AvatarConcurrency = 6;

    private readonly IFriendsService _friends;
    private readonly ISettingsService _settings;
    private readonly IImageCacheService _images;
    private readonly INavigationService _navigation;
    private readonly Dispatcher _dispatcher;

    private readonly List<FriendItemViewModel> _all = [];

    /// <summary>Creates the page view model.</summary>
    /// <param name="friends">Friends list and per-friend library sync.</param>
    /// <param name="settings">Source of the "is an API key configured" answer.</param>
    /// <param name="images">Decoder and cache for avatars.</param>
    /// <param name="navigation">Used by the empty state to jump to the Settings page.</param>
    /// <param name="logger">Log sink for guarded failures.</param>
    public FriendsViewModel(
        IFriendsService friends,
        ISettingsService settings,
        IImageCacheService images,
        INavigationService navigation,
        ILogger<FriendsViewModel> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(friends);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(navigation);

        _friends = friends;
        _settings = settings;
        _images = images;
        _navigation = navigation;

        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _hasApiKey = settings.HasApiKey;
        settings.Changed += OnSettingsChanged;
        PropertyChanged += OnSelfPropertyChanged;
    }

    /// <summary>The friends matching <see cref="FilterText"/>, most useful first.</summary>
    public ObservableCollection<FriendItemViewModel> Friends { get; } = [];

    /// <summary>Name filter applied to the list.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>The refresh's latest progress line, for example <c>"12 of 180 friends"</c>.</summary>
    [ObservableProperty]
    private string? _progressMessage;

    /// <summary>Whether a Steam Web API key is configured. Everything on this page depends on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApiKeyNotice))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _hasApiKey;

    /// <summary>One line summarising the whole list, shown under the page title.</summary>
    [ObservableProperty]
    private string _summaryText = string.Empty;

    /// <summary>True when the page should explain that no API key is configured.</summary>
    public bool ShowApiKeyNotice => !HasApiKey;

    /// <summary>
    /// True when a key is configured but nothing has been synced yet, so the page offers a refresh
    /// rather than an empty box.
    /// </summary>
    public bool ShowEmptyState => HasApiKey && _all.Count == 0 && !IsBusy;

    /// <summary>True when at least one friend is listed after filtering.</summary>
    public bool HasFriends => Friends.Count > 0;

    /// <summary>
    /// Reads the cached friends list. Called on every navigation, not just the first, so a sync
    /// started from the Settings page shows up when the user comes back here. It never touches the
    /// network.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task InitialiseAsync(CancellationToken ct = default) =>
        RunGuardedAsync(LoadAsync, "Loading friends", ct);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync(CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                var progress = new Progress<string>(message => ProgressMessage = message);

                try
                {
                    await _friends.RefreshAsync(force: true, progress, token).ConfigureAwait(true);
                    await LoadAsync(token).ConfigureAwait(true);
                }
                finally
                {
                    ProgressMessage = null;
                }
            },
            "Refreshing friends",
            ct);

    private bool CanRefresh() => HasApiKey;

    [RelayCommand]
    private void OpenSettings() => _navigation.Navigate(typeof(SettingsPage));

    private async Task LoadAsync(CancellationToken ct)
    {
        var summaries = await _friends.GetFriendsAsync(ct).ConfigureAwait(true);

        _all.Clear();
        _all.AddRange(summaries
            .OrderByDescending(friend => friend.State != PersonaState.Offline)
            .ThenBy(friend => friend.PersonaName, StringComparer.CurrentCultureIgnoreCase)
            .Select(friend => new FriendItemViewModel(friend)));

        UpdateSummary();
        ApplyFilter();

        await LoadAvatarsAsync([.. Friends], ct).ConfigureAwait(true);
    }

    private async Task LoadAvatarsAsync(IReadOnlyList<FriendItemViewModel> items, CancellationToken ct)
    {
        var pending = items
            .Where(item => item.Avatar is null && !string.IsNullOrWhiteSpace(item.AvatarUrl))
            .ToArray();

        if (pending.Length == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(AvatarConcurrency, AvatarConcurrency);

        var loads = pending.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(true);

            try
            {
                item.Avatar = await _images
                    .GetImageAsync(item.AvatarUrl, AvatarPixelWidth, ct)
                    .ConfigureAwait(true);
            }
            finally
            {
                _ = gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(loads).ConfigureAwait(true);
    }

    private void ApplyFilter()
    {
        var term = FilterText.Trim();

        Friends.Clear();
        foreach (var item in _all)
        {
            if (term.Length == 0
                || item.DisplayName.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                Friends.Add(item);
            }
        }

        OnPropertyChanged(nameof(HasFriends));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void UpdateSummary()
    {
        if (_all.Count == 0)
        {
            SummaryText = HasApiKey
                ? "No friends synced yet."
                : "Add a Steam Web API key in Settings to see your friends.";
            return;
        }

        var online = _all.Count(item => item.IsOnline);
        var hidden = _all.Count(item => item.IsLibraryHidden);
        var unsynced = _all.Count(item => !item.IsSynced);

        var parts = new List<string>(4)
        {
            _all.Count == 1 ? "1 friend" : $"{_all.Count.ToString("N0", CultureInfo.CurrentCulture)} friends",
            $"{online.ToString("N0", CultureInfo.CurrentCulture)} online",
        };

        if (hidden > 0)
        {
            parts.Add($"{hidden.ToString("N0", CultureInfo.CurrentCulture)} with a hidden game list");
        }

        if (unsynced > 0)
        {
            parts.Add($"{unsynced.ToString("N0", CultureInfo.CurrentCulture)} not synced");
        }

        SummaryText = string.Join(" - ", parts);
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    /// <remarks>
    /// <see cref="ISettingsService.Changed"/> is documented as firing on the calling thread, and
    /// <see cref="ISettingsService.SaveAsync"/> is awaited from a command, so this arrives on a
    /// thread-pool thread. Both statements below reach the UI: <see cref="HasApiKey"/> is
    /// <c>[NotifyCanExecuteChangedFor]</c> the refresh command, and notifying a command asks its
    /// bound <c>Button</c> for its <c>Command</c>, which is dispatcher-affine and throws off-thread.
    /// </remarks>
    private void OnSettingsChanged(object? sender, UserSettingsChangedEventArgs e) =>
        _ = _dispatcher.InvokeAsync(
            () =>
            {
                HasApiKey = _settings.HasApiKey;
                UpdateSummary();
            },
            DispatcherPriority.Background);

    private void OnSelfPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBusy))
        {
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }
}
