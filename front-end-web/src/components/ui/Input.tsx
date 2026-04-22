import React from 'react';

interface InputProps extends React.InputHTMLAttributes<HTMLInputElement> {
  label: string;
  error?: string;
}

export const Input = React.forwardRef<HTMLInputElement, InputProps>(
  ({ label, error, ...props }, ref) => {
    return (
      <div className="flex flex-col gap-1 w-full mb-4 text-left">
        <label className="text-sm font-medium text-slate-700 dark:text-slate-300">{label}</label>
        <input
          ref={ref}
          className={`px-4 py-2.5 bg-slate-50 border rounded-lg outline-none transition-all 
            dark:bg-slate-950/50 dark:text-slate-100
            focus:ring-2 focus:ring-violet-500/50 focus:border-violet-500
            ${error ? 'border-red-500/80 focus:border-red-500 focus:ring-red-500/50' : 'border-slate-200 dark:border-slate-800'}`}
          {...props}
        />
        {error && <span className="text-xs text-red-500 font-medium">{error}</span>}
      </div>
    );
  }
);
Input.displayName = 'Input';