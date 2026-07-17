import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AxiosError } from 'axios';
import { QaPanel } from './QaPanel';
import { renderWithProviders } from '../../../test/test-utils';
import type { QaAnswerResponse } from '../types';

vi.mock('../api/qa', () => ({
  askClassroomQuestion: vi.fn(),
}));

import { askClassroomQuestion } from '../api/qa';

const mockAsk = vi.mocked(askClassroomQuestion);

const CLASSROOM_ID = 'class-42';

const groundedAnswer: QaAnswerResponse = {
  answer: 'Mitochondria produce ATP [1].',
  sources: [
    { citation: 1, documentId: 'doc-1', page: 12, slide: null, section: null },
    { citation: 2, documentId: 'doc-1', page: null, slide: 4, section: null },
  ],
  hasAnswer: true,
};

describe('QaPanel', () => {
  afterEach(() => vi.clearAllMocks());

  it('submits with the CONTEXT classroom id and the typed question', async () => {
    const user = userEvent.setup();
    mockAsk.mockResolvedValue(groundedAnswer);

    renderWithProviders(<QaPanel classroomId={CLASSROOM_ID} />);

    await user.type(screen.getByLabelText('Your question'), 'What is ATP?');
    await user.click(screen.getByRole('button', { name: /ask/i }));

    await waitFor(() =>
      expect(mockAsk).toHaveBeenCalledWith(CLASSROOM_ID, 'What is ATP?'),
    );
    // The panel never accepts a classroom id from the user — only (contextId, question).
    expect(mockAsk).toHaveBeenCalledTimes(1);
  });

  it('renders the answer with citation chips', async () => {
    const user = userEvent.setup();
    mockAsk.mockResolvedValue(groundedAnswer);

    renderWithProviders(<QaPanel classroomId={CLASSROOM_ID} />);
    await user.type(screen.getByLabelText('Your question'), 'What is ATP?');
    await user.click(screen.getByRole('button', { name: /ask/i }));

    const answer = await screen.findByText('Mitochondria produce ATP [1].');
    expect(answer).toBeInTheDocument();
    // Backend content uses auto-direction so English renders cleanly in RTL chrome.
    expect(answer).toHaveAttribute('dir', 'auto');
    expect(screen.getByText('p. 12')).toBeInTheDocument();
    expect(screen.getByText('slide 4')).toBeInTheDocument();
  });

  it('shows the no-material state when hasAnswer is false', async () => {
    const user = userEvent.setup();
    mockAsk.mockResolvedValue({
      answer: 'I could not find relevant material.',
      sources: [],
      hasAnswer: false,
    });

    renderWithProviders(<QaPanel classroomId={CLASSROOM_ID} />);
    await user.type(screen.getByLabelText('Your question'), 'Unrelated?');
    await user.click(screen.getByRole('button', { name: /ask/i }));

    expect(
      await screen.findByText('No relevant material found for this question.'),
    ).toBeInTheDocument();
  });

  it('shows a permission message on a 403 (not a crash)', async () => {
    const user = userEvent.setup();
    mockAsk.mockRejectedValue(
      new AxiosError('Forbidden', 'ERR_BAD_REQUEST', undefined, undefined, {
        status: 403,
        data: {},
        statusText: 'Forbidden',
        headers: {},
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        config: {} as any,
      }),
    );

    renderWithProviders(<QaPanel classroomId={CLASSROOM_ID} />);
    await user.type(screen.getByLabelText('Your question'), 'Can I ask?');
    await user.click(screen.getByRole('button', { name: /ask/i }));

    expect(
      await screen.findByText("You don't have access to this classroom's Q&A."),
    ).toBeInTheDocument();
  });

  it('shows a generic error on other failures', async () => {
    const user = userEvent.setup();
    mockAsk.mockRejectedValue(new Error('boom'));

    renderWithProviders(<QaPanel classroomId={CLASSROOM_ID} />);
    await user.type(screen.getByLabelText('Your question'), 'hi');
    await user.click(screen.getByRole('button', { name: /ask/i }));

    expect(
      await screen.findByText('Something went wrong. Please try again.'),
    ).toBeInTheDocument();
  });

  it('routes chrome through i18n (no raw keys leak)', async () => {
    mockAsk.mockResolvedValue(groundedAnswer);
    const { container } = renderWithProviders(<QaPanel classroomId={CLASSROOM_ID} />);

    expect(screen.getByText('Ask about this classroom')).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/qa\./);
  });
});
