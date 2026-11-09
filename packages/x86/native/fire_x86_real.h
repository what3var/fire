/* fire x86 package: the switch from the real mode of DOS (dos16; Open Watcom) into the 32-bit protected mode or the 64-bit long mode and back. A small stub (fire_x86_stubs.h, from
 * tools/x86_protected.asm) lies at a paragraph of its own: it loads a GDT (flat code and data of 4 GB, and 16-bit code and data at the stub for the way back),
 * sets PE, calls the routine with the registers, clears PE and comes back to the program. Interrupts and NMI are off meanwhile (an empty IDT): the routine
 * must not take long - the clock of the BIOS misses its ticks. The long mode (tools/x86_long.asm) goes on from the protected mode: PAE, the page tables of
 * fire_x86_pagesFor (the first 4 GB 1:1, pages of 2 MB), EFER.LME, paging on, a jump into 64-bit code - and back the same way. Not in virtual 8086 mode (EMM386, Windows: the switch would need VCPI or DPMI). */
#ifndef FIRE_X86_REAL_H
#define FIRE_X86_REAL_H
#include "fire_x86_c.h"
#include "fire_x86_stubs.h"
#include <conio.h>
#include <dos.h>
#include <i86.h>
#include <stdlib.h>
#include <string.h>

#define FIRE_X86_STACK 4096u   /* the stack of the routine, after the stub */

static uint32_t fire_x86_linearOf(const void* p) { return ((uint32_t)FP_SEG(p) << 4) + FP_OFF(p); }

/* 1: a 386 or later (the 8086 keeps the flags 12-15 set, the 286 in real mode keeps them clear) */
static int fire_x86_is386(void) {
    int level = 0;
    __asm {
        pushf
        pop ax
        mov cx, ax
        and ax, 0x0FFF
        push ax
        popf
        pushf
        pop ax
        and ax, 0xF000
        cmp ax, 0xF000
        je done
        or cx, 0x7000
        push cx
        popf
        pushf
        pop ax
        and ax, 0x7000
        jz done
        mov level, 1
    done:
    }
    __asm {
        pushf
        pop ax
        and ax, 0x0FFF
        push ax
        popf
    }
    return level;
}
/* the PE bit of the machine status word (286 on): set in real-mode DOS means virtual 8086 mode */
static int fire_x86_inV86(void) {
    unsigned short msw = 0;
    __asm {
        db 0x0F, 0x01, 0xE0          /* smsw ax */
        mov msw, ax
    }
    return (msw & 1) != 0;
}
/* 1: CPUID knows the long mode (the ID flag can be toggled, the extended leaf 80000001h exists, EDX bit 29) */
static int fire_x86_canLong(void) {
    int ok = 0;
    if (!fire_x86_is386()) return 0;
    __asm {
        db 0x60                                    /* pusha */
        db 0x66, 0x9C                              /* pushfd */
        db 0x66, 0x58                              /* pop eax */
        db 0x66, 0x8B, 0xC8                        /* mov ecx, eax */
        db 0x66, 0x35, 0x00, 0x00, 0x20, 0x00      /* xor eax, 200000h (ID) */
        db 0x66, 0x50                              /* push eax */
        db 0x66, 0x9D                              /* popfd */
        db 0x66, 0x9C                              /* pushfd */
        db 0x66, 0x58                              /* pop eax */
        db 0x66, 0x51                              /* push ecx */
        db 0x66, 0x9D                              /* popfd: as before */
        db 0x66, 0x33, 0xC1                        /* xor eax, ecx */
        db 0x66, 0xA9, 0x00, 0x00, 0x20, 0x00      /* test eax, 200000h */
        jz none
        db 0x66, 0xB8, 0x00, 0x00, 0x00, 0x80      /* mov eax, 80000000h */
        db 0x0F, 0xA2                              /* cpuid */
        db 0x66, 0x3D, 0x01, 0x00, 0x00, 0x80      /* cmp eax, 80000001h */
        jb none
        db 0x66, 0xB8, 0x01, 0x00, 0x00, 0x80      /* mov eax, 80000001h */
        db 0x0F, 0xA2                              /* cpuid */
        db 0x66, 0xF7, 0xC2, 0x00, 0x00, 0x00, 0x20 /* test edx, 20000000h (LM) */
        jz none
        mov ok, 1
    none:
        db 0x61                                    /* popa */
    }
    return ok;
}
static const char* fire_x86_mode(void) {
    if (!fire_x86_is386()) return "real";
    return fire_x86_inV86() ? "v86" : "real";
}
static const char* fire_x86_unavailable(void) {
    if (!fire_x86_is386()) return "The protected mode with 32 bits needs a 386 or later processor.";
    if (fire_x86_inV86()) return "The processor is in virtual 8086 mode (EMM386, QEMM or Windows): a program can only switch through VCPI or DPMI.";
    return NULL;
}

