"""Sentence boundaries in a language the product ships in (work-plan P3).

P3 is parked as "needs running containers", and its stated blocker is whether the retrieval
embedder handles Arabic — a question about a model. This is the other half, which needs nothing
running: whether the **pipeline in front of the embedder** handles Arabic. It did not.

`_SENTENCE_RE` was `(?<=[.!?])\\s+` — every terminator English uses, and none of the ones Arabic
adds. Arabic borrows the Latin full stop, so statements split correctly and the failure showed up
only on questions and on the Arabic full stop `۔`. A partial failure is the hard kind: the feature
looks like it works.

**What it costs.** `SemanticChunker` embeds each sentence and places a chunk boundary where the
meaning shifts. Handed one "sentence" it takes the `len(sentences) == 1` shortcut, never calls the
embedder at all, and falls through to the token-window packer — so for Arabic the semantic tier
silently became the structural tier and `semantic_breakpoint_percentile` had no effect. No error,
no warning, and chunks that look perfectly reasonable until someone asks why retrieval is worse in
Arabic than in English.

Educational material is the worst possible content for this. "ما هو التعلم العميق؟" — *what is
deep learning?* — is how a lecture introduces a topic, and a run of them collapsed into one block.
"""

from __future__ import annotations

from uuid import uuid4

from app.application.services.token_counter import HeuristicTokenCounter
from app.infrastructure.chunking._text_splitter import (
    _TERMINATORS,
    _boundary_pattern,
    split_sentences,
)
from app.infrastructure.chunking.semantic_chunker import SemanticChunker

from tests.chunking.fixtures import (
    FakeEmbeddingProvider,
    result,
    settings_for,
    text_block,
)

DOC_ID = uuid4()
CLASS_ID = uuid4()

# Four questions, the shape a lecture actually uses. Under the old pattern this was one sentence.
ARABIC_QUESTIONS = (
    "ما هو التعلم العميق؟ "
    "كيف تعمل الشبكات العصبية؟ "
    "ما الفرق بينه وبين تعلم الآلة؟ "
    "أين يستخدم في الترجمة؟"
)

ARABIC_STATEMENTS = (
    "الذكاء الاصطناعي فرع من علوم الحاسوب. "
    "يهدف إلى بناء أنظمة تحاكي الذكاء البشري. "
    "تشمل تطبيقاته الترجمة الآلية."
)


# --- the splitter ---------------------------------------------------------------------


def test_arabic_questions_are_four_sentences_not_one() -> None:
    assert len(split_sentences(ARABIC_QUESTIONS)) == 4


def test_the_arabic_full_stop_ends_a_sentence() -> None:
    # U+06D4, used in Arabic typography and standard in Urdu and Persian. Distinct from the
    # Latin full stop, and previously invisible.
    assert len(split_sentences("هذه جملة أولى۔ وهذه جملة ثانية۔ وهذه ثالثة۔")) == 3


def test_arabic_statements_still_split_as_they_always_did() -> None:
    # These already worked, because Arabic borrows the Latin full stop — which is exactly why
    # the defect survived: the language appeared to be supported.
    assert len(split_sentences(ARABIC_STATEMENTS)) == 3


def test_a_mixed_paragraph_splits_on_both_alphabets_terminators() -> None:
    text = "ما هو التعلم العميق؟ هو فرع من تعلم الآلة. يستخدم الشبكات العصبية؟ نعم."
    assert len(split_sentences(text)) == 4


def test_english_is_unchanged() -> None:
    # The regression guard. Widening a character class is an easy way to start splitting things
    # that are not sentence ends.
    assert split_sentences("First one.  Second one!  Third one?") == [
        "First one.",
        "Second one!",
        "Third one?",
    ]


def test_a_decimal_point_still_does_not_split() -> None:
    # The rule is "terminator followed by WHITESPACE", and that is what keeps numbers, file
    # names and abbreviations intact. Worth pinning while the class is being widened.
    assert split_sentences("The rate is 3.5 percent and rising.") == [
        "The rate is 3.5 percent and rising."
    ]


def test_every_listed_terminator_actually_ends_a_sentence() -> None:
    # Both directions on the list. A character added to `_TERMINATORS` that the pattern does not
    # honour is a silent no-op, and one that is honoured but never listed is the defect this file
    # exists for.
    for terminator in _TERMINATORS:
        assert len(split_sentences(f"one{terminator} two")) == 2, terminator


