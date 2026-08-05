interface BulkFailure {
  userId: string;
  error: string | null;
}

interface BulkFailurePanelProps {
  title: string;
  failures: ReadonlyArray<BulkFailure>;
  /** Turns an id into something the admin recognises; fall back to the id when the row is gone. */
  resolveName: (userId: string) => string;
}

/**
 * The per-account reasons a bulk action did not apply, kept on screen rather than in a toast that
 * vanishes — a partial failure is something the admin has to act on, not just be told about.
 *
 * Shared by the admin dashboard and the super-admin directory: the two surfaces call the same
 * endpoint and must report its partial results the same way.
 */
export const BulkFailurePanel = ({ title, failures, resolveName }: BulkFailurePanelProps) => {
  if (failures.length === 0) return null;

  return (
    <div className="mb-3 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 dark:border-amber-900/50 dark:bg-amber-950/30">
      <p className="mb-2 text-sm font-medium text-amber-900 dark:text-amber-200">{title}</p>
      <ul className="space-y-1 text-sm text-amber-800 dark:text-amber-300">
        {failures.map((failure) => (
          <li key={failure.userId}>
            <span className="font-medium">{resolveName(failure.userId)}</span> — {failure.error}
          </li>
        ))}
      </ul>
    </div>
  );
};
