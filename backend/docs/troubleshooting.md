# Troubleshooting

Failures that cost real debugging time on this project, with the evidence that identifies each one
and the fix that actually worked. Every entry is something that was diagnosed wrongly at least once
first — the CHECK commands exist to stop that repeating.

Section numbers match the local `run-services.txt` runbook, so the two can be read together.

> **Docker Desktop is the common thread.** Five of these are the same underlying fact wearing
> different clothes: with Docker Desktop, `network_mode: host` means the **VM's** network namespace,
> not the developer's machine. On native Docker Engine most of this section does not apply.

---

## 1. Speech-to-text stops working (Groq returns 403)

**Symptom.** No transcription at all. `GroqSpeechToTextError` mentioning 403, or in a browser
`{"error":{"message":"Access denied. Please check your network settings."}}`.

**Cause.** Two independent blocks that stack:

| Block | What it is |
| --- | --- |
| **Geography** | The development connection geolocates to a region Groq refuses outright. Confirm with `curl -s https://api.groq.com/cdn-cgi/trace` → `loc=`. A VPN is the only way in, not the problem. |
| **Datacenter IPs** | Groq sits behind Cloudflare, which also blocks datacenter address space. Every commercial VPN exit is a datacenter IP, so a permitted country is **necessary but not sufficient**. |

A working exit must satisfy both. That is why "hop servers until it works" keeps breaking — one of
the two conditions lapses.

**The API key is almost never the cause.** Both failures are 403; tell them apart by the body:

```text
{"error":{"message":"Forbidden"}}          -> blocked at the Cloudflare edge
{"error":{... "code":"invalid_api_key"}}   -> actually the key
```

**Check** — from inside the container, which is the namespace transcription actually runs in.
`/v1/models` costs no audio quota:

```bash
docker exec live-assistant-service python -c "
import os,httpx
print(httpx.get('https://api.groq.com/openai/v1/models',
  headers={'Authorization':'Bearer '+os.environ['GROQ_API_KEY']},timeout=20).status_code)"
```

200 or 401 is fine. 403 is this problem.

**Fix.** VPN on, and find an exit that is both in a served country and not datacenter-flagged.
Expect to re-hunt periodically — a clean exit gets classified eventually.

**Do not split-tunnel.** `scripts/groq-split-tunnel.sh` correctly routes Groq around the tunnel, but
on this connection that hands Groq the blocked home address, which is refused every time. It is the
right tool for a datacenter-only block, and the wrong one here.

**Why there is no fallback.** Only Groq keeps up with a live lecture:

| Provider | Throughput |
| --- | --- |
| `groq` | ~5× realtime |
| `gemini` | 0.5–0.67× realtime |
| `faster_whisper` (local) | 0.2× realtime |

Both fallbacks consume audio **slower than a lecture produces it** and fall progressively further
behind. They are "some transcription beats none", not a configuration to be evaluated on.

**Known weakness.** A single 403 currently ends the whole session — `session_crashed` → `agent_left`,
about 4s after joining. Until retry/backoff is added around window transcription, one transient blip
mid-lecture stops the assistant for the rest of the class.

---

## 2. "Connection Lost" — participants dropped from a live session

Sections 2, 2b, 2c and 2d are **indistinguishable in the browser**. Run this first; it decides which
half of the section applies, in under a second:

```bash
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:7880/
```

- **`000`** — LiveKit is unreachable from this machine. Go straight to **2d**. Nothing else applies.
- **`200`** — LiveKit is reachable, so the fault is in the media path. Continue with 2 / 2b / 2c.

### 2. Dropped after ~13 seconds — wrong advertised node IP

**Symptom.** Joins, media never connects, dropped ~13s later, repeats. LiveKit logs
`SIGNAL_SOURCE_CLOSE` with `connectionType: unknown` and `sessionDuration: 0s`.

**Cause.** On Docker Desktop, `network_mode: host` is the VM's namespace, whose interfaces are
`192.168.65.x` / `172.1x.0.1` / `127.0.0.1`. The machine's LAN IP **does not exist there to bind**.
LiveKit advertises an address it never bound, every ICE pair fails, and the browser gives up after
~13s. Docker Desktop forwards host-network TCP on all interfaces but **UDP on `127.0.0.1` only**,
which is why signalling connects while media cannot.

