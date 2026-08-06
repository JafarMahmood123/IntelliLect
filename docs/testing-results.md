# Testing results

The template the report's testing chapter fills in (work-plan §10.5). Every number here either
came from an artifact or says which command produces it — nothing is transcribed.

Regenerate the machine-derived blocks after producing the artifacts:

```bash
cd backend/tests/e2e && .venv/bin/python collect_results.py
```

<!-- generated:stamp -->
_Generated 2026-08-06 18:21 UTC by `backend/tests/e2e/collect_results.py` from the artifacts present at that moment._
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

| Suite | Tests | Command |
|---|---|---|
| UserManagementService | 183 | `dotnet test UserManagementService/tests/*/*.csproj` |
| ClassroomService | 426 | `dotnet test ClassroomService/tests/*/*.csproj` |
| StreamingService | 168 | `dotnet test StreamingService/tests/*/*.csproj` |
| EmailService | 28 | `dotnet test EmailService/tests/*/*.csproj` |
| RagService | 351 (+9 skipped) | `cd backend/RagService && .venv/bin/python -m pytest` |
| LiveAssistantService | 381 (+3 skipped) | `cd backend/LiveAssistantService && .venv/bin/python -m pytest` |
| front-end-web | 346 in 38 files | `cd front-end-web && npx vitest run` |
| Cross-service E2E | 149 collected, of which **73 need nothing running** | `cd backend/tests/e2e && .venv/bin/python -m pytest` |
| **Total** | **2,032** | |

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
| UserManagementService | 51.2% | 34.6% | measured 2026-08-06 (899 lines) | `cd backend/UserManagementService && dotnet test --collect:'XPlat Code Coverage' -s ../coverlet.runsettings` |
| ClassroomService | 62.3% | 62.9% | measured 2026-08-06 (1208 lines) | `cd backend/ClassroomService && dotnet test --collect:'XPlat Code Coverage' -s ../coverlet.runsettings` |
| StreamingService | 51.5% | 34.4% | measured 2026-08-06 (651 lines) | `cd backend/StreamingService && dotnet test --collect:'XPlat Code Coverage' -s ../coverlet.runsettings` |
| EmailService | 72.0% | 87.5% | measured 2026-08-06 (168 lines) | `cd backend/EmailService && dotnet test --collect:'XPlat Code Coverage' -s ../coverlet.runsettings` |
| RagService | 83.0% | 68.2% | measured 2026-08-06 (3911 lines) | `cd backend/RagService && .venv/bin/python -m pytest --cov=app --cov-report=xml` |
| LiveAssistantService | 87.2% | 74.3% | measured 2026-08-06 (2700 lines) | `cd backend/LiveAssistantService && .venv/bin/python -m pytest --cov=app --cov-report=xml` |
| front-end-web | 41.4% | 79.5% | measured 2026-08-06 (14857 lines) | `cd front-end-web && npm run test:coverage` |
<!-- /generated:coverage -->

### Read the layer, not the headline

<!-- generated:layers -->
| Service | Layer | Line |
|---|---|---|
| UserManagementService | Domain | 60.7% |
| UserManagementService | Application | 67.2% |
| UserManagementService | Infrastructure | 33.3% |
| ClassroomService | Domain | 100.0% |
| ClassroomService | Application | 97.8% |
| ClassroomService | Infrastructure | 39.5% |
| ClassroomService | Presentation | 19.0% |
| StreamingService | Domain | 100.0% |
| StreamingService | Application | 58.9% |
| StreamingService | Infrastructure | 47.0% |
| StreamingService | Presentation | 59.3% |
| EmailService | Application | 100.0% |
| EmailService | Infrastructure | 72.0% |
<!-- /generated:layers -->

