import { describe, expect, it } from 'vitest';
import { AxiosError, AxiosHeaders } from 'axios';
import { getApiErrorMessage } from './getApiErrorMessage';

/**
 * The sentence a user reads when something goes wrong.
 *
 * Every failure in the app funnels through here, and its job is to prefer the server's own
 * wording over a generic fallback — because the server is the only party that knows *why*. The
 * difference is between "This email is already registered" and "Something went wrong", and the
 * second one sends the user to try the same thing again.
 *
 * It was at 11%, which for a pure function of this shape means almost none of the fallback chain
 * had ever been executed. That chain is the whole file: the order decides which of several
 * candidate messages wins, and picking the wrong one shows the user a framework string.
 */

const FALLBACK = 'Something went wrong.';

const axiosError = (data: unknown, message = 'Request failed with status code 400') => {
  const error = new AxiosError(message, 'ERR_BAD_REQUEST', undefined, undefined, {
    data,
    status: 400,
    statusText: 'Bad Request',
    headers: new AxiosHeaders(),
    config: { headers: new AxiosHeaders() },
  });
  return error;
};

describe('getApiErrorMessage', () => {
  describe('the server said something', () => {
    it('prefers the problem detail, which is where our services put the reason', () => {
      // ASP.NET's ProblemDetails and FastAPI's `detail` both land here, and it is the field our
      // own handlers deliberately write a human sentence into.
      const message = getApiErrorMessage(
        axiosError({ detail: 'This email is already registered.' }),
        FALLBACK,
      );

      expect(message).toBe('This email is already registered.');
    });

    it('reads a plain string body', () => {
      expect(getApiErrorMessage(axiosError('Quiz is already closed.'), FALLBACK)).toBe(
        'Quiz is already closed.',
      );
    });

    it('falls back to the title, then the message, in that order', () => {
      // `title` is ProblemDetails' generic category ("One or more validation errors occurred")
      // while `detail` is the specific reason — so detail has to win, and title is only reached
      // when there is nothing better.
      expect(getApiErrorMessage(axiosError({ title: 'Conflict.' }), FALLBACK)).toBe('Conflict.');
      expect(getApiErrorMessage(axiosError({ message: 'Nope.' }), FALLBACK)).toBe('Nope.');
      expect(
        getApiErrorMessage(axiosError({ detail: 'Specific.', title: 'Generic.' }), FALLBACK),
      ).toBe('Specific.');
    });
  });

  describe('model validation', () => {
    it('surfaces the first validation message rather than the generic title', () => {
      // ASP.NET returns `errors` alongside a title of "One or more validation errors occurred",
      // which tells the user nothing. The field message is the actionable half.
      const message = getApiErrorMessage(
        axiosError({
          title: 'One or more validation errors occurred.',
          errors: { Password: ['Password must be at least 8 characters.'] },
        }),
        FALLBACK,
      );

      expect(message).toBe('Password must be at least 8 characters.');
    });

    it('accepts a validation entry that is a bare string, not an array', () => {
      expect(
        getApiErrorMessage(axiosError({ errors: { Email: 'Email is required.' } }), FALLBACK),
      ).toBe('Email is required.');
    });

    it('skips a field whose messages are unusable and keeps looking', () => {
      // A field with an empty array, or one holding non-strings, must not end the search — the
      // next field may carry the message the user needs.
      const message = getApiErrorMessage(
        axiosError({
          errors: {
            Ignored: [],
            AlsoIgnored: [123, null],
            Password: ['Password is too short.'],
          },
        }),
        FALLBACK,
      );

      expect(message).toBe('Password is too short.');
    });

    it('still prefers an explicit detail over a validation message', () => {
      expect(
        getApiErrorMessage(
          axiosError({ detail: 'Account is locked.', errors: { Email: ['Invalid.'] } }),
          FALLBACK,
        ),
      ).toBe('Account is locked.');
    });
  });

  describe('the server said nothing useful', () => {
    it('uses the axios message when the body carries none', () => {
      expect(getApiErrorMessage(axiosError({}, 'Network Error'), FALLBACK)).toBe('Network Error');
    });

    it('falls back when every candidate is blank', () => {
      // Whitespace counts as blank. A message of "   " renders as an empty error box — visibly
      // broken, and worse than the generic sentence.
      expect(
        getApiErrorMessage(axiosError({ detail: '   ', title: '', message: '' }, '  '), FALLBACK),
      ).toBe(FALLBACK);
    });

    it('falls back when the body is a type nobody expected', () => {
      expect(getApiErrorMessage(axiosError([1, 2, 3], '   '), FALLBACK)).toBe(FALLBACK);
      expect(getApiErrorMessage(axiosError(null, '   '), FALLBACK)).toBe(FALLBACK);
    });

    it('falls back when the detail is a non-string', () => {
      // A server that answers `{"detail": {"code": 42}}` must not put "[object Object]" in front
      // of a user.
      expect(getApiErrorMessage(axiosError({ detail: { code: 42 } }, '  '), FALLBACK)).toBe(
        FALLBACK,
      );
    });
  });

  describe('errors that never reached the server', () => {
    it('uses a plain Error message', () => {
      expect(getApiErrorMessage(new Error('The file is too large.'), FALLBACK)).toBe(
        'The file is too large.',
      );
    });

    it('falls back for anything that is not an error at all', () => {
      // A rejected promise carrying a string, a number, or undefined — none of them a shape this
      // function can read, and all of them things a `catch` block genuinely receives.
      expect(getApiErrorMessage('a bare string', FALLBACK)).toBe(FALLBACK);
      expect(getApiErrorMessage(undefined, FALLBACK)).toBe(FALLBACK);
      expect(getApiErrorMessage(null, FALLBACK)).toBe(FALLBACK);
      expect(getApiErrorMessage({ detail: 'not an axios error' }, FALLBACK)).toBe(FALLBACK);
    });
  });
});
