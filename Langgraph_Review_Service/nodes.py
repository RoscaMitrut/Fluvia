"""
LangGraph nodes for the parallel review pipeline.

The graph has three identical "slot" nodes (slot1, slot2, slot3). Each slot
picks a provider and model from environment variables:

    SLOT1_PROVIDER / SLOT1_MODEL
    SLOT2_PROVIDER / SLOT2_MODEL
    SLOT3_PROVIDER / SLOT3_MODEL

SLOT{N}_PROVIDER is one of: ollama | huggingface | google  (see providers.py).
SLOT{N}_MODEL is optional — each provider has a sensible default.

Every slot returns its result as {"partial_reviews": {slot_key: review}}.
The `partial_reviews` channel has a reducer (see models.py) that unions
these concurrent writes, so each slot returns ONLY its own entry and must
NOT spread state.partial_reviews itself.

A slot that is unconfigured or whose provider lacks credentials still
returns {"partial_reviews": {}} (never a bare {}) — LangGraph requires
every node to write at least one channel, and an empty dict merges
harmlessly through the reducer.

After the slots, merge_node mechanically unions every slot's output into
`merged_review`. consolidate_node then sends that to a model for a
deduplication / format-tidying pass and writes `final_review`: it tries
SLOT 1's model first, then SLOT 2, then SLOT 3, falling back to the
mechanical merge only if all three are unavailable.
"""
from __future__ import annotations

import logging
import os

from langchain_core.messages import HumanMessage, SystemMessage

from models import ReviewState, CodeReview
from prompt import (
    build_prompt,
    build_consolidation_prompt,
    SYSTEM_PROMPT,
    CONSOLIDATION_SYSTEM_PROMPT,
)
from parser import parse_llm_response
from providers import get_provider, known_provider_names

logger = logging.getLogger(__name__)


def _slot_key(slot: int, provider_name: str) -> str:
    """
    Key under which a slot stores its review in partial_reviews.

    Includes the slot number so two slots using the SAME provider don't
    overwrite each other (e.g. SLOT1=google and SLOT2=google).
    """
    return f"slot{slot}:{provider_name}"


async def _run_slot(state: ReviewState, slot: int) -> dict:
    """
    Generic review slot. Reads SLOT{slot}_PROVIDER / SLOT{slot}_MODEL,
    runs the chosen LLM, and returns its CodeReview under a unique key.
    """
    provider_name = os.getenv(f"SLOT{slot}_PROVIDER", "").strip()

    # Slot not configured at all — leave it empty.
    if not provider_name:
        logger.info("slot%d: SLOT%d_PROVIDER not set — skipping.", slot, slot)
        return {"partial_reviews": {}}

    provider = get_provider(provider_name)
    if provider is None:
        logger.warning(
            "slot%d: unknown provider '%s' — skipping. Known providers: %s",
            slot, provider_name, ", ".join(known_provider_names()),
        )
        return {"partial_reviews": {}}

    # Provider known but missing credentials / config.
    reason = provider.unavailable_reason()
    if reason is not None:
        logger.warning(
            "slot%d: provider '%s' unavailable (%s) — skipping.",
            slot, provider.name, reason,
        )
        return {"partial_reviews": {}}

    model = os.getenv(f"SLOT{slot}_MODEL", "").strip() or provider.default_model
    key = _slot_key(slot, provider.name)

    logger.info("slot%d: reviewing with provider=%s model=%s", slot, provider.name, model)

    try:
        llm = provider.build(model)
        response = await llm.ainvoke([
            SystemMessage(content=SYSTEM_PROMPT),
            HumanMessage(content=build_prompt(state.context)),
        ])
    except Exception as e:
        # A failed slot should not take down the other two — return a
        # degraded review for this slot only.
        logger.exception("slot%d (%s) failed: %s", slot, provider.name, e)
        return {"partial_reviews": {key: CodeReview(
            summary=f"[{provider.name}] Reviewer failed: {e}",
            file_reviews=[],
        )}}

    review = parse_llm_response(str(response.content), provider.name)
    return {"partial_reviews": {key: review}}


# ── The three slot nodes ──────────────────────────────────────────────────────
# Thin wrappers so the graph can register them by distinct names.

async def slot1_node(state: ReviewState) -> dict:
    return await _run_slot(state, 1)


async def slot2_node(state: ReviewState) -> dict:
    return await _run_slot(state, 2)


async def slot3_node(state: ReviewState) -> dict:
    return await _run_slot(state, 3)


# ── Merge ─────────────────────────────────────────────────────────────────────

