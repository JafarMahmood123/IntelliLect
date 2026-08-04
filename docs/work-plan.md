# IntelliLect — Work Plan

Working checklist. Ordered so that cross-cutting renames land before new code, and
so each feature is testable the moment it is written. **Nothing here is run** — the
containers are down, so all test work in this plan is *authoring* test code and test
design, not executing it. Execution items are parked at the bottom.

Legend: `[ ]` todo · `[~]` in progress · `[x]` done

---

## 0. Test design — do this first

Written before the feature work so every feature below lands with its cases already
named. This is also a report artefact (chapter: Implementation & Testing).

- [x] **0.1 Test-case catalogue** — **DONE**, see [test-plan.md](test-plan.md).
      13 areas (A–M), ~150 cases with IDs, priority by blast radius, level assignment,
      current-coverage marking, an explicit "not covered and why" section, and exit
      criteria. Traceability table maps every area back to a section here.
- [ ] **0.2 Per-service coverage baseline** — record today's line/branch coverage per
      service from the existing runsettings (`backend/coverlet.runsettings`) and the
      frontend suite, so the 85% target has a starting number. Numbers get filled in
      when containers are back up; the table lands now.
- [ ] **0.3 Decide the coverage exclusions** — migrations, generated DTOs, `Program.cs`,
      DI wiring. Without exclusions 85% is measuring the wrong thing.

---

## 1. Rename KnowledgeService → RagService

Doing this first: ~754 `knowledge` occurrences across backend, frontend, compose and
docs. Every feature added before the rename is extra rename surface.

- [ ] **1.1 Inventory** — enumerate the occurrence classes: directory name, .NET
      project/namespace (`IKnowledgeAdminService`, `KnowledgeAdminClient`,
      `KnowledgeAdminDtos`), Python module paths (`knowledge_retrieval_client.py`),
      compose service name + hostname, nginx routes, internal HTTP base URLs in
      appsettings, frontend feature dir (`superAdmin/api/knowledge.ts`,
      `KnowledgeBasePage.tsx`), i18n keys in `en/superAdmin.json` + `ar/superAdmin.json`,
      env var prefixes, README/report text.
- [ ] **1.2 Decide what is user-facing vs internal.** "Knowledge base" may be the right
      *product* word even if the *service* is `rag-service`. Pin this before touching
      i18n — renaming UI strings the user liked is a regression, not a rename.
- [ ] **1.3 Rename in dependency order**: service dir + csproj/namespaces → internal
      HTTP clients and their config keys → compose service name, hostname, nginx
      upstream → frontend api module + hooks + components → i18n keys → docs/report.
- [ ] **1.4 Compatibility sweep** — DB name, volume names, MinIO bucket names, and any
      persisted config that a rename would orphan. Renaming a volume silently loses data.
- [ ] **1.5 Update the tests that name it** (`test_knowledge_retrieval_client.py`,
      `KnowledgeAdminServiceTests.cs`, `KnowledgeInternalClientTests.cs`,
      `classrooms.indexing.test.ts`) and the memory notes that reference the old name.

---

## 2. Bulk accept / reject users

