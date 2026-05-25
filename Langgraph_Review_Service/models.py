from __future__ import annotations
from enum import Enum
from typing import Annotated, Optional
from pydantic import BaseModel


# ── Inbound (from .NET) ───────────────────────────────────────────────────────

class ChangedFile(BaseModel):
    filename: str
    status: str                  # added | modified | removed | renamed
    additions: int
    deletions: int
    patch: Optional[str] = None
    full_content: Optional[str] = None


class PullRequestContext(BaseModel):
    repo_owner: str
    repo_name: str
    pr_number: int
    pr_title: str
    pr_description: Optional[str] = None
    base_branch: str
    head_branch: str
    files: list[ChangedFile]


# ── Outbound (to .NET) ────────────────────────────────────────────────────────

class Severity(str, Enum):
    Info    = "Info"
    Warning = "Warning"
    Error   = "Error"


class Finding(BaseModel):
    severity: Severity
    description: str
    agent_prompt: str
    location: Optional[str] = None


class FileReview(BaseModel):
    filename: str
    findings: list[Finding]


class CodeReview(BaseModel):
    summary: str
    file_reviews: list[FileReview]


# ── LangGraph state ───────────────────────────────────────────────────────────

def _merge_partial_reviews(
    left: dict[str, CodeReview],
    right: dict[str, CodeReview],
) -> dict[str, CodeReview]:
    """
    Reducer for the `partial_reviews` channel.

    The three reviewer nodes run concurrently and each writes its own result
    into `partial_reviews`. LangGraph requires a reducer for any channel that
    more than one node writes in the same super-step — without it, parallel
    writes raise InvalidUpdateError ("Can receive only one value per step").

    Each node returns just its own entry, e.g. {"anthropic": review}; this
    reducer unions them so the merge node sees all providers' results.
    """
    return {**left, **right}


class ReviewState(BaseModel):
    """Mutable state threaded through the graph."""
    context: PullRequestContext

    # Written concurrently by the parallel reviewer nodes — keyed by provider
    # name. The Annotated reducer makes concurrent writes union instead of
    # collide. Each node returns only its own key; do NOT spread the existing
    # dict in a node, the reducer handles merging.
    partial_reviews: Annotated[dict[str, CodeReview], _merge_partial_reviews] = {}

    # Mechanical union of all slot outputs — populated by the merge node and
    # consumed by the consolidate node.
    merged_review: Optional[CodeReview] = None

    # Final output — populated by the consolidate node (SLOT 1's model tidies
    # `merged_review`, or falls back to it verbatim if SLOT 1 is unavailable).
    final_review: Optional[CodeReview] = None