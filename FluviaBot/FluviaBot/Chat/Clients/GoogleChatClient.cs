using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluviaBot.Chat;
using Microsoft.Extensions.Logging;

namespace FluviaBot.Chat.Clients;

/// <see cref="IChatClient"/> for the Google Gemini API. Gemini has its own wire
/// format — the model is in the URL path, auth is the <c>x-goog-api-key</c>
/// header, the system prompt is a <c>systemInstruction</c> object, and turns
/// live under a <c>contents</c> array — so, like Anthropic, it gets a
/// dedicated client.
public sealed class GoogleChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly GoogleOptions _options;
    private readonly ILogger<GoogleChatClient> _logger;

    public GoogleChatClient(
        HttpClient http,
        GoogleOptions options,
        ILogger<GoogleChatClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;

        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string ProviderName => "google";

    public async Task<string> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            systemInstruction = string.IsNullOrWhiteSpace(request.SystemPrompt)
                ? null
                : new { parts = new[] { new { text = request.SystemPrompt } } },
            contents = request.Messages
                .Select(m => new
                {
                    role = RoleString(m.Role),
                    parts = new[] { new { text = m.Content } },
                })
                .ToArray(),
            generationConfig = new
            {
                maxOutputTokens = request.MaxTokens,
                // Gemini supports a real JSON mode via responseMimeType.
                responseMimeType = request.JsonMode ? "application/json" : "text/plain",
            },
        };

        // WhenWritingNull keeps a null systemInstruction out of the payload
        // rather than sending an explicit null the API rejects.
        var json = JsonSerializer.Serialize(payload, SerializerOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.GenerateContentUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("x-goog-api-key", _options.ApiKey);

        var response = await _http.SendAsync(httpRequest, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Google Gemini API error (HTTP {(int)response.StatusCode}): {errorBody}");
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        var text = ExtractText(body);
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogError(
                "GoogleChatClient: no text content in response. Raw body:\n{Body}", body);
            throw new InvalidOperationException(
                "Unexpected Gemini response shape — no candidate text " +
                "(the request may have been blocked by a safety filter).");
        }

        return text;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// Gemini uses <c>"model"</c> for the assistant and <c>"user"</c> for the
    /// human. There is no system role in <c>contents</c> — see the payload.
    private static string RoleString(ChatRole role) => role switch
    {
        ChatRole.Assistant => "model",
        _ => "user",
    };

    /// Pulls the answer text from a <c>generateContent</c> response. The shape
    /// is <c>candidates[0].content.parts[*].text</c>; parts are concatenated
    /// because a long answer can be split across several text parts.
    private static string? ExtractText(string body)
    {
        var parts = JsonNode.Parse(body)
            ?["candidates"]?[0]
            ?["content"]
            ?["parts"]?.AsArray();

        if (parts is null)
            return null;

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            var text = part?["text"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text))
                sb.Append(text);
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }
}
