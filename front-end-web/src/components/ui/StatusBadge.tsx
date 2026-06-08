interface StatusBadgeProps {
  status: string | number; // Updated to accept both
}

export const StatusBadge = ({ status }: StatusBadgeProps) => {
  // 1. Map numeric enums to strings if necessary
  const statusMap: Record<number, string> = {
    0: "Scheduled",
    1: "Live",
    2: "Ended",
  };

  // 2. Ensure we have a string to work with
  const statusString =
    typeof status === "number"
      ? statusMap[status] || "Unknown"
      : status || "Unknown";

  const normalizedStatus = statusString.toLowerCase();

  let colorClasses =
    "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300";

  // Live / Active
  if (normalizedStatus === "active" || normalizedStatus === "live") {
    colorClasses =
      "bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400";
  }
  // Pending / Scheduled
  else if (normalizedStatus === "pending" || normalizedStatus === "scheduled") {
    colorClasses =
      "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400";
  }
  // Error / Ended / Rejected
  else if (
    ["deactivated", "inactive", "rejected", "ended"].includes(normalizedStatus)
  ) {
    colorClasses =
      "bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400";
  }

  return (
    <span
      className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-medium ${colorClasses}`}
    >
      {statusString}
    </span>
  );
};
