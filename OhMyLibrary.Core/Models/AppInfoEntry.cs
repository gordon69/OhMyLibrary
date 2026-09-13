namespace OhMyLibrary.Core.Models;

/// <summary>
/// One app record out of <c>appcache/appinfo.vdf</c> — the local metadata source that needs no network.
/// </summary>
/// <param name="AppId">Steam application id.</param>
/// <param name="Name">Value of <c>common/name</c>.</param>
/// <param name="Type">
/// Value of <c>common/type</c>. Casing is inconsistent between apps (<c>game</c> vs <c>Game</c>),
/// so always compare with <see cref="StringComparison.OrdinalIgnoreCase"/>.
/// </param>
/// <param name="SortAs">Value of <c>common/sortas</c>, when the app overrides its sort name.</param>
/// <param name="GenreIds">Genre ids from <c>common/genres</c>, in file order.</param>
/// <param name="StoreTagIds">Store tag ids from <c>common/store_tags</c>, most relevant first.</param>
/// <param name="CategoryIds">Store category ids parsed out of the <c>common/category</c> key suffixes.</param>
/// <param name="LocalizedNames">Language code to name, from <c>common/name_localized</c>.</param>
/// <param name="Developer">Developer name when the record carries one.</param>
/// <param name="Publisher">Publisher name when the record carries one.</param>
/// <param name="ReleaseDate">Parsed <c>common/steam_release_date</c>.</param>
/// <param name="MetacriticScore">Parsed <c>common/metacritic_score</c>.</param>
/// <param name="OsList">Raw <c>common/oslist</c>, a comma-separated list such as <c>windows,macos,linux</c>.</param>
/// <param name="ChangeNumber">PICS change number from the record header, useful for change detection.</param>
/// <param name="LastUpdated">Record header timestamp; <see langword="null"/> when it was zero.</param>
public sealed record AppInfoEntry(
    int AppId,
    string? Name,
    string? Type,
    string? SortAs,
    IReadOnlyList<int> GenreIds,
    IReadOnlyList<int> StoreTagIds,
    IReadOnlyList<int> CategoryIds,
    IReadOnlyDictionary<string, string> LocalizedNames,
    string? Developer,
    string? Publisher,
    DateTimeOffset? ReleaseDate,
    int? MetacriticScore,
    string? OsList,
    uint ChangeNumber,
    DateTimeOffset? LastUpdated)
{
    /// <summary>
    /// True when <see cref="Type"/> is <c>game</c>, ignoring case. Everything else — DLC, demos,
    /// tools and redistributables such as app <c>228980</c> — is filtered out of the library view.
    /// </summary>
    public bool IsGame => string.Equals(Type, "game", StringComparison.OrdinalIgnoreCase);
}
