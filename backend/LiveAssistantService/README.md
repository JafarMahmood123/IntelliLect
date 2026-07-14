# LiveAssistantService

A Python microservice for the IntelliLect platform. Its eventual purpose is to join
a live class session as a **server-side agent**, transcribe the teacher's speech,
detect when the teacher finishes an "idea", check that idea against the classroom's
uploaded material via the existing **KnowledgeService** (RAG), and **privately**
suggest corrections to the teacher only.

**This build covers phases LA-0 → LA-6.** It contains the clean-architecture
skeleton, a LiveKit agent that **captures the teacher's live audio** behind an
`AudioSource` port, **streaming English speech-to-text** behind a `SpeechToText`
port, **idea boundary detection** that segments the transcript into completed
"ideas", **retrieval + evaluation on each idea** — pulling relevant course material
from KnowledgeService and asking the brain whether the explanation has a real problem
— **private, teacher-only feedback delivery** of the resulting suggestion, and the
**full per-session loop assembled and started/stopped in sync with real sessions**
(triggered by the streaming service). **Pacing/rate-limiting** (LA-7) is **not**
implemented — the code leaves a clearly-marked seam for it.

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
- **Private feedback delivery (LA-5)** — `FeedbackDispatcher` (pure application
  connector) sends an `EvaluationOutcome`'s suggestion to the teacher and **drops
  no-feedback outcomes** (no send); a delivery failure is logged and swallowed so the
  loop survives. `LiveKitFeedbackSink` serializes the suggestion to a **versioned JSON
  contract** and publishes it as a **reliable LiveKit data message targeted to
  `session.teacher_identity` only** — over the agent's existing room connection (no
  second connection). **Teacher-only is structural:** delivery goes through an
  `AgentDataChannel` port whose only method targets a single identity — there is no
  broadcast path, so a student can never receive feedback.
- `FakeFeedbackSink` / `FakeAgentDataChannel` (test support) — record what would be
  sent and to whom, so LA-5 is tested with **no LiveKit**.
- `scripts/feedback_check.py` — a CLI that runs a suggestion through the real
  dispatcher + sink (via an in-process recording channel) and prints the **exact
  serialized payload** and the **teacher-only target**, with **no LiveKit**; a
  deferred `--live` mode publishes to a real room.
- **Session lifecycle (LA-6)** — `SessionPipeline` (pure application) assembles the
  full loop for one session — `AudioSource → SpeechToText → BoundaryDetector →
  IdeaEvaluator → (has_feedback) → FeedbackSink` — as one cancellable async task; a
  per-idea error is logged and the run continues, the trailing idea is flushed on
  stream end, and stopping disconnects the source. `SessionManager` keeps **exactly
  one pipeline per session_id** (idempotent start/stop, `MAX_CONCURRENT_SESSIONS`
  cap, graceful shutdown, auto-deregister on unexpected end). The composition root
  wires each session's LiveKit agent as **both** the capture source and the feedback
  channel, so feedback returns over the same connection.
- **Internal session endpoints** (secured by `INTERNAL_API_SECRET`), triggered by the
  streaming service: `POST /api/internal/sessions/start` (202, idempotent, 503 at
  cap), `POST /api/internal/sessions/{id}/stop` (204), `GET /api/internal/sessions`
  (active ids). `/health` also reports `activeSessions`.

## Ports

Defined under `app/application/ports/`:

| Port | Purpose |
| --- | --- |
| `AudioSource` | **Implemented (LA-1).** Stream of the teacher's normalized audio. |
| `SpeechToText` | **Implemented (LA-2).** Streaming English transcription → `TranscriptSegment`s. |
| `EmbeddingProvider` | **Implemented (LA-3).** Embed a segment/idea (local Ollama) for drift & later retrieval. |
| `RetrievalClient` | **Implemented (LA-4).** Classroom-scoped RAG search via KnowledgeService. |
| `BrainClient` | **Implemented (LA-4).** Judge an idea against retrieved material; propose a correction. |
| `FeedbackSink` | **Implemented (LA-5).** Deliver a suggestion to the teacher **privately**. |
| `AgentDataChannel` | **Implemented (LA-5).** Targeted (single-identity) data send over the agent's room. |

