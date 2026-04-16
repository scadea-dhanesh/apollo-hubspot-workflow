using System.Text.Json;
using ApolloHubSpot.Api.Contracts;
using ApolloHubSpot.Api.Options;
using Microsoft.Extensions.Options;

namespace ApolloHubSpot.Api.Services;

public sealed class LeadSyncService(
    IOptions<ApolloOptions> apolloOpts,
    IOptions<HubSpotOptions> hubOpts,
    IOptions<SyncOptions> syncOpts,
    ApolloSearchBodyComposer composer,
    ApolloLeadClient apolloClient,
    HubSpotContactClient hubspotClient,
    ILogger<LeadSyncService> log)
{
    public async Task<SyncResponse> RunAsync(SyncRequest? request, CancellationToken ct)
    {
        var sync = syncOpts.Value;
        var apolloO = apolloOpts.Value;
        var hubO = hubOpts.Value;

        var maxPages = request?.MaxPages ?? Math.Min(sync.ApolloMaxPages, 500);
        if (maxPages < 1)
            maxPages = 1;

        if (string.IsNullOrWhiteSpace(apolloO.ApiKey))
            throw new InvalidOperationException("Apollo:ApiKey is required.");
        // Paid reveal flags: opt-in on JSON body (default false). If the body is omitted entirely, use ApolloOptions (legacy).
        // Waterfall flags always come from ApolloOptions / env (not the sync JSON body).
        bool revealPersonalEmail;
        bool revealPhoneNumber;
        if (request is null)
        {
            revealPersonalEmail = apolloO.RevealPersonalEmails;
            revealPhoneNumber = apolloO.RevealPhoneNumber;
        }
        else
        {
            revealPersonalEmail = request.RevealPersonalEmail;
            revealPhoneNumber = request.RevealPhoneNumber;
        }

        var runWaterfallEmail = apolloO.RunWaterfallEmail;
        var runWaterfallPhone = apolloO.RunWaterfallPhone;

        if (revealPhoneNumber && string.IsNullOrWhiteSpace(apolloO.WebhookUrl))
            throw new InvalidOperationException(
                "Apollo:WebhookUrl is required when reveal_phone_number is true on the sync request (Apollo bulk_match requires webhook_url for phone reveal).");
        if (string.IsNullOrWhiteSpace(hubO.AccessToken))
            throw new InvalidOperationException(
                "HubSpot:AccessToken is required (configure HubSpot:AccessToken, HubSpot__AccessToken, or HUBSPOT_ACCESS_TOKEN).");

        JsonElement? defaultIcp = null;
        if (!string.IsNullOrWhiteSpace(sync.DefaultIcpFiltersJson))
        {
            try
            {
                using var d = JsonDocument.Parse(sync.DefaultIcpFiltersJson);
                defaultIcp = d.RootElement.Clone();
            }
            catch (JsonException) { /* ignore */ }
        }

        JsonElement? reqIcp = request?.IcpFilters;
        if (reqIcp is { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
            reqIcp = null;
        JsonElement? icpPack = reqIcp ?? defaultIcp;

        JsonElement? reqApollo = request?.ApolloFilters;
        if (reqApollo is { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
            reqApollo = null;

        var bulkMatchQuery = new ApolloBulkMatchQuery(
            revealPersonalEmail,
            revealPhoneNumber,
            runWaterfallEmail,
            runWaterfallPhone);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipReasons = new List<string>();
        var pagesProcessed = 0;
        var apolloWithEmail = 0;
        var enrichedRows = 0;
        var upserted = 0;
        var dupSkipped = 0;

        for (var page = 1; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            var body = composer.Build(page, icpPack, reqApollo);
            var search = await apolloClient.SearchAsync(apolloO.ApiKey, body, ct).ConfigureAwait(false);
            var people = search.People.ToList();

            pagesProcessed++;
            if (people.Count == 0)
            {
                skipReasons.Add($"page_{page}_empty_apollo_page");
                continue;
            }

            var filtered = people
                .Where(p => p.TryGetProperty("has_email", out var he) && he.ValueKind == JsonValueKind.True)
                .ToList();
            apolloWithEmail += filtered.Count;
            if (filtered.Count == 0)
            {
                skipReasons.Add($"page_{page}_no_has_email");
                continue;
            }

            var toHubSpot = new List<HubSpotContactInput>();
            foreach (var person in filtered)
            {
                if (!person.TryGetProperty("id", out var idEl))
                    continue;
                var id = idEl.GetString();
                if (string.IsNullOrEmpty(id))
                    continue;

                var matches = await apolloClient
                    .EnrichMatchesAsync(apolloO.ApiKey, id, bulkMatchQuery, ct)
                    .ConfigureAwait(false);

                foreach (var m in matches)
                {
                    var contact = FormatContact(m);
                    if (contact is null)
                        continue;
                    enrichedRows++;
                    if (sync.LogApolloEnrichment)
                        LogEnrichedLead(log, m, contact);
                    var email = contact.Email;
                    if (!seen.Add(email))
                    {
                        dupSkipped++;
                        continue;
                    }
                    toHubSpot.Add(contact);
                }
            }

            const int chunk = 100;
            for (var i = 0; i < toHubSpot.Count; i += chunk)
            {
                var slice = toHubSpot.Skip(i).Take(chunk).ToList();
                if (slice.Count == 0)
                    continue;
                await hubspotClient.UpsertBatchAsync(hubO.AccessToken, slice, ct).ConfigureAwait(false);
                upserted += slice.Count;
                if (sync.MillisecondsBetweenHubSpotBatches > 0)
                    await Task.Delay(sync.MillisecondsBetweenHubSpotBatches, ct).ConfigureAwait(false);
            }
        }

        log.LogInformation(
            "Sync finished: pages={Pages}, apollo_has_email={HasEmail}, enriched_rows={Enriched}, hubspot={Hs}, dupes={Dup}",
            pagesProcessed, apolloWithEmail, enrichedRows, upserted, dupSkipped);

        return new SyncResponse
        {
            MaxPages = maxPages,
            PagesProcessed = pagesProcessed,
            ApolloPeopleWithEmail = apolloWithEmail,
            EnrichedWithEmail = enrichedRows,
            HubSpotContactsUpserted = upserted,
            SkippedDuplicateEmail = dupSkipped,
            SkipReasons = skipReasons
        };
    }

    private static void LogEnrichedLead(ILogger log, JsonElement rawApollo, HubSpotContactInput mapped)
    {
        var apolloTitle = ReadString(rawApollo, "title") ?? "";
        log.LogInformation(
            "Apollo→HubSpot | email={Email} | name={First} {Last} | apollo_title={ApolloTitle} | hubspot_jobtitle={JobTitle} | company={Company} | city={City} | state={State} | country={Country} | zip={Zip} | phone={Phone} | mobile={Mobile} | website={Website} | linkedin={Linkedin} | twitter={Twitter}",
            mapped.Email, mapped.FirstName, mapped.LastName, apolloTitle, mapped.JobTitle, mapped.Company, mapped.City,
            mapped.State, mapped.Country, mapped.Zip, mapped.Phone, mapped.MobilePhone, mapped.Website, mapped.LinkedinUrl,
            mapped.TwitterHandle);

        var json = rawApollo.GetRawText();
        const int max = 3500;
        if (json.Length > max)
            json = json[..max] + "…";
        log.LogDebug("Apollo raw match for {Email}: {Json}", mapped.Email, json);
    }

    private static HubSpotContactInput? FormatContact(JsonElement m)
    {
        var email = ReadString(m, "email")?.Trim().ToLowerInvariant() ?? "";
        if (email.Length == 0)
            return null;

        var fn = ReadString(m, "first_name") ?? "";
        var ln = ReadString(m, "last_name") ?? ReadString(m, "last_name_obfuscated") ?? "";
        if (string.IsNullOrWhiteSpace(fn) && ReadString(m, "name") is { Length: > 0 } fullName)
            SplitFullName(fullName, out fn, out ln);

        var (phone, mobile) = ReadPhones(m);

        var company = "";
        var website = "";
        var orgPhone = "";
        var orgIndustry = "";
        JsonElement org = default;
        var hasOrg = m.TryGetProperty("organization", out org) && org.ValueKind == JsonValueKind.Object;
        if (hasOrg)
        {
            company = ReadString(org, "name") ?? "";
            website = ReadString(org, "website_url") ?? "";
            if (string.IsNullOrWhiteSpace(website))
            {
                var domain = ReadString(org, "primary_domain") ?? ReadString(org, "domain");
                if (!string.IsNullOrWhiteSpace(domain) && domain.Contains('.'))
                    website = domain.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? domain : "https://" + domain.Trim();
            }
            orgPhone = ReadString(org, "primary_phone") ?? ReadString(org, "phone") ?? "";
            orgIndustry = ReadString(org, "industry") ?? ReadString(org, "org_industry") ?? "";
        }

        if (string.IsNullOrWhiteSpace(phone) && !string.IsNullOrWhiteSpace(orgPhone))
            phone = orgPhone.Trim();
        if (string.IsNullOrWhiteSpace(phone))
            phone = ReadString(m, "sanitized_phone") ?? ReadString(m, "direct_phone") ?? ReadString(m, "phone") ?? "";

        var li = ReadString(m, "linkedin_url") ?? ReadString(m, "linkedin") ?? "";
        var city = ReadString(m, "city") ?? ReadString(m, "person_city") ?? "";
        var state = ReadString(m, "state") ?? "";
        var country = ReadString(m, "country") ?? "";
        var zip = ReadString(m, "postal_code") ?? ReadString(m, "zip") ?? "";
        var street = ReadString(m, "street_address") ?? ReadString(m, "formatted_address") ?? ReadString(m, "present_raw_address") ?? "";

        if (hasOrg)
        {
            if (string.IsNullOrWhiteSpace(city))
                city = ReadString(org, "city") ?? ReadString(org, "headquarters_city") ?? ReadString(org, "hq_city") ?? "";
            if (string.IsNullOrWhiteSpace(state))
                state = ReadString(org, "state") ?? ReadString(org, "headquarters_state") ?? ReadString(org, "hq_state") ?? "";
            if (string.IsNullOrWhiteSpace(country))
                country = ReadString(org, "country") ?? ReadString(org, "headquarters_country") ?? ReadString(org, "hq_country") ?? "";
            if (string.IsNullOrWhiteSpace(zip))
                zip = ReadString(org, "postal_code") ?? ReadString(org, "zip") ?? "";
            if (string.IsNullOrWhiteSpace(street))
                street = ReadString(org, "street_address") ?? ReadString(org, "raw_address") ?? "";
        }

        var headline = ReadString(m, "headline") ?? "";
        var title = ReadString(m, "title") ?? ReadString(m, "current_title") ?? "";
        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(headline))
            title = headline;
        var seniority = ReadString(m, "seniority") ?? "";
        var departments = ReadDepartments(m);
        var jobTitle = BuildJobTitle(title, headline, seniority, departments, orgIndustry);

        var twitter = TwitterHandleFromUrl(ReadString(m, "twitter_url"));

        return new HubSpotContactInput
        {
            Email = email,
            FirstName = fn.Trim(),
            LastName = ln.Trim(),
            Phone = phone.Trim(),
            MobilePhone = mobile.Trim(),
            Company = company.Trim(),
            Website = website.Trim(),
            JobTitle = jobTitle,
            LinkedinUrl = li.Trim(),
            StreetAddress = street.Trim(),
            City = city.Trim(),
            State = state.Trim(),
            Zip = zip.Trim(),
            Country = country.Trim(),
            TwitterHandle = twitter ?? ""
        };
    }

    private static string? ReadString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static (string Phone, string Mobile) ReadPhones(JsonElement m)
    {
        if (!m.TryGetProperty("phone_numbers", out var pn) || pn.ValueKind != JsonValueKind.Array)
            return ("", "");

        string? first = null;
        string? mobile = null;
        foreach (var item in pn.EnumerateArray())
        {
            var raw = ReadString(item, "raw_number") ?? ReadString(item, "sanitized_number");
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            first ??= raw;
            var type = (ReadString(item, "type_cd") ?? ReadString(item, "type") ?? "").ToLowerInvariant();
            if (type.Contains("mobile", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("cell", StringComparison.OrdinalIgnoreCase))
                mobile = raw;
        }

        return (first ?? "", mobile ?? "");
    }

    private static string ReadDepartments(JsonElement m)
    {
        if (!m.TryGetProperty("departments", out var d) || d.ValueKind != JsonValueKind.Array)
            return "";
        var parts = d.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()?.Trim() : null)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
        return parts.Count == 0 ? "" : string.Join(", ", parts);
    }

    private static string BuildJobTitle(string title, string headline, string seniority, string departments, string orgIndustry)
    {
        const int maxLen = 250;
        var parts = new List<string>();
        var t = title.Trim();
        var h = headline.Trim();
        if (t.Length > 0)
            parts.Add(t);
        if (h.Length > 0 && !h.Equals(t, StringComparison.OrdinalIgnoreCase))
            parts.Add(h);
        var sen = seniority.Trim();
        if (sen.Length > 0)
            parts.Add("Seniority: " + sen);
        var dep = departments.Trim();
        if (dep.Length > 0)
            parts.Add("Departments: " + dep);
        var ind = orgIndustry.Trim();
        if (ind.Length > 0)
            parts.Add("Company industry: " + ind);

        var s = string.Join(" | ", parts);
        if (s.Length > maxLen)
            s = s[..maxLen].TrimEnd() + "..."; // ASCII ellipsis — HubSpot may reject Unicode … in some validations
        return s;
    }

    private static string? TwitterHandleFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var s = url.Trim();
        try
        {
            if (!s.Contains("://", StringComparison.Ordinal))
                s = "https://" + s.TrimStart('/');
            var u = new Uri(s);
            var host = u.Host.ToLowerInvariant();
            if (host.Contains("twitter.", StringComparison.Ordinal) || host == "x.com" || host.EndsWith(".x.com", StringComparison.Ordinal))
            {
                var seg = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length > 0)
                {
                    var h = seg[0].TrimStart('@');
                    if (h.Length > 0 && !h.Equals("intent", StringComparison.OrdinalIgnoreCase))
                        return h;
                }
            }
        }
        catch (UriFormatException) { /* ignore */ }

        return null;
    }

    private static void SplitFullName(string fullName, out string firstName, out string lastName)
    {
        firstName = "";
        lastName = "";
        var t = fullName.Trim();
        if (t.Length == 0)
            return;
        var sp = t.IndexOf(' ');
        if (sp < 0)
        {
            firstName = t;
            return;
        }
        firstName = t[..sp].Trim();
        lastName = t[(sp + 1)..].Trim();
    }
}
