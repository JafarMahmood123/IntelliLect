#!/usr/bin/env bash
# Convert the report to main.docx via pandoc.
#
#   ./make-docx.sh              # whole report
#   ./make-docx.sh chapters/05-2-design-patterns.tex   # one file
#
# Two things pandoc gets wrong on its own, both handled here:
#
#   1. Direction. Pandoc's docx writer emits no w:bidi/w:rtl, so an Arabic
#      document opens left-to-right in Word: paragraphs align left and mixed
#      Arabic/English lines come out in the wrong visual order. Fixed by
#      patching a reference.docx so RTL is the document default.
#
#   2. Cross-references. \ref{fig:pattern-01} becomes the literal string
#      "fig:pattern-01", not "56". Fixed by substituting the numbers LaTeX
#      already resolved into main.aux, so main.aux must be current — run
#      latexmk first.
#
# This is a ONE-WAY export. Edits made in the .docx do not come back; the .tex
# files stay the source of truth for the PDF.

set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

SRC="${1:-main.tex}"
OUT="${2:-$(basename "${SRC%.tex}").docx}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

[[ -f main.aux ]] || { echo "error: main.aux missing — run 'latexmk -xelatex main.tex' first" >&2; exit 1; }

# --- 1. label -> number map from main.aux, applied to a throwaway copy -------
cp -r chapters front figures preamble.tex main.tex "$WORK"/ 2>/dev/null || true

python3 - "$WORK" <<'PY'
import re, sys, pathlib

work = pathlib.Path(sys.argv[1])
aux = pathlib.Path('main.aux').read_text(encoding='utf-8')

# \newlabel{key}{{number}{page}...}
labels = dict(re.findall(r'\\newlabel\{([^}]+)\}\{\{([^}]*)\}', aux))

def sub(m):
    return labels.get(m.group(1), m.group(1))

def number_captions(text):
    """Bake 'الشكل N–' / 'الجدول N–' into each caption.

    Pandoc writes captions with no number but resolves \\ref to its own
    chapter-relative count, so left alone the body says 5.2 and the caption
    below the figure says nothing. Both sides get LaTeX's number instead.
    """
    def fix(env, word):
        def repl(m):
            block = m.group(0)
            lab = re.search(r'\\label\{([^}]+)\}', block)
            if not lab:
                return block
            num = labels.get(lab.group(1))
            if not num:
                return block
            return block.replace(r'\caption{', '\\caption{%s %s– ' % (word, num), 1)
        return lambda t: re.sub(r'\\begin\{%s\}.*?\\end\{%s\}' % (env, env),
                                repl, t, flags=re.S)
    text = fix('figure', 'الشكل')(text)
    text = fix('table', 'الجدول')(text)
    return text

n = 0
for f in list(work.rglob('*.tex')):
    t = f.read_text(encoding='utf-8')
    t2 = number_captions(re.sub(r'\\ref\{([^}]+)\}', sub, t))
    if t2 != t:
        f.write_text(t2, encoding='utf-8')
        n += 1
print(f'  resolved cross-references and numbered captions in {n} files '
      f'({len(labels)} labels known)')
PY

# --- 2. reference.docx with RTL as the document default ---------------------
REF="$WORK/reference.docx"
pandoc --print-default-data-file reference.docx > "$REF"

python3 - "$REF" <<'PY'
import sys, zipfile, shutil, re, pathlib

ref = pathlib.Path(sys.argv[1])
tmp = ref.with_suffix('.patched.docx')

with zipfile.ZipFile(ref) as zin, zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED) as zout:
    for item in zin.infolist():
        data = zin.read(item.filename)
        if item.filename == 'word/styles.xml':
            x = data.decode('utf-8')
            # Document-wide defaults: RTL paragraphs, RTL runs, Arabic font.
            # Regex, not str.replace: pandoc's reference.docx has whitespace
            # between <w:pPrDefault> and <w:pPr>, and either may be self-closing.
            x = re.sub(r'<w:pPrDefault>\s*<w:pPr>', '<w:pPrDefault><w:pPr><w:bidi/>', x, count=1)
            x = re.sub(r'<w:pPrDefault\s*/>', '<w:pPrDefault><w:pPr><w:bidi/></w:pPr></w:pPrDefault>', x, count=1)
            x = re.sub(r'<w:rPrDefault>\s*<w:rPr>', '<w:rPrDefault><w:rPr><w:rtl/>', x, count=1)
            x = re.sub(r'<w:rPrDefault\s*/>', '<w:rPrDefault><w:rPr><w:rtl/></w:rPr></w:rPrDefault>', x, count=1)
            x = re.sub(r'w:cs="[^"]*"', 'w:cs="Traditional Arabic"', x)
            # Every named style that carries a <w:pPr> gets bidi too, so
            # headings and captions do not fall back to LTR.
            x = x.replace('<w:pPr>', '<w:pPr><w:bidi/>')
            x = x.replace('<w:pPr><w:bidi/><w:bidi/>', '<w:pPr><w:bidi/>')
            data = x.encode('utf-8')
        zout.writestr(item, data)

shutil.move(tmp, ref)
print('  reference.docx patched for RTL')
PY

# --- 3. convert --------------------------------------------------------------
# Run from $WORK. pandoc resolves \input relative to the CURRENT DIRECTORY, not
# to the input file and not via --resource-path; started from $DIR it silently
# reads the original unpatched chapters and the substitutions above do nothing.
OUT_ABS="$DIR/$OUT"
( cd "$WORK" && pandoc "$SRC" \
    -o "$OUT_ABS" \
    --reference-doc="$REF" \
    --resource-path=".:figures" \
    --standalone \
    --toc )

printf 'wrote %s (%s)\n' "$OUT" "$(du -h "$OUT" | cut -f1)"
