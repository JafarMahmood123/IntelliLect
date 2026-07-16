import { describe, expect, it } from 'vitest';
import { parseTeachingSuggestion } from './teachingSuggestion';

const encode = (value: unknown): Uint8Array =>
  new TextEncoder().encode(
    typeof value === 'string' ? value : JSON.stringify(value),
  );

const validPayload = {
  type: 'teaching_suggestion',
  version: 1,
  session_id: 'sess-1',
  feedback_type: 'discrepancy',
  text: 'Slide 4 contradicts what you just said about latency.',
  sources: [
    { citation: 1, document_id: 'doc-1', page: null, slide: 4, section: null },
    { citation: 2, document_id: 'doc-1', page: 12, slide: null, section: 'Intro' },
  ],
  created_at: '2026-01-01T10:00:00Z',
};

describe('parseTeachingSuggestion', () => {
  it('parses a valid payload', () => {
    const result = parseTeachingSuggestion(encode(validPayload));
    expect(result).not.toBeNull();
    expect(result?.feedbackType).toBe('discrepancy');
    expect(result?.text).toBe(validPayload.text);
    expect(result?.sessionId).toBe('sess-1');
    expect(result?.sources).toHaveLength(2);
    expect(result?.id).toBeTruthy();
  });

  it('ignores a non-matching type', () => {
    expect(
      parseTeachingSuggestion(encode({ ...validPayload, type: 'chat' })),
    ).toBeNull();
  });

  it('ignores an unknown version (forward-compat)', () => {
    expect(
      parseTeachingSuggestion(encode({ ...validPayload, version: 2 })),
    ).toBeNull();
    expect(
      parseTeachingSuggestion(encode({ ...validPayload, version: undefined })),
    ).toBeNull();
  });

  it('does not throw on malformed JSON, returns null', () => {
    expect(parseTeachingSuggestion(encode('not-json{'))).toBeNull();
  });

  it('falls back to "unclear" for an unknown feedback_type', () => {
    const result = parseTeachingSuggestion(
      encode({ ...validPayload, feedback_type: 'weird' }),
    );
    expect(result?.feedbackType).toBe('unclear');
  });

  it('drops sources missing a citation or document id, and tolerates non-arrays', () => {
    const result = parseTeachingSuggestion(
      encode({
        ...validPayload,
        sources: [
          { citation: 1, document_id: 'doc-1', page: 3, slide: null, section: null },
          { citation: 2, page: 5 }, // no document_id -> dropped
          { document_id: 'doc-2' }, // no citation -> dropped
        ],
      }),
    );
    expect(result?.sources).toHaveLength(1);

    const noSources = parseTeachingSuggestion(
      encode({ ...validPayload, sources: 'nope' }),
    );
    expect(noSources?.sources).toEqual([]);
  });

  it('ignores a payload with no usable text', () => {
    expect(
      parseTeachingSuggestion(encode({ ...validPayload, text: '   ' })),
    ).toBeNull();
  });
});
