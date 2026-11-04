/* fire native bridge "usb" (C): the natives behind `#import "usb"` (docs/devices/usb.md): USB devices from the program - the list of devices, control transfers, bulk and
 * interrupt transfers on the endpoints, and a bulk endpoint pair as a stream. The fire side (Usb.Device, Usb.Bus, Usb.UsbStream - fire source) is the same in the
 * VM and in a native build; this file is what its `__Usb...` functions do.
 *
 * This file is the one implementation of the usb natives, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual machine
 * runs it in a shared library built from it (native/abi/fire_pkg_abi.h). The devices come from the platform layer (FIRE_PLATFORM_USB_C_HEADER: fire_usb_*, see
 * platform/std/fire_usb_c.h): libusb-1.0 on Linux and macOS, WinUSB on Windows. One device is always there:
 *
 *   "sim"   a simulated device (vendor 0x1209, product 0x0001): interface 0 with a bulk OUT endpoint 0x01, a bulk IN endpoint 0x81 (64-byte packets) and an
 *           interrupt IN endpoint 0x82. What is written to 0x01 comes back on 0x81 (a loopback; reading with nothing queued times out at once); 0x82 answers a
 *           counter byte. The vendor request 1 stores (OUT, 0x40) and returns (IN, 0xC0) up to 64 bytes; the standard request GET_DESCRIPTOR (0x80, 6, 0x0100)
 *           returns its device descriptor. For tests, and to develop a program on a machine without the device.
 *
 * A device is an integer handle in a table of this bridge (index 0 is not used; handles are not used again). Every function reports an error the same way: a
 * result of -1/false/undefined and the code and message of this thread (`__UsbLastError`, `__UsbLastErrorMessage`, the slot of the io bridge), from which the
 * fire code throws a typed exception. */
#ifndef FIRE_BRIDGE_USB_H
#define FIRE_BRIDGE_USB_H

#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include FIRE_PLATFORM_USB_C_HEADER

static int64_t fire_usbb_error(int code, const char* message) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s", message);
    return -1;
}
static void fire_usbb_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }
static int64_t fire_usbb_errorStatus(const FireUsbStatus* s) { return fire_usbb_error(s->code, s->message); }

