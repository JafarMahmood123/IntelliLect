import { describe, expect, it } from 'vitest';

/**
 * English and Arabic must describe exactly the same set of keys — test-plan M-09.
 *
 * A missing Arabic key does not throw. i18next falls back to English, so the app keeps working
 * and an Arabic user simply reads a sentence in the wrong language, in the middle of an otherwise
 * translated page. Nothing fails, nothing logs, and it survives until somebody who reads Arabic
 * happens to open that screen.
 *
 * The reverse — a key only Arabic has — is a leftover from a rename, and it is the sign that the
 * two files are being edited independently rather than together.
 *
 * Interpolation placeholders are compared too, because `{{count}}` written as `{{n}}` in one
 * language renders the literal braces to the user.
 */

const english = import.meta.glob('./en/*.json', { eager: true }) as Record<
  string,
  { default: Record<string, unknown> }
>;
const arabic = import.meta.glob('./ar/*.json', { eager: true }) as Record<
  string,
  { default: Record<string, unknown> }
>;

const namespaceOf = (path: string) => path.replace(/^\.\/(en|ar)\//, '').replace(/\.json$/, '');

/** Every leaf key as a dotted path, so a nested block reports the exact key rather than its parent. */
const leafKeys = (value: unknown, prefix = ''): string[] => {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return [prefix];
  return Object.entries(value as Record<string, unknown>).flatMap(([key, child]) =>
    leafKeys(child, prefix ? `${prefix}.${key}` : key),
  );
};

const leafEntries = (value: unknown, prefix = ''): Array<[string, string]> => {
  if (typeof value === 'string') return [[prefix, value]];
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return [];
  return Object.entries(value as Record<string, unknown>).flatMap(([key, child]) =>
    leafEntries(child, prefix ? `${prefix}.${key}` : key),
  );
};

const placeholders = (text: string) =>
  [...text.matchAll(/\{\{\s*([\w]+)/g)].map((match) => match[1]).sort();

/**
 * i18next resolves a `count` option to `key_one`, `key_few`, `key_many`… and the set of categories
 * is a property of the LANGUAGE. English needs two; Arabic needs six. So `groupCount_few`
 * existing only in Arabic is the translation being correct, not the files drifting apart — the
 * comparison has to be between base keys, with the suffixes stripped.
 */
const PLURAL_SUFFIX = /_(zero|one|two|few|many|other)$/;
const isPluralForm = (key: string) => PLURAL_SUFFIX.test(key);
const baseKey = (key: string) => key.replace(PLURAL_SUFFIX, '');

const namespaces = Object.keys(english).map(namespaceOf).sort();

describe('locale parity', () => {
  it('has an Arabic file for every English one', () => {
    expect(Object.keys(arabic).map(namespaceOf).sort()).toEqual(namespaces);
  });

  // A test per namespace: a failure names the file, not "locales are broken".
  it.each(namespaces)('%s has the same keys in both languages', (namespace) => {
    const en = english[`./en/${namespace}.json`].default;
    const ar = arabic[`./ar/${namespace}.json`].default;

    const enKeys = [...new Set(leafKeys(en).map(baseKey))].sort();
    const arKeys = [...new Set(leafKeys(ar).map(baseKey))].sort();

    // Reported as two lists rather than a diff: "missing from Arabic" is the actionable half.
    expect(enKeys.filter((key) => !arKeys.includes(key))).toEqual([]);
    expect(arKeys.filter((key) => !enKeys.includes(key))).toEqual([]);
  });

  it.each(namespaces)('%s uses the same placeholders in both languages', (namespace) => {
    const en = Object.fromEntries(leafEntries(english[`./en/${namespace}.json`].default));
    const ar = Object.fromEntries(leafEntries(arabic[`./ar/${namespace}.json`].default));

    // Plural forms are exempt: "مجموعة واحدة" is the right Arabic for the _one case and carries
    // no numeral, while English's "1 group" does. Only non-plural strings must agree.
    const mismatched = Object.keys(en)
      .filter((key) => key in ar && !isPluralForm(key))
      .filter((key) => placeholders(en[key]).join() !== placeholders(ar[key]).join())
      .map((key) => `${key}: en(${placeholders(en[key])}) ar(${placeholders(ar[key])})`);

    expect(mismatched).toEqual([]);
  });
});
