"""The recursive splitter and overlap packer that every chunking strategy sits on.

Both chunkers delegate their sizing to this file, so its promise — *no chunk exceeds
max_tokens* — is the one thing standing between a real lecture PDF and an embedding call the
model truncates. Truncation is the failure mode that matters: the API accepts the request, bills
it, and returns a vector for the first part of the text. The chunk is then indexed under a
vector that does not describe most of it, and nothing anywhere reports a problem.

The paths the existing tests did not reach are the awkward ones — the hard character wrap, and
the overlap seed being shrunk to make room — which is exactly where a real document ends up.
"""

from __future__ import annotations

from app.application.services.token_counter import HeuristicTokenCounter
from app.domain.extraction.text_block import TextBlockSource
from app.infrastructure.chunking._text_splitter import (
    Atom,
    RecursiveTextSplitter,
    split_sentences,
)

from tests.chunking.fixtures import text_block


class WordCounter:
    """One token per whitespace-separated word — so the arithmetic in a test is readable."""

    def count(self, text: str) -> int:
        return len(text.split())


def _splitter(max_tokens: int, overlap: int = 0, counter=None) -> RecursiveTextSplitter:
    return RecursiveTextSplitter(counter or WordCounter(), max_tokens, overlap)


def _atoms(*texts: str) -> list[Atom]:
    return [Atom(text=text, source=TextBlockSource.NATIVE) for text in texts]


# --- sentence splitting -------------------------------------------------------------------


def test_sentences_are_split_on_end_punctuation_and_stripped():
    assert split_sentences("First one.  Second one!  Third one?") == [
        "First one.",
        "Second one!",
        "Third one?",
    ]


def test_empty_text_yields_no_sentences():
    assert split_sentences("   \n  ") == []


# --- atomization --------------------------------------------------------------------------


def test_text_that_already_fits_is_left_whole():
    splitter = _splitter(max_tokens=10)

    assert splitter.atomize("three little words") == ["three little words"]


def test_the_coarsest_separator_that_actually_splits_is_preferred():
    """Paragraphs before sentences before words.

    Splitting a paragraph that would have fitted whole scatters its sentences across chunks,
    and each fragment then retrieves on its own without the context that made it meaningful.
    """
    splitter = _splitter(max_tokens=4)

    pieces = splitter.atomize("Alpha beta. Gamma delta.\n\nEpsilon zeta. Eta theta.")

    # Two paragraphs, each keeping both of its sentences — not four separate sentences.
    assert pieces == ["Alpha beta. Gamma delta.", "Epsilon zeta. Eta theta."]


def test_a_paragraph_too_large_for_the_budget_falls_through_to_sentences():
    splitter = _splitter(max_tokens=3)

    assert splitter.atomize("Alpha beta gamma. Delta epsilon zeta.") == [
        "Alpha beta gamma.",
        "Delta epsilon zeta.",
    ]


def test_a_sentence_too_large_for_the_budget_falls_through_to_words():
    splitter = _splitter(max_tokens=2)

    pieces = splitter.atomize("one two three four five")

    assert all(WordCounter().count(piece) <= 2 for piece in pieces)
    assert " ".join(pieces) == "one two three four five"


def test_a_single_unsplittable_token_is_wrapped_by_characters():
    """The last resort, and not a hypothetical one.

    PDF extraction routinely produces one enormous run-on token: a base64 image blob, a URL, a
    table flattened without spaces. With no separator to recurse on, the only options are to
    emit it over budget — where the embedder truncates it — or to cut it. It gets cut.
    """
    counter = HeuristicTokenCounter()  # ~4 characters per token
    splitter = _splitter(max_tokens=5, counter=counter)
    blob = "x" * 400

    pieces = splitter.atomize(blob)

    assert len(pieces) > 1
    assert all(counter.count(piece) <= 5 for piece in pieces)


def test_the_character_wrap_loses_and_duplicates_nothing():
    # A slicing bug here drops or repeats characters in the middle of the corpus, which is not
    # visible in a chunk count or a token count — only in the text itself.
    counter = HeuristicTokenCounter()
    splitter = _splitter(max_tokens=6, counter=counter)
    blob = "".join(chr(ord("a") + i % 26) for i in range(500))

    assert "".join(splitter.atomize(blob)) == blob


def test_a_long_word_among_short_ones_is_wrapped_without_dragging_them_in():
    # The word packer emits a lone over-budget word as its own piece, which then needs the
    # character wrap. Getting this wrong caps the whole document at that word.
    counter = HeuristicTokenCounter()
    splitter = _splitter(max_tokens=4, counter=counter)

    pieces = splitter.atomize("short " + "y" * 200 + " tail")

    assert all(counter.count(piece) <= 4 for piece in pieces)
    assert "short" in pieces[0]
    assert pieces[-1].endswith("tail")


def test_blocks_keep_the_source_they_came_from():
    """A chunk that mixes native text and OCR output is tagged by what it contains.

    OCR text is lower confidence, and the source tag is what lets a reader (or a later filter)
    know that. It is carried per atom because a single chunk can span both.
    """
    splitter = _splitter(max_tokens=10)

    atoms = splitter.atomize_blocks(
        [
            text_block(0, "native paragraph", source=TextBlockSource.NATIVE),
            text_block(1, "scanned paragraph", source=TextBlockSource.OCR),
        ]
    )

    assert [atom.source for atom in atoms] == [
        TextBlockSource.NATIVE,
        TextBlockSource.OCR,
    ]


