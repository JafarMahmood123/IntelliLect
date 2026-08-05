# IntelliLect — Test Plan

Companion to [work-plan.md](work-plan.md) §0.1. This is the **case catalogue**: what
gets tested, at which level, and in what priority order. It is deliberately not
exhaustive — an exhaustive plan for a system this size would be unmaintainable and would
hide the cases that matter. §16 records what is deliberately *not* covered, and why.

**Status of this document:** cases are designed, not executed. Containers are down, so
nothing here has been run. The `Cov` column reflects whether a test file targeting that
behaviour exists today — not whether its assertions are adequate. Mutation spot-checks
(work-plan §7.8) are what turn "covered" into "verified".

---

## 1. Priority model

Priority follows blast radius, not implementation difficulty.

| | Meaning | Rule |
| --- | --- | --- |
| **P0** | Wrong grades, data loss, security breach, or a core journey fully unavailable | Must pass before any release. A failing P0 blocks. |
| **P1** | A core journey degraded, with a workaround | Fix before the next milestone |
| **P2** | Cosmetic, rare edge, or convenience | Fix when convenient |

Grade correctness is treated as P0 throughout. This is an academic-records system: a
quiz mark that is silently wrong is worse than a service that is visibly down, because
nobody goes looking for it.

## 2. Levels — and what belongs at each

| Level | Belongs here | Does not |
| --- | --- | --- |
| **Unit** | Domain rules, scoring arithmetic, parsers, state transitions, guards | Anything needing a real DB, broker or HTTP hop |
| **Integration** | One service + its real Postgres/RabbitMQ/MinIO; cross-service contracts | Model providers, STT, LiveKit media — fake these |
| **E2E** | A whole journey across services, API level (`backend/tests/e2e` already does this for the assistant) | Anything a unit test can prove |
| **Browser E2E** | Only seams no API test can reach: rendering, focus, tab visibility, notification permission | Business rules |
| **Non-functional** | Latency budgets, load profiles, breaking point | Correctness |

The existing suite is strong at the unit level. The gaps are concentrated at
integration and above, and in one service with no tests at all.

---

## 3. Area A — Authentication & accounts (UserManagementService)

The security core, and the least-covered part of the system: only logout and 2FA have
test classes today.

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| A-01 | Registration creates the account in `Pending`, never `Active` | Unit | P0 | gap |
| A-02 | Login is refused while `Pending` — and the message does not reveal whether the account exists | Unit | P0 | gap |
| A-03 | Login is refused when `Rejected` or `Deactivated` | Unit | P0 | gap |
| A-04 | Correct credentials on an `Active` account issue a token carrying the right role claims | Unit | P0 | gap |
| A-05 | Wrong password is rejected; the response is indistinguishable from an unknown email (no user enumeration) | Unit | P0 | gap |
| A-06 | Passwords are stored hashed and salted; the hash is never returned by any endpoint or log line | Unit | P0 | gap |
| A-07 | Repeated failures trigger lockout; lockout expires | Unit | P1 | gap |
| A-08 | Password reset token is single-use, expires, and is invalidated by a successful reset | Unit | P0 | gap |
| A-09 | Reset for a non-existent email returns the same response as a real one | Unit | P1 | gap |
| A-10 | Email verification token is single-use and expiring | Unit | P1 | gap |
| A-11 | Refresh token rotates on use; a replayed old token is rejected | Unit | P0 | gap |
| A-12 | Logout invalidates the refresh token | Unit | P0 | ✓ |
| A-13 | Super admin stage 1 issues no usable session — only a 2FA challenge | Unit | P0 | ✓ |
| A-14 | Stage 2 with a correct code issues a token bearing `amr:mfa` | Unit | P0 | ✓ |
| A-15 | A token without `amr:mfa` fails the `SuperAdminTwoFactor` policy | Integration | P0 | gap |
| A-16 | 2FA code is single-use, expiring, and rate-limited against brute force | Unit | P0 | partial |
| A-17 | Expired access token is rejected; clock-skew tolerance is bounded | Integration | P1 | gap |

