from __future__ import annotations

from app.application.services.text_similarity import jaccard_similarity


def test_identical_text_is_one():
    assert jaccard_similarity("photosynthesis in the chloroplast", "photosynthesis in the chloroplast") == 1.0


def test_disjoint_text_is_zero():
    assert jaccard_similarity("alpha beta gamma", "delta epsilon zeta") == 0.0


def test_partial_overlap_is_fractional():
    # {a,b,c} vs {b,c,d}: intersection 2, union 4 -> 0.5
    assert jaccard_similarity("a b c", "b c d") == 0.5


def test_case_and_punctuation_insensitive():
    assert jaccard_similarity("The Chloroplast!", "the chloroplast") == 1.0


def test_two_empty_strings_are_one_one_empty_is_zero():
    assert jaccard_similarity("", "") == 1.0
    assert jaccard_similarity("something", "") == 0.0