**Check** — the advertised nodeIP must appear in the bound list:

```bash
docker logs livekit-server 2>&1 | grep "starting LiveKit server" | tail -1
docker exec livekit-server sh -c 'awk "\$2 ~ /:1ECA\$/ {print \$2}" /proc/net/udp'
```

`0100007F:1ECA` is `127.0.0.1:7882` — that entry must be present.

**Fix.** Keep `LIVEKIT_HOST_IP=127.0.0.1` in `backend/.env` (the default). That one variable drives
`--node-ip`, `LIVEKIT_NODE_IP` and both `LiveKit__Host` values, so they cannot drift apart.

**Trade-off:** the browser must be on the same machine. LAN devices need bridge networking with
published ports (published ports **do** bind `0.0.0.0`, unlike host networking) or native Docker
Engine.

### 2b. Same symptom, node IP already correct — stale UDP forwarding

The fix above makes LiveKit reachable **by** the browser. ICE also needs the reverse, and that half
fails independently.

**Cause.** ICE validates in both directions before nominating a pair:

```text
browser -> LiveKit   works   (loopback is forwarded into the VM; binding requests arrive and are answered)
LiveKit -> browser   fails   (checks toward the browser's advertised LAN IP go nowhere)
```

The giveaway is the **asymmetry**: `requestsReceived` and `responsesSent` are non-zero while
`responsesReceived` stays 0. Traffic arrives; nothing gets back.

**Check** — success is logged as `participant active`, **not** "ice connected" (no such line exists;
grepping for it reports 0 even on a healthy session):

```bash
docker logs livekit-server 2>&1 | grep -c '"state": "failed"'
docker logs livekit-server 2>&1 | grep -c "participant active"
docker logs livekit-server 2>&1 | grep "removing participant without connection" | tail -1
```

**Fix** — recreate the container. Docker Desktop's UDP forwarding for a host-networked container
goes stale; the host's network changing underneath it (VPN connecting, wifi reconnecting) is enough.
Nothing reports it: the container stays healthy, TCP signalling keeps working, only media is dead.
A plain `docker restart` does **not** fix it (see 4).

```bash
cd backend && docker compose up -d --force-recreate livekit-server
```

Then **hard-reload** the browser tab — the old page holds a dead room.

**Confirm which path was selected:**

```bash
docker logs livekit-server 2>&1 | grep "participant active" | tail -1 | grep -oE '\[remote\]\[selected:1\][^"]*'
```

`udp host <IP>` or `127.0.0.1` is a direct pair (healthy). `relay` means the direct path is still
broken and TURN is the only thing keeping media alive — worth knowing before a demo.

**Safety net.** `StreamingService/livekit.yaml` enables an embedded TURN relay. The browser
allocates outbound on it, so LiveKit never has to originate a packet toward the client and the
client's advertised interfaces stop mattering. ICE prefers a direct pair, so it costs nothing while
healthy. Two details that make it usable — do not "tidy" them away:

- TURN binds **all** interfaces, unlike the media port that `node_ip` pins to loopback.
- `relay_range_end` is widened to 30100. The default range is **three ports**, and a teacher plus
  two students exhausts it — which then fails identically to the bug it exists to cover.

No TLS, deliberately: dev, loopback only. Do not copy that section anywhere reachable off the
machine — there TURN belongs on `tls_port` with a real certificate.

**Do not chase the VPN for this.** Two hours went into proving it is not causal. The kill switch was
off, and WireGuard's `suppress_prefixlength 0` rule already keeps loopback and LAN traffic out of the
tunnel (`ip rule show`). What the VPN does is change which interface Chrome ranks first — that makes
it worse, not causal.

### 2c. Same fix again, after the machine slept — the process is wedged

**Symptom.** Identical, but the 2b ICE check comes back **clean**: 0 failed pairs, no "removing
participant without connection" at all.

**Cause.** The process was wedged by a host suspend/resume, not by a network path. Nothing was
reaching it to fail, so the ICE counters have nothing to show.

**Check** — the giveaway is in the **timestamps**, not the contents. On a healthy server the egress
poll lands every 30s, so any gap longer than that means it is not running:

```bash
docker inspect -f '{{.State.StartedAt}}' livekit-server
docker logs livekit-server 2>&1 | tail -1
docker logs --since "$(docker inspect -f '{{.State.StartedAt}}' livekit-server)" livekit-server 2>&1 | wc -l
```

