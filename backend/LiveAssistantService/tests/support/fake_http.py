"""A recording stand-in for `httpx.AsyncClient`, for the brain clients.

They build their own client inside the method under test, so there is nothing to inject — the
class itself is patched. Requests are recorded because most of what these tests assert is about
the request that was *sent*: which generation caps applied, where the API key travelled, what
shape the response schema arrived in.
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field


@dataclass
class Request:
    url: str
    json: dict
    headers: dict

    @property
    def system_prompt(self) -> str:
        return self.json["systemInstruction"]["parts"][0]["text"]

    @property
    def user_prompt(self) -> str:
        return self.json["contents"][0]["parts"][0]["text"]

    @property
    def generation_config(self) -> dict:
        return self.json["generationConfig"]


class FakeResponse:
    def __init__(
        self, status_code: int = 200, payload: dict | None = None, text: str = ""
    ) -> None:
        self.status_code = status_code
        self._payload = payload if payload is not None else {}
        # `response.text` is read into several error messages, so it has to be a real string.
        self.text = text

    def json(self) -> dict:
        return self._payload


def reply(text: str) -> FakeResponse:
    """A well-formed generateContent response carrying `text`."""
    return FakeResponse(200, {"candidates": [{"content": {"parts": [{"text": text}]}}]})


@dataclass
class FakeHttp:
    responder: Callable[[Request], FakeResponse]
    requests: list[Request] = field(default_factory=list)
    client_kwargs: list[dict] = field(default_factory=list)

    def client_factory(self):
        """Returns something usable as `httpx.AsyncClient(...)`, recording its arguments."""
        outer = self

        class _Client:
            async def __aenter__(self):
                return self

            async def __aexit__(self, *_):
                return False

            async def post(self, url, json=None, headers=None):
                request = Request(url=url, json=json or {}, headers=headers or {})
                outer.requests.append(request)
                return outer.responder(request)

        def _build(*_args, **kwargs):
            outer.client_kwargs.append(dict(kwargs))
            return _Client()

        return _build
