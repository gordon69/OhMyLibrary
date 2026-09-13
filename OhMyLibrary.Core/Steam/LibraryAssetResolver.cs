using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Services;

namespace OhMyLibrary.Core.Steam;

/// <summary>
/// Finds cached library art under <c>appcache/librarycache/&lt;appid&gt;/</c> in the client's current
/// hashed layout.
/// </summary>
/// <remarks>
/// <para>
/// The modern client stores each asset inside an opaque hash subfolder
/// (<c>librarycache/570/6843027380.../library_capsule.jpg</c>), but the legacy flat layout has not
/// disappeared: on the reference install the loose <c>library_600x900.jpg</c> and <c>header.jpg</c>
/// were <i>more</i> common than their hashed <c>library_capsule.jpg</c> / <c>library_header.jpg</c>
/// counterparts, and <c>logo.png</c> plus a <c>&lt;sha1&gt;.jpg</c> 32x32 icon sit loose as well.
/// Both levels are therefore enumerated, both names are accepted for the cover and the header, and
/// the hash is never predicted. Localised variants (<c>*_russian.jpg</c>) and
/// <c>library_hero_blur.jpg</c> are ignored, because matching is on the exact file name.
/// </para>
/// <para>
/// Results are cached per app because the folder scan is the expensive part of drawing a grid of a
/// few thousand cards. Steam rewrites these files when art changes, so the resolver subscribes to
/// <see cref="ISteamWatcherService.Changed"/> and drops its own entry for every app named by a
/// <see cref="SteamFileChangeKind.LibraryCache"/> event — the cache root is watched recursively, so
/// the app id is the first path segment below it. A burst that overflowed the watcher's buffer
/// arrives with no ids at all and drops everything, because at that point no entry can be trusted.
/// The <c>appinfo.vdf</c> signal is deliberately <i>not</i> used for this: it fires whenever the
/// client refreshes metadata for any of the ~2900 apps it knows, names none of them, and would
/// therefore throw the whole art cache away many times an hour for changes that are usually not art.
/// </para>
/// </remarks>
public sealed class LibraryAssetResolver : ILibraryAssetResolver, IDisposable
{
    private const string CapsuleFileName = "library_capsule.jpg";
    private const string LegacyCapsuleFileName = "library_600x900.jpg";
    private const string HeaderFileName = "library_header.jpg";
    private const string LegacyHeaderFileName = "header.jpg";
    private const string HeroFileName = "library_hero.jpg";
    private const string LogoFileName = "logo.png";
    private const string CdnRoot = "https://cdn.cloudflare.steamstatic.com/steam/apps";
    private const int IconNameLength = 40;

    private readonly ISteamPathResolver _pathResolver;
    private readonly ISteamWatcherService _watcher;
    private readonly ILogger<LibraryAssetResolver> _logger;
    private readonly ConcurrentDictionary<int, GameAssets> _cache = new();

    private long _revision;
    private bool _disposed;

    /// <summary>Creates a resolver and subscribes it to the Steam file watcher.</summary>
    /// <param name="pathResolver">Supplies the Steam install root that holds <c>appcache</c>.</param>
    /// <param name="watcher">
    /// Tells the resolver when Steam rewrote cached art. Subscribing here rather than expecting some
    /// other component to remember is what keeps the cache from serving a stale path for the
    /// lifetime of the process; the watcher raising nothing (it was never started, or Steam is not
    /// installed) simply leaves the cache as long-lived as it was before.
    /// </param>
    /// <param name="logger">Logger.</param>
    public LibraryAssetResolver(
        ISteamPathResolver pathResolver,
        ISteamWatcherService watcher,
        ILogger<LibraryAssetResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(watcher);

        _pathResolver = pathResolver;
        _watcher = watcher;
        _logger = logger;

        _watcher.Changed += OnSteamFilesChanged;
    }

    /// <inheritdoc />
    public GameAssets Resolve(int appId)
    {
        if (appId <= 0)
        {
            return GameAssets.None(appId);
        }

        if (_cache.TryGetValue(appId, out GameAssets? cached))
        {
            return cached;
        }

        string? steamPath = _pathResolver.FindSteamPath();
        if (steamPath is null)
        {
            // Not cached: Steam may appear later, and then the art appears with it.
            return GameAssets.None(appId);
        }

        // Stamped with the invalidation count this scan ran under, so a caller can tell a rescan
        // that found the same paths from no rescan at all — which is exactly the case Steam produces
        // when it overwrites an app's art in place.
        GameAssets assets = Scan(steamPath, appId) with { Revision = Interlocked.Read(ref _revision) };
        _cache[appId] = assets;
        return assets;
    }

