/* fire machine package: the machine for the 16-bit real mode of the PC (dos16; Open Watcom): the ports with IN/OUT (32 bits with the
 * operand-size prefix from the 386 on), physical memory below 1 MB + 64 KB through segment:offset, the CPU level by the classic flag tests and CPUID,
 * IRQ 0..15 through the vectors 08h-0Fh and 70h-77h and the two 8259 PICs. The 386 instructions are written as bytes: the program is built for the 8086
 * and only runs them after the check (fire_mach_cpuLevel). */
#ifndef FIRE_MACHINE_X86_16_H
#define FIRE_MACHINE_X86_16_H
#include "fire_machine_c.h"
#include <conio.h>
#include <dos.h>
#include <i86.h>
#include <stdlib.h>

static const char* fire_mach_unavailable(void) { return NULL; }

/* 0 8086, 2 286, 3 386, 4 486, 5 CPUID (8086: the flags 12-15 are always set; 286 in real mode: always clear; 386: AC can be set; 486: ID too) */
static int fire_mach_cpuLevel(void) {
    static int known = -1;
    int level = 0;
    if (known >= 0) return known;
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
        mov level, 2
        or cx, 0xF000
        push cx
        popf
        pushf
        pop ax
        push cx
        popf
        and ax, 0xF000
        jz done
        mov level, 3
        db 0x66, 0x9C                              /* pushfd */
        db 0x66, 0x58                              /* pop eax */
        db 0x66, 0x8B, 0xC8                        /* mov ecx, eax */
        db 0x66, 0x35, 0x00, 0x00, 0x04, 0x00      /* xor eax, 40000h (AC) */
        db 0x66, 0x50                              /* push eax */
        db 0x66, 0x9D                              /* popfd */
        db 0x66, 0x9C                              /* pushfd */
        db 0x66, 0x58                              /* pop eax */
        db 0x66, 0x33, 0xC1                        /* xor eax, ecx */
        db 0x66, 0x51                              /* push ecx */
        db 0x66, 0x9D                              /* popfd: as before */
        db 0x66, 0xA9, 0x00, 0x00, 0x04, 0x00      /* test eax, 40000h */
        jz done
        mov level, 4
        db 0x66, 0x8B, 0xC1                        /* mov eax, ecx */
        db 0x66, 0x35, 0x00, 0x00, 0x20, 0x00      /* xor eax, 200000h (ID) */
        db 0x66, 0x50
        db 0x66, 0x9D
        db 0x66, 0x9C
        db 0x66, 0x58
        db 0x66, 0x33, 0xC1
        db 0x66, 0x51
        db 0x66, 0x9D
        db 0x66, 0xA9, 0x00, 0x00, 0x20, 0x00      /* test eax, 200000h */
        jz done
        mov level, 5
    done:
    }
    known = level;
    return level;
}
static void fire_mach_cpuid(uint32_t leaf, uint32_t sub, uint32_t out[4]) {
    uint16_t r[8];
    uint16_t lo = (uint16_t)leaf, hi = (uint16_t)(leaf >> 16), slo = (uint16_t)sub, shi = (uint16_t)(sub >> 16);
    __asm {
        mov ax, hi
        db 0x66, 0xC1, 0xE0, 0x10                  /* shl eax, 16 */
        mov ax, lo
        mov cx, shi
        db 0x66, 0xC1, 0xE1, 0x10                  /* shl ecx, 16 */
        mov cx, slo
        db 0x0F, 0xA2                              /* cpuid */
        mov r[0], ax
        db 0x66, 0xC1, 0xE8, 0x10                  /* shr eax, 16 */
        mov r[2], ax
        mov r[4], bx
        db 0x66, 0xC1, 0xEB, 0x10                  /* shr ebx, 16 */
        mov r[6], bx
        mov r[8], cx
        db 0x66, 0xC1, 0xE9, 0x10                  /* shr ecx, 16 */
        mov r[10], cx
        mov r[12], dx
        db 0x66, 0xC1, 0xEA, 0x10                  /* shr edx, 16 */
        mov r[14], dx
    }
    out[0] = r[0] | ((uint32_t)r[1] << 16); out[1] = r[2] | ((uint32_t)r[3] << 16);
    out[2] = r[4] | ((uint32_t)r[5] << 16); out[3] = r[6] | ((uint32_t)r[7] << 16);
}

static uint32_t fire_mach_in32(uint16_t port) {
    uint16_t lo, hi;
    __asm {
        mov dx, port
        db 0x66, 0xED                              /* in eax, dx */
        mov lo, ax
        db 0x66, 0xC1, 0xE8, 0x10                  /* shr eax, 16 */
        mov hi, ax
    }
    return lo | ((uint32_t)hi << 16);
}
static void fire_mach_out32(uint16_t port, uint32_t v) {
    uint16_t lo = (uint16_t)v, hi = (uint16_t)(v >> 16);
    __asm {
        mov dx, port
        mov ax, hi
        db 0x66, 0xC1, 0xE0, 0x10                  /* shl eax, 16 */
        mov ax, lo
        db 0x66, 0xEF                              /* out dx, eax */
    }
}
static uint32_t fire_mach_in(unsigned port, int bits) {
    if (bits == 8) return (uint32_t)(inp(port) & 0xFF);
    if (bits == 16) return (uint32_t)(inpw(port) & 0xFFFF);
    return fire_mach_in32((uint16_t)port);
}
static void fire_mach_out(unsigned port, int bits, uint32_t v) {
    if (bits == 8) outp(port, (int)(v & 0xFF));
    else if (bits == 16) outpw(port, (unsigned)(v & 0xFFFF));
    else fire_mach_out32((uint16_t)port, v);
}

/* a physical address as segment:offset (the segment as high as it goes: up to FFFF:FFFF = 10FFEFh, the HMA when A20 is on) */
static unsigned char __far* fire_mach_at(uint32_t address, int bytes) {
    uint32_t seg;
    if (address + (uint32_t)bytes > 0x10FFF0ul) return NULL;
    seg = address >> 4;
    if (seg > 0xFFFF) seg = 0xFFFF;
    return (unsigned char __far*)MK_FP((unsigned)seg, (unsigned)(address - (seg << 4)));
}
static int fire_mach_peek(uint32_t address, int bits, uint32_t* v) {
    unsigned char __far* p = fire_mach_at(address, bits / 8);
    int i;
    if (p == NULL) return 0;
    *v = 0;
    for (i = bits / 8 - 1; i >= 0; i--) *v = (*v << 8) | p[i];   /* (little endian; byte by byte: a word may cross the end of the offset) */
    return 1;
}
static int fire_mach_poke(uint32_t address, int bits, uint32_t v) {
    unsigned char __far* p = fire_mach_at(address, bits / 8);
    int i;
    if (p == NULL) return 0;
    if (bits == 16) { *(volatile uint16_t __far*)p = (uint16_t)v; return 1; }   /* (one access: a register of a device may want it whole) */
    for (i = 0; i < bits / 8; i++) p[i] = (unsigned char)(v >> (8 * i));
    return 1;
}

static void fire_mach_interrupts(int on) { if (on) _enable(); else _disable(); }

#include "fire_machine_pc_irq.h"   /* IRQ 0..15 (the same in real mode and with a DOS extender) */

#endif
