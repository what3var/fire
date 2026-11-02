/* fire native bridge "spi" (C): the natives behind `#import "spi"` (docs/NETWORK.md, "SPI"): the SPI bus as a controller - a device is a bus with one chip
 * select; a transfer sends and receives bytes at the same time. The fire side (Spi.Device, Spi.Board, Spi.Sim - fire source) is the same in the VM and in a
 * native build; this file is what its `__Spi...` functions do.
 *
 * This file is the one implementation of the spi natives, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual machine
 * runs it in a shared library built from it (native/abi/fire_pkg_abi.h). The devices come from the platform layer (FIRE_PLATFORM_SPI_C_HEADER: fire_spi_*, see
 * platform/std/fire_spi_c.h): /dev/spidevB.C on a Linux board, the SPI master driver of ESP-IDF on an ESP32. Where there is no hardware the list of devices is
 * empty - but one device is always there:
 *
 *   "sim"   a simulated device. By default it is a loopback (MISO is tied to MOSI: what is sent comes back), which is what the usual wiring test does. `Reply`
 *           makes it a device that answers: the bytes that were queued come back one per byte sent (zeros when the queue is empty); `Loopback` turns the
 *           loopback on again. Everything the program sent is kept (`SentCount`/`SentByte`), the mode and speed that were set can be asked. For tests, and to
 *           develop a program on a PC that later talks to the real chip.
 *
 * A device is an integer handle in a table of this bridge (handles are not used again). Every function reports an error the same way: a result of
 * -1/false/undefined and the code and message of this thread (`__SpiLastError`, `__SpiLastErrorMessage`, the slot of the io bridge), from which the fire code
 * throws a typed exception. A transfer is done in the call. */
#ifndef FIRE_BRIDGE_SPI_H
#define FIRE_BRIDGE_SPI_H

#include <stdarg.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include FIRE_PLATFORM_SPI_C_HEADER

/* (the codes are those of the platform layer: FIRE_SPI_E_*, the same numbers as Spi.SpiException.code) */

static int64_t fire_spib_fail(int code, const char* message) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s", message);
    return -1;
}
static int64_t fire_spib_failf(int code, const char* format, ...) {
    char text[176];
    va_list args;
    va_start(args, format);
    vsnprintf(text, sizeof text, format, args);
    va_end(args);
    return fire_spib_fail(code, text);
}
static void fire_spib_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }
static int64_t fire_spib_failStatus(const FireSpiStatus* s) { return fire_spib_fail(s->code, s->message); }

