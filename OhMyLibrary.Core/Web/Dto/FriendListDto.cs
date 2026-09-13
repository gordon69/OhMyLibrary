using System.Text.Json.Serialization;

namespace OhMyLibrary.Core.Web.Dto;

/// <summary>Envelope of <c>ISteamUser/GetFriendList/v1/</c>. A private profile answers 401/403 instead.</summary>
internal sealed record FriendListEnvelope
{
    [JsonPropertyName("friendslist")]
    public FriendListDto? FriendsList { get; init; }
}

/// <summary>Body of a <c>GetFriendList</c> answer.</summary>
internal sealed record FriendListDto
{
    [JsonPropertyName("friends")]
    public IReadOnlyList<FriendDto>? Friends { get; init; }
}

/// <summary>One entry of the <c>friends</c> array; ids and dates only, no names.</summary>
internal sealed record FriendDto
{
    /// <summary>SteamID64 as a decimal string — it does not fit a signed 64-bit field.</summary>
    [JsonPropertyName("steamid")]
    public string? SteamId { get; init; }

    [JsonPropertyName("relationship")]
    public string? Relationship { get; init; }

    /// <summary>Unix seconds; <c>0</c> when Valve does not report it.</summary>
    [JsonPropertyName("friend_since")]
    public long FriendSince { get; init; }
}
