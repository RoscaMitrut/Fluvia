import * as vscode from "vscode";
import * as path from "path";
import { CodeReview, FileReview, Finding, PullRequest } from "../types";

// ── Tree item types ───────────────────────────────────────────────────────────

export type ReviewTreeItem =
  | PrHeaderItem
  | SummaryItem
  | FileItem
  | FindingItem
  | MessageItem;

/** Map a severity to the theme colour used for its icon. */
function severityColor(severity: Finding["severity"]): vscode.ThemeColor {
  switch (severity) {
    case "Error":
      return new vscode.ThemeColor("list.errorForeground");
    case "Warning":
      return new vscode.ThemeColor("list.warningForeground");
    default:
      return new vscode.ThemeColor("descriptionForeground");
  }
}

/** The most severe severity present in a list of findings. */
function worstSeverity(findings: Finding[]): Finding["severity"] | undefined {
  if (findings.some((f) => f.severity === "Error")) { return "Error"; }
  if (findings.some((f) => f.severity === "Warning")) { return "Warning"; }
  if (findings.some((f) => f.severity === "Info")) { return "Info"; }
  return undefined;
}

/**
 * Top bar showing the selected PR.
 * Display-only — no command, not collapsible.
 */
export class PrHeaderItem extends vscode.TreeItem {
  constructor(public readonly pr: PullRequest) {
    super(`#${pr.number}  ${pr.title}`, vscode.TreeItemCollapsibleState.None);
    this.iconPath = new vscode.ThemeIcon("git-pull-request");
    this.description = `${pr.headBranch} → ${pr.baseBranch}`;
    this.tooltip = new vscode.MarkdownString(
      `**${pr.repo}**\n\nPR #${pr.number} — ${pr.title}\n\n` +
        `\`${pr.headBranch}\` → \`${pr.baseBranch}\``
    );
    this.contextValue = "prHeader";
  }
}

/**
 * Summary paragraph.
 * Display-only — no command, not collapsible.
 */
export class SummaryItem extends vscode.TreeItem {
  constructor(summary: string) {
    // Truncate long summaries for the tree label; full text in the tooltip.
    const label = summary.length > 80 ? summary.slice(0, 77) + "…" : summary;
    super(label, vscode.TreeItemCollapsibleState.None);
    this.iconPath = new vscode.ThemeIcon("note");
    this.tooltip = new vscode.MarkdownString(summary);
    this.contextValue = "summary";
  }
}

/**
 * File-level node — collapsible.
 * Clicking the row only expands/collapses it. Opening the file is a
 * deliberate action via the inline button / context menu (see package.json).
 */
export class FileItem extends vscode.TreeItem {
  constructor(public readonly fileReview: FileReview) {
    super(
      path.basename(fileReview.filename),
      vscode.TreeItemCollapsibleState.Expanded
    );

    const findings = fileReview.findings;
    const errors = findings.filter((f) => f.severity === "Error").length;
    const warnings = findings.filter((f) => f.severity === "Warning").length;
    const infos = findings.filter((f) => f.severity === "Info").length;

    // Counts live in the description (greyed-out) instead of the label.
    const counts = [
      errors ? `${errors} error${errors > 1 ? "s" : ""}` : "",
      warnings ? `${warnings} warning${warnings > 1 ? "s" : ""}` : "",
      infos ? `${infos} info` : "",
    ]
      .filter(Boolean)
      .join(", ");

    const dir = path.dirname(fileReview.filename);
    this.description = dir === "." ? counts : `${dir} · ${counts}`;

    // Tint the file icon by the worst severity in the file.
    const worst = worstSeverity(findings);
    this.iconPath = worst
      ? new vscode.ThemeIcon("file-code", severityColor(worst))
      : new vscode.ThemeIcon("file-code");

    // resourceUri lets VS Code show native file decorations / type icons.
    this.resourceUri = vscode.Uri.file(fileReview.filename);
    this.tooltip = fileReview.filename;
    this.contextValue = "file";
  }
}