/* ---- text ---------------------------------------------------------------------------------------------------------------------------------- */
/* A fire string as UTF-8 (malloc; the caller frees it; NULL when out of memory); a lone surrogate becomes U+FFFD. */
static char* fire_spib_utf8(Value v) {
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
static Value fire_spib_str8(const char* text, OwnList* list) {
    size_t n = strlen(text), i;
    Str* s = allocStr((uint32_t)n, list);
    for (i = 0; i < n; i++) strChars(s)[i] = (fire_char16)(uint8_t)text[i];
    return StrV(s);
}

/* ---- the simulated device -------------------------------------------------------------------------------------------------------------------- */
enum { FIRE_SPIB_SENT_LIMIT = 65536 };
typedef struct FireSpiSim {
    int loopback;
    uint8_t* replies;       /* the queued answers: replies[replyHead .. replyCount) */
    size_t replyHead, replyCount, replyCap;
    uint8_t* sent;          /* what the program sent (up to FIRE_SPIB_SENT_LIMIT bytes) */
    size_t sentCount, sentCap;
    int mode;
    int speed;
    int lsb;
    int64_t transfers;
    int64_t total;          /* bytes sent since the start (not limited like `sent`) */
    int last;               /* the last byte sent (-1: none yet) */
} FireSpiSim;

static FireSpiSim g_spiSim = { 1, NULL, 0, 0, 0, NULL, 0, 0, 0, 0, 0, 0, 0, -1 };

static void fire_spib_simClearReplies(FireSpiSim* s) { s->replyHead = s->replyCount = 0; }
static void fire_spib_simClearSent(FireSpiSim* s) { s->sentCount = 0; }
/* Queues an answer (dropped when the memory is gone). */
static void fire_spib_simQueue(FireSpiSim* s, uint8_t b) {
    if (s->replyCount == s->replyCap) {
        if (s->replyHead > 0) {   /* (the answers that were taken make room first) */
            memmove(s->replies, s->replies + s->replyHead, s->replyCount - s->replyHead);
            s->replyCount -= s->replyHead;
            s->replyHead = 0;
        }
        if (s->replyCount == s->replyCap) {
            size_t cap = s->replyCap ? s->replyCap * 2 : 64;
            uint8_t* grown = (uint8_t*)realloc(s->replies, cap);
            if (grown == NULL) return;
            s->replies = grown;
            s->replyCap = cap;
        }
    }
    s->replies[s->replyCount++] = b;
}
/* The simulated chip as it starts: a loopback, nothing sent, nothing queued (the memory is given back). */
static void fire_spib_simInit(FireSpiSim* s) {
    free(s->replies);
    free(s->sent);
    memset(s, 0, sizeof *s);
    s->loopback = 1;
    s->last = -1;
}
static void fire_spib_simRun(FireSpiSim* s, const uint8_t* out, uint8_t* in, int n) {
    int i;
    s->transfers++;
    for (i = 0; i < n; i++) {
        uint8_t o = out ? out[i] : 0;
        uint8_t r;
        if (s->sentCount < FIRE_SPIB_SENT_LIMIT) {
            if (s->sentCount == s->sentCap) {
                size_t cap = s->sentCap ? s->sentCap * 2 : 256;
                uint8_t* grown;
                if (cap > FIRE_SPIB_SENT_LIMIT) cap = FIRE_SPIB_SENT_LIMIT;
                grown = (uint8_t*)realloc(s->sent, cap);
                if (grown != NULL) { s->sent = grown; s->sentCap = cap; }
            }
            if (s->sentCount < s->sentCap) s->sent[s->sentCount++] = o;
        }
        s->total++;
        s->last = o;
        r = o;
        if (!s->loopback) {
            r = 0;
            if (s->replyHead < s->replyCount) { r = s->replies[s->replyHead++]; if (s->replyHead == s->replyCount) fire_spib_simClearReplies(s); }
        }
        if (in) in[i] = r;
    }
}

/* ---- the devices ----------------------------------------------------------------------------------------------------------------------------- */
typedef struct FireSpiEntry {
    int isSim;
    FireSpiDevice* device;
    char* name;
    int mode;
    int speed;
    int lsb;
} FireSpiEntry;

static void fire_spib_release(FireSpiEntry* d) {
    if (d == NULL) return;
    if (d->device) fire_spi_closeDevice(d->device);
    free(d->name);
    free(d);
}

/* The open devices; the handle is the index (0 is not used). */
static FireSpiEntry** g_spiDevices = NULL;
static size_t g_spiDeviceCount = 1, g_spiDeviceCap = 0;

/* The program has ended (the library for the VM stays loaded for the next one): closes the devices and resets the simulated one. */
static void fire_spi_reset(void) {
    size_t i;
    for (i = 1; i < g_spiDeviceCount; i++) {
        fire_spib_release(g_spiDevices[i]);
        g_spiDevices[i] = NULL;
    }
    fire_spib_simInit(&g_spiSim);
}

/* Puts a device into the table: the handle (-1: out of memory, the device is released). */
static int64_t fire_spib_add(FireSpiEntry* d) {
    if (g_spiDeviceCount >= g_spiDeviceCap) {
        size_t cap = g_spiDeviceCap ? g_spiDeviceCap * 2 : 16;
        FireSpiEntry** grown = (FireSpiEntry**)realloc(g_spiDevices, sizeof(FireSpiEntry*) * cap);
        if (grown == NULL) { fire_spib_release(d); return fire_spib_fail(FIRE_SPI_E_OTHER, "Out of memory."); }
        if (g_spiDeviceCap == 0) grown[0] = NULL;
        g_spiDevices = grown;
        g_spiDeviceCap = cap;
    }
    g_spiDevices[g_spiDeviceCount] = d;
    return (int64_t)g_spiDeviceCount++;
}
static FireSpiEntry* fire_spib_find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < g_spiDeviceCount && g_spiDevices[i] != NULL) return g_spiDevices[i];
    fire_spib_fail(FIRE_SPI_E_INVALID_HANDLE, "Invalid or already closed device handle.");
    return NULL;
}
static int fire_spib_checkSetup(int64_t mode, int64_t speed) {
    if (mode < 0 || mode > 3) { fire_spib_fail(FIRE_SPI_E_INVALID_ARGUMENT, "The SPI mode is 0..3 (clock polarity and phase)."); return 0; }
    if (speed < 1 || speed > 80000000) { fire_spib_fail(FIRE_SPI_E_INVALID_ARGUMENT, "The speed must be between 1 Hz and 80 MHz."); return 0; }
    return 1;
}
static int fire_spib_checkRange(Value bufferValue, int64_t offset, int64_t count) {
    int64_t length = bufOf(bufferValue)->length;
    if (offset < 0 || count < 0 || offset > length || count > length - offset) {
        fire_spib_failf(FIRE_SPI_E_INVALID_ARGUMENT, "offset/count (%lld/%lld) are outside of the buffer (length %lld).", (long long)offset, (long long)count, (long long)length);
        return 0;
    }
    return 1;
}

