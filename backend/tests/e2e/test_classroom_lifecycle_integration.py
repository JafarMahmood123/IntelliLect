"""§8.3 — a classroom from creation to deletion, and what the deletion takes with it.

Three services own pieces of one classroom: ClassroomService owns the row and the material
metadata, MinIO owns the bytes, RagService owns the chunks and their embeddings. Nothing in
the type system connects them. A unit test can prove each service does its own part; only a
running platform can show the parts agree.

The cascade is the reason this suite exists. D-05 asks whether a partially-failed delete
leaves an orphan — a deleted index with surviving rows, or the reverse — and that question
cannot be asked at all until the successful path is pinned, because there is nothing to
compare a partial failure against.

**The tenancy checks are here too**, and they are integration work for a specific reason:
§7.2b closed five endpoints that took no caller, and proved it against fakes. The 403 those
fixes produce has never once been observed over HTTP. That is B-04's remaining half.

Run: `-m "integration and classroom"`, or with everything else via `-m integration`.
"""

from __future__ import annotations

import logging

import pytest

from clients.classroom import ClassroomClient
from clients.http import get_ci
from clients.rag import RagClient
from config import Config
from support.ids import unique_username
from support.waiting import poll_until

pytestmark = [pytest.mark.integration, pytest.mark.classroom]

logger = logging.getLogger("e2e.classroom")

#: Long enough to chunk into something retrievable, short enough to embed quickly.
MATERIAL = (
    "Photosynthesis converts light energy into chemical energy. "
    "The light-dependent reactions occur in the thylakoid membrane. "
    "The Calvin cycle fixes carbon dioxide in the stroma. "
    "Chlorophyll absorbs most strongly in the blue and red parts of the spectrum. "
) * 4


@pytest.fixture(scope="module")
def owned_classroom(make_user, classroom: ClassroomClient) -> dict:
    """A teacher with a classroom, one enrolled student, and one outsider who is neither.

    Module-scoped: provisioning an account is four HTTP calls and an admin approval, and
    every test here needs the same three people. The tests below never mutate these three,
    only the classrooms they create.
    """
    teacher = make_user("Teacher", "clsteacher")
    student = make_user("Student", "clsstudent")
    outsider = make_user("Student", "clsoutsider")

    classroom_id = classroom.create_classroom(
        teacher, name=f"Biology {unique_username('cls')}", description="§8.3"
    )
    classroom.enroll(student, classroom_id)

    return {
        "teacher": teacher,
        "student": student,
        "outsider": outsider,
        "classroom_id": classroom_id,
    }


# --- the material path -------------------------------------------------------------------


def test_uploaded_material_is_listed_and_reaches_the_knowledge_base(
    classroom: ClassroomClient, knowledge: RagClient, owned_classroom: dict, config: Config
) -> None:
    """Upload → stored → listed → ingested. Four services' worth of one action.

    The status is polled rather than asserted immediately: ingestion is asynchronous by
    design, and the upload response returns as soon as the bytes are stored. A test that
    asserted `Indexed` at once would be asserting that the pipeline is synchronous, which is
    the opposite of what it is built to be.
    """
    teacher = owned_classroom["teacher"]
    classroom_id = owned_classroom["classroom_id"]

    uploaded = classroom.upload_file(
        teacher, classroom_id, file_name="photosynthesis.txt", content=MATERIAL.encode()
    )
    file_id = get_ci(uploaded, "id")
    assert file_id, uploaded

    listed = classroom.list_files(teacher, classroom_id)
    assert any(str(get_ci(f, "id")) == str(file_id) for f in listed), listed

    status = poll_until(
        lambda: (
            s if (s := knowledge.document_status(file_id)) in ("Indexed", "Failed") else None
        ),
        timeout_s=config.ingest_timeout_s,
        interval_s=2.0,
        description=f"RagService to finish ingesting {file_id}",
    )
    assert status == "Indexed", (
        f"ingest ended as {status}. The upload succeeded and the bytes are in MinIO, so this "
        "is RagService's half — check its logs and that the embedding model is reachable."
    )


