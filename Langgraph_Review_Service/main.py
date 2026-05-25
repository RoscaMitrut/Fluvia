"""
FastAPI service that wraps the LangGraph review pipeline.

Exposes:
  GET  /healthz  →  liveness probe
  POST /review   →  runs the parallel graph and returns a CodeReview

The .NET LangGraphCodeReviewer calls POST /review. That client serialises
PullRequestContext as snake_case JSON and expects a snake_case CodeReview
back (it reads `file_reviews`, `agent_prompt`), so this service neither
needs nor wants camelCase aliasing on the response.
"""
from __future__ import annotations

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException
from fastapi.responses import JSONResponse

from models import PullRequestContext, CodeReview, ReviewState
from graph import review_graph
logging.basicConfig(level=logging.INFO, force=True)  # force=True overrides uvicorn
logging.getLogger("nodes").setLevel(logging.INFO)

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info("Review service starting — graph compiled and ready.")
    yield
    logger.info("Review service shutting down.")


app = FastAPI(title="LangGraph Code Review Service", lifespan=lifespan)


@app.get("/healthz")
async def healthz():
    return {"status": "ok"}


@app.post("/review", response_model=CodeReview)
async def review(context: PullRequestContext) -> JSONResponse:
    logger.info(
        "Received review request for %s/%s PR #%d",
        context.repo_owner, context.repo_name, context.pr_number,
    )

    try:
        initial_state = ReviewState(context=context)
        # ainvoke returns the graph's channel values as a dict-like object,
        # NOT a ReviewState instance — so values are accessed by key. We
        # re-validate into ReviewState for type-safe access below.
        raw_result = await review_graph.ainvoke(initial_state)
        result = ReviewState.model_validate(dict(raw_result))
    except Exception as e:
        logger.exception("Graph execution failed: %s", e)
        raise HTTPException(status_code=500, detail=str(e))

    if result.final_review is None:
        raise HTTPException(status_code=500, detail="Graph produced no output.")

    logger.info(
        "Review complete for PR #%d — %d file(s) with findings.",
        context.pr_number,
        sum(1 for f in result.final_review.file_reviews if f.findings),
    )

    # by_alias=False → snake_case keys, matching what the .NET client parses.
    
    print(f"REVIEW RETURNING for PR #{context.pr_number}, "
        f"final_review id={id(result.final_review)}, "
        f"summary[:60]={result.final_review.summary[:60]!r}", flush=True)
    
    return JSONResponse(result.final_review.model_dump(by_alias=False))