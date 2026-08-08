# Load and stress harness — work-plan §10.2 / §10.3

> **Status: written, never run.** k6 is not installed on the development machine and these
> scripts have never been executed against a running platform. They are reviewed source, not
> results. The first real run should be expected to find mistakes *here* before it finds any in
> the platform — wrong field names, a threshold set from a guess, a `setup()` that outlives its
> patience. Treat a first-run failure as a bug in this directory until proven otherwise.

## What is here

| Script | Mix | Shape | The question it asks |
| --- | --- | --- | --- |
| `load-session-join.js` | a class arriving at one session | open model, ramping arrival | What does the join-token mint cost now that it makes a synchronous internal call to ClassroomService (§7.4d)? |
| `load-quiz-deadline.js` | concurrent submissions at the deadline | all VUs at once, one iteration each | Does the platform *keep* every submission it acknowledges? |
| `load-rag-search.js` | retrieval under load | constant arrival rate | What does a busy search surface cost, and does the classroom scope hold when the pool is saturated? |
| `load-bulk-approve.js` | bulk approve of a large batch | one request, one iteration | How large can a batch get before it crosses nginx's 60s proxy timeout — and what was applied when it does? |
| `stress-ramp.js` | ramp to failure | ramping arrival to collapse, then recovery | Does it *refuse* or does it *corrupt*? And does it come back? |

`lib/` holds the shared pieces: `config.js` (environment), `api.js` (the HTTP surface),
`provision.js` (everything that happens in `setup()`).

## Running

```bash
# k6 is not in the default apt repositories; see the Grafana apt instructions,
# or download the standalone binary.
k6 run backend/tests/load/load-session-join.js
```

The platform must be up (`docker compose up -d` from `backend/`) and **the three pending
migrations must have applied** — `IX_Streams_SessionId`, `IX_Users_Email`,
`IX_Participants_StreamId_UserId`. The last of those is not incidental here: `stress-ramp.js`
checks for duplicate roster rows, which is precisely what that index prevents. Running the
stress script against a database where the migration was refused would report a defect the
platform does not have.

### Variables

The same `E2E_*` names the functional harness uses, so one environment serves both.

| Variable | Default | Notes |
| --- | --- | --- |
| `E2E_GATEWAY_URL` | `http://localhost` | Through nginx on purpose — its worker count and proxy timeouts are part of what is measured. |
| `E2E_KNOWLEDGE_URL` | `http://localhost:8083` | RagService is not routed through the gateway. |
| `E2E_INTERNAL_SECRET` | `changeme-internal-secret` | |
| `E2E_ADMIN_EMAIL` / `E2E_ADMIN_PASSWORD` | seeded admin | Used only to approve the accounts each run registers. |
| `LOAD_STUDENTS` | `50` | Accounts provisioned in `setup()`. |
| `LOAD_PEAK_RPS` | `20` | `load-session-join.js`. |
| `LOAD_SEARCH_RPS` | `10` | `load-rag-search.js`. |
| `LOAD_BATCH_SIZE` | `200` | `load-bulk-approve.js`. |
| `LOAD_STRESS_PEAK` | `200` | `stress-ramp.js` peak arrival rate. |

## Design decisions worth knowing before reading the numbers

**Provisioning is in `setup()` and is not measured.** Registering fifty students is about a
hundred and fifty sequential requests. Doing it inside the VU loop would measure account
creation, and the admin-approval endpoint would become the bottleneck that hides the real one.
The cost is that `setup()` is slow and that a failure there aborts the run with an exception
rather than a metric — both correct, because a load run against a half-provisioned classroom
produces numbers that look like results.

**Open models, not closed ones.** Four of the five scripts use an arrival-rate executor rather
than a fixed pool of looping VUs. A closed model hides the failure being looked for: when the
platform slows down, VUs issue fewer requests, and the offered load quietly falls to whatever
the server can serve. Real students do not do that.

**Every request carries a `name` tag.** k6 tags requests by URL, so `/api/streams/{a-uuid}`
would produce one metric row per session and no threshold could be written against it —
thresholds match on tags. `named()` in `config.js` is the only way requests are issued here.

**Thresholds are the pass/fail, not a chart to read afterwards.** Each one is a claim about
what a user experiences, and each is commented with why that number and not another. The most
important is in `load-quiz-deadline.js`, and it is not about speed: a run can meet every
latency threshold and still fail the reconciliation, which is the point of that script.

**`stress-ramp.js` deliberately has no latency or error-rate threshold.** It is *supposed* to
push past the point where those would fail; thresholding them would abort the run at exactly
the moment it starts producing its result. The one hard rule there is `server_fault_rate` —
refusals are allowed at any volume, 500s are not at any volume.

## What these scripts do not cover

- **No media.** k6 has no WebRTC client, so nothing here measures LiveKit. The join path is
  measured up to the point where a browser would connect and no further. Glass-to-glass timing
  needs a browser (`docs/latency.md`, §11.12).
- **No SignalR.** The push channel's per-hop budgets are §9's, measured by the pytest harness
  in `backend/tests/e2e/test_latency.py`, which has a hand-rolled client. k6 has none.
- **No assistant loop.** It needs a model, and a load figure whose variance is a language
  model's mood is not a load figure.
- **Absolute numbers do not transfer.** Every result is about one machine running seventeen
  containers. The shape of a curve and the presence of a fault are the transferable parts.