def test_empty_blocks_contribute_nothing():
    # Extraction emits blank blocks for spacer paragraphs and empty table cells; an empty atom
    # would become an empty chunk, and an embedding call for nothing.
    splitter = _splitter(max_tokens=10)

    atoms = splitter.atomize_blocks(
        [
            text_block(0, "   \n  ", source=TextBlockSource.NATIVE),
            text_block(1, "real text", source=TextBlockSource.NATIVE),
        ]
    )

    assert [atom.text for atom in atoms] == ["real text"]


# --- packing ------------------------------------------------------------------------------


def test_packing_never_exceeds_the_budget():
    splitter = _splitter(max_tokens=3, overlap=1)

    chunks = splitter.pack(_atoms(*[f"w{i}" for i in range(9)]))

    assert all(splitter.count_atoms(chunk) <= 3 for chunk in chunks)


def test_consecutive_chunks_share_their_overlap_atoms_by_identity():
    """The overlap contract, and it is an identity contract rather than an equality one.

    Overlap exists so a sentence spanning a chunk boundary is retrievable from either side. The
    callers dedupe it with `id()` — comparing text instead would also delete a sentence that
    genuinely repeats in the document.
    """
    splitter = _splitter(max_tokens=3, overlap=1)
    atoms = _atoms("a", "b", "c", "d", "e", "f")

    chunks = splitter.pack(atoms)

    assert len(chunks) > 1
    for previous, following in zip(chunks, chunks[1:]):
        assert following[0] is previous[-1]


def test_no_overlap_is_configurable_and_really_means_none():
    splitter = _splitter(max_tokens=3, overlap=0)
    atoms = _atoms("a", "b", "c", "d", "e", "f")

    chunks = splitter.pack(atoms)

    flattened = [id(atom) for chunk in chunks for atom in chunk]
    assert len(flattened) == len(set(flattened))


def test_the_overlap_seed_shrinks_so_the_next_atom_still_fits():
    """The case where overlap and the token cap disagree.

    A full-size overlap seed plus a large incoming atom is over budget. Keeping the seed would
    put the chunk over `max_tokens` — quietly, since nothing downstream re-measures it — so the
    seed gives way. The budget is the hard constraint; the overlap is the preference.
    """
    splitter = _splitter(max_tokens=5, overlap=4)
    atoms = _atoms("a", "b", "c", "d", "one two three four")

    chunks = splitter.pack(atoms)

    assert all(splitter.count_atoms(chunk) <= 5 for chunk in chunks)
    # The seed was trimmed to the single atom that leaves room for the incoming one.
    assert [atom.text for atom in chunks[-1]] == ["d", "one two three four"]


def test_an_atom_that_fills_the_budget_alone_still_gets_its_own_chunk():
    # Otherwise a document containing one maximal atom produces no chunk for it at all.
    splitter = _splitter(max_tokens=3, overlap=2)
    atoms = _atoms("a", "one two three")

    chunks = splitter.pack(atoms)

    assert [atom.text for atom in chunks[-1]] == ["one two three"]


def test_packing_nothing_produces_nothing():
    assert _splitter(max_tokens=3).pack([]) == []


# --- the trailing runt --------------------------------------------------------------------


def test_a_tiny_trailing_chunk_is_absorbed_into_its_predecessor():
    """A greedy packer strands whatever is left at the end.

    That runt is often a heading or a closing line — short, low-information, and as a chunk of
    its own it matches queries on the strength of a couple of words with no surrounding context.
    """
    splitter = _splitter(max_tokens=2, overlap=1)
    chunks = splitter.pack(_atoms("a", "b", "c", "d", "e"))

    merged = splitter.merge_small_tail(chunks, min_tokens=3)

    assert splitter.count_atoms(merged[-1]) >= 3
    assert len(merged) == len(chunks) - 1


def test_the_absorbed_runt_does_not_bring_its_overlap_along_twice():
    # The runt starts with atoms it shares with the chunk it is being merged into. Appending it
    # wholesale would repeat that text inside the merged chunk.
    splitter = _splitter(max_tokens=2, overlap=1)
    chunks = splitter.pack(_atoms("a", "b", "c", "d", "e"))

    merged = splitter.merge_small_tail(chunks, min_tokens=3)

    texts = [atom.text for atom in merged[-1]]
    assert texts == sorted(set(texts), key=texts.index)  # no duplicates
    assert texts == ["c", "d", "e"]


def test_a_lone_chunk_is_never_merged_away():
    # A short document is allowed to be one short chunk; merging would leave nothing.
    splitter = _splitter(max_tokens=10)
    chunks = splitter.pack(_atoms("a"))

    assert splitter.merge_small_tail(chunks, min_tokens=50) == chunks


def test_a_healthy_tail_is_left_alone():
    splitter = _splitter(max_tokens=3, overlap=0)
    chunks = splitter.pack(_atoms("a", "b", "c", "d", "e", "f"))

    assert splitter.merge_small_tail(chunks, min_tokens=2) == chunks
