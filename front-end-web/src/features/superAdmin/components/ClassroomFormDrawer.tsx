import { useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { School, Check } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Drawer } from '../../../components/ui/Drawer';
import { Input } from '../../../components/ui/Input';
import { useToast } from '../../../components/ui/ToastProvider';
import { searchUsers } from '../api/users';
import { useCreateClassroom, useUpdateClassroom } from '../hooks/useClassroomQueries';
import type { ClassroomAdminItem } from '../types';

interface ClassroomFormDrawerProps {
  isOpen: boolean;
  onClose: () => void;
  // When provided the drawer edits that classroom; otherwise it creates a new one.
  classroom?: ClassroomAdminItem | null;
  onSaved: (name: string) => void;
}

// Searchable picker over active teachers, backed by the existing user directory search.
const TeacherPicker = ({
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
    queryKey: ['teacher-picker', debounced],
    queryFn: () =>
      searchUsers({ role: 'Teacher', status: 'Active', searchTerm: debounced, pageSize: 8 }),
    enabled: open,
  });

  return (
    <div className="mb-4">
      <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">
        {t('classrooms.form.teacher')}
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
        placeholder={t('classrooms.form.teacherPlaceholder')}
        className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2.5 text-sm text-slate-900 outline-none focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950/50 dark:text-slate-100"
      />

      {open && debounced.length > 0 && (
        <ul className="mt-1 max-h-52 overflow-auto rounded-lg border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
          {isFetching && (
            <li className="px-4 py-2 text-sm text-slate-400">{t('classrooms.form.searching')}</li>
          )}
          {!isFetching && (data?.items.length ?? 0) === 0 && (
            <li className="px-4 py-2 text-sm text-slate-400">{t('classrooms.form.noTeachers')}</li>
          )}
          {data?.items.map((teacher) => (
            <li key={teacher.id}>
              <button
                type="button"
                onClick={() => {
                  onSelect(teacher.id, `${teacher.firstName} ${teacher.lastName}`);
                  setOpen(false);
                  setTerm('');
                }}
                className="flex w-full flex-col items-start px-4 py-2 text-left text-sm hover:bg-violet-50 dark:hover:bg-violet-950/20"
              >
                <span className="font-medium text-slate-800 dark:text-slate-200">
                  {teacher.firstName} {teacher.lastName}
                </span>
                <span className="text-xs text-slate-500">{teacher.email}</span>
              </button>
            </li>
          ))}
        </ul>
      )}

      {error && <span className="mt-1 block text-xs font-medium text-red-500">{error}</span>}
    </div>
  );
};

export const ClassroomFormDrawer = ({
  isOpen,
  onClose,
  classroom,
  onSaved,
}: ClassroomFormDrawerProps) => {
  const { t } = useTranslation('superAdmin');
  const { showToast } = useToast();
  const isEdit = !!classroom;

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [teacherId, setTeacherId] = useState('');
  const [teacherLabel, setTeacherLabel] = useState('');
  const [errors, setErrors] = useState<{ name?: string; description?: string; teacher?: string }>({});
  const [serverError, setServerError] = useState('');

  const createMutation = useCreateClassroom();
  const updateMutation = useUpdateClassroom();
  const isPending = createMutation.isPending || updateMutation.isPending;

  useEffect(() => {
    if (isOpen) {
      setName(classroom?.name ?? '');
      setDescription(classroom?.description ?? '');
      setTeacherId(classroom?.teacherId ?? '');
      setTeacherLabel(classroom?.teacherName ?? '');
      setErrors({});
      setServerError('');
    }
  }, [isOpen, classroom]);

  const validate = () => {
    const next: typeof errors = {};
    if (name.trim().length === 0) next.name = t('classrooms.form.validation.nameRequired');
    if (name.trim().length > 100) next.name = t('classrooms.form.validation.nameTooLong');
    if (description.trim().length === 0)
      next.description = t('classrooms.form.validation.descriptionRequired');
    if (!isEdit && !teacherId) next.teacher = t('classrooms.form.validation.teacherRequired');
    setErrors(next);
    return Object.keys(next).length === 0;
  };

  const title = useMemo(
    () => (isEdit ? t('classrooms.form.editTitle') : t('classrooms.form.createTitle')),
    [isEdit, t],
  );

  const onSubmit = async () => {
    setServerError('');
    if (!validate()) return;

    try {
      if (isEdit && classroom) {
        await updateMutation.mutateAsync({
          id: classroom.id,
          data: { name: name.trim(), description: description.trim(), version: classroom.version },
        });
      } else {
        await createMutation.mutateAsync({
          teacherId,
          name: name.trim(),
          description: description.trim(),
        });
      }

      showToast({
        type: 'success',
        title: t('classrooms.form.successTitle'),
        message: isEdit ? t('classrooms.form.updated') : t('classrooms.form.created'),
      });
      onSaved(name.trim());
    } catch (error: any) {
      const status = error?.response?.status;
      // Alternate path 6أ: someone else changed the classroom in the meantime.
      if (status === 409) {
        setServerError(t('classrooms.form.conflict'));
        return;
      }
      setServerError(
        error?.response?.data?.detail ||
          error?.response?.data?.title ||
          t('classrooms.form.fallbackError'),
      );
    }
  };

  return (
    <Drawer
      isOpen={isOpen}
      onClose={onClose}
      title={title}
      description={t('classrooms.form.description')}
      icon={<School size={22} />}
      footer={
        <div className="flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={onClose}
            disabled={isPending}
            className="inline-flex items-center justify-center rounded-lg border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-800 dark:text-slate-200 dark:hover:bg-slate-900"
          >
            {t('common:buttons.cancel')}
          </button>
          <button
            type="submit"
            form="classroom-form"
            disabled={isPending}
            className="inline-flex items-center justify-center gap-2 rounded-lg bg-gradient-to-r from-violet-600 to-indigo-600 px-4 py-2.5 text-sm font-semibold text-white shadow-md hover:from-violet-700 hover:to-indigo-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isPending ? t('classrooms.form.submitting') : t('classrooms.form.submit')}
          </button>
        </div>
      }
    >
      <form
        id="classroom-form"
        onSubmit={(e) => {
          e.preventDefault();
          onSubmit();
        }}
        className="space-y-2"
      >
        {serverError && (
          <div className="mb-2 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-300">
            {serverError}
          </div>
        )}

        <Input
          label={t('classrooms.form.name')}
          value={name}
          onChange={(e) => setName(e.target.value)}
          maxLength={100}
          error={errors.name}
        />

        <div className="mb-4">
          <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">
            {t('classrooms.form.descriptionLabel')}
          </label>
          <textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            rows={4}
            className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2.5 text-sm text-slate-900 outline-none focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950/50 dark:text-slate-100"
          />
          {errors.description && (
            <span className="mt-1 block text-xs font-medium text-red-500">{errors.description}</span>
          )}
        </div>

        {isEdit ? (
          <div className="mb-4">
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">
              {t('classrooms.form.teacher')}
            </label>
            <div className="rounded-lg bg-slate-100 px-4 py-2.5 text-sm text-slate-600 dark:bg-slate-800 dark:text-slate-300">
              {teacherLabel || classroom?.teacherEmail || t('classrooms.form.teacherUnknown')}
            </div>
          </div>
        ) : (
          <TeacherPicker
            selectedId={teacherId}
            selectedLabel={teacherLabel}
            onSelect={(id, label) => {
              setTeacherId(id);
              setTeacherLabel(label);
            }}
            error={errors.teacher}
          />
        )}
      </form>
    </Drawer>
  );
};
