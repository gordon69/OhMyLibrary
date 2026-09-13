using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyLibrary.Core.Web.Dto;

/// <summary>
/// Source-generated metadata for every Steam JSON payload the clients read. Using it keeps the
/// deserialiser off reflection, so the clients stay trim- and AOT-friendly.
/// </summary>
/// <remarks>
/// <see cref="JsonNumberHandling.AllowReadingFromString"/> is deliberate: Valve is inconsistent
/// about quoting numeric fields between endpoints and client builds.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(OwnedGamesEnvelope))]
[JsonSerializable(typeof(FriendListEnvelope))]
[JsonSerializable(typeof(PlayerSummariesEnvelope))]
[JsonSerializable(typeof(ResolveVanityUrlEnvelope))]
[JsonSerializable(typeof(List<StoreTagDto>))]
internal sealed partial class SteamJsonContext : JsonSerializerContext
{
}
