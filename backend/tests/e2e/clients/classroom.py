"""ClassroomService client: classrooms, enrollment, sessions, session start.

Through the gateway (`/api/classrooms/*`). Starting a session is the trigger that
makes ClassroomService call StreamingService, which in turn spins up the LiveKit
room and notifies LiveAssistantService to start the agent pipeline.
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
