import { useEffect, useMemo, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { ShieldPlus } from 'lucide-react';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { Input } from '../../../components/ui/Input';
import { Drawer } from '../../../components/ui/Drawer';
import { useCreateAdmin } from '../hooks/useAdminQueries';
import type { CreateAdminRequest } from '../types';

const buildSchema = (t: (key: string) => string) =>
  z
    .object({
      firstName: z.string().min(2, t('drawer.validation.firstNameMin')),
      lastName: z.string().min(2, t('drawer.validation.lastNameMin')),
      userName: z.string().min(3, t('drawer.validation.userNameMin')),
      email: z.string().email(t('drawer.validation.invalidEmail')),
      password: z.string().min(8, t('drawer.validation.passwordMin')),
      confirmPassword: z
        .string()
        .min(8, t('drawer.validation.confirmPasswordMin')),
    })
    .refine((data) => data.password === data.confirmPassword, {
      path: ['confirmPassword'],
      message: t('drawer.validation.passwordMismatch'),
    });

type CreateAdminFormValues = z.infer<ReturnType<typeof buildSchema>>;

interface CreateAdminDrawerProps {
  isOpen: boolean;
  onClose: () => void;
  onCreated: (adminName: string) => void;
}

export const CreateAdminDrawer = ({
  isOpen,
  onClose,
  onCreated,
}: CreateAdminDrawerProps) => {
  const { t } = useTranslation('admin');
  const[serverError, setServerError] = useState('');

  const schema = useMemo(() => buildSchema(t), [t]);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<CreateAdminFormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      firstName: '',
      lastName: '',
      userName: '',
      email: '',
      password: '',
      confirmPassword: '',
    },
  });

  // Replaced direct useMutation with our custom Application Layer hook
  const {
    mutateAsync: submitCreateAdmin,
    isPending,
    reset: resetCreateAdminMutation,
  } = useCreateAdmin();

  useEffect(() => {
    if (!isOpen) {
      reset();
      setServerError('');
      resetCreateAdminMutation();
    }
  }, [isOpen, reset, resetCreateAdminMutation]);

  const onSubmit = async (values: CreateAdminFormValues) => {
    setServerError('');

    try {
      const payload: CreateAdminRequest = {
        firstName: values.firstName.trim(),
        lastName: values.lastName.trim(),
        userName: values.userName.trim(),
        email: values.email.trim(),
        password: values.password,
      };

      await submitCreateAdmin(payload);

      const fullName = `${values.firstName.trim()} ${values.lastName.trim()}`.trim();
      reset();
      onCreated(fullName);
    } catch (error: any) {
      setServerError(
        error?.response?.data?.detail ||
          error?.response?.data?.title ||
          t('drawer.fallbackError'),
      );
    }
  };

  return (
    <Drawer
      isOpen={isOpen}
      onClose={onClose}
      title={t('drawer.title')}
      description={t('drawer.description')}
      icon={<ShieldPlus size={22} />}
      footer={
        <div className="flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={onClose}
            disabled={isSubmitting || isPending}
            className="inline-flex items-center justify-center rounded-lg border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-800 dark:text-slate-200 dark:hover:bg-slate-900"
          >
            {t('common:buttons.cancel')}
          </button>

          <button
            type="submit"
            form="create-admin-form"
            disabled={isSubmitting || isPending}
            className="inline-flex items-center justify-center gap-2 rounded-lg bg-gradient-to-r from-violet-600 to-indigo-600 px-4 py-2.5 text-sm font-semibold text-white shadow-md hover:from-violet-700 hover:to-indigo-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isSubmitting || isPending
              ? t('drawer.submitting')
              : t('drawer.submit')}
          </button>
        </div>
      }
    >
      <form
        id="create-admin-form"
        onSubmit={handleSubmit(onSubmit)}
        className="space-y-8"
      >
        {serverError && (
          <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-300">
            {serverError}
          </div>
        )}

        <section>
          <div className="mb-4">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
              {t('drawer.sections.identity')}
            </h3>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
              {t('drawer.sectionDescriptions.identity')}
            </p>
          </div>

          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <Input
              label={t('drawer.fields.firstName.label')}
              placeholder={t('drawer.fields.firstName.placeholder')}
              {...register('firstName')}
              error={errors.firstName?.message}
            />

            <Input
              label={t('drawer.fields.lastName.label')}
              placeholder={t('drawer.fields.lastName.placeholder')}
              {...register('lastName')}
              error={errors.lastName?.message}
            />
          </div>
        </section>

        <section>
          <div className="mb-4">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
              {t('drawer.sections.account')}
            </h3>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
              {t('drawer.sectionDescriptions.account')}
            </p>
          </div>

          <Input
            label={t('drawer.fields.userName.label')}
            placeholder={t('drawer.fields.userName.placeholder')}
            {...register('userName')}
            error={errors.userName?.message}
          />

          <Input
            label={t('drawer.fields.email.label')}
            type="email"
            placeholder={t('drawer.fields.email.placeholder')}
            {...register('email')}
            error={errors.email?.message}
          />
        </section>

        <section>
          <div className="mb-4">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
              {t('drawer.sections.security')}
            </h3>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
              {t('drawer.sectionDescriptions.security')}
            </p>
          </div>

          <Input
            label={t('drawer.fields.password.label')}
            type="password"
            placeholder={t('drawer.fields.password.placeholder')}
            {...register('password')}
            error={errors.password?.message}
          />

          <Input
            label={t('drawer.fields.confirmPassword.label')}
            type="password"
            placeholder={t('drawer.fields.confirmPassword.placeholder')}
            {...register('confirmPassword')}
            error={errors.confirmPassword?.message}
          />
        </section>
      </form>
    </Drawer>
  );
};