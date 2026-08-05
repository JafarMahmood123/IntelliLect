import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import MockAdapter from 'axios-mock-adapter';
import axios from 'axios';
import { apiClient } from './axios';

/**
 * The axios interceptors — the app's session continuity, and the only place a signed-in user's
 * session is renewed or ended.
 *
 * Access tokens are short-lived by design, so an expiry mid-session is the normal case, not an
 * edge one: every user hits it if they stay on a page long enough. What happens next is either
 * invisible (the request is retried and the user notices nothing) or maximally visible (they are
 * thrown back to the login screen holding a valid session). There is very little in between,
 * which is why this file was worth taking from 31% to covered.
 */

/** The refresh endpoint is called on the bare `axios` default instance, not on `apiClient`. */
let onApi: MockAdapter;
let onBare: MockAdapter;

const setSession = (access = 'access-old', refresh = 'refresh-old') => {
  localStorage.setItem('accessToken', access);
  localStorage.setItem('refreshToken', refresh);
  localStorage.setItem('auth-storage', JSON.stringify({ state: { user: { id: 'u1' } } }));
};

/** jsdom refuses a real navigation, so `location` is replaced with something assignable. */
let assignedHref: string | null = null;

beforeEach(() => {
  localStorage.clear();
  assignedHref = null;
  onApi = new MockAdapter(apiClient);
  onBare = new MockAdapter(axios);

  Object.defineProperty(window, 'location', {
    configurable: true,
    value: {
      ...window.location,
      set href(value: string) {
        assignedHref = value;
      },
      get href() {
        return assignedHref ?? 'http://localhost/';
      },
    },
  });
});

afterEach(() => {
  onApi.restore();
  onBare.restore();
  vi.restoreAllMocks();
});

describe('the request interceptor', () => {
  it('attaches the stored access token to every request', async () => {
    setSession('access-1');
    onApi.onGet('/classrooms').reply(200, []);

    await apiClient.get('/classrooms');

    expect(onApi.history.get[0].headers?.Authorization).toBe('Bearer access-1');
  });

  it('sends no authorization header when nobody is signed in', async () => {
    // A stale or empty `Bearer ` would be rejected by the server as malformed rather than simply
    // treated as anonymous — turning a public request into an error.
    onApi.onGet('/public').reply(200, {});

    await apiClient.get('/public');

    expect(onApi.history.get[0].headers?.Authorization).toBeUndefined();
  });
});

