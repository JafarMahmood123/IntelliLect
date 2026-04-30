import { useState } from 'react';
import { Plus } from 'lucide-react';
import { PageHeader } from '../../../components/ui/PageHeader';
import { Button } from '../../../components/ui/Button';
import { useTeacherClassrooms } from '../hooks/useClassroomQueries';
import { ClassroomCard } from './ClassroomCard';
import { CreateClassroomDrawer } from './CreateClassroomDrawer';

export const TeacherClassroomDashboard = () => {
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);
  const { data: classrooms, isLoading, isError } = useTeacherClassrooms();

  const handleClassroomClick = (id: string) => {
    // We will implement navigation to details later
    console.log("Navigating to classroom:", id);
  };

  return (
    <div className="mx-auto w-full max-w-6xl p-6">
      <PageHeader 
        title="My Classrooms" 
        description="Manage your active teaching spaces and students."
        action={
          <Button onClick={() => setIsDrawerOpen(true)}>
            <Plus size={18} />
            New Classroom
          </Button>
        }
      />

      {isLoading ? (
        <div className="grid gap-6 md:grid-cols-2 lg:grid-cols-3">
          {[1, 2, 3].map((n) => (
            <div key={n} className="h-48 animate-pulse rounded-2xl bg-slate-100 dark:bg-slate-800" />
          ))}
        </div>
      ) : isError ? (
        <div className="text-center text-red-500">Failed to load classrooms.</div>
      ) : (
        <div className="grid gap-6 md:grid-cols-2 lg:grid-cols-3">
          {classrooms?.map((classroom) => (
            <ClassroomCard 
              key={classroom.id} 
              classroom={classroom} 
              onClick={handleClassroomClick} 
            />
          ))}
        </div>
      )}

      <CreateClassroomDrawer 
        isOpen={isDrawerOpen} 
        onClose={() => setIsDrawerOpen(false)} 
      />
    </div>
  );
};