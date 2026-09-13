using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IGameRepository"/> that records what each upsert was asked to write.
/// </summary>
/// <remarks>
/// It deliberately does <b>not</b> reimplement the SQL merge semantics — those are covered against a
/// real SQLite database — so the service tests can assert on the calls a service made rather than on
/// a second, parallel implementation of the same rules.
/// </remarks>
public sealed class FakeGameRepository : IGameRepository
{
    /// <summary>Rows returned by <see cref="GetAllAsync"/> and <see cref="GetAsync"/>.</summary>
    public List<GameEntry> Rows { get; } = [];

    /// <summary>Every batch handed to <see cref="UpsertInstalledAsync"/>.</summary>
    public List<IReadOnlyList<InstalledApp>> InstalledUpserts { get; } = [];

    /// <summary>Every batch handed to <see cref="UpsertOwnedAsync"/>.</summary>
    public List<IReadOnlyList<OwnedGame>> OwnedUpserts { get; } = [];

    /// <summary>Every batch handed to <see cref="UpsertMetadataAsync"/>.</summary>
    public List<IReadOnlyList<AppInfoEntry>> MetadataUpserts { get; } = [];

    /// <summary>Every id set handed to <see cref="MarkNotInstalledExceptAsync"/>.</summary>
    public List<IReadOnlySet<int>> NotInstalledExcept { get; } = [];

    /// <summary>Exception to throw from every member, to exercise the degradation paths.</summary>
    public Exception? Failure { get; set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<GameEntry>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Throw();
        return Task.FromResult<IReadOnlyList<GameEntry>>([.. Rows]);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<int>> GetAllAppIdsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Throw();
        return Task.FromResult<IReadOnlyList<int>>([.. Rows.Select(row => row.AppId)]);
    }

    /// <inheritdoc />
    public Task<GameEntry?> GetAsync(int appId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Throw();
        return Task.FromResult(Rows.Find(row => row.AppId == appId));
    }

    /// <inheritdoc />
    public Task UpsertInstalledAsync(IReadOnlyList<InstalledApp> apps, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Throw();
        InstalledUpserts.Add(apps);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpsertOwnedAsync(IReadOnlyList<OwnedGame> games, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Throw();
        OwnedUpserts.Add(games);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpsertMetadataAsync(IReadOnlyList<AppInfoEntry> entries, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Throw();
        MetadataUpserts.Add(entries);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The returned delta is the one piece of behaviour the fake has to reproduce rather than just
    /// record: the caller derives the "these cards changed" event from it, so a fake that always
    /// answered empty would let the very regression this exists to catch through. It is computed
    /// from <see cref="Rows"/> — the rows that were installed and are not in the scan.
    /// </remarks>
    public Task<IReadOnlyList<int>> MarkNotInstalledExceptAsync(IReadOnlySet<int> installedAppIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Throw();
        NotInstalledExcept.Add(installedAppIds);

        IReadOnlyList<int> cleared =
        [
            .. Rows.Where(row => row.IsInstalled && !installedAppIds.Contains(row.AppId)).Select(row => row.AppId),
        ];

        return Task.FromResult(cleared);
    }

    private void Throw()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

/// <summary>An in-memory <see cref="ISyncMetaRepository"/>.</summary>
public sealed class FakeSyncMetaRepository : ISyncMetaRepository
{
    private readonly Dictionary<string, (DateTimeOffset When, string? Payload)> _entries = [];

    /// <summary>Keys that have been stamped, in call order.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>Pre-seeds a last-run timestamp, as an earlier session would have left it.</summary>
    /// <param name="key">One of the <see cref="SyncKeys"/> constants.</param>
    /// <param name="when">The timestamp to store.</param>
    /// <param name="payload">Optional payload.</param>
    public void Seed(string key, DateTimeOffset when, string? payload = null) => _entries[key] = (when, payload);

    /// <inheritdoc />
    public Task<DateTimeOffset?> GetLastRunAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_entries.TryGetValue(key, out var entry) ? entry.When : (DateTimeOffset?)null);
    }

    /// <inheritdoc />
    public Task SetLastRunAsync(string key, DateTimeOffset whenUtc, string? payload, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Writes.Add(key);
        var existing = _entries.TryGetValue(key, out var entry) ? entry.Payload : null;
        _entries[key] = (whenUtc, payload ?? existing);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetPayloadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_entries.TryGetValue(key, out var entry) ? entry.Payload : null);
    }
}

/// <summary>An in-memory <see cref="IFriendRepository"/> that keeps the last write per friend.</summary>
public sealed class FakeFriendRepository : IFriendRepository
{
    /// <summary>The friend rows, keyed by SteamID64, as the last upsert left them.</summary>
    public Dictionary<ulong, FriendSummary> Friends { get; } = [];

