using System.Text.Json;
using System.Text.Json.Nodes;
using ApolloHubSpot.Api.Options;
using Microsoft.Extensions.Options;

namespace ApolloHubSpot.Api.Services;

/// <summary>Ports n8n node "Apollo: Compose search request".</summary>
public sealed class ApolloSearchBodyComposer(IOptions<ApolloOptions> options)
{
    private static readonly HashSet<string> ArrayKeys =
    [
        "person_titles", "person_not_titles", "person_locations", "organization_locations",
        "person_seniorities", "organization_num_employees_ranges", "organization_industries",
        "q_organization_domains_list",
        "organization_ids", "contact_email_status", "currently_using_all_of_technology_uids",
        "currently_using_any_of_technology_uids", "currently_not_using_any_of_technology_uids",
        "q_organization_keyword_ids", "q_organization_not_keyword_ids", "q_organization_job_titles",
        "person_department_or_subdepartments", "q_not_organization_keyword_ids"
    ];

    private static readonly HashSet<string> OverlaySkip =
 ["persona", "apollo_filters", "_parse_error", "_raw", "icp_persona"];

    private readonly ApolloOptions _opts = options.Value;

    public JsonObject Build(int page, JsonElement? icpPack, JsonElement? requestApolloFilters)
    {
        var perPage = Math.Clamp(_opts.PerPage, 1, 100);
        var titles = SplitComma(_opts.PersonTitles);
        if (titles.Count == 0) titles = ["recruiter"];
        var locs = SplitComma(_opts.PersonLocations);
        if (locs.Count == 0) locs = ["India"];

        var body = new JsonObject
        {
            ["page"] = page,
            ["per_page"] = perPage,
            ["person_titles"] = new JsonArray(titles.Select(t => JsonValue.Create(t)!).ToArray()),
            ["person_locations"] = new JsonArray(locs.Select(t => JsonValue.Create(t)!).ToArray())
        };

        var orgLocs = SplitComma(_opts.OrganizationLocations);
        if (orgLocs.Count > 0)
            body["organization_locations"] = ToJsonArray(orgLocs);

        var sen = SplitComma(_opts.PersonSeniorities);
        if (sen.Count > 0)
            body["person_seniorities"] = ToJsonArray(sen);

        var kw = (_opts.QKeywords ?? "").Trim();
        if (kw.Length > 0)
            body["q_keywords"] = kw;

        var sim = (_opts.IncludeSimilarTitles ?? "").Trim().ToLowerInvariant();
        if (sim is "true" or "false")
            body["include_similar_titles"] = sim == "true";

        var domains = SplitComma(_opts.OrganizationDomains);
        if (domains.Count > 0)
            body["q_organization_domains_list"] = ToJsonArray(domains);

        var orgIds = SplitComma(_opts.OrganizationIds);
        if (orgIds.Count > 0)
            body["organization_ids"] = ToJsonArray(orgIds);

        var emailSt = SplitComma(_opts.ContactEmailStatus);
        if (emailSt.Count > 0)
            body["contact_email_status"] = ToJsonArray(emailSt);

        var empRaw = (_opts.OrganizationNumEmployeesRanges ?? "").Trim();
        if (empRaw.Length > 0)
        {
            var ranges = empRaw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x.Length > 0).ToList();
            if (ranges.Count > 0)
                body["organization_num_employees_ranges"] = ToJsonArray(ranges);
        }

        if (!string.IsNullOrWhiteSpace(_opts.RevenueMin) && double.TryParse(_opts.RevenueMin, out var rmin))
            body["revenue_range[min]"] = rmin;
        if (!string.IsNullOrWhiteSpace(_opts.RevenueMax) && double.TryParse(_opts.RevenueMax, out var rmax))
            body["revenue_range[max]"] = rmax;

        var extras = (_opts.SearchExtrasJson ?? "").Trim();
        if (extras.Length > 0)
        {
            try
            {
                var node = JsonNode.Parse(extras);
                if (node is JsonObject eo)
                {
                    foreach (var kv in eo)
                        body[kv.Key] = kv.Value?.DeepClone();
                }
            }
            catch (JsonException) { /* ignore like n8n */ }
        }

        ApplyIcpOverlay(body, icpPack);
        ApplyIcpOverlay(body, requestApolloFilters);

        body["page"] = page;
        return body;
    }

    private static void ApplyIcpOverlay(JsonObject body, JsonElement? pack)
    {
        if (pack is not { ValueKind: JsonValueKind.Object } el)
            return;
        if (el.TryGetProperty("_parse_error", out _))
            return;

        JsonElement? overlay = null;
        if (el.TryGetProperty("apollo_filters", out var af) && af.ValueKind == JsonValueKind.Object)
            overlay = af;

        if (overlay is null)
        {
            var personaTruthy = el.TryGetProperty("persona", out var personaEl) &&
 personaEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            var apolloFiltersTruthy = el.TryGetProperty("apollo_filters", out var apolloEl) &&
 apolloEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            if (!personaTruthy && !apolloFiltersTruthy)
                overlay = el;
        }

        if (overlay is not { ValueKind: JsonValueKind.Object } o)
            return;

        foreach (var prop in o.EnumerateObject())
        {
            if (OverlaySkip.Contains(prop.Name))
                continue;

            var v = prop.Value;
            if (ArrayKeys.Contains(prop.Name))
            {
                var arr = CoerceArr(v);
                if (arr is null || arr.Count == 0)
                    continue;
                body[prop.Name] = ToJsonArray(arr);
                continue;
            }

            if (v.ValueKind is JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && v.GetString() == ""))
                continue;

            if (prop.Name == "revenue_range_min" && v.TryGetDouble(out var rmin))
            {
                body["revenue_range[min]"] = rmin;
                continue;
            }
            if (prop.Name == "revenue_range_max" && v.TryGetDouble(out var rmax))
            {
                body["revenue_range[max]"] = rmax;
                continue;
            }
            if (prop.Name is "revenue_range[min]" or "revenue_range[max]" && v.TryGetDouble(out var rr))
            {
                body[prop.Name] = rr;
                continue;
            }

            body[prop.Name] = JsonNode.Parse(v.GetRawText());
        }
    }

    private static List<string>? CoerceArr(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(x => x.ToString().Trim()).Where(s => s.Length > 0).ToList();
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()?.Trim();
            return string.IsNullOrEmpty(s) ? null : [s];
        }
        return null;
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> items) =>
        new(items.Select(x => JsonValue.Create(x)!).ToArray());

    private static List<string> SplitComma(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
}