- [ ] **2.1 Backend** — bulk endpoint on the admin surface alongside the existing
      single-user path (`AdminController`, `UserStatusService`, `UserStatusAction`,
      `ChangeUserStatusRequest`). Decisions to make explicitly:
      - partial success semantics — all-or-nothing transaction vs per-item result list
        (recommend: per-item result, so one bad id doesn't sink 200 approvals)
      - a cap on batch size
      - idempotency — approving an already-approved user must not error or re-notify
      - authorization re-checked **per user**, not once for the batch
- [ ] **2.2 Notifications** — approving 200 users must not fan out 200 emails
      synchronously. Route through the notification bus (see the `INotificationBus` vs
      `IEventBus` distinction) and confirm the outbox handles the batch.
- [ ] **2.3 Frontend** — selection model on the pending-users table: row checkboxes,
      select-all-on-page vs select-all-matching-filter (these are different features —
      pick one and say so), a confirmation dialog carrying the count, and a result
      summary showing partial failures.
- [ ] **2.4 Audit trail** — one audit record per user, not one per batch.
- [ ] **2.5 Tests** — see §7.

### 2.6 Scan for other useful bulk operations

Scan and produce a ranked shortlist (value vs effort), then implement only what earns
it. Candidate areas to examine, to be confirmed against the code rather than assumed:

- [ ] super-admin knowledge/RAG file management — bulk re-index, bulk delete
- [ ] classroom membership — bulk enrol / bulk remove students
- [ ] classroom materials — multi-file upload and multi-file delete
- [ ] quizzes — bulk extend deadline (`QuizExtension` already exists), bulk publish/close
- [ ] sessions & recordings — bulk delete of old recordings/transcripts
- [ ] role assignment — bulk role change
- [ ] notifications — bulk mark-as-read once §5 exists

For each: is the loop currently N HTTP calls from the browser? That is the real signal.

---

## 3. Assistant feedback — colour semantics + wording

- [ ] **3.1 Contract first.** The colour must not be a frontend guess. Add an explicit
      semantic field to the feedback payload the assistant returns
      (e.g. `severity`/`kind`: `error` | `correction` | `uncertain`) plus, where the
      assistant can supply it, the **span** — what exactly was wrong and what it should
      be. A card that says "wrong" without pointing at the word can't be coloured.
- [ ] **3.2 Model side** — update the evaluation prompt/parser
      (`evaluation_prompt.py`, the idea evaluator) so the brain emits the wrong text and
      the correction as separate fields, not one prose blob. Parser must degrade
      gracefully when the model omits them.
- [ ] **3.3 Frontend rendering** — red for the wrong word/number, green for the
      correction, orange for uncertain. Accessibility: colour alone is not a signal —
      pair each with an icon or label, and check contrast in both light and dark themes.
- [ ] **3.4 Rename "unclear" → "Likely to be"** everywhere: the enum/type value, the
      prompt vocabulary, the i18n strings (en **and** ar — the Arabic phrasing needs a
      real translation, not a transliteration), and the report text.
- [ ] **3.5 Tests** — parser tests for each severity, a malformed-model-output test,
      and a frontend render test per colour.

---

## 4. Quiz total mark — ~~build~~ **ALREADY IMPLEMENTED, verify only**

Checked the code before planning this: **it is already built end to end.** Revised from
a feature to a verification.

- `QuizQuestion.Points` carries per-question marks (set by the teacher when composing),
  so "total" is already defined as their sum — not every question is worth 1.
- `TotalPoints` is already on `QuizTeacherDto`, `QuizStudentDto`, `QuizResultsDto`,
  `StudentQuizResultDto` and `MyQuizResultDto`; `SessionQuizSummaryDto` carries
  `TotalPointsAvailable` and `CountedQuizCount`.
- Scores are computed server-side from snapshotted `PointsAwarded`, never client-totalled.
- The frontend already renders it: `StudentQuizSummary.tsx` shows `score/totalPoints`
  per quiz and `score/totalPointsAvailable` overall; `TeacherQuizSummary.tsx` shows
  per-student `score/totalPointsAvailable` plus "N marks available" for the session.
  Both have test suites.

Remaining, and small:

- [ ] **4.1 Verify the pre-close leak case** — `MyAnswerDto.IsCorrect` is `bool?`.
      Confirm it is actually null before the quiz closes, rather than nullable-in-shape
      but always populated. This is test-plan case I-19 and the one real risk left here.
- [ ] **4.2 Confirm cancelled-quiz exclusion is applied in exactly one place** across
      the teacher, student and session views (test-plan I-14). The domain comment says
      it is; verify rather than trust.
- [ ] **4.3 Decide whether anything is actually missing** from your point of view — if
      the totals are already visible where you wanted them, close this item.

---

## 5. Real-time notifications for in-session chat & quizzes

So users don't have to sit on the tab to see a message or a new quiz.

- [ ] **5.1 Pick the mechanism** — three different things, decide which are in scope:
      (a) in-app badge/toast when on another route,
      (b) browser Notification API when the tab is backgrounded (needs permission flow),
      (c) title-bar/favicon unread count. Recommend (a)+(c) first; (b) needs a
      permission prompt and is easy to make annoying.
- [ ] **5.2 Transport** — reuse the existing live-session channel rather than adding a
      second socket. Confirm what chat and quiz-published already emit.
- [ ] **5.3 Unread state** — where it lives, when it clears, and per-session vs global.
- [ ] **5.4 Do-not-disturb** — mute per session; never notify for your own message;
      suppress when the relevant panel is already open and focused.
- [ ] **5.5 Tests** — visibility-change handling, permission-denied path, no
      self-notification, unread counter arithmetic.

---

## 6. Student ranking (best → worst)

- [ ] **6.1 Define the ranking metric** — quiz average? total marks? participation?
      A single "best to worst" number is a product decision with fairness implications;
      write down the formula before coding it. Ties need a defined rule.
- [ ] **6.2 Scope & visibility** — per classroom. Does a student see the whole
      leaderboard, only their own rank, or nothing? Showing every student's standing to
      peers is a privacy decision, not a UI one — flag it for the teacher-only default.
- [ ] **6.3 Backend** — ranked query with paging; avoid N+1 across submissions.
- [ ] **6.4 Frontend** — teacher table with sort; student's own position highlighted.
- [ ] **6.5 Tests** — ties, students with no submissions, students who joined late,
      and the visibility rule.

---

## 7. Unit testing — ≥85% coverage per service

Target applies per service, measured after the §0.3 exclusions.

- [ ] **7.1 UserManagementService** — auth, 2FA/super-admin staged login, user status
      transitions, the new bulk path (§2), role assignment, internal-secret guarding.
- [ ] **7.2 ClassroomService** — quiz lifecycle & scoring (§4), deadline sweeper,
      extensions, submissions, classroom/session deletion cascades, file indexing status.
- [ ] **7.3 LiveAssistantService** — idea evaluator, boundary/drift detection, quiz
      generator + parser, retrieval client, the new severity contract (§3), pacing rules.
- [ ] **7.4 StreamingService** — token/role issuance, media config, session end &
      ejection, reconnection handling.
- [ ] **7.5 KnowledgeService/RagService** — chunking, embedding, retrieval scoring and
      the min-score cutoff, indexing state machine.
- [ ] **7.6 EmailService** — templating, retry/failure paths.
- [ ] **7.7 Frontend** — the 28 existing suites plus new ones for §2–§6.
- [ ] **7.8 Mutation-check the weak spots** — 85% line coverage with assertion-free
      tests is worse than 60% honest coverage. Spot-check the critical services by
      breaking a line and confirming a test fails.

---

## 8. Integration testing — core logic only

- [ ] **8.1 Choose the harness** — Testcontainers vs the existing compose file, and
      where these live (`backend/tests/` already has an `e2e` folder — reuse or extend).
- [ ] **8.2 Auth → approval → login** across UMS + EmailService, including the bulk path.
- [ ] **8.3 Classroom lifecycle** — create, enrol, upload material, index into RAG,
      delete and confirm the cascade across services.
- [ ] **8.4 Session lifecycle** — start, join, end, ejection, recording lands in MinIO,
      transcript persists, summary written back to ClassroomService.
- [ ] **8.5 Assistant loop** — transcript → boundary → retrieval → evaluation → card,
      with the model and STT faked so it is deterministic and runnable without Groq.
- [ ] **8.6 Quiz loop** — generate, publish, submit, score, extend, close.
- [ ] **8.7 Inter-service contract tests** — the `X-Internal-Secret` `/api/internal`
      surface: a missing/wrong secret must 401 on every internal route.

---

## 9. Session broadcast latency

- [ ] **9.1 Define what is being measured** — speaker-audio-to-listener glass-to-glass,
      chat send-to-render, quiz publish-to-appear, and assistant idea→card (there is
      already a 3.10s / 12.18s / 2.02s baseline recorded for that last one).
- [ ] **9.2 Instrument** — timestamps at each hop; the measurement must not itself
      distort the number.
- [ ] **9.3 Set budgets** — a target per hop, so a result can pass or fail rather than
      just being a number in the report.
- [ ] **9.4 Author the harness now, run when containers are up.** Note the known
      constraint: LiveKit is loopback-only today, so multi-participant numbers are not
      obtainable until the bridge-networking work is done.

---

## 10. Smoke, performance and stress testing

- [ ] **10.1 Smoke suite** — the shortest sequence that proves a deployment is alive:
      every service health endpoint, one login, one classroom fetch, one session start.
      Must run in well under a minute.
- [ ] **10.2 Performance** — pick the tool (k6 / NBomber / Locust) and script the
      realistic mixes: many students joining one session; concurrent quiz submissions at
      the deadline (the natural thundering herd); RAG search under load; bulk approve of
      a large batch.
- [ ] **10.3 Stress / breaking point** — ramp until failure to find the limit and, more
      importantly, confirm it degrades rather than corrupts: no dropped submissions, no
      half-written recordings, no orphaned sessions.
- [ ] **10.4 Resource ceilings** — what happens when MinIO, Postgres or the model
      provider is slow or down. Timeouts and retries are the thing under test.
- [ ] **10.5 Write the results template** for the report so the run only has to fill it.

---

## 11. Test double-check — gaps that add real value

Reviewed the existing suite (~22 UMS/Classroom/Streaming .NET test classes, ~50
LiveAssistant and ~45 KnowledgeService Python test modules, 28 frontend suites, one
backend e2e scenario). It is a lot more thorough than a coverage number would suggest —
the drift detector, quiz parser, egress webhooks, outbox envelopes and chunkers are all
genuinely covered. So the items below are **not** "write more of the same". They are the
places where a test would catch something nothing currently catches.

Ranked by value:

- [ ] **11.1 EmailService has zero tests — no `tests/` directory at all.**
      The highest-value gap, and it sits directly under §2: bulk-approving 200 users
      fans out through this service. Untested templating, retry and failure handling
      means a bulk approve can silently deliver nothing. Start here.
- [ ] **11.2 Authorization enforcement per endpoint.** I found no test asserting that a
      student hitting a teacher-only route gets 403, or that a non-member of a classroom
      cannot read its materials/quizzes/recordings. Services are tested; the *guards* are
      not. For a system with roles this is the biggest correctness-and-security hole, and
      it grows with every feature in §2–§6.
- [ ] **11.3 `UserManagementService.IntegrationTests` is an empty shell** — the project
      exists and builds, but contains no test source, only `obj/` artefacts. Either fill
      it (§8 needs it anyway, via `Microsoft.AspNetCore.Mvc.Testing`) or delete it. An
      empty test project reads as coverage that does not exist.
- [ ] **11.4 AuthService core is largely untested.** Only `AuthServiceLogoutTests` and
      `AuthServiceTwoFactorTests` exist. Untested: registration, login success/failure,
      password hashing and reset, email verification, refresh-token lifetime and reuse,
      lockout on repeated failures. This is the security core of the application.
- [ ] **11.5 Internal-surface negative tests.** Every `/api/internal` route should 401 on
      a missing or wrong `X-Internal-Secret`. Individual internal *clients* are tested;
      the *rejection* path does not appear to be. One parametrised test per service
      covers it cheaply and prevents an accidentally-public internal route.
- [ ] **11.6 Idempotency and duplicate delivery.** The bus is at-least-once and there is
      an outbox — so consumers must tolerate the same message twice without double
      recording, double emailing or double crediting a quiz. Worth a test per consumer.
- [ ] **11.7 Concurrency races that only appear under real use:**
      quiz double-submit (same student, two tabs), submission landing exactly at the
      deadline against `QuizDeadlineSweeper`, an extension granted while the sweeper is
      closing the quiz, and bulk approve retried after a timeout (§2 idempotency).
- [ ] **11.8 Migration tests.** EF migrations apply cleanly to an empty database and
      Alembic upgrades **and downgrades** without loss. Directly relevant to §1 (rename)
      and P3 (an embedding-dimension change rewrites the pgvector column).
- [ ] **11.9 i18n key parity, en vs ar.** A trivial test asserting the two locale JSON
      trees have identical key sets. You ship Arabic; every feature in §2–§6 adds strings
      to both files, and a missing key currently shows a raw key to the user. Cheap, and
      it never stops paying.
- [ ] **11.10 Frontend has no tests for auth, users, admin, superAdmin or roles.** The 28
      suites cluster in quizzes, streaming and whiteboard. Login, registration and the
      admin approval table — where §2's UI lands — have no coverage at all.
- [ ] **11.11 Accessibility assertions on the feedback cards (§3).** If red/green/orange
      is the signal, an automated a11y check plus a contrast assertion in both themes
      stops the colour work from being unusable for colour-blind users.
- [ ] **11.12 No browser-level e2e.** `backend/tests/e2e` drives the API and LiveKit
      directly, which is the right layer for the assistant loop but never exercises the
      React app. One Playwright journey (login → join session → submit quiz → see mark)
      would cover the seams between §4, §5 and §6.
- [ ] **11.13 Dependency vulnerability scan** — `npm audit`, `dotnet list package
      --vulnerable`, `pip-audit`. No install needed for the first two. Fast, and a good
      line in the report.

**Explicitly not recommended:** adding a mocking library. Both .NET services hand-roll
`TestDoubles.cs` and the Python services use `tests/support/fake_*.py`. That is a
deliberate, consistent style and it reads well — introducing Moq or NSubstitute now
would fragment it for no gain.

---

## 12. Tooling to install

Nothing here is installed yet unless stated. Grouped by which section needs it, so you
can install only what the current task requires.

### Required — the 85% target cannot be measured without these

| Tool | Scope | Install | Why |
| --- | --- | --- | --- |
| `@vitest/coverage-v8` | frontend | `npm i -D @vitest/coverage-v8` | **Vitest is installed but the coverage provider is not, and there is no `test:coverage` script.** Frontend coverage is currently unmeasurable. Also add `"test:coverage": "vitest run --coverage"` to `package.json`. |
| ReportGenerator | .NET | `dotnet tool install -g dotnet-reportgenerator-globaltool` | coverlet emits per-project Cobertura; this merges them into one HTML/summary report. Needed to state a per-service number in the report. |

Already present, no install: `coverlet.collector` + `backend/coverlet.runsettings` (.NET),
`pytest-cov` in both Python services' `pyproject.toml`. Note the runsettings already
excludes Migrations/obj/Program.cs — that is §0.3 mostly answered for .NET; the Python
`[tool.coverage]` sections need the same treatment.

### Needed for §8 (integration)

| Tool | Scope | Install |
| --- | --- | --- |
| `Microsoft.AspNetCore.Mvc.Testing` | .NET | NuGet — `WebApplicationFactory` for in-process API tests; fills §11.3 |
| `Testcontainers.PostgreSql` / `Testcontainers.RabbitMq` / `Testcontainers.Minio` | .NET | NuGet — real dependencies per test run |
| `testcontainers[postgres]` | Python | `uv add --dev testcontainers` |
| `Respawn` | .NET | NuGet — resets DB state between integration tests (optional but saves a lot of fixture code) |

### Needed for §9–§10 (latency, perf, stress)

| Tool | Scope | Install |
| --- | --- | --- |
| **k6** | load/stress | `sudo apt install k6` (via the Grafana apt repo) or the standalone binary. Recommended over NBomber/Locust here: scripts are JS, so they sit naturally next to the frontend, and it has first-class thresholds — which is what turns §9.3's budgets into pass/fail. |
| `k6-browser` or Playwright | glass-to-glass timing | only if you want real browser timings rather than API timings |

### Recommended, not required

| Tool | Scope | Install | Note |
| --- | --- | --- | --- |
| Playwright | frontend e2e | `npm i -D @playwright/test && npx playwright install` | §11.12. Downloads browser binaries — sizeable. |
| `msw` | frontend | `npm i -D msw` | Mock Service Worker; makes the untested auth/admin feature tests (§11.10) practical without hand-stubbing fetch |
| `jest-axe` + `axe-core` | frontend | `npm i -D jest-axe axe-core` | §11.11, a11y assertions on the feedback cards |
| Stryker.NET | .NET | `dotnet tool install -g dotnet-stryker` | §7.8 mutation checking. Slow — point it at one critical service (quiz scoring), not the whole solution. |
| `mutmut` | Python | `uv add --dev mutmut` | Python equivalent; target the idea evaluator and quiz parser |
| `hypothesis` | Python | `uv add --dev hypothesis` | property-based tests for the chunkers and retrieval scoring, where hand-picked cases miss edges |
| `pip-audit` | Python | `uv add --dev pip-audit` | §11.13 |

---

## 13. Upload size limit + show the teacher the size

Sequenced with the feature block (§2–§6); listed here to avoid renumbering.

**Confirmed by inspection:** there is no size validation anywhere —
no `MaxRequestBodySize`, no `MultipartBodyLengthLimit`, no `client_max_body_size`, no
app-level check, no frontend pre-check. `ClassroomFilesController.Upload` takes an
`IFormFile` and passes it straight through.

- [x] **13.1 Check the accidental limit first — DONE. It is worse than a production-only
      bug: the 1 MB cap is live in development too.** Traced end to end:
      - `vite.config.ts` proxies `/api` → `http://localhost`, which is the **gateway**
        (compose publishes nginx on `80:80`), not the service. Dev does *not* bypass it.
      - `apiClient` uses `baseURL: '/api'`, so every browser call goes through that proxy.
      - Uploads are `POST /api/classrooms/{id}/files`, matched by nginx
        `location /api/classrooms` → classroom-service. They traverse nginx.
      - `nginx.conf` sets no `client_max_body_size`, and compose mounts it over
        `/etc/nginx/nginx.conf` entirely — there is no `conf.d` include that could set it
        elsewhere. So nginx's **1 MB default is in force on every upload, today, in both
        environments**.

      **Conclusion: uploading any file over 1 MB currently fails with a bare nginx 413
      HTML page** — which the frontend cannot parse into a useful message. Adding
      `client_max_body_size` to nginx is therefore not part of the new feature; it is a
      bug fix that must land regardless of what limit you choose.

      Two secondary findings from the same trace, worth recording:
      - **Internal routes are not externally reachable** — `location /api/` sends
        anything unmatched to user-service, which has no `/api/internal` controllers
        (only outbound clients), so external `/api/internal/*` 404s rather than reaching
        ClassroomService. Test-plan B-10 passes today *by routing accident*, not by
        design — so a future nginx location could silently break it. Keep the test.
      - **`/api/webhooks/livekit` is not proxied either** and would fall through to
        user-service. Harmless only if LiveKit calls streaming-service directly on the
        Docker network; confirm when containers are back up, because a gateway-routed
        webhook would silently break recording.
- [x] **13.2 One configured value, enforced at every layer — DONE.**
      - [x] nginx `client_max_body_size 64m`, deliberately *above* the app's 50 MB so the
            application produces the typed error and nginx only catches what the app
            would refuse anyway. Commented in-place to say that raising the app limit
            past it silently moves enforcement back to nginx.
      - [x] Kestrel's per-request ceiling, raised to the configured value by
            `UploadSizeLimitFilter`.
      - [x] `ClassroomFileService.ValidateSize` / `ValidateType` — the exact per-file
            rules, throwing `PayloadTooLargeException` (413) or `ValidationException`
            (422).
      - [x] **Correction: the RAG service has no upload endpoint to limit.**
            `internal_documents.ingest_document` takes an **`s3_key`** in a JSON payload
            and fetches the object itself — nothing is ever POSTed to it. Since
            ClassroomService is now the only writer and caps at 50 MB, the equivalent
            protection there would be a max-bytes guard on the S3 *fetch* before
            extraction. Not implemented: the only way to exceed it is a direct write to
            the bucket, which no code path does. Recorded rather than silently dropped.
- [x] **13.3 Configurable from the backend — DONE.** `Uploads` section in appsettings →
      `UploadOptions : IUploadSettings`, registered as a singleton exactly like
      `QuizOptions`. Served to the browser from `GET /api/classrooms/{id}/upload-limits`,
      following the `QuizLimitsDto` precedent so the control and the server cannot drift.
- [x] **13.4 Reject early — DONE.** `UploadSizeLimitFilter` is an `IAsyncResourceFilter`,
      which runs *before* model binding: the last point at which an oversized body can be
      refused without buffering it. Uses `[ServiceFilter]` rather than
      `[RequestSizeLimit]` because that attribute needs a compile-time constant and the
      whole point is a configurable value. Two guards — the declared `Content-Length`
      (typed ProblemDetails for an honest client) and Kestrel's ceiling (catches a lying
      or absent one).
