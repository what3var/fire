/* fire dos package: DOS with a DOS extender (dos32; Open Watcom 386, DOS/32A) - an interrupt runs in real mode through DPMI (0300h: the registers of the real
 * mode, DS and ES as given); a pointer of the program has a segment:offset only when it lies in the first megabyte (DOS memory, the video memory). A buffer of
 * the program lies above it: for a function that wants one in DS:DX, take DOS memory (Dos.Alloc) and Machine.Memory to fill it. */
#ifndef FIRE_DOS_DPMI_H
#define FIRE_DOS_DPMI_H
#include "fire_dos_c.h"
#include <dos.h>
#include <i86.h>
#include <string.h>

static const char* fire_dos_unavailable(void) { return NULL; }

#pragma pack(push, 1)
typedef struct FireDosRmRegs {
    uint32_t edi, esi, ebp, reserved, ebx, edx, ecx, eax;
    uint16_t flags, es, ds, fs, gs, ip, cs, sp, ss;
} FireDosRmRegs;
#pragma pack(pop)

static void fire_dos_interrupt(int number, uint16_t r[9]) {
    FireDosRmRegs m;
    union REGS d;
    struct SREGS s;
    memset(&m, 0, sizeof m);
    m.eax = r[0]; m.ebx = r[1]; m.ecx = r[2]; m.edx = r[3]; m.esi = r[4]; m.edi = r[5];
    m.ds = r[6]; m.es = r[7];   /* (0: there is no data segment of the program in real mode - DOS gets 0) */
    memset(&d, 0, sizeof d);
    segread(&s);
    d.w.ax = 0x0300; d.h.bl = (unsigned char)number; d.h.bh = 0; d.w.cx = 0;
    d.x.edi = (unsigned)&m;
    int386x(0x31, &d, &d, &s);
    r[0] = (uint16_t)m.eax; r[1] = (uint16_t)m.ebx; r[2] = (uint16_t)m.ecx; r[3] = (uint16_t)m.edx; r[4] = (uint16_t)m.esi; r[5] = (uint16_t)m.edi;
    r[6] = m.ds; r[7] = m.es; r[8] = (uint16_t)(d.w.cflag ? 1 : (m.flags & 1));
}
/* a pointer as segment:offset - only in the first megabyte; else 0:0 */
static void fire_dos_farOf(const void* p, uint16_t* seg, uint16_t* off) {
    uint32_t linear = (uint32_t)p;
    if (linear >= 0x100000ul) { *seg = 0; *off = 0; return; }
    *seg = (uint16_t)(linear >> 4);
    *off = (uint16_t)(linear & 15);
}

#endif
