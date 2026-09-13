using System.Windows.Media;

namespace OhMyLibrary.App.Services;

/// <summary>
/// Turns a local file path or a CDN URL into a frozen, size-limited <see cref="ImageSource"/> that a
/// view model can expose for binding.
/// </summary>
/// <remarks>
/// <para>
/// A three-thousand game grid must never hold three thousand full-size bitmaps, so every image is
/// decoded at the requested pixel width and frozen, the in-memory set is LRU-bounded, and downloaded
/// CDN images are kept on disk under <c>%LOCALAPPDATA%\OhMyLibrary\imagecache\</c>.
/// </para>
/// <para>
/// A missing file, a 404, a broken JPEG or a transport failure all resolve to <see langword="null"/>
/// and the caller shows a placeholder. The only exception that escapes is
/// <see cref="OperationCanceledException"/> when the caller's own token is cancelled.
/// </para>
/// </remarks>
public interface IImageCacheService
{
    /// <summary>
    /// Resolves an image, downloading and caching it when the source is an <c>http</c> or
    /// <c>https</c> URL.
    /// </summary>
    /// <param name="source">
    /// An absolute local file path or an absolute <c>http(s)</c> URL. <see langword="null"/>, empty
    /// and unsupported schemes all resolve to <see langword="null"/>.
    /// </param>
    /// <param name="decodePixelWidth">
    /// Width in pixels to decode at; the height follows the aspect ratio. Pass <c>0</c> to decode at
    /// the native size, which is only appropriate for a single hero image.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A frozen image, or <see langword="null"/> when it could not be produced.</returns>
    Task<ImageSource?> GetImageAsync(string? source, int decodePixelWidth, CancellationToken ct = default);

    /// <summary>
    /// Returns an already-decoded image without touching the disk or the network. Use it to paint a
    /// recycled list item immediately and only fall back to
    /// <see cref="GetImageAsync"/> when it misses.
    /// </summary>
    /// <param name="source">The same source string that would be passed to <see cref="GetImageAsync"/>.</param>
    /// <param name="decodePixelWidth">The same decode width that would be passed to <see cref="GetImageAsync"/>.</param>
    /// <param name="image">The cached image, which may be <see langword="null"/> for a known-bad source.</param>
    /// <returns><see langword="true"/> when the source has already been resolved.</returns>
    bool TryGetCached(string? source, int decodePixelWidth, out ImageSource? image);

    /// <summary>
    /// Drops the decoded bitmaps for these sources, at every decode width, so the next request reads
    /// the bytes again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cache is keyed by source, not by content, and Steam frequently rewrites an app's art
    /// <i>at the same path</i>. Without this, a corrected path is not enough: the same key resolves
    /// to the bitmap decoded from the previous bytes and the new art never reaches the screen.
    /// </para>
    /// <para>
    /// It also covers a load already in flight for one of these sources: the bytes it read are older
    /// than this call, so its result is returned to whoever asked for it but is not stored.
    /// </para>
    /// <para>
    /// Per source deliberately. Dropping the whole cache on every art event would re-decode a
    /// thousand covers because one game got a new capsule.
    /// </para>
    /// </remarks>
    /// <param name="sources">
    /// The same source strings that would be passed to <see cref="GetImageAsync"/>.
    /// <see langword="null"/> and unusable entries are ignored.
    /// </param>
    /// <returns>
    /// How many decoded bitmaps were actually dropped. It is the only externally visible evidence
    /// that this link of the chain did something, so the caller that reports the invalidation logs
    /// it rather than asserting the effect.
    /// </returns>
    int Invalidate(IEnumerable<string?> sources);

    /// <summary>Drops every decoded bitmap from memory. The disk cache is untouched.</summary>
    void ClearMemory();

    /// <summary>
    /// Deletes the downloaded-image disk cache. Never throws; a file that will not delete is logged
    /// and skipped.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of bytes reclaimed.</returns>
    Task<long> ClearDiskAsync(CancellationToken ct = default);
}
