; fire bios16 (ROADMAP 13.5): the entry of the program. The boot sector (fire_bios16_boot.asm) has loaded and relocated the program (a DOS executable of
; Open Watcom, huge model) and jumps here with BP = the first free paragraph behind it. This sets up DGROUP and the stack, clears the BSS, gives the C library
; a program segment prefix and an environment as DOS would (empty), serves int 21h itself (fire_bios_int21: the console, version, time, vectors, the end -
; fire_bios16_libc.h), runs the init routines of the C library and calls fire_bios_cmain. Assembled with wasm (Open Watcom) by the toolchain driver.
.8086
_TEXT     segment word public 'CODE'
_TEXT     ends
_NULL     segment para public 'BEGDATA'
__nullarea label word
          db 16 dup(0)
_NULL     ends
_AFTERNULL segment word public 'BEGDATA'
_AFTERNULL ends
CONST     segment word public 'DATA'
CONST     ends
CONST2    segment word public 'DATA'
CONST2    ends
_DATA     segment word public 'DATA'
_fire_bios_heapseg dw 0    ; the first free paragraph behind the program (the boot sector says it in BP)
_DATA     ends
_BSS      segment word public 'BSS'
_BSS      ends
; a program segment prefix and an environment as DOS would give them: the C library reads its arguments and environment there (none; the program is A:\FIRE.EXE)
FAKEPSP   segment para public 'DATA'
__fakepsp label byte
          db 0CDh, 20h, 0, 0A0h
          db 40 dup(0)
          dw 0                     ; 2Ch: segment of the environment (set at the start)
          db (80h - 2Eh) dup(0)
          db 0, 0Dh                ; 80h: an empty command tail
          db (100h - 82h) dup(0)
__fakeenv label byte
          db 0, 0                  ; no variables
          dw 1
          db 'A:\FIRE.EXE', 0
          db 13 dup(0)
FAKEPSP   ends
STACK     segment para stack 'STACK'
__bss_end label byte
          db 32768 dup(?)
__stack_top label byte
STACK     ends
DGROUP group _NULL,_AFTERNULL,CONST,CONST2,_DATA,FAKEPSP,_BSS,STACK

extrn fire_bios_cmain_:far
extrn fire_bios_int21_:far
extrn __InitRtns:near
extrn __FiniRtns:near
extrn __psp:word
extrn fire_bios_exit_:far
extrn fire_bios_exitmsg_:far
public _fire_bios_heapseg
public _cstart_, __exit_, __do_exit_with_msg_, __GETDS, __nullarea
public __bss_begin, __bss_end

_BSS      segment word public 'BSS'
__bss_begin label byte
_BSS      ends
_TEXT     segment word public 'CODE'
          assume cs:_TEXT, ds:DGROUP
_cstart_  proc far
          cli
          mov ax, DGROUP
          mov ds, ax
          mov _fire_bios_heapseg, bp
          mov es, ax
          mov ss, ax
          mov sp, offset DGROUP:__stack_top
          sti
          cld
          ; clear the BSS
          mov di, offset DGROUP:__bss_begin
          mov cx, offset DGROUP:__bss_end
          sub cx, di
          xor ax, ax
          rep stosb
          ; the fake PSP: its paragraph, and the one of the environment behind it
          mov ax, offset DGROUP:__fakepsp
          mov cl, 4
          shr ax, cl
          mov bx, ds
          add ax, bx
          mov __psp, ax
          mov dx, offset DGROUP:__fakeenv
          shr dx, cl
          add dx, bx
          mov es, ax
          mov es:[2Ch], dx
          push ds
          pop es
          ; int 21h: the services of DOS that the C library uses, served by the program itself (fire_bios_int21)
          xor ax, ax
          mov es, ax
          cli
          mov word ptr es:[21h*4], offset fire_int21
          mov word ptr es:[21h*4+2], cs
          sti
          push ds
          pop es
          ; the init routines of the C library (stdio, 8087, arguments, environment, the formatting of floats)
          mov ax, 255
          call __InitRtns
          call fire_bios_cmain_
halt:     cli
          hlt
          jmp halt
_cstart_  endp

; the ends of the C library (exit, abort, a fatal message): AX = code (DX:AX = the message, BX = code)
__exit_   proc far
          push ax
          mov ax, DGROUP
          mov ds, ax
          pop ax
          call fire_bios_exit_
          jmp halt
__exit_   endp
__do_exit_with_msg_ proc far
          push ax
          mov ax, DGROUP
          mov ds, ax
          pop ax
          call fire_bios_exitmsg_
          jmp halt
__do_exit_with_msg_ endp
; int 21h: the registers go to fire_bios_int21 (a far pointer to them in DX:AX) as this frame: AX BX CX DX SI DI BP DS ES FLAGS; the carry of the result goes
; into the flags the caller gets back
fire_int21 proc far
          push es
          push ds
          push bp
          push di
          push si
          push dx
          push cx
          push bx
          push ax
          mov bp, sp
          mov ax, [bp+22]          ; the caller's flags (IRET frame: IP CS FLAGS above the 9 words)
          push ax                  ; frame word 9: flags
          ; the frame: 10 words at SS:SP
          mov ax, sp
          mov dx, ss
          push ax
          mov ax, DGROUP
          mov ds, ax
          pop ax
          call fire_bios_int21_
          pop ax                   ; flags as the handler left them
          mov bp, sp
          mov bx, [bp+22]          ; the caller's flags
          and bx, 0FFFEh
          and ax, 1
          or bx, ax
          mov [bp+22], bx
          pop ax
          pop bx
          pop cx
          pop dx
          pop si
          pop di
          pop bp
          pop ds
          pop es
          iret
fire_int21 endp

; DS = DGROUP, nothing else changes
__GETDS   proc near
          push ax
          mov ax, DGROUP
          mov ds, ax
          pop ax
          ret
__GETDS   endp
_TEXT     ends
          end _cstart_
