; fire x86 package: the switch real mode -> 32-bit protected mode -> long mode (64 bit, paging: the first 4 GB mapped 1:1 with pages of 2 MB) -> a routine -> back.
; Copied to a paragraph S:0, the C side (fire_x86_real.h) fills the header and builds the page tables (cr3).
; Build: tools/build_stubs.sh (nasm) writes the bytes into fire_x86_stubs.h.
BITS 16
ORG 0
        jmp near start
        times 4-($-$$) db 0
base    dd 0                    ; 4  linear address of the stub (S*16)
code    dd 0                    ; 8  linear address of the 64-bit routine (below 4 GB)
regs    times 6 dd 0            ; 12 rax rbx rcx rdx rsi rdi - the low 32 bits (in: zero-extended)
stack   dd 0                    ; 36 linear top of the stack for the routine
savess  dw 0                    ; 40
savesp  dw 0                    ; 42
pml4    dd 0                    ; 44 linear address of the PML4 (cr3)
gdt:    dq 0                    ; 48
        dq 0x00AF9A000000FFFF   ; 56 08: code, 64 bit
        dq 0x00CF92000000FFFF   ; 64 10: data, flat
        dq 0x00009A000000FFFF   ; 72 18: code, 16 bit, base = the stub
        dq 0x000092000000FFFF   ; 80 20: data, 16 bit, base = the stub
        dq 0x00CF9A000000FFFF   ; 88 28: code, 32 bit, flat
gdtr:   dw 47                   ; 96
        dd gdt                  ; 98 linear of gdt (+ base)
        dw 0
idtrm:  dw 0x3FF                ; 104
        dd 0
        dw 0
idtpm:  dw 0                    ; 112
        dd 0
        dw 0
pm32p:  dd pm32                 ; 120 (+ base)
        dw 0x28
rmp:    dw rm                   ; 126
        dw 0                    ; 128 S
        dw 0
lm64p:  dd lm64                 ; 132 (+ base)
        dw 0x08
        dw 0
back32p: dd back32              ; 140 (+ base)
        dw 0x28
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
pm32:                           ; protected mode, 32 bit, no paging
        mov ax, 0x10
        mov ds, ax
        mov es, ax
        mov fs, ax
        mov gs, ax
        mov ss, ax
        mov eax, cr4
        or eax, 0x20            ; PAE
        mov cr4, eax
        mov eax, [ebp + 44]
        mov cr3, eax
        mov ecx, 0xC0000080     ; EFER
        rdmsr
        or eax, 0x100           ; LME
        wrmsr
        mov eax, cr0
        or eax, 0x80000000      ; PG: long mode active (compatibility mode until the jump)
        mov cr0, eax
        jmp far dword [ebp + 132]
BITS 64
lm64:
        mov ax, 0x10
        mov ds, ax
        mov es, ax
        mov ss, ax
        mov esp, [rbp + 36]
        push rbp
        mov eax, [rbp + 12]
        mov ebx, [rbp + 16]
        mov ecx, [rbp + 20]
        mov edx, [rbp + 24]
        mov esi, [rbp + 28]
        mov edi, [rbp + 32]
        cld
        mov r11d, [rbp + 8]
        call r11
        xchg rbp, [rsp]
        mov [rbp + 12], eax
        mov [rbp + 16], ebx
        mov [rbp + 20], ecx
        mov [rbp + 24], edx
        mov [rbp + 28], esi
        mov [rbp + 32], edi
        pop rax
        jmp far dword [rbp + 140]
BITS 32
back32:                         ; compatibility mode: paging off leaves the long mode
        mov eax, cr0
        and eax, 0x7FFFFFFF
        mov cr0, eax
        mov ecx, 0xC0000080
        rdmsr
        and eax, ~0x100
        wrmsr
        mov eax, cr4
        and eax, ~0x20
        mov cr4, eax
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
