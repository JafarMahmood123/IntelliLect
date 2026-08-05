import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useAuthStore } from './useAuthStore';
import type { User } from '../types';

vi.mock('../features/auth/api/auth', () => ({ logout: vi.fn() }));

import { logout as logoutApi } from '../features/auth/api/auth';

const mockLogoutApi = vi.mocked(logoutApi);

/**
 * The session store, and specifically what signing out actually clears.
 *
 * The failure this guards against is silent by construction: a logout that leaves a token in
 * localStorage looks completely correct — the user is returned to the login screen and the app
 * behaves as though they left. The credential is simply still on the machine, which is exactly
 * the situation someone on a shared or lab computer used the button to avoid.
 */
const someone = { id: 'u1', roleName: 'Student', status: 'Active' } as User;

describe('useAuthStore', () => {
  beforeEach(() => {
    localStorage.clear();
    useAuthStore.setState({ user: null, isAuthenticated: false });
    mockLogoutApi.mockResolvedValue(undefined as never);
  });

  afterEach(() => vi.clearAllMocks());

  it('signing in stores both tokens and the user', () => {
    useAuthStore.getState().setAuth(someone, 'access-1', 'refresh-1');

    expect(localStorage.getItem('accessToken')).toBe('access-1');
    expect(localStorage.getItem('refreshToken')).toBe('refresh-1');
    expect(useAuthStore.getState().isAuthenticated).toBe(true);
    expect(useAuthStore.getState().user).toEqual(someone);
  });

  it('signing out revokes the session server-side before forgetting it', async () => {
    // Clearing locally alone would leave a refresh token the server still honours — the session
    // would survive the logout on any machine holding a copy.
    useAuthStore.getState().setAuth(someone, 'access-1', 'refresh-1');

    await useAuthStore.getState().logout();

    expect(mockLogoutApi).toHaveBeenCalledExactlyOnceWith('refresh-1');
  });

  it('signing out clears the local session', async () => {
    useAuthStore.getState().setAuth(someone, 'access-1', 'refresh-1');

    await useAuthStore.getState().logout();

    expect(localStorage.getItem('accessToken')).toBeNull();
    expect(localStorage.getItem('refreshToken')).toBeNull();
    expect(useAuthStore.getState().user).toBeNull();
    expect(useAuthStore.getState().isAuthenticated).toBe(false);
    // The store is persisted, so clearing it in memory is not enough — the mirrored copy must
    // not keep the profile of whoever just left the machine.
    expect(localStorage.getItem('auth-storage') ?? '').not.toContain(someone.id);
  });

  it('a server that refuses the revocation still ends the local session', async () => {
    // The offline case, and the one that matters on a shared machine: if a failed request left
    // the tokens in place, the button would appear to work and change nothing.
    useAuthStore.getState().setAuth(someone, 'access-1', 'refresh-1');
    mockLogoutApi.mockRejectedValue(new Error('network down'));

    await useAuthStore.getState().logout();

    expect(localStorage.getItem('accessToken')).toBeNull();
    expect(localStorage.getItem('refreshToken')).toBeNull();
    expect(useAuthStore.getState().isAuthenticated).toBe(false);
  });

  it('signing out with no refresh token does not call the server', async () => {
    // Nothing to revoke. Calling anyway would send a request guaranteed to fail and log noise.
    useAuthStore.setState({ user: someone, isAuthenticated: true });

    await useAuthStore.getState().logout();

    expect(mockLogoutApi).not.toHaveBeenCalled();
    expect(useAuthStore.getState().isAuthenticated).toBe(false);
  });

  it('setUser refreshes the profile without touching the tokens', async () => {
    // Used after a profile edit; re-issuing or dropping tokens here would sign the user out
    // for changing their name.
    useAuthStore.getState().setAuth(someone, 'access-1', 'refresh-1');

    useAuthStore.getState().setUser({ ...someone, roleName: 'Teacher' } as User);

    expect(useAuthStore.getState().user?.roleName).toBe('Teacher');
    expect(localStorage.getItem('accessToken')).toBe('access-1');
  });
});