/* ---- text ----------------------------------------------------------------------------------------------------------------------------------- */
/* A fire string as UTF-8 (malloc; the caller frees it; NULL when out of memory); `length` (may be NULL) gets the number of bytes. */
static char* fire_usbb_utf8(Value v, size_t* length) {
    const Str* s = strOf(v);
    char* out = (char*)malloc((size_t)s->length * 3 + 1);
    size_t n = 0;
    uint32_t i;
    if (out == NULL) return NULL;
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xC0 | (c >> 6)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else { out[n++] = (char)(0xE0 | (c >> 12)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[n] = 0;
    if (length) *length = n;
    return out;
}
/* (a byte above 127 becomes the Latin-1 character of the same number) */
static Value fire_usbb_str8(const char* text, size_t n, OwnList* list) {
    Str* s = allocStr((uint32_t)n, list);
    size_t i;
    for (i = 0; i < n; i++) strChars(s)[i] = (fire_char16)(uint8_t)text[i];
    return StrV(s);
}

/* ---- the lines of a result (malloc'd texts; an array of strings) ---- */
typedef struct FireUsbbLines { char** items; size_t count, cap; } FireUsbbLines;
static void fire_usbb_linesFree(FireUsbbLines* l) {
    size_t i;
    for (i = 0; i < l->count; i++) free(l->items[i]);
    free(l->items);
    l->items = NULL;
    l->count = l->cap = 0;
}
/* Adds a copy of `text`: 0 when out of memory. */
static int fire_usbb_linesAdd(FireUsbbLines* l, const char* text) {
    size_t n = strlen(text);
    char* copy;
    if (l->count == l->cap) {
        size_t cap = l->cap ? l->cap * 2 : 8;
        char** grown = (char**)realloc(l->items, sizeof(char*) * cap);
        if (grown == NULL) return 0;
        l->items = grown;
        l->cap = cap;
    }
    copy = (char*)malloc(n + 1);
    if (copy == NULL) return 0;
    memcpy(copy, text, n + 1);
    l->items[l->count++] = copy;
    return 1;
}
static Value fire_usbb_lines(const FireUsbbLines* items, OwnList* list) {
    Arr* a = allocArr((uint32_t)items->count, list);
    size_t i;
    for (i = 0; i < items->count; i++) { arrItems(a)[i] = fire_usbb_str8(items->items[i], strlen(items->items[i]), list); retain(arrItems(a)[i]); }
    return ArrV(a);
}
/* Appends a field of a line ('|' becomes '/') to `out` (`*n` bytes used of `size`). */
static void fire_usbb_field(char* out, size_t* n, size_t size, const char* s) {
    for (; *s && *n + 1 < size; s++) out[(*n)++] = (*s == '|' || *s == '\n') ? '/' : *s;
    out[*n] = 0;
}
/* Appends `text` to `out` (`*n` bytes used of `size`). */
static void fire_usbb_append(char* out, size_t* n, size_t size, const char* text) {
    for (; *text && *n + 1 < size; text++) out[(*n)++] = *text;
    out[*n] = 0;
}

/* ---- the simulated device ------------------------------------------------------------------------------------------------------------------- */
typedef struct FireUsbbSim {
    uint8_t* loop;                 /* the queue: `loopCount` bytes from `loopHead` */
    size_t loopHead, loopCount, loopCap;
    uint8_t stored[64];
    int storedCount;
    int counter;
    int64_t written, read, controls;
    int claimed;
} FireUsbbSim;
static FireUsbbSim g_usbSim;
enum { FIRE_USBB_SIM_VENDOR = 0x1209, FIRE_USBB_SIM_PRODUCT = 0x0001 };

static void fire_usbb_simClear(void) {
    free(g_usbSim.loop);
    memset(&g_usbSim, 0, sizeof g_usbSim);
}
/* Queues a byte: 0 when out of memory. */
static int fire_usbb_simPush(uint8_t b) {
    FireUsbbSim* s = &g_usbSim;
    if (s->loopHead + s->loopCount == s->loopCap) {
        if (s->loopHead > 0) {
            memmove(s->loop, s->loop + s->loopHead, s->loopCount);
            s->loopHead = 0;
        } else {
            size_t cap = s->loopCap ? s->loopCap * 2 : 256;
            uint8_t* grown = (uint8_t*)realloc(s->loop, cap);
            if (grown == NULL) return 0;
            s->loop = grown;
            s->loopCap = cap;
        }
    }
    s->loop[s->loopHead + s->loopCount++] = b;
    return 1;
}

static int fire_usbb_simControl(int requestType, int request, int value, uint8_t* data, int length) {
    FireUsbbSim* s = &g_usbSim;
    int in = (requestType & 0x80) != 0;
    s->controls++;
    if ((requestType & 0x60) == 0x40 && request == 1) {
        int n;
        if (in) {
            n = s->storedCount < length ? s->storedCount : length;
            if (n > 0) memcpy(data, s->stored, (size_t)n);
            return n;
        }
        n = length > 64 ? 64 : length;
        if (n > 0) memcpy(s->stored, data, (size_t)n);
        s->storedCount = n < 0 ? 0 : n;
        return n;
    }
    if (requestType == 0x80 && request == 6 && (value >> 8) == 1) {
        const uint8_t descriptor[18] = {18, 1, 0x00, 0x02, 0xFF, 0, 0, 64, (uint8_t)(FIRE_USBB_SIM_VENDOR & 0xFF), (uint8_t)(FIRE_USBB_SIM_VENDOR >> 8),
                                        (uint8_t)(FIRE_USBB_SIM_PRODUCT & 0xFF), (uint8_t)(FIRE_USBB_SIM_PRODUCT >> 8), 0x00, 0x01, 1, 2, 3, 1};
        int n = length < 18 ? length : 18;
        memcpy(data, descriptor, (size_t)n);
        return n;
    }
    return (int)fire_usbb_error(FIRE_USB_E_STALL, "The simulated device does not know this request (it stalls).");
}

static int fire_usbb_simTransfer(int type, int endpoint, uint8_t* data, int length) {
    FireUsbbSim* s = &g_usbSim;
    char text[96];
    if (endpoint == 0x01 && type == 2) {
        int i;
        for (i = 0; i < length; i++)
            if (!fire_usbb_simPush(data[i])) return (int)fire_usbb_error(FIRE_USB_E_OTHER, "Out of memory.");
        s->written += length;
        return length;
    }
    if (endpoint == 0x81 && type == 2) {
        int n = 0;
        if (s->loopCount == 0) return (int)fire_usbb_error(FIRE_USB_E_TIMEOUT, "Reading from endpoint 1: timed out (the simulated device has nothing to send).");
        /* a whole packet at most, as a real device sends it */
        while (n < length && n < 64 && s->loopCount > 0) { data[n++] = s->loop[s->loopHead++]; s->loopCount--; }
        if (s->loopCount == 0) s->loopHead = 0;
        s->read += n;
        return n;
    }
    if (endpoint == 0x82 && type == 3) {
        if (length < 1) return 0;
        data[0] = (uint8_t)(s->counter++ & 0xFF);
        return 1;
    }
    snprintf(text, sizeof text, "The simulated device has no %s endpoint 0x%d.", type == 3 ? "interrupt" : "bulk", endpoint);
    return (int)fire_usbb_error(FIRE_USB_E_INVALID_ARGUMENT, text);
}

/* ---- the devices ---------------------------------------------------------------------------------------------------------------------------- */
typedef struct FireUsbbDevice {
    int isSim;
    FireUsbDevice* device;
} FireUsbbDevice;
static void fire_usbb_release(FireUsbbDevice* d) {
    if (!d) return;
    if (d->device) fire_usb_closeDevice(d->device);
    free(d);
}

/* The open devices; the handle is the index (0 is not used). */
static FireUsbbDevice** g_usbDevices = NULL;
static size_t g_usbDeviceCount = 1, g_usbDeviceCap = 0;

/* The program has ended (the library for the VM stays loaded for the next one): closes the devices and resets the simulated one. */
static void fire_usb_reset(void) {
    size_t i;
    for (i = 1; i < g_usbDeviceCount; i++) {
        fire_usbb_release(g_usbDevices[i]);
        g_usbDevices[i] = NULL;
    }
    fire_usbb_simClear();
}

static int64_t fire_usbb_add(FireUsbbDevice* d) {
    if (g_usbDeviceCount >= g_usbDeviceCap) {
        size_t cap = g_usbDeviceCap ? g_usbDeviceCap * 2 : 16;
        FireUsbbDevice** grown = (FireUsbbDevice**)realloc(g_usbDevices, sizeof(FireUsbbDevice*) * cap);
        if (grown == NULL) { fire_usbb_release(d); return fire_usbb_error(FIRE_USB_E_OTHER, "Out of memory."); }
        if (g_usbDeviceCap == 0) grown[0] = NULL;
        g_usbDevices = grown;
        g_usbDeviceCap = cap;
    }
    g_usbDevices[g_usbDeviceCount] = d;
    return (int64_t)g_usbDeviceCount++;
}
static FireUsbbDevice* fire_usbb_find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < g_usbDeviceCount && g_usbDevices[i] != NULL) return g_usbDevices[i];
    fire_usbb_error(FIRE_USB_E_INVALID_HANDLE, "Invalid or already closed USB device handle.");
    return NULL;
}
static int fire_usbb_checkRange(Value bufferValue, int64_t offset, int64_t count) {
    int64_t length = bufOf(bufferValue)->length;
    if (offset < 0 || count < 0 || offset > length || count > length - offset) {
        char text[176];
        snprintf(text, sizeof text, "offset/count (%lld/%lld) are outside of the buffer (length %lld).", (long long)offset, (long long)count, (long long)length);
        fire_usbb_error(FIRE_USB_E_INVALID_ARGUMENT, text);
        return 0;
    }
    return 1;
}

