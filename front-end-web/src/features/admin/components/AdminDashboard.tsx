import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ShieldAlert, ShieldCheck, UserCheck, UserX, Users, Clock } from 'lucide-react';
import { PageHeader } from '../../../components/ui/PageHeader';
import { Tabs } from '../../../components/ui/Tabs';
import { ConfirmationModal } from '../../../components/ui/ConfirmationModal';
import { Button } from '../../../components/ui/Button';
import { UsersTable } from './UsersTable';
import { 
  usePendingUsers, 
  useAllUsers, 
  useUpdateUserStatus, 
  useDeactivateUser, 
  useReactivateUser 
} from '../hooks/useAdminQueries';
import type { User } from '../../../types';
import type { UserStatusPayload } from '../types';

type ActionType = 'approve' | 'reject' | 'deactivate' | 'reactivate' | null;

export const AdminDashboard = () => {
  const { t } = useTranslation('admin');
  const[activeTab, setActiveTab] = useState('pending');
  
  // Modal State
  const[modal, setModal] = useState<{ isOpen: boolean; type: ActionType; user: User | null }>({
    isOpen: false,
    type: null,
    user: null,
  });

  // Queries
  const pendingQuery = usePendingUsers({ page: 1, pageSize: 50 });
  const allUsersQuery = useAllUsers({ page: 1, pageSize: 50 });

  // Mutations
  const updateStatusMutation = useUpdateUserStatus();
  const deactivateMutation = useDeactivateUser();
  const reactivateMutation = useReactivateUser();

  const isMutating = updateStatusMutation.isPending || deactivateMutation.isPending || reactivateMutation.isPending;

  const handleConfirmAction = async () => {
    if (!modal.user || !modal.type) return;

    try {
      if (modal.type === 'approve') {
        await updateStatusMutation.mutateAsync({ id: modal.user.id, status: 'Active' });
      } else if (modal.type === 'reject') {
        await updateStatusMutation.mutateAsync({ id: modal.user.id, status: 'Rejected' });
      } else if (modal.type === 'deactivate') {
        await deactivateMutation.mutateAsync(modal.user.id);
      } else if (modal.type === 'reactivate') {
        await reactivateMutation.mutateAsync(modal.user.id);
      }
      setModal({ isOpen: false, type: null, user: null });
    } catch (error) {
      console.error('Action failed:', error);
    }
  };

  const openModal = (type: ActionType, user: User) => {
    setModal({ isOpen: true, type, user });
  };

  const renderPendingActions = (user: User) => (
    <div className="flex justify-center gap-2">
      <Button 
        variant="ghost" 
        className="w-32 border border-red-200 bg-red-50 !text-red-600 hover:border-red-300 hover:!bg-red-100 dark:border-red-900/50 dark:bg-red-950/30 dark:!text-red-400 dark:hover:!bg-red-950/50" 
        onClick={() => openModal('reject', user)}
      >
        <UserX size={16} />
        {t('actions.reject')}
      </Button>
      <Button 
        variant="ghost" 
        className="w-32 border border-green-200 bg-green-50 !text-green-600 shadow-none hover:border-green-300 hover:!bg-green-100 dark:border-green-900/50 dark:bg-green-950/30 dark:!text-green-400 dark:hover:!bg-green-950/50" 
        onClick={() => openModal('approve', user)}
      >
        <UserCheck size={16} />
        {t('actions.approve')}
      </Button>
    </div>
  );

  const renderAllUsersActions = (user: User) => {
    const isActive = user.status === 'Active';
    const isPending = user.status === 'Pending';

    if (isPending) return null;

    return (
      <div className="flex justify-center gap-2">
        {isActive ? (
          <Button 
            variant="ghost" 
            className="w-36 border border-red-200 bg-red-50 !text-red-600 hover:border-red-300 hover:!bg-red-100 dark:border-red-900/50 dark:bg-red-950/30 dark:!text-red-400 dark:hover:!bg-red-950/50" 
            onClick={() => openModal('deactivate', user)}
          >
            <ShieldAlert size={16} />
            {t('actions.deactivate')}
          </Button>
        ) : (
          <Button 
            variant="ghost" 
            className="w-36 border border-green-200 bg-green-50 !text-green-600 hover:border-green-300 hover:!bg-green-100 dark:border-green-900/50 dark:bg-green-950/30 dark:!text-green-400 dark:hover:!bg-green-950/50" 
            onClick={() => openModal('reactivate', user)}
          >
            <ShieldCheck size={16} />
            {t('actions.reactivate')}
          </Button>
        )}
      </div>
    );
  };

  const tabs =[
    { id: 'pending', label: t('tabs.pending'), icon: <Clock size={18} /> },
    { id: 'all', label: t('tabs.all'), icon: <Users size={18} /> },
  ];

  return (
    <div className="mx-auto w-full max-w-6xl p-6">
      <PageHeader 
        title={t('management.title')} 
        description={t('management.description')} 
      />

      <div className="mb-6">
        <Tabs tabs={tabs} activeTab={activeTab} onChange={setActiveTab} />
      </div>

      {activeTab === 'pending' ? (
        <UsersTable 
          users={pendingQuery.data?.items ??[]} 
          isLoading={pendingQuery.isLoading} 
          isError={pendingQuery.isError}
          renderActions={renderPendingActions} 
        />
      ) : (
        <UsersTable 
          users={allUsersQuery.data?.items ??[]} 
          isLoading={allUsersQuery.isLoading} 
          isError={allUsersQuery.isError}
          renderActions={renderAllUsersActions} 
        />
      )}

      <ConfirmationModal
        isOpen={modal.isOpen}
        onClose={() => setModal({ isOpen: false, type: null, user: null })}
        onConfirm={handleConfirmAction}
        isLoading={isMutating}
        title={t(`modals.${modal.type}.title`)}
        description={t(`modals.${modal.type}.description`, { name: `${modal.user?.firstName} ${modal.user?.lastName}` })}
        confirmText={t(`modals.${modal.type}.confirm`)}
        variant={modal.type === 'approve' || modal.type === 'reactivate' ? 'success' : 'danger'}
      />
    </div>
  );
};