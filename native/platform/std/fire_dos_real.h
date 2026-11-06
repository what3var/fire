/* fire native platform layer, DOS part for the real mode (dos16; Open Watcom): the interrupts through int86x, pointers as segment:offset, the XMS driver
 * through the entry point INT 2Fh AX=4310h gives (a far call with DS:SI). */
#ifndef FIRE_DOS_REAL_H
#define FIRE_DOS_REAL_H
#include "fire_dos_c.h"
#include <dos.h>
#include <i86.h>

static const char* fire_dos_unavailable(void) { return NULL; }

static void fire_dos_interrupt(int number, uint16_t r[9]) {
    union REGS regs;
    struct SREGS sregs;
    segread(&sregs);
    regs.x.ax = r[0]; regs.x.bx = r[1]; regs.x.cx = r[2]; regs.x.dx = r[3]; regs.x.si = r[4]; regs.x.di = r[5];
    if (r[6]) sregs.ds = r[6];
    if (r[7]) sregs.es = r[7];
    int86x(number, &regs, &regs, &sregs);
    r[0] = regs.x.ax; r[1] = regs.x.bx; r[2] = regs.x.cx; r[3] = regs.x.dx; r[4] = regs.x.si; r[5] = regs.x.di;
    r[6] = sregs.ds; r[7] = sregs.es; r[8] = (uint16_t)regs.x.cflag;
}
static void fire_dos_farOf(const void* p, uint16_t* seg, uint16_t* off) {
    uint32_t linear = ((uint32_t)FP_SEG(p) << 4) + FP_OFF(p);
    *seg = (uint16_t)(linear >> 4);
    *off = (uint16_t)(linear & 15);
}

static void (__far* fire_dos_xmsEntry)(void) = 0;
static int fire_dos_xmsState = -1;
static int fire_dos_xmsInit(void) {
    union REGS r;
    struct SREGS s;
    if (fire_dos_xmsState >= 0) return fire_dos_xmsState;
    r.x.ax = 0x4300;
    int86(0x2F, &r, &r);
    if (r.h.al != 0x80) return fire_dos_xmsState = 0;
    r.x.ax = 0x4310;
    segread(&s);
    int86x(0x2F, &r, &r, &s);
    fire_dos_xmsEntry = (void (__far*)(void))MK_FP(s.es, r.x.bx);
    return fire_dos_xmsState = 1;
}
static uint16_t fire_dos_xmsCall(int func, uint16_t dxIn, const void* dsSi, uint16_t* bx, uint16_t* dxOut) {
    void (__far* entry)(void) = fire_dos_xmsEntry;   /* (a local: reached through SS:BP while DS points elsewhere) */
    uint16_t pseg = FP_SEG(dsSi), poff = FP_OFF(dsSi), fn = (uint16_t)func, rax, rbx, rdx;
    __asm {
        push ds
        push si
        mov bx, pseg
        mov ds, bx
        mov si, poff
        mov dx, dxIn
        mov ax, fn
        mov ah, al
        call dword ptr entry
        mov rax, ax
        mov rbx, bx
        mov rdx, dx
        pop si
        pop ds
    }
    if (bx) *bx = rbx;
    if (dxOut) *dxOut = rdx;
    return rax;
}

#endif
