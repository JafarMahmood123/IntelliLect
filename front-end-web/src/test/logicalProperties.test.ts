import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, relative } from 'node:path';

/**
 * Directional CSS that survives `dir="rtl"` (work-plan §11.14, test-plan M-10).
 *
 * M-10 asks that "RTL layout renders without overflow on the main pages". The obvious way to test
 * that is to render and measure — and it is not available here: jsdom has no layout engine, so
 * every width it reports is zero. A browser could measure it, but a browser assertion tells you
 * the pixels were right on the machine that ran it, for the pages somebody remembered to open.
 *
 * The **cause** is not in the pixels. Tailwind's physical utilities — `ml-*`, `pl-*`, `left-*`,
 * `text-left`, `border-l`, `rounded-tl-*` — do not flip when the document direction does. Their
 * logical counterparts (`ms-*`, `ps-*`, `start-*`, `text-start`, `border-s`, `rounded-ss-*`) do.
 * So a physical utility on a directional edge is an RTL bug written down in source, and this reads
 * it there instead of hunting for its consequences.
 *
 * When this rule was first written the app had **91 of them**, and the app already sets
 * `document.documentElement.dir = 'rtl'` for Arabic. The clearest was a pair repeated eleven
 * times: a search icon at `left-3` inside an input padded `pl-10`. In Arabic the text starts on
 * the right, the padding stays on the left, and the icon sits on top of the first characters the
 * user types. Two places had already been patched by hand — a `ltr:right-4 rtl:left-4` and an
 * `isRtl ? … : …` ternary — which is how a problem that is known but not systematised looks.
 *
 * Exemptions are named individually with a reason, and checked in both directions: an entry whose
 * file no longer contains a physical utility fails, because a stale exemption is a hole nobody is
 * looking at.
 */

// Resolved from the working directory rather than import.meta.url: Vitest rewrites module URLs,
// so `new URL('..', import.meta.url)` resolves to `/src` at the filesystem root.
const SOURCE_ROOT = join(process.cwd(), 'src');

/**
 * Physical utilities that have a logical counterpart, and what to use instead.
 *
 * `translate-x-*` is deliberately absent: it is physical, it has no logical form in Tailwind, and
 * the two places that need it spell the direction out with an `rtl:` variant. A rule that flagged
 * it would have no correct fix to point at.
 */
const PHYSICAL: Array<{ pattern: RegExp; instead: string }> = [
  { pattern: /(?<![\w-])ml-[a-z0-9.[\]/%-]+/g, instead: 'ms-*' },
  { pattern: /(?<![\w-])mr-[a-z0-9.[\]/%-]+/g, instead: 'me-*' },
  { pattern: /(?<![\w-])pl-[a-z0-9.[\]/%-]+/g, instead: 'ps-*' },
  { pattern: /(?<![\w-])pr-[a-z0-9.[\]/%-]+/g, instead: 'pe-*' },
  { pattern: /(?<![\w-])left-[a-z0-9.[\]/%-]+/g, instead: 'start-*' },
  { pattern: /(?<![\w-])right-[a-z0-9.[\]/%-]+/g, instead: 'end-*' },
  { pattern: /(?<![\w-])text-left(?![\w-])/g, instead: 'text-start' },
  { pattern: /(?<![\w-])text-right(?![\w-])/g, instead: 'text-end' },
  { pattern: /(?<![\w-])border-l(?![\w-])/g, instead: 'border-s' },
  { pattern: /(?<![\w-])border-r(?![\w-])/g, instead: 'border-e' },
  { pattern: /(?<![\w-])rounded-(tl|tr|bl|br)(-[a-z0-9]+)?(?![\w-])/g, instead: 'rounded-ss/se/es/ee-*' },
];

/** Files allowed to keep a physical utility, each with the reason it is genuinely physical. */
const EXEMPT: Record<string, string> = {
  'features/summaries/components/SummaryPreview.tsx':
    'The rendered markdown is inside dir="ltr" and is English-only by design, so its list '
    + 'indentation and text alignment are physically left on purpose.',
  'features/whiteboard/components/Toolbox.tsx':
    'left-1/2 with -translate-x-1/2 is the horizontal CENTRING idiom, not a directional edge. '
    + 'start-1/2 would break it, because translate-x does not flip.',
};

const componentFiles = (directory: string): string[] =>
  readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return componentFiles(path);
    return entry.name.endsWith('.tsx') && !entry.name.includes('.test.') ? [path] : [];
  });