- [x] **13.5 Allowed-type check — DONE.** Defaults mirror KnowledgeService's extractor
      table exactly (`_support.py`), so nothing is accepted that could never be indexed.
      Content type **or** extension is sufficient — not both — because browsers send a
      generic type for Markdown and the RAG router itself dispatches on either.
- [x] **13.6 Show the size — DONE, and it was already there.** The Size column already
      rendered in `ClassroomFileList`. What it did NOT do was use the shared formatter:
      there was a second, local `formatBytes` in the component shadowing the tested one
      in `src/utils/format.ts` ("0 Bytes"/2-dp vs "0 KB"/1-dp — the same column formatted
      two different ways depending on which file you read). Deduplicated onto the shared
      one. The configured limit and accepted formats now show beside the upload button.
- [x] **13.7 Frontend pre-flight — DONE.** `rejectionFor` mirrors the server rule
      (content type OR extension) and runs before the request starts, so an oversized
      file is never transferred. Two deliberate choices:
      - the `accept` attribute narrows the OS picker, but is a convenience only — a user
        switching the dialog to "All files" defeats it, which is why the JS guard runs
        regardless. Both are tested, the guard via `applyAccept: false`.
      - if the limits request FAILS, the picker degrades to letting the server decide
        rather than blocking everything. A limits outage should not look like a broken
        upload button.
