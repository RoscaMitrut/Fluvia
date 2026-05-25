using Microsoft.Extensions.Configuration;

namespace FluviaBot.Chat;

/// <summary>
/// Builds the per-provider transport options from <see cref="IConfiguration"/>.
///
/// This centralises every <c>Provider__*</c> env-var lookup and its defaulting
/// rules in one place, so the chat clients themselves contain no configuration
/// logic — they just receive a fully-formed options record.
/// </summary>
public static class ChatProviderOptionsFactory
{
    // ── OpenAI-compatible providers ───────────────────────────────────────────

    /// <summary>
    /// Builds options for the OpenAI provider (<c>OpenAI__*</c> keys).
    /// </summary>
    public static OpenAiCompatibleOptions OpenAi(IConfiguration cfg) => new(
        ProviderName: "openai",
        BaseUrl: NonEmpty(cfg["OpenAI:BaseUrl"], "https://api.openai.com"),
        Model: Required(cfg["OpenAI:Model"], "OpenAI__Model"),
        ApiKey: Required(cfg["OpenAI:ApiKey"], "OpenAI__ApiKey"));

    /// <summary>
    /// Builds options for a local Ollama instance (<c>Ollama__*</c> keys).
    /// Ollama exposes an OpenAI-compatible endpoint and has no auth.
    /// </summary>
    public static OpenAiCompatibleOptions Ollama(IConfiguration cfg) => new(
        ProviderName: "ollama",
        BaseUrl: NonEmpty(cfg["Ollama:BaseUrl"], "http://localhost:11434"),
        Model: Required(cfg["Ollama:Model"], "Ollama__Model"),
        ApiKey: null);

    /// <summary>
    /// Builds options for the Hugging Face Inference router
    /// (<c>HuggingFace__*</c> keys).
    ///
    /// HuggingFace can pin an inference provider by prefixing the request
    /// path; "auto" (or unset) lets HF choose. It also cold-starts models, so
    /// 503 retries are enabled.
    /// </summary>
    public static OpenAiCompatibleOptions HuggingFace(IConfiguration cfg)
    {
        const string chatPath = "/v1/chat/completions";

        var provider = cfg["HuggingFace:Provider"]?.Trim();
        var path =
            string.IsNullOrWhiteSpace(provider) ||
            provider.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? chatPath
                : $"/{provider}{chatPath}";

        return new OpenAiCompatibleOptions(
            ProviderName: "huggingface",
            BaseUrl: NonEmpty(cfg["HuggingFace:BaseUrl"], "https://router.huggingface.co"),
            Model: Required(cfg["HuggingFace:Model"], "HuggingFace__Model"),
            ApiKey: Required(cfg["HuggingFace:ApiKey"], "HuggingFace__ApiKey"),
            ChatPath: path,
            ColdStartRetries: 3,
            ColdStartDelay: TimeSpan.FromSeconds(10));
    }

    // ── Bespoke providers ─────────────────────────────────────────────────────

    /// <summary>Builds options for the Anthropic Messages API.</summary>
    public static AnthropicOptions Anthropic(IConfiguration cfg) => new(
        BaseUrl: NonEmpty(cfg["Anthropic:BaseUrl"], "https://api.anthropic.com"),
        Model: Required(cfg["Anthropic:Model"], "Anthropic__Model"),
        ApiKey: Required(cfg["Anthropic:ApiKey"], "Anthropic__ApiKey"));

    /// <summary>Builds options for the Google Gemini API.</summary>
    public static GoogleOptions Google(IConfiguration cfg) => new(
        BaseUrl: NonEmpty(cfg["Google:BaseUrl"], "https://generativelanguage.googleapis.com"),
        Model: Required(cfg["Google:Model"], "Google__Model"),
        ApiKey: Required(cfg["Google:ApiKey"], "Google__ApiKey"));

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns <paramref name="value"/> or throws if it is missing.</summary>
    private static string Required(string? value, string envVarName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{envVarName} is not set.")
            : value.Trim();

    /// <summary>Returns <paramref name="value"/> trimmed, or the fallback if blank.</summary>
    private static string NonEmpty(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
