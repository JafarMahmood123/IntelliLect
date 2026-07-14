# LiveAssistantService

A Python microservice for the IntelliLect platform. Its eventual purpose is to join
a live class session as a **server-side agent**, transcribe the teacher's speech,
detect when the teacher finishes an "idea", check that idea against the classroom's
uploaded material via the existing **KnowledgeService** (RAG), and **privately**
suggest corrections to the teacher only.

**This build is the foundation only (phases LA-0 + LA-1).** It contains the
clean-architecture skeleton and a LiveKit agent that **captures the teacher's live
audio** behind an `AudioSource` port. STT, transcript buffering, boundary detection,
embedding, retrieval, brain evaluation, and feedback delivery are **not** implemented
— the code leaves clearly-marked ports and placeholders for them.

> **No models in the container.** There is no `torch`, `transformers`, or Ollama
> dependency, and no STT model. This phase pulls in **no** ML weights. `numpy` is
> present for audio format normalization only.

## What works today

- Clean-architecture skeleton with strict dependency inversion: the **domain** layer
  is plain Python (no framework/SDK imports); **application** defines the ports;
  **infrastructure** implements them; **api** depends only on the application ports.
- `AudioSource` port with a real `LiveKitAudioSource` and an offline
  `FakeAudioSource`.
- `LiveKitAudioSource` — joins a LiveKit room as `AGENT_IDENTITY`, subscribes to
  **only** the teacher's audio track (auto-subscribe off, so student tracks are never
  received), waits for the teacher if they haven't joined yet, and exposes the audio
  as an async stream of `AudioFrame`s **normalized to `TARGET_SAMPLE_RATE`/mono**.
- `FakeAudioSource` — yields the same normalized `AudioFrame`s from a local WAV file
  (any rate/channels) or a synthesized tone, with **no LiveKit and no live session**,
  so every later phase can be built and tested offline.
- Audio normalization (`numpy`): channel downmix + linear-interpolation resample to
  `TARGET_SAMPLE_RATE`/mono, owned and unit-tested in-service rather than delegated to
  the SDK.
- `GET /health` → `{"status": "ok", "livekit": "configured" | "not-configured"}`.
  Always `200`; it reports whether LiveKit join credentials are present but does
  **not** require a live connection.
- `scripts/capture_check.py` — a CLI that drives an `AudioSource` and prints frame
  count / duration / sample rate, proving normalization end-to-end (offline by
  default; `--livekit` mode connects to a real room).

## Ports (later phases)

Defined now under `app/application/ports/`; only `AudioSource` is implemented this
phase. The rest are abstract stubs (`raise NotImplementedError`) with the signatures
their phase will need:

| Port | Purpose (later phase) |
| --- | --- |
| `AudioSource` | **Implemented.** Stream of the teacher's normalized audio. |
| `SpeechToText` | Streaming transcription of audio frames → incremental text. |
| `EmbeddingProvider` | Embed a finished idea for retrieval. |
| `RetrievalClient` | Classroom-scoped RAG search via KnowledgeService. |
| `BrainClient` | Judge an idea against retrieved material; propose a correction. |
| `FeedbackSink` | Deliver a correction to the teacher **privately**. |

The future **live-loop orchestrator** (the use case that wires these together) will
live in `app/application/services/` — currently an empty, documented placeholder.

## Architecture

```
app/
  domain/            # plain Python — no framework/SDK imports
    entities/        # SessionContext
    audio/           # AudioFrame
  application/
    ports/           # AudioSource (+ SpeechToText, EmbeddingProvider,
                     #   RetrievalClient, BrainClient, FeedbackSink stubs)
    services/        # placeholder for the future live-loop orchestrator
  infrastructure/
    config/          # pydantic-settings Settings
    audio/           # LiveKitAudioSource, FakeAudioSource, normalization
  api/
    main.py          # FastAPI app factory
    dependencies.py  # composition root (names concrete classes)
    routers/         # health
scripts/capture_check.py
tests/
```

### Why raw `livekit` (rtc) rather than `livekit-agents`

The `AudioSource` port models an explicit `connect` / `frames` / `disconnect`
lifecycle. That maps directly onto `livekit.rtc.Room`, so the realtime SDK
(`livekit`) plus token minting (`livekit-api`) is a cleaner fit than the
`livekit-agents` worker/job-dispatch framework. LiveKit SDK calls whose shape is
version-sensitive are marked with `# SDK:` comments in `livekit_audio_source.py` so
they can be re-verified against the pinned SDK.

## Configuration

All variables are read by `app/infrastructure/config/settings.py` (case-insensitive).
See `.env.example`.

| Variable | Default | Notes |
| --- | --- | --- |
| `LIVEKIT_URL` | _(empty)_ | LiveKit server URL. Empty → offline only. |
| `LIVEKIT_API_KEY` | _(empty)_ | For minting the agent's join token. |
| `LIVEKIT_API_SECRET` | _(empty)_ | For minting the agent's join token. |
| `AGENT_IDENTITY` | `ai-assistant` | Identity the agent joins under. |
| `TARGET_SAMPLE_RATE` | `16000` | Normalize captured audio to this rate. |
| `TARGET_CHANNELS` | `1` | Mono. |
| `LOG_LEVEL` | `INFO` | Root log level. |
| `KNOWLEDGE_BASE_URL` | _(empty)_ | Placeholder — unused this phase. |
| `INTERNAL_API_SECRET` | _(empty)_ | Placeholder — unused this phase. |

`/health` reports `livekit: configured` only when `LIVEKIT_URL`, `LIVEKIT_API_KEY`,
and `LIVEKIT_API_SECRET` are all set.

## Running

### Docker (with the rest of the stack)

From `backend/`:

```bash
docker compose up -d live-assistant-service    # or ./build.sh
curl localhost:8084/health
# {"status":"ok","livekit":"configured"}
```

The compose entry points `LIVEKIT_URL` at the in-stack `livekit-server` (defined in
`StreamingService/docker-compose.unit.yml`) using its `--dev` credentials, so the
real capture path is reachable within the stack. The service is published on
host port **8084**.

### Capture check (offline — no LiveKit, no models)

```bash
python scripts/capture_check.py               # synthesized 440Hz tone
python scripts/capture_check.py some.wav       # any 16-bit WAV (any rate/channels)
```

Prints frame count, total duration, sample rate, and channel count. Expect mono
frames at `TARGET_SAMPLE_RATE` and `RESULT: OK`.

### Capture check (real room — DEFERRED)

Requires LiveKit credentials **and** a live room with the teacher publishing audio,
so this is exercised only once the stack is running:

```bash
python scripts/capture_check.py --livekit \
    --room <room_name> --teacher <teacher_identity> [--classroom <uuid>] [--seconds 10]

# inside the container (credentials already set by compose):
docker compose exec live-assistant-service \
    python scripts/capture_check.py --livekit --room demo --teacher teacher-1
```

## Tests

Fully offline — no LiveKit server, no network, no models. `livekit` is imported
**lazily** inside `LiveKitAudioSource`, so the suite runs without the SDK installed.

```bash
pip install -e '.[dev]'
pytest
```

Covers: `/health` (configured / not-configured), `FakeAudioSource` frame format /
rate / channels / ordering from an in-code WAV fixture, and the normalization path
(a stereo 48kHz fixture comes out mono at 16kHz). The live LiveKit path is not
unit-tested against a server; it is isolated behind lazy imports and exercised only
via the deferred `--livekit` CLI mode.
