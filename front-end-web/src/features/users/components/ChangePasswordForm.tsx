import { useEffect, useMemo, useRef } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import { KeyRound } from 'lucide-react';
import { Button } from '../../../components/ui/Button';
import { Input } from '../../../components/ui/Input';

const buildSchema = (t: (key: string) => string) =>
  z
    .object({
      oldPassword: z.string().min(1, t('validation.oldPasswordRequired')),
      newPassword: z.string().min(6, t('validation.newPasswordMin')),
      confirmPassword: z.string().min(1, t('validation.confirmPasswordRequired')),
    })
    .refine((data) => data.newPassword === data.confirmPassword, {
      message: t('validation.passwordMismatch'),
      path: ['confirmPassword'],
    });

export type ChangePasswordFormData = z.infer<ReturnType<typeof buildSchema>>;

interface ChangePasswordFormProps {
  isLoading: boolean;
  onSubmit: (data: ChangePasswordFormData) => Promise<void>;
}

export const ChangePasswordForm = ({
  isLoading,
  onSubmit,
}: ChangePasswordFormProps) => {
  const { t, i18n } = useTranslation('users');

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
  } = useForm<ChangePasswordFormData>({
    resolver,
    mode: 'onTouched',
    reValidateMode: 'onChange',
    defaultValues: {
      oldPassword: '',
      newPassword: '',
      confirmPassword: '',
    },
  });

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

  const handleValidSubmit = async (data: ChangePasswordFormData) => {
    await onSubmit(data);

    reset({
      oldPassword: '',
      newPassword: '',
      confirmPassword: '',
    });
  };

  return (
    <section className="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="mb-6">
        <h2 className="text-lg font-semibold text-slate-900 dark:text-white">
          {t('changePassword.title')}
        </h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          {t('changePassword.description')}
        </p>
      </div>

      <form onSubmit={handleSubmit(handleValidSubmit)} noValidate>
        <Input
          label={t('changePassword.oldPasswordLabel')}
          type="password"
          autoComplete="current-password"
          {...register('oldPassword')}
          error={errors.oldPassword?.message}
        />

        <Input
          label={t('changePassword.newPasswordLabel')}
          type="password"
          autoComplete="new-password"
          {...register('newPassword')}
          error={errors.newPassword?.message}
        />

        <Input
          label={t('changePassword.confirmPasswordLabel')}
          type="password"
          autoComplete="new-password"
          {...register('confirmPassword')}
          error={errors.confirmPassword?.message}
        />

        <div className="flex justify-end">
          <Button type="submit" isLoading={isSubmitting || isLoading}>
            <KeyRound size={16} />
            {t('changePassword.submit')}
          </Button>
        </div>
      </form>
    </section>
  );
};