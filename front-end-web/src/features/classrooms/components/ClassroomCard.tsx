import { Users, FileText, Calendar } from 'lucide-react';
import type { Classroom } from '../types';

interface ClassroomCardProps {
  classroom: Classroom;
  onClick: (id: string) => void;
}

export const ClassroomCard = ({ classroom, onClick }: ClassroomCardProps) => {
  const formattedDate = new Date(classroom.createdAtUtc).toLocaleDateString();

  return (
    <div 
      onClick={() => onClick(classroom.id)}
      className="group cursor-pointer rounded-2xl border border-slate-200 bg-white p-6 shadow-sm transition-all hover:border-violet-300 hover:shadow-md dark:border-slate-800 dark:bg-slate-900"
    >
      <h3 className="text-xl font-bold text-slate-900 group-hover:text-violet-600 dark:text-white dark:group-hover:text-violet-400">
        {classroom.name}
      </h3>
      <p className="mt-2 line-clamp-2 text-sm text-slate-500 dark:text-slate-400">
        {classroom.description}
      </p>

      <div className="mt-6 flex items-center gap-4 border-t border-slate-100 pt-4 dark:border-slate-800">
        <div className="flex items-center gap-1.5 text-slate-600 dark:text-slate-400">
          <Users size={16} />
          <span className="text-xs font-medium">{classroom.studentCount} Students</span>
        </div>
        <div className="flex items-center gap-1.5 text-slate-600 dark:text-slate-400">
          <FileText size={16} />
          <span className="text-xs font-medium">{classroom.fileCount} Files</span>
        </div>
        <div className="ml-auto flex items-center gap-1.5 text-slate-400">
          <Calendar size={14} />
          <span className="text-[10px] uppercase tracking-wider">{formattedDate}</span>
        </div>
      </div>
    </div>
  );
};