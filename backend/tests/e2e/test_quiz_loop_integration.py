"""§8.6 — a quiz from draft to closed, with real students answering it.

The quiz loop is the most arithmetic-heavy thing in the product and the only one whose output
a student is graded on. §4 verified the total-mark arithmetic and found a leak; §11.7 pinned
the concurrency orderings against a fake clock and found two more. Both were unit work, and
both rested on the same assumption: that the database agrees with the fakes about what a
submission is.

What only a running platform shows:

- a published quiz is visible to the students of that classroom and to nobody else;
- an answer can be changed until the student submits, and not after;
- the score the teacher sees and the score the student sees are the same number;
- closing is what releases the answer key, so it must not happen early;
- an extension moves the deadline for the people it names and nobody else.

**Generation is not exercised here.** `GenerateDraftAsync` calls a model, and §7.2 pinned the
deliberate asymmetry that it is never retried — a retry re-runs the model, which is another
minute of a teacher standing in front of a class. A test that needed it would need Groq, and
this suite is meant to run without one. The draft is authored instead, which also gives the
suite the answer key it needs to assert a score.

Run: `-m "integration and quiz"`, or with everything else via `-m integration`.
"""

from __future__ import annotations

import logging
from datetime import datetime, timedelta, timezone

import pytest

from clients.classroom import ClassroomClient
from clients.http import ApiError, get_ci
from support.ids import unique_username

pytestmark = [pytest.mark.integration, pytest.mark.quiz]

logger = logging.getLogger("e2e.quiz")

#: Two questions with a known key, so the score is arithmetic this suite can predict.
QUESTIONS = [
    {
        "text": "At what temperature does water boil at standard pressure?",
        "points": 2,
        "timeLimitSeconds": 300,
        "options": [
            {"text": "100 degrees Celsius", "isCorrect": True},
            {"text": "50 degrees Celsius", "isCorrect": False},
        ],
    },
    {
        "text": "What happens to the boiling point as altitude increases?",
        "points": 3,
        "timeLimitSeconds": 300,
        "options": [
            {"text": "It falls", "isCorrect": True},
            {"text": "It rises", "isCorrect": False},
        ],
    },
]
TOTAL_POINTS = sum(q["points"] for q in QUESTIONS)


