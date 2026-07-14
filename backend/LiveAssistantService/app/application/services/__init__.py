"""Application services (use cases) for LiveAssistantService.

Placeholder for the future **live-loop orchestrator** (later phase): the use case
that wires the ports together — pull normalized frames from an ``AudioSource``,
transcribe them via ``SpeechToText``, detect when the teacher finishes an "idea",
retrieve classroom material via ``RetrievalClient``, evaluate the idea with
``BrainClient``, and privately deliver any correction through ``FeedbackSink``.

Nothing lives here yet. This phase (LA-0 + LA-1) builds only the skeleton and the
``AudioSource`` implementations; the orchestrator depends solely on the port
abstractions in ``app.application.ports`` and no framework/SDK.
"""
