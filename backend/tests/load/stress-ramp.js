/**
 * §10.3 — ramp until it breaks, then ask *how* it broke.
 *
 * Finding the breaking point is the easy half and the less useful one. The number itself is
 * about a laptop running seventeen containers and does not transfer to anything. What does
 * transfer is the **shape** of the failure, and that is what this script is built to record:
 *
 * - **Refusal, not corruption.** A 429, a 503 or a connection reset are all acceptable answers
 *   from an overloaded platform. A 500 is not: it means the load reached code that was not
 *   written for it. The two are tracked as separate metrics for exactly that reason, and only
 *   one of them has a threshold.
 * - **Nothing half-written.** The teardown ends the session and checks the roster: a
 *   participant count above the number of distinct students that ever joined means the unique
 *   index added in §7.4c stopped holding under contention, and the ghost rows outlive the run.
 * - **It still recovers.** After the ramp collapses, the last stage drops back to a trickle. A
 *   platform that serves the trickle has degraded; one that does not has fallen over, and the
 *   difference decides whether an overloaded lecture recovers on its own or needs a restart.
 *
 * The path being ramped is the session admission chain — token mint, roster join — because it
 * is the most cross-service request in the product: nginx → StreamingService → (internal HTTP)
 * ClassroomService → Postgres, plus a LiveKit grant. Whichever pool runs out first, it runs
 * out here.
 *
 * `abortOnFail` is set on the error threshold so the run stops when the platform is plainly
 * gone rather than hammering a dead stack for the remaining stages. The stage at which it
 * aborts IS the result.
 *
 *   k6 run backend/tests/load/stress-ramp.js
 *   LOAD_STRESS_PEAK=400 k6 run backend/tests/load/stress-ramp.js
 */

import { check } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import {
  body,
  endSession,
  field,
  getStream,
  joinStream,
} from './lib/api.js';
import { config } from './lib/config.js';
import { provisionClassroom, tokenForVU } from './lib/provision.js';

const peak = Number(__ENV.LOAD_STRESS_PEAK || 200);

const admissionLatency = new Trend('admission_ms', true);
/** Answers that mean "not now": the platform is protecting itself and saying so. */
const refused = new Counter('overload_refusals');
/** Answers that mean "something went wrong inside": the platform was not written for this. */
const faulted = new Counter('server_faults');
const faultRate = new Rate('server_fault_rate');
/** Served during the recovery stage, after the peak. The difference between bent and broken. */
const recovered = new Rate('recovery_served');

export const options = {
  scenarios: {
    ramp_to_failure: {
      executor: 'ramping-arrival-rate',
      startRate: 5,
      timeUnit: '1s',
      preAllocatedVUs: 50,
      maxVUs: Math.max(200, peak * 3),
      stages: [
        { target: Math.round(peak * 0.25), duration: '1m' },
        { target: Math.round(peak * 0.5), duration: '1m' },
        { target: Math.round(peak * 0.75), duration: '1m' },
        { target: peak, duration: '2m' }, // hold at the peak — brief spikes are survivable
        { target: 5, duration: '30s' }, // does it come back?
      ],
    },
  },
  thresholds: {
    // The one hard rule. Refusals are allowed at any volume; faults are not, at any volume.
    server_fault_rate: [{ threshold: 'rate<0.01', abortOnFail: true, delayAbortEval: '30s' }],

    // Deliberately absent: a threshold on `http_req_failed` or on latency. This script is
    // *supposed* to push past the point where those fail — thresholding them would abort the
    // run at precisely the moment it starts producing its result.
  },
};

export function setup() {
  // Fewer accounts than the peak arrival rate, on purpose: at high rates several VUs share an
  // identity, which is a reconnecting student rather than a new one. Reconnect storms are the
  // realistic overload here — a lecture drops and everyone retries at once.
  const world = provisionClassroom({
    studentCount: Math.min(config.students, 30),
    live: true,
    label: 'stress',
  });
  // Stamped after provisioning, because the load starts when setup() returns. The recovery
  // check below is relative to this, and the stages above sum to 5m30s.
  return { ...world, startedAtMs: Date.now() };
}

export default function (world) {
  const token = tokenForVU(world.students);

  const stream = getStream(token, world.sessionId);
  admissionLatency.add(stream.timings.duration);

  const status = stream.status;
  // status 0 is k6's "no response": a reset, a refused connection or a timeout. That is the
  // platform shedding load at the socket, so it counts as a refusal rather than a fault.
  const isRefusal = status === 0 || status === 429 || status === 502 || status === 503 || status === 504;
  const isFault = status === 500;

  if (isRefusal) refused.add(1);
  if (isFault) faulted.add(1);
  faultRate.add(isFault);

  check(stream, {
    // Named for what it means rather than for the code, because this is the assertion a reader
    // of the summary needs to understand without opening the file.
    'overload was refused rather than faulted': () => !isFault,
  });

  // The recovery stage is the last 30 seconds; approximating it by wall clock keeps the check
  // in one place. `__ITER` and the executor give no direct handle on the current stage.
  const inRecovery = (Date.now() - world.startedAtMs) > 5.5 * 60 * 1000;
  if (inRecovery) {
    recovered.add(status === 200);
  }

  if (status !== 200) return;

  const joined = joinStream(token, world.sessionId);
  check(joined, {
    'roster join did not fault': (r) => r.status !== 500,
  });
}

export function teardown(world) {
  // Corruption check. Everything above is about how the platform answered; this is about what
  // it wrote. The roster is the piece under the most contention during the ramp — every
  // admitted VU inserts into it — and §7.4c's unique index is the only thing keeping a
  // reconnect from becoming a second person in the room.
  const stream = getStream(world.teacher, world.sessionId);
  if (stream.status === 200) {
    const count = Number(field(body(stream, 'final stream read'), 'participantCount', 0));
    const distinct = world.students.length;
    check(count, {
      'the roster holds no more rows than there are students': (c) => c <= distinct,
    });
    console.log(
      `[corruption check] participantCount=${count} against ${distinct} distinct students. ` +
        'A count above the student total is a duplicate roster row that survived the ramp — ' +
        "the ghost stays in the teacher's participant count for the rest of the session.",
    );
  } else {
    console.log(
      `[corruption check] could not read the stream after the ramp (HTTP ${stream.status}); ` +
        'the platform did not recover enough to be inspected, which is itself the result.',
    );
  }

  const ended = endSession(world.teacher, world.classroomId, world.sessionId);
  check(ended, {
    // A session that cannot be ended after an overload leaves a LiveKit room open and the
    // assistant registered against it — the orphan §10.3 asks about, in its most literal form.
    'the session could still be ended': (r) => r.status >= 200 && r.status < 400,
  });
}
