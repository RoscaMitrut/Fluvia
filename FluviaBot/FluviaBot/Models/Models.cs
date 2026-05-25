namespace FluviaBot.Models;

// ── Input ────────────────────────────────────────────────────────────────────

/// <summary>
/// Everything an ICodeReviewer needs to know about a pull request.
/// Fetched once by PullRequestReviewService and passed to any reviewer.
/// </summary>
public record PullRequestContext(
    // PR metadata
    string  RepoOwner,
    string  RepoName,
    int     PrNumber,
    string  PrTitle,
    string? PrDescription,
    string  BaseBranch,
    string  HeadBranch,

    // Per-file data
    IReadOnlyList<ChangedFile> Files
);

/// <summary>
/// One file that changed in the PR, with both the raw patch and the full
/// contents of the file at HEAD — giving reviewers maximum context.
/// </summary>
public record ChangedFile(
    string  Filename,
    string  Status,          // "added" | "modified" | "removed" | "renamed"
    int     Additions,
    int     Deletions,
    string? Patch,           // unified diff for this file (may be null for binaries)
    string? FullContent      // full file at HEAD; null for deleted/binary files
);

// ── Output ───────────────────────────────────────────────────────────────────

/// <summary>
/// Structured review result returned by any ICodeReviewer implementation.
/// PullRequestReviewService formats this into a GitHub comment.
/// </summary>
public record CodeReview(
    string                    Summary,
    IReadOnlyList<FileReview> FileReviews
);

/// <summary>Review findings for a single file.</summary>
public record FileReview(
    string                   Filename,
    IReadOnlyList<Finding>   Findings
);

/// <summary>One specific finding within a file.</summary>
public record Finding(
    Severity Severity,
    string   Description,

    /// <summary>
    /// A ready-to-paste prompt the developer can give an AI agent to fix this.
    /// e.g. "Refactor the Foo method in Bar.cs to dispose the HttpClient correctly."
    /// </summary>
    string   AgentPrompt,

    /// <summary>Optional: line number or range for reference (e.g. "L42" or "L10-L18").</summary>
    string?  Location = null
);

public enum Severity { Info, Warning, Error }
