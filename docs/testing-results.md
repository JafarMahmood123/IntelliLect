# Testing results

The template the report's testing chapter fills in (work-plan §10.5). Every number here either
came from an artifact or says which command produces it — nothing is transcribed.

Regenerate the machine-derived blocks after producing the artifacts:

```bash
cd backend/tests/e2e && .venv/bin/python collect_results.py
```

<!-- generated:stamp -->
_Generated 2026-08-07 18:37 UTC by `backend/tests/e2e/collect_results.py` from the artifacts present at that moment._
<!-- /generated:stamp -->

---

## 1. What was tested, and at which level

| Level | What it covers | Runs without containers |
|---|---|---|
| Unit | Domain and application logic, mappers, consumers, options binding, and **rules over the assembly** — authorization attributes, internal-secret guards, i18n key parity, broadcast timing | yes |
| Component | Consumers driven through MassTransit's in-memory harness; FastAPI routers through `TestClient` | yes |
| Frontend | Components, hooks, stores and the axios interceptor via Vitest + Testing Library | yes |
| Cross-service E2E | The real platform over HTTP + LiveKit: session orchestration, the assistant feedback loop, the internal-surface guard, smoke and latency | **no** |

The suite leans deliberately on **rules over the assembly** rather than example-by-example tests
wherever the failure being guarded against is "somebody forgot one". A list of endpoints that
require authorization goes stale; a test that enumerates every endpoint and fails on the one
without an attribute does not.

## 2. Test inventory

<!-- generated:inventory -->
| Suite | Tests | Status | Command |
|---|---|---|---|
| UserManagementService | 343 | passed 2026-08-07 | `cd backend/UserManagementService && dotnet test UserManagementService.slnx --logger trx` |
| ClassroomService | 522 | passed 2026-08-07 | `cd backend/ClassroomService && dotnet test ClassroomService.slnx --logger trx` |
| StreamingService | 252 | passed 2026-08-07 | `cd backend/StreamingService && dotnet test StreamingService.slnx --logger trx` |
| EmailService | 59 | passed 2026-08-07 | `cd backend/EmailService && dotnet test EmailService.slnx --logger trx` |
| RagService | 397 (+13 skipped) | passed 2026-08-07 | `cd backend/RagService && .venv/bin/python -m pytest --junitxml=test-results.xml` |
| LiveAssistantService | 381 (+3 skipped) | passed 2026-08-07 | `cd backend/LiveAssistantService && .venv/bin/python -m pytest --junitxml=test-results.xml` |
| front-end-web | 373 | passed 2026-08-07 | `cd front-end-web && npx vitest run --reporter=junit --outputFile=test-results.xml` |
| Cross-service E2E (offline subset) | 91 | passed 2026-08-07 — the rest of the suite needs the platform; see below | `cd backend/tests/e2e && .venv/bin/python -m pytest -m offline --junitxml=test-results.xml` |
| **Total** | **2,418** | all 8 suites passing, 16 skipped | |
<!-- /generated:inventory -->

**This table was the last thing in the document still typed by hand**, while line 4 claimed
nothing here is transcribed. Four cycles of updating it by hand produced one wrong number that
only the coverage generator caught, so it now reads the same kind of artifact every other block
does — a TRX or JUnit file each runner already knows how to write — with the same rule attached:
a result older than the tests it counts is **withheld**, never quoted.

Two things follow from that, both deliberate:

- **The count is what RAN.** The cross-service row reports its `-m offline` subset, not the full
  collection. The remaining 76 tests are authored and have never executed; adding them to the
  others would be claiming they pass.
- **The total refuses to be a number when any row is withheld.** Summing whichever rows happen to
  be readable gives a smaller figure that looks exactly like a real one, and a report that
  under-counts silently has the same defect as one that over-counts.

The E2E figure needs the split. Most of that suite requires a live platform, but the
containerless part — the smoke inventory, the latency harness's own arithmetic, the
configuration-binding and dependency-ceiling rules — runs anywhere:

```bash
cd backend/tests/e2e && .venv/bin/python -m pytest -m offline
```

`-m offline` and not the topical markers: `-m smoke` and `-m latency` each select a
containerless rule module **and** a module that needs the real platform, so with nothing running
that selection waits two minutes and then fails.

## 3. Coverage

