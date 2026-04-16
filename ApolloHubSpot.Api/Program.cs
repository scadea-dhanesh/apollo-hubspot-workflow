using ApolloHubSpot.Api.Contracts;
using ApolloHubSpot.Api.Options;
using ApolloHubSpot.Api.Services;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ApolloOptions>(builder.Configuration.GetSection("Apollo"));
builder.Services.PostConfigure<ApolloOptions>(opts =>
{
    var wh = Environment.GetEnvironmentVariable("APOLLO_WEBHOOK_URL");
    if (!string.IsNullOrWhiteSpace(wh))
        opts.WebhookUrl = wh.Trim();
    var revealPhone = Environment.GetEnvironmentVariable("APOLLO_REVEAL_PHONE_NUMBER");
    if (!string.IsNullOrWhiteSpace(revealPhone) &&
        bool.TryParse(revealPhone.Trim(), out var rp))
        opts.RevealPhoneNumber = rp;
    var revealEmail = Environment.GetEnvironmentVariable("APOLLO_REVEAL_PERSONAL_EMAILS");
    if (!string.IsNullOrWhiteSpace(revealEmail) &&
        bool.TryParse(revealEmail.Trim(), out var re))
        opts.RevealPersonalEmails = re;
    var wfEmail = Environment.GetEnvironmentVariable("APOLLO_RUN_WATERFALL_EMAIL");
    if (!string.IsNullOrWhiteSpace(wfEmail) && bool.TryParse(wfEmail.Trim(), out var wfe))
        opts.RunWaterfallEmail = wfe;
    var wfPhone = Environment.GetEnvironmentVariable("APOLLO_RUN_WATERFALL_PHONE");
    if (!string.IsNullOrWhiteSpace(wfPhone) && bool.TryParse(wfPhone.Trim(), out var wfp))
        opts.RunWaterfallPhone = wfp;
});
builder.Services.Configure<HubSpotOptions>(builder.Configuration.GetSection("HubSpot"));
builder.Services.PostConfigure<HubSpotOptions>(opts =>
{
    if (!string.IsNullOrWhiteSpace(opts.AccessToken))
        return;
    var fromEnv = Environment.GetEnvironmentVariable("HUBSPOT_ACCESS_TOKEN");
    if (!string.IsNullOrWhiteSpace(fromEnv))
        opts.AccessToken = fromEnv.Trim();
});
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection("Sync"));
builder.Services.Configure<GroqOptions>(builder.Configuration.GetSection("Groq"));
builder.Services.PostConfigure<GroqOptions>(opts =>
{
    if (string.IsNullOrWhiteSpace(opts.ApiKey))
    {
        var fromEnv = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            opts.ApiKey = fromEnv.Trim();
    }

    var modelEnv = Environment.GetEnvironmentVariable("GROQ_MODEL");
    if (!string.IsNullOrWhiteSpace(modelEnv))
        opts.Model = modelEnv.Trim();
});

builder.Services.AddHttpClient<ApolloLeadClient>(c => c.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddHttpClient<HubSpotContactClient>(c => c.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddHttpClient<GroqApolloFilterGenerator>(c =>
{
    c.BaseAddress = new Uri("https://api.groq.com/");
    c.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services.AddSingleton<ApolloSearchBodyComposer>();
builder.Services.AddScoped<LeadSyncService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Apollo → HubSpot",
        Version = "v1",
        Description =
            "Live sync: Apollo People Search → per-person enrich → HubSpot contacts batch upsert. "
            + "Optional JSON body: max_pages, apollo_filters, icp_filters, and opt-in Apollo bulk_match flags "
            + "(reveal_personal_email, reveal_phone_number; default false when JSON is sent). "
            + "POST /api/sync/from-prompt: Groq → apollo_filters, then same sync pipeline."
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Apollo → HubSpot v1");
        options.RoutePrefix = "swagger";
    });
}

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithTags("Health")
    .WithSummary("Liveness check");

app.MapPost("/api/sync", async (SyncRequest? body, LeadSyncService sync, CancellationToken ct) =>
    {
        try
        {
            var result = await sync.RunAsync(body, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    })
    .WithName("SyncApolloToHubSpot")
    .WithTags("Sync")
    .WithSummary("Live sync: Apollo search → enrich → HubSpot upsert. Optional apollo_filters and icp_filters.");

app.MapPost("/api/sync/from-prompt", async (
        PromptSyncRequest body,
        GroqApolloFilterGenerator groq,
        LeadSyncService sync,
        CancellationToken ct) =>
    {
        try
        {
            var (req, raw) = await groq
                .BuildSyncRequestAsync(body.Prompt, body.MaxPages, body.IncludeAssistantRaw, ct)
                .ConfigureAwait(false);
            req.RevealPersonalEmail = body.RevealPersonalEmail;
            req.RevealPhoneNumber = body.RevealPhoneNumber;
            var result = await sync.RunAsync(req, ct).ConfigureAwait(false);
            return Results.Ok(new PromptSyncResponse
            {
                MaxPages = req.MaxPages ?? 0,
                ApolloFilters = req.ApolloFilters!.Value,
                Sync = result,
                AssistantRaw = raw
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    })
    .WithName("SyncFromPrompt")
    .WithTags("Sync")
    .WithSummary("Groq converts prompt to apollo_filters, then runs the same sync as POST /api/sync.");

app.Run();
