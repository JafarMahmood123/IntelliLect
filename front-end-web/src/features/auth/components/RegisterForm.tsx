import { useEffect, useMemo, useRef, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { register as registerUser } from '../api/auth';
import { useRegistrationRoles } from '../../roles/hooks/useRolesQueries';
import { Input } from '../../../components/ui/Input';
import { Button } from '../../../components/ui/Button';

const buildSchema = (t: (key: string) => string) =>
  z.object({
    firstName: z.string().trim().min(1, t('validation.firstNameRequired')),
    lastName: z.string().trim().min(1, t('validation.lastNameRequired')),
    userName: z.string().trim().min(3, t('validation.userNameMin')),
    email: z.string().trim().email(t('validation.invalidEmail')),
    password: z.string().min(6, t('validation.passwordMin')),
    roleId: z.string().min(1, t('validation.roleRequired')),
  });

type RegisterFormData = z.infer<ReturnType<typeof buildSchema>>;

export const RegisterForm = () => {
  const { t, i18n } = useTranslation('auth');
  const [serverError, setServerError] = useState('');
  const navigate = useNavigate();

  const hasMountedRef = useRef(false);
  const shouldRefreshErrorsOnLanguageChangeRef = useRef(false);

  const schema = useMemo(() => buildSchema(t), [t, i18n.language]);
  const resolver = useMemo(() => zodResolver(schema), [schema]);

  const {
    data: roles = [],
    isLoading: isLoadingRoles,
    isError: isRolesError,
  } = useRegistrationRoles();

  const {
    register,
    handleSubmit,
    trigger,
    formState: { errors, isSubmitting, isSubmitted, touchedFields },
  } = useForm<RegisterFormData>({
    resolver,
    mode: 'onTouched',
    reValidateMode: 'onChange',
    defaultValues: {
      firstName: '',
      lastName: '',
      userName: '',
      email: '',
      password: '',
      roleId: '',
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

  const onSubmit = async (data: RegisterFormData) => {
    setServerError('');

    try {
      await registerUser(data);
      alert(t('register.success'));
      navigate('/login');
    } catch (error: any) {
      setServerError(error.response?.data?.detail || t('register.fallbackError'));
    }
  };

  const isSubmitDisabled = isSubmitting || isLoadingRoles || isRolesError;

  return (
    <div className="mx-auto w-full max-w-md rounded-2xl border border-slate-100 bg-white p-8 shadow-xl shadow-slate-200/40 dark:border-slate-800 dark:bg-slate-900 dark:shadow-none">
      <h2 className="mb-6 text-center text-2xl font-bold text-gray-900 dark:text-white">
        {t('register.title')}
      </h2>

      {serverError && (
        <div className="mb-4 rounded-md bg-red-100 p-3 text-sm text-red-700">
          {serverError}
        </div>
      )}

      {isRolesError && (
        <div className="mb-4 rounded-md bg-red-100 p-3 text-sm text-red-700">
          {t('register.rolesLoadError')}
        </div>
      )}

      <form onSubmit={handleSubmit(onSubmit)} noValidate>
        <div className="grid grid-cols-2 gap-4">
          <Input
            label={t('register.firstNameLabel')}
            {...register('firstName')}
            error={errors.firstName?.message}
          />

          <Input
            label={t('register.lastNameLabel')}
            {...register('lastName')}
            error={errors.lastName?.message}
          />
        </div>

        <Input
          label={t('register.userNameLabel')}
          {...register('userName')}
          error={errors.userName?.message}
        />

        <Input
          label={t('register.emailLabel')}
          type="email"
          {...register('email')}
          error={errors.email?.message}
        />

        <Input
          label={t('register.passwordLabel')}
          type="password"
          {...register('password')}
          error={errors.password?.message}
        />

        <div className="mb-4 flex w-full flex-col gap-1 text-start">
          <label
            htmlFor="roleId"
            className="text-sm font-medium text-slate-700 dark:text-slate-300"
          >
            {t('register.roleLabel')}
          </label>

          <select
            id="roleId"
            {...register('roleId')}
            disabled={isLoadingRoles || isRolesError}
            className={`rounded-lg border px-4 py-2.5 outline-none transition-all dark:bg-slate-950/50 dark:text-slate-100
              focus:border-violet-500 focus:ring-2 focus:ring-violet-500/50
              ${
                errors.roleId?.message
                  ? 'border-red-500/80 focus:border-red-500 focus:ring-red-500/50'
                  : 'border-slate-200 bg-slate-50 dark:border-slate-800'
              }
              disabled:cursor-not-allowed disabled:opacity-60`}
          >
            <option value="">
              {isLoadingRoles
                ? t('register.rolesLoading')
                : t('register.rolePlaceholder')}
            </option>

            {roles.map((role) => (
              <option key={role.id} value={role.id}>
                {role.name}
              </option>
            ))}
          </select>

          {errors.roleId?.message && (
            <span className="text-xs font-medium text-red-500">
              {errors.roleId.message}
            </span>
          )}
        </div>

        <Button
          type="submit"
          isLoading={isSubmitting}
          fullWidth
          disabled={isSubmitDisabled}
        >
          {t('register.submit')}
        </Button>
      </form>

      <p className="mt-4 text-center text-sm text-gray-600 dark:text-gray-400">
        {t('register.hasAccount')}{' '}
        <Link to="/login" className="text-purple-600 hover:underline">
          {t('register.loginLink')}
        </Link>
      </p>
    </div>
  );
};