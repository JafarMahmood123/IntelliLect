import { useMemo, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '../../../store/useAuthStore';
import { verifyTwoFactor } from '../api/auth';
import { Input } from '../../../components/ui/Input';
import { Button } from '../../../components/ui/Button';
import { getDefaultRoute } from '../../../utils/getDefaultRoute';

const buildSchema = (t: (key: string) => string) =>
  z.object({
    code: z
      .string()
      .regex(/^\d{6}$/, t('validation.twoFactorCodeLength')),
  });

type TwoFactorFormData = z.infer<ReturnType<typeof buildSchema>>;

interface TwoFactorFormProps {
  email: string;
  /** Return to the credentials step (e.g. after an expired code / too many attempts). */
  onBackToLogin: () => void;
}

export const TwoFactorForm = ({ email, onBackToLogin }: TwoFactorFormProps) => {
  const { t } = useTranslation('auth');
  const [serverError, setServerError] = useState('');
  const setAuth = useAuthStore((state) => state.setAuth);
  const navigate = useNavigate();

  const schema = useMemo(() => buildSchema(t), [t]);

  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<TwoFactorFormData>({
    resolver: zodResolver(schema),
    defaultValues: { code: '' },
  });

  const onSubmit = async (data: TwoFactorFormData) => {
    setServerError('');
    try {
      const result = await verifyTwoFactor({ email, code: data.code });
      setAuth(result.response, result.accessToken, result.refreshToken);
      navigate(getDefaultRoute(result.response), { replace: true });
    } catch (error: any) {
      setServerError(error.response?.data?.detail || t('twoFactor.fallbackError'));
    }
  };

  return (
    <div className="mx-auto w-full max-w-md rounded-2xl border border-slate-100 bg-white p-8 shadow-xl shadow-slate-200/40 dark:border-slate-800 dark:bg-slate-900 dark:shadow-none">
      <h2 className="mb-2 text-center text-2xl font-bold text-slate-900 dark:text-white">
        {t('twoFactor.title')}
      </h2>
      <p className="mb-6 text-center text-sm text-slate-600 dark:text-slate-400">
        {t('twoFactor.description', { email })}
      </p>

      {serverError && (
        <div className="mb-4 rounded-md bg-red-100 p-3 text-sm text-red-700">
          {serverError}
        </div>
      )}

      <form onSubmit={handleSubmit(onSubmit)}>
        <Input
          label={t('twoFactor.codeLabel')}
          placeholder="123456"
          inputMode="numeric"
          autoComplete="one-time-code"
          maxLength={6}
          autoFocus
          {...register('code')}
          error={errors.code?.message}
        />

        <Button type="submit" isLoading={isSubmitting} fullWidth className="mt-2">
          {t('twoFactor.submit')}
        </Button>
      </form>

      <div className="mt-6 text-center">
        <button
          type="button"
          onClick={onBackToLogin}
          className="text-sm font-medium text-slate-600 hover:text-violet-600 dark:text-slate-400 dark:hover:text-violet-400 transition-colors"
        >
          {t('twoFactor.backToLogin')}
        </button>
      </div>
    </div>
  );
};