describe('the response interceptor', () => {
  it('passes a successful response through untouched', async () => {
    setSession();
    onApi.onGet('/classrooms').reply(200, [{ id: 'c1' }]);

    const response = await apiClient.get('/classrooms');

    expect(response.data).toEqual([{ id: 'c1' }]);
  });

  it('leaves an error that is not a 401 alone', async () => {
    // A 403 or a 500 has nothing to do with the session; refreshing on one would hide the real
    // error behind a token round trip and, on failure, sign the user out over a server fault.
    setSession();
    onApi.onGet('/classrooms').reply(500, { detail: 'boom' });

    await expect(apiClient.get('/classrooms')).rejects.toMatchObject({
      response: { status: 500 },
    });
    expect(onBare.history.post).toHaveLength(0);
  });

  it('refreshes on a 401 and retries the original request with the new token', async () => {
    // The invisible path, and the one that has to work: the user is mid-action and should never
    // learn that their token expired.
    setSession();
    onBare
      .onPost('/api/auth/refresh')
      .reply(200, { accessToken: 'access-new', refreshToken: 'refresh-new' });
    onApi
      .onGet('/classrooms')
      .replyOnce(401)
      .onGet('/classrooms')
      .reply(200, [{ id: 'c1' }]);

    const response = await apiClient.get('/classrooms');

    expect(response.data).toEqual([{ id: 'c1' }]);
    expect(onApi.history.get[1].headers?.Authorization).toBe('Bearer access-new');
  });

  it('stores the rotated tokens', async () => {
    // The backend revokes the old refresh token when it issues a new one, so failing to store
    // the replacement leaves the app holding a token that is already dead.
    setSession();
    onBare
      .onPost('/api/auth/refresh')
      .reply(200, { accessToken: 'access-new', refreshToken: 'refresh-new' });
    onApi.onGet('/classrooms').replyOnce(401).onGet('/classrooms').reply(200, {});

    await apiClient.get('/classrooms');

    expect(localStorage.getItem('accessToken')).toBe('access-new');
    expect(localStorage.getItem('refreshToken')).toBe('refresh-new');
  });

  it('sends the refresh token to the refresh endpoint', async () => {
    setSession('access-old', 'refresh-old');
    onBare
      .onPost('/api/auth/refresh')
      .reply(200, { accessToken: 'access-new', refreshToken: 'refresh-new' });
    onApi.onGet('/classrooms').replyOnce(401).onGet('/classrooms').reply(200, {});

    await apiClient.get('/classrooms');

    expect(JSON.parse(onBare.history.post[0].data)).toEqual({ refreshToken: 'refresh-old' });
  });

  it('does not refresh twice for the same request', async () => {
    /**
     * The infinite-loop guard. If the retried request 401s as well — a revoked account, or a
     * server that rejects the brand-new token — refreshing again spins forever against the auth
     * endpoint rather than signing the user out once.
     *
     * The refresh endpoint deliberately succeeds only ONCE here. Without that, removing the
     * guard makes this test hang instead of fail, and a test that hangs reports nothing; with
     * it, the loop terminates on the second refresh and the count assertion says what happened.
     */
    setSession();
    onBare
      .onPost('/api/auth/refresh')
      .replyOnce(200, { accessToken: 'access-new', refreshToken: 'refresh-new' })
      .onPost('/api/auth/refresh')
      .reply(500);
    onApi.onGet('/classrooms').reply(401);

    await expect(apiClient.get('/classrooms')).rejects.toBeTruthy();

    expect(onBare.history.post).toHaveLength(1);
  });

  it('does not try to refresh a failed login', async () => {
    // A wrong password is a 401. Refreshing on it would, when the refresh fails, redirect the
    // user away from the login form they are standing at — losing what they typed and reading
    // as the page reloading itself for no reason.
    onApi.onPost('/auth/login').reply(401, { detail: 'Invalid credentials.' });

    await expect(apiClient.post('/auth/login', {})).rejects.toMatchObject({
      response: { status: 401 },
    });
    expect(onBare.history.post).toHaveLength(0);
    expect(assignedHref).toBeNull();
  });

  it('does not try to refresh a failed refresh', async () => {
    // Otherwise a rejected refresh triggers another refresh.
    setSession();
    onApi.onPost('/auth/refresh').reply(401);

    await expect(apiClient.post('/auth/refresh', {})).rejects.toBeTruthy();

    expect(onBare.history.post).toHaveLength(0);
  });

  it('signs the user out when there is no refresh token to use', async () => {
    localStorage.setItem('accessToken', 'access-old');
    onApi.onGet('/classrooms').reply(401);

    await expect(apiClient.get('/classrooms')).rejects.toBeTruthy();

    expect(localStorage.getItem('accessToken')).toBeNull();
    expect(assignedHref).toBe('/login');
  });

  it('clears every trace of the session when the refresh is rejected', async () => {
    // Including `auth-storage`, the persisted profile. Leaving it behind means the next visitor
    // to this browser sees the previous user's name and role on the login screen.
    setSession();
    onBare.onPost('/api/auth/refresh').reply(401);
    onApi.onGet('/classrooms').reply(401);

    await expect(apiClient.get('/classrooms')).rejects.toBeTruthy();

    expect(localStorage.getItem('accessToken')).toBeNull();
    expect(localStorage.getItem('refreshToken')).toBeNull();
    expect(localStorage.getItem('auth-storage')).toBeNull();
    expect(assignedHref).toBe('/login');
  });

  it('refreshes once for several requests that expire together', async () => {
    /**
     * The failure this whole file was worth writing for.
     *
     * A dashboard fires several requests at once. When the access token expires they all 401
     * together, and each one independently reads the SAME refresh token and posts it. The
     * backend rotates: the first call revokes that token and issues a new one, so the second
     * and third arrive with a token that is already dead, fail, and take the log-out branch.
     *
     * The user is signed out even though the refresh succeeded — and because it depends on how
     * many requests happened to be in flight, it looks random.
     */
    setSession();
    onBare
      .onPost('/api/auth/refresh')
      .reply(200, { accessToken: 'access-new', refreshToken: 'refresh-new' });
    onApi.onGet('/a').replyOnce(401).onGet('/a').reply(200, {});
    onApi.onGet('/b').replyOnce(401).onGet('/b').reply(200, {});
    onApi.onGet('/c').replyOnce(401).onGet('/c').reply(200, {});

    await Promise.all([apiClient.get('/a'), apiClient.get('/b'), apiClient.get('/c')]);

    expect(onBare.history.post).toHaveLength(1);
    expect(assignedHref).toBeNull();
    expect(localStorage.getItem('accessToken')).toBe('access-new');
  });

  it('every request that waited on a shared refresh gets the new token', async () => {
    /**
     * Not just "one refresh happened" — the requests that waited have to be retried with the
     * token that refresh produced, or they simply 401 again and the user is signed out anyway.
     *
     * Asserted by making the server DEMAND the new token rather than by reading the recorded
     * request headers: the retry mutates the original config object, so the mock adapter's
     * history shows the final header on the original entry too, and an assertion over it would
     * pass whatever the retry actually sent.
     */
    setSession();
    onBare
      .onPost('/api/auth/refresh')
      .reply(200, { accessToken: 'access-new', refreshToken: 'refresh-new' });

    const requiresNewToken = (config: { headers?: Record<string, unknown> }) =>
      config.headers?.Authorization === 'Bearer access-new' ? [200, { ok: true }] : [401];

    onApi.onGet('/a').replyOnce(401).onGet('/a').reply(requiresNewToken);
    onApi.onGet('/b').replyOnce(401).onGet('/b').reply(requiresNewToken);

    const [first, second] = await Promise.all([apiClient.get('/a'), apiClient.get('/b')]);

    expect(first.data).toEqual({ ok: true });
    expect(second.data).toEqual({ ok: true });
  });

  it('a later expiry starts a fresh refresh rather than reusing the finished one', async () => {
    // The shared promise must not be sticky: once it settles, the next 401 has to do real work
    // again, otherwise the session can never be renewed a second time.
    setSession();
    onBare
      .onPost('/api/auth/refresh')
      .replyOnce(200, { accessToken: 'access-2', refreshToken: 'refresh-2' })
      .onPost('/api/auth/refresh')
      .replyOnce(200, { accessToken: 'access-3', refreshToken: 'refresh-3' });
    onApi.onGet('/a').replyOnce(401).onGet('/a').reply(200, {});
    await apiClient.get('/a');

    onApi.resetHistory();
    onApi.onGet('/b').replyOnce(401).onGet('/b').reply(200, {});
    await apiClient.get('/b');

    expect(onBare.history.post).toHaveLength(2);
    expect(localStorage.getItem('accessToken')).toBe('access-3');
  });
});
