using System.Text;
using FluviaBot.Models;
using FluviaBot.Review;
using Octokit;

namespace FluviaBot.Services;

/// <summary>
/// Orchestrates a pull request review end-to-end:
///   1. Fetches full context (PR metadata, diffs, file contents) from GitHub
///   2. Passes it to the configured ICodeReviewer
///   3. Formats the result and posts (or updates) it as a PR comment
///
/// Context fetching is delegated to <see cref="PullRequestContextFetcher"/> so
/// the Q&amp;A flow can reuse it.
/// </summary>
public sealed class PullRequestReviewService
{
    /// <summary>
    /// Hidden HTML marker embedded in every Fluvia review comment. Lets us find
    /// a previous review comment and update it in place rather than posting a
    /// fresh comment on every push to the PR branch (#8).
    /// </summary>
    public const string CommentMarker = "<!-- fluvia-code-review -->";

    private readonly ICodeReviewer _reviewer;
    private readonly GitHubAppTokenProvider _tokenProvider;
    private readonly PullRequestContextFetcher _contextFetcher;
    private readonly ILogger<PullRequestReviewService> _logger;

    public PullRequestReviewService(
        ICodeReviewer reviewer,
        GitHubAppTokenProvider tokenProvider,
        PullRequestContextFetcher contextFetcher,
        ILogger<PullRequestReviewService> logger)
    {
        _reviewer = reviewer;
        _tokenProvider = tokenProvider;
        _contextFetcher = contextFetcher;
        _logger = logger;
    }

    public async Task ReviewAndCommentAsync(
        string owner,
        string repo,
        int prNumber,
        long installationId,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Starting review for {Owner}/{Repo} PR #{Number}", owner, repo, prNumber);

        var octokit = await _tokenProvider.GetInstallationClientAsync(installationId);

        // ── 1. Fetch full PR context (metadata, diffs, file contents) ────────
        var context = await _contextFetcher.FetchAsync(octokit, owner, repo, prNumber, ct);

        // ── 2. Run the reviewer ───────────────────────────────────────────────
        var review = await _reviewer.ReviewAsync(context, ct);

        // ── 3. Format and post (or update) the comment ───────────────────────
        var comment = FormatComment(review, prNumber);
        await PostOrUpdateCommentAsync(octokit, owner, repo, prNumber, comment, ct);
    }

    // ── Comment posting ───────────────────────────────────────────────────────

    /// <summary>
    /// Posts the review as a PR comment, or — if a previous Fluvia comment
    /// already exists — updates that comment in place. This keeps a busy PR
    /// from accumulating a long trail of stale review comments (#8).
    /// </summary>
    private async Task PostOrUpdateCommentAsync(
        IGitHubClient octokit,
        string owner,
        string repo,
        int prNumber,
        string comment,
        CancellationToken ct)
    {
        // A PR and its backing issue share the same number.
        var existing = await GitHubRetry.ExecuteAsync(
            () => octokit.Issue.Comment.GetAllForIssue(owner, repo, prNumber),
            _logger, "Issue.Comment.GetAllForIssue", ct);

        var previous = existing.FirstOrDefault(
            c => c.Body is not null && c.Body.Contains(CommentMarker, StringComparison.Ordinal));

        if (previous is not null)
        {
            await GitHubRetry.ExecuteAsync(
                () => octokit.Issue.Comment.Update(owner, repo, previous.Id, comment),
                _logger, "Issue.Comment.Update", ct);

            _logger.LogInformation(
                "Updated existing review comment {CommentId} for PR #{Number}",
                previous.Id, prNumber);
        }
        else
        {
            await GitHubRetry.ExecuteAsync(
                () => octokit.Issue.Comment.Create(owner, repo, prNumber, comment),
                _logger, "Issue.Comment.Create", ct);

            _logger.LogInformation("Posted review comment for PR #{Number}", prNumber);
        }
    }

    // ── Comment formatting ────────────────────────────────────────────────────

    private static string FormatComment(CodeReview review, int prNumber)
    {
        var sb = new StringBuilder();

        // Hidden marker so a later run can find and update this comment (#8).
        sb.AppendLine(CommentMarker);
        sb.AppendLine("## Fluvia Code Review");
        sb.AppendLine();
        sb.AppendLine(review.Summary);

        if (review.FileReviews.Count == 0 ||
            review.FileReviews.All(f => f.Findings.Count == 0))
        {
            sb.AppendLine();
            sb.AppendLine("✅ No issues found.");
            return sb.ToString();
        }

        foreach (var fileReview in review.FileReviews.Where(f => f.Findings.Count > 0))
        {
            sb.AppendLine();
            sb.AppendLine($"### 📄 `{fileReview.Filename}`");

            foreach (var finding in fileReview.Findings)
            {
                var icon = finding.Severity switch
                {
                    Severity.Error => "🔴",
                    Severity.Warning => "🟡",
                    _ => "🔵",
                };

                var location = finding.Location is not null ? $" _{finding.Location}_" : "";
                sb.AppendLine();
                sb.AppendLine($"{icon} **{finding.Severity}**{location}");
                sb.AppendLine(finding.Description);
                sb.AppendLine();
                sb.AppendLine("<details>");
                sb.AppendLine("<summary>💬 Agent prompt to fix this</summary>");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(finding.AgentPrompt);
                sb.AppendLine("```");
                sb.AppendLine("</details>");
            }
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"_Generated by Fluvia Code Review bot for PR #{prNumber}_");

        return sb.ToString();
    }
}