## 4. Area B — Authorization (cross-cutting)

**The largest hole in the suite.** Services are tested; the guards in front of them are
not. Every case here is a template applied per endpoint, not a one-off.

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| B-01 | Anonymous request to any `[Authorize]` route → 401 | Integration | P0 | gap |
| B-02 | Student calling a `[Authorize(Roles="Teacher")]` route → 403 (upload, publish quiz, close quiz, extend, change teacher) | Integration | P0 | gap |
| B-03 | Teacher calling a super-admin route → 403 | Integration | P0 | gap |
| B-04 | Non-member of a classroom cannot read its files, quizzes, recordings, summaries or Q&A | Integration | P0 | gap |
| B-05 | Teacher of classroom X cannot act on classroom Y | Integration | P0 | gap |
| B-06 | Student cannot read another student's answers, marks or submission state | Integration | P0 | gap |
| B-07 | IDOR sweep: substituting another tenant's GUID into every `{id}` route param is refused, and returns 403/404 without confirming existence | Integration | P0 | gap |
| B-08 | `/api/internal/*` with a missing `X-Internal-Secret` → 401 | Integration | P0 | gap |
| B-09 | `/api/internal/*` with a wrong secret → 401 | Integration | P0 | gap |
| B-10 | Internal routes are not reachable through nginx from outside | Integration | P0 | gap |
| B-11 | Role change takes effect on the next token, and an in-flight old token cannot exceed its new rights on sensitive routes | Integration | P1 | gap |

## 5. Area C — User administration & bulk accept/reject (work-plan §2)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| C-01 | Each legal transition applies: Pending→Active, Pending→Rejected, Active→Deactivated, Deactivated→Active | Unit | P0 | ✓ |
| C-02 | Illegal transitions are refused (e.g. Rejected→Deactivated, Active→Active via Accept) | Unit | P0 | partial |
| C-03 | **Bulk**: a mixed batch returns a per-item result — successes are applied, failures named | Unit | P0 | new |
| C-04 | **Bulk**: one invalid ID does not roll back the other 199 | Unit | P0 | new |
| C-05 | **Bulk**: authorization is evaluated per user, not once for the batch | Unit | P0 | new |
| C-06 | **Bulk**: replaying the same batch (retry after timeout) is idempotent — no second status change, no second email | Unit | P0 | new |
| C-07 | **Bulk**: a batch over the configured cap is refused with a clear error | Unit | P1 | new |
| C-08 | **Bulk**: an empty selection is refused, not treated as "all" | Unit | P0 | new |
| C-09 | **Bulk**: one audit record per user, not one per batch | Unit | P1 | new |
| C-10 | **Bulk**: N users produce N notifications, dispatched without blocking the response | Integration | P1 | new |
| C-11 | **Bulk**: a notification failure does not roll back an already-applied status change | Integration | P0 | new |
| C-12 | Accepting an already-active user is a no-op, not an error and not a re-notify | Unit | P0 | new |
| C-13 | UI: select-all-on-page selects only the page; the count in the confirm dialog matches what is sent | Frontend | P1 | new |
| C-14 | UI: partial failure is surfaced per row, not as a single generic error | Frontend | P1 | new |

## 6. Area D — Classrooms & membership

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| D-01 | Creating a classroom assigns the creating teacher | Unit | P0 | ✓ |
| D-02 | Enrol / remove a student; duplicate enrolment is rejected or idempotent | Unit | P1 | ✓ |
| D-03 | Changing the teacher transfers ownership and revokes the old teacher's write access | Unit | P0 | ✓ |
| D-04 | Deleting a classroom cascades: files, sessions, quizzes, recordings, summaries, and the RAG index | Integration | P0 | ✓ (unit) |
| D-05 | A partially-failed cascade leaves no orphan (deleted index but surviving rows, or vice versa) | Integration | P0 | gap |
| D-06 | A removed student immediately loses read access to classroom content | Integration | P0 | gap |