/**
 * Individual finding — leaf node.
 * Clicking sends the agent prompt to Copilot Chat; an inline button
 * exposes the same action visibly (see package.json).
 */
export class FindingItem extends vscode.TreeItem {
  constructor(
    public readonly finding: Finding,
    public readonly filename: string
  ) {
    super(finding.description, vscode.TreeItemCollapsibleState.None);

    // Location moves to the description field — keeps the label clean.
    if (finding.location) {
      this.description = finding.location;
    }

    this.iconPath = new vscode.ThemeIcon(
      finding.severity === "Error"
        ? "error"
        : finding.severity === "Warning"
        ? "warning"
        : "info",
      severityColor(finding.severity)
    );

    this.tooltip = new vscode.MarkdownString(
      `**${finding.severity}**` +
        (finding.location ? ` · _${finding.location}_` : "") +
        `\n\n${finding.description}\n\n_Click to send to Copilot Chat._`
    );
    this.contextValue = "finding";

    // Clicking inserts the agent prompt into Copilot Chat.
    this.command = {
      command: "fluvia.sendToChat",
      title: "Send to Copilot Chat",
      arguments: [finding.agentPrompt],
    };
  }
}

/**
 * Placeholder shown when loading or empty.
 * Display-only — no command, not collapsible.
 */
export class MessageItem extends vscode.TreeItem {
  constructor(message: string, icon = "circle-slash") {
    super(message, vscode.TreeItemCollapsibleState.None);
    this.iconPath = new vscode.ThemeIcon(icon);
    this.contextValue = "message";
  }
}

// ── Provider ──────────────────────────────────────────────────────────────────

export class ReviewTreeProvider
  implements vscode.TreeDataProvider<ReviewTreeItem>
{
  private _onDidChangeTreeData =
    new vscode.EventEmitter<ReviewTreeItem | undefined | void>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

  private review: CodeReview | undefined;
  private pr: PullRequest | undefined;
  private state: "idle" | "loading" | "no-pr" | "no-review" | "ready" = "idle";

  setLoading() {
    this.state = "loading";
    this.review = undefined;
    this._onDidChangeTreeData.fire();
  }

  setNoPr() {
    this.state = "no-pr";
    this.pr = undefined;
    this.review = undefined;
    this._onDidChangeTreeData.fire();
  }

  setNoReview(pr: PullRequest) {
    this.state = "no-review";
    this.pr = pr;
    this.review = undefined;
    this._onDidChangeTreeData.fire();
  }

  setReview(pr: PullRequest, review: CodeReview) {
    this.state = "ready";
    this.pr = pr;
    this.review = review;
    this._onDidChangeTreeData.fire();
  }

  getTreeItem(element: ReviewTreeItem): vscode.TreeItem {
    return element;
  }

  getChildren(element?: ReviewTreeItem): ReviewTreeItem[] {
    // Root level
    if (!element) {
      return this.rootItems();
    }

    // File node → its findings
    if (element instanceof FileItem) {
      return element.fileReview.findings.map(
        (f) => new FindingItem(f, element.fileReview.filename)
      );
    }

    return [];
  }

  private rootItems(): ReviewTreeItem[] {
    switch (this.state) {
      case "idle":
        return [new MessageItem("Waiting for a PR branch…", "git-branch")];
      case "loading":
        return [new MessageItem("Fetching review…", "loading~spin")];
      case "no-pr":
        return [
          new MessageItem("No open PR for this branch.", "git-pull-request"),
        ];
      case "no-review":
        return [
          new PrHeaderItem(this.pr!),
          new MessageItem("No bot review found on this PR yet.", "comment"),
        ];
      case "ready": {
        const items: ReviewTreeItem[] = [
          new PrHeaderItem(this.pr!),
          new SummaryItem(this.review!.summary),
        ];

        const filesWithFindings = this.review!.fileReviews.filter(
          (f) => f.findings.length > 0
        );

        if (filesWithFindings.length === 0) {
          items.push(new MessageItem("No issues found.", "pass-filled"));
        } else {
          items.push(...filesWithFindings.map((f) => new FileItem(f)));
        }

        return items;
      }
    }
  }
}