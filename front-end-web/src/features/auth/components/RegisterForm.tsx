import { useMemo, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { register as registerUser } from '../api/auth';
import { Input } from '../../../components/ui/Input';
import { Button } from '../../../components/ui/Button';

const buildSchema = (t: (key: string) => string) =>
  z.object({
    firstName: z.string().min(2, t('validation.firstNameRequired')),
    lastName: z.string().min(2, t('validation.lastNameRequired')),
    userName: z.string().min(3, t('validation.userNameMin')),
    email: z.string().email(t('validation.invalidEmail')),
    password: z.string().min(6, t('validation.passwordMin')),
    roleId: z.string().uuid(t('validation.roleIdInvalid')),
  });

type RegisterFormData = z.infer<ReturnType<typeof buildSchema>>;

export const RegisterForm = () => {
  const { t } = useTranslation('auth');
  const [serverError, setServerError] = useState('');
  const navigate = useNavigate();

  const schema = useMemo(() => buildSchema(t), [t]);

  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<RegisterFormData>({
    resolver: zodResolver(schema),
  });

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

      <form onSubmit={handleSubmit(onSubmit)}>
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

        <Input
          label={t('register.roleIdLabel')}
          placeholder={t('register.roleIdPlaceholder')}
          {...register('roleId')}
          error={errors.roleId?.message}
        />

        <Button type="submit" isLoading={isSubmitting} fullWidth>
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