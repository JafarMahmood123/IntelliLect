import React from 'react';

interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  isLoading?: boolean;
}

export const Button: React.FC<ButtonProps> = ({ children, isLoading, disabled, ...props }) => {
  return (
    <button
      disabled={isLoading || disabled}
      className="w-full bg-purple-600 hover:bg-purple-700 text-white font-medium py-2 px-4 rounded-md transition-colors disabled:opacity-50 flex justify-center"
      {...props}
    >
      {isLoading ? 'Loading...' : children}
    </button>
  );
};