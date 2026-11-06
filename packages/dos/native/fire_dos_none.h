/* fire dos package: no DOS (another operating system, a board, the VM) - every function of the package is a DosException
 * (fire_dos_unavailable says why); the others are never called. */
#ifndef FIRE_DOS_NONE_H
#define FIRE_DOS_NONE_H
#include "fire_dos_c.h"

static const char* fire_dos_unavailable(void) { return "DOS and its services are only there on dos16 (and later dos32), not on this target."; }
static void fire_dos_interrupt(int number, uint16_t r[9]) { (void)number; r[8] = 1; }
static void fire_dos_farOf(const void* p, uint16_t* seg, uint16_t* off) { (void)p; *seg = 0; *off = 0; }
#endif