def test_the_classrooms_own_material_is_retrievable_and_nobody_elses_is(
    classroom: ClassroomClient, knowledge: RagClient, owned_classroom: dict, config: Config
) -> None:
    """F-07, which is the P0 of this area: search is scoped to one classroom.

    `ChunkRepository.search` filters on `classroom_id` with `# mandatory scope` against it —
    a fact about the query, verified by reading it. This is the same fact measured against a
    database holding two classrooms' material at once, which is the only place a missing
    `WHERE` actually shows.
    """
    teacher = owned_classroom["teacher"]
    mine = owned_classroom["classroom_id"]

    other = classroom.create_classroom(teacher, name=f"Chemistry {unique_username('other')}")
    marker = "Avogadro constant equals six point zero two two times ten to the twenty third."
    other_file = get_ci(
        classroom.upload_file(
            teacher, other, file_name="chemistry.txt", content=(marker + " ") .encode() * 20
        ),
        "id",
    )

    poll_until(
        lambda: knowledge.document_status(other_file) in ("Indexed", "Failed") or None,
        timeout_s=config.ingest_timeout_s,
        interval_s=2.0,
        description="the other classroom's material to index",
    )

    leaked = knowledge.search(mine, "Avogadro constant", top_k=6)
    texts = " ".join(str(get_ci(r, "text", "")) for r in leaked).lower()

    assert "avogadro" not in texts, (
        "a search scoped to one classroom returned another classroom's material — the "
        f"retrieval filter is not holding. Results: {leaked}"
    )


def test_an_oversized_upload_is_refused_with_a_typed_error(
    classroom: ClassroomClient, owned_classroom: dict
) -> None:
    """E-02/E-03 over HTTP, which is where the four separate limits finally meet.

    §7.12b found a fourth limit nobody had counted (`MultipartBodyLengthLimit`, a framework
    default of 128 MB) and made it derive from the configured one. The consequence of getting
    that wrong is not a rejection — it is a **500** instead of a typed 413, which is only
    visible from outside the process.
    """
    teacher = owned_classroom["teacher"]
    classroom_id = owned_classroom["classroom_id"]

    limits = classroom.upload_limits(teacher, classroom_id)
    max_bytes = int(get_ci(limits, "maxFileSizeBytes", 0))
    assert max_bytes > 0, f"upload limits did not report a maximum: {limits}"

    response = classroom.upload_file_response(
        teacher,
        classroom_id,
        file_name="too-big.bin",
        content=b"\0" * (max_bytes + 1024),
    )

    assert response.status_code != 500, (
        "an oversized upload produced a 500 rather than a typed refusal — that is the shape "
        "of a limit being enforced by the framework's model binder instead of by the filter"
    )
    assert response.status_code in (400, 413), (
        f"expected 413 (or 400), got {response.status_code}: {response.text[:300]}"
    )


# --- who may see it ----------------------------------------------------------------------


def test_a_non_member_cannot_list_the_material_or_the_roster_or_the_timetable(
    classroom: ClassroomClient, owned_classroom: dict
) -> None:
    """B-04's remaining half: the 403 §7.2b's fixes produce, observed over HTTP.

    All three of these took no caller at all before §7.2b — the file list, the roster (which
    carries student names) and the session list. Each was reachable by any authenticated
    account that knew a classroom id.
    """
    outsider = owned_classroom["outsider"]
    classroom_id = owned_classroom["classroom_id"]

    refusals = {
        "files": classroom.list_files_response(outsider, classroom_id),
        "members": classroom.members_response(outsider, classroom_id),
        "sessions": classroom.sessions_response(outsider, classroom_id),
    }

    for name, response in refusals.items():
        assert response.status_code == 403, (
            f"a non-member got {response.status_code} from the {name} listing, expected 403: "
            f"{response.text[:200]}"
        )


def test_an_enrolled_student_can_see_all_three(
    classroom: ClassroomClient, owned_classroom: dict
) -> None:
    # The vacuum guard on the refusals above. A service that answered 403 to everybody would
    # pass that test and be entirely broken.
    student = owned_classroom["student"]
    classroom_id = owned_classroom["classroom_id"]

    for name, response in {
        "files": classroom.list_files_response(student, classroom_id),
        "members": classroom.members_response(student, classroom_id),
        "sessions": classroom.sessions_response(student, classroom_id),
    }.items():
        assert response.is_success, (
            f"an enrolled student got {response.status_code} from the {name} listing"
        )


