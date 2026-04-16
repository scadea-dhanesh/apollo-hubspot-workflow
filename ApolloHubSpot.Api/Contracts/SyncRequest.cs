using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApolloHubSpot.Api.Contracts;

/// <summary>
/// Live Apollo search → enrich → HubSpot upsert.
/// When this object is sent as JSON, paid Apollo bulk_match flags default to false unless set (saves credits).
/// If the sync endpoint receives no body (<c>null</c>), bulk_match uses <see cref="ApolloHubSpot.Api.Options.ApolloOptions"/> instead.
/// </summary>
public sealed class SyncRequest
{
    [JsonPropertyName("max_pages")]
    public int? MaxPages { get; set; }

    /// <summary>Optional ICP pack; merged like n8n <c>icp_filters</c> into Apollo search body (before <c>apollo_filters</c>).</summary>
    [JsonPropertyName("icp_filters")]
    public JsonElement? IcpFilters { get; set; }

    /// <summary>
    /// When true, Apollo <c>people/bulk_match</c> uses <c>reveal_personal_emails=true</c> (consumes credits). Default false.
    /// </summary>
    [JsonPropertyName("reveal_personal_email")]
    public bool RevealPersonalEmail { get; set; }

    /// <summary>
    /// When true, <c>reveal_phone_number=true</c> on bulk_match (consumes credits; requires <c>Apollo:WebhookUrl</c>). Default false.
    /// </summary>
    [JsonPropertyName("reveal_phone_number")]
    public bool RevealPhoneNumber { get; set; }

    /// <summary>
    /// Optional Apollo People Search fields merged last (highest priority). Use the same keys as Apollo’s API body, e.g.
    /// <c>person_titles</c>, <c>person_not_titles</c>, <c>person_locations</c>, <c>organization_locations</c>, <c>person_seniorities</c>,
    /// <c>organization_num_employees_ranges</c>, <c>organization_industries</c> (Apollo’s exact lowercase strings, e.g. <c>hospital &amp; health care</c>),
    /// <c>q_keywords</c>, <c>q_organization_domains_list</c>, <c>organization_ids</c>,
    /// <c>contact_email_status</c>, <c>per_page</c>, <c>include_similar_titles</c>, revenue as <c>revenue_range_min</c> / <c>revenue_range_max</c> or bracket keys, technology UID arrays, etc.
    /// </summary>
    [JsonPropertyName("apollo_filters")]
    public JsonElement? ApolloFilters { get; set; }
}
