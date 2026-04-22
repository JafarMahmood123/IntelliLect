interface StatusBadgeProps {
  status: string;
}

export const StatusBadge = ({ status }: StatusBadgeProps) => {
  const normalizedStatus = status.toLowerCase();

  let colorClasses =
    'bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300';

  if (normalizedStatus === 'active') {
    colorClasses =
      'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400';
  } else if (normalizedStatus === 'pending') {
    colorClasses =
      'bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400';
  } else if (
    normalizedStatus === 'deactivated' ||
    normalizedStatus === 'inactive' ||
    normalizedStatus === 'rejected'
  ) {
    colorClasses =
      'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400';
  }

  return (
    <span
      className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-medium ${colorClasses}`}
    >
      {status}
    </span>
  );
};