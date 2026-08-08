/**
 * §10.2, mix 4 — bulk approval of a large batch.
 *
 * A different shape from the other three and worth saying so plainly: this is not a herd. One
 * admin sends one request carrying hundreds of user ids, and the question is how that single
 * request behaves as the batch grows — not how many of them the platform serves per second.
 * The metric that matters is therefore iteration duration at a given batch size, and the
 * failure mode is a request that runs past nginx's fixed 60s proxy timeout and returns a 504
 * to an admin whose approvals may or may not have been applied.
 *
 * That ambiguity is the real risk, and it is why this script re-reads the pending list
 * afterwards rather than trusting the response. §2 made this endpoint report each account
 * separately because partial success is the expected outcome; a timeout is the one case where
 * the report never arrives, and the only way to know what happened is to ask.
 *
 * The batch is registered fresh in `setup()`. Approving accounts that some other run left
 * pending would give a first iteration that does real work and later ones that no-op, which
 * looks like a platform that gets faster under load.
 *
 *   k6 run backend/tests/load/load-bulk-approve.js
 *   LOAD_BATCH_SIZE=500 k6 run backend/tests/load/load-bulk-approve.js
 */

import { check } from 'k6';
import { Trend } from 'k6/metrics';
import http from 'k6/http';
import { adminToken, approveBulk, bearer, body, field } from './lib/api.js';
import { config, named } from './lib/config.js';
import { registerMany } from './lib/provision.js';

const batchSize = config.batchSize;

const bulkDuration = new Trend('bulk_approve_ms', true);

export const options = {
  scenarios: {
    one_big_batch: {
      // A single iteration. Repeating it would approve already-approved accounts, which is a
      // no-op path and not what is being measured.
      executor: 'shared-iterations',
      vus: 1,
      iterations: 1,
      maxDuration: '10m',
    },
  },
  thresholds: {
    // nginx's proxy timeout is 60s and it is not configurable per route here. A bulk approve
    // that crosses it returns a 504 to the admin with no report of what was applied, which is
    // worse than a refusal. The threshold sits below it deliberately.
    bulk_approve_ms: ['p(100)<55000'],
    'http_req_failed{name:admin-bulk-status}': ['rate==0'],
  },
};

export function setup() {
  const admin = adminToken();
  const { userIds } = registerMany(batchSize, 'Student', 'bulk');
  return { admin, userIds };
}

export default function (world) {
  const response = approveBulk(world.admin, world.userIds);
  bulkDuration.add(response.timings.duration);

  check(response, {
    'bulk approve answered': (r) => r.status >= 200 && r.status < 300,
    // The specific failure this script exists to catch. A 504 means nginx gave up on a request
    // the service may well have completed.
    'did not time out at the gateway': (r) => r.status !== 504,
  });

  if (response.status < 200 || response.status >= 300) return;

  const parsed = body(response, 'bulk approve');
  const rows = field(parsed, 'results', field(parsed, 'items', []));
  check(rows, {
    'every id in the batch is reported': (r) => r.length === world.userIds.length,
  });
}

export function teardown(world) {
  // The reconciliation, and the reason a 504 is not simply a slow success: ask the platform
  // what it actually did. Anything still pending was in a batch the admin was told nothing
  // about.
  const pending = http.get(
    `${config.gateway}/api/admin/requests`,
    named('admin-pending', bearer(world.admin)),
  );
  if (pending.status !== 200) {
    console.log(`[reconciliation] could not re-read the pending list: HTTP ${pending.status}`);
    return;
  }

  const parsed = body(pending, 'pending requests');
  const rows = Array.isArray(parsed) ? parsed : field(parsed, 'items', []);
  const stillPending = rows.filter((row) =>
    world.userIds.includes(String(field(row, 'userId', field(row, 'id', '')))),
  );

  console.log(
    `[reconciliation] ${stillPending.length} of ${batchSize} accounts are still pending after ` +
      'the bulk approve. Anything above zero alongside a 2xx response is an approval the ' +
      'endpoint reported and did not perform.',
  );
}