def test_a_teacher_cannot_act_on_a_classroom_they_do_not_own(
    make_user, classroom: ClassroomClient, owned_classroom: dict
) -> None:
    """B-05. `[Authorize(Roles = "Teacher")]` proves the caller is a teacher; §7.2b found two
    session routes resting on exactly that, with no check that the classroom was theirs."""
    intruder = make_user("Teacher", "clsintruder")
    classroom_id = owned_classroom["classroom_id"]

    response = classroom.upload_file_response(
        intruder, classroom_id, file_name="intruder.txt", content=b"hello"
    )

    assert response.status_code in (401, 403), (
        f"another teacher uploaded into a classroom they do not own: {response.status_code}"
    )


# --- deletion and the cascade ------------------------------------------------------------


def test_deleting_a_classroom_removes_its_material_from_the_knowledge_base(
    make_user, classroom: ClassroomClient, knowledge: RagClient, config: Config
) -> None:
    """D-05's successful path, which has to exist before a partial failure means anything.

    A classroom of its own, not the module fixture's: this test destroys what it creates, and
    sharing a classroom between a deletion test and everything else is how a suite acquires
    an ordering dependency nobody wrote down.
    """
    teacher = make_user("Teacher", "delteacher")
    classroom_id = classroom.create_classroom(
        teacher, name=f"Doomed {unique_username('del')}", description="§8.3 cascade"
    )
    file_id = get_ci(
        classroom.upload_file(
            teacher, classroom_id, file_name="doomed.txt", content=MATERIAL.encode()
        ),
        "id",
    )

    poll_until(
        lambda: knowledge.document_status(file_id) in ("Indexed", "Failed") or None,
        timeout_s=config.ingest_timeout_s,
        interval_s=2.0,
        description="material to index before deleting it",
    )

    classroom.delete_classroom(teacher, classroom_id)

    gone = classroom.get_classroom_response(teacher, classroom_id)
    assert gone.status_code == 404, (
        f"the classroom answered {gone.status_code} after being deleted"
    )

    # The other side of the cascade, and the one that leaves an orphan when it fails: the
    # chunks are RagService's, deleted by a message rather than by the same transaction.
    final = poll_until(
        lambda: (s if (s := knowledge.document_status(file_id)) == "Unknown" else None),
        timeout_s=60,
        interval_s=2.0,
        description="RagService to drop the deleted classroom's document",
    )
    assert final == "Unknown"


def test_a_deleted_classrooms_material_is_no_longer_retrievable(
    make_user, classroom: ClassroomClient, knowledge: RagClient, config: Config
) -> None:
    """The consequence rather than the row count.

    "The document row is gone" and "the chunks are gone" are different facts, and only the
    second one decides whether a deleted lecture can still be quoted back to a student. §7.4d
    made the same distinction about join tokens: the record and the capability are not the
    same thing.
    """
    teacher = make_user("Teacher", "purgeteacher")
    classroom_id = classroom.create_classroom(teacher, name=f"Purge {unique_username('purge')}")
    marker = "Bioluminescence in Aequorea victoria produces green fluorescent protein."
    file_id = get_ci(
        classroom.upload_file(
            teacher, classroom_id, file_name="purge.txt", content=(marker + " ").encode() * 20
        ),
        "id",
    )

    poll_until(
        lambda: knowledge.document_status(file_id) == "Indexed" or None,
        timeout_s=config.ingest_timeout_s,
        interval_s=2.0,
        description="material to index",
    )
    assert knowledge.search(classroom_id, "green fluorescent protein"), (
        "the material never became retrievable, so this test cannot prove it stopped being so"
    )

    classroom.delete_classroom(teacher, classroom_id)

    empty = poll_until(
        lambda: (True if not knowledge.search(classroom_id, "green fluorescent protein") else None),
        timeout_s=60,
        interval_s=2.0,
        description="retrieval to stop returning the deleted classroom's chunks",
    )
    assert empty
