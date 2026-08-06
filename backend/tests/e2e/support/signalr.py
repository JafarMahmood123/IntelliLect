"""A minimal SignalR hub client, written for *measuring* rather than for using.

The obvious move is `signalrcore` off PyPI. It was rejected on purpose: it runs its
receive loop on a background thread with its own sleep-based polling, and the interval
of that poll lands directly inside the number this harness exists to measure. An
instrument that adds tens of milliseconds of jitter to a hop budgeted at 250ms is not
measuring the hop, it is measuring itself (work-plan §9.2).

What is left once you drop the library is small, because the frontend connects with
`skipNegotiation: true` over WebSockets (`useStreamHub.ts`), so there is no negotiate
handshake, no long-polling fallback and no transport selection to reproduce — just a
WebSocket, the SignalR JSON handshake, and 0x1e-separated records.

The one rule that matters here: **the arrival timestamp is taken the instant a frame
comes off the socket, before it is parsed or dispatched.** JSON decoding a chat message
is microseconds, but writing it the other way round is how a harness quietly folds its
own work into the result.
"""

from __future__ import annotations

import asyncio
import json
import logging
import time
from dataclasses import dataclass
from typing import Any

from websockets.asyncio.client import connect

logger = logging.getLogger("e2e.signalr")

# SignalR frames a message with 0x1e ("record separator"). One WebSocket frame can hold
# several records, which is why the pump splits rather than parsing straight through.
RECORD_SEPARATOR = "\x1e"

_INVOCATION = 1
_COMPLETION = 3
_PING = 6
_CLOSE = 7


def to_websocket_url(http_url: str, path: str) -> str:
    """`http://localhost` + `/hubs/stream` -> `ws://localhost/hubs/stream`."""
    base = http_url.rstrip("/")
    if base.startswith("https://"):
        base = "wss://" + base[len("https://") :]
    elif base.startswith("http://"):
        base = "ws://" + base[len("http://") :]
    return base + path


@dataclass(frozen=True)
class Received:
    """One server->client invocation, with the moment its frame arrived."""

    target: str
    arguments: list[Any]
    at: float  # time.perf_counter(), taken before parsing


class SignalRClient:
    """One hub connection. Not thread-safe; drive it from a single event loop."""

    def __init__(self, hub_url: str, access_token: str, *, name: str = "client") -> None:
        self._url = f"{hub_url}?access_token={access_token}"
        self._name = name
        self._ws: Any = None
        self._pump: asyncio.Task | None = None
        self._watchers: dict[str, asyncio.Queue[Received]] = {}
        self._pending: dict[str, asyncio.Future] = {}
        self._next_invocation = 0

    # --- lifecycle ------------------------------------------------------------

    async def __aenter__(self) -> "SignalRClient":
        await self.connect()
        return self

    async def __aexit__(self, *exc) -> None:
        await self.close()

    async def connect(self, timeout_s: float = 15.0) -> None:
        self._ws = await asyncio.wait_for(connect(self._url, max_size=None), timeout_s)
        await self._write({"protocol": "json", "version": 1})
        raw = await asyncio.wait_for(self._ws.recv(), timeout_s)
        response = json.loads(str(raw).split(RECORD_SEPARATOR)[0] or "{}")
        if response.get("error"):
            raise AssertionError(f"SignalR handshake refused: {response['error']}")
        self._pump = asyncio.create_task(self._read_loop(), name=f"signalr-{self._name}")
        logger.info("[%s] hub connected", self._name)

    async def close(self) -> None:
        if self._pump is not None:
            self._pump.cancel()
            try:
                await self._pump
            except (asyncio.CancelledError, Exception):  # noqa: BLE001 — teardown
                pass
        if self._ws is not None:
            try:
                await self._write({"type": _CLOSE})
            except Exception:  # noqa: BLE001 — the socket may already be gone
                pass
            await self._ws.close()

    # --- sending --------------------------------------------------------------

    async def _write(self, message: dict) -> None:
        await self._ws.send(json.dumps(message) + RECORD_SEPARATOR)

    async def send(self, target: str, *args: Any) -> float:
        """Fire-and-forget invocation. Returns the perf_counter reading taken as late as
        possible before the bytes go out — the start of the measured interval."""
        payload = json.dumps({"type": _INVOCATION, "target": target, "arguments": list(args)})
        frame = payload + RECORD_SEPARATOR
        sent_at = time.perf_counter()
        await self._ws.send(frame)
        return sent_at

    async def invoke(self, target: str, *args: Any, timeout_s: float = 15.0) -> Any:
        """Invocation that waits for the server's completion — used for setup steps like
        JoinStreamRoom, where measuring before the room membership exists would time a
        message nobody was listening for."""
        self._next_invocation += 1
        invocation_id = str(self._next_invocation)
        waiter: asyncio.Future = asyncio.get_running_loop().create_future()
        self._pending[invocation_id] = waiter
        await self._write(
            {
                "type": _INVOCATION,
                "invocationId": invocation_id,
                "target": target,
                "arguments": list(args),
            }
        )
        try:
            return await asyncio.wait_for(waiter, timeout_s)
        finally:
            self._pending.pop(invocation_id, None)

    # --- receiving ------------------------------------------------------------

    def watch(self, target: str) -> asyncio.Queue[Received]:
        """Start queueing a server->client method. Must be called BEFORE the event can
        occur: a queue registered afterwards has already missed it, and the test would
        time out rather than report a slow hop."""
        queue: asyncio.Queue[Received] = self._watchers.setdefault(target, asyncio.Queue())
        return queue

    async def next(self, target: str, timeout_s: float) -> Received:
        return await asyncio.wait_for(self.watch(target).get(), timeout_s)

    async def _read_loop(self) -> None:
        while True:
            raw = await self._ws.recv()
            # Stamped here, before json.loads, before dispatch. See the module docstring.
            arrived_at = time.perf_counter()
            for record in str(raw).split(RECORD_SEPARATOR):
                if not record:
                    continue
                try:
                    message = json.loads(record)
                except ValueError:
                    continue
                await self._dispatch(message, arrived_at)

    async def _dispatch(self, message: dict, arrived_at: float) -> None:
        kind = message.get("type")
        if kind == _PING:
            # The server pings every 15s and drops a connection that never answers. A
            # measurement run outliving one keep-alive interval would otherwise be cut
            # off mid-series and look like a latency failure.
            await self._write({"type": _PING})
        elif kind == _INVOCATION:
            target = message.get("target", "")
            queue = self._watchers.get(target)
            if queue is not None:
                queue.put_nowait(
                    Received(target=target, arguments=message.get("arguments", []), at=arrived_at)
                )
        elif kind == _COMPLETION:
            waiter = self._pending.get(str(message.get("invocationId")))
            if waiter is not None and not waiter.done():
                if message.get("error"):
                    waiter.set_exception(AssertionError(f"hub invocation failed: {message['error']}"))
                else:
                    waiter.set_result(message.get("result"))
        elif kind == _CLOSE:
            raise AssertionError(f"hub closed the connection: {message.get('error', 'no reason given')}")
