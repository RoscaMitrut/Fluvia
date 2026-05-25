export type Severity = "Info" | "Warning" | "Error";

export interface Finding {
  severity: Severity;
  description: string;
  agentPrompt: string;
  location?: string;        // e.g. "L42" or "L10-L18"
}

export interface FileReview {
  filename: string;
  findings: Finding[];
}

export interface CodeReview {
  summary: string;
  fileReviews: FileReview[];
}

export interface PullRequest {
  number: number;
  title: string;
  headBranch: string;
  baseBranch: string;
  repo: string;             // "owner/name"
}
