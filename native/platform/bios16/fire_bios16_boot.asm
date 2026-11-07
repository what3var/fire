; fire bios16 (ROADMAP 13.5): the boot sector. The toolchain driver carries it assembled (WatcomToolchain.Bios16BootSector; nasm -f bin) and writes the
; disk image: this sector, then the program from sector 1 on. Loads the program (a DOS MZ executable, stored from sector 1 on) to LOADSEG, relocates it, clears the memory it asks for
; beyond its image (BSS) and jumps to its entry with its stack. Sectors are read one by one with CHS (int 13h AH=02h) - floppies and hard disks alike.
bits 16
org 0x7C00
LOADSEG equ 0x0800

start:
    cli
    xor ax, ax
    mov ds, ax
    mov ss, ax
    mov sp, 0x7C00
    sti
    cld
    mov [drive], dl
    ; the geometry of the drive (a floppy whose BIOS does not answer: 18 sectors, 2 heads)
    mov ah, 0x08
    xor di, di
    mov es, di
    int 0x13
    jc .nogeo
    and cl, 0x3F
    mov [spt], cl
    inc dh
    mov [heads], dh
.nogeo:
    ; the first sector of the program: its header
    mov ax, LOADSEG
    mov es, ax
    mov ax, 1
    call readsec
    ; its size in sectors (pages of 512 bytes; the last one may be partial: the rest is read anyway)
    mov cx, [es:4]
    mov [left], cx
    mov word [lba], 1
    mov ax, LOADSEG
    mov [dst], ax
.load:
    dec word [left]
    jz .loaded
    add word [dst], 0x20
    inc word [lba]
    mov ax, [dst]
    mov es, ax
    mov ax, [lba]
    call readsec
    jmp .load
.loaded:
    mov ax, LOADSEG
    mov es, ax
    ; base = LOADSEG + header paragraphs
    mov bx, [es:8]
    add bx, LOADSEG
    mov [base], bx
    ; clear the memory the program asks for beyond its file (minalloc paragraphs from the end of the image)
    mov ax, [es:4]
    mov cl, 5
    shl ax, cl              ; pages * 32 = paragraphs of the file
    add ax, LOADSEG
    mov dx, [es:0x0A]       ; minalloc
.clear:
    or dx, dx
    jz .cleared
    push es
    mov es, ax
    xor di, di
    push ax
    xor ax, ax
    mov cx, 8
    rep stosw
    pop ax
    pop es
    inc ax
    dec dx
    jmp .clear
.cleared:
    mov [heap], ax          ; the first free paragraph behind the program
    ; relocations: count at [6], table at [0x18]
    mov cx, [es:6]
    mov si, [es:0x18]
.reloc:
    jcxz .relocated
    mov di, [es:si]         ; offset
    mov ax, [es:si+2]       ; segment
    add ax, [base]
    push es
    mov es, ax
    mov ax, [base]
    add [es:di], ax
    pop es
    add si, 4
    dec cx
    jmp .reloc
.relocated:
    mov ax, [es:0x14]
    mov [entry], ax
    mov ax, [es:0x16]
    add ax, [base]
    mov [entry+2], ax
    mov ax, [es:0x0E]
    add ax, [base]
    mov bx, [es:0x10]
    mov dl, [drive]
    mov bp, [heap]
    cli
    mov ss, ax
    mov sp, bx
    sti
    jmp far [cs:entry]

; reads the sector with the LBA in AX to ES:0
readsec:
    push ax
    xor dx, dx
    xor bh, bh
    mov bl, [spt]
    div bx                  ; ax = lba / spt, dx = lba % spt
    inc dx
    mov cl, dl              ; sector (1-based)
    xor dx, dx
    mov bl, [heads]
    div bx                  ; ax = cylinder, dx = head
    mov ch, al
    ror ah, 1
    ror ah, 1
    and ah, 0xC0
    or cl, ah               ; cylinder bits 8-9
    mov dh, dl
    mov dl, [drive]
    mov si, 3
.try:
    mov ax, 0x0201
    xor bx, bx
    int 0x13
    jnc .ok
    xor ax, ax
    int 0x13
    dec si
    jnz .try
    mov al, 'E'
    mov ah, 0x0E
    int 0x10
.halt:
    hlt
    jmp .halt
.ok:
    pop ax
    ret

drive db 0
spt   db 18
heads db 2
left  dw 0
lba   dw 0
dst   dw 0
base  dw 0
heap  dw 0
entry dw 0, 0

times 510-($-$$) db 0
dw 0xAA55
