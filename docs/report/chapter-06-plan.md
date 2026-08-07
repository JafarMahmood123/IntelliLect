# Chapter 4 (Implementation & Testing) — Restructuring Plan

Working document. Nothing in `chapters/06-implementation-and-testing.tex` has been
changed yet. Review this, mark the decisions at the end, and I'll apply it in one pass.

Source file: `chapters/06-implementation-and-testing.tex` — 602 lines, 9 sections.
Chapter number in the built PDF is **4** (chapters 2 and 3 are excluded from the build).

---

## 1. Current state

| § | Title | Lines | Proposed fate |
|---|---|---|---|
| 4.1 | مقدمة | 6 | **Delete** — requested |
| 4.2 | معمارية النظام المتبعة | 43 | **Delete** — repeats §3.1 |
| 4.3 | تجسيد أنماط التصميم في الشيفرة | 105 | **Delete** — repeats §3.2 |
| 4.4 | بيئة التطوير والتقنيات المستخدمة | 106 | **Keep** — becomes the core of the new section |
| 4.5 | تفصيل أجزاء النظام وطريقة تنفيذ التكامل | 57 | **Keep in part** — LiveKit / whiteboard / recording |
| 4.6 | المشكلات التي واجهت التنفيذ وحلولها | 71 | **Decision needed** — holds the only record of the models we tried |
| 4.7 | بسودوكود | 133 | **Decision needed** |
| 4.8 | خطة الاختبارات ونتائجها | 64 | **Keep unchanged** |
| 4.9 | الخاتمة | 11 | **Rewrite** to match the new shape |

Net effect if all deletions go through: roughly 602 → 300 lines, before the new
material is written.

---

## 2. Proposed new outline

### 4.1 التنفيذ

#### 4.1.1 التقنيات المستخدمة

Reuse the existing `tab:tech` table as-is. It already carries a justification column
("why this and not the alternative"), which is the part that is hard to write and easy
to lose.

Rows: `.NET 10`, `Python / FastAPI`, `React 19 + Vite`, `PostgreSQL + pgvector`,
`LiveKit`, `RabbitMQ + MassTransit`, `MinIO / S3`, `Docker Compose`.

#### 4.1.2 قواعد البيانات — NEW

**No schema content here.** Tables, columns and relationships are already covered by
the five ERDs in chapter 3 and are not repeated. This subsection answers only *what is
deployed*: database-per-service is a diagram in chapter 3 and five running containers
here.

| Database | Owner | Engine |
|---|---|---|
| `users_db` | UserManagementService | PostgreSQL 17 |
| `classroomdb` | ClassroomService | PostgreSQL |
| `streaming_db` | StreamingService | PostgreSQL 16 |
| `knowledgedb` | RagService | PostgreSQL + **pgvector** — the only one with the extension |
| `liveassistantdb` | LiveAssistantService | PostgreSQL |
| — | EmailService | **No database.** Pure consumer |

Plus the non-relational stores: **MinIO/S3** for objects (uploads, recordings, summary
PDFs) and **RabbitMQ** for domain events.

Two sentences beyond the table, both about deployment rather than schema: each database
runs as its own container with its own credentials, and no service holds a connection
string to another service's database.

#### 4.1.3 المكتبات والأطر — NEW

Three tables, one per stack. Verified against `*.csproj`, `pyproject.toml` and
`package.json`.

**.NET services**

| Library | Role |
|---|---|
| Entity Framework Core + `Npgsql` | ORM, migrations, unit of work |
| MassTransit (`.RabbitMQ`, `.EntityFrameworkCore`) | Event bus and the transactional outbox |
| SignalR | Push to the browser inside a live session |
| `Livekit.Server.Sdk.Dotnet` | Room creation, access tokens, egress control |
| `AWSSDK.S3` | Object storage against MinIO |
| JWT Bearer + `System.IdentityModel.Tokens.Jwt` | Authentication |
| Serilog | Structured logging |
| AutoMapper | DTO mapping |
| MailKit | SMTP in EmailService |
| xUnit | Tests |

**Python services**

