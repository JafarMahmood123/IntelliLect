import { useState } from 'react';
import { Presentation, Plus } from 'lucide-react';
import { useNavigate } from 'react-router-dom';
import { PageHeader } from '../../../components/ui/PageHeader';
import { Button } from '../../../components/ui/Button';
import { useTeacherClassrooms } from '../hooks/useClassroomQueries';
import { ClassroomCard } from './ClassroomCard';
import { CreateClassroomDrawer } from './CreateClassroomDrawer';

export const TeacherClassroomDashboard = () => {
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);
  const navigate = useNavigate();
  
  const { data: classrooms, isLoading, isError } = useTeacherClassrooms();

  // Boolean flag to simplify our conditional rendering
  const hasClassrooms = classrooms && classrooms.length > 0;

  const handleClassroomClick = (id: string) => {
    navigate(`/classrooms/${id}`);
  };

  return (
    <div className="mx-auto w-full max-w-6xl p-6">
      <PageHeader 
        title="My Classrooms" 
        description="Manage your active teaching spaces and students."
        action={
          /* Only render the top-right button if classrooms exist */
          hasClassrooms ? (
            <Button onClick={() => setIsDrawerOpen(true)}>
              <Plus size={18} />
              New Classroom
            </Button>
          ) : undefined
        }
      />

      {isLoading ? (
        <div className="grid gap-6 md:grid-cols-2 lg:grid-cols-3">
          {[1, 2, 3].map((n) => (
            <div key={n} className="h-48 animate-pulse rounded-2xl bg-slate-100 dark:bg-slate-800" />
          ))}
        </div>
      ) : isError ? (
        <div className="rounded-xl border border-red-200 bg-red-50 p-6 text-center text-sm text-red-600 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-400">
          Failed to load classrooms. Please try refreshing the page.
        </div>
      ) : !hasClassrooms ? (
        <div className="mt-8 flex flex-col items-center justify-center rounded-2xl border border-dashed border-slate-300 bg-slate-50 py-16 text-center dark:border-slate-800 dark:bg-slate-900/40">
          <div className="mb-4 flex h-16 w-16 items-center justify-center rounded-full bg-violet-100 text-violet-600 dark:bg-violet-900/30 dark:text-violet-400">
            <Presentation size={32} />
          </div>
          <h3 className="mb-2 text-lg font-bold text-slate-900 dark:text-white">No classrooms yet</h3>
          <p className="mb-6 max-w-md text-sm text-slate-500 dark:text-slate-400">
            You haven't created any classrooms yet. Get started by setting up your first teaching space to organize students, files, and live sessions.
          </p>
          <Button onClick={() => setIsDrawerOpen(true)}>
            <Plus size={18} />
            Create Your First Classroom
          </Button>
        </div>
      ) : (
        <div className="grid gap-6 md:grid-cols-2 lg:grid-cols-3">
          {classrooms.map((classroom) => (
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