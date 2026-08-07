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
| A-01 | Registration creates the account in `Pending`, never `Active` | Unit | P0 | ✓ |
| A-02 | Login is refused while `Pending`; the status message sits BEHIND the credential check, so it is not an enumeration oracle | Unit | P0 | ✓ |
| A-03 | Login is refused when `Rejected` or `Deactivated` | Unit | P0 | ✓ |
| A-04 | Correct credentials on an `Active` account issue a token carrying the right role claims | Unit | P0 | ✓ |
| A-05 | Wrong password is rejected; the response is indistinguishable from an unknown email (no user enumeration) | Unit | P0 | ✓ |
| A-06 | Passwords are stored hashed; the plaintext never reaches the entity | Unit | P0 | ✓ (hashing verified; "never returned by any endpoint" still needs the response-shape sweep) |
| A-07 | Repeated failures trigger lockout; lockout expires | Unit | P1 | ✓ (there was no lockout to test — the feature was built here, `AuthServiceLockoutTests`) |
| A-08 | Password reset token is single-use, expires, and is invalidated by a successful reset | Unit | P0 | ✓ (13 cases; found that the code was logged in plaintext and that a reset left every session alive) |
| A-23 | **A successful password reset revokes every existing session** — the whole point of resetting a credential you believe is compromised | Unit | P0 | ✓ |
| A-24 | The reset code is never written to a log, and what is stored is not the code itself | Unit | P0 | ✓ |
| A-25 | A refused reset leaves both the sessions and the outstanding code untouched — otherwise guessing wrong is a denial of service | Unit | P0 | ✓ |
| A-26 | The daily reset cap starts over after a day, giving a whole new allowance rather than one request | Unit | P1 | ✓ |
| A-09 | Reset for a non-existent email returns the same response as a real one | Unit | P1 | ✓ |
| A-10 | Email verification token is single-use and expiring | Unit | P1 | n/a — **there is no email-verification step in this system.** Registration lands in `Pending` and a human administrator approves it (A-01), which is a stronger gate than a mailed link and is the one the product actually uses. Listed as a `gap` until now, which read as "tests missing" rather than "feature absent". Residual risk, stated rather than tested away: an account can be registered against an address its owner never sees, and the administrator approving it has no signal that the address was proven. |
| A-27 | A locked account answers exactly like a wrong password and an unknown email — the lock is not an enumeration oracle | Unit | P0 | ✓ |
| A-28 | The lock is enforced BEFORE the password is checked, so no reply can vary with the password | Unit | P0 | ✓ (asserted on the hasher, not on the message) |
| A-29 | Attempts made during a lock do not extend it — a stranger cannot hold one account shut indefinitely | Unit | P1 | ✓ |
| A-30 | An expired lock returns the whole allowance, not one attempt | Unit | P1 | ✓ |
| A-31 | A correct password clears the run of failures | Unit | P1 | ✓ |
| A-32 | Each failed attempt is persisted, and an unknown email persists nothing | Unit | P1 | ✓ |
| A-33 | A locked super admin is sent no verification code | Unit | P1 | ✓ |
| A-11 | Refresh token rotates on use; a replayed old token is rejected | Unit | P0 | ✓ |
| A-12 | Logout invalidates the refresh token | Unit | P0 | ✓ |
| A-13 | Super admin stage 1 issues no usable session — only a 2FA challenge | Unit | P0 | ✓ |
| A-14 | Stage 2 with a correct code issues a token bearing `amr:mfa` | Unit | P0 | ✓ |
| A-15 | A token without `amr:mfa` fails the `SuperAdminTwoFactor` policy | Integration | P0 | gap |
| A-16 | 2FA code is single-use, expiring, and rate-limited against brute force | Unit | P0 | ✓ (`AuthServiceTwoFactorTests`: expiry deletes the challenge, a wrong code increments attempts, max attempts invalidates it, a valid one is single-use) |
| A-17 | Expired access token is rejected; clock-skew tolerance is bounded | Integration | P1 | gap |
| A-18 | An expired refresh token is refused | Unit | P0 | ✓ |
| A-19 | A rejected or deactivated account cannot RENEW a session, independently of revocation having run | Unit | P0 | ✓ |
| A-20 | A super admin's rotated token keeps its `amr:mfa` marking | Unit | P0 | ✓ |
| A-21 | Self-registration cannot select a privileged role | Unit | P0 | ✓ |
| A-22 | The registration outbox row is published before the account commits | Unit | P0 | ✓ |

## 4. Area B — Authorization (cross-cutting)

