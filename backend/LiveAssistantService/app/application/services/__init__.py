"""Application services (use cases) for LiveAssistantService.

Pure application logic that depends only on the ports in ``app.application.ports``
and domain types — no framework/SDK/infrastructure imports.

- ``boundary_detector`` (LA-3) — segments the transcript stream into ``CompletedIdea``s
  by semantic drift, pause, and length/time caps.
- ``token_estimate`` — a tiny whitespace token estimator used by the length caps.

Still to come: the **live-loop orchestrator** that wires the ports end to end — pull
frames from an ``AudioSource``, transcribe via ``SpeechToText``, detect idea
boundaries here, retrieve classroom material via ``RetrievalClient``, evaluate with
``BrainClient``, and privately deliver corrections through ``FeedbackSink``.
"""
