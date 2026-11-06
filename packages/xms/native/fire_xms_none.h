/* fire xms package: no DOS (another operating system, a board, the VM) - every function of the package is an XmsException (fire_xms_unavailable says why). */
#ifndef FIRE_XMS_NONE_H
#define FIRE_XMS_NONE_H
#include "fire_xms_c.h"

static const char* fire_xms_unavailable(void) { return "DOS and its extended memory are only there on dos16 (and later dos32), not on this target."; }
static uint32_t fire_xms_far(const void* p) { (void)p; return 0; }
static int fire_xms_init(void) { return 0; }
static uint16_t fire_xms_call(int func, uint16_t dx, const void* dsSi, uint16_t* bx, uint16_t* dxOut) {
    (void)func; (void)dx; (void)dsSi;
    if (bx) *bx = 0x80;   /* (XMS: "function not implemented") */
    if (dxOut) *dxOut = 0;
    return 0;
}

#endif
