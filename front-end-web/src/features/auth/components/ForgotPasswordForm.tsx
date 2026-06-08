import { useMemo, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { forgotPassword } from '../api/auth';
import { Input } from '../../../components/ui/Input';
import { Button } from '../../../components/ui/Button';

const buildSchema = (t: (key: string) => string) =>
  z.object({
    email: z.string().email(t('validation.invalidEmail')),
  });

type ForgotPasswordData = z.infer<ReturnType<typeof buildSchema>>;

export const ForgotPasswordForm = () => {
  const { t } = useTranslation('auth');
  const[serverError, setServerError] = useState('');
  const [isSuccess, setIsSuccess] = useState(false);
  const navigate = useNavigate();

  const schema = useMemo(() => buildSchema(t), [t]);

  const {
    register,
    handleSubmit,
    getValues,
    formState: { errors, isSubmitting },
  } = useForm<ForgotPasswordData>({
    resolver: zodResolver(schema),
  });

  const onSubmit = async (data: ForgotPasswordData) => {
    setServerError('');
    try {
      await forgotPassword(data.email);
      setIsSuccess(true);
    } catch (error: any) {
      setServerError(error.response?.data?.detail || t('forgotPassword.fallbackError'));
    }
  };

  const handleProceedToReset = () => {
    navigate(`/reset-password?email=${encodeURIComponent(getValues('email'))}`);
  };

  if (isSuccess) {
    return (
      <div className="mx-auto w-full max-w-md rounded-2xl border border-slate-100 bg-white p-8 text-center shadow-xl shadow-slate-200/40 dark:border-slate-800 dark:bg-slate-900 dark:shadow-none">
        <div className="mb-4 inline-flex h-12 w-12 items-center justify-center rounded-full bg-green-100 text-green-600 dark:bg-green-900/30 dark:text-green-400">
          <svg className="h-6 w-6" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth="2">
            <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
          </svg>
        </div>
        <h2 className="mb-2 text-2xl font-bold text-slate-900 dark:text-white">Check Your Email</h2>
        <p className="mb-6 text-sm text-slate-600 dark:text-slate-400">
          {t('forgotPassword.success')}
        </p>
        <Button onClick={handleProceedToReset} fullWidth className="mb-3">
          {t('forgotPassword.proceedToReset')}
        </Button>
        <Link to="/login" className="text-sm font-medium text-violet-600 hover:text-violet-700 dark:text-violet-400">
          {t('forgotPassword.backToLogin')}
        </Link>
      </div>
    );
  }

  return (
    <div className="mx-auto w-full max-w-md rounded-2xl border border-slate-100 bg-white p-8 shadow-xl shadow-slate-200/40 dark:border-slate-800 dark:bg-slate-900 dark:shadow-none">
      <h2 className="mb-2 text-center text-2xl font-bold text-slate-900 dark:text-white">
        {t('forgotPassword.title')}
      </h2>
      <p className="mb-6 text-center text-sm text-slate-600 dark:text-slate-400">
        {t('forgotPassword.description')}
      </p>

      {serverError && (
        <div className="mb-4 rounded-md bg-red-100 p-3 text-sm text-red-700">
          {serverError}
        </div>
      )}

      <form onSubmit={handleSubmit(onSubmit)}>
        <Input
          label={t('forgotPassword.emailLabel')}
          type="email"
          {...register('email')}
          error={errors.email?.message}
        />

        <Button type="submit" isLoading={isSubmitting} fullWidth className="mt-2">
          {t('forgotPassword.submit')}
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