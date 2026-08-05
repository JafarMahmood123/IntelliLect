import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { UsersDirectoryPage } from './UsersDirectoryPage';
import { renderWithProviders } from '../../../test/test-utils';
import { useAuthStore } from '../../../store/useAuthStore';
import type { User } from '../../../types';
import type { UserSummary } from '../types';

vi.mock('../api/users', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/users')>()),
  searchUsers: vi.fn(),
  bulkChangeUserStatus: vi.fn(),
  changeUserStatus: vi.fn(),
}));

import { searchUsers, bulkChangeUserStatus } from '../api/users';

const mockSearch = vi.mocked(searchUsers);
const mockBulk = vi.mocked(bulkChangeUserStatus);

const SELF_ID = 'self-super-admin';

const account = (id: string, firstName: string, status: string): UserSummary => ({
  id,
  userName: firstName.toLowerCase(),
  email: `${firstName.toLowerCase()}@intellilect.io`,
  firstName,
  lastName: 'Test',
  roleName: 'Student',
  status,
  createdAtUtc: '2026-01-01T00:00:00Z',
  version: '1',
});

const arrange = (items: UserSummary[]) => {
  mockSearch.mockResolvedValue({
    items,
    pageNumber: 1,
    pageSize: 20,
    totalCount: items.length,
    totalPages: 1,
    hasPreviousPage: false,
    hasNextPage: false,
  });
};

const renderPage = () =>
  renderWithProviders(
    <MemoryRouter>
      <UsersDirectoryPage />
    </MemoryRouter>,
  );

/** The bulk result the server returns when every attempted account changed. */
const allSucceeded = (ids: string[], status: string) => ({
  requested: ids.length,
  succeeded: ids.length,
  failed: 0,
  results: ids.map((userId) => ({ userId, succeeded: true, status, error: null })),
});

