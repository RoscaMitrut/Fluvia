using FluviaBot.Chat;
using FluviaBot.Models;
using FluviaBot.Prompts;
using Microsoft.Extensions.Logging;

namespace FluviaBot.Review;

/// <summary>
/// An <see cref="ICodeReviewer"/> that works with any single-call LLM
/// provider. It does three things and nothing else:
///   1. builds the prompt with <see cref="PromptBuilder"/>,
///   2. sends it through an <see cref="IChatClient"/>,
///   3. parses the reply with <see cref="ReviewJsonParser"/>.
///
/// All provider-specific behaviour (URLs, auth, retries, wire format) lives
/// in the injected <see cref="IChatClient"/>, so adding a provider does not
/// mean adding a reviewer.
///
/// LangGraph is the deliberate exception: it is a multi-model fan-out/merge
/// pipeline, not a single chat call, so it keeps its own
/// <see cref="LangGraphCodeReviewer"/>.
/// </summary>
public sealed class ChatCodeReviewer : ICodeReviewer
{
    private readonly IChatClient _chat;
    private readonly ILogger<ChatCodeReviewer> _logger;

    public ChatCodeReviewer(IChatClient chat, ILogger<ChatCodeReviewer> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    /// <summary>
    /// System prompt for the structured-review task. Shared by every provider
    /// so the JSON contract is defined once.
    /// </summary>
    private const string SystemPrompt = """
        You are an expert code reviewer. Your job is to analyse pull request
        diffs and return a structured JSON review. Be concise but precise.

        For each finding, include an "agentPrompt" — a clear, self-contained
        prompt a developer could paste directly into an AI coding agent (like
        Claude Code or Copilot) to fix the issue. The prompt should name the
        file, describe the problem, and specify the desired outcome.

        Respond ONLY with valid JSON. No markdown fences, no preamble.
        Use this exact schema:
        {
          "summary": "string — 2-4 sentence overall assessment",
          "fileReviews": [
            {
              "filename": "string",
              "findings": [
                {
                  "severity": "Info" | "Warning" | "Error",
                  "description": "string — what the problem is and why it matters",
                  "agentPrompt": "string — ready-to-use prompt for an AI agent",
                  "location": "string | null — e.g. L42 or L10-L18"
                }
              ]
            }
          ]
        }
        """;

    public async Task<CodeReview> ReviewAsync(
        PullRequestContext context, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "ChatCodeReviewer: reviewing PR #{Number} via {Provider}",
            context.PrNumber, _chat.ProviderName);

        var request = new ChatRequest(
            SystemPrompt: SystemPrompt,
            Messages: new[] { ChatMessage.User(PromptBuilder.Build(context)) },
            MaxTokens: 4096,
            // Ask for JSON mode; ReviewJsonParser still tolerates prose/fences
            // from providers that ignore the hint.
            JsonMode: true);

        string raw;
        try
        {
            raw = await _chat.CompleteAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A transport / shape failure becomes a degraded review rather
            // than crashing the webhook — consistent with parse failures.
            _logger.LogError(ex,
                "ChatCodeReviewer: {Provider} call failed for PR #{Number}.",
                _chat.ProviderName, context.PrNumber);
            return new CodeReview(
                $"Review could not be produced — the {_chat.ProviderName} request " +
                $"failed: {ex.Message}",
                Array.Empty<FileReview>());
        }

        return ReviewJsonParser.Parse(raw, _logger, _chat.ProviderName);
    }
}
