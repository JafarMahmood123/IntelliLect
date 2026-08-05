import { describe, expect, it } from 'vitest';
import { parseTeachingSuggestion } from './teachingSuggestion';

const encode = (value: unknown): Uint8Array =>
  new TextEncoder().encode(
    typeof value === 'string' ? value : JSON.stringify(value),
  );

const validPayload = {
  type: 'teaching_suggestion',
  version: 2,
  session_id: 'sess-1',
  feedback_type: 'discrepancy',
  severity: 'incorrect',
  text: 'Slide 4 contradicts what you just said about latency.',
  incorrect_text: 'about 40 milliseconds',
  corrected_text: 'about 40 microseconds',
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
    expect(result?.severity).toBe('incorrect');
    expect(result?.incorrectText).toBe('about 40 milliseconds');
    expect(result?.correctedText).toBe('about 40 microseconds');
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
      parseTeachingSuggestion(encode({ ...validPayload, version: 3 })),
    ).toBeNull();
    expect(
      parseTeachingSuggestion(encode({ ...validPayload, version: undefined })),
    ).toBeNull();
  });

  it('ignores a version 1 message rather than rendering it', () => {
    // v1 carried feedback_type "unclear", which no longer exists here. A v1 message is not
    // merely missing the new fields — it would misrender — so silence is the correct outcome.
    expect(
      parseTeachingSuggestion(
        encode({ ...validPayload, version: 1, feedback_type: 'unclear' }),
      ),
    ).toBeNull();
  });

  it('does not throw on malformed JSON, returns null', () => {
    expect(parseTeachingSuggestion(encode('not-json{'))).toBeNull();
  });

  it('falls back to the hedged type and severity for values it does not know', () => {
    // Overstating a claim the server did not make is the only failure here with a real cost:
    // telling a teacher "this is wrong" off a message we did not understand.
    const result = parseTeachingSuggestion(
      encode({ ...validPayload, feedback_type: 'weird', severity: 'catastrophic' }),
    );
    expect(result?.feedbackType).toBe('likely');
    expect(result?.severity).toBe('likely');
  });

  it('drops a correction whose incorrect span is missing', () => {
    // Green text alone reads as "it should be X" with no sign of what X replaces.
    const result = parseTeachingSuggestion(
      encode({ ...validPayload, incorrect_text: null }),
    );
    expect(result?.incorrectText).toBeNull();
    expect(result?.correctedText).toBeNull();
  });

  it('keeps an incorrect span that has no correction', () => {
    // Knowing a claim is wrong does not require knowing the right answer.
    const result = parseTeachingSuggestion(
      encode({ ...validPayload, corrected_text: null }),
    );
    expect(result?.incorrectText).toBe('about 40 milliseconds');
    expect(result?.correctedText).toBeNull();
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
