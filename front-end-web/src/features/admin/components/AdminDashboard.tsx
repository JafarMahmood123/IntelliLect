import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Clock,
  ShieldAlert,
  ShieldCheck,
  UserCheck,
  UserX,
  Users,
} from 'lucide-react';
import { PageHeader } from '../../../components/ui/PageHeader';
import { Tabs } from '../../../components/ui/Tabs';
import { ConfirmationModal } from '../../../components/ui/ConfirmationModal';
import { Button } from '../../../components/ui/Button';
import { Pagination } from '../../../components/ui/Pagination';
import { useToast } from '../../../components/ui/ToastProvider';
import { useRegistrationRoles } from '../../roles/hooks/useRolesQueries';
import { AdminRoleFilter } from './AdminRoleFilter';
import { UserDetailsDrawer } from './UserDetailsDrawer';
import { UsersTable } from './UsersTable';
import {
  usePendingUsers,
  useAllUsers,
  useUpdateUserStatus,
  useDeactivateUser,
  useReactivateUser,
} from '../hooks/useAdminQueries';
import { getApiErrorMessage } from '../../../utils/getApiErrorMessage';
import type { User } from '../../../types';

type ActionType = 'approve' | 'reject' | 'deactivate' | 'reactivate' | null;
type ConfirmableActionType = Exclude<ActionType, null>;
type AdminTab = 'pending' | 'all';

const FIXED_PAGE_SIZE = 10;

const successFeedbackByAction: Record<
  ConfirmableActionType,
  {
    titleKey: string;
    messageKey: string;
  }
> = {
  approve: {
    titleKey: 'feedback.approveSuccessTitle',
    messageKey: 'feedback.approveSuccessMessage',
  },
  reject: {
    titleKey: 'feedback.rejectSuccessTitle',
    messageKey: 'feedback.rejectSuccessMessage',
  },
  deactivate: {
    titleKey: 'feedback.deactivateSuccessTitle',
    messageKey: 'feedback.deactivateSuccessMessage',
  },
  reactivate: {
    titleKey: 'feedback.reactivateSuccessTitle',
    messageKey: 'feedback.reactivateSuccessMessage',
  },
};

