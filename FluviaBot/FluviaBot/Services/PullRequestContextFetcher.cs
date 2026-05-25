using System.Text;
using FluviaBot.Models;
using Octokit;

namespace FluviaBot.Services;

/// <summary>
/// Fetches the full context of a pull request — metadata, per-file diffs, and
/// full file contents at HEAD — into a <see cref="PullRequestContext"/>.
///
/// Shared by the review and the comment Q&amp;A flows so both reason over the
/// exact same PR context without duplicating the GitHub plumbing.
/// </summary>
public sealed class PullRequestContextFetcher
{
    private readonly ILogger<PullRequestContextFetcher> _logger;

    public PullRequestContextFetcher(ILogger<PullRequestContextFetcher> logger)
        => _logger = logger;

    /// <summary>
    /// Builds a full <see cref="PullRequestContext"/> for the given PR using an
    /// already-authenticated Octokit client.
    /// </summary>
    public async Task<PullRequestContext> FetchAsync(
        IGitHubClient octokit,
        string owner,
        string repo,
        int prNumber,
        CancellationToken ct = default)
    {
        // ── PR metadata ──────────────────────────────────────────────────────
        var pr = await GitHubRetry.ExecuteAsync(
            () => octokit.PullRequest.Get(owner, repo, prNumber),
            _logger, "PullRequest.Get", ct);

        // ── Changed files (includes patch/diff per file) ─────────────────────
        var prFiles = await GitHubRetry.ExecuteAsync(
            () => octokit.PullRequest.Files(owner, repo, prNumber),
            _logger, "PullRequest.Files", ct);

        // ── Full file contents for non-deleted files ─────────────────────────
        var changedFiles = new List<ChangedFile>();

        foreach (var file in prFiles)
        {
            ct.ThrowIfCancellationRequested();

            string? fullContent = null;
            if (file.Status != "removed")
            {
                fullContent = await TryGetFullContentAsync(
                    octokit, owner, repo, file.FileName, pr.Head.Sha, ct);
            }

            changedFiles.Add(new ChangedFile(
                Filename: file.FileName,
                Status: file.Status,
                Additions: file.Additions,
                Deletions: file.Deletions,
                Patch: file.Patch,
                FullContent: fullContent
            ));
        }

        return new PullRequestContext(
            RepoOwner: owner,
            RepoName: repo,
            PrNumber: prNumber,
            PrTitle: pr.Title,
            PrDescription: pr.Body,
            BaseBranch: pr.Base.Ref,
            HeadBranch: pr.Head.Ref,
            Files: changedFiles
        );
    }

    /// <summary>
    /// Fetches the full text of a file at a given ref. Returns <c>null</c> (and
    /// logs a warning) if the content cannot be retrieved — callers then fall
    /// back to the diff only.
    /// </summary>
    private async Task<string?> TryGetFullContentAsync(
        IGitHubClient octokit,
        string owner,
        string repo,
        string path,
        string headSha,
        CancellationToken ct)
    {
        try
        {
            var contentResponse = await GitHubRetry.ExecuteAsync(
                () => octokit.Repository.Content.GetAllContentsByRef(owner, repo, path, headSha),
                _logger, $"Repository.Content.GetAllContentsByRef({path})", ct);

            var item = contentResponse.FirstOrDefault();
            if (item is null)
            {
                _logger.LogWarning(
                    "No content returned for {File}; falling back to diff only.", path);
                return null;
            }

            // Octokit has already decoded the file to UTF-8 text.
            if (!string.IsNullOrEmpty(item.Content))
                return item.Content;

            // Fallback: decode the raw transport-encoded payload ourselves.
            if (!string.IsNullOrEmpty(item.EncodedContent) &&
                string.Equals(item.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
            {
                // GitHub may wrap the Base-64 payload at fixed line widths;
                // Convert.FromBase64String does not tolerate whitespace.
                var base64Clean = item.EncodedContent
                    .Replace("\r", "")
                    .Replace("\n", "");

                return Encoding.UTF8.GetString(Convert.FromBase64String(base64Clean));
            }

            _logger.LogWarning(
                "Content for {File} is empty or non-text (encoding: {Encoding}); " +
                "falling back to diff only.", path, item.Encoding ?? "unknown");
            return null;
        }
        catch (OperationCanceledException)
        {
            // Cancellation must propagate, not be swallowed as a "diff only" warning.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not fetch full content for {File}; falling back to diff only.", path);
            return null;
        }
    }
}