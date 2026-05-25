from __future__ import annotations
import json
import re
from models import CodeReview, FileReview, Finding, Severity


def parse_llm_response(raw: str, provider: str) -> CodeReview:
    """
    Parse a raw LLM string into a CodeReview.
    Handles models that wrap JSON in ```json ... ``` fences despite being asked
    not to, and maps camelCase keys from the prompt schema to snake_case models.
    """
    json_str = _extract_json(raw)

    try:
        data = json.loads(json_str)
    except json.JSONDecodeError as e:
        # Return a degraded review rather than crashing the whole graph
        return CodeReview(
            summary=f"[{provider}] Review could not be parsed: {e}\n\nRaw output:\n{raw}",
            file_reviews=[],
        )

    summary = data.get("summary", "No summary provided.")
    file_reviews: list[FileReview] = []

    for fr in data.get("fileReviews", []):
        findings: list[Finding] = []
        for f in fr.get("findings", []):
            try:
                severity = Severity(f.get("severity", "Info"))
            except ValueError:
                severity = Severity.Info

            findings.append(Finding(
                severity=severity,
                description=f.get("description", ""),
                agent_prompt=f.get("agentPrompt", ""),
                location=f.get("location"),
            ))

        file_reviews.append(FileReview(
            filename=fr.get("filename", "unknown"),
            findings=findings,
        ))

    return CodeReview(summary=summary, file_reviews=file_reviews)


def _extract_json(raw: str) -> str:
    """Strip markdown fences and extract the outermost JSON object."""
    fence = re.search(r"```(?:json)?\s*(\{[\s\S]*?\})\s*```", raw)
    if fence:
        return fence.group(1)

    start = raw.find("{")
    end   = raw.rfind("}")
    if start >= 0 and end > start:
        return raw[start : end + 1]

    return raw