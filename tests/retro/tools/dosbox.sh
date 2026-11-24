#!/bin/sh
# Runs C:\PROG.EXE of a folder in DOSBox-X (no window, no sound, at most 120 s) and prints what it wrote.
#   dosbox.sh <folder> [<result file>] [<options of DOSBox-X>...]
# Without a result file (or OUT.TXT) the program runs as `PROG.EXE > OUT.TXT`; another name: the program runs on the screen (a console program that reads the
# screen and the keyboard) and writes that file itself. The options may name files of the folder (`-set "serial1=file file:<folder>/SER.TXT"`).
dir=$1
out=${2:-OUT.TXT}
[ $# -ge 2 ] && shift 2 || shift 1
if [ "$out" = OUT.TXT ]; then run="PROG.EXE > OUT.TXT"; else run="PROG.EXE"; fi
rm -f "$dir/$out"
SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy timeout 120 dosbox-x -silent -nogui -nomenu -set "cpu cycles=max" "$@" \
    -c "mount c \"$dir\"" -c "c:" -c "$run" -c "exit" > "$dir/dosbox.log" 2>&1
code=$?
file=$(find "$dir" -maxdepth 1 -iname "$out" | head -n 1)
if [ -n "$file" ]; then tr -d '\r' < "$file"; else echo "(no output)"; cat "$dir/dosbox.log"; fi
[ $code -eq 124 ] && echo "(DOSBox-X ended after 120 s)"
exit 0