| Library | Role |
|---|---|
| FastAPI + Uvicorn | HTTP layer |
| SQLAlchemy (async) + `asyncpg` + Alembic | Persistence and migrations |
| Pydantic / `pydantic-settings` | Validation and typed configuration |
| `pgvector` | Vector column and HNSW index |
| PyMuPDF, `python-docx`, `python-pptx`, Pillow | Document extraction |
| `pytesseract` | Selective OCR |
| `boto3` | Reading uploaded files from S3 |
| `markdown` + WeasyPrint | Rendering the session summary to PDF |
| `aio-pika` | Publishing events to RabbitMQ |
| `prometheus-client` | Metrics |
| pytest / `pytest-asyncio` | Tests |

Worth one sentence: neither Python service ships model weights — no `torch`, no
`transformers`. All inference is an HTTP call behind a port.

**Front-end**

| Library | Role |
|---|---|
| `livekit-client` + `@livekit/components-react` | WebRTC session and the data channel |
| `@microsoft/signalr` | Server push |
| TanStack Query | Server state and caching |
| Zustand | Client state |
| react-hook-form + Zod | Forms and validation |
| React Router | Routing |
| Tailwind CSS 4 | Styling |
| i18next / react-i18next | Localization |
| react-markdown + rehype-sanitize | Rendering model output safely |
| Vitest + Testing Library | Tests |

#### 4.1.4 نماذج الذكاء الصنعي: المجرَّب والمعتمد

Merges two existing tables and one narrative that currently sit in different sections.

**What we tried and rejected** — from `tab:stt-engines`. Speed is relative to real
time; below 1.0 means the engine consumes audio slower than the lecture produces it
and falls permanently behind.

| Engine | Where | Speed | Outcome |
|---|---|---|---|
| `faster-whisper small.en` | local | 0.2× | Most accurate local, far too slow |
| `faster-whisper tiny.en` | local | 1.8× | Keeps up, worst accuracy |
| `faster-whisper base.en` (int8, 2 threads) | local | > 1× | The practical local compromise; kept as the offline fallback |
| `whisper-large-v3-turbo` via Groq | hosted | ~4.5× | Fastest *and* most accurate — adopted |
| `gemini-flash-latest` | hosted | 0.5–0.67× | Last resort when Groq is blocked; hallucinates speech over silence |

Also tried and dropped: `qwen3-embedding` and `qwen2.5:7b-instruct` under Ollama, both
local. The blocker was not any single model but concurrency — STT, embedding and
generation all run *simultaneously* through a live session on an 8-core host, and the
same short reply took 5 s once and 12 s the next time. Splitting cores explicitly
(2 for STT, 6 for generation) fixed the variance but not the underlying latency.

**What runs in production** — from `tab:models`.

| Function | Model | Governing constraint |
|---|---|---|
| Speech to text | `whisper-large-v3-turbo` (Groq) | Latency — the text must land before the next idea ends |
| Embedding (live assistant) | `gemini-embedding-001` | Latency — on the critical path of boundary detection |
| Analysis and intervention | `gemini-flash-lite` | Latency plus a structured-output requirement |
| Embedding (knowledge service) | `gemini-embedding-001`, 3072 dims | Retrieval quality; runs in the background, so latency is free |
| Answering and summarization | `gemini-flash` | Output quality and context length |

Two points to carry over, because they are the engineering content rather than the list:

- Migrating the embedding model is **not** free the way migrating the generator is.
  Vectors from two models live in different spaces, so mixing them produces confident
  wrong results rather than an error — a partial migration is worse than none. And the
  vector width *is* the database column width, so going from 1024 to 3072 dimensions
  required a schema migration and a re-embed of every stored chunk.
- The local path is still fully wired, switchable by one variable per stage, but it is
  deliberately **not** an automatic fallback. Silent degradation to an engine 20× slower
  is harder to diagnose than a loud failure.

#### 4.1.5 البثّ المباشر: كيف يعمل LiveKit

