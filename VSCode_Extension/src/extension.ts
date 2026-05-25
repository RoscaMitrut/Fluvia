import * as vscode from "vscode";
import { GitHubClient } from "./github";
import {
  ReviewTreeProvider,
  FileItem,
  FindingItem,
} from "./providers/reviewTreeProvider";
import { PullRequest } from "./types";

let treeProvider: ReviewTreeProvider;
let githubClient: GitHubClient;

export async function activate(context: vscode.ExtensionContext) {
  githubClient = new GitHubClient();
  treeProvider = new ReviewTreeProvider();

  // ── Sidebar tree view ───────────────────────────────────────────────────────
  const treeView = vscode.window.createTreeView("fluvia.findings", {
    treeDataProvider: treeProvider,
    showCollapseAll:  true,
  });

  // ── Auto-refresh when the panel becomes visible ─────────────────────────────
  let lastVisibleRefresh = 0;
  context.subscriptions.push(
    treeView.onDidChangeVisibility((e) => {
      if (e.visible && Date.now() - lastVisibleRefresh > 30_000) {
        lastVisibleRefresh = Date.now();
        loadReviewForCurrentBranch();
      }
    })
  );

  // ── Commands ────────────────────────────────────────────────────────────────

  context.subscriptions.push(
    vscode.commands.registerCommand("fluvia.refresh", () =>
      loadReviewForCurrentBranch()
    ),

    vscode.commands.registerCommand("fluvia.selectPR", () =>
      selectPRManually()
    ),

    vscode.commands.registerCommand(
      "fluvia.sendToChat",
      (arg: FindingItem | string) => {
        // Invoked from a finding's click command (string) or from the
        // inline / context-menu button (the FindingItem itself).
        const prompt = arg instanceof FindingItem ? arg.finding.agentPrompt : arg;
        return sendToChat(prompt);
      }
    ),

    vscode.commands.registerCommand(
      "fluvia.openFile",
      (arg: FileItem | string) => {
        // Invoked from the inline / context-menu button (the FileItem)
        // or programmatically with a raw filename string.
        const filename =
          arg instanceof FileItem ? arg.fileReview.filename : arg;
        return openFile(filename);
      }
    ),

    treeView
  );

  // ── Branch change watcher ───────────────────────────────────────────────────
  // Fires whenever HEAD changes (checkout, rebase, etc.)
  const gitExtension = vscode.extensions.getExtension("vscode.git")?.exports;
  const git = gitExtension?.getAPI(1);

  if (git?.repositories[0]) {
    const repo = git.repositories[0];
    context.subscriptions.push(
      repo.state.onDidChange(() => {
        const branch = repo.state.HEAD?.name;
        if (branch) {
          // Debounce — git fires multiple state changes during a single checkout
          scheduleLoad(branch);
        }
      })
    );
  }

  // ── Initial load ─────────────────────────────────────────────────────────────
  await loadReviewForCurrentBranch();
}

// ── Load logic ────────────────────────────────────────────────────────────────

let loadTimer: ReturnType<typeof setTimeout> | undefined;
let lastLoadedBranch: string | undefined;

function scheduleLoad(branch: string) {
  if (loadTimer) { clearTimeout(loadTimer); }
  loadTimer = setTimeout(() => {
    if (branch !== lastLoadedBranch) {
      lastLoadedBranch = branch;
      loadReviewForCurrentBranch();
    }
  }, 800);
}

async function loadReviewForCurrentBranch() {
  treeProvider.setLoading();

  try {
    const ownerRepo = await githubClient.detectRepo();
    if (!ownerRepo) {
      treeProvider.setNoPr();
      return;
    }

    const branch = await githubClient.currentBranch();
    if (!branch) {
      treeProvider.setNoPr();
      return;
    }

    const pr = await githubClient.findPRForBranch(ownerRepo, branch);
    if (!pr) {
      treeProvider.setNoPr();
      return;
    }

    pr.repo = ownerRepo;
    await loadReviewForPR(pr);
  } catch (err) {
    vscode.window.showErrorMessage(`Fluvia Code Review: ${String(err)}`);
    treeProvider.setNoPr();
  }
}

async function loadReviewForPR(pr: PullRequest) {
  const review = await githubClient.fetchReview(pr.repo, pr.number);
  if (!review) {
    treeProvider.setNoReview(pr);
  } else {
    treeProvider.setReview(pr, review);
  }
}

// ── Manual PR picker ──────────────────────────────────────────────────────────

async function selectPRManually() {
  const ownerRepo = await githubClient.detectRepo();
  if (!ownerRepo) {
    vscode.window.showErrorMessage("Could not detect a GitHub repo in this workspace.");
    return;
  }

  let prs;
  try {
    prs = await githubClient.listPRs(ownerRepo);
  } catch {
    vscode.window.showErrorMessage("Failed to fetch pull requests. Are you signed in to GitHub?");
    return;
  }

  if (prs.length === 0) {
    vscode.window.showInformationMessage("No open pull requests found.");
    return;
  }

  const items = prs.map((pr) => ({
    label:       `#${pr.number} ${pr.title}`,
    description: `${pr.headBranch} → ${pr.baseBranch}`,
    pr,
  }));

  const picked = await vscode.window.showQuickPick(items, {
    placeHolder:        "Select a pull request to load its review",
    matchOnDescription: true,
  });

  if (!picked) { return; }

  treeProvider.setLoading();
  picked.pr.repo = ownerRepo;
  await loadReviewForPR(picked.pr);
}

// ── Copilot Chat integration ──────────────────────────────────────────────────

async function sendToChat(agentPrompt: string) {
  try {
    await vscode.commands.executeCommand("workbench.action.chat.open", {
      query: agentPrompt,
      isPartialQuery: true,
    });
  } catch (err) {
    console.error("chat.open failed:", err);
    await vscode.env.clipboard.writeText(agentPrompt);
    vscode.window.showInformationMessage(
      "Copilot Chat not found — prompt copied to clipboard instead."
    );
  }
}
// ── File navigation ───────────────────────────────────────────────────────────

async function openFile(filename: string) {
  const workspaceRoot = vscode.workspace.workspaceFolders?.[0]?.uri;
  if (!workspaceRoot) { return; }

  const fileUri = vscode.Uri.joinPath(workspaceRoot, filename);
  try {
    const doc = await vscode.workspace.openTextDocument(fileUri);
    await vscode.window.showTextDocument(doc);
  } catch {
    vscode.window.showWarningMessage(`Could not open file: ${filename}`);
  }
}

export function deactivate() {}