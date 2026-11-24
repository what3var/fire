#!/bin/sh
# assembles the stubs of the package x86 and the boot sector that runs them, boots it in QEMU and prints what it wrote to COM1
tools=$1; out=$2
nasm -f bin "$tools/x86_protected.asm" -o "$out/protected.bin" || exit 1
nasm -f bin "$tools/x86_long.asm" -o "$out/long.bin" || exit 1
nasm -f bin -i "$out/" "$tools/test_boot.asm" -o "$out/boot.img" || exit 1
truncate -s '>65536' "$out/boot.img"
rm -f "$out/serial.txt"
timeout 60 qemu-system-x86_64 -drive "format=raw,file=$out/boot.img" -serial "file:$out/serial.txt" -display none -device isa-debug-exit,iobase=0xf4,iosize=0x04 -no-reboot > /dev/null 2>&1
tr -d '\r' < "$out/serial.txt" | sed -e 's/[[:space:]]*$//' -e '/^$/d'
