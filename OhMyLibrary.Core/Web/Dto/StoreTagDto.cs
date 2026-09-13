using System.Text.Json.Serialization;

namespace OhMyLibrary.Core.Web.Dto;

/// <summary>
/// One entry of <c>store.steampowered.com/tagdata/populartags/&lt;language&gt;</c>, which is a bare
/// JSON array rather than an enveloped response.
/// </summary>
internal sealed record StoreTagDto
{
    [JsonPropertyName("tagid")]
    public int TagId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
