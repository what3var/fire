/* fire native bridge "i2c" (C): the natives behind `#import "i2c"` (docs/NETWORK.md, "I2C"): the I2C bus as a controller - write to a device, read from it, write
 * then read with a repeated start, look which addresses answer. The fire side (I2c.Bus, I2c.Board, I2c.Sim - fire source) is the same in the VM and in a native
 * build; this file is what its `__I2c...` functions do.
 *
 * This file is the one implementation of the i2c natives, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual machine
 * runs it in a shared library built from it (native/abi/fire_pkg_abi.h). The buses come from the platform layer (FIRE_PLATFORM_I2C_C_HEADER: fire_i2c_*, see
 * platform/std/fire_i2c_c.h): /dev/i2c-N on a Linux board, the master driver of ESP-IDF on an ESP32. Where there is no hardware the list of buses is empty - but
 * one bus is always there:
 *
 *   "sim"   a simulated bus with devices that the program (or a test) puts on it: each device has a register file of 256 bytes that behaves like a typical sensor
 *           chip - the first byte written is the register number, more bytes are stored from there on (the number counts up), a read returns the bytes from the
 *           current register on (counting up). An address with no device does not answer (NoAck), as on a real bus. For tests, and to develop a program on a PC
 *           that later talks to the real device.
 *
 * A bus is an integer handle in a table of this bridge (handles are not used again). Every function reports an error the same way: a result of
 * -1/false/undefined and the code and message of this thread (`__I2cLastError`, `__I2cLastErrorMessage`, the slot of the io bridge), from which the fire code
 * throws a typed exception. A transfer is short (a few bytes at 100 kHz take a millisecond), so it is done in the call and not polled. */
#ifndef FIRE_BRIDGE_I2C_H
#define FIRE_BRIDGE_I2C_H

#include <stdarg.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include FIRE_PLATFORM_I2C_C_HEADER

/* (the codes are those of the platform layer: FIRE_I2C_E_*, the same numbers as I2c.I2cException.code) */

static int64_t fire_i2cb_fail(int code, const char* message) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s", message);
    return -1;
}
static int64_t fire_i2cb_failf(int code, const char* format, ...) {
    char text[176];
    va_list args;
    va_start(args, format);
    vsnprintf(text, sizeof text, format, args);
    va_end(args);
    return fire_i2cb_fail(code, text);
}
static void fire_i2cb_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }
static int64_t fire_i2cb_failStatus(const FireI2cStatus* s) { return fire_i2cb_fail(s->code, s->message); }

