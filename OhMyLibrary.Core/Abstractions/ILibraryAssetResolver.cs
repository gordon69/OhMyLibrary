using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Finds cached library art under <c>appcache/librarycache/&lt;appid&gt;/</c>.
/// </summary>
/// <remarks>
/// <para>
/// The current client stores assets inside opaque hash subfolders, so implementations glob for the
/// well-known file names rather than predicting the hash.
/// </para>
/// <para>
/// A resolver is free to cache its scans — the folder walk is the expensive part of drawing a grid —
/// which is why <see cref="Invalidate(int)"/> and <see cref="InvalidateAll"/> are on the interface
/// rather than on one implementation. A component that knows Steam has just rewritten art needs to
/// be able to drop the stale answer <i>and then</i> read the fresh one in a determined order; it
/// cannot get that from an implementation that only invalidates itself on an event whose handler
/// order is undefined.
/// </para>
/// </remarks>
public interface ILibraryAssetResolver
{
    /// <summary>
    /// Resolves whatever art the client has cached for an app. Never <see langword="null"/>: an app
    /// with no cached art yields a set whose paths are all <see langword="null"/>.
    /// </summary>
    /// <param name="appId">Steam application id.</param>
    GameAssets Resolve(int appId);

    /// <summary>
    /// A CDN cover URL to use when nothing is cached locally, or <see langword="null"/> when the
    /// app id is not usable. The URL needs no API key and may still 404.
    /// </summary>
    /// <param name="appId">Steam application id.</param>
    string? GetCoverUrlFallback(int appId);

    /// <summary>
    /// Drops any cached art for one app, so the next <see cref="Resolve(int)"/> reads the disk again.
    /// </summary>
    /// <param name="appId">Steam application id.</param>
    void Invalidate(int appId);

    /// <summary>
    /// Drops every cached result — for a change that could not be narrowed to app ids, or after
    /// Steam itself moved.
    /// </summary>
    void InvalidateAll();
}
