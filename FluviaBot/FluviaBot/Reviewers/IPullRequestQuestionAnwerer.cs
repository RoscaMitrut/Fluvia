using FluviaBot.Models;

namespace FluviaBot.Review;

/// <summary>
/// Answers a free-form question a user asked in a PR comment (e.g.
/// "@fluvia why is the issue on line 67 a security risk?").
///
/// This is deliberately separate from <see cref="ICodeReviewer"/>: a review
/// produces a structured <see cref="CodeReview"/>, whereas a question gets a
/// plain prose answer. They share providers and configuration but not output.
/// </summary>
public interface IPullRequestQuestionAnswerer
{
    /// <summary>
    /// Produce an answer to <paramref name="question"/> using the supplied PR
    /// context. Implementations should be self-contained and must NOT post to
    /// GitHub directly — the caller handles posting.
    /// </summary>
    Task<string> AnswerAsync(
        PullRequestContext context,
        QuestionContext question,
        CancellationToken ct = default);
}

/// <summary>
/// Everything the answerer needs about the question being asked.
/// </summary>
/// <param name="QuestionBody">
/// The user's comment text, with the "/fluvia" mention already stripped.
/// </param>
/// <param name="Asker">GitHub login of the user who asked, for a personal reply.</param>
/// <param name="PriorReview">
/// The most recent Fluvia review on this PR, if one exists. Gives the answerer
/// the findings it previously reported so it can explain them. May be null.
/// </param>
public record QuestionContext(
    string QuestionBody,
    string Asker,
    string? PriorReview
);

/// <summary>
/// Fallback used when no AI provider is configured for Q&amp;A.
/// </summary>
public sealed class NoOpQuestionAnswerer : IPullRequestQuestionAnswerer
{
    public Task<string> AnswerAsync(
        PullRequestContext context,
        QuestionContext question,
        CancellationToken ct = default)
        => Task.FromResult(
            "No AI provider is configured, so I can't answer questions right now.");
}