# Browser journey — work-plan §11.12

> **Status: written, never run.** Playwright is not installed on the development machine and
> this journey has never been executed against a running platform. Selectors were read off the
> components (`LoginForm`, `ClassroomSessionList`, `InteractionSidebar`, `StudentQuizPanel`,
> `StudentQuizSummary`) rather than observed in a browser. Expect the first run to find
> mistakes here before it finds any in the app.

## Why one journey and not a suite

Two large bodies of tests already exist and neither crosses this seam:

- `backend/tests/e2e` drives the API and LiveKit directly. Right layer for the assistant loop
  and for authorization; never renders the React app.
- The frontend unit tests render components against mocked HTTP. Fast and precise; never touch
  a service.

What is left over is whether the app a user loads, talking to the services actually running,
puts the right number on the screen. That is one journey's worth of question — §4 (quizzes),
§5 (in-session notification) and §6 (marks) meeting in one page — and everything a component
test can answer, a component test should still answer: seconds against minutes, and a failure
that names a component rather than handing you a screenshot.

## Install and run

```bash
cd front-end-web
npm i -D @playwright/test    # not in package.json — see the note below
npx playwright install chromium
npm run test:e2e
```

**`@playwright/test` is deliberately not in `package.json`'s devDependencies.** It was left out
because the lockfile could not be regenerated offline when this was written, and a
`package.json` that lists a dependency the lockfile does not carry breaks `npm ci` for everyone
else. Add it in the same commit as a regenerated `package-lock.json`, not before. The
`test:e2e` script is there, and it fails with a plain "Cannot find module" until the install
above has been run — which is the clearest possible statement of what is missing.

### What must be up

- The platform (`docker compose up -d` from `backend/`).
- **The three pending migrations applied** — `IX_Streams_SessionId`, `IX_Users_Email`,
  `IX_Participants_StreamId_UserId`. They run at service startup and each refuses over
  pre-existing duplicate rows, so a service that failed to start is the first thing to check
  when provisioning fails with connection errors.
- The Vite dev server. Playwright starts it via `webServer` and reuses one that is already
  running, which is the normal state on a development machine.

| Variable | Default | Purpose |
| --- | --- | --- |
| `E2E_WEB_URL` | `http://localhost:5173` | Where the app is served. |
| `E2E_GATEWAY_URL` | `http://localhost` | nginx — used by the API arrangement. Same name the pytest and k6 harnesses use. |
| `E2E_ADMIN_EMAIL` / `E2E_ADMIN_PASSWORD` | seeded admin | Approves the accounts each run registers. |
| `E2E_USER_PASSWORD` | `Passw0rd!23` | Password for the teacher and student it creates. |

## How it is built

**Arrangement over the API, assertions in the browser.** `support/platform.ts` registers a
teacher and a student, approves them, creates a classroom, enrols the student, starts a session
and publishes a quiz — all over HTTP. Doing that through the UI would make the journey depend
on five features it is not testing, and when one changed, every step would go red without any
of them naming what broke.

**The quiz is authored, not generated.** `GenerateDraftAsync` calls a model, so a generated
quiz would put Groq in the path of a browser test — and would leave it with no answer key,
which is exactly what the final assertion needs in order to predict a mark rather than merely
observe that one appeared.

**Media is out of scope and cannot fail the run.** Chromium launches with fake devices, and
`LiveRoomPage` renders `<InteractionSidebar>` as a sibling of `<LiveKitRoom>` rather than a
child — so the quiz panel is present whether or not the room connects. Glass-to-glass timing
needs `getStats()` from inside the browser and belongs to P1 (`docs/latency.md`).

**Serial, one worker, no retries locally.** The four steps are one story told to one browser;
they provision real accounts against one shared platform, and a second worker would be a second
live session competing for the same LiveKit and the same embedder. A journey that only passes
on the second attempt is a flake, and a retry that hides it is how a browser suite becomes
something nobody trusts.

## Selector notes worth carrying forward

The app has almost no `data-testid` — six across the whole `src` tree, five of them inside
tests. That turned out to be a virtue: every selector here is a role, a label or visible text,
so the journey breaks when the accessible name breaks, which is the right coupling.

One exception, and it is a genuine finding rather than a workaround: **the classroom card is a
`<div onClick=…>` with no `role` and no `tabIndex`**, so it cannot be addressed as a button and
cannot be reached by keyboard at all. The journey clicks it by its text. Recorded in the
work-plan under the accessibility item rather than fixed here.