/* ---- natives -------------------------------------------------------------------------------------------------------------------------------- */
static Value fire_usb_LastError(void) { return Int(g_ioError.code); }
static Value fire_usb_LastErrorMessage(OwnList* list) { return fire_usbb_str8(g_ioError.message, strlen(g_ioError.message), list); }
static Value fire_usb_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 1; i < g_usbDeviceCount; i++) if (g_usbDevices[i] != NULL) n++;
    return Int(n);
}
/* 1 if this machine has USB access (libusb or WinUSB is there; the simulated device is there anyway), else 0. */
static Value fire_usb_Supported(void) { fire_usbb_ok(); return Int(fire_usb_supported() ? 1 : 0); }

/* The devices, one line each: id|vendor|product|class|manufacturer|product name|serial|bus|address - "sim" first. */
static Value fire_usb_Devices(OwnList* list) {
    FireUsbDeviceList found = {NULL, 0, 0};
    FireUsbbLines out = {NULL, 0, 0};
    FireUsbStatus st;
    char line[2048], number[64];
    size_t i;
    int done = 1;
    Value result;
    if (!fire_usb_devices(&found, &st)) { fire_usb_deviceListFree(&found); fire_usbb_errorStatus(&st); return Undef(); }
    snprintf(line, sizeof line, "sim|%d|%d|255|fire|Simulated loopback device|0001|0|0", FIRE_USBB_SIM_VENDOR, FIRE_USBB_SIM_PRODUCT);
    done = fire_usbb_linesAdd(&out, line);
    for (i = 0; i < found.count && done; i++) {
        const FireUsbDeviceInfo* d = &found.items[i];
        size_t n = 0;
        line[0] = 0;
        fire_usbb_field(line, &n, sizeof line, d->id);
        snprintf(number, sizeof number, "|%d|%d|%d|", d->vendorId, d->productId, d->deviceClass);
        fire_usbb_append(line, &n, sizeof line, number);
        fire_usbb_field(line, &n, sizeof line, d->manufacturer);
        fire_usbb_append(line, &n, sizeof line, "|");
        fire_usbb_field(line, &n, sizeof line, d->product);
        fire_usbb_append(line, &n, sizeof line, "|");
        fire_usbb_field(line, &n, sizeof line, d->serial);
        snprintf(number, sizeof number, "|%d|%d", d->bus, d->address);
        fire_usbb_append(line, &n, sizeof line, number);
        done = fire_usbb_linesAdd(&out, line);
    }
    fire_usb_deviceListFree(&found);
    if (!done) { fire_usbb_linesFree(&out); fire_usbb_error(FIRE_USB_E_OTHER, "Out of memory."); return Undef(); }
    fire_usbb_ok();
    result = fire_usbb_lines(&out, list);
    fire_usbb_linesFree(&out);
    return result;
}

