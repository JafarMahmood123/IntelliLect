# IntelliLect

A live online-classroom platform with an AI teaching assistant that listens to the lesson, detects
when the teacher has finished explaining an idea, and acts on it — privately coaching the teacher,
and generating quizzes grounded in the course material.

Seven services: five .NET (Clean Architecture), two Python/FastAPI, one React frontend.

**1,283 automated tests.** UserManagement 120 · Classroom 296 · Streaming 104 · Knowledge 242 ·
LiveAssistant 302 · frontend 219.

---

## Contents

- [What it does](#what-it-does)
- [Repository layout](#repository-layout)
- [Architecture](#architecture)
- [Services](#services)
- [Anatomy of a live session](#anatomy-of-a-live-session)
- [Cross-cutting design decisions](#cross-cutting-design-decisions)
- [Optimisations](#optimisations)
- [Running it](#running-it)
- [Tests](#tests)
- [Further reading](#further-reading)

---

## Repository layout

```text
IntelliLect/
├── backend/
│   ├── UserManagementService/    .NET — identity, JWT + refresh, 2FA, roles, approval
│   ├── ClassroomService/         .NET — classrooms, sessions, files, quizzes, summaries
│   ├── StreamingService/         .NET — LiveKit rooms and tokens, SignalR hub, recording
│   ├── EmailService/             .NET — event consumer only, no HTTP surface
│   ├── RagService/         Python — ingestion, chunking, embeddings, RAG retrieval
│   ├── LiveAssistantService/     Python — STT, idea detection, feedback, quiz generation
│   ├── IntelliLect.Contracts/    shared message contracts for the event bus
│   ├── LocalPackages/            locally built NuGet packages
│   ├── tests/                    cross-service test helpers
│   └── docs/                     troubleshooting and runbooks
├── front-end-web/                React 19 + Vite SPA
│   └── src/                      features: live room, whiteboard, quizzes, dashboards
├── docs/
│   ├── report/                   final report (LaTeX) + diagram sources + figures
│   └── code-jury/                this submission's report
├── scripts/                      helper scripts
└── README.md
```

Each .NET service follows the same internal shape — `Domain`, `Application`,
`Infrastructure`, `Api` under `src/`, with its tests under `tests/`. Each Python
service mirrors that with `app/domain`, `app/application`, `app/infrastructure`,
`app/api` and its own `alembic/` migrations.

---

## What it does

A teacher schedules a session inside a classroom, uploads course material, and goes live. During the
lesson:

- **Students** see the teacher's camera and screen share, chat, ask questions, answer quizzes and
  watch the teacher annotate the slides in real time.
- **An AI assistant** joins the room as a hidden participant, transcribes continuously, detects
  *idea boundaries* (the teacher finishing a point rather than merely pausing), and privately
  suggests pacing or clarity corrections — visible to the teacher alone.
- **Quizzes** are generated from what was actually said, corrected against the uploaded material
  where the two disagree, and never repeat an idea already examined.
- **The session is recorded** to S3, including the teacher's whiteboard annotations, and summarised
  afterwards.

Everything is graded server-side, and marks accumulate into per-student and per-classroom tracking.

---

## Architecture

```text
                                  ┌──────────────┐
   browser ─────── :80 ──────────▶│ nginx gateway│
   (React SPA)                    └──────┬───────┘
        │                                │
        │  WebRTC media                  │  /api/*      /api/classrooms   /api/streams
        │  (never through the gateway)   ▼              ▼                 ▼  /hubs/stream
        │                        UserManagement     Classroom          Streaming
        │                                │              │                  │  (:8085)
        ▼                                │              │                  │
   ┌─────────────┐                       │              │                  │ LiveKit API
   │   LiveKit   │◀──────────────────────┼──────────────┼──────────────────┘
   │   server    │  hidden participant   │              │
   └──────┬──────┘         ▲             │              │  internal HTTP + shared secret
          │                │             │              ├──────────────────▶ Knowledge   :8083
          │  room          │             │              │                    (RAG, embeddings)
          │  composite     │             │              │
          ▼                │             │              └──────────────────▶ LiveAssistant :8084
   ┌─────────────┐         └─────────────┘                                   (STT, brain)
   │   egress    │  captures the /recorder page                                    │
   │  (headless  │  — which is how whiteboard ink                                  │
   │   Chrome)   │  reaches the MP4 at all                                         │
   └──────┬──────┘                                                                 │
          │  MP4                                                                   ▼
          ▼                                                                   Postgres ×5
       MinIO / S3  ◀────────── recordings, summaries, course files ──────  (one per service)

              RabbitMQ / MassTransit ── domain events ──▶ EmailService
```

**Two transports, deliberately separated.** Request/response between services is **internal HTTP**
with a shared secret on `/api/internal` routes, kept off the gateway. Fire-and-forget domain events
go over **RabbitMQ/MassTransit**. There is no request/response over the bus — a service blocking on
a queue for an answer is a distributed deadlock waiting to happen.

---

## Services

| Service | Stack | Port | Responsibility |
| --- | --- | --- | --- |
| [UserManagementService](backend/UserManagementService/) | .NET 10 | 8080 | Identity, JWT + refresh, 2FA, roles, approval workflow |
| [ClassroomService](backend/ClassroomService/) | .NET 10 | 8080 | Classrooms, sessions, files, **quizzes and marks**, Q&A, summaries, recording metadata |
| [StreamingService](backend/StreamingService/) | .NET 10 | 8080 → 8085 | LiveKit rooms and tokens, SignalR hub, **recording egress**, media policy |
| [RagService](backend/RagService/) | Python / FastAPI | 8083 | Course-material ingestion, chunking, embeddings, retrieval |
| [LiveAssistantService](backend/LiveAssistantService/) | Python / FastAPI | 8084 | STT, idea-boundary detection, brain, feedback, quiz generation, transcripts |
| [EmailService](backend/EmailService/) | .NET 10 | — | Event consumer only; no HTTP surface |
| [front-end-web](front-end-web/) | React 19 + Vite | 5173 | SPA — live room, whiteboard, quizzes, dashboards |

Shared message contracts live in [IntelliLect.Contracts](backend/IntelliLect.Contracts/).

Infrastructure: **Postgres ×5** (one per service, no shared database), **RabbitMQ**, **MinIO**
(S3-compatible), **LiveKit server + egress + Redis**.

---

## Anatomy of a live session

The single most useful thing to understand about this codebase.

```text
1  Teacher starts the session
     ClassroomService  ──internal HTTP──▶  StreamingService      creates the LiveKit room
     StreamingService  ──internal HTTP──▶  LiveAssistantService  "session started"

2  The assistant joins as a HIDDEN LiveKit participant
     audio ──▶ STT ──▶ segments ──▶ boundary detector ──▶ "an idea just completed"
                                          │
                                          └─▶ brain ──▶ private teacher feedback

3  Teacher shares a screen and opens the whiteboard
     strokes travel on LiveKit's DATA CHANNEL, never through our backend
     coordinates are normalised 0..1 against the video's content rectangle

4  Teacher generates a quiz
     ClassroomService ──▶ LiveAssistant    "quiz from the last N unexamined ideas"
     LiveAssistant    ──▶ RagService  retrieve material for those ideas
     the brain answers with constrained JSON; ideas used are marked so they never repeat

5  Students answer
     grading is SERVER-side; the correct answer never reaches a student's browser
     QuizAnswer snapshots IsCorrect and PointsAwarded at answer time

6  Recording
     LiveKit egress renders the /recorder page in headless Chrome and captures it,
     which is how whiteboard annotations reach the MP4 at all

7  Session ends
     egress finalises to S3 → SessionRecordingReady event
     summary requested → LiveAssistant → stored → SessionSummaryReady event
```

---

## Cross-cutting design decisions

**Clean Architecture in every .NET service.** `Domain` → `Application` → `Infrastructure` /
`Presentation` → `Api`. Dependencies point inward; `Application` declares the interfaces that
`Infrastructure` implements. Every service is unit-tested through hand-written fakes rather than a
mocking framework, which is why the suites run in seconds with no database.

**A database per service.** No shared schema, no cross-service joins. Data another service owns is
either fetched over internal HTTP or carried on an event.

**Two-DTO security for anything with a right answer.** `QuizOptionTeacherDto` carries `IsCorrect`;
`QuizOptionStudentDto` is *structurally incapable* of expressing it, and they are served by separate
endpoints. The student view cannot leak the answer key because the type has nowhere to put it.

**Correctness is decided by the server, always.** `SubmitAnswerAsync` reads `IsCorrect` from the
database row and snapshots the result onto the answer, so a later edit to a quiz cannot
retroactively change a grade already awarded.

**Uniqueness is arbitrated by the database.** Unique indexes on `(QuestionId, StudentId)` and
`(QuizId, StudentId)` rather than check-then-insert, which races under concurrency.

**Events for facts, HTTP for questions.** `INotificationBus` publishes directly; `IEventBus` uses a
transactional outbox where the database write and the event must commit together.

---

## Optimisations

Everything here was measured, not assumed.

### Live media — [`MediaOptions.cs`](backend/StreamingService/src/StreamingService.Infrastructure/Configuration/MediaOptions.cs)

| Setting | Value | Reasoning |
| --- | --- | --- |
| `ScreenShareFramerate` | **5** | Framerate is what costs the publisher CPU; resolution is what keeps text readable. Slides do not move. |
| `ScreenShareWidth/Height` | 1920×1080 | Kept at full resolution for exactly that reason. |
| `ScreenShareMaxBitrate` | 1.2 Mbps | The `h1080fps15` preset used 2.5 Mbps; at 5fps far less is needed. |
| `adaptiveStream` | on | Each subscriber receives the simulcast layer matching its rendered size. |
| `dynacast` | on | Publishers stop encoding layers nobody subscribes to. |
| `maxRetries` | 5 | Was 1 — a single failed reconnect ejected a participant from a running lecture. |

### Recording — [`EgressOptions.cs`](backend/StreamingService/src/StreamingService.Infrastructure/Configuration/EgressOptions.cs)

Room-composite egress runs headless Chrome plus a GStreamer H.264 encode. On a constrained host it
starves, and the worst failure mode is a **0-byte file**. `videoBuffersDropped` is the ground truth:

| Configuration | Dropped | Frozen | Outcome |
| --- | --- | --- | --- |
| 1280×720 @15 | 3708 / 284s | 67% | unwatchable slideshow |
| 960×540 @10 | **0** | 28% (static content) | smooth |

- **Bitrate 4500 → 1200 kbps.** LiveKit's default is tuned for 1080p30 and cost ~1 GB/hour of static
  slides. Lower bitrate also means less encoder work — the same pressure that freezes the muxer at
  finalisation.
- **`adaptiveStream` on the recorder page.** livekit-client defaults it to `false`, so the recorder
  was subscribing to the full 1080p layer and downscaling every frame inside the very Chrome that
  was dropping them.
- **Recorder layout.** A full-width camera strip cost 22% of the frame to black bars at 960×540;
  corner tiles took the material from **54% → 97%** of the frame.

### Whiteboard — [`front-end-web/src/features/whiteboard/`](front-end-web/src/features/whiteboard/)

- **Normalised coordinates.** Every point is 0..1 against the video's *content rectangle* — what is
  left after `object-contain` letterboxing. Screen pixels would put a circle around a word in a
  different place in every browser.
- **LiveKit data channel, not SignalR.** No backend round trip for a purely visual feature; the
  whole feature needs no server change at all.
- **Deltas, not scenes.** Freehand streams `begin`/`point` batched every 50 ms; shapes are sent once
  on release. Board handover to a late joiner is chunked under the 15 KiB reliable-packet cap.
- **Lossy laser pointer.** A dot that arrives after the hand has moved on is worse than one that
  never came.
- **`canvas.width` is assigned only when it changes.** Assigning it reallocates and zeroes the whole
  bitmap even for an identical value — a ~15 MB buffer, dozens of times a second while drawing.

### AI pipeline

- **Prefetch decouples STT from the boundary detector.** Without it the two alternate — nothing is
  transcribed while an embedding is in flight — and the serial cost per window is STT + embed.
- **Constrained decoding.** Gemini `responseSchema` / Ollama `format` force valid JSON, with
  `correct_index` as an integer rather than per-option booleans that can contradict each other.
- **Used-idea tracking.** Ideas consumed by a quiz are keyed by `(start_ms, end_ms)` and excluded
  from the next one, so a quick test never re-examines the same explanation.
- **Grounded corrections only.** The model may overrule the transcript from retrieved material, but
  corrections are discarded entirely when retrieval returned nothing — no ungrounded assertions
  reach a student.

### Frontend

- **Empty-or-complete withholding.** A student's quiz review is *absent* while the quiz is open
  rather than blanked, which keeps correctness fields non-nullable `bool`.
- **TanStack Query key factories** with SignalR-driven invalidation. `QuizChanged` carries ids and
  state only, never the payload, so clients refetch the view they are entitled to instead of
  trusting the wire.

---

## Running it

**Prerequisites:** Docker (Desktop or Engine), Node 20+, .NET 10 SDK, and a host
[Ollama](https://ollama.com) for embeddings.

```bash
# 1. Build sequentially — parallel builds saturate the connection and produce
#    NuGet "Connection reset by peer" failures
cd backend
for s in user-service classroom-service rag-service live-assistant-service streaming-service email-service; do
  echo ">>> Building $s"; docker compose build "$s" || break
done

# 2. Start the stack
docker compose up -d

# 3. Frontend
cd ../front-end-web && npm install && npm run dev
```

App: **http://localhost:5173** · API gateway: **http://localhost:80**

Compose must be run from `backend/` — it `include:`s each service's `docker-compose.unit.yml`, and
running one of those alone fails with *"refers to undefined network intellilect-net"*.

**Host-specific configuration** lives in `backend/.env`. The important one is `LIVEKIT_HOST_IP`
(default `127.0.0.1`), which drives `--node-ip`, `LIVEKIT_NODE_IP` and both `LiveKit__Host` values
from a single place so they cannot drift apart — see
[troubleshooting §2](backend/docs/troubleshooting.md#2-connection-lost--participants-dropped-from-a-live-session).

---

## Tests

```bash
# .NET
cd backend
dotnet test ClassroomService/tests/ClassroomService.UnitTests/ClassroomService.UnitTests.csproj
dotnet test StreamingService/tests/StreamingService.UnitTests/StreamingService.UnitTests.csproj
dotnet test UserManagementService/tests/UserManagementService.UnitTests/UserManagementService.UnitTests.csproj

# Python
cd backend/RagService     && ./.venv/bin/python -m pytest
cd backend/LiveAssistantService && ./.venv/bin/python -m pytest

# Frontend
cd front-end-web && npm test
```

None of these need Docker, a database, a model or a network. Every external dependency sits behind
an interface with a hand-written fake, which is why the whole suite runs in well under a minute.

---

## Further reading

| Document | |
| --- | --- |
| [backend/docs/troubleshooting.md](backend/docs/troubleshooting.md) | Failures that cost real debugging time, with the evidence that identifies each |
| [backend/docs/complex-logic/live-session-media.md](backend/docs/complex-logic/live-session-media.md) | WebRTC, ICE and the Docker Desktop networking constraints |
| [backend/docs/recordings-live-runbook.md](backend/docs/recordings-live-runbook.md) | Recording verification runbook |
| [backend/docs/summaries-live-runbook.md](backend/docs/summaries-live-runbook.md) | Summary generation runbook |
| [docs/report/README.md](docs/report/README.md) | Building the graduation report PDF |
| [docs/report/STRUCTURE.md](docs/report/STRUCTURE.md) | The HIAST report template spec the report follows |

Each service directory carries its own README with endpoints, configuration and design notes.
