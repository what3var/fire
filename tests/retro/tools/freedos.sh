#!/bin/sh
# Runs PROG.EXE of a folder under FreeDOS 1.4 in QEMU (a 64-bit processor, which DOSBox-X has not) and prints what it wrote (at most 120 s).
#   freedos.sh <folder>
# The image (FD14LITE.img of FD14-LiteUSB) is FIRE_FREEDOS_IMAGE (tools/setup-retro.sh --freedos puts it into ~/.cache/fire/freedos); mtools put the
# program on a copy of it. CONFIG without EMM386 (the processor stays in real mode), with HIMEMX; AUTOEXEC runs PROG.EXE > OUT.TXT and QUIT.COM (out to
# the isa-debug-exit of QEMU: B0 00 E6 F4 C3).
dir=$1
img="$dir/disk.img"
cp "$FIRE_FREEDOS_IMAGE" "$img" || exit 1
printf 'FILES=40\r\nBUFFERS=20\r\nDOS=HIGH\r\nDEVICE=\\FREEDOS\\BIN\\HIMEMX.EXE\r\nSHELL=\\COMMAND.COM \\ /E:1024 /P=\\FDAUTO.BAT\r\n' > "$dir/FDCONFIG.SYS"
printf '@echo off\r\nC:\r\ncd \\\r\nPROG.EXE > OUT.TXT\r\nQUIT.COM\r\n' > "$dir/FDAUTO.BAT"
printf '\260\000\346\364\303' > "$dir/QUIT.COM"
printf 'drive c: file="%s" offset=32256\nmtools_skip_check=1\n' "$img" > "$dir/mtoolsrc"
cd "$dir" || exit 1
MTOOLSRC="$dir/mtoolsrc" mcopy -o FDCONFIG.SYS FDAUTO.BAT QUIT.COM PROG.EXE c:/ || exit 1
timeout 120 qemu-system-x86_64 -drive "format=raw,file=$img" -display none -device isa-debug-exit,iobase=0xf4,iosize=0x04 -no-reboot -m 64 > qemu.log 2>&1
code=$?
MTOOLSRC="$dir/mtoolsrc" mtype c:/OUT.TXT 2> /dev/null | tr -d '\r' || echo "(no output)"
[ $code -eq 124 ] && echo "(QEMU ended after 120 s)"
exit 0
