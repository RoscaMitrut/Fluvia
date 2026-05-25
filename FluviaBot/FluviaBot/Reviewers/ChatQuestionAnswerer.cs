using FluviaBot.Chat;
using FluviaBot.Models;
using FluviaBot.Prompts;
using Microsoft.Extensions.Logging;

namespace FluviaBot.Review;

/// <summary>
/// An <see cref="IPullRequestQuestionAnswerer"/> that works with any
/// single-call LLM provider. Like <see cref="ChatCodeReviewer"/>, it is a thin
/// composition over an <see cref="IChatClient"/>: build the Q&amp;A prompt,
/// send it, return the prose reply. Because it sits on the shared
/// <see cref="IChatClient"/> seam, Q&amp;A works for every chat provider.
/// </summary>
public sealed class ChatQuestionAnswerer : IPullRequestQuestionAnswerer
{
    private readonly IChatClient _chat;
    private readonly ILogger<ChatQuestionAnswerer> _logger;

    public ChatQuestionAnswerer(IChatClient chat, ILogger<ChatQuestionAnswerer> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    public async Task<string> AnswerAsync(
        PullRequestContext context,
        QuestionContext question,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "ChatQuestionAnswerer: answering question from {Asker} on PR #{Number} via {Provider}",
            question.Asker, context.PrNumber, _chat.ProviderName);

        var request = new ChatRequest(
            SystemPrompt: QuestionPromptBuilder.SystemPrompt,
            Messages: new[]
            {
                ChatMessage.User(QuestionPromptBuilder.BuildUserPrompt(context, question)),
            },
            MaxTokens: 2048,
            // Q&A is free-form Markdown prose, not JSON.
            JsonMode: false);

        var answer = await _chat.CompleteAsync(request, ct);

        if (string.IsNullOrWhiteSpace(answer))
        {
            _logger.LogError(
                "ChatQuestionAnswerer: {Provider} returned an empty answer.",
                _chat.ProviderName);
            return "I wasn't able to produce an answer — the model returned an " +
                   "empty response. Please try rephrasing the question.";
        }

        return answer.Trim();
    }
}
