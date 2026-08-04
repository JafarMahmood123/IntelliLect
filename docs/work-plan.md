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
- [x] **0.2 Per-service coverage baseline — DONE, measured 2026-08-04.** All of it runs
      locally; no containers were needed.

      | Service | Line | Tests | Notes |
      | --- | --- | --- | --- |
      | LiveAssistantService | **81%** | 299 (+3 skip) | closest to target |
      | RagService | **77%** | 235 (+9 skip) | |
      | UserManagementService | **62.1%** | 120 | Application 63.3%, Domain 54%; **Infrastructure never loaded by any test** |
      | ClassroomService | **57.4%** | 306 | Application **93.2%**, Infrastructure 30% |
      | StreamingService | **28.2%** | 104 | Application 25%, Infra 29.4%, Presentation 22.9% |
      | Frontend | **31.4%** | 226 | branch 77.4%, funcs 54.7% |
      | EmailService | **none** | 0 | no test project exists |

      **Read the shape, not the headline.** ClassroomService's *Application* layer is at
      93.2% — the domain logic is genuinely well tested, and the 57.4% is Infrastructure
      (30%) dragging it down. The deficit across every .NET service is concentrated in
      Infrastructure and Presentation, i.e. adapters, DI wiring and **controllers** —
      which is exactly the §11.2 authorization gap showing up as a number.

      **This changes what "85% per service" should mean.** Chasing 85% *including*
      Infrastructure and DI wiring buys tests for adapters that mostly prove a mapper
      maps. Recommend instead: **≥85% on Application/Domain, plus explicit controller
      authorization tests**, and let Infrastructure land where it lands. StreamingService
      is the one genuine outlier — 25% Application is a real gap, not a measurement
      artefact.
- [x] **0.3 Coverage exclusions — DONE.** .NET already had them in
      `backend/coverlet.runsettings` (Migrations, `obj/`, `Program.cs`, generated
      attributes, auto-props). Frontend exclusions now set in `vitest.config.ts`:
      types, `.d.ts`, the test harness, `main.tsx`, barrel `index.ts`. Used `all: true`
      so untested files count against the number rather than being invisible to it —
      without it, an entirely untested feature silently *raises* the percentage.
      Deliberately **not** gated on a threshold yet: a gate that fails on day one gets
      switched off. Add thresholds once the baseline clears them.

      Python now matches, in both `pyproject.toml` files: `omit` covers `app/api/main.py`
      (the entry point — the Program.cs/main.tsx analogue) and `__init__.py`; migrations
      needed no entry because Alembic lives outside `app/`, so `source` already excluded
      them. `exclude_lines` covers `raise NotImplementedError`, `@abstractmethod`,
      ellipsis bodies, `@overload` and `if TYPE_CHECKING:` — the port/ABC declarations,
      which are the Python equivalent of a C# interface and which coverlet does not count
      as coverable at all. Excluding the *lines* rather than omitting the port files keeps
      any real logic in those modules measured.

      **The check that matters: both percentages were unchanged.** LiveAssistantService
      stayed at 81% (2758 → 2658 statements), RagService at 77% (4161 → 3910). The
      exclusions removed roughly as much covered code as uncovered, which is what an
      honest exclusion set looks like — if the number had jumped, the rules would have
      been gaming it rather than measuring the right thing.

---

## 1. Rename KnowledgeService → RagService — **DONE**

**Decision (§1.2): internal only.** The service, its clients, config sections, env keys,
compose service and hostnames, README, docs and report all say Rag now. The
**"Knowledge Base" product wording stays** in both locales, and so does the frontend
feature naming (`KnowledgeBasePage`, `useKnowledgeQueries`, the `knowledge` i18n key) —
those name the *feature a user sees*, not the service. "RAG" is jargon a teacher has no
reason to know.

**The line drawn:** anything naming the service *identity*, or pointing at it, was
renamed — `RagServiceOptions`, `IRagInternalClient`, `RagAdminClient`,
`RagRetrievalClient`, `RAG_BASE_URL`, `rag-service`, `rag-db`. Anything naming the
knowledge-base *feature* was kept — `KnowledgeAdminService`, `KnowledgeAdminDtos`,
`KnowledgeAnswerResult`, and the whole frontend.

**Two names deliberately NOT renamed, because they are stateful:** the
`knowledge_db_data` Docker volume and the `knowledgedb` database name. Renaming either
would have pointed the service at a fresh empty volume and silently orphaned the entire
indexed corpus, with no way to migrate while the stack is down. The rename script carried
a guard that aborted if either string changed; confirmed afterwards in the resolved
compose output — the volume still resolves to
`intellilect-platform_knowledge_db_data`, and the DSN still ends `@rag-db:5432/knowledgedb`.

