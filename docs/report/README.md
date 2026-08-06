# Building the report PDF

The report is written in LaTeX and typeset with **XeLaTeX** (pdfLaTeX cannot shape
Arabic). Formatting follows the institute's template — see [STRUCTURE.md](STRUCTURE.md).

## Build it

```bash
cd ~/Development/Repositories/IntelliLect/docs/report && latexmk -xelatex main.tex
```

Output is `main.pdf` in this directory (~15 MB). `latexmk` runs XeLaTeX as many
times as the contents, figure list, and cross-references need — usually three
passes.

**A clean build takes about two minutes** — the 26 MB of figures dominate, and
each pass re-embeds them. Once built, `latexmk` only redoes what changed, so
rebuilding after editing a chapter takes seconds and a no-op rebuild is instant.
Don't kill it thinking it has hung.

Open it:

```bash
xdg-open ~/Development/Repositories/IntelliLect/docs/report/main.pdf
```

Build and open in one go:

```bash
cd ~/Development/Repositories/IntelliLect/docs/report && latexmk -xelatex main.tex && xdg-open main.pdf
```

## Rebuild from scratch

Stale `.aux` files cause wrong page numbers and `??` cross-references. When output
looks stale, wipe and rebuild:

```bash
cd ~/Development/Repositories/IntelliLect/docs/report && latexmk -C && latexmk -xelatex main.tex
```

`latexmk -C` deletes the PDF too; `latexmk -c` keeps it and removes only the
intermediates.

## Rebuild automatically while writing

Recompiles on every save — useful with a PDF viewer that reloads on change
(Okular, Evince, Zathura):

```bash
cd ~/Development/Repositories/IntelliLect/docs/report && latexmk -xelatex -pvc main.tex
```

Stop it with `Ctrl-C`.

## Check a build actually succeeded

`latexmk` can exit non-zero while still producing a PDF, and a missing glyph is
silent. This reports both:

```bash
cd ~/Development/Repositories/IntelliLect/docs/report && \
  grep -E "^! " main.log | sort -u; \
  echo "missing glyphs: $(grep -c 'Missing character' main.log)"; \
  pdfinfo main.pdf | grep -E "^Pages|Page size"
```

A healthy build prints no `!` lines, `missing glyphs: 0`, and `Page size: 612 x 792
pts (letter)`.

## Requirements

TeX packages — installed already on this machine:

```bash
sudo apt install -y texlive-xetex texlive-lang-arabic texlive-latex-extra \
                    texlive-fonts-recommended latexmk
```

Fonts — the template mandates these, and **they are not installed by any apt
package**. They currently live in `~/.local/share/fonts` on this machine, so a
fresh clone on another machine will fail until they are copied there and
`fc-cache -f` is run:

| Font | Used for |
| --- | --- |
| Traditional Arabic | all Arabic text |
| Times New Roman | embedded Latin text |
| DejaVu Sans Mono | code listings |

Verify all three resolve before blaming the build:

```bash
for f in "Traditional Arabic" "Times New Roman" "DejaVu Sans Mono"; do \
  printf '%-20s %s\n' "$f" "$(fc-match "$f")"; done
```

If `fc-match` falls back to a different family, XeLaTeX will silently substitute
and the Arabic will look wrong rather than error.

## Diagrams

Diagrams are written as text in [diagrams/](diagrams/) and rendered to 300 DPI PNGs
in `figures/` by PlantUML. Rebuild them with:

```bash
cd ~/Development/Repositories/IntelliLect/docs/report/diagrams && ./build.sh
```

Only diagrams whose source changed are re-rendered. Force everything with
`./build.sh --all` — needed after editing `_style.puml`, though the script already
detects that.

The script needs **Java only** (already installed). On first run it downloads
`plantuml.jar` into `tools/`, which is gitignored — it is 29 MB and not ours to
vendor. Graphviz is **not** required: `_style.puml` sets `!pragma layout smetana`,
PlantUML's pure-Java layout engine.

Every diagram starts with `!include _style.puml`, so fonts, colours and shapes are
defined in exactly one place. Change that file and the whole set follows.

### Do not export PDF directly

`plantuml -tpdf` **silently drops every Arabic glyph**. Shapes render, labels
vanish, no error, and the output looks plausible by file size and page count.
Verified. Use PNG (what `build.sh` does) or SVG.

### Never bold Arabic text

`**...**` inside an Arabic sentence **scrambles the word order**. The bold span
becomes its own bidi run, so PlantUML reorders it against the surrounding text:

```
في **صندوق الصادر** ضمن المعاملة نفسها     ← source
ضمن المعاملة نفسها**صندوق الصادر**في       ← what actually renders
```

