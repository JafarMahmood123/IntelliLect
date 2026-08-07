"""What happens when a file is not what it claims to be (test-plan E-06).

E-06 asks that an extension/content-type mismatch be "refused by the extractor rather than
crashing it". It was marked `partial`, and the reason turns out to be that the interesting
half was never the crash.

Running every extractor against every other format's bytes gave three answers, not two.
Nine of the twelve pairs already raised `CorruptFileError` — the parsers object, the router
reports it, nothing is indexed. **Three did not, and none of those three crashed:**

* `PdfExtractor` given PNG bytes **succeeded with zero blocks**. PyMuPDF opens images as
  documents and `filetype="pdf"` does not stop it. Extraction reported success, the
  document was marked Done, and a teacher saw an indexed file the assistant could never
  retrieve a word from.
* `TxtExtractor` given PDF bytes **succeeded with eighteen blocks** of cp1252 replacement
  characters, which were then chunked and embedded into the classroom's index — noise that
  degrades retrieval for every later question, not only for that document. The extractor's
  own docstring promised the opposite: "an uploaded binary renamed to .txt surfaces as a
  clear error rather than as a document full of garbage." Its guard is a NUL-byte check,
  and a small uncompressed PDF contains no NUL bytes.
* `PdfExtractor` given DOCX or PPTX bytes succeeded and extracted the text — harmless in
  itself, but recorded with `source_format="pdf"`, so the PDF-only OCR path then ran over a
  document that is not a PDF.

Silence, not a crash. So the router now sniffs the bytes and refuses when they contradict
the extractor that dispatch chose.
"""

from __future__ import annotations

import pytest

from app.application.ports.extractor import (
    CorruptFileError,
    ExtractionError,
    UnsupportedFormatError,
)
from app.infrastructure.extraction._support import sniff_format
from app.infrastructure.extraction.router import ExtractorRouter

from tests.extraction.fixtures import make_docx, make_png, make_pptx, make_text_pdf

DOCX_TYPE = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
PPTX_TYPE = "application/vnd.openxmlformats-officedocument.presentationml.presentation"

# (name it was given, content type it was sent as)
DECLARED = {
    "pdf": ("report.pdf", "application/pdf"),
    "docx": ("notes.docx", DOCX_TYPE),
    "pptx": ("deck.pptx", PPTX_TYPE),
    "txt": ("notes.txt", "text/plain"),
}


def payloads() -> dict[str, bytes]:
    return {
        "pdf": make_text_pdf(),
        "docx": make_docx(),
        "pptx": make_pptx(),
        "txt": b"First paragraph.\n\nSecond paragraph.\n",
        "png": make_png(),
    }


@pytest.mark.parametrize("declared", sorted(DECLARED))
@pytest.mark.parametrize("actual", ["pdf", "docx", "pptx", "txt", "png"])
def test_every_mismatch_is_refused(declared: str, actual: str) -> None:
    """The full matrix, not the pairs somebody thought of.

    Written as a cross-product because the three that got through were exactly the three
    a hand-written list would not have contained — nobody writes "PNG sent as PDF" into a
    list of cases unless they have already seen it happen.
    """
    if declared == actual:
        pytest.skip("covered by test_a_matching_file_still_extracts")

    file_name, content_type = DECLARED[declared]
    router = ExtractorRouter.default()

    with pytest.raises(ExtractionError):
        router.extract(payloads()[actual], file_name, content_type)


@pytest.mark.parametrize("fmt", sorted(DECLARED))
def test_a_matching_file_still_extracts(fmt: str) -> None:
    """The other direction, and the one that matters more.

    A guard that refuses everything passes every case above. Each honest file must still
    come through with content, or the fix is worse than the defect it replaces.
    """
    file_name, content_type = DECLARED[fmt]
    router = ExtractorRouter.default()

    result = router.extract(payloads()[fmt], file_name, content_type)

    assert result.blocks, f"{fmt} extracted nothing"


