using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluviaBot.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FluviaBot.Review;

/// <summary>
/// <see cref="ICodeReviewer"/> that delegates to the Python LangGraph review
/// microservice at <c>LangGraph__BaseUrl</c>.
///
/// Unlike the chat-based reviewers, this is NOT a single LLM call — the service
/// runs a fan-out graph that queries three models concurrently and merges them:
///
///   fetch → [model 1, model 2, model 3] (concurrent) → merge → return
///
/// Which models run is controlled by the Python service's own environment
/// (SLOT1/2/3_PROVIDER, etc.). Being a pipeline, not a chat call, it stays its
/// own reviewer and is not part of <c>ChatClientRegistry</c>.
///
/// Config: <c>LangGraph__BaseUrl</c> — optional, defaults to
/// http://langgraph-review-service:8000 (must match the compose service name).
/// </summary>
public sealed class LangGraphCodeReviewer : ICodeReviewer
{
    // Must match the `langgraph-review-service` service name in docker-compose.
    private const string DefaultBaseUrl = "http://langgraph-review-service:8000";

    private readonly HttpClient _http;
    private readonly ILogger<LangGraphCodeReviewer> _logger;

    public LangGraphCodeReviewer(
        HttpClient http, IConfiguration config, ILogger<LangGraphCodeReviewer> logger)
    {
        _http = http;
        _logger = logger;

        var baseUrl = config["LangGraph:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = DefaultBaseUrl;

        _http.BaseAddress = new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromMinutes(10); // parallel LLM calls can be slow
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<CodeReview> ReviewAsync(
        PullRequestContext context, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "LangGraphCodeReviewer: delegating PR #{Number} to review service at {BaseUrl}",
            context.PrNumber, _http.BaseAddress);

        // Serialise using snake_case to match the Python Pydantic models.
        var payload = JsonSerializer.Serialize(context, SerializerOptions);

        var response = await _http.PostAsync(
            "/review",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Review service returned {Status}: {Error}", response.StatusCode, error);
            throw new HttpRequestException(
                $"Review service error {(int)response.StatusCode}: {error}");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        return ParseResponse(body);
    }

    // ── JSON options ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // ── Response parsing ──────────────────────────────────────────────────────

    /// <summary>
    /// Parses the merged review the service returns. The service uses
    /// snake_case keys (<c>file_reviews</c>, <c>agent_prompt</c>), which is why
    /// this does not reuse <see cref="ReviewJsonParser"/> — that one expects
    /// the camelCase schema the chat models are prompted to emit.
    /// </summary>
    private static CodeReview ParseResponse(string json)
    {
        var doc = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("Review service returned null JSON.");

        var summary = doc["summary"]?.GetValue<string>() ?? "No summary provided.";
        var fileReviews = new List<FileReview>();

        foreach (var fr in doc["file_reviews"]?.AsArray() ?? new JsonArray())
        {
            if (fr is null) continue;

            var findings = new List<Finding>();
            foreach (var f in fr["findings"]?.AsArray() ?? new JsonArray())
            {
                if (f is null) continue;

                var severity = Enum.TryParse<Severity>(
                    f["severity"]?.GetValue<string>(), ignoreCase: true, out var s)
                        ? s : Severity.Info;

                findings.Add(new Finding(
                    Severity: severity,
                    Description: f["description"]?.GetValue<string>() ?? "",
                    AgentPrompt: f["agent_prompt"]?.GetValue<string>() ?? "",
                    Location: f["location"]?.GetValue<string>()));
            }

            fileReviews.Add(new FileReview(
                fr["filename"]?.GetValue<string>() ?? "unknown",
                findings));
        }

        return new CodeReview(summary, fileReviews);
    }
}