**The largest hole in the suite.** Services are tested; the guards in front of them are
not. Every case here is a template applied per endpoint, not a one-off.

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| B-01 | Anonymous request to any `[Authorize]` route → 401 | Integration | P0 | ✓ (unit rule) — every controller either requires authentication or is on a named, reasoned exemption list; the list is checked for stale and unnecessary entries in both directions |
| B-02 | Student calling a `[Authorize(Roles="Teacher")]` route → 403 (upload, publish quiz, close quiz, extend, change teacher) | Integration | P0 | ✓ (unit rule) — every state-changing action decides authorization somewhere explicit: a role attribute, or a `Controller.Action` entry naming the service that checks it |
| B-03 | Teacher calling a super-admin route → 403 | Integration | P0 | gap |
| B-04 | Non-member of a classroom cannot read its files, quizzes, recordings, summaries or Q&A | Integration | P0 | gap |
| B-05 | Teacher of classroom X cannot act on classroom Y | Integration | P0 | gap |
| B-06 | Student cannot read another student's answers, marks or submission state | Integration | P0 | gap |
| B-22 | The classroom's teacher cannot take part in their own quiz — including when also enrolled as a student | Unit | P0 | ✓ (defect found & fixed) |
| B-07 | IDOR sweep: substituting another tenant's GUID into every `{id}` route param is refused, and returns 403/404 without confirming existence | Integration | P0 | gap |
| B-08 | `/api/internal/*` with a missing `X-Internal-Secret` → 401 | Unit | P0 | ✓ (filter + conformance rule, ClassroomService & StreamingService) |
| B-09 | `/api/internal/*` with a wrong secret → 401 | Unit | P0 | ✓ |
| B-10 | Internal routes are not reachable through nginx from outside | Integration | P0 | authored, unrun — `tests/e2e/test_internal_surface_contract.py`, 55 tests over 18 routes; probes in-network so it tests the guard rather than nginx's route table |
| B-11 | Role change takes effect on the next token, and an in-flight old token cannot exceed its new rights on sensitive routes | Integration | P1 | gap |
| B-14 | Client route guards: a disallowed role is redirected, not shown the page; matching is exact (no substring, no case-insensitive) and an empty allow-list admits nobody | Frontend | P1 | ✓ |
| B-15 | Logout clears both tokens AND the persisted store copy, even when the server-side revocation fails | Frontend | P0 | ✓ |
| B-16 | **Several requests expiring together share ONE refresh** — the backend rotates refresh tokens, so a refresh per request signs the user out despite the refresh succeeding | Frontend | P0 | ✓ (defect found & fixed) |
| B-17 | A 401 refresh retries the original request with the new token; a non-401 never triggers a refresh | Frontend | P0 | ✓ |
| B-18 | A failed **login** is exempt from refresh — a wrong password must not redirect the user away from the form | Frontend | P0 | ✓ |
| B-19 | The `_retry` guard prevents an infinite refresh loop when the retried request also 401s | Frontend | P0 | ✓ |
| B-20 | A rejected refresh clears both tokens *and* `auth-storage`, then redirects to `/login` | Frontend | P0 | ✓ |
| B-21 | Error messages prefer the server's `detail`, then a validation message, then the generic title; whitespace counts as blank | Frontend | P1 | ✓ |
| B-12 | The internal guard **fails closed**: an unconfigured secret refuses every call rather than admitting all of them | Unit | P0 | ✓ |
| B-13 | Every controller serving `api/internal` carries the guard — a rule over the assembly, so a new one cannot ship without it | Unit | P0 | ✓ |

