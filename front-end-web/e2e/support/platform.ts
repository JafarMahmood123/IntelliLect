/**
 * Arrangement for the browser journey, done over the API.
 *
 * Everything a journey needs to *exist* before a browser opens — accounts, a classroom, an
 * enrolment, a live session, a published quiz — is created here with HTTP calls rather than by
 * driving the UI. That is a deliberate split and worth defending:
 *
 * - Registering a teacher through the form, approving them through the admin screen and
 *   creating a classroom through its drawer would make every journey depend on five features
 *   it is not testing. When one of them changes, every journey goes red at once and none of
 *   the failures names the thing that broke.
 * - The API paths are already covered by `backend/tests/e2e` and are asserted there. Driving
 *   them again through a browser adds minutes and no information.
 *
 * What the browser is for is the part nothing else reaches: the seams between §4 (quizzes),
 * §5 (in-session notifications) and §6 (marks), rendered by the real React app against the
 * real services.
 *
 * The paths and payload shapes here mirror `backend/tests/e2e/clients/`. When an endpoint
 * moves, both change.
 */

import { request, type APIRequestContext } from '@playwright/test';

const env = (name: string, fallback: string): string => {
  const value = process.env[name];
  return value !== undefined && value !== '' ? value : fallback;
};

export const platformConfig = {
  /** nginx, the same surface the app's `/api` proxy points at. */
  gateway: env('E2E_GATEWAY_URL', 'http://localhost'),
  adminEmail: env('E2E_ADMIN_EMAIL', 'admin@intellilect.com'),
  adminPassword: env('E2E_ADMIN_PASSWORD', 'Admin123!'),
  userPassword: env('E2E_USER_PASSWORD', 'Passw0rd!23'),
};

export interface Account {
  userId: string;
  email: string;
  password: string;
  token: string;
}

export interface QuizWorld {
  api: APIRequestContext;
  teacher: Account;
  student: Account;
  classroomId: string;
  classroomName: string;
  sessionId: string;
  quizId: string;
  /** The full mark, so the journey can assert a number rather than "something appeared". */
  totalPoints: number;
}

/** Two questions with a known key — the mark is then arithmetic the journey can predict. */
export const QUESTIONS = [
  {
    text: 'At what temperature does water boil at standard pressure?',
    points: 2,
    timeLimitSeconds: 900,
    options: [
      { text: '100 degrees Celsius', isCorrect: true },
      { text: '50 degrees Celsius', isCorrect: false },
    ],
  },
  {
    text: 'What happens to the boiling point as altitude increases?',
    points: 3,
    timeLimitSeconds: 900,
    options: [
      { text: 'It falls', isCorrect: true },
      { text: 'It rises', isCorrect: false },
    ],
  },
];

export const TOTAL_POINTS = QUESTIONS.reduce((sum, q) => sum + q.points, 0);

export const unique = (prefix: string): string =>
  `${prefix}${Date.now().toString(36)}${Math.floor(Math.random() * 1e6).toString(36)}`;

/** Case-insensitive field read: the .NET and Python halves disagree on casing. */
function field<T = unknown>(obj: Record<string, unknown>, name: string, fallback?: T): T {
  if (name in obj) return obj[name] as T;
  const lower = name.toLowerCase();
  for (const key of Object.keys(obj)) {
    if (key.toLowerCase() === lower) return obj[key] as T;
  }
  return fallback as T;
}

async function json(
  api: APIRequestContext,
  method: 'get' | 'post' | 'put',
  path: string,
  options: { data?: unknown; token?: string } = {},
): Promise<Record<string, unknown>> {
  const headers: Record<string, string> = {};
  if (options.token) headers.Authorization = `Bearer ${options.token}`;

  const response = await api[method](path, {
    headers,
    ...(options.data === undefined ? {} : { data: options.data }),
  });

  if (!response.ok()) {
    // The body, not just the status. A 403 from the classroom routes is now a real decision
    // (§7.2b/§7.4d) and its ProblemDetails says which check refused — losing that turns a
    // provisioning bug into a bare number.
    const body = await response.text();
    throw new Error(`${method.toUpperCase()} ${path} → ${response.status()}: ${body.slice(0, 400)}`);
  }

  const text = await response.text();
  return text ? (JSON.parse(text) as Record<string, unknown>) : {};
}

async function login(api: APIRequestContext, email: string, password: string): Promise<string> {
  const payload = await json(api, 'post', '/api/auth/login', { data: { email, password } });
  const token = field<string>(payload, 'accessToken');
  if (!token) throw new Error(`login ${email} returned no accessToken`);
  return token;
}

