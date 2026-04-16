namespace ApolloHubSpot.Api.Options;

public sealed class SyncOptions
{
    /// <summary>Capped at 500 in n8n prod path.</summary>
    public int ApolloMaxPages { get; set; } = 50;

    /// <summary>Delay after each HubSpot batch (n8n Wait node: 600 ms per contact).</summary>
    public int MillisecondsBetweenHubSpotBatches { get; set; } = 600;

    /// <summary>Optional default ICP filter pack (same shape as n8n icp_filters).</summary>
    public string? DefaultIcpFiltersJson { get; set; }

    /// <summary>
    /// When true, logs each enriched person: Information = mapped fields for HubSpot; Debug = truncated raw Apollo JSON.
    /// </summary>
    public bool LogApolloEnrichment { get; set; }
}