<!-- generated:coverage -->
| Component | Line | Branch | Status | How it is produced |
|---|---|---|---|---|
| UserManagementService | 51.8% | 36.9% | measured 2026-08-07 (1022 lines) | `cd backend/UserManagementService && dotnet test --collect:'XPlat Code Coverage' -s ../coverlet.runsettings` |
| ClassroomService | 81.3% | 73.9% | measured 2026-08-07 (1228 lines) | `cd backend/ClassroomService && dotnet test --collect:'XPlat Code Coverage' -s ../coverlet.runsettings` |
| StreamingService | 78.1% | 43.8% | measured 2026-08-07 (686 lines) | `cd backend/StreamingService && dotnet test --collect:'XPlat Code Coverage' -s ../coverlet.runsettings` |
| EmailService | 100.0% | 94.4% | measured 2026-08-07 (191 lines) | `cd backend/EmailService && dotnet test --collect:'XPlat Code Coverage' -s ../coverlet.runsettings` |
| RagService | 83.2% | 69.0% | measured 2026-08-07 (3960 lines) | `cd backend/RagService && .venv/bin/python -m pytest --cov=app --cov-report=xml` |
| LiveAssistantService | 87.2% | 74.3% | measured 2026-08-06 (2700 lines) | `cd backend/LiveAssistantService && .venv/bin/python -m pytest --cov=app --cov-report=xml` |
| front-end-web | 41.4% | 79.5% | measured 2026-08-07 (14863 lines) | `cd front-end-web && npm run test:coverage` |
<!-- /generated:coverage -->

### Read the layer, not the headline

<!-- generated:layers -->
| Service | Layer | Line |
|---|---|---|
| UserManagementService | Domain | 84.9% |
| UserManagementService | Application | 69.0% |
| UserManagementService | Infrastructure | 34.5% |
| UserManagementService | Presentation | 0.0% |
| UserManagementService | Api | 100.0% |
| ClassroomService | Domain | 100.0% |
| ClassroomService | Application | 97.9% |
| ClassroomService | Infrastructure | 75.1% |
| ClassroomService | Presentation | 35.6% |
| StreamingService | Domain | 100.0% |
| StreamingService | Application | 88.3% |
| StreamingService | Infrastructure | 83.8% |
| StreamingService | Presentation | 59.3% |
| EmailService | Application | 100.0% |
| EmailService | Infrastructure | 100.0% |
<!-- /generated:layers -->

**The headline moves for reasons that have nothing to do with testing, and this project has a
live example of it.** Coverlet reports on the assemblies a test run actually loads. If no test
ever touches an assembly, it is absent from the report entirely and the percentage is computed
without it.

**And a third time, inflating rather than deflating — read the L-04 jump with care.** Adding the
consumer-retry rules moved ClassroomService from 62.3% to **77.8%**, StreamingService from 51.5%
to **73.6%** and EmailService from 72.0% to **97.6%**, on three to seven new tests each. Nothing
about those services became better tested to that degree. The rules run the real
`AddInfrastructure`, and a service's composition root is hundreds of lines that had never been
*executed* by any test before — the entire jump is in the Infrastructure layer, and it is
execution, not verification. What those tests actually assert is narrow and deliberate: that
every consumer is registered with a definition and that the definition configures a retry. The
DI wiring around it is now merely *run*. A reader who takes 97.6% as "EmailService is almost
fully verified" has been misled by a number this document produced, which is why it says so here.

**And the follow-up, which is the only place a headline in this document has been *earned* by
closing the gap it was hiding.** The 2.4% that EmailService was missing was `SmtpEmailSender` —
the one class in the service that talks to anything outside it, and the one nothing tested.
N-10 tested it against a loopback SMTP server, found three defects, and took the figure to
**100.0%**. That is a different event from the L-04 jump above, and worth naming as such: the
number moved because code became verified, not because more code became executed. It is also
the reason the branch figure moved only 93.8% → 94.4% while the line figure moved to 100 —
`SmtpEmailSender` has almost no branching in it, which is exactly why "100% line" is still not
a claim that the service is correct.

**It happened again in this cycle, in the other direction.** Testing `GlobalExceptionHandler`
required the test project to reference the Api assembly, which pulled it and Presentation into the
report for the first time. UserManagementService's headline **fell from 53.5% to 50.0% while
eighteen tests were added and two defects were fixed**, and a `Presentation | 0.0%` row appeared
that had simply been invisible before. The controllers were never covered; they were merely not
being counted.

