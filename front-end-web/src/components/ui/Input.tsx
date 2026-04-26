import React from 'react';

interface InputProps extends React.InputHTMLAttributes<HTMLInputElement> {
  label: string;
  error?: string;
}

export const Input = React.forwardRef<HTMLInputElement, InputProps>(
  ({ label, error, id, name, ...props }, ref) => {
    const inputId = id ?? name;

    return (
      <div className="mb-4 flex w-full flex-col gap-1 text-start">
        <label
          htmlFor={inputId}
          className="text-sm font-medium text-slate-700 dark:text-slate-300"
        >
          {label}
        </label>

        <input
          id={inputId}
          name={name} 
          ref={ref}
          className={`rounded-lg border px-4 py-2.5 outline-none transition-all dark:bg-slate-950/50 dark:text-slate-100
            focus:border-violet-500 focus:ring-2 focus:ring-violet-500/50
            ${
              error
                ? 'border-red-500/80 focus:border-red-500 focus:ring-red-500/50'
                : 'border-slate-200 bg-slate-50 dark:border-slate-800'
            }`}
          {...props}
        />

        {error && <span className="text-xs font-medium text-red-500">{error}</span>}
      </div>
    );
  }
);

Input.displayName = 'Input';