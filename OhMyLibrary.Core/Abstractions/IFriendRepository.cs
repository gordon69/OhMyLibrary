using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Persistence for friends and the games they own.
/// </summary>
public interface IFriendRepository
{
    /// <summary>
    /// Returns every stored friend, ordered by persona name.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FriendSummary>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the friends recorded as owning an app.
    /// </summary>
    /// <param name="appId">Steam application id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FriendSummary>> GetOwnersOfAsync(int appId, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates friend rows. A <see langword="null"/> avatar, profile URL or sync
    /// timestamp on an incoming summary leaves the stored value in place, so a friend-list refresh
    /// does not erase what a summaries call previously filled in.
    /// </summary>
    /// <param name="friends">The friends to write.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertFriendsAsync(IReadOnlyList<FriendSummary> friends, CancellationToken ct = default);

    /// <summary>
    /// Replaces one friend's owned-games set and stamps their sync time. Call it only when the
    /// library was actually visible — a hidden library must not be recorded as an empty one.
    /// </summary>
    /// <param name="steamId64">The friend's 64-bit Steam id.</param>
    /// <param name="appIds">Every app id the friend owns.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReplaceFriendGamesAsync(ulong steamId64, IReadOnlyList<int> appIds, CancellationToken ct = default);
}
