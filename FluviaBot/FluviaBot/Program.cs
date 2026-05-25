using FluviaBot.Review;
using FluviaBot.Services;
using FluviaBot.Webhooks;
using Octokit.Webhooks.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ── Required configuration validation ────────────────────────────────────────
var requiredSecrets = new Dictionary<string, string>
{
    ["GitHub:AppId"] = "GitHub__AppId",
    ["GitHub:WebhookSecret"] = "GitHub__WebhookSecret",
};

var missing = requiredSecrets
    .Where(kv => string.IsNullOrWhiteSpace(builder.Configuration[kv.Key]))
    .Select(kv => kv.Value)
    .ToList();

if (missing.Count > 0)
{
    Console.Error.WriteLine("ERROR: The following required environment variables are not set:");
    foreach (var name in missing)
        Console.Error.WriteLine($"  {name}");
    Environment.Exit(1);
}

// AppId must also be numeric (and within Int32 range, which the JWT factoryrequires)
if (!int.TryParse(builder.Configuration["GitHub:AppId"], out var parsedAppId) || parsedAppId < 1)
{
    Console.Error.WriteLine(
        "ERROR: GitHub__AppId must be a positive numeric GitHub App ID, but got: " +
        $"'{builder.Configuration["GitHub:AppId"]}'");
    Environment.Exit(1);
}

var webhookSecret = builder.Configuration["GitHub:WebhookSecret"]!;

var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddLogging();
builder.Services.AddRouting();

// HTTP request logging — gives visibility into webhook deliveries that the
// MapGitHubWebhooks middleware rejects (e.g. a failed signature check returns
// a 4xx before any processor runs, so it is otherwise invisible).
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields =
        Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestPath |
        Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestMethod |
        Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.ResponseStatusCode;
});

builder.Services.AddSingleton<GitHubAppTokenProvider>();

// Shared PR-context fetcher — used by both the review and Q&A flows.
builder.Services.AddSingleton<PullRequestContextFetcher>();

// ── Chat / AI providers ──────────────────────────────────────────────────────
// The chat layer is one IChatClient seam shared by both the review and the
// Q&A flow. ChatClientRegistry builds the right client on demand from
// IHttpClientFactory, so all that is registered here is a named HttpClient per
// provider — no per-provider service classes.
//
// To add an OpenAI-compatible provider you add ONE entry to
// ChatClientRegistry.ProviderBuilders plus one AddHttpClient line here.
builder.Services.AddHttpClient("chat:openai");
builder.Services.AddHttpClient("chat:ollama");
builder.Services.AddHttpClient("chat:huggingface");
builder.Services.AddHttpClient("chat:anthropic");
builder.Services.AddHttpClient("chat:google");

// LangGraph is NOT a chat client — it is a multi-model fan-out/merge pipeline,
// so it keeps a typed HttpClient and its own reviewer class.
builder.Services.AddHttpClient<LangGraphCodeReviewer>();

// ── Fluvia code review and comment Q&A ───────────────────────────────────────
// ProviderFactory resolves both flows from configuration: a ChatCodeReviewer /
// ChatQuestionAnswerer over any chat provider, or the LangGraphCodeReviewer.
builder.Services.AddScoped<ICodeReviewer>(ProviderFactory.ResolveReviewer);
builder.Services.AddScoped<PullRequestReviewService>();

builder.Services.AddScoped<IPullRequestQuestionAnswerer>(ProviderFactory.ResolveQuestionAnswerer);
builder.Services.AddScoped<CommentQuestionService>();

// ── Webhook processor ────────────────────────────────────────────────────────
builder.Services.AddScoped<Octokit.Webhooks.WebhookEventProcessor, WebhookProcessor>();

var app = builder.Build();

// ── Middleware pipeline ──────────────────────────────────────────────────────
// UseExceptionHandler must be registered FIRST so it sits at the top of the
// pipeline and can catch exceptions thrown by anything downstream.
app.UseExceptionHandler(errApp =>
{
    errApp.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var feature = context.Features
            .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();

        if (feature?.Error is not null)
            logger.LogError(feature.Error, "Unhandled exception while processing request.");

        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("An internal error occurred.");
    });
});

// Log request method/path/status for every request, including webhook
// deliveries the GitHub middleware rejects on a bad signature.
app.UseHttpLogging();

// Modern minimal-hosting routing — MapGitHubWebhooks directly on the app.
app.MapGitHubWebhooks("/api/github/webhooks", webhookSecret);

app.Logger.LogInformation("Server is running on port {Port}", port);
app.Logger.LogInformation(
    "Listening for webhooks at http://localhost:{Port}/api/github/webhooks", port);

app.Run();

/// Declared so <c>ILogger&lt;Program&gt;</c> can be resolved inside the
/// factory delegates (a top-level-statements program otherwise has no
/// accessible Program type).
public partial class Program;
