using System.Globalization;
using System.Text;
using System.Windows.Media;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.Extensions.Logging;

using OhMyLibrary.App.Services;
using OhMyLibrary.Core.Models;

using Wpf.Ui.Controls;

namespace OhMyLibrary.App.ViewModels.Pages;

/// <summary>What the card's primary button offers, derived from the app's install state.</summary>
public enum GameCardAction
{
    /// <summary>The app is installed and idle: launch it.</summary>
    Play = 0,

    /// <summary>The app is owned but not on disk: ask Steam to install it.</summary>
    Install = 1,

    /// <summary>The app is on disk but stale, missing files or corrupt: ask Steam to update it.</summary>
    Update = 2,

    /// <summary>Steam is already working on the app: show progress, offer nothing.</summary>
    Working = 3,
}

/// <summary>
/// One card in the library grid: a <see cref="GameEntry"/> plus everything the grid needs that the
/// domain model deliberately does not carry — a decoded cover, formatted captions, and the
/// pre-folded keys the filter and the sort comparer run against.
/// </summary>
/// <remarks>
/// <para>
/// The pre-computed <see cref="SearchKey"/>, <see cref="SortKey"/> and id sets exist so that
/// filtering three thousand cards costs a few thousand set lookups rather than a few thousand string
/// normalisations. They are rebuilt only when <see cref="Update"/> replaces the entry.
/// </para>
/// <para>
/// The card owns its cover but not its commands: those live on the page view model, which every
/// binding reaches through <see cref="Owner"/>. That is what keeps the context menu working, since a
/// popup is outside the page's visual tree and cannot use <c>RelativeSource AncestorType</c>.
/// </para>
/// </remarks>
public sealed partial class GameCardViewModel : ViewModelBase
{
    private readonly IImageCacheService _images;
    private readonly string? _coverUrlFallback;

    private HashSet<int> _genreIds = [];
    private HashSet<int> _tagIds = [];
    private HashSet<long> _collectionIds = [];
    private GameEntry _entry;
    private bool _coverRequested;

    /// <summary>Creates a card for one library row.</summary>
    /// <param name="owner">The page view model that owns the grid and the commands.</param>
    /// <param name="entry">The library row this card shows.</param>
    /// <param name="coverUrlFallback">
    /// CDN cover URL from <c>ILibraryAssetResolver.GetCoverUrlFallback</c>, used when nothing is
    /// cached locally, or <see langword="null"/> when there is no usable fallback.
    /// </param>
    /// <param name="images">Decoding image cache.</param>
    /// <param name="cardWidth">Initial cover width in device-independent pixels.</param>
    /// <param name="logger">Log sink.</param>
    public GameCardViewModel(
        LibraryViewModel owner,
        GameEntry entry,
        string? coverUrlFallback,
        IImageCacheService images,
        double cardWidth,
        ILogger logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(images);

        Owner = owner;
        _entry = entry;
        _coverUrlFallback = coverUrlFallback;
        _images = images;
        _cardWidth = cardWidth;

        SearchKey = Fold(entry.Name);
        SortKey = SearchKey;
        RebuildIndexes(entry);
    }

    /// <summary>The page view model, so a card's buttons and context menu can reach its commands.</summary>
    public LibraryViewModel Owner { get; }

    /// <summary>Steam application id.</summary>
    public int AppId => _entry.AppId;

    /// <summary>The library row behind the card.</summary>
    public GameEntry Entry => _entry;

    /// <summary>Case- and diacritic-folded name, matched against the folded search text.</summary>
    public string SearchKey { get; private set; }

    /// <summary>Folded name used by the sort comparer, so ordering does not depend on the culture.</summary>
    public string SortKey { get; private set; }

    /// <summary>Genre ids on this app, for the genre facet.</summary>
    public IReadOnlyCollection<int> GenreIds => _genreIds;

    /// <summary>Store tag ids on this app, for the tag facet.</summary>
    public IReadOnlyCollection<int> TagIds => _tagIds;

    /// <summary>Decoded cover, or <see langword="null"/> while it is missing or still loading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private ImageSource? _cover;

    /// <summary>True once a cover has been decoded; drives the generated placeholder.</summary>
    public bool HasCover => Cover is not null;

    /// <summary>Cover width in device-independent pixels, from <c>Ui:CardWidth</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoverHeight))]
    private double _cardWidth;

    /// <summary>Cover height: a Steam capsule is 300x450, so height is width times 1.5.</summary>
    public double CoverHeight => Math.Round(CardWidth * LibraryViewModel.CoverAspectRatio);

