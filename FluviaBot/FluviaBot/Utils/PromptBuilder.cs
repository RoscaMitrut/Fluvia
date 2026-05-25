using FluviaBot.Models;
using System.Text;

namespace FluviaBot.Prompts;

/// <summary>
/// Builds the user prompt from a PullRequestContext.
/// Shared by all LLM-based ICodeReviewer implementations so the
/// context format stays consistent regardless of provider.
/// </summary>
public static class PromptBuilder
{
    public static string Build(PullRequestContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Pull Request: {ctx.PrTitle}");
        sb.AppendLine($"**Repo:** {ctx.RepoOwner}/{ctx.RepoName}  |  **PR #{ctx.PrNumber}**");
        sb.AppendLine($"**Merging:** `{ctx.HeadBranch}` → `{ctx.BaseBranch}`");

        if (!string.IsNullOrWhiteSpace(ctx.PrDescription))
        {
            sb.AppendLine();
            sb.AppendLine("### Description");
            sb.AppendLine(ctx.PrDescription);
        }

        sb.AppendLine();
        sb.AppendLine("### Changed Files");

        foreach (var file in ctx.Files)
        {
            sb.AppendLine();
            sb.AppendLine($"#### `{file.Filename}` ({file.Status}, +{file.Additions} -{file.Deletions})");

            if (file.FullContent is not null)
            {
                sb.AppendLine("**Full file content:**");
                sb.AppendLine("```");
                sb.AppendLine(file.FullContent);
                sb.AppendLine("```");
            }

            if (file.Patch is not null)
            {
                sb.AppendLine("**Diff:**");
                sb.AppendLine("```diff");
                sb.AppendLine(file.Patch);
                sb.AppendLine("```");
            }
        }

        return sb.ToString();
    }
}
