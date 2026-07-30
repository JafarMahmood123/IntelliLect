# Live Session Media — Architecture

How video/audio publishing quality and reconnection behaviour are configured for a live session,
and why the design is shaped the way it is. Covers StreamingService and the web frontend together,
because the whole point is that one config section drives both.

> Scope: this is the **media transport** — what the browser publishes and subscribes to, and what
> happens when the connection drops. Recording (egress) is a separate path; see
> `recordings-live-runbook.md`.

## The problem this solves

Before this, `<LiveKitRoom>` was mounted with **no `options` prop at all**, so every session ran on
`livekit-client`'s library defaults. Measured against the installed `livekit-client@2.18.4` bundle:

| Setting | Was | Consequence |
|---|---|---|
| `adaptiveStream` | `false` | every tile pulled FULL resolution regardless of rendered size |
| `dynacast` | `false` | publishers encoded all simulcast layers even when unwatched |
| `screenShareEncoding` | `h1080fps15` | 1080p at 15fps for slides that need ~5 |
| `audioPreset` | `music` (48 kbps) | music profile for a lecture voice |
| `videoCodec` | `vp8` | software encode |
| `maxRetries` | **`1`** | one failed reconnect ejected the participant from the lecture |

None of it was tunable: the frontend had no config mechanism at all — no `.env`, no
`import.meta.env` usage anywhere in `src`.

## Settings path, end to end

```
appsettings.json  "Media": { ... }
      │
      ▼   Infrastructure/DependencyInjection.cs   (Configure<MediaOptions> + AddSingleton<IMediaSettings>)
MediaOptions.cs                                    ← the AUTHORITY. Every default + why.
      │   implements
      ▼
IMediaSettings.cs        (Application/Abstractions)  ← port, so StreamService never imports Infrastructure
      │   injected into
      ▼
StreamService.cs         ToMediaResponse(...)
      │
      ▼
StreamResponse.Media     (MediaSettingsResponse, sent with the join token)
      │   HTTP
      ▼
types/index.ts           MediaSettings              ← all fields OPTIONAL on purpose
      │
      ▼
config/mediaDefaults.ts  validate + fall back       ← MEDIA_FALLBACK, isVideoCodec, isAudioPreset
      │
      ▼
config/toRoomOptions.ts  toRoomOptions / toRoomConnectOptions / toScreenShareCaptureOptions
      │
      ▼
<LiveKitRoom options={...} connectOptions={...}>    LiveRoomPage.tsx
```

`MediaOptions.cs` is the single authority for both values and reasoning. `mediaDefaults.ts` mirrors
it only as a fallback — do not "fix" a value there without reading the C# first.

## Why the server owns these, not a frontend `.env`

Vite does not read env vars at runtime; it **string-substitutes** `import.meta.env.VITE_*` into the
bundle at build time. Three consequences made the frontend the wrong home:

1. **Every change costs a full frontend rebuild** (~8 minutes on the dev link at the time).
2. **The server could not vary quality per session** — with settings server-side, a large class can
   be given lower capture resolution than a 3-person one from the same build.
3. **It would be net-new surface** (env files, typing, validation, docs, one set per environment)
   where the server already had `EgressOptions` and `LiveKitSettings` to copy.

Precedent settled it: `liveKitHost` and `studentsCanPublishAudio/Video` were already server-owned
settings delivered in `StreamResponse`. Media quality is the same kind of thing.

## Why delivering construction options in the payload is safe

`adaptiveStream` and `dynacast` are LiveKit `Room` **construction** options — frozen when the room
connects, not live-updatable. That is fine here because `<LiveKitRoom>` only mounts once
`data?.joinToken && serverUrl` are present, so the settings always arrive before construction. This
is the same reasoning as the pre-existing `initialPublishRef` device request, which is deliberately
frozen at connect so a policy re-grant lets a student *choose* to share rather than force-enabling
their camera.

Consequence for reconnection: a rejoin **unmounts and remounts** the room rather than reusing it, so
a fresh room picks up current settings. Reusing the room would silently keep the old ones.

## Validation at the boundary

`videoCodec` and `audioPreset` are strings crossing a service boundary into a third-party library.
An unsupported codec fails at SDP negotiation, which surfaces to a user as "no video" rather than as
a config error — so `mediaDefaults.ts` validates against `ALLOWED_VIDEO_CODECS` /
`ALLOWED_AUDIO_PRESETS` and falls back on anything unrecognised. Numbers go through `positiveIntOr`,
which also rejects `0`, negatives, `NaN` and `Infinity`, and floors fractional framerates.

Every field of `MediaSettings` is optional so a server that predates the section still produces a
working room — and critically, the fallback is **our** defaults, not the library's. Falling through
to `livekit-client` would silently restore `adaptiveStream: false` and undo the whole feature. There
is a regression test for exactly that.

## Reconnection semantics

`utils/disconnectPolicy.ts` decides whether a disconnect ends the session.

**Terminal** (exit to the classroom): `CLIENT_INITIATED` (user clicked Leave), `ROOM_DELETED` /
`ROOM_CLOSED` (session ended), `PARTICIPANT_REMOVED` (evicted), `DUPLICATE_IDENTITY`,
`USER_REJECTED`.

**Recoverable** (stay, show a rejoin prompt): everything else — notably `SIGNAL_CLOSE`,
`CONNECTION_TIMEOUT`, `MEDIA_FAILURE`, and `UNKNOWN_REASON`/`undefined`.