/* ---- text ---------------------------------------------------------------------------------------------------------------------------------- */
/* A fire string as UTF-8 (malloc; the caller frees it; NULL when out of memory); a lone surrogate becomes U+FFFD. */
static char* fire_i2cb_utf8(Value v) {
    const Str* s;
    char* out;
    size_t n = 0;
    uint32_t i;
    if (v.kind != K_String) { out = (char*)malloc(1); if (out) out[0] = 0; return out; }
    s = strOf(v);
    out = (char*)malloc((size_t)s->length * 3 + 1);
    if (out == NULL) return NULL;
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xC0 | (c >> 6)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { out[n++] = (char)(0xE0 | (c >> 12)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else { out[n++] = (char)(0xF0 | (c >> 18)); out[n++] = (char)(0x80 | ((c >> 12) & 0x3F)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[n] = 0;
    return out;
}
/* (names and messages are ASCII or UTF-8 from the system; a byte above 127 becomes the Latin-1 character of the same number) */
static Value fire_i2cb_str8(const char* text, OwnList* list) {
    size_t n = strlen(text), i;
    Str* s = allocStr((uint32_t)n, list);
    for (i = 0; i < n; i++) strChars(s)[i] = (fire_char16)(uint8_t)text[i];
    return StrV(s);
}
/* "0x48" (the text has room for 16 bytes) */
static const char* fire_i2cb_hex(char* text, int64_t address) {
    snprintf(text, 16, "0x%02llX", (unsigned long long)(address & 0xFF));
    return text;
}

/* ---- the simulated bus ----------------------------------------------------------------------------------------------------------------------- */
enum { FIRE_I2CB_SIM_ADDRESSES = 128, FIRE_I2CB_SIM_REGISTERS = 256 };
typedef struct FireI2cSimDevice {
    int present;
    uint8_t regs[FIRE_I2CB_SIM_REGISTERS];
    int pointer;     /* the register the next byte is written to / read from */
} FireI2cSimDevice;
typedef struct FireI2cSim {
    FireI2cSimDevice device[FIRE_I2CB_SIM_ADDRESSES];
} FireI2cSim;

static FireI2cSim g_i2cSim;   /* (all zero: no device on the bus) */

/* A write: the first byte selects the register, the others are stored from there on (an empty write only asks "is anybody there"). */
static void fire_i2cb_simWrite(FireI2cSimDevice* d, const uint8_t* bytes, int n) {
    int i;
    if (n == 0) return;
    d->pointer = bytes[0];
    for (i = 1; i < n; i++) {
        d->regs[d->pointer] = bytes[i];
        d->pointer = (d->pointer + 1) % FIRE_I2CB_SIM_REGISTERS;
    }
}
static void fire_i2cb_simRead(FireI2cSimDevice* d, uint8_t* bytes, int n) {
    int i;
    for (i = 0; i < n; i++) {
        bytes[i] = d->regs[d->pointer];
        d->pointer = (d->pointer + 1) % FIRE_I2CB_SIM_REGISTERS;
    }
}

/* ---- the buses ------------------------------------------------------------------------------------------------------------------------------- */
typedef struct FireI2cBusEntry {
    int isSim;
    FireI2cBus* bus;
    char* name;
} FireI2cBusEntry;

static void fire_i2cb_release(FireI2cBusEntry* b) {
    if (b == NULL) return;
    if (b->bus) fire_i2c_closeBus(b->bus);
    free(b->name);
    free(b);
}

/* The open buses; the handle is the index (0 is not used). */
static FireI2cBusEntry** g_i2cBuses = NULL;
static size_t g_i2cBusCount = 1, g_i2cBusCap = 0;

/* The program has ended (the library for the VM stays loaded for the next one): closes the buses and takes the devices off the simulated bus. */
static void fire_i2c_reset(void) {
    size_t i;
    for (i = 1; i < g_i2cBusCount; i++) {
        fire_i2cb_release(g_i2cBuses[i]);
        g_i2cBuses[i] = NULL;
    }
    memset(&g_i2cSim, 0, sizeof g_i2cSim);
}

/* Puts a bus into the table: the handle (-1: out of memory, the bus is released). */
static int64_t fire_i2cb_add(FireI2cBusEntry* b) {
    if (g_i2cBusCount >= g_i2cBusCap) {
        size_t cap = g_i2cBusCap ? g_i2cBusCap * 2 : 16;
        FireI2cBusEntry** grown = (FireI2cBusEntry**)realloc(g_i2cBuses, sizeof(FireI2cBusEntry*) * cap);
        if (grown == NULL) { fire_i2cb_release(b); return fire_i2cb_fail(FIRE_I2C_E_OTHER, "Out of memory."); }
        if (g_i2cBusCap == 0) grown[0] = NULL;
        g_i2cBuses = grown;
        g_i2cBusCap = cap;
    }
    g_i2cBuses[g_i2cBusCount] = b;
    return (int64_t)g_i2cBusCount++;
}
static FireI2cBusEntry* fire_i2cb_find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < g_i2cBusCount && g_i2cBuses[i] != NULL) return g_i2cBuses[i];
    fire_i2cb_fail(FIRE_I2C_E_INVALID_HANDLE, "Invalid or already closed bus handle.");
    return NULL;
}
static int fire_i2cb_checkAddress(int64_t address) {
    if (address < 0 || address > 127) { fire_i2cb_failf(FIRE_I2C_E_INVALID_ARGUMENT, "Invalid address %lld (7 bits: 0..127).", (long long)address); return 0; }
    return 1;
}
static int fire_i2cb_checkRange(int64_t length, int64_t offset, int64_t count) {
    if (offset < 0 || count < 0 || offset > length || count > length - offset) {
        fire_i2cb_failf(FIRE_I2C_E_INVALID_ARGUMENT, "offset/count (%lld/%lld) are outside of the buffer (length %lld).", (long long)offset, (long long)count, (long long)length);
        return 0;
    }
    return 1;
}
static int fire_i2cb_checkCount(int64_t count) {
    if (count > 65535) { fire_i2cb_fail(FIRE_I2C_E_INVALID_ARGUMENT, "A transfer is at most 65535 bytes."); return 0; }
    return 1;
}
static int64_t fire_i2cb_noAck(int64_t address) {
    char hex[16];
    return fire_i2cb_failf(FIRE_I2C_E_NO_ACK, "No device answered at address %s.", fire_i2cb_hex(hex, address));
}

/* ---- natives --------------------------------------------------------------------------------------------------------------------------------- */
static Value fire_i2c_LastError(void) { return Int(g_ioError.code); }
static Value fire_i2c_LastErrorMessage(OwnList* list) { return fire_i2cb_str8(g_ioError.message, list); }
static Value fire_i2c_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 1; i < g_i2cBusCount; i++) if (g_i2cBuses[i] != NULL) n++;
    return Int(n);
}
/* 1 if this machine has I2C hardware (a driver for it and at least one bus; the simulated bus is there anyway), else 0. */
static Value fire_i2c_Supported(void) {
    FireI2cList hardware = { NULL, 0, 0 };
    FireI2cStatus st;
    int yes = 0;
    fire_i2cb_ok();
    if (fire_i2c_supported()) yes = fire_i2c_buses(&hardware, &st) && hardware.count > 0;
    fire_i2c_listFree(&hardware);
    return Int(yes ? 1 : 0);
}

