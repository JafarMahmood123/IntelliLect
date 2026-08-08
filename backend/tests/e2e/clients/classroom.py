"""ClassroomService client: classrooms, enrollment, sessions, session start/end.

Through the gateway (`/api/classrooms/*`). Starting a session is the trigger that
makes ClassroomService call StreamingService, which in turn spins up the LiveKit
room and notifies LiveAssistantService to start the agent pipeline. Ending it runs
the same path in reverse: participants are disconnected, the recording is finalized
and the summary is generated.
"""

from __future__ import annotations

import httpx

from clients.http import expect_ok, get_ci
from clients.ums import Account


class ClassroomClient:
    def __init__(self, gateway_url: str, timeout_s: float) -> None:
        self._http = httpx.Client(base_url=gateway_url, timeout=timeout_s)

    def close(self) -> None:
        self._http.close()

    def create_classroom(self, teacher: Account, name: str, description: str = "") -> str:
        resp = expect_ok(
            self._http.post(
                "/api/classrooms",
                json={"name": name, "description": description},
                headers=teacher.auth,
            )
        )
        classroom_id = get_ci(resp.json(), "id")
        assert classroom_id, f"create classroom returned no id: {resp.text}"
        return classroom_id

    def enroll(self, student: Account, classroom_id: str) -> None:
        expect_ok(
            self._http.post(
                f"/api/classrooms/{classroom_id}/members/enroll",
                headers=student.auth,
            )
        )

    def create_session(
        self,
        teacher: Account,
        classroom_id: str,
        *,
        title: str,
        scheduled_at_utc: str,
        participation_mode: int = 1,
        description: str = "",
    ) -> str:
        body = {
            "title": title,
            "description": description,
            "scheduledAtUtc": scheduled_at_utc,
            "participationMode": participation_mode,
        }
        resp = expect_ok(
            self._http.post(
                f"/api/classrooms/{classroom_id}/sessions",
                json=body,
                headers=teacher.auth,
            )
        )
        session_id = get_ci(resp.json(), "id")
        assert session_id, f"create session returned no id: {resp.text}"
        return session_id

    def start_session(self, teacher: Account, classroom_id: str, session_id: str) -> None:
        """Flip the session Live -> triggers StreamingService + LiveAssistant start."""
        expect_ok(
            self._http.post(
                f"/api/classrooms/{classroom_id}/sessions/{session_id}/start",
                headers=teacher.auth,
            )
        )

    def end_session(self, teacher: Account, classroom_id: str, session_id: str) -> dict:
        """Flip the session Ended -> disconnects participants, stops the recording,
        tears the agent pipeline down and triggers summary generation.

        Returns the outcome body, whose ``streamEnded``/``summaryTriggered`` flags report
        the best-effort steps (the session is Ended either way).
        """
        resp = expect_ok(
            self._http.post(
                f"/api/classrooms/{classroom_id}/sessions/{session_id}/end",
                headers=teacher.auth,
            )
        )
        return resp.json()

    def get_sessions(self, account: Account, classroom_id: str) -> list[dict]:
        resp = expect_ok(
            self._http.get(
                f"/api/classrooms/{classroom_id}/sessions",
                headers=account.auth,
            )
        )
        return resp.json()

    # --- quizzes (used by the latency harness, §9) ----------------------------

    def create_quiz_draft(
        self, teacher: Account, classroom_id: str, session_id: str, *, title: str
    ) -> str:
        """One trivial question. The quiz's content is irrelevant to the hop being
        measured; its *timer* is not, which is why the question is given a long limit —
        a quiz that self-closes mid-run would turn a latency failure into a 409."""
        body = {
            "title": title,
            "questions": [
                {
                    "text": "Latency probe.",
                    "points": 1,
                    "timeLimitSeconds": 300,
                    "options": [
                        {"text": "A", "isCorrect": True},
                        {"text": "B", "isCorrect": False},
                    ],
                }
            ],
        }
        resp = expect_ok(
            self._http.post(
                f"/api/classrooms/{classroom_id}/sessions/{session_id}/quizzes",
                json=body,
                headers=teacher.auth,
            )
        )
        quiz_id = get_ci(resp.json(), "id")
        assert quiz_id, f"create quiz draft returned no id: {resp.text}"
        return quiz_id

    def publish_quiz_response(
        self, teacher: Account, classroom_id: str, quiz_id: str
    ) -> httpx.Response:
        """Publish, returning the raw response. Unwrapped on purpose: the caller stamps
        the clock immediately around this call, so it must not be wrapped in retry or
        polling logic that would land inside the measured interval."""
        return self._http.post(
            f"/api/classrooms/{classroom_id}/quizzes/{quiz_id}/publish",
            headers=teacher.auth,
        )

    def cancel_quiz(self, teacher: Account, classroom_id: str, quiz_id: str) -> None:
        expect_ok(
            self._http.post(
                f"/api/classrooms/{classroom_id}/quizzes/{quiz_id}/cancel",
                headers=teacher.auth,
            )
        )

    def get_student_quiz(self, student: Account, classroom_id: str, quiz_id: str) -> dict:
        """The student's view — no answer key. This is the follow-up fetch every client
        makes on a QuizChanged broadcast, because the broadcast carries the id only."""
        resp = expect_ok(
            self._http.get(
                f"/api/classrooms/{classroom_id}/quizzes/{quiz_id}/student-view",
                headers=student.auth,
            )
        )
        return resp.json()

    # --- material (§8.3) ------------------------------------------------------

    def upload_file(
        self, teacher: Account, classroom_id: str, *, file_name: str, content: bytes,
        content_type: str = "text/plain",
    ) -> dict:
        """Upload through the public route, the way a teacher does.

        Deliberately not seeded straight into MinIO: the upload endpoint is where the size
        guard, the storage write and the RagService ingest trigger all live, and seeding the
        bucket would skip all three while looking like the same starting state.
        """
        resp = expect_ok(
            self._http.post(
                f"/api/classrooms/{classroom_id}/files",
                files={"file": (file_name, content, content_type)},
                headers=teacher.auth,
            )
        )
        return resp.json()

    def upload_file_response(
        self, teacher: Account, classroom_id: str, *, file_name: str, content: bytes,
        content_type: str = "application/octet-stream",
    ) -> httpx.Response:
        """Unwrapped, for the cases whose point is the refusal (an oversized upload)."""
        return self._http.post(
            f"/api/classrooms/{classroom_id}/files",
            files={"file": (file_name, content, content_type)},
            headers=teacher.auth,
        )

    def list_files(self, account: Account, classroom_id: str) -> list[dict]:
        resp = expect_ok(
            self._http.get(f"/api/classrooms/{classroom_id}/files", headers=account.auth)
        )
        return resp.json()

    def list_files_response(self, account: Account, classroom_id: str) -> httpx.Response:
        return self._http.get(f"/api/classrooms/{classroom_id}/files", headers=account.auth)

    def indexing_status(self, account: Account, classroom_id: str, file_id: str) -> str:
        resp = expect_ok(
            self._http.get(
                f"/api/classrooms/{classroom_id}/files/{file_id}/indexing-status",
                headers=account.auth,
            )
        )
        return str(get_ci(resp.json(), "status", ""))

    def delete_file(self, teacher: Account, classroom_id: str, file_id: str) -> None:
        expect_ok(
            self._http.delete(
                f"/api/classrooms/{classroom_id}/files/{file_id}", headers=teacher.auth
            )
        )

    def upload_limits(self, account: Account, classroom_id: str) -> dict:
        resp = expect_ok(
            self._http.get(
                f"/api/classrooms/{classroom_id}/files/upload-limits", headers=account.auth
            )
        )
        return resp.json()

    # --- membership + lifecycle (§8.3) ---------------------------------------

    def members_response(self, account: Account, classroom_id: str) -> httpx.Response:
        return self._http.get(f"/api/classrooms/{classroom_id}/members", headers=account.auth)

    def get_classroom(self, account: Account, classroom_id: str) -> dict:
        resp = expect_ok(
            self._http.get(f"/api/classrooms/{classroom_id}", headers=account.auth)
        )
        return resp.json()

    def get_classroom_response(self, account: Account, classroom_id: str) -> httpx.Response:
        return self._http.get(f"/api/classrooms/{classroom_id}", headers=account.auth)

    def delete_classroom(self, teacher: Account, classroom_id: str) -> None:
        expect_ok(
            self._http.delete(f"/api/classrooms/{classroom_id}", headers=teacher.auth)
        )

    def sessions_response(self, account: Account, classroom_id: str) -> httpx.Response:
        return self._http.get(f"/api/classrooms/{classroom_id}/sessions", headers=account.auth)

    # --- session outputs (§8.4) ----------------------------------------------

    def list_recordings(self, account: Account, classroom_id: str) -> dict:
        resp = expect_ok(
            self._http.get(f"/api/classrooms/{classroom_id}/recordings", headers=account.auth)
        )
        return resp.json()

    def list_summaries(self, account: Account, classroom_id: str) -> dict:
        resp = expect_ok(
            self._http.get(f"/api/classrooms/{classroom_id}/summaries", headers=account.auth)
        )
        return resp.json()

    # --- quizzes, the rest of the loop (§8.6) --------------------------------

    def get_open_quiz(self, account: Account, classroom_id: str, session_id: str) -> dict | None:
        resp = expect_ok(
            self._http.get(
                f"/api/classrooms/{classroom_id}/sessions/{session_id}/quizzes/open",
                headers=account.auth,
            )
        )
        return resp.json() if resp.content and resp.text.strip() not in ("", "null") else None

    def publish_quiz(self, teacher: Account, classroom_id: str, quiz_id: str) -> dict:
        return expect_ok(self.publish_quiz_response(teacher, classroom_id, quiz_id)).json()

    def answer_quiz(
        self, student: Account, classroom_id: str, quiz_id: str, *, question_id: str, option_id: str
    ) -> dict:
        resp = expect_ok(
            self._http.post(
                f"/api/classrooms/{classroom_id}/quizzes/{quiz_id}/answers",
                json={"questionId": question_id, "optionId": option_id},
                headers=student.auth,
            )
        )
        return resp.json()

    def answer_quiz_response(
        self, student: Account, classroom_id: str, quiz_id: str, *, question_id: str, option_id: str
    ) -> httpx.Response:
        return self._http.post(
            f"/api/classrooms/{classroom_id}/quizzes/{quiz_id}/answers",
            json={"questionId": question_id, "optionId": option_id},
            headers=student.auth,
        )

    def submit_quiz(self, student: Account, classroom_id: str, quiz_id: str) -> dict:
        resp = expect_ok(
            self._http.post(
                f"/api/classrooms/{classroom_id}/quizzes/{quiz_id}/submit",
                headers=student.auth,
            )
        )
        return resp.json()

    def close_quiz(self, teacher: Account, classroom_id: str, quiz_id: str) -> dict:
        resp = expect_ok(
            self._http.post(
                f"/api/classrooms/{classroom_id}/quizzes/{quiz_id}/close", headers=teacher.auth
            )
        )
        return resp.json()

    def extend_quiz(
        self, teacher: Account, classroom_id: str, quiz_id: str, *,
        seconds: int, student_ids: list[str] | None = None,
    ) -> dict:
        body: dict = {"seconds": seconds}
        if student_ids is not None:
            body["studentIds"] = student_ids
        resp = expect_ok(
            self._http.post(
                f"/api/classrooms/{classroom_id}/quizzes/{quiz_id}/extend",
                json=body,
                headers=teacher.auth,
            )
        )
        return resp.json()

    def quiz_results(self, teacher: Account, classroom_id: str, quiz_id: str) -> dict:
        resp = expect_ok(
            self._http.get(
                f"/api/classrooms/{classroom_id}/quizzes/{quiz_id}/results", headers=teacher.auth
            )
        )
        return resp.json()

    def my_quiz_result(self, student: Account, classroom_id: str, quiz_id: str) -> dict:
        resp = expect_ok(
            self._http.get(
                f"/api/classrooms/{classroom_id}/quizzes/{quiz_id}/my-result",
                headers=student.auth,
            )
        )
        return resp.json()

    def quiz_draft_with(
        self, teacher: Account, classroom_id: str, session_id: str, *,
        title: str, questions: list[dict],
    ) -> str:
        """A draft with caller-supplied questions, so a suite can control the answer key."""
        resp = expect_ok(
            self._http.post(
                f"/api/classrooms/{classroom_id}/sessions/{session_id}/quizzes",
                json={"title": title, "questions": questions},
                headers=teacher.auth,
            )
        )
        quiz_id = get_ci(resp.json(), "id")
        assert quiz_id, f"create quiz draft returned no id: {resp.text}"
        return quiz_id