## 7. Area E — File upload, size limits & indexing (work-plan §13)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| E-01 | A file at exactly the configured limit is accepted | Unit | P0 | new |
| E-02 | One byte over the limit is refused with a typed error, not a raw 413 page | Integration | P0 | new |
| E-03 | Over-size is refused on `Content-Length`, before the body is buffered | Integration | P0 | new |
| E-04 | A zero-byte file is refused | Unit | P1 | new |
| E-05 | A disallowed content type is refused even when under the size limit | Unit | P0 | new |
| E-06 | Extension/content-type mismatch (a `.pdf` that is not a PDF) is refused by the extractor rather than crashing it | Unit | P1 | partial |
| E-07 | **nginx `client_max_body_size` is ≥ the app limit** — otherwise nginx rejects first with unparseable HTML | Integration | P0 | new |
| E-08 | ~~RAG ingest enforces the same limit~~ — **void**: `ingest_document` takes an `s3_key` and fetches the object itself; there is no upload endpoint there. Replaced by: ingestion of an object larger than the configured limit is refused before extraction | Unit | P2 | not built |
| E-09 | The limit reaches the browser as configuration; the UI never hardcodes it | Frontend | P1 | ✓ |
| E-10 | Frontend pre-flight rejects an over-size file before the request starts | Frontend | P2 | ✓ |
| E-11 | `SizeBytes` is recorded correctly and rendered human-readably in the teacher's file list | Frontend | P1 | ✓ |
| E-15 | A failed limits fetch degrades to server-side enforcement, not a blocked upload button | Frontend | P1 | ✓ |
| E-16 | The `accept` attribute narrows the picker, and the JS guard still refuses when it is bypassed | Frontend | P2 | ✓ |
| E-12 | A rejected upload leaves no orphaned S3 object and no DB row | Integration | P0 | gap |
| E-13 | Indexing status transitions are observable and terminal on failure (never stuck "in progress" forever) | Unit | P1 | ✓ |
| E-14 | Upload succeeds but indexing fails → the file is still listed, flagged as unindexed | Integration | P1 | ✓ (unit) |

## 8. Area F — RAG service: extract → chunk → embed → retrieve

Well covered at unit level already. New cases target the seams and the Arabic question.

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| F-01 | PDF / DOCX / PPTX / TXT extraction, including a scanned PDF routed to OCR | Unit | P0 | ✓ |
| F-02 | Corrupt, empty and password-protected documents fail cleanly with a typed error | Unit | P1 | ✓ |
| F-03 | Structural and semantic chunkers produce non-empty, bounded, non-overlapping-beyond-config chunks | Unit | P0 | ✓ |
| F-04 | Embedding dimension guard rejects a vector of the wrong size | Unit | P0 | ✓ |
| F-05 | pgvector search returns results ordered by score | Unit | P0 | ✓ |
| F-06 | Retrieval below the min-score cutoff returns nothing rather than a weak match | Unit | P0 | ✓ |
| F-07 | Search is scoped to one classroom — never returns another classroom's chunks | Integration | P0 | gap |
| F-08 | Deleting a classroom's index removes every vector; a later search returns nothing | Integration | P0 | ✓ (unit) |
| F-09 | Re-ingesting the same document replaces rather than duplicates its chunks | Integration | P1 | gap |
| F-10 | Ingestion worker resumes/retries after a crash mid-document without half-indexing | Integration | P1 | partial |
| F-11 | **Arabic**: an Arabic query against Arabic material retrieves above the cutoff (work-plan P3 — the open unknown) | Integration | P0 | gap |
| F-12 | Outbox: a message is published exactly once per committed transaction, and survives a broker outage | Unit | P0 | ✓ |

