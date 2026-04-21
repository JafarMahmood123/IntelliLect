import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Link, useNavigate } from 'react-router-dom';
import { register as registerUser } from '../api/auth';
import { Input } from '../../../components/ui/Input';
import { Button } from '../../../components/ui/Button';

const schema = z.object({
  firstName: z.string().min(2, 'First name is required'),
  lastName: z.string().min(2, 'Last name is required'),
  userName: z.string().min(3, 'Username must be at least 3 characters'),
  email: z.string().email('Invalid email address'),
  password: z.string().min(6, 'Password must be at least 6 characters'),
  roleId: z.string().uuid('Must be a valid GUID'),
});

type RegisterFormData = z.infer<typeof schema>;

export const RegisterForm = () => {
  const[serverError, setServerError] = useState('');
  const navigate = useNavigate();

  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<RegisterFormData>({
    resolver: zodResolver(schema),
  });

  const onSubmit = async (data: RegisterFormData) => {
    setServerError('');
    try {
      await registerUser(data);
      // Wait a moment so the user reads the success state (optional, or just redirect)
      alert("Registration request submitted! Please wait for Admin approval.");
      navigate('/login');
    } catch (error: any) {
      setServerError(error.response?.data?.detail || 'Registration failed. Please try again.');
    }
  };

  return (
    <div className="max-w-md w-full mx-auto p-8 bg-white dark:bg-gray-900 border dark:border-gray-800 shadow-lg rounded-lg">
      <h2 className="text-2xl font-bold text-center mb-6 text-gray-900 dark:text-white">Create an Account</h2>
      
      {serverError && (
        <div className="mb-4 p-3 bg-red-100 text-red-700 rounded-md text-sm">
          {serverError}
        </div>
      )}

      <form onSubmit={handleSubmit(onSubmit)}>
        <div className="grid grid-cols-2 gap-4">
          <Input label="First Name" {...register('firstName')} error={errors.firstName?.message} />
          <Input label="Last Name" {...register('lastName')} error={errors.lastName?.message} />
        </div>
        <Input label="Username" {...register('userName')} error={errors.userName?.message} />
        <Input label="Email Address" type="email" {...register('email')} error={errors.email?.message} />
        <Input label="Password" type="password" {...register('password')} error={errors.password?.message} />
        
        {/* Temporary: Hardcoded role input because there is no API to fetch roles */}
        <Input 
          label="Role ID (Get from DB for now)" 
          placeholder="e.g. 123e4567-e89b-12d3-a456-426614174000"
          {...register('roleId')} 
          error={errors.roleId?.message} 
        />

        <Button type="submit" isLoading={isSubmitting}>Register</Button>
      </form>

      <p className="mt-4 text-center text-sm text-gray-600 dark:text-gray-400">
        Already have an account? <Link to="/login" className="text-purple-600 hover:underline">Sign In</Link>
      </p>
    </div>
  );
};