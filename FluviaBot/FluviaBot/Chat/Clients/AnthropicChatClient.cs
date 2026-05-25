using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluviaBot.Chat;
using Microsoft.Extensions.Logging;

namespace FluviaBot.Chat.Clients;

/// <see cref="IChatClient"/> for the Anthropic Messages API. Anthropic does not
/// use the OpenAI wire shape — the system prompt is a top-level <c>system</c>
/// field, auth is the <c>x-api-key</c> header, and the reply is a
/// <c>content</c> array of typed blocks — so it gets its own client.
public sealed class AnthropicChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly AnthropicOptions _options;
    private readonly ILogger<AnthropicChatClient> _logger;

    public AnthropicChatClient(
        HttpClient http,
        AnthropicOptions options,
        ILogger<AnthropicChatClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;

        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string ProviderName => "anthropic";

    public async Task<string> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            model = _options.Model,
            max_tokens = request.MaxTokens,
            system = request.SystemPrompt ?? string.Empty,
            messages = request.Messages
                .Select(m => new { role = RoleString(m.Role), content = m.Content })
                .ToArray(),
        };

        var json = JsonSerializer.Serialize(payload);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.MessagesUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("x-api-key", _options.ApiKey);
        httpRequest.Headers.Add("anthropic-version", _options.AnthropicVersion);

        var response = await _http.SendAsync(httpRequest, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Anthropic API error (HTTP {(int)response.StatusCode}): {errorBody}");
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        var text = ExtractText(body);
        if (text is null)
        {
            _logger.LogError(
                "AnthropicChatClient: no text content block in response. Raw body:\n{Body}",
                body);
            throw new InvalidOperationException(
                "Unexpected Anthropic response shape — no text content block.");
        }

        return text;
    }

    /// Maps roles to Anthropic's wire values. <see cref="ChatRole.System"/>
    /// never appears here — the system prompt is sent out-of-band.
    private static string RoleString(ChatRole role) => role switch
    {
        ChatRole.Assistant => "assistant",
        _ => "user",
    };

    /// Pulls the first <c>text</c> block out of the response <c>content</c>
    /// array. The array can hold non-text blocks (e.g. <c>tool_use</c>), so we
    /// scan rather than blindly indexing [0] (#12).
    private static string? ExtractText(string body)
    {
        var content = JsonNode.Parse(body)?["content"]?.AsArray();
        if (content is null)
            return null;

        foreach (var block in content)
        {
            if (block?["type"]?.GetValue<string>() == "text")
                return block["text"]?.GetValue<string>();
        }

        return null;
    }
}
