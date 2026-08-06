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

1. ~~**Wire the super-admin users directory to the bulk endpoint that already exists.**~~
   **DONE — see 2.7.** `PUT /api/super-admin/users/status` was already built and tested;
   `UsersDirectoryPage` changed status one row at a time via `useChangeUserStatus`. Zero
   backend work, as predicted.
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

### 2.7 Bulk status changes in the super-admin directory — **DONE**

Tier-1 item 1 above. No backend change: `PUT /api/super-admin/users/status` already
existed from §2, so this was the UI it was missing.

The directory is not the admin dashboard's pending-only list — it mixes every status on
one page — and every decision below follows from that:

- [x] **One action never fits the whole selection.** Each action button carries the number
      of selected accounts it can actually change (`Accept (2)`, `Deactivate (1)`), and
      only those ids are sent. Actions that reach nothing are not offered at all.
- [x] **The confirmation counts what will change, not what is selected**, and adds an
      explicit line for the accounts it will skip. The number in the prompt is the number
      in the request.
- [x] **The accounts an action did not touch stay selected.** Approving the pending half
      of a mixed selection must not silently discard the other half — that would leave the
      admin reselecting rows they never acted on. Covered by its own test.
- [x] **Rows no action can reach are disabled, not hidden**: `Rejected` is terminal, and a
      super admin may not change their own status. Select-all skips them, so "select all"
      never claims more than it can do.
- [x] **Eligibility comes from `getStatusActions`** — the same function the per-row buttons
      use. The bulk bar and the row actions cannot disagree about what is legal.
- [x] **Partial failures stay on screen** and stay selected for retry, as in §2.
- [x] The failure panel is now `components/ui/BulkFailurePanel`, shared with the admin
      dashboard — the two surfaces call the same endpoint and must report it the same way.
- [x] **Tests:** 10 new cases in `UsersDirectoryPage.bulk.test.tsx`, the first tests this
      page has ever had. Frontend suite 245 green.

Not done here, deliberately: selection is per page, exactly as in §2. "Select every
account matching this filter" is a different and far more dangerous feature.

---

## 3. Assistant feedback — colour semantics + wording — **DONE**

- [x] **3.1 Contract first.** The wire payload (v2) now carries `severity`
      (`incorrect` | `likely` | `missing`) **and** the span: `incorrect_text` /
      `corrected_text`. Severity is derived server-side by `severity_of()` — the
      frontend must never be the thing that decides which colour a diagnostic label
      deserves, or every new feedback type becomes a change in every client.
- [x] **3.2 Model side** — the prompt asks for the wrong wording and its correction as
      separate fields, quoted VERBATIM, and the parser drops any quote it cannot find in
      what the teacher actually said. That guard is the important part: a hallucinated
      phrase painted red in front of a class reads as the assistant mishearing the
      lecture, and costs more trust than showing no highlight at all. Matching is loose
      about case, punctuation, spacing and typographic variants (the things STT and a
      model disagree on) and strict about the words and their order. A dropped span never
      costs the teacher the suggestion itself.
- [x] **3.3 Frontend rendering** — red struck-through "You said", green "Should be",
      amber for the hedged category. Every signal carries an icon **and** a written
      label, so the card reads correctly in greyscale, to a colour-blind teacher, and to
      a screen reader. The panel is dark-only chrome, so H-10's light-theme contrast
      check does not apply to it.
- [x] **3.4 Renamed "unclear" → "Likely to be"** — and it turned out to be a rename of
      *meaning*, not wording: the category now reports the brain's own certainty
      ("probably wrong, but I would not assert it") rather than a property of the
      teacher's phrasing, which is the more useful thing to tell a teacher mid-lecture.
      Enum, prompt vocabulary, wire value, `StatusBadge` grouping and both locales. The
      parser still accepts `unclear` from the model as an alias — it is the obvious
      synonym to reach for, and one word should not cost a teacher their feedback.
- [x] **3.5 Tests** — 16 backend (13 span/severity + 3 payload) and 7 frontend. Backend
      315 green, frontend 252 green.

**Deliberately not done: confidence does not affect the colour.** It was tempting to
downgrade a low-confidence discrepancy to amber, but `feedback_confidence_min` already
suppresses those suggestions entirely — anything that reaches a card has passed that bar
already, so re-using confidence for colour would dim cards for a reason the teacher
cannot see. The model chooses `discrepancy` vs `likely` instead, which is the same
judgement made where the information actually is.

---

## 4. Quiz total mark — ~~build~~ **VERIFIED — and one real leak fixed. DONE**

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

Remaining, and small — **all three now done, and 4.1 found a real leak.**

- [x] **4.1 Pre-close leak (I-19)** — `MyAnswerDto.IsCorrect` is genuinely null before
      the quiz closes: `GetMyResultAsync` gates it on `quiz.Status is Closed or
      Cancelled`, and gates `PointsAwarded` the same way. The FIELD was fine.
      **The same information was reachable by arithmetic through a different endpoint.**
      `GET /classrooms/{id}/my-quiz-tracking` summed `PointsAwarded` over every *counted*
      quiz — which includes the OPEN ones — and `PointsAwarded` is written when the
      answer is written, not when the quiz closes. So a student could answer, refresh
      their tracking, and read correctness off whether their total moved, while their
      answer was still changeable. Fixed by narrowing the student's own view to
      `GradedQuizzes` (Closed only), including the class average, which is the same
      inference in a classroom where only one student has answered so far. The teacher's
      view is untouched — watching a live quiz fill in is the job — so the two disagree
      while a quiz is open, deliberately. Four tests, one of which asserts the totals do
      not move across an answer.
- [x] **4.2 Cancelled-quiz exclusion (I-14)** — true, with one wrinkle now removed. The
      *predicate* was in one place (`CountsTowardsMarks`), but "counted" was expressed
      twice: `CountedQuizzes(...)` and an inline copy of the same `Where` in the session
      summary. Unified onto the helper.
- [x] **4.3 Nothing missing on totals.** `TotalPoints` is on every quiz DTO, the session
      summary carries `TotalPointsAvailable`, and both the teacher and student views
      already render `score/total`. The gap in this section was never the number — it was
      who could infer it and when.

**Noted, not changed:** the session summary (`GetMySessionSummaryAsync`) still counts an
open quiz's marks in its DENOMINATOR while contributing 0 to the numerator, so a
student's in-progress session percentage reads pessimistically. It leaks nothing — an
open quiz contributes a constant 0 regardless of correctness — and listing the quiz a
student just sat is worth more than a tidier percentage, so it stays.

---

## 5. Real-time notifications for in-session chat & quizzes — **DONE**

So users don't have to sit on the tab to see a message or a new quiz.

- [x] **5.1 Mechanism — all three, in order of ambition** (`useSessionNotifications`):
      (a) an in-app unread badge on the drawer's Chat section, for the person on the
      session page with the drawer closed; (c) a `(3)` count in the **document title**,
      which is the only carrier that reaches a backgrounded tab without asking anyone's
      permission; (b) a desktop notification for the person who has switched applications
      entirely. **No favicon badge** — it needs canvas drawing to add nothing the title
      count does not already say from the same place in the tab strip.
- [x] **5.2 Transport — nothing new was needed.** `ReceiveChatMessage` and `QuizChanged`
      already existed on the SignalR `StreamHub`; this is a second reader of the same
      events, not a second socket.
- [x] **5.3 Unread state** lives in the drawer, which is the only component mounted for
      the whole session — a panel that is not the open section is unmounted and cannot
      notice what it missed, which is the entire case this feature exists for. It is
      per-session by construction (it dies with the page). Clearing is by a `seenCount`
      ref rather than derived state, so a count cleared by opening the panel cannot be
      resurrected by a later re-render.
- [x] **5.4 Do-not-disturb** — a mute toggle in the drawer; your own message never
      notifies you; nothing fires while the relevant panel is open **and the tab is
      visible** (an open panel in a backgrounded tab has shown nobody anything, so it
      still counts, and clears when they come back to it).
