# IntelliLect cross-service E2E — "teacher teaches → gets feedback"

This suite drives the **whole running platform** over HTTP + LiveKit to exercise the
core product scenario: a teacher teaches a live session and the AI assistant sends
back suggestions to improve their teaching.

It is a real end-to-end test — it starts nothing itself. You bring the platform up
with `docker compose`, then run pytest against it.

## What it covers

```
UserManagementService   register → admin-approve → login (teacher + students)
ClassroomService        create classroom → enroll → create session → START session
StreamingService        mints LiveKit tokens; on start, spins the room + notifies the agent
RagService        seeded PDF is ingested + embedded → retrievable material
LiveAssistantService    agent joins room → STT → idea → retrieve → LLM eval → pace → feedback
LiveKit                 the synthetic teacher publishes speech and RECEIVES the feedback
```

Two tests (`test_teaching_feedback_e2e.py`):

| Test | Marker | Needs | Proves |
|------|--------|-------|--------|
| `test_session_orchestration_seams` | — | platform up | The full cross-service wiring: starting a session cascades Classroom → Streaming → LiveKit token + agent registration. Deterministic; no audio/LLM. |
| `test_teacher_teaches_and_gets_feedback` | `media` | + Ollama, TTS | The real AI loop: seeded material, teacher speaks a wrong claim, transcript persists, and the teacher **receives an improvement suggestion**. |

> Not covered here: the egress **recording** MP4 and the post-session **summary**.
> Those need a `livekit/egress` container (absent from compose) and reaching the
> unpublished `/api/internal/*` ports on Classroom/Streaming. See "Extending" below.

## Why a synthetic teacher

The deployed LiveAssistant hardcodes the real LiveKit/Whisper/Ollama pipeline — there
is no fake-mode env switch, and no endpoint to read back delivered feedback. So the
test plays a **real participant**: it joins the room with the teacher's own join token
(identity == the `teacherIdentity` the agent watches), publishes a spoken WAV as a mic
track, and — because feedback is a LiveKit data message addressed to the teacher
identity — **receives its own feedback**. No production code is modified.

## Prerequisites

1. **The platform, running** — from `backend/`:
   ```bash
   docker compose up -d          # or: build one-by-one per run-services.txt
   ```
   Health is polled by the suite, so a cold start is waited on.

2. **Ollama on the host** with the models the services expect (media test only):
   ```bash
   ollama serve                  # must listen on 0.0.0.0:11434
   ollama pull qwen2.5:7b-instruct
   ollama pull qwen3-embedding
   ```

3. **LiveKit media routing** (media test only). LiveKit is configured with
   `node-ip 192.168.1.104`. WebRTC media only flows if that IP is reachable from
   both the containers and this host. If your host LAN IP differs, set it in
   `StreamingService/docker-compose.unit.yml` (livekit `--node-ip` + `livekit.yaml`
   `node_ip` + `LiveKit__Host`) and override `E2E_LIVEKIT_WS_URL`.

4. **A TTS for the teacher's voice** (media test only). One of, in priority order:
   - `export E2E_TEACHER_WAV=/path/to/your.wav` — any 16-bit PCM WAV, **preferred**;
   - a piper voice: install `piper-tts`, then drop a `*.onnx` voice (+ its `.onnx.json`)
     into `assets/` — it is auto-discovered. To fetch one:
     ```bash
     uv pip install piper-tts huggingface_hub
     python -c "from huggingface_hub import hf_hub_download as d; \
       [d('rhasspy/piper-voices', f, local_dir='assets') for f in \
        ('en/en_US/ryan/low/en_US-ryan-low.onnx','en/en_US/ryan/low/en_US-ryan-low.onnx.json')]"
     ```
     (or point `E2E_PIPER_MODEL` at an `.onnx` elsewhere);
   - `espeak-ng` installed on PATH.
   Without any, the media test **skips** (the orchestration test still runs).

## Running

### Recommended: in the compose network (most reliable)

`run-in-network.sh` builds a tiny runner image and runs pytest **inside** the compose
network. This is the robust way here because it (a) reaches services directly, bypassing
nginx's fixed 60s proxy timeout on the slow synchronous session-start, and (b) puts the
synthetic teacher on the same network as LiveKit so media flows container↔container.

