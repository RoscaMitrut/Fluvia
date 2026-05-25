using FluviaBot.Chat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FluviaBot.Review;

/// <summary>
/// Resolves the <see cref="ICodeReviewer"/> and <see cref="IPullRequestQuestionAnswerer"/>
/// from configuration. Both flows share the same selection logic — read a
/// provider name, map it to an <see cref="IChatClient"/> via
/// <see cref="ChatClientRegistry"/>, and wrap it — so it lives here once.
///
/// Selection is case-insensitive and whitespace-trimmed:
///   - unset             → the NoOp implementation (with a warning).
///   - "langgraph"        → review only; Q&amp;A degrades to NoOp.
///   - any chat provider  → the chat-backed implementation.
///   - anything else      → throws; it is a configuration typo.
/// </summary>
public static class ProviderFactory
{
    private const string LangGraphProvider = "langgraph";

    /// <summary>
    /// Resolves the reviewer from <c>ReviewProvider</c>. LangGraph maps to its
    /// own pipeline; every chat provider maps to <see cref="ChatCodeReviewer"/>.
    /// </summary>
    public static ICodeReviewer ResolveReviewer(IServiceProvider sp)
        => Resolve<ICodeReviewer>(
            sp,
            configKeys: ["ReviewProvider"],
            flowName: "ReviewProvider",
            onUnset: () => new NoOpCodeReviewer(),
            onLangGraph: () => sp.GetRequiredService<LangGraphCodeReviewer>(),
            onChat: chat => new ChatCodeReviewer(
                chat, sp.GetRequiredService<ILogger<ChatCodeReviewer>>()),
            langGraphIsSupported: true);

    /// <summary>
    /// Resolves the question answerer. <c>QuestionProvider</c> takes priority
    /// and falls back to <c>ReviewProvider</c>, so one setting can drive both
    /// flows. LangGraph has no Q&amp;A form, so it degrades to NoOp.
    /// </summary>
    public static IPullRequestQuestionAnswerer ResolveQuestionAnswerer(IServiceProvider sp)
        => Resolve<IPullRequestQuestionAnswerer>(
            sp,
            configKeys: ["QuestionProvider", "ReviewProvider"],
            flowName: "QuestionProvider/ReviewProvider",
            onUnset: () => new NoOpQuestionAnswerer(),
            onLangGraph: () => new NoOpQuestionAnswerer(),
            onChat: chat => new ChatQuestionAnswerer(
                chat, sp.GetRequiredService<ILogger<ChatQuestionAnswerer>>()),
            langGraphIsSupported: false);

    /// <summary>
    /// Shared resolution: read the first non-empty key, branch on unset /
    /// langgraph / chat provider / typo.
    /// </summary>
    /// <param name="langGraphIsSupported">
    /// True for the review flow (LangGraph is a real reviewer); false for Q&amp;A,
    /// where it has no implementation and degrades to <paramref name="onLangGraph"/>.
    /// </param>
    private static T Resolve<T>(
        IServiceProvider sp,
        string[] configKeys,
        string flowName,
        Func<T> onUnset,
        Func<T> onLangGraph,
        Func<IChatClient, T> onChat,
        bool langGraphIsSupported)
    {
        var cfg = sp.GetRequiredService<IConfiguration>();
        var logger = sp.GetRequiredService<ILogger<Program>>();

        var provider = configKeys
            .Select(k => cfg[k]?.Trim())
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        if (string.IsNullOrWhiteSpace(provider))
        {
            logger.LogWarning("{Flow} is not set — using the NoOp implementation.", flowName);
            return onUnset();
        }

        if (provider.Equals(LangGraphProvider, StringComparison.OrdinalIgnoreCase))
        {
            if (!langGraphIsSupported)
                logger.LogWarning(
                    "Provider 'langgraph' is a review-only pipeline with no Q&A form. " +
                    "@fluvia questions will not be answered. Set QuestionProvider to one " +
                    "of: {Supported}.",
                    string.Join(", ", ChatClientRegistry.SupportedProviders));
            return onLangGraph();
        }

        if (ChatClientRegistry.IsSupported(provider))
            return onChat(ChatClientRegistry.Resolve(sp, provider));

        var valid = langGraphIsSupported
            ? $"{string.Join(", ", ChatClientRegistry.SupportedProviders)}, {LangGraphProvider}"
            : string.Join(", ", ChatClientRegistry.SupportedProviders);
        throw new InvalidOperationException(
            $"Unknown {flowName}: '{provider}'. Valid values: {valid}.");
    }
}
