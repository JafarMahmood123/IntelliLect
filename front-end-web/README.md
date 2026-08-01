# front-end-web

The IntelliLect SPA: dashboards, classroom management, the live classroom with its whiteboard and
quizzes, and the admin consoles.

React 19 · TypeScript · Vite 8 · Tailwind 4 · TanStack Query · LiveKit · SignalR · **219 tests**

---

## Contents

- [Layout](#layout)
- [Data layer](#data-layer)
- [The live room](#the-live-room)
- [The whiteboard](#the-whiteboard)
- [The recorder page](#the-recorder-page)
- [Quizzes](#quizzes)
- [Testing](#testing)
- [Running](#running)

---

## Layout

Organised by **feature**, not by file type. A feature owns its API calls, hooks, components and
types, so a change to quizzes touches one directory.

```text
src/
  features/
    streaming/    25 files   live room shell, stages, media config, SignalR hub
    whiteboard/   20 files   drawing surface, wire protocol, geometry
    quizzes/      18 files   composer, student panel, marks, tracking
    classrooms/   18 files   dashboards, details, sessions
    superAdmin/   36 files   platform console
    recorder/      2 files   the page LiveKit egress captures
    auth users admin roles qa recordings summaries
  components/   shared UI
  layouts/ pages/ routes/   shell and routing
  store/        zustand (auth)
  lib/          axios, i18n
  test/         setup, shared render helpers
```

Inside a feature: `api/` (typed calls), `hooks/` (query and mutation hooks), `components/`,
`types/`, and — importantly — `utils/` for pure logic worth testing on its own.

---

## Data layer

**TanStack Query** with key factories per feature:

```ts
export const quizKeys = {
  all: ['quizzes'] as const,
  detail: (quizId: string) => [...quizKeys.all, 'detail', quizId] as const,
  tracking: (classroomId: string) => [...quizKeys.all, 'tracking', classroomId] as const,
};
```

Invalidation is driven by SignalR. `QuizChanged(quizId, state)` carries **ids and state only** — the
payload is deliberately never broadcast, so clients refetch the view they are entitled to rather
than trusting the wire. That is what keeps the server's teacher/student DTO split meaningful on the
client too.

Auth lives in a small **zustand** store; everything server-owned lives in Query.

---

## The live room

`LiveRoomPage` owns the shell — the LiveKit connection, header, control bar, audio and the
interaction sidebar — and delegates the video area to a role-specific stage:

| Stage | Sees |
| --- | --- |
| `TeacherStage` | **Everyone.** Tiles are driven by the participant list, not by tracks, so a view-only student who never publishes still gets a named placeholder tile |
| `StudentStage` | **Only the teacher**, filtered by role metadata — so this holds even when the session lets students publish |

Room options come from the **server** (`MediaOptions.cs`) and are validated client-side:
`toRoomOptions.ts` runs every field through a validator, so a missing `media` object or a bad codec
string degrades to a working configuration instead of reaching the SDK. A server-side typo must
never become "no video".

Two behaviours worth knowing:

- **Disconnects are classified.** `disconnectPolicy.ts` distinguishes a terminal disconnect (leave
  the page) from a transport failure (stay, offer a rejoin). A flaky link must not eject someone
  from a running lecture.
- **The device request is frozen at connect time.** Live policy changes flow only to the control
  bar, so re-granting permission lets a student *choose* to share rather than force-enabling their
  camera.

---

## The whiteboard

`features/whiteboard/` — the teacher annotates the shared screen, or draws on a blank board.

**Coordinates are fractions of the picture, never pixels.** The shared screen is letterboxed by
`object-contain` and rendered at a different size in every browser, so screen pixels would put a
circle around a word somewhere else on every student's display. The canvas is positioned over the
video's *content rectangle* and every point is 0..1 against it.

```text
utils/geometry.ts    content rect, normalise/denormalise      <- 12 tests; everything rests on it
utils/protocol.ts    wire format, validation, chunking
utils/strokes.ts     the reducer, eraser hit-testing, thinning
components/          provider, canvas, toolbox, layer
```

**A local stroke and a received one take the same path.** Drawing builds a wire message, feeds it
through the same reducer a remote message goes through, and only then publishes it. There is no
separate "my strokes" code, so the two boards cannot disagree about what a message means.

**Transport is LiveKit's data channel**, not SignalR — already open, already authenticated by the
room token, and no round trip through the backend for a purely visual feature. The whole feature
needed **no server change**.

- Freehand streams `begin`/`point` batched every 50 ms; shapes are sent once on release.
- Late joiners send `hello`; the teacher replies with the board addressed to that participant alone,
  chunked under the 15 KiB reliable-packet cap. Asking is more robust than watching for arrivals —
  it also covers the teacher having been the one to reconnect.
- The laser pointer is sent **lossy**: a dot arriving after the hand has moved on is worse than one
  that never came.
- Every packet is validated on arrival and dropped if unrecognised. It comes from another browser
  and feeds straight into a render.

**Freeze** pauses each viewer's own `<video>` rather than shipping a still. It exists because
annotations pin to the frame: scroll the PDF and the drawings stay put, pointing at the wrong thing.

---

## The recorder page

`/recorder` is loaded by **LiveKit's egress worker in headless Chrome and captured**. It is how
whiteboard ink reaches the MP4 at all — egress composites a web page, and LiveKit's built-in
template only knows about tracks, so a `<canvas>` is invisible to it.

Three things it does differently, each of which would otherwise be burned into every recording:

- **It renders ahead of the router and every provider.** `AppControls` sits outside `<Routes>`, so
  routing it normally would put the theme toggle in the corner of every recorded lesson.
- **It reads remote participants only.** The recorder joins hidden, but it can still see *itself* —
  `useParticipants` would add a permanently empty tile of the robot.
- **It sets `adaptiveStream: true`.** livekit-client defaults it to `false`, which meant subscribing
  to the full 1080p layer and downscaling every frame inside the same Chrome that was dropping them.

It logs `START_RECORDING` only once connected, so a misconfigured template fails the egress loudly
instead of recording an hour of blank video.

---

## Quizzes

The client never sees an answer key it should not have: teacher and student call **different
endpoints** returning **different types**, so `QuizStudent` has nowhere to put `isCorrect`.

- **Composition** — manual, or AI-generated whole-quiz / single-question / answers-for-my-question.
- **Live panel** — countdown, per-question answering, early submission.
- **Marks** — per session and classroom-wide cumulative tracking, including sessions a student
  missed (hiding them would leave the percentage unexplained).

---

## Testing

**Vitest + Testing Library + jsdom.** 219 tests, no browser and no backend.

```bash
npm test
```

The house pattern: **pure logic lives in `utils/` and is tested directly**; components are tested
through what a user can see and do. That split is not stylistic — jsdom implements neither
`canvas.getContext` nor `ResizeObserver`, so anything depending on real painting is untestable here.
Both are stubbed inertly in `src/test/setup.ts`, and the geometry, wire format and eraser are pure
so nothing worth asserting depends on a painted pixel.

`asyncUtilTimeout` is raised to 5s and `testTimeout` to 15s in `vitest.config.ts` — `findByRole`
rebuilds the accessibility tree on every retry and loses under parallel load, which produced flakes
that moved between files as tests were added.

Note `tsconfig.app.json` **excludes** `*.test.tsx`, so `tsc --noEmit` does not typecheck fixtures. A
fixture missing a newly required field is only caught at runtime.

---

## Running

```bash
npm install
npm run dev      # http://localhost:5173
npm run build    # tsc -b && vite build
npm run lint
```

The backend stack must be running — see the [root README](../README.md). Vite proxies `/api` and
`/hubs` to the gateway on port 80.

**`server.host` and `allowedHosts` are set for recording**, not for convenience: the egress worker
lives inside the Docker VM, so a loopback-only dev server is invisible to it, and Vite otherwise
rejects the unknown `Host` header with a bare `Blocked request` that reads like a routing fault.
This also exposes the dev server to the LAN — drop it on an untrusted network.
