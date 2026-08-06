# Session broadcast latency — what is measured, and what it is allowed to cost

Work-plan §9. The harness that produces these numbers is
`backend/tests/e2e/test_latency.py`; it writes `backend/tests/e2e/latency-results.md`.

```bash
cd backend && docker compose up -d
cd tests/e2e && ./run-in-network.sh -m latency
```

---

## 9.1 What is being measured

A latency figure is worthless without the two events it spans and the clock it was
taken on. Every hop below states all three, because most of the ways this measurement
could be wrong are ways of being vague about exactly that.

| Hop | Starts when | Ends when | Observed by | Clock |
|---|---|---|---|---|
| **L-1** chat | the sender's frame leaves the socket | the *other* participant's `ReceiveChatMessage` frame arrives | two hub connections in the harness process | one |
| **L-2a** quiz signal | the teacher's `POST …/publish` is written | a student's `QuizChanged` frame arrives | teacher HTTP + student socket, same process | one |
| **L-2b** quiz visible | as L-2a | the student's follow-up `student-view` fetch returns | same | one |
| **L-3** assistant | an idea boundary closes | feedback is delivered to the teacher | the service's own histogram | in-process |
| **L-4** audio transit | a tone frame is captured by the publisher | that tone arrives at a subscriber | two LiveKit participants, same process | one |

### One clock, and why that constraint drives everything

The tempting design is to stamp a timestamp into the message on the server and
subtract it on the client. It is wrong here. The services run in containers and the
harness runs on the host; subtracting one clock from another measures skew as much as
latency, and on a laptop that has suspended, skew is routinely larger than the hop.

So the harness plays **both ends of every hop itself** — it sends as the teacher and
receives as the student, in one process, on one `time.perf_counter()`. The only
server-side numbers used are L-3's, and those come from a histogram the service fills
in-process, where there is no second clock to reconcile.

The consequence is that these are **user-perceived** figures, not server-side ones.
That is the right choice for a budget: nobody experiences the hub's fan-out time. The
server-side split is a separate instrument — see §9.2 below — and it is what tells you
*where* a missed budget went.

### Two hops that are not what their name suggests

**L-3 is cumulative unless this run drove it.** The assistant's histogram counts every
idea since the process started. Run `./run-in-network.sh -m feedback` first if the
figure needs to belong to a specific build; otherwise it reports what the instance has
seen. Its observations are charged at their bucket's *upper* bound, so the reported
figure over-states — deliberately, since a latency number that errs should err high.

**L-4 is SFU transit, not glass-to-glass.** The work plan asks for glass-to-glass, and
glass-to-glass cannot be measured from here, because its two largest terms happen
inside a browser this harness is not:

```
mic capture buffer  →  [ encode → SFU → decode ]  →  playout jitter buffer
   ~10–40ms, browser     ← L-4 measures this →         ~40–200ms, adaptive
```

The jitter buffer alone is usually larger than everything L-4 measures, and it adapts
to network conditions, so it cannot even be assumed as a constant. What L-4 gives is
the **floor**: the portion of the path our deployment owns, and the only portion any
change to our infrastructure can move. Reported as glass-to-glass it would understate
the real figure by a factor of two or more, so it is not reported that way here and
must not be in the report either.

The remaining term — a *second real participant on a different machine* — is still out
of reach, but not for the reason the work plan originally gave. LiveKit's `node_ip` is
pinned to `127.0.0.1` for Docker Desktop, and that does not stop two participants on
the *same* host from connecting, which is what L-4 does. What it stops is a measurement
across a real network, which is the one that would include real jitter. L-4 on loopback
is therefore a lower bound on a lower bound, and the report should say so.

---

## 9.2 Instrumentation

Two instruments exist, and they answer different questions.

**Client-observed, from the harness.** The hop as experienced. This is what the budgets
below are stated against. It needs no product change at all, which is itself the point:
the paths measured are byte-for-byte the paths the browser uses (`skipNegotiation` +
WebSockets, same as `useStreamHub.ts`), so there is no instrumented variant that could
drift from the shipped one.

**Server-side, `signalr_broadcast_duration_seconds{event}`** (StreamingService,
`BroadcastMetrics`). How long the hub took to fan one event out to a session group.
Added for §9.2 and tested by `BroadcastMetricsTests`. When L-1 misses its budget, "it
was slow" is not a finding; this is what splits it into server time and wire time.

Three rules the instrumentation follows, each of which is a way it could have distorted
the number it exists to report:

1. **Stamp before you parse.** Arrival is recorded the instant a frame comes off the
   socket, before JSON decoding and before dispatch. Microseconds, but writing it the
   other way round is how a harness folds its own work into the result.
2. **A failed broadcast records nothing.** Failures return fast; feeding them into a
   latency histogram moves the percentiles in the reassuring direction, which is the
   one direction in which a latency metric actively lies.
3. **No polling.** "Quiz appeared" is measured on the push channel, not by polling
   `GET`. A 500ms poll would add up to 500ms of quantisation to a hop budgeted at 700ms.

**Rejected: `signalrcore`.** It runs its receive loop on a background thread with its
own sleep-based poll, and that interval lands directly inside the number being
measured. The hand-rolled client in `support/signalr.py` exists for that reason and no
other.

---

## 9.3 Budgets

Each is derived from what the *feature* needs, not from what was measured. A budget set
to last week's number cannot be missed, and therefore cannot tell you anything.

| Hop | p50 | p95 | Why that number |
|---|---|---|---|
| **L-1** chat | 150 ms | 350 ms | Text a user did not send has no perceptual deadline as sharp as an echo, but conversation stops feeling live somewhere past a third of a second. The path is one DB insert and one socket hop, so p50 above 150ms means something structural — pool exhaustion, a missing index, a GC pause — not physics. |
| **L-2a** quiz signal | 300 ms | 700 ms | Crosses two services (ClassroomService → internal HTTP → StreamingService → socket) plus a commit. Three network hops at LAN latency plus a write should not reach 300ms; 700ms at p95 leaves room for one retry-free slow commit. |
| **L-2b** quiz visible | 600 ms | 1200 ms | **This is a fairness budget, not a comfort one.** `ClosesAtUtc` is set to `PublishedAtUtc + totalSeconds` on the server, so every millisecond between publish and the student holding the quiz is deducted from their answering time. Against the shortest sensible per-question timer (30s), 1200ms is 4% — under the 5% we are willing to take from a student without compensating for it. If per-question timers ever go below 30s, this budget must come down with them. |
| **L-3** assistant | 5 s | 15 s | Feedback exists to be actionable while the teacher is still on the topic. An idea runs 30–60s of speech, so advice arriving 15s after the idea closed is still about the current point; at 30s it is about the previous one and is worse than silence. p50 of 5s matches the pipeline's own stated target. Today's measured 3.10 / 12.18 / 2.02s passes — but the 12.18s is one Gemini call's thinking tokens, i.e. a vendor number, not an architectural one, and it is the reason p95 is set at 15 rather than 8. |
| **L-4** SFU transit | 80 ms | 150 ms | ITU-T G.114 puts comfortable one-way mouth-to-ear at ≤150ms and tolerable at ≤400ms. Since L-4 excludes the capture and playout buffers, its budget has to be *much* tighter than 150ms end-to-end to leave those their room; 80/150 for the transit portion keeps the full path plausibly inside G.114's comfortable band on a LAN. |

### How a run is judged

- **p95 is the verdict**, p50 is the health check, and `worst` is always printed
  beside them. At twenty samples, nearest-rank p95 is the second-slowest observation,
  so exactly one catastrophic sample does not move it — that is what p95 means, and it
  is why the outlier is reported separately rather than being the thing judged.
- **The first sample of every series is discarded.** It pays for a cold connection
  pool and a cold code path. Keeping it would make it the reported `worst` on every
  run, and that column would then carry no information.
- **A hop that could not be measured is reported as not measured**, with the reason,
  and never as a pass. A hop that was measured and then cut short is judged on the
  samples it got and marked `INCOMPLETE`, because throwing those away would excuse a
  hop that was genuinely slow.
- **Every breach is collected before anything fails**, so the table can be read whole.
  A chat p95 of 900ms means one thing alone and something else when the quiz hop is
  slow too.

---

## Status

| Item | State |
|---|---|
| 9.1 definitions | done — above |
| 9.2 instrumentation | done — `BroadcastMetrics` (server), the harness (client); both tested |
| 9.3 budgets | done — above, with derivations |
| 9.4 harness | **authored and unit-tested; not yet run.** Needs `docker compose up -d`. |

The harness's own arithmetic and protocol handling are covered by
`backend/tests/e2e/test_latency_support.py`, which runs today with no containers —
percentile ranks, the warm-up rule, budget judgement, and the SignalR framing. That
suite exists because the harness will sit unrun for a while and its output goes into
the report as measured fact; a percentile off by one rank would be invisible and
uncorrectable after the fact.
