/* fire xms package: the XMS driver in real mode (dos16; Open Watcom) - its entry point from INT 2Fh AX=4310h, called far with DS:SI. */
#ifndef FIRE_XMS_REAL_H
#define FIRE_XMS_REAL_H
#include "fire_xms_c.h"
#include <dos.h>
#include <i86.h>

static const char* fire_xms_unavailable(void) { return NULL; }
static uint32_t fire_xms_far(const void* p) {
    uint32_t linear = ((uint32_t)FP_SEG(p) << 4) + FP_OFF(p);
    return ((linear >> 4) << 16) | (linear & 15);
}
static void (__far* fire_xms_entry)(void) = 0;
static int fire_xms_state = -1;
static int fire_xms_init(void) {
    union REGS r;
    struct SREGS s;
    if (fire_xms_state >= 0) return fire_xms_state;
    r.x.ax = 0x4300;
    int86(0x2F, &r, &r);
    if (r.h.al != 0x80) return fire_xms_state = 0;
    r.x.ax = 0x4310;
    segread(&s);
    int86x(0x2F, &r, &r, &s);
    fire_xms_entry = (void (__far*)(void))MK_FP(s.es, r.x.bx);
    return fire_xms_state = 1;
}
static uint16_t fire_xms_call(int func, uint16_t dxIn, const void* dsSi, uint16_t* bx, uint16_t* dxOut) {
    void (__far* entry)(void) = fire_xms_entry;   /* (a local: reached through SS:BP while DS points elsewhere) */
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