- [x] **13.8 Tests — DONE for everything a unit test can reach.**
      `ClassroomFileUploadLimitsTests`, 10 cases: exactly at the limit accepted, one byte
      over refused, zero-byte refused, disallowed type refused, `; charset=` parameters
      do not defeat the allow-list, generic type saved by extension, allowed type saved
      by a missing extension, a rejected upload leaves no S3 object and no row, and
      authorization precedes validation (so an outsider cannot probe the limits by
      watching 413 vs 401). Full suite: **306 passed, 0 failed.**
      Still needs containers: E-07 (nginx-level 413 surfacing readably) and E-03
      (Content-Length rejection before buffering) — both are integration cases.

---

## 14. Configuration hygiene + `.env.example`

- [ ] **14.1 Inventory the hardcoded values** before moving anything — one pass per
      service, recording value, where it lives, and whether it differs per environment.
      Known or likely candidates to check: retrieval min-score, boundary drift
      threshold, feedback pacing interval, embedding model + dimension, internal service
      base URLs and ports, LiveKit `node_ip`, the public service URL used for presigned
      downloads, timeouts and retry counts, batch sizes, and any fixed IDs left in
      scripts.
- [ ] **14.2 Classify each one — not everything belongs in env.** Three buckets:
      - **env var** — differs per environment or is a secret (URLs, credentials, keys)
      - **config file** (appsettings / settings.py defaults) — a tunable with a sane
        default that rarely changes
      - **leave as a constant** — a genuine domain invariant. Moving these to config is
        a downgrade: it turns a compile-time guarantee into a runtime failure. Resist
        the urge to externalise everything.