static Value fire_usb_Open(Value id) {
    size_t length = 0;
    char* n = fire_usbb_utf8(id, &length);
    FireUsbbDevice* d;
    int64_t handle;
    if (n == NULL) return Int(fire_usbb_error(FIRE_USB_E_OTHER, "Out of memory."));
    d = (FireUsbbDevice*)calloc(1, sizeof(FireUsbbDevice));
    if (d == NULL) { free(n); return Int(fire_usbb_error(FIRE_USB_E_OTHER, "Out of memory.")); }
    if (length == 3 && memcmp(n, "sim", 3) == 0) d->isSim = 1;
    else {
        FireUsbStatus st;
        if (!fire_usb_openDevice(n, &d->device, &st)) { free(n); free(d); return Int(fire_usbb_errorStatus(&st)); }
    }
    free(n);
    handle = fire_usbb_add(d);
    if (handle < 0) return Int(-1);
    fire_usbb_ok();
    return Int(handle);
}

/* The endpoints of the device, one line each: interface|alternate|address|type|max packet|interface class. */
static Value fire_usb_Endpoints(Value h, OwnList* list) {
    FireUsbbDevice* d = fire_usbb_find(h);
    FireUsbEndpointList eps = {NULL, 0, 0};
    FireUsbbLines out = {NULL, 0, 0};
    size_t i;
    int done = 1;
    Value result;
    if (!d) return Undef();
    if (d->isSim) {
        static const FireUsbEndpointInfo simEndpoints[3] = {{0, 0, 0x01, 2, 64, 255}, {0, 0, 0x81, 2, 64, 255}, {0, 0, 0x82, 3, 8, 255}};
        for (i = 0; i < 3; i++) done = done && fire_usb_endpointListAdd(&eps, &simEndpoints[i]);
    } else {
        FireUsbStatus st;
        if (!fire_usb_endpoints(d->device, &eps, &st)) { fire_usb_endpointListFree(&eps); fire_usbb_errorStatus(&st); return Undef(); }
    }
    for (i = 0; i < eps.count && done; i++) {
        const FireUsbEndpointInfo* e = &eps.items[i];
        char line[128];
        snprintf(line, sizeof line, "%d|%d|%d|%d|%d|%d", e->interfaceNumber, e->alternate, e->address, e->type, e->maxPacket, e->interfaceClass);
        done = fire_usbb_linesAdd(&out, line);
    }
    fire_usb_endpointListFree(&eps);
    if (!done) { fire_usbb_linesFree(&out); fire_usbb_error(FIRE_USB_E_OTHER, "Out of memory."); return Undef(); }
    fire_usbb_ok();
    result = fire_usbb_lines(&out, list);
    fire_usbb_linesFree(&out);
    return result;
}