**Verified:** 306 + 120 + 104 (.NET) + 299 (LiveAssistant) + 235 (Rag) + 226 (frontend)
tests green, four .NET services build, `docker compose config` valid, final sweep finds
zero remaining references.

**Gotchas hit, worth knowing:**
- **The venv broke.** Console scripts embed an absolute-path shebang, so everything in
  `RagService/.venv/bin` still pointed at `.../KnowledgeService/.venv/bin/python` and
  failed with "No such file or directory" — *for a file that exists*. Fixed by deleting
  and re-syncing. **Anyone pulling this must recreate that venv.**
- **`run-services.txt` builds by service name**, so it would have failed on
  `docker compose build knowledge-service`. Updated.
- **Two report diagrams named the service in visible labels**, leaving the figures stale.
  `diagrams/build.sh` re-rendered all six affected PNGs (Java was available; the script
  fetches the PlantUML jar itself).
- Also removed the stale tracked `knowledge_service.egg-info` while moving the directory,
  which closes the separate cleanup item.

## 2. Bulk accept / reject users — **DONE**

**Found while building it: there were TWO status-change implementations, and the admin
dashboard used the weaker one.** `ManagementService.ChangeUserStatus` (Admin role,
`/api/admin/requests/{id}/status`) carried three defects the super-admin path did not:

1. **No refresh-token revocation on rejection.** A rejected user kept a valid refresh
   token and could renew their session indefinitely. Security-relevant, and the reason
   this was worth stopping for.
2. **No transition validation.** `User.Approve()` sets the status unconditionally, so a
   *rejected* or *deactivated* account could be silently re-approved.
3. **No self-target guard** — an admin could act on their own account.

And `DeactivateUserAsync` had a fourth: it called `SaveChangesAsync` **before**
`PublishAsync`, so the outbox row was never persisted and **the deactivation email was
never sent**. Exactly the outbox trap noted in the inter-service comms memory.

**Resolved by unifying** (decision taken 2026-08-04): `AdminController` now calls
`IUserStatusService` for all four transitions, and the three weak duplicates are deleted
from `ManagementService`/`IManagementService`. The wire contract is unchanged
("Active"/"Rejected"), so the existing client is unaffected — but rejection now revokes
sessions, invalid transitions now error instead of silently succeeding, and the
deactivation notification is actually published.

**Bulk endpoint**, exposed on both routes (`PUT /api/admin/requests/status` for the
dashboard, `PUT /api/super-admin/users/status`):
- per-item results, never all-or-nothing — one unknown id cannot sink 199 approvals
- the per-account rules are the SAME `Decide()` the single path runs, so bulk cannot
  quietly accept what single refuses
- authorization evaluated per account, not once for the batch
- ids deduplicated; empty selection refused; cap of 200 (a guard, so a constant — §14.2)
- a no-op counts as success, because that is what a retried request looks like
- one query, one notification per genuinely-changed account, one transaction

**12 new tests** (`UserStatusBulkServiceTests`), 132 UMS tests green — including the
existing 120, which is what confirms the single-path refactor preserved behaviour.

**Selection UI (C-13, C-14) — done.** Checkbox column in `UsersTable` (opt-in: pass the
three selection props or get a plain table), selection state owned by `AdminDashboard`,
an action bar that appears only when something is selected, and a confirmation carrying
the count rather than a generic "are you sure".

Four decisions worth recording:
- **Select-all covers the PAGE, not every account matching the filter.** Those are
  different features, and the one that acts on rows you cannot see is the dangerous one.
  The header checkbox is labelled "Select all accounts on this page" so it cannot be
  misread, and goes indeterminate on a partial selection.
- **Selection clears on page, filter or tab change** — otherwise an admin acts on rows
  that scrolled out of view.
- **Failures stay selected and stay on screen.** The successes drop out of the selection
  and the failures remain, with their reasons in a panel rather than a toast, so a partial
  failure can be retried without hunting for the rows again.
- **Bulk is offered on the pending tab only**, and the checkbox column is absent
  elsewhere.

9 frontend tests; suite 235 green, `tsc` clean.

<details><summary>Original plan</summary>

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
</details>

### 2.6 Scan for other useful bulk operations — **DONE, ranked below**

Swept every list screen and its per-row actions, plus the backend surfaces behind them.

