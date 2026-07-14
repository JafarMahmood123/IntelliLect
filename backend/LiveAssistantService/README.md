# LiveAssistantService

A Python microservice for the IntelliLect platform. Its eventual purpose is to join
a live class session as a **server-side agent**, transcribe the teacher's speech,
detect when the teacher finishes an "idea", check that idea against the classroom's
uploaded material via the existing **KnowledgeService** (RAG), and **privately**
suggest corrections to the teacher only.

**This build covers phases LA-0 → LA-4.** It contains the clean-architecture
skeleton, a LiveKit agent that **captures the teacher's live audio** behind an
`AudioSource` port, **streaming English speech-to-text** behind a `SpeechToText`
port, **idea boundary detection** that segments the transcript into completed
"ideas", and **retrieval + evaluation on each idea** — pulling relevant course
material from KnowledgeService and asking the brain whether the explanation has a
real problem. **Feedback delivery** (LA-5), **session lifecycle** (LA-6), and
**pacing/rate-limiting** (LA-7) are **not** implemented — the code leaves
clearly-marked ports and placeholders for them.

> **No torch/transformers in this service.** STT runs on **faster-whisper
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
- **Idea boundary detection (LA-3)** — `BoundaryDetector` (pure application service)
  consumes the `TranscriptSegment` stream, buffers finalized segments into an idea,
  and emits a `CompletedIdea` when a boundary fires: **DRIFT** (the newest segment's
  embedding is cosine-far from the running idea vector), **PAUSE** (`followed_by_pause`
  or a silent inter-segment gap), or the **TIME_CAP** / **TOKEN_CAP** safety nets.
  Fragments below `BOUNDARY_MIN_TOKENS` merge forward; a trailing idea is flushed at
  stream end. Only finalized segments drive decisions (interim text is ignored).
- `OllamaEmbeddingProvider` — implements `EmbeddingProvider.embed_query` via host
  Ollama (`POST /api/embed`) for drift measurement. No weights in the container; the
  boundary tests use a deterministic fake instead, so **no live model is required**.
- `FakeSpeechToText` / `FakeEmbeddingProvider` (test support) — scripted segments and
  deterministic topic vectors, so the boundary logic (and later phases) is unit-tested
  with no models.
- `scripts/boundary_check.py` — a CLI that segments a **scripted** transcript into
  ideas (DRIFT / PAUSE / caps / min-token merge / flush) with **no models**; a
  deferred `--live` mode chains real STT + Ollama.
- **Retrieval + evaluation on idea boundary (LA-4)** — `IdeaEvaluator` (pure
  application service) takes each `CompletedIdea` and: (1) retrieves the classroom's
  top-k material via `RetrievalClient`, (2) drops chunks below `RETRIEVAL_MIN_SCORE`
  and **short-circuits to "no feedback" without calling the brain** if nothing
  remains, (3) asks `BrainClient` to evaluate the idea against the material. A
  retrieval or brain failure degrades to "no feedback" — a failed evaluation never
  breaks the loop. `IdeaEvaluationPipeline` pipes a `CompletedIdea` stream through it.
- `KnowledgeRetrievalClient` — implements `RetrievalClient` via KnowledgeService
  `POST /api/search` (sends the idea **text**; KnowledgeService owns the vector DB),
  authed with `INTERNAL_API_SECRET`, mapping results to `RetrievedChunk`.
- `OllamaBrainClient` — implements `BrainClient` via host Ollama `POST /api/chat` with
  this service's own **grounded, silence-biased** evaluation prompt. Parses the reply
  as **strict JSON** (stripping code fences); malformed output degrades to "no
  feedback". Maps cited `[n]` back to source chunks.
- `FakeRetrievalClient` / `FakeBrainClient` (test support) — deterministic spies for
  the evaluator's paths, so LA-4 is tested with **no KnowledgeService and no Ollama**.
- `scripts/evaluate_check.py` — a CLI that evaluates a **scripted** idea against
  fixture chunks with **no models**; a deferred `--live` mode uses the real clients.

## Ports

Defined under `app/application/ports/`; the remaining stubs (`raise
NotImplementedError`) carry the signatures their phase will need:

| Port | Purpose |
| --- | --- |
| `AudioSource` | **Implemented (LA-1).** Stream of the teacher's normalized audio. |
| `SpeechToText` | **Implemented (LA-2).** Streaming English transcription → `TranscriptSegment`s. |
| `EmbeddingProvider` | **Implemented (LA-3).** Embed a segment/idea (local Ollama) for drift & later retrieval. |
| `RetrievalClient` | **Implemented (LA-4).** Classroom-scoped RAG search via KnowledgeService. |
| `BrainClient` | **Implemented (LA-4).** Judge an idea against retrieved material; propose a correction. |
| `FeedbackSink` | Deliver a correction to the teacher **privately** (LA-5). |

The future **live-loop orchestrator** (the use case that wires audio → STT → boundary
detection → retrieval → brain → feedback end to end) will join these in
`app/application/services/`, alongside the existing `BoundaryDetector` and
`IdeaEvaluator`.

## Architecture

```text
app/
  domain/            # plain Python — no framework/SDK imports
    entities/        # SessionContext
    audio/           # AudioFrame
    transcript/      # TranscriptSegment
    idea/            # CompletedIdea, BoundaryTrigger
    evaluation/      # FeedbackType, RetrievedChunk, TeacherSuggestion, EvaluationOutcome
  application/
    ports/           # AudioSource, SpeechToText, EmbeddingProvider, RetrievalClient,
                     #   BrainClient (+ FeedbackSink stub)
    services/        # boundary_detector, idea_evaluator, token_estimate
  infrastructure/
    config/          # pydantic-settings Settings
    audio/           # LiveKitAudioSource, FakeAudioSource, normalization
    stt/             # FasterWhisperSpeechToText, audio_analysis (energy/pause)
    embeddings/      # OllamaEmbeddingProvider
    retrieval/       # KnowledgeRetrievalClient (POST /api/search)
    brain/           # OllamaBrainClient, evaluation_prompt
  api/
    main.py          # FastAPI app factory
    dependencies.py  # composition root (names concrete classes)
    routers/         # health
scripts/capture_check.py, stt_check.py, boundary_check.py, evaluate_check.py
tests/                # offline; tests/support/Fake{SpeechToText,EmbeddingProvider,
                     #   RetrievalClient,BrainClient}
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
| `BOUNDARY_DRIFT_THRESHOLD` | `0.35` | Cosine distance marking a topic/idea change. |
| `BOUNDARY_PAUSE_SECONDS` | `0.8` | Pause (flag or silent gap) that ends an idea. |
| `BOUNDARY_MAX_SECONDS` | `90` | Safety-net cap on idea duration. |
| `BOUNDARY_MAX_TOKENS` | `400` | Safety-net cap on idea length (whitespace tokens). |
| `BOUNDARY_MIN_TOKENS` | `20` | Ideas below this merge forward (no boundary). |
| `OLLAMA_BASE_URL` | `http://host.docker.internal:11434` | Host Ollama for drift embeddings + the brain. |
| `OLLAMA_AUTH_TOKEN` | _(empty)_ | Optional bearer token, sent only if set. |
| `EMBEDDING_MODEL` | `qwen3-embedding` | Embedding model for drift (must be pulled in Ollama). |
| `EMBEDDING_TIMEOUT_SECONDS` | `60` | Ollama request timeout (embeddings). |
| `KNOWLEDGE_BASE_URL` | _(empty)_ | KnowledgeService base URL for retrieval (`POST /api/search`). |
| `INTERNAL_API_SECRET` | _(empty)_ | Shared secret sent as `X-Internal-Secret` to KnowledgeService. |
| `RETRIEVAL_TOP_K` | `6` | Chunks requested per idea. |
| `RETRIEVAL_MIN_SCORE` | `0.25` | Below this = "no relevant material" (short-circuit, no brain). |
| `EVAL_MODEL` | `qwen2.5:7b-instruct` | Brain (generation) model; must be pulled in Ollama. |
| `EVAL_TEMPERATURE` | `0.2` | Brain sampling temperature. |
| `EVAL_TIMEOUT_SECONDS` | `60` | Ollama request timeout (brain). |
| `EVAL_MAX_TOKENS` | `512` | Brain `num_predict`. |
| `LOG_LEVEL` | `INFO` | Root log level. |

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

