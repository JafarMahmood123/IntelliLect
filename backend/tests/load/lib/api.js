// The platform's HTTP surface, as the load scripts use it.
//
// Paths and payload shapes are the same ones `backend/tests/e2e/clients/` drives, and the two
// must not be allowed to drift: if an endpoint moves, the functional suite fails loudly and
// this harness fails as a flat wall of 404s that reads like a broken deployment. When you
// change one, change both.

import http from 'k6/http';
import { config, named } from './config.js';

const JSON_HEADERS = { 'Content-Type': 'application/json' };

export const bearer = (token) => ({ Authorization: `Bearer ${token}` });

/**
 * Parse a response body, or explain why it could not be parsed.
 *
 * Deliberately not silent: a load script that treats an unparseable body as an empty object
 * turns a 502 from nginx into a missing field twenty lines later, which is the single most
 * expensive way to debug a load run.
 */
export function body(response, what) {
  if (response.status === 0) {
    throw new Error(`${what}: no response at all (${response.error_code} ${response.error})`);
  }
  try {
    return JSON.parse(response.body);
  } catch (e) {
    throw new Error(
      `${what}: HTTP ${response.status}, body is not JSON: ${String(response.body).slice(0, 300)}`,
    );
  }
}

export function expectOk(response, what) {
  if (response.status < 200 || response.status >= 300) {
    throw new Error(
      `${what}: HTTP ${response.status} ${String(response.body).slice(0, 300)}`,
    );
  }
  return response;
}

/** Case-insensitive field read — .NET and Python halves of the platform disagree on casing. */
export function field(obj, name, fallback = undefined) {
  if (obj === null || obj === undefined) return fallback;
  if (name in obj) return obj[name];
  const lower = name.toLowerCase();
  for (const key of Object.keys(obj)) {
    if (key.toLowerCase() === lower) return obj[key];
  }
  return fallback;
}

// --- accounts ------------------------------------------------------------------------------

export function registrationRoleIds() {
  const res = expectOk(
    http.get(`${config.gateway}/api/auth/registration-roles`, named('registration-roles')),
    'registration-roles',
  );
  const roles = body(res, 'registration-roles');
  const byName = {};
  for (const role of roles) {
    byName[String(field(role, 'name'))] = String(field(role, 'id'));
  }
  return byName;
}

export function register({ username, email, firstName, lastName, roleId }) {
  const res = expectOk(
    http.post(
      `${config.gateway}/api/auth/register`,
      JSON.stringify({
        userName: username,
        email,
        firstName,
        lastName,
        roleId,
        password: config.userPassword,
      }),
      named('register', JSON_HEADERS),
    ),
    `register ${email}`,
  );
  return String(field(body(res, 'register'), 'userId'));
}

export function login(email, password) {
  const res = expectOk(
    http.post(
      `${config.gateway}/api/auth/login`,
      JSON.stringify({ email, password }),
      named('login', JSON_HEADERS),
    ),
    `login ${email}`,
  );
  const token = field(body(res, 'login'), 'accessToken');
  if (!token) {
    throw new Error(`login ${email} returned no accessToken — not approved, or 2FA-gated`);
  }
  return String(token);
}

export const adminToken = () => login(config.adminEmail, config.adminPassword);

/** One request, many accounts — the endpoint §2 exists for, and scenario 4's subject. */
export function approveBulk(token, userIds, action = 'Accept') {
  const res = http.put(
    `${config.gateway}/api/admin/requests/status`,
    JSON.stringify({ userIds, action }),
    named('admin-bulk-status', { ...JSON_HEADERS, ...bearer(token) }),
  );
  return res;
}

// --- classrooms and sessions ---------------------------------------------------------------

export function createClassroom(token, name) {
  const res = expectOk(
    http.post(
      `${config.gateway}/api/classrooms`,
      JSON.stringify({ name, description: 'load harness' }),
      named('create-classroom', { ...JSON_HEADERS, ...bearer(token) }),
    ),
    'create classroom',
  );
  return String(field(body(res, 'create classroom'), 'id'));
}

export function enroll(token, classroomId) {
  return expectOk(
    http.post(
      `${config.gateway}/api/classrooms/${classroomId}/members/enroll`,
      null,
      named('enroll', bearer(token)),
    ),
    'enroll',
  );
}

export function createSession(token, classroomId, title) {
  const scheduled = new Date(Date.now() + 5 * 60 * 1000).toISOString();
  const res = expectOk(
    http.post(
      `${config.gateway}/api/classrooms/${classroomId}/sessions`,
      JSON.stringify({
        title,
        description: '',
        scheduledAtUtc: scheduled,
        participationMode: 1,
      }),
      named('create-session', { ...JSON_HEADERS, ...bearer(token) }),
    ),
    'create session',
  );
  return String(field(body(res, 'create session'), 'id'));
}