    /// <summary>Owned-games sets written by the fan-out, keyed by SteamID64.</summary>
    public Dictionary<ulong, IReadOnlyList<int>> Games { get; } = [];

    /// <summary>Every <see cref="UpsertFriendsAsync"/> batch, in call order.</summary>
    public List<IReadOnlyList<FriendSummary>> Upserts { get; } = [];

    /// <summary>SteamID64s <see cref="ReplaceFriendGamesAsync"/> was called for, in call order.</summary>
    public List<ulong> GameReplacements { get; } = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<FriendSummary>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<FriendSummary>>([.. Friends.Values]);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FriendSummary>> GetOwnersOfAsync(int appId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var owners = Games
            .Where(entry => entry.Value.Contains(appId))
            .Select(entry => Friends.TryGetValue(entry.Key, out var friend) ? friend : null)
            .Where(friend => friend is not null)
            .Select(friend => friend!)
            .ToList();

        return Task.FromResult<IReadOnlyList<FriendSummary>>(owners);
    }

    /// <inheritdoc />
    public Task UpsertFriendsAsync(IReadOnlyList<FriendSummary> friends, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Upserts.Add(friends);

        foreach (var friend in friends)
        {
            Friends[friend.SteamId64] = friend;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReplaceFriendGamesAsync(ulong steamId64, IReadOnlyList<int> appIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        GameReplacements.Add(steamId64);
        Games[steamId64] = appIds;
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory <see cref="ITagRepository"/>.</summary>
public sealed class FakeTagRepository : ITagRepository
{
    /// <summary>Stored tag names, keyed by language.</summary>
    public Dictionary<string, List<TagRef>> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stored genre names, keyed by language.</summary>
    public Dictionary<string, List<GenreRef>> Genres { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<IReadOnlyList<TagRef>> GetTagsAsync(string lang, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<TagRef>>(Tags.TryGetValue(lang, out var tags) ? [.. tags] : []);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GenreRef>> GetGenresAsync(string lang, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<GenreRef>>(Genres.TryGetValue(lang, out var genres) ? [.. genres] : []);
    }

    /// <inheritdoc />
    public Task UpsertTagNamesAsync(IReadOnlyList<TagRef> tags, string lang, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Merge(Tags, lang, tags, tag => tag.TagId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpsertGenreNamesAsync(IReadOnlyList<GenreRef> genres, string lang, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Merge(Genres, lang, genres, genre => genre.GenreId);
        return Task.CompletedTask;
    }

    private static void Merge<T>(Dictionary<string, List<T>> store, string lang, IReadOnlyList<T> values, Func<T, int> idOf)
    {
        if (!store.TryGetValue(lang, out var existing))
        {
            existing = [];
            store[lang] = existing;
        }

        foreach (var value in values)
        {
            existing.RemoveAll(stored => idOf(stored) == idOf(value));
            existing.Add(value);
        }
    }
}

/// <summary>An in-memory <see cref="ICollectionRepository"/>.</summary>
public sealed class FakeCollectionRepository : ICollectionRepository
{
    private readonly List<GameCollection> _collections = [];
    private long _nextId = 1;

    /// <inheritdoc />
    public Task<IReadOnlyList<GameCollection>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<GameCollection>>(
            [.. _collections.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <inheritdoc />
    public Task<GameCollection?> GetAsync(long collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_collections.Find(c => c.CollectionId == collectionId));
    }

    /// <inheritdoc />
    public Task<GameCollection> CreateAsync(string name, DateTimeOffset createdUtc, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var created = new GameCollection(_nextId++, name, _collections.Count, createdUtc, []);
        _collections.Add(created);
        return Task.FromResult(created);
    }

    /// <inheritdoc />
    public Task RenameAsync(long collectionId, string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Replace(collectionId, existing => existing with { Name = name });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(long collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _collections.RemoveAll(c => c.CollectionId == collectionId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        for (var i = 0; i < orderedIds.Count; i++)
        {
            var order = i;
            Replace(orderedIds[i], existing => existing with { SortOrder = order });
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddGameAsync(long collectionId, int appId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Replace(collectionId, existing => existing.AppIds.Contains(appId)
            ? existing
            : existing with { AppIds = [.. existing.AppIds, appId] });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveGameAsync(long collectionId, int appId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Replace(collectionId, existing => existing with { AppIds = [.. existing.AppIds.Where(id => id != appId)] });
        return Task.CompletedTask;
    }

    private void Replace(long collectionId, Func<GameCollection, GameCollection> update)
    {
        var index = _collections.FindIndex(c => c.CollectionId == collectionId);
        if (index >= 0)
        {
            _collections[index] = update(_collections[index]);
        }
    }
}
