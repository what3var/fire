; fire x86 package: a test of the two stubs without DOS (the long mode needs a 64-bit processor: QEMU). Boots, loads the stubs, the routines and the page tables
; to 1000:0, runs both, prints the registers to COM1 and ends QEMU (isa-debug-exit). Assembled with -i <folder of protected.bin and long.bin>; the test suite runs it.
BITS 16
ORG 0x7C00
        cli
        xor ax, ax
        mov ds, ax
        mov ss, ax
        mov sp, 0x7C00
        sti
        mov [drive], dl
        mov si, dap
        mov ah, 0x42
        int 0x13
        jc fail
        ; the protected-mode stub at 1000:0 (linear 10000h)
        mov ax, 0x1000
        mov es, ax
        mov dword [es:4], 0x10000
        mov dword [es:8], 0x12000
        mov dword [es:12], 0x120000        ; eax: above 1 MB
        mov dword [es:16], 1000000         ; ebx
        mov dword [es:20], 23              ; ecx
        mov dword [es:36], 0x13000
        mov byte [es:72+4], 1              ; descriptor bases 010000h
        mov byte [es:80+4], 1
        add dword [es:90], 0x10000
        add dword [es:112], 0x10000
        mov word [es:120], 0x1000
        call 0x1000:0
        mov ax, 0x1000
        mov es, ax
        mov eax, [es:12]
        call hex
        mov eax, [es:20]
        call hex
        ; the long-mode stub at 1100:0 (linear 11000h)
        mov ax, 0x1100
        mov es, ax
        mov dword [es:4], 0x11000
        mov dword [es:8], 0x12100
        mov dword [es:12], 7
        mov dword [es:36], 0x13800
        mov dword [es:44], 0x14000
        mov word [es:72+2], 0x1000         ; descriptor bases 011000h
        mov byte [es:72+4], 1
        mov word [es:80+2], 0x1000
        mov byte [es:80+4], 1
        add dword [es:98], 0x11000
        add dword [es:120], 0x11000
        mov word [es:128], 0x1100
        add dword [es:132], 0x11000
        add dword [es:140], 0x11000
        call 0x1100:0
        mov ax, 0x1100
        mov es, ax
        mov eax, [es:12]
        call hex
        mov eax, [es:16]
        call hex
        mov eax, [es:24]
        call hex
        mov al, 'O'
        call putc
        mov al, 'K'
        call putc
        mov al, 10
        call putc
        mov ax, 0
        jmp quit
fail:   mov al, 'F'
        call putc
quit:   mov dx, 0xF4                       ; isa-debug-exit
        out dx, al
        hlt
        jmp quit
hex:    mov cx, 8
.next:  rol eax, 4
        push eax
        and al, 15
        add al, '0'
        cmp al, '9'
        jbe .digit
        add al, 7
.digit: call putc
        pop eax
        loop .next
        mov al, ' '
        call putc
        ret
putc:   push dx
        push ax
        mov dx, 0x3FD
.wait:  in al, dx
        test al, 0x20
        jz .wait
        pop ax
        mov dx, 0x3F8
        out dx, al
        pop dx
        ret
drive:  db 0
dap:    db 16, 0
        dw 80                               ; sectors
        dw 0, 0x1000                        ; to 1000:0
        dq 1                                ; from LBA 1
        times 510-($-$$) db 0
        dw 0xAA55
; the image after the boot sector: linear 10000h on
pmstub: incbin "protected.bin"
        times 0x1000-($-pmstub) db 0
lmstub: incbin "long.bin"
        times 0x2000-($-pmstub) db 0
BITS 32                                     ; 12000h: the 32-bit routine
        mov [eax], ebx
        mov edx, [eax]
        add edx, ecx
        mov eax, edx
        mov ecx, 0x12345678
        ret
        times 0x2100-($-pmstub) db 0
BITS 64                                     ; 12100h: the 64-bit routine
        mov ecx, 0xC0000080
        rdmsr
        mov ebx, eax                        ; EFER: LME (100h) and LMA (400h)
        mov r8, 0x100000005                 ; a 64-bit register
        mov rax, r8
        shr rax, 32
        mov edx, eax                        ; 1
        mov eax, r8d                        ; 5
        ret
        times 0x4000-($-pmstub) db 0
pml4:   dq 0x15003                          ; 14000h
        times 0x5000-($-pmstub) db 0
pdpt:   dq 0x16003, 0x17003, 0x18003, 0x19003
        times 0x6000-($-pmstub) db 0
%assign i 0
%rep 2048
        dq (i * 0x200000) | 0x83
%assign i i+1
%endrep
