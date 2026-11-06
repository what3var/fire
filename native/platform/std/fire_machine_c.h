/* fire native platform layer, machine part in C (the HAL of the machine bridge, packages/machine): the hardware of the PC itself - I/O ports, physical
 * memory, the processor, hardware interrupts. Only where a program owns the machine (DOS, later dos32 through DPMI and bios-x86 with its own IDT); elsewhere
 * none (an operating system does not let a program at its ports).
 *
 * A platform package offers `fire_machine.h` (FIRE_PLATFORM_MACHINE_C_HEADER points to it) with:
 *   const char* fire_mach_unavailable(void)       NULL: the hardware is there; else why not (the text of the MachineException)
 *   uint32_t fire_mach_in(unsigned port, int bits) / void fire_mach_out(unsigned port, int bits, uint32_t v)     8, 16 or 32 bits (32: from the 386 on)
 *   int  fire_mach_peek(uint32_t address, int bits, uint32_t* v) / int fire_mach_poke(uint32_t address, int bits, uint32_t v)
 *                                                 physical addresses; 0: the address cannot be reached from here (real mode: above 1 MB + 64 KB)
 *   int  fire_mach_cpuLevel(void)                 0 8086/8088, 2 286, 3 386, 4 486 without CPUID, 5 CPUID
 *   void fire_mach_cpuid(uint32_t leaf, uint32_t sub, uint32_t out[4])   EAX, EBX, ECX, EDX (only with level 5)
 *   void fire_mach_interrupts(int on)             STI / CLI
 *   int  fire_mach_irqOpen(int line, int chain)   hooks IRQ `line` (0..15): it is counted; chain: the handler before it runs too (it acknowledges the PIC),
 *                                                 else this one does (EOI); the line is unmasked. 0: it is hooked already / no such line
 *   void fire_mach_irqClose(int line)             the handler and the mask as they were (also for all lines at the end of the program)
 *   uint32_t fire_mach_irqTake(int line)          the interrupts since the last call (and 0 again)
 * Never is fire code run in an interrupt: the program asks for the count (Machine.Irq.Count/Wait) at its own time. */
#ifndef FIRE_MACHINE_C_H
#define FIRE_MACHINE_C_H
#include <stdint.h>
#endif
