import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AdminDashboard } from './AdminDashboard';
import { renderWithProviders } from '../../../test/test-utils';
import type { User } from '../../../types';

vi.mock('../api/admin', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/admin')>()),
  getPendingRequests: vi.fn(),
  getAllUsers: vi.fn(),
  bulkUpdateUserStatus: vi.fn(),
  updateUserStatus: vi.fn(),
}));
vi.mock('../../roles/hooks/useRolesQueries', () => ({
  useRegistrationRoles: () => ({ data: [], isLoading: false, isError: false }),
}));

import { getPendingRequests, getAllUsers, bulkUpdateUserStatus } from '../api/admin';

const mockPending = vi.mocked(getPendingRequests);
const mockAll = vi.mocked(getAllUsers);
const mockBulk = vi.mocked(bulkUpdateUserStatus);

const user = (id: string, firstName: string): User =>
  ({
    id,
    firstName,
    lastName: 'Test',
    userName: firstName.toLowerCase(),
    email: `${firstName.toLowerCase()}@intellilect.io`,
    roleName: 'Student',
    status: 'Pending',
    createdAtUtc: '2026-01-01T00:00:00Z',
  }) as User;

const page = (items: User[]) => ({
  items,
  pageNumber: 1,
  pageSize: 10,
  totalCount: items.length,
  totalPages: 1,
  hasPreviousPage: false,
  hasNextPage: false,
});

const arrange = (items: User[]) => {
  mockPending.mockResolvedValue(page(items));
  mockAll.mockResolvedValue(page([]));
};

/**
 * queryAllByRole, not getAllByRole: the latter THROWS when there are none, so it can never be
 * used to assert absence.
 */
const checkboxes = () => screen.queryAllByRole('checkbox');

describe('AdminDashboard bulk selection', () => {
  afterEach(() => vi.clearAllMocks());

  it('shows no action bar until something is selected', async () => {
    arrange([user('a', 'Amira')]);
    renderWithProviders(<AdminDashboard />);

    expect(await screen.findByText('Amira Test')).toBeInTheDocument();
    expect(screen.queryByText(/selected on this page/)).not.toBeInTheDocument();
  });

  it('selects a row and reports the count', async () => {
    arrange([user('a', 'Amira'), user('b', 'Bilal')]);
    renderWithProviders(<AdminDashboard />);
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText('Select Amira Test'));

    expect(await screen.findByText('1 selected on this page')).toBeInTheDocument();
  });

  it('select-all covers only the rows on this page', async () => {
    arrange([user('a', 'Amira'), user('b', 'Bilal')]);
    renderWithProviders(<AdminDashboard />);
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText('Select all accounts on this page'));

    expect(await screen.findByText('2 selected on this page')).toBeInTheDocument();
  });

  it('sends exactly the selected ids, once', async () => {
    arrange([user('a', 'Amira'), user('b', 'Bilal')]);
    mockBulk.mockResolvedValue({
      requested: 1,
      succeeded: 1,
      failed: 0,
      results: [{ userId: 'a', succeeded: true, status: 'Active', error: null }],
    });
    renderWithProviders(<AdminDashboard />);
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText('Select Amira Test'));
    await userEvent.click(screen.getByRole('button', { name: /Approve selected/ }));

    // The confirmation carries the number about to be affected, not a generic prompt.
    expect(await screen.findByText(/Approve 1 pending registration/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /Yes, apply to 1/ }));

    await waitFor(() =>
      expect(mockBulk).toHaveBeenCalledExactlyOnceWith(['a'], 'Accept'),
    );
  });

  it('surfaces per-account failures instead of reporting success', async () => {
    arrange([user('a', 'Amira'), user('b', 'Bilal')]);
    mockBulk.mockResolvedValue({
      requested: 2,
      succeeded: 1,
      failed: 1,
      results: [
        { userId: 'a', succeeded: true, status: 'Active', error: null },
        { userId: 'b', succeeded: false, status: 'Pending', error: 'Account not found.' },
      ],
    });
    renderWithProviders(<AdminDashboard />);
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText('Select all accounts on this page'));
    await userEvent.click(screen.getByRole('button', { name: /Approve selected/ }));
    await userEvent.click(screen.getByRole('button', { name: /Yes, apply to 2/ }));

    // The reason stays on screen — a partial failure is something to act on, not a toast to miss.
    const panel = await screen.findByText(/1 account\(s\) were not changed/);
    expect(panel).toBeInTheDocument();
    expect(await screen.findByText(/Account not found\./)).toBeInTheDocument();
  });

  it('keeps the failed accounts selected so they can be retried', async () => {
    arrange([user('a', 'Amira'), user('b', 'Bilal')]);
    mockBulk.mockResolvedValue({
      requested: 2,
      succeeded: 1,
      failed: 1,
      results: [
        { userId: 'a', succeeded: true, status: 'Active', error: null },
        { userId: 'b', succeeded: false, status: 'Pending', error: 'Account not found.' },
      ],
    });
    renderWithProviders(<AdminDashboard />);
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText('Select all accounts on this page'));
    await userEvent.click(screen.getByRole('button', { name: /Approve selected/ }));
    await userEvent.click(screen.getByRole('button', { name: /Yes, apply to 2/ }));

    // One survivor: the failure. The success dropped out of the selection.
    expect(await screen.findByText('1 selected on this page')).toBeInTheDocument();
  });

  it('clearing the selection hides the action bar', async () => {
    arrange([user('a', 'Amira')]);
    renderWithProviders(<AdminDashboard />);
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText('Select Amira Test'));
    await screen.findByText('1 selected on this page');
    await userEvent.click(screen.getByRole('button', { name: /Clear selection/ }));

    await waitFor(() =>
      expect(screen.queryByText(/selected on this page/)).not.toBeInTheDocument(),
    );
  });

  it('selecting a row does not open the details drawer', async () => {
    // The checkbox sits inside a clickable row; without stopPropagation every tick would also
    // open the drawer over the table.
    arrange([user('a', 'Amira')]);
    renderWithProviders(<AdminDashboard />);
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByLabelText('Select Amira Test'));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('renders no checkbox column on the all-users tab', async () => {
    // Bulk applies to pending registrations only; the other tab must not offer it.
    arrange([user('a', 'Amira')]);
    mockAll.mockResolvedValue(page([{ ...user('c', 'Carim'), status: 'Active' } as User]));
    renderWithProviders(<AdminDashboard />);
    await screen.findByText('Amira Test');

    await userEvent.click(screen.getByRole('button', { name: /All/i }));

    await waitFor(() => expect(checkboxes()).toHaveLength(0));
  });
});