## 9. Area G — Live session, media & recording (StreamingService)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| G-01 | Token issuance encodes the correct role in metadata; a student token cannot publish as teacher | Unit | P0 | partial |
| G-02 | A non-member cannot obtain a token for a session | Integration | P0 | gap |
| G-03 | LiveKit webhook signature is verified; a forged webhook is rejected | Unit | P0 | ✓ |
| G-04 | Recording start/stop toggles produce exactly one egress; a double-start does not | Unit | P0 | ✓ |
| G-05 | Egress reconciler recovers a recording orphaned by a crash | Unit | P1 | ✓ |
| G-06 | A completed recording lands in MinIO with non-zero size, and the DB points at the real key | Integration | P0 | partial |
| G-07 | Media settings reach the browser (adaptiveStream, dynacast, framerate, retries) | Frontend | P1 | ✓ |
| G-08 | Brief disconnect → reconnect keeps the user in the session (does not bounce to the classroom page) | Browser E2E | P0 | ✓ (unit) |
| G-09 | Ending the session properly still ejects everyone — the same change touches both paths | Browser E2E | P0 | ✓ (unit) |
| G-10 | Session end triggers transcript persistence and summary generation | Integration | P0 | ✓ (unit) |

## 10. Area H — Live assistant & feedback colours (work-plan §3)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| H-01 | Boundary detector splits on drift above threshold, not on a pause | Unit | P0 | ✓ |
| H-02 | Code-switching (Arabic speech, English terms) does not trigger a false split | Unit | P1 | ✓ |
| H-03 | Retrieval returning nothing above cutoff yields **silence**, not a fabricated correction | Unit | P0 | ✓ |
| H-04 | A contradiction of indexed material yields a discrepancy card | Integration | P0 | ✓ (e2e) |
| H-05 | Pacing: no more than one card per configured interval; low-confidence cards are dropped | Unit | P0 | ✓ |
| H-06 | Feedback is published only to the teacher, never to students | Unit | P0 | ✓ |
| H-07 | **New contract**: the payload carries a severity and the wrong/corrected spans as separate fields | Unit | P0 | ✓ |
| H-08 | **New contract**: a model response missing those fields degrades to a plain card rather than crashing the parser | Unit | P0 | ✓ |
| H-09 | Each severity renders its colour, *and* a non-colour cue (icon/label) | Frontend | P0 | ✓ |
| H-10 | Contrast passes in both light and dark themes | Frontend | P1 | new |
| H-11 | "Likely to be" replaces "unclear" in the enum, prompt vocabulary and both locales | Unit + Frontend | P1 | ✓ |
| H-12 | STT failure or model timeout degrades to silence, never to a crashed session | Unit | P0 | partial |
| H-13 | A quoted span the teacher never said is discarded, and the suggestion survives without it | Unit | P0 | ✓ |

## 11. Area I — Quizzes (work-plan §4)

