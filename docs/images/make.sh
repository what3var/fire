#!/bin/sh
# Makes the screenshots of the documentation: runs every program in src/ (except shot.fire) in the VM and lets it write its PNG here.
# Needs a display: under Linux without one it uses xvfb-run. The UI takes the system font; for the same look as the pictures in the
# repository use a KDE settings file with "font=NotoSans-Regular,10" (~/.config/kdeglobals) and XDG_CURRENT_DESKTOP=KDE.
# Usage: ./make.sh [name ...]   (FORGE=path/to/forge to use another compiler)
cd "$(dirname "$0")"
FORGE=${FORGE:-forge}
if [ $# -eq 0 ]; then set -- $(ls src/*.fire | xargs -n1 basename | sed 's/\.fire$//' | grep -v '^shot$'); fi
for name in "$@"; do
    echo "== $name"
    if [ -z "$DISPLAY" ] && command -v xvfb-run >/dev/null 2>&1; then xvfb-run -a -s "-screen 0 1600x1000x24" "$FORGE" run "src/$name.fire" || echo "FAILED: $name"
    else "$FORGE" run "src/$name.fire" || echo "FAILED: $name"; fi
done
