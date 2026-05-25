using FluviaBot.Models;
using FluviaBot.Review;
using System.Text;

namespace FluviaBot.Prompts;

/// <summary>
/// Builds the system and user prompts for answering a user's question about a
/// pull request. Mirrors <see cref="PromptBuilder"/> in structure so the PR
/// context is rendered consistently, but the task is question-answering rather
/// than structured review.
/// </summary>
public static class QuestionPromptBuilder
{
    /// <summary>
    /// System prompt: defines the assistant's role for the Q&amp;A task.
    /// </summary>
    public const string SystemPrompt = """
        You are Fluvia, an AI code review assistant. A developer has asked you a
        question in a comment on a GitHub pull request.

        Answer the question directly, accurately, and concisely, grounding your
        answer in the actual code provided below. When the question refers to a
        specific line or file, quote the relevant snippet so the answer is easy
        to follow.

        Guidelines:
        - Be helpful and precise. Prefer concrete explanation over generic advice.
        - If the question is about a security risk, explain the concrete attack
          or failure scenario, not just that something is "bad practice".
        - If you genuinely cannot answer from the provided context, say so
          plainly and explain what additional information would be needed.
        - Do not invent code that is not present in the context.
        - Reply in GitHub-flavoured Markdown. Keep it focused — a few short
          paragraphs, with a code block only when it aids the explanation.
        - Do not greet, sign off, or restate the question. Just answer.
        """;

    /// <summary>
    /// Builds the user prompt: the PR context, the prior review (if any), and
    /// the question itself.
    /// </summary>
    public static string BuildUserPrompt(PullRequestContext ctx, QuestionContext question)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"## Pull Request: {ctx.PrTitle}");
        sb.AppendLine($"**Repo:** {ctx.RepoOwner}/{ctx.RepoName}  |  **PR #{ctx.PrNumber}**");
        sb.AppendLine($"**Merging:** `{ctx.HeadBranch}` → `{ctx.BaseBranch}`");

        if (!string.IsNullOrWhiteSpace(ctx.PrDescription))
        {
            sb.AppendLine();
            sb.AppendLine("### PR Description");
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

        if (!string.IsNullOrWhiteSpace(question.PriorReview))
        {
            sb.AppendLine();
            sb.AppendLine("### Fluvia's previous review of this PR");
            sb.AppendLine(
                "This is the review you posted earlier. The question may refer to it.");
            sb.AppendLine();
            sb.AppendLine(question.PriorReview);
        }

        sb.AppendLine();
        sb.AppendLine("### Question");
        sb.AppendLine($"`{question.Asker}` asked:");
        sb.AppendLine();
        sb.AppendLine(question.QuestionBody.Trim());

        return sb.ToString();
    }
}