async function registerAndApprove(
  api: APIRequestContext,
  adminToken: string,
  roleName: 'Teacher' | 'Student',
  prefix: string,
): Promise<Account> {
  const rolesResponse = await api.get('/api/auth/registration-roles');
  if (!rolesResponse.ok()) {
    throw new Error(
      `GET /api/auth/registration-roles → ${rolesResponse.status()}: ` +
        (await rolesResponse.text()).slice(0, 300),
    );
  }
  const roles = (await rolesResponse.json()) as Array<Record<string, unknown>>;
  const role = roles.find((r) => String(field(r, 'name')) === roleName);
  if (!role) {
    throw new Error(
      `no self-registration role named ${roleName}; the endpoint offers ` +
        roles.map((r) => String(field(r, 'name'))).join(', '),
    );
  }

  const username = unique(prefix);
  const email = `${username}@browser.intellilect.test`;
  const registered = await json(api, 'post', '/api/auth/register', {
    data: {
      userName: username,
      email,
      firstName: 'Browser',
      lastName: roleName,
      roleId: String(field(role, 'id')),
      password: platformConfig.userPassword,
    },
  });
  const userId = String(field(registered, 'userId'));

  // Approval is a raw JSON string body — not an object. Getting this wrong yields a 400 that
  // reads like a validation failure on a field that does not exist.
  const approved = await api.put(`/api/admin/requests/${userId}/status`, {
    headers: { Authorization: `Bearer ${adminToken}`, 'Content-Type': 'application/json' },
    data: '"Active"',
  });
  if (!approved.ok()) {
    throw new Error(`approving ${email} → ${approved.status()}: ${(await approved.text()).slice(0, 300)}`);
  }

  const token = await login(api, email, platformConfig.userPassword);
  return { userId, email, password: platformConfig.userPassword, token };
}

/**
 * A teacher, an enrolled student, a live session and a published quiz.
 *
 * The session is started here rather than in the browser because starting one is the slowest
 * call in the platform — it waits on StreamingService, LiveKit and the assistant — and a
 * browser test that waits for it inside a `test()` spends its timeout on arrangement.
 */
export async function provisionLiveQuiz(): Promise<QuizWorld> {
  const api = await request.newContext({
    baseURL: platformConfig.gateway,
    // Generous: the session-start chain crosses three services, and nginx itself gives up at
    // 60s. A shorter timeout here would report the harness's impatience as a platform failure.
    timeout: 90_000,
  });

  const adminToken = await login(api, platformConfig.adminEmail, platformConfig.adminPassword);
  const teacher = await registerAndApprove(api, adminToken, 'Teacher', 'pwteacher');
  const student = await registerAndApprove(api, adminToken, 'Student', 'pwstudent');

  const classroomName = `Browser ${unique('cls')}`;
  const classroom = await json(api, 'post', '/api/classrooms', {
    token: teacher.token,
    data: { name: classroomName, description: 'Playwright journey (§11.12)' },
  });
  const classroomId = String(field(classroom, 'id'));

  await json(api, 'post', `/api/classrooms/${classroomId}/members/enroll`, {
    token: student.token,
  });

  const session = await json(api, 'post', `/api/classrooms/${classroomId}/sessions`, {
    token: teacher.token,
    data: {
      title: 'Boiling points',
      description: '',
      scheduledAtUtc: new Date(Date.now() + 5 * 60 * 1000).toISOString(),
      participationMode: 1,
    },
  });
  const sessionId = String(field(session, 'id'));

  await json(api, 'post', `/api/classrooms/${classroomId}/sessions/${sessionId}/start`, {
    token: teacher.token,
  });

  // Authored rather than generated. `GenerateDraftAsync` calls a model, so a generated quiz
  // would make this journey depend on Groq — and would leave it with no answer key, which is
  // what the final assertion needs to predict a mark.
  const quiz = await json(api, 'post', `/api/classrooms/${classroomId}/sessions/${sessionId}/quizzes`, {
    token: teacher.token,
    data: { title: 'Boiling points check', questions: QUESTIONS },
  });
  const quizId = String(field(quiz, 'id'));

  await json(api, 'post', `/api/classrooms/${classroomId}/quizzes/${quizId}/publish`, {
    token: teacher.token,
  });

  return {
    api,
    teacher,
    student,
    classroomId,
    classroomName,
    sessionId,
    quizId,
    totalPoints: TOTAL_POINTS,
  };
}

/** Closing is what computes and releases the marks — the student sees nothing until it runs. */
export async function closeQuiz(world: QuizWorld): Promise<void> {
  await json(world.api, 'post', `/api/classrooms/${world.classroomId}/quizzes/${world.quizId}/close`, {
    token: world.teacher.token,
  });
}

/**
 * Leave the platform as it was found.
 *
 * A journey that abandons a live session leaves a LiveKit room open and the assistant
 * registered against it, so the next run is measured against a machine still busy with this
 * one. Failures here are logged, never thrown: a cleanup that fails the test would replace a
 * real result with a housekeeping error.
 */
export async function teardown(world: QuizWorld): Promise<void> {
  try {
    await json(
      world.api,
      'post',
      `/api/classrooms/${world.classroomId}/sessions/${world.sessionId}/end`,
      { token: world.teacher.token },
    );
  } catch (error) {
    console.warn(`[cleanup] could not end session ${world.sessionId}:`, error);
  }
  await world.api.dispose();
}
