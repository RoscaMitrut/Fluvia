using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.PullRequest;
using FluviaBot.Services;

namespace FluviaBot.Webhooks;

/// <summary>
/// The application's single <see cref="WebhookEventProcessor"/> — the
/// Octokit.Webhooks middleware supports only one, so all event handling lives
/// here. Handles two events:
///   - pull_request (opened / synchronize) → runs a full code review
///   - issue_comment (created)             → answers /fluvia mentions
///
/// Signature validation (#16): MapGitHubWebhooks verifies X-Hub-Signature-256
/// before dispatching here and rejects bad payloads with a 4xx. That rejection
/// never reaches this processor, so validation *failures* cannot be logged
/// here — request logging on the webhook path captures those.
/// </summary>
public sealed class WebhookProcessor : WebhookEventProcessor
{
    private readonly PullRequestReviewService _reviewService;
    private readonly CommentQuestionService _questionService;
    private readonly ILogger<WebhookProcessor> _logger;

    public WebhookProcessor(
        PullRequestReviewService reviewService,
        CommentQuestionService questionService,
        ILogger<WebhookProcessor> logger)
    {
        _reviewService = reviewService;
        _questionService = questionService;
        _logger = logger;
    }

    // ── Fluvia code review (pull_request events) ──────────────────────────────

    protected override async ValueTask ProcessPullRequestWebhookAsync(
        WebhookHeaders headers,
        PullRequestEvent payload,
        PullRequestAction action,
        CancellationToken cancellationToken = default)
    {
        // Reaching this method means the signature was validated — log it so
        // a successful delivery is always visible (#16).
        _logger.LogInformation(
            "Validated pull_request webhook received: action={Action}", action);

        // Review on open and on new commits pushed to the PR.
        if (action != PullRequestAction.Opened &&
            action != PullRequestAction.Synchronize)
        {
            _logger.LogDebug(
                "PR #{Number} action '{Action}' is not reviewable — ignoring.",
                payload.PullRequest.Number, action);
            return;
        }

        _logger.LogInformation(
            "PR #{Number} {Action} — starting review.",
            payload.PullRequest.Number, action);

        var installationId = payload.Installation?.Id
            ?? throw new InvalidOperationException("No installation ID in payload.");

        try
        {
            await _reviewService.ReviewAndCommentAsync(
                owner: payload.Repository!.Owner.Login,
                repo: payload.Repository.Name,
                prNumber: (int)payload.PullRequest.Number,
                installationId: installationId,
                ct: cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Review failed for PR #{Number} in {Owner}/{Repo}.",
                payload.PullRequest.Number,
                payload.Repository?.Owner.Login,
                payload.Repository?.Name);
            throw;
        }
    }

    // ── Fluvia Q&A (issue_comment events) ─────────────────────────────────────

    protected override async ValueTask ProcessIssueCommentWebhookAsync(
        WebhookHeaders headers,
        IssueCommentEvent payload,
        IssueCommentAction action,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Validated issue_comment webhook received: action={Action}", action);

        // Only respond to brand-new comments — not edits or deletions.
        if (action != IssueCommentAction.Created)
        {
            _logger.LogDebug(
                "issue_comment action '{Action}' is not actionable — ignoring.", action);
            return;
        }

        var issue = payload.Issue;

        // issue_comment fires for both issues and PRs. Only PRs have a
        // PullRequest sub-object; plain issues have no diff to reason about.
        if (issue.PullRequest is null)
        {
            _logger.LogDebug(
                "Comment on issue #{Number} is not on a pull request — ignoring.",
                issue.Number);
            return;
        }

        var commentBody = payload.Comment.Body;

        // CRITICAL loop guard: never answer our own comments. Otherwise the
        // bot sees its own reply, treats the "@fluvia" in it as a question,
        // and replies to itself indefinitely.
        if (IsFromBot(payload))
        {
            _logger.LogDebug(
                "Comment on PR #{Number} is from the bot itself — ignoring.",
                issue.Number);
            return;
        }

        // Only act on comments that actually mention the bot.
        if (!CommentQuestionService.MentionsBot(commentBody))
        {
            _logger.LogDebug(
                "Comment on PR #{Number} does not mention {Mention} — ignoring.",
                issue.Number, CommentQuestionService.Mention);
            return;
        }

        _logger.LogInformation(
            "PR #{Number}: /fluvia mention from {Asker} — answering.",
            issue.Number, payload.Comment.User.Login);

        var installationId = payload.Installation?.Id
            ?? throw new InvalidOperationException("No installation ID in payload.");

        try
        {
            await _questionService.AnswerQuestionAsync(
                owner: payload.Repository!.Owner.Login,
                repo: payload.Repository.Name,
                prNumber: (int)issue.Number,
                commentBody: commentBody,
                asker: payload.Comment.User.Login,
                installationId: installationId,
                ct: cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to answer @fluvia comment on PR #{Number} in {Owner}/{Repo}.",
                issue.Number,
                payload.Repository?.Owner.Login,
                payload.Repository?.Name);
            throw;
        }
    }

    /// <summary>
    /// True if the comment was authored by a bot account (including this app's
    /// own installation account). This is the loop-prevention guard.
    /// </summary>
    private static bool IsFromBot(IssueCommentEvent payload)
    {
        var user = payload.Comment.User;

        // GitHub App comments are authored by a "Bot"-type account; App
        // installation accounts also carry a "[bot]" login suffix.
        return string.Equals(user.Type?.ToString(), "Bot", StringComparison.OrdinalIgnoreCase)
               || (user.Login?.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}