describe('UsersDirectoryPage bulk status changes', () => {
  beforeEach(() => {
    // The self-target rule reads the signed-in account off the auth store.
    useAuthStore.setState({ user: { id: SELF_ID } as User, isAuthenticated: true });
  });

  afterEach(() => {
    vi.clearAllMocks();
    useAuthStore.setState({ user: null, isAuthenticated: false });
  });

  it('shows no action bar until something is selected', async () => {
    arrange([account('a', 'Amira', 'Pending')]);
    renderPage();

    expect(await screen.findByText('Amira Test')).toBeInTheDocument();
    expect(screen.queryByText(/selected on this page/)).not.toBeInTheDocument();
  });

  it('cannot select a Rejected account — the status is terminal', async () => {
    arrange([account('a', 'Amira', 'Rejected')]);
    renderPage();
    await screen.findByText('Amira Test');

    expect(screen.getByLabelText(/Amira Test cannot be changed/)).toBeDisabled();
  });

  it('cannot select your own account', async () => {
    // Active would otherwise be selectable — it is the identity that blocks it, not the status.
    arrange([account(SELF_ID, 'Sara', 'Active')]);
    renderPage();
    await screen.findByText('Sara Test');

    expect(screen.getByLabelText(/Sara Test cannot be changed/)).toBeDisabled();
  });

  it('select-all skips the rows that can never be acted on', async () => {
    arrange([
      account('a', 'Amira', 'Pending'),
      account('b', 'Bilal', 'Rejected'),
      account(SELF_ID, 'Sara', 'Active'),
    ]);
    renderPage();
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText(/Select every changeable account/));

    // Three rows on the page, one of them selectable.
    expect(await screen.findByText('1 selected on this page')).toBeInTheDocument();
  });

  it('offers only the actions that fit the selection, each with its own count', async () => {
    arrange([
      account('a', 'Amira', 'Pending'),
      account('b', 'Bilal', 'Pending'),
      account('c', 'Carim', 'Active'),
    ]);
    renderPage();
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText(/Select every changeable account/));

    // Accept and Reject reach the two pending accounts; Deactivate reaches the one active one.
    expect(await screen.findByRole('button', { name: 'Accept (2)' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reject (2)' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Deactivate (1)' })).toBeInTheDocument();
    // Nothing here is deactivated, so there is nothing to reactivate.
    expect(screen.queryByRole('button', { name: /Reactivate/ })).not.toBeInTheDocument();
  });

  it('sends only the accounts the action applies to', async () => {
    arrange([account('a', 'Amira', 'Pending'), account('c', 'Carim', 'Active')]);
    mockBulk.mockResolvedValue(allSucceeded(['a'], 'Active'));
    renderPage();
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText(/Select every changeable account/));
    await userEvent.click(screen.getByRole('button', { name: 'Accept (1)' }));

    // The confirmation counts what will change, and says plainly what will not.
    expect(await screen.findByText(/Accept 1 pending account/)).toBeInTheDocument();
    expect(
      screen.getByText(/1 other selected account\(s\) will be skipped/),
    ).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /Yes, apply to 1/ }));

    // Carim is Active: sending their id would be a request we already know cannot succeed.
    await waitFor(() => expect(mockBulk).toHaveBeenCalledExactlyOnceWith(['a'], 'Accept'));
  });

  it('keeps the untouched accounts selected after acting on part of a mixed selection', async () => {
    // The failure mode this guards: approving the pending half of a selection and silently
    // discarding the other half, leaving the admin to reselect rows they never acted on.
    arrange([account('a', 'Amira', 'Pending'), account('c', 'Carim', 'Active')]);
    mockBulk.mockResolvedValue(allSucceeded(['a'], 'Active'));
    renderPage();
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText(/Select every changeable account/));
    await userEvent.click(screen.getByRole('button', { name: 'Accept (1)' }));
    await userEvent.click(screen.getByRole('button', { name: /Yes, apply to 1/ }));

    // Amira changed and left the selection; Carim was never sent and stays.
    expect(await screen.findByText('1 selected on this page')).toBeInTheDocument();
    expect(screen.getByLabelText('Select Carim Test')).toBeChecked();
  });

  it('surfaces per-account failures instead of reporting success', async () => {
    arrange([account('a', 'Amira', 'Pending'), account('b', 'Bilal', 'Pending')]);
    mockBulk.mockResolvedValue({
      requested: 2,
      succeeded: 1,
      failed: 1,
      results: [
        { userId: 'a', succeeded: true, status: 'Active', error: null },
        { userId: 'b', succeeded: false, status: 'Pending', error: 'Account not found.' },
      ],
    });
    renderPage();
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText(/Select every changeable account/));
    await userEvent.click(screen.getByRole('button', { name: 'Accept (2)' }));
    await userEvent.click(screen.getByRole('button', { name: /Yes, apply to 2/ }));

    // The reason stays on screen — a partial failure is something to act on, not a toast to miss.
    expect(await screen.findByText(/1 account\(s\) were not changed/)).toBeInTheDocument();
    expect(screen.getByText(/Account not found\./)).toBeInTheDocument();
    // And the account that failed stays selected so it can be retried in place.
    expect(await screen.findByText('1 selected on this page')).toBeInTheDocument();
  });

  it('clearing the selection hides the action bar', async () => {
    arrange([account('a', 'Amira', 'Pending')]);
    renderPage();
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText('Select Amira Test'));
    await screen.findByText('1 selected on this page');
    await userEvent.click(screen.getByRole('button', { name: /Clear selection/ }));

    await waitFor(() =>
      expect(screen.queryByText(/selected on this page/)).not.toBeInTheDocument(),
    );
  });

  it('selecting a row does not open the user detail page', async () => {
    // The checkbox sits inside a row that navigates on click.
    arrange([account('a', 'Amira', 'Pending')]);
    renderPage();
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText('Select Amira Test'));

    // Still on the directory: the filters and the row are both present.
    expect(screen.getByText('Amira Test')).toBeInTheDocument();
    expect(await screen.findByText('1 selected on this page')).toBeInTheDocument();
  });
});