export const AdminDashboard = () => {
  const { t } = useTranslation('admin');
  const { showToast } = useToast();

  const [activeTab, setActiveTab] = useState<AdminTab>('pending');
  const [selectedRoleId, setSelectedRoleId] = useState('');

  const [pendingPage, setPendingPage] = useState(1);
  const [allUsersPage, setAllUsersPage] = useState(1);

  const [detailsUser, setDetailsUser] = useState<User | null>(null);
  const [isDetailsDrawerOpen, setIsDetailsDrawerOpen] = useState(false);

  const [modal, setModal] = useState<{ isOpen: boolean; type: ActionType; user: User | null }>({
    isOpen: false,
    type: null,
    user: null,
  });

  const {
    data: roles = [],
    isLoading: isLoadingRoles,
    isError: isRolesError,
  } = useRegistrationRoles();

  const selectedRoleFilter = selectedRoleId || undefined;

  const pendingQuery = usePendingUsers({
    page: pendingPage,
    pageSize: FIXED_PAGE_SIZE,
    roleId: selectedRoleFilter,
  });

  const allUsersQuery = useAllUsers({
    page: allUsersPage,
    pageSize: FIXED_PAGE_SIZE,
    roleId: selectedRoleFilter,
  });

  const updateStatusMutation = useUpdateUserStatus();
  const deactivateMutation = useDeactivateUser();
  const reactivateMutation = useReactivateUser();

  const isMutating =
    updateStatusMutation.isPending ||
    deactivateMutation.isPending ||
    reactivateMutation.isPending;

  useEffect(() => {
    const totalPages = pendingQuery.data?.totalPages;

    if (totalPages && pendingPage > totalPages) {
      setPendingPage(totalPages);
    }
  }, [pendingPage, pendingQuery.data?.totalPages]);

  useEffect(() => {
    const totalPages = allUsersQuery.data?.totalPages;

    if (totalPages && allUsersPage > totalPages) {
      setAllUsersPage(totalPages);
    }
  }, [allUsersPage, allUsersQuery.data?.totalPages]);

  const handleRoleChange = (roleId: string) => {
    setSelectedRoleId(roleId);
    setPendingPage(1);
    setAllUsersPage(1);
  };

  const handleConfirmAction = async () => {
    if (!modal.user || !modal.type) return;

    const currentUser = modal.user;
    const currentAction = modal.type;
    const userFullName = `${currentUser.firstName} ${currentUser.lastName}`.trim();

    try {
      if (currentAction === 'approve') {
        await updateStatusMutation.mutateAsync({ id: currentUser.id, status: 'Active' });
      } else if (currentAction === 'reject') {
        await updateStatusMutation.mutateAsync({ id: currentUser.id, status: 'Rejected' });
      } else if (currentAction === 'deactivate') {
        await deactivateMutation.mutateAsync(currentUser.id);
      } else if (currentAction === 'reactivate') {
        await reactivateMutation.mutateAsync(currentUser.id);
      }

      const feedback = successFeedbackByAction[currentAction];

      showToast({
        type: 'success',
        title: t(feedback.titleKey),
        message: t(feedback.messageKey, { name: userFullName }),
      });

      setModal({ isOpen: false, type: null, user: null });
    } catch (error) {
      showToast({
        type: 'error',
        title: t('feedback.actionFailedTitle'),
        message: getApiErrorMessage(error, t('feedback.fallbackError')),
      });
    }
  };

  const openDetailsDrawer = (user: User) => {
    setDetailsUser(user);
    setIsDetailsDrawerOpen(true);
  };

  const closeDetailsDrawer = () => {
    setIsDetailsDrawerOpen(false);
  };

  const openActionModal = (type: ActionType, user: User) => {
    setIsDetailsDrawerOpen(false);
    setModal({ isOpen: true, type, user });
  };

  const handleTabChange = (tabId: string) => {
    setActiveTab(tabId as AdminTab);
  };

  const renderPendingActions = (user: User) => (
    <div className="flex flex-wrap justify-center gap-2">
      <Button
        variant="ghost"
        className="w-28 border border-red-200 bg-red-50 !text-red-600 hover:border-red-300 hover:!bg-red-100 dark:border-red-900/50 dark:bg-red-950/30 dark:!text-red-400 dark:hover:!bg-red-950/50"
        onClick={() => openActionModal('reject', user)}
      >
        <UserX size={16} />
        {t('actions.reject')}
      </Button>

      <Button
        variant="ghost"
        className="w-28 border border-green-200 bg-green-50 !text-green-600 shadow-none hover:border-green-300 hover:!bg-green-100 dark:border-green-900/50 dark:bg-green-950/30 dark:!text-green-400 dark:hover:!bg-green-950/50"
        onClick={() => openActionModal('approve', user)}
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
      <div className="flex flex-wrap justify-center gap-2">
        {isActive ? (
          <Button
            variant="ghost"
            className="w-36 border border-red-200 bg-red-50 !text-red-600 hover:border-red-300 hover:!bg-red-100 dark:border-red-900/50 dark:bg-red-950/30 dark:!text-red-400 dark:hover:!bg-red-950/50"
            onClick={() => openActionModal('deactivate', user)}
          >
            <ShieldAlert size={16} />
            {t('actions.deactivate')}
          </Button>
        ) : (
          <Button
            variant="ghost"
            className="w-36 border border-green-200 bg-green-50 !text-green-600 hover:border-green-300 hover:!bg-green-100 dark:border-green-900/50 dark:bg-green-950/30 dark:!text-green-400 dark:hover:!bg-green-950/50"
            onClick={() => openActionModal('reactivate', user)}
          >
            <ShieldCheck size={16} />
            {t('actions.reactivate')}
          </Button>
        )}
      </div>
    );
  };

  const tabs = [
    { id: 'pending', label: t('tabs.pending'), icon: <Clock size={18} /> },
    { id: 'all', label: t('tabs.all'), icon: <Users size={18} /> },
  ];

  const filterDescription =
    activeTab === 'pending'
      ? t('filters.descriptionPending')
      : t('filters.descriptionAll');

  const modalUserName = modal.user
    ? `${modal.user.firstName} ${modal.user.lastName}`
    : '';

  const modalTitle = modal.type ? t(`modals.${modal.type}.title`) : '';
  const modalDescription = modal.type
    ? t(`modals.${modal.type}.description`, { name: modalUserName })
    : '';
  const modalConfirmText = modal.type ? t(`modals.${modal.type}.confirm`) : '';

  return (
    <div className="mx-auto w-full max-w-6xl p-6">
      <PageHeader
        title={t('management.title')}
        description={t('management.description')}
      />

      <AdminRoleFilter
        roles={roles}
        selectedRoleId={selectedRoleId}
        description={filterDescription}
        isLoading={isLoadingRoles}
        isError={isRolesError}
        onChange={handleRoleChange}
      />

      <div className="mb-6">
        <Tabs tabs={tabs} activeTab={activeTab} onChange={handleTabChange} />
      </div>

      {activeTab === 'pending' ? (
        <>
          <UsersTable
            users={pendingQuery.data?.items ?? []}
            isLoading={pendingQuery.isLoading}
            isError={pendingQuery.isError}
            renderActions={renderPendingActions}
            onUserClick={openDetailsDrawer}
          />

          {pendingQuery.data && (
            <Pagination
              pageNumber={pendingQuery.data.pageNumber}
              pageSize={pendingQuery.data.pageSize}
              totalCount={pendingQuery.data.totalCount}
              totalPages={pendingQuery.data.totalPages}
              hasPreviousPage={pendingQuery.data.hasPreviousPage}
              hasNextPage={pendingQuery.data.hasNextPage}
              onPageChange={setPendingPage}
            />
          )}
        </>
      ) : (
        <>
          <UsersTable
            users={allUsersQuery.data?.items ?? []}
            isLoading={allUsersQuery.isLoading}
            isError={allUsersQuery.isError}
            renderActions={renderAllUsersActions}
            onUserClick={openDetailsDrawer}
          />

          {allUsersQuery.data && (
            <Pagination
              pageNumber={allUsersQuery.data.pageNumber}
              pageSize={allUsersQuery.data.pageSize}
              totalCount={allUsersQuery.data.totalCount}
              totalPages={allUsersQuery.data.totalPages}
              hasPreviousPage={allUsersQuery.data.hasPreviousPage}
              hasNextPage={allUsersQuery.data.hasNextPage}
              onPageChange={setAllUsersPage}
            />
          )}
        </>
      )}

      <UserDetailsDrawer
        user={detailsUser}
        isOpen={isDetailsDrawerOpen}
        activeTab={activeTab}
        isMutating={isMutating}
        onClose={closeDetailsDrawer}
        onApprove={(user) => openActionModal('approve', user)}
        onReject={(user) => openActionModal('reject', user)}
        onDeactivate={(user) => openActionModal('deactivate', user)}
        onReactivate={(user) => openActionModal('reactivate', user)}
      />

      <ConfirmationModal
        isOpen={modal.isOpen}
        onClose={() => setModal({ isOpen: false, type: null, user: null })}
        onConfirm={handleConfirmAction}
        isLoading={isMutating}
        title={modalTitle}
        description={modalDescription}
        confirmText={modalConfirmText}
        variant={modal.type === 'approve' || modal.type === 'reactivate' ? 'success' : 'danger'}
      />
    </div>
  );
};