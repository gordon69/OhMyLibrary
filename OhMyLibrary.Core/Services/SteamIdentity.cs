using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;

namespace OhMyLibrary.Core.Services;

/// <summary>
/// Works out which account the library belongs to: the configured id if there is one, otherwise the
/// account the Steam client most recently signed in as.
/// </summary>
internal static class SteamIdentity
{
    /// <summary>
    /// Resolves the SteamID64 to query the Web API for.
    /// </summary>
    /// <param name="options">Steam options; <see cref="SteamOptions.SteamId64"/> wins when set.</param>
    /// <param name="localUsers">Accounts from <c>config/loginusers.vdf</c>, newest sign-in first.</param>
    /// <returns>The id, or <see langword="null"/> when neither source produced one.</returns>
    public static ulong? Resolve(SteamOptions options, IReadOnlyList<SteamUser> localUsers)
    {
        var configured = options.SteamId64.AsSpan().Trim();
        if (!configured.IsEmpty && ulong.TryParse(configured, out var fromOptions) && fromOptions != 0)
        {
            return fromOptions;
        }

        SteamUser? fallback = null;
        foreach (var user in localUsers)
        {
            if (user.SteamId64 == 0)
            {
                continue;
            }

            if (user.MostRecent)
            {
                return user.SteamId64;
            }

            fallback ??= user;
        }

        return fallback?.SteamId64;
    }
}
