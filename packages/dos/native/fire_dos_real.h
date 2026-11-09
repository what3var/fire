/* fire dos package: DOS in real mode (dos16; Open Watcom) - the interrupts through int86x, pointers as segment:offset. */
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

#endif