def test_a_character_that_is_not_a_terminator_does_not_split() -> None:
    # The vacuum guard. A pattern broadened into matching any whitespace would satisfy every
    # assertion above and destroy sentence structure entirely.
    for benign in ",;:،؛-":
        assert len(split_sentences(f"one{benign} two")) == 1, benign


def test_a_terminator_is_taken_literally_and_never_as_a_range() -> None:
    # `-` between two characters in a class is a RANGE, and `[!-?]` covers most of ASCII
    # punctuation and every digit. The result compiles, splits text, and is wrong — which is the
    # same failure mode as the one this file was written for, one layer down.
    #
    # Nothing adds `-` today. That is exactly why the escaping needs a test rather than a reader
    # who happens to notice: a mutation removing it survives against the current list.
    pattern = _boundary_pattern("!-?")

    assert pattern.split("one5 two") == ["one5 two"]
    assert pattern.split("one! two") == ["one!", "two"]
    assert pattern.split("one? two") == ["one?", "two"]


def test_a_closing_bracket_does_not_break_the_class() -> None:
    # The other character that changes the meaning of a class rather than joining it.
    pattern = _boundary_pattern(".]")

    assert pattern.split("one] two") == ["one]", "two"]


# --- what it actually cost --------------------------------------------------------------


async def test_the_embedder_is_consulted_for_arabic_prose() -> None:
    """The consequence, not the cause.

    This is the assertion that would have caught it. With one "sentence" the semantic chunker
    returns early and `embed_documents_calls` stays at **0** — the tier is inert, and every other
    test in the chunking suite still passes because they are all written in English.
    """
    res = result("pdf", [text_block(0, ARABIC_QUESTIONS, page=1)])
    fake = FakeEmbeddingProvider({})

    chunker = SemanticChunker(
        settings_for(chunk_max_tokens=512, chunk_overlap_tokens=0),
        fake,
        HeuristicTokenCounter(),
    )
    await chunker.chunk(res, DOC_ID, CLASS_ID)

    assert fake.embed_documents_calls == 1


async def test_a_topic_shift_in_arabic_produces_a_breakpoint() -> None:
    # The full behaviour, in Arabic: two topics, orthogonal vectors, one boundary between them.
    # `test_breakpoint_is_placed_at_topic_shift` asserts exactly this in English and passed
    # throughout, which is the point — the suite had no way to see the gap.
    topics = {"الحاسوب": [1.0, 0.0, 0.0, 0.0], "الاقتصاد": [0.0, 1.0, 0.0, 0.0]}
    computing = "الحاسوب سريع؟ الحاسوب دقيق؟ الحاسوب مفيد؟"
    economics = "الاقتصاد ينمو؟ الاقتصاد يتغير؟ الاقتصاد معقد؟"
    res = result("pdf", [text_block(0, f"{computing} {economics}", page=1)])
    fake = FakeEmbeddingProvider(topics)

    chunker = SemanticChunker(
        settings_for(
            chunk_max_tokens=512,
            chunk_overlap_tokens=0,
            semantic_breakpoint_percentile=90,
        ),
        fake,
        HeuristicTokenCounter(),
    )
    chunks = await chunker.chunk(res, DOC_ID, CLASS_ID)

    assert len(chunks) == 2
    assert "الحاسوب" in chunks[0].text and "الاقتصاد" not in chunks[0].text
    assert "الاقتصاد" in chunks[1].text and "الحاسوب" not in chunks[1].text


async def test_arabic_chunks_carry_their_text_intact() -> None:
    # Nothing along the path may normalise the script away — the packer joins atoms with a plain
    # space, and a chunk that lost its diacritics or arrived reversed would embed as noise.
    res = result("pdf", [text_block(0, ARABIC_STATEMENTS, page=1)])
    fake = FakeEmbeddingProvider({})

    chunker = SemanticChunker(
        settings_for(chunk_max_tokens=512, chunk_overlap_tokens=0),
        fake,
        HeuristicTokenCounter(),
    )
    chunks = await chunker.chunk(res, DOC_ID, CLASS_ID)

    joined = " ".join(chunk.text for chunk in chunks)
    assert "الذكاء الاصطناعي" in joined
    assert "الترجمة الآلية" in joined