## 5. Area C — User administration & bulk accept/reject (work-plan §2)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| C-01 | Each legal transition applies: Pending→Active, Pending→Rejected, Active→Deactivated, Deactivated→Active | Unit | P0 | ✓ |
| C-02 | Illegal transitions are refused (e.g. Rejected→Deactivated, Active→Active via Accept) | Unit | P0 | ✓ (`UserStatusTransitionMatrixTests`; the hand-written lists covered 13 of 16 combinations and the plan's own example, Rejected→Deactivated, was one of the three missing) |
| C-15 | **All sixteen** combinations of status × action behave as declared, driven from the enums rather than a list | Unit | P0 | ✓ |
| C-16 | The bulk path agrees with the single path on every one of the sixteen — a bulk endpoint more permissive than the single one lets fifty clicks do what one cannot | Unit | P0 | ✓ |
| C-17 | Sessions end exactly when the DESTINATION is Rejected or Deactivated, never on approve or reactivate | Unit | P0 | ✓ |
| C-18 | Adding a `UserStatus` or `UserStatusAction` fails the suite until its rules are decided — otherwise `IsValidSource`'s `_ => false` refuses it silently everywhere | Unit | P1 | ✓ |
| C-03 | **Bulk**: a mixed batch returns a per-item result — successes are applied, failures named | Unit | P0 | ✓ (`Bulk_MixedBatch_AppliesTheValidOnesAndNamesTheFailures`) |
| C-04 | **Bulk**: one invalid ID does not roll back the other 199 | Unit | P0 | ✓ |
| C-05 | **Bulk**: authorization is evaluated per user, not once for the batch | Unit | P0 | ✓ (`Bulk_SelfTargeting_FailsOnlyThatAccount`) |
| C-06 | **Bulk**: replaying the same batch (retry after timeout) is idempotent — no second status change, no second email | Unit | P0 | ✓ (`UserStatusRetryTests`, S-14) |
| C-07 | **Bulk**: a batch over the configured cap is refused with a clear error | Unit | P1 | ✓ |
| C-08 | **Bulk**: an empty selection is refused, not treated as "all" | Unit | P0 | ✓ |
| C-09 | **Bulk**: one audit record per user, not one per batch | Unit | P1 | ✓ (`UserStatusAuditTests`; there was no audit record of any kind — the service had no logger) |
| C-19 | A refusal is recorded, and at Warning — a run of them is somebody attempting what they may not | Unit | P1 | ✓ |
| C-20 | The early self-target return records too; the shortcut that saves a query was skipping the audit | Unit | P1 | ✓ |
| C-21 | A no-op records nothing — a retried page of them would bury the changes that did happen | Unit | P1 | ✓ |
| C-22 | Accounts are identified by id, never by email or name (A-24's rule, different sink) | Unit | P0 | ✓ |
| C-10 | **Bulk**: N users produce N notifications, dispatched without blocking the response | Integration | P1 | new |
| C-11 | **Bulk**: a notification failure does not roll back an already-applied status change | Integration | P0 | new |
| C-12 | Accepting an already-active user is a no-op, not an error and not a re-notify | Unit | P0 | ✓ (`Bulk_AlreadyInTargetState_IsASuccessfulNoOp_WithNoSecondNotification`, and C-15's matrix) |
| C-13 | UI: select-all-on-page selects only the page; the count in the confirm dialog matches what is sent | Frontend | P1 | ✓ (`AdminDashboard.bulk.test.tsx`, `UsersDirectoryPage.bulk.test.tsx`) |
| C-14 | UI: partial failure is surfaced per row, not as a single generic error | Frontend | P1 | ✓ (both suites: the reason stays on screen and the failed rows stay selected for retry) |

## 6. Area D — Classrooms & membership

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| D-01 | Creating a classroom assigns the creating teacher | Unit | P0 | ✓ |
| D-02 | Enrol / remove a student; duplicate enrolment is rejected or idempotent | Unit | P1 | ✓ |
| D-03 | Changing the teacher transfers ownership and revokes the old teacher's write access | Unit | P0 | ✓ |
| D-04 | Deleting a classroom cascades: files, sessions, quizzes, recordings, summaries, and the RAG index | Integration | P0 | ✓ (unit) |
| D-05 | A partially-failed cascade leaves no orphan (deleted index but surviving rows, or vice versa) | Integration | P0 | gap |
| D-06 | A removed student immediately loses read access to classroom content | Integration | P0 | gap |
| D-07 | Only the classroom's own teacher may remove a student; ownership is checked *before* the membership lookup, so the two failures stay indistinguishable | Unit | P0 | ✓ |
| D-08 | Enrolment is scoped to the (classroom, student) pair — being in one classroom does not block joining another | Unit | P1 | ✓ |
| D-09 | Enrolment and removal are persisted, not merely tracked | Unit | P0 | ✓ |
| D-10 | The roster maps successfully for a classroom that has members (positional-record constructor mapping) | Unit | P0 | ✓ |
| D-11 | The AutoMapper profile validates as a whole, so a broken map fails a test rather than an endpoint | Unit | P0 | ✓ |
| D-12 | `TeacherId` is never mapped from the create request body — ownership comes from the authenticated caller | Unit | P0 | ✓ |

## 7. Area E — File upload, size limits & indexing (work-plan §13)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| E-01 | A file at exactly the configured limit is accepted | Unit | P0 | ✓ (`ClassroomFileUploadLimitsTests`) |
| E-02 | One byte over the limit is refused with a typed error, not a raw 413 page | Integration | P0 | ✓ (unit half: `One_byte_over_the_maximum_is_refused`); the typed error as seen over HTTP still needs a host |
| E-03 | Over-size is refused on `Content-Length`, before the body is buffered | Integration | P0 | new |
| E-04 | A zero-byte file is refused | Unit | P1 | ✓ |
| E-05 | A disallowed content type is refused even when under the size limit | Unit | P0 | ✓ (plus: content-type parameters do not defeat the allow list, and extension is an ALTERNATIVE signal, not an addition) |
| E-06 | Extension/content-type mismatch (a `.pdf` that is not a PDF) is refused by the extractor rather than crashing it | Unit | P1 | ✓ (`test_format_mismatch.py`, full 4×5 matrix; three of twelve pairs did not crash — they succeeded, which is worse) |
| E-17 | **An image sent as `application/pdf` is refused, not extracted as an empty document** — PyMuPDF opens it and `filetype="pdf"` does not stop it | Unit | P0 | ✓ |
| E-18 | **A binary renamed `.txt` is refused, not decoded into paragraphs of noise and embedded** — the NUL-byte guard misses a NUL-free PDF | Unit | P0 | ✓ |
| E-19 | A document that yields no chunks is FAILED, never marked indexed with nothing behind it | Unit | P0 | ✓ |
| E-20 | Bytes with no recognisable signature always proceed — sniffing can refuse a file, never accept one it otherwise would not | Unit | P0 | ✓ |
| E-21 | A corrupt file of the RIGHT format stays a `CorruptFileError`, not an "unsupported format" — "broken file" and "wrong file" mean different things to whoever reads the status | Unit | P1 | ✓ |
| E-22 | A legacy `.doc`/`.ppt` (OLE2) is named as such rather than reported as a damaged package | Unit | P2 | ✓ |
| E-07 | **nginx `client_max_body_size` is ≥ the app limit** — otherwise nginx rejects first with unparseable HTML | Integration | P0 | new |
| E-08 | ~~RAG ingest enforces the same limit~~ — **void**: `ingest_document` takes an `s3_key` and fetches the object itself; there is no upload endpoint there. Replaced by: ingestion of an object larger than the configured limit is refused before extraction | Unit | P2 | not built |
| E-09 | The limit reaches the browser as configuration; the UI never hardcodes it | Frontend | P1 | ✓ |
| E-10 | Frontend pre-flight rejects an over-size file before the request starts | Frontend | P2 | ✓ |
| E-11 | `SizeBytes` is recorded correctly and rendered human-readably in the teacher's file list | Frontend | P1 | ✓ |
| E-15 | A failed limits fetch degrades to server-side enforcement, not a blocked upload button | Frontend | P1 | ✓ |
| E-16 | The `accept` attribute narrows the picker, and the JS guard still refuses when it is bypassed | Frontend | P2 | ✓ |
| E-12 | A rejected upload leaves no orphaned S3 object and no DB row | Integration | P0 | ✓ (unit: `Rejected_upload_writes_no_storage_object_and_no_row`); the real-MinIO half still needs containers |
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
| F-06 | Retrieval below the min-score cutoff returns nothing rather than a weak match | Unit | P0 | **n/a — no cutoff exists** |
| F-07 | Search is scoped to one classroom — never returns another classroom's chunks | Integration | P0 | gap |
| F-08 | Deleting a classroom's index removes every vector; a later search returns nothing | Integration | P0 | ✓ (unit) |
| F-09 | Re-ingesting the same document replaces rather than duplicates its chunks | Integration | P1 | gap |
| F-10 | Ingestion worker resumes/retries after a crash mid-document without half-indexing | Integration | P1 | partial |
| F-11 | **Arabic**: an Arabic query against Arabic material retrieves above the cutoff (work-plan P3 — the open unknown) | Integration | P0 | gap |
| F-12 | Outbox: a message is published exactly once per committed transaction, and survives a broker outage | Unit | P0 | ✓ |
| F-13 | Documents embed as `RETRIEVAL_DOCUMENT` and queries as `RETRIEVAL_QUERY`; the qwen instruction never reaches Gemini | Unit | P0 | ✓ |
| F-14 | A batch's vectors come back in the order of the texts they came from, even when completion order differs | Unit | P0 | ✓ |
| F-15 | A truncated (Matryoshka) vector is L2-normalised before it is stored | Unit | P0 | ✓ |
| F-16 | The embedding fan-out is bounded by the configured concurrency | Unit | P1 | ✓ |
| F-17 | An embedding failure fails the whole batch rather than returning a short, misaligned list | Unit | P0 | ✓ |
| F-18 | Embedder errors name the cause an operator can act on (rejected key, rate limit, unpulled model, unreachable host) | Unit | P1 | ✓ |
| F-19 | Re-embed refuses a mismatched embedder on a probe vector *before* embedding anything | Unit | P0 | ✓ |
| F-20 | Re-embed is resumable from `embedding IS NULL`: a re-run picks up only what is missing and never re-embeds | Unit | P0 | ✓ |
| F-21 | A second concurrent re-embed sweep is refused (409), never queued; a failed sweep leaves a readable reason | Unit | P0 | ✓ |
| F-22 | The character wrap for an unsplittable token loses and duplicates nothing | Unit | P0 | ✓ |
| F-23 | The overlap seed shrinks so the incoming atom still fits — the token cap outranks the overlap | Unit | P0 | ✓ |
| F-24 | A merged trailing runt does not duplicate the overlap atoms it shares with its predecessor | Unit | P1 | ✓ |
| F-25 | An unrecognised `CHUNKING_STRATEGY` falls back to the offline chunker rather than one needing a live model | Unit | P1 | ✓ |
| F-26 | An empty page produces no chunk and no embedding call; a one-sentence slide skips the model entirely | Unit | P1 | ✓ |

**Note on F-06.** There is no min-score cutoff in the code, and the ✓ this row carried was
wrong. Grounding is done at the prompt instead: `AnswerService` does not call the model at all
when retrieval is empty, and the system prompt instructs a refusal when the context does not
contain the answer. A numeric threshold needs calibrating against a real corpus and a live
embedder — §8 work, not a unit test — and F-11 (Arabic) depends on the same calibration.

## 9. Area G — Live session, media & recording (StreamingService)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| G-01 | Token issuance encodes the correct role in metadata; a student token cannot publish as teacher | Unit | P0 | ✓ |
| G-02 | A non-member cannot obtain a token for a session | Integration | P0 | gap |
| G-03 | LiveKit webhook signature is verified; a forged webhook is rejected | Unit | P0 | ✓ |
| G-04 | Recording start/stop toggles produce exactly one egress; a double-start does not | Unit | P0 | ✓ |
| G-05 | Egress reconciler recovers a recording orphaned by a crash | Unit | P1 | ✓ |
| G-06 | A completed recording lands in MinIO with non-zero size, and the DB points at the real key | Integration | P0 | partial |
| G-07 | Media settings reach the browser (adaptiveStream, dynacast, framerate, retries) | Frontend | P1 | ✓ |
| G-08 | Brief disconnect → reconnect keeps the user in the session (does not bounce to the classroom page) | Browser E2E | P0 | ✓ (unit) |
| G-09 | Ending the session properly still ejects everyone — the same change touches both paths | Browser E2E | P0 | ✓ (unit) |
| G-10 | Session end triggers transcript persistence and summary generation | Integration | P0 | ✓ (unit) |

| G-20 | A view-only student's join token has `canPublish` false AND an empty source list | Unit | P0 | ✓ |
| G-21 | Publish sources match exactly the permissions granted; one permission never implies the other | Unit | P0 | ✓ |
| G-22 | A student never receives screen-share; a teacher keeps it with camera and mic off | Unit | P0 | ✓ |
| G-23 | Role matching is case-insensitive, and an unknown role gets a student's rights | Unit | P0 | ✓ |
| G-24 | The token binds room to session and identity to user; the role travels in metadata | Unit | P0 | ✓ |
| G-25 | Subscribe and data-channel rights never depend on publish rights | Unit | P1 | ✓ |
| G-26 | Ending a session deletes the LiveKit room, so a client that never saw the broadcast is still cut off | Unit | P0 | ✓ |
| G-27 | A failure to close the room is reported to the caller, never swallowed | Unit | P0 | ✓ |
| G-28 | The live publish policy touches students only — never the teacher or the AI assistant | Unit | P0 | ✓ |
| G-29 | A participant whose role cannot be read (absent, malformed, or role-less JSON) is left alone | Unit | P0 | ✓ |
| G-30 | Muting sets `canPublish` false **and** an empty source list; the student keeps subscribe and data | Unit | P0 | ✓ |
| G-31 | One participant that cannot be updated does not stop the sweep; a room that does not exist yet is not an error | Unit | P1 | ✓ |
| G-32 | Every `IMediaSettings` value reaches the join response, and the response carries nothing the settings do not define (rule over the interface, both directions) | Unit | P0 | ✓ |
| G-33 | The reconnection settings (retries, peer-connection and websocket timeouts) arrive intact — they are frozen at connect time | Unit | P0 | ✓ |
| G-34 | Teacher and student receive identical media configuration | Unit | P1 | ✓ |
| G-35 | No join token is issued once the session has ended — not even to the teacher | Unit | P0 | ✓ |

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
| H-10 | Feedback-card contrast meets WCAG AA on every label, over both a dark and a bright room backdrop, measured on the composited translucent layers | Unit | P1 | ✓ (found the timestamp at 3.38:1; the palette is read from the installed Tailwind, not copied) |
| H-11 | "Likely to be" replaces "unclear" in the enum, prompt vocabulary and both locales | Unit + Frontend | P1 | ✓ |
| H-12 | STT failure or model timeout degrades to silence, never to a crashed session | Unit | P0 | ✓ |
| H-13 | A quoted span the teacher never said is discarded, and the suggestion survives without it | Unit | P0 | ✓ |
| H-14 | A prompt blocked by safety filters yields no feedback rather than an exception | Unit | P0 | ✓ |
| H-15 | A reply split across response parts is joined, not truncated to the first part | Unit | P0 | ✓ |
| H-16 | The brain's API key travels in a header, never in the URL | Unit | P0 | ✓ |
| H-17 | A bad key reported as HTTP 400 is still named as a key problem; an ordinary 400 is not | Unit | P1 | ✓ |
| H-18 | A malformed `GEMINI_GENERATION_CONFIG_JSON` is ignored rather than fatal at startup | Unit | P1 | ✓ |
| H-19 | Quiz generation uses its own token cap, temperature and timeout — not the evaluation ones | Unit | P0 | ✓ |
| H-20 | The response schema's `type` is uppercased at every nesting level for Gemini's proto dialect | Unit | P0 | ✓ |
| H-21 | Ollama: generation is bounded to a core budget, and `0` means the provider default rather than zero threads | Unit | P1 | ✓ |
| H-22 | Ollama: streaming is off, so the reply parses as one object | Unit | P0 | ✓ |
| H-23 | A crash or cancellation mid-session still finalizes the transcript, disconnects, and releases the pacer and retained ideas | Unit | P0 | ✓ |
| H-24 | The crash log carries the exception **type** only — never lecture content | Unit | P0 | ✓ |
| H-25 | A failing disconnect does not abort the finalize and state release after it | Unit | P1 | ✓ |

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
| I-06 | An answer at exactly the deadline boundary — pick the rule and pin it | Unit | P0 | ✓ (§11.7: one `QuizDeadline.IsPast` both callers ask; the boundary is a table in `QuizConcurrencyTests`) |
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
| I-24 | Publishing refuses an empty quiz, a question with no text, and a question worth zero or negative marks | Unit | P0 | ✓ |
| I-25 | The answer-count range is enforced at **both** ends, and the duration ceiling at its exact boundary (`>`, not `>=`) | Unit | P0 | ✓ |
| I-26 | `GetLimits()` returns the same limits publishing enforces, so the composer cannot offer a quiz the server would refuse | Unit | P1 | ✓ |
| I-27 | Quiz and answer generation are **never retried** — a retry re-runs the model while a teacher waits — while transcript calls retry 3× | Unit | P0 | ✓ |
| I-28 | A generation 409 is a conflict carrying the assistant's own wording, and survives an unreadable body | Unit | P1 | ✓ |
| I-29 | A generated quiz with no questions, or answers with no options, is a failure rather than an empty composer | Unit | P0 | ✓ |
| I-30 | An incomplete AI correction is dropped rather than shown as half a sentence; a complete one is trimmed | Unit | P1 | ✓ |

## 12. Area J — Ranking (work-plan §6)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| J-01 | Ranking uses only quizzes that count (cancelled excluded) | Unit | P0 | ✓ |
| J-02 | Ties resolve by the documented rule (competition ranking), deterministically across repeated calls | Unit | P0 | ✓ |
| J-03 | A student with no submissions appears with zero rather than vanishing | Unit | P1 | ✓ (participants only — no names exist for absentees, see work-plan §6) |
| J-04 | A student who joined late is ranked on available quizzes, not penalised for ones predating them — decide and pin | Unit | P0 | ✓ (pinned: whole-term, by test) |
| J-05 | Visibility rule enforced server-side: a student cannot fetch the full leaderboard if it is teacher-only | Unit | P0 | ✓ |
| J-06 | Ranked query does not N+1 across submissions | Integration | P1 | ✓ (one load per collection, grouped in memory) |

## 13. Area K — In-session notifications (work-plan §5)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| K-01 | A chat message while the sidebar is closed increments the unread badge | Frontend | P1 | ✓ |
| K-02 | Your own message never notifies you | Frontend | P0 | ✓ |
| K-03 | No notification when the relevant panel is already open and focused | Frontend | P1 | ✓ |
| K-04 | A published quiz notifies every student in the session, once | Frontend | P0 | ✓ |
| K-05 | Unread count clears on open and does not resurrect on re-render | Frontend | P1 | ✓ |
| K-06 | Backgrounded tab: **title** reflects unread (visibilitychange handling) | Frontend | P1 | ✓ (title only — no favicon badge, see work-plan §5) |
| K-07 | Denied browser-notification permission degrades to in-app only, no crash, no repeated prompting | Frontend | P0 | ✓ |
| K-08 | Mute suppresses notifications without suppressing the messages themselves | Frontend | P2 | ✓ |

## 14. Area L — Cross-service messaging & consistency

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| L-01 | Every **state-changing** consumer is idempotent: the same message twice produces one effect | Unit | P0 | ✓ (recording-ready, summary-ready, session-started; the five email consumers are exempt — see §17) |
| L-02 | MassTransit envelope round-trips between the .NET and Python services | Unit | P0 | ✓ |
| L-03 | Outbox message survives a service restart between commit and publish | Integration | P0 | partial |
| L-04 | A consumer throwing does not lose the message (retry/DLQ observable) | Unit | P0 | ✓ (all four services; found ClassroomService's recording consumer and StreamingService's only consumer registered with no retry at all) |
| L-07 | Every consumer is REGISTERED with a definition, checked through the real composition root — not merely that a definition type exists | Unit | P0 | ✓ (ClassroomService, StreamingService, EmailService) |
| L-08 | Every definition actually configures a retry; an empty `ConfigureConsumer` is caught | Unit | P0 | ✓ (the definition is run against a recording configurator) |
| L-09 | The retry detection itself is verified in both directions, so a MassTransit rename fails loudly instead of matching nothing | Unit | P1 | ✓ |
| L-10 | A database failure inside a consumer faults the message rather than being swallowed — without this the retry policy never runs | Unit | P0 | ✓ (recording, summary, session-started) |
| L-05 | Recording-ready and summary-ready consumers tolerate arriving out of order | Unit | P1 | ✓ |
| L-06 | An internal HTTP call timing out degrades the caller gracefully rather than cascading | Unit | P1 | ✓ (`DownstreamDegradationTests`, `StreamingInternalClientTests`; the degradation paths were only ever tested with `HttpRequestException`, which is the one case that is unambiguous) |
| L-10 | A downstream TIMEOUT degrades — and it arrives as `TaskCanceledException`, the same type an abandoned caller produces | Unit | P0 | ✓ |
| L-11 | **A caller who has gone propagates instead of being swallowed** — otherwise the request keeps calling other services to finish an answer nobody will read | Unit | P0 | ✓ |
| L-12 | The degradation flag is not raised against healthy services by ordinary browser navigation — it is the signal an operator uses to find a real outage | Unit | P1 | ✓ |
| L-13 | A cancelled caller is not reported as StreamingService refusing the call, which would record a session as having failed to start a stream nobody refused | Unit | P0 | ✓ |
| L-14 | Best-effort quiz broadcasts still swallow a genuine downstream failure — guarding cancellation must not make an endpoint fail because a notification did | Unit | P1 | ✓ |

## 15. Area M — Configuration, migrations & deployment (work-plan §14)

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| M-01 | Settings bind correctly; documented defaults apply when a key is absent | Unit | P0 | ✓ |
| M-02 | A missing **required** setting fails at startup, not on first request | Unit | P0 | ✓ (and the broker credentials genuinely do now — see work-plan §7.9) |
| M-03 | Drift test: every variable compose requires appears in `.env.example`, and nothing in it is unread | Unit | P1 | ✓ |
| M-04 | `.env.example` contains no real secret | Unit | P0 | ✓ |
| M-05 | EF migrations apply cleanly to an empty database | Integration | P0 | gap |
| M-06 | Alembic upgrades **and downgrades** without loss | Integration | P0 | gap |
| M-07 | An embedding-dimension change migrates the pgvector column and forces re-embedding rather than silently mismatching | Integration | P0 | gap |
| M-08 | Health endpoints report unhealthy when a dependency is down (not a blind 200) | Integration | P1 | partial |
| M-09 | en and ar locale files have identical key sets **and matching placeholders**, allowing for Arabic's six plural categories | Frontend | P1 | ✓ |
| M-10 | RTL layout renders without overflow on the main pages | Frontend | P2 | partial |
| M-11 | Every downstream `/api/internal` client binds its OWN section — a client wired to the wrong one is caught | Unit | P0 | ✓ |
| M-12 | A missing internal secret or base URL refuses startup with a message naming the key to set and the env var it must match | Unit | P0 | ✓ (defect found: LiveAssistant & RagService secrets were never configured in UMS) |
| M-13 | A timeout `HttpClient` would reject (zero or negative) is refused at startup rather than crashing the host | Unit | P1 | ✓ |
| M-14 | Each internal client is built with its configured base address, timeout, and the `X-Internal-Secret` header | Unit | P0 | ✓ |
| M-15 | Dependency vulnerability scan across .NET, npm and Python — no known-vulnerable package ships | Tooling | P0 | ✓ (22 found, 20 fixed; the 2 remaining are documented as not-applicable — see work-plan §11.13) |

## 16. Area N — Email delivery (work-plan §7.6)

The service that had no tests at all. It does one thing — turn a message into an email — so
every case here is about routing and content, plus the one failure mode that loses mail
silently.

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| N-01 | Each account status maps to its own subject **and** its own wording | Unit | P0 | ✓ |
| N-02 | Status matching is case-insensitive — the producer sends an enum name, not lowercase | Unit | P0 | ✓ |
| N-03 | An unrecognised status still sends a neutral email rather than failing or congratulating | Unit | P1 | ✓ |
| N-04 | Reset and 2FA codes carry the right code, the right expiry, and distinguishable copy | Unit | P0 | ✓ |
| N-05 | Teacher assigned/reassigned and member added/removed each pick the matching subject and body | Unit | P0 | ✓ |
| N-06 | **A name or classroom name containing markup is HTML-encoded, not rendered** | Unit | P0 | ✓ |
| N-07 | A null name produces an email rather than an exception | Unit | P1 | ✓ |
| N-08 | A failed send faults the message (publishes `Fault<T>`) instead of being swallowed | Unit | P0 | ✓ |
| N-09 | **Every** consumer in the assembly has a retry policy — a rule, not a list, so a new consumer cannot ship without one | Unit | P0 | ✓ |
| N-10 | SMTP transport itself (connect, STARTTLS, auth) | Integration | P1 | not covered — needs a real or fake SMTP server, see §17 |

## 17. Area O — Latency budgets (work-plan §9)

Definitions, budgets and the derivation of every number: **[latency.md](latency.md)**.
Harness: `backend/tests/e2e/test_latency.py` (`-m latency`), which writes
`latency-results.md`. The harness's own arithmetic and protocol handling are covered by
`test_latency_support.py`, which needs nothing running.

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| O-01 | Chat send → another participant renders, p50 ≤ 150ms / p95 ≤ 350ms | E2E | P1 | authored, unrun |
| O-02 | Quiz publish → a student's socket learns of it, p50 ≤ 300ms / p95 ≤ 700ms | E2E | P1 | authored, unrun |
| O-03 | Quiz publish → the student **holds** the quiz (signal + fetch), p95 ≤ 1200ms — a fairness budget, since the answering clock starts at publish | E2E | P0 | authored, unrun |
| O-04 | Assistant idea closes → feedback delivered, p50 ≤ 5s / p95 ≤ 15s | E2E | P1 | authored, unrun (reads the service's own histogram; needs a feedback run first) |
| O-05 | Audio publish → subscriber through the SFU, p50 ≤ 80ms / p95 ≤ 150ms — **transit only, not glass-to-glass** | E2E | P2 | authored, unrun (needs UDP media) |
| O-06 | Every broadcast the hub sends is timed, under the name of the client method it invoked (rule over `IStreamHubContext`, so a ninth broadcast cannot ship untimed) | Unit | P1 | ✓ |
| O-07 | A broadcast that throws records no latency sample — fast failures must not flatter the percentiles | Unit | P0 | ✓ |
| O-08 | The timer spans the awaited fan-out, not the group lookup that starts it | Unit | P0 | ✓ |
| O-09 | Each event is its own series, so a slow quiz relay cannot be averaged into fast chat traffic | Unit | P1 | ✓ |
| O-10 | The metric actually emits on the meter a scraper would read (`MeterListener`, not just the interface being called) | Unit | P1 | ✓ |
| O-11 | Percentiles are observed values at nearest rank, never interpolated ones | Unit | P0 | ✓ |
| O-12 | The warm-up sample is discarded from the front, and never when it is the only one | Unit | P0 | ✓ |
| O-13 | A hop that could not be measured reports as not-measured with the reason, never as a pass | Unit | P0 | ✓ |
| O-14 | A hop that was cut short is still judged on the samples it got, and marked INCOMPLETE | Unit | P0 | ✓ |
| O-15 | Several SignalR records packed into one WebSocket frame are all dispatched, and share one arrival stamp | Unit | P0 | ✓ |
| O-16 | The send stamp is taken before the write, and the arrival stamp before parsing | Unit | P0 | ✓ |
| O-17 | Glass-to-glass proper (browser capture + playout buffers) and a second participant across a real network | E2E | P2 | **not covered — cannot be, from here.** See latency.md; L-4 measures the transit floor and says so. |

**Not covered on purpose:** the runtime numbers themselves, until the platform is up.
Everything above that could be decided without a container has been.

## 18. Area P — Smoke & deployment liveness (work-plan §10.1)

Harness: `backend/tests/e2e/test_smoke.py` (`-m smoke`). `test_smoke_inventory.py`
carries the same marker but needs nothing running.

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| P-01 | Every service answers its health endpoint with **2xx** — a 503 is a working endpoint reporting a broken service, and only 2xx is alive | E2E | P0 | authored, unrun |
| P-02 | A seeded account can log in — proves the seeder, the hasher and the signing key, none of which liveness can see | E2E | P0 | authored, unrun |
| P-03 | A teacher can be registered, approved, logged in, and can read and write a classroom | E2E | P0 | authored, unrun |
| P-04 | Starting a session cascades ClassroomService → StreamingService → LiveKit → LiveAssistantService, and ending it always runs | E2E | P0 | authored, unrun |
| P-05 | The whole smoke run finishes inside 60s, and a breach names its own long pole | E2E | P1 | authored, unrun |
| P-06 | Every service in the compose graph is probed or exempt **with a written reason** — read from the compose files, not a hand-kept list | Unit | P0 | ✓ |
| P-07 | Nothing is listed that compose no longer runs (an exemption for a deleted service silently excuses the next one to take the name) | Unit | P0 | ✓ |
| P-08 | Every .NET service maps `/health`, matched on the real shapes — a comment mentioning the path must not satisfy the rule | Unit | P0 | ✓ |
| P-09 | Every Python service serves a `/health` route | Unit | P0 | ✓ |
| P-10 | The health check of every data-holding service can report **Unhealthy**, and does not report Degraded — which `MapHealthChecks` answers with 200 | Unit | P0 | ✓ |
| P-11 | A reachable database is Healthy; an unreachable one is Unhealthy, not Degraded | Unit | P0 | ✓ |
| P-12 | The health-check failure text never names the host, database or path — `/health` is unauthenticated | Unit | P0 | ✓ |
| P-13 | The probe answers inside its own deadline rather than waiting out the driver's 15s connect timeout | Unit | P1 | ✓ |
| P-14 | EmailService is probed too — it has no host port, so from the host this skips loudly rather than being dropped | E2E | P1 | authored, unrun |

**Not covered on purpose:** RabbitMQ and Redis are not probed directly (no
unauthenticated health surface; their outages surface through the services that depend
on them), and `livekit-egress` has no HTTP surface at all — it is a worker, covered by
the recording path in G-06.

## 19. Area Q — Dependency ceilings & configuration binding (work-plan §10.4)

Rules over configuration: `backend/tests/e2e/test_resource_ceilings.py` and
`test_settings_binding.py` (`-m resilience`, nothing running), plus `S3ClientFactoryTests`
in ClassroomService.

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| Q-01 | **No variable a deployment sets binds to nothing** — `extra="ignore"` discards a renamed setting in silence | Unit | P0 | ✓ |
| Q-02 | The retrieval base URL binds to the name compose, the README and the error messages all use, and the pre-rename name still works | Unit | P0 | ✓ |
| Q-03 | Every configured `BaseUrl` declares its own `TimeoutSeconds`, so the value is where a deployment can see it | Unit | P0 | ✓ |
| Q-04 | Object-storage calls are bounded and retry a bounded number of times — not the SDK's 100s × 4 | Unit | P0 | ✓ |
| Q-05 | Turning retries off is honoured rather than read as "unset" and silently restored | Unit | P1 | ✓ |
| Q-06 | The storage health probe has a tighter budget than a storage call | Unit | P0 | ✓ |
| Q-07 | Everything that talks to MinIO reads the **same two** environment variables | Unit | P0 | ✓ |
| Q-08 | The development MinIO credentials appear in no shipped source, appsettings or compose file | Unit | P0 | ✓ |
| Q-09 | Missing storage credentials stop startup naming the variables, rather than failing on the first upload | Unit | P0 | ✓ |
| Q-10 | Every compose file actually reaches the rules above (six share one filename) | Unit | P0 | ✓ |
| Q-11 | A stopped MinIO degrades rather than hangs — the request fails inside its budget | Integration | P0 | gap (needs containers) |
| Q-12 | A stopped Postgres surfaces as 503 from `/health` and not as a hung request | Integration | P0 | gap (needs containers) |
| Q-13 | A slow or absent model provider degrades the assistant to "no feedback" without stalling the session | Integration | P1 | gap (needs containers) |
| Q-14 | The .NET equivalent of Q-01 — a `Section__Key` that binds to no options property | Unit | P1 | **not covered.** .NET's binder ignores unknown keys just as silently, but settings are read through a mix of options classes and direct `configuration["A:B"]` lookups; a static rule over that gave false positives on every service. |

## 20. Area R — Results reporting (work-plan §10.5)

The document: **[testing-results.md](testing-results.md)**, filled by
`backend/tests/e2e/collect_results.py`. Rules: `test_results_collector.py` (`-m offline`).

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| R-01 | A coverage artifact older than the code it measures **withholds the number**, and shows both dates | Unit | P0 | ✓ |
| R-02 | A current measurement is reported with the date it was taken | Unit | P0 | ✓ |
| R-03 | A missing artifact says so **and** gives the command that would produce it — including when no artifact exists anywhere | Unit | P0 | ✓ |
| R-04 | Equal timestamps count as current, not stale | Unit | P1 | ✓ |
| R-05 | Staleness is undecidable rather than assumed when a date is missing | Unit | P1 | ✓ |
| R-06 | Frontend coverage is summed from LCOV counts, never averaged across per-file percentages | Unit | P0 | ✓ |
| R-07 | Every component produces a row — one silently dropped is one the report silently omits | Unit | P0 | ✓ |
| R-08 | Regenerating replaces only the generated blocks and never the prose | Unit | P0 | ✓ |
| R-09 | A missing generated marker stops the run rather than writing nothing | Unit | P0 | ✓ |
| R-10 | The results document still contains every block the collector fills | Unit | P1 | ✓ |
| R-11 | Every module that needs nothing running carries the `offline` marker, or is a named exception with a reason (both directions) | Unit | P0 | ✓ |

**Not covered:** test counts are still transcribed into §2 of the document by hand. Parsing them
would mean running every suite from the collector, which turns reading the file into a two-minute
operation; the commands are stated instead.

## 21. Area S — Concurrency & interleaving (work-plan §11.7)

`QuizConcurrencyTests` (ClassroomService). Driven interleavings, not threads — see the file's
own note on why.

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| S-01 | An extension granted while the sweep is running is not overwritten, and the teacher's deadline is the one that survives | Unit | P0 | ✓ |
| S-02 | A quiz that was **not** extended still closes in the same sweep — the reprieve is per quiz | Unit | P0 | ✓ |
| S-03 | A reprieved quiz is not announced as closed | Unit | P0 | ✓ |
| S-04 | An ordinary sweep with nothing racing still closes the quiz (the control) | Unit | P0 | ✓ |
| S-05 | Submitting twice records one submission and returns the original timestamp, not a conflict | Unit | P0 | ✓ |
| S-06 | Two students submitting interleaved do not displace each other | Unit | P1 | ✓ |
| S-07 | Answering the same question twice updates the answer instead of accumulating a second row | Unit | P0 | ✓ |
| S-08 | An answer inside the late-answer grace is accepted; past it, refused — even while the quiz is still `Open` | Unit | P0 | ✓ |
| S-09 | The sweep and the answer path agree on where the deadline is, at the exact boundary | Unit | P0 | ✓ |
| S-10 | One rule decides when a quiz is over: at the deadline, at deadline+grace, and one second past | Unit | P0 | ✓ |
| S-11 | A quiz with no timer is never over; a negative grace does not bring the deadline forward | Unit | P1 | ✓ |
| S-12 | A student with extra time is not cut off by the class deadline | Unit | P0 | ✓ |
| S-13 | Two real connections racing on the same row — the remaining window needs an optimistic concurrency token on `Quiz` | Integration | P1 | **gap.** A fake cannot model transaction isolation, and a token makes every quiz write able to throw, so it wants an integration suite behind it first. |
| S-14 | Bulk approve retried after a timeout (UMS, §2 idempotency) | Unit | P1 | ✓ (`UserStatusRetryTests`; the batch is one transaction, so a retry only ever meets it entirely done or entirely undone) |
| S-15 | A batch that fails to commit approves nobody and emails nobody — the outbox rows die with the transaction | Unit | P0 | ✓ |
| S-16 | A retry that is partly already done finishes the rest without notifying anyone twice | Unit | P1 | ✓ |
| S-17 | Every status transition rolls `User.Version`, which is what makes a racing retry collide instead of duplicating | Unit | P1 | ✓ (the entity's half; behaviour under real isolation is S-13) |
| S-18 | A caller that gave up is answered 499 and is not logged as a server error | Unit | P1 | ✓ (`GlobalExceptionHandlerTests`) |
| S-19 | An unexpected failure does not hand its exception message to the caller | Unit | P0 | ✓ |
| S-20 | Nothing is written over a response that has already started | Unit | P1 | ✓ |

## 22. Area T — Migrations (work-plan §11.8)

`MigrationConformanceTests` (ClassroomService, StreamingService, UserManagementService) and
`test_migration_conformance.py` (RagService, LiveAssistantService). All containerless.

| ID | Case | Level | Pri | Cov |
| --- | --- | --- | --- | --- |
| T-01 | **The model and the migrations have not drifted** — an entity changed with no migration to match | Unit | P0 | ✓ |
| T-02 | Every EF migration can be undone — an empty `Down` reports success and changes nothing | Unit | P0 | ✓ |
| T-03 | Migration ids are unique and ordered by their timestamps | Unit | P1 | ✓ |
| T-04 | There are migrations and entities to check (the guard on the guards) | Unit | P0 | ✓ |
| T-05 | The Alembic history has exactly one root and one head — two heads break on deploy, not in CI | Unit | P0 | ✓ |
| T-06 | Every Alembic parent link points at a revision that exists | Unit | P0 | ✓ |
| T-07 | Every Alembic revision id is unique | Unit | P0 | ✓ |
| T-08 | Every Alembic upgrade does something | Unit | P1 | ✓ |
| T-09 | **Every Alembic revision can actually be undone** — `alembic downgrade` reports success on an empty body | Unit | P0 | ✓ |
| T-10 | EF migrations apply cleanly to an empty database | Integration | P0 | gap (needs Postgres) |
| T-11 | Alembic upgrades **and downgrades** without loss — an empty downgrade is detectable here, a *wrong* one is not | Integration | P0 | gap (needs Postgres + pgvector) |

## 23. Deliberately not covered — and why

Stating this is part of the plan. An unstated gap reads as an oversight; a stated one is
a decision.

- **Third-party correctness.** LiveKit's SFU, MinIO's storage, Postgres, RabbitMQ. We
  test our *use* of them, not them.
- **Idempotency of the email consumers.** L-01 covers the consumers that write rows,
  where a redelivery would corrupt data. The five in EmailService are deliberately left
  out: MassTransit redelivers after a *failure*, and if the SMTP call threw then the mail
  was almost certainly never sent — so re-sending is the correct behaviour, not a
  duplicate. A true duplicate needs the send to succeed and the acknowledgement to be
  lost, and the cost of that is one repeated email. Making it exact would mean a dedup
  store keyed on message id: real infrastructure, permanently, to prevent a rare and
  harmless annoyance. Revisit if an email ever carries a side effect stronger than
  telling someone something.
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

## 24. Entry / exit criteria

**Entry** — before a suite is considered runnable: containers up, migrations applied,
seed data present, `.env` complete, and for the assistant path a working model/STT key
(or fakes selected).

**Exit** — before the work in [work-plan.md](work-plan.md) is called done:

1. Every **P0** case above exists and passes.
2. Per-service line coverage ≥ 85% after the agreed exclusions. **Currently met by one of
   seven components on the headline** — see [testing-results.md](testing-results.md) §3, which
   also explains why the headline is the wrong number to judge it on.
3. Mutation spot-check passes on quiz scoring and the auth path — the two places where
   a test that never fails would be most dangerous.
4. No P0 case is marked "not implemented" without a written reason here.
5. The smoke suite passes against a fresh `docker compose up` from a clean volume
   (**authored — Area P; the containerless half already passes**).

## 25. Traceability

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
| N | §7.6, §11.1 |
| O | §9 |
| P | §10.1 |
| Q | §10.4 |
| R | §10.5 |
| S | §11.7 |
| T | §11.8 |
