import { Link, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  AlertTriangle,
  ArrowLeft,
  BookOpen,
  FileText,
  GraduationCap,
  Users as UsersIcon,
} from 'lucide-react';
import { useUserDetail } from '../hooks/useUserQueries';
import { UserStatusActions } from './UserStatusActions';
import type { ClassroomSummary } from '../types';

const getStatusBadgeClasses = (status: string) => {
  switch (status) {
    case 'Active':
      return 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400';
    case 'Pending':
      return 'bg-yellow-100 text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400';
    case 'Rejected':
    case 'Deactivated':
      return 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400';
    default:
      return 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300';
  }
};

export const UserDetailPage = () => {
  const { t } = useTranslation('superAdmin');
  const { id } = useParams<{ id: string }>();
  const { data, isLoading, isError, error } = useUserDetail(id);

  const backLink = (
    <Link
      to="/super-admin/users"
      className="mb-4 inline-flex items-center gap-2 text-sm font-medium text-slate-600 hover:text-violet-600 dark:text-slate-400 dark:hover:text-violet-400"
    >
      <ArrowLeft size={16} />
      {t('users.detail.backToUsers')}
    </Link>
  );

  if (isLoading) {
    return (
      <div className="mx-auto w-full max-w-4xl p-6">
        {backLink}
        <div className="p-8 text-slate-500">{t('users.detail.loading')}</div>
      </div>
    );
  }

  if (isError || !data) {
    // Alternate path 7أ: the account does not exist (404) — distinct from a generic error.
    const status = (error as { response?: { status?: number } } | null)?.response?.status;
    const message =
      status === 404 ? t('users.detail.notFound') : t('users.detail.loadError');
    return (
      <div className="mx-auto w-full max-w-4xl p-6">
        {backLink}
        <div className="rounded-lg border border-amber-200 bg-amber-50 p-6 text-amber-800 dark:border-amber-900/50 dark:bg-amber-950/30 dark:text-amber-300">
          {message}
        </div>
      </div>
    );
  }

  const { user, teaching, enrolled, membershipsUnavailable } = data;
  const joined = new Date(user.createdAtUtc);

  const renderClassrooms = (classrooms: ClassroomSummary[], emptyKey: string) => {
    if (classrooms.length === 0) {
      return (
        <p className="text-sm text-slate-500 dark:text-slate-400">{t(emptyKey)}</p>
      );
    }
    return (
      <ul className="grid gap-3 sm:grid-cols-2">
        {classrooms.map((c) => (
          <li
            key={c.id}
            className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"
          >
            <div className="font-medium text-slate-900 dark:text-slate-100">
              {c.name}
            </div>
            {c.description && (
              <p className="mt-1 line-clamp-2 text-xs text-slate-500 dark:text-slate-400">
                {c.description}
              </p>
            )}
            <div className="mt-3 flex items-center gap-4 text-xs text-slate-500 dark:text-slate-400">
              <span className="inline-flex items-center gap-1">
                <UsersIcon size={14} />
                {t('users.detail.classroom.students', { count: c.studentCount })}
              </span>
              <span className="inline-flex items-center gap-1">
                <FileText size={14} />
                {t('users.detail.classroom.files', { count: c.fileCount })}
              </span>
            </div>
          </li>
        ))}
      </ul>
    );
  };

  return (
    <div className="mx-auto w-full max-w-4xl p-6">
      {backLink}

      {/* Profile card */}
      <div className="rounded-xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <h1 className="text-2xl font-bold text-slate-900 dark:text-white">
              {user.firstName} {user.lastName}
            </h1>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              @{user.userName}
            </p>
          </div>
          <div className="flex flex-col items-end gap-3">
            <span
              className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${getStatusBadgeClasses(
                user.status,
              )}`}
            >
              {user.status}
            </span>
            <UserStatusActions userId={user.id} status={user.status} size="md" />
          </div>
        </div>

        <dl className="mt-6 grid gap-4 sm:grid-cols-2">
          <div>
            <dt className="text-xs uppercase tracking-wide text-slate-400">
              {t('users.detail.profile.email')}
            </dt>
            <dd className="text-sm text-slate-800 dark:text-slate-200">{user.email}</dd>
          </div>
          <div>
            <dt className="text-xs uppercase tracking-wide text-slate-400">
              {t('users.detail.profile.role')}
            </dt>
            <dd className="text-sm text-slate-800 dark:text-slate-200">{user.roleName}</dd>
          </div>
          <div>
            <dt className="text-xs uppercase tracking-wide text-slate-400">
              {t('users.detail.profile.joined')}
            </dt>
            <dd className="text-sm text-slate-800 dark:text-slate-200">
              {joined.toLocaleDateString()} {joined.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
            </dd>
          </div>
          <div>
            <dt className="text-xs uppercase tracking-wide text-slate-400">
              {t('users.detail.profile.bio')}
            </dt>
            <dd className="text-sm text-slate-800 dark:text-slate-200">
              {user.bio || t('users.detail.profile.noBio')}
            </dd>
          </div>
        </dl>
      </div>

      {/* Memberships */}
      <div className="mt-6">
        {membershipsUnavailable ? (
          <div className="flex items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800 dark:border-amber-900/50 dark:bg-amber-950/30 dark:text-amber-300">
            <AlertTriangle size={18} />
            {t('users.detail.membershipsUnavailable')}
          </div>
        ) : (
          <div className="space-y-8">
            <section>
              <h2 className="mb-3 flex items-center gap-2 text-lg font-semibold text-slate-900 dark:text-white">
                <GraduationCap size={20} className="text-violet-500" />
                {t('users.detail.teaching.title')}
              </h2>
              {renderClassrooms(teaching, 'users.detail.teaching.empty')}
            </section>

            <section>
              <h2 className="mb-3 flex items-center gap-2 text-lg font-semibold text-slate-900 dark:text-white">
                <BookOpen size={20} className="text-violet-500" />
                {t('users.detail.enrolled.title')}
              </h2>
              {renderClassrooms(enrolled, 'users.detail.enrolled.empty')}
            </section>
          </div>
        )}
      </div>
    </div>
  );
};