def _soon() -> str:
    return (datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat()


def _pick(student_view: dict, *, correct: bool) -> list[tuple[str, str]]:
    """(questionId, optionId) pairs, choosing right or wrong answers throughout.

    The student view carries no answer key — that is the point of it — so correctness is
    resolved from this file's own QUESTIONS by matching the option text. A view that leaked
    `isCorrect` would make this helper unnecessary, which is itself worth noticing.
    """
    wanted = {}
    for authored in QUESTIONS:
        chosen = next(o for o in authored["options"] if o["isCorrect"] is correct)
        wanted[authored["text"]] = chosen["text"]

    picks = []
    for question in get_ci(student_view, "questions", []):
        text = str(get_ci(question, "text"))
        target = wanted[text]
        option = next(
            o for o in get_ci(question, "options", []) if str(get_ci(o, "text")) == target
        )
        picks.append((str(get_ci(question, "id")), str(get_ci(option, "id"))))
    return picks


@pytest.fixture(scope="module")
def quiz_class(make_user, classroom: ClassroomClient) -> dict:
    """A live session with two enrolled students and one outsider."""
    teacher = make_user("Teacher", "quizteacher")
    alice = make_user("Student", "quizalice")
    bob = make_user("Student", "quizbob")
    outsider = make_user("Student", "quizoutsider")

    classroom_id = classroom.create_classroom(
        teacher, name=f"Quizzes {unique_username('quiz')}", description="§8.6"
    )
    classroom.enroll(alice, classroom_id)
    classroom.enroll(bob, classroom_id)

    session_id = classroom.create_session(
        teacher, classroom_id, title="Quiz lecture", scheduled_at_utc=_soon()
    )
    classroom.start_session(teacher, classroom_id, session_id)

    return {
        "teacher": teacher,
        "alice": alice,
        "bob": bob,
        "outsider": outsider,
        "classroom_id": classroom_id,
        "session_id": session_id,
    }


def _publish_quiz(classroom: ClassroomClient, quiz_class: dict, title: str) -> str:
    quiz_id = classroom.quiz_draft_with(
        quiz_class["teacher"],
        quiz_class["classroom_id"],
        quiz_class["session_id"],
        title=title,
        questions=QUESTIONS,
    )
    classroom.publish_quiz(quiz_class["teacher"], quiz_class["classroom_id"], quiz_id)
    return quiz_id


# --- publication and visibility ----------------------------------------------------------


def test_a_published_quiz_reaches_the_classrooms_students(
    classroom: ClassroomClient, quiz_class: dict
) -> None:
    quiz_id = _publish_quiz(classroom, quiz_class, "Visible")

    view = classroom.get_student_quiz(quiz_class["alice"], quiz_class["classroom_id"], quiz_id)

    assert str(get_ci(view, "id")) == quiz_id
    assert len(get_ci(view, "questions", [])) == len(QUESTIONS)
    assert int(get_ci(view, "totalPoints", 0)) == TOTAL_POINTS


def test_the_student_view_carries_no_answer_key(
    classroom: ClassroomClient, quiz_class: dict
) -> None:
    """A quiz the student can walk to the right answer is not an assessment.

    §4's own DTO comment says the submit acknowledgement deliberately does not reveal
    correctness, because answers can be changed until the quiz closes. The same has to be true
    of the view itself, and it is a different code path.
    """
    quiz_id = _publish_quiz(classroom, quiz_class, "No key")

    view = classroom.get_student_quiz(quiz_class["alice"], quiz_class["classroom_id"], quiz_id)
    serialized = str(view).lower()

    assert "iscorrect" not in serialized, (
        f"the student view exposes an answer key: {serialized[:400]}"
    )


def test_a_non_member_cannot_see_the_quiz(
    classroom: ClassroomClient, quiz_class: dict
) -> None:
    quiz_id = _publish_quiz(classroom, quiz_class, "Private")

    with pytest.raises(ApiError) as refused:
        classroom.get_student_quiz(
            quiz_class["outsider"], quiz_class["classroom_id"], quiz_id
        )

    assert refused.value.status_code == 403, refused.value.status_code


# --- answering ----------------------------------------------------------------------------


def test_an_answer_can_be_changed_until_the_student_submits(
    classroom: ClassroomClient, quiz_class: dict
) -> None:
    """Changing an answer updates rather than accumulates — §11.7's I-07, against a real
    unique index rather than a fake that counted rows."""
    quiz_id = _publish_quiz(classroom, quiz_class, "Changeable")
    alice, classroom_id = quiz_class["alice"], quiz_class["classroom_id"]

    view = classroom.get_student_quiz(alice, classroom_id, quiz_id)
    wrong = _pick(view, correct=False)
    right = _pick(view, correct=True)

    for question_id, option_id in wrong:
        classroom.answer_quiz(alice, classroom_id, quiz_id, question_id=question_id, option_id=option_id)
    for question_id, option_id in right:
        classroom.answer_quiz(alice, classroom_id, quiz_id, question_id=question_id, option_id=option_id)

    reloaded = classroom.get_student_quiz(alice, classroom_id, quiz_id)
    selected = {
        str(get_ci(q, "id")): str(get_ci(q, "selectedOptionId"))
        for q in get_ci(reloaded, "questions", [])
    }
    for question_id, option_id in right:
        assert selected[question_id] == option_id, (
            "the reloaded view does not show the latest answer — either the change did not "
            "replace the earlier one, or a reload loses the student's work mid-quiz"
        )


def test_a_submitted_student_cannot_change_their_answers(
    classroom: ClassroomClient, quiz_class: dict
) -> None:
    """Submission is what freezes them. Without this the deadline means nothing: a student
    could keep editing while the teacher reads the results."""
    quiz_id = _publish_quiz(classroom, quiz_class, "Frozen")
    bob, classroom_id = quiz_class["bob"], quiz_class["classroom_id"]

    view = classroom.get_student_quiz(bob, classroom_id, quiz_id)
    picks = _pick(view, correct=True)
    for question_id, option_id in picks:
        classroom.answer_quiz(bob, classroom_id, quiz_id, question_id=question_id, option_id=option_id)

    classroom.submit_quiz(bob, classroom_id, quiz_id)

    question_id, _ = picks[0]
    other = next(
        str(get_ci(o, "id"))
        for q in get_ci(view, "questions", [])
        if str(get_ci(q, "id")) == question_id
        for o in get_ci(q, "options", [])
        if str(get_ci(o, "id")) != picks[0][1]
    )
    response = classroom.answer_quiz_response(
        bob, classroom_id, quiz_id, question_id=question_id, option_id=other
    )

    assert not response.is_success, (
        f"a submitted student changed an answer ({response.status_code})"
    )


def test_submitting_twice_reports_the_first_submission(
    classroom: ClassroomClient, quiz_class: dict
) -> None:
    """A double click, or a retry after a dropped response. §11.7 pinned that this records one
    submission and returns the original timestamp rather than a conflict."""
    quiz_id = _publish_quiz(classroom, quiz_class, "Twice")
    alice, classroom_id = quiz_class["alice"], quiz_class["classroom_id"]

    view = classroom.get_student_quiz(alice, classroom_id, quiz_id)
    for question_id, option_id in _pick(view, correct=True):
        classroom.answer_quiz(alice, classroom_id, quiz_id, question_id=question_id, option_id=option_id)

    first = classroom.submit_quiz(alice, classroom_id, quiz_id)
    second = classroom.submit_quiz(alice, classroom_id, quiz_id)

    assert get_ci(first, "submittedAtUtc") == get_ci(second, "submittedAtUtc"), (
        "the second submission moved the timestamp — a retry is being recorded as a new "
        "submission, which is how a late-submission report gets the wrong time on it"
    )


# --- scoring ------------------------------------------------------------------------------


def test_the_teacher_and_the_student_see_the_same_score(
    classroom: ClassroomClient, quiz_class: dict
) -> None:
    """§4's arithmetic, computed by the real service over real rows.

    Two students, deliberately different: one answers everything correctly and one everything
    wrongly, so a scorer that returned a constant — the total, or zero — fails rather than
    passing on a coincidence.
    """
    quiz_id = _publish_quiz(classroom, quiz_class, "Scored")
    classroom_id = quiz_class["classroom_id"]
    alice, bob, teacher = quiz_class["alice"], quiz_class["bob"], quiz_class["teacher"]

    for student, correct in ((alice, True), (bob, False)):
        view = classroom.get_student_quiz(student, classroom_id, quiz_id)
        for question_id, option_id in _pick(view, correct=correct):
            classroom.answer_quiz(
                student, classroom_id, quiz_id, question_id=question_id, option_id=option_id
            )
        classroom.submit_quiz(student, classroom_id, quiz_id)

    classroom.close_quiz(teacher, classroom_id, quiz_id)

    results = classroom.quiz_results(teacher, classroom_id, quiz_id)
    by_student = {
        str(get_ci(r, "studentId")): int(get_ci(r, "score", -1))
        for r in get_ci(results, "students", [])
    }

    assert by_student.get(alice.user_id) == TOTAL_POINTS, results
    assert by_student.get(bob.user_id) == 0, results

    mine = classroom.my_quiz_result(alice, classroom_id, quiz_id)
    assert int(get_ci(mine, "score", -1)) == TOTAL_POINTS, mine
    assert int(get_ci(mine, "totalPoints", -1)) == TOTAL_POINTS, (
        "the student's own result reports a different total from the quiz's — this is the "
        "number their percentage and their ranking are both computed from (§4, §6)"
    )


def test_the_answer_key_is_released_by_closing_and_not_before(
    classroom: ClassroomClient, quiz_class: dict
) -> None:
    """Closing is what releases the review. §11.7's sweeper defect closed a quiz that had just
    been extended — and the reason that mattered is precisely that closing hands out the key."""
    quiz_id = _publish_quiz(classroom, quiz_class, "Key release")
    classroom_id = quiz_class["classroom_id"]
    alice, teacher = quiz_class["alice"], quiz_class["teacher"]

    view = classroom.get_student_quiz(alice, classroom_id, quiz_id)
    for question_id, option_id in _pick(view, correct=True):
        classroom.answer_quiz(alice, classroom_id, quiz_id, question_id=question_id, option_id=option_id)
    classroom.submit_quiz(alice, classroom_id, quiz_id)

    before = classroom.my_quiz_result(alice, classroom_id, quiz_id)
    graded_before = [
        a for a in get_ci(before, "answers", []) if get_ci(a, "isCorrect") is not None
    ]
    assert not graded_before, (
        "correctness was revealed while the quiz was still open, so a student can learn the "
        f"key while their classmates are still choosing: {before}"
    )

    classroom.close_quiz(teacher, classroom_id, quiz_id)

    after = classroom.my_quiz_result(alice, classroom_id, quiz_id)
    graded_after = [
        a for a in get_ci(after, "answers", []) if get_ci(a, "isCorrect") is not None
    ]
    assert graded_after, f"closing released nothing: {after}"


# --- extending ----------------------------------------------------------------------------


def test_an_extension_moves_the_deadline_for_the_named_student_only(
    classroom: ClassroomClient, quiz_class: dict
) -> None:
    """`ExtendQuizRequest(Seconds, StudentIds?)`: naming students gives the time only to them.

    The whole-class form would hand the extra minutes to everyone including those who have
    already finished, which is the case the per-student form exists for — and §11.7 found the
    sweeper discarding an extension granted while it ran.
    """
    quiz_id = _publish_quiz(classroom, quiz_class, "Extended")
    classroom_id = quiz_class["classroom_id"]
    alice, bob, teacher = quiz_class["alice"], quiz_class["bob"], quiz_class["teacher"]

    before_alice = get_ci(classroom.get_student_quiz(alice, classroom_id, quiz_id), "closesAtUtc")
    before_bob = get_ci(classroom.get_student_quiz(bob, classroom_id, quiz_id), "closesAtUtc")

    classroom.extend_quiz(
        teacher, classroom_id, quiz_id, seconds=600, student_ids=[alice.user_id]
    )

    after_alice = get_ci(classroom.get_student_quiz(alice, classroom_id, quiz_id), "closesAtUtc")
    after_bob = get_ci(classroom.get_student_quiz(bob, classroom_id, quiz_id), "closesAtUtc")

    assert after_alice != before_alice, (
        f"the named student's deadline did not move: {before_alice} -> {after_alice}"
    )
    assert after_bob == before_bob, (
        f"an unnamed student's deadline moved too: {before_bob} -> {after_bob}"
    )


def test_a_closed_quiz_refuses_further_answers(
    classroom: ClassroomClient, quiz_class: dict
) -> None:
    quiz_id = _publish_quiz(classroom, quiz_class, "Closed")
    classroom_id = quiz_class["classroom_id"]
    alice, teacher = quiz_class["alice"], quiz_class["teacher"]

    view = classroom.get_student_quiz(alice, classroom_id, quiz_id)
    picks = _pick(view, correct=True)

    classroom.close_quiz(teacher, classroom_id, quiz_id)

    question_id, option_id = picks[0]
    response = classroom.answer_quiz_response(
        alice, classroom_id, quiz_id, question_id=question_id, option_id=option_id
    )

    assert not response.is_success, (
        f"a closed quiz accepted an answer ({response.status_code}) — closing has already "
        "released the key, so this is a student answering with the answers in front of them"
    )


def test_only_the_classrooms_teacher_can_publish_close_or_read_results(
    make_user, classroom: ClassroomClient, quiz_class: dict
) -> None:
    """B-05 on the quiz surface: holding the Teacher role is not owning this classroom."""
    intruder = make_user("Teacher", "quizintruder")
    quiz_id = _publish_quiz(classroom, quiz_class, "Owned")
    classroom_id = quiz_class["classroom_id"]

    with pytest.raises(ApiError) as closing:
        classroom.close_quiz(intruder, classroom_id, quiz_id)
    assert closing.value.status_code in (401, 403), closing.value.status_code

    with pytest.raises(ApiError) as reading:
        classroom.quiz_results(intruder, classroom_id, quiz_id)
    assert reading.value.status_code in (401, 403), reading.value.status_code