UserManagementService's Infrastructure assembly was in exactly that position at the §0.2 baseline
— "never loaded by any test" — so its headline of 62.1% was really Application + Domain. Adding a
single Infrastructure test pulled ~300 lines of untested adapter code into the denominator, and
the headline **fell to 49.6% while the test count rose from 120 to 179 and Application coverage
went up**. Quoting only the headline records that as a regression. It is the opposite.

The same reading applies to the target. ClassroomService is at 62.2% overall and **97.6% in
Application, 100% in Domain**; the deficit is Infrastructure (39.5%) and Presentation (19.0%) —
adapters, DI wiring and controllers. Chasing 85% *including* those buys tests that mostly prove a
mapper maps. The recommendation recorded in work-plan §0.2 stands: **≥85% on Application and
Domain, plus explicit controller-authorization rules**, which is what `PublicRouteAuthorizationTests`
and `InternalSecretGuardTests` are.

### Against the stated exit criterion

Test-plan exit criterion 2 is "per-service line coverage ≥ 85% after the agreed exclusions".
On the headline, **two of seven components meet it** (EmailService, 100.0%; LiveAssistantService,
87.2%; RagService is close at 83.2%). On the Application+Domain reading above, ClassroomService
meets it comfortably too, and UserManagementService and StreamingService do not. Either way it is
not met across the board, and the report should say so rather than average it away.

EmailService is the one that should be read with the least suspicion, and only because of what it
is: five consumers, a template factory and an SMTP client, with nothing that needs a database or a
broker to exercise. It is not evidence that the other six could reach the same figure by trying
harder.

## 4. Latency (work-plan §9)

Definitions, budgets and the derivation of every number: [latency.md](latency.md). The harness
writes its own table, which is quoted here verbatim rather than retyped.

<!-- generated:latency -->
_Not yet run._ `cd backend && docker compose up -d && cd tests/e2e && ./run-in-network.sh -m latency` writes `backend/tests/e2e/latency-results.md`; its table belongs here verbatim. Budgets and derivations: `docs/latency.md`.
<!-- /generated:latency -->

Two hops will not say what their names suggest, and the report must not let them:

- **L-4 is SFU transit, not glass-to-glass.** The browser's microphone capture buffer and its
  adaptive playout jitter buffer (40–200 ms) are the larger terms and are not observable from
  the harness. Reported as glass-to-glass, the figure would understate reality twofold.
- **L-3 is the assistant's own in-process histogram**, cumulative since the service started
  unless the run drove it.

## 5. Smoke (work-plan §10.1)

| Check | Status |
|---|---|
| Every service probed, or exempt with a written reason (17 services, read from the compose files) | ✓ passing, containerless |
| Every service exposes the `/health` its probe assumes | ✓ passing, containerless |
| Health can report Unhealthy for the services holding data | ✓ passing, containerless |
| One login, one classroom read, one session start, inside a 60 s budget | **not yet run** — `./run-in-network.sh -m smoke` |

## 6. Performance and stress (work-plan §10.2, §10.3)

**Not started; blocked on tooling.** k6 is the chosen tool (work-plan §12) and is not installed —
`sudo apt install k6` via the Grafana apt repository, or the standalone binary. The mixes to
script, once it is:

| Scenario | Why this one |
|---|---|
| Many students joining one session | The fan-out every other feature sits on top of |
| Concurrent quiz submissions at the deadline | The natural thundering herd — every client submits on the same second |
| RAG search under load | The only CPU-bound path, and the assistant's critical dependency |
| Bulk approve of a large batch | Fans out through EmailService; the one operation whose failure is silent |

k6 has no SignalR client, so the first scenario needs the hand-rolled client in
`backend/tests/e2e/support/signalr.py` rather than k6 — noted so it is not discovered late.

## 7. Resource ceilings (work-plan §10.4)

The configuration half is done and containerless: every configured base URL declares its own
timeout, storage calls are bounded and retry a bounded number of times, the health probe is
tighter than the call it probes, everything that talks to MinIO reads the same two variables, and
no variable a deployment sets binds to nothing. The runtime half — stopping MinIO, Postgres and
the model provider and watching what the services do — needs containers and is not done.

## 8. Defects found by testing

The strongest evidence a test suite offers is the list of things it caught. All of these were
found by writing a test, not by a user.

