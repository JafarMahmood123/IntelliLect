import { isAxiosError } from 'axios';

type ApiErrorData = {
  detail?: unknown;
  title?: unknown;
  message?: unknown;
  errors?: unknown;
};

const isRecord = (value: unknown): value is Record<string, unknown> => {
  return typeof value === 'object' && value !== null;
};

const getStringValue = (value: unknown): string | null => {
  if (typeof value !== 'string') return null;

  const trimmedValue = value.trim();
  return trimmedValue.length > 0 ? trimmedValue : null;
};

const getFirstValidationError = (errors: unknown): string | null => {
  if (!isRecord(errors)) return null;

  for (const value of Object.values(errors)) {
    if (Array.isArray(value)) {
      const firstMessage = value.find((item) => typeof item === 'string');
      const message = getStringValue(firstMessage);

      if (message) return message;
    }

    const message = getStringValue(value);

    if (message) return message;
  }

  return null;
};

export const getApiErrorMessage = (error: unknown, fallback: string): string => {
  if (isAxiosError<ApiErrorData>(error)) {
    const responseData = error.response?.data;

    if (typeof responseData === 'string') {
      return getStringValue(responseData) ?? fallback;
    }

    if (isRecord(responseData)) {
      return (
        getStringValue(responseData.detail) ??
        getFirstValidationError(responseData.errors) ??
        getStringValue(responseData.title) ??
        getStringValue(responseData.message) ??
        getStringValue(error.message) ??
        fallback
      );
    }

    return getStringValue(error.message) ?? fallback;
  }

  if (error instanceof Error) {
    return getStringValue(error.message) ?? fallback;
  }

  return fallback;
};