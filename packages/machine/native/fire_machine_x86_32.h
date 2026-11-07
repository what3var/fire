/* fire machine package: the machine for DOS with a DOS extender (dos32; Open Watcom 386, DOS/32A): the ports with IN/OUT, physical memory - the first megabyte
 * (and the HMA) at its linear addresses, above it a page mapped through DPMI (0800h: the video memory of a VESA card, a PCI device) -, the processor by the
 * flag tests and CPUID, IRQ 0..15 as in real mode (fire_machine_pc_irq.h). */
#ifndef FIRE_MACHINE_X86_32_H
#define FIRE_MACHINE_X86_32_H
#include "fire_machine_c.h"
#include <conio.h>
#include <dos.h>
#include <i86.h>
#include <stdlib.h>
#include <string.h>

static const char* fire_mach_unavailable(void) { return NULL; }

/* 3 386, 4 486 (AC can be set), 5 CPUID (ID can be toggled) */
static int fire_mach_cpuLevel(void) {
    static int known = -1;
    int level = 3;
    if (known >= 0) return known;
    __asm {
        pushfd
        pop eax
        mov ecx, eax
        xor eax, 40000h
        push eax
        popfd
        pushfd
        pop eax
        push ecx
        popfd
        xor eax, ecx
        test eax, 40000h
        jz done
        mov level, 4
        mov eax, ecx
        xor eax, 200000h
        push eax
        popfd
        pushfd
        pop eax
        push ecx
        popfd
        xor eax, ecx
        test eax, 200000h
        jz done
        mov level, 5
    done:
    }
    known = level;
    return level;
}
static void fire_mach_cpuid(uint32_t leaf, uint32_t sub, uint32_t out[4]) {
    uint32_t a, b, c, d;
    __asm {
        push ebx
        mov eax, leaf
        mov ecx, sub
        db 0x0F, 0xA2              /* cpuid */
        mov a, eax
        mov b, ebx
        mov c, ecx
        mov d, edx
        pop ebx
    }
    out[0] = a; out[1] = b; out[2] = c; out[3] = d;
}

static uint32_t fire_mach_in(unsigned port, int bits) {
    if (bits == 8) return (uint32_t)(inp(port) & 0xFF);
    if (bits == 16) return (uint32_t)(inpw(port) & 0xFFFF);
    return (uint32_t)inpd(port);
}
static void fire_mach_out(unsigned port, int bits, uint32_t v) {
    if (bits == 8) outp(port, (int)(v & 0xFF));
    else if (bits == 16) outpw(port, (unsigned)(v & 0xFFFF));
    else outpd(port, v);
}

/* a physical address as a pointer: the first megabyte and the HMA directly; above, the page through DPMI 0800h (mapped once, kept) */
static unsigned char* fire_mach_at(uint32_t address, int bytes) {
    static uint32_t mappedPhys = 0, mappedLin = 0;
    union REGS r;
    uint32_t page = address & ~0xFFFul;
    if (address + (uint32_t)bytes <= 0x10FFF0ul) return (unsigned char*)address;
    if (((address + (uint32_t)bytes - 1) & ~0xFFFul) != page) return NULL;   /* (one page at a time) */
    if (mappedLin == 0 || mappedPhys != page) {
        memset(&r, 0, sizeof r);
        r.w.ax = 0x0800;
        r.w.bx = (unsigned short)(page >> 16); r.w.cx = (unsigned short)page;
        r.w.si = 0; r.w.di = 0x1000;
        int386(0x31, &r, &r);
        if (r.w.cflag) return NULL;
        mappedPhys = page;
        mappedLin = ((uint32_t)r.w.bx << 16) | r.w.cx;
    }
    return (unsigned char*)(mappedLin + (address - page));
}
static int fire_mach_peek(uint32_t address, int bits, uint32_t* v) {
    unsigned char* p = fire_mach_at(address, bits / 8);
    if (p == NULL) return 0;
    if (bits == 8) *v = *(volatile uint8_t*)p;
    else if (bits == 16) *v = *(volatile uint16_t*)p;
    else *v = *(volatile uint32_t*)p;
    return 1;
}
static int fire_mach_poke(uint32_t address, int bits, uint32_t v) {
    unsigned char* p = fire_mach_at(address, bits / 8);
    if (p == NULL) return 0;
    if (bits == 8) *(volatile uint8_t*)p = (uint8_t)v;
    else if (bits == 16) *(volatile uint16_t*)p = (uint16_t)v;
    else *(volatile uint32_t*)p = v;
    return 1;
}

static void fire_mach_interrupts(int on) { if (on) _enable(); else _disable(); }

#include "fire_machine_pc_irq.h"   /* IRQ 0..15 (the same in real mode and with a DOS extender) */

#endif
