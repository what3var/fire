/* fire native bridge "devices" (C): the natives behind `#import "devices"` (SPEC 8.16) - the device manager, devices (serial ports, the loopback device), the
 * receive buffer of a device and the waiting functions. The fire side (Device, DeviceManagerFacade - fire source, src/fire/Standard/DevicesPrelude.cs) calls
 * the `__DEV...` functions this file implements.
 *
 * This file is the one implementation, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual machine runs it in a
 * shared library built from it (native/abi/fire_pkg_abi.h). What differs is where the devices come from - the *drivers*:
 *   - a native build: FIRE_DEVICES, a string with the drivers in the order they are registered: "serial" (the default: the serial ports of the platform),
 *     "loopback" (a simulated device that sends back what it gets, identifier `loopback:echo`), e.g. -DFIRE_DEVICES="\"loopback,serial\""  (target
 *     configuration: defines); FIRE_DEFAULT_DEVICE is the identifier of the default device (`Device.Default`); none by default. The serial ports come from the
 *     platform layer (FIRE_PLATFORM_DEV_C_HEADER: fire_dev_serialNames and FireDevSerialPort, see platform/std/fire_dev_c.h, 115200 baud 8N1); a board without
 *     one has none and so no `serial` devices.
 *   - the library for the VM: the driver "host" - the devices are those of the host (the device manager of the editor with its drivers, sharing and packet
 *     trace; the callbacks `dev_*` of `fire_host`), the identifiers are the host's. The receive buffer, the handles and the matching of `WaitFor` stay in this
 *     file.
 *
 * Differences between the two: a native build has no background threads - received data is collected when the program asks (HasData, Read..., the waiting
 * functions, Connect), not while it does something else (the receive buffer of the operating system or of the UART driver holds it meanwhile); the device list
 * is made when the program first uses a device function (and again at every Refresh). In the VM the waiting functions (`WaitFor`, `WaitForString`) are run by
 * the VM itself (they have to be abortable and know the program's `#timeout`): it calls `WaitStep` until it succeeds. */
#ifndef FIRE_BRIDGE_DEVICES_H
#define FIRE_BRIDGE_DEVICES_H

#include <stdlib.h>
#include <string.h>
#include FIRE_PLATFORM_DEV_C_HEADER

#ifndef FIRE_DEVICES
#ifdef FIRE_LIBRARY
#define FIRE_DEVICES "host"
#else
#define FIRE_DEVICES "serial"
#endif
#endif
#ifndef FIRE_DEFAULT_DEVICE
#define FIRE_DEFAULT_DEVICE ""
#endif

enum { FIRE_DEVB_UNAVAILABLE = 0, FIRE_DEVB_UNCHECKED = 1, FIRE_DEVB_AVAILABLE = 2 };
enum { FIRE_DEVB_LOOPBACK = 1, FIRE_DEVB_SERIAL = 2, FIRE_DEVB_HOST = 3 };

/* ---- bytes: a packet, a list of packets --------------------------------------------------------------------------------------------------------- */
typedef struct FireDevbBytes { uint8_t* data; size_t n; } FireDevbBytes;
typedef struct FireDevbPackets { FireDevbBytes* items; size_t count, cap; } FireDevbPackets;

/* A copy of `n` bytes (an empty one has no memory): 0 when out of memory. */
static int fire_devb_bytesOf(FireDevbBytes* b, const uint8_t* data, size_t n) {
    b->data = NULL;
    b->n = 0;
    if (n == 0) return 1;
    b->data = (uint8_t*)malloc(n);
    if (b->data == NULL) return 0;
    memcpy(b->data, data, n);
    b->n = n;
    return 1;
}
/* Appends `b` (the list owns it now; when out of memory it is freed). */
static void fire_devb_packetsPush(FireDevbPackets* l, FireDevbBytes b) {
    if (l->count == l->cap) {
        size_t cap = l->cap ? l->cap * 2 : 8;
        FireDevbBytes* grown = (FireDevbBytes*)realloc(l->items, sizeof(FireDevbBytes) * cap);
        if (grown == NULL) { free(b.data); return; }
        l->items = grown;
        l->cap = cap;
    }
    l->items[l->count++] = b;
}
/* Takes the first packet out of the list (the caller owns it). */
static FireDevbBytes fire_devb_packetsPop(FireDevbPackets* l) {
    FireDevbBytes first = l->items[0];
    memmove(l->items, l->items + 1, sizeof(FireDevbBytes) * (l->count - 1));
    l->count--;
    return first;
}

