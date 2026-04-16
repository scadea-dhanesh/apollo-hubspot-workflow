using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApolloHubSpot.Api.Contracts;

public sealed class PromptSyncResponse
{
    [JsonPropertyName("max_pages")]
    public int MaxPages { get; init; }

    [JsonPropertyName("apollo_filters")]
    public JsonElement ApolloFilters { get; init; }

    [JsonPropertyName("sync")]
    public SyncResponse Sync { get; init; } = null!;

    /// <summary>Trimmed model output for debugging (optional).</summary>
    [JsonPropertyName("assistant_raw")]
    public string? AssistantRaw { get; init; }
}