**First, the thing worth knowing: three operations are ALREADY bulk**, so the candidate
list in the original plan was partly answered before it started.
- **Quiz extension** — `ExtendQuizRequest(int Seconds, List<Guid>? StudentIds)` already
  grants extra time to many named students in one call, and replaces rather than stacks
  a repeat grant.
- **RAG reindex** — `reindex_classroom` takes a file-id list (or a `failedOnly` sweep of a
  whole classroom), capped at `reindex_bulk_max = 50`. Exposed all the way to
  `useReindexClassroom` in the UI.
- **User accept/reject** — built in §2.

**Second, the signal I said to look for is not firing anywhere.** No screen currently
issues N requests in a loop, because none of them offer multi-select at all. So the
ranking below is by *how many rows a user would realistically act on at once*, not by
existing pain in the code.

#### Tier 1 — worth building

1. **Wire the super-admin users directory to the bulk endpoint that already exists.**
   `PUT /api/super-admin/users/status` is built and tested; `UsersDirectoryPage` still
   changes status one row at a time via `useChangeUserStatus`. **Zero backend work** —
   pure UI wiring, reusing the selection component pattern from `AdminDashboard`. Cheapest
   item on this list by a wide margin.
2. **Outputs (recordings + summaries) bulk delete.** `IOutputAdminService` exposes only
   `DeleteRecordingAsync`/`DeleteSummaryAsync` per id. Recordings are the largest objects
   in storage and a term's worth is dozens of rows, so "reclaim space" is inherently a
   multi-select job. Note the wrinkle: the list mixes two entity types, so a batch has to
   carry `(id, type)` pairs rather than bare ids.
3. **Classroom members bulk add/remove.** `AddMemberAsync`/`RemoveMemberAsync` are
   per-student on both the UMS and ClassroomService sides. Enrolling a cohort at the start
   of term is the single most obviously repetitive action in the product — 30 students is
   30 clicks and 30 requests today.

#### Tier 2 — plausible, lower value

4. **Knowledge-base file bulk delete** by arbitrary selection. Classroom-scoped
   index deletion already exists, so this only covers "delete these five specific files".
5. **Teacher classroom materials**: multi-file upload and multi-delete in
   `ClassroomFileList`. Multi-upload is the better half — a teacher uploads a folder of
   slides, not one file.

#### Tier 3 — deliberately not worth it

6. **Session deletion.** Each has an impact preview the admin is meant to read; batching
   them makes the preview meaningless, which is the opposite of safe.
7. **Force-end session.** You force-end one stuck session, not twenty.
8. **Admin status toggle.** There are a handful of admins, and each change is deliberate.
9. **Quiz publish/close.** A quiz is published to a live room one at a time by nature.

**Recommendation: do item 1 only, for now.** It closes a gap where the backend is already
paid for, and it makes the bulk feature reachable from both admin surfaces rather than
one. Items 2 and 3 are real but are new features rather than finishing this one — worth
scheduling deliberately rather than folding into §2.

<details><summary>Original candidate list</summary>

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

</details>

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
- [ ] **7.5 RagService/RagService** — chunking, embedding, retrieval scoring and
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
LiveAssistant and ~45 RagService Python test modules, 28 frontend suites, one
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
- [x] **13.5 Allowed-type check — DONE.** Defaults mirror RagService's extractor
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

