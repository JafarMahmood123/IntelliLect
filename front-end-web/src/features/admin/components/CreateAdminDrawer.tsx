import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation } from '@tanstack/react-query';
import { ShieldPlus } from 'lucide-react';
import { z } from 'zod';
import { Input } from '../../../components/ui/Input';
import { Drawer } from '../../../components/ui/Drawer';
import { createAdmin } from '../api/superAdmin';
import type { CreateAdminRequest } from '../types';

const createAdminSchema = z
  .object({
    firstName: z.string().min(2, 'First name must be at least 2 characters'),
    lastName: z.string().min(2, 'Last name must be at least 2 characters'),
    userName: z.string().min(3, 'Username must be at least 3 characters'),
    email: z.string().email('Please enter a valid email address'),
    password: z.string().min(8, 'Password must be at least 8 characters'),
    confirmPassword: z.string().min(8, 'Please confirm the password'),
  })
  .refine((data) => data.password === data.confirmPassword, {
    path: ['confirmPassword'],
    message: 'Passwords do not match',
  });

type CreateAdminFormValues = z.infer<typeof createAdminSchema>;

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
  const [serverError, setServerError] = useState('');

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<CreateAdminFormValues>({
    resolver: zodResolver(createAdminSchema),
    defaultValues: {
      firstName: '',
      lastName: '',
      userName: '',
      email: '',
      password: '',
      confirmPassword: '',
    },
  });

  const createAdminMutation = useMutation({
    mutationFn: (data: CreateAdminRequest) => createAdmin(data),
  });

  useEffect(() => {
    if (!isOpen) {
      reset();
      setServerError('');
      createAdminMutation.reset();
    }
  }, [isOpen, reset, createAdminMutation]);

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

      await createAdminMutation.mutateAsync(payload);

      const fullName = `${values.firstName.trim()} ${values.lastName.trim()}`.trim();
      reset();
      onCreated(fullName);
    } catch (error: any) {
      setServerError(
        error?.response?.data?.detail ||
          error?.response?.data?.title ||
          'Failed to create admin. Please review the data and try again.'
      );
    }
  };

  return (
    <Drawer
      isOpen={isOpen}
      onClose={onClose}
      title="Create New Admin"
      description="Add a new administrator account. The new admin will be created as an active account automatically."
      icon={<ShieldPlus size={22} />}
      footer={
        <div className="flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={onClose}
            disabled={isSubmitting || createAdminMutation.isPending}
            className="inline-flex items-center justify-center rounded-lg border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-800 dark:text-slate-200 dark:hover:bg-slate-900 cursor-pointer"
          >
            Cancel
          </button>

          <button
            type="submit"
            form="create-admin-form"
            disabled={isSubmitting || createAdminMutation.isPending}
            className="inline-flex items-center justify-center gap-2 rounded-lg bg-gradient-to-r from-violet-600 to-indigo-600 px-4 py-2.5 text-sm font-semibold text-white shadow-md hover:from-violet-700 hover:to-indigo-700 disabled:cursor-not-allowed disabled:opacity-50 cursor-pointer"
          >
            {isSubmitting || createAdminMutation.isPending
              ? 'Creating Admin...'
              : 'Create Admin'}
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
              Identity
            </h3>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
              Basic information about the new administrator.
            </p>
          </div>

          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <Input
              label="First Name"
              placeholder="Enter first name"
              {...register('firstName')}
              error={errors.firstName?.message}
            />

            <Input
              label="Last Name"
              placeholder="Enter last name"
              {...register('lastName')}
              error={errors.lastName?.message}
            />
          </div>
        </section>

        <section>
          <div className="mb-4">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
              Account
            </h3>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
              Login details used to identify the administrator.
            </p>
          </div>

          <Input
            label="Username"
            placeholder="Enter username"
            {...register('userName')}
            error={errors.userName?.message}
          />

          <Input
            label="Email Address"
            type="email"
            placeholder="admin@example.com"
            {...register('email')}
            error={errors.email?.message}
          />
        </section>

        <section>
          <div className="mb-4">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
              Security
            </h3>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
              Set a temporary password for the admin. They should change it
              after first login.
            </p>
          </div>

          <Input
            label="Temporary Password"
            type="password"
            placeholder="Enter temporary password"
            {...register('password')}
            error={errors.password?.message}
          />

          <Input
            label="Confirm Password"
            type="password"
            placeholder="Re-enter the password"
            {...register('confirmPassword')}
            error={errors.confirmPassword?.message}
          />
        </section>
      </form>
    </Drawer>
  );
};