    /// <summary>Display name.</summary>
    public string Name => _entry.Name;

    /// <summary>True when a manifest for the app was found in a reachable library.</summary>
    public bool IsInstalled => _entry.IsInstalled;

    /// <summary>True when the content on disk is stale, missing or corrupt.</summary>
    public bool NeedsUpdate => _entry.NeedsUpdate;

    /// <summary>True while the Steam client is downloading, validating or removing the app.</summary>
    public bool IsWorking => _entry.IsBusy;

    /// <summary>What the primary button offers for the current state.</summary>
    public GameCardAction PrimaryAction =>
        IsWorking ? GameCardAction.Working
        : !IsInstalled ? GameCardAction.Install
        : NeedsUpdate ? GameCardAction.Update
        : GameCardAction.Play;

    /// <summary>Caption of the primary button.</summary>
    public string PrimaryActionText => PrimaryAction switch
    {
        GameCardAction.Install => "Install",
        GameCardAction.Update => "Update",
        GameCardAction.Working => WorkingText,
        _ => "Play",
    };

    /// <summary>Icon of the primary button.</summary>
    public SymbolRegular PrimaryActionIcon => PrimaryAction switch
    {
        GameCardAction.Install => SymbolRegular.ArrowDownload24,
        GameCardAction.Update => SymbolRegular.ArrowSync24,
        GameCardAction.Working => SymbolRegular.ArrowSync24,
        _ => SymbolRegular.Play24,
    };

    /// <summary>False while Steam is already working on the app, when there is nothing to ask for.</summary>
    public bool CanRunPrimaryAction => PrimaryAction != GameCardAction.Working;

    /// <summary>True when the "update available" badge belongs on the cover.</summary>
    public bool ShowUpdateBadge => NeedsUpdate && !IsWorking;

    /// <summary>True when the "installed" badge belongs on the cover.</summary>
    public bool ShowInstalledBadge => IsInstalled && !NeedsUpdate && !IsWorking;

    /// <summary>True while the transient state badge belongs on the cover.</summary>
    public bool ShowWorkingBadge => IsWorking;

    /// <summary>What Steam is doing right now, for the transient badge.</summary>
    public string WorkingText => DescribeWork(_entry.StateFlags);

    /// <summary>Transfer progress in the range <c>0..1</c>; <c>0</c> when nothing is in flight.</summary>
    public double DownloadProgress => _entry.DownloadProgress ?? 0d;

    /// <summary>True when a transfer size is known, so the progress strip is meaningful.</summary>
    public bool HasDownloadProgress => _entry.DownloadProgress.HasValue;

    /// <summary>Size on disk, formatted, or <see langword="null"/> when the app is not installed.</summary>
    public string? SizeText => _entry.SizeBytes > 0 ? FormatBytes(_entry.SizeBytes) : null;

    /// <summary>Lifetime playtime, formatted, or <see langword="null"/> when it is unknown.</summary>
    public string? PlaytimeText =>
        _entry.PlaytimeForeverMinutes > 0 ? FormatPlaytime(_entry.PlaytimeForeverMinutes) : null;

    /// <summary>Last played, formatted, or <see langword="null"/> when it was never played.</summary>
    public string? LastPlayedText => _entry.LastPlayed is { } played ? FormatLastPlayed(played) : null;

    /// <summary>The single muted metadata line under the title.</summary>
    public string Caption
    {
        get
        {
            var parts = new List<string>(2);

            if (IsWorking)
            {
                parts.Add(WorkingText);
            }
            else if (!IsInstalled)
            {
                parts.Add("Not installed");
            }
            else if (SizeText is { } size)
            {
                parts.Add(size);
            }

            if (PlaytimeText is { } playtime)
            {
                parts.Add(playtime);
            }
            else if (IsInstalled && !IsWorking)
            {
                parts.Add("Never played");
            }

            return parts.Count == 0 ? "Owned" : string.Join(" · ", parts);
        }
    }

    /// <summary>Everything the card cannot fit, for the tooltip.</summary>
    public string TooltipText
    {
        get
        {
            var builder = new StringBuilder(Name);

            _ = builder.Append('\n').Append(Caption);

            if (LastPlayedText is { } lastPlayed)
            {
                _ = builder.Append("\nLast played ").Append(lastPlayed);
            }

            if (_entry.Genres.Count > 0)
            {
                _ = builder.Append('\n').Append(string.Join(", ", _entry.Genres.Take(4).Select(genre => genre.Name)));
            }

            if (_entry.Tags.Count > 0)
            {
                _ = builder.Append('\n').Append(string.Join(", ", _entry.Tags.Take(5).Select(tag => tag.Name)));
            }

            return builder.ToString();
        }
    }

