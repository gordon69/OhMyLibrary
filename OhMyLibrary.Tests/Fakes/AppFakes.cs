using System.Windows.Media;

using OhMyLibrary.App.Services;
using OhMyLibrary.Tests.Infrastructure;

namespace OhMyLibrary.Tests.Fakes;

/// <summary>
/// An <see cref="IImageCacheService"/> that records what it was asked to drop instead of decoding
/// anything.
/// </summary>
/// <remarks>
/// The coordinator tests are about <i>which</i> sources are invalidated and in what order relative
/// to the other two caches, never about bitmaps — and decoding a real one would need a dispatcher.
/// <see cref="ImageCacheServiceTests"/> covers the real implementation's behaviour.
/// </remarks>
public sealed class FakeImageCacheService : IImageCacheService
{
    /// <summary>Journal label recorded when <see cref="Invalidate"/> runs.</summary>
    public const string InvalidateCall = "images.invalidate";

    /// <summary>Journal label recorded when <see cref="ClearMemory"/> runs.</summary>
    public const string ClearMemoryCall = "images.clear";

    private readonly Lock _sync = new();
    private readonly List<string?> _invalidated = [];

    /// <summary>Shared ordering journal, when the test is asserting on call order.</summary>
    public CallLog? Journal { get; init; }

    /// <summary>How many times <see cref="ClearMemory"/> was called.</summary>
    public int ClearMemoryCalls { get; private set; }

    /// <summary>Every source passed to <see cref="Invalidate"/>, in order, nulls included.</summary>
    public IReadOnlyList<string?> Invalidated
    {
        get
        {
            lock (_sync)
            {
                return [.. _invalidated];
            }
        }
    }

    /// <inheritdoc />
    public Task<ImageSource?> GetImageAsync(string? source, int decodePixelWidth, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<ImageSource?>(null);
    }

    /// <inheritdoc />
    public bool TryGetCached(string? source, int decodePixelWidth, out ImageSource? image)
    {
        image = null;
        return false;
    }

    /// <inheritdoc />
    public int Invalidate(IEnumerable<string?> sources)
    {
        int dropped;

        lock (_sync)
        {
            var before = _invalidated.Count;
            _invalidated.AddRange(sources);
            dropped = _invalidated.Count - before;
        }

        Journal?.Record(InvalidateCall);
        return dropped;
    }

    /// <inheritdoc />
    public void ClearMemory()
    {
        ClearMemoryCalls++;
        Journal?.Record(ClearMemoryCall);
    }

    /// <inheritdoc />
    public Task<long> ClearDiskAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(0L);
    }
}