- [ ] **14.3 Bind through typed options,** not scattered `IConfiguration["Key"]` /
      `os.getenv` reads. .NET: an options class per section. Python: extend the existing
      `Settings` objects — both Python services already have one, so follow that shape.
- [ ] **14.4 Fail fast on startup** when a required setting is missing or malformed.
      A service that boots with a blank internal secret and only fails on the first
      request is far harder to diagnose than one that refuses to start.
- [ ] **14.5 Write `.env.example` — one per service plus a root one**, mirroring the
      three real `.env` files that exist today (`backend/.env`,
      `backend/KnowledgeService/.env`, `backend/LiveAssistantService/.env`). Every key
      present, every value a placeholder or safe default, each with a one-line comment
      saying what it does and whether it is required. **Never a real secret** — these
      are committed.
- [ ] **14.6 GOTCHA: `.gitignore` will swallow it.** Lines 72–73 ignore `.env` **and
      `.env.*`**, so `.env.example` is ignored today and `git add` will silently do
      nothing. Add a negation (`!.env.example`) or name the files `example.env`.
      This is the single step most likely to be missed.
- [ ] **14.7 Wire compose to it.** `backend/docker-compose.yml` currently has no
      `env_file:` entries and inlines `environment:` blocks. Decide deliberately:
      `env_file` per service, or keep inline with `${VAR}` interpolation from a root
      `.env`. Mixed styles are how a value ends up defined twice with different values.