    /// <summary>Sort key for "last played": ticks, with never-played sorting last.</summary>
    public long LastPlayedTicks => _entry.LastPlayed?.UtcTicks ?? 0L;

    /// <summary>Sort key for "playtime".</summary>
    public int PlaytimeMinutes => _entry.PlaytimeForeverMinutes;

    /// <summary>Sort key for "size on disk".</summary>
    public long SizeBytes => _entry.SizeBytes;

    /// <summary>
    /// Sort key for "install state": what needs attention first, then what can be played, then the
    /// rest of the owned library.
    /// </summary>
    public int InstallRank =>
        IsWorking ? 0
        : NeedsUpdate ? 1
        : IsInstalled ? 2
        : 3;

    /// <summary>Replaces the row behind the card and re-raises every derived property.</summary>
    /// <param name="entry">The refreshed row, for the same <see cref="AppId"/>.</param>
    public void Update(GameEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var nameChanged = !string.Equals(_entry.Name, entry.Name, StringComparison.Ordinal);

        // Record equality, not a path comparison: art that Steam rewrote where it stood produces the
        // same path with different bytes, and GameAssets.Revision is what separates that from a
        // refresh in which nothing about the art moved. Comparing paths alone left the card holding
        // a bitmap decoded from bytes that no longer exist.
        var artChanged = _entry.Assets != entry.Assets;

        _entry = entry;

        if (nameChanged)
        {
            SearchKey = Fold(entry.Name);
            SortKey = SearchKey;
        }

        RebuildIndexes(entry);

        // One blanket notification: a card has a dozen derived properties, and only the handful of
        // realised containers ever re-read them.
        OnPropertyChanged(string.Empty);

        // Only a card that has already asked for its cover — that is, one a container has realised —
        // re-decodes here, and only when the bytes behind it actually moved. A card that has never
        // been realised keeps _coverRequested false, so the next realisation reads the new art path
        // by itself; asking on its behalf is what turned the virtualised grid into one decode per
        // game in the library on every refresh, whether the game was on screen or not. See
        // ReloadCover for why the latch, not Cover, is the test: a realised card whose art is
        // missing has a null Cover for good, and "Cover is null" made every such card re-decode on
        // every event forever.
        if (artChanged && _coverRequested)
        {
            ReloadCover();
        }
    }

    /// <summary>
    /// Re-decodes the cover because the art behind it changed on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="EnsureCoverAsync"/> is idempotent by design — it is called every time a container
    /// is realised — so it cannot be the thing that repaints a card whose art moved. A card that is
    /// already on screen is never realised again either, so nothing else would ask. This clears the
    /// latch and asks, and it is called only when something upstream has established that the bytes
    /// are stale, which is what keeps a routine rescan from re-decoding the whole grid.
    /// </para>
    /// <para>
    /// It is called only for a card whose latch is already set, i.e. one a container has realised.
    /// Decoding for a card that was never shown costs a file read, a JPEG decode and — when Steam
    /// has cached no art for the app — a CDN request, none of which anybody is waiting to see, and
    /// doing it for every row of the library is work proportional to the whole library on an event
    /// that changed one thing.
    /// </para>
    /// </remarks>
    private void ReloadCover()
    {
        _coverRequested = false;

        // Fire and forget, on the dispatcher, exactly as a realised container does: the call never
        // throws, and the new cover simply appears when it is ready.
        _ = EnsureCoverAsync(Owner.PageLifetime);
    }