- [x] **5.5 Tests** — 15 cases covering K-01..K-08: self-messages, the open-and-visible
      suppression, the open-but-hidden case, clear-and-do-not-resurrect, title arithmetic
      and restoration on unmount, one alert per batch, announce-once per quiz, the
      no-`Notification`-API browser, the refusal path, and mute.

**Permission is offered, never taken.** `requestDesktop` only runs from a click on
"Alert me outside the tab", and the offer disappears once the answer is in either way. An
unprompted permission dialog on entering a class is how a site gets blocked for good —
and a blocked site cannot alert anyone about anything.

**Known limit, stated rather than hidden:** notifications work while the session page is
mounted, including when its tab is backgrounded or its window is behind another
application — which is what was asked for. They do **not** survive navigating to another
route in the app: the hub connection is torn down with the page. Reaching a user who has
left the session for another part of the site means hoisting a session-scoped socket
above the router, which is a larger change and its own decision.

**Noticed while working here, not fixed:** `useStreamHub` is called by both `LiveRoomPage`
and `InteractionSidebar`, so every participant opens **two** SignalR connections and keeps
two copies of the chat log. Harmless today and unrelated to notifications (which read only
the drawer's instance), but it is the thing to fix first if the connection is ever
hoisted for the limit above.

---

## 6. Student ranking (best → worst) — **DONE**

The table was *already* sorted best-first. What was missing was everything that makes a
sort into a ranking: an explicit position, a rule for ties, and an answer to who may see
it.

- [x] **6.1 Metric: cumulative marks on quizzes that count.** Not an average of
      percentages — every student is measured against the same class-wide total, so score
      and percentage give the identical order, and score is the one that cannot round two
      different students onto the same number.
      **Ties: standard competition ranking.** Two students on 18 marks are both 2nd and
      the next is 4th. The rejected alternative was breaking ties by name, which is what
      the old row-index numbering effectively did — it tells a teacher that Amina beat
      Bilal when the marks say nothing of the kind. Name still orders *within* a tie so
      the list is stable across refreshes; a table that reshuffles reads as marks moving.
- [x] **6.2 Visibility: teacher sees the table, a student sees only their own position**
      and the size of the pool. Enforced by construction rather than by a filter — the
      student endpoint never builds a table, so there is none to leak. `MyClassroomQuizTracking`
      gains `Rank` and `RankedStudentCount` and still names nobody.
      The student's rank is computed over **graded** quizzes, like everything else in that
      view (§4). Ranking over open ones would restore the correctness leak by a new route:
      a position that shifts the moment you answer tells you whether you were right.
- [x] **6.3 Backend** — `Ranked()` and `RankOf()`, one rule expressed twice for the two
      directions (a whole table, and one student's place in it). No paging: the query is
      already one round trip for the whole classroom and ranking is a property of the
      complete set — paging it would mean ranking a page. No N+1: answers, submissions and
      memberships are each loaded once and grouped in memory.
- [x] **6.4 Frontend** — the teacher's list now renders the **server's** rank instead of
      the array index, so a tied pair both show the trophy. The rank cell gained an
      `aria-label`, since a bare number beside a name says nothing read aloud and the top
      row was an icon with no text at all. Students get a "Your rank" stat, `#2 of 5`, or
      a dash when they have not sat a graded quiz yet.
- [x] **6.5 Tests** — 10 backend, 4 frontend, covering J-01..J-06.

**J-04 pinned rather than changed.** A student who joins late is still ranked against the
whole term. `ClassroomMembership.JoinedAtUtc` and `Quiz.PublishedAtUtc` exist, so the
alternative was implementable — but measuring each student only against quizzes that
postdate their enrolment lets someone who sat one quiz outrank someone who sat ten, and it
makes the rankings of two students incomparable, which is the one thing a ranking must not
be. The teacher already sees `QuizzesTaken` against `QuizCount`. There is a test asserting
this so the decision is visible rather than accidental; flipping it is a product call.

**J-03 partially answered, and the limit is a data one.** A student who took part and
scored nothing IS ranked last — including one who submitted without answering. A student
who did *nothing at all* is not a row, because ClassroomService has no student names: they
arrive only as snapshots on answer and submission rows, which is exactly what keeps this
dashboard free of a cross-service call. Listing absentees by name would mean a UMS lookup
in a teacher's hot path. The teacher still sees the gap — `EnrolledStudentCount` against
`ActiveStudentCount`. Also pinned: a student whose only work was on a quiz the teacher
later cancelled drops out of the table for the same reason.

---

## 7. Unit testing — ≥85% coverage per service

Target applies per service, measured after the §0.3 exclusions.

- [x] **7.1 UserManagementService — auth core DONE.** 2FA/staged login, status transitions
      and the bulk path (§2) already had tests; registration, login, refresh and password
      reset had none, which left the least-covered part of the system as the one every
      other authorization check assumes has already happened. 21 new cases, 132 → **153**.

      Covers A-01..A-06, A-09, A-11 plus six cases the plan had not listed (A-18..A-22):
      expired refresh tokens, the super admin's rotated `amr:mfa` marking, privileged-role
      self-registration, and that the registration outbox row is published *before* the
      account commits.

      **One gap found and closed:** `RefreshAsync` never re-read the account's status, so
      session renewal depended entirely on rejection and deactivation having remembered to
      revoke every token. They do — but renewing a session is the one operation that can
      outlive the decision to end it, and it should not rest on another method's diligence.
      Now checked in both places. Mutation-verified: removing the check fails two tests.

      **One subtlety worth writing down rather than "fixing":** login's status messages are
      deliberately specific ("pending approval", "rejected") while A-02 asks for a message
      that reveals nothing. Both are satisfied because the status check sits BEHIND the
      credential check — without the password every outcome is the same generic failure, so
      the specific message is only ever shown to someone who already proved they own the
      account. There is now a test pinning that ordering, because it is the ordering that
      makes the friendly message safe.
- [x] **7.2 ClassroomService — DONE.** 330 → **376** tests, coverage **53.8% → 61.2%**
      (branch 44.1% → 58.5%). The item's named areas — quiz lifecycle and scoring, the
      deadline sweeper, extensions, submissions, the deletion cascades and file indexing
      status — were **already at or near 100%** from the §4 and §6 work; measuring first is
      what showed that, and what showed where the real holes were instead.

      **Two defects found, both fixed.**

      - **`GET /api/classrooms/{id}/members` threw for every classroom that had students.**
        `MemberResponse` is a positional record, and the profile carried
        `.ForMember(dest => dest.FullName, opt => opt.Ignore())` on one of its constructor
        parameters. `Ignore()` cannot express "leave this parameter out" — it makes
        AutoMapper look for a parameterless constructor, find none, and throw at *map* time.
        So the roster 500'd, while an **empty** classroom returned 200, because mapping an
        empty list never constructs anything. That is why it survived: the one case that
        works is the one a new classroom is in. Now mapped through `ForCtorParam`.
      - **A second dead token minter, exactly like StreamingService's.** `JwtProvider` (124
        lines) plus its `IJwtProvider` interface: mints access *and* refresh tokens with role
        claims off the shared JWT secret, registered in no container and injected nowhere.
        This service only validates UMS's tokens. Deleted rather than tested — an unused
        second minter sharing the signing key is a hazard, not a feature. It was also the
        largest 0%-covered file in the service.

      **A rule over the whole mapping profile**, so the roster defect's *class* cannot
      return: `AssertConfigurationIsValid()` runs as a test. AutoMapper resolves everything
      at map time, so a broken configuration is not a compile error and not a startup error
      — it is a 500 on whichever endpoint happens to use that map, found by whoever opened
      the page. Making it pass surfaced a second, quieter thing worth writing down:
      `CreateClassroomRequest → Classroom` left `TeacherId` unmapped. Harmless today
      (`CreateAsync` sets it from the authenticated caller *after* the map) but it is now an
      explicit `.Ignore()` with the reason, because adding a `TeacherId` to the request DTO
      would otherwise quietly let a teacher create a classroom owned by somebody else.

      **`MembershipService` 0% → 100%** (15 tests). It had no tests at all, and membership is
      the input to nearly everything else: who may join the session, who is counted in the
      tracking summary, who is ranked, whose answers are graded. The rule worth protecting is
      on removal — only the classroom's own teacher may unenrol a student, and there is a
      test pinning that ownership is checked **before** the membership lookup, so the two
      failures stay indistinguishable and a classroom id (which is in every URL a student
      uses) cannot be used to probe who is enrolled.

      **`LiveAssistantInternalClient` 0% → 100%** (21 tests). Two things live here and
      nowhere else. The internal secret is the *whole* of the authorization on those routes —
      no user token is involved — and a blank one now provably sends no header rather than an
      empty one. And the retry policy is deliberately **not** uniform: transcript calls retry
      3× because a lost delete orphans the one copy of what was said in a session, in another
      service's database (6ب); **quiz generation is never retried**, because a retry re-runs
      the model — another minute of a teacher standing in front of a class, and another call's
      cost, to repeat work that just failed. That asymmetry is precisely what a well-meaning
      "make it retry like the others" edit would undo, so both halves are pinned.

      **QuizService 93.9% → 99.3%.** The uncovered branches were all in `ValidateForPublish`,
      and they are §4 arithmetic: an empty quiz, a question with no text, a question worth
      zero or negative marks (a negative one makes a perfect paper score *less* than the
      quiz's own total, so the percentage the student and the ranking both read is wrong
      rather than merely odd), the total-duration ceiling and its exact boundary, and the
      upper half of the answer-count range — only the lower bound had a test, so an edit
      dropping the `||`'s second comparison would have gone unnoticed. Plus `GetLimits()`,
      which is the contract the composer builds itself from; if it disagreed with the server
      the composer would offer a quiz that cannot be published.

      **Mutation-checked (§7.8), 8 mutations, all caught:** a zero-mark question allowed
      through, an empty quiz allowed through, the duration ceiling off by one, generation
      retrying like the transcript calls, the internal secret never attached, any teacher
      allowed to remove any student, an enrolment never saved, and the roster map reverted to
      its broken form.

      **Still infrastructure-only and container-blocked here:** the EF repositories, the
      hosted-service wrappers (their sweepers are already at 100%), and the controllers.
- [x] **7.3 LiveAssistantService — DONE.** 315 → **375** tests, coverage **82% → 85%**.
      As in §7.2, measuring first is what mattered: every area this item names was already
      at or near 100% — idea evaluator 100%, pacing 100%, retrieval client 100%,
      boundary/drift 96%, quiz generator 95%, quiz parser 93%, and the §3 severity contract
      (`feedback_severity`, `feedback_payload`) 100% with `outcome_parser` at 97%.

      **The gap was the brains, and it was the same shape as RagService's.** Both compose and
      `.env.example` set `BRAIN_PROVIDER=gemini`, so `GeminiBrainClient` is the model that
      actually decides whether a teacher said something wrong — and it sat at **39%**. The
      existing brain tests stub `_complete` and cover parsing, so everything between the
      prompt and the parser was untested: the request that gets built, the caps that apply to
      it, and what happens when the model refuses, is blocked, or is unreachable. That half
      fails *during a live lecture*, beside a teacher mid-sentence in front of a class, where
      the symptom is not a stack trace but the assistant going quiet for the rest of the
      session. Both brains are now at **100%** (Ollama 54% → 100% on the same lines — it is
      the documented way back to a local model and the only brain that works with no key and
      no internet, which is exactly when nobody wants to find an untested path).

      What the 49 new tests pin, beyond the happy path:

      - **`_to_gemini_schema` uppercases `type` all the way down.** Gemini's `responseSchema`
        is an OpenAPI-derived proto whose `type` is an ENUM, so proto JSON wants the enum
        NAME — `"OBJECT"`, not `"object"` — while the canonical schema stays lowercase
        because that is what Ollama's `format` takes. Convert only the top level and every
        quiz generation fails, reported as nothing that mentions a schema.
      - **Quiz generation gets its own cap, temperature and timeout, not the evaluation
        ones.** The configured model *thinks*, and thinking tokens are charged against the
        same cap, so a quiz generated under the 512-token evaluation cap truncates mid-JSON.
        That does not raise — the parser gets a partial object, returns nothing, and the
        teacher sees the button do nothing at all.
      - **A blocked prompt degrades to silence rather than raising.** Lecture material trips
        safety filters more often than it sounds (medicine, history, chemistry); the
        assistant has to fall silent for that idea and carry on.
      - **A reply split across parts is joined, not truncated** — half a correction is worse
        than none, because the teacher is told they were wrong without being told about what.
      - **A malformed `GEMINI_GENERATION_CONFIG_JSON` is ignored, never fatal.** It is read
        once at startup; raising takes the service down over a stray comma in an optional
        tuning knob.
      - **The API key travels in a header, not the URL**, and a 400 whose body mentions
        `API_KEY` is reported as a key problem rather than as a malformed request — Google
        answers 400 for some bad-key cases, and the generic bucket sends the operator looking
        at a payload that is fine.
      - Ollama-specific: the **core budget** (`num_thread`) that stops generation starving
        STT on the same 8-core host, with `0` meaning "Ollama's default" rather than "no
        threads"; `stream: False`, without which the reply arrives as newline-delimited JSON
        and fails to parse; and the unpulled-model 404, which reads like a wrong URL.

      **Session teardown, 86% and 11 new tests.** Every session ends; the well-covered path
      is the one where the audio simply ran out. These are the other endings — a crash, a
      cancellation, a source that never connects — and the cleanup they skip is invisible at
      the time and surfaces later, in another service: a transcript left un-finalized never
      becomes readable, so ClassroomService's summary comes back empty with nothing to
      explain it. Also pinned: the run loop never propagates a fault, the crash log carries
      the exception **type** and never the message (the transcript is course content and logs
      outlive the session), a failing disconnect does not abort the finalize and state
      release after it, and both the retained ideas and the pacer state are released — left
      behind, they make a *later* run of the same session id start already rate-limited and
      silently drop its first real feedback.

      **Mutation-checked (§7.8), 18 mutations.** One survived on the first pass and the test
      was wrong, not the code: "a crash releases the retained ideas" asserted an empty store,
      which is empty anyway in a crash because no idea ever completes. Rewritten to seed the
      store first, and split so the pacer reset is asserted separately rather than implied by
      a test name. Both now fail when their line is removed, along with: a crash escaping the
      loop, the crash log carrying the message, finalize only on a clean end, a failing
      disconnect aborting cleanup, no disconnect at all, top-level-only schema conversion,
      first-part-only reply reading, a blocked prompt raising, a fatal config parse, the
      evaluation cap on quiz generation, env extras merged under rather than over the
      defaults, the key moved into the URL, `num_thread` always sent, streaming left on, and
      the 404 falling through to the generic message.

      **Still container- or model-blocked here:** `livekit_audio_source` (26%),
      `sqlalchemy_transcript_repository` (38%) and `faster_whisper_speech_to_text` (40%).
- [x] **7.4 StreamingService — DONE.** Coverage
      **30.1% → 39.0%**, and the two biggest movers were not more tests.

      **Deleted a dead token issuer.** `JwtProvider` (78 lines, 0% covered — the largest
      untested file in the service) minted access and refresh tokens with role claims.
      Nothing injected `IJwtProvider` anywhere: this service only *validates* UMS's tokens,
      it never issues any. A second, unused token minter sharing the JWT secret is a hazard
      rather than a feature, so it is gone rather than tested.

      **Then the JWT signing key turned out to fail open.** ClassroomService and
      StreamingService both read
      `jwtSettings["SecretKey"] ?? "MY_SUPER_DUPER_STRONG_UNEXPECTED_SECRET_KEY"` — so a
      missing `Jwt__SecretKey` did not break them, it made them validate tokens signed with
      a key that is public in this repository's history. Anyone could mint a SuperAdmin
      token and both services would accept it. Same fail-open shape as the internal secret,
      on the front door. All three services now use the `Required(...)` helper from §14, and
      the literal is deleted from the three `appsettings.Development.json` files that also
      carried it. Every compose file already sets `Jwt__SecretKey` with `:?`, so nothing
      that runs today changes — a bare `dotnet run` without it now fails fast naming the key.

      **13 tests on the LiveKit join token**, which is the actual "token/role issuance" this
      item names: it IS the authorization for the media room, and once LiveKit holds it our
      code is never consulted again. Covers the view-only student (master switch off AND an
      empty source list, because either alone would leave a way in), one-permission grants,
      that a student never gets screen-share, that a teacher keeps it with camera and mic
      off, case-insensitive role matching, an unknown role failing toward fewer rights, room
      and identity binding, the role in participant metadata, and that subscribe and data
      never depend on publish rights. Mutation-checked on the master switch.

      **The rest is now DONE too.** 130 → **158** tests, coverage **39.6% → 44.8%**
      (branch 31.2% → 34.4%). The three things this bullet still listed — media config
      beyond the token, session end and ejection, and reconnection handling — are covered,
      and `StreamService` went **38.7% → 100%** with `LiveKitRoomLifecycleService` **0% →
      100%**.

      **A seam, not a mock.** `LiveKitRoomLifecycleService` was untestable because it built
      the SDK's `RoomServiceClient` in its own constructor, and the SDK's methods are
      non-virtual. Rather than invent a mocking approach, it now takes `ILiveKitRoomClient` —
      the same pattern, and the same reasoning, as the `ILiveKitEgressClient` this service
      has used all along. The transport (and its 5s fail-fast timeout) moved to a thin
      `LiveKitRoomClient` adapter; every decision stayed on the testable side.

      **Session end and ejection, 19 tests.** Closing the room is what actually removes
      people: the "session ended" broadcast is a courtesy that a sleeping tab or a flaky
      connection never receives, and if the room is not deleted that student stays connected
      to live audio and video of a classroom everyone else considers closed. A failure to
      close is deliberately *not* swallowed — the caller is the only one positioned to retry
      — while the publish-policy path deliberately is, because a room that does not exist yet
      is the normal case when a teacher sets the policy before anyone joins.

      The publish policy is the mute switch, applied by role read from participant metadata,
      and the tests are mostly about who is *not* touched: revoking a source
      force-unpublishes it immediately, so mistaking the teacher for a student cuts off the
      person giving the lecture, mid-sentence, in front of the class. Covered: only students
      are updated, the role match is case-insensitive, an unreadable role (absent, malformed,
      or valid JSON with no role) is left alone rather than guessed at, muting sets **both**
      the master switch off and an empty source list, a muted student keeps subscribe and
      data (muting is not ejection — dropping either turns "you may not speak" into "you may
      not attend"), each granted source is the only one granted, and one participant that
      disconnects mid-sweep does not stop the rest.

      **Media config beyond the token, 9 tests.** `MediaOptionsTests` already pinned how the
      "Media" section binds; what had no coverage was whether what was bound actually reaches
      the browser. That is a separate failure with no symptom — a value dropped in the
      mapping does not error, it silently leaves livekit-client on its own default, which by
      those same tests' reasoning means a thumbnail-sized tile pulling a full-resolution
      stream or a single failed reconnect ejecting a student. Written as a **rule over
      `IMediaSettings`** in both directions, so a nineteenth setting that is never mapped
      fails and names itself, and a response field with no source cannot appear either. Every
      value in the fixture is distinct, so a crossed pair (width/height) cannot pass by
      coincidence. The reconnection settings get their own case: they are frozen when the
      browser constructs its Room, so a value that does not arrive with the token has no
      second chance.

      **`LiveKitEgressClient` and `LiveKitRoomClient` stay at 0%, deliberately.** Both are
      pure delegation to the SDK behind an interface whose consumers *are* tested; there is
      no branch in either to get wrong. Testing them would be testing the LiveKit SDK. Noted
      here rather than papered over with a hollow test.

      **Mutation-checked (§7.8), 9 mutations, all caught:** the role check removed, the role
      match made case-sensitive, the master publish switch left on with an empty source list,
      one failed participant aborting the sweep, a missing room made fatal, a blank room name
      still issuing a delete, a media setting dropped from the join response, two media
      fields crossed, and an ended session still issuing a join token.

      **Still open in this service, and container-bound:** `StreamHub` (the SignalR hub, 96
      lines at 0%), the EF repositories, and the composition root.
- [x] **7.5 RagService — DONE.** 235 → **335** tests, coverage **77% → 81%**. Four new
      suites, taking the three named areas from partial or absent to complete.

      **Embedding, which was the real gap.** `embedding_provider` defaults to `gemini` and
      every compose file keeps it there, yet `GeminiEmbeddingProvider` sat at **33%** — the
      embedder that actually deploys, barely tested. That matters more than the percentage
      because almost nothing in it fails loudly: a vector built from the wrong task type,
      left unnormalised, or paired with the wrong chunk produces no error anywhere. It
      produces retrieval that returns the wrong passage and an assistant that answers
      confidently from it. Now **100%**, 22 tests, all against a recording fake HTTP client
      so the assertions are about the request that gets *sent*:

      - Documents go as `RETRIEVAL_DOCUMENT` and queries as `RETRIEVAL_QUERY` — the whole
        reason this provider replaced the Ollama one, and sending both the same way costs
        retrieval quality with no other symptom.
      - **Order is preserved across the concurrent fan-out.** `embed_documents` issues one
        request per text and the caller zips the result straight onto the chunk list; if the
        order followed *completion* rather than input, chunk 3's text would be stored with
        chunk 7's vector. Same count, same widths, so the repository's `strict=True` zip
        passes and the database accepts it. The test makes the first text the slowest so
        completion order really is reversed.
      - The Matryoshka-truncated vector is L2-normalised (a 768-dim reply measures ~0.59),
        the qwen `retrieval_instruction` never reaches Gemini, the API key travels in a
        header and not the URL, an empty batch costs nothing, and one failed text fails the
        whole batch rather than returning a short — and therefore misaligned — list.
      - Error mapping, because the operator only ever sees the message: rejected key, rate
        limit, generic 5xx, unreachable endpoint, and a 200 carrying no vector.

      `OllamaEmbeddingProvider` (the offline fallback, and the one with the multilingual
      story) went **76% → 100%**: the instruction asymmetry, batching and its ordering, the
      optional bearer token, and the "is Ollama down or is the model not pulled?" messages.

      **The indexing state machine had no tests at all** — service, runner and both
      endpoints. It is the only recovery path from an embedder change, because changing the
      model means an Alembic migration that DROPS every stored vector. Now 100% / 98% / 100%
      across 26 tests: the dimension probe refuses a mismatched embedder *before* anything is
      embedded and billed; each chunk keeps its own vector (keyed by id, so a mis-zip writes
      real vectors against the wrong rows rather than raising); resumability really does come
      from `embedding IS NULL`, so a re-run picks up only what is missing and never
      re-embeds; a second sweep is refused rather than queued; and a failed sweep leaves a
      readable reason behind, since the POST returned long before the failure happened.

      **One defect found and fixed.** `POST /api/internal/reembed` answered **202 for a run
      it had refused** — the status code said accepted while the body said already-running.
      Its own docstring promised 409. This is a curl-and-script endpoint with no UI, the
      obvious way to drive it is `-f` or a `resp.ok` check, and "accepted but nothing is
      happening" invites POSTing again. Now 409, as documented.

      **Chunking** — `_text_splitter.py` **78% → 97%**, the factory to 100%, the semantic
      chunker to 96%, over 30 new tests. The untested paths were the awkward ones, which is
      where a real lecture PDF ends up: the hard character wrap for a single unsplittable
      token (a base64 blob, a URL, a flattened table), which is now checked to lose and
      duplicate nothing; the overlap seed being shrunk so the incoming atom still fits, since
      the budget is the hard constraint and the overlap only a preference; the identity-based
      overlap contract the callers dedupe on; and the trailing-runt merge not bringing its
      shared atoms along twice. Plus the degenerate documents that are common rather than
      exotic — an image-only page, a one-sentence title slide (which must not cost an
      embedding call), and a format with no grouping rule.

      **On the "min-score cutoff" this item named: there isn't one, and that is deliberate
      rather than missing.** Retrieval returns `top_k` regardless of similarity. The
      grounding is done at the prompt instead — `AnswerService` never calls the model when
      retrieval is empty, and `SYSTEM_PROMPT` instructs a refusal when the context does not
      contain the answer. Adding a numeric threshold would need it calibrated against a real
      corpus and a live embedder, which is container work; noted in §8 rather than guessed
      at here. `RetrievalService` is already at 100%; the pgvector scoring itself
      (`1 - cosine_distance`, ordering, classroom scoping) has a test that skips cleanly
      until `TEST_DATABASE_URL` points at a real Postgres.

      **Mutation-checked (§7.8), 18 mutations across the four suites**, every one caught and
      named: completion-order results, dropped normalisation, a shared task type, an
      unbounded fan-out, a removed width guard, a dropped query instruction, a missing auth
      header, a swallowed 404, a character-skipping wrap, an unshrunk overlap seed, sentence-
      before-paragraph splitting, a duplicated runt merge, an unchecked vector count, a
      permissive dimension probe, a second concurrent sweep, the 409 reverted to 202, an
      un-normalised strategy name, and an oversized span emitted whole.
- [x] **7.6 EmailService — DONE.** The service that had **zero** tests now has 28, in a
      new `EmailService.UnitTests` project. Consumers are driven through MassTransit's
      in-memory harness (a real publish and consume, no broker, no SMTP), templates are
      tested directly. **72.9%** line coverage measured with the shared
      `backend/coverlet.runsettings`; everything with logic in it — the body factory and
      all five consumers — is at 100%. The remainder is `SmtpEmailSender` (needs a real
      SMTP server), the composition root, and the consumer definitions, which only execute
      against a live bus.

      Writing them turned up two defects, both fixed here:

      - **HTML injection into outgoing email.** `firstName` and `classroomName` were
        interpolated raw into the templates. Both are user-supplied — a registration form
        and a classroom title — so a name containing markup landed as live HTML inside an
        email carrying our name and branding. Mail clients mostly refuse to run script but
        render links and images perfectly well, which is all a convincing phishing line
        needs. Every interpolation is now HTML-encoded, including the codes: a rule with an
        exception is a rule someone eventually forgets to apply.
      - **Three of five consumers had no retry policy.** Reset-code and 2FA had
        definitions; status-changed, teacher-changed and membership-changed did not — not
        by decision, just omission. One SMTP hiccup sent an approval or enrolment email
        straight to the error queue, and nobody watches an error queue for the mail that
        told a student they were accepted. All five now share the same three-attempt
        policy, and the test that enforces it is a **rule over the assembly** rather than a
        list of today's five, so the sixth consumer cannot ship without one.

      Also fixed: `EmailService.slnx` referenced a `EmailService.Contracts` project that
      does not exist, so `dotnet build`/`dotnet test` on the solution failed outright. Stale
      entry removed, test project added — the solution now builds and runs its tests.

      **Mutation-checked (§7.8).** Adding an undefended consumer makes the retry rule fail
      and name it; the rule is not passing by vacuum, and a second test asserts the
      reflection finds five consumers so a broken query cannot make it green.
- [x] **7.7 Frontend — DONE.** 28 suites at the start of this work, **38** now, 346 tests. Coverage
      **31.4% → 41.4%**. New suites for §2 (bulk selection), §3 (feedback severity and the
      correction span), §5 (notifications), §6 (ranking), plus the locale-parity rule (§7.9).

      This pass took the **client-side authorization gates from 0% to 100%** — `src/routes`
      had no tests at all. They are not the security boundary (the server is, and it refuses
      on its own terms); what they decide is whether someone is *shown* a page they cannot
      use, which is the difference between a clean redirect and a dashboard that renders and
      then fills with failed requests. 15 cases, including the two that matter most: a role
      is not admitted by a **substring** match (`SuperAdmin` contains `Admin`) or by a
      **case-insensitive** one, and an empty allow-list admits nobody.

      Also `getDefaultRoute` (7 cases — status outranks role, and an unknown role falls back
      to a real path rather than `undefined`) and the auth store's logout (6 cases). The
      logout ones are about a failure that is invisible by construction: a logout leaving a
      token behind looks entirely correct — the user is returned to the login screen — while
      the credential is still on the machine, which is precisely what someone on a shared
      computer pressed the button to avoid. Covered: the server-side revocation happens, the
      local session is cleared even when that request fails, and the **persisted** copy does
      not keep the departing user's profile.

      **A later pass covered the two that mattered, and found a defect.** 318 → **346**
      tests, coverage 40.9% → **41.4%**. `axios.ts` **31% → 100%**, `getApiErrorMessage`
      **11% → 95%**. The headline barely moves because both files are small; what they decide
      is not.

      **DEFECT FOUND AND FIXED: several requests expiring together signed the user out.**
      The response interceptor refreshed **per request**. A page fires several requests at
      once, so when the access token expires they all 401 together — and each one
      independently read the *same* refresh token from `localStorage` and posted it. The
      backend **rotates**: `AuthService.RefreshAsync` revokes the presented token and issues a
      new one. So the first call succeeded and the second and third arrived with a token that
      was already dead, failed, and took the sign-out branch — clearing storage and
      redirecting to `/login`.

      The user was signed out *even though the refresh had succeeded*, and because it depends
      on how many requests happened to be in flight, it looks random and is nearly
      unreportable. Fixed with a single shared in-flight refresh promise that every concurrent
      401 awaits, cleared when it settles so a later expiry still does real work. The test
      that found it fails against the old code and passes against the new.

      Also pinned: the token is attached to every request and no header is invented when
      nobody is signed in; a non-401 never triggers a refresh (refreshing on a 500 would hide
      the real error and, on failure, sign the user out over a server fault); a failed
      **login** is exempt, or a wrong password would redirect the user away from the form they
      are standing at; the `_retry` guard stops an infinite refresh loop; and sign-out clears
      `auth-storage` too, so the next person at that browser does not see the previous user's
      name and role.

      `getApiErrorMessage` is the sentence a user reads when anything fails, and at 11% almost
      none of its fallback chain had ever executed. 13 tests fix the precedence that decides
      which of several candidate messages wins: the problem `detail` beats a validation error
      beats ASP.NET's generic `title` ("One or more validation errors occurred", which tells
      the user nothing) — and whitespace counts as blank, because a message of `"   "` renders
      as an empty error box, which is worse than the generic sentence.

      **Mutation-checked (§7.8), 10 mutations.** Two needed work rather than being clean
      passes, and both are recorded because the fix is the interesting part:

      - Removing the `_retry` guard made the test **hang** rather than fail — the infinite loop
        the guard prevents. A test that hangs reports nothing, so the refresh endpoint now
        succeeds only once in that case: the loop terminates on the second refresh and the
        count assertion says what happened.
      - Removing the explicit `Authorization` header on the retry changed nothing, because
        replaying through `apiClient` re-runs the REQUEST interceptor, which re-reads the
        freshly stored token. The line is genuinely redundant; it is kept as belt and braces
        with a comment saying so, rather than left looking load-bearing.

      Still at 0% and genuinely lower value: `src/pages` and `AuthLayout` (thin wrappers), the
      profile forms, `useThemeStore`, and `download.ts`.
- [x] **7.8 Mutation-check the weak spots — DONE, and done continuously rather than as a
      pass at the end.** Every §7 item above was mutation-checked as part of finishing it, by
      breaking a line in the source and confirming a named test fails: EmailService (the retry
      rule), UMS (the status re-check), StreamingService (the publish master switch, then 9
      more), ClassroomService (8), RagService (18), LiveAssistantService (18), the frontend
      (10), and the settings work below (6). **Roughly 70 mutations in total, across every
      service.**

      The value was not the ones that passed. **Four survived and each exposed a test that was
      passing for the wrong reason**, which is precisely what this item exists to catch:

      - *A crash releases the retained ideas* (LiveAssistantService) asserted an empty store —
        which is empty anyway in a crash, because no idea ever completes. Rewritten to seed the
        store first, and split so the pacer reset is asserted rather than implied by a name.
      - *Every request that waited on a shared refresh gets the new token* (frontend) read the
        recorded request headers, but the retry mutates the original config object, so the
        mock's history showed the final header on the original entry too. Rewritten to make the
        server DEMAND the new token.
      - *Removing the `_retry` guard* made a frontend test **hang** rather than fail. A test
        that hangs reports nothing; the refresh endpoint now succeeds only once there, so the
        loop terminates and the count assertion says what happened.
      - *A missing internal secret names its section* (UMS) passed with no custom message at
        all, because the default DataAnnotations text contains the section name by accident of
        the options type being called `RagServiceOptions`. Now asserted on the actionable half
        of the message instead.

      Two mutations were also *informative rather than failing*: removing the explicit
      `Authorization` header on the frontend retry changed nothing (the request interceptor
      re-reads the token), so that line is documented as belt-and-braces rather than left
      looking load-bearing.

---

## 7b. Authorization on service-to-service routes (Area B, B-08/B-09) — **DONE**

Started as "write authorization tests" and turned into a fix, because the first thing the
tests found was that the guard did not guard.

`WebApplicationFactory` is not usable here — `Program.cs` migrates the database at startup
and throws after five retries, so an HTTP-level suite needs containers. The declarations
themselves do not: authorization is *declared* in the presentation layer, and that is where
our bugs are, so the tests target the filter and the declarations rather than ASP.NET's
enforcement of them.

**Two defects, both fixed:**

- **The guard failed open.** `IsInternalSecretValid()` returned *true* when
  `Internal:ApiSecret` was unset. A missing or misspelled environment variable silently
  exposed every internal endpoint — file deletion, classroom administration, session
  control — to anything that could reach the port. The comment above it shows this was
  deliberate ("when it is unset, e.g. local dev, the header check is skipped so nothing
  breaks"), which is exactly how a convenience becomes a production hole. The Python side
  of the same secret already says "fails closed if the server has no secret configured";
  the .NET side now agrees.
- **StreamingService had no check at all** on its four internal routes, while every other
  service required the header. Being off the nginx path is a fact about today's topology,
  not a property of the code.

**And the shape changed.** The check was hand-written inside every action — 23 copies across
five controllers. Nothing would have noticed the 24th action that forgot it. It is now one
`[InternalSecret]` filter declared on the controller, running at the authorization stage so
a rejected caller never gets a request body deserialized on their behalf, and comparing in
constant time because the secret is long-lived and shared by every service.

**The whole loop had to move together**, or fixing StreamingService would have broken the
system: none of its three callers sent the header. The secret is now attached where the
base address already is (one place per caller, not one per client), and the three compose
files carry the matching values — `Internal__ApiSecret` inbound on StreamingService,
`StreamingService__InternalApiSecret` outbound from ClassroomService and UMS.

- [x] Filter tests: match, missing header, wrong secret, exact comparison, and the
      unconfigured case that used to admit everyone (B-08, B-09, **B-12**).
- [x] Conformance rule per service: every controller routed under `api/internal` carries the
      guard (**B-13**). Mutation-checked — removing the attribute fails the test and names
      the controller.
- [x] 327 ClassroomService and 111 StreamingService tests green.

**Still open, and it needs containers:** B-01..B-07 and B-11 — that anonymous and
wrong-role callers are actually refused at runtime — plus B-10, that nginx does not expose
the internal paths. Those are enforcement rather than declaration, and they belong in the
integration suite (§8).

---

## 7.9 Configuration & localisation conformance — **DONE**

Three rules that watch the things this session has been quietly relying on.

- [x] **M-02 — required settings fail at startup**, tested against the real composition root
      so a reintroduced `?? "default"` fails the test rather than only a changed helper.
      **Writing it found that the claim was not true.** The broker credentials were read
      inside MassTransit's `UsingRabbitMq` callback, which is deferred until the bus starts —
      so a missing one surfaced after the container was built, wrapped in bus-startup noise,
      rather than "at startup naming the key" as the comment beside it promised. Both reads
      are now hoisted above `AddMassTransit` in all four services, which makes the promise
      literally true and the test meaningful.
- [x] **M-03/M-04 — `.env.example` drift**, both directions: every variable a compose file
      *requires* is documented, and nothing documented goes unread. `${VAR:-default}` is
      deliberately exempt — it carries its own fallback, and the e2e harness uses that form
      for an audio fixture nobody else needs to configure. Plus a blunt heuristic for a
      pasted credential, since the template is committed.
- [x] **M-09 — locale parity**, per namespace so a failure names the file. Keys *and*
      interpolation placeholders, because `{{count}}` written as `{{n}}` in one language
      renders literal braces to the user.

      The first run failed, and the failure was **Arabic being right**: `groupCount_few`,
      `_many` and `_two` exist only there, because the plural categories are a property of
      the language — English needs two, Arabic six. The dual form "مسؤولان" also carries no
      numeral, which is correct Arabic and would have failed a naive placeholder check. Both
      comparisons are now plural-aware, over base keys.

- [x] **The empty `UserManagementService.IntegrationTests` project is deleted.** It held one
      `.csproj` and no tests. Integration coverage needs containers (§8), and an empty
      project sitting in the solution listing reads as "integration tests exist" to anyone
      scanning it. Recreating it is a minute's work on the day §8 starts.

---

## 7.10 Consumer idempotency (L-01) — **DONE for the consumers that matter**

At-least-once delivery is the broker's design, not a failure mode, so every consumer is
redelivered eventually — a lost acknowledgement is enough. The question is only what a
second delivery does.

- [x] **`SessionStartedConsumer` had no tests at all**, and it holds the only guard stopping
      a redelivery from creating a second live-stream row for one session. Two rows would
      leave every later lookup picking one arbitrarily. Six cases: the happy path, an
      unguessable stream key, the redelivery, that an already-live session is left *exactly*
      as it was (a no-op, not a repair — overwriting would reset the start time and key of a
      session people are watching), that two different sessions still each get a stream, and
      that a repository failure faults the message instead of being swallowed.
      Mutation-checked: removing the guard fails two of them.
- [x] Recording-ready and summary-ready consumers already had duplicate-delivery tests.

**The five EmailService consumers are deliberately exempt**, recorded in the test plan's
§17 rather than left looking like an oversight. MassTransit redelivers after a *failure*,
and if the SMTP call threw then the mail was almost certainly never sent — so re-sending is
correct rather than duplicate. A genuine duplicate needs the send to succeed and the ack to
be lost, and costs one repeated email. Making that exact means a dedup store keyed on
message id: permanent infrastructure against a rare, harmless annoyance.

---

## 8. Integration testing — core logic only

- [x] **8.1 Choose the harness — DECIDED: extend `backend/tests/e2e`, not Testcontainers.**
      Not a close call once the existing harness is read properly. It is already a pytest
      suite with typed HTTP clients for all five services, a config module driven by `E2E_*`
      env vars, readiness polling, and — the part that matters most — a `run-in-network.sh`
      that runs the suite **inside the compose network**, which is the only way to reach the
      unpublished `/api/internal` ports on ClassroomService and StreamingService.

      Testcontainers would mean re-declaring a topology compose already declares: seven
      services, four Postgres instances, RabbitMQ, MinIO and LiveKit. Worse, the compose files
      encode constraints that were expensive to learn and are not obvious — `LIVEKIT_HOST_IP`
      having to be `127.0.0.1` on Docker Desktop because host networking forwards UDP on
      loopback only, and the `include:`-based layout that makes running a unit file alone
      fail. A second declaration of all that would drift from the first, and the drift would
      present as a test failure rather than as a configuration difference.

      `WebApplicationFactory` was ruled out separately, in §7b: `Program.cs` migrates the
      database at startup and throws after five retries, so an in-process host needs a real
      Postgres anyway — at which point compose is already doing the job.

      **Where they live:** alongside the existing suite, one file per §8 item, each behind its
      own pytest marker so a run can pick what its environment supports (the existing
      `media` / `feedback` / `recording` markers already work this way).
- [ ] **8.2 Auth → approval → login** across UMS + EmailService, including the bulk path.
- [ ] **8.3 Classroom lifecycle** — create, enrol, upload material, index into RAG,
      delete and confirm the cascade across services.
- [ ] **8.4 Session lifecycle** — start, join, end, ejection, recording lands in MinIO,
      transcript persists, summary written back to ClassroomService.
- [ ] **8.5 Assistant loop** — transcript → boundary → retrieval → evaluation → card,
      with the model and STT faked so it is deterministic and runnable without Groq.
- [ ] **8.6 Quiz loop** — generate, publish, submit, score, extend, close.
- [~] **8.7 Inter-service contract tests — AUTHORED, not yet run** (needs the platform up).
      `tests/e2e/test_internal_surface_contract.py`, marker `internal`, **55 tests**: 18
      read-only internal routes × three cases, plus a rule over the table.

      The route table is derived from the controllers and routers themselves and every path
      was verified against the source, not against documentation. Covers all four services
      that expose an internal surface.

      Three things about its design are the point of it:

      - **The third case is the one that makes the other two mean anything.** A service that
        is down, a path that no longer exists, or a guard that rejects everything would make
        both "must 401" assertions pass for entirely the wrong reason — and refusal tests are
        the ones most likely to be trusted without being re-read. So every route is also
        probed *with* the correct secret and asserted **not** 401. Deliberately weak on the
        success side: a missing id gives 404, an empty list gives 200: pinning an exact status
        would make the file break whenever fixture data changed, which is how a security test
        ends up deleted.
      - **Read-only, on purpose.** The internal surface includes deletes — purging a
        classroom's index, dropping a transcript. If the guard were broken, which is precisely
        what this file exists to detect, probing those would carry them out. A read is a safe
        proxy for the whole controller because the .NET filter is declared at controller level
        and the FastAPI dependency at router level, with unit-level assembly rules pinning
        that. Two POST-only routers are therefore not probed at all, and the file says so
        rather than leaving it as an apparent omission — probing them would trigger a real
        summary run and a real quiz generation, really billed, on every run.
      - **It must run in-network.** Through the gateway these routes are blocked by nginx, so
        a version pointed at `E2E_GATEWAY_URL` would be testing nginx's route table and would
        pass with the guard removed entirely. The in-network runner points each client at the
        service's own container DNS name — the exact path the guard exists to protect.

      This is the integration half of test-plan B-10; B-08/B-09/B-12/B-13 already cover the
      guard's logic at unit level. **To run it:**

      ```bash
      cd backend && docker compose up -d
      cd tests/e2e && ./run-in-network.sh -m internal
      ```

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

- [x] **11.1 EmailService has zero tests — no `tests/` directory at all.**
      The highest-value gap, and it sits directly under §2: bulk-approving 200 users
      fans out through this service. Untested templating, retry and failure handling
      means a bulk approve can silently deliver nothing. Start here. → **DONE (§7.6).** 28 tests in a new `EmailService.UnitTests` project; consumers driven through MassTransit's in-memory harness. Found HTML injection into outgoing mail and three consumers with no retry policy.
- [ ] **11.2 Authorization enforcement per endpoint.** I found no test asserting that a
      student hitting a teacher-only route gets 403, or that a non-member of a classroom
      cannot read its materials/quizzes/recordings. Services are tested; the *guards* are
      not. For a system with roles this is the biggest correctness-and-security hole, and
      it grows with every feature in §2–§6.
- [x] **11.3 `UserManagementService.IntegrationTests` is an empty shell** — the project
      exists and builds, but contains no test source, only `obj/` artefacts. Either fill
      it (§8 needs it anyway, via `Microsoft.AspNetCore.Mvc.Testing`) or delete it. An
      empty test project reads as coverage that does not exist. → **DONE (§7.9).** Deleted. An empty project that never runs is worse than no project: it reports green.
- [x] **11.4 AuthService core is largely untested.** Only `AuthServiceLogoutTests` and
      `AuthServiceTwoFactorTests` exist. Untested: registration, login success/failure,
      password hashing and reset, email verification, refresh-token lifetime and reuse,
      lockout on repeated failures. This is the security core of the application. → **DONE (§7.1).** `AuthServiceCoreTests` added alongside the logout and 2FA suites. Found that `RefreshAsync` never re-checked account status.
- [x] **11.5 Internal-surface negative tests.** Every `/api/internal` route should 401 on
      a missing or wrong `X-Internal-Secret`. Individual internal *clients* are tested;
      the *rejection* path does not appear to be. One parametrised test per service
      covers it cheaply and prevents an accidentally-public internal route. → **DONE (§7b).** Filter tests plus a rule over the assembly per service, so a new `api/internal` controller cannot ship without the guard. Found the guard failing *open* when unconfigured, and StreamingService having none at all.
- [x] **11.6 Idempotency and duplicate delivery.** The bus is at-least-once and there is
      an outbox — so consumers must tolerate the same message twice without double
      recording, double emailing or double crediting a quiz. Worth a test per consumer. → **DONE (§7.10).** `SessionStartedConsumer` had no tests and holds the only guard against a redelivery creating a second stream; recording-ready and summary-ready already had theirs.
- [ ] **11.7 Concurrency races that only appear under real use:**
      quiz double-submit (same student, two tabs), submission landing exactly at the
      deadline against `QuizDeadlineSweeper`, an extension granted while the sweeper is
      closing the quiz, and bulk approve retried after a timeout (§2 idempotency).
- [ ] **11.8 Migration tests.** EF migrations apply cleanly to an empty database and
      Alembic upgrades **and downgrades** without loss. Directly relevant to §1 (rename)
      and P3 (an embedding-dimension change rewrites the pgvector column).
- [x] **11.9 i18n key parity, en vs ar.** A trivial test asserting the two locale JSON
      trees have identical key sets. You ship Arabic; every feature in §2–§6 adds strings
      to both files, and a missing key currently shows a raw key to the user. Cheap, and
      it never stops paying. → **DONE (§7.9).** Per namespace, keys *and* interpolation placeholders, plural-aware over base keys so Arabic's six categories are not read as drift.
- [x] **11.10 Frontend has no tests for auth, users, admin, superAdmin or roles.** The 28
      suites cluster in quizzes, streaming and whiteboard. Login, registration and the
      admin approval table — where §2's UI lands — have no coverage at all. → **DONE (§7.7).** Route guards 0% → 100%, the auth store, `getDefaultRoute`, and the axios interceptor — where a refresh-per-request defect was found and fixed.
- [~] **11.11 Accessibility assertions on the feedback cards (§3).** If red/green/orange
      is the signal, an automated a11y check plus a contrast assertion in both themes
      stops the colour work from being unusable for colour-blind users. → **The
      colour-blindness half is DONE (§3/§7.7):** each severity carries an icon *and* a
      label, not colour alone, and there are tests asserting the non-colour cue. **The
      contrast half is not** — test-plan H-10 is still open, and it needs a rendering
      check in both themes rather than a unit test.
- [ ] **11.12 No browser-level e2e.** `backend/tests/e2e` drives the API and LiveKit
      directly, which is the right layer for the assistant loop but never exercises the
      React app. One Playwright journey (login → join session → submit quiz → see mark)
      would cover the seams between §4, §5 and §6.
- [x] **11.13 Dependency vulnerability scan — DONE. 22 advisories found, 20 fixed.**
      `dotnet list package --vulnerable --include-transitive` across all four .NET services,
      `npm audit` on the frontend, `pip-audit` on both Python services. Every suite re-run
      after every bump; **1,451 tests still green** (179 + 376 + 158 + 28 + 335 + 375) plus a
      clean `tsc` and production build.

      **.NET — 10 advisories, now clean in all four services.**

      - **AutoMapper 13.0.1 → 15.1.3** (High, GHSA-rvv3-g6hj-g44x, DoS via uncontrolled
        recursion). This one was not a version bump: AutoMapper 14 moved the assembly list
        onto the configuration expression, so `AddAutoMapper(Assembly)` no longer compiles,
        and it added a required `ILoggerFactory` to `MapperConfiguration`. Both composition
        roots and six test call sites were updated; UMS's five identical constructions became
        one `TestMapper.Create()` helper, matching ClassroomService's, so the next API change
        is a one-line fix rather than five.
      - **MailKit 4.15.1 → 4.17.0** (Moderate, STARTTLS response injection via an unflushed
        stream buffer). This one is not academic for us: EmailService talks to Gmail over
        STARTTLS on every message it sends.
      - **Microsoft.OpenApi 2.0.0 → 2.7.5** and **System.Security.Cryptography.Xml 9.0.0 →
        9.0.18**, both *transitive* (via `Microsoft.AspNetCore.OpenApi` and `AWSSDK.S3`) and
        so pinned directly with a comment saying why — otherwise the next person to read the
        csproj finds a package reference nothing appears to use and deletes it. Fixing the Xml
        one surfaced a *second* advisory against the version that fixed the first
        (9.0.15 → GHSA-cvvh-rhrc-wg4q), which is exactly why this is worth re-running rather
        than doing once.
      - **SQLitePCLRaw.lib.e_sqlite3 2.1.11 → 2.1.12** (High, vulnerable bundled SQLite).
        Test-only — that database never ships — but the advisory reports "no patched version"
        while 2.1.12 exists, so it needed a pin rather than a wait.

      **Frontend — 12 advisories, 10 fixed by `npm audit fix` with no `package.json` change**
      (axios, vite, postcss, form-data, js-yaml, undici, ws, brace-expansion and two low).

      **The two that remain are a deliberate decision, and npm's suggested fix is wrong.**
      `react-router` 7.18.2 is flagged by GHSA-qwww-vcr4-c8h2 — *RSC Mode CSRF bypass* —
      fixed only in **8.3.0**, a major upgrade. This app is a Vite SPA: it imports nothing but
      `react-router-dom`, uses no RSC entry points, and has no server actions for the bypass
      to reach. Meanwhile `npm audit fix --force` proposes **downgrading to 7.11.0**, which
      would reintroduce two advisories that 7.18.0 fixed (GHSA-chx6-hx7r-mcp5, unauthenticated
      DoS via inefficient route matching, and GHSA-wrjc-x8rr-h8h6, open redirect via a
      backslash in `<Link>`). Taking the suggested remedy would leave the app *less* secure
      against issues it is actually exposed to, in exchange for one it is not. Left at 7.18.2
      deliberately; revisit when React Router 8 is worth the migration on its own merits.

      **Python — both services clean**, no advisories. `pip-audit` was not installed and is
      now available in both venvs.

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
- [x] **14.2 Classify each one — DONE, and the classification was largely already made.**
      The sweep in §14.1 plus the `.env.example` work in §14.5 had already sorted things into
      exactly these three buckets; this item was mostly confirming that and writing the rule
      down where a reader will find it (see §14.8).

      - **env var** — every credential, signing key and shared secret, in `backend/.env`.
        Eleven variables, all using compose's `${VAR:?}` form.
      - **config file** — base URLs, timeouts and feature toggles, inline in each
        `docker-compose.unit.yml` next to the service they configure, or as `appsettings.json`
        / `settings.py` defaults. The two model-heavy Python services keep their own
        `.env.example` because their settings are numerous and specific.
      - **constant** — left alone. The one deliberate correction made under this item was in
        the *other* direction, in §14.4: two timeouts were constants in C# while compose
        supplied values nothing read, so they became configuration. Nothing was externalised
        just because it could be.
- [x] **14.3 Bind through typed options — DONE.** Both Python services already had a single
      `Settings` object, and ClassroomService and StreamingService already bound their sections
      (`RagServiceOptions`, `LiveAssistantOptions`, `LiveKitSettings`, `MediaOptions`,
      `UploadOptions`). **UserManagementService was the last one that did not**: it reached four
      downstream services and read all three of their settings by string index, with an inline
      default per read, spread between the composition root and each client — so the effective
      value of any of them was not discoverable from one place.

      Now one `InternalServiceOptions` shape with four bound sections, and a single
      `AddInternalHttpClient` that applies the base address, the timeout **and** the
      `X-Internal-Secret` header once at registration instead of in three separate constructors.
      A client that forgets the header produces a 401 from a service that is running perfectly,
      which is among the least obvious failures in this system to diagnose.
- [x] **14.4 Fail fast on startup — DONE, and it immediately found a live misconfiguration.**
      The four sections are `ValidateOnStart`, so a missing URL or secret refuses the boot
      naming the key rather than surfacing later as someone else's 401.

      **DEFECT FOUND: UMS never had the LiveAssistant or RagService secrets configured at all.**
      Its compose file set `ClassroomService__*` and `StreamingService__InternalApiSecret`, and
      nothing else. Both clients defaulted the secret to `""` and then omitted the header — and
      those routes fail closed on the far side by design (§7b). So **every call UMS made to
      RagService and LiveAssistantService was being refused**: the super-admin knowledge-base
      view and the assistant half of the live-session monitor were 401ing in the deployed
      configuration, presenting as empty panels rather than as an error. The missing variables
      are now in the compose file with the same `${INTERNAL_API_SECRET:?}` shape as the rest.

      Two smaller things fell out of the same work. `TimeoutSeconds` for StreamingService and
      LiveAssistant was **hard-coded to 5s in C# while compose supplied a value nothing read** —
      a setting that is provided and ignored is worse than either value, because the next person
      to change it will believe they have; the 5s now lives in the compose file where it is
      visible. And the timeout is range-checked, because `HttpClient.Timeout` throws on zero or
      negative, so a typo'd `0` used to take the service down with an `ArgumentOutOfRangeException`
      naming neither the setting nor the service it belonged to.

      **Mutation-checked (§7.8), 6 mutations.** One survived twice before the test was right:
      "a missing secret names its section" passed with no custom message at all, because the
      default DataAnnotations text contains the section name by accident of the options type
      being called `RagServiceOptions`. It now asserts the actionable half of the message —
      the key to set and the env var it must match.
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
- [x] **14.8 Document the required-vs-optional split — DONE.** A new **Configuration**
      section in the root README, built from the code rather than from memory: the three
      buckets and where each lives; a table of the eleven required variables with **what a
      mismatch actually looks like** for each (the JWT key is "every request 401s with no
      other symptom"; the internal secret is "indexing, transcripts and quiz generation stop
      silently"), since that is the information someone debugging has and a list of names is
      not; which three are additionally enforced *inside* the .NET services by `Required(...)`
      and which four sections UMS validates with `ValidateOnStart`; and the one genuinely
      optional variable, `LIVEKIT_HOST_IP`, with the Docker Desktop constraint that makes
      changing it wrong on most machines.

      A **"Before a real deployment"** list closes it: replace every `change-me` (they are
      placeholders, not defaults — the stack starts with them), give each service its own
      Postgres password, set a real `LIVEKIT_HOST_IP`, and **rotate `SMTP_APP_PASSWORD`**,
      with the reason stated plainly: a working Google App Password is in this repository's
      history, and rotating it is the only thing that invalidates it.

      Also fixed while there: the README's Tests section omitted EmailService entirely and
      listed the services in a different order from everything else, and it now points at the
      dependency-scan commands from §11.13.
- [x] **14.9 Tests — DONE**, across §7.9 and §14.3/14.4 rather than as one batch:
      - **binding and defaults** — `InternalServiceOptionsTests` (each section binds its own
        values; an unset `TimeoutSeconds` falls back to 10), `MediaOptionsTests`
        (StreamingService), `UploadLimits` tests (ClassroomService).
      - **startup failure on a missing required key** — `RequiredSettingsTests` against the
        real composition root for `Jwt:SecretKey` and both broker credentials, plus
        `InternalServiceOptionsTests` for all four downstream sections, asserting the message
        names the key to set and the env var it must match.
      - **drift, both directions** — `EnvironmentTemplateTests`: every variable a compose file
        requires appears in `.env.example`, and nothing in `.env.example` is unread. This is
        the one that keeps the file honest, and it has already caught two false positives
        worth remembering (compose's `${VAR:-default}` form, and `bin`/`obj` copies of the
        compose files being picked up by a recursive search).

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
