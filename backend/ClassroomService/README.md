# ClassroomService

The largest service in the platform, and the one that owns everything a lesson leaves behind:
classrooms and enrolment, sessions, course files, **quizzes and marks**, Q&A, summaries, and
recording metadata.

.NET 10 · Clean Architecture · Postgres · **296 unit tests**

---

## Contents

- [Responsibilities](#responsibilities)
- [Architecture](#architecture)
- [Domain model](#domain-model)
- [The quiz subsystem](#the-quiz-subsystem)
- [Marks and tracking](#marks-and-tracking)
- [Background workers](#background-workers)
- [API surface](#api-surface)
- [Configuration](#configuration)
- [Running](#running)
- [Tests](#tests)

---

## Responsibilities

| Area | What it owns |
| --- | --- |
| **Classrooms** | Creation, teacher ownership, enrolment, discovery, membership roles |
| **Sessions** | Scheduling, start/end lifecycle, stalled-session recovery |
| **Files** | Course material upload to S3, forwarded to KnowledgeService for indexing |
| **Quizzes** | Composition, AI generation, publish/close/cancel, per-student extensions, **server-side grading** |
| **Marks** | Per-session summaries and classroom-wide cumulative tracking |
| **Q&A** | Student questions raised during a session |
| **Summaries** | Metadata and presigned download for AI-generated session summaries |
| **Recordings** | Metadata and presigned download; the bytes live in S3 and never pass through here |

What it deliberately does **not** own: identity (UserManagementService), the live room
(StreamingService), embeddings (KnowledgeService), or transcription (LiveAssistantService).

---

## Architecture

```text
ClassroomService.Api            composition root, health checks, middleware
ClassroomService.Presentation   controllers, route definitions
ClassroomService.Application    services, DTOs, abstractions  ← the interesting layer
ClassroomService.Infrastructure EF Core, repositories, S3, HTTP clients, messaging
ClassroomService.Domain         entities and enums, no dependencies at all
```

Dependencies point inward. `Application` declares the interfaces (`IQuizRepository`,
`ILiveAssistantClient`, `IFileStorage`, `INotificationBus`) that `Infrastructure` implements, so
every service class can be unit-tested against a hand-written fake with no database, no S3 and no
network. That is why 296 tests run in about three seconds.

---

## Domain model

```text
Classroom ──┬── ClassroomMembership   (student enrolment, role)
            ├── ClassroomFile         (course material in S3)
            └── Session ──┬── SessionRecording   (S3 key, status)
                          ├── SessionSummary     (AI-generated)
                          └── Quiz ──┬── QuizQuestion ── QuizAnswerOption
                                     ├── QuizAnswer      (one per student per question)
                                     ├── QuizSubmission  (student finished early)
                                     └── QuizExtension   (extra time, class-wide or per student)
```

Guid keys are application-assigned, so every key mapping uses `ValueGeneratedNever()` — without it
EF Core assumes a database-generated value and silently discards the one already set.

---

## The quiz subsystem

The most security-sensitive part of the codebase, and worth reading first.

### The answer key never reaches a student

Two DTO families, not one with conditional fields:

```csharp
QuizOptionTeacherDto(Guid Id, int Order, string Text, bool IsCorrect)
QuizOptionStudentDto(Guid Id, int Order, string Text)   // no IsCorrect — structurally
```

Served by separate endpoints (`GET quizzes/{id}` is teacher-only; `GET quizzes/{id}/student-view`
is what a student calls). A student response **cannot** leak the key, because the type has nowhere
to put it. This is stronger than filtering, which one forgotten branch defeats.

### Grading is server-side and immutable

`SubmitAnswerAsync` reads `IsCorrect` from the database row and **snapshots** it, along with
`PointsAwarded`, onto the `QuizAnswer`. Editing a quiz afterwards therefore cannot retroactively
change a grade already awarded — the answer records what was true when it was given.

### Reviews are empty-or-complete

While a quiz is Open, a student's review contains **no questions at all** rather than questions with
blanked fields. That keeps correctness a non-nullable `bool` throughout the DTO: a `null` in
`MyQuestionReviewDto.IsCorrect` unambiguously means *unanswered*, never *withheld*.

### Races are settled by the database

Unique indexes on `(QuestionId, StudentId)` and `(QuizId, StudentId)` rather than check-then-insert.
Two tabs submitting simultaneously produce a constraint violation, not two rows.

### AI generation

| Endpoint | What it does |
| --- | --- |
| `POST .../quizzes/generate` | Whole quiz. `wholeSession=false` uses only **unexamined** ideas (a quick test); `true` reads the session transcript (a full quiz) |
| `POST .../quizzes/generate-question` | One question appended to the composer. `avoid` carries existing questions so a second press does not repeat |
| `POST .../quizzes/generate-answers` | Answer options for a question the teacher wrote. The question text is never altered |

Generation returns **draft content only** — nothing is persisted until the teacher saves. Generating
a question and deleting it leaves no abandoned draft behind.

The model may **correct** the transcript from retrieved course material where the two disagree, but
corrections are discarded when retrieval returned nothing. See
[LiveAssistantService](../LiveAssistantService/) for the grounding rules.

### Lifecycle

```text
Draft ──publish──▶ Open ──close──▶ Closed        marks released to students on close
  │                  │
  │                  └──cancel──▶ Cancelled      answers kept, they simply stop counting
  └──────────────────edit only while Draft
```

Publishing sets a deadline from `DefaultSecondsPerQuestion`. `QuizDeadlineSweeper` closes expired
quizzes automatically so grades are released even if the teacher never presses close.

---

## Marks and tracking

Two levels, with one deliberate design decision worth defending:

**Per session** — `GET sessions/{id}/quiz-summary` (teacher, everyone's marks) and
`my-quiz-summary` (student, their own).

**Per classroom** — `GET quiz-tracking` (teacher) and `my-quiz-tracking` (student).

> **A percentage is measured against what the class was offered, not against what each student
> sat.** Otherwise missing the hardest week *improves* a student's score, which is the opposite of
> what a tracking view is for. Each row carries "1 of 3 quizzes" beside the percentage; the gap
> between the two is the signal.

The class average counts **only students who have taken part**. Scoring an absent student as zero
reports a failing class when what you have is an absent one — different problems, different fixes.

A student who submitted without answering still counts as having taken the quiz. Draft and cancelled
quizzes are excluded from every total.

The student's own view names nobody else; the class average is the only thing said about anyone
else, and it is a single number. A test asserts no other student's name reaches that payload.

---

## Background workers

| Worker | Interval | Purpose |
| --- | --- | --- |
| `QuizDeadlineSweeper` | 5s | Closes Open quizzes past their deadline (plus any extension), releasing marks |
| `StalledSessionSweeper` | 60 min | Ends sessions still Live after `StalledAfterHours`, e.g. after a crash |
| `RecordingReconciler` | 15 min | Recovers recordings stuck in Processing |

All follow the same shape: `BackgroundService` + `PeriodicTimer` + `IServiceScopeFactory`, because a
scoped `DbContext` cannot be captured by a singleton.

The deadline sweeper resolves each quiz's effective deadline as
`max(quiz.ClosesAtUtc, extension.ClosesAtUtc)` — extensions are stored as **absolute deadlines**
rather than "+N seconds", so two overlapping grants combine predictably instead of stacking.

---

## API surface

All routes are under `/api/classrooms/{classroomId}`. Internal routes sit under `/api/internal` and
are excluded from the gateway, authenticated by a shared secret header.

### Quizzes — teacher

```text
GET    quiz-limits
POST   sessions/{sessionId}/quizzes                    create draft
POST   sessions/{sessionId}/quizzes/generate           AI: whole quiz
POST   sessions/{sessionId}/quizzes/generate-question  AI: one question
POST   sessions/{sessionId}/quizzes/generate-answers   AI: options for a written question
PUT    quizzes/{quizId}                                edit draft
POST   quizzes/{quizId}/publish | close | cancel
POST   quizzes/{quizId}/extend                         whole class, or named students
GET    quizzes/{quizId}                                teacher view — includes the answer key
GET    quizzes/{quizId}/results
```

### Quizzes — student

```text
GET    sessions/{sessionId}/quizzes/open               the open quiz, or 204
GET    quizzes/{quizId}/student-view                   no answer key, by type
POST   quizzes/{quizId}/answers
POST   quizzes/{quizId}/submit                         finish early; idempotent
GET    quizzes/{quizId}/my-result
```

### Marks

```text
GET    sessions/{sessionId}/quiz-summary      teacher
GET    sessions/{sessionId}/my-quiz-summary   student
GET    quiz-tracking                          teacher, classroom-wide
GET    my-quiz-tracking                       student, classroom-wide
```

### Everything else

```text
Classrooms   GET / POST / GET {id} / PUT {id} / DELETE {id} / GET teacher / GET enrolled
Sessions     GET / POST / POST {id}/start / POST {id}/end
Membership   enrolment and roles
Files        upload, list, delete, presigned download
Qa           student questions
Summaries    metadata + presigned download
Recordings   metadata + presigned download
```

---

## Configuration

Bound from `appsettings.json`, overridden by `Classroom__*` environment variables in compose.

### `Quiz`

| Setting | Default | Notes |
| --- | --- | --- |
| `MaxQuestionsPerQuiz` | 20 | Also bounds what generation may return |
| `MinAnswersPerQuestion` | 2 | |
| `MaxAnswersPerQuestion` | 6 | |
| `DefaultSecondsPerQuestion` | 60 | Deadline = questions × this |
| `MaxQuizDurationSeconds` | 7200 | Ceiling including extensions |
| `LateAnswerGraceSeconds` | 3 | Covers clock skew and network latency, so an answer sent just before the buzzer still counts |
| `DeadlineSweepSeconds` | 5 | How often the sweeper runs |

### `Sessions`

| Setting | Default | Notes |
| --- | --- | --- |
| `StalledSweepEnabled` | true | |
| `StalledSweepIntervalMinutes` | 60 | |
| `StalledAfterHours` | 4 | A session Live longer than this is presumed crashed |
| `StalledSweepBatchSize` | 50 | Bounds one pass so a backlog cannot stall startup |

### `Recordings` / `Summaries`

| Setting | Default | Notes |
| --- | --- | --- |
| `DownloadUrlTtlSeconds` | 600 | Presigned URL lifetime |
| `StuckProcessingMinutes` | 30 | Before the reconciler intervenes |
| `ReconcileEnabled` | true | |
| `ReconcileIntervalMinutes` | 15 | |

### Outbound clients

| Setting | Notes |
| --- | --- |
| `LiveAssistant:BaseUrl` / `InternalApiSecret` | Quiz generation and summaries |
| `LiveAssistant:TimeoutSeconds` | 10 |
| `LiveAssistant:GenerationTimeoutSeconds` | **120** — generation runs a language model against a transcript and does not fit in the normal budget |
| `KnowledgeService:BaseUrl` / `InternalApiSecret` | Course-file indexing |
| `S3:*` | Bucket, region, credentials, endpoint |

> Presigned download URLs are generated against a **browser-reachable** host, which is not
> necessarily the endpoint this service uploads through. See `S3Settings` for the split.

---

## Running

Part of the platform stack; see the [root README](../../README.md).

```bash
cd backend && docker compose up -d classroom-service
```

Migrations are applied at startup.

---

## Tests

```bash
cd backend
dotnet test ClassroomService/tests/ClassroomService.UnitTests/ClassroomService.UnitTests.csproj
```

**296 tests, no database, no network, no Docker.** Every dependency is behind an interface with a
hand-written fake (`FakeQuizRepository`, `FakeSessionRepository`, `FakeLiveAssistantClient`,
`FakeFileStorage`, `FakeNotifier`), which keeps the suite honest and fast.

Building at the solution root can fail on a stale `.slnx`; build the test project or
`src/ClassroomService.Api` directly.

The fakes are treated as production code — two bugs found in them (`GetByClassroomIdAsync` throwing
`NotImplementedException`, `GetMembersWithDetailsAsync` returning an empty list regardless of what
was seeded) were each capable of making a broken feature look correct.