The richest domain in the system, and the one where a bug is most expensive. Note the
architecture already decides several of these: `ClosesAtUtc` is the authority (not
`Status`), `IsCorrect`/`PointsAwarded` are snapshotted at answer time, and uniqueness is
DB-arbitrated on `(QuestionId, StudentId)` and `(QuizId, StudentId)`.

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| I-01 | A draft is invisible to students until published | Unit | P0 | ✓ |
| I-02 | Only a `Draft` is editable; editing an `Open`/`Closed`/`Cancelled` quiz is refused | Unit | P0 | ✓ |
| I-03 | Publish stamps `ClosesAtUtc` from the sum of question time limits | Unit | P0 | ✓ |
| I-04 | Composer limits enforced server-side (max questions, min/max answers, max duration) — not merely offered by the UI | Unit | P0 | ✓ |
| I-05 | An answer after `ClosesAtUtc` is refused **even if `Status` is still `Open`** (a missed status flip must not extend scoring) | Unit | P0 | ✓ |
| I-06 | An answer at exactly the deadline boundary — pick the rule and pin it | Unit | P0 | partial |
| I-07 | Changing an answer before submitting replaces it; the unique index holds under two concurrent writes | Integration | P0 | partial |
| I-08 | After `SubmitQuiz`, answers are frozen — a later change is refused | Unit | P0 | ✓ |
| I-09 | `SubmitQuiz` is idempotent: a double-click returns the existing submission, not a conflict | Unit | P0 | ✓ |
| I-10 | A student who never submits still keeps every mark they earned | Unit | P0 | ✓ |
| I-11 | Score = sum of snapshotted `PointsAwarded`; editing a question afterwards does not change a recorded mark | Unit | P0 | ✓ |
| I-12 | Unanswered questions score zero and do not reduce `TotalPoints` | Unit | P0 | ✓ |
| I-13 | A `Cancelled` quiz preserves every answer row but is excluded from all totals | Unit | P0 | ✓ |
| I-14 | Exclusion of cancelled quizzes happens in exactly one place — no half-applied variant across teacher, student and session views | Unit | P0 | ✓ |
| I-15 | An extension is absolute, never earlier than the class deadline; granting twice replaces rather than compounds | Unit | P0 | ✓ |
| I-16 | An extended student may answer after the class deadline; a non-extended student may not | Unit | P0 | ✓ |
| I-17 | Deadline sweeper closes an expired quiz exactly once, and is safe to run concurrently with a submission | Integration | P0 | partial |
| I-18 | Extension granted while the sweeper is closing the quiz — pin the winner | Integration | P1 | gap |
| I-19 | A student cannot see correct answers before the quiz closes (`IsCorrect` nullable in `MyAnswerDto` — verify it is actually null pre-close) | Integration | P0 | ✓ |
| I-20 | A student cannot INFER correctness from their own totals while a quiz is open — tracking score, available marks and class average all exclude open quizzes | Unit | P0 | ✓ |
| I-20 | `RespondentCount`/`SubmittedCount` are accurate under concurrent answering | Integration | P1 | partial |
| I-21 | Generated draft goes through the identical validation and publish path as a hand-written one | Unit | P0 | ✓ |
| I-22 | Generation is authorized *before* the model is called | Unit | P1 | ✓ |
| I-23 | A malformed model response fails generation without persisting a broken draft | Unit | P0 | ✓ |

## 12. Area J — Ranking (work-plan §6)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| J-01 | Ranking uses only quizzes that count (cancelled excluded) | Unit | P0 | new |
| J-02 | Ties resolve by the documented rule, deterministically across repeated calls | Unit | P0 | new |
| J-03 | A student with no submissions appears with zero rather than vanishing | Unit | P1 | new |
| J-04 | A student who joined late is ranked on available quizzes, not penalised for ones predating them — decide and pin | Unit | P0 | new |
| J-05 | Visibility rule enforced server-side: a student cannot fetch the full leaderboard if it is teacher-only | Integration | P0 | new |
| J-06 | Ranked query does not N+1 across submissions | Integration | P1 | new |

## 13. Area K — In-session notifications (work-plan §5)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| K-01 | A chat message while the sidebar is closed increments the unread badge | Frontend | P1 | new |
| K-02 | Your own message never notifies you | Frontend | P0 | new |
| K-03 | No notification when the relevant panel is already open and focused | Frontend | P1 | new |
| K-04 | A published quiz notifies every student in the session, once | Frontend | P0 | new |
| K-05 | Unread count clears on open and does not resurrect on re-render | Frontend | P1 | new |
| K-06 | Backgrounded tab: title/favicon reflects unread (visibilitychange handling) | Browser E2E | P1 | new |
| K-07 | Denied browser-notification permission degrades to in-app only, no crash, no repeated prompting | Frontend | P0 | new |
| K-08 | Mute suppresses notifications without suppressing the messages themselves | Frontend | P2 | new |