- LiveKit is an **SFU**: it forwards tracks selectively without re-encoding them.
- StreamingService creates the room and issues each participant a scoped access token,
  then leaves. Media travels browser ↔ LiveKit directly over WebRTC — it passes through
  neither the reverse proxy nor the service, so the service cannot become a bottleneck
  as participant count grows.
- The participant's **role travels in the token metadata**, not the display name.
  The display name is client-editable, so any authorization decision built on it is
  spoofable.
- The token also declares what the holder may publish (audio, video, screen).
- **TURN.** ICE requires both directions to validate, and only one worked: the browser
  could always reach LiveKit, but LiveKit could reach none of the addresses the browser
  advertised. 30 candidate pairs, 0 successful, sessions dropping at ~13 s. Rather than
  satisfy the requirement, we removed it — a relay lets the browser *allocate* over an
  outbound connection, so LiveKit never originates a packet toward the client and the
  question of which address is routable disappears. ICE still prefers a direct pair when
  one works, so this costs nothing when the direct path is available.

#### 4.1.6 السبورة البيضاء

Rewritten against the actual implementation — see correction #1 in §3 below.

- Two modes: `annotate` (draw over the shared screen) and `board` (draw on an empty
  16:9 surface).
- The canvas is positioned over the video's **content rectangle** — what remains after
  letterboxing — and coordinates are normalized to `[0,1]` against it, never pixels.
  In pixels, a circle drawn around one word would land around a different word on every
  viewer's screen, because each window is a different size.
- **Transport is LiveKit's data channel, not the SignalR hub.** It is already open,
  already authenticated by the room token, and it does not send every stroke on a round
  trip through our backend. The feature required no server change at all.
- Free drawing is sent as successive diffs batched every 50 ms; geometric shapes are
  sent once, on pointer-up.
- The data channel does not buffer, so a student joining two minutes in would see an
  empty board. They ask for it instead: a `hello` on connect, answered by the teacher
  with the board addressed to that student alone. Asking is more robust than having the
  teacher watch for arrivals — it also covers the case where it was the *teacher* who
  reconnected and therefore never saw anyone arrive.
- Freeze is implemented by pausing the `<video>` element rather than shipping a still
  frame: a MediaStream-backed video holds its last frame while paused and rejoins the
  live edge on play, so it costs nothing on the wire.
- The layer never blocks the pointer; only the canvas does, and only while a tool is in
  hand — otherwise it would swallow clicks meant for the video controls beneath it.

#### 4.1.7 التسجيل: Egress و Redis والتخزين

- **Room-composite egress does not composite tracks.** It opens headless Chrome, loads
  a URL, and captures what that page renders. That is the entire reason the `/recorder`
  page exists.
- `/recorder` joins the room as an ordinary participant, renders the same stage and the
  same whiteboard layer, and the ink lands in the MP4 because it is on the screen being
  filmed. A track-based template could not see it — annotations are drawn on a canvas
  in a browser, not carried on any track.
- The page carries no app chrome, no theme controls and no authentication: it is loaded
  by a robot with a token in the query string, and anything it renders is burned into the
  recording permanently. Controls are explicitly disabled there.
- It waits for `START_RECORDING` before capture begins, and refuses to signal it when the
  URL or token is missing — so a misconfigured egress fails loudly instead of recording an
  hour of blank video.
- **Redis.** It is LiveKit's, not ours. `livekit-redis` is how `livekit-server` dispatches
  recording jobs to the egress worker; both run on host networking and reach it over the
  host loopback. Without it, egress requests are never handed off. No service in this
  system reads or writes it.
- **Storage.** The S3 target is not in `egress.yaml` — StreamingService supplies bucket,
  key and MinIO endpoint per request when it starts the egress. Completion arrives as a
  signed **webhook** from LiveKit (`room_started`, `egress_ended`), verified against the
  shared key.
- A **3-hour ceiling** (`file_output_max_duration`) is set on a single recording: an egress
  whose stop request never arrives would otherwise composite until the room empties.