```bash
cd backend/tests/e2e
./run-in-network.sh                 # media loop
./run-in-network.sh -m "not media"  # just the cross-service wiring
./run-in-network.sh -k feedback -s  # any pytest args
./run-in-network.sh -m smoke        # is this deployment alive? (see below)
./run-in-network.sh -m latency      # the §9 latency budgets (see below)
./run-in-network.sh -m internal     # the /api/internal shared-secret guard
```

### Directly on the host (venv)

```bash
cd backend/tests/e2e
uv venv && source .venv/bin/activate
uv pip install pytest pytest-asyncio httpx "livekit>=1.0" "minio>=7.2" "reportlab>=4.0" "gTTS>=2.5" "websockets>=13"

pytest -m "not media"   # cross-service wiring (needs a responsive host — see below)
pytest -m media         # the AI feedback loop
pytest                  # both
```

> On a memory-pressured host the synchronous session-start can exceed nginx's 60s
> gateway timeout; the test retries, but the in-network runner (direct URLs) avoids
> the cap entirely. Prefer `run-in-network.sh` when the host is loaded.

## Smoke (`-m smoke`)

`test_smoke.py` is the shortest sequence that proves a deployment is alive: every
service probed, then one login, one classroom read, one session start (the cascade
through ClassroomService → StreamingService → LiveKit → LiveAssistantService). It
asserts its own runtime against a 60s budget, because a smoke suite that takes two
minutes stops being run, and then it stops being true.

```bash
cd backend/tests/e2e
./run-in-network.sh -m smoke
```

**It does not wait for the platform.** conftest's readiness gate polls for two minutes,
which is right for a functional suite and wrong here — the question is "is this
deployment alive *now*", and waiting turns a dead service into a slow pass. Set
`E2E_SMOKE_WAIT_S` when running against a stack that is still coming up.

Run it **in-network** if you want EmailService covered: it publishes no host port and
nginx does not route to it, so from the host that one probe skips (loudly — bulk
approve fans out through it).

`test_smoke_inventory.py` (also `-m smoke`) needs **nothing running**. It reads the
compose files and checks that every service is either probed or exempt with a reason,
in both directions, plus that every service actually exposes the `/health` the probes
assume. A smoke suite decays invisibly — a service gets added and never gets a probe,
and the suite passes while proving less each release.

## Everything that needs nothing running (`-m offline`)

```bash
cd backend/tests/e2e && .venv/bin/python -m pytest -m offline
```

73 tests: the smoke inventory, the latency harness's own arithmetic, the dependency-ceiling and
settings-binding rules, and the results collector. **Use this marker, not the topical ones** —
`-m smoke` and `-m latency` each also select a module that needs the real platform, so with
nothing up that selection waits two minutes and then fails.

## Results collector (§10.5)

```bash
cd backend/tests/e2e && .venv/bin/python collect_results.py
```

Fills the generated blocks in `docs/testing-results.md` from coverage artifacts and the latency
harness's output. Produce the artifacts first; it will not run the suites for you, and it will not
print a number from an artifact older than the code it measures — that is reported as stale.

## Resilience config (`-m resilience`)

`test_resource_ceilings.py` and `test_settings_binding.py` need **nothing running**. They are
§10.4's configuration half: every configured base URL declares its own timeout, storage calls
are bounded and do not retry forever, everything that talks to MinIO reads the same two
variables, and — the one that matters most — **no variable a deployment sets binds to nothing**.

Both Python services declare `extra="ignore"`, so a renamed setting goes quietly dead. That is
how `RAG_BASE_URL` came to be set in compose while `Settings` still declared
`knowledge_base_url`, and the live assistant retrieved course material from an empty base URL.

```bash
cd backend/tests/e2e && .venv/bin/python -m pytest -m resilience
```

## Latency (`-m latency`)

`test_latency.py` measures the session-broadcast hops and writes `latency-results.md`
next to it — the table the report consumes, filled in rather than transcribed. What
each hop is, and what it is allowed to cost, is in **`docs/latency.md`**; do not read a
number out of the results file without it, because two of the five hops do not measure
what their names suggest.

