"""A fake pipeline handle for SessionManager/endpoint tests — no event loop, no models.

Implements the tiny surface SessionManager depends on: ``start()`` returns a handle
with ``add_done_callback`` (like an asyncio.Task) and ``stop()`` finishes it.
``finish()`` fires the done-callbacks to simulate a pipeline ending on its own. It is
deliberately loop-free so it survives Starlette TestClient's per-request event loops.
"""

from __future__ import annotations


class _DoneHandle:
    """Minimal stand-in for the asyncio.Task returned by SessionPipeline.start()."""

    def __init__(self) -> None:
        self._callbacks: list = []
        self._done = False

    def add_done_callback(self, callback) -> None:
        if self._done:
            callback(self)
        else:
            self._callbacks.append(callback)

    def done(self) -> bool:
        return self._done

    def _finish(self) -> None:
        if self._done:
            return
        self._done = True
        for callback in self._callbacks:
            callback(self)


class FakePipeline:
    def __init__(self) -> None:
        self.started = False
        self.stopped = False
        self._handle = _DoneHandle()

    def start(self) -> _DoneHandle:
        self.started = True
        return self._handle

    async def stop(self) -> None:
        self.stopped = True
        self._handle._finish()  # deregister callback fires; manager already popped -> no-op

    def finish(self) -> None:
        """Simulate the pipeline task ending on its own (crash / stream end)."""
        self._handle._finish()


class FakePipelineFactory:
    """Records the pipelines it creates so tests can inspect start/stop."""

    def __init__(self) -> None:
        self.created: list[FakePipeline] = []

    def __call__(self, session) -> FakePipeline:
        pipeline = FakePipeline()
        self.created.append(pipeline)
        return pipeline
