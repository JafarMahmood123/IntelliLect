import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { isAxiosError } from 'axios';
import { AlertTriangle, Loader2, SearchX, Send } from 'lucide-react';
import { Button } from '../../../components/ui/Button';
import { useAskQuestion } from '../hooks/useQa';
import type { QaEntry, QaSource } from '../types';

interface QaPanelProps {
  /** Classroom scope from route/context — never entered by the user. */
  classroomId: string;
}

const createId = (): string => {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }
  return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
};

export const QaPanel = ({ classroomId }: QaPanelProps) => {
  const { t } = useTranslation('qa');
  const askMutation = useAskQuestion(classroomId);

  const [question, setQuestion] = useState('');
  const [history, setHistory] = useState<QaEntry[]>([]);

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault();
    const trimmed = question.trim();
    if (!trimmed || askMutation.isPending) return;

    try {
      const response = await askMutation.mutateAsync(trimmed);
      // Newest first so the latest answer is at the top of the live region.
      setHistory((previous) => [
        { id: createId(), question: trimmed, response },
        ...previous,
      ]);
      setQuestion('');
    } catch {
      // Error surfaced from the mutation state below; keep the question for retry.
    }
  };

  const errorMessage = askMutation.isError
    ? isAxiosError(askMutation.error) && askMutation.error.response?.status === 403
      ? t('error.forbidden')
      : t('error.generic')
    : null;

  return (
    <div className="space-y-6">
      <div>
        <h3 className="text-lg font-bold text-slate-900 dark:text-white">
          {t('title')}
        </h3>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          {t('description')}
        </p>
      </div>

      <form onSubmit={handleSubmit} className="space-y-3">
        <label htmlFor="qa-question" className="sr-only">
          {t('inputLabel')}
        </label>
        <div className="flex flex-col gap-3 sm:flex-row">
          <input
            id="qa-question"
            type="text"
            dir="auto"
            value={question}
            onChange={(event) => setQuestion(event.target.value)}
            placeholder={t('placeholder')}
            disabled={askMutation.isPending}
            className="flex-1 rounded-lg border border-slate-200 bg-white px-4 py-2.5 text-sm text-slate-900 outline-none transition-colors placeholder:text-slate-400 focus:border-violet-500 focus-visible:ring-2 focus-visible:ring-violet-500/50 disabled:opacity-60 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100"
          />
          <Button
            type="submit"
            isLoading={askMutation.isPending}
            disabled={!question.trim()}
          >
            <Send size={16} />
            {t('ask')}
          </Button>
        </div>
      </form>

      {askMutation.isPending && (
        <div
          className="flex items-center gap-2 text-sm font-medium text-slate-500 dark:text-slate-400"
          role="status"
        >
          <Loader2 size={16} className="animate-spin" aria-hidden="true" />
          {t('thinking')}
        </div>
      )}

      {errorMessage && (
        <div
          role="alert"
          className="flex items-start gap-3 rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-600 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-400"
        >
          <AlertTriangle size={18} className="mt-0.5 shrink-0" aria-hidden="true" />
          <div>
            <p className="font-semibold">{t('error.title')}</p>
            <p className="mt-0.5">{errorMessage}</p>
          </div>
        </div>
      )}

      <div className="space-y-4" aria-live="polite">
        {history.length === 0 && !askMutation.isPending ? (
          <p className="py-8 text-center text-sm italic text-slate-500">
            {t('emptyHistory')}
          </p>
        ) : (
          history.map((entry) => <QaCard key={entry.id} entry={entry} />)
        )}
      </div>
    </div>
  );
};

const QaCard = ({ entry }: { entry: QaEntry }) => {
  const { t } = useTranslation('qa');
  const { question, response } = entry;

  return (
    <article className="rounded-2xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900/50">
      <p className="text-sm font-semibold text-slate-900 dark:text-white">
        <span className="text-slate-400">{t('you')}: </span>
        {/* User/backend content: auto-direction so an English question or answer
            renders LTR even inside RTL (Arabic) chrome. */}
        <span dir="auto">{question}</span>
      </p>

      <div className="mt-3 border-t border-slate-100 pt-3 dark:border-slate-800">
        {response.hasAnswer ? (
          <>
            <p
              dir="auto"
              className="whitespace-pre-wrap text-sm leading-relaxed text-slate-700 dark:text-slate-200"
            >
              {response.answer}
            </p>

            {response.sources.length > 0 && (
              <div className="mt-3">
                <p className="mb-1.5 text-[10px] font-bold uppercase tracking-wider text-slate-400">
                  {t('sourcesLabel')}
                </p>
                <ul className="flex flex-wrap gap-1.5">
                  {response.sources.map((source) => (
                    <li key={`${source.citation}-${source.documentId}`}>
                      <span className="inline-flex items-center gap-1 rounded-md bg-slate-100 px-2 py-1 text-[11px] font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                        <span className="font-bold text-violet-500 dark:text-violet-400">
                          [{source.citation}]
                        </span>
                        {formatSourceLocator(source, t)}
                      </span>
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </>
        ) : (
          <div className="flex items-center gap-2 text-sm text-slate-500 dark:text-slate-400">
            <SearchX size={16} className="shrink-0" aria-hidden="true" />
            {t('noMaterial')}
          </div>
        )}
      </div>
    </article>
  );
};

const formatSourceLocator = (
  source: QaSource,
  t: (key: string, options?: Record<string, unknown>) => string,
): string => {
  if (source.slide !== null) return t('source.slide', { n: source.slide });
  if (source.page !== null) return t('source.page', { n: source.page });
  if (source.section) return source.section;
  return t('source.document');
};
