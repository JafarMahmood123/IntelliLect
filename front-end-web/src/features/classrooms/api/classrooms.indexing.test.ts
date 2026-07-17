import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('../../../lib/axios', () => ({
  apiClient: { get: vi.fn() },
}));

import { apiClient } from '../../../lib/axios';
import { getFileIndexingStatus } from './classrooms';

const mockGet = vi.mocked(apiClient.get);

describe('getFileIndexingStatus', () => {
  afterEach(() => vi.clearAllMocks());

  it('calls the member-authorized indexing-status endpoint (no internal path/secret)', async () => {
    mockGet.mockResolvedValue({ data: { fileId: 'f1', status: 'Done' } });

    const result = await getFileIndexingStatus('c1', 'f1');

    expect(mockGet).toHaveBeenCalledWith(
      '/classrooms/c1/files/f1/indexing-status',
    );
    // The browser never targets KnowledgeService's internal API or sends a secret.
    const [url, config] = mockGet.mock.calls[0];
    expect(url).not.toContain('internal');
    expect(config).toBeUndefined();
    expect(result.status).toBe('Done');
  });
});
