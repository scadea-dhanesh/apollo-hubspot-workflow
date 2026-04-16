using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApolloHubSpot.Api.Options;
using Microsoft.Extensions.Options;

namespace ApolloHubSpot.Api.Services;

/// <summary>Query flags for <c>people/bulk_match</c>; omitted behaviors default off at the API layer.</summary>
public readonly record struct ApolloBulkMatchQuery(
    bool RevealPersonalEmails,
    bool RevealPhoneNumber,
    bool RunWaterfallEmail,
    bool RunWaterfallPhone);

public sealed class ApolloLeadClient(HttpClient http, IOptions<ApolloOptions> apolloOpts)
{
    private const string SearchUrl = "https://api.apollo.io/api/v1/mixed_people/api_search";
    private const string BulkMatchBase = "https://api.apollo.io/api/v1/people/bulk_match";
    private readonly ApolloOptions _opts = apolloOpts.Value;

    public async Task<ApolloSearchPage> SearchAsync(string apiKey, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, SearchUrl);
        req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Apollo search failed {(int)resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var people = ExtractPeople(root);
        var total = 0;
        if (root.TryGetProperty("total_entries", out var te) && te.TryGetInt32(out var t))
            total = t;
        else if (root.TryGetProperty("pagination", out var pag) && pag.TryGetProperty("total_entries", out var te2) &&
                 te2.TryGetInt32(out var t2))
            total = t2;

        return new ApolloSearchPage(people, total);
    }

    /// <summary>One id per call, matching n8n "Apollo Enrich". Query string from <paramref name="query"/> (per-sync overrides).</summary>
    public async Task<IReadOnlyList<JsonElement>> EnrichMatchesAsync(
        string apiKey,
        string personId,
        ApolloBulkMatchQuery query,
        CancellationToken ct)
    {
        var bulkUrl = BuildBulkMatchUrl(query);
        var payload = new JsonObject
        {
            ["details"] = new JsonArray(new JsonObject { ["id"] = personId })
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, bulkUrl);
        req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Apollo enrich failed {(int)resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
            return [];
        return matches.EnumerateArray().Select(m => m.Clone()).ToList();
    }

    private string BuildBulkMatchUrl(ApolloBulkMatchQuery query)
    {
        var q = new List<string>
        {
            "reveal_personal_emails=" + (query.RevealPersonalEmails ? "true" : "false")
        };

        if (query.RevealPhoneNumber)
        {
            q.Add("reveal_phone_number=true");
            var wh = _opts.WebhookUrl.Trim();
            if (wh.Length == 0)
                throw new InvalidOperationException(
                    "Apollo:WebhookUrl is required when reveal_phone_number is true (or set APOLLO_WEBHOOK_URL).");
            q.Add("webhook_url=" + Uri.EscapeDataString(wh));
        }
        else
            q.Add("reveal_phone_number=false");

        if (query.RunWaterfallEmail)
            q.Add("run_waterfall_email=true");
        if (query.RunWaterfallPhone)
            q.Add("run_waterfall_phone=true");

        return BulkMatchBase + "?" + string.Join("&", q);
    }

    private static List<JsonElement> ExtractPeople(JsonElement root)
    {
        JsonElement arr = default;
        if (root.TryGetProperty("people", out var p) && p.ValueKind == JsonValueKind.Array)
            arr = p;
        else if (root.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array)
            arr = r;
        else if (root.TryGetProperty("contacts", out var c) && c.ValueKind == JsonValueKind.Array)
            arr = c;
        else
            return [];

        return arr.EnumerateArray().Select(x => x.Clone()).ToList();
    }
}

public sealed record ApolloSearchPage(IReadOnlyList<JsonElement> People, int TotalEntries);
