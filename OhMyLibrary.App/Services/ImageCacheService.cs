using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Microsoft.Extensions.Logging;

namespace OhMyLibrary.App.Services;

/// <summary>
/// LRU-bounded, disk-backed implementation of <see cref="IImageCacheService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Decoding happens off the dispatcher and the resulting <see cref="BitmapImage"/> is frozen, which
/// is what makes it legal to hand across threads and cheap for WPF to render.
/// </para>
/// <para>
/// Concurrent requests for the same source collapse onto one load. That shared load runs on the
/// service's own lifetime token rather than the caller's, so one card scrolling out of view does not
/// cancel a download three other cards are waiting on; each caller still observes its own token.
/// </para>
/// </remarks>
/// <param name="httpClientFactory">Factory for the named client used to fetch CDN images.</param>
/// <param name="logger">Log sink.</param>
public sealed class ImageCacheService(
    IHttpClientFactory httpClientFactory,
    ILogger<ImageCacheService> logger) : IImageCacheService, IDisposable
{
    /// <summary>Name of the <see cref="HttpClient"/> registration this service resolves.</summary>
    public const string HttpClientName = "OhMyLibrary.Images";

    /// <summary>How many decoded bitmaps are kept in memory before the least recently used is dropped.</summary>
    public const int MemoryCapacity = 768;

    /// <summary>Largest CDN response accepted, in bytes. Steam capsules are well under this.</summary>
    private const int MaxDownloadBytes = 16 * 1024 * 1024;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _order = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> _inFlight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// How many times each source has been invalidated. A load stamps the count it started with and
    /// declines to store its result if the count has moved, which is what stops a decode of the
    /// bytes Steam has just overwritten from being cached a moment after the invalidation.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _invalidations = new(StringComparer.Ordinal);

    /// <summary>
    /// The whole-cache counterpart of <see cref="_invalidations"/>, moved by <see cref="ClearMemory"/>.
    /// A clear has to make a load in flight just as unpublishable as a per-source invalidation does,
    /// or the one path that cannot name its sources — the watcher losing its events to a buffer
    /// overflow — is also the one path that can put a stale bitmap straight back.
    /// </summary>
    private long _epoch;

    private bool _disposed;

    /// <inheritdoc />
    public async Task<ImageSource?> GetImageAsync(string? source, int decodePixelWidth, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!TryNormalise(source, out var normalised, out var isRemote))
        {
            return null;
        }

        var key = MakeKey(normalised, decodePixelWidth);

        if (TryGetFromMemory(key, out var cached))
        {
            return cached;
        }

        if (_disposed)
        {
            return null;
        }

        var load = _inFlight.GetOrAdd(
            key,
            k => new Lazy<Task<ImageSource?>>(
                () => LoadAsync(k, normalised, isRemote, decodePixelWidth),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return await load.Value.WaitAsync(ct).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public bool TryGetCached(string? source, int decodePixelWidth, out ImageSource? image)
    {
        image = null;
        return TryNormalise(source, out var normalised, out _)
            && TryGetFromMemory(MakeKey(normalised, decodePixelWidth), out image);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The whole batch is collected first and the cache is then walked <b>once</b>. A pass per
    /// source instead is <c>sources x</c> <see cref="MemoryCapacity"/> string comparisons, and the
    /// one caller that matters — the sync coordinator, which hands over five paths per app whose art
    /// Steam rewrote — can name thousands of apps in a single burst.
    /// </remarks>
    public int Invalidate(IEnumerable<string?> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var normalisedSources = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            if (!TryNormalise(source, out var normalised, out _))
            {
                continue;
            }

            // Bumped before the entries go, so a load that is already past its own read cannot slip
            // a stale bitmap back in behind the removal.
            _ = _invalidations.AddOrUpdate(normalised, 1, static (_, count) => count + 1);
            _ = normalisedSources.Add(normalised);
        }

        var dropped = normalisedSources.Count == 0 ? 0 : DropAllWidths(normalisedSources);

        if (dropped > 0)
        {
            logger.LogDebug("Dropped {Count} decoded bitmap(s) whose source was rewritten", dropped);
        }

        return dropped;
    }

    /// <inheritdoc />
    public void ClearMemory()
    {
        _ = Interlocked.Increment(ref _epoch);

        lock (_sync)
        {
            _index.Clear();
            _order.Clear();
        }
    }

    /// <inheritdoc />
    public async Task<long> ClearDiskAsync(CancellationToken ct = default)
    {
        return await Task.Run(
            () =>
            {
                long reclaimed = 0;

                if (!Directory.Exists(AppPaths.ImageCacheDirectory))
                {
                    return reclaimed;
                }

                foreach (var file in Directory.EnumerateFiles(AppPaths.ImageCacheDirectory, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var length = new FileInfo(file).Length;
                        File.Delete(file);
                        reclaimed += length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogDebug(ex, "Could not delete cached image {Path}", file);
                    }
                }

                return reclaimed;
            },
            ct).ConfigureAwait(true);
    }

    /// <summary>Cancels in-flight downloads and releases the cache.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down; nothing left to cancel.
        }

        _lifetime.Dispose();
        ClearMemory();
        _inFlight.Clear();
        _invalidations.Clear();
    }

    private async Task<ImageSource?> LoadAsync(string key, string normalised, bool isRemote, int decodePixelWidth)
    {
        // Taken before the first byte is read. Anything that invalidates this source from here on
        // makes the bytes below stale, and the Store calls check the stamp rather than caching them.
        var stamp = new LoadStamp(CurrentInvalidationCount(normalised), Interlocked.Read(ref _epoch));

        try
        {
            var fetch = isRemote
                ? await GetRemoteBytesAsync(normalised, _lifetime.Token).ConfigureAwait(false)
                : await GetLocalBytesAsync(normalised, _lifetime.Token).ConfigureAwait(false);

            if (fetch.Bytes is null)
            {
                logger.LogDebug("No bytes for {Source} (definitive: {Definitive})", normalised, fetch.Definitive);

                // A 404 or a missing decode target is worth remembering; a timed-out connection is
                // not, or the card would stay blank until the app restarts.
                if (fetch.Definitive)
                {
                    Store(key, normalised, stamp, null);
                }

                return null;
            }

            var image = await Task
                .Run(() => Decode(fetch.Bytes, decodePixelWidth), _lifetime.Token)
                .ConfigureAwait(false);

            if (image is null)
            {
                logger.LogDebug("Bytes for {Source} are not a decodable image", normalised);
            }

            Store(key, normalised, stamp, image);
            return image;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Shutdown, or the whole service was disposed. Nothing is cached; a later call retries.
            logger.LogDebug(ex, "Image load for {Source} was abandoned", normalised);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected failure resolving image {Source}", normalised);
            Store(key, normalised, stamp, null);
            return null;
        }
        finally
        {
            _ = _inFlight.TryRemove(key, out _);
        }
    }

    private async Task<FetchResult> GetLocalBytesAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
            {
                // Steam writes library art lazily, so "not there yet" must stay retryable.
                return FetchResult.Retryable;
            }

            return new FetchResult(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false), true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogDebug(ex, "Could not read local image {Path}", path);
            return FetchResult.Retryable;
        }
    }

    private async Task<FetchResult> GetRemoteBytesAsync(string url, CancellationToken ct)
    {
        var cachePath = GetDiskCachePath(url);

        var cached = await GetLocalBytesAsync(cachePath, ct).ConfigureAwait(false);
        if (cached.Bytes is { Length: > 0 })
        {
            return cached;
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // A 404 for an app with no CDN art is the common case and will not change today.
                logger.LogDebug("Image {Url} returned {Status}", url, (int)response.StatusCode);
                return FetchResult.NotAvailable;
            }

            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                logger.LogDebug("Image {Url} is larger than the {Limit} byte cap", url, MaxDownloadBytes);
                return FetchResult.NotAvailable;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length is 0 or > MaxDownloadBytes)
            {
                return FetchResult.NotAvailable;
            }

            await WriteDiskCacheAsync(cachePath, bytes, ct).ConfigureAwait(false);
            return new FetchResult(bytes, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            // Offline, DNS down, a timeout: all temporary, so the next request tries again.
            logger.LogDebug(ex, "Could not download image {Url}", url);
            return FetchResult.Retryable;
        }
    }

    private async Task WriteDiskCacheAsync(string cachePath, byte[] bytes, CancellationToken ct)
    {
        var temporaryPath = cachePath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (directory is not null)
            {
                _ = Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(temporaryPath, bytes, ct).ConfigureAwait(false);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not write the image cache entry {Path}", cachePath);
        }
    }

    private static BitmapImage? Decode(byte[] bytes, int decodePixelWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

            if (decodePixelWidth > 0)
            {
                image.DecodePixelWidth = decodePixelWidth;
            }

            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FileFormatException or OverflowException or IOException)
        {
            // A truncated or non-image payload. The caller shows a placeholder.
            return null;
        }
    }

    private static string GetDiskCachePath(string url)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(AppPaths.ImageCacheDirectory, hash[..2], hash + ".img");
    }

    private static string MakeKey(string normalised, int decodePixelWidth) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{decodePixelWidth}|{normalised}");

    private static bool TryNormalise(string? source, out string normalised, out bool isRemote)
    {
        normalised = string.Empty;
        isRemote = false;

        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var trimmed = source.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            normalised = uri.AbsoluteUri;
            isRemote = true;
            return true;
        }

        try
        {
            normalised = Path.GetFullPath(trimmed);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    private bool TryGetFromMemory(string key, out ImageSource? image)
    {
        lock (_sync)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    private long CurrentInvalidationCount(string normalised) =>
        _invalidations.TryGetValue(normalised, out var count) ? count : 0;

    /// <summary>
    /// Removes every decode-width variant of every source in one walk of the cache. The key is
    /// <c>&lt;width&gt;|&lt;source&gt;</c>, and a decode width never contains a separator, so the
    /// part after the first one is the source itself and no second index is needed. One walk rather
    /// than one per source is what keeps a burst naming thousands of apps proportional to the burst
    /// plus the cache instead of to their product.
    /// </summary>
    /// <param name="normalisedSources">Normalised sources to drop; never empty.</param>
    private int DropAllWidths(HashSet<string> normalisedSources)
    {
        var dropped = 0;

        lock (_sync)
        {
            var node = _order.First;
            while (node is not null)
            {
                var next = node.Next;
                var key = node.Value.Key;
                var separator = key.IndexOf('|', StringComparison.Ordinal);

                if (separator >= 0 && normalisedSources.Contains(key[(separator + 1)..]))
                {
                    _order.Remove(node);
                    _ = _index.Remove(key);
                    dropped++;
                }

                node = next;
            }
        }

        return dropped;
    }

    /// <summary>
    /// Publishes a load's result, unless the source was invalidated while the load was running — in
    /// which case the bytes it decoded are already known to be stale and caching them would undo the
    /// invalidation.
    /// </summary>
    private void Store(string key, string normalised, LoadStamp stamp, ImageSource? image)
    {
        if (stamp != new LoadStamp(CurrentInvalidationCount(normalised), Interlocked.Read(ref _epoch)))
        {
            logger.LogDebug("Not caching {Source}: it was invalidated while it was being loaded", normalised);
            return;
        }

        lock (_sync)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _ = _index.Remove(key);
            }

            var node = _order.AddFirst(new CacheEntry(key, image));
            _index[key] = node;

            while (_index.Count > MemoryCapacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _ = _index.Remove(last.Value.Key);
            }
        }
    }

    private sealed record CacheEntry(string Key, ImageSource? Image);

    /// <summary>
    /// What a load has to still be true when it finishes for its result to be worth caching: neither
    /// its own source nor the cache as a whole was invalidated while it was reading.
    /// </summary>
    /// <param name="Source">The source's invalidation count when the load started.</param>
    /// <param name="Epoch">The whole-cache clear count when the load started.</param>
    private readonly record struct LoadStamp(long Source, long Epoch);

    /// <summary>
    /// The outcome of a byte fetch. <paramref name="Definitive"/> says whether a failure is worth
    /// remembering: a 404 is, an offline network is not.
    /// </summary>
    private readonly record struct FetchResult(byte[]? Bytes, bool Definitive)
    {
        /// <summary>The source will not produce bytes: cache the miss.</summary>
        public static FetchResult NotAvailable => new(null, true);

        /// <summary>The source failed for a transient reason: do not cache the miss.</summary>
        public static FetchResult Retryable => new(null, false);
    }
}
