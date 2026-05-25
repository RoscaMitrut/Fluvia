"""
Provider registry for the review pipeline.

Each of the three review slots picks a provider by name. A provider knows:
  - which credentials it needs (and whether they are present)
  - how to build a LangChain chat model for a given model name

Supported providers:
  ollama       — local Ollama server (no API key; OLLAMA_BASE_URL)
  huggingface  — Hugging Face Inference router, OpenAI-compatible
                 (HUGGINGFACE_API_KEY; HUGGINGFACE_BASE_URL)
  google       — Google AI Studio / Gemini (GOOGLE_API_KEY)

Adding a provider is a single entry in the PROVIDERS dict below — the slot
nodes and graph wiring never change.
"""
from __future__ import annotations

import os
from dataclasses import dataclass
from typing import Callable, Optional

from langchain_core.language_models.chat_models import BaseChatModel


# Shared cap on output length. Each provider passes this through under
# whatever parameter name its SDK expects.
MAX_TOKENS = 4096


@dataclass(frozen=True)
class Provider:
    """Describes one LLM provider."""
    name: str

    # Returns a human-readable reason the provider is unavailable, or None
    # if it is ready to use (all required config present).
    unavailable_reason: Callable[[], Optional[str]]

    # Builds a chat model for the given model name. Only called when
    # unavailable_reason() returned None.
    build: Callable[[str], BaseChatModel]

    # Default model used when SLOT{N}_MODEL is not set.
    default_model: str


# ── Ollama ────────────────────────────────────────────────────────────────────

def _ollama_unavailable() -> Optional[str]:
    # Ollama needs no API key; it just needs a reachable base URL. We can't
    # cheaply prove reachability here, so treat it as always "available" and
    # let the actual call fail loudly if the server is down.
    return None


def _ollama_build(model: str) -> BaseChatModel:
    from langchain_ollama import ChatOllama

    return ChatOllama(
        model=model,
        base_url=os.getenv("OLLAMA_BASE_URL", "http://ollama-server:11434"),
        format="json",          # JSON mode for models that honour it
        num_predict=MAX_TOKENS,
    )


# ── Hugging Face (OpenAI-compatible router) ───────────────────────────────────

def _huggingface_unavailable() -> Optional[str]:
    if not os.getenv("HUGGINGFACE_API_KEY"):
        return "HUGGINGFACE_API_KEY is not set"
    return None


def _huggingface_build(model: str) -> BaseChatModel:
    # The HF Inference router exposes an OpenAI-compatible /v1 endpoint, so we
    # reuse ChatOpenAI rather than pulling in another SDK. This mirrors the
    # .NET HuggingFaceCodeReviewer, which does the same thing.
    from langchain_openai import ChatOpenAI

    base_url = os.getenv("HUGGINGFACE_BASE_URL", "https://router.huggingface.co")
    return ChatOpenAI(
        model=model,
        api_key=os.environ["HUGGINGFACE_API_KEY"],
        base_url=base_url.rstrip("/") + "/v1",
        max_tokens=MAX_TOKENS,
        # Many HF-hosted models honour response_format; parser.py covers the
        # rest by stripping fences.
        model_kwargs={"response_format": {"type": "json_object"}},
    )


# ── Google AI Studio (Gemini) ─────────────────────────────────────────────────

def _google_unavailable() -> Optional[str]:
    if not os.getenv("GOOGLE_API_KEY"):
        return "GOOGLE_API_KEY is not set"
    return None


def _google_build(model: str) -> BaseChatModel:
    from langchain_google_genai import ChatGoogleGenerativeAI

    return ChatGoogleGenerativeAI(
        model=model,
        google_api_key=os.environ["GOOGLE_API_KEY"],
        max_output_tokens=MAX_TOKENS,
        # Gemini can emit raw JSON directly; parser.py still strips fences as
        # a fallback for any model that wraps its output.
        model_kwargs={"response_mime_type": "application/json"},
    )


# ── Registry ──────────────────────────────────────────────────────────────────

PROVIDERS: dict[str, Provider] = {
    "ollama": Provider(
        name="ollama",
        unavailable_reason=_ollama_unavailable,
        build=_ollama_build,
        default_model="qwen2.5-coder:7b",
    ),
    "huggingface": Provider(
        name="huggingface",
        unavailable_reason=_huggingface_unavailable,
        build=_huggingface_build,
        default_model="meta-llama/Llama-3.3-70B-Instruct",
    ),
    "google": Provider(
        name="google",
        unavailable_reason=_google_unavailable,
        build=_google_build,
        default_model="gemini-2.0-flash",
    ),
}


def get_provider(name: str) -> Optional[Provider]:
    """Look up a provider by name (case-insensitive). None if unknown."""
    return PROVIDERS.get(name.strip().lower()) if name else None


def known_provider_names() -> list[str]:
    return sorted(PROVIDERS)