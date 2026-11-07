; fire bios16 (ROADMAP 13.5): the boot sector of a FAT12 floppy of 1.44 MB. The toolchain driver carries it assembled (BiosBootSectors.Bios16; nasm -f bin) and
; writes the disk image (WatcomToolchain.AfterLink): this sector with its BIOS parameter block, the FATs, the root directory, FIRE.EXE as the first file in one
; piece, then the files of the program's `disk` folder. Finds FIRE.EXE in the first sector of the root directory, loads it (a DOS MZ executable) to LOADSEG,
; relocates it, clears the memory it asks for beyond its image (BSS) and jumps to its entry with its stack (BP = the first free paragraph behind it).
; Sectors are read one by one with CHS (int 13h AH=02h). FIRE.EXE must lie in one piece - the image is written so; a tool that moves it breaks the start.
bits 16
org 0x7C00
LOADSEG equ 0x0800
ROOTSEC equ 19              ; 1 reserved sector + 2 FATs of 9 sectors
DATASEC equ 31              ; the first data sector (33) minus the 2 clusters that are not on the disk
DIRSEG  equ 0x0050          ; a sector of the root directory is read to 0050:0000

    jmp short start
    nop
    db "FIRE    "           ; OEM name
bps     dw 512              ; bytes per sector
        db 1                ; sectors per cluster
        dw 1                ; reserved sectors
        db 2                ; FATs
        dw 224              ; entries of the root directory
        dw 2880             ; sectors
        db 0xF0             ; media
        dw 9                ; sectors per FAT
spt     dw 18               ; sectors per track
heads   dw 2                ; heads
        dd 0                ; hidden sectors
        dd 0                ; sectors (32 bits)
drive   db 0
        db 0
        db 0x29             ; extended boot signature
        dd 0x46495245       ; serial
        db "NO NAME    "    ; label (none: the root directory has no label entry)
        db "FAT12   "

start:
    cli
    xor ax, ax
    mov ds, ax
    mov ss, ax
    mov sp, 0x7C00
    sti
    cld
    mov [drive], dl
    ; FIRE.EXE in the first sector of the root directory: its first cluster
    mov ax, DIRSEG
    mov es, ax
    mov ax, ROOTSEC
    call readsec
    xor di, di
.find:
    mov si, name
    mov cx, 11
    push di
    repe cmpsb
    pop di
    je .found
    add di, 32
    cmp di, 512
    jb .find
    mov al, 'F'
    jmp fail
.found:
    mov ax, [es:di+26]
    add ax, DATASEC
    mov [lba], ax
    ; the first sector of the program: its header
    mov ax, LOADSEG
    mov es, ax
    mov ax, [lba]
    call readsec
    ; its size in sectors (pages of 512 bytes; the last one may be partial: the rest is read anyway)
    mov cx, [es:4]
    mov [left], cx
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
    div word [spt]                  ; ax = lba / spt, dx = lba % spt
    inc dx
    mov cl, dl              ; sector (1-based)
    xor dx, dx
    div word [heads]                  ; ax = cylinder, dx = head
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
    jnc rs_ok
    xor ax, ax
    int 0x13
    dec si
    jnz .try
    mov al, 'E'
fail:                       ; (AL: E a disk error, F no FIRE.EXE)
    mov ah, 0x0E
    int 0x10
halt:
    hlt
    jmp halt
rs_ok:
    pop ax
    ret

name  db "FIRE    EXE"
left  dw 0
lba   dw 0
dst   dw 0
base  dw 0
heap  dw 0
entry dw 0, 0

times 510-($-$$) db 0
dw 0xAA55