    /// <summary>True when the app carries at least one of the given genre ids.</summary>
    /// <param name="genreIds">Selected genre ids; never empty when this is called.</param>
    public bool MatchesAnyGenre(IReadOnlyCollection<int> genreIds)
    {
        ArgumentNullException.ThrowIfNull(genreIds);

        foreach (var id in genreIds)
        {
            if (_genreIds.Contains(id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the app carries at least one of the given store tag ids.</summary>
    /// <param name="tagIds">Selected tag ids; never empty when this is called.</param>
    public bool MatchesAnyTag(IReadOnlyCollection<int> tagIds)
    {
        ArgumentNullException.ThrowIfNull(tagIds);

        foreach (var id in tagIds)
        {
            if (_tagIds.Contains(id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the app belongs to the given collection.</summary>
    /// <param name="collectionId">Collection id.</param>
    public bool IsInCollection(long collectionId) => _collectionIds.Contains(collectionId);

    /// <summary>
    /// Decodes the cover once per card and per width. Synchronous and allocation-free when the image
    /// cache already holds it, which is what keeps a recycled container from flashing a placeholder.
    /// </summary>
    /// <param name="ct">Cancellation token, normally the page's lifetime token.</param>
    public async Task EnsureCoverAsync(CancellationToken ct = default)
    {
        if (_coverRequested)
        {
            return;
        }

        var decodeWidth = (int)Math.Round(CardWidth);
        var local = _entry.Assets?.BestCover;

        if (_images.TryGetCached(local, decodeWidth, out var cached) && cached is not null)
        {
            _coverRequested = true;
            Cover = cached;
            return;
        }

        _coverRequested = true;

        try
        {
            var image = await _images.GetImageAsync(local, decodeWidth, ct).ConfigureAwait(true);

            if (image is null && _coverUrlFallback is not null)
            {
                image = await _images.GetImageAsync(_coverUrlFallback, decodeWidth, ct).ConfigureAwait(true);
            }

            Cover = image;
        }
        catch (OperationCanceledException)
        {
            // The page is shutting down: let a later realisation try again.
            _coverRequested = false;
        }
        catch (Exception ex)
        {
            // IImageCacheService promises not to throw for missing or broken art; anything else is a
            // bug worth a log line, never a broken grid.
            Logger.LogError(ex, "Cover load failed for {AppId}", AppId);
        }
    }

    /// <summary>Re-decodes the cover after the configured card width changed.</summary>
    /// <param name="cardWidth">The new cover width in device-independent pixels.</param>
    public void Resize(double cardWidth)
    {
        if (Math.Abs(CardWidth - cardWidth) < 0.5)
        {
            return;
        }

        CardWidth = cardWidth;
        _coverRequested = false;
    }

    /// <summary>
    /// Folds a string for searching and sorting: trimmed, lower-cased and stripped of diacritics, so
    /// that "pokemon" finds "Pokémon".
    /// </summary>
    /// <param name="value">The text to fold.</param>
    /// <returns>The folded text; empty when <paramref name="value"/> was null or blank.</returns>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                _ = builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private void RebuildIndexes(GameEntry entry)
    {
        _genreIds = entry.Genres.Count == 0 ? [] : [.. entry.Genres.Select(genre => genre.GenreId)];
        _tagIds = entry.Tags.Count == 0 ? [] : [.. entry.Tags.Select(tag => tag.TagId)];
        _collectionIds = entry.CollectionIds.Count == 0 ? [] : [.. entry.CollectionIds];
    }

    private static string DescribeWork(AppStateFlags flags)
    {
        if ((flags & AppStateFlags.Uninstalling) != 0)
        {
            return "Uninstalling";
        }

        if ((flags & AppStateFlags.Validating) != 0)
        {
            return "Validating";
        }

        if ((flags & AppStateFlags.UpdatePaused) != 0)
        {
            return "Paused";
        }

        if ((flags & (AppStateFlags.Staging | AppStateFlags.Committing)) != 0)
        {
            return "Installing";
        }

        if ((flags & (AppStateFlags.Downloading | AppStateFlags.Preallocating | AppStateFlags.AddingFiles)) != 0)
        {
            return "Downloading";
        }

        if ((flags & AppStateFlags.BackupRunning) != 0)
        {
            return "Backing up";
        }

        return (flags & AppStateFlagsExtensions.BusyMask) != 0 ? "Updating" : "Idle";
    }

    private static string FormatBytes(long bytes)
    {
        const double Kilobyte = 1024d;
        string[] units = ["B", "KB", "MB", "GB", "TB"];

        double value = bytes;
        var unit = 0;

        while (value >= Kilobyte && unit < units.Length - 1)
        {
            value /= Kilobyte;
            unit++;
        }

        var format = unit == 0 || value >= 100 ? "0" : "0.0";
        return $"{value.ToString(format, CultureInfo.CurrentCulture)} {units[unit]}";
    }

    private static string FormatPlaytime(int minutes)
    {
        if (minutes < 60)
        {
            return $"{minutes.ToString(CultureInfo.CurrentCulture)} min";
        }

        var hours = minutes / 60d;
        var format = hours >= 100 ? "0" : "0.0";
        return $"{hours.ToString(format, CultureInfo.CurrentCulture)} h";
    }

    private static string FormatLastPlayed(DateTimeOffset played)
    {
        var days = (int)(DateTimeOffset.UtcNow - played).TotalDays;

        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 30 => $"{days.ToString(CultureInfo.CurrentCulture)} days ago",
            _ => played.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
        };
    }
}