def merge_node(state: ReviewState) -> dict:
    """
    Mechanically union all partial reviews from the parallel slots into one
    CodeReview, written to the `merged_review` channel.

    This is a deterministic, no-LLM step. The consolidate node afterwards
    hands this result to SLOT 1's model for deduplication / tidying — and
    falls back to this merged review verbatim if SLOT 1 is unavailable.

    Strategy:
    - Summaries: concatenated with a divider, labelled by slot key.
    - Findings: unioned per file, preserving all findings from all slots.
    """
    print("MERGE NODE ENTERED", flush=True)

    reviews = state.partial_reviews
    
    logger.info("merge_node: consolidating %d slot result(s): %s",
                len(reviews), ", ".join(sorted(reviews)))
    
    if not reviews:
        return {"merged_review": CodeReview(
            summary="No reviewers produced output. Check that at least one of "
                    "SLOT1_PROVIDER / SLOT2_PROVIDER / SLOT3_PROVIDER is set to "
                    "a configured provider.",
            file_reviews=[],
        )}

    # Summaries, sorted by slot key for deterministic output.
    summary_parts = [
        f"**[{key.upper()}]** {reviews[key].summary}"
        for key in sorted(reviews)
    ]
    merged_summary = "\n\n---\n\n".join(summary_parts)

    # Union file-level findings across all slots.
    files: dict[str, list] = {}
    for review in reviews.values():
        for fr in review.file_reviews:
            files.setdefault(fr.filename, []).extend(fr.findings)

    from models import FileReview
    merged_file_reviews = [
        FileReview(filename=fname, findings=files[fname])
        for fname in sorted(files)
    ]

    return {"merged_review": CodeReview(
        summary=merged_summary,
        file_reviews=merged_file_reviews,
    )}


# ── Consolidate ───────────────────────────────────────────────────────────────

async def _try_consolidate_with_slot(
    slot: int, state: ReviewState
) -> tuple[CodeReview | None, str]:
    """
    Attempt the consolidation pass using SLOT{slot}'s provider/model.

    Returns (consolidated_review, "") on success, or (None, reason) if the
    slot is unconfigured, its provider is unknown / unavailable, the call or
    parsing fails, or the model returned an empty review despite findings in
    the input. The caller falls through to the next slot on None.
    """
    provider_name = os.getenv(f"SLOT{slot}_PROVIDER", "").strip()
    if not provider_name:
        return None, f"SLOT{slot}_PROVIDER not set"

    provider = get_provider(provider_name)
    if provider is None:
        return None, f"unknown SLOT {slot} provider '{provider_name}'"

    reason = provider.unavailable_reason()
    if reason is not None:
        return None, f"SLOT {slot} provider '{provider.name}' unavailable ({reason})"

    model = os.getenv(f"SLOT{slot}_MODEL", "").strip() or provider.default_model
    logger.info("consolidate: trying slot%d — provider=%s model=%s",
                slot, provider.name, model)

    had_findings = any(
        fr.findings
        for review in state.partial_reviews.values()
        for fr in review.file_reviews
    )

    try:
        llm = provider.build(model)
        response = await llm.ainvoke([
            SystemMessage(content=CONSOLIDATION_SYSTEM_PROMPT),
            HumanMessage(content=build_consolidation_prompt(state.partial_reviews)),
        ])
        consolidated = parse_llm_response(str(response.content), provider.name)
    except Exception as e:
        logger.exception("consolidate: slot%d (%s) failed: %s", slot, provider.name, e)
        return None, f"SLOT {slot} consolidation call failed: {e}"

    # Guard against a model that drops everything: if it produced an empty
    # review while the input had findings, treat the slot as failed so the
    # caller can fall through to the next one.
    if not consolidated.file_reviews and had_findings:
        return None, f"SLOT {slot} consolidation returned no findings despite input"

    return consolidated, ""


async def consolidate_node(state: ReviewState) -> dict:
    """
    Final pass: hand every reviewer slot's output to a configured model so it
    can deduplicate findings, fix malformed entries, and tidy the format.

    SLOT 1's model is tried first. If it is unconfigured, its provider is
    unknown / unavailable, or the call/parsing fails, SLOT 2 is tried, then
    SLOT 3. Only if all three fail is the mechanical merge used verbatim, so
    the pipeline always produces a result.

    The consolidating model is instructed (see CONSOLIDATION_SYSTEM_PROMPT)
    to merge the slots' outputs into one unified review — deduplicating
    findings and dropping per-reviewer attribution so the result reads as a
    single coherent review.
    """
    merged = state.merged_review
    if merged is None:
        # merge_node always writes merged_review, so this is defensive only.
        return {"final_review": CodeReview(
            summary="No merged review available to consolidate.",
            file_reviews=[],
        )}

    failures: list[str] = []
    for slot in (1, 2, 3):
        consolidated, reason = await _try_consolidate_with_slot(slot, state)
        if consolidated is not None:
            logger.info("consolidate: slot%d produced the final review.", slot)
            return {"final_review": consolidated}
        failures.append(reason)

    logger.info(
        "consolidate: all slots failed (%s) — using mechanical merge.",
        "; ".join(failures),
    )
    return {"final_review": merged}