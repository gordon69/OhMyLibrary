using System.Text.Json.Serialization;

namespace OhMyLibrary.Core.Web.Dto;

/// <summary>Envelope of <c>ISteamUser/ResolveVanityURL/v1/</c>.</summary>
internal sealed record ResolveVanityUrlEnvelope
{
    [JsonPropertyName("response")]
    public ResolveVanityUrlDto? Response { get; init; }
}

/// <summary>Body of a <c>ResolveVanityURL</c> answer.</summary>
internal sealed record ResolveVanityUrlDto
{
    /// <summary><c>1</c> means resolved; <c>42</c> means no match.</summary>
    [JsonPropertyName("success")]
    public int Success { get; init; }

    /// <summary>SteamID64 as a decimal string; absent unless <see cref="Success"/> is <c>1</c>.</summary>
    [JsonPropertyName("steamid")]
    public string? SteamId { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
