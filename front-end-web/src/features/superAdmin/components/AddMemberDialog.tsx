import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Check, UserPlus } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useToast } from '../../../components/ui/ToastProvider';
import { searchUsers } from '../api/users';
import { useAddClassroomMember } from '../hooks/useMemberQueries';

interface AddMemberDialogProps {
  classroomId: string;
  isOpen: boolean;
  onClose: () => void;
  onAdded: (name: string) => void;
}

// Searchable picker over active students, backed by the user directory search.
const StudentPicker = ({
  selectedId,
  selectedLabel,
  onSelect,
  error,
}: {
  selectedId: string;
  selectedLabel: string;
  onSelect: (id: string, label: string) => void;
  error?: string;
}) => {
  const { t } = useTranslation('superAdmin');
  const [term, setTerm] = useState('');
  const [debounced, setDebounced] = useState('');
  const [open, setOpen] = useState(false);

  useEffect(() => {
    const id = window.setTimeout(() => setDebounced(term.trim()), 300);
    return () => window.clearTimeout(id);
  }, [term]);

  const { data, isFetching } = useQuery({
    queryKey: ['student-picker', debounced],
    queryFn: () =>
      searchUsers({ role: 'Student', status: 'Active', searchTerm: debounced, pageSize: 8 }),
    enabled: open,
  });

  return (
    <div className="mb-2">
      <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">
        {t('members.add.student')}
      </label>

      {selectedId && (
        <div className="mb-2 inline-flex items-center gap-2 rounded-md bg-violet-50 px-3 py-1.5 text-sm text-violet-700 dark:bg-violet-950/30 dark:text-violet-300">
          <Check size={14} />
          {selectedLabel}
        </div>
      )}

      <input
        type="text"
        value={term}
        onFocus={() => setOpen(true)}
        onChange={(e) => setTerm(e.target.value)}
        placeholder={t('members.add.studentPlaceholder')}
        className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2.5 text-sm text-slate-900 outline-none focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950/50 dark:text-slate-100"
      />

      {open && debounced.length > 0 && (
        <ul className="mt-1 max-h-52 overflow-auto rounded-lg border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
          {isFetching && (
            <li className="px-4 py-2 text-sm text-slate-400">{t('members.add.searching')}</li>
          )}
          {!isFetching && (data?.items.length ?? 0) === 0 && (
            <li className="px-4 py-2 text-sm text-slate-400">{t('members.add.noStudents')}</li>
          )}
          {data?.items.map((student) => (
            <li key={student.id}>
              <button
                type="button"
                onClick={() => {
                  onSelect(student.id, `${student.firstName} ${student.lastName}`);
                  setOpen(false);
                  setTerm('');
                }}
                className="flex w-full flex-col items-start px-4 py-2 text-left text-sm hover:bg-violet-50 dark:hover:bg-violet-950/20"
              >
                <span className="font-medium text-slate-800 dark:text-slate-200">
                  {student.firstName} {student.lastName}
                </span>
                <span className="text-xs text-slate-500">{student.email}</span>
              </button>
            </li>
          ))}
        </ul>
      )}

      {error && <span className="mt-1 block text-xs font-medium text-red-500">{error}</span>}
    </div>
  );
};

export const AddMemberDialog = ({ classroomId, isOpen, onClose, onAdded }: AddMemberDialogProps) => {
  const { t } = useTranslation('superAdmin');
  const { showToast } = useToast();

  const [studentId, setStudentId] = useState('');
  const [studentLabel, setStudentLabel] = useState('');
  const [error, setError] = useState('');
  const [serverError, setServerError] = useState('');

  const mutation = useAddClassroomMember(classroomId);

  useEffect(() => {
    if (isOpen) {
      setStudentId('');
      setStudentLabel('');
      setError('');
      setServerError('');
    }
  }, [isOpen]);

  if (!isOpen) return null;

  const submit = async () => {
    setServerError('');
    if (!studentId) {
      setError(t('members.add.validation.studentRequired'));
      return;
    }
    setError('');

    try {
      const result = await mutation.mutateAsync(studentId);
      if (result.changed) {
        showToast({
          type: 'success',
          title: t('members.add.successTitle'),
          message: t('members.add.success', { student: studentLabel }),
        });
        onAdded(studentLabel);
      } else {
        // 5ج: already a member — nothing changed.
        showToast({
          type: 'info',
          title: t('members.add.alreadyTitle'),
          message: t('members.add.already', { student: studentLabel }),
        });
      }
      onClose();
    } catch (err: any) {
      const status = err?.response?.status;
      if (status === 404) {
        setServerError(t('members.add.classroomNotFound')); // 5أ
      } else if (status === 400) {
        setServerError(err?.response?.data?.detail || t('members.add.invalidStudent')); // 5ب
      } else {
        setServerError(err?.response?.data?.detail || t('members.add.fallbackError'));
      }
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 shadow-xl dark:border-slate-800 dark:bg-slate-900">
        <div className="mb-4 flex items-start gap-3">
          <span className="rounded-lg bg-violet-100 p-2 text-violet-600 dark:bg-violet-950/40 dark:text-violet-400">
            <UserPlus size={20} />
          </span>
          <div>
            <h2 className="text-lg font-semibold text-slate-900 dark:text-white">
              {t('members.add.title')}
            </h2>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
              {t('members.add.description')}
            </p>
          </div>
        </div>

        {serverError && (
          <div className="mb-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-300">
            {serverError}
          </div>
        )}

        <StudentPicker
          selectedId={studentId}
          selectedLabel={studentLabel}
          onSelect={(id, label) => {
            setStudentId(id);
            setStudentLabel(label);
          }}
          error={error}
        />

        <div className="mt-5 flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={onClose}
            disabled={mutation.isPending}
            className="inline-flex items-center justify-center rounded-lg border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-100 disabled:opacity-50 dark:border-slate-800 dark:text-slate-200 dark:hover:bg-slate-900"
          >
            {t('common:buttons.cancel')}
          </button>
          <button
            type="button"
            onClick={submit}
            disabled={mutation.isPending}
            className="inline-flex items-center justify-center gap-2 rounded-lg bg-gradient-to-r from-violet-600 to-indigo-600 px-4 py-2.5 text-sm font-semibold text-white shadow-md hover:from-violet-700 hover:to-indigo-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {mutation.isPending ? t('members.add.submitting') : t('members.add.submit')}
          </button>
        </div>
      </div>
    </div>
  );
};