/* what 0 claims, 1 releases interface `itf`; 2 sets its alternate setting `alt`. */
static Value fire_usb_Interface(Value h, Value what, Value itf, Value alt) {
    FireUsbbDevice* d = fire_usbb_find(h);
    FireUsbStatus st;
    int done;
    if (!d) return Bool(false);
    if (d->isSim) {
        if (itf.i != 0) { fire_usbb_error(FIRE_USB_E_NOT_FOUND, "The simulated device has only interface 0."); return Bool(false); }
        if (what.i == 0) g_usbSim.claimed = 1;
        if (what.i == 1) g_usbSim.claimed = 0;
        fire_usbb_ok();
        return Bool(true);
    }
    done = what.i == 0 ? fire_usb_claim(d->device, (int)itf.i, &st) : what.i == 1 ? fire_usb_release(d->device, (int)itf.i, &st)
                                                                    : fire_usb_setAlternate(d->device, (int)itf.i, (int)alt.i, &st);
    if (!done) { fire_usbb_errorStatus(&st); return Bool(false); }
    fire_usbb_ok();
    return Bool(true);
}

/* A control transfer: the setup (request type, request, value, index), the data stage in buffer[offset .. offset + length) (in or out as bit 7 of the request
 * type says); the bytes moved, -1 on an error. */
