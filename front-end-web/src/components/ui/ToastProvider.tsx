import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import { AlertTriangle, CheckCircle, Info, X, XCircle } from 'lucide-react';
import { useTranslation } from 'react-i18next';

type ToastType = 'success' | 'error' | 'warning' | 'info';

interface ToastOptions {
  type: ToastType;
  title: string;
  message?: string;
  durationMs?: number;
}

interface Toast extends ToastOptions {
  id: string;
}

interface ToastContextValue {
  showToast: (toast: ToastOptions) => string;
  closeToast: (id: string) => void;
  clearToasts: () => void;
}

interface ToastProviderProps {
  children: ReactNode;
}

interface ToastItemProps {
  toast: Toast;
  onClose: (id: string) => void;
}

const AUTO_DISMISS_MS = 5000;

const ToastContext = createContext<ToastContextValue | null>(null);

const createToastId = () => {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }

  return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
};

const toastIcons: Record<ToastType, ReactNode> = {
  success: <CheckCircle className="h-5 w-5 text-green-600 dark:text-green-400" />,
  error: <XCircle className="h-5 w-5 text-red-600 dark:text-red-400" />,
  warning: <AlertTriangle className="h-5 w-5 text-amber-600 dark:text-amber-400" />,
  info: <Info className="h-5 w-5 text-blue-600 dark:text-blue-400" />,
};

const iconBackgroundClasses: Record<ToastType, string> = {
  success: 'bg-green-100 dark:bg-green-900/30',
  error: 'bg-red-100 dark:bg-red-900/30',
  warning: 'bg-amber-100 dark:bg-amber-900/30',
  info: 'bg-blue-100 dark:bg-blue-900/30',
};

const borderClasses: Record<ToastType, string> = {
  success: 'border-green-200 dark:border-green-900/60',
  error: 'border-red-200 dark:border-red-900/60',
  warning: 'border-amber-200 dark:border-amber-900/60',
  info: 'border-blue-200 dark:border-blue-900/60',
};

const ToastItem = ({ toast, onClose }: ToastItemProps) => {
  const { t } = useTranslation('common');

  useEffect(() => {
    if (toast.durationMs === 0) return;

    const timeoutId = window.setTimeout(() => {
      onClose(toast.id);
    }, toast.durationMs ?? AUTO_DISMISS_MS);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [toast.durationMs, toast.id, onClose]);

  return (
    <div
      role={toast.type === 'error' ? 'alert' : 'status'}
      aria-live={toast.type === 'error' ? 'assertive' : 'polite'}
      className={`pointer-events-auto w-full overflow-hidden rounded-xl border bg-white shadow-xl shadow-slate-900/10 dark:bg-slate-900 dark:shadow-black/30 ${borderClasses[toast.type]}`}
    >
      <div className="flex gap-3 p-4">
        <div
          className={`mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-full ${iconBackgroundClasses[toast.type]}`}
        >
          {toastIcons[toast.type]}
        </div>

        <div className="min-w-0 flex-1">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">
            {toast.title}
          </p>

          {toast.message && (
            <p className="mt-1 text-sm leading-5 text-slate-600 dark:text-slate-400">
              {toast.message}
            </p>
          )}
        </div>

        <button
          type="button"
          onClick={() => onClose(toast.id)}
          aria-label={t('toast.close')}
          className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg text-slate-400 transition-colors hover:bg-slate-100 hover:text-slate-700 dark:hover:bg-slate-800 dark:hover:text-slate-200"
        >
          <X size={16} />
        </button>
      </div>
    </div>
  );
};

export const ToastProvider = ({ children }: ToastProviderProps) => {
  const [toasts, setToasts] = useState<Toast[]>([]);

  const closeToast = useCallback((id: string) => {
    setToasts((currentToasts) => currentToasts.filter((toast) => toast.id !== id));
  }, []);

  const clearToasts = useCallback(() => {
    setToasts([]);
  }, []);

  const showToast = useCallback((toast: ToastOptions) => {
    const id = createToastId();

    setToasts((currentToasts) => [
      ...currentToasts,
      {
        ...toast,
        id,
        durationMs: toast.durationMs ?? AUTO_DISMISS_MS,
      },
    ]);

    return id;
  }, []);

  const value = useMemo(
    () => ({
      showToast,
      closeToast,
      clearToasts,
    }),
    [showToast, closeToast, clearToasts],
  );

  return (
    <ToastContext.Provider value={value}>
      {children}

      {/* `end-*` rather than a direction conditional: the toast stack belongs on the
          trailing edge in both directions, and saying so once is what makes that true. */}
      <div className="fixed top-6 end-0 z-[200] flex w-full max-w-sm flex-col gap-3 px-4 sm:end-6 sm:px-0">
        {toasts.map((toast) => (
          <ToastItem key={toast.id} toast={toast} onClose={closeToast} />
        ))}
      </div>
    </ToastContext.Provider>
  );
};

export const useToast = () => {
  const context = useContext(ToastContext);

  if (!context) {
    throw new Error('useToast must be used inside ToastProvider.');
  }

  return context;
};