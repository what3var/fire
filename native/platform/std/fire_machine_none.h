/* fire native platform layer, machine part: no access to the hardware (an operating system owns it, or the VM) - every function of Machine is a
 * MachineException (fire_mach_unavailable says why); the others are never called. */
#ifndef FIRE_MACHINE_NONE_H
#define FIRE_MACHINE_NONE_H
#include "fire_machine_c.h"

static const char* fire_mach_unavailable(void) { return "The hardware of the machine (ports, physical memory, interrupts) is only reachable on dos16 (and later dos32 and bios-x86), not on this target."; }
static uint32_t fire_mach_in(unsigned port, int bits) { (void)port; (void)bits; return 0; }
static void fire_mach_out(unsigned port, int bits, uint32_t v) { (void)port; (void)bits; (void)v; }
static int fire_mach_peek(uint32_t address, int bits, uint32_t* v) { (void)address; (void)bits; *v = 0; return 0; }
static int fire_mach_poke(uint32_t address, int bits, uint32_t v) { (void)address; (void)bits; (void)v; return 0; }
static int fire_mach_cpuLevel(void) { return 0; }
static void fire_mach_cpuid(uint32_t leaf, uint32_t sub, uint32_t out[4]) { (void)leaf; (void)sub; out[0] = out[1] = out[2] = out[3] = 0; }
static void fire_mach_interrupts(int on) { (void)on; }
static int fire_mach_irqOpen(int line, int chain) { (void)line; (void)chain; return 0; }
static void fire_mach_irqClose(int line) { (void)line; }
static uint32_t fire_mach_irqTake(int line) { (void)line; return 0; }

#endif