Two rules make this correct:

- **Default to recoverable.** No information must not be read as "the session is over". Being wrong
  costs a dismissible prompt; being wrong the other way throws someone out of a running lecture.
- **A SignalR session-end always wins.** `shouldExitSession(reason, hasEnded)` exits on `hasEnded`
  regardless of the media reason, because the server closes the room right behind that broadcast and
  ordering between the two is not guaranteed.

Note *when* `onDisconnected` fires: `livekit-client` retries internally and only emits Disconnected
after exhausting `MaxRetries` (now 5). So a recoverable classification means **the SDK already gave
up** — hence a rejoin button rather than a silent wait. `<ConnectionStateToast />` covers the window
*before* that, making the SDK's own retry attempts visible instead of looking like a frozen app.

## Do not change these three

| Setting | Value | Why |
|---|---|---|
| `Dtx` | `true` | Stops audio packets during silence. The assistant's pause detection accumulates trailing silence from **received** frames; `audio_level_probe` confirms silent frames arrive today (100 frames, 0 speech). If they stopped, windows would never close on a pause and only the length cap would fire — which reads as broken boundary detection, not a media setting. |
| `StopMicTrackOnMute` | `false` | `false` keeps a muted mic publishing silence. `true` would END the track, gapping or terminating the assistant's audio stream on every mute. |
| `Simulcast` | `true` | The layered encoding `AdaptiveStream` selects from. Disabling it makes `AdaptiveStream` pointless. |

Egress is also off-limits, for a reason recorded at `EgressOptions.cs:22`: raising the encode above
720p@15 starved the Chrome + GStreamer pipeline, the audio branch dropped samples, and the muxer
froze at finalization producing **0-byte recordings**. The `speaker` layout is likewise the cheap
choice — `grid` composites every tile in Chrome. Nothing in this feature touches egress.

## Two deliberately deferred flips

Both are exposed in config at their pre-existing value. Neither is an oversight.

**`AudioPreset: "music"` → `"speech"`.** Would cut bitrate meaningfully. Held because LiveKit is an
SFU: it forwards the teacher's Opus stream untouched, so this is the *exact* audio the STT
transcribes. Waiting until transcription is verified working, otherwise a quality regression cannot
be attributed between the prompt, the boundary detector, and the bitrate.

**`VideoCodec: "vp8"` → `"h264"`.** May cut publisher CPU via hardware encode. Held because vp8 is
the library default *precisely* because H.264 simulcast support is uneven across browsers (fine in
Chrome, historically broken in Firefox/Safari). Simulcast is what `AdaptiveStream` selects from, so
switching could undermine the optimization that matters most at scale. Needs a real Firefox/Safari
client to test against — `backupCodec: true` exists in the SDK for this class of incompatibility.

## Known ceilings (not addressed here)

**LiveKit is loopback-only.** `use_external_ip: false`, `enable_loopback_candidate: true`,
`LIVEKIT_HOST_IP=127.0.0.1`, and no TURN server. No second machine can reach the media path, so a
real multi-participant class is not currently possible for reasons unrelated to these settings. See
`run-services.txt` section 2. Fixing it means bridge networking with published ports (LAN) or a
public IP / TURN (internet).

**One host does everything.** The same 8-core / 15 GB machine is the SFU, the egress Chrome +
GStreamer encode, the Whisper audio pipeline, and six backend services. No client setting changes
that, and it is why the egress values stay modest.

**`dynacast` scales down, not up.** It stops publishers encoding layers nobody watches — but with
many subscribers someone is usually watching every layer, so its benefit *shrinks* as the class
grows. `AdaptiveStream` is the one that pays off at 10–30 participants. Do not expect both to scale
equally.

## Tests

| What | Where |
|---|---|
| `MediaOptions` defaults, full + partial config binding, port implementation | `tests/StreamingService.UnitTests/MediaOptionsTests.cs` |
| Payload→`RoomOptions` mapping, fallback, enum/number rejection | `front-end-web/src/features/streaming/config/toRoomOptions.test.ts` |
| Terminal vs recoverable disconnects, session-end override | `front-end-web/src/features/streaming/utils/disconnectPolicy.test.ts` |

Run: `dotnet test tests/StreamingService.UnitTests/StreamingService.UnitTests.csproj` and
`npm run test` in `front-end-web`.

The two assertions worth knowing about were mutation-tested — reverting `adaptiveStream`/`dynacast`
to `false` and `MaxRetries` to `1` fails 4 backend tests, and treating `SIGNAL_CLOSE` as terminal
(the original eject-on-blip bug) fails 2 frontend tests.

## Manual verification

Only the live path can confirm the settings reach the browser:

1. Join a session; in DevTools → Network, check the stream detail response carries a `media` object.
2. `chrome://webrtc-internals` — screen-share framerate should track `ScreenShareFramerate`, and
   small tiles should request lower simulcast layers than a fullscreen one.
3. The per-participant connection-quality indicator renders on each tile (top-right).
4. **Reconnection, both directions.** Disable wifi ~5s and re-enable: expect a reconnecting toast and
   recovery, *not* a bounce to the classroom page. Then end the session properly and confirm everyone
   still gets ejected. Both must be checked — the change touches session-end handling.
5. Confirm a recording still lands in S3 (regression check; egress is untouched).