- [ ] **14.8 Document the required-vs-optional split** in the README, and note which
      keys must be changed before a real deployment.
- [ ] **14.9 Tests** — settings binding, defaults applied when a key is absent, startup
      failure on a missing required key, and a drift test asserting every key read by
      the code appears in `.env.example` (this is the one that keeps the file honest
      after you stop looking at it).

---

## Parked — needs running containers

Not forgotten; blocked on the environment.

- [ ] **P1 Live-session media verification** — rebuild `streaming-service`, confirm the
      `media` settings object reaches the browser, reconnection in **both** directions,
      quality indicator, screen-share framerate ~5, recording still lands in MinIO.
      Cheapest available verification: needs no Groq.
- [ ] **P2 Real assistant detector end-to-end** — confirm `stream_prefetch.py` is in the
      built image, probe retrieval directly before blaming the assistant, force a
      `discrepancy`, re-measure latency. Blocked on a clean Groq exit IP.
- [ ] **P3 Arabic / multilingual** — the one open blocker is whether the RAG service's
      `qwen3-embedding` retrieval embedder handles Arabic. If not: schema migration
      (dim change) **and** re-embedding the whole corpus. The drift embedder needs no
      change; STT is already multilingual.
- [ ] **P4 Run everything authored in §7–§10** and fill in the numbers.
