namespace ApolloHubSpot.Api.Contracts;

public sealed class SyncResponse
{
    public int MaxPages { get; init; }
    public int PagesProcessed { get; init; }
    public int ApolloPeopleWithEmail { get; init; }
    public int EnrichedWithEmail { get; init; }
    public int HubSpotContactsUpserted { get; init; }
    public int SkippedDuplicateEmail { get; init; }
    public IReadOnlyList<string> SkipReasons { get; init; } = [];
}