    /// <inheritdoc />
    public string? GetCoverUrlFallback(int appId) =>
        appId <= 0 ? null : $"{CdnRoot}/{appId.ToString(CultureInfo.InvariantCulture)}/library_600x900.jpg";

    /// <inheritdoc />
    public void Invalidate(int appId)
    {
        _ = Interlocked.Increment(ref _revision);
        _ = _cache.TryRemove(appId, out _);
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        _ = Interlocked.Increment(ref _revision);
        _cache.Clear();
    }

    /// <summary>Unsubscribes from the watcher. The container owns the lifetime of both singletons.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.Changed -= OnSteamFilesChanged;
    }

    /// <summary>
    /// Drops the art of the apps a <see cref="SteamFileChangeKind.LibraryCache"/> change names, or
    /// all of it when the change could not be narrowed to app ids.
    /// </summary>
    /// <remarks>Runs on the watcher's debounce thread; the cache is concurrent, so no lock is taken.</remarks>
    private void OnSteamFilesChanged(object? sender, SteamFilesChangedEventArgs e)
    {
        if (e.Kind != SteamFileChangeKind.LibraryCache)
        {
            return;
        }

        if (e.AppIds.Count == 0)
        {
            _logger.LogDebug("Steam rewrote library art for apps we could not identify; dropping the whole art cache.");
            InvalidateAll();
            return;
        }

        foreach (var appId in e.AppIds)
        {
            Invalidate(appId);
        }

        _logger.LogDebug("Dropped cached library art for {AppCount} apps Steam rewrote.", e.AppIds.Count);
    }

    private GameAssets Scan(string steamPath, int appId)
    {
        string appDirectory = Path.Combine(
            steamPath,
            "appcache",
            "librarycache",
            appId.ToString(CultureInfo.InvariantCulture));

        try
        {
            if (!Directory.Exists(appDirectory))
            {
                return GameAssets.None(appId);
            }

            Candidate capsule = default;
            Candidate header = default;
            Candidate hero = default;
            Candidate logo = default;
            Candidate icon = default;

            Classify(Directory.EnumerateFiles(appDirectory), isAppRoot: true);
            foreach (string hashDirectory in Directory.EnumerateDirectories(appDirectory))
            {
                Classify(Directory.EnumerateFiles(hashDirectory), isAppRoot: false);
            }

            return new GameAssets(appId, capsule.Path, header.Path, hero.Path, logo.Path, icon.Path);

            void Classify(IEnumerable<string> files, bool isAppRoot)
            {
                foreach (string file in files)
                {
                    string name = Path.GetFileName(file);
                    if (Matches(name, CapsuleFileName))
                    {
                        capsule.Offer(file, rank: 0);
                    }
                    else if (Matches(name, LegacyCapsuleFileName))
                    {
                        capsule.Offer(file, rank: 1);
                    }
                    else if (Matches(name, HeaderFileName))
                    {
                        header.Offer(file, rank: 0);
                    }
                    else if (Matches(name, LegacyHeaderFileName))
                    {
                        header.Offer(file, rank: 1);
                    }
                    else if (Matches(name, HeroFileName))
                    {
                        hero.Offer(file, rank: 0);
                    }
                    else if (Matches(name, LogoFileName))
                    {
                        logo.Offer(file, rank: 0);
                    }
                    else if (isAppRoot && IsIconFileName(name))
                    {
                        icon.Offer(file, rank: 0);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Scanning library art for app {AppId} under {Directory} failed.", appId, appDirectory);
            return GameAssets.None(appId);
        }
    }

    private static bool Matches(string fileName, string wellKnownName) =>
        string.Equals(fileName, wellKnownName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A loose <c>&lt;sha1&gt;.jpg</c> directly under the app folder is the 32x32 icon; the
    /// hash-named blur and marker assets live in subfolders and are never matched here.
    /// </summary>
    private static bool IsIconFileName(string fileName)
    {
        if (!fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> stem = Path.GetFileNameWithoutExtension(fileName.AsSpan());
        if (stem.Length != IconNameLength)
        {
            return false;
        }

        foreach (char c in stem)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The best file found so far for one asset role. A lower rank wins, so the current file name
    /// beats the legacy one no matter which directory level it turned up in.
    /// </summary>
    private struct Candidate
    {
        public string? Path { get; private set; }

        private int _rank;

        public void Offer(string path, int rank)
        {
            if (Path is null || rank < _rank)
            {
                Path = path;
                _rank = rank;
            }
        }
    }
}
