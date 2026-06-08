import { useTranslation } from 'react-i18next';
import { Filter } from 'lucide-react';
import { Select } from '../../../components/ui/Select';
import type { RegistrationRole } from '../../roles/types';

interface AdminRoleFilterProps {
  roles: RegistrationRole[];
  selectedRoleId: string;
  description: string;
  isLoading: boolean;
  isError: boolean;
  onChange: (roleId: string) => void;
}

export const AdminRoleFilter = ({
  roles,
  selectedRoleId,
  description,
  isLoading,
  isError,
  onChange,
}: AdminRoleFilterProps) => {
  const { t } = useTranslation('admin');

  const getRoleLabel = (roleName: string) => {
    const key = `roles.${roleName}`;
    const translated = t(key);

    return translated === key ? roleName : translated;
  };

  const options = [
    {
      value: '',
      label: isLoading ? t('filters.role.loading') : t('filters.role.all'),
    },
    ...roles.map((role) => ({
      value: role.id,
      label: getRoleLabel(role.name),
    })),
  ];

  return (
    <div className="mb-6 rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-col gap-4 md:flex-row md:items-end md:justify-between">
        <div>
          <div className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
            <Filter size={16} className="text-violet-600 dark:text-violet-400" />
            {t('filters.title')}
          </div>

          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            {description}
          </p>
        </div>

        <div className="w-full md:w-72">
          <Select
            id="admin-role-filter"
            label={t('filters.role.label')}
            value={selectedRoleId}
            options={options}
            disabled={isLoading || isError}
            error={isError ? t('filters.role.error') : undefined}
            onChange={onChange}
          />
        </div>
      </div>
    </div>
  );
};