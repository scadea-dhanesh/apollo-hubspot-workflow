namespace ApolloHubSpot.Api.Options;

public sealed class ApolloOptions
{
    public string ApiKey { get; set; } = "";

    /// <summary>1–100; mirrors APOLLO_PER_PAGE.</summary>
    public int PerPage { get; set; } = 100;

    public string PersonTitles { get; set; } = "recruiter";
    public string PersonLocations { get; set; } = "India";
    public string OrganizationLocations { get; set; } = "";
    public string PersonSeniorities { get; set; } = "";
    public string QKeywords { get; set; } = "";
    public string IncludeSimilarTitles { get; set; } = "";
    public string OrganizationDomains { get; set; } = "";
    public string OrganizationIds { get; set; } = "";
    public string ContactEmailStatus { get; set; } = "";
    /// <summary>Ranges separated by | e.g. 1,10|250,500</summary>
    public string OrganizationNumEmployeesRanges { get; set; } = "";
    public string? RevenueMin { get; set; }
    public string? RevenueMax { get; set; }
    /// <summary>Raw JSON merged into the Apollo search body (APOLLO_SEARCH_EXTRAS_JSON).</summary>
    public string SearchExtrasJson { get; set; } = "";

    /// <summary>Passed to <c>people/bulk_match</c> as <c>reveal_personal_emails</c> (uses credits).</summary>
    public bool RevealPersonalEmails { get; set; } = true;

    /// <summary>
    /// Passed to <c>people/bulk_match</c> as <c>reveal_phone_number</c>. When true, Apollo requires <see cref="WebhookUrl"/> (uses credits; may deliver async to webhook per Apollo docs).
    /// </summary>
    public bool RevealPhoneNumber { get; set; }

    /// <summary>Required when <see cref="RevealPhoneNumber"/> is true — Apollo <c>webhook_url</c> query parameter.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary><c>run_waterfall_email</c> on bulk enrich (optional).</summary>
    public bool RunWaterfallEmail { get; set; }

    /// <summary><c>run_waterfall_phone</c> on bulk enrich (optional).</summary>
    public bool RunWaterfallPhone { get; set; }
}
