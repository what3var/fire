/* fire native platform layer, a helper for the DOS files of the packages (console, the display of windows, devices, machine): the BIOS and the first megabyte
 * from a DOS program, the same calls in real mode (dos16: int86, far pointers segment:offset) and with a DOS extender (dos32: flat memory - the extender maps the
 * first megabyte to its linear addresses and passes interrupts that only take registers down to the real mode; one with a buffer goes through DPMI).
 *   FIRE_DOS_FAR                    __far in real mode, nothing in flat memory (a pointer into the first megabyte)
 *   FIRE_DOS_LOW(seg, off)          a pointer to segment:offset of the real mode (unsigned char FIRE_DOS_FAR*)
 *   FIRE_DOS_COPY(to, from, n)      copies to / from such a pointer
 *   FIRE_DOS_W                      the member of union REGS with the 16-bit registers (r.FIRE_DOS_W.ax)
 *   fire_dos_int(n, r)              INT n with the registers of r (in and out), no segment registers
 *   fire_dos_lowBuffer(&seg)        a buffer of 1024 bytes below 1 MB (segment in seg, offset 0) for an interrupt that wants one in ES:DI and the like
 *   fire_dos_realInt(n, r, es)      INT n in real mode with the registers of r and ES = es (DPMI 0300h with an extender)
 */
#ifndef FIRE_DOS_BIOS_H
#define FIRE_DOS_BIOS_H
#include <dos.h>
#include <i86.h>
#include <stdint.h>
#include <string.h>

#if defined(__386__)
#define FIRE_DOS_FAR
#define FIRE_DOS_LOW(seg, off) ((unsigned char*)(((uint32_t)(seg) << 4) + (uint32_t)(off)))
#define FIRE_DOS_COPY(to, from, n) memcpy((to), (from), (n))
#define FIRE_DOS_W w
#define fire_dos_int(n, r) int386((n), (r), (r))

/* DOS memory through DPMI 0100h: once, it stays until the program ends */
static unsigned char* fire_dos_lowBuffer(unsigned* seg) {
    static unsigned short buffer = 0;
    if (buffer == 0) {
        union REGS r;
        memset(&r, 0, sizeof r);
        r.w.ax = 0x0100;
        r.w.bx = 1024 / 16;
        int386(0x31, &r, &r);
        if (r.w.cflag) return NULL;
        buffer = r.w.ax;
    }
    *seg = buffer;
    return FIRE_DOS_LOW(buffer, 0);
}
/* the registers of the real mode for DPMI 0300h */
#pragma pack(push, 1)
typedef struct FireDpmiRegs {
    uint32_t edi, esi, ebp, reserved, ebx, edx, ecx, eax;
    uint16_t flags, es, ds, fs, gs, ip, cs, sp, ss;
} FireDpmiRegs;
#pragma pack(pop)
static void fire_dos_realInt(int n, union REGS* r, unsigned es) {
    FireDpmiRegs m;
    union REGS d;
    struct SREGS s;
    memset(&m, 0, sizeof m);
    m.eax = r->w.ax; m.ebx = r->w.bx; m.ecx = r->w.cx; m.edx = r->w.dx; m.esi = r->w.si; m.edi = r->w.di;
    m.es = (uint16_t)es; m.ds = (uint16_t)es;
    memset(&d, 0, sizeof d);
    segread(&s);
    d.w.ax = 0x0300; d.h.bl = (unsigned char)n; d.h.bh = 0; d.w.cx = 0;
    d.x.edi = (unsigned)(uintptr_t)&m;
    int386x(0x31, &d, &d, &s);
    r->w.ax = (unsigned short)m.eax; r->w.bx = (unsigned short)m.ebx; r->w.cx = (unsigned short)m.ecx; r->w.dx = (unsigned short)m.edx;
    r->w.si = (unsigned short)m.esi; r->w.di = (unsigned short)m.edi; r->w.cflag = m.flags & 1;
}
#else
#define FIRE_DOS_FAR __far
#define FIRE_DOS_LOW(seg, off) ((unsigned char __far*)MK_FP((seg), (off)))
#define FIRE_DOS_COPY(to, from, n) _fmemcpy((to), (from), (n))
#define FIRE_DOS_W x
#define fire_dos_int(n, r) int86((n), (r), (r))

static unsigned char __far* fire_dos_lowBuffer(unsigned* seg) {
    static unsigned buffer = 0;
    if (buffer == 0 && _dos_allocmem(1024 / 16, &buffer) != 0) { buffer = 0; return NULL; }
    *seg = buffer;
    return FIRE_DOS_LOW(buffer, 0);
}
static void fire_dos_realInt(int n, union REGS* r, unsigned es) {
    struct SREGS s;
    segread(&s);
    s.es = es;
    int86x(n, r, r, &s);
}
#endif

#endif
