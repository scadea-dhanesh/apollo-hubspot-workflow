namespace ApolloHubSpot.Api.Options;

public sealed class HubSpotOptions
{
    /// <summary>Private app token (pat-...) or full Bearer value.</summary>
    public string AccessToken { get; set; } = "";
}
