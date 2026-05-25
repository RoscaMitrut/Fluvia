import * as vscode from "vscode";
import { CodeReview, FileReview, Finding, PullRequest, Severity } from "./types";

const API = "https://api.github.com";

export class GitHubClient {
  private async token(): Promise<string> {
    // Reuse the session the built-in GitHub extension already has.
    // This prompts for sign-in only if the user isn't already authenticated.
    const session = await vscode.authentication.getSession(
      "github",
      ["repo"],           // read access to repos (PRs, comments)
      { createIfNone: true }
    );
    return session.accessToken;
  }

  private async get<T>(path: string): Promise<T> {
    const token = await this.token();
    const res = await fetch(`${API}${path}`, {
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
      },
    });

    if (!res.ok) {
      throw new Error(`GitHub API error ${res.status} for ${path}`);
    }

    return res.json() as Promise<T>;
  }

  // ── Repo detection ──────────────────────────────────────────────────────────

  /** Infer "owner/repo" from the workspace's git remote. */
  async detectRepo(): Promise<string | undefined> {
    const gitExtension = vscode.extensions.getExtension("vscode.git");
    if (!gitExtension) { return undefined; }

    // Ensure the Git extension is activated before using its API.
    await gitExtension.activate();

    const git = (gitExtension.exports as { getAPI(version: number): any })?.getAPI(1);
    if (!git) { return undefined; }

    const repo = git.repositories[0];
    if (!repo) { return undefined; }

    const remote = repo.state.remotes.find(
      (r: { name: string }) => r.name === "origin"
    );

    const url: string | undefined =
      remote?.fetchUrl ?? remote?.pushUrl;

    if (!url) { return undefined; }

    // Extract owner/repo from https or ssh remote URLs
    const match =
      url.match(/github\.com[:/](.+?\/.+?)(?:\.git)?$/) ??
      url.match(/github\.com\/(.+?\/.+?)(?:\.git)?$/);

    return match?.[1];
  }

  /** Return the name of the currently checked-out branch. */
  async currentBranch(): Promise<string | undefined> {
    const gitExtension = vscode.extensions.getExtension("vscode.git");
    if (!gitExtension) { return undefined; }

    await gitExtension.activate();

    const git = (gitExtension.exports as { getAPI(version: number): any })?.getAPI(1);
    const repo = git?.repositories[0];
    return repo?.state?.HEAD?.name;
  }

  // ── Pull requests ───────────────────────────────────────────────────────────

  /** List open PRs for the given repo. */
  async listPRs(ownerRepo: string): Promise<PullRequest[]> {
    const items = await this.get<GhPr[]>(
      `/repos/${ownerRepo}/pulls?state=open&per_page=30`
    );
    return items.map(prFromGh);
  }

  /** Find the open PR whose head branch matches the given branch name. */
  async findPRForBranch(
    ownerRepo: string,
    branch: string
  ): Promise<PullRequest | undefined> {
    const prs = await this.listPRs(ownerRepo);
    return prs.find((p) => p.headBranch === branch);
  }

  // ── Review comment ──────────────────────────────────────────────────────────

  /**
   * Find the most recent issue comment on a PR that contains the bot marker
   * and parse it into a CodeReview.  Returns undefined if none found.
   */
  async fetchReview(
    ownerRepo: string,
    prNumber: number
  ): Promise<CodeReview | undefined> {
    const marker =
      vscode.workspace
        .getConfiguration("fluvia")
        .get<string>("botCommentMarker") ?? "Fluvia Code Review";

    const comments = await this.get<GhComment[]>(
      `/repos/${ownerRepo}/issues/${prNumber}/comments?per_page=100`
    );

    // Find the last bot comment (most recent re-review wins)
    const botComment = [...comments]
      .reverse()
      .find((c) => c.body.includes(marker));

    if (!botComment) { return undefined; }

    return parseCommentBody(botComment.body);
  }
}

