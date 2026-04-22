import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Link, useNavigate } from 'react-router-dom';
import { useAuthStore } from '../../../store/useAuthStore';
import { login } from '../api/auth';
import { Input } from '../../../components/ui/Input';
import { Button } from '../../../components/ui/Button';
import { getDefaultRoute } from '../../../utils/getDefaultRoute';

const schema = z.object({
  email: z.string().email('Invalid email address'),
  password: z.string().min(6, 'Password must be at least 6 characters'),
});

type LoginFormData = z.infer<typeof schema>;

export const LoginForm = () => {
  const [serverError, setServerError] = useState('');
  const setAuth = useAuthStore((state) => state.setAuth);
  const navigate = useNavigate();

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
      setServerError(
        error.response?.data?.detail || 'Login failed. Please try again.'
      );
    }
  };

  return (
    <div className="max-w-md w-full mx-auto p-8 bg-white dark:bg-slate-900 border border-slate-100 dark:border-slate-800 shadow-xl shadow-slate-200/40 dark:shadow-none rounded-2xl">
      <h2 className="text-2xl font-bold text-center mb-6 text-gray-900 dark:text-white">
        Welcome Back
      </h2>

      {serverError && (
        <div className="mb-4 p-3 bg-red-100 text-red-700 rounded-md text-sm">
          {serverError}
        </div>
      )}

      <form onSubmit={handleSubmit(onSubmit)}>
        <Input
          label="Email Address"
          type="email"
          {...register('email')}
          error={errors.email?.message}
        />
        <Input
          label="Password"
          type="password"
          {...register('password')}
          error={errors.password?.message}
        />
        <Button type="submit" isLoading={isSubmitting} fullWidth>
          Sign In
        </Button>
      </form>

      <p className="mt-4 text-center text-sm text-gray-600 dark:text-gray-400">
        Don't have an account?{' '}
        <Link to="/register" className="text-purple-600 hover:underline">
          Register here
        </Link>
      </p>
    </div>
  );
};