**Zero lines since `StartedAt` is conclusive.** The container reports `Up N hours (healthy)`
throughout — the healthcheck passes while media is dead.

**Fix.** The same recreate as 2b, plus egress, which goes quiet alongside it:

```bash
cd backend && docker compose up -d --force-recreate livekit-server livekit-egress
```

**Check this before re-reading application logs.** Nothing in ClassroomService, StreamingService or
LiveAssistantService reports it — from their side the session is simply idle.

### 2d. LiveKit is healthy but the port is gone

**Symptom.** Identical again, and **no** session will start. Alongside it, StreamingService logs a
socket exception from `ListEgress` every 30s: `Skipping reconciliation: could not read egress state.`

**Cause.** The LiveKit group runs `network_mode: host`, so Docker Desktop must forward those ports
out of the VM. That forwarding does not always come back when the VM restarts — and **any settings
change restarts the VM** (this was first hit after lowering the VM's memory). LiveKit is fine and
serving; the door to it is shut.

Not 2c: the process is not wedged. It answers normally, just nowhere the host can reach.

**Check** — alive inside, absent outside:

```bash
docker exec livekit-server wget -q -O- --timeout=3 http://127.0.0.1:7880/   # prints OK
ss -ltn | grep 7880                                                         # prints nothing
```

`docker ps` says healthy the whole time: that healthcheck runs **inside** the container. It cannot
see this failure by design. MinIO on 9000 keeps working — a useful contrast, not a coincidence: it
is a bridge container with a *published* port, a different forwarding path.

**Fix:**

```bash
cd backend && docker compose up -d --force-recreate livekit-server livekit-egress livekit-redis
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:7880/   # must be 200
```

Still `000`? Docker Desktop itself needs restarting.

`livekit-redis` is included deliberately — it is host-networked too, and both other containers reach
it at `127.0.0.1:6379` through the same forwarding.

**Afterwards: end the session and start a new one.** Recreating containers is not enough. Any
running session had its AI assistant crash on the same broken port, and the assistant only attaches
at session **start** (`POST /api/internal/sessions/start`). There is no retry — `session_pipeline`
logs `session_crashed`, deregisters and stops. Rejoining as the teacher does not bring it back; the
room works and the assistant panel sits on "No suggestions yet" forever.

**Distinguish from §1**, which looks similar from that panel:

```bash
docker logs live-assistant-service --since 30m 2>&1 | grep -E "failed to connect|session_crashed|GroqSpeechToTextError"
```

- `failed to connect: Signal(WsError(...))` within ~1s of session start → **this section**; the
  agent never joined.
- Agent joins fine, then dies ~5s later with `GroqSpeechToTextError` and a 403 → **§1**.

---

## 3. Everything is crawling

**Symptom.** Transcription far slower than it should be, egress logging "Can't record audio fast
enough", the whole desktop sluggish.

**Cause.** Docker Desktop allocated most of the machine's RAM, so the host OS, browser and editor
are pushed into swap.

**Check** — GBs of swap in use with little available memory is this:

```bash
free -m | awk 'NR==2{print "  avail="$7"MB"} NR==3{print "  swap used="$3"MB"}'
docker info --format '  VM MemTotal: {{.MemTotal}}'
```

**Fix.** Docker Desktop → Settings → Resources → Memory. The whole stack idles around 1.6 GB, so
**4–8 GB is ample**; allocating the maximum is what causes this. Measured 2026-07-28: 15.7 GB → 8 GB
took host available memory from 2.1 GB to 9 GB and ended the thrashing.

**Note this restarts the VM**, which can trigger §2d.

Also frees ~2 GB when not testing recording: `docker stop livekit-egress`.

---

## 4. A config file edit appears to do nothing

**Symptom.** You edit a bind-mounted config (`livekit.yaml`, `egress.yaml`), restart the container,
and it keeps running the old values. It reports healthy and logs no error, so it looks like the
setting is misspelled.

**Cause.** `docker restart` does **not** re-read a bind mount on Docker Desktop. The container is
restarted against the file contents it already had.

**Check** — compare what the container sees against the file on disk:

```bash
docker exec livekit-server sh -c 'cat /livekit.yaml' | head -40
```

**Fix** — recreate rather than restart:

```bash
cd backend && docker compose up -d --force-recreate livekit-server
```

Run compose from `backend/` — it `include:`s each service's `docker-compose.unit.yml`. Running one
of those files alone fails with *"refers to undefined network intellilect-net"*.

---

## 5. Whiteboard annotations are missing from the recording

**Symptom.** The class sees the teacher's pen strokes live, but the downloaded MP4 shows the slides
with nothing drawn on them.

**Cause.** Room-composite egress does not composite tracks — it opens headless Chrome, loads a **web
page**, and captures that. LiveKit's built-in template only knows about tracks, and annotations are
drawn on a `<canvas>`, so the template genuinely cannot see them. This is expected with
`Egress__CustomBaseUrl` unset.

**Fix.** Point egress at the recorder page in `StreamingService/docker-compose.unit.yml`:

```yaml
- Egress__CustomBaseUrl=http://host.docker.internal:5173/recorder
```

```bash
cd backend && docker compose up -d --force-recreate streaming-service
curl -s http://127.0.0.1:8085/health | grep -o 'recordingTemplate[^,]*'
```

**It must be `host.docker.internal`, not `127.0.0.1`.** Host networking on Docker Desktop is the
VM's namespace, so `127.0.0.1` there is the VM's own loopback and cannot see the Vite dev server,
which is a plain process on the machine. `Egress__S3__Endpoint` beside it **is** correctly
`127.0.0.1` — MinIO is a container with a published port, which Docker Desktop forwards into the
VM's loopback. A host process gets none. Same-looking addresses, different mechanisms.

**The dev server must also bind past loopback.** `front-end-web/vite.config.ts` sets `server.host`
and `allowedHosts` for this; Vite otherwise rejects the unknown `Host` header with a bare
`Blocked request`, which reads like a routing fault and is not. Verify from the worker itself:

```bash
docker exec livekit-egress sh -c 'wget -q -O- http://host.docker.internal:5173/recorder | head -c 200'
```

**This makes recording depend on the frontend being up.** With the dev server stopped, egress loads
nothing and the recording fails rather than falling back. Blank the setting to return to LiveKit's
template — no rebuild needed.

### 5b. Recording fails entirely after enabling the custom template

**Cause.** LiveKit waits for the page to log `START_RECORDING` to the browser console before
capturing. The recorder page only logs it once **connected**, so a page that 404s, fails to load its
JS, or is handed no `url`/`token` never starts. That is deliberate: a misconfigured template fails
the egress loudly instead of recording an hour of blank video.

**Fix.** Wrong host/port, or the frontend is not running. If urgent, blank
`Egress__CustomBaseUrl` — recordings return to LiveKit's template immediately and only the
annotations are lost.

---

## 6. Recording is a slideshow / flickers

**Symptom.** The recording holds still for seconds then lurches. Both the screen share **and** the
camera are frozen — a talking head does not freeze at the source, which is what rules out the
publisher.

**Cause.** The egress pipeline is dropping frames because the host cannot feed it. Chrome capture →
composite → encode is the path, and it scales with **pixels × framerate**, not bitrate.

**Check** — this number is the ground truth for every recording question:

```bash
docker logs livekit-egress --since 15m 2>&1 | grep -oE 'videoBuffersDropped[^,]*' | tail -1
```

Measured history on a 4-core laptop, all with the same content:

| Setting | Dropped | Video frozen | Longest freeze |
| --- | --- | --- | --- |
| 1280×720 @15 | 3708 in 284s | 67% | 9.2s |
| 1280×720 @15 (after freeing host RAM) | 809 in 83s | 54% | 13.2s |
| 960×540 @10 | **0** | 28% (static content) | 0.9s |

**Distinguish two kinds of "frozen".** Frozen because nothing on screen changed is fine. Frozen
because frames were discarded is not. `videoBuffersDropped: 0` proves the former.

**Fix.** Lower `Egress__Width`/`Height` first — resolution and framerate are the levers, bitrate is
not. Raise `Egress__VideoBitrate` **with** the resolution, or the same bits spread over more pixels
and the picture gets worse at a higher resolution.

**Also check** `Media__ScreenShareFramerate` (default **5**). Recording faster than the source
publishes only duplicates frames.

**Worst case** — the pipeline freezes at finalisation and writes a **0-byte file**. The comments in
`EgressOptions.cs` record the settings that avoid it.
