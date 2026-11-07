/* fire native bridge "dos" (C): the natives behind `#import "dos"` (docs/stdlib/dos.md) - interrupts of DOS and the BIOS in real mode, through DOS of the
 * platform that comes with the package (fire_dos_*: platformSources of dos.fireproj). The checks and the exceptions are in the prelude. */
#ifndef FIRE_BRIDGE_DOS_H
#define FIRE_BRIDGE_DOS_H

#include <stdint.h>
#include <string.h>
#include "fire_dos_c.h"   /* (and before this file DOS of the platform: platformSources of dos.fireproj - the VM gets fire_dos_none.h) */

static Value fire_dos_Unavailable(OwnList* list) {
    const char* why = fire_dos_unavailable();
    return why ? newStrFromUtf8((const uint8_t*)why, strlen(why), list) : Undef();
}
/* INT `number` with AX BX CX DX SI DI DS ES: the registers after it and the flags, as an int[9] */
static Value fire_dos_Int(Value number, Value ax, Value bx, Value cx, Value dx, Value si, Value di, Value ds, Value es, OwnList* list) {
    uint16_t r[9];
    Arr* a;
    int i;
    r[0] = (uint16_t)ax.i; r[1] = (uint16_t)bx.i; r[2] = (uint16_t)cx.i; r[3] = (uint16_t)dx.i;
    r[4] = (uint16_t)si.i; r[5] = (uint16_t)di.i; r[6] = (uint16_t)ds.i; r[7] = (uint16_t)es.i; r[8] = 0;
    fire_dos_interrupt((int)(number.i & 0xFF), r);
    a = allocArr(9, list);
    for (i = 0; i < 9; i++) arrItems(a)[i] = Int((int64_t)r[i]);
    return ArrV(a);
}
/* where the bytes of a buffer are (from `offset` on): segment * 65536 + offset (normalized), for DS:DX and the like - valid while the buffer lives */
static Value fire_dos_FarOf(Value buffer, Value offset) {
    uint16_t seg, off;
    Buf* b;
    if (buffer.kind != K_Buffer || !leafAlive(buffer)) return Undef();
    b = bufOf(buffer);
    if (offset.i < 0 || (uint64_t)offset.i > bufSize(b)) return Undef();
    fire_dos_farOf(b->data + offset.i, &seg, &off);
    if (seg == 0 && off == 0) return Int(-1);   /* (above the first megabyte: dos32) */
    return Int(((int64_t)seg << 16) | off);
}

#endif