/** Each `` `…` `` or '…' className expression, so a rule can ask about one element at a time. */
const classExpressions = (source: string): string[] => [
  ...[...source.matchAll(/`[^`]*`/g)].map((match) => match[0]),
  ...[...source.matchAll(/className="([^"]*)"/g)].map((match) => match[1]),
];

const physicalUsesIn = (file: string): string[] => {
  const source = readFileSync(file, 'utf8');
  return PHYSICAL.flatMap(({ pattern, instead }) =>
    [...source.matchAll(pattern)].map((match) => `${match[0]} (use ${instead})`),
  );
};

describe('directional CSS survives RTL', () => {
  const files = componentFiles(SOURCE_ROOT);

  it('finds the components at all', () => {
    // Every assertion below passes over an empty list, and this one reads from disk — a moved
    // folder or a changed extension would take the whole rule green with it.
    expect(files.length).toBeGreaterThan(50);
  });

  it('no component positions itself with a utility that ignores the document direction', () => {
    const offenders = files
      .map((file) => ({ file: relative(SOURCE_ROOT, file), uses: physicalUsesIn(file) }))
      .filter(({ file, uses }) => uses.length > 0 && !(file in EXEMPT))
      .map(({ file, uses }) => `${file}: ${[...new Set(uses)].join(', ')}`);

    expect(
      offenders,
      'These stay put when the document flips to RTL, so spacing lands on the wrong side and '
        + 'absolutely-positioned chrome overlaps the text it was meant to sit beside',
    ).toEqual([]);
  });

  it('horizontal travel is spelled for both directions', () => {
    // `translate-x` is physical and Tailwind has no logical form for it, so it is absent from
    // PHYSICAL above — and that left the two places that NEED it unguarded. Both were found by a
    // mutation surviving: a drawer whose closed position is off the right edge stays off the right
    // edge in RTL, so it never leaves the screen.
    //
    // File-level pairing rather than per-expression. It is coarse, and it is enough: a file that
    // moves something horizontally and never mentions RTL has not thought about it.
    const unpaired = files
      .map((file) => ({ file: relative(SOURCE_ROOT, file), source: readFileSync(file, 'utf8') }))
      .filter(({ file }) => !(file in EXEMPT))
      .filter(({ source }) => /(?<![\w:-])-?translate-x-(?!1\/2)/.test(source))
      .filter(({ source }) => !/rtl:-?translate-x-/.test(source))
      .map(({ file }) => file);

    expect(unpaired, 'moves horizontally with no RTL counterpart').toEqual([]);
  });

  it('anything that moves horizontally is also anchored horizontally', () => {
    // The other half of the same pair, and the other surviving mutation. An absolutely-positioned
    // element with no horizontal anchor sits at its static position — the RIGHT edge under
    // dir="rtl" — and the travel below then pushes it further right, off whatever it was sliding
    // along. A toggle knob doing that leaves the track entirely.
    // Scoped to the enclosing className expression, not the file. A file-wide check passes as
    // soon as ANY element in it is anchored, which is almost always — the first version of this
    // rule did exactly that and the mutation walked straight through it.
    const unanchored = files
      .map((file) => ({ file: relative(SOURCE_ROOT, file), source: readFileSync(file, 'utf8') }))
      .filter(({ file }) => !(file in EXEMPT))
      .filter(({ source }) =>
        classExpressions(source).some(
          (expression) =>
            /(?<![\w:-])absolute(?![\w-])/.test(expression)
            && /(?<![\w:-])-?translate-x-(?!1\/2)/.test(expression)
            && !/(?<![\w-])(start|end|inset-x)-[a-z0-9.[\]/%-]+/.test(expression),
        ),
      )
      .map(({ file }) => file);

    expect(unanchored, 'moves horizontally from an unanchored position').toEqual([]);
  });

  it('every exemption is still a file that needs one', () => {
    // The other direction. An exemption whose file no longer has a physical utility is a hole
    // left open for a reason that expired, and nothing else would ever report it.
    for (const [file, reason] of Object.entries(EXEMPT)) {
      expect(reason.length, `${file} needs a real reason`).toBeGreaterThan(30);
      const source = readFileSync(join(SOURCE_ROOT, file), 'utf8');
      const stillPhysical =
        physicalUsesIn(join(SOURCE_ROOT, file)).length > 0 || /translate-x-/.test(source);

      expect(
        stillPhysical,
        `${file} is exempted but no longer contains a physical utility — drop the exemption`,
      ).toBe(true);
    }
  });

  it('the app really does flip direction, or none of this would matter', () => {
    // The premise. If the app never set dir="rtl", every rule above would be enforcing a
    // convention with no consequence attached to breaking it.
    const i18n = readFileSync(join(SOURCE_ROOT, 'lib/i18n.ts'), 'utf8');

    expect(i18n).toMatch(/documentElement\.dir\s*=/);
    expect(i18n).toContain("'rtl'");
  });
});