/* The buses: "sim" first, then those of the hardware. */
static Value fire_i2c_Buses(OwnList* list) {
    FireI2cList hardware = { NULL, 0, 0 };
    FireI2cStatus st;
    Arr* a;
    size_t i;
    if (!fire_i2c_buses(&hardware, &st)) { fire_i2c_listFree(&hardware); fire_i2cb_failStatus(&st); return Undef(); }
    fire_i2cb_ok();
    a = allocArr((uint32_t)(hardware.count + 1), list);
    arrItems(a)[0] = fire_i2cb_str8("sim", list);
    retain(arrItems(a)[0]);
    for (i = 0; i < hardware.count; i++) { arrItems(a)[i + 1] = fire_i2cb_str8(hardware.items[i], list); retain(arrItems(a)[i + 1]); }
    fire_i2c_listFree(&hardware);
    return ArrV(a);
}

static Value fire_i2c_Open(Value name, Value speed) {
    FireI2cBusEntry* b;
    int64_t handle;
    char* n = fire_i2cb_utf8(name);
    if (n == NULL) return Int(fire_i2cb_fail(FIRE_I2C_E_OTHER, "Out of memory."));
    if (speed.i < 1000 || speed.i > 5000000) { free(n); return Int(fire_i2cb_fail(FIRE_I2C_E_INVALID_ARGUMENT, "The speed must be between 1 kHz and 5 MHz.")); }
    b = (FireI2cBusEntry*)calloc(1, sizeof(FireI2cBusEntry));
    if (b == NULL) { free(n); return Int(fire_i2cb_fail(FIRE_I2C_E_OTHER, "Out of memory.")); }
    b->name = n;
    if (strcmp(n, "sim") == 0) b->isSim = 1;
    else {
        FireI2cStatus st;
        b->bus = fire_i2c_openBus(n, (int)speed.i, &st);
        if (b->bus == NULL) { fire_i2cb_release(b); return Int(fire_i2cb_failStatus(&st)); }
    }
    handle = fire_i2cb_add(b);
    if (handle < 0) return Int(-1);
    fire_i2cb_ok();
    return Int(handle);
}

/* Sets the clock speed in Hz (the simulated bus accepts any; Linux leaves it to the device tree). */
static Value fire_i2c_SetSpeed(Value h, Value speed) {
    FireI2cBusEntry* b = fire_i2cb_find(h);
    if (b == NULL) return Bool(false);
    if (speed.i < 1000 || speed.i > 5000000) { fire_i2cb_fail(FIRE_I2C_E_INVALID_ARGUMENT, "The speed must be between 1 kHz and 5 MHz."); return Bool(false); }
    if (!b->isSim) {
        FireI2cStatus st;
        if (!fire_i2c_setSpeed(b->bus, (int)speed.i, &st)) { fire_i2cb_failStatus(&st); return Bool(false); }
    }
    fire_i2cb_ok();
    return Bool(true);
}

/* Writes `count` bytes to the device; the number written, -1 on an error (NoAck: nobody answered). */
static Value fire_i2c_Write(Value h, Value address, Value buffer, Value offset, Value count) {
    FireI2cBusEntry* b;
    const uint8_t* bytes;
    FireBytes in;
    if (!bytesArg(buffer, "I2c.Device.Write", &in)) return Undef();
    if (!fire_i2cb_checkAddress(address.i) || !fire_i2cb_checkRange(in.length, offset.i, count.i) || !fire_i2cb_checkCount(count.i)) return Int(-1);
    b = fire_i2cb_find(h);
    if (b == NULL) return Int(-1);
    bytes = in.data + offset.i;
    if (b->isSim) {
        FireI2cSimDevice* d = &g_i2cSim.device[address.i];
        if (!d->present) return Int(fire_i2cb_noAck(address.i));
        fire_i2cb_simWrite(d, bytes, (int)count.i);
    } else {
        FireI2cStatus st;
        if (!fire_i2c_write(b->bus, (int)address.i, bytes, (int)count.i, &st)) return Int(fire_i2cb_failStatus(&st));
    }
    fire_i2cb_ok();
    return Int(count.i);
}

/* Reads `count` bytes from the device; the number read, -1 on an error. */
static Value fire_i2c_Read(Value h, Value address, Value buffer, Value offset, Value count) {
    FireI2cBusEntry* b;
    uint8_t* bytes;
    Buf* target = bufferArg(buffer, "I2c.Device.Read");
    if (target == NULL) return Undef();
    if (!fire_i2cb_checkAddress(address.i) || !fire_i2cb_checkRange(target->length, offset.i, count.i) || !fire_i2cb_checkCount(count.i)) return Int(-1);
    b = fire_i2cb_find(h);
    if (b == NULL) return Int(-1);
    bytes = target->data + offset.i;
    if (b->isSim) {
        FireI2cSimDevice* d = &g_i2cSim.device[address.i];
        if (!d->present) return Int(fire_i2cb_noAck(address.i));
        fire_i2cb_simRead(d, bytes, (int)count.i);
    } else {
        FireI2cStatus st;
        if (!fire_i2c_read(b->bus, (int)address.i, bytes, (int)count.i, &st)) return Int(fire_i2cb_failStatus(&st));
    }
    fire_i2cb_ok();
    return Int(count.i);
}

/* Writes, then reads with a repeated start in between (the usual way to read a register): the number read, -1 on an error. */
static Value fire_i2c_WriteRead(Value h, Value address, Value outBuffer, Value outOffset, Value outCount, Value inBuffer, Value inOffset, Value inCount) {
    FireI2cBusEntry* b;
    const uint8_t* out;
    uint8_t* in;
    FireBytes sent;
    Buf* target;
    if (!bytesArg(outBuffer, "I2c.Device.WriteRead", &sent)) return Undef();
    if ((target = bufferArg(inBuffer, "I2c.Device.WriteRead")) == NULL) return Undef();
    if (!fire_i2cb_checkAddress(address.i) || !fire_i2cb_checkRange(sent.length, outOffset.i, outCount.i) || !fire_i2cb_checkRange(target->length, inOffset.i, inCount.i)
        || !fire_i2cb_checkCount(outCount.i) || !fire_i2cb_checkCount(inCount.i)) return Int(-1);
    b = fire_i2cb_find(h);
    if (b == NULL) return Int(-1);
    out = sent.data + outOffset.i;
    in = target->data + inOffset.i;
    if (b->isSim) {
        FireI2cSimDevice* d = &g_i2cSim.device[address.i];
        if (!d->present) return Int(fire_i2cb_noAck(address.i));
        fire_i2cb_simWrite(d, out, (int)outCount.i);
        fire_i2cb_simRead(d, in, (int)inCount.i);
    } else {
        FireI2cStatus st;
        if (!fire_i2c_writeRead(b->bus, (int)address.i, out, (int)outCount.i, in, (int)inCount.i, &st)) return Int(fire_i2cb_failStatus(&st));
    }
    fire_i2cb_ok();
    return Int(inCount.i);
}

/* 1 if a device answers at the address, 0 if not; -1 on an error. */
static Value fire_i2c_Probe(Value h, Value address) {
    FireI2cBusEntry* b;
    int present = 0;
    if (!fire_i2cb_checkAddress(address.i)) return Int(-1);
    b = fire_i2cb_find(h);
    if (b == NULL) return Int(-1);
    if (b->isSim) present = g_i2cSim.device[address.i].present;
    else {
        FireI2cStatus st;
        if (!fire_i2c_probe(b->bus, (int)address.i, &present, &st)) return Int(fire_i2cb_failStatus(&st));
    }
    fire_i2cb_ok();
    return Int(present ? 1 : 0);
}

static Value fire_i2c_Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= g_i2cBusCount || g_i2cBuses[i] == NULL) { fire_i2cb_fail(FIRE_I2C_E_INVALID_HANDLE, "Invalid or already closed bus handle."); return Bool(false); }
    fire_i2cb_release(g_i2cBuses[i]);
    g_i2cBuses[i] = NULL;
    fire_i2cb_ok();
    return Bool(true);
}

/* ---- the simulated bus from the outside ------------------------------------------------------------------------------------------------------ */
/* Puts a device on the simulated bus (its registers are all 0); an existing one is replaced. */
static Value fire_i2c_SimAdd(Value address) {
    if (!fire_i2cb_checkAddress(address.i)) return Bool(false);
    memset(&g_i2cSim.device[address.i], 0, sizeof(FireI2cSimDevice));
    g_i2cSim.device[address.i].present = 1;
    fire_i2cb_ok();
    return Bool(true);
}
static Value fire_i2c_SimRemove(Value address) {
    if (!fire_i2cb_checkAddress(address.i)) return Bool(false);
    memset(&g_i2cSim.device[address.i], 0, sizeof(FireI2cSimDevice));
    fire_i2cb_ok();
    return Bool(true);
}
static FireI2cSimDevice* fire_i2cb_simDevice(Value address, Value reg) {
    FireI2cSimDevice* d;
    if (!fire_i2cb_checkAddress(address.i)) return NULL;
    if (reg.i < 0 || reg.i >= FIRE_I2CB_SIM_REGISTERS) { fire_i2cb_failf(FIRE_I2C_E_INVALID_ARGUMENT, "Invalid register %lld (0..255).", (long long)reg.i); return NULL; }
    d = &g_i2cSim.device[address.i];
    if (!d->present) {
        char hex[16];
        fire_i2cb_failf(FIRE_I2C_E_NO_ACK, "There is no simulated device at address %s.", fire_i2cb_hex(hex, address.i));
        return NULL;
    }
    return d;
}
/* Sets a register of a simulated device from the outside (a sensor value that changed). */
static Value fire_i2c_SimSetRegister(Value address, Value reg, Value value) {
    FireI2cSimDevice* d = fire_i2cb_simDevice(address, reg);
    if (d == NULL) return Bool(false);
    d->regs[reg.i] = (uint8_t)(value.i & 0xFF);
    fire_i2cb_ok();
    return Bool(true);
}
/* A register of a simulated device (what a program wrote there); -1 on an error. */
static Value fire_i2c_SimGetRegister(Value address, Value reg) {
    FireI2cSimDevice* d = fire_i2cb_simDevice(address, reg);
    if (d == NULL) return Int(-1);
    fire_i2cb_ok();
    return Int(d->regs[reg.i]);
}
/* Takes all devices off the simulated bus (open buses stay open). */
static Value fire_i2c_SimReset(void) {
    memset(&g_i2cSim, 0, sizeof g_i2cSim);
    fire_i2cb_ok();
    return Bool(true);
}

#endif