- **Measured settings** — reuse `tab:egress`. The metric that decided it was dropped frames.

  | Setting | Dropped frames | Freeze ratio | Result |
  |---|---|---|---|
  | 1280×720 @ 15 fps | 3708 | 67% | Unwatchable |
  | 960×540 @ 10 fps | 0 | 28% | Smooth |

  The 28% freeze is not a defect — static slides produce no new frames. Bitrate was cut
  from 4500 to 1200 kbps because the default is tuned for high-motion content and was
  costing ~1 GB/hour of static slides. Re-laying out the recorder page (camera in a small
  corner tile instead of a full-width strip) raised the share of the frame occupied by
  actual lecture material from 54% to 97%.
- `adaptiveStream` is enabled on the recorder. Without it the page subscribes to the
  highest simulcast layer — a 1080p screen share — then scales it down to the 960×540
  egress viewport on every frame, inside the same headless Chrome that was already the
  bottleneck. It does not raise the recording's resolution; it only decides which layer is
  fetched to fill it.

#### 4.1.8 تكامل قاعدة المعرفة — added per D5

Short, four points only:

- The upload does not wait for indexing. ClassroomService stores the file, calls
  RagService internally, and returns; extraction, OCR and embedding run in a background
  worker with bounded concurrency, because they can take minutes.
- A **content hash** makes processing idempotent: resending the same request does not
  re-index, while genuinely changed content does.
- **OCR is selective**, applied only to scanned pages and images carrying text. Running
  it on every page doubles processing time and adds no text on pages that were already
  text.
- **Retrieval is always scoped to one classroom**, because `classroom_id` is a column on
  the chunk and enters the query as a condition rather than a post-filter. That is what
  makes cross-classroom leakage structurally impossible rather than merely unlikely.

Keeps `fig:arch-02` referenced from chapter 4.

#### 4.1.9 بسودوكود

Two algorithms, per D1b: boundary detection, and evaluation + pacing. Written in English
because the identifiers are English and mixing writing directions inside a code block
corrupts the ordering of its symbols.

### 4.2 خطة الاختبارات ونتائجها

Unchanged. Keeps: methodology (hand-written fakes over mocking frameworks; the suite runs
with no database, network, model or container), the results table (1285 tests, 1273 passing,
0 failing, 12 conditionally skipped), the six worked examples, and the known-gaps table
(integration, security, load).

### No الخاتمة

Removed at your request. The chapter ends with the testing section (or the pseudocode
section, depending on ordering).

One mechanical detail: the current §4.9 ends with a `\transition{...}` line that carries
the reader into the report's conclusion. That macro is not part of the الخاتمة section
proper — I plan to keep the transition line and drop only the section around it, so the
chapter still hands off rather than stopping abruptly. Say if you'd rather it go too.

---

## 3. Three corrections to the brief

### 3.1 The whiteboard is not overlaid onto the video stream

The brief said "how we added the overlay to the video stream so the student can see the
whiteboard interactions". That is not what the code does, and the real mechanism is the
more interesting one. There are two distinct paths:

**Live — nothing is composited.** Strokes go out as normalized coordinates over LiveKit's
data channel, and every client redraws them on its own canvas positioned over the video.
The video stream is untouched. The backend is not involved at all.

**Recording — the only place ink and video merge.** Egress films the `/recorder` page,
which renders both. That is why the page exists.

Writing it the other way round would describe a system we did not build, and would make
the `/recorder` page look redundant when it is in fact the whole mechanism.

### 3.2 Redis is LiveKit's, not ours

Listing Redis as part of the application stack would be wrong. Its only role is job
dispatch between `livekit-server` and the egress worker. One sentence, in the recording
subsection, not in the technology table.

### 3.3 The models we tried live in §4.6

`tab:stt-engines` and the Ollama narrative are inside «من النماذج المحلّية إلى الخدمات
المستضافة», a subsection of the section slated for deletion. The brief explicitly asks
for the models we tried, so that content moves into 4.1.4 rather than being deleted.

---

## 4. Decisions needed

All settled except D4.

