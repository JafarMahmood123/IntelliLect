interface SelectOption {
  value: string;
  label: string;
}

interface SelectProps {
  id?: string;
  label?: string;
  value: string;
  options: SelectOption[];
  disabled?: boolean;
  error?: string;
  onChange: (value: string) => void;
}

export const Select = ({
  id,
  label,
  value,
  options,
  disabled = false,
  error,
  onChange,
}: SelectProps) => {
  return (
    <div className="flex w-full flex-col gap-1 text-start">
      {label && (
        <label
          htmlFor={id}
          className="text-sm font-medium text-slate-700 dark:text-slate-300"
        >
          {label}
        </label>
      )}

      <select
        id={id}
        value={value}
        disabled={disabled}
        onChange={(event) => onChange(event.target.value)}
        className={`rounded-lg border px-4 py-2.5 outline-none transition-all dark:bg-slate-950/50 dark:text-slate-100
          focus:border-violet-500 focus:ring-2 focus:ring-violet-500/50
          ${
            error
              ? 'border-red-500/80 focus:border-red-500 focus:ring-red-500/50'
              : 'border-slate-200 bg-slate-50 dark:border-slate-800'
          }
          disabled:cursor-not-allowed disabled:opacity-60`}
      >
        {options.map((option) => (
          <option key={option.value || 'empty'} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>

      {error && <span className="text-xs font-medium text-red-500">{error}</span>}
    </div>
  );
};