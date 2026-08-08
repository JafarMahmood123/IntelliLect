/**
 * §11.12 — the one browser journey: log in → join the live session → answer a quiz → see the mark.
 *
 * `backend/tests/e2e` drives the API and LiveKit directly, which is the right layer for the
 * assistant loop and for authorization, and it never once renders the React app. The frontend
 * unit tests render components against mocked HTTP, and never once touch a service. Between
 * those two bodies of work sits a seam nothing crosses: whether the app the user actually
 * loads, talking to the services actually running, puts the right number on the screen.
 *
 * That seam is where §4 (quizzes), §5 (in-session notification of a published quiz) and §6
 * (marks) meet, and it is what this journey covers.
 *
 * **It deliberately asserts nothing about media.** The page mounts `<LiveKitRoom>` and will
 * attempt a WebRTC connection; Chromium is launched with fake devices so the attempt is
 * harmless, and nothing here waits on video. A failed media connection must not fail this
 * test — and it does not, because `LiveRoomPage` renders `<InteractionSidebar>` as a *sibling*
 * of `<LiveKitRoom>` rather than a child, so the quiz panel is there whether or not the room
 * connects. Glass-to-glass timing is P1's and needs `getStats()`, not a DOM assertion (see
 * docs/latency.md).
 *
 * **Arrangement is over the API** (`support/platform.ts`) — see the note there for why.
 *
 * Prerequisites: the platform up, the three pending migrations applied, and the Vite dev
 * server (Playwright starts it via `webServer` unless one is already running).
 */

import { expect, test, type Page } from '@playwright/test';
import {
  closeQuiz,
  platformConfig,
  provisionLiveQuiz,
  QUESTIONS,
  teardown,
  TOTAL_POINTS,
  type QuizWorld,
} from './support/platform';

let world: QuizWorld;

test.beforeAll(async () => {
  // Named in the log because provisioning against the wrong deployment is the single most
  // common way a first run goes wrong, and every failure that follows looks like something else.
  console.log(`[journey] provisioning against ${platformConfig.gateway}`);
  world = await provisionLiveQuiz();
});

test.afterAll(async () => {
  if (world) await teardown(world);
});

// Serial: these steps are one continuous story, and step four is meaningless without step
// three having happened. Playwright's default isolation would mean logging in and joining four
// separate times to tell it.
test.describe.configure({ mode: 'serial' });

