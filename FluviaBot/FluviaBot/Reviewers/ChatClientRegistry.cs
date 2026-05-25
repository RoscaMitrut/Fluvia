using FluviaBot.Chat;
using FluviaBot.Chat.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FluviaBot.Review;

/// <summary>
/// The single place that turns a provider *name* into a built
/// <see cref="IChatClient"/>. Both the review and Q&amp;A flows resolve
/// through here, so they always support the exact same provider set. Adding a
/// provider is one entry in <see cref="ProviderBuilders"/>.
///
/// LangGraph is intentionally absent: it is not a chat client (see
/// <see cref="LangGraphCodeReviewer"/>) and is wired separately for the
/// review flow only.
/// </summary>
public static class ChatClientRegistry
{
    /// <summary>
    /// Builder per provider name. Each returns a fully-constructed chat client;
    /// <see cref="HttpClient"/> instances come from <see cref="IHttpClientFactory"/>
    /// for correct connection pooling. Declared before
    /// <see cref="SupportedProviders"/> so that initializer can read its keys.
    /// </summary>
    private static readonly Dictionary<string, Func<IServiceProvider, IChatClient>>
        ProviderBuilders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = sp => BuildOpenAiCompatible(sp, ChatProviderOptionsFactory.OpenAi),
            ["ollama"] = sp => BuildOpenAiCompatible(sp, ChatProviderOptionsFactory.Ollama),
            ["huggingface"] = sp => BuildOpenAiCompatible(sp, ChatProviderOptionsFactory.HuggingFace),
            ["anthropic"] = BuildAnthropic,
            ["google"] = BuildGoogle,
        };

    /// <summary>
    /// Provider names that resolve to an <see cref="IChatClient"/>. These work
    /// for both review and Q&amp;A. Compared case-insensitively.
    /// </summary>
    public static IReadOnlyCollection<string> SupportedProviders { get; } =
        ProviderBuilders.Keys.ToArray();

    /// <summary>True if <paramref name="provider"/> resolves to a chat client.</summary>
    public static bool IsSupported(string? provider)
        => !string.IsNullOrWhiteSpace(provider)
           && ProviderBuilders.ContainsKey(provider.Trim());

    /// <summary>
    /// Builds the <see cref="IChatClient"/> for <paramref name="provider"/>.
    /// Throws <see cref="InvalidOperationException"/> for an unknown name.
    /// </summary>
    public static IChatClient Resolve(IServiceProvider sp, string provider)
    {
        var key = provider.Trim();
        if (ProviderBuilders.TryGetValue(key, out var builder))
            return builder(sp);

        throw new InvalidOperationException(
            $"Unknown provider: '{provider}'. " +
            $"Valid providers: {string.Join(", ", SupportedProviders)}.");
    }

    // ── Builders ──────────────────────────────────────────────────────────────

    private static IChatClient BuildOpenAiCompatible(
        IServiceProvider sp,
        Func<IConfiguration, OpenAiCompatibleOptions> optionsFor)
    {
        var cfg = sp.GetRequiredService<IConfiguration>();
        var options = optionsFor(cfg);
        var http = sp.GetRequiredService<IHttpClientFactory>()
            .CreateClient($"chat:{options.ProviderName}");
        var logger = sp.GetRequiredService<ILogger<OpenAiCompatibleChatClient>>();

        return new OpenAiCompatibleChatClient(http, options, logger);
    }

    private static IChatClient BuildAnthropic(IServiceProvider sp)
    {
        var cfg = sp.GetRequiredService<IConfiguration>();
        var options = ChatProviderOptionsFactory.Anthropic(cfg);
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("chat:anthropic");
        var logger = sp.GetRequiredService<ILogger<AnthropicChatClient>>();

        return new AnthropicChatClient(http, options, logger);
    }

    private static IChatClient BuildGoogle(IServiceProvider sp)
    {
        var cfg = sp.GetRequiredService<IConfiguration>();
        var options = ChatProviderOptionsFactory.Google(cfg);
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("chat:google");
        var logger = sp.GetRequiredService<ILogger<GoogleChatClient>>();

        return new GoogleChatClient(http, options, logger);
    }
}
