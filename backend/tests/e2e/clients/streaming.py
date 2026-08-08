"""StreamingService client: mint a per-participant LiveKit join token.

`GET /api/streams/{sessionId}` returns a StreamResponse whose `joinToken` is a
LiveKit access token with the caller's user id as the participant identity — which
is exactly the `teacherIdentity` the agent watches for. Routed via the gateway
(`/api/streams/*`).
"""

from __future__ import annotations

from dataclasses import dataclass

import httpx

from clients.http import expect_ok, get_ci
from clients.ums import Account


@dataclass
class StreamToken:
    session_id: str
    status: str
    join_token: str
    livekit_host: str
    participant_count: int


class StreamingClient:
    def __init__(self, gateway_url: str, timeout_s: float) -> None:
        self._http = httpx.Client(base_url=gateway_url, timeout=timeout_s)

    def close(self) -> None:
        self._http.close()

    def get_stream(self, participant: Account, session_id: str) -> StreamToken:
        resp = expect_ok(
            self._http.get(f"/api/streams/{session_id}", headers=participant.auth)
        )
        data = resp.json()
        return StreamToken(
            session_id=str(get_ci(data, "sessionId")),
            status=str(get_ci(data, "status")),
            join_token=get_ci(data, "joinToken"),
            livekit_host=get_ci(data, "liveKitHost"),
            participant_count=int(get_ci(data, "participantCount", 0)),
        )

    # --- the roster and the interaction surface (§8.4, §7.4d/e) ---------------
    #
    # Raw responses on purpose. Every one of these is asserted in both directions — a member
    # succeeds, a stranger is refused — and a client that raised on a non-2xx would make the
    # refusal half unwriteable without catching exceptions for control flow.

    def get_stream_response(self, participant: Account, session_id: str) -> httpx.Response:
        return self._http.get(f"/api/streams/{session_id}", headers=participant.auth)

    def join(self, participant: Account, session_id: str) -> httpx.Response:
        return self._http.post(f"/api/streams/{session_id}/join", headers=participant.auth)

    def leave(self, participant: Account, session_id: str) -> httpx.Response:
        return self._http.delete(f"/api/streams/{session_id}/leave", headers=participant.auth)

    def chat_history(self, participant: Account, session_id: str) -> httpx.Response:
        return self._http.get(f"/api/streams/{session_id}/chat", headers=participant.auth)

    def questions(self, participant: Account, session_id: str) -> httpx.Response:
        return self._http.get(f"/api/streams/{session_id}/questions", headers=participant.auth)

    def ask_question(self, participant: Account, session_id: str, text: str) -> httpx.Response:
        return self._http.post(
            f"/api/streams/{session_id}/questions",
            json={"questionText": text},
            headers=participant.auth,
        )
