import type {
  FeedbackType,
  SuggestionSource,
  TeachingSuggestion,
} from '../types';

/** The only wire version this client understands. Unknown versions are ignored. */
export const SUPPORTED_SUGGESTION_VERSION = 1;

const FEEDBACK_TYPES: FeedbackType[] = ['discrepancy', 'gap', 'unclear'];

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

  const feedbackType = FEEDBACK_TYPES.includes(raw.feedback_type as FeedbackType)
    ? (raw.feedback_type as FeedbackType)
    : 'unclear';

  const sources = Array.isArray(raw.sources)
    ? raw.sources
        .map(normalizeSource)
        .filter((source): source is SuggestionSource => source !== null)
    : [];

  return {
    id: createClientId(),
    sessionId: asNullableString(raw.session_id) ?? '',
    feedbackType,
    text,
    sources,
    createdAt: asNullableString(raw.created_at) ?? new Date().toISOString(),
    receivedAt: Date.now(),
  };
};
