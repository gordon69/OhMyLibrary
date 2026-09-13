using System.Collections.ObjectModel;
using System.Windows.Media;

using CommunityToolkit.Mvvm.ComponentModel;

using OhMyLibrary.Core.Models;

namespace OhMyLibrary.App.ViewModels.Pages;

/// <summary>
/// One row of the collections list: the name, the member count and a short strip of cover art.
/// </summary>
/// <remarks>
/// A pure data holder — every service call lives on <see cref="CollectionsViewModel"/>, which is
/// where the guarded-run helper is. The inline rename editor is modelled with
/// <see cref="IsEditing"/> plus <see cref="EditName"/> so a cancelled edit never touches
/// <see cref="Name"/>.
/// </remarks>
public partial class CollectionItemViewModel : ObservableObject
{
    /// <summary>Creates a row from a stored collection.</summary>
    /// <param name="collection">The collection this row shows.</param>
    public CollectionItemViewModel(GameCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        CollectionId = collection.CollectionId;
        CreatedUtc = collection.CreatedUtc;
        _name = collection.Name;
        _gameCount = collection.AppIds.Count;
        AppIds = collection.AppIds;
    }

    /// <summary>Database id of the collection.</summary>
    public long CollectionId { get; }

    /// <summary>When the collection was created, in UTC.</summary>
    public DateTimeOffset CreatedUtc { get; }

    /// <summary>Member app ids, in the collection's own order.</summary>
    public IReadOnlyList<int> AppIds { get; private set; }

    /// <summary>Up to a handful of member covers, shown as a strip under the name.</summary>
    public ObservableCollection<CollectionGameViewModel> Covers { get; } = [];

    /// <summary>The committed display name.</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>True while the inline rename editor is open on this row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditing))]
    private bool _isEditing;

    /// <summary>The text being edited. Discarded unless the rename is committed.</summary>
    [ObservableProperty]
    private string _editName = string.Empty;

    /// <summary>How many games the collection holds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameCountText))]
    private int _gameCount;

    /// <summary>Inverse of <see cref="IsEditing"/>, for the read-only half of the row.</summary>
    public bool IsNotEditing => !IsEditing;

    /// <summary>The member count as a caption, for example <c>"12 games"</c>.</summary>
    public string GameCountText => GameCount == 1 ? "1 game" : $"{GameCount} games";

    /// <summary>Replaces the row's membership after an add, a remove or a reload.</summary>
    /// <param name="appIds">The new member app ids, in collection order.</param>
    public void SetMembers(IReadOnlyList<int> appIds)
    {
        ArgumentNullException.ThrowIfNull(appIds);

        AppIds = appIds;
        GameCount = appIds.Count;
    }
}

/// <summary>
/// One game shown inside the collections page: a cover thumbnail plus its name.
/// </summary>
/// <remarks>
/// The two source strings are resolved once by <see cref="CollectionsViewModel"/> — the local
/// library-cache path first, the CDN URL as the fallback — and handed to
/// <c>IImageCacheService</c>. <see cref="Cover"/> stays <see langword="null"/> when neither
/// resolves, which is the signal to show the placeholder brush.
/// </remarks>
public partial class CollectionGameViewModel : ObservableObject
{
    /// <summary>Creates the item.</summary>
    /// <param name="appId">Steam application id.</param>
    /// <param name="name">Display name.</param>
    /// <param name="localCoverPath">Absolute path of the locally cached cover, when there is one.</param>
    /// <param name="coverUrl">CDN cover URL used when nothing is cached locally.</param>
    public CollectionGameViewModel(int appId, string name, string? localCoverPath, string? coverUrl)
    {
        AppId = appId;
        Name = name;
        LocalCoverPath = localCoverPath;
        CoverUrl = coverUrl;
    }

    /// <summary>Steam application id.</summary>
    public int AppId { get; }

    /// <summary>Display name, or a synthetic <c>"App 440"</c> when the library has no row for it.</summary>
    public string Name { get; }

    /// <summary>Absolute path of the locally cached cover, or <see langword="null"/>.</summary>
    public string? LocalCoverPath { get; }

    /// <summary>CDN cover URL, or <see langword="null"/> when the app id is not usable.</summary>
    public string? CoverUrl { get; }

    /// <summary>The decoded cover, or <see langword="null"/> while it is missing or still loading.</summary>
    [ObservableProperty]
    private ImageSource? _cover;
}
