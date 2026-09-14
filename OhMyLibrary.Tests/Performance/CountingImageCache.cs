using System.Windows.Media;

using OhMyLibrary.App.Services;

namespace OhMyLibrary.Tests.Performance;

/// <summary>An image cache that answers nothing and counts how often it was asked.</summary>
public sealed class CountingImageCache : IImageCacheService
{
    private readonly Lock _sync = new();
    private readonly List<string?> _sources = [];
    private readonly List<int> _widths = [];

    /// <summary>How many decode requests reached the cache.</summary>
    public int Requests
    {
        get
        {
            lock (_sync)
            {
                return _sources.Count;
            }
        }
    }

    /// <summary>Every source asked for, in order.</summary>
    public IReadOnlyList<string?> Sources
    {
        get
        {
            lock (_sync)
            {
                return [.. _sources];
            }
        }
    }

    /// <summary>Every decode width asked for, in order, aligned with <see cref="Sources"/>.</summary>
    public IReadOnlyList<int> Widths
    {
        get
        {
            lock (_sync)
            {
                return [.. _widths];
            }
        }
    }

    /// <inheritdoc />
    public Task<ImageSource?> GetImageAsync(string? source, int decodePixelWidth, CancellationToken ct = default)
    {
        lock (_sync)
        {
            _sources.Add(source);
            _widths.Add(decodePixelWidth);
        }

        return Task.FromResult<ImageSource?>(null);
    }

    /// <inheritdoc />
    /// <remarks>Never a hit, so every request goes through <see cref="GetImageAsync"/> and is counted.</remarks>
    public bool TryGetCached(string? source, int decodePixelWidth, out ImageSource? image)
    {
        image = null;
        return false;
    }

    /// <inheritdoc />
    public int Invalidate(IEnumerable<string?> sources) => 0;

    /// <inheritdoc />
    public void ClearMemory()
    {
    }

    /// <inheritdoc />
    public Task<long> ClearDiskAsync(CancellationToken ct = default) => Task.FromResult(0L);
}
