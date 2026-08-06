#!/usr/bin/env bash
# Render every .puml in this directory to a 450 DPI PNG in ../figures/.
#
#   ./build.sh            # only diagrams whose source changed
#   ./build.sh --all      # force a full re-render
#
# Needs Java only. The PlantUML jar is downloaded on first run into tools/
# (gitignored — it is 29 MB and not ours to vendor).
#
# NOTE: do NOT switch this to `-tpdf`. PlantUML's direct PDF export silently
# drops every Arabic glyph: the shapes render and the labels vanish, with no
# error and a plausible-looking file. Verified. PNG and SVG are both fine.

set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$DIR/../figures"
TOOLS="$DIR/../tools"
JAR="$TOOLS/plantuml.jar"
URL="https://github.com/plantuml/plantuml/releases/latest/download/plantuml.jar"

command -v java >/dev/null || { echo "error: java not found (needed by PlantUML)" >&2; exit 1; }

if [[ ! -f "$JAR" ]]; then
  echo "PlantUML jar not present, downloading to $JAR ..."
  mkdir -p "$TOOLS"
  curl -fsSL -o "$JAR" "$URL" || { echo "error: download failed" >&2; exit 1; }
  echo "downloaded $(du -h "$JAR" | cut -f1)"
fi

mkdir -p "$OUT"
force="${1:-}"
rendered=0
skipped=0

for src in "$DIR"/*.puml; do
  name="$(basename "$src" .puml)"
  [[ "$name" == _* ]] && continue          # _*.puml are includes, not diagrams
  target="$OUT/$name.png"

  if [[ "$force" != "--all" && -f "$target" && "$target" -nt "$src" ]]; then
    # Staleness against the includes this diagram actually names, not just
    # _style.puml. Diagrams pulling in _pattern-style.puml were silently
    # skipped after it changed, and a skip here is invisible — the old PNG
    # stays and the report keeps using it.
    stale=0
    while read -r inc; do
      [[ -f "$DIR/$inc" ]] || continue
      [[ "$target" -nt "$DIR/$inc" ]] || { stale=1; break; }
    done < <(grep -oP '^\s*!include\s+\K\S+' "$src")
    if [[ $stale -eq 0 ]]; then
      skipped=$((skipped + 1))
      continue
    fi
  fi

  printf '  %-44s' "$name"
  # PLANTUML_LIMIT_SIZE defaults to 4096 px. Any diagram larger than that is
  # silently CLIPPED — no warning, no error, just a truncated PNG that looks
  # fine until you notice a table missing off the right edge. At 450 DPI the
  # ERDs and the block diagram all exceed it, so the limit is raised here.
  if java -DPLANTUML_LIMIT_SIZE=16384 -jar "$JAR" -tpng -Sdpi=450 -charset UTF-8 -o "$OUT" "$src" 2>/dev/null; then
    printf 'ok  (%s)\n' "$(du -h "$target" 2>/dev/null | cut -f1)"
    rendered=$((rendered + 1))
  else
    printf 'FAILED\n'
    exit 1
  fi
done

echo "rendered $rendered, up to date $skipped"
