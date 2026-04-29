import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { PageHeader } from '../../../components/ui/PageHeader';
import { useToast } from '../../../components/ui/ToastProvider';
import { useAuthStore } from '../../../store/useAuthStore';
import { getApiErrorMessage } from '../../../utils/getApiErrorMessage';
import {
  useChangePassword,
  useCurrentUser,
  useUpdateCurrentUser,
} from '../hooks/useUserQueries';
import {
  ChangePasswordForm,
  type ChangePasswordFormData,
} from './ChangePasswordForm';
import {
  ProfileInfoForm,
  type ProfileInfoFormData,
} from './ProfileInfoForm';
import {
  UserProfileSidebar,
  type UserProfileSection,
} from './UserProfileSidebar';

export const UserProfilePage = () => {
  const { t } = useTranslation('users');
  const { showToast } = useToast();
  const setUser = useAuthStore((state) => state.setUser);

  const [activeSection, setActiveSection] = useState<UserProfileSection>('profile');

  const profileQuery = useCurrentUser();
  const updateProfileMutation = useUpdateCurrentUser();
  const changePasswordMutation = useChangePassword();

  const user = profileQuery.data;

  useEffect(() => {
    if (user) {
      setUser(user);
    }
  }, [setUser, user]);

  const handleProfileSubmit = async (data: ProfileInfoFormData) => {
    if (!user) return;

    try {
      await updateProfileMutation.mutateAsync({
        firstName: data.firstName,
        lastName: data.lastName,
        userName: data.userName,
        bio: data.bio?.trim() ? data.bio.trim() : null,
        version: user.version,
      });

      showToast({
        type: 'success',
        title: t('feedback.profileUpdatedTitle'),
        message: t('feedback.profileUpdatedMessage'),
      });
    } catch (error) {
      showToast({
        type: 'error',
        title: t('feedback.actionFailedTitle'),
        message: getApiErrorMessage(error, t('feedback.fallbackError')),
      });

      throw error;
    }
  };

  const handlePasswordSubmit = async (data: ChangePasswordFormData) => {
    try {
      await changePasswordMutation.mutateAsync({
        oldPassword: data.oldPassword,
        newPassword: data.newPassword,
      });

      showToast({
        type: 'success',
        title: t('feedback.passwordChangedTitle'),
        message: t('feedback.passwordChangedMessage'),
      });
    } catch (error) {
      showToast({
        type: 'error',
        title: t('feedback.actionFailedTitle'),
        message: getApiErrorMessage(error, t('feedback.fallbackError')),
      });

      throw error;
    }
  };

  if (profileQuery.isLoading) {
    return (
      <div className="mx-auto w-full max-w-5xl p-6">
        <div className="rounded-2xl border border-slate-200 bg-white p-6 text-sm text-slate-600 shadow-sm dark:border-slate-800 dark:bg-slate-900 dark:text-slate-400">
          {t('loading')}
        </div>
      </div>
    );
  }

  if (profileQuery.isError || !user) {
    return (
      <div className="mx-auto w-full max-w-5xl p-6">
        <div className="rounded-2xl border border-red-200 bg-red-50 p-6 text-sm text-red-700 shadow-sm dark:border-red-900/60 dark:bg-red-950/30 dark:text-red-400">
          {t('loadError')}
        </div>
      </div>
    );
  }

  return (
    <div className="mx-auto w-full max-w-5xl p-6">
      <PageHeader
        title={t('page.title')}
        description={t('page.description')}
      />

      <div className="grid gap-6 lg:grid-cols-[20rem_minmax(0,1fr)]">
        <UserProfileSidebar
          user={user}
          activeSection={activeSection}
          onSectionChange={setActiveSection}
        />

        <main className="min-w-0">
          {activeSection === 'profile' && (
            <ProfileInfoForm
              user={user}
              isLoading={updateProfileMutation.isPending}
              onSubmit={handleProfileSubmit}
            />
          )}

          {activeSection === 'password' && (
            <ChangePasswordForm
              isLoading={changePasswordMutation.isPending}
              onSubmit={handlePasswordSubmit}
            />
          )}
        </main>
      </div>
    </div>
  );
};