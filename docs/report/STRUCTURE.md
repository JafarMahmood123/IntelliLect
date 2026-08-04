# HIAST report structure and formatting spec

Extracted from `Hiast Student report template v14.dotx` (the institute's official
template) by reading `styles.xml`, `document.xml`, and the guide text the template
itself contains. This file is the reference we follow; the LaTeX preamble implements it.

## 1. Page setup

| Property | Value |
| --- | --- |
| Page size | **US Letter, 8.5 × 11 in** (12240 × 15840 twips) — *not A4* |
| Margin top | 1.00 in (1440 twips) |
| Margin bottom | 1.10 in (1588 twips) |
| Margin left / right | 1.00 in (1440 twips) |
| Header / footer | 0.5 in (720 twips) |
| Page number | centred in the footer |

## 2. Fonts and sizes

Arabic and Latin sizes differ: the template sets Latin 12 pt against Arabic 15 pt,
a ratio of **1.25**. Every size below is the **Arabic** size; the Latin size is that
divided by 1.25.

| Element | Style name | Arabic pt | Weight | Alignment | Space before / after |
| --- | --- | --- | --- | --- | --- |
| Body | `فقرة` | 15 | — | justified | 0 / 6 pt |
| Chapter number | `رقم الفصل` | 22 | — | centred | 0 / 12 pt |
| Chapter title | `عنوان الفصل` | 32 | bold | centred | 6 / 18 pt |
| Heading 1 | `عنوان 1` | 22 | bold | right | 18 / 6 pt |
| Heading 2 | `عنوان 2` | 17 | bold | right | 12 / 6 pt |
| Heading 3 | `عنوان 3` | 16 | bold | right | 12 / 6 pt |
| Heading 4 | `عنوان 4` | 15 | bold | right | 6 / 0 pt |
| Chapter summary | `ملخص الفصل` | 15 | — | justified | 0 / 36 pt |
| Centred title | `عنوان موسط` | 24 | bold | centred | 0 / 24 pt |
| Caption | `تعليق الشكل` | 14 | bold | centred | 0 / 16 pt |
| Code | `رماز برنامج` | Cambria 14 | — | **forced LTR** | — |
| Appendices page | `الملاحق` | 48 | bold | centred | — |
| Appendix title | `عنوان الملحق` | 32 | bold | centred | — |

Title page: `ترويسة الواجهة` 15 pt bold · `نوع التقرير` 18 pt centred ·
`عنوان التقرير` 28 pt centred · `الأسماء` 18 pt bold centred.

Body paragraph: **justified**, first-line indent 0.5 in (720 twips), line spacing 1.15.

Fonts: **Traditional Arabic** (Arabic) and **Times New Roman** (Latin) are installed
and used as specified.

**One deliberate deviation.** The template specifies Cambria for code, but only
Cambria *Italic* and *Bold* are installed here — there is no Regular — and Cambria
is proportional, which reads poorly for the volume of code this report carries. A
monospace face is used instead. Worth raising with the supervisor; reverting is one
line in `preamble.tex`.

## 3. Document order

Front matter is numbered in **lower-case Roman numerals**; numbering restarts at **1**
at the first chapter. The cover, dedication, quote, and acknowledgement pages are
**unnumbered**, each ending in its own section break.

1. **Cover page** — republic / institute / department / academic year, report type,
   degree, title, author, supervisor, date. No background images, no extra fields.
2. **Dedication** (`الإهداء`) — a few words.
3. **Quote** — optional, with attribution.
4. **Acknowledgements** (`كلمة شكر`) — full names of those thanked.
5. **Abstract** (`الخلاصة`) — ≤ half a page, states the objective and what was built.
6. **Abstract (English)** — same text translated, ideally on the same page.
7. **Contents** (`المحتويات`) — 2 levels deep (3 if wanted).
8. **List of figures** (`قائمة الأشكال`)
9. **List of tables** (`قائمة الجداول`)
10. **Abbreviations** (`الاختصارات`) — `SNR: Signal-to-Noise Ratio` + Arabic gloss.
11. **Symbols** (`الرموز`) — only if the report has significant mathematics.
12. **General introduction** (`مقدمة عامة`) — sits *before* chapter 1, not inside it.
13. **Chapters** — each starts on a new page.
14. **Conclusion** (`الخاتمة`)
15. **Appendices** (`الملاحق`) — divider page, then `الملحق آ`, `الملحق ب`, …
16. **References** (`المراجع`)
17. **Back cover** — the Arabic and English abstracts repeated.

