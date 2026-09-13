using CommunityToolkit.Mvvm.ComponentModel;

namespace OhMyLibrary.App.ViewModels.Pages;

/// <summary>How the library grid is ordered.</summary>
public enum LibrarySortOrder
{
    /// <summary>By name, ascending, with digit runs compared numerically.</summary>
    Name = 0,

    /// <summary>Most recently played first; never-played games last.</summary>
    LastPlayed = 1,

    /// <summary>Longest played first.</summary>
    Playtime = 2,

    /// <summary>Largest install first; games that are not installed last.</summary>
    Size = 3,

    /// <summary>Busy first, then update required, then installed, then the rest of the library.</summary>
    InstallState = 4,
}

/// <summary>One entry in the sort selector.</summary>
/// <param name="Order">The ordering this entry selects.</param>
/// <param name="Name">Its label.</param>
public sealed record LibrarySortOption(LibrarySortOrder Order, string Name);

/// <summary>
/// One entry in the collection selector. <see cref="CollectionId"/> is <see langword="null"/> for
/// the "all games" entry, which applies no collection filter at all.
/// </summary>
/// <param name="CollectionId">Collection id, or <see langword="null"/> for "all games".</param>
/// <param name="Name">Display name.</param>
public sealed record CollectionOption(long? CollectionId, string Name);

/// <summary>
/// A selectable genre or store tag in one of the facet lists, with the number of library rows that
/// carry it.
/// </summary>
/// <remarks>
/// The view model watches <see cref="IsSelected"/> on every option it publishes, so a checkbox in
/// the facet list re-runs the filter without any command plumbing.
/// </remarks>
public sealed partial class FilterOption : ObservableObject
{
    /// <summary>Creates a facet entry.</summary>
    /// <param name="id">Genre or store tag id.</param>
    /// <param name="name">Display name; <c>#id</c> when no name table resolved it.</param>
    /// <param name="count">How many library rows carry it.</param>
    public FilterOption(int id, string name, int count)
    {
        Id = id;
        Name = name;
        _count = count;
    }

    /// <summary>Genre or store tag id.</summary>
    public int Id { get; }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>How many library rows carry this id.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private int _count;

    /// <summary>Whether the facet is currently applied.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Name with its row count, for the checkbox caption.</summary>
    public string Label => Count > 0 ? $"{Name} ({Count})" : Name;
}
