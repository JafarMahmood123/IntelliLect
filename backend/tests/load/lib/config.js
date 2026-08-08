// Environment-driven configuration, deliberately sharing the `E2E_*` variable names with
// `backend/tests/e2e/config.py`.
//
// Two harnesses reading two different sets of variables for the same platform is how a load
// run ends up pointed at a stale deployment while the functional suite is green against the
// real one. One name per fact.

const env = (name, fallback) =>
  __ENV[name] !== undefined && __ENV[name] !== '' ? __ENV[name] : fallback;

const num = (name, fallback) => {
  const raw = env(name, null);
  if (raw === null) return fallback;
  const parsed = Number(raw);
  if (!Number.isFinite(parsed)) {
    throw new Error(`${name}=${raw} is not a number`);
  }
  return parsed;
};

export const config = {
  // The public surface, through nginx. Load through the gateway rather than around it: nginx's
  // worker count, keepalive pool and 60s proxy timeout are part of what is being measured, and
  // bypassing it measures a deployment nobody runs.
  gateway: env('E2E_GATEWAY_URL', 'http://localhost'),

  // RagService is not published through nginx (the gateway does not route `/api/search`), so
  // the retrieval mix talks to it directly with the shared secret.
  knowledge: env('E2E_KNOWLEDGE_URL', 'http://localhost:8083'),
  internalSecret: env('E2E_INTERNAL_SECRET', 'changeme-internal-secret'),

  adminEmail: env('E2E_ADMIN_EMAIL', 'admin@intellilect.com'),
  adminPassword: env('E2E_ADMIN_PASSWORD', 'Admin123!'),
  userPassword: env('E2E_USER_PASSWORD', 'Passw0rd!23'),

  // How many accounts each script provisions in setup(). Defaults are small enough that a
  // first run on a laptop finishes, and every script takes its own override.
  students: num('LOAD_STUDENTS', 50),
  batchSize: num('LOAD_BATCH_SIZE', 200),

  // Ingest is the slowest thing in setup(): the embedding model loads cold on the first call.
  ingestTimeoutMs: num('LOAD_INGEST_TIMEOUT_MS', 240000),

  // Per-request ceiling. Generous, because a request that takes 30s is a *result*, not an
  // error, and a timeout here would record it as the wrong kind of failure.
  timeout: env('LOAD_HTTP_TIMEOUT', '60s'),
};

/**
 * A stable metric tag for a URL that contains an id.
 *
 * k6 tags every request with its URL by default, so `/api/streams/{a-uuid}` produces one
 * metric row per session and the summary becomes unreadable — worse, no threshold can be
 * written against it, because thresholds match on tags. Every request in this harness passes
 * `tags: { name: ... }` instead.
 */
export const named = (name, headers = null) => {
  const params = { timeout: config.timeout, tags: { name } };
  if (headers) params.headers = headers;
  return params;
};
