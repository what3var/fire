; fire x86 package: the switch real mode -> 32-bit flat protected mode -> a routine -> back. Copied to a paragraph S:0, the C side (fire_x86_real.h) fills the header.
; Build: tools/build_stubs.sh (nasm) writes the bytes into fire_x86_stubs.h.
BITS 16
ORG 0
        jmp short start
        times 4-($-$$) db 0
base    dd 0                    ; 4  linear address of the stub (S*16)
code    dd 0                    ; 8  linear address of the 32-bit routine
regs    times 6 dd 0            ; 12 eax ebx ecx edx esi edi (in and out)
stack   dd 0                    ; 36 linear top of the stack for the routine
savess  dw 0                    ; 40
savesp  dw 0                    ; 42
        dd 0                    ; 44
gdt:    dq 0                    ; 48
        dq 0x00CF9A000000FFFF   ; 56 08: code, 32 bit, flat
        dq 0x00CF92000000FFFF   ; 64 10: data, 32 bit, flat
        dq 0x00009A000000FFFF   ; 72 18: code, 16 bit, base = the stub
        dq 0x000092000000FFFF   ; 80 20: data, 16 bit, base = the stub
gdtr:   dw 39                   ; 88
        dd gdt                  ; 90 linear of gdt (the C side adds base)
        dw 0
idtrm:  dw 0x3FF                ; 96 the vectors of the real mode
        dd 0
        dw 0
idtpm:  dw 0                    ; 104 none: interrupts stay off
        dd 0
        dw 0
pm32p:  dd pm32                 ; 112 linear of pm32 (+ base)
        dw 0x08
rmp:    dw rm                   ; 118
        dw 0                    ; 120 S
start:
        pushf
        cli
        pushad
        push ds
        push es
        push fs
        push gs
        mov ax, cs
        mov ds, ax
        mov [savess], ss
        mov [savesp], sp
        in al, 0x70
        or al, 0x80             ; NMI off
        out 0x70, al
        mov ebp, [base]
        lgdt [gdtr]
        lidt [idtpm]
        mov eax, cr0
        or al, 1
        mov cr0, eax
        jmp far dword [pm32p]
BITS 32
pm32:
        mov ax, 0x10
        mov ds, ax
        mov es, ax
        mov fs, ax
        mov gs, ax
        mov ss, ax
        mov esp, [ebp + 36]
        push ebp
        mov eax, [ebp + 12]
        mov ebx, [ebp + 16]
        mov ecx, [ebp + 20]
        mov edx, [ebp + 24]
        mov esi, [ebp + 28]
        mov edi, [ebp + 32]
        cld
        call dword [ebp + 8]
        xchg ebp, [esp]
        mov [ebp + 12], eax
        mov [ebp + 16], ebx
        mov [ebp + 20], ecx
        mov [ebp + 24], edx
        mov [ebp + 28], esi
        mov [ebp + 32], edi
        pop eax
        jmp 0x18:pm16
BITS 16
pm16:
        mov ax, 0x20
        mov ds, ax
        mov es, ax
        mov fs, ax
        mov gs, ax
        mov ss, ax
        mov eax, cr0
        and al, 0xFE
        mov cr0, eax
        jmp far [rmp]
rm:
        mov ax, cs
        mov ds, ax
        mov ss, [savess]
        mov sp, [savesp]
        lidt [idtrm]
        in al, 0x70
        and al, 0x7F
        out 0x70, al
        pop gs
        pop fs
        pop es
        pop ds
        popad
        popf
        retf
