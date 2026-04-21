import React from 'react';

interface InputProps extends React.InputHTMLAttributes<HTMLInputElement> {
  label: string;
  error?: string;
}

export const Input = React.forwardRef<HTMLInputElement, InputProps>(
  ({ label, error, ...props }, ref) => {
    return (
      <div className="flex flex-col gap-1 w-full mb-4 text-left">
        <label className="text-sm font-medium text-gray-700 dark:text-gray-300">{label}</label>
        <input
          ref={ref}
          className={`px-3 py-2 border rounded-md outline-none transition-colors 
            dark:bg-gray-800 dark:border-gray-700 dark:text-white
            focus:border-purple-500 focus:ring-1 focus:ring-purple-500
            ${error ? 'border-red-500' : 'border-gray-300'}`}
          {...props}
        />
        {error && <span className="text-xs text-red-500">{error}</span>}
      </div>
    );
  }
);
Input.displayName = 'Input';