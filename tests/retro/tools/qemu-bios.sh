#!/bin/sh
# Boots the floppy image of a bios16 program in QEMU (no display, at most 120 s) and prints what it wrote to COM1.
#   qemu-bios.sh <image>
# The program ends through the port isa-debug-exit: QEMU then ends with (code << 1) | 1 - 1 is the end with code 0; anything else is printed.
img=$1
serial="$img.com1.txt"
rm -f "$serial"
timeout 120 qemu-system-i386 -display none -serial "file:$serial" -device isa-debug-exit,iobase=0xf4,iosize=1 \
    -drive "file=$img,format=raw,if=floppy" -boot a -no-reboot > "$img.qemu.log" 2>&1
code=$?
if [ -f "$serial" ]; then tr -d '\r' < "$serial"; else echo "(no output)"; cat "$img.qemu.log"; fi
if [ $code -eq 124 ]; then echo "(QEMU ended after 120 s)"; elif [ $code -ne 1 ]; then echo "(QEMU ended with $code)"; fi
exit 0