| # | Defect | Found by |
|---|---|---|
| 1 | `RAG_BASE_URL` bound to nothing after the §1 rename — the live assistant retrieved course material from an empty base URL and degraded every idea to "no feedback" | §10.4 settings-binding rule |
| 2 | ClassroomService's MinIO credentials were string literals; rotating the password would break every upload, download, recording and summary in that service alone | §10.4 credential-source rule |
| 3 | No timeout or retry bound on the S3 client — AWS SDK defaults meant a stopped MinIO held a request for minutes | §10.4 ceiling rules |
| 4 | **A successful password reset revoked no sessions** — an attacker holding a stolen refresh token kept access after the victim reset their password | A-08 reset tests |
| 5 | **The password-reset code was logged in plaintext** beside the email address, via `Console.WriteLine`, into Serilog's file sink | A-08 reset tests |
| 6 | UserManagementService had no `/health` endpoint at all | §10.1 smoke inventory |
| 7 | Three `/health` endpoints could only ever return 200 — every check reported `Degraded`, which maps to 200 | §10.1 smoke inventory |
| 8 | The E2E readiness gate was effectively checking nginx only | §10.1 |
| 9 | The classroom's teacher could take part in their own quiz, polluting the live respondent counts | §11.2 authorization rules |
| 10 | UMS was never configured to call LiveAssistant or RagService — every such call 401'd in the deployed compose | §14.3 |
| 11 | `GET /api/classrooms/{id}/members` threw for every classroom with students | §7.2 mapping tests |
| 12 | Token refresh signed users out despite succeeding, non-deterministically | §7.7 axios interceptor tests |
| 13 | `RefreshAsync` never re-checked account status — a disabled account kept working | §7.1 |
| 14 | The internal-secret guard failed **open** when unconfigured; StreamingService had no guard at all | §7b |
| 15 | HTML injection into outgoing mail; three consumers with no retry policy | §7.6 |
| 16 | `POST /api/internal/reembed` returned 202 for a refused run, contradicting its own docstring | §7.5 |
| 17 | A second, dead JWT minter in ClassroomService sharing the signing secret | §7.2 |
| 18 | 22 dependency advisories, 20 fixed; npm's proposed react-router "fix" refused as a net regression | §11.13 |
| 20 | **A client that timed out and hung up was logged as a 500 server error** — one manufactured error per abandoned request, arriving in bursts behind every retry, in the log somebody reads to find the real failure | S-14 exception-handler tests |
| 22 | **ClassroomService's recording consumer and StreamingService's only consumer were registered with no retry policy** — one attempt, then an error queue nobody watches. A lecture already recorded in MinIO stays permanently invisible; a class that has just started gets no stream row while everyone is in the room | L-04 consumer-retry rules |
| 23 | ClassroomService's solution file still pointed at the pre-`src/` layout and omitted its tests, and StreamingService's sat inside `src/` — so two of the coverage commands printed in this document did not run at all | noticed while regenerating this table |
| 33 | **91 physical directional utilities in an app that flips to `dir="rtl"` for Arabic** — a search icon at `left-3` inside an input padded `pl-10` sits on top of the first characters an Arabic user types, on four super-admin pages | M-10 |
| 34 | The drawer hides off the right edge in both directions, so in RTL it never leaves the screen; the settings toggle knob was unanchored and travelled off its own track | M-12, M-13 |
| 32 | **RagService had no size limit on ingestion at all** — `get_bytes` reads the whole object into memory in one call, and ingestion takes an `s3_key`, so nothing on that side had ever asked how big it was. ClassroomService's 50 MB upload cap does not reach it | E-08 |
| 30 | **There was no audit record of any account status change** — approving, rejecting and deactivating accounts is the most privileged operation in the product and `UserStatusService` had no logger, while ClassroomService logged every recording download | C-09 |
| 31 | The early self-target return skipped the audit — a super admin attempting to change their own status is the line the log most needs, and the shortcut that saved a query was the one path that lost it | C-20 |
| 28 | **An abandoned request kept working and blamed healthy services** — a cancelled caller was swallowed like an outage, so UMS carried on calling the remaining services and returned 200 with the "downstream unavailable" flag raised. It also stopped these requests reaching the 499 accounting added for exactly this | L-06 |
| 29 | **A cancelled caller was reported as StreamingService refusing the call** — `false` from `StreamingInternalClient` meant a session was recorded as having failed to start a stream nobody refused | L-13 |
| 25 | **An image sent as `application/pdf` extracted as an empty document and was marked indexed** — PyMuPDF opens images and `filetype="pdf"` does not stop it. The teacher saw an indexed file the assistant could never retrieve from, with no error anywhere | E-06 format-mismatch matrix |
| 26 | **A PDF renamed `.txt` was decoded into 18 paragraphs of replacement characters and embedded into the classroom index** — noise competing with real material for every later question. The extractor's docstring promised this could not happen | E-06 format-mismatch matrix |
| 27 | A document yielding no chunks was marked Done rather than Failed — reachable by a scanned PDF with OCR unavailable, or a blank file | E-19 |
| 24 | `FakeStreamRepository` kept a plain `List<T>` while the in-memory transport delivers concurrently, so one run in six failed in a way that read as a product bug in the idempotency check | L-04 work; a flake that accuses the code under test |
| 21 | **Every unexpected failure handed its exception message to the caller** — Npgsql messages carry the SQL, the table and the constraint; a configuration failure carries the connection string it tried | S-14 exception-handler tests |
| 19 | **Nothing limited password guessing anywhere in the system** — no lockout in `AuthService`, no ASP.NET rate limiter, no `limit_req` in nginx. The reset endpoint and the 2FA challenge were both capped; the front door was not | A-07 lockout tests |
| 35 | **EmailService's required-credential guard was dead code** — the sender threw on a null `AppPassword`, and `appsettings.json` shipped `""` for it. An empty string is not null, so a deployment that forgot the environment variable started cleanly, answered `/health` with "ok", bound all five queues, and lost every email it was handed: three retries each, then the error queue. The repository's own `Required()` helper states the rule this missed, and was applied to the broker credentials in the same file | N-13 |
| 36 | A malformed `SmtpPort` silently became 587 via `int.TryParse(...) ? parsed : 587`, and MailKit's 120-second default timeout was never overridden — one black-holed SMTP host held a consumer for six minutes per message across its retries | N-13, N-14 |
| 37 | **A fourth upload limit that nobody had counted.** `IUploadSettings` documented three copies of the one configured value; the multipart reader applies a fourth during model binding — `FormOptions.MultipartBodyLengthLimit`, a framework default of 128 MB derived from nothing. Raise the limit past it and a file in between passes the Content-Length guard and Kestrel's, is buffered in full, and dies in model binding as a **500 "An unexpected error occurred"** instead of the typed 413 the other guards produce. Safe today only because 50 MB is under 128 MB | E-03, E-30 |
| 38 | `UploadSizeLimitFilter` — the code answering E-03, a P0 — had **no test executing a single line of it**, and neither did the `[ServiceFilter]` wiring that makes it run at all. Deleting one line of `Program.cs` turns every upload into a 500, and `Program.cs` is excluded from coverage | E-03, E-31 |
| 46 | **Two participant rows for one person in a live lecture** — `JoinStreamAsync` checks `Participants.Any(...)` then inserts, and `Participants` had no constraint. The easiest of the three instances to reach: a LiveKit reconnect re-joins, a second tab joins, a retried request joins. It inflates the roster and the count the teacher is watching; `LeaveStreamAsync` deletes one row so the person leaves and their ghost stays; `ToggleHandRaiseAsync` resolves to one of the two arbitrarily | L-20 |
| 47 | **The participant count was arithmetic on a stale read** — `Participants.Count + 1` on join, `- 1` on leave, against a collection loaded at the top of the request. Two people joining at once both announce the same number and the class is told there are fewer people present than there are, with nothing to correct it until the next join or leave. A unique index does not fix this; the count has to be counted | L-22, L-23 |
| 48 | Neither join nor leave had a service-level test, and `RecordingStreamHubContext.NotifyParticipantCountAsync` **discarded its argument**. A double that drops a value cannot fail on the value being wrong — the third double-vs-reality finding in three cycles | L-22 |
| 43 | **Two accounts for one email address, and no race required.** `u.Email == email` is an exact, case-SENSITIVE comparison in Postgres, so `Jafar@x.com` and `jafar@x.com` were two accounts — one capital letter, no concurrency. The owner then signs in only when their capitalisation matches, a password reset for the other spelling finds nobody and (correctly, per A-13) answers as though it sent a code, and an administrator approves one row while the person signs in to the other and is told they are pending | A-34, A-35 |
| 44 | **`Users` had no unique index at all** — `HasKey("Id")` and an FK index on `RoleId`. `RegisterAsync` asks "is this taken?" then inserts, and a double-clicked Register button is two requests. Same class as defect 41, found by sweeping for it; ClassroomService was clean | A-38, A-40 |
| 45 | `StubUserRepository.FindByEmail` compared with `StringComparison.OrdinalIgnoreCase` while production compared exactly. **The double agreed with the assumption instead of with the database**, so `Registration_refuses_an_email_that_already_exists` passed throughout. The second instance of this meta-defect in two cycles, from the opposite direction to defect 42 | A-34 |
| 41 | **Two live streams for one session, with nothing to stop it.** `SessionStartedConsumer` guards a redelivery with `ExistsAsync` then `AddAsync` — two calls — and `Streams.SessionId` carried **no index at all**, unique or otherwise. At-least-once delivery means the message arrives twice; two concurrent invocations both pass the check before either insert. The consumer's own comment names the consequence: "every later lookup picks one arbitrarily", so students join one row while recording state and participant count attach to the other, permanently and silently | found by a test FAILING under load, twice — see below |
| 42 | `FakeStreamRepository` accepted two rows for one session, so the suite could not tell an idempotent consumer from one that usually wins the race. **A double more permissive than the database turns a real defect into a flake** — and this file already carried a comment about an earlier flake here that accused the product wrongly. This time the accusation was correct | L-01 |
| 40 | **Sentence splitting was English-only, and it made the semantic chunker inert in Arabic.** `_SENTENCE_RE` was `[.!?]` — none of Arabic's terminators. Arabic borrows the Latin full stop, so statements split and questions did not: four Arabic questions came back as **one** sentence. `SemanticChunker` then takes its `len(sentences) == 1` shortcut, **never calls the embedder**, and falls through to the token-window packer — so the semantic tier silently became the structural tier and `semantic_breakpoint_percentile` did nothing. Every chunking test in the suite is written in English | F-27, F-28 |
| 63 | **The super-admin surface could be deactivated out of existence, with no way back.** `SuperAdminService.DeactivateAdminAsync` refuses a super-admin target — its query filters to `Role.Name == Admin` — but `UserStatusService`, behind `PUT /api/super-admin/users/{id}/status` and its bulk sibling, reached the same accounts and never looked at who the target was. The self-target guard covers one super admin; with two, A disables B and B disables A. Deactivation revokes sessions immediately, nothing in the platform can appoint a replacement (`CreateAdminAsync` mints an **Admin**), and the directory lists super admins alongside everyone else — so "select all, deactivate" gets there by accident. Recovery is editing the database by hand | C-26, C-27 |
| 59 | **Any authenticated account could post chat into any live lecture**, from its session id. The three write paths on `InteractionService` took a `userId` and never consulted it, and a chat message is broadcast to everyone in the room with the sender's display name against it, while the class is running. Reactions and questions the same | G-44 |
| 60 | **Any authenticated account could read any lecture's entire chat history and question list** — both read methods took no caller at all | G-45 |
| 61 | **Any authenticated connection could subscribe to any session's SignalR broadcast group** — the live feed of every message, reaction, hand-raise and participant count. `StreamHub.JoinStreamRoom` was one `AddToGroupAsync` call and nothing else, and a hub method has no controller, filter or attribute in front of it that ever sees the session id | G-46 |
| 62 | `RecordingStreamHubContext` discarded the arguments to `BroadcastChatMessageAsync`, `BroadcastReactionAsync` and `NotifyHandRaisedAsync`. **The fourth double-vs-reality finding, in the same double that produced the third** — fixed one method at a time as each cycle happened to need it, which is how three of them were still discarding | G-44 |
| 55 | **The LiveKit join token was handed to anyone who asked.** `GET /api/streams/{sessionId}` checked that the stream existed and that it was Live, and nothing about the caller — so any account in the platform could name any live session and receive a token for it. The token is not a step towards entry, it *is* entry: §7.4's own note says "once LiveKit holds it our code is never consulted again", so there was no second place this could have been caught and no record that it had happened | G-02 |
| 56 | **And publishing rights came from the caller's own role claim.** `isTeacher = role.Equals("Teacher", …)`, where `role` was read off the requester's token and had nothing to do with this classroom — so any Teacher-role account could walk into any live lecture **with camera and microphone**, appearing to the room as a teacher. The parameter is gone rather than better checked | G-36 |
| 57 | **Joining the roster was separately unguarded.** A different endpoint from the token, and the one that writes the participant row the teacher's screen counts and that hand-raise and chat look up — so a stranger could appear in a lecture's roster even where the publish policy would not let them speak | G-39 |
| 58 | **Every refusal in StreamingService was a 401**, which the browser reads as an expired access token: the axios interceptor refreshes the session — rotating the refresh token — replays the request and is refused again. A refresh that failed during that would have signed the user out and sent them to `/login` for clicking on a lecture they are not enrolled in. Found by asking what the new refusal would actually look like to the front end, not by a mutation | G-02 follow-through |
| 49 | **Any teacher could start any session in the platform, from its id alone.** `StartSessionAsync(sessionId, ct)` took no caller *and no classroom* — its route is `/api/classrooms/{classroomId}/sessions/{sessionId}/start`, and the action never bound the classroom, so the id in the URL was decorative and nothing cross-checked that the session belonged to it. `[Authorize(Roles = "Teacher")]` was the whole check. The class goes live, the media room opens, recording begins if configured; the teacher who owns it later gets "Only scheduled sessions can be started" with nothing naming who did it | B-25, B-30 |
| 50 | **Any teacher could schedule a session in any other teacher's classroom**, visible to that classroom's students. Same shape, same route family, same role attribute standing in for a tenancy check | B-25 |
| 51 | **Any authenticated user could read any classroom's roster by id** — and `MemberResponse` carries each student's full name, so a personal-data disclosure rather than a metadata one. `RemoveStudentAsync` next door checks ownership *before* the membership lookup precisely so a classroom id cannot be used to probe who is enrolled; the listing answered the question outright | B-24 |
| 52 | **Any authenticated user could list any classroom's material by id** — file names and S3 keys. The two methods immediately below it, indexing status and download, both gate on membership first: the bytes were protected and the catalogue naming them was not. This endpoint is the worked example §11.2's own rule used for "a GET can be left to a membership check in the service layer" | B-24 |
| 53 | **Any authenticated user could read any classroom's timetable by id** — session titles, descriptions and times. `SessionService` had no service-level tests at all, and `EndSessionAsync` — the one method on it that is written correctly, scoped and ownership-checked — sits directly beneath the three that were not | B-24 |
| 54 | **The same membership rule written out five times**, byte-identical, in `QuizService`, `ClassroomFileService`, `ClassroomQaService`, `ClassroomRecordingService` and `ClassroomSummaryService`. Not a defect yet, which is the point: the next reader to change it changes the copy in front of them and the other four go on enforcing the old rule with nothing reporting the disagreement. §11.7 spent two surviving mutations learning that with *two* copies of the quiz deadline | B-31 |
| 39 | **The account audit trail recorded intent, not outcome.** C-09's records were written inside the decision loop, before the transaction that makes them true — and the batch is atomic, so a bulk approve that timed out on its commit wrote fifty Information lines saying fifty accounts had been approved, approved **none** of them, and wrote nothing at all to say it had failed. The log exists to answer "was this person's account deactivated, by whom, and when"; after a rolled-back batch it answered yes when the truth was no | C-11 work, from reading two passing tests together |

