using System.Windows.Media;

using OhMyLibrary.App.Services;

namespace OhMyLibrary.Tests.Performance;

/// <summary>
/// Wraps a real <see cref="IImageCacheService"/> and counts how many decodes were asked for and how
/// many are still outstanding, so a benchmark can wait for the grid to actually go quiet.
/// </summary>
/// <param name="inner">The cache doing the work.</param>
public sealed class TrackingImageCache(IImageCacheService inner) : IImageCacheService
{
    private int _requests;
    private int _inFlight;

    /// <summary>Total decode requests seen.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Requests that have not answered yet.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <inheritdoc />
    public async Task<ImageSource?> GetImageAsync(string? source, int decodePixelWidth, CancellationToken ct = default)
    {
        _ = Interlocked.Increment(ref _requests);
        _ = Interlocked.Increment(ref _inFlight);

        try
        {
            return await inner.GetImageAsync(source, decodePixelWidth, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <inheritdoc />
    public bool TryGetCached(string? source, int decodePixelWidth, out ImageSource? image)
    {
        var hit = inner.TryGetCached(source, decodePixelWidth, out image);
        if (hit)
        {
            _ = Interlocked.Increment(ref _requests);
        }

        return hit;
    }

    /// <inheritdoc />
    public int Invalidate(IEnumerable<string?> sources) => inner.Invalidate(sources);

    /// <inheritdoc />
    public void ClearMemory() => inner.ClearMemory();

    /// <inheritdoc />
    public Task<long> ClearDiskAsync(CancellationToken ct = default) => inner.ClearDiskAsync(ct);
}