**The headline moves for reasons that have nothing to do with testing, and this project has a
live example of it.** Coverlet reports on the assemblies a test run actually loads. If no test
ever touches an assembly, it is absent from the report entirely and the percentage is computed
without it.

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
On the headline, **one of seven components meets it** (LiveAssistantService, 87.2%; RagService is
close at 83.0%). On the Application+Domain reading above, ClassroomService and EmailService meet
it comfortably and UserManagementService and StreamingService do not. Either way it is not met
across the board, and the report should say so rather than average it away.

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
| 4 | UserManagementService had no `/health` endpoint at all | §10.1 smoke inventory |
| 5 | Three `/health` endpoints could only ever return 200 — every check reported `Degraded`, which maps to 200 | §10.1 smoke inventory |
| 6 | The E2E readiness gate was effectively checking nginx only | §10.1 |
| 7 | The classroom's teacher could take part in their own quiz, polluting the live respondent counts | §11.2 authorization rules |
| 8 | UMS was never configured to call LiveAssistant or RagService — every such call 401'd in the deployed compose | §14.3 |
| 9 | `GET /api/classrooms/{id}/members` threw for every classroom with students | §7.2 mapping tests |
| 10 | Token refresh signed users out despite succeeding, non-deterministically | §7.7 axios interceptor tests |
| 11 | `RefreshAsync` never re-checked account status — a disabled account kept working | §7.1 |
| 12 | The internal-secret guard failed **open** when unconfigured; StreamingService had no guard at all | §7b |
| 13 | HTML injection into outgoing mail; three consumers with no retry policy | §7.6 |
| 14 | `POST /api/internal/reembed` returned 202 for a refused run, contradicting its own docstring | §7.5 |
| 15 | A second, dead JWT minter in ClassroomService sharing the signing secret | §7.2 |
| 16 | 22 dependency advisories, 20 fixed; npm's proposed react-router "fix" refused as a net regression | §11.13 |

## 9. Mutation testing

Coverage says a line ran; it does not say anything would have noticed if it were wrong. Every
work-plan item in §7 onwards ends with a mutation spot-check: a deliberate defect introduced into
the code under test, to confirm the suite fails. Roughly 100 mutations across the project so far.

Six survived, and each one meant a test was passing for the wrong reason:

| Mutation that survived | What it exposed |
|---|---|
| Removing the retained-idea release on crash | The test asserted an empty store that was empty anyway |
| Removing the shared refresh promise | The test read recorded headers, but the retry mutated the shared config object |
| Removing the `_retry` guard | The test **hung** instead of failing — the refresh endpoint now succeeds only once |
| Removing the teacher check on quiz submission | A teacher is not normally enrolled, so the enrolment check refused them anyway — until one enrols |
| Removing `[Authorize(Roles = "Teacher")]` from `ClassroomsController.Delete` | The exemption list was keyed on action name; an entry meant for recordings was covering it |
| Removing a `TimeoutSeconds` from compose | The rule's file map was keyed on `path.name`, and six compose files share one name — five services were never checked |

One "mutation" turned out never to have applied (attribute order in the source differed), which
is worth recording on its own: a patch that silently does not apply is indistinguishable from a
test that works.

## 10. What is not covered, and why

| Gap | Reason |
|---|---|
| Runtime 401/403 assertions, cross-tenant reads, the IDOR sweep | Need a running host and real data (test-plan B-04…B-07) |
| Integration suites (§8.2–8.6), migrations (§11.8), concurrency races (§11.7) | Need containers |
| Browser-level E2E (§11.12) | Needs Playwright; one journey would cover the §4/§5/§6 seams |
| Contrast assertions on the feedback cards (H-10) | Needs a rendering check in both themes; the colour-blindness half is done (icon + label, not colour alone) |
| Performance, stress, breaking point (§10.2–10.3) | k6 not installed |
| Behaviour under a stopped dependency (§10.4 runtime half) | Needs containers |
| The .NET equivalent of the settings-binding rule | .NET's binder ignores unknown keys just as silently, but settings are read through a mix of options classes and direct `configuration["A:B"]` lookups; a static rule over that produced false positives on every service |
| Penetration testing | Out of scope; dependency scanning (§11.13) is the cheap partial substitute |
| Load beyond a single host | Everything runs on one machine, so §9–§10 numbers are directional, not capacity planning |
