import { useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import { AtSign, Edit3, FileText, Save, UserRound, X } from 'lucide-react';
import { Button } from '../../../components/ui/Button';
import { Input } from '../../../components/ui/Input';
import type { User } from '../../../types';

const buildSchema = (t: (key: string) => string) =>
  z.object({
    firstName: z.string().trim().min(1, t('validation.firstNameRequired')),
    lastName: z.string().trim().min(1, t('validation.lastNameRequired')),
    userName: z.string().trim().min(3, t('validation.userNameMin')),
    bio: z.string().max(500, t('validation.bioMax')).optional(),
  });

export type ProfileInfoFormData = z.infer<ReturnType<typeof buildSchema>>;

interface ProfileInfoFormProps {
  user: User;
  isLoading: boolean;
  onSubmit: (data: ProfileInfoFormData) => Promise<void>;
}

interface InfoItemProps {
  icon: ReactNode;
  label: string;
  value: ReactNode;
}

const InfoItem = ({ icon, label, value }: InfoItemProps) => {
  return (
    <div className="flex gap-3 rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-950/50">
      <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-violet-100 text-violet-700 dark:bg-violet-900/30 dark:text-violet-300">
        {icon}
      </div>

      <div className="min-w-0">
        <p className="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
          {label}
        </p>
        <div className="mt-1 break-words text-sm font-medium text-slate-900 dark:text-slate-100">
          {value}
        </div>
      </div>
    </div>
  );
};

export const ProfileInfoForm = ({
  user,
  isLoading,
  onSubmit,
}: ProfileInfoFormProps) => {
  const { t, i18n } = useTranslation('users');
  const [isEditing, setIsEditing] = useState(false);

  const hasMountedRef = useRef(false);
  const shouldRefreshErrorsOnLanguageChangeRef = useRef(false);

  const schema = useMemo(() => buildSchema(t), [t, i18n.language]);
  const resolver = useMemo(() => zodResolver(schema), [schema]);

  const {
    register,
    handleSubmit,
    reset,
    trigger,
    formState: { errors, isSubmitting, isSubmitted, touchedFields },
  } = useForm<ProfileInfoFormData>({
    resolver,
    mode: 'onTouched',
    reValidateMode: 'onChange',
    defaultValues: {
      firstName: user.firstName,
      lastName: user.lastName,
      userName: user.userName,
      bio: user.bio ?? '',
    },
  });

  const resetForm = () => {
    reset({
      firstName: user.firstName,
      lastName: user.lastName,
      userName: user.userName,
      bio: user.bio ?? '',
    });
  };

  useEffect(() => {
    resetForm();
  }, [reset, user]);

  useEffect(() => {
    const hasTouchedFields = Object.keys(touchedFields).length > 0;
    const hasVisibleErrors = Object.keys(errors).length > 0;

    shouldRefreshErrorsOnLanguageChangeRef.current =
      isSubmitted || hasTouchedFields || hasVisibleErrors;
  }, [isSubmitted, touchedFields, errors]);

  useEffect(() => {
    if (!hasMountedRef.current) {
      hasMountedRef.current = true;
      return;
    }

    if (shouldRefreshErrorsOnLanguageChangeRef.current) {
      void trigger(undefined, { shouldFocus: false });
    }
  }, [i18n.language, trigger]);

  const handleCancelEdit = () => {
    resetForm();
    setIsEditing(false);
  };

  const handleValidSubmit = async (data: ProfileInfoFormData) => {
    await onSubmit(data);
    setIsEditing(false);
  };

  const fullName = `${user.firstName} ${user.lastName}`.trim();

  return (
    <section className="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h2 className="text-lg font-semibold text-slate-900 dark:text-white">
            {t('profileInfo.title')}
          </h2>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            {t('profileInfo.description')}
          </p>
        </div>

        {!isEditing && (
          <Button
            type="button"
            variant="secondary"
            onClick={() => setIsEditing(true)}
          >
            <Edit3 size={16} />
            {t('profileInfo.edit')}
          </Button>
        )}
      </div>

      {!isEditing ? (
        <div className="grid gap-4">
          <InfoItem
            icon={<UserRound size={18} />}
            label={t('profileInfo.fullNameLabel')}
            value={fullName || '-'}
          />

          <InfoItem
            icon={<AtSign size={18} />}
            label={t('profileInfo.userNameLabel')}
            value={`@${user.userName}`}
          />

          <InfoItem
            icon={<FileText size={18} />}
            label={t('profileInfo.bioLabel')}
            value={
              user.bio?.trim() ? (
                user.bio
              ) : (
                <span className="font-normal text-slate-500 dark:text-slate-400">
                  {t('profileInfo.emptyBio')}
                </span>
              )
            }
          />
        </div>
      ) : (
        <form onSubmit={handleSubmit(handleValidSubmit)} noValidate>
          <div className="grid gap-4 md:grid-cols-2">
            <Input
              label={t('profileInfo.firstNameLabel')}
              {...register('firstName')}
              error={errors.firstName?.message}
            />

            <Input
              label={t('profileInfo.lastNameLabel')}
              {...register('lastName')}
              error={errors.lastName?.message}
            />
          </div>

          <Input
            label={t('profileInfo.userNameLabel')}
            {...register('userName')}
            error={errors.userName?.message}
          />

          <div className="mb-4 flex w-full flex-col gap-1 text-start">
            <label
              htmlFor="bio"
              className="text-sm font-medium text-slate-700 dark:text-slate-300"
            >
              {t('profileInfo.bioLabel')}
            </label>

            <textarea
              id="bio"
              rows={5}
              {...register('bio')}
              className={`resize-none rounded-lg border px-4 py-2.5 outline-none transition-all dark:bg-slate-950/50 dark:text-slate-100
                focus:border-violet-500 focus:ring-2 focus:ring-violet-500/50
                ${
                  errors.bio?.message
                    ? 'border-red-500/80 focus:border-red-500 focus:ring-red-500/50'
                    : 'border-slate-200 bg-slate-50 dark:border-slate-800'
                }`}
            />

            {errors.bio?.message && (
              <span className="text-xs font-medium text-red-500">
                {errors.bio.message}
              </span>
            )}
          </div>

          <div className="flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
            <Button
              type="button"
              variant="secondary"
              disabled={isSubmitting || isLoading}
              onClick={handleCancelEdit}
            >
              <X size={16} />
              {t('profileInfo.cancel')}
            </Button>

            <Button type="submit" isLoading={isSubmitting || isLoading}>
              <Save size={16} />
              {t('profileInfo.submit')}
            </Button>
          </div>
        </form>
      )}
    </section>
  );
};