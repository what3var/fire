/* fire dos package: the services of DOS and of the real-mode BIOS, called by their interrupts. Only under DOS (dos16 directly; dos32 through DPMI, which calls the real mode for it).
 *
 * The package brings them for each platform (`platformSources` of dos.fireproj: fire_dos_real.h for dos16, fire_dos_dpmi.h for dos32; `vm` and `default`: fire_dos_none.h), each with:
 *   const char* fire_dos_unavailable(void)        NULL: DOS is there; else why not (the text of the DosException)
 *   void fire_dos_interrupt(int number, uint16_t r[9])   INT number with AX BX CX DX SI DI DS ES (r[0..7]; DS/ES 0: the program's own data segment) -
 *                                                 afterwards the registers and the flags (r[8]; bit 0 the carry)
 *   void fire_dos_farOf(const void* p, uint16_t* seg, uint16_t* off)   a pointer of the program as segment:offset (normalized: the offset below 16; 0:0 when it lies above the first megabyte)
 */
#ifndef FIRE_DOS_C_H
#define FIRE_DOS_C_H
#include <stdint.h>
#endif
