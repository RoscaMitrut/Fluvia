# Fluvia — Architecture & Documentation

Presentation video: https://youtu.be/3-qA7fnCmm0

An AI-powered GitHub pull request review system. A GitHub App receives PR webhooks, runs the changed files through one or more LLMs, posts a structured review back as a PR comment, and surfaces those findings inside VS Code.

---
## Table of Contents

1. [Overview](#1-overview)
2. [System Architecture](#2-system-architecture)
3. [FluviaBot — the .NET GitHub App](#3-fluviabot--the-net-github-app)
4. [LangGraph Review Service — the Python pipeline](#4-langgraph-review-service--the-python-pipeline)
5. [Fluvia VS Code Extension](#5-fluvia-vs-code-extension)
6. [The Data Model](#6-the-data-model)
7. [End-to-End Flow](#7-end-to-end-flow)
8. [Configuration](#8-configuration)
9. [Deployment & Development](#9-deployment--development)
10. [Extending Fluvia](#10-extending-fluvia)

---
## 1. Overview

Fluvia automates code review on GitHub pull requests. It has three components
that can be deployed independently and share a common review data model.
| Component | Stack | Role |
|---|---|---|
| **FluviaBot** | C# / ASP.NET Core | GitHub App. Receives webhooks, runs a review, posts the result as a PR comment. |
| **LangGraph Review Service** | Python / FastAPI + LangGraph | Optional microservice. Runs up to three LLM reviewers in parallel and consolidates them. |
| **VS Code Extension** | TypeScript | Editor sidebar. Reads the bot's PR comment back and shows the findings. |

---
## 2. System Architecture

```
   GitHub  ──webhook──►  FluviaBot (.NET)  ──REST──►  GitHub (posts comment)
                              │
                              │ ICodeReviewer (chosen by ReviewProvider)
                              ▼
        ┌─────────────────────────────────────────────┐
        │  ChatCodeReviewer  ──► IChatClient ────────────┼──► one LLM call
        │     └─ OpenAI / Ollama / HuggingFace /          │
        │        Anthropic / Google                       │
        │                                                 │
        │  LangGraphCodeReviewer ───────────────────────┼──► Python LangGraph service
        └─────────────────────────────────────────────┘

   LangGraph service:  START ─► LLM 1 ┐
                       START ─► LLM 2 ┼─► merge ─► consolidate ─► CodeReview
                       START ─► LLM 3 ┘

   VS Code Extension:  reads the posted PR comment back via GitHub REST,
                       parses it, shows findings in a sidebar tree.
```

**Communication contracts:**
| Hop | Protocol | Notes |
|---|---|---|
| GitHub → FluviaBot | Signed webhook | HMAC `X-Hub-Signature-256`, verified before dispatch. |
| FluviaBot → LangGraph service | HTTP POST `/review` | snake_case JSON in and out. |
| FluviaBot ↔ GitHub | REST (Octokit) | Authenticated as a GitHub App installation. |
| Extension → GitHub | REST | Uses VS Code's built-in GitHub auth session. |

---
## 3. FluviaBot — the .NET GitHub App

The only component GitHub talks to directly, and the only one that writes to a PR.

### Files
| File | Responsibility |
|---|---|
| `Program.cs` | Bootstrap: config validation, DI, middleware, provider selection. |
| `WebhookProcessor.cs` | Filters PR events and triggers reviews. |
| `PullRequestReviewService.cs` | Orchestrates a review: fetch → review → post comment. |
|                `CommentQuestionService.cs`               |                            Orchestrates a Q&A reply: fetch context → answer → post comment.                           |
| `GitHubAppTokenProvider.cs` | Mints short-lived installation access tokens. |
| `GitHubRetry.cs` | Retry/backoff wrapper for GitHub API calls. |
| `PromptBuilder.cs` / `QuestionPromptBuilder.cs` | Builds the shared LLM user prompt from PR context. |
| `Models.cs` | The shared record types. |
| `ICodeReviewer.cs` | The reviewer abstraction + `NoOpCodeReviewer`. |
|          `IPullRequestQuestionAnswerer.cs`          |                        The Q&A abstraction + `NoOpQuestionAnswerer`.                        |
| `Chat/IChatClient.cs` | The transport seam: send a chat request, get text back. |
| `Chat/Clients/*ChatClient.cs` | One transport per wire format: `OpenAiCompatible` (OpenAI/Ollama/HuggingFace), `Anthropic`, `Google`. |
| `Review/ChatCodeReviewer.cs` | Provider-agnostic reviewer over any `IChatClient`. |
|               `Review/ChatQuestionAnswerer.cs`               |                          Provider-agnostic Q&A answerer over any `IChatClient`.                          |
| `Review/ReviewJsonParser.cs` | Shared parser: raw LLM JSON → `CodeReview`. |
| `Review/LangGraphCodeReviewer.cs` | The odd one out — delegates to the Python LangGraph pipeline. |
| `Review/ChatClientRegistry.cs` | Maps a provider name to a built `IChatClient`. |

### How it works

**Startup** `Program.cs` validates that required secrets (`GitHub:AppId`, `GitHub:WebhookSecret`) are present , it then wires up the
chat clients and selects a reviewer via the `ReviewProvider` config value . An unset value falls back to `NoOpCodeReviewer`. 

**Webhook handling** `WebhookProcessor.cs`: reaching the processor means the signature already passed validation. It handles two kinds of event. A `pull_request` event that is `opened` or `synchronize` (new commits pushed) triggers a review. An `issue_comment` event triggers the Q&A flow if the comment mentions `/fluvia` and was not written by the bot itself. Everything else (other PR actions, non-mention comments, the bot's own replies) is ignored.

**Review orchestration** (`PullRequestReviewService.cs`) runs five steps:

1. Authenticate as the installation.
2. Fetch PR metadata.
3. Fetch the changed files (with per-file diffs).
4. Fetch full file contents at the PR head SHA
5. Run the reviewer, format the result, and post or update the comment.

Every comment carries a hidden marker (`<!-- fluvia-code-review -->`). On each run the service finds the existing marked comment and **updates it in place**, so a busy PR never accumulates stale comments.

**Comment Q&A** (`CommentQuestionService.cs`) lets a developer ask Fluvia a question by mentioning `/fluvia` in a PR comment (e.g. _"/fluvia why is the issue on line 67 a security risk?"_). The flow mirrors a review: gather the same `PullRequestContext`, also pull in Fluvia's own prior review comment for grounding, send it all to the configured answerer, and post the reply as a new comment.

**Authentication** (`GitHubAppTokenProvider.cs`) does the standard GitHub App two-step: build a short-lived JWT signed with the App's private key, then exchange it for an installation access token.

### The reviewers

There are really only two reviewers, plus a fallback.

**`ChatCodeReviewer`** handles every single-LLM provider. It doesn't know or care which one. It builds the prompt with `PromptBuilder`, hands it to an `IChatClient`, and parses the reply with `ReviewJsonParser`. 

All the provider-specific knowledge lives one layer down, in the `IChatClient`
implementations:
| Client | Speaks for |
|---|---|
| `OpenAiCompatibleChatClient` | OpenAI, Ollama, HuggingFace — anything with an OpenAI-style `/v1/chat/completions` endpoint. |
| `AnthropicChatClient` | The Anthropic Messages API. |
| `GoogleChatClient` | The Google Gemini API. |

`ChatClientRegistry` maps a provider name to a built client. It's the single list of "what providers exist," and both the review flow and the comment Q&A flow read from it, which is why they always support exactly the same set.

Parsing (`ReviewJsonParser`) is shared and defensive: it strips Markdown fences, tolerates prose around the JSON, and turns malformed output into a _degraded_ review rather than throwing. Auth headers are set **per request**, never on the shared `HttpClient`.

**`LangGraphCodeReviewer`** is the exception to all of the above. It sends no prompt and parses no model output of its own: it POSTs the `PullRequestContext` to the Python service's `/review` endpoint and parses the `CodeReview` that comes back. It is a multi-model _pipeline_, not a chat call, so it stays its own class and is not part of the registry.

**`NoOpCodeReviewer`** is the fallback when `ReviewProvider` is unset.

---
## 4. LangGraph Review Service — the Python pipeline

A standalone FastAPI microservice that `LangGraphCodeReviewer` delegates to. Its purpose is to run **multiple LLM reviewers in parallel** and consolidate their findings into one review.

### Files
| File | Responsibility |
|---|---|
| `main.py` | FastAPI app: `GET /healthz`, `POST /review`. |
| `graph.py` | Builds and compiles the LangGraph state graph. |
| `nodes.py` | The graph nodes: three slots, merge, consolidate. |
| `models.py` | Pydantic models + the graph state object. |
| `providers.py` | Provider registry (Ollama, HuggingFace, Google). |
| `prompt.py` | System prompts and prompt builders. |
| `parser.py` | Parses raw LLM output into a `CodeReview`. |

### The graph
```
   START ──► slot1 ┐
   START ──► slot2 ┼──► merge ──► consolidate ──► END
   START ──► slot3 ┘
```

- **Three slot nodes** run concurrently. Each picks a provider and model from `SLOT{N}_PROVIDER` / `SLOT{N}_MODEL` environment variables. A slot that is unconfigured or lacks credentials simply writes nothing. A slot whose LLM call fails returns a degraded review for that slot only.
- **merge** mechanically unions every slot's findings into one review. No LLM.
- **consolidate** hands the merged review to an LLM for a deduplication and tidy-up pass. It tries SLOT 1's model first, then SLOT 2, then SLOT 3, falling back to the mechanical merge only if all three are unavailable.

### Providers

A provider knows what credentials it needs and how to build a chat model.
Adding one is a single registry entry.

| Provider | Credentials | Default model |
|---|---|---|
| `ollama` | none (local server) | `qwen2.5-coder:7b` |
| `huggingface` | `HUGGINGFACE_API_KEY` | `meta-llama/Llama-3.3-70B-Instruct` |
| `google` | `GOOGLE_API_KEY` | `gemini-2.0-flash` |

### The API

`POST /review` accepts a `PullRequestContext`, runs the graph, and returns the final `CodeReview` as snake_case JSON — matching what the .NET client parses.
`GET /healthz` is a liveness probe.

---
## 5. Fluvia VS Code Extension

A read-only consumer of reviews. It never triggers a review; it finds the comment FluviaBot already posted and presents it in the editor.

### Files
| File | Responsibility |
|---|---|
| `extension.ts` | Activation, commands, branch-change watching, load logic. |
| `github.ts` | Repo/branch detection, PR lookup, comment fetch + parser. |
| `reviewTreeProvider.ts` | The sidebar tree and its item classes. |
| `types.ts` | TypeScript mirrors of the review data model. |
| `package.json` | Manifest: views, commands, menus, settings. |

### How it works

On activation the extension registers a sidebar tree view and watches the Git repository's HEAD. When the branch changes (debounced to absorb the multiple events Git fires during a checkout) it reloads.

**Load logic:** detect `owner/repo` from the Git remote → get the current branch → find the open PR for that branch → fetch and parse its review comment. Any missing step puts the tree into a "no PR" state.

**GitHub access** reuses VS Code's built-in GitHub authentication session, so the extension stores no token. It fetches the PR's comments and finds the most recent one matching the configured marker.

**The Markdown parser** (`parseCommentBody`) is a line-by-line state machine that reverses FluviaBot's comment format, turning the posted Markdown back into a structured `CodeReview`.

**The sidebar tree** shows the PR header, a summary, then one node per file, each expanding into its findings. Clicking a finding sends its `agentPrompt` to Copilot Chat (or copies it to the clipboard if Copilot isn't installed).

### Commands
| Command | Purpose |
|---|---|
| `fluvia.refresh` | Reload the review for the current branch. |
| `fluvia.selectPR` | Pick an open PR from a list and load its review. |
| `fluvia.sendToChat` | Send a finding's fix prompt to Copilot Chat. |
| `fluvia.openFile` | Open the file a finding refers to. |

---
## 6. The Data Model

The same four concepts exist in all three components, in three languages.

| Concept | Fields |
|---|---|
| **PullRequestContext** | Repo owner/name, PR number/title/description, branches, changed files. |
| **ChangedFile** | Filename, status, additions, deletions, optional patch and full content. |
| **CodeReview** | Summary + list of file reviews. |
| **FileReview** | Filename + list of findings. |
| **Finding** | Severity (`Info`/`Warning`/`Error`), description, agent prompt, optional location. |

---
## 7. End-to-End Flow

**The review flow:**

1. A developer opens or pushes to a PR. GitHub sends a `pull_request` webhook to FluviaBot.
2. The webhook signature is verified; the processor filters to reviewable actions and calls the review service.
3. FluviaBot authenticates as the installation and gathers the PR's metadata, diffs, and full file contents into a `PullRequestContext`.
4. The configured `ICodeReviewer` runs — either a single LLM call, or a delegated POST to the LangGraph service.
5. If LangGraph is used, its graph fans out to up to three parallel LLM reviewers, merges their findings, and runs a consolidation pass.
6. FluviaBot formats the result as Markdown and updates (or creates) the marked PR comment.
7. When the developer's branch matches that PR, the VS Code extension finds the comment, parses it, and shows the findings. Clicking one sends a fix prompt to Copilot Chat.

**The Q&A flow:**

1. A developer posts a PR comment mentioning `@fluvia`. GitHub sends an `issue_comment` webhook.
2. The processor checks the comment mentions the bot and is not the bot's own reply, then calls the question service.
3. FluviaBot gathers the same `PullRequestContext`, plus its own prior review comment for grounding.
4. The configured `IPullRequestQuestionAnswerer` answers the question, and FluviaBot posts the answer as a new PR comment.

---
## 8. Configuration

### FluviaBot (.NET)

Environment variables use `Section__Key`, mapping to `Section:Key` in code.

| Env var | Required | Purpose |
|---|---|---|
| `GitHub__AppId` | ✅ | GitHub App ID (positive integer). |
| `GitHub__WebhookSecret` | ✅ | Webhook HMAC secret. |
| `GitHub__PrivateKeyPath` | ✅ | Path to the App private key `.pem`. |
| `ReviewProvider` | — | `OpenAI` / `Anthropic` / `Ollama` / `HuggingFace` / `Google` / `LangGraph`. Unset → NoOp. |
| `QuestionProvider` | — | Provider for `/fluvia` comment Q&A. Any non-LangGraph value above. Unset → inherits `ReviewProvider`. |
| `PORT` | — | Listening port (default `3000`). |
| `<Provider>__ApiKey` / `__Model` / `__BaseUrl` | conditionally | Required for the chosen provider. |
| `LangGraph__BaseUrl` | — | Defaults to the compose service name. |

### LangGraph Review Service (Python)

| Env var | Purpose |
|---|---|
| `SLOT1/2/3_PROVIDER` | Provider per slot: `ollama` / `huggingface` / `google`. |
| `SLOT1/2/3_MODEL` | Optional per-slot model override. |
| `OLLAMA_BASE_URL` | Ollama server URL. |
| `HUGGINGFACE_API_KEY` / `GOOGLE_API_KEY` | Provider credentials. |

---
## 9. Deployment & Development

Deployment is via `docker-compose.yml`, which uses **Compose profiles** so you start only the services you need.

| Service | Profile | Purpose |
|---|---|---|
| `fluviabot` | always | The .NET GitHub App. |
| `smee` | `smee` | Forwards public webhooks to a local bot for development. |
| `langgraph-review-service` | `langgraph-review-service` | The Python review microservice. |
| `ollama` / `ollama-gpu` | `ollama` / `ollama-gpu` | Local model server (CPU or GPU). |
| `ollama-pull` | `ollama`, `ollama-gpu` | One-shot job that pulls the configured model. |

---
## 10. Extending Fluvia

**Add an OpenAI-compatible provider** (Groq, Together, OpenRouter, Mistral, …): no new class. Add a builder to `ChatProviderOptionsFactory`, one entry to `ChatClientRegistry`, and one `AddHttpClient("chat:<name>")` line in `Program.cs`. It works for both code review and comment Q&A immediately.

**Add a provider with a different wire format:** write one `IChatClient` (use `AnthropicChatClient` as a template — set auth per request, keep parsing defensive), then do the registry + `Program.cs` steps above.

**Add a LangGraph provider:** add one entry to the `PROVIDERS` dict in `providers.py`. The slot nodes and graph wiring need no changes.

**Add a fourth review slot:** add a `slot4_node` in `nodes.py`, register it with `START → slot4` and `slot4 → merge` edges in `graph.py`, and extend the slot loop in `consolidate_node`. The state reducer already handles any number of concurrent writers.