/* is the A20 line on (0000:0500 and FFFF:0510 are different bytes)? */
static int fire_x86_a20on(void) {
    unsigned char __far* lo = (unsigned char __far*)MK_FP(0x0000, 0x0500);
    unsigned char __far* hi = (unsigned char __far*)MK_FP(0xFFFF, 0x0510);
    unsigned char a, b;
    int on;
    _disable();
    a = *lo; b = *hi;
    *lo = 0x5A; *hi = 0xA5;
    on = *lo != 0xA5;
    *lo = a; *hi = b;
    _enable();
    return on;
}
static int fire_x86_a20(void) {
    union REGS r;
    if (fire_x86_a20on()) return 1;
    r.x.ax = 0x2401;   /* the BIOS */
    int86(0x15, &r, &r);
    if (fire_x86_a20on()) return 1;
    outp(0x92, (inp(0x92) | 2) & ~1);   /* "fast A20" of the system control port (bit 0 would reset the machine) */
    return fire_x86_a20on();
}

static unsigned char __far* g_x86Stub = NULL;   /* the stub at S:0, then its stack */
static uint32_t g_x86Base = 0;

static void fire_x86_put32(unsigned char __far* p, uint32_t v) { p[0] = (unsigned char)v; p[1] = (unsigned char)(v >> 8); p[2] = (unsigned char)(v >> 16); p[3] = (unsigned char)(v >> 24); }
static uint32_t fire_x86_get32(const unsigned char __far* p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
/* the base of a descriptor of the GDT */
static void fire_x86_setBase(unsigned char __far* d, uint32_t b) { d[2] = (unsigned char)b; d[3] = (unsigned char)(b >> 8); d[4] = (unsigned char)(b >> 16); d[7] = (unsigned char)(b >> 24); }

/* the stub at a paragraph of its own, once (it stays until the program ends) */
static int fire_x86_prepare(void) {
    unsigned char* block;
    unsigned seg;
    if (g_x86Stub) return 1;
    block = (unsigned char*)malloc(sizeof fire_x86_protectedStub + 16 + FIRE_X86_STACK + 16);
    if (block == NULL) return 0;
    seg = (unsigned)((fire_x86_linearOf(block) + 15) >> 4);
    g_x86Base = (uint32_t)seg << 4;
    g_x86Stub = (unsigned char __far*)MK_FP(seg, 0);
    _fmemcpy(g_x86Stub, fire_x86_protectedStub, sizeof fire_x86_protectedStub);
    fire_x86_put32(g_x86Stub + 4, g_x86Base);
    fire_x86_put32(g_x86Stub + 36, g_x86Base + ((sizeof fire_x86_protectedStub + 15) & ~15u) + FIRE_X86_STACK);
    fire_x86_setBase(g_x86Stub + 72, g_x86Base);
    fire_x86_setBase(g_x86Stub + 80, g_x86Base);
    fire_x86_put32(g_x86Stub + 90, fire_x86_get32(g_x86Stub + 90) + g_x86Base);     /* the GDT */
    fire_x86_put32(g_x86Stub + 112, fire_x86_get32(g_x86Stub + 112) + g_x86Base);   /* the 32-bit entry */
    *(unsigned short __far*)(g_x86Stub + 120) = (unsigned short)seg;                /* the way back */
    return 1;
}

static unsigned char __far* g_x86Long = NULL;   /* the long-mode stub at T:0, its stack, then 6 pages of page tables */
#define FIRE_X86_PAGES 6u

static void fire_x86_putEntry(unsigned char __far* p, uint32_t lo, uint32_t hi) { fire_x86_put32(p, lo); fire_x86_put32(p + 4, hi); }
/* PML4 -> PDPT -> 4 page directories: linear = physical for the first 4 GB, pages of 2 MB */
static void fire_x86_pagesFor(unsigned char __far* pages, uint32_t at) {
    unsigned i, j;
    _fmemset(pages, 0, FIRE_X86_PAGES * 4096u);
    fire_x86_putEntry(pages, (at + 4096u) | 3u, 0);   /* PML4[0] */
    for (i = 0; i < 4; i++) fire_x86_putEntry(pages + 4096u + 8u * i, (at + 4096u * (2 + i)) | 3u, 0);   /* PDPT[i] */
    for (i = 0; i < 4; i++)
        for (j = 0; j < 512; j++)
            fire_x86_putEntry(pages + 4096u * (2 + i) + 8u * j, ((uint32_t)i << 30) + ((uint32_t)j << 21) | 0x83u, 0);   /* present, writable, 2 MB */
}
static int fire_x86_prepareLong(void) {
    unsigned char* block;
    unsigned seg;
    uint32_t base, pages, stubEnd;
    if (g_x86Long) return 1;
    stubEnd = ((sizeof fire_x86_longStub + 15) & ~15u) + FIRE_X86_STACK;
    block = (unsigned char*)malloc((size_t)(16 + stubEnd + 4096u + FIRE_X86_PAGES * 4096u));
    if (block == NULL) return 0;
    seg = (unsigned)((fire_x86_linearOf(block) + 15) >> 4);
    base = (uint32_t)seg << 4;
    pages = (base + stubEnd + 4095u) & ~4095UL;
    g_x86Long = (unsigned char __far*)MK_FP(seg, 0);
    _fmemcpy(g_x86Long, fire_x86_longStub, sizeof fire_x86_longStub);
    fire_x86_put32(g_x86Long + 4, base);
    fire_x86_put32(g_x86Long + 36, base + stubEnd);
    fire_x86_put32(g_x86Long + 44, pages);
    fire_x86_setBase(g_x86Long + 72, base);
    fire_x86_setBase(g_x86Long + 80, base);
    fire_x86_put32(g_x86Long + 98, fire_x86_get32(g_x86Long + 98) + base);     /* the GDT */
    fire_x86_put32(g_x86Long + 120, fire_x86_get32(g_x86Long + 120) + base);   /* the 32-bit entry */
    *(unsigned short __far*)(g_x86Long + 128) = (unsigned short)seg;           /* the way back */
    fire_x86_put32(g_x86Long + 132, fire_x86_get32(g_x86Long + 132) + base);   /* the 64-bit entry */
    fire_x86_put32(g_x86Long + 140, fire_x86_get32(g_x86Long + 140) + base);   /* back to 32 bits */
    fire_x86_pagesFor((unsigned char __far*)MK_FP((unsigned)(pages >> 4), 0), pages);
    return 1;
}
static int fire_x86_runLong(uint32_t code, uint32_t regs[6]) {
    void (__far* entry)(void);
    int i;
    if (fire_x86_unavailable() != NULL || !fire_x86_canLong() || !fire_x86_a20() || !fire_x86_prepareLong()) return 0;
    fire_x86_put32(g_x86Long + 8, code);
    for (i = 0; i < 6; i++) fire_x86_put32(g_x86Long + 12 + 4 * i, regs[i]);
    entry = (void (__far*)(void))g_x86Long;
    entry();
    for (i = 0; i < 6; i++) regs[i] = fire_x86_get32(g_x86Long + 12 + 4 * i);
    return 1;
}

static int fire_x86_runProtected(uint32_t code, uint32_t regs[6]) {
    void (__far* entry)(void);
    int i;
    if (fire_x86_unavailable() != NULL || !fire_x86_a20() || !fire_x86_prepare()) return 0;
    fire_x86_put32(g_x86Stub + 8, code);
    for (i = 0; i < 6; i++) fire_x86_put32(g_x86Stub + 12 + 4 * i, regs[i]);
    entry = (void (__far*)(void))g_x86Stub;
    entry();
    for (i = 0; i < 6; i++) regs[i] = fire_x86_get32(g_x86Stub + 12 + 4 * i);
    return 1;
}

#endif
