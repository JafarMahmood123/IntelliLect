import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import type { User } from '../types';
import { logout as logoutApi } from '../features/auth/api/auth';

interface AuthState {
  user: User | null;
  isAuthenticated: boolean;
  setAuth: (user: User, accessToken: string, refreshToken: string) => void;
  setUser: (user: User) => void;
  logout: () => Promise<void>;
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      user: null,
      isAuthenticated: false,

      setAuth: (user, accessToken, refreshToken) => {
        localStorage.setItem('accessToken', accessToken);
        localStorage.setItem('refreshToken', refreshToken);
        set({ user, isAuthenticated: true });
      },

      setUser: (user) => {
        set({ user, isAuthenticated: true });
      },

      logout: async () => {
        const refreshToken = localStorage.getItem('refreshToken');

        // Ask the server to revoke the refresh token so the session cannot be resumed.
        try {
          if (refreshToken) {
            await logoutApi(refreshToken);
          }
        } catch {
          // Clear the local session regardless of the server response.
        } finally {
          localStorage.removeItem('accessToken');
          localStorage.removeItem('refreshToken');
          set({ user: null, isAuthenticated: false });
        }
      },
    }),
    {
      name: 'auth-storage',
    },
  ),
);