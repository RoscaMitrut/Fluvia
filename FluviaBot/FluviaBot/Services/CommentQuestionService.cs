using FluviaBot.Models;
using FluviaBot.Review;
using Octokit;

namespace FluviaBot.Services;

/// <summary>
/// Handles a user question posted as a PR comment that mentions the bot
/// (e.g. "@fluvia why is the issue on line 67 a security risk?").
///
/// Flow:
///   1. Build full PR context (reusing PullRequestContextFetcher)
///   2. Find the bot's prior review comment, if any, to give the AI context
///   3. Ask the configured IPullRequestQuestionAnswerer
///   4. Post the answer as a reply comment on the PR thread
/// </summary>
public sealed class CommentQuestionService
{
    /// <summary>
    /// The mention that triggers a Q&amp;A response. Matched case-insensitively.
    /// </summary>
    public const string Mention = "/fluvia";

    /// <summary>
    /// Hidden marker embedded in every Q&amp;A reply. Distinct from the review
    /// comment marker so the two comment types are never confused — in
    /// particular, the review-comment lookup must not pick up an answer.
    /// </summary>
    public const string AnswerMarker = "<!-- fluvia-answer -->";

    private readonly IPullRequestQuestionAnswerer _answerer;
    private readonly GitHubAppTokenProvider _tokenProvider;
    private readonly PullRequestContextFetcher _contextFetcher;
    private readonly ILogger<CommentQuestionService> _logger;

    public CommentQuestionService(
        IPullRequestQuestionAnswerer answerer,
        GitHubAppTokenProvider tokenProvider,
        PullRequestContextFetcher contextFetcher,
        ILogger<CommentQuestionService> logger)
    {
        _answerer = answerer;
        _tokenProvider = tokenProvider;
        _contextFetcher = contextFetcher;
        _logger = logger;
    }

    /// <summary>
    /// Returns true if a comment body mentions the bot and should be answered.
    /// </summary>
    public static bool MentionsBot(string? commentBody)
        => !string.IsNullOrWhiteSpace(commentBody)
           && commentBody.Contains(Mention, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Strips the "/fluvia" mention out of the comment so the model receives
    /// just the question.
    /// </summary>
    private static string StripMention(string commentBody)
    {
        // Replace every case-variant of the mention with nothing, then tidy
        // up any doubled whitespace that leaves behind.
        var withoutMention = System.Text.RegularExpressions.Regex.Replace(
            commentBody, System.Text.RegularExpressions.Regex.Escape(Mention),
            string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return System.Text.RegularExpressions.Regex
            .Replace(withoutMention, @"\s{2,}", " ")
            .Trim();
    }

    /// <summary>
    /// Answers a single @fluvia question and posts the reply to the PR.
    /// </summary>
    /// <param name="owner">Repository owner.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="prNumber">PR / issue number the comment was posted on.</param>
    /// <param name="commentBody">The raw comment text, including "@fluvia".</param>
    /// <param name="asker">GitHub login of the user who asked.</param>
    /// <param name="installationId">GitHub App installation ID.</param>
    public async Task AnswerQuestionAsync(
        string owner,
        string repo,
        int prNumber,
        string commentBody,
        string asker,
        long installationId,
        CancellationToken ct = default)
    {
        var question = StripMention(commentBody);

        if (string.IsNullOrWhiteSpace(question))
        {
            _logger.LogInformation(
                "PR #{Number}: /fluvia mention with no question text — ignoring.",
                prNumber);
            return;
        }

        _logger.LogInformation(
            "Answering @fluvia question from {Asker} on {Owner}/{Repo} PR #{Number}",
            asker, owner, repo, prNumber);

        var octokit = await _tokenProvider.GetInstallationClientAsync(installationId);

        // ── 1. Full PR context ───────────────────────────────────────────────
        var context = await _contextFetcher.FetchAsync(octokit, owner, repo, prNumber, ct);

        // ── 2. Prior review, for grounding ───────────────────────────────────
        var priorReview = await TryGetPriorReviewAsync(octokit, owner, repo, prNumber, ct);

        // ── 3. Ask the model ─────────────────────────────────────────────────
        string answer;
        try
        {
            answer = await _answerer.AnswerAsync(
                context,
                new QuestionContext(QuestionBody: question, Asker: asker, PriorReview: priorReview),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed answer should leave a visible, honest reply rather than
            // silently dropping the user's question.
            _logger.LogError(ex,
                "Failed to answer @fluvia question on PR #{Number}.", prNumber);
            answer =
                "I ran into an error while trying to answer this. " +
                "Please try again, and if it keeps failing let a maintainer know.";
        }

        // ── 4. Post the reply ────────────────────────────────────────────────
        var reply = FormatAnswer(answer, asker);
        await GitHubRetry.ExecuteAsync(
            () => octokit.Issue.Comment.Create(owner, repo, prNumber, reply),
            _logger, "Issue.Comment.Create (answer)", ct);

        _logger.LogInformation("Posted @fluvia answer on PR #{Number}.", prNumber);
    }

    /// <summary>
    /// Finds the most recent Fluvia review comment on the PR so the answerer
    /// can explain its own previous findings. Returns null if none exists.
    /// </summary>
    private async Task<string?> TryGetPriorReviewAsync(
        IGitHubClient octokit,
        string owner,
        string repo,
        int prNumber,
        CancellationToken ct)
    {
        try
        {
            var comments = await GitHubRetry.ExecuteAsync(
                () => octokit.Issue.Comment.GetAllForIssue(owner, repo, prNumber),
                _logger, "Issue.Comment.GetAllForIssue (prior review)", ct);

            // Most recent review comment wins. Match the review marker, and
            // explicitly exclude our own answers so we never feed a previous
            // answer back in as if it were a review.
            var review = comments
                .Where(c => c.Body is not null
                            && c.Body.Contains(
                                PullRequestReviewService.CommentMarker, StringComparison.Ordinal)
                            && !c.Body.Contains(AnswerMarker, StringComparison.Ordinal))
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefault();

            return review?.Body;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Prior review is a nice-to-have — never fail the answer over it.
            _logger.LogWarning(ex,
                "Could not fetch prior review for PR #{Number}; answering without it.",
                prNumber);
            return null;
        }
    }

    /// <summary>
    /// Wraps the model's answer with the hidden marker and an attribution line.
    /// </summary>
    private static string FormatAnswer(string answer, string asker)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(AnswerMarker);
        sb.AppendLine($"> 💬 Replying to @{asker}");
        sb.AppendLine();
        sb.AppendLine(answer);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("_Answered by Fluvia · mention `/fluvia` in a comment to ask a question._");
        return sb.ToString();
    }
}