No error, no warning — it simply comes out as nonsense to an Arabic reader.
Use the report's guillemets `«...»` for emphasis instead; they are ordinary
characters and stay inside the run. Bold is safe only for a Latin-only label
that is separated from any Arabic by `\n`, as in `**WebRTC**\n(لا يمر عبر البوابة)`.

### Keep diagrams close to square

Check the aspect ratio after rendering:

```bash
cd ~/Development/Repositories/IntelliLect/docs/report/figures && python3 -c "
import glob,struct
for f in sorted(glob.glob('*.png')):
    d=open(f,'rb').read(24); w,h=struct.unpack('>II',d[16:24])
    print(f'{f:40s} {w:5d}x{h:<5d} ratio={w/h:.2f}')"
```

Anything wider than about **2.2:1** shrinks to an unreadable strip once fitted
to the page width, and anything narrower than **0.35:1** goes too thin when
fitted to the page height. Two fixes, in order of preference:

1. **Cut arrows.** A component diagram should show structure, not every call.
   Drawing all ~25 edges made the layers overlap and read diagonally; one
   representative edge per layer boundary plus every outward edge reads far
   better and renders near-square.
2. **Split by phase.** A tall flowchart usually has a natural seam — the
   two-factor login was split at its two HTTP requests into `flow-01a`/`flow-01b`.

Note that smetana lays *nested packages* badly: a `package` per layer with 5-6
components inside overlapped its neighbours. The `comp-*.puml` diagrams instead
give each layer **one box that lists its components**, breaking a component out
only when an outward arrow lands on it. See the header comment in
`comp-01-user-management.puml`.

### Naming

`uc-NN-*` use cases, `ssd-NN-*` system sequence diagrams, `erd-NN-*` entity
relations, `comp-NN-*` per-service component diagrams, `flow-NN-*` and `arch-NN-*`
flowcharts, `class-NN-*` class diagrams, `pattern-NN-*` design patterns. The
rendered PNG takes the same stem, so
`\includegraphics{figures/uc-01-unregistered.png}` pairs with
`diagrams/uc-01-unregistered.puml`.

## Layout

```
docs/report/
├── main.tex          document skeleton: front matter, chapters, back matter
├── preamble.tex      all formatting — implements STRUCTURE.md
├── STRUCTURE.md      the template spec we follow
├── chapters/         one file per chapter, plus per-section files
├── front/            abstract, dedication, acknowledgements, references
└── figures/          images extracted from the Word draft
```

Edit chapters individually — a broken chapter fails on its own rather than taking
the build down, and the diffs stay reviewable.

A long chapter splits one level further: the chapter file keeps `\chapter`, the
`\section` headings and their `\label`s, and pulls each section's body from
`chapters/<NN>-<n>-<slug>.tex` — chapter number, section number, English slug.
Chapter 5 is fully split this way, chapter 1 partly. The heading stays with the
chapter so the chapter file alone still shows the outline; keep `\label`
directly under its `\section` there, since a label in the included file would
attach to whatever heading precedes it.

## When it breaks

**`! Package bidi Error: Oops! you have loaded package X after bidi package.`**
`polyglossia` loads `bidi`, which refuses most packages loaded after it. Move the
`\usepackage` **above** `\usepackage{polyglossia}` in `preamble.tex`.

**`! Emergency stop. ... no legal \end found`**
An environment was left open. The usual cause is wrapping `lstlisting` in a custom
environment: `listings` reads its body verbatim, so the wrapper never closes.
Write listings out in full instead:

```latex
\begin{english}
\begin{lstlisting}[language=Java]
...
\end{lstlisting}
\end{english}
```

**`Latexmk: First line of .log file 'main.log' is not in standard format.`**
The log was left corrupt by a build that was interrupted part-way — usually
because a slow clean build was cancelled. `latexmk` then refuses to parse it and
exits non-zero even though XeLaTeX itself may have succeeded. Clear it and
rebuild:

```bash
cd ~/Development/Repositories/IntelliLect/docs/report && latexmk -C && latexmk -xelatex main.tex
```

**Arabic renders as boxes, or Latin text appears in the wrong font**
A font failed to resolve. Run the `fc-match` check above.

**Section numbers appear as `1.2` instead of `2.1`**
Something redefined `\thesection`. The defaults are correct — bidi reverses
period-separated digit groups on its own. See STRUCTURE.md §5.

**Latin words inside Arabic have their punctuation in the wrong place**
The term was not wrapped. Use `\en{WebRTC}` for every Latin-script term, and
`\code{SomeIdentifier}` for inline identifiers.