def test_an_image_sent_as_a_pdf_is_refused_rather_than_indexed_empty() -> None:
    """The defect this file exists for, named on its own.

    It is in the matrix above, but the matrix would still pass if this became a
    `CorruptFileError` from deep inside PyMuPDF for some unrelated reason. What is being
    asserted is that the file is turned away for the RIGHT reason, with a message that
    tells the teacher what they actually uploaded.
    """
    router = ExtractorRouter.default()

    with pytest.raises(UnsupportedFormatError) as raised:
        router.extract(make_png(), "lecture.pdf", "application/pdf")

    assert "png" in str(raised.value)


def test_a_pdf_renamed_to_txt_is_refused_rather_than_indexed_as_noise() -> None:
    """The second defect: garbage in the index is worse than a rejected file.

    A rejected upload is one teacher, told immediately, re-uploading. Eighteen paragraphs
    of decoded binary in the vector index is every question that classroom asks afterwards
    competing with noise, with nothing to indicate why the answers got worse.
    """
    router = ExtractorRouter.default()

    with pytest.raises(UnsupportedFormatError) as raised:
        router.extract(make_text_pdf(), "notes.txt", "text/plain")

    assert "pdf" in str(raised.value)


def test_a_legacy_office_file_is_named_rather_than_called_corrupt() -> None:
    """.doc is not .docx, and saying so is the difference between one click and a ticket.

    Pre-2007 Office files are an OLE2 container, not a zip, so python-docx reports them as
    a bad package — "could not open" for a file that opens perfectly well in Word. The
    error now says which format it is, and the fix is Save As.
    """
    ole2 = b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1" + b"\x00" * 512
    router = ExtractorRouter.default()

    with pytest.raises(UnsupportedFormatError) as raised:
        router.extract(ole2, "handout.docx", DOCX_TYPE)

    assert "ole2" in str(raised.value)


def test_text_without_a_signature_is_never_refused_by_sniffing() -> None:
    """The rule that keeps this safe: unrecognised bytes always proceed.

    Plain text and Markdown carry no signature at all, so `sniff_format` returns None for
    every legitimate .txt in the system. If an unknown result were treated as suspicious,
    this guard would reject the one format it can say the least about — and it would do it
    to real uploads, not to mislabelled ones.
    """
    router = ExtractorRouter.default()

    for content in (b"", b"# Heading\n\nBody.", "café — naïve\n".encode("utf-8")):
        assert sniff_format(content) is None
        router.extract(content, "notes.md", "text/markdown")


class TestSniffing:
    """The signature reader on its own, so a failure points at the right half."""

    def test_it_names_each_format_from_its_bytes_alone(self) -> None:
        assert sniff_format(make_text_pdf()) == "pdf"
        assert sniff_format(make_docx()) == "docx"
        assert sniff_format(make_pptx()) == "pptx"
        assert sniff_format(make_png()) == "png"

    def test_docx_and_pptx_are_told_apart_by_what_is_inside_the_zip(self) -> None:
        """Both are zips with the same first four bytes.

        A signature check that stopped at `PK\\x03\\x04` would call them both "zip" and
        then be unable to tell a DOCX sent as a PPTX from a correct one — which is a
        mismatch a teacher can make in one wrong click in the upload dialog.
        """
        assert sniff_format(make_docx()) != sniff_format(make_pptx())

    def test_a_damaged_zip_is_still_reported_as_a_zip(self) -> None:
        # Truncated mid-archive: the names cannot be read, so the format inside is unknown,
        # but "this is a zip" is still true and still more use than "unknown" — which would
        # let a corrupt DOCX through to a parser that reports it as something else.
        assert sniff_format(b"PK\x03\x04" + b"\x00" * 32) == "zip"

    def test_an_empty_file_has_no_signature(self) -> None:
        assert sniff_format(b"") is None


def test_a_corrupt_file_of_the_right_format_is_still_a_corrupt_file() -> None:
    """Sniffing must not swallow the case that already worked.

    A truncated PDF is a PDF: it sniffs as one, dispatch is correct, and the failure
    belongs to the parser. It has to stay a CorruptFileError rather than becoming an
    "unsupported format", because the two mean different things to whoever reads the
    status — one is "wrong file", the other is "broken file".
    """
    router = ExtractorRouter.default()

    with pytest.raises(CorruptFileError):
        router.extract(b"%PDF-1.7\ntruncated right here", "broken.pdf", "application/pdf")
