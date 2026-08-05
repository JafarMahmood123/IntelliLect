import axios from 'axios';

export const apiClient = axios.create({
  baseURL: '/api',
  headers: {
    'Content-Type': 'application/json',
  },
});

apiClient.interceptors.request.use(
  (config) => {
    const token = localStorage.getItem('accessToken');
    if (token && config.headers) {
      config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
  },
  (error) => Promise.reject(error)
);

/**
 * The refresh currently in flight, shared by every request waiting on it.
 *
 * A page fires several requests at once, so when the access token expires they all 401 together.
 * Refreshing per request meant each one independently read the SAME refresh token and posted it —
 * and the backend ROTATES: the first call revokes that token and issues a new one, so the second
 * and third arrived with a token that was already dead, failed, and took the sign-out branch.
 *
 * The user was signed out even though the refresh had succeeded, and because it depended on how
 * many requests happened to be in flight, it looked random.
 *
 * Cleared when it settles, so the next expiry starts a real refresh rather than replaying a
 * finished one.
 */
let refreshInFlight: Promise<string> | null = null;

const clearSession = () => {
  localStorage.removeItem('accessToken');
  localStorage.removeItem('refreshToken');
  // The persisted store too — left behind, the next person at this browser sees the previous
  // user's name and role.
  localStorage.removeItem('auth-storage');
};

/** Renews the session at most once at a time, resolving to the new access token. */
const refreshSession = (): Promise<string> => {
  if (refreshInFlight) return refreshInFlight;

  const attempt = (async () => {
    const refreshToken = localStorage.getItem('refreshToken');
    if (!refreshToken) {
      throw new Error('No refresh token available');
    }

    // On the bare instance, so this request does not re-enter the interceptor below.
    const response = await axios.post('/api/auth/refresh', { refreshToken });
    const { accessToken, refreshToken: newRefreshToken } = response.data;

    localStorage.setItem('accessToken', accessToken);
    localStorage.setItem('refreshToken', newRefreshToken);
    return accessToken as string;
  })();

  refreshInFlight = attempt.finally(() => {
    refreshInFlight = null;
  });
  return refreshInFlight;
};

apiClient.interceptors.response.use(
  (response) => response,
  async (error) => {
    const originalRequest = error.config;

    const isAuthRequest =
      originalRequest.url?.includes('/auth/login') || originalRequest.url?.includes('/auth/refresh');

    if (error.response?.status === 401 && !originalRequest._retry && !isAuthRequest) {
      originalRequest._retry = true;

      try {
        const accessToken = await refreshSession();

        // Belt and braces: replaying the request through `apiClient` runs the REQUEST
        // interceptor again, which re-reads the freshly stored token and overwrites this header
        // anyway. Set explicitly so the retry does not silently depend on that ordering — if the
        // request interceptor ever stops attaching the token, the retry still carries it.
        originalRequest.headers.Authorization = `Bearer ${accessToken}`;
        return apiClient(originalRequest);
      } catch (refreshError) {
        // The session cannot be renewed: end it here rather than leaving credentials behind.
        clearSession();
        window.location.href = '/login';

        return Promise.reject(refreshError);
      }
    }

    return Promise.reject(error);
  }
);
