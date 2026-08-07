from __future__ import annotations

import re
from dataclasses import dataclass

from app.application.services.token_counter import TokenCounter
from app.domain.extraction.text_block import TextBlock, TextBlockSource

# Sentence-ending punctuation, followed by whitespace. Deterministic and offline — good enough
# for chunking, not linguistic-grade segmentation.
#
# **Not only the ASCII three.** This was `[.!?]`, which is every terminator English uses and
# none of the ones Arabic adds — and Arabic is the platform's second language. Arabic prose does
# borrow the Latin full stop, so the failure was partial and therefore invisible: statements
# split, questions did not. "ما هو التعلم العميق؟" is how a lecture asks a question, and four
# such sentences in a row came back as one.
#
# What that costs is not obvious from here, so it is written down: `SemanticChunker` embeds each
# sentence and puts a chunk boundary where meaning shifts. Handed a single "sentence" it takes
# the `len(sentences) == 1` path, never calls the embedder, and falls through to the token-window
# packer — so semantic chunking silently becomes structural chunking, and
# `semantic_breakpoint_percentile` has no effect at all. Nothing errors and the chunks look fine.
#
# ؟ U+061F Arabic question mark, ۔ U+06D4 Arabic full stop (also Urdu/Persian), … U+2026
# ellipsis. The ideographic and fullwidth marks cost one character each and are here so the next
# script does not have to rediscover this; Arabic is the one covered by tests, because it is the
# one the product ships.
_TERMINATORS = ".!?؟۔…。！？"


def _boundary_pattern(terminators: str) -> re.Pattern[str]:
    """A "one of these, then whitespace" pattern, with every character taken literally.

    The escaping is the whole reason this is a function. Inside a character class the current
    terminators are already literal, so `re.escape` changes nothing today — but a later addition
    of `-`, `]` or `^` would turn the class into a RANGE or a negation, and the result would still
    compile and still split text, just not the text anyone intended. Extracted so a test can hold
    that property instead of leaving it as an assumption about characters nobody has added yet.
    """
    return re.compile(rf"(?<=[{re.escape(terminators)}])\s+")


_SENTENCE_RE = _boundary_pattern(_TERMINATORS)

# Atom join separator; also used when recounting a candidate chunk's tokens.
_JOIN = " "


@dataclass
class Atom:
    """The smallest indivisible unit of text the packer moves around.

    Carries the `source` of the block it came from so a chunk that ends up mixing
    NATIVE and OCR atoms can be tagged accordingly.
    """

    text: str
    source: TextBlockSource


def split_sentences(text: str) -> list[str]:
    """Split text into non-empty, stripped sentences."""
    stripped = text.strip()
    if not stripped:
        return []
    return [part.strip() for part in _SENTENCE_RE.split(stripped) if part.strip()]


