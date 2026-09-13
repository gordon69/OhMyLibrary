using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Services;

/// <summary>
/// The friends list and which of them own a given game.
/// </summary>
/// <remarks>
/// Friend fan-out is the most rate-limit-sensitive thing the app does — one owned-games call per
/// friend — so it is throttled, cached for hours and never triggered by window focus. A friend whose
/// library is private is stored with <see cref="FriendSummary.GameListVisible"/> set to
/// <see langword="false"/> and must be shown as hidden, not as owning nothing.
/// </remarks>
public interface IFriendsService
{
    /// <summary>
    /// Returns the cached friends list. Empty when no API key is configured or the profile is private.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FriendSummary>> GetFriendsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the friends known to own an app. Only friends with a visible library can appear.
    /// </summary>
    /// <param name="appId">Steam application id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FriendSummary>> GetOwnersOfAsync(int appId, CancellationToken ct = default);

    /// <summary>
    /// Refreshes the friends list and each friend's library, reporting progress because the fan-out
    /// is slow.
    /// </summary>
    /// <param name="force">Ignore the cache lifetime and fetch anyway.</param>
    /// <param name="progress">Optional status sink, for example "12 of 180 friends".</param>
    /// <param name="ct">Cancellation token.</param>
    Task RefreshAsync(bool force, IProgress<string>? progress = null, CancellationToken ct = default);
}
