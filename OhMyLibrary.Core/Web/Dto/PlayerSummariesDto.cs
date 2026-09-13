using System.Text.Json.Serialization;

namespace OhMyLibrary.Core.Web.Dto;

/// <summary>Envelope of <c>ISteamUser/GetPlayerSummaries/v2/</c>.</summary>
internal sealed record PlayerSummariesEnvelope
{
    [JsonPropertyName("response")]
    public PlayerSummariesResponseDto? Response { get; init; }
}

/// <summary>Body of a <c>GetPlayerSummaries</c> answer. v2 returns a flat array, unlike v1.</summary>
internal sealed record PlayerSummariesResponseDto
{
    [JsonPropertyName("players")]
    public IReadOnlyList<PlayerSummaryDto>? Players { get; init; }
}

/// <summary>One entry of the <c>players</c> array.</summary>
internal sealed record PlayerSummaryDto
{
    /// <summary>SteamID64 as a decimal string.</summary>
    [JsonPropertyName("steamid")]
    public string? SteamId { get; init; }

    [JsonPropertyName("personaname")]
    public string? PersonaName { get; init; }

    [JsonPropertyName("profileurl")]
    public string? ProfileUrl { get; init; }

    [JsonPropertyName("avatar")]
    public string? Avatar { get; init; }

    [JsonPropertyName("avatarmedium")]
    public string? AvatarMedium { get; init; }

    [JsonPropertyName("avatarfull")]
    public string? AvatarFull { get; init; }

    /// <summary>Valve's <c>personastate</c>; absent for accounts that hide their profile.</summary>
    [JsonPropertyName("personastate")]
    public int PersonaState { get; init; }

    /// <summary><c>3</c> is public, anything else is friends-only or private.</summary>
    [JsonPropertyName("communityvisibilitystate")]
    public int? CommunityVisibilityState { get; init; }
}
