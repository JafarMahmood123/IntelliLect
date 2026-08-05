import type {
  FeedbackSeverity,
  FeedbackType,
  SuggestionSource,
  TeachingSuggestion,
} from '../types';

/**
 * The only wire version this client understands. Unknown versions are ignored.
 *
 * Bumped to 2 with `severity` and the correction span. Version 1 carried a `feedback_type` of
 * `unclear` that no longer exists here, so a v1 message is not merely missing fields — it would
 * misrender. Falling silent on it is the correct behaviour.
 */
export const SUPPORTED_SUGGESTION_VERSION = 2;

const FEEDBACK_TYPES: FeedbackType[] = ['discrepancy', 'gap', 'likely'];
const SEVERITIES: FeedbackSeverity[] = ['incorrect', 'likely', 'missing'];

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null;

const asNullableNumber = (value: unknown): number | null =>
  typeof value === 'number' && Number.isFinite(value) ? value : null;

const asNullableString = (value: unknown): string | null => {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
};

const createClientId = (): string => {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }
  return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
};

const normalizeSource = (value: unknown): SuggestionSource | null => {
  if (!isRecord(value)) return null;

  const citation = asNullableNumber(value.citation);
  const documentId = asNullableString(value.document_id);
  // A source is only useful if it can be cited and traced to a document.
  if (citation === null || documentId === null) return null;

  return {
    citation,
    documentId,
    page: asNullableNumber(value.page),
    slide: asNullableNumber(value.slide),
    section: asNullableString(value.section),
  };
};

/**
 * Decodes a LiveKit data payload into a TeachingSuggestion, or returns null if
 * it is not a suggestion we should render. Never throws — malformed JSON,
 * wrong `type`, or an unrecognized `version` are all ignored gracefully
 * (logged at debug), so a bad packet can never crash the room UI.
 */
export const parseTeachingSuggestion = (
  payload: Uint8Array,
  decoder: TextDecoder = new TextDecoder(),
): TeachingSuggestion | null => {
  let raw: unknown;
  try {
    raw = JSON.parse(decoder.decode(payload));
  } catch {
    console.debug('[TeacherFeedback] ignored non-JSON data payload');
    return null;
  }

  if (!isRecord(raw)) return null;
  if (raw.type !== 'teaching_suggestion') return null;
  // Forward-compat: ignore any version we don't explicitly understand.
  if (raw.version !== SUPPORTED_SUGGESTION_VERSION) {
    console.debug('[TeacherFeedback] ignored unknown suggestion version', raw.version);
    return null;
  }

  const text = asNullableString(raw.text);
  if (text === null) return null;

  // An unrecognized type or severity falls back to the hedged one. Overstating a claim the
  // server did not make is the only failure here with a cost: a teacher told flatly "this is
  // wrong", on a message this client did not actually understand, is worse than a cautious one.
  const feedbackType = FEEDBACK_TYPES.includes(raw.feedback_type as FeedbackType)
    ? (raw.feedback_type as FeedbackType)
    : 'likely';

  const severity = SEVERITIES.includes(raw.severity as FeedbackSeverity)
    ? (raw.severity as FeedbackSeverity)
    : 'likely';

  const sources = Array.isArray(raw.sources)
    ? raw.sources
        .map(normalizeSource)
        .filter((source): source is SuggestionSource => source !== null)
    : [];

  // The server drops a correction whose incorrect span failed verification, but this client is
  // the thing that paints them, so it enforces the same pairing rather than trusting it.
  const incorrectText = asNullableString(raw.incorrect_text);
  const correctedText = incorrectText === null ? null : asNullableString(raw.corrected_text);

  return {
    id: createClientId(),
    sessionId: asNullableString(raw.session_id) ?? '',
    feedbackType,
    severity,
    text,
    incorrectText,
    correctedText,
    sources,
    createdAt: asNullableString(raw.created_at) ?? new Date().toISOString(),
    receivedAt: Date.now(),
  };
};