static Value fire_usb_Control(Value h, Value requestType, Value request, Value value, Value index, Value buffer, Value offset, Value length, Value timeout) {
    FireUsbbDevice* d;
    FireUsbStatus st;
    uint8_t* data;
    int done = 0;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_usbb_checkRange(buffer, offset.i, length.i)) return Int(-1);
    if (length.i > 65535) return Int(fire_usbb_error(FIRE_USB_E_INVALID_ARGUMENT, "A control transfer moves at most 65535 bytes."));
    d = fire_usbb_find(h);
    if (!d) return Int(-1);
    data = bufOf(buffer)->data + offset.i;
    if (d->isSim) {
        int n = fire_usbb_simControl((int)requestType.i, (int)request.i, (int)value.i, data, (int)length.i);
        if (n < 0) return Int(-1);
        fire_usbb_ok();
        return Int(n);
    }
    if (!fire_usb_control(d->device, (int)requestType.i, (int)request.i, (int)value.i, (int)index.i, data, (int)length.i, (int)timeout.i, &done, &st))
        return Int(fire_usbb_errorStatus(&st));
    fire_usbb_ok();
    return Int(done);
}

/* A bulk (type 2) or interrupt (type 3) transfer on an endpoint (bit 7: in); the bytes moved, -1 on an error. */
static Value fire_usb_Transfer(Value h, Value type, Value endpoint, Value buffer, Value offset, Value length, Value timeout) {
    FireUsbbDevice* d;
    FireUsbStatus st;
    uint8_t* data;
    int done = 0;
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!fire_usbb_checkRange(buffer, offset.i, length.i)) return Int(-1);
    if (type.i != 2 && type.i != 3) return Int(fire_usbb_error(FIRE_USB_E_INVALID_ARGUMENT, "The transfer type is 2 (bulk) or 3 (interrupt)."));
    d = fire_usbb_find(h);
    if (!d) return Int(-1);
    data = bufOf(buffer)->data + offset.i;
    if (d->isSim) {
        int n = fire_usbb_simTransfer((int)type.i, (int)endpoint.i, data, (int)length.i);
        if (n < 0) return Int(-1);
        fire_usbb_ok();
        return Int(n);
    }
    /* (a transfer that timed out after moving some bytes counts as done: the fire side goes on with the rest) */
    if (!fire_usb_transfer(d->device, (int)type.i, (int)endpoint.i, data, (int)length.i, (int)timeout.i, &done, &st) && !(st.code == FIRE_USB_E_TIMEOUT && done > 0))
        return Int(fire_usbb_errorStatus(&st));
    fire_usbb_ok();
    return Int(done);
}

static Value fire_usb_ClearHalt(Value h, Value endpoint) {
    FireUsbbDevice* d = fire_usbb_find(h);
    if (!d) return Bool(false);
    if (!d->isSim) {
        FireUsbStatus st;
        if (!fire_usb_clearHalt(d->device, (int)endpoint.i, &st)) { fire_usbb_errorStatus(&st); return Bool(false); }
    }
    fire_usbb_ok();
    return Bool(true);
}

static Value fire_usb_Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= g_usbDeviceCount || g_usbDevices[i] == NULL) { fire_usbb_error(FIRE_USB_E_INVALID_HANDLE, "Invalid or already closed USB device handle."); return Bool(false); }
    fire_usbb_release(g_usbDevices[i]);
    g_usbDevices[i] = NULL;
    fire_usbb_ok();
    return Bool(true);
}

/* What the simulated device has seen: 0 bytes written to it, 1 bytes read from it, 2 control transfers, 3 bytes still queued, 4 interface 0 claimed (1/0). */
static Value fire_usb_SimInfo(Value which) {
    fire_usbb_ok();
    switch (which.i) {
        case 0: return Int(g_usbSim.written);
        case 1: return Int(g_usbSim.read);
        case 2: return Int(g_usbSim.controls);
        case 3: return Int((int64_t)g_usbSim.loopCount);
        case 4: return Int(g_usbSim.claimed ? 1 : 0);
        default: return Int(fire_usbb_error(FIRE_USB_E_INVALID_ARGUMENT, "No such information."));
    }
}
static Value fire_usb_SimClear(void) { fire_usbb_simClear(); fire_usbb_ok(); return Bool(true); }

#endif
