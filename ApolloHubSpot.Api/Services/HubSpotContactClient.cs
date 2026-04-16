using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace ApolloHubSpot.Api.Services;

public sealed class HubSpotContactClient(HttpClient http, ILogger<HubSpotContactClient> log)
{
    private const string BatchUpsertUrl = "https://api.hubapi.com/crm/v3/objects/contacts/batch/upsert";

    public async Task UpsertBatchAsync(string accessToken, IReadOnlyList<HubSpotContactInput> contacts, CancellationToken ct)
    {
        if (contacts.Count == 0)
            return;

        var trimmed = accessToken.Trim();
        string credential;
        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            credential = trimmed["Bearer ".Length..].Trim();
        else
            credential = trimmed;
        if (string.IsNullOrWhiteSpace(credential))
            throw new ArgumentException("HubSpot access token is empty.", nameof(accessToken));

        var inputs = new JsonArray();
        foreach (var c in contacts)
        {
            var props = new JsonObject();
            Add(props, "email", c.Email);
            Add(props, "firstname", c.FirstName);
            Add(props, "lastname", c.LastName);
            Add(props, "phone", c.Phone);
            Add(props, "mobilephone", c.MobilePhone);
            Add(props, "company", c.Company);
            Add(props, "website", NormalizeHttpUrl(c.Website));
            Add(props, "jobtitle", c.JobTitle);
            Add(props, "hs_linkedin_url", NormalizeHttpUrl(c.LinkedinUrl));
            Add(props, "address", c.StreetAddress);
            Add(props, "city", c.City);
            Add(props, "state", c.State);
            Add(props, "zip", c.Zip);
            Add(props, "country", c.Country);
            Add(props, "twitterhandle", c.TwitterHandle);

            inputs.Add(new JsonObject
            {
                ["id"] = c.Email,
                ["idProperty"] = "email",
                ["properties"] = props
            });
        }

        var body = new JsonObject { ["inputs"] = inputs };
        using var req = new HttpRequestMessage(HttpMethod.Post, BatchUpsertUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HubSpot batch upsert failed {(int)resp.StatusCode}: {json}");

        LogHubSpotBatchOutcome(json);
    }

    private void LogHubSpotBatchOutcome(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("numErrors", out var ne) && ne.ValueKind == JsonValueKind.Number && ne.TryGetInt32(out var n) && n > 0)
                log.LogWarning("HubSpot batch upsert reported numErrors={Num}: {Snippet}", n, Truncate(json, 2500));

            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
                log.LogWarning("HubSpot batch upsert errors array: {Snippet}", Truncate(errs.GetRawText(), 2000));

            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
            {
                var first = results[0];
                               if (first.TryGetProperty("properties", out var pr))
                    log.LogDebug("HubSpot first contact properties returned: {Props}", Truncate(pr.GetRawText(), 2000));
            }
        }
        catch (JsonException)
        {
            log.LogDebug("HubSpot batch response (unparsed): {Snippet}", Truncate(json, 1500));
        }
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max)
            return s;
        return s[..max] + "…";
    }

    private static string? NormalizeHttpUrl(string? url)
    {
        var v = url?.Trim();
        if (string.IsNullOrEmpty(v))
            return null;
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return v;
        return "https://" + v;
    }

    private static void Add(JsonObject props, string name, string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
            return;
        props[name] = v;
    }
}

/// <summary>Contact fields mapped to standard HubSpot contact properties (batch upsert).</summary>
public sealed record HubSpotContactInput
{
    public required string Email { get; init; }
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Phone { get; init; } = "";
    public string MobilePhone { get; init; } = "";
    public string Company { get; init; } = "";
    public string Website { get; init; } = "";
    public string JobTitle { get; init; } = "";
    public string LinkedinUrl { get; init; } = "";
    public string StreetAddress { get; init; } = "";
    public string City { get; init; } = "";
    public string State { get; init; } = "";
    public string Zip { get; init; } = "";
    public string Country { get; init; } = "";
    /// <summary>Twitter/X username only (no @), for HubSpot <c>twitterhandle</c>.</summary>
    public string TwitterHandle { get; init; } = "";
}