test.describe('a student sits a quiz in a live lecture', () => {
  test('logs in and lands in their classroom list', async ({ page }) => {
    await page.goto('/login');

    // By label, not by placeholder or CSS: `Input` wires `htmlFor`/`id`, so the accessible name
    // is also what a screen reader announces. A selector that survives a restyle and breaks
    // when accessibility breaks is the right way round.
    await page.getByLabel('Email Address').fill(world.student.email);
    await page.getByLabel('Password').fill(world.student.password);
    await page.getByRole('button', { name: 'Sign In' }).click();

    // `getDefaultRoute` sends both Teacher and Student to /classrooms.
    await expect(page).toHaveURL(/\/classrooms$/);
    await expect(page.getByText(world.classroomName)).toBeVisible();
  });

  test('opens the classroom and joins the live session', async ({ page }) => {
    await signIn(page);

    // NOTE: the classroom card is a `<div onClick=…>` with no role and no tabIndex, so there is
    // no button to address it by — hence the text selector. That is also a real keyboard
    // accessibility gap; it is recorded in the work-plan rather than worked around here.
    await page.getByText(world.classroomName).click();
    await expect(page).toHaveURL(new RegExp(`/classrooms/${world.classroomId}$`));

    // "Join Now" is enabled only while the session is Live; otherwise the same button reads
    // "Locked". Asserting on the enabled control is therefore also an assertion that the
    // session-start cascade reached the frontend's view of the session.
    const join = page.getByRole('button', { name: 'Join Now' });
    await expect(join).toBeEnabled();
    await join.click();

    await expect(page).toHaveURL(
      new RegExp(`/classrooms/${world.classroomId}/live/${world.sessionId}$`),
    );
    await expect(page.getByRole('heading', { name: 'Live Classroom' })).toBeVisible();
  });

  test('answers the published quiz and submits it', async ({ page }) => {
    await enterLiveRoom(page);

    // The quiz was published before the browser opened, so the panel is seeded from the "open
    // quiz for this session" endpoint rather than from the SignalR broadcast. That fallback is
    // deliberate in the component — a student who joins after the teacher publishes must still
    // get the quiz — and it is the path this journey exercises.
    await openQuizPanel(page);
    await expect(page.getByRole('heading', { name: 'Boiling points check' })).toBeVisible();

    for (const question of QUESTIONS) {
      const correct = question.options.find((option) => option.isCorrect)!;
      await page.getByRole('button', { name: correct.text, exact: true }).click();
    }

    // The answered count before submitting. Each click is a request; a click that never reached
    // the server leaves this at zero while the button still looks selected, because the panel
    // holds selections locally for responsiveness and rolls them back on error.
    await expect(
      page.getByText(`${QUESTIONS.length} of ${QUESTIONS.length} answered`),
    ).toBeVisible();

    await page.getByRole('button', { name: 'Submit my answers' }).click();

    // Server-owned state, not a local flag: the panel derives "submitted" from
    // `quiz.submittedAtUtc` as the server reported it, so this is an assertion that the write
    // landed rather than that the button was clicked.
    await expect(page.getByText('You have finished this quiz')).toBeVisible();
  });

  test('sees the mark once the teacher closes the quiz', async ({ page }) => {
    await enterLiveRoom(page);
    await openQuizPanel(page);

    // Before closing there is deliberately no mark. Answers can be changed until a quiz closes,
    // so releasing the score early would also release the key. This is the same rule the §8.6
    // suite asserts over HTTP, seen here as what the student is actually shown.
    //
    // The wording distinguishes it from the near-identical line in the submitted panel ("once
    // your teacher closes the quiz"); this one belongs to the summary row below it.
    await expect(
      page.getByText('Your marks appear once the teacher closes this quiz'),
    ).toBeVisible();

    await closeQuiz(world);

    // The close is announced over SignalR and the panel re-reads; `useQuizCloseWatch` polls as
    // well, so this lands either way. The generous timeout covers the broadcast, not a
    // suspicion that it is slow.
    const totalCard = page.getByText('Your total this session').locator('..');
    await expect(totalCard).toBeVisible({ timeout: 30_000 });

    // The number, not merely a number. A scorer that returned a constant — zero, or the total
    // for everybody — is exactly what a "marks appeared" assertion cannot see. This student
    // answered every question correctly, so full marks is the only right answer.
    await expect(totalCard).toContainText(
      new RegExp(`${TOTAL_POINTS}\\s*/\\s*${TOTAL_POINTS}`),
      { timeout: 30_000 },
    );
    await expect(totalCard).toContainText('100%');
  });
});

// --- helpers --------------------------------------------------------------------------------

/**
 * Log in through the form.
 *
 * Done per test rather than shared through `storageState`, because each test gets a fresh
 * browser context. Going through the UI keeps the login path exercised in every step; it costs
 * about a second, and it is the one arrangement step that is also a feature.
 */
async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Email Address').fill(world.student.email);
  await page.getByLabel('Password').fill(world.student.password);
  await page.getByRole('button', { name: 'Sign In' }).click();
  await expect(page).toHaveURL(/\/classrooms$/);
}

/** Straight into the room, for the steps whose subject is what is inside it. */
async function enterLiveRoom(page: Page): Promise<void> {
  await signIn(page);
  await page.goto(`/classrooms/${world.classroomId}/live/${world.sessionId}`);
  await expect(page.getByRole('heading', { name: 'Live Classroom' })).toBeVisible();
}

/** The sidebar opens on a section menu; the quiz panel is one section behind a button. */
async function openQuizPanel(page: Page): Promise<void> {
  await page.getByRole('button', { name: /^Quiz/ }).click();
  await expect(page.getByRole('heading', { name: 'Quiz', exact: true })).toBeVisible();
}
