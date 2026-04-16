using System.Text.Json.Serialization;

namespace ApolloHubSpot.Api.Contracts;

public sealed class PromptSyncRequest
{
    /// <summary>Natural-language ICP / search criteria; converted to <c>apollo_filters</c> via Groq.</summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    /// <summary>Optional cap on pages; if omitted, the model may suggest <c>max_pages</c> in JSON (capped in generator).</summary>
    [JsonPropertyName("max_pages")]
    public int? MaxPages { get; set; }

    /// <summary>When true, include the model’s raw text in the response for debugging.</summary>
    [JsonPropertyName("include_assistant_raw")]
    public bool IncludeAssistantRaw { get; set; }

    /// <inheritdoc cref="SyncRequest.RevealPersonalEmail" />
    [JsonPropertyName("reveal_personal_email")]
    public bool RevealPersonalEmail { get; set; }

    /// <inheritdoc cref="SyncRequest.RevealPhoneNumber" />
    [JsonPropertyName("reveal_phone_number")]
    public bool RevealPhoneNumber { get; set; }
}