| # | Question | Decision |
|---|---|---|
| D1 | §4.7 بسودوكود | **Keep, trimmed to core logic only** — see D1b. |
| D1b | Which algorithms survive | **Boundary detection + evaluation/pacing.** RAG answering and answer-key protection are dropped; the answer-key decision becomes two prose sentences in the testing section's coverage list, which already mentions it. |
| D2 | The four implementation problems | **Fold the relevant ones in.** ICE/TURN → 4.1.5 (LiveKit). Groq geo-block → 4.1.4 (models), since it explains why a second hosted STT engine exists. Boundary-detection redesign and «أعطالٌ تبدو نجاحًا» are dropped as a section — but see the note below. |
| D3 | The three configuration tables | **Keep under التنفيذ**, unchanged. |
| D5 | Knowledge-base ingestion | **Add a short subsection** — new 4.1.8 below. |
| D4 | §4.3 covers the **Interceptor** pattern, not among the 7 in §3.2 | *Still open.* Let it go / move to §3.2. My recommendation: let it go, unless you want 8 patterns in chapter 3. |

**Note on what D2 costs.** Dropping «أعطالٌ تبدو نجاحًا» removes the justification for
the testing section's central claim — that coverage was aimed at *silent* failures.
§4.2.3 currently opens with "اختير التركيز على المواضع التي يكون الخطأ فيها صامتًا"
and the four worked examples behind that choice live in the deleted subsection. I plan
to keep one short paragraph of that material as the lead-in to the testing section, so
the claim still has ground under it. Flag if you'd rather it go entirely.

---

### D1b — which pseudocode survives

"Minimal, core logic only." The four currently in §4.7, with what each is worth:

| # | Algorithm | Lines | Assessment |
|---|---|---|---|
| 1 | كشف حدود الفكرة التعليمية | ~40 | **Core.** The system's most distinctive logic — semantic drift against a calibrated threshold, with silence and hard caps as safety nets rather than primary rules. Nothing off-the-shelf. |
| 2 | التقييم مقابل المادة المرجعية وتوقيت التدخّل | ~31 | **Core.** Grounded evaluation plus the four ordered pacing rules. This is the intervention decision, which is the product. |
| 3 | الاسترجاع المعزَّز بالتوليد للإجابة عن سؤال | ~26 | Textbook RAG — embed, search, threshold, prompt. Adds little a reader does not already expect. |
| 4 | حماية مفتاح الإجابة والتصحيح على الخادم | ~32 | A security decision, not an algorithm. Reads better as two prose sentences (the DTO has no answer field at all; grading happens server-side). |

**Recommendation: keep 1 and 2, drop 3 and 4.** That takes the section from ~133 lines
to ~75 and leaves exactly the logic that is ours.

---

## 5. Cross-reference cleanup

No chapter outside 4 references any label defined in chapter 4, so the damage is
contained. Internally, deleting §4.2, §4.3 and §4.6 breaks these:

| Reference | Used by | Fix |
|---|---|---|
| `tab:services` | §4.2 | Deleted with its section |
| `tab:pattern-implementation` | §4.3 | Deleted with its section |
| `subsec:silent-failures` | `tab:cfg-retrieval` row `ASSISTANT_SMOKE_TEST` | The label's section is deleted; the cell gets rewritten to state the constraint directly instead of pointing at it |
| `sec:design-patterns` | §4.3 intro | Deleted with its section |
| `tab:stt-engines` | §4.6.1 | Moves to 4.1.4 |
| `fig:class-01` | §4.3 | Figure now lives in chapter 2 — reference is orphaned either way |
| `fig:arch-01` | §4.2 | Deleted with its section |
| `fig:arch-02` | §4.5.3 | **Kept** — referenced from the new 4.1.8 |
| `fig:flow-03` | §4.5.2 | Keep — it is the recording flow, referenced from 4.1.7 |

---

## 6. Open item, unrelated to this chapter

Naming: the new components diagram for the live assistant reads «خدمة المساعد المباشر»,
while the section heading, both captions and 8 other places read «المساعد الحيّ» — 9
occurrences against 1. §1.1.1 on page 98 already said «المباشر» before today. Pick one
and I will make all ten agree.
