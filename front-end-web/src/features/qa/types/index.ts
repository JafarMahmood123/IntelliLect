// Student Q&A over classroom material (F-5). The classroom scope is always taken
// from the current route/context — never entered by the user — and enforced by
// server-side membership.

export interface QaSource {
  citation: number;
  documentId: string;
  page: number | null;
  slide: number | null;
  section: string | null;
}

export interface QaAnswerResponse {
  answer: string;
  sources: QaSource[];
  /** false when retrieval found no relevant material (don't render as a real answer). */
  hasAnswer: boolean;
}

/** A single asked/answered pair kept in the in-session history. */
export interface QaEntry {
  id: string;
  question: string;
  response: QaAnswerResponse;
}
