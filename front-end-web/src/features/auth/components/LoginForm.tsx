import { useMemo, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '../../../store/useAuthStore';
import { login } from '../api/auth';
import { Input } from '../../../components/ui/Input';
import { Button } from '../../../components/ui/Button';
import { getDefaultRoute } from '../../../utils/getDefaultRoute';

const buildSchema = (t: (key: string) => string) =>
  z.object({
    email: z.string().email(t('validation.invalidEmail')),
    password: z.string().min(6, t('validation.passwordMin')),
  });

type LoginFormData = z.infer<ReturnType<typeof buildSchema>>;

export const LoginForm = () => {
  const { t } = useTranslation('auth');
  const [serverError, setServerError] = useState('');
  const setAuth = useAuthStore((state) => state.setAuth);
  const navigate = useNavigate();

  const schema = useMemo(() => buildSchema(t), [t]);

  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<LoginFormData>({
    resolver: zodResolver(schema),
  });

  const onSubmit = async (data: LoginFormData) => {
    setServerError('');

    try {
      const result = await login(data);
      setAuth(result.response, result.accessToken, result.refreshToken);
      navigate(getDefaultRoute(result.response), { replace: true });
    } catch (error: any) {
      setServerError(error.response?.data?.detail || t('login.fallbackError'));
    }
  };

  return (
    <div className="mx-auto w-full max-w-md rounded-2xl border border-slate-100 bg-white p-8 shadow-xl shadow-slate-200/40 dark:border-slate-800 dark:bg-slate-900 dark:shadow-none">
      <h2 className="mb-6 text-center text-2xl font-bold text-gray-900 dark:text-white">
        {t('login.title')}
      </h2>

      {serverError && (
        <div className="mb-4 rounded-md bg-red-100 p-3 text-sm text-red-700">
          {serverError}
        </div>
      )}

      <form onSubmit={handleSubmit(onSubmit)}>
        <Input
          label={t('login.emailLabel')}
          type="email"
          {...register('email')}
          error={errors.email?.message}
        />

        <div className="relative">
          <div className="absolute right-0 top-0 flex h-6 items-center">
            <Link 
              to="/forgot-password" 
              tabIndex={-1}
              className="text-xs font-medium text-violet-600 hover:text-violet-700 dark:text-violet-400 hover:underline"
            >
              {t('forgotPassword.title')} {/* Uses the title we created earlier */}
            </Link>
          </div>
          <Input
            label={t('login.passwordLabel')}
            type="password"
            {...register('password')}
            error={errors.password?.message}
          />
        </div>

        <Button type="submit" isLoading={isSubmitting} fullWidth>
          {t('login.submit')}
        </Button>
      </form>

      <p className="mt-4 text-center text-sm text-gray-600 dark:text-gray-400">
        {t('login.noAccount')}{' '}
        <Link to="/register" className="text-purple-600 hover:underline">
          {t('login.registerLink')}
        </Link>
      </p>
    </div>
  );
};