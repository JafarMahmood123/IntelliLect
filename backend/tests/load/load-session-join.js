/**
 * §10.2, mix 1 — a class arriving at one session.
 *
 * The realistic shape is not a steady rate: a lecture starts and thirty to a hundred students
 * open the page inside about fifteen seconds. So this uses a `ramping-arrival-rate` that goes
 * from nothing to the full cohort in twenty seconds and then holds — an open model, where
 * arrivals do not slow down because the server did. A closed model (fixed VUs looping) hides
 * exactly the failure being looked for: when the platform slows, VUs simply issue fewer
 * requests and the arrival rate quietly falls to whatever the server can serve.
 *
 * **What is actually under test is the token mint.** Since §7.4d, `GET /api/streams/{id}` is
 * no longer a local read: it makes a synchronous internal HTTP call to ClassroomService to ask
 * whether the caller is a member of that classroom, and only then signs a LiveKit grant. That
 * call was added for a correctness reason — the token IS entry, so it has to be authorized —
 * and its cost under a class-sized arrival has never been measured. If ClassroomService's
 * connection pool or the internal HTTP client's own limits are the ceiling, this is where it
 * shows, and it shows as the whole class waiting to get in.
 *
 * Not measured here: LiveKit itself. No media is established — k6 has no WebRTC. This measures
 * the platform's admission path up to the point where the browser would connect.
 *
 *   k6 run backend/tests/load/load-session-join.js
 *   LOAD_STUDENTS=100 LOAD_PEAK_RPS=40 k6 run backend/tests/load/load-session-join.js
 */

import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';
import { chatHistory, endSession, getStream, joinStream, leaveStream } from './lib/api.js';
import { config } from './lib/config.js';
import { provisionClassroom, tokenForVU } from './lib/provision.js';

const peakRps = Number(__ENV.LOAD_PEAK_RPS || 20);

const tokenMint = new Trend('join_token_mint_ms', true);
const admitted = new Rate('join_admitted');

export const options = {
  scenarios: {
    arriving_class: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      // Headroom over the arrival rate, because each iteration is four sequential calls. Too
      // few and k6 reports "insufficient VUs", which is the harness throttling itself and
      // looks identical in the summary to the platform coping.
      preAllocatedVUs: Math.max(20, peakRps * 4),
      maxVUs: Math.max(50, peakRps * 10),
      stages: [
        { target: peakRps, duration: '20s' }, // the doors open
        { target: peakRps, duration: '1m' }, // latecomers and reconnects
        { target: 0, duration: '10s' },
      ],
    },
  },
  thresholds: {
    // A student who cannot get a token cannot attend. This is the only threshold here that is
    // about correctness rather than speed, so it is the strictest.
    join_admitted: ['rate>0.99'],

    // 2s is not a comfort target, it is the point past which a student reloads the page — and
    // a reload is another token mint, which is how a slow admission path becomes a stampede.
    join_token_mint_ms: ['p(95)<2000'],

    'http_req_failed{name:stream-token}': ['rate<0.01'],
    'http_req_duration{name:stream-join}': ['p(95)<1500'],
  },
};

export function setup() {
  // `live: true` — the session must be Live before any token can be minted, and starting it is
  // the single slowest call in the platform. Doing it here keeps it out of the measurement.
  const world = provisionClassroom({
    studentCount: config.students,
    live: true,
    label: 'join',
  });
  return world;
}

export default function (world) {
  const token = tokenForVU(world.students);

  // 1. Mint the join token — the hop this whole scenario exists to measure.
  const stream = getStream(token, world.sessionId);
  tokenMint.add(stream.timings.duration);
  const gotToken = check(stream, {
    'token minted': (r) => r.status === 200,
    'token is non-empty': (r) => {
      if (r.status !== 200) return false;
      try {
        const parsed = JSON.parse(r.body);
        return Boolean(parsed.joinToken || parsed.JoinToken);
      } catch (e) {
        return false;
      }
    },
  });
  admitted.add(gotToken);

  if (!gotToken) {
    // Refusals are cheap; continuing would measure the error path and report it as throughput.
    return;
  }

  // 2. Take the roster row. Separate endpoint, separately authorized since §7.4e, and the one
  //    the teacher's participant count reads.
  const joined = joinStream(token, world.sessionId);
  check(joined, { 'joined the roster': (r) => r.status >= 200 && r.status < 300 });

  // 3. What a real client does immediately after joining: read the backlog.
  const chat = chatHistory(token, world.sessionId);
  check(chat, { 'chat history readable': (r) => r.status === 200 });

  // A student sits in the lecture; they do not immediately leave. The sleep keeps the roster
  // populated, which is the state the participant count is computed under.
  sleep(Math.random() * 3 + 2);

  leaveStream(token, world.sessionId);
}

export function teardown(world) {
  // Leave the platform as it was found. A load run that abandons live sessions leaves LiveKit
  // rooms open and the assistant registered against them, and the next run's numbers are then
  // measured against a machine still busy with this one.
  endSession(world.teacher, world.classroomId, world.sessionId);
}
