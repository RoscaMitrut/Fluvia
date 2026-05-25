from models import PullRequestContext, CodeReview


SYSTEM_PROMPT = """\
You are an expert code reviewer. Analyse the pull request and return a
structured JSON review. Be concise but precise.

For each finding include an "agentPrompt" — a clear, self-contained prompt
a developer can paste into an AI coding agent to fix the issue. Name the
file, describe the problem, and specify the desired outcome.

Respond ONLY with a valid JSON object. No markdown fences, no preamble.

Use exactly this schema:
{
  "summary": "string",
  "fileReviews": [
    {
      "filename": "string",
      "findings": [
        {
          "severity": "Info" | "Warning" | "Error",
          "description": "string",
          "agentPrompt": "string",
          "location": "string | null"
        }
      ]
    }
  ]
}
"""


def build_prompt(ctx: PullRequestContext) -> str:
    lines: list[str] = []

    lines.append(f"## Pull Request: {ctx.pr_title}")
    lines.append(f"**Repo:** {ctx.repo_owner}/{ctx.repo_name}  |  **PR #{ctx.pr_number}**")
    lines.append(f"**Merging:** `{ctx.head_branch}` → `{ctx.base_branch}`")

    if ctx.pr_description:
        lines.append("")
        lines.append("### Description")
        lines.append(ctx.pr_description)

    lines.append("")
    lines.append("### Changed Files")

    for f in ctx.files:
        lines.append("")
        lines.append(f"#### `{f.filename}` ({f.status}, +{f.additions} -{f.deletions})")

        if f.full_content:
            lines.append("**Full file content:**")
            lines.append("```")
            lines.append(f.full_content)
            lines.append("```")

        if f.patch:
            lines.append("**Diff:**")
            lines.append("```diff")
            lines.append(f.patch)
            lines.append("```")

    return "\n".join(lines)


# ── Consolidation pass ────────────────────────────────────────────────────────

CONSOLIDATION_SYSTEM_PROMPT = """\
You are consolidating the output of several independent code reviewers into
one single, unified review. You are NOT reviewing the code yourself — only
combining what the reviewers already produced.

The input groups findings by file. Each finding is tagged with the reviewer
slot(s) that raised it, e.g. "[SLOT1:GOOGLE]". The summaries likewise have
one labelled block per reviewer. These tags are provided ONLY so you can tell
which findings are duplicates of one another.

Do the following:
- Remove duplicate findings: if two or more reviewers reported the same issue
  on the same file/location, keep only one, picking the clearest wording and
  the highest severity among the duplicates.
- Keep every distinct finding. Do NOT drop a finding just because only one
  reviewer raised it.
- Produce ONE unified review. Do NOT mention which reviewer raised which
  point, and do not include any "[SLOT...]" tags, reviewer names, or other
  attribution in the descriptions, agentPrompts, or summary. The output must
  read as a single coherent review written by one reviewer.
- Group findings by file. Each file appears once.
- Make sure every finding has a valid severity (Info | Warning | Error), a
  description, and an agentPrompt. Fix obviously malformed entries.
- Write one coherent overall summary. Do not invent issues not present in the
  input.

Respond ONLY with a valid JSON object. No markdown fences, no preamble.

Use exactly this schema:
{
  "summary": "string",
  "fileReviews": [
    {
      "filename": "string",
      "findings": [
        {
          "severity": "Info" | "Warning" | "Error",
          "description": "string",
          "agentPrompt": "string",
          "location": "string | null"
        }
      ]
    }
  ]
}
"""


def build_consolidation_prompt(partial_reviews: "dict[str, CodeReview]") -> str:
    """
    Render every reviewer slot's output as the input for the consolidation
    model, with each summary and finding tagged by its slot key (e.g.
    "[SLOT1:GOOGLE]"). The model receives this attributed view and returns a
    single deduplicated, tidied CodeReview in the same schema — keeping the
    attributions in its output (see CONSOLIDATION_SYSTEM_PROMPT).

    Findings are grouped by file, then listed per reviewer slot, so the model
    can see which reviewer(s) raised each point and merge duplicates while
    preserving the combined attribution.
    """
    lines: list[str] = []
    lines.append("## Reviews from all reviewer slots")

    if not partial_reviews:
        lines.append("")
        lines.append("(no reviewers produced output)")
        return "\n".join(lines)

    slot_keys = sorted(partial_reviews)

    # Per-slot summaries.
    lines.append("")
    lines.append("### Summaries (one block per reviewer slot)")
    for key in slot_keys:
        lines.append("")
        lines.append(f"**[{key.upper()}]** {partial_reviews[key].summary}")

    # Findings grouped by file, then by slot.
    lines.append("")
    lines.append("### Findings to deduplicate and tidy")

    # filename -> list of (slot_key, finding)
    by_file: dict[str, list] = {}
    for key in slot_keys:
        for fr in partial_reviews[key].file_reviews:
            for finding in fr.findings:
                by_file.setdefault(fr.filename, []).append((key, finding))

    if not by_file:
        lines.append("")
        lines.append("(no file-level findings)")
    else:
        for fname in sorted(by_file):
            lines.append("")
            lines.append(f"#### `{fname}`")
            for i, (key, finding) in enumerate(by_file[fname], 1):
                loc = f" [{finding.location}]" if finding.location else ""
                lines.append(
                    f"{i}. [{key.upper()}] ({finding.severity.value}){loc} "
                    f"{finding.description}"
                )
                if finding.agent_prompt:
                    lines.append(f"   agentPrompt: {finding.agent_prompt}")

    return "\n".join(lines)