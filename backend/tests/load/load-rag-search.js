/**
 * §10.2, mix 3 — retrieval under load.
 *
 * Every search is an embedding call plus a pgvector scan. The embedding is the expensive half
 * and it is **shared with the live assistant**: the same model serves the retrieval stage of
 * every idea the assistant evaluates during a lecture. So this scenario is not really "how
 * fast is search" — it is "what does a busy search surface cost the lecture happening at the
 * same time", which is why it is worth running while a session is live rather than on an idle
 * machine.
 *
 * Two things this script refuses to do quietly:
 *
 * 1. **It fails setup if the material never indexes.** Searching an empty index is fast and
 *    meaningless; a run that did it would report excellent p95s for a service doing no work.
 * 2. **It checks that results come back non-empty**, not merely 200. `search` answers 200 with
 *    an empty list for a query it cannot match, and under load a degraded embedder can return
 *    exactly that — a fast, successful, useless answer. Latency alone cannot see it.
 *
 * The scope check is here too, cheaply: every result must belong to the classroom asked for.
 * F-07 is the P0 of that area and a load run is a good place to notice a filter that only
 * holds when the connection pool is not saturated.
 *
 *   k6 run backend/tests/load/load-rag-search.js
 *   LOAD_SEARCH_RPS=15 k6 run backend/tests/load/load-rag-search.js
 */

import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';
import { body, field, indexingStatus, ragSearch, uploadMaterial } from './lib/api.js';
import { config } from './lib/config.js';
import { provisionClassroom, unique } from './lib/provision.js';

const searchRps = Number(__ENV.LOAD_SEARCH_RPS || 10);

const searchLatency = new Trend('rag_search_ms', true);
const searchUseful = new Rate('rag_search_returned_results');
const searchScoped = new Rate('rag_search_scoped_correctly');

const MATERIAL = (
  'Photosynthesis converts light energy into chemical energy. ' +
  'The light-dependent reactions occur in the thylakoid membrane. ' +
  'The Calvin cycle fixes carbon dioxide in the stroma. ' +
  'Chlorophyll absorbs most strongly in the blue and red parts of the spectrum. ' +
  'Stomata regulate gas exchange and water loss in the leaf. ' +
  'Rubisco is the enzyme that fixes carbon dioxide onto ribulose bisphosphate. '
).repeat(30);

//: Varied on purpose. One repeated query would be answered from whatever cache exists at any
//: layer, and would measure the cache rather than the pipeline.
const QUERIES = [
  'where do the light dependent reactions happen',
  'what does chlorophyll absorb',
  'explain the calvin cycle',
  'what is the role of rubisco',
  'how do stomata control water loss',
  'what converts light energy into chemical energy',
];

export const options = {
  scenarios: {
    searching: {
      executor: 'constant-arrival-rate',
      rate: searchRps,
      timeUnit: '1s',
      duration: '2m',
      preAllocatedVUs: Math.max(10, searchRps * 2),
      maxVUs: Math.max(30, searchRps * 6),
    },
  },
  thresholds: {
    // An embedding call plus a vector scan. 3s is the point at which the assistant's own
    // retrieval stage starts eating into the feedback budget in docs/latency.md.
    rag_search_ms: ['p(95)<3000'],
    rag_search_returned_results: ['rate>0.99'],
    // Not a performance threshold. A single leak is a defect, so the bar is every request.
    rag_search_scoped_correctly: ['rate==1.0'],
    'http_req_failed{name:rag-search}': ['rate<0.01'],
  },
};

export function setup() {
  const world = provisionClassroom({ studentCount: 1, live: false, label: 'rag' });

  const fileId = uploadMaterial(
    world.teacher,
    world.classroomId,
    `${unique('material')}.txt`,
    MATERIAL,
  );

  // Ingestion is asynchronous by design — the upload returns as soon as the bytes are stored.
  // Polling rather than sleeping, because the first ingest of a cold deployment loads the
  // embedding model and can take minutes, while a warm one takes seconds.
  const deadline = Date.now() + config.ingestTimeoutMs;
  let status = 'Unknown';
  while (Date.now() < deadline) {
    status = indexingStatus(fileId);
    if (status === 'Indexed' || status === 'Failed') break;
    sleep(2);
  }
  if (status !== 'Indexed') {
    throw new Error(
      `material ended as ${status} rather than Indexed. Searching an empty index would report ` +
        'excellent latency for a service doing no work, so this run is aborted instead.',
    );
  }

  // The vacuum guard, run once before any load: prove the corpus is actually retrievable.
  // Without it, `rag_search_returned_results` failing at 100% would be indistinguishable from
  // a broken deployment and a genuine load failure.
  const probe = ragSearch(world.classroomId, QUERIES[0], 6);
  if (probe.status !== 200 || field(body(probe, 'probe search'), 'results', []).length === 0) {
    throw new Error(
      `indexed material returned no results for "${QUERIES[0]}" before any load was applied ` +
        `(HTTP ${probe.status}). The corpus, not the platform's capacity, is the problem.`,
    );
  }

  return { classroomId: world.classroomId };
}

export default function (world) {
  const query = QUERIES[Math.floor(Math.random() * QUERIES.length)];
  const response = ragSearch(world.classroomId, query, 6);
  searchLatency.add(response.timings.duration);

  const ok = check(response, { 'search answered': (r) => r.status === 200 });
  if (!ok) {
    searchUseful.add(false);
    return;
  }

  const results = field(body(response, 'search'), 'results', []);
  searchUseful.add(results.length > 0);

  // Every chunk must belong to the classroom that was asked for. Results that carry no
  // classroom id are counted as scoped — the assertion is about what the field says when it is
  // there, and inventing a failure from an absent field would make this rule noise.
  const foreign = results.filter((r) => {
    const owner = field(r, 'classroomId', null);
    return owner !== null && String(owner) !== String(world.classroomId);
  });
  searchScoped.add(foreign.length === 0);
  check(foreign, { 'no other classroom material in the results': (f) => f.length === 0 });
}