## 9. Mutation testing

Coverage says a line ran; it does not say anything would have noticed if it were wrong. Every
work-plan item in §7 onwards ends with a mutation spot-check: a deliberate defect introduced into
the code under test, to confirm the suite fails. Roughly 334 mutations across the project so far.

Thirteen survived, and each one meant a test was passing for the wrong reason. (This said "nine"
until it was counted against the table below, which had thirteen rows by then — the count was
edited by hand while the rows were appended. It is counted now, and the count is checked against
the table whenever a row is added. One row is present because it is the most instructive entry here
and **not** because it survived; it says so.)

| Mutation that survived | What it exposed |
|---|---|
| Removing the retained-idea release on crash | The test asserted an empty store that was empty anyway |
| Removing the shared refresh promise | The test read recorded headers, but the retry mutated the shared config object |
| Removing the `_retry` guard | The test **hung** instead of failing — the refresh endpoint now succeeds only once |
| Removing the teacher check on quiz submission | A teacher is not normally enrolled, so the enrolment check refused them anyway — until one enrols |
| Removing `[Authorize(Roles = "Teacher")]` from `ClassroomsController.Delete` | The exemption list was keyed on action name; an entry meant for recordings was covering it |
| Removing a `TimeoutSeconds` from compose | The rule's file map was keyed on `path.name`, and six compose files share one name — five services were never checked |
| Renaming a `TimeoutSeconds` property out of UserManagementService's options | Twice. First the settings rule asked "does ANYTHING read this?", and ClassroomService binds the same section with the same property name, so the wrong service vouched for it. Then it still passed, because the rule was reading TEST sources — UMS's own options test builds a config containing that exact key, so the test was vouching for the production code |
| Removing the drawer's `rtl:-translate-x-full`, and the toggle knob's anchor | The RTL rule listed only utilities that HAVE a logical counterpart, so `translate-x` — which has none — was excluded, and the two defects the rule had just prompted fixes for were the two it could not see. The anchoring case then survived a second time because it was scoped per file rather than per className expression |
| Removing the `HasStarted` guard from `GlobalExceptionHandler` | **It did not survive — the run did.** A recursive test double overflowed the stack and killed the test host, so the run reported `Passed!` with 12 of 235 tests executed. Checking the word and not the count would have recorded a fixed defect as unfixable |
| Removing the unique-index modelling from `FakeStreamRepository` | Honest, and recorded rather than engineered around. Once `ConsumeAsync` was made to publish **sequentially** — which is what a redelivery actually is — the second consume returns at `ExistsAsync` and never reaches `AddAsync`, so the double's constraint is not exercised there. It exists so the fake cannot be more permissive than the schema; the concurrent case has its own file, with its own constrained repository |
| Removing the `\b` anchor from the TRX reader's counter lookup | **An honest survivor, and the comment claiming otherwise was the thing that changed.** The anchor guards `executed="` against matching inside `notExecuted="` — and it cannot, because the counter is spelled with a capital E, so no attribute order collides. Recorded in the test that was written to kill it. Two neighbouring mutations did earn their keep: one exposed a "test" asserting `61 - 59 == 2`, a tautology exercising no product code at all |
| Answering `IsMember: true` for an unknown classroom on the internal membership route | The service's "returns null for a classroom that does not exist" was tested; the **controller's translation of that null into a 404** was not. So the fail-OPEN direction went unchecked on the one route whose answer decides entry to a live lecture — and a stream naming a deleted classroom is precisely when it fires |
| Dropping the classroom scoping from `StartSessionAsync` | **The test could not fail.** "A session started under the wrong classroom is 404" asked with an *invented* classroom id — and an unknown classroom is 404 from the ownership check too, so both the fixed and the broken code answered the same. Rewritten to ask as the *other* teacher, under a real classroom that is genuinely theirs, for a session that is not: the probe the 404 exists to defeat, and the one that was not being made |
| Swapping `StartTls` for `StartTlsWhenAvailable` in the SMTP sender | **The fake server was too well-behaved.** It withheld AUTH until the connection was encrypted — what a careful server does — so a client that had silently downgraded failed anyway, for want of a mechanism rather than by its own decision. The test asserted "no password reached the server" and that was true for the server's reason. Once the fake offers AUTH PLAIN in the clear, as a misconfigured or hostile one does, MailKit **completes the send in plaintext with no exception at all** |

