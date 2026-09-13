namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// The <c>SyncMeta</c> key-value table: when each source was last refreshed, plus an optional
/// payload for whatever else that source needs to remember.
/// </summary>
public interface ISyncMetaRepository
{
    /// <summary>
    /// Returns when a sync last completed, or <see langword="null"/> when it never has.
    /// </summary>
    /// <param name="key">One of the <see cref="SyncKeys"/> constants.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DateTimeOffset?> GetLastRunAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Records a completed sync. Call it only on success, so a failure leaves the previous
    /// timestamp and the data stays due for a retry.
    /// </summary>
    /// <param name="key">One of the <see cref="SyncKeys"/> constants.</param>
    /// <param name="whenUtc">Completion time, in UTC.</param>
    /// <param name="payload">Optional opaque state, such as the language the names were fetched for.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetLastRunAsync(string key, DateTimeOffset whenUtc, string? payload, CancellationToken ct = default);

    /// <summary>
    /// Returns the payload stored alongside a key, or <see langword="null"/> when there is none.
    /// </summary>
    /// <param name="key">One of the <see cref="SyncKeys"/> constants.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> GetPayloadAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// The keys used in <c>SyncMeta</c>. Each corresponds to one cache lifetime in
/// <c>OhMyLibrary.Core.Options.SyncOptions</c>.
/// </summary>
public static class SyncKeys
{
    /// <summary>The signed-in account's owned-games list.</summary>
    public const string OwnedGames = "owned_games";

    /// <summary>The friends list and the per-friend library fan-out.</summary>
    public const string Friends = "friends";

    /// <summary>The parse of <c>appcache/appinfo.vdf</c>.</summary>
    public const string AppInfo = "appinfo";

    /// <summary>The downloaded store tag names; the payload carries the language.</summary>
    public const string TagNames = "tag_names";

    /// <summary>The local <c>.acf</c> manifest scan.</summary>
    public const string LocalScan = "local_scan";
}
