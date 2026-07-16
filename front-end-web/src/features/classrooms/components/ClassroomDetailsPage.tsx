import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { FileText, Film, ScrollText, Video } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { PageHeader } from '../../../components/ui/PageHeader';
import { Tabs } from '../../../components/ui/Tabs';
import { useClassroomDetails } from '../hooks/useClassroomQueries';
import { useAuthStore } from '../../../store/useAuthStore';
import { ClassroomFileList } from './ClassroomFileList';
import { ClassroomSessionList } from './ClassroomSessionList';
import { RecordingsList } from '../../recordings';
import { SummariesList } from '../../summaries';

type ClassroomTab = 'files' | 'sessions' | 'recordings' | 'summaries';

export const ClassroomDetailsPage = () => {
  const { id } = useParams<{ id: string }>();
  const [activeTab, setActiveTab] = useState<ClassroomTab>('files');
  
  // Check if the current user is a teacher to show/hide management actions
  const { user } = useAuthStore();
  const isTeacher = user?.roleName === 'Teacher';
  const { t } = useTranslation('recordings');
  
  // Fetch classroom metadata (name, description, etc.)
  const { data: classroom, isLoading, isError } = useClassroomDetails(id!);

  if (isLoading) {
    return (
      <div className="flex h-64 items-center justify-center">
        <div className="text-slate-500 animate-pulse font-medium">
          Loading classroom details...
        </div>
      </div>
    );
  }

  if (isError || !classroom) {
    return (
      <div className="mx-auto max-w-6xl p-6">
        <div className="rounded-xl border border-red-200 bg-red-50 p-8 text-center text-red-600 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-400">
          <p className="font-bold text-lg">Failed to load classroom</p>
          <p className="text-sm mt-1">The classroom might have been deleted or you don't have access.</p>
        </div>
      </div>
    );
  }

  const tabs = [
    { id: 'files', label: 'Files & Materials', icon: <FileText size={18} /> },
    { id: 'sessions', label: 'Live Sessions', icon: <Video size={18} /> },
    { id: 'recordings', label: t('tab'), icon: <Film size={18} /> },
    { id: 'summaries', label: t('tab', { ns: 'summaries' }), icon: <ScrollText size={18} /> },
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
          <ClassroomFileList 
            classroomId={classroom.id} 
            isTeacher={isTeacher} 
          />
        )}
        
        {activeTab === 'sessions' && (
          <ClassroomSessionList
            classroomId={classroom.id}
            isTeacher={isTeacher}
          />
        )}

        {activeTab === 'recordings' && (
          <RecordingsList classroomId={classroom.id} />
        )}

        {activeTab === 'summaries' && (
          <SummariesList classroomId={classroom.id} />
        )}
      </div>
    </div>
  );
};