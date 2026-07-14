# LiveAssistantService

A Python microservice for the IntelliLect platform. Its eventual purpose is to join
a live class session as a **server-side agent**, transcribe the teacher's speech,
detect when the teacher finishes an "idea", check that idea against the classroom's
uploaded material via the existing **KnowledgeService** (RAG), and **privately**
suggest corrections to the teacher only.

**This build covers phases LA-0 → LA-2.** It contains the clean-architecture
skeleton, a LiveKit agent that **captures the teacher's live audio** behind an
`AudioSource` port, and **streaming English speech-to-text** behind a `SpeechToText`
port. Transcript buffering, idea/boundary detection (LA-3), embedding, retrieval,
brain evaluation, and feedback delivery are **not** implemented — the code leaves
clearly-marked ports and placeholders for them.

> **No Ollama, no torch/transformers in this service.** STT runs on **faster-whisper
> (CTranslate2)** — a self-contained inference engine that uses its **own** English
> model, unrelated to the embedder/brain. Its model downloads on first use (see
> [STT model & resources](#stt-model--resources)). `numpy` is present for audio
> normalization only.

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
- **Streaming English STT (LA-2)** — `SpeechToText` port with a real
  `FasterWhisperSpeechToText` (faster-whisper / CTranslate2). It consumes the
  normalized `AudioFrame` stream from **any** `AudioSource` (LiveKit or Fake — no
  LiveKit required), accumulates a rolling per-utterance buffer, emits **interim**
  `TranscriptSegment`s every `STT_CHUNK_SECONDS`, and **finalizes** a segment when a
  silence gap ≥ `STT_PAUSE_SECONDS` is detected (flagging `followed_by_pause`) or the
  stream ends. Blocking model calls run off the event loop via `asyncio.to_thread`.
- `FakeSpeechToText` (test support) — yields **scripted** `TranscriptSegment`s
  deterministically, so LA-3+ can be unit-tested with no model.
- `scripts/stt_check.py` — a CLI that runs a WAV through `FakeAudioSource` →
  `FasterWhisperSpeechToText` and prints each segment (timing, `is_final`,
  `followed_by_pause`, text) — eyeball transcript quality and pause detection with
  **no LiveKit and no Ollama**.

## Ports (later phases)

Defined now under `app/application/ports/`; only `AudioSource` is implemented this
phase. The rest are abstract stubs (`raise NotImplementedError`) with the signatures
their phase will need:

| Port | Purpose (later phase) |
| --- | --- |
| `AudioSource` | **Implemented (LA-1).** Stream of the teacher's normalized audio. |
| `SpeechToText` | **Implemented (LA-2).** Streaming English transcription → `TranscriptSegment`s. |
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
    transcript/      # TranscriptSegment
  application/
    ports/           # AudioSource, SpeechToText (+ EmbeddingProvider,
                     #   RetrievalClient, BrainClient, FeedbackSink stubs)
    services/        # placeholder for the future live-loop orchestrator
  infrastructure/
    config/          # pydantic-settings Settings
    audio/           # LiveKitAudioSource, FakeAudioSource, normalization
    stt/             # FasterWhisperSpeechToText, audio_analysis (energy/pause)
  api/
    main.py          # FastAPI app factory
    dependencies.py  # composition root (names concrete classes)
    routers/         # health
scripts/capture_check.py, scripts/stt_check.py
tests/                # offline; tests/support/FakeSpeechToText for later phases
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
| `TARGET_SAMPLE_RATE` | `16000` | Normalize captured audio to this rate (STT assumes 16k). |
| `TARGET_CHANNELS` | `1` | Mono. |
| `STT_MODEL` | `base.en` | faster-whisper English model id (`tiny.en`/`base.en`/`small.en`/…). |
| `STT_DEVICE` | `cpu` | `cpu` or `cuda`. |
| `STT_COMPUTE_TYPE` | `int8` | `int8` (low RAM on CPU), `float16`, … |
| `STT_LANGUAGE` | `en` | English only for now (Arabic deferred). |
| `STT_CHUNK_SECONDS` | `3.0` | Audio accumulated before a transcription step (interim cadence). |
| `STT_PAUSE_SECONDS` | `0.8` | Trailing silence that marks a segment boundary. |
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

### STT check (offline — no LiveKit, no Ollama)

Transcribe an English WAV through `FakeAudioSource` → `FasterWhisperSpeechToText` and
watch interim/final segments and pause flags stream out:

```bash
python scripts/stt_check.py path/to/english.wav
STT_MODEL=small.en python scripts/stt_check.py path/to/english.wav   # override model
```

Example output (`[FINAL]` lines marked `<pause>` were closed by a detected silence gap):

```text
[interim]       0-2000   ms  The mitochondria is the powerhouse ...
[FINAL ]       0-2980   ms  <pause>  The mitochondria is the powerhouse of the cell.
[FINAL ]    3480-6837   ms  Photosynthesis converts sunlight into chemical energy.
```

## STT model & resources

STT uses **faster-whisper (CTranslate2)** with its **own** English model — no Ollama,
no torch/transformers. The model (`STT_MODEL`, default `base.en`) **downloads from
HuggingFace on first use**; in Docker it is cached in the `live_assistant_hf_cache`
volume so restarts don't re-download. To pre-pull it, run the STT check (or any
transcription) once while the container has network, or bake the download into the
image.

STT runs **continuously during a session, alongside the embedder and the 7B brain**,
so size the model to fit the shared memory budget: **`base.en` (or `small.en`) with
`int8`** on CPU is the safe default. Larger models (`medium.en`, `float16`, or `cuda`)
are a later, hardware-dependent upgrade — change `STT_MODEL` / `STT_DEVICE` /
`STT_COMPUTE_TYPE`, nothing else, since the engine is fully behind the `SpeechToText`
port.

faster-whisper is not natively streaming, so `FasterWhisperSpeechToText` implements
_pseudo-streaming_ (re-transcribe a growing per-utterance window; finalize on a
silence gap). SDK calls whose shape is version-sensitive carry `# SDK:` comments.

## Tests

Fully offline — no LiveKit server, no network, no models. Both `livekit` (in
`LiveKitAudioSource`) and `faster_whisper` (in `FasterWhisperSpeechToText`) are
imported **lazily**, so the whole suite runs without either installed.

```bash
pip install -e '.[dev]'
pytest
```

Covers:

- `/health` (configured / not-configured).
- `FakeAudioSource` frame format / rate / channels / ordering, and normalization (a
  stereo 48kHz fixture comes out mono at 16kHz).
- **STT streaming state machine** — a `tone/silence/tone` fixture through
  `FakeAudioSource` into a subclass that stubs `_transcribe_window` (no model):
  asserts two utterances split on the pause, interim-before-final ordering, exactly
  the first utterance flagged `followed_by_pause`, sane monotonic timings, and pure
  silence yielding nothing.
- **STT energy/pause helpers** (`audio_analysis`) directly.
- **`FakeSpeechToText`** yields scripted segments deterministically (the seam LA-3+
  tests against).

Two paths are **not** unit-tested against real infrastructure and are isolated behind
lazy imports:

- The live LiveKit path — exercised only via the deferred `capture_check.py --livekit`
  mode.
- The real Whisper model — `tests/test_faster_whisper_real.py` is **opt-in** and
  skips cleanly unless faster-whisper is installed **and** an English WAV is provided
  (`STT_TEST_WAV=/path/to.wav` or a file in `tests/fixtures/`). No audio is committed.
