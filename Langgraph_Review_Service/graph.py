"""
Review pipeline graph.

                    ┌─────────────────┐
                    │      START      │
                    └────────┬────────┘
                             │  fan-out (parallel edges)
              ┌──────────────┼──────────────┐
              ▼              ▼              ▼
         slot1_node     slot2_node     slot3_node
              │              │              │
              └──────────────┼──────────────┘
                             │  fan-in
                    ┌────────▼────────┐
                    │   merge_node    │
                    └────────┬────────┘
                             │
                    ┌────────▼────────┐
                    │ consolidate_node│  ← SLOT 1's model dedups / tidies
                    └────────┬────────┘
                             │
                           END

Three identical review slots run concurrently via LangGraph's native async
fan-out. Each slot picks its provider + model from SLOT{N}_PROVIDER /
SLOT{N}_MODEL env vars (see nodes.py / providers.py). Slots that are
unconfigured or lack credentials write nothing and are effectively no-ops.

All slots write the `partial_reviews` channel, which has a reducer (see
models.py) so the concurrent writes union rather than collide.

merge_node mechanically unions every slot's output. consolidate_node then
hands that merged review to SLOT 1's model for a final dedup + format pass;
if SLOT 1 is unconfigured or its call fails, the mechanical merge is used
as-is so the pipeline always produces a result.
"""
from langgraph.graph import StateGraph, START, END
from langgraph.graph.state import CompiledStateGraph

from models import ReviewState
from nodes import slot1_node, slot2_node, slot3_node, merge_node, consolidate_node


def build_graph() -> CompiledStateGraph:
    builder = StateGraph(ReviewState)

    # ── Nodes ─────────────────────────────────────────────────────────────────
    builder.add_node("slot1", slot1_node)
    builder.add_node("slot2", slot2_node)
    builder.add_node("slot3", slot3_node)
    builder.add_node("merge", merge_node)
    builder.add_node("consolidate", consolidate_node)

    # ── Fan-out: START → all three slots, concurrently ────────────────────────
    builder.add_edge(START, "slot1")
    builder.add_edge(START, "slot2")
    builder.add_edge(START, "slot3")

    # ── Fan-in: all slots → merge ─────────────────────────────────────────────
    builder.add_edge("slot1", "merge")
    builder.add_edge("slot2", "merge")
    builder.add_edge("slot3", "merge")

    # ── Consolidate: SLOT 1's model dedups / tidies the merged review ─────────
    builder.add_edge("merge", "consolidate")

    # ── Terminate ─────────────────────────────────────────────────────────────
    builder.add_edge("consolidate", END)

    return builder.compile()


# Module-level singleton — compiled once at startup.
review_graph: CompiledStateGraph = build_graph()