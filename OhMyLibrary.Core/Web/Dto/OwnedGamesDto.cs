using System.Text.Json.Serialization;

namespace OhMyLibrary.Core.Web.Dto;

/// <summary>Envelope of <c>IPlayerService/GetOwnedGames/v1/</c>.</summary>
internal sealed record OwnedGamesEnvelope
{
    /// <summary>
    /// The payload. A private library answers with an empty object, so both
    /// <see cref="OwnedGamesResponseDto.Games"/> and <see cref="OwnedGamesResponseDto.GameCount"/>
    /// being absent is the "hidden" signal.
    /// </summary>
    [JsonPropertyName("response")]
    public OwnedGamesResponseDto? Response { get; init; }
}

/// <summary>Body of a <c>GetOwnedGames</c> answer.</summary>
internal sealed record OwnedGamesResponseDto
{
    [JsonPropertyName("game_count")]
    public int? GameCount { get; init; }

    [JsonPropertyName("games")]
    public IReadOnlyList<OwnedGameDto>? Games { get; init; }
}

/// <summary>One entry of the <c>games</c> array.</summary>
internal sealed record OwnedGameDto
{
    [JsonPropertyName("appid")]
    public int AppId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("playtime_forever")]
    public int PlaytimeForever { get; init; }

    [JsonPropertyName("playtime_2weeks")]
    public int Playtime2Weeks { get; init; }

    [JsonPropertyName("img_icon_url")]
    public string? ImgIconUrl { get; init; }

    /// <summary>Unix seconds of the last session; <c>0</c> means never played.</summary>
    [JsonPropertyName("rtime_last_played")]
    public long RtimeLastPlayed { get; init; }
}