```bash
cd backend/tests/e2e
./run-in-network.sh -m latency
```

Run it **in-network**: through the gateway the SignalR hop would also be measuring
nginx's proxying, which is not what the budget is about. The assistant hop (L-3) reads
the service's own histogram, so it reports "not measured" until a feedback run has
happened on that instance — do `./run-in-network.sh -m feedback` first if you want it
in the table. The audio hop (L-4) needs UDP media to actually flow, and reports itself
as not-measured with the reason rather than failing the run when it does not.

`test_latency_support.py` (also `-m latency`) is the odd one out in this directory: it
needs **nothing running**. It covers the harness's own percentile arithmetic, warm-up
rule, budget judgement and SignalR framing, because the harness will sit unrun for a
while and its output later becomes measured fact in the report.

## Configuration

All defaults match `docker-compose.yml`. Override via env (see `config.py`):

| Env var | Default | Purpose |
|---------|---------|---------|
| `E2E_GATEWAY_URL` | `http://localhost` | nginx gateway (User/Classroom/Streaming) |
| `E2E_LIVEASSISTANT_URL` | `http://localhost:8084` | agent internal API |
| `E2E_KNOWLEDGE_URL` | `http://localhost:8083` | knowledge internal API |
| `E2E_INTERNAL_SECRET` | `changeme-internal-secret` | `X-Internal-Secret` |
| `E2E_LIVEKIT_WS_URL` | `ws://192.168.1.104:7880` | synthetic teacher connection |
| `E2E_MINIO_ENDPOINT` | `localhost:9000` | seed-material upload |
| `E2E_FEEDBACK_TIMEOUT_S` | `180` | window to receive feedback |
| `E2E_TEACHER_WAV` / `E2E_PIPER_MODEL` | — | TTS source |

## How the media assertion stays honest

Real STT + a 7B LLM are not bit-deterministic, so the test:
- **hard-asserts** the deterministic facts — transcript persisted (`segmentCount > 0`),
  an idea boundary was detected, and the brain evaluated it (Prometheus counters);
- seeds a **clear** contradiction (doc: water boils at 100 °C; teacher says 50 °C) and
  gives feedback a generous window, so the "suggestion delivered" assertion is reliable
  rather than flaky.

## Media transport (why the media test may skip)

The media test needs real **WebRTC media** to flow between the synthetic teacher and
the LiveKit server. Signaling (WebSocket) almost always works; the *media* (ICE/UDP)
is the fragile part, because LiveKit is pinned to advertise `--node-ip 192.168.1.104`
(so LAN browsers can reach it). Two host conditions block the synthetic participant:

- **A VPN.** If a VPN is active, its firewall/kill-switch often drops the local UDP
  between the docker containers (LiveKit's ICE stats then show the teacher's candidates
  as `srflx` via the VPN exit IP, and every candidate pair fails with 0 responses).
  **Fix: run with the VPN off, or configure it to allow the LAN + docker subnet.**
- **Docker Desktop.** Its Linux VM breaks the host↔container UDP hairpin to the pinned
  node-ip. `run-in-network.sh` mitigates this by running the test *inside* the compose
  network and DNAT-ing the advertised node-ip to the livekit-server container IP
  (needs `--cap-add=NET_ADMIN`, already set). On **native Linux docker** this is not
  needed and media just works.

When media can't establish, the test **skips** with a clear reason (the cross-service
wiring is still fully covered by `test_session_orchestration_seams`). The two reliable
ways to get a green media run: **native Linux docker with no VPN**, or provide a TURN
server. Everything else in the loop (STT, retrieval, LLM eval, feedback delivery) works
once media connects.

## Extending: recording + summary

To also assert the MP4 recording and the session summary you would:
1. add a `livekit/egress` container to compose (egress writes the MP4 to MinIO);
2. expose Classroom/Streaming `/api/internal/*` on host ports via a
   `docker-compose.e2e.yml` overlay;
3. end the session via `POST /api/internal/streams/{sid}/end`, then assert the
   `SessionRecordingReadyMessage` lands (recording `Available`) and the summary is
   produced. This is left as a follow-up.