/* ---- text ------------------------------------------------------------------------------------------------------------------------------------------- */
/* `n` characters as UTF-8 (malloc, zero terminated; `*length` gets the number of bytes): NULL when out of memory. */
static char* fire_devb_utf8Of(const fire_char16* text, size_t length, size_t* outLength) {
    char* out = (char*)malloc(length * 3 + 1);
    size_t n = 0, i;
    if (out == NULL) return NULL;
    for (i = 0; i < length; i++) {
        uint32_t c = text[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < length && text[i + 1] >= 0xDC00 && text[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (text[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xC0 | (c >> 6)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { out[n++] = (char)(0xE0 | (c >> 12)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else { out[n++] = (char)(0xF0 | (c >> 18)); out[n++] = (char)(0x80 | ((c >> 12) & 0x3F)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[n] = 0;
    if (outLength) *outLength = n;
    return out;
}
static char* fire_devb_strdup(const char* s) {
    size_t n = strlen(s);
    char* copy = (char*)malloc(n + 1);
    if (copy) memcpy(copy, s, n + 1);
    return copy;
}

#ifdef FIRE_LIBRARY
/* ---- the devices of the host (the library for the VM) ----------------------------------------------------------------------------------------------- */
static const fire_host* fire_devb_host(void) {
    const fire_host* host = libraryHost();
    return host && host->size >= (int32_t)sizeof(fire_host) && host->dev_count ? host : NULL;
}
/* A text of the host (an identifier, a port name) into `out` (512 bytes); "" when there is none. */
static void fire_devb_hostText(int32_t (*get)(int32_t, char*, int32_t), int32_t handle, char* out) {
    enum { SIZE = 512 };
    int32_t n = get(handle, out, (int32_t)SIZE - 1);
    if (n <= 0) n = 0;
    if (n > SIZE - 1) n = SIZE - 1;
    out[n] = 0;
}
#endif

/* ---- devices ---------------------------------------------------------------------------------------------------------------------------------------- */
typedef struct FireDevbEcho { FireDevbBytes bytes; int64_t due; } FireDevbEcho;

/* A device: the loopback device (sends back what it gets, 5 ms later and in order - like the loopback device of the VM, whose echo comes from another
 * thread), a serial port of the platform (115200 baud, 8N1) or a device of the host (everything is forwarded to the host's device manager). */
typedef struct FireDevbDevice {
    int kind;
    char* name;                  /* loopback, serial: the port name */
    int connected;               /* loopback, serial */
    int availability;            /* serial */
    FireDevSerialPort port;      /* serial */
    FireDevbEcho* queue;         /* loopback: what comes back, and when */
    size_t queueCount, queueCap;
    int32_t handle;              /* host: the handle of the host */
} FireDevbDevice;

static int64_t fire_devb_nowMs(void) { return fire_plat_nowMs(); }

static void fire_devb_disconnect(FireDevbDevice* d);
static void fire_devb_free(FireDevbDevice* d) {
    size_t i;
    if (!d) return;
    if (d->kind == FIRE_DEVB_SERIAL) fire_devb_disconnect(d);
    for (i = 0; i < d->queueCount; i++) free(d->queue[i].bytes.data);
    free(d->queue);
    free(d->name);
    free(d);
}
static FireDevbDevice* fire_devb_newDevice(int kind, const char* name) {
    FireDevbDevice* d = (FireDevbDevice*)calloc(1, sizeof(FireDevbDevice));
    if (d == NULL) return NULL;
    d->kind = kind;
    d->availability = FIRE_DEVB_UNCHECKED;
    fire_dev_serialInit(&d->port);
    if (name) {
        d->name = fire_devb_strdup(name);
        if (d->name == NULL) { free(d); return NULL; }
    }
    return d;
}

static int fire_devb_availability(FireDevbDevice* d) {
    switch (d->kind) {
        case FIRE_DEVB_LOOPBACK: return FIRE_DEVB_AVAILABLE;
        case FIRE_DEVB_SERIAL: return d->availability;
#ifdef FIRE_LIBRARY
        case FIRE_DEVB_HOST: return fire_devb_host()->dev_availability(d->handle);
#endif
        default: return FIRE_DEVB_UNAVAILABLE;
    }
}
static int fire_devb_testAvailability(FireDevbDevice* d) {
    switch (d->kind) {
        case FIRE_DEVB_LOOPBACK: return FIRE_DEVB_AVAILABLE;
        case FIRE_DEVB_SERIAL: {
            FireDevSerialPort probe;
            if (d->connected) return d->availability = FIRE_DEVB_AVAILABLE;   /* a connected device holds its port: a second open would fail */
            fire_dev_serialInit(&probe);
            d->availability = fire_dev_serialOpen(&probe, d->name) ? FIRE_DEVB_AVAILABLE : FIRE_DEVB_UNAVAILABLE;
            fire_dev_serialClose(&probe);
            return d->availability;
        }
#ifdef FIRE_LIBRARY
        case FIRE_DEVB_HOST: return fire_devb_host()->dev_test_availability(d->handle);
#endif
        default: return FIRE_DEVB_UNAVAILABLE;
    }
}
static int fire_devb_connected(FireDevbDevice* d) {
#ifdef FIRE_LIBRARY
    if (d->kind == FIRE_DEVB_HOST) return fire_devb_host()->dev_connected(d->handle) != 0;
#endif
    return d->connected;
}
/* The port name into `out` (512 bytes). */
static void fire_devb_portName(FireDevbDevice* d, char* out) {
#ifdef FIRE_LIBRARY
    if (d->kind == FIRE_DEVB_HOST) { fire_devb_hostText(fire_devb_host()->dev_port_name, d->handle, out); return; }
#endif
    snprintf(out, 512, "%s", d->name ? d->name : "");
}
static void fire_devb_disconnect(FireDevbDevice* d) {
    switch (d->kind) {
        case FIRE_DEVB_LOOPBACK: d->connected = 0; break;
        case FIRE_DEVB_SERIAL: fire_dev_serialClose(&d->port); d->connected = 0; break;
#ifdef FIRE_LIBRARY
        case FIRE_DEVB_HOST: fire_devb_host()->dev_disconnect(d->handle); break;
#endif
        default: break;
    }
}
static int fire_devb_connect(FireDevbDevice* d) {
    switch (d->kind) {
        case FIRE_DEVB_LOOPBACK: d->connected = 1; return 1;
        case FIRE_DEVB_SERIAL:
            fire_devb_disconnect(d);
            if (!fire_dev_serialOpen(&d->port, d->name)) return 0;
            d->connected = 1;
            d->availability = FIRE_DEVB_AVAILABLE;
            return 1;
#ifdef FIRE_LIBRARY
        case FIRE_DEVB_HOST: return fire_devb_host()->dev_connect(d->handle) != 0;
#endif
        default: return 0;
    }
}
static int fire_devb_write(FireDevbDevice* d, const uint8_t* data, size_t n) {
    switch (d->kind) {
        case FIRE_DEVB_LOOPBACK: {
            int64_t due;
            FireDevbBytes bytes;
            if (!d->connected) return 0;
            due = fire_devb_nowMs() + 5;
            if (d->queueCount > 0 && d->queue[d->queueCount - 1].due + 5 > due) due = d->queue[d->queueCount - 1].due + 5;
            if (d->queueCount == d->queueCap) {
                size_t cap = d->queueCap ? d->queueCap * 2 : 8;
                FireDevbEcho* grown = (FireDevbEcho*)realloc(d->queue, sizeof(FireDevbEcho) * cap);
                if (grown == NULL) return 0;
                d->queue = grown;
                d->queueCap = cap;
            }
            if (!fire_devb_bytesOf(&bytes, data, n)) return 0;
            d->queue[d->queueCount].bytes = bytes;
            d->queue[d->queueCount].due = due;
            d->queueCount++;
            return 1;
        }
        case FIRE_DEVB_SERIAL: return d->connected && fire_dev_serialWrite(&d->port, data, n);
#ifdef FIRE_LIBRARY
        case FIRE_DEVB_HOST: return fire_devb_host()->dev_write(d->handle, data, (int32_t)n) != 0;
#endif
        default: return 0;
    }
}
/* Sends a command line: the text and a line ending, in the encoding of the device (the device of the host encodes it itself). */
static int fire_devb_sendCommand(FireDevbDevice* d, const fire_char16* text, size_t length) {
    uint8_t* bytes;
    size_t n = 0, i;
    int done;
    switch (d->kind) {
        case FIRE_DEVB_LOOPBACK:   /* UTF-8 */
            bytes = (uint8_t*)fire_devb_utf8Of(text, length, &n);
            if (bytes == NULL) return 0;
            bytes[n++] = '\n';
            break;
        case FIRE_DEVB_SERIAL:     /* the default encoding of a serial port is ASCII */
            bytes = (uint8_t*)malloc(length + 1);
            if (bytes == NULL) return 0;
            for (i = 0; i < length; i++) bytes[n++] = text[i] < 0x80 ? (uint8_t)text[i] : (uint8_t)'?';
            bytes[n++] = '\n';
            break;
#ifdef FIRE_LIBRARY
        case FIRE_DEVB_HOST: {
            char* utf8 = fire_devb_utf8Of(text, length, NULL);
            if (utf8 == NULL) return 0;
            done = fire_devb_host()->dev_send_command(d->handle, utf8) != 0;
            free(utf8);
            return done;
        }
#endif
        default: return 0;
    }
    done = fire_devb_write(d, bytes, n);
    free(bytes);
    return done;
}
/* Does the device belong to a manager that stays after the program (the editor's)? */
static int fire_devb_shared(FireDevbDevice* d) {
#ifdef FIRE_LIBRARY
    if (d->kind == FIRE_DEVB_HOST) return fire_devb_host()->dev_shared(d->handle) != 0;
#endif
    (void)d;
    return 0;
}
/* Collects what arrived since the last call, one packet per delivery. */
static void fire_devb_poll(FireDevbDevice* d, FireDevbPackets* packets) {
    switch (d->kind) {
        case FIRE_DEVB_LOOPBACK: {
            int64_t now = fire_devb_nowMs();
            size_t taken = 0;
            while (taken < d->queueCount && d->queue[taken].due <= now) {
                if (d->connected) fire_devb_packetsPush(packets, d->queue[taken].bytes);
                else free(d->queue[taken].bytes.data);
                taken++;
            }
            if (taken > 0) {
                memmove(d->queue, d->queue + taken, sizeof(FireDevbEcho) * (d->queueCount - taken));
                d->queueCount -= taken;
            }
            break;
        }
        case FIRE_DEVB_SERIAL: {
            uint8_t buffer[512];
            if (!d->connected) return;
            for (;;) {
                int64_t n = fire_dev_serialRead(&d->port, buffer, sizeof buffer);
                FireDevbBytes b;
                if (n < 0) { fire_devb_disconnect(d); return; }   /* the port is gone (unplugged): the connection is lost */
                if (n == 0) return;
                if (fire_devb_bytesOf(&b, buffer, (size_t)n)) fire_devb_packetsPush(packets, b);
                if (n < (int64_t)sizeof buffer) return;
            }
        }
#ifdef FIRE_LIBRARY
        case FIRE_DEVB_HOST: {
            int32_t size = 4096;
            uint8_t* buffer = (uint8_t*)malloc((size_t)size);
            if (buffer == NULL) return;
            for (;;) {
                int32_t n = fire_devb_host()->dev_poll(d->handle, buffer, size);
                FireDevbBytes b;
                if (n < 0) break;
                if (n > size) {   /* the packet is bigger: it was kept, ask again with room */
                    uint8_t* grown = (uint8_t*)realloc(buffer, (size_t)n);
                    if (grown == NULL) break;
                    buffer = grown;
                    size = n;
                    continue;
                }
                if (fire_devb_bytesOf(&b, buffer, (size_t)n)) fire_devb_packetsPush(packets, b);
            }
            free(buffer);
            break;
        }
#endif
        default: break;
    }
}

/* ---- the receive buffer: packets, a consumed head --------------------------------------------------------------------------------------------------- */
typedef struct FireDevbReceive {
    FireDevbPackets packets;
    size_t head;   /* bytes of the first packet that are used up */
} FireDevbReceive;

/* The next packet (what is left of the first one); the caller frees it. */
static FireDevbBytes fire_devb_take(FireDevbReceive* r) {
    FireDevbBytes first, result;
    if (r->packets.count == 0) { result.data = NULL; result.n = 0; return result; }
    first = fire_devb_packetsPop(&r->packets);
    if (r->head == 0) result = first;
    else {
        if (!fire_devb_bytesOf(&result, first.data + r->head, first.n - r->head)) { result.data = NULL; result.n = 0; }
        free(first.data);
    }
    r->head = 0;
    return result;
}
/* Looks for `pattern` across the packets; found: everything up to and including it is used up. An empty pattern is always found. */
static int fire_devb_consumeThrough(FireDevbReceive* r, const uint8_t* pattern, size_t patternLength) {
    uint8_t* all;
    size_t total = 0, i, index = 0, consumed;
    int found = 0;
    if (patternLength == 0) return 1;
    if (r->packets.count == 0) return 0;
    for (i = 0; i < r->packets.count; i++) total += r->packets.items[i].n - (i == 0 ? r->head : 0);
    if (total < patternLength) return 0;
    all = (uint8_t*)malloc(total);
    if (all == NULL) return 0;
    total = 0;
    for (i = 0; i < r->packets.count; i++) {
        size_t from = i == 0 ? r->head : 0;
        memcpy(all + total, r->packets.items[i].data + from, r->packets.items[i].n - from);
        total += r->packets.items[i].n - from;
    }
    for (; index + patternLength <= total && !found; index++)
        if (memcmp(all + index, pattern, patternLength) == 0) { found = 1; break; }
    free(all);
    if (!found) return 0;
    consumed = index + patternLength;
    while (r->packets.count > 0) {
        size_t available = r->packets.items[0].n - r->head;
        if (consumed >= available) { consumed -= available; free(fire_devb_packetsPop(&r->packets).data); r->head = 0; }
        else { r->head += consumed; break; }
    }
    return 1;
}

/* ---- the manager ------------------------------------------------------------------------------------------------------------------------------------ */
typedef struct FireDevbSlot {
    int handle;
    char* identifier;
    const char* driver;
    FireDevbDevice* device;
    FireDevbReceive buffer;
} FireDevbSlot;
typedef struct FireDevbDriver {
    int handle;
    const char* id;
    int verbatim;   /* the identifiers of the devices are the names as they are (the host's), not `driver:name` */
    void (*names)(FireDevNames*);
    FireDevbDevice* (*make)(const char*);
} FireDevbDriver;

static void fire_devb_loopbackNames(FireDevNames* names) { fire_dev_namesAdd(names, "echo"); }
static FireDevbDevice* fire_devb_makeLoopback(const char* name) { return fire_devb_newDevice(FIRE_DEVB_LOOPBACK, name); }
#ifndef FIRE_LIBRARY
static void fire_devb_serialNames(FireDevNames* names) { fire_dev_serialNames(names); }
static FireDevbDevice* fire_devb_makeSerial(const char* name) { return fire_devb_newDevice(FIRE_DEVB_SERIAL, name); }
#else
static void fire_devb_hostNames(FireDevNames* names) {
    const fire_host* host = fire_devb_host();
    int32_t i, n;
    char text[512];
    if (!host) return;
    for (i = 0, n = host->dev_count(); i < n; i++) {
        int32_t handle = host->dev_handle_at(i);
        if (handle >= 0) { fire_devb_hostText(host->dev_identifier, handle, text); fire_dev_namesAdd(names, text); }
    }
}
static FireDevbDevice* fire_devb_makeHost(const char* name) {
    const fire_host* host = fire_devb_host();
    int32_t i, n;
    char text[512];
    for (i = 0, n = host->dev_count(); i < n; i++) {
        int32_t handle = host->dev_handle_at(i);
        if (handle < 0) continue;
        fire_devb_hostText(host->dev_identifier, handle, text);
        if (strcmp(text, name) == 0) {
            FireDevbDevice* d = fire_devb_newDevice(FIRE_DEVB_HOST, NULL);
            if (d) d->handle = handle;
            return d;
        }
    }
    return NULL;
}
#endif

typedef struct FireDevbManager {
    int counter;
    int ready;
    FireDevbDriver drivers[8];
    size_t driverCount;
    FireDevbSlot** slots;
    size_t slotCount, slotCap;
} FireDevbManager;
static FireDevbManager g_devManager;

static FireDevbSlot* fire_devb_slotByHandle(int64_t handle) {
    size_t i;
    for (i = 0; i < g_devManager.slotCount; i++) if (g_devManager.slots[i]->handle == handle) return g_devManager.slots[i];
    return NULL;
}

static void fire_devb_registerDriver(const char* id, size_t length) {
    FireDevbManager* m = &g_devManager;
    FireDevbDriver slot;
    size_t i;
    for (i = 0; i < m->driverCount; i++) if (strlen(m->drivers[i].id) == length && memcmp(m->drivers[i].id, id, length) == 0) return;
    memset(&slot, 0, sizeof slot);
    slot.handle = m->counter++;
    if (length == 8 && memcmp(id, "loopback", 8) == 0) { slot.id = "loopback"; slot.names = fire_devb_loopbackNames; slot.make = fire_devb_makeLoopback; }
#ifdef FIRE_LIBRARY
    else if (length == 4 && memcmp(id, "host", 4) == 0) { slot.id = "host"; slot.names = fire_devb_hostNames; slot.make = fire_devb_makeHost; slot.verbatim = 1; }
#else
    else if (length == 6 && memcmp(id, "serial", 6) == 0) { slot.id = "serial"; slot.names = fire_devb_serialNames; slot.make = fire_devb_makeSerial; }
#endif
    else return;
    if (m->driverCount < sizeof m->drivers / sizeof m->drivers[0]) m->drivers[m->driverCount++] = slot;
}

/* Adds `s` to the list `*items` (`*count` of `*cap`): 0 when out of memory. */
static int fire_devb_slotListAdd(FireDevbSlot*** items, size_t* count, size_t* cap, FireDevbSlot* s) {
    if (*count == *cap) {
        size_t grownCap = *cap ? *cap * 2 : 16;
        FireDevbSlot** grown = (FireDevbSlot**)realloc(*items, sizeof(FireDevbSlot*) * grownCap);
        if (grown == NULL) return 0;
        *items = grown;
        *cap = grownCap;
    }
    (*items)[(*count)++] = s;
    return 1;
}

/* RefreshDevices: new devices get handles, known ones stay; without `fast` every device is tested (also those that are gone). */
static void fire_devb_refresh(int fast) {
    FireDevbManager* m = &g_devManager;
    size_t k;
    for (k = 0; k < m->driverCount; k++) {
        const FireDevbDriver* driver = &m->drivers[k];
        FireDevNames names = {NULL, 0, 0};
        FireDevbSlot** toTest = NULL;
        size_t testCount = 0, testCap = 0, i, j;
        driver->names(&names);
        for (i = 0; i < names.count; i++) {
            const char* name = names.items[i];
            size_t length = driver->verbatim ? strlen(name) : strlen(driver->id) + 1 + strlen(name);
            char* identifier = (char*)malloc(length + 1);
            FireDevbSlot* existing = NULL;
            if (identifier == NULL) continue;
            if (driver->verbatim) memcpy(identifier, name, length + 1);
            else snprintf(identifier, length + 1, "%s:%s", driver->id, name);
            for (j = 0; j < m->slotCount; j++) if (strcmp(m->slots[j]->identifier, identifier) == 0) { existing = m->slots[j]; break; }
            if (!existing) {
                FireDevbDevice* made = driver->make(name);
                if (!made) { free(identifier); continue; }
                existing = (FireDevbSlot*)calloc(1, sizeof(FireDevbSlot));
                if (existing == NULL || !fire_devb_slotListAdd(&m->slots, &m->slotCount, &m->slotCap, existing)) {
                    free(existing);
                    fire_devb_free(made);
                    free(identifier);
                    continue;
                }
                existing->handle = m->counter++;
                existing->identifier = identifier;
                existing->driver = driver->id;
                existing->device = made;
            } else free(identifier);
            fire_devb_slotListAdd(&toTest, &testCount, &testCap, existing);
        }
        fire_dev_namesFree(&names);
        if (!fast) {
            for (j = 0; j < m->slotCount; j++) {
                FireDevbSlot* s = m->slots[j];
                int listed = 0;
                for (i = 0; i < testCount; i++) if (toTest[i] == s) listed = 1;
                if (strcmp(s->driver, driver->id) == 0 && !listed) fire_devb_slotListAdd(&toTest, &testCount, &testCap, s);
            }
            for (i = 0; i < testCount; i++) fire_devb_testAvailability(toTest[i]->device);
        }
        free(toTest);
    }
}

/* The drivers of FIRE_DEVICES (in this order) and the first device list. */
static void fire_devb_init(void) {
    const char* list = FIRE_DEVICES;
    size_t length = strlen(list), i = 0;
    if (g_devManager.ready) return;
    g_devManager.ready = 1;
    while (i <= length) {
        const char* comma = strchr(list + i, ',');
        size_t next = comma ? (size_t)(comma - list) : length;
        size_t from = i, to = next;
        while (from < to && list[from] == ' ') from++;
        while (to > from && list[to - 1] == ' ') to--;
        if (to > from) fire_devb_registerDriver(list + from, to - from);
        i = next + 1;
    }
    fire_devb_refresh(1);
}

/* Collects what has arrived on the device of `s` into its receive buffer. */
static void fire_devb_collect(FireDevbSlot* s) {
    FireDevbPackets arrived = {NULL, 0, 0};
    size_t i;
    fire_devb_poll(s->device, &arrived);
    for (i = 0; i < arrived.count; i++) {
        if (arrived.items[i].n > 0) fire_devb_packetsPush(&s->buffer.packets, arrived.items[i]);
        else free(arrived.items[i].data);
    }
    free(arrived.items);
}

/* The slot of a handle, with what has arrived since collected into its receive buffer. */
static FireDevbSlot* fire_devb_resolve(Value handle) {
    FireDevbSlot* s;
    fire_devb_init();
    s = fire_devb_slotByHandle(handle.i);
    if (!s) return NULL;
    fire_devb_collect(s);
    return s;
}

#ifndef FIRE_LIBRARY
/* What a wait looks for: the slot and the pattern. */
typedef struct FireDevbWait { FireDevbSlot* slot; const uint8_t* pattern; size_t patternLength; int found; } FireDevbWait;
static bool fire_devb_waitPred(void* ctx) {
    FireDevbWait* w = (FireDevbWait*)ctx;
    fire_devb_collect(w->slot);
    if (fire_devb_consumeThrough(&w->slot->buffer, w->pattern, w->patternLength)) { w->found = 1; return true; }
    return !fire_devb_connected(w->slot->device);   /* a disconnected device delivers nothing more: after a last look the wait ends */
}
/* Waits until `pred` is true or the time is up; false then (also when `terminate` cuts the wait short: the main program keeps serving its queue). */
static bool fire_devb_waitUntil(bool (*pred)(void*), void* ctx, int64_t ticks) {
    int64_t ms = (ticks + TICKS_PER_MS - 1) / TICKS_PER_MS;
    int64_t deadline;
    if (ms < 0) ms = 0;
    deadline = fire_devb_nowMs() + ms;
#ifdef FIRE_THREADS
    {
        /* (a thread waiting inside a method of a shared object does not hold the main program meanwhile) */
        int32_t held = sectionYieldBegin();
        bool done = blockUntilServing(pred, ctx, true, deadline == 0 ? 1 : deadline);   /* (the main program serves its queue meanwhile, like Sleep) */
        sectionYieldEnd(held);
        return done;
    }
#else
    for (;;) {
        if (pred(ctx)) return true;
        if (fire_devb_nowMs() >= deadline) return false;
        fire_plat_sleepMs(2);
    }
#endif
}
#endif

/* ---- strings and buffers ---------------------------------------------------------------------------------------------------------------------------- */
static Value fire_devb_ascii(const char* text, OwnList* list) {
    uint32_t n = (uint32_t)strlen(text);
    Str* s = allocStr(n, list);
    widenAscii(text, n, strChars(s));
    return StrV(s);
}
/* One character per byte (Latin1): every byte value 0..255 maps to exactly one character. */
static Value fire_devb_latin1(const FireDevbBytes* bytes, OwnList* list) {
    Str* s = allocStr((uint32_t)bytes->n, list);
    fire_char16* out = strChars(s);
    size_t i;
    for (i = 0; i < bytes->n; i++) out[i] = bytes->data[i];
    return StrV(s);
}
/* The text as bytes (a character above 255 becomes '?'; malloc, the caller frees it): 0 when out of memory. */
static int fire_devb_toLatin1(Value text, FireDevbBytes* out) {
    const Str* s = strOf(text);
    uint32_t i;
    out->n = s->length;
    out->data = (uint8_t*)malloc(s->length ? s->length : 1);
    if (out->data == NULL) { out->n = 0; return 0; }
    for (i = 0; i < s->length; i++) out->data[i] = s->data[i] < 256 ? (uint8_t)s->data[i] : (uint8_t)'?';
    return 1;
}

/* ---- the natives (called from the generated code as `fire_devices_Name`: the part after `__DEV`) ------------------------------------------------------ */
static Value fire_devices_MgrRefresh(Value fast) {
    fire_devb_init();
#ifdef FIRE_LIBRARY
    {
        const fire_host* host = fire_devb_host();
        if (host) host->dev_refresh((int32_t)fast.i);   /* the host looks at its drivers first */
    }
#endif
    fire_devb_refresh(fast.i != 0);
    return Undef();
}
static Value fire_devices_MgrHandleForIdentifier(Value identifier) {
    const Str* s = strOf(identifier);
    char* id;
    size_t i;
    fire_devb_init();
    id = (char*)malloc((size_t)s->length + 1);
    if (id == NULL) return Int(-1);
    for (i = 0; i < s->length; i++) id[i] = s->data[i] < 128 ? (char)s->data[i] : '?';
    id[s->length] = 0;
    for (i = 0; i < g_devManager.slotCount; i++) {
        const FireDevbSlot* slot = g_devManager.slots[i];
        if (strlen(slot->identifier) == s->length && memcmp(slot->identifier, id, s->length) == 0) { free(id); return Int(slot->handle); }
    }
    free(id);
    return Int(-1);
}
static Value fire_devices_MgrCount(void) { fire_devb_init(); return Int((int64_t)g_devManager.slotCount); }
static Value fire_devices_MgrHandleAt(Value index) {
    fire_devb_init();
    return Int(index.i >= 0 && index.i < (int64_t)g_devManager.slotCount ? g_devManager.slots[(size_t)index.i]->handle : -1);
}
static Value fire_devices_MgrIsShared(void) {
#ifdef FIRE_LIBRARY
    const fire_host* host = fire_devb_host();
    if (host) return Bool(host->dev_manager_shared() != 0);
#endif
    return Bool(false);
}
/* The identifier of the default device into `out` (512 bytes): the host's (the editor), in a native build FIRE_DEFAULT_DEVICE; "" when there is none. */
static void fire_devb_defaultIdentifier(char* out) {
#ifdef FIRE_LIBRARY
    const fire_host* host = fire_devb_host();
    out[0] = 0;
    if (host) { int32_t handle = host->dev_default(); if (handle >= 0) fire_devb_hostText(host->dev_identifier, handle, out); }
#else
    snprintf(out, 512, "%s", FIRE_DEFAULT_DEVICE);
#endif
}
static Value fire_devices_MgrDefaultHandle(void) {
    char id[512];
    size_t i;
    fire_devb_init();
    fire_devb_defaultIdentifier(id);
    if (id[0] == 0) return Int(-1);
    for (i = 0; i < g_devManager.slotCount; i++) if (strcmp(g_devManager.slots[i]->identifier, id) == 0) return Int(g_devManager.slots[i]->handle);
    return Int(-1);
}

/* DeviceHost.DefaultIdentifier: the identifier of the device the host chose (the editor; a native build: FIRE_DEFAULT_DEVICE), "" when there is none. It may
 * name a device of any driver of the program (`gpio:gpioa`), not only one of this manager. */
static Value fire_devices_HostDefault(OwnList* list) {
    char id[512];
    fire_devb_init();
    fire_devb_defaultIdentifier(id);
    return fire_devb_ascii(id, list);
}
static Value fire_devices_Identifier(Value handle, OwnList* list) {
    FireDevbSlot* s;
    fire_devb_init();
    s = fire_devb_slotByHandle(handle.i);
    return fire_devb_ascii(s ? s->identifier : "", list);
}
static Value fire_devices_IsShared(Value handle) { FireDevbSlot* s = fire_devb_resolve(handle); return Bool(s && fire_devb_shared(s->device)); }
static Value fire_devices_IsConnected(Value handle) { FireDevbSlot* s = fire_devb_resolve(handle); return Bool(s && fire_devb_connected(s->device)); }
static Value fire_devices_PortName(Value handle, OwnList* list) {
    FireDevbSlot* s = fire_devb_resolve(handle);
    char name[512];
    name[0] = 0;
    if (s) fire_devb_portName(s->device, name);
    return fire_devb_ascii(name, list);
}
static Value fire_devices_Availability(Value handle) { FireDevbSlot* s = fire_devb_resolve(handle); return Int(s ? fire_devb_availability(s->device) : FIRE_DEVB_UNAVAILABLE); }
static Value fire_devices_TestAvailability(Value handle) { FireDevbSlot* s = fire_devb_resolve(handle); return Int(s ? fire_devb_testAvailability(s->device) : FIRE_DEVB_UNAVAILABLE); }
static Value fire_devices_Connect(Value handle) { FireDevbSlot* s = fire_devb_resolve(handle); return Bool(s && fire_devb_connect(s->device)); }
static Value fire_devices_Disconnect(Value handle) { FireDevbSlot* s = fire_devb_resolve(handle); if (s) fire_devb_disconnect(s->device); return Undef(); }
/* A line: the text and a line ending, in the encoding of the device (the prelude decides between a text and a command object). */
static Value fire_devices_DoCommand(Value handle, Value text) {
    FireDevbSlot* s = fire_devb_resolve(handle);
    const Str* str;
    if (!s) return Bool(false);
    str = strOf(text);
    return Bool(fire_devb_sendCommand(s->device, str->data, str->length));
}
static Value fire_devices_HasData(Value handle) { FireDevbSlot* s = fire_devb_resolve(handle); return Bool(s && s->buffer.packets.count > 0); }
static Value fire_devices_ReadString(Value handle, OwnList* list) {
    FireDevbSlot* s = fire_devb_resolve(handle);
    FireDevbBytes bytes = {NULL, 0};
    Value result;
    if (s) bytes = fire_devb_take(&s->buffer);
    result = fire_devb_latin1(&bytes, list);
    free(bytes.data);
    return result;
}
static Value fire_devices_Read(Value handle, OwnList* list) {
    FireDevbSlot* s = fire_devb_resolve(handle);
    FireDevbBytes bytes = {NULL, 0};
    Buf* b;
    if (s) bytes = fire_devb_take(&s->buffer);
    b = allocBuf((uint32_t)bytes.n, list);
    if (bytes.n > 0) memcpy(b->data, bytes.data, bytes.n);
    free(bytes.data);
    return BufV(b);
}
static Value fire_devices_WriteString(Value handle, Value text) {
    FireDevbSlot* s = fire_devb_resolve(handle);
    FireDevbBytes bytes;
    int done;
    if (!s) return Bool(false);
    if (!fire_devb_toLatin1(text, &bytes)) return Bool(false);
    done = fire_devb_write(s->device, bytes.data, bytes.n);
    free(bytes.data);
    return Bool(done);
}
static Value fire_devices_Write(Value handle, Value buffer) {
    FireDevbSlot* s;
    FireBytes b;
    if (!bytesArg(buffer, "Device.Write", &b)) return Undef();
    s = fire_devb_resolve(handle);
    if (!s) return Bool(false);
    return Bool(fire_devb_write(s->device, b.data, b.length));
}

#ifndef FIRE_LIBRARY
/* 1: found (the buffer is cut behind it), 0: time up / device disconnected / program ended, -1: not a time. */
static Value fire_devb_waitFor(Value handle, const uint8_t* pattern, size_t patternLength, Value timeout) {
    FireDevbSlot* s = fire_devb_resolve(handle);
    FireDevbWait w;
    int64_t ticks;
    if (!s) return Int(0);
    if (timeout.kind == K_Undefined) ticks = g_defaultTimeoutTicks;
    else if (!timeTicksOf(timeout, &ticks)) return Int(-1);
    w.slot = s;
    w.pattern = pattern;
    w.patternLength = patternLength;
    w.found = 0;
    fire_devb_waitUntil(fire_devb_waitPred, &w, ticks);
    return Int(w.found ? 1 : 0);
}
static Value fire_devices_WaitForString(Value handle, Value text, Value timeout) {
    FireDevbBytes pattern;
    Value result;
    if (!fire_devb_toLatin1(text, &pattern)) return Int(0);
    result = fire_devb_waitFor(handle, pattern.data, pattern.n, timeout);
    free(pattern.data);
    return result;
}
static Value fire_devices_WaitFor(Value handle, Value buffer, Value timeout) {
    FireDevbBytes pattern;
    FireBytes b;
    Value result;
    if (!bytesArg(buffer, "Device.WaitFor", &b)) return Undef();
    /* (a copy: the buffer of the program may change while the wait serves the other threads) */
    if (!fire_devb_bytesOf(&pattern, b.data, b.length)) return Int(0);
    result = fire_devb_waitFor(handle, pattern.data, pattern.n, timeout);
    free(pattern.data);
    return result;
}
#endif

/* One step of a wait (the VM runs the waiting functions itself and calls this until it succeeds): looks at what has arrived and cuts the buffer behind
 * `pattern` if it is there. 1: found, 0: not yet, 2: not there and the device is disconnected (nothing more will come). */
static Value fire_devices_WaitStep(Value handle, Value buffer) {
    FireDevbSlot* s;
    FireBytes b;
    if (!bytesArg(buffer, "Device.WaitFor", &b)) return Undef();
    s = fire_devb_resolve(handle);
    if (!s) return Int(2);
    if (fire_devb_consumeThrough(&s->buffer, b.data, b.length)) return Int(1);
    return Int(fire_devb_connected(s->device) ? 0 : 2);
}

#endif
