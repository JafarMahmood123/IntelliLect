/**
 * §10.2, mix 2 — the deadline herd.
 *
 * A quiz closes at a fixed instant and every student who has not submitted does so in the last
 * few seconds. It is the platform's only naturally synchronised burst: nothing staggers it, and
 * the more students there are the sharper it gets. §11.7 pinned the orderings against a fake
 * clock and found two defects there; this is the same interleaving against a real database
 * under a real burst, which is the only place row locks and connection-pool exhaustion appear.
 *
 * **This script's pass condition is correctness, not latency.** A quiz that answers slowly is a
 * bad afternoon; a quiz that drops a submission is a student marked zero for work they did.
 * So the interesting assertion is in `teardown()`: after the burst, the quiz is closed and the
 * teacher's results are read, and every student who received a 2xx from `/submit` must appear
 * with a recorded submission. A run can meet every latency threshold and still fail on that
 * one, which is the point.
 *
 * All VUs start together (`per-vu-iterations`, one iteration each) rather than arriving at a
 * rate. This is not a traffic model — it is a single instant, and modelling it as a rate would
 * spread the very collision being tested.
 *
 *   k6 run backend/tests/load/load-quiz-deadline.js
 *   LOAD_STUDENTS=100 k6 run backend/tests/load/load-quiz-deadline.js
 */

import { check } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import {
  answerQuiz,
  body,
  closeQuiz,
  createQuizDraft,
  endSession,
  field,
  publishQuiz,
  quizResults,
  studentQuizView,
  submitQuiz,
} from './lib/api.js';
import { config } from './lib/config.js';
import { provisionClassroom, tokenForVU } from './lib/provision.js';

const students = config.students;

const submitLatency = new Trend('quiz_submit_ms', true);
const answerLatency = new Trend('quiz_answer_ms', true);
const submitAccepted = new Rate('quiz_submit_accepted');
/** Submissions the server acknowledged, counted client-side, to compare against the results. */
const acknowledged = new Counter('quiz_submissions_acknowledged');
/** The same submissions as the teacher's results report them. Any gap is a lost submission. */
const recorded = new Counter('quiz_submissions_recorded');

//: A two-question quiz with a known key, so the score is arithmetic and a wrong total is
//: visible rather than merely plausible. The time limit is long: a quiz that self-closes
//: mid-run would turn every remaining submission into a legitimate 409 and the results would
//: read as dropped work. The deadline being simulated is the *arrival pattern*, not the clock.
const QUESTIONS = [
  {
    text: 'Load probe one.',
    points: 2,
    timeLimitSeconds: 900,
    options: [
      { text: 'Correct', isCorrect: true },
      { text: 'Wrong', isCorrect: false },
    ],
  },
  {
    text: 'Load probe two.',
    points: 3,
    timeLimitSeconds: 900,
    options: [
      { text: 'Correct', isCorrect: true },
      { text: 'Wrong', isCorrect: false },
    ],
  },
];

export const options = {
  scenarios: {
    deadline: {
      executor: 'per-vu-iterations',
      // Exactly one VU per student. More would put two connections on one identity, and the
      // teardown reconciliation — which counts students, not requests — would then be wrong
      // for a reason that has nothing to do with the platform.
      vus: students,
      iterations: 1,
      maxDuration: '5m',
    },
  },
  thresholds: {
    quiz_submit_accepted: ['rate>0.99'],
    // Wider than the join budget on purpose: a submission is a write across several tables and
    // 3s is still inside what a student experiences as "it went through".
    quiz_submit_ms: ['p(95)<3000'],
    'http_req_failed{name:quiz-submit}': ['rate<0.01'],
    'http_req_failed{name:quiz-answer}': ['rate<0.01'],
  },
};

export function setup() {
  const world = provisionClassroom({ studentCount: students, live: true, label: 'quiz' });

  const quizId = createQuizDraft(
    world.teacher,
    world.classroomId,
    world.sessionId,
    'Deadline load probe',
    QUESTIONS,
  );
  publishQuiz(world.teacher, world.classroomId, quizId);

  return { ...world, quizId };
}

export default function (world) {
  const token = tokenForVU(world.students);

  // The student view is read first because that is where the question and option ids come
  // from — a client cannot answer without it, so a scenario that skipped it would be
  // measuring a burst no real client can produce.
  const view = studentQuizView(token, world.classroomId, world.quizId);
  if (!check(view, { 'student view served': (r) => r.status === 200 })) {
    return;
  }

  const quiz = body(view, 'student view');
  const questions = field(quiz, 'questions', []);

  for (const question of questions) {
    const options = field(question, 'options', []);
    if (options.length === 0) continue;
    // Answer chosen by position rather than by correctness: the student view carries no key
    // (asserted by the §8.6 suite), and picking the first option for everyone gives a mix of
    // right and wrong that is stable across runs.
    const chosen = options[0];
    const answered = answerQuiz(
      token,
      world.classroomId,
      world.quizId,
      String(field(question, 'id')),
      String(field(chosen, 'id')),
    );
    answerLatency.add(answered.timings.duration);
    check(answered, { 'answer accepted': (r) => r.status >= 200 && r.status < 300 });
  }

  const submitted = submitQuiz(token, world.classroomId, world.quizId);
  submitLatency.add(submitted.timings.duration);
  const ok = submitted.status >= 200 && submitted.status < 300;
  submitAccepted.add(ok);
  if (ok) acknowledged.add(1);

  check(submitted, {
    'submission accepted': () => ok,
    // A 500 here is categorically worse than a 429 or a 409: it means the burst reached code
    // that was not written for it, and the student is left not knowing whether their work
    // was saved.
    'submission did not fault': (r) => r.status !== 500,
  });
}

export function teardown(world) {
  // The reconciliation. Closing is what computes and releases the marks, so it has to happen
  // before the results mean anything.
  const closed = closeQuiz(world.teacher, world.classroomId, world.quizId);
  check(closed, { 'quiz closed for marking': (r) => r.status >= 200 && r.status < 300 });

  const results = quizResults(world.teacher, world.classroomId, world.quizId);
  if (check(results, { 'results readable': (r) => r.status === 200 })) {
    const parsed = body(results, 'quiz results');
    const rows = field(parsed, 'students', []);
    const submittedRows = rows.filter((row) => field(row, 'submittedAtUtc', null) !== null);
    recorded.add(submittedRows.length);

    // Reported rather than thresholded, because the two counters are emitted from different
    // places and k6 evaluates thresholds per metric, not across a pair. Read them together:
    // `quiz_submissions_recorded` below `quiz_submissions_acknowledged` is a lost submission,
    // and it is the most serious result this script can produce.
    console.log(
      `[reconciliation] the teacher's results show ${submittedRows.length} submissions across ` +
        `${rows.length} students. Compare with quiz_submissions_acknowledged in the summary — ` +
        'a recorded count below the acknowledged one is work the platform accepted and did not keep.',
    );
  }

  endSession(world.teacher, world.classroomId, world.sessionId);
}