## 4. Chapter anatomy

Every chapter follows the same shape:

1. `الفصل الأول` (number) then the title, both centred.
2. A short **chapter summary** paragraph.
3. Section `-1.x مقدمة` — the chapter's introduction.
4. Body sections.
5. Section `-n.x الخاتمة` — summarises the chapter and states its conclusions.
6. A **transition sentence**, separated by several blank lines, previewing the next
   chapter. The template is explicit that this is required, not optional.

## 5. Numbering rules

- The **chapter number sits furthest right**: section 2 of chapter 1 appears as
  `-2.1`. Verified by rendering the `.dotx` itself to PDF and comparing.
  **Deviation:** the leading dash is dropped in this report — headings render as
  `2.1`, not `-2.1`. See the note above the `\titleformat` block in
  `preamble.tex`; restoring it is a one-character change on three lines.
- **In Word this is manual and written in reverse**, because Word's automatic
  multi-level numbering comes out backwards in Arabic — the template devotes a
  whole section to the problem and asks students to number by hand, last, after
  the text is final.
- **In LaTeX the defaults are already correct, and must be left alone.** Under
  bidi the period between digits is a neutral, so `1.2` splits into two runs
  ordered right-to-left and renders as `2.1`. LaTeX's default
  `\thesection` = `\thechapter.\arabic{section}` therefore produces exactly the
  template's appearance. Redefining it to `section.chapter` double-reverses and
  renders `1.2`, which is wrong — this was caught by visual comparison, not by
  the compiler.
- Maximum depth is **4 levels** (chapter + 3).
- Contents lists **2 levels** deep.
- **Figures, tables, and equations are numbered sequentially across the whole
  report** — never `Figure 3-2`. The chapter number must not appear in them.
- Appendices are lettered `آ`, `ب`, …

## 6. Figures, tables, equations

- Figure caption goes **below** the figure; table caption goes **above** the table.
- Figures are centred on their own line (`شكل موسط`); never text-wrapped left or right.
- Every figure and table must be **cited by number in the body text**, and commented
  on — the template is emphatic that a figure must never be left to speak for itself.
- Captions must be self-contained: not "Data rate" but "Data rate against
  transmitter–receiver distance, for a transmit power of 1 W".
- Similar figures must share size, axes, grid, and line style.
- Only numbered equations are those cited from elsewhere in the text.

## 7. House rules from the guide

- **Black only.** No colour in text or headings; colour is allowed inside figures
  where it carries meaning.
- **No blank lines** for vertical spacing — the styles already carry it.
- **Maximum sentence length ≈ 30–40 words** (about two lines). The guide devotes a
  whole section to over-long sentences.
- Prefer verbal sentences (جملة فعلية) over nominal ones; do not translate English
  sentence structure literally.
- No space before a punctuation mark, one space after. No space after the
  conjunction `و`.
- Define every abbreviation on first use in the body even if it is in the
  abbreviations list; do not invent new abbreviations.
- **Length is not a virtue.** The guide explicitly warns against padding — inflated
  fonts, margins, figure sizes, blank lines, and filler appendices.
- References numbered by **order of first citation**, `[1]`. Every listed reference
  must be cited in the text. Avoid relying on web links.

## 8. Current draft status

The Word draft has been migrated into `main.tex` and its `chapters/`. Latest clean
build: **160 pages, 72 figures, 37 tables, 0 errors, 0 missing glyphs**.

| Chapter | State |
| --- | --- |
| 1 — الإطار العام للمشروع | written |
| 2 — الدراسة النظرية | written |
| 3 — الأعمال المشابهة | written |
| 4 — تحليل المتطلبات | written (largest — use cases, narratives, SSDs) |
| 5 — تصميم النظام | written — overview, then one section per service |
| 6 — التنفيذ والاختبار | written |

The formatting gaps the Word draft had against this spec are closed: numbering now
uses the template form, numerals are Western (`numerals=maghrib`), and the front
matter skeleton (§3 items 1–12) exists.

Remaining work, all content rather than formatting:

- Front matter prose is still placeholder text — abstract (both languages),
  dedication, acknowledgements, and the general introduction.
- The cover page needs department, academic year, degree wording and supervisor
  confirmed; `main.tex` carries a `TODO(jafar)` marking the spot.
- SSDs 09–31 from the chapter 4 narratives are not yet drawn.
- 35 design-level sequence diagrams inherited from the draft are still referenced
  in chapter 4; whether they stay there, move to chapter 5, or go is a question for
  the supervisor.
