using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ApolloHubSpot.Api.Contracts;
using ApolloHubSpot.Api.Options;
using Microsoft.Extensions.Options;

namespace ApolloHubSpot.Api.Services;

public sealed class GroqApolloFilterGenerator(HttpClient http, IOptions<GroqOptions> opts, ILogger<GroqApolloFilterGenerator> log)
{
    private const int MaxPagesCap = 50;

    private static readonly JsonSerializerOptions DeserializeOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly GroqOptions _o = opts.Value;

    public async Task<(SyncRequest Request, string? AssistantRaw)> BuildSyncRequestAsync(
        string userPrompt,
        int? maxPagesOverride,
        bool includeAssistantRaw,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_o.ApiKey))
            throw new InvalidOperationException(
                "Groq:ApiKey is required (configure Groq:ApiKey, User Secrets, or GROQ_API_KEY).");

        var prompt = userPrompt.Trim();
        if (prompt.Length == 0)
            throw new ArgumentException("Prompt is required.", nameof(userPrompt));

        const string system = """
You convert natural-language lead criteria into a JSON object for Apollo.io People Search (fields used with mixed_people/api_search).

Output ONE JSON object only. No markdown fences, no commentary.

Schema:
{
  "max_pages": <integer 1-50, search pages; use 5-15 unless user asks for more or fewer>,
  "apollo_filters": {
    "per_page": <integer 1-100>,
    "person_titles": [ "<title tokens>" ],
    "person_locations": [ "<e.g. India, United States, California, US>" ],
    "organization_locations": [ "<optional employer HQ regions>" ],
    "organization_industries": [ "<Apollo lowercase industry strings>" ],
    "organization_num_employees_ranges": [ "<min,max per range as one string, e.g. 51,200>" ],
    "person_seniorities": [ "<optional: owner, founder, c_suite, partner, vp, head, director, manager, senior, ...>" ],
    "q_keywords": "<optional string>",
    "include_similar_titles": <true|false optional>,
    "contact_email_status": [ "<optional>" ],
    "q_organization_domains_list": [ "<optional>" ]
  }
}

Rules:
- Use JSON arrays for list fields. Omit keys you cannot infer.
- organization_industries: use Apollo-style lowercase labels when possible, e.g. "hospital & health care", "medical practice", "health, wellness & fitness", "marketing & advertising", "information technology & services", "computer software".
- person_locations: country names or "State, US" style.
- If criteria are extremely narrow (likely zero Apollo results), broaden slightly while staying faithful.
- Never invent emails, phones, or domains.
- Do not include reveal_personal_email or reveal_phone_number; the caller sets those on the API request.

""";

        var body = new JsonObject
        {
            ["model"] = _o.Model,
            ["temperature"] = _o.Temperature,
            ["max_tokens"] = _o.MaxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = prompt })
        };

        var path = _o.ChatCompletionsPath.TrimStart('/');
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiKey.Trim());
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Groq chat failed {(int)resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
            throw new InvalidOperationException("Groq response missing choices.");

        var msg = choices[0];
        if (!msg.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var contentEl) ||
            contentEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Groq response missing message content.");

        var raw = contentEl.GetString() ?? "";
        var cleaned = StripMarkdownFence(raw.Trim());
        log.LogDebug("Groq apollo_filters JSON: {Text}", cleaned.Length > 4000 ? cleaned[..4000] + "…" : cleaned);

        var sync = JsonSerializer.Deserialize<SyncRequest>(cleaned, DeserializeOpts);
        if (sync is null)
            throw new InvalidOperationException("Model returned JSON that could not be parsed as a sync request.");
        if (sync.ApolloFilters is not { ValueKind: JsonValueKind.Object })
            throw new InvalidOperationException("Model JSON must include an object \"apollo_filters\".");

        if (maxPagesOverride.HasValue)
            sync.MaxPages = Math.Clamp(maxPagesOverride.Value, 1, MaxPagesCap);
        else if (sync.MaxPages.HasValue)
            sync.MaxPages = Math.Clamp(sync.MaxPages.Value, 1, MaxPagesCap);
        else
            sync.MaxPages = Math.Min(10, MaxPagesCap);

        var assistantOut = includeAssistantRaw ? raw : null;
        return (sync, assistantOut);
    }

    private static string StripMarkdownFence(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = t.IndexOf('\n');
            if (nl > 0)
                t = t[(nl + 1)..];
            var end = t.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 0)
                t = t[..end];
        }
        return t.Trim();
    }
}
