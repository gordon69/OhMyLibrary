namespace OhMyLibrary.Core.Models;

/// <summary>
/// Local art for one app, resolved out of <c>appcache/librarycache/&lt;appid&gt;/</c>.
/// Every path is absolute, and <see langword="null"/> whenever the client has not cached that asset.
/// </summary>
/// <param name="AppId">Steam application id.</param>
/// <param name="CapsulePath"><c>library_capsule.jpg</c>, 300x450 — the grid cover.</param>
/// <param name="HeaderPath"><c>library_header.jpg</c>, 460x215.</param>
/// <param name="HeroPath"><c>library_hero.jpg</c>, 1920x620.</param>
/// <param name="LogoPath"><c>logo.png</c>, the loose transparent logo.</param>
/// <param name="IconPath">The loose <c>&lt;sha1&gt;.jpg</c> 32x32 icon.</param>
public sealed record GameAssets(
    int AppId,
    string? CapsulePath,
    string? HeaderPath,
    string? HeroPath,
    string? LogoPath,
    string? IconPath)
{
    /// <summary>
    /// Which read of the library cache produced this set. It moves every time the folder is scanned
    /// again after Steam rewrote the app's art, and it is part of the record's equality.
    /// </summary>
    /// <remarks>
    /// Without it, "Steam replaced <c>library_capsule.jpg</c> where it stood" is indistinguishable
    /// from "nothing happened": both produce a set with identical paths. A consumer holding a
    /// decoded bitmap needs to be able to tell those apart, because the second means keep it and the
    /// first means throw it away. It is an opaque counter — compare it, never interpret it.
    /// </remarks>
    public long Revision { get; init; }

    /// <summary>
    /// The best locally available grid cover: the capsule, else the header. When this is
    /// <see langword="null"/> the caller falls back to the CDN and then to a generated placeholder.
    /// </summary>
    public string? BestCover => CapsulePath ?? HeaderPath;

    /// <summary>True when nothing at all was cached locally for this app.</summary>
    public bool IsEmpty =>
        CapsulePath is null && HeaderPath is null && HeroPath is null
        && LogoPath is null && IconPath is null;

    /// <summary>An all-empty set of assets for an app with nothing in the library cache.</summary>
    /// <param name="appId">The app the empty set belongs to.</param>
    public static GameAssets None(int appId) => new(appId, null, null, null, null, null);
}
