from abc import ABC, abstractmethod


class EmbeddingProvider(ABC):
    """Port for turning text into embedding vectors.

    Implemented in the infrastructure layer (e.g. OllamaEmbeddingProvider). The
    application/domain layers depend only on this abstraction.
    """

    @abstractmethod
    async def embed_documents(self, texts: list[str]) -> list[list[float]]:
        """Embed a batch of document/passage texts for indexing."""
        raise NotImplementedError

    @abstractmethod
    async def embed_query(self, text: str) -> list[float]:
        """Embed a single query text for retrieval."""
        raise NotImplementedError
