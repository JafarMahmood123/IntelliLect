import { describe, expect, it } from 'vitest';

import {
  AA,
  colorOf,
  contrastRatio,
  flatten,
  parseColorClass,
  type Rgb,
} from '../../../test/contrast';
import { SEVERITY_STYLES, SURFACES, TEXT_ON_SURFACE } from './TeacherFeedbackPanel';

/**
 * Contrast on the assistant's feedback cards (work-plan §11.11, test-plan H-10).
 *
 * The colour-blindness half was closed earlier: every severity carries an icon AND a written
 * label, so the card reads correctly in greyscale. This is the other half — that the labels can
 * be read at all.
 *
 * **The plan said this needed a rendering check in both themes. It does not, and a rendering
 * check would have been the weaker test.** `getComputedStyle` in jsdom hands the class names
 * back unresolved, and in a real browser it reports whatever that build produced — so a browser
 * assertion tells you the pixels were right on the machine that ran it. These colours are design
 * tokens; asserting on the tokens catches the problem where it is introduced.
 *
 * **And there is only one theme here.** The panel carries no `dark:` variants at all — it is
 * fixed dark because it floats over a video room, where a light surface would glare. That is a
 * deliberate exception to the app's light/dark support, and the last test in this file pins it,
 * because "in both themes" is otherwise an assumption nobody would re-check.
 *
 * The values are imported from the component, never copied. A test holding its own copy of a
 * colour passes forever after the component stops using it.
 */

/**
 * Behind a translucent panel is the video. Both extremes are checked because the room's content
 * is not ours to control: a lecturer on a bright slide and one on a dark stage produce different
 * composites, and only the worse of the two is a real guarantee.
 */
const ROOM_BACKDROPS: Array<[string, Rgb]> = [
  ['a dark room', colorOf('black')],
  ['a bright slide', colorOf('white')],
];

const cardOver = (backdrop: Rgb): Rgb => flatten(backdrop, [SURFACES.panel, SURFACES.card]);
const panelOver = (backdrop: Rgb): Rgb => flatten(backdrop, [SURFACES.panel]);

describe('feedback card contrast', () => {
  describe.each(ROOM_BACKDROPS)('over %s', (_label, backdrop) => {
    it.each(Object.keys(SEVERITY_STYLES))(
      'the %s chip label is readable',
      (severity) => {
        const style = SEVERITY_STYLES[severity as keyof typeof SEVERITY_STYLES];
        const [background, text] = style.chip.split(' ');

        // The chip's own tint sits on top of the card, which sits on the panel, which sits on
        // the video. Measuring the label against the chip colour alone would flatter it.
        const behindTheLabel = flatten(cardOver(backdrop), [background]);
        const ratio = contrastRatio(behindTheLabel, parseColorClass(text).color);

        expect(
          ratio,
          `${severity}: ${text} on ${background} reads at ${ratio.toFixed(2)}:1`,
        ).toBeGreaterThanOrEqual(AA.normalText);
      },
    );

    it('the suggestion text is readable', () => {
      const ratio = contrastRatio(
        cardOver(backdrop),
        parseColorClass(TEXT_ON_SURFACE.body).color,
      );

      expect(ratio).toBeGreaterThanOrEqual(AA.normalText);
    });

    it('the timestamp beside each chip is readable', () => {
      // The defect this file was written for. It was `text-slate-500`, which reads at 3.4:1 on
      // the card — below AA — and it is 10px text, so the normal-text threshold is the one that
      // applies. "It's only a small timestamp" is exactly the reasoning that would have picked
      // the large-text number instead.
      const ratio = contrastRatio(
        cardOver(backdrop),
        parseColorClass(TEXT_ON_SURFACE.meta).color,
      );

      expect(ratio).toBeGreaterThanOrEqual(AA.normalText);
    });

    it('the empty-state line is readable', () => {
      // Same colour, different surface: this one sits on the panel with no card beneath it, so
      // it is measured against a lighter composite and fails independently.
      const ratio = contrastRatio(
        panelOver(backdrop),
        parseColorClass(TEXT_ON_SURFACE.emptyState).color,
      );

      expect(ratio).toBeGreaterThanOrEqual(AA.normalText);
    });

    it('every severity is distinguishable from the card it sits on', () => {
      // A non-text requirement: the chip has to be visible as a shape, not just its label. Three
      // chips that all disappear into the card would be three identical rectangles with
      // different words in them — which is the accessible-but-useless outcome.
      for (const style of Object.values(SEVERITY_STYLES)) {
        const [background] = style.chip.split(' ');
        const ring = style.chip.split(' ').find((c) => c.startsWith('ring-') && c.includes('/'));
        expect(ring, 'each chip needs a ring to define its edge').toBeDefined();

        const chipSurface = flatten(cardOver(backdrop), [background]);
        const ringColor = parseColorClass(ring!);
        const ringSurface = flatten(chipSurface, [ring!]);

        expect(
          contrastRatio(cardOver(backdrop), ringSurface),
          `the ${ringColor.alpha} ring does not separate the chip from the card`,
        ).toBeGreaterThan(1.05);
      }
    });
  });

  it('every severity is a different colour, not only a different word', () => {
    // Colour is not the only carrier — icon and label do that job — but it must still carry
    // something. Three severities painted identically would make the palette decorative.
    const chipColors = Object.values(SEVERITY_STYLES).map((style) => style.chip.split(' ')[1]);

    expect(new Set(chipColors).size).toBe(chipColors.length);
  });

  it('the panel is deliberately one theme, and says so', () => {
    // 648 `dark:` variants elsewhere in the app; none here. If someone adds light-theme support
    // to this panel, every ratio above was computed against the wrong background and this test
    // is the reminder to recompute them rather than to delete this line.
    const surfaces = Object.values({ ...SURFACES, ...TEXT_ON_SURFACE });

    expect(surfaces.every((className) => !className.includes('dark:'))).toBe(true);
  });
});

describe('the contrast maths itself', () => {
  // Pinned against values that are true by definition, so a wrong implementation cannot make
  // every assertion above pass by reporting large numbers for everything.
  it('white on black is the maximum ratio of 21', () => {
    expect(contrastRatio(colorOf('white'), colorOf('black'))).toBeCloseTo(21, 5);
  });

  it('a colour against itself is 1', () => {
    expect(contrastRatio(colorOf('slate-800'), colorOf('slate-800'))).toBeCloseTo(1, 5);
  });

  it('resolves a Tailwind colour to the value Tailwind ships', () => {
    // red-300 is #ffa2a2 in Tailwind 4. If the palette moves, this fails and the ratios above
    // are recomputed against the new one — which is the point of reading node_modules rather
    // than keeping a table here.
    expect(colorOf('red-300')).toEqual({ r: 255, g: 162, b: 162 });
  });

  it('compositing a fully transparent layer changes nothing', () => {
    const base = colorOf('slate-900');

    expect(flatten(base, ['bg-white/0'])).toEqual(base);
  });

  it('compositing an opaque layer replaces what is beneath', () => {
    expect(flatten(colorOf('black'), ['bg-white'])).toEqual(colorOf('white'));
  });
});
