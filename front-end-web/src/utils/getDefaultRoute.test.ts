import { describe, expect, it } from 'vitest';
import { getDefaultRoute } from './getDefaultRoute';
import type { User } from '../types';

/**
 * Where each person lands after signing in.
 *
 * Small and pure, and consulted by every route guard — so an error here is not one wrong page,
 * it is everyone with that role sent somewhere they cannot use, on every navigation that
 * redirects.
 */
const user = (roleName: string, status = 'Active') => ({ roleName, status }) as User;

describe('getDefaultRoute', () => {
  it.each([
    ['SuperAdmin', '/super-admin'],
    ['Admin', '/admin'],
    ['Teacher', '/classrooms'],
    ['Student', '/classrooms'],
  ])('sends a %s to %s', (roleName, expected) => {
    expect(getDefaultRoute(user(roleName))).toBe(expected);
  });

  it('sends a signed-out visitor to login', () => {
    expect(getDefaultRoute(null)).toBe('/login');
    expect(getDefaultRoute(undefined)).toBe('/login');
  });

  it('status outranks role', () => {
    // A pending super admin is still pending. Checking the role first would drop them on a
    // dashboard whose every request the server refuses.
    expect(getDefaultRoute(user('SuperAdmin', 'Pending'))).toBe('/pending-approval');
    expect(getDefaultRoute(user('Teacher', 'Pending'))).toBe('/pending-approval');
  });

  it('falls back to the root for a role it does not know', () => {
    // A role added server-side before the client learns about it must not produce `undefined`
    // as a path, which react-router would treat as a relative navigation.
    expect(getDefaultRoute(user('Registrar'))).toBe('/');
  });
});