export function startSession(token, classroomId, sessionId) {
  // The slowest single call in the platform: it flips a row, calls StreamingService over the
  // internal surface, which opens a LiveKit room and notifies the assistant. Under nginx's
  // fixed 60s proxy timeout this is the call most likely to be the reason a setup() fails.
  return expectOk(
    http.post(
      `${config.gateway}/api/classrooms/${classroomId}/sessions/${sessionId}/start`,
      null,
      named('start-session', bearer(token)),
    ),
    'start session',
  );
}

export function endSession(token, classroomId, sessionId) {
  return http.post(
    `${config.gateway}/api/classrooms/${classroomId}/sessions/${sessionId}/end`,
    null,
    named('end-session', bearer(token)),
  );
}

// --- the live session ----------------------------------------------------------------------

/**
 * Mint a join token.
 *
 * This is the hop scenario 1 exists for. Since §7.4d it is no longer a local read: the request
 * makes a synchronous internal HTTP call to ClassroomService to ask whether the caller is a
 * member of the classroom, and only then signs a LiveKit grant. That call was added for a
 * correctness reason and its cost has never been measured under a class-sized arrival.
 */
export const getStream = (token, sessionId) =>
  http.get(`${config.gateway}/api/streams/${sessionId}`, named('stream-token', bearer(token)));

export const joinStream = (token, sessionId) =>
  http.post(`${config.gateway}/api/streams/${sessionId}/join`, null, named('stream-join', bearer(token)));

export const leaveStream = (token, sessionId) =>
  http.del(`${config.gateway}/api/streams/${sessionId}/leave`, null, named('stream-leave', bearer(token)));

export const chatHistory = (token, sessionId) =>
  http.get(`${config.gateway}/api/streams/${sessionId}/chat`, named('stream-chat', bearer(token)));

// --- quizzes -------------------------------------------------------------------------------

export function createQuizDraft(token, classroomId, sessionId, title, questions) {
  const res = expectOk(
    http.post(
      `${config.gateway}/api/classrooms/${classroomId}/sessions/${sessionId}/quizzes`,
      JSON.stringify({ title, questions }),
      named('quiz-draft', { ...JSON_HEADERS, ...bearer(token) }),
    ),
    'create quiz draft',
  );
  return String(field(body(res, 'create quiz draft'), 'id'));
}

export const publishQuiz = (token, classroomId, quizId) =>
  expectOk(
    http.post(
      `${config.gateway}/api/classrooms/${classroomId}/quizzes/${quizId}/publish`,
      null,
      named('quiz-publish', bearer(token)),
    ),
    'publish quiz',
  );

export const studentQuizView = (token, classroomId, quizId) =>
  http.get(
    `${config.gateway}/api/classrooms/${classroomId}/quizzes/${quizId}/student-view`,
    named('quiz-student-view', bearer(token)),
  );

export const answerQuiz = (token, classroomId, quizId, questionId, optionId) =>
  http.post(
    `${config.gateway}/api/classrooms/${classroomId}/quizzes/${quizId}/answers`,
    JSON.stringify({ questionId, optionId }),
    named('quiz-answer', { ...JSON_HEADERS, ...bearer(token) }),
  );

export const submitQuiz = (token, classroomId, quizId) =>
  http.post(
    `${config.gateway}/api/classrooms/${classroomId}/quizzes/${quizId}/submit`,
    null,
    named('quiz-submit', bearer(token)),
  );

export const closeQuiz = (token, classroomId, quizId) =>
  http.post(
    `${config.gateway}/api/classrooms/${classroomId}/quizzes/${quizId}/close`,
    null,
    named('quiz-close', bearer(token)),
  );

export const quizResults = (token, classroomId, quizId) =>
  http.get(
    `${config.gateway}/api/classrooms/${classroomId}/quizzes/${quizId}/results`,
    named('quiz-results', bearer(token)),
  );

// --- material and retrieval ------------------------------------------------------------------

export function uploadMaterial(token, classroomId, fileName, content) {
  const res = expectOk(
    http.post(
      `${config.gateway}/api/classrooms/${classroomId}/files`,
      { file: http.file(content, fileName, 'text/plain') },
      named('upload-file', bearer(token)),
    ),
    'upload material',
  );
  return String(field(body(res, 'upload material'), 'id'));
}

export function indexingStatus(fileId) {
  const res = http.get(
    `${config.knowledge}/api/internal/documents/${fileId}/status`,
    named('doc-status', { 'X-Internal-Secret': config.internalSecret }),
  );
  if (res.status !== 200) return 'Unknown';
  return String(field(body(res, 'doc status'), 'status', 'Unknown'));
}

export const ragSearch = (classroomId, query, topK = 6) =>
  http.post(
    `${config.knowledge}/api/search`,
    JSON.stringify({ classroomId, query, topK }),
    named('rag-search', {
      'Content-Type': 'application/json',
      'X-Internal-Secret': config.internalSecret,
    }),
  );