### Boundary check (offline — no models)

Segment a **scripted** transcript into ideas with `BoundaryDetector`, using a
deterministic keyword embedder — proves DRIFT / PAUSE / caps / min-token merge / flush
with **no STT model and no Ollama**:

```bash
python scripts/boundary_check.py
```

```text
idea 1: trigger=Drift    tokens=13  segs=2 dur=  4.0s [0-4000ms]   Photosynthesis converts ...
idea 2: trigger=Pause    tokens=17  segs=2 dur=  4.0s [4000-8000ms]   Newton described gravity ...
idea 3: trigger=TokenCap tokens=29  segs=4 dur=  5.0s [8000-13000ms]   Okay Now let's discuss the history ...
idea 4: trigger=Pause    tokens=9   segs=1 dur=  2.0s [13000-15000ms]   In summary photosynthesis ...
```

A deferred `--live <wav>` mode chains the real STT (LA-2) + the real Ollama embedder;
it needs the STT model and a running Ollama with `EMBEDDING_MODEL` pulled.

### Evaluate check (offline — no models)

Evaluate a **scripted** idea against fixture chunks with `IdeaEvaluator`, using a fake
retrieval client and a fake brain — proves retrieval → short-circuit → evaluate and
citation→source mapping with **no KnowledgeService and no Ollama**:

```bash
python scripts/evaluate_check.py
```

```text
has_feedback : True
type         : Discrepancy
citations    : [1, 2]
suggestion   : The explanation conflicts with the material on photosynthesis location [1]; ...
sources:
  [1] (slide 4) score=0.82  Photosynthesis occurs in the chloroplast, not the mitochondria.
  [2] (page 12) score=0.66  The light-dependent reactions take place in the thylakoid membrane.
```

A deferred `--live --classroom <uuid> --idea "<text>"` mode uses the real
KnowledgeService retrieval + Ollama brain; it needs KnowledgeService reachable at
`KNOWLEDGE_BASE_URL` and Ollama running with `EVAL_MODEL` pulled.

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
- **Boundary detection** (`BoundaryDetector`) — scripted segments + a one-hot topic
  embedder cover every trigger: DRIFT at a topic shift, TOKEN_CAP / TIME_CAP on a
  monologue, PAUSE (flag and silent gap), a stray sub-`MIN_TOKENS` word merging
  forward, end-of-stream flush, and interim segments never splitting an idea (nor
  being embedded).
- **`FakeEmbeddingProvider`** determinism / orthogonal topic vectors.
- **Idea evaluation** (`IdeaEvaluator`) — with `FakeRetrievalClient` /
  `FakeBrainClient`: happy path (brain sees the relevant chunks), the no-results
  short-circuit (brain **never** called), `min_score` boundary, a consistent idea
  (brain called, returns no feedback), and retrieval/brain errors degrading to "no
  feedback"; plus the `IdeaEvaluationPipeline` over an idea stream.
- **Brain parsing** (`OllamaBrainClient`, HTTP stubbed) — strict-JSON parse, code-fence
  stripping, malformed output → no feedback, silence bias (empty suggestion / `none`
  type), and citation `[n]` → source mapping with out-of-range/bogus citations dropped.
- **Retrieval HTTP contract** (`KnowledgeRetrievalClient` via `httpx.MockTransport`) —
  asserts the `POST /api/search` URL, `X-Internal-Secret` header, JSON body
  (`classroomId`/`query`/`topK`), result → `RetrievedChunk` mapping (page/slide from
  metadata), and 401/500/transport errors → `RetrievalError`.

Paths **not** unit-tested against real infrastructure (isolated behind lazy imports /
injectable transports):

- The live LiveKit path — exercised only via the deferred `capture_check.py --livekit`
  mode.
- The real Whisper model — `tests/test_faster_whisper_real.py` is **opt-in** and
  skips cleanly unless faster-whisper is installed **and** an English WAV is provided
  (`STT_TEST_WAV=/path/to.wav` or a file in `tests/fixtures/`). No audio is committed.
- The real Ollama embedder (`OllamaEmbeddingProvider`) — used only by
  `boundary_check.py --live`; the boundary tests inject `FakeEmbeddingProvider`.