## 14. Area L — Cross-service messaging & consistency

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| L-01 | Every consumer is idempotent: the same message twice produces one effect (at-least-once delivery makes this inevitable, not hypothetical) | Unit | P0 | gap |
| L-02 | MassTransit envelope round-trips between the .NET and Python services | Unit | P0 | ✓ |
| L-03 | Outbox message survives a service restart between commit and publish | Integration | P0 | partial |
| L-04 | A consumer throwing does not lose the message (retry/DLQ observable) | Integration | P0 | gap |
| L-05 | Recording-ready and summary-ready consumers tolerate arriving out of order | Unit | P1 | ✓ |
| L-06 | An internal HTTP call timing out degrades the caller gracefully rather than cascading | Unit | P1 | partial |

## 15. Area M — Configuration, migrations & deployment (work-plan §14)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| M-01 | Settings bind correctly; documented defaults apply when a key is absent | Unit | P0 | new |
| M-02 | A missing **required** setting fails at startup, not on first request | Integration | P0 | new |
| M-03 | Drift test: every key the code reads appears in `.env.example` | Unit | P1 | new |
| M-04 | `.env.example` contains no real secret | Unit | P0 | new |
| M-05 | EF migrations apply cleanly to an empty database | Integration | P0 | gap |
| M-06 | Alembic upgrades **and downgrades** without loss | Integration | P0 | gap |
| M-07 | An embedding-dimension change migrates the pgvector column and forces re-embedding rather than silently mismatching | Integration | P0 | gap |
| M-08 | Health endpoints report unhealthy when a dependency is down (not a blind 200) | Integration | P1 | partial |
| M-09 | en and ar locale files have identical key sets | Frontend | P1 | new |
| M-10 | RTL layout renders without overflow on the main pages | Frontend | P2 | partial |

## 16. Deliberately not covered — and why

Stating this is part of the plan. An unstated gap reads as an oversight; a stated one is
a decision.

- **Third-party correctness.** LiveKit's SFU, MinIO's storage, Postgres, RabbitMQ. We
  test our *use* of them, not them.
- **Model output quality.** Whether Gemini writes a *good* quiz question, or phrases
  feedback well, is not machine-assertable. We test the contract around the model:
  parsing, degradation, authorization, pacing, and silence-on-no-retrieval.
- **STT transcription accuracy.** Word error rate depends on the provider and the
  speaker. We test that a transcript flows through the pipeline, not what it says.
- **Multi-participant media behaviour.** LiveKit is loopback-only in this environment, so
  adaptiveStream's *benefit* cannot be measured (work-plan P1). Its configuration can be.
- **Browser matrix.** One engine (Chromium) only. Cross-browser is out of scope for a
  graduation project.
- **Full accessibility audit.** Targeted a11y assertions on the feedback cards (H-09,
  H-10) only, since colour is being made load-bearing there.
- **Penetration testing.** The authorization cases in Area B are correctness tests, not a
  security audit. Dependency scanning (work-plan §11.13) is the cheap partial substitute.
- **Load beyond a single host.** Everything runs on one machine; numbers from §9–§10 are
  comparative and directional, not capacity planning.

## 17. Entry / exit criteria

**Entry** — before a suite is considered runnable: containers up, migrations applied,
seed data present, `.env` complete, and for the assistant path a working model/STT key
(or fakes selected).

**Exit** — before the work in [work-plan.md](work-plan.md) is called done:

1. Every **P0** case above exists and passes.
2. Per-service line coverage ≥ 85% after the agreed exclusions.
3. Mutation spot-check passes on quiz scoring and the auth path — the two places where
   a test that never fails would be most dangerous.
4. No P0 case is marked "not implemented" without a written reason here.
5. The smoke suite passes against a fresh `docker compose up` from a clean volume.

## 18. Traceability

| Area | Work-plan section |
| --- | --- |
| A, B | §11.2, §11.4, §11.5 |
| C | §2 |
| D | §8.3 |
| E | §13 |
| F | §8.3, P3 |
| G | §9, P1 |
| H | §3, P2 |
| I | §4 |
| J | §6 |
| K | §5 |
| L | §11.6 |
| M | §14, §11.8, §11.9 |