The future **live-loop orchestrator** (the use case that wires audio → STT → boundary
detection → retrieval → brain → feedback end to end) will join these in
`app/application/services/`, alongside the existing `BoundaryDetector`,
`IdeaEvaluator`, and `FeedbackDispatcher`.

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
                     #   BrainClient, FeedbackSink, AgentDataChannel
    services/        # boundary_detector, idea_evaluator, feedback_dispatcher,
                     #   session_pipeline, session_manager, token_estimate
  infrastructure/
    config/          # pydantic-settings Settings
    audio/           # LiveKitAudioSource (AudioSource + AgentDataChannel), FakeAudioSource, normalization
    stt/             # FasterWhisperSpeechToText, audio_analysis (energy/pause)
    embeddings/      # OllamaEmbeddingProvider
    retrieval/       # KnowledgeRetrievalClient (POST /api/search)
    brain/           # OllamaBrainClient, evaluation_prompt
    feedback/        # LiveKitFeedbackSink, feedback_payload (versioned wire contract)
  api/
    main.py          # FastAPI app factory + lifespan (SessionManager start/stop)
    dependencies.py  # composition root (names concrete classes)
    routers/         # health, internal_sessions
scripts/capture_check.py, stt_check.py, boundary_check.py, evaluate_check.py, feedback_check.py
tests/                # offline; tests/support/Fake{SpeechToText,EmbeddingProvider,
                     #   RetrievalClient,BrainClient,FeedbackSink,AgentDataChannel,Pipeline}
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
| `RETRIEVAL_TOP_K` | `6` | Chunks requested per idea. |
| `RETRIEVAL_MIN_SCORE` | `0.25` | Below this = "no relevant material" (short-circuit, no brain). |
| `EVAL_MODEL` | `qwen2.5:7b-instruct` | Brain (generation) model; must be pulled in Ollama. |
| `EVAL_TEMPERATURE` | `0.2` | Brain sampling temperature. |
| `EVAL_TIMEOUT_SECONDS` | `60` | Ollama request timeout (brain). |
| `EVAL_MAX_TOKENS` | `512` | Brain `num_predict`. |
| `FEEDBACK_TRANSPORT` | `livekit` | Delivery transport (`livekit`; `signalr` is a future option). |
| `FEEDBACK_MESSAGE_VERSION` | `1` | Version stamped into the feedback wire contract. |
| `MAX_CONCURRENT_SESSIONS` | `20` | Cap on active session pipelines; start beyond it → 503. |
| `INTERNAL_API_SECRET` | _(empty)_ | Shared secret guarding `/api/internal/sessions` (and KnowledgeService calls). |
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

### Feedback check (offline — no LiveKit)

Run a suggestion through the real `FeedbackDispatcher` + `LiveKitFeedbackSink` (wired
to an in-process recording channel) — proves the no-feedback **drop**, the **exact
serialized payload**, and **teacher-only** targeting, with **no LiveKit**:

```bash
python scripts/feedback_check.py
```

```text
no-feedback outcome -> sent=False (dropped, nothing published)
suggestion outcome  -> sent=True
target identity : teacher-1   (session.teacher_identity)
topic           : teaching_suggestion
payload (the exact bytes published to the teacher):
{ "type": "teaching_suggestion", "version": 1, "feedback_type": "discrepancy", ... }
RESULT          : OK (teacher-only)
```

A deferred `--live --room <room> --teacher <identity>` mode publishes to a real room
via the agent's connection; it needs a live session with the teacher present.

## Session lifecycle (LA-6)

Sessions are started and stopped by the **streaming service**, not by a user-facing
endpoint. When a live session becomes active (its LiveKit room is created), the .NET
`StreamingService` calls this service — best-effort, so a session starts/ends normally
even if the assistant is down:

```text
StreamingService (InternalStreamsController)
  POST {LiveAssistant:BaseUrl}/api/internal/sessions/start
       { sessionId, classroomId, roomName=<sessionId>, teacherIdentity=<teacherId> }
  POST {LiveAssistant:BaseUrl}/api/internal/sessions/{sessionId}/stop
  (X-Internal-Secret: <shared INTERNAL_API_SECRET>)
```

`roomName` and `teacherIdentity` follow `LiveKitMediaProvider`'s token conventions
(room = `sessionId`, participant identity = the user id, so the teacher's identity is
`teacherId`). On start, the `SessionManager` launches one `SessionPipeline`; on stop,
it tears it down. Try it locally against a running service:

