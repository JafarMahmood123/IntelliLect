"""A deterministic ``EmbeddingProvider`` for testing LA-3 drift boundaries with no
live model.

Vectors are chosen by matching topic keywords in the text, so semantic drift is fully
predictable: same-topic segments get the same vector (distance 0), different-topic
segments get a far vector (distance controlled by the test). Lives under
``tests/support`` because it is test scaffolding.
"""

from __future__ import annotations

from app.application.ports.embedding_provider import EmbeddingProvider


class FakeEmbeddingProvider(EmbeddingProvider):
    """Returns a fixed vector per matched topic keyword (case-insensitive).

    The first keyword found in the text (in insertion order) selects the vector; if
    none match, ``default`` is returned. Deterministic and dependency-free.
    """

    def __init__(
        self, topic_vectors: dict[str, list[float]], default: list[float]
    ) -> None:
        self._topics = {k.lower(): list(v) for k, v in topic_vectors.items()}
        self._default = list(default)
        self.calls: list[str] = []  # record inputs so tests can assert what was embedded

    async def embed_query(self, text: str) -> list[float]:
        self.calls.append(text)
        lowered = text.lower()
        for keyword, vector in self._topics.items():
            if keyword in lowered:
                return list(vector)
        return list(self._default)

    @classmethod
    def orthogonal_topics(cls, keywords: list[str]) -> "FakeEmbeddingProvider":
        """Build one-hot vectors per keyword: within-topic distance 0, cross-topic 1.0.

        A trailing dimension is the "default" axis for text matching no keyword, so it
        stays orthogonal (maximally far) from every topic.
        """
        dim = len(keywords) + 1
        topics = {
            keyword: [1.0 if i == index else 0.0 for i in range(dim)]
            for index, keyword in enumerate(keywords)
        }
        default = [1.0 if i == len(keywords) else 0.0 for i in range(dim)]
        return cls(topics, default)
