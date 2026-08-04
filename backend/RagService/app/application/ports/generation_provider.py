from __future__ import annotations

from abc import ABC, abstractmethod


class GenerationProvider(ABC):
    """Port for generating a chat completion from a system + user prompt.

    Implemented in the infrastructure layer (e.g. OllamaGenerationProvider). The
    application/domain layers depend only on this abstraction. Streaming is an
    optional capability offered by concrete implementations, not part of the port.
    """

    @abstractmethod
    async def generate(self, system: str, prompt: str) -> str:
        """Return the assistant's answer for the given system + user prompt."""
        raise NotImplementedError