// ── Markdown parser ───────────────────────────────────────────────────────────

/**
 * Parse the formatted markdown comment produced by PullRequestReviewService
 * back into a structured CodeReview.
 *
 * The comment format is:
 *   ## Fluvia Code Review
 *   <summary>
 *   ### 📄 `filename`
 *   🔴/🟡/🔵 **Severity** _location_
 *   <description>
 *   <details><summary>💬 Agent prompt...</summary>
 *   ```
 *   <agentPrompt>
 *   ```
 *   </details>
 */
function parseCommentBody(body: string): CodeReview {
  const lines = body.split("\n");

  let summary = "";
  const fileReviews: FileReview[] = [];
  let currentFile: FileReview | null = null;
  let currentFinding: Partial<Finding> | null = null;
  let inPromptBlock = false;
  let promptLines: string[] = [];
  let inSummary = false;
  const summaryLines: string[] = [];

  for (const line of lines) {
    // Header — skip
    if (line.startsWith("## Fluvia Code Review")) {
      inSummary = true;
      continue;
    }

    // File header
    if (line.startsWith("### 📄 `")) {
      // Flush previous finding
      if (currentFinding && currentFile) {
        currentFile.findings.push(currentFinding as Finding);
        currentFinding = null;
      }
      inSummary = false;
      summary = summaryLines.join(" ").trim();

      const filenameMatch = line.match(/`([^`]+)`/);
      if (filenameMatch) {
        currentFile = { filename: filenameMatch[1], findings: [] };
        fileReviews.push(currentFile);
      }
      continue;
    }

    // Summary collection
    if (inSummary && line.trim() && !line.startsWith("---")) {
      summaryLines.push(line.trim());
      continue;
    }

    // Finding header line: 🔴/🟡/🔵 **Severity** _location_
    const findingMatch = line.match(/^(🔴|🟡|🔵)\s+\*\*(Error|Warning|Info)\*\*(.*)/);
    if (findingMatch && currentFile) {
      if (currentFinding) {
        currentFile.findings.push(currentFinding as Finding);
      }
      const locationMatch = findingMatch[3].match(/_([^_]+)_/);
      currentFinding = {
        severity: findingMatch[2] as Severity,
        location: locationMatch?.[1],
        description: "",
        agentPrompt: "",
      };
      continue;
    }

    // Agent prompt fenced block
    if (line.trim() === "```" && currentFinding) {
      if (inPromptBlock) {
        currentFinding.agentPrompt = promptLines.join("\n").trim();
        promptLines = [];
        inPromptBlock = false;
      } else {
        inPromptBlock = true;
      }
      continue;
    }

    if (inPromptBlock) {
      promptLines.push(line);
      continue;
    }

    // Finding description lines
    if (
      currentFinding &&
      line.trim() &&
      !line.startsWith("<details>") &&
      !line.startsWith("<summary>") &&
      !line.startsWith("</details>") &&
      !line.startsWith("</summary>") &&
      !line.startsWith("---") &&
      !line.startsWith("_Generated")
    ) {
      currentFinding.description =
        ((currentFinding.description ?? "") + " " + line.trim()).trim();
    }
  }

  // Flush last finding
  if (currentFinding && currentFile) {
    currentFile.findings.push(currentFinding as Finding);
  }

  if (!summary) {
    summary = summaryLines.join(" ").trim();
  }

  return { summary, fileReviews };
}

// ── GitHub API shapes ─────────────────────────────────────────────────────────

interface GhPr {
  number: number;
  title: string;
  head: { ref: string };
  base: { ref: string };
}

interface GhComment {
  id: number;
  body: string;
}

function prFromGh(pr: GhPr): PullRequest {
  return {
    number: pr.number,
    title: pr.title,
    headBranch: pr.head.ref,
    baseBranch: pr.base.ref,
    repo: "",  // filled in by callers that know the repo
  };
}
