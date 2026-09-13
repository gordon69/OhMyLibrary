using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Thin client over the public Steam Web API endpoints the launcher needs.
/// </summary>
/// <remarks>
/// No member throws for an expected failure. A missing API key, a private profile, a 401/403 or a
/// transport error all degrade to an empty result, and for owned games to
/// <see cref="OwnedGamesResult.Hidden"/>. Only <see cref="OperationCanceledException"/> escapes.
/// </remarks>
public interface ISteamWebApiClient
{
    /// <summary>
    /// Whether an API key is configured. When <see langword="false"/> every call short-circuits to
    /// an empty result without touching the network.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Fetches an account's owned games with app info and played free games included.
    /// </summary>
    /// <param name="steamId64">The account to query; the signed-in user or a friend.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A result whose <see cref="OwnedGamesResult.Visible"/> flag distinguishes a private library
    /// from an account that genuinely owns nothing. Steam answers a private profile with an empty
    /// <c>response</c> object rather than an error, so the flag is the only way to tell.
    /// </returns>
    Task<OwnedGamesResult> GetOwnedGamesAsync(ulong steamId64, CancellationToken ct = default);

    /// <summary>
    /// Fetches the friend list. The returned summaries carry ids and <c>friend_since</c> only —
    /// names, avatars and states come from <see cref="GetPlayerSummariesAsync"/>.
    /// </summary>
    /// <param name="steamId64">The account whose friends to list.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The friends, or an empty list when the profile is private or no key is configured.</returns>
    Task<IReadOnlyList<FriendSummary>> GetFriendListAsync(ulong steamId64, CancellationToken ct = default);

    /// <summary>
    /// Fetches player summaries, batching internally at Valve's limit of 100 ids per call.
    /// </summary>
    /// <param name="steamIds">The accounts to describe; duplicates are collapsed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// One summary per account the API returned. Accounts it omitted are simply absent, so the
    /// result can be shorter than the input.
    /// </returns>
    Task<IReadOnlyList<FriendSummary>> GetPlayerSummariesAsync(IEnumerable<ulong> steamIds, CancellationToken ct = default);

    /// <summary>
    /// Resolves a custom profile URL name to a SteamID64.
    /// </summary>
    /// <param name="vanityName">The vanity name, without the <c>/id/</c> prefix.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The id, or <see langword="null"/> when it does not resolve.</returns>
    Task<ulong?> ResolveVanityUrlAsync(string vanityName, CancellationToken ct = default);
}
