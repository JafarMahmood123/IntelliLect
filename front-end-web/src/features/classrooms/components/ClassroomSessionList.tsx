import { useState } from 'react';
import { Calendar, Video, Clock, Plus } from 'lucide-react';
import { Button } from '../../../components/ui/Button';
import { useClassroomSessions } from '../hooks/useClassroomQueries';
import { CreateSessionDrawer } from './CreateSessionDrawer'; 

interface ClassroomSessionListProps {
  classroomId: string;
  isTeacher: boolean;
}

export const ClassroomSessionList = ({ classroomId, isTeacher }: ClassroomSessionListProps) => {
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);
  const { data: sessions = [], isLoading } = useClassroomSessions(classroomId);

  if (isLoading) return <div className="p-8 text-center animate-pulse">Loading sessions...</div>;

  return (
    <div className="space-y-6">
      {isTeacher && (
        <div className="flex justify-end">
          <Button onClick={() => setIsDrawerOpen(true)}>
            <Plus size={18} />
            Schedule Session
          </Button>
        </div>
      )}

      {sessions.length === 0 ? (
        <div className="py-12 text-center">
          <Calendar className="mx-auto mb-4 text-slate-300" size={48} />
          <p className="text-slate-500">No sessions scheduled yet.</p>
        </div>
      ) : (
        <div className="grid gap-4">
          {sessions.map((session) => (
            <div key={session.id} className="flex items-center justify-between rounded-xl border border-slate-200 bg-slate-50/50 p-5 dark:border-slate-800 dark:bg-slate-900/50">
              <div className="flex items-start gap-4">
                <div className="flex h-12 w-12 items-center justify-center rounded-full bg-indigo-100 text-indigo-600 dark:bg-indigo-900/30 dark:text-indigo-400">
                  <Video size={24} />
                </div>
                <div>
                  <h4 className="font-bold text-slate-900 dark:text-white">{session.title}</h4>
                  <p className="text-sm text-slate-500 line-clamp-1">{session.description}</p>
                  <div className="mt-2 flex items-center gap-3 text-xs font-medium text-slate-600 dark:text-slate-400">
                    <span className="flex items-center gap-1">
                      <Clock size={14} />
                      {new Date(session.scheduledAt).toLocaleString()}
                    </span>
                  </div>
                </div>
              </div>
              <Button variant="secondary" className="px-6">
                Join
              </Button>
            </div>
          ))}
        </div>
      )}

      <CreateSessionDrawer 
        isOpen={isDrawerOpen} 
        onClose={() => setIsDrawerOpen(false)} 
        classroomId={classroomId}
      />
    </div>
  );
};