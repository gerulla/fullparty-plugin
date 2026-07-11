using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace FullParty.Auth;

public sealed class FullPartyUser
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("primary_character")]
    public FullPartyCharacter? PrimaryCharacter { get; set; }

    [JsonPropertyName("characters")]
    public List<FullPartyCharacter> Characters { get; set; } = [];
}

public sealed class FullPartyCharacter
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("world")]
    public string World { get; set; } = string.Empty;

    [JsonPropertyName("datacenter")]
    public string Datacenter { get; set; } = string.Empty;

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("lodestone_id")]
    public string? LodestoneId { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("is_primary")]
    public bool? IsPrimary { get; set; }

    [JsonPropertyName("is_verified")]
    public bool? IsVerified { get; set; }
}