```bash
SECRET=dev-live-assistant-secret
SID=$(uuidgen)
curl -X POST localhost:8084/api/internal/sessions/start -H "X-Internal-Secret: $SECRET" \
  -H 'content-type: application/json' \
  -d "{\"sessionId\":\"$SID\",\"classroomId\":\"$(uuidgen)\",\"roomName\":\"$SID\",\"teacherIdentity\":\"teacher-1\"}"
curl localhost:8084/api/internal/sessions -H "X-Internal-Secret: $SECRET"
curl -X POST localhost:8084/api/internal/sessions/$SID/stop -H "X-Internal-Secret: $SECRET"
```

(A real pipeline needs LiveKit + models to do useful work; the endpoints, registry,
idempotency, and cap are exercised offline by the test suite.)

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

## Feedback delivery (LA-5)

Feedback is delivered as a **reliable LiveKit data message** published from the
agent's existing room connection (LA-1) to `destination_identities=[teacher]` only —
so it reaches the teacher's client and no student's. The agent's join token grants
`can_publish_data` (but not `can_publish` — the agent still never publishes media).
The teacher's frontend filters on the `teaching_suggestion` data-message topic and
parses this versioned contract:

```json
{
  "type": "teaching_suggestion",
  "version": 1,
  "session_id": "...",
  "feedback_type": "discrepancy|gap|unclear",
  "text": "<the suggestion>",
  "sources": [ { "citation": 1, "document_id": "...", "page": null,
                 "slide": 4, "section": null } ],
  "created_at": "<iso8601>"
}
```

Raw chunk text is intentionally omitted — citations + document locations are enough
for the UI to reference the source. **Alternative transport (not implemented):** a
teacher-only method on the StreamingService `StreamHub` (SignalR) could carry the same
payload; `FEEDBACK_TRANSPORT=signalr` is reserved for it. The LiveKit path is the one
built here.

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
- **Feedback delivery (LA-5)** — the **teacher-only invariant** (dispatcher targets
  `session.teacher_identity` only, never a student), the no-feedback **drop** (sink
  never called), sink error swallowed by the connector; the **payload contract**
  (`build_feedback_payload` schema, `feedback_type` lowercase, citation→location
  sources, raw chunk text omitted); `LiveKitFeedbackSink` publishing the correct bytes
  to the teacher over `FEEDBACK_TOPIC` (via `FakeAgentDataChannel`) and raising
  `FeedbackDeliveryError` on channel failure; and the agent's `publish_to_identity`
  issuing a **reliable, single-`destination_identities`** publish (fake room, no SDK).
- **Session lifecycle (LA-6)** — `SessionPipeline` end to end with every port faked
  (WAV → scripted STT → boundary → evaluate → feedback): feedback sent per idea,
  no-feedback dropped, a bad idea logged without stopping the run, trailing idea
  flushed, stop-before-start safe. `SessionManager`: start registers one pipeline,
  duplicate start is a no-op, stop cancels + deregisters, unknown stop is safe, the
  `MAX_CONCURRENT_SESSIONS` cap rejects, `stop_all` on shutdown, and a pipeline ending
  on its own auto-deregisters. Internal endpoints: start 202 + register, idempotent,
  stop 204, cap 503, auth enforced, `/health` active count. (.NET side: see below.)

Paths **not** unit-tested against real infrastructure (isolated behind lazy imports /
injectable transports/channels):

- The live LiveKit path — exercised only via the deferred `capture_check.py --livekit`
  mode.
- The real Whisper model — `tests/test_faster_whisper_real.py` is **opt-in** and
  skips cleanly unless faster-whisper is installed **and** an English WAV is provided
  (`STT_TEST_WAV=/path/to.wav` or a file in `tests/fixtures/`). No audio is committed.
- The real Ollama embedder (`OllamaEmbeddingProvider`) — used only by
  `boundary_check.py --live`; the boundary tests inject `FakeEmbeddingProvider`.

### The .NET trigger (StreamingService)

The start/stop notifications come from `StreamingService`'s `LiveAssistantInternalClient`
(a typed `HttpClient`, mirroring ClassroomService's internal clients), called from
`InternalStreamsController` when a stream is created / ended. The calls are **best-effort**
— wrapped in try/catch so a session starts/ends normally even if the assistant is
unreachable (a warning is logged). Covered by `StreamingService.UnitTests`:
`dotnet test backend/StreamingService/tests/StreamingService.UnitTests` — the client
posts the right start/stop URLs, bodies (`roomName`/`teacherIdentity`), and
`X-Internal-Secret`; and stream create/end still succeed (with a warning) when the
assistant call throws.
