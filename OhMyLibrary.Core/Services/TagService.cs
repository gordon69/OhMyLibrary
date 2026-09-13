using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Steam;

namespace OhMyLibrary.Core.Services;

/// <summary>
/// Resolves store tag and genre ids to display names for the configured language.
/// </summary>
/// <remarks>
/// Tag names come from the keyless <c>store.steampowered.com/tagdata/populartags</c> endpoint and are
/// cached in the database for <c>Sync:TagNamesMaxAgeHours</c>. Genre names come from
/// <see cref="SteamGenres"/> because the client ships no id-to-name file for them. A failed download
/// leaves the previous names in place, and an id nothing resolves still renders as <c>#&lt;id&gt;</c>
/// rather than being dropped.
/// </remarks>
public sealed class TagService : ITagService
{
    private readonly ITagDataClient _tagData;
    private readonly ITagRepository _tagRepository;
    private readonly ISyncMetaRepository _syncMeta;
    private readonly SteamOptions _steamOptions;
    private readonly SyncOptions _syncOptions;
    private readonly ILogger<TagService> _logger;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private volatile IReadOnlyList<TagRef>? _tagCache;
    private volatile IReadOnlyList<GenreRef>? _genreCache;

    /// <summary>Creates the service.</summary>
    /// <param name="tagData">Downloads the popular-tag table.</param>
    /// <param name="tagRepository">Stores tag and genre names per language.</param>
    /// <param name="syncMeta">Holds the last-run timestamp and the language it was fetched for.</param>
    /// <param name="steamOptions">Supplies <see cref="SteamOptions.Language"/>.</param>
    /// <param name="syncOptions">Supplies <see cref="SyncOptions.TagNamesMaxAgeHours"/>.</param>
    /// <param name="logger">Log sink; a failed download is logged, never thrown.</param>
    public TagService(
        ITagDataClient tagData,
        ITagRepository tagRepository,
        ISyncMetaRepository syncMeta,
        IOptions<SteamOptions> steamOptions,
        IOptions<SyncOptions> syncOptions,
        ILogger<TagService> logger)
    {
        _tagData = tagData;
        _tagRepository = tagRepository;
        _syncMeta = syncMeta;
        _steamOptions = steamOptions.Value;
        _syncOptions = syncOptions.Value;
        _logger = logger;
    }

    /// <summary>The language tag and genre names are resolved for; never empty.</summary>
    public string Language =>
        string.IsNullOrWhiteSpace(_steamOptions.Language) ? "english" : _steamOptions.Language.Trim();

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagRef>> GetAllTagsAsync(CancellationToken ct = default)
    {
        var cached = _tagCache;
        if (cached is not null)
        {
            return cached;
        }

        var stored = await ReadTagsAsync(ct).ConfigureAwait(false);
        if (stored.Count == 0)
        {
            // First run: nothing has been downloaded yet, so fetch once before giving up on names.
            await RefreshNamesAsync(force: false, ct).ConfigureAwait(false);
            stored = await ReadTagsAsync(ct).ConfigureAwait(false);
        }

        _tagCache = stored;
        return stored;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The built-in table is the floor: anything the database has for this language wins over it, so
    /// a localised name upserted later replaces the shipped English one without a code change.
    /// </remarks>
    public async Task<IReadOnlyList<GenreRef>> GetAllGenresAsync(CancellationToken ct = default)
    {
        var cached = _genreCache;
        if (cached is not null)
        {
            return cached;
        }

        var byId = SteamGenres.All.ToDictionary(static g => g.GenreId, static g => g);

        try
        {
            foreach (var genre in await _tagRepository.GetGenresAsync(Language, ct).ConfigureAwait(false))
            {
                byId[genre.GenreId] = genre;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read stored genre names; falling back to the built-in table.");
        }

        var result = byId.Values
            .OrderBy(static g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _genreCache = result;
        return result;
    }

    /// <inheritdoc />
    public async Task RefreshNamesAsync(bool force, CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var language = Language;

            if (!force && await IsFreshAsync(language, ct).ConfigureAwait(false))
            {
                return;
            }

            // Genres are local, so seed them regardless of whether the download succeeds.
            await SeedGenreNamesAsync(language, ct).ConfigureAwait(false);

            var tags = await _tagData.GetPopularTagsAsync(language, ct).ConfigureAwait(false);
            if (tags.Count == 0)
            {
                _logger.LogWarning(
                    "The popular-tag download for {Language} returned nothing; the stored names are kept.",
                    language);
                return;
            }

            await _tagRepository.UpsertTagNamesAsync(tags, language, ct).ConfigureAwait(false);
            await _syncMeta
                .SetLastRunAsync(SyncKeys.TagNames, DateTimeOffset.UtcNow, language, ct)
                .ConfigureAwait(false);

            _tagCache = null;
            _genreCache = null;

            _logger.LogInformation("Stored {TagCount} {Language} store tag names.", tags.Count, language);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The tag name refresh failed; the previous names are kept.");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<IReadOnlyList<TagRef>> ReadTagsAsync(CancellationToken ct)
    {
        try
        {
            return await _tagRepository.GetTagsAsync(Language, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read stored tag names for {Language}.", Language);
            return [];
        }
    }

    private async Task SeedGenreNamesAsync(string language, CancellationToken ct)
    {
        try
        {
            await _tagRepository.UpsertGenreNamesAsync(SteamGenres.All, language, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not seed the built-in genre names for {Language}.", language);
        }
    }

    /// <summary>
    /// Names are stale when they are older than the configured lifetime, and also when they were
    /// downloaded for a different language than the one now configured.
    /// </summary>
    private async Task<bool> IsFreshAsync(string language, CancellationToken ct)
    {
        if (_syncOptions.TagNamesMaxAgeHours <= 0)
        {
            return false;
        }

        try
        {
            var last = await _syncMeta.GetLastRunAsync(SyncKeys.TagNames, ct).ConfigureAwait(false);
            if (last is null || DateTimeOffset.UtcNow - last.Value >= TimeSpan.FromHours(_syncOptions.TagNamesMaxAgeHours))
            {
                return false;
            }

            var storedLanguage = await _syncMeta.GetPayloadAsync(SyncKeys.TagNames, ct).ConfigureAwait(false);
            return string.Equals(storedLanguage, language, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the tag name sync state; treating the names as stale.");
            return false;
        }
    }
}
