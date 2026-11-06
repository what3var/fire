/* fire native bridge "machine" (C): the natives behind `#import "machine"` (docs/stdlib/machine.md) - ports, physical memory, the processor and hardware
 * interrupts of the PC, through the machine part of the platform layer (FIRE_PLATFORM_MACHINE_C_HEADER: fire_mach_*). The checks of the arguments and the
 * exceptions are in the prelude: a native that cannot do its work returns undefined (Peek of an address out of reach, an IRQ hooked twice). */
#ifndef FIRE_BRIDGE_MACHINE_H
#define FIRE_BRIDGE_MACHINE_H

#include <stdint.h>
#include <string.h>
#ifdef FIRE_LIBRARY
#include "platform/std/fire_machine_none.h"   /* (the VM: never the hardware) */
#else
#include FIRE_PLATFORM_MACHINE_C_HEADER
#endif

static int fire_machb_bits(Value bits) { return bits.i == 8 || bits.i == 16 ? (int)bits.i : 32; }

/* the reason the hardware is out of reach (a string), or undefined */
static Value fire_machine_Unavailable(OwnList* list) {
    const char* why = fire_mach_unavailable();
    return why ? newStrFromUtf8((const uint8_t*)why, strlen(why), list) : Undef();
}
static Value fire_machine_In(Value port, Value bits) { return Int((int64_t)(int32_t)fire_mach_in((unsigned)(port.i & 0xFFFF), fire_machb_bits(bits))); }
static Value fire_machine_Out(Value port, Value bits, Value v) { fire_mach_out((unsigned)(port.i & 0xFFFF), fire_machb_bits(bits), (uint32_t)v.i); return Undef(); }
static Value fire_machine_Peek(Value address, Value bits) {
    uint32_t v;
    if (!fire_mach_peek((uint32_t)address.i, fire_machb_bits(bits), &v)) return Undef();
    return Int(bits.i == 32 ? (int64_t)(int32_t)v : (int64_t)v);
}
static Value fire_machine_Poke(Value address, Value bits, Value v) { return Bool(fire_mach_poke((uint32_t)address.i, fire_machb_bits(bits), (uint32_t)v.i) != 0); }
/* `count` bytes from a physical address as a buffer (undefined: out of reach) */
static Value fire_machine_Read(Value address, Value count, OwnList* list) {
    Buf* b;
    uint32_t i, v, n = count.i > 0 ? (uint32_t)count.i : 0;
    if (n > 0 && !fire_mach_peek((uint32_t)address.i + n - 1, 8, &v)) return Undef();
    b = allocBuf(n, list);
    for (i = 0; i < n; i++) { fire_mach_peek((uint32_t)address.i + i, 8, &v); b->data[i] = (uint8_t)v; }
    return BufV(b);
}
static Value fire_machine_Write(Value address, Value data) {
    FireBytes bytes;
    uint32_t i, v;
    if (!bytesArg(data, "Machine.Memory.Write", &bytes)) return Undef();
    if (bytes.length > 0 && !fire_mach_peek((uint32_t)address.i + bytes.length - 1, 8, &v)) return Bool(false);
    for (i = 0; i < bytes.length; i++) fire_mach_poke((uint32_t)address.i + i, 8, bytes.data[i]);
    return Bool(true);
}
static Value fire_machine_CpuLevel(void) { return Int(fire_mach_cpuLevel()); }
/* EAX, EBX, ECX, EDX of CPUID as an int[] of 4 */
static Value fire_machine_Cpuid(Value leaf, Value sub, OwnList* list) {
    uint32_t r[4];
    Arr* a;
    int i;
    fire_mach_cpuid((uint32_t)leaf.i, (uint32_t)sub.i, r);
    a = allocArr(4, list);
    for (i = 0; i < 4; i++) arrItems(a)[i] = Int((int64_t)(int32_t)r[i]);
    return ArrV(a);
}
static Value fire_machine_Interrupts(Value on) { fire_mach_interrupts(on.kind == K_Bool ? on.i != 0 : 1); return Undef(); }
static Value fire_machine_IrqOpen(Value line, Value chain) { return Bool(fire_mach_irqOpen((int)line.i, chain.kind == K_Bool && chain.i != 0) != 0); }
static Value fire_machine_IrqClose(Value line) { fire_mach_irqClose((int)line.i); return Undef(); }
static Value fire_machine_IrqTake(Value line) { return Int((int64_t)fire_mach_irqTake((int)line.i)); }
/* waits up to `ms` (< 0: without end) for an interrupt of the line: how many came (0: none in the time); other fire threads go on meanwhile */
static Value fire_machine_IrqWait(Value line, Value ms) {
    uint32_t n = fire_mach_irqTake((int)line.i);
#if !defined(FIRE_LIBRARY)
    int64_t start = fire_plat_nowMs();
    while (n == 0 && (ms.i < 0 || fire_plat_nowMs() - start < ms.i)) {
#ifdef FIRE_THREADS
        gilRelease();
#endif
        fire_plat_sleepMs(1);
#ifdef FIRE_THREADS
        gilAcquire();
#endif
        n = fire_mach_irqTake((int)line.i);
    }
#else
    (void)ms;
#endif
    return Int((int64_t)n);
}

#endif
