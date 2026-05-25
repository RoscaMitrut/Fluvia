using FluviaBot.Models;

namespace FluviaBot.Review;

/// <summary>
/// Core abstraction. Implement this to add a new AI provider, a static
/// analysis tool, a multi-step pipeline, or any combination.
/// </summary>
public interface ICodeReviewer
{
    /// <summary>
    /// Analyse the pull request and return a structured review.
    /// Implementations should be self-contained and not post to GitHub directly.
    /// </summary>
    Task<CodeReview> ReviewAsync(PullRequestContext context, CancellationToken ct = default);
}

/// <summary>
/// Fallback reviewer used when no AI provider is configured.
/// </summary>
public sealed class NoOpCodeReviewer : ICodeReviewer
{
    public Task<CodeReview> ReviewAsync(PullRequestContext context, CancellationToken ct = default)
        => Task.FromResult(new CodeReview("No AI reviewer configured.", []));
}