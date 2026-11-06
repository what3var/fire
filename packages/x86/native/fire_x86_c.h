/* fire x86 package: the modes of the x86 processor - from the real mode of DOS into the 32-bit protected mode (flat memory, all 4 GB) or the 64-bit long mode and back, to run a routine
 * there or to reach the memory above 1 MB. Only on dos16 (the program runs in real mode); elsewhere the processor is in the mode of its operating system.
 *
 * The package brings it for each platform (`platformSources` of x86.fireproj: fire_x86_real.h for dos16; `vm` and `default`: fire_x86_none.h), each with:
 *   const char* fire_x86_unavailable(void)     NULL: the switch is possible; else why not (the text of the X86Exception)
 *   const char* fire_x86_mode(void)            "real", "v86" (virtual 8086 mode: EMM386, Windows - the switch needs VCPI/DPMI) or "none" (not here)
 *   int fire_x86_a20(void)                     enables the A20 line (the memory above 1 MB): 1 when it is on
 *   int fire_x86_runProtected(uint32_t code, uint32_t regs[6])   calls the 32-bit routine at the linear address `code` in protected mode, interrupts off,
 *                                              with EAX EBX ECX EDX ESI EDI from regs (and their values afterwards in regs); the routine returns with RET. 1: done
 *   int fire_x86_canLong(void)                 1: the processor has the long mode (64 bit; CPUID 80000001h, EDX bit 29)
 *   int fire_x86_runLong(uint32_t code, uint32_t regs[6])        the same for a 64-bit routine in the long mode (the first 4 GB mapped 1:1 in pages of 2 MB;
 *                                              RAX..RDI get the 32 bits of regs zero-extended, afterwards regs has their low 32 bits)
 *   uint32_t fire_x86_linearOf(const void* p)  the linear (physical) address of a byte of the program
 */
#ifndef FIRE_X86_C_H
#define FIRE_X86_C_H
#include <stdint.h>
#endif