One "mutation" turned out never to have applied (attribute order in the source differed), which
is worth recording on its own: a patch that silently does not apply is indistinguishable from a
test that works.

## 10. What is not covered, and why

| Gap | Reason |
|---|---|
| Runtime 401/403 assertions, and the IDOR sweep over every `{id}` route param | Need a running host and real data. The cross-tenant *decisions* are no longer in this row: B-04/B-05 are covered at the unit level and found five open endpoints, and B-07's question — can this method refuse at all — is now a rule over the service layer (B-23). G-02, the LiveKit join token, is covered too, including the cross-service contract the check now depends on |
| Integration suites (§8.2–8.6), migrations (§11.8), concurrency races (§11.7) | Need containers |
| Browser-level E2E (§11.12) | Needs Playwright; one journey would cover the §4/§5/§6 seams |
| Email verification of a registered address (A-10) | **The feature does not exist.** Registration lands in `Pending` and an administrator approves it, which is the stronger gate — but nothing proves the address belongs to the registrant, and the approving administrator gets no signal either way |
| Performance, stress, breaking point (§10.2–10.3) | k6 not installed |
| Behaviour under a stopped dependency (§10.4 runtime half) | Needs containers |
| ~~The .NET equivalent of the settings-binding rule~~ | **Closed (Q-14).** Left here struck through because the stated reason — "a static rule produced false positives on every service" — was true of the rule that had been tried and was the wrong conclusion to draw from it. Recognising all five ways .NET reaches a setting reports zero across the 54 the compose files pass |
| SMTP delivery to a real mail provider | N-10 covers the transport against a loopback server, which is where the protocol lives. Whether Gmail accepts the message, and what it does with it, is Gmail's behaviour and not ours |
| Penetration testing | Out of scope; dependency scanning (§11.13) is the cheap partial substitute |
| Load beyond a single host | Everything runs on one machine, so §9–§10 numbers are directional, not capacity planning |
