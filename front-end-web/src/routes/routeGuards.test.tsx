import { beforeEach, describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { ProtectedRoute } from './ProtectedRoute';
import { PublicRoute } from './PublicRoute';
import { RoleProtectedRoute } from './RoleProtectedRoute';
import { useAuthStore } from '../store/useAuthStore';
import type { User } from '../types';

/**
 * The client-side route guards.
 *
 * These are not the security boundary — the server is, and it refuses on its own terms. What
 * they decide is whether someone is shown a page they cannot use: a signed-out visitor watching
 * a dashboard render and then fill with failed requests, or a student reaching an admin screen
 * whose every call 403s. Getting that wrong looks like the product being broken.
 *
 * They had no tests at all, and the whole `src/routes` folder sat at 0%.
 */
const signIn = (roleName: string, status = 'Active') =>
  useAuthStore.setState({
    user: { id: 'u1', roleName, status } as User,
    isAuthenticated: true,
  });

/**
 * Reset in `beforeEach`, never `afterEach`: testing-library unmounts between tests, and a store
 * update in `afterEach` lands while the previous test's tree is still mounted — which React
 * reports as an un-acted update on every run.
 */
const signOut = () => useAuthStore.setState({ user: null, isAuthenticated: false });

/** Renders the guard at `/secret`, with recognisable stand-ins for every destination. */
const renderGuard = (guard: React.ReactNode) =>
  render(
    <MemoryRouter initialEntries={['/secret']}>
      <Routes>
        <Route element={guard}>
          <Route path="/secret" element={<div>the guarded page</div>} />
        </Route>
        <Route path="/login" element={<div>login page</div>} />
        <Route path="/pending-approval" element={<div>pending page</div>} />
        <Route path="/classrooms" element={<div>classrooms page</div>} />
        <Route path="/admin" element={<div>admin page</div>} />
        <Route path="/super-admin" element={<div>super admin page</div>} />
        {/* getDefaultRoute's fallback for a role the client does not know. Present so a
            redirect there is observable rather than a blank render. */}
        <Route path="/" element={<div>root page</div>} />
      </Routes>
    </MemoryRouter>,
  );

describe('ProtectedRoute', () => {
  beforeEach(signOut);

  it('lets an active signed-in user through', () => {
    signIn('Student');
    renderGuard(<ProtectedRoute />);
    expect(screen.getByText('the guarded page')).toBeInTheDocument();
  });

  it('sends a signed-out visitor to login', () => {
    signOut();
    renderGuard(<ProtectedRoute />);
    expect(screen.getByText('login page')).toBeInTheDocument();
    expect(screen.queryByText('the guarded page')).not.toBeInTheDocument();
  });

  it('holds a pending account on the approval page', () => {
    // They have real credentials and a real session; what they do not have is an approved
    // account, so every request behind this page would fail.
    signIn('Teacher', 'Pending');
    renderGuard(<ProtectedRoute />);
    expect(screen.getByText('pending page')).toBeInTheDocument();
  });

  it('checks authentication before status', () => {
    // A stale user object with no session must still go to login rather than to the pending
    // page, which would tell a signed-out visitor something about an account.
    useAuthStore.setState({
      user: { id: 'u1', roleName: 'Student', status: 'Pending' } as User,
      isAuthenticated: false,
    });
    renderGuard(<ProtectedRoute />);
    expect(screen.getByText('login page')).toBeInTheDocument();
  });
});

describe('PublicRoute', () => {
  beforeEach(signOut);

  it('shows the page to a signed-out visitor', () => {
    signOut();
    renderGuard(<PublicRoute />);
    expect(screen.getByText('the guarded page')).toBeInTheDocument();
  });

  it.each([
    ['SuperAdmin', 'super admin page'],
    ['Admin', 'admin page'],
    ['Teacher', 'classrooms page'],
  ])('sends a signed-in %s to their own landing page', (roleName, expected) => {
    // Landing an already-signed-in admin on the login form is the bug this prevents.
    signIn(roleName);
    renderGuard(<PublicRoute />);
    expect(screen.getByText(expected)).toBeInTheDocument();
  });

  it('sends a signed-in pending user to the approval page, not to a dashboard', () => {
    signIn('Teacher', 'Pending');
    renderGuard(<PublicRoute />);
    expect(screen.getByText('pending page')).toBeInTheDocument();
  });
});

describe('RoleProtectedRoute', () => {
  beforeEach(signOut);

  it('lets an allowed role through', () => {
    signIn('Admin');
    renderGuard(<RoleProtectedRoute allowedRoles={['Admin', 'SuperAdmin']} />);
    expect(screen.getByText('the guarded page')).toBeInTheDocument();
  });

  it('turns a disallowed role away to their own landing page', () => {
    // Not to login: they ARE signed in, and bouncing them to a login form would read as their
    // session having expired.
    signIn('Student');
    renderGuard(<RoleProtectedRoute allowedRoles={['Admin']} />);
    expect(screen.getByText('classrooms page')).toBeInTheDocument();
    expect(screen.queryByText('the guarded page')).not.toBeInTheDocument();
  });

  it('sends a visitor with no user to login', () => {
    signOut();
    renderGuard(<RoleProtectedRoute allowedRoles={['Admin']} />);
    expect(screen.getByText('login page')).toBeInTheDocument();
  });

  it('does not let a role in on a substring match', () => {
    // "SuperAdmin" contains "Admin". Anything matching loosely would hand the higher role a
    // page the lower one was scoped to — and the redirect proves it was turned away rather
    // than the page merely failing to render.
    signIn('SuperAdmin');
    renderGuard(<RoleProtectedRoute allowedRoles={['Admin']} />);
    expect(screen.getByText('super admin page')).toBeInTheDocument();
    expect(screen.queryByText('the guarded page')).not.toBeInTheDocument();
  });

  it('does not let a role in on a case-insensitive match', () => {
    // A casing change upstream must close the door, not open it. The unknown role falls to
    // getDefaultRoute's root, which is why that route exists in this harness.
    signIn('admin');
    renderGuard(<RoleProtectedRoute allowedRoles={['Admin']} />);
    expect(screen.getByText('root page')).toBeInTheDocument();
    expect(screen.queryByText('the guarded page')).not.toBeInTheDocument();
  });

  it('an empty allow-list admits nobody', () => {
    // A guard configured with no roles is a misconfiguration; it must fail closed.
    signIn('SuperAdmin');
    renderGuard(<RoleProtectedRoute allowedRoles={[]} />);
    expect(screen.queryByText('the guarded page')).not.toBeInTheDocument();
    expect(screen.getByText('super admin page')).toBeInTheDocument();
  });
});
