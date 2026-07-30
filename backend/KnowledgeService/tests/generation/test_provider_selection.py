"""Cover the GENERATION_PROVIDER switch itself.

The switch is one line of config that silently decides whether a summary runs on a hosted
API or on the host CPU. These pin that it routes correctly, that each use case keeps its own
model and budget, and that a typo degrades to the local backend instead of crashing at
startup or — worse — half-configuring.
"""

from __future__ import annotations

import pytest

from app.api.dependencies import (
    build_generation_provider,
    get_generation_provider,
    get_summary_generation_provider,
)
from app.infrastructure.config.settings import Settings
from app.infrastructure.generation.gemini_generation_provider import GeminiGenerationProvider
from app.infrastructure.generation.ollama_generation_provider import OllamaGenerationProvider


def _build(provider: str):
    return build_generation_provider(
        Settings(generation_provider=provider),
        ollama_model="local-model",
        gemini_model="hosted-model",
        temperature=0.5,
        max_tokens=123,
    )


@pytest.mark.parametrize("value", ["gemini", "GEMINI", "  gemini  "])
def test_gemini_is_selected_case_and_space_insensitively(value: str) -> None:
    assert isinstance(_build(value), GeminiGenerationProvider)


def test_ollama_is_selected() -> None:
    assert isinstance(_build("ollama"), OllamaGenerationProvider)


def test_unknown_provider_falls_back_to_ollama() -> None:
    """A typo must not take the service down, and must not silently reach for a paid API."""
    assert isinstance(_build("gemeni"), OllamaGenerationProvider)


def test_each_backend_gets_its_own_model_name() -> None:
    """An Ollama tag and a Gemini model are not interchangeable; the wrong one 404s."""
    assert _build("gemini")._model == "hosted-model"
    assert _build("ollama")._model == "local-model"


def test_portable_parameters_reach_both_backends() -> None:
    for provider in ("gemini", "ollama"):
        built = _build(provider)
        assert built._temperature == 0.5, provider
        assert built._max_tokens == 123, provider


def test_summary_and_answering_use_separate_models_and_budgets() -> None:
    """Summaries are the largest generation the service does and are tuned separately."""
    settings = Settings(
        generation_provider="gemini",
        gemini_generation_model="answer-model",
        gemini_summary_model="summary-model",
        generation_max_tokens=1024,
        summary_max_tokens=1500,
    )

    answering = get_generation_provider(settings)
    summary = get_summary_generation_provider(settings)

    assert answering._model == "answer-model"
    assert summary._model == "summary-model"
    assert answering._max_tokens == 1024
    assert summary._max_tokens == 1500


def test_the_switch_moves_summaries_too() -> None:
    """The regression guard: summarization must not stay pinned to Ollama."""
    gemini = get_summary_generation_provider(Settings(generation_provider="gemini"))
    ollama = get_summary_generation_provider(Settings(generation_provider="ollama"))

    assert isinstance(gemini, GeminiGenerationProvider)
    assert isinstance(ollama, OllamaGenerationProvider)
