# StreamingService

Owns the live room: LiveKit rooms and access tokens, the SignalR hub that carries chat and
real-time notifications, the media policy handed to every client, and **session recording** via
LiveKit egress.

.NET 10 · Clean Architecture · Postgres · **104 unit tests**

---

## Contents

- [Responsibilities](#responsibilities)
- [Architecture](#architecture)
- [Two channels: SignalR and WebRTC](#two-channels-signalr-and-webrtc)
- [Recording](#recording)
- [API surface](#api-surface)
- [Configuration](#configuration)
- [The Docker Desktop networking constraint](#the-docker-desktop-networking-constraint)
- [Running](#running)
- [Tests](#tests)

---

## Responsibilities

| Area | What it owns |
| --- | --- |
| **Rooms** | Creating LiveKit rooms, issuing scoped access tokens, participant lifecycle |
| **Media policy** | The `RoomOptions` every client connects with — codec, simulcast, capture geometry, reconnection budget |
| **Publish permissions** | Whether students may publish audio/video, changeable mid-session |
| **SignalR hub** | Chat, participant count, recording state, quiz notifications, session-ended broadcast |
| **Q&A and reactions** | Hand raise, questions, reactions |
| **Recording** | Starting and stopping egress, reconciling orphans, publishing a "ready" event |
| **Webhooks** | LiveKit's `room_started` / `egress_ended` callbacks |

It does **not** own classrooms, sessions or marks — those belong to
[ClassroomService](../ClassroomService/), which calls in over internal HTTP to create the room.

---

## Architecture

```text
StreamingService.Api            composition root, health checks, webhook verification
StreamingService.Presentation   controllers, SignalR hub
StreamingService.Application    services, DTOs, abstractions
StreamingService.Infrastructure LiveKit clients, egress, EF Core, messaging
StreamingService.Domain         LiveStream, StreamParticipant, StreamChatMessage, StreamQuestion, StreamReaction
```

LiveKit is reached through two thin interfaces — `ILiveKitEgressClient` and the room client — so
every recording test runs against `FakeLiveKitEgressClient` and never touches a media server.

---

## Two channels: SignalR and WebRTC

A live session uses **two independent transports**, and knowing which is which explains most of the
debugging in this service.

| | SignalR (`/hubs/stream`) | LiveKit (WebRTC) |
| --- | --- | --- |
| Carries | chat, participant count, recording state, quiz events, session-ended | audio, video, screen share, **whiteboard strokes** |
| Path | browser → nginx → this service | browser ↔ LiveKit server, directly |
| Fails as | stale UI, missing notifications | "Connection Lost", black tiles |

The gateway proxies `/hubs/stream`; **media never passes through nginx or this service**.

`QuizChanged(quizId, state)` deliberately carries **ids and state only** — never the quiz payload.
Clients refetch the view they are entitled to, so a student's socket cannot be the leak that the
two-DTO split in ClassroomService prevents.

Whiteboard strokes ride LiveKit's **data channel**, not this hub — see
[front-end-web](../../front-end-web/). That is why the whiteboard needed no backend change at all.

---

## Recording

### How annotations reach the MP4

Room-composite egress does **not** composite tracks. It opens headless Chrome, loads a web page, and
captures what that page renders. LiveKit's built-in template only knows about tracks, so a
`<canvas>` is invisible to it.

```text
Egress__CustomBaseUrl unset  ──▶ LiveKit's template ──▶ MP4 without whiteboard ink
Egress__CustomBaseUrl set    ──▶ our /recorder page  ──▶ MP4 with everything on screen
```

The recorder page joins the room as an ordinary hidden participant and receives strokes exactly as a
student does. **Blank is the default**, and means byte-for-byte what was recorded before the option
existed — taking over the recording layout also takes over responsibility for it, and a mistake
there surfaces after the lesson rather than during it. The way back is a config edit, not a rebuild.

An empty string is treated as unset: to LiveKit an empty `CustomBaseUrl` is a template URL that
resolves to nothing, which fails the egress rather than falling back.

### Encode settings are not cosmetic

Egress runs headless Chrome plus a GStreamer H.264 encode. On a constrained host it starves, and the
worst failure mode is a **0-byte file** — the muxer freezes at finalisation. Measured on a 4-core
laptop:

| Configuration | `videoBuffersDropped` | Frozen | Outcome |
| --- | --- | --- | --- |
| 1280×720 @15 | 3708 / 284s | 67% | unwatchable slideshow |
| 960×540 @10 | **0** | 28% (static content) | smooth |

The bottleneck is **Chrome capture → composite**, not the encoder: drops are counted at
`video_input_queue`, and x264 benchmarks 1080p30 at 2.4× realtime on the same machine. Cost scales
with **pixels × framerate**; bitrate is not the lever.

```bash
# the ground truth for any recording question
docker logs livekit-egress --since 15m 2>&1 | grep -oE 'videoBuffersDropped[^,]*' | tail -1
```

Raise `VideoBitrate` **with** resolution or the picture gets worse at a higher resolution — the same
bits spread over more pixels.

### Finalisation

`StopEgress` returns when LiveKit *accepts* the stop, not when the file is muxed. MP4 writes its
index **last**, so closing the room early truncates the recording. `FinalizeWaitSeconds` bounds how
long session-end waits — enough to flush, bounded so a stuck egress cannot block session end.

---

## API surface

### Public — `/api/streams`

```text
GET    {sessionId}                       join payload: LiveKit token, host, media settings
POST   {sessionId}/join
DELETE {sessionId}/leave
PUT    {sessionId}/hand-raise
PUT    {sessionId}/publish-policy        teacher: may students publish audio/video
PUT    {sessionId}/recording             teacher: start/stop recording mid-session
GET    {sessionId}/chat
GET    {sessionId}/questions
POST   {sessionId}/questions
POST   {sessionId}/questions/{id}/answer
```

### Internal — `/api/internal/streams`, shared-secret header, off the gateway

```text
POST   /                                 create the room (called by ClassroomService)
GET    live
POST   {sessionId}/quiz-event            fan a quiz state change out over SignalR
POST   {sessionId}/end
```

### Webhooks — `/api/webhooks/livekit`

`room_started` and egress lifecycle callbacks, verified against the LiveKit API secret. The webhook
key is the same `LiveKit:ApiSecret` used for tokens.

### SignalR hub — `/hubs/stream`

Client → server: `JoinStreamRoom`, `LeaveStreamRoom`, `SendChatMessage`, `SendReaction`.
Server → client: `ReceiveChatMessage`, `UpdateParticipantCount`, `StreamStatusChanged`,
`PublishPolicyChanged`, `RecordingStateChanged`, `QuizChanged`.

---

## Configuration

### `Media` — [`MediaOptions.cs`](src/StreamingService.Infrastructure/Configuration/MediaOptions.cs)

Delivered to the browser in the join payload. The server owns these; the frontend validates and
falls back rather than trusting them blindly.

| Setting | Default | Reasoning |
| --- | --- | --- |
| `ScreenShareFramerate` | **5** | Framerate costs the publisher CPU; resolution is what keeps text readable. Slides do not move. Raise it if sharing video |
| `ScreenShareWidth/Height` | 1920×1080 | Kept at full resolution for exactly that reason |
| `ScreenShareMaxBitrate` | 1.2 Mbps | The `h1080fps15` preset used 2.5 Mbps; at 5fps far less is needed |
| `VideoWidth/Height/Framerate` | 1280×720 @30 | Camera capture. Lower to 960×540 for CPU headroom |
| `AdaptiveStream` | true | Each subscriber gets the simulcast layer matching its rendered size |
| `Dynacast` | true | Publishers stop encoding layers nobody subscribes to |
| `Simulcast` | true | The layered encoding `AdaptiveStream` selects from; disabling it makes adaptive moot |
| `VideoCodec` | vp8 | |
| `AudioPreset` | music | |
| `Dtx` / `Red` / `StopMicTrackOnMute` | true / true / false | **Affect the AI assistant's audio pipeline.** Read the file before changing any of them |
| `MaxRetries` | **5** | Was 1 — a single failed reconnect ejected a participant from a running lecture |
| `PeerConnectionTimeoutMs` / `WebsocketTimeoutMs` | 15000 | |

### `Egress` — [`EgressOptions.cs`](src/StreamingService.Infrastructure/Configuration/EgressOptions.cs)

Two columns, because they differ and the difference matters: **code default** is what a fresh
deployment gets; **dev compose** is what this repository's `docker-compose.unit.yml` sets after
measuring on a 4-core laptop.

| Setting | Code default | Dev compose | Notes |
| --- | --- | --- | --- |
| `Enabled` | true | true | Feature flag; false runs sessions unrecorded |
| `CustomBaseUrl` | *(empty)* | `/recorder` page | Empty = LiveKit's template, which cannot see whiteboard ink |
| `Layout` | speaker | speaker | Only meaningful for LiveKit's template; forwarded as `?layout=` to a custom one |
| `Width` / `Height` | 1280 × 720 | 1280 × 720 | **The hard cap on recording quality**, and the browser viewport egress renders at |
| `Framerate` | 15 | **10** | Lowered after measuring; also capped in practice by `Media:ScreenShareFramerate` |
| `VideoBitrate` | 1200 | **2500** | **kilobits**/sec. Raised with resolution — the same bits over more pixels looks worse, not better |
| `AudioBitrate` | 96 | 96 | 128 is music-grade; speech does not need it |
| `AudioOnly` | false | false | Drops the video path entirely — the cheapest way to guarantee a usable artifact on a struggling host |
| `KeyTemplate` | `recordings/{room_name}/{time}.mp4` | same | Tokens: `{room_name}`, `{time}` |
| `FinalizeWaitSeconds` | 20 | 20 | See [finalisation](#finalisation) |
| `ReconcileIntervalSeconds` | 30 | 30 | Starts recordings whose webhook was missed; stops orphans |
| `S3:*` | — | MinIO | LiveKit uploads directly — bytes never pass through this service |

### `LiveKit`

`ApiKey`, `ApiSecret` (also the webhook verification key), `Host` (the ws URL handed to browsers),
`ApiUrl` (the server-side Twirp endpoint this service calls).

---

## The Docker Desktop networking constraint

Three addresses that look similar and are resolved by three different things:

| Setting | Resolved by | Correct value locally |
| --- | --- | --- |
| `LiveKit:Host` | the **browser** | `ws://127.0.0.1:7880` |
| `LiveKit:ApiUrl` | **this service**, in a bridge container | `http://host.docker.internal:7880` |
| `Egress:S3:Endpoint` | the **egress worker**, host-networked | `http://127.0.0.1:9000` (MinIO is a container with a *published* port, forwarded into the VM) |
| `Egress:CustomBaseUrl` | the **egress worker** | `http://host.docker.internal:5173/recorder` (the dev server is a *host process* — it gets no such forwarding) |

`LIVEKIT_HOST_IP` in `backend/.env` drives `--node-ip`, `LIVEKIT_NODE_IP` and both `LiveKit:Host`
values from one place, so they cannot drift apart. If they do, the browser signals to one address
and is offered media candidates on another, ICE fails, and participants are dropped ~13s after
joining — which looks exactly like being kicked.

Full diagnosis in [docs/troubleshooting.md §2](../docs/troubleshooting.md).

---

## Running

```bash
cd backend && docker compose up -d streaming-service
```

Brings up `livekit-server`, `livekit-egress` and `livekit-redis` alongside. All three are
`network_mode: host` — required for WebRTC host candidates, and the source of the constraints above.

Health, including a live probe of the egress worker:

```bash
curl -s http://127.0.0.1:8085/health
```

The probe exists because config-only checks are blind to the failure that actually happens: the
egress worker sat dead for over ten hours while this check reported healthy, because a bucket name
was still present in configuration.

---

## Tests

```bash
cd backend
dotnet test StreamingService/tests/StreamingService.UnitTests/StreamingService.UnitTests.csproj
```

**104 tests**, no LiveKit, no media, no network. Coverage focuses on the parts that are expensive to
get wrong: egress request construction (including that unset options are never forwarded — a
protobuf `0` is indistinguishable from unset and would replace a working default), the reconciler,
webhook verification, media-option mapping, and the recording toggle.
