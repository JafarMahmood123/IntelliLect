"""A recording stand-in for `httpx.AsyncClient`, shared by the embedding provider tests.

The providers build their own client inside the method under test, so there is nothing to
inject — the class itself is patched. Every request is recorded, because most of what these
tests assert is about the request that was *sent* (task type, dimensionality, where the API
key travelled) rather than about what came back.
"""

from __future__ import annotations

import asyncio
from collections.abc import Callable
from dataclasses import dataclass, field


@dataclass
class Request:
    url: str
    json: dict
    headers: dict

    @property
    def text(self) -> str:
        """The single text Gemini's embedContent carries, for readability at the call site."""
        return self.json["content"]["parts"][0]["text"]


class FakeResponse:
    def __init__(self, status_code: int = 200, payload: dict | None = None, text: str = "") -> None:
        self.status_code = status_code
        self._payload = payload if payload is not None else {}
        self.text = text

    def json(self) -> dict:
        return self._payload


@dataclass
class FakeHttp:
    """Records requests and answers them with whatever `responder` returns.

    Also tracks how many calls are in flight at once, which is the only way to observe that a
    fan-out is actually bounded rather than merely written to look bounded.
    """

    responder: Callable[[Request], FakeResponse]
    requests: list[Request] = field(default_factory=list)
    # Kwargs the provider passed to `AsyncClient(...)`. The Ollama provider sets its auth header
    # on the client rather than per request, so this is where that lands.
    client_kwargs: list[dict] = field(default_factory=list)
    in_flight: int = 0
    peak_in_flight: int = 0
    # Awaited inside `post`, so a test can make one request finish after a later one and prove
    # the provider does not rely on completion order.
    delay_for: Callable[[Request], float] | None = None

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
                outer.in_flight += 1
                outer.peak_in_flight = max(outer.peak_in_flight, outer.in_flight)
                try:
                    delay = outer.delay_for(request) if outer.delay_for else 0
                    # A zero sleep still yields to the loop, which is what lets the other
                    # coroutines start and makes the concurrency observation meaningful.
                    await asyncio.sleep(delay)
                    return outer.responder(request)
                finally:
                    outer.in_flight -= 1

        def _build(*_args, **kwargs):
            outer.client_kwargs.append(dict(kwargs))
            return _Client()

        return _build
