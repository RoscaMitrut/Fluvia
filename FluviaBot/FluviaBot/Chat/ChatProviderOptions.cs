namespace FluviaBot.Chat;

/// Transport configuration for any provider that exposes an OpenAI-compatible
/// <c>/v1/chat/completions</c> endpoint. 
///
/// To add a new OpenAI-compatible provider (Groq, Together, OpenRouter,
/// Mistral, DeepSeek, …) you add one of these — no new class.
/// <param name="ProviderName">Short id for logging, e.g. "ollama".</param>
/// <param name="BaseUrl">Scheme + host (+ optional port), no trailing slash.</param>
/// <param name="Model">Model identifier sent in the request body.</param>
/// <param name="ApiKey">
/// API key / token sent as <c>Authorization: Bearer</c>. Null for providers
/// with no auth (e.g. a local Ollama instance), in which case no auth header
/// is sent.
/// </param>
/// <param name="ChatPath">
/// Path appended to <paramref name="BaseUrl"/> for the chat endpoint.
/// Defaults to the OpenAI-standard path; HuggingFace's provider-pinning
/// (e.g. "/together/v1/chat/completions") is expressed by overriding this.
/// </param>
/// <param name="ColdStartRetries">
/// Number of times to retry on HTTP 503. HuggingFace cold-starts a model on
/// first request and returns 503 meanwhile; other providers leave this at 0.
/// </param>
/// <param name="ColdStartDelay">Delay between cold-start retries.</param>
/// <param name="Timeout">
/// Per-request timeout. Local and free-tier models can be slow, so this
/// defaults generously.
/// </param>
public sealed record OpenAiCompatibleOptions(
    string ProviderName,
    string BaseUrl,
    string Model,
    string? ApiKey,
    string ChatPath = "/v1/chat/completions",
    int ColdStartRetries = 0,
    TimeSpan? ColdStartDelay = null,
    TimeSpan? Timeout = null)
{
    /// <summary>Fully-qualified chat endpoint URL.</summary>
    public string ChatUrl => BaseUrl.TrimEnd('/') + ChatPath;

    /// <summary>True if outgoing requests carry a Bearer auth header.</summary>
    public bool UsesBearerAuth => !string.IsNullOrWhiteSpace(ApiKey);

    public TimeSpan EffectiveColdStartDelay => ColdStartDelay ?? TimeSpan.FromSeconds(10);
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes(10);
}

/// Transport configuration for the Anthropic Messages API. Anthropic does not
/// use the OpenAI request/response shape, so it has its own client and its
/// own options.
public sealed record AnthropicOptions(
    string BaseUrl,
    string Model,
    string ApiKey,
    string AnthropicVersion = "2023-06-01")
{
    public string MessagesUrl => BaseUrl.TrimEnd('/') + "/v1/messages";
}

/// Transport configuration for the Google Gemini (Generative Language) API.
/// Gemini encodes the model in the URL path and uses an
/// <c>x-goog-api-key</c> header, so it too has its own client.
public sealed record GoogleOptions(
    string BaseUrl,
    string Model,
    string ApiKey)
{
    /// <summary>The <c>:generateContent</c> URL for the configured model.</summary>
    public string GenerateContentUrl =>
        $"{BaseUrl.TrimEnd('/')}/v1beta/models/{Model}:generateContent";
}
