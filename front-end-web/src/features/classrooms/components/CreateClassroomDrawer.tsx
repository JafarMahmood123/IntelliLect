import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Presentation } from 'lucide-react';
import { Drawer } from '../../../components/ui/Drawer';
import { Input } from '../../../components/ui/Input';
import { Button } from '../../../components/ui/Button';
import { useCreateClassroom } from '../hooks/useClassroomQueries';

const classroomSchema = z.object({
  name: z.string().min(3, 'Name must be at least 3 characters'),
  description: z.string().min(10, 'Description must be at least 10 characters'),
});

type ClassroomFormValues = z.infer<typeof classroomSchema>;

interface CreateClassroomDrawerProps {
  isOpen: boolean;
  onClose: () => void;
}

export const CreateClassroomDrawer = ({ isOpen, onClose }: CreateClassroomDrawerProps) => {
  const { mutateAsync: create, isPending } = useCreateClassroom();
  
  const { register, handleSubmit, reset, formState: { errors } } = useForm<ClassroomFormValues>({
    resolver: zodResolver(classroomSchema),
  });

  const onSubmit = async (data: ClassroomFormValues) => {
    try {
      await create(data);
      reset();
      onClose();
    } catch (error) {
      console.error(error);
    }
  };

  return (
    <Drawer
      isOpen={isOpen}
      onClose={onClose}
      title="Create Classroom"
      description="Set up a new space for your students and learning materials."
      icon={<Presentation size={22} />}
    >
      <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
        <Input 
          label="Classroom Name" 
          placeholder="e.g. Advanced Mathematics" 
          {...register('name')} 
          error={errors.name?.message} 
        />
        <div className="flex flex-col gap-1">
          <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Description</label>
          <textarea 
            {...register('description')}
            className="rounded-lg border border-slate-200 bg-slate-50 p-2.5 text-sm outline-none focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950/50"
            rows={4}
          />
          {errors.description && <span className="text-xs text-red-500">{errors.description.message}</span>}
        </div>
        <Button type="submit" fullWidth isLoading={isPending} className="mt-4">
          Create Classroom
        </Button>
      </form>
    </Drawer>
  );
};