using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Microsoft.Extensions.Logging.Abstractions;

using OhMyLibrary.App.Services;
using OhMyLibrary.Tests.Fakes;
using OhMyLibrary.Tests.Infrastructure;

namespace OhMyLibrary.Tests.Services;

/// <summary>
/// <see cref="ImageCacheService"/> against real files on disk.
/// </summary>
/// <remarks>
/// The behaviour worth pinning here is the second break in the cover-art chain: the cache is keyed
/// by source, not by content, and Steam rewrites an app's art <i>at the same path</i> far more often
/// than it moves it. Everything about that is provable with two small PNGs and a file overwrite —
/// no dispatcher, because the service decodes off the UI thread and freezes the result.
/// </remarks>
public sealed class ImageCacheServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new("imagecache");
    private readonly ImageCacheService _images = new(new StubHttpClientFactory(), NullLogger<ImageCacheService>.Instance);

    /// <inheritdoc />
    public void Dispose()
    {
        _images.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task GetImage_DecodesALocalFileAtTheRequestedWidth()
    {
        var path = WritePng("capsule.png", 60, 90);

        var image = await _images.GetImageAsync(path, 30);

        Assert.NotNull(image);
        Assert.Equal(30, ((BitmapSource)image).PixelWidth);
        Assert.Equal(45, ((BitmapSource)image).PixelHeight);
        Assert.True(image.IsFrozen);
    }

    [Fact]
    public async Task GetImage_KeepsServingTheOldBitmapWhenTheFileIsRewrittenUnderneathIt()
    {
        // Not a bug to fix here, but the reason Invalidate has to exist: nothing about a path tells
        // the cache that the bytes behind it changed.
        var path = WritePng("capsule.png", 60, 90);
        var first = await _images.GetImageAsync(path, 30);

        WritePng("capsule.png", 60, 30);
        var second = await _images.GetImageAsync(path, 30);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task Invalidate_MakesTheNextRequestReadTheNewBytes()
    {
        // Break 2 of the cover-art chain. Steam rewrites library_capsule.jpg where it stands, so a
        // corrected path is not enough — the decoded bitmap keyed by that path has to go too.
        var path = WritePng("capsule.png", 60, 90);
        var before = await _images.GetImageAsync(path, 30);

        Assert.Equal(45, ((BitmapSource)before!).PixelHeight);

        // New art, same path — the case that used to survive every invalidation upstream of here.
        WritePng("capsule.png", 60, 30);

        Assert.Equal(1, _images.Invalidate([path]));

        var after = await _images.GetImageAsync(path, 30);

        Assert.NotNull(after);
        Assert.NotSame(before, after);
        Assert.Equal(15, ((BitmapSource)after).PixelHeight);
    }

    [Fact]
    public async Task Invalidate_DropsEveryDecodeWidthOfTheSameSource()
    {
        // The grid and a detail view ask for different widths of the same file, and both go stale
        // together.
        var path = WritePng("capsule.png", 60, 90);
        _ = await _images.GetImageAsync(path, 30);
        _ = await _images.GetImageAsync(path, 60);

        Assert.True(_images.TryGetCached(path, 30, out _));
        Assert.True(_images.TryGetCached(path, 60, out _));

        _images.Invalidate([path]);

        Assert.False(_images.TryGetCached(path, 30, out _));
        Assert.False(_images.TryGetCached(path, 60, out _));
    }

    [Fact]
    public async Task Invalidate_LeavesOtherSourcesAlone()
    {
        // Per app, not per event: a new capsule for one game must not cost a thousand re-decodes.
        var mine = WritePng("mine.png", 60, 90);
        var theirs = WritePng("theirs.png", 60, 90);

        _ = await _images.GetImageAsync(mine, 30);
        _ = await _images.GetImageAsync(theirs, 30);

        _images.Invalidate([mine]);

        Assert.False(_images.TryGetCached(mine, 30, out _));
        Assert.True(_images.TryGetCached(theirs, 30, out _));
    }

    [Fact]
    public void Invalidate_IgnoresNullAndUnusableSources()
    {
        // GameAssets paths are null whenever the client has not cached that asset, and the caller
        // hands the whole set over rather than filtering it first.
        Assert.Equal(0, _images.Invalidate([null, string.Empty, "   "]));
    }

    [Fact]
    public async Task GetImage_ReturnsNullForAMissingFileAndRetriesItLater()
    {
        // Steam writes library art lazily, so "not there yet" must not be remembered as "no art".
        var path = _temp.Combine("late.png");

        Assert.Null(await _images.GetImageAsync(path, 30));

        _ = WritePng("late.png", 60, 90);

        Assert.NotNull(await _images.GetImageAsync(path, 30));
    }

    /// <summary>Writes a solid PNG of the given size and returns its absolute path.</summary>
    private string WritePng(string name, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0x40);

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        var path = _temp.Combine(name);
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        return path;
    }

    /// <summary>
    /// An <see cref="IHttpClientFactory"/> for a service that never reaches the network in these
    /// tests: every source is a local path.
    /// </summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler());
    }
}
