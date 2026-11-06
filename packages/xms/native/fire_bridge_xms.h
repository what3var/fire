/* fire native bridge "xms" (C): the natives behind `#import "xms"` (docs/stdlib/xms.md) - extended memory above 1 MB through the XMS driver (HIMEM.SYS,
 * the one of DOSBox, ...), via the DOS part of the platform layer (FIRE_PLATFORM_DOS_C_HEADER: fire_dos_xms*). The blocks that are still allocated when the
 * program ends are given back (else they are lost until the next start of DOS). */
#ifndef FIRE_BRIDGE_XMS_H
#define FIRE_BRIDGE_XMS_H

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#ifdef FIRE_LIBRARY
#include "platform/std/fire_dos_none.h"   /* (the VM: never DOS) */
#else
#include FIRE_PLATFORM_DOS_C_HEADER
#endif

#define FIRE_XMS_KEEP 64
static uint16_t g_xmsHandles[FIRE_XMS_KEEP];
static int g_xmsAtExit = 0;
static void fire_xmsb_freeAll(void) {
    int i;
    for (i = 0; i < FIRE_XMS_KEEP; i++)
        if (g_xmsHandles[i]) { fire_dos_xmsCall(0x0D, g_xmsHandles[i], NULL, NULL, NULL); fire_dos_xmsCall(0x0A, g_xmsHandles[i], NULL, NULL, NULL); g_xmsHandles[i] = 0; }
}

/* why there is no extended memory (a string), or undefined */
static Value fire_xms_Unavailable(OwnList* list) {
    const char* why = fire_dos_unavailable();
    if (!why && !fire_dos_xmsInit()) why = "There is no XMS driver (HIMEM.SYS) - no memory above 1 MB.";
    return why ? newStrFromUtf8((const uint8_t*)why, strlen(why), list) : Undef();
}
/* AH = func, DX: [AX, BX, DX] after the call */
static Value fire_xms_Call(Value func, Value dx, OwnList* list) {
    uint16_t bx = 0, dxOut = 0, ax = fire_dos_xmsCall((int)func.i, (uint16_t)dx.i, NULL, &bx, &dxOut);
    Arr* a = allocArr(3, list);
    arrItems(a)[0] = Int((int64_t)ax);
    arrItems(a)[1] = Int((int64_t)bx);
    arrItems(a)[2] = Int((int64_t)dxOut);
    return ArrV(a);
}
/* `kb` kilobytes: the handle, or the error code of the driver as a negative number */
static Value fire_xms_Alloc(Value kb) {
    uint16_t bx = 0, handle = 0;
    int i;
    if (!fire_dos_xmsCall(0x09, (uint16_t)kb.i, NULL, &bx, &handle)) return Int(-(int64_t)(bx & 0xFF));
    for (i = 0; i < FIRE_XMS_KEEP && g_xmsHandles[i]; i++) {}
    if (i < FIRE_XMS_KEEP) g_xmsHandles[i] = handle;
    if (!g_xmsAtExit) { atexit(fire_xmsb_freeAll); g_xmsAtExit = 1; }
    return Int((int64_t)handle);
}
static Value fire_xms_Free(Value handle) {
    int i;
    for (i = 0; i < FIRE_XMS_KEEP; i++) if (g_xmsHandles[i] == (uint16_t)handle.i) g_xmsHandles[i] = 0;
    return Bool(fire_dos_xmsCall(0x0A, (uint16_t)handle.i, NULL, NULL, NULL) != 0);
}

#pragma pack(push, 1)
typedef struct FireXmsMove { uint32_t length; uint16_t srcHandle; uint32_t srcOffset; uint16_t dstHandle; uint32_t dstOffset; } FireXmsMove;
#pragma pack(pop)
/* one move of the driver (an even length); 0 or the error code */
static int fire_xmsb_move(uint32_t length, uint16_t srcHandle, uint32_t srcOffset, uint16_t dstHandle, uint32_t dstOffset) {
    FireXmsMove m;
    uint16_t bx = 0;
    m.length = length; m.srcHandle = srcHandle; m.srcOffset = srcOffset; m.dstHandle = dstHandle; m.dstOffset = dstOffset;
    return fire_dos_xmsCall(0x0B, 0, &m, &bx, NULL) ? 0 : (bx & 0xFF);
}
static uint32_t fire_xmsb_far(const void* p) { uint16_t seg, off; fire_dos_farOf(p, &seg, &off); return ((uint32_t)seg << 16) | off; }
/* `count` bytes between a buffer (from `at`) and a block (from `offset`); toBlock: buffer -> block. 0 or the error code of the driver. (The driver moves even
 * lengths: an odd last byte goes through a word read from the block and written back.) */
static Value fire_xms_Move(Value handle, Value offset, Value buffer, Value at, Value count, Value toBlock) {
    Buf* b;
    uint32_t n = (uint32_t)count.i, even = n & ~(uint32_t)1, off = (uint32_t)offset.i;
    uint16_t h = (uint16_t)handle.i;
    uint8_t* data;
    int err = 0;
    if (buffer.kind != K_Buffer || !leafAlive(buffer)) return Int(-1);
    b = bufOf(buffer);
    if (at.i < 0 || count.i < 0 || (uint64_t)at.i + (uint64_t)count.i > bufSize(b)) return Int(-1);
    data = b->data + at.i;
    if (even) err = toBlock.i ? fire_xmsb_move(even, 0, fire_xmsb_far(data), h, off) : fire_xmsb_move(even, h, off, 0, fire_xmsb_far(data));
    if (!err && (n & 1)) {
        uint8_t word[2];
        err = fire_xmsb_move(2, h, off + even, 0, fire_xmsb_far(word));
        if (!err && toBlock.i) { word[0] = data[even]; err = fire_xmsb_move(2, 0, fire_xmsb_far(word), h, off + even); }
        else if (!err) data[even] = word[0];
    }
    return Int(err);
}

#endif
