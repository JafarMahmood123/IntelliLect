/**
 * WCAG contrast, computed from the Tailwind palette the app is actually built against.
 *
 * The work plan (§11.11, test-plan H-10) assumed this needed a rendering check in both themes.
 * It does not, and a rendering check would be the weaker test: `getComputedStyle` in jsdom
 * reports the class names back, and in a real browser it reports whatever the current build
 * resolved them to — so a browser assertion tells you the pixels were right on the machine that
 * ran it. The colours here are design tokens. Asserting on the tokens catches the problem where
 * it is introduced, and does it in milliseconds.
 *
 * The palette is READ from `node_modules/tailwindcss/theme.css`, never copied. Tailwind 4 ships
 * its colours in OKLCH, and it has re-tuned them between minor versions before; a hard-coded
 * table would keep passing against values the app no longer uses.
 */

import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';

// --- the palette ------------------------------------------------------------------

const themeCss = (): string => {
  const require = createRequire(import.meta.url);
  return readFileSync(require.resolve('tailwindcss/theme.css'), 'utf8');
};

export type Rgb = { r: number; g: number; b: number };

/** `oklch(80.8% 0.114 19.571)` -> linear-light sRGB, then to 0-255 sRGB. */
export const oklchToRgb = (lightness: number, chroma: number, hueDegrees: number): Rgb => {
  const hue = (hueDegrees * Math.PI) / 180;
  const a = chroma * Math.cos(hue);
  const bComponent = chroma * Math.sin(hue);

  // OKLab -> LMS
  const l = (lightness + 0.3963377774 * a + 0.2158037573 * bComponent) ** 3;
  const m = (lightness - 0.1055613458 * a - 0.0638541728 * bComponent) ** 3;
  const s = (lightness - 0.0894841775 * a - 1.291485548 * bComponent) ** 3;

  // LMS -> linear sRGB
  const linear = {
    r: +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    g: -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    b: -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  };

  const encode = (channel: number): number => {
    const clamped = Math.min(1, Math.max(0, channel));
    const encoded =
      clamped <= 0.0031308 ? clamped * 12.92 : 1.055 * clamped ** (1 / 2.4) - 0.055;
    return Math.round(encoded * 255);
  };

  return { r: encode(linear.r), g: encode(linear.g), b: encode(linear.b) };
};

let cachedPalette: Map<string, Rgb> | null = null;

/** Every `--color-*` Tailwind defines, as sRGB. */
export const palette = (): Map<string, Rgb> => {
  if (cachedPalette) return cachedPalette;

  const found = new Map<string, Rgb>();
  const pattern = /--color-([a-z0-9-]+):\s*oklch\(([\d.]+)%\s+([\d.]+)\s+([\d.]+)\s*\)/g;
  for (const match of themeCss().matchAll(pattern)) {
    const [, name, lightness, chroma, hue] = match;
    found.set(name, oklchToRgb(Number(lightness) / 100, Number(chroma), Number(hue)));
  }
  // Not every colour is OKLCH — white and black are keywords.
  found.set('white', { r: 255, g: 255, b: 255 });
  found.set('black', { r: 0, g: 0, b: 0 });

  cachedPalette = found;
  return found;
};

export const colorOf = (name: string): Rgb => {
  const found = palette().get(name);
  if (!found) {
    throw new Error(
      `No Tailwind colour named "${name}". If it was renamed or removed upstream, the app is ` +
        'painting with something else than this test believes.',
    );
  }
  return found;
};

// --- compositing ------------------------------------------------------------------

/**
 * A Tailwind class like `bg-slate-900/95` or `text-red-300`, split into colour and alpha.
 *
 * Alpha is why this cannot be read off a palette table alone: the feedback chips are a
 * translucent colour over a translucent card over a translucent panel, and the contrast a
 * teacher sees is against the composite, not against any one of them.
 */
export const parseColorClass = (className: string): { color: Rgb; alpha: number } => {
  const match = /(?:bg|text|ring|border)-([a-z0-9-]+?)(?:\/(\d+))?$/.exec(className);
  if (!match) throw new Error(`Not a Tailwind colour utility: "${className}"`);
  const [, name, alpha] = match;
  return { color: colorOf(name), alpha: alpha === undefined ? 1 : Number(alpha) / 100 };
};

/** Paint `over` on top of `base`. Source-over, straight alpha. */
export const composite = (base: Rgb, over: Rgb, alpha: number): Rgb => ({
  r: Math.round(over.r * alpha + base.r * (1 - alpha)),
  g: Math.round(over.g * alpha + base.g * (1 - alpha)),
  b: Math.round(over.b * alpha + base.b * (1 - alpha)),
});

/** Flatten a stack of `bg-*` classes, bottom first, onto an opaque starting colour. */
export const flatten = (base: Rgb, layers: string[]): Rgb =>
  layers.reduce((beneath, className) => {
    const { color, alpha } = parseColorClass(className);
    return composite(beneath, color, alpha);
  }, base);

// --- WCAG -------------------------------------------------------------------------

const relativeLuminance = ({ r, g, b }: Rgb): number => {
  const channel = (value: number): number => {
    const scaled = value / 255;
    return scaled <= 0.04045 ? scaled / 12.92 : ((scaled + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
};

/** WCAG 2.1 contrast ratio, 1 to 21. */
export const contrastRatio = (a: Rgb, b: Rgb): number => {
  const [lighter, darker] = [relativeLuminance(a), relativeLuminance(b)].sort((x, y) => y - x);
  return (lighter + 0.05) / (darker + 0.05);
};

/**
 * WCAG 2.1 AA thresholds.
 *
 * `largeText` is 18.66px bold or 24px regular. Every label on the feedback card is 10-14px, so
 * the normal-text threshold is the one that applies to all of them — worth stating, because
 * "it's only a small badge" is exactly the reasoning that would pick the lower number.
 */
export const AA = { normalText: 4.5, largeText: 3, nonText: 3 } as const;
