/* fire native platform layer, DOS part in C (the HAL of the packages dos and xms): the services of DOS and of the real-mode BIOS, called by their
 * interrupts, and the XMS driver (memory above 1 MB). Only under DOS (dos16 directly; dos32 later through DPMI, which calls the real mode for it).
 *
 * A platform package offers `fire_dos.h` (FIRE_PLATFORM_DOS_C_HEADER points to it) with:
 *   const char* fire_dos_unavailable(void)        NULL: DOS is there; else why not (the text of the DosException)
 *   void fire_dos_interrupt(int number, uint16_t r[9])   INT number with AX BX CX DX SI DI DS ES (r[0..7]; DS/ES 0: the program's own data segment) -
 *                                                 afterwards the registers and the flags (r[8]; bit 0 the carry)
 *   void fire_dos_farOf(const void* p, uint16_t* seg, uint16_t* off)   a pointer of the program as segment:offset (normalized: the offset below 16)
 *   int  fire_dos_xmsInit(void)                   1: an XMS driver is there (asked once)
 *   uint16_t fire_dos_xmsCall(int func, uint16_t dx, const void* dsSi, uint16_t* bx, uint16_t* dxOut)   AH = func, DX, DS:SI -> AX (BX, DX after it)
 */
#ifndef FIRE_DOS_C_H
#define FIRE_DOS_C_H
#include <stdint.h>
#endif
