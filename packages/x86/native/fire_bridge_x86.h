/* fire native bridge "x86" (C): the natives behind `#import "x86"` (docs/stdlib/x86.md) - the modes of the x86 processor, through the platform file of the
 * package (fire_x86_*: platformSources of x86.fireproj). The checks and the exceptions are in the prelude. */
#ifndef FIRE_BRIDGE_X86_H
#define FIRE_BRIDGE_X86_H

#include <stdint.h>
#include <string.h>
#include "fire_x86_c.h"   /* (and before this file the platform's: platformSources of x86.fireproj - the VM gets fire_x86_none.h) */

static Value fire_x86_Unavailable(OwnList* list) {
    const char* why = fire_x86_unavailable();
    return why ? newStrFromUtf8((const uint8_t*)why, strlen(why), list) : Undef();
}
static Value fire_x86_Mode(OwnList* list) {
    const char* m = fire_x86_mode();
    return newStrFromUtf8((const uint8_t*)m, strlen(m), list);
}
static Value fire_x86_A20(void) { return Bool(fire_x86_a20() != 0); }
/* the routine at the linear address `code` with EAX..EDI: [done, eax, ebx, ecx, edx, esi, edi] */
static Value fire_x86_Run(Value code, Value eax, Value ebx, Value ecx, Value edx, Value esi, Value edi, OwnList* list) {
    uint32_t r[6];
    Arr* a;
    int i, done;
    r[0] = (uint32_t)eax.i; r[1] = (uint32_t)ebx.i; r[2] = (uint32_t)ecx.i; r[3] = (uint32_t)edx.i; r[4] = (uint32_t)esi.i; r[5] = (uint32_t)edi.i;
    done = fire_x86_runProtected((uint32_t)code.i, r);
    a = allocArr(7, list);
    arrItems(a)[0] = Bool(done != 0);
    for (i = 0; i < 6; i++) arrItems(a)[i + 1] = Int((int64_t)(int32_t)r[i]);
    return ArrV(a);
}
static Value fire_x86_CanLong(void) { return Bool(fire_x86_canLong() != 0); }
/* the same for a 64-bit routine in the long mode */
static Value fire_x86_RunLong(Value code, Value eax, Value ebx, Value ecx, Value edx, Value esi, Value edi, OwnList* list) {
    uint32_t r[6];
    Arr* a;
    int i, done;
    r[0] = (uint32_t)eax.i; r[1] = (uint32_t)ebx.i; r[2] = (uint32_t)ecx.i; r[3] = (uint32_t)edx.i; r[4] = (uint32_t)esi.i; r[5] = (uint32_t)edi.i;
    done = fire_x86_runLong((uint32_t)code.i, r);
    a = allocArr(7, list);
    arrItems(a)[0] = Bool(done != 0);
    for (i = 0; i < 6; i++) arrItems(a)[i + 1] = Int((int64_t)(int32_t)r[i]);
    return ArrV(a);
}
/* the linear address of the bytes of a byte buffer from `offset` on (undefined: no such place) - valid while the buffer lives and keeps its size */
static Value fire_x86_LinearOf(Value buffer, Value offset) {
    Buf* b;
    if (buffer.kind != K_Buffer || !leafAlive(buffer)) return Undef();
    b = bufOf(buffer);
    if (b->elem != 1 || offset.i < 0 || (uint64_t)offset.i > bufSize(b)) return Undef();
    return Int((int64_t)(int32_t)fire_x86_linearOf(b->data + offset.i));
}

#endif