- [x] **14.1 Inventory — DONE.** Swept every service. The result is much better than
      feared: nearly all the "hardcoded" values are prompts, MIME tables, sentinels,
      claim names, header names and email subjects — genuine constants. **Two real
      problems, both now fixed**, plus a set that deliberately stays put.

      **FIXED — broker credentials committed to source.** All four .NET services carried
      `h.Username("jafar.mahmood"); h.Password("Jafar123!")` literally in
      `DependencyInjection.cs`. Both Python services already read theirs from settings, so
      the .NET half was the outlier. Worse, the plumbing already existed: ClassroomService
      and StreamingService compose units *already pass* `RabbitMq__Username/__Password` —
      the code simply ignored them. Now read via a `Required()` helper that throws at
      startup naming the missing key. EmailService and UserManagementService compose units
      were missing the two vars entirely and have been given them, so behaviour in compose
      is unchanged.
      **The credential is still in git history** — rotating it is a separate decision.

      **FIXED — the one unconfigurable internal hop.** `StreamingInternalClient` and
      `StreamingQuizNotifier` wrote `http://streaming-service:8080/...` into the call
      itself, while every other internal client bound a typed options object. Now a
      `StreamingServiceOptions` + shared `ConfigureStreamingClient`, with the clients
      using relative paths.

      **Deliberately left alone** — these are the "leave as a constant" bucket, and moving
      them would be a downgrade:
      - `DefaultTtlSeconds`, `DefaultBatchSize`, `MinStalledAfterHours` — these are NOT
        duplicated config. They are guards that apply only when the configured value is
        `<= 0`, i.e. floors protecting against a mistyped setting. Externalising a floor
        defeats its purpose.
      - Prompts, MIME/extension tables, `X-Internal-Secret` header name, JWT claim names,
        email subject lines, parser sentinels — domain invariants and protocol constants.
      - Frontend poll intervals (5s indexing, 8s recordings, 10s sessions) and the 300ms
        debounce. UI feel, not deployment configuration. The 300ms literal *is* repeated
        across 8 superAdmin components — worth a shared constant, but as tidying rather
        than configuration.

      **Still open, noted not fixed:** `MaxAttempts = 3` retry counts in the Knowledge and
      LiveAssistant clients are arguably tunable; and several tuning thresholds in the
      Python services (`DEFAULT_SILENCE_RMS`, `_MIN_TRANSCRIPT_WORDS`,
      `_GROUNDING_QUERY_MAX_CHARS`) sit as module constants rather than in `Settings`.
      All are single-valued today with no evidence of needing per-environment variation —
      left until something actually needs to vary.
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
- [x] **14.5 `.env.example` — DONE.** Three files, matching the three `.env` files that
      are actually consumed: `backend/.env.example` (compose interpolation),
      `backend/RagService/.env.example` and
      `backend/LiveAssistantService/.env.example` (both loaded via `env_file:`).

      Both Python compose units **already said** "copy .env.example to .env" — the file
      they pointed at had simply never been written.

      **Deliberately NOT one per service.** The four .NET services have no `env_file:`
      entry and read nothing from a `.env`; their configuration is written inline in each
      `docker-compose.unit.yml`. A `.env.example` beside them would be a file that looks
      authoritative and changes nothing — the worst kind of documentation. The root
      example lists those values in a clearly-marked REFERENCE ONLY section saying where
      to actually change them. Whether to move them into env files is §14.7's decision.

      **Structure over completeness.** Each file leads with what is REQUIRED (no default
      exists, or it is a secret), then what is commonly changed, then points at
      `settings.py` for the rest. The Python settings files already document every field
      and why it holds its default; a duplicated 60-key list would drift within a week
      and be worse than a pointer.

      Verified no real value leaked: no API key, password, secret, token or database URL
      from any of the three real `.env` files appears in any example.
- [x] **14.6 The `.gitignore` gotcha — DONE, and it was real.** `.env.*` (line 73) and
      `*.env` (line 92) both matched `.env.example`, so `git add` would have silently
      done nothing — no error, no file. Added `!.env.example` and `!*.env.example`
      negations after each rule. Verified in both directions with `git check-ignore`:
      all three examples are now trackable, and all three real `.env` files remain
      ignored.
- [x] **14.7 Wire compose to it — DONE, via `${VAR}` interpolation rather than
      `env_file`.** The risk named here turned out to be already real, not hypothetical:
      the broker credentials were written out in **4** compose units, the JWT key in
      **3**, the internal secret in **5**, the LiveKit credentials in **2** — with
      nothing keeping the copies in step. A drifted JWT key presents as every request
      being 401; a drifted internal secret stops indexing, transcripts and quiz
      generation with nothing visible to the user.

      **Why interpolation over `env_file`:** the values that were duplicated are exactly
      the *shared* ones. Interpolation single-sources them in `backend/.env` while
      leaving each compose unit readable — you can still see what a service gets, next to
      the service. `env_file` per service would have re-scattered the shared secrets into
      six files, which is the same problem with more indirection. Per-service *non-secret*
      settings stay inline for the same reason.

      Used the `${VAR:?message}` form throughout, so a missing variable stops compose with
      the variable's name. That is the compose-layer counterpart of §14.4's `Required()`.

      **Verified behaviour-preserving, twice.** First, all 11 values in the existing
      `backend/.env` were compared against the compose literals before touching anything —
      all 11 matched exactly, confirming the root `.env` had been written for this and
      never wired up. Then `docker compose config` was diffed before and after:
      **byte-for-byte identical**. Every container receives exactly the environment it did
      before. No daemon needed, so this was verifiable with the stack down.

      37 substitutions across 7 files; zero credential literals remain in any compose file.
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
