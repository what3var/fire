/* fire native platform layer, DOS part: no DOS (another operating system, a board, the VM) - every function of the packages dos and xms is a DosException
 * (fire_dos_unavailable says why); the others are never called. */
#ifndef FIRE_DOS_NONE_H
#define FIRE_DOS_NONE_H
#include "fire_dos_c.h"

static const char* fire_dos_unavailable(void) { return "DOS and its services are only there on dos16 (and later dos32), not on this target."; }
static void fire_dos_interrupt(int number, uint16_t r[9]) { (void)number; r[8] = 1; }
static void fire_dos_farOf(const void* p, uint16_t* seg, uint16_t* off) { (void)p; *seg = 0; *off = 0; }
static int fire_dos_xmsInit(void) { return 0; }
static uint16_t fire_dos_xmsCall(int func, uint16_t dx, const void* dsSi, uint16_t* bx, uint16_t* dxOut) {
    (void)func; (void)dx; (void)dsSi;
    if (bx) *bx = 0x80;   /* (XMS: "function not implemented") */
    if (dxOut) *dxOut = 0;
    return 0;
}

#endif
