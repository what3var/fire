/* fire machine package: the hardware interrupts of the PC (IRQ 0..15 through the vectors 08h-0Fh and 70h-77h and the two 8259 PICs) - the same code in real
 * mode (dos16) and with a DOS extender (dos32: _dos_setvect installs a protected-mode handler, the extender passes the IRQ to it). A header of
 * fire_machine_x86_16.h and fire_machine_x86_32.h. */
#ifndef FIRE_MACHINE_PC_IRQ_H
#define FIRE_MACHINE_PC_IRQ_H
#include <conio.h>
#include <stdint.h>
#include <dos.h>
#include <stdlib.h>

/* ---- IRQ 0..15: a handler per line that counts; chained to the one before it, or it acknowledges the PIC itself ---- */
typedef void (__interrupt __far *fire_mach_isr)(void);
static volatile uint16_t fire_mach_irqCounts[16];
static fire_mach_isr fire_mach_irqOld[16];
static unsigned char fire_mach_irqChain[16], fire_mach_irqHooked[16], fire_mach_irqMasked[16];
static int fire_mach_irqAtExit = 0;
static unsigned fire_mach_vectorOf(int line) { return line < 8 ? 0x08 + line : 0x70 + (line - 8); }
#define FIRE_MACH_ISR(n) \
    static void __interrupt __far fire_mach_isr##n(void) { \
        fire_mach_irqCounts[n]++; \
        if (fire_mach_irqChain[n]) _chain_intr(fire_mach_irqOld[n]); \
        if (n >= 8) outp(0xA0, 0x20); \
        outp(0x20, 0x20); \
    }
FIRE_MACH_ISR(0) FIRE_MACH_ISR(1) FIRE_MACH_ISR(2) FIRE_MACH_ISR(3) FIRE_MACH_ISR(4) FIRE_MACH_ISR(5) FIRE_MACH_ISR(6) FIRE_MACH_ISR(7)
FIRE_MACH_ISR(8) FIRE_MACH_ISR(9) FIRE_MACH_ISR(10) FIRE_MACH_ISR(11) FIRE_MACH_ISR(12) FIRE_MACH_ISR(13) FIRE_MACH_ISR(14) FIRE_MACH_ISR(15)
static const fire_mach_isr fire_mach_isrs[16] = { fire_mach_isr0, fire_mach_isr1, fire_mach_isr2, fire_mach_isr3, fire_mach_isr4, fire_mach_isr5, fire_mach_isr6,
    fire_mach_isr7, fire_mach_isr8, fire_mach_isr9, fire_mach_isr10, fire_mach_isr11, fire_mach_isr12, fire_mach_isr13, fire_mach_isr14, fire_mach_isr15 };

static void fire_mach_irqClose(int line) {
    unsigned port, bit;
    if (line < 0 || line > 15 || !fire_mach_irqHooked[line]) return;
    port = line < 8 ? 0x21 : 0xA1;
    bit = 1u << (line & 7);
    _disable();
    if (fire_mach_irqMasked[line]) outp(port, inp(port) | bit);   /* (masked before: masked again) */
    _dos_setvect(fire_mach_vectorOf(line), fire_mach_irqOld[line]);
    fire_mach_irqHooked[line] = 0;
    _enable();
}
/* (at the end of the program: a vector left pointing into it would bring DOS down with the next interrupt) */
static void fire_mach_irqCloseAll(void) { int i; for (i = 0; i < 16; i++) fire_mach_irqClose(i); }
static int fire_mach_irqOpen(int line, int chain) {
    unsigned port, bit;
    if (line < 0 || line > 15 || fire_mach_irqHooked[line]) return 0;
    if (!fire_mach_irqAtExit) { atexit(fire_mach_irqCloseAll); fire_mach_irqAtExit = 1; }
    port = line < 8 ? 0x21 : 0xA1;
    bit = 1u << (line & 7);
    _disable();
    fire_mach_irqOld[line] = _dos_getvect(fire_mach_vectorOf(line));
    fire_mach_irqChain[line] = (unsigned char)(chain != 0);
    fire_mach_irqCounts[line] = 0;
    _dos_setvect(fire_mach_vectorOf(line), fire_mach_isrs[line]);
    fire_mach_irqMasked[line] = (unsigned char)((inp(port) & bit) != 0);
    outp(port, inp(port) & ~bit);
    if (line >= 8) outp(0x21, inp(0x21) & ~4u);   /* (the second PIC comes in on IRQ 2 of the first) */
    fire_mach_irqHooked[line] = 1;
    _enable();
    return 1;
}
static uint32_t fire_mach_irqTake(int line) {
    uint32_t n;
    if (line < 0 || line > 15) return 0;
    _disable();
    n = fire_mach_irqCounts[line];
    fire_mach_irqCounts[line] = 0;
    _enable();
    return n;
}

#endif
