import { useMemo, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { resetPassword } from '../api/auth';
import { Input } from '../../../components/ui/Input';
import { Button } from '../../../components/ui/Button';

const buildSchema = (t: (key: string) => string) =>
  z.object({
    email: z.string().email(t('validation.invalidEmail')),
    token: z.string().length(6, t('validation.tokenLength')),
    newPassword: z.string().min(6, t('validation.passwordMin')),
    confirmPassword: z.string()
  }).refine((data) => data.newPassword === data.confirmPassword, {
    message: t('validation.passwordMismatch'),
    path: ['confirmPassword']
  });

type ResetPasswordData = z.infer<ReturnType<typeof buildSchema>>;

export const ResetPasswordForm = () => {
  const { t } = useTranslation('auth');
  const [searchParams] = useSearchParams();
  const[serverError, setServerError] = useState('');
  const navigate = useNavigate();

  const defaultEmail = searchParams.get('email') || '';
  const schema = useMemo(() => buildSchema(t), [t]);

  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<ResetPasswordData>({
    resolver: zodResolver(schema),
    defaultValues: {
      email: defaultEmail,
      token: '',
      newPassword: '',
      confirmPassword: ''
    }
  });

  const onSubmit = async (data: ResetPasswordData) => {
    setServerError('');
    try {
      await resetPassword({
        email: data.email,
        token: data.token,
        newPassword: data.newPassword
      });
      alert(t('resetPassword.success'));
      navigate('/login');
    } catch (error: any) {
      setServerError(error.response?.data?.detail || t('resetPassword.fallbackError'));
    }
  };

  return (
    <div className="mx-auto w-full max-w-md rounded-2xl border border-slate-100 bg-white p-8 shadow-xl shadow-slate-200/40 dark:border-slate-800 dark:bg-slate-900 dark:shadow-none">
      <h2 className="mb-2 text-center text-2xl font-bold text-slate-900 dark:text-white">
        {t('resetPassword.title')}
      </h2>
      <p className="mb-6 text-center text-sm text-slate-600 dark:text-slate-400">
        {t('resetPassword.description')}
      </p>

      {serverError && (
        <div className="mb-4 rounded-md bg-red-100 p-3 text-sm text-red-700">
          {serverError}
        </div>
      )}

      <form onSubmit={handleSubmit(onSubmit)}>
        <Input
          label={t('resetPassword.emailLabel')}
          type="email"
          readOnly={!!defaultEmail}
          className={defaultEmail ? 'bg-slate-100 dark:bg-slate-800 text-slate-500' : ''}
          {...register('email')}
          error={errors.email?.message}
        />

        <Input
          label={t('resetPassword.tokenLabel')}
          placeholder="123456"
          maxLength={6}
          {...register('token')}
          error={errors.token?.message}
        />

        <Input
          label={t('resetPassword.newPasswordLabel')}
          type="password"
          {...register('newPassword')}
          error={errors.newPassword?.message}
        />

        <Input
          label={t('resetPassword.confirmPasswordLabel')}
          type="password"
          {...register('confirmPassword')}
          error={errors.confirmPassword?.message}
        />

        <Button type="submit" isLoading={isSubmitting} fullWidth className="mt-2">
          {t('resetPassword.submit')}
        </Button>
      </form>

      <div className="mt-6 text-center">
        <Link to="/login" className="text-sm font-medium text-slate-600 hover:text-violet-600 dark:text-slate-400 dark:hover:text-violet-400 transition-colors">
          {t('forgotPassword.backToLogin')}
        </Link>
      </div>
    </div>
  );
};