class RecursiveTextSplitter:
    """Token-aware recursive splitter + overlap packer.

    `atomize` breaks text down (paragraph -> line -> sentence -> word -> hard char
    wrap) until every atom fits `max_tokens`. `pack` greedily fills chunks up to
    `max_tokens`, seeding each new chunk with the tail of the previous one to create
    `overlap_tokens` of overlap. All sizing goes through the injected TokenCounter.
    """

    def __init__(
        self, counter: TokenCounter, max_tokens: int, overlap_tokens: int
    ) -> None:
        self._counter = counter
        self._max = max(1, max_tokens)
        self._overlap = max(0, overlap_tokens)

    # --- sizing helpers -------------------------------------------------------

    @staticmethod
    def join(atoms: list[Atom]) -> str:
        return _JOIN.join(atom.text for atom in atoms)

    def count_atoms(self, atoms: list[Atom]) -> int:
        return self._counter.count(self.join(atoms))

    # --- atomization ----------------------------------------------------------

    def atomize_blocks(self, blocks: list[TextBlock]) -> list[Atom]:
        """Atomize each block's text, tagging every atom with the block's source.

        Empty/whitespace blocks are ignored.
        """
        atoms: list[Atom] = []
        for block in blocks:
            text = block.text.strip()
            if not text:
                continue
            for piece in self.atomize(text):
                atoms.append(Atom(text=piece, source=block.source))
        return atoms

    def atomize(self, text: str) -> list[str]:
        """Split text into pieces that each fit `max_tokens`, recursively."""
        text = text.strip()
        if not text:
            return []
        if self._counter.count(text) <= self._max:
            return [text]

        # Prefer the coarsest separator that actually splits the text.
        for separator in ("\n\n", "\n"):
            if separator in text:
                parts = [part for part in text.split(separator) if part.strip()]
                if len(parts) > 1:
                    return self._flatten(parts)

        sentences = split_sentences(text)
        if len(sentences) > 1:
            return self._flatten(sentences)

        words = text.split(" ")
        if len(words) > 1:
            return self._wrap_words(words)

        # A single unsplittable token longer than the budget: hard char wrap.
        return self._hard_wrap(text)

    def _flatten(self, parts: list[str]) -> list[str]:
        pieces: list[str] = []
        for part in parts:
            pieces.extend(self.atomize(part))
        return pieces

    def _wrap_words(self, words: list[str]) -> list[str]:
        """Greedily pack space-separated words into <= max_tokens pieces."""
        pieces: list[str] = []
        current: list[str] = []
        for word in words:
            candidate = " ".join(current + [word])
            if current and self._counter.count(candidate) > self._max:
                pieces.append(" ".join(current))
                current = [word]
            else:
                current.append(word)
        if current:
            pieces.append(" ".join(current))

        # A lone word still over budget -> hard char wrap.
        result: list[str] = []
        for piece in pieces:
            if self._counter.count(piece) > self._max:
                result.extend(self._hard_wrap(piece))
            else:
                result.append(piece)
        return result

    def _hard_wrap(self, text: str) -> list[str]:
        """Last resort: slice by characters into <= max_tokens windows.

        Counter-agnostic — grows/shrinks each window against the real TokenCounter.
        """
        pieces: list[str] = []
        start = 0
        length = len(text)
        while start < length:
            end = min(length, start + max(1, self._max * 4))
            while end > start + 1 and self._counter.count(text[start:end]) > self._max:
                end -= 1
            while end < length and self._counter.count(text[start : end + 1]) <= self._max:
                end += 1
            pieces.append(text[start:end])
            start = end
        return pieces

    # --- packing with overlap -------------------------------------------------

    def pack(self, atoms: list[Atom]) -> list[list[Atom]]:
        """Pack atoms into chunks of <= max_tokens with overlap between them.

        New chunks reuse the *same* trailing Atom objects from the previous chunk as
        their overlap seed, so callers can dedupe overlap by object identity.
        """
        chunks: list[list[Atom]] = []
        current: list[Atom] = []
        for atom in atoms:
            if current and self.count_atoms(current + [atom]) > self._max:
                chunks.append(current)
                seed = self._overlap_seed(current)
                # Guarantee the incoming atom fits after the overlap seed.
                while seed and self.count_atoms(seed + [atom]) > self._max:
                    seed = seed[1:]
                current = seed + [atom]
            else:
                current = current + [atom]
        if current:
            chunks.append(current)
        return chunks

    def _overlap_seed(self, atoms: list[Atom]) -> list[Atom]:
        """Trailing atoms of `atoms` whose combined size is <= overlap_tokens."""
        if self._overlap <= 0:
            return []
        seed: list[Atom] = []
        for atom in reversed(atoms):
            if seed and self.count_atoms([atom] + seed) > self._overlap:
                break
            seed = [atom] + seed
        return seed

    def merge_small_tail(
        self, chunks: list[list[Atom]], min_tokens: int
    ) -> list[list[Atom]]:
        """Merge a trailing chunk smaller than `min_tokens` into its predecessor.

        A greedy packer only leaves a sub-`min_tokens` final chunk when that content
        did not fit the previous chunk, so absorbing it is a deliberate, bounded
        relaxation of the token cap (the runt is < `min_tokens`, so the merged chunk
        exceeds `max_tokens` by less than that). This avoids stranding tiny, low-value
        chunks. Overlap atoms shared with the predecessor (by identity) are dropped so
        they are not duplicated.
        """
        merged = [list(chunk) for chunk in chunks]
        while len(merged) >= 2 and self.count_atoms(merged[-1]) < min_tokens:
            previous = merged[-2]
            last = merged[-1]
            previous_ids = {id(atom) for atom in previous}
            extra = [atom for atom in last if id(atom) not in previous_ids]
            merged = merged[:-2] + [previous + extra]
        return merged
