using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluviaBot.Chat;
using Microsoft.Extensions.Logging;

namespace FluviaBot.Chat.Clients;

/// One <see cref="IChatClient"/> for every provider that speaks the
/// OpenAI-compatible <c>/v1/chat/completions</c> protocol — currently OpenAI,
/// Ollama, and Hugging Face. It deals only in transport; prompt construction
/// and JSON parsing live in the review / Q&amp;A layers above it.
public sealed class OpenAiCompatibleChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly OpenAiCompatibleOptions _options;
    private readonly ILogger<OpenAiCompatibleChatClient> _logger;

    public OpenAiCompatibleChatClient(
        HttpClient http,
        OpenAiCompatibleOptions options,
        ILogger<OpenAiCompatibleChatClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;

        _http.Timeout = options.EffectiveTimeout;
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string ProviderName => _options.ProviderName;

    public async Task<string> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var body = BuildBody(request);
        var json = JsonSerializer.Serialize(body);

        var responseBody = await PostWithColdStartRetryAsync(json, ct);

        var text = JsonNode.Parse(responseBody)
            ?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();

        if (text is null)
        {
            _logger.LogError(
                "{Provider}: no message content in response. Raw body:\n{Body}",
                ProviderName, responseBody);
            throw new InvalidOperationException(
                $"Unexpected {ProviderName} response shape — no choices[0].message.content.");
        }

        return text;
    }

    // ── Request body ──────────────────────────────────────────────────────────

    /// Builds the OpenAI-style request body. The system prompt becomes a
    /// leading <c>system</c>-role turn, which is how OpenAI-compatible
    /// providers expect it.
    private object BuildBody(ChatRequest request)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });

        foreach (var m in request.Messages)
            messages.Add(new { role = RoleString(m.Role), content = m.Content });

        // response_format only matters in JSON mode. Not every model honours
        // it (notably some Ollama models), so callers must still tolerate
        // prose-wrapped JSON — but for models that do, it skips the fallback.
        if (request.JsonMode)
        {
            return new
            {
                model = _options.Model,
                max_tokens = request.MaxTokens,
                stream = false,
                response_format = new { type = "json_object" },
                messages,
            };
        }

        return new
        {
            model = _options.Model,
            max_tokens = request.MaxTokens,
            stream = false,
            messages,
        };
    }

    private static string RoleString(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => "user",
    };

    // ── Transport ─────────────────────────────────────────────────────────────

    /// POSTs the payload, retrying on HTTP 503 up to
    /// <see cref="OpenAiCompatibleOptions.ColdStartRetries"/> times — Hugging
    /// Face returns 503 while a model loads. Providers that do not cold-start
    /// configure 0 retries, collapsing this to a single attempt.
    ///
    /// Each attempt builds a fresh <see cref="HttpRequestMessage"/> so the auth
    /// header is request-scoped, never mutating the pooled HttpClient.
    private async Task<string> PostWithColdStartRetryAsync(string json, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.ChatUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            if (_options.UsesBearerAuth)
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            var response = await _http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(ct);

            var errorBody = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable &&
                attempt <= _options.ColdStartRetries)
            {
                _logger.LogWarning(
                    "{Provider} model still loading (HTTP 503), attempt {Attempt}/{Max}. " +
                    "Retrying in {Delay}s…",
                    ProviderName, attempt, _options.ColdStartRetries,
                    _options.EffectiveColdStartDelay.TotalSeconds);

                await Task.Delay(_options.EffectiveColdStartDelay, ct);
                continue;
            }

            throw new HttpRequestException(
                $"{ProviderName} API error (HTTP {(int)response.StatusCode}): {errorBody}");
        }
    }
}
