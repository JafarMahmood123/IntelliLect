import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { FileText, Video } from 'lucide-react';
import { PageHeader } from '../../../components/ui/PageHeader';
import { Tabs } from '../../../components/ui/Tabs';
import { useClassroomDetails } from '../hooks/useClassroomQueries';
import { useAuthStore } from '../../../store/useAuthStore';
import { ClassroomFileList } from './ClassroomFileList'; // Import our new component

type ClassroomTab = 'files' | 'sessions';

export const ClassroomDetailsPage = () => {
  const { id } = useParams<{ id: string }>();
  const [activeTab, setActiveTab] = useState<ClassroomTab>('files');
  
  const { user } = useAuthStore();
  const isTeacher = user?.roleName === 'Teacher';
  
  const { data: classroom, isLoading, isError } = useClassroomDetails(id!);

  if (isLoading) {
    return <div className="p-8 text-center text-slate-500">Loading classroom details...</div>;
  }

  if (isError || !classroom) {
    return <div className="p-8 text-center text-red-500">Failed to load classroom.</div>;
  }

  const tabs = [
    { id: 'files', label: 'Files & Materials', icon: <FileText size={18} /> },
    { id: 'sessions', label: 'Live Sessions', icon: <Video size={18} /> },
  ];

  return (
    <div className="mx-auto w-full max-w-6xl p-6">
      <PageHeader 
        title={classroom.name} 
        description={classroom.description} 
      />

      <div className="mb-6">
        <Tabs 
          tabs={tabs} 
          activeTab={activeTab} 
          onChange={(tabId) => setActiveTab(tabId as ClassroomTab)} 
        />
      </div>
      
      <div className="rounded-xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        {activeTab === 'files' && (
          <ClassroomFileList classroomId={classroom.id} isTeacher={isTeacher} />
        )}
        
        {activeTab === 'sessions' && (
          <p className="py-8 text-center text-slate-500 dark:text-slate-400">
            The Live Sessions scheduler will go here next!
          </p>
        )}
      </div>
    </div>
  );
};