/* ---- natives --------------------------------------------------------------------------------------------------------------------------------- */
static Value fire_spi_LastError(void) { return Int(g_ioError.code); }
static Value fire_spi_LastErrorMessage(OwnList* list) { return fire_spib_str8(g_ioError.message, list); }
static Value fire_spi_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 1; i < g_spiDeviceCount; i++) if (g_spiDevices[i] != NULL) n++;
    return Int(n);
}
/* 1 if this machine has SPI hardware (a driver for it and at least one device; the simulated device is there anyway), else 0. */
static Value fire_spi_Supported(void) {
    FireSpiList hardware = { NULL, 0, 0 };
    FireSpiStatus st;
    int yes = 0;
    fire_spib_ok();
    if (fire_spi_supported()) yes = fire_spi_devices(&hardware, &st) && hardware.count > 0;
    fire_spi_listFree(&hardware);
    return Int(yes ? 1 : 0);
}

/* The devices: "sim" first, then those of the hardware. */
static Value fire_spi_Devices(OwnList* list) {
    FireSpiList hardware = { NULL, 0, 0 };
    FireSpiStatus st;
    Arr* a;
    size_t i;
    if (!fire_spi_devices(&hardware, &st)) { fire_spi_listFree(&hardware); fire_spib_failStatus(&st); return Undef(); }
    fire_spib_ok();
    a = allocArr((uint32_t)(hardware.count + 1), list);
    arrItems(a)[0] = fire_spib_str8("sim", list);
    retain(arrItems(a)[0]);
    for (i = 0; i < hardware.count; i++) { arrItems(a)[i + 1] = fire_spib_str8(hardware.items[i], list); retain(arrItems(a)[i + 1]); }
    fire_spi_listFree(&hardware);
    return ArrV(a);
}

/* Opens a device with mode 0..3, a clock in Hz and the bit order (lsb 1: least significant bit first). */
static Value fire_spi_Open(Value name, Value mode, Value speed, Value lsb) {
    FireSpiEntry* d;
    char* n;
    int64_t handle;
    if (!fire_spib_checkSetup(mode.i, speed.i)) return Int(-1);
    n = fire_spib_utf8(name);
    if (n == NULL) return Int(fire_spib_fail(FIRE_SPI_E_OTHER, "Out of memory."));
    d = (FireSpiEntry*)calloc(1, sizeof(FireSpiEntry));
    if (d == NULL) { free(n); return Int(fire_spib_fail(FIRE_SPI_E_OTHER, "Out of memory.")); }
    d->name = n;
    d->mode = (int)mode.i;
    d->speed = (int)speed.i;
    d->lsb = lsb.i != 0;
    if (strcmp(n, "sim") == 0) {
        d->isSim = 1;
        g_spiSim.mode = d->mode;
        g_spiSim.speed = d->speed;
        g_spiSim.lsb = d->lsb;
    } else {
        FireSpiStatus st;
        d->device = fire_spi_openDevice(n, d->mode, d->speed, d->lsb, &st);
        if (d->device == NULL) { fire_spib_release(d); return Int(fire_spib_failStatus(&st)); }
    }
    handle = fire_spib_add(d);
    if (handle < 0) return Int(-1);
    fire_spib_ok();
    return Int(handle);
}

static Value fire_spi_Configure(Value h, Value mode, Value speed, Value lsb) {
    FireSpiEntry* d = fire_spib_find(h);
    if (d == NULL) return Bool(false);
    if (!fire_spib_checkSetup(mode.i, speed.i)) return Bool(false);
    if (d->isSim) {
        g_spiSim.mode = (int)mode.i;
        g_spiSim.speed = (int)speed.i;
        g_spiSim.lsb = lsb.i != 0;
    } else {
        FireSpiStatus st;
        if (!fire_spi_configure(d->device, (int)mode.i, (int)speed.i, lsb.i != 0, &st)) { fire_spib_failStatus(&st); return Bool(false); }
    }
    d->mode = (int)mode.i;
    d->speed = (int)speed.i;
    d->lsb = lsb.i != 0;
    fire_spib_ok();
    return Bool(true);
}

static int64_t fire_spib_run(FireSpiEntry* d, const uint8_t* out, uint8_t* in, int64_t count) {
    if (d->isSim) fire_spib_simRun(&g_spiSim, out, in, (int)count);
    else {
        FireSpiStatus st;
        if (!fire_spi_transfer(d->device, out, in, (int)count, &st)) return fire_spib_failStatus(&st);
    }
    fire_spib_ok();
    return count;
}

/* Sends `count` bytes and receives `count` bytes at the same time; the number of bytes, -1 on an error. */
static Value fire_spi_Transfer(Value h, Value outBuffer, Value outOffset, Value inBuffer, Value inOffset, Value count) {
    FireSpiEntry* d;
    if (!leafAlive(outBuffer)) return destroyedError(outBuffer);
    if (!leafAlive(inBuffer)) return destroyedError(inBuffer);
    if (!fire_spib_checkRange(outBuffer, outOffset.i, count.i) || !fire_spib_checkRange(inBuffer, inOffset.i, count.i)) return Int(-1);
    d = fire_spib_find(h);
    if (d == NULL) return Int(-1);
    /* (the buffers may be the same: what comes in replaces what was sent, byte by byte) */
    return Int(fire_spib_run(d, bufOf(outBuffer)->data + outOffset.i, bufOf(inBuffer)->data + inOffset.i, count.i));
}

/* Sends `count` bytes and drops what comes in. */
static Value fire_spi_Write(Value h, Value buffer, Value offset, Value count) {
    FireSpiEntry* d;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_spib_checkRange(buffer, offset.i, count.i)) return Int(-1);
    d = fire_spib_find(h);
    if (d == NULL) return Int(-1);
    return Int(fire_spib_run(d, bufOf(buffer)->data + offset.i, NULL, count.i));
}

/* Receives `count` bytes (the device is sent zeros meanwhile). */
static Value fire_spi_Read(Value h, Value buffer, Value offset, Value count) {
    FireSpiEntry* d;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_spib_checkRange(buffer, offset.i, count.i)) return Int(-1);
    d = fire_spib_find(h);
    if (d == NULL) return Int(-1);
    return Int(fire_spib_run(d, NULL, bufOf(buffer)->data + offset.i, count.i));
}

static Value fire_spi_Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= g_spiDeviceCount || g_spiDevices[i] == NULL) { fire_spib_fail(FIRE_SPI_E_INVALID_HANDLE, "Invalid or already closed device handle."); return Bool(false); }
    fire_spib_release(g_spiDevices[i]);
    g_spiDevices[i] = NULL;
    fire_spib_ok();
    return Bool(true);
}

/* ---- the simulated device from the outside --------------------------------------------------------------------------------------------------- */
/* Queues bytes that the simulated device answers with (one per byte sent); it stops being a loopback. */
static Value fire_spi_SimReply(Value buffer, Value offset, Value count) {
    const uint8_t* bytes;
    int64_t i;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_spib_checkRange(buffer, offset.i, count.i)) return Bool(false);
    bytes = bufOf(buffer)->data + offset.i;
    g_spiSim.loopback = 0;
    for (i = 0; i < count.i; i++) fire_spib_simQueue(&g_spiSim, bytes[i]);
    fire_spib_ok();
    return Bool(true);
}
/* Like SimReply, but drops the answers that are still queued first (a device whose answer changes all the time, like a shift register with inputs). */
static Value fire_spi_SimSetReply(Value buffer, Value offset, Value count) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_spib_checkRange(buffer, offset.i, count.i)) return Bool(false);
    fire_spib_simClearReplies(&g_spiSim);
    return fire_spi_SimReply(buffer, offset, count);
}
/* The simulated device is a loopback again (MISO tied to MOSI); the queued answers are dropped. */
static Value fire_spi_SimLoopback(void) {
    g_spiSim.loopback = 1;
    fire_spib_simClearReplies(&g_spiSim);
    fire_spib_ok();
    return Bool(true);
}
/* How many bytes the program has sent to the simulated device (up to 65536 are kept). */
static Value fire_spi_SimSentCount(void) { fire_spib_ok(); return Int((int64_t)g_spiSim.sentCount); }
static Value fire_spi_SimSentByte(Value index) {
    if (index.i < 0 || (size_t)index.i >= g_spiSim.sentCount) return Int(fire_spib_fail(FIRE_SPI_E_INVALID_ARGUMENT, "No such sent byte."));
    fire_spib_ok();
    return Int(g_spiSim.sent[index.i]);
}
/* What was last set up on the simulated device: 0 the mode, 1 the speed, 2 the bit order (1: least significant first), 3 the number of transfers done, 4 the
 * last byte sent (-1: none), 5 the bytes sent since the start (not limited). */
static Value fire_spi_SimInfo(Value which) {
    fire_spib_ok();
    switch (which.i) {
        case 0: return Int(g_spiSim.mode);
        case 1: return Int(g_spiSim.speed);
        case 2: return Int(g_spiSim.lsb ? 1 : 0);
        case 3: return Int(g_spiSim.transfers);
        case 4: return Int(g_spiSim.last);
        case 5: return Int(g_spiSim.total);
        default: return Int(fire_spib_fail(FIRE_SPI_E_INVALID_ARGUMENT, "No such information."));
    }
}
/* Forgets what was sent and the queued answers, keeps the mode of the loopback. */
static Value fire_spi_SimClear(void) {
    fire_spib_simClearSent(&g_spiSim);
    fire_spib_simClearReplies(&g_spiSim);
    g_spiSim.transfers = 0;
    fire_spib_ok();
    return Bool(true);
}

#endif
