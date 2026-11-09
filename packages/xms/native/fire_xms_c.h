/* fire xms package: the XMS driver (extended memory above 1 MB) of DOS. The package brings it for each platform (`platformSources` of xms.fireproj:
 * fire_xms_real.h for dos16; `vm` and `default`: fire_xms_none.h), each with:
 *   const char* fire_xms_unavailable(void)        NULL: DOS is there (whether a driver is, says fire_xms_init); else why not
 *   int  fire_xms_init(void)                      1: an XMS driver is there (asked once)
 *   uint16_t fire_xms_call(int func, uint16_t dx, const void* dsSi, uint16_t* bx, uint16_t* dxOut)   AH = func, DX, DS:SI -> AX (BX, DX after it)
 *   uint32_t fire_xms_far(const void* p)          a pointer of the program as segment * 65536 + offset (normalized) - the address of a move */
#ifndef FIRE_XMS_C_H
#define FIRE_XMS_C_H
#include <stdint.h>
#endif
