/* fire native platform layer, USB part through libusb-1.0 (Linux, macOS; see fire_usb_c.h for the interface). libusb is loaded with dlopen when it is first
 * needed and only the few functions used here are looked up - a program runs without it (the list of devices is then empty and opening fails with a message
 * that says what to install). On Linux the user needs access to the device nodes (/dev/bus/usb/...: a udev rule, or the group of the device); a device that a
 * kernel driver holds is detached from it while an interface is claimed. */
#ifndef FIRE_USB_LIBUSB_H
#define FIRE_USB_LIBUSB_H

#include "fire_usb_c.h"
#include <dlfcn.h>
#include <pthread.h>

/* the parts of libusb.h used here (its ABI has been stable since libusb 1.0) */
typedef struct fire_lu_device_descriptor {
    uint8_t bLength, bDescriptorType; uint16_t bcdUSB; uint8_t bDeviceClass, bDeviceSubClass, bDeviceProtocol, bMaxPacketSize0;
    uint16_t idVendor, idProduct, bcdDevice; uint8_t iManufacturer, iProduct, iSerialNumber, bNumConfigurations;
} fire_lu_device_descriptor;
typedef struct fire_lu_endpoint_descriptor {
    uint8_t bLength, bDescriptorType, bEndpointAddress, bmAttributes; uint16_t wMaxPacketSize; uint8_t bInterval, bRefresh, bSynchAddress;
    const unsigned char* extra; int extra_length;
} fire_lu_endpoint_descriptor;
typedef struct fire_lu_interface_descriptor {
    uint8_t bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
    const fire_lu_endpoint_descriptor* endpoint; const unsigned char* extra; int extra_length;
} fire_lu_interface_descriptor;
typedef struct fire_lu_interface { const fire_lu_interface_descriptor* altsetting; int num_altsetting; } fire_lu_interface;
typedef struct fire_lu_config_descriptor {
    uint8_t bLength, bDescriptorType; uint16_t wTotalLength; uint8_t bNumInterfaces, bConfigurationValue, iConfiguration, bmAttributes, MaxPower;
    const fire_lu_interface* interfaces; const unsigned char* extra; int extra_length;
} fire_lu_config_descriptor;
typedef struct fire_lu_context fire_lu_context;
typedef struct fire_lu_device fire_lu_device;
typedef struct fire_lu_handle fire_lu_handle;

typedef struct FireUsbLib {
    void* handle;
    int tried;
    fire_lu_context* ctx;
    char error[176];
    int (*init)(fire_lu_context**);
    long (*get_device_list)(fire_lu_context*, fire_lu_device***);
    void (*free_device_list)(fire_lu_device**, int);
    int (*get_device_descriptor)(fire_lu_device*, fire_lu_device_descriptor*);
    uint8_t (*get_bus_number)(fire_lu_device*);
    uint8_t (*get_device_address)(fire_lu_device*);
    fire_lu_device* (*ref_device)(fire_lu_device*);
    void (*unref_device)(fire_lu_device*);
    int (*open)(fire_lu_device*, fire_lu_handle**);
    void (*close)(fire_lu_handle*);
    int (*get_string_descriptor_ascii)(fire_lu_handle*, uint8_t, unsigned char*, int);
    int (*get_active_config_descriptor)(fire_lu_device*, fire_lu_config_descriptor**);
    void (*free_config_descriptor)(fire_lu_config_descriptor*);
    int (*claim_interface)(fire_lu_handle*, int);
    int (*release_interface)(fire_lu_handle*, int);
    int (*set_interface_alt_setting)(fire_lu_handle*, int, int);
    int (*set_auto_detach_kernel_driver)(fire_lu_handle*, int);
    int (*control_transfer)(fire_lu_handle*, uint8_t, uint8_t, uint16_t, uint16_t, unsigned char*, uint16_t, unsigned int);
    int (*bulk_transfer)(fire_lu_handle*, unsigned char, unsigned char*, int, int*, unsigned int);
    int (*interrupt_transfer)(fire_lu_handle*, unsigned char, unsigned char*, int, int*, unsigned int);
    int (*clear_halt)(fire_lu_handle*, unsigned char);
} FireUsbLib;

static FireUsbLib g_fireUsbLib;
static pthread_mutex_t g_fireUsbLock = PTHREAD_MUTEX_INITIALIZER;

/* Looks up `name` into the function pointer at `fn` (a pointer to it): 0 when libusb does not have it. */
static int fire_usb_luBind(const char* name, void* fn) {
    void* p = dlsym(g_fireUsbLib.handle, name);
    memcpy(fn, &p, sizeof p);
    return p != NULL;
}

static int fire_usb_luLoad(void) {
    FireUsbLib* l = &g_fireUsbLib;
    static const char* const names[] = {"libusb-1.0.so.0", "libusb-1.0.so", "libusb-1.0.0.dylib", "libusb-1.0.dylib", "/opt/homebrew/lib/libusb-1.0.0.dylib",
                                        "/usr/local/lib/libusb-1.0.0.dylib"};
    size_t i;
    int all;
    if (l->tried) return l->ctx != NULL;
    l->tried = 1;
    for (i = 0; i < sizeof names / sizeof names[0]; i++) { l->handle = dlopen(names[i], RTLD_LAZY | RTLD_LOCAL); if (l->handle) break; }
    if (!l->handle) { snprintf(l->error, sizeof l->error, "libusb-1.0 is not installed (Debian/Ubuntu: apt install libusb-1.0-0, macOS: brew install libusb)."); return 0; }
    all = fire_usb_luBind("libusb_init", &l->init) && fire_usb_luBind("libusb_get_device_list", &l->get_device_list)
        && fire_usb_luBind("libusb_free_device_list", &l->free_device_list) && fire_usb_luBind("libusb_get_device_descriptor", &l->get_device_descriptor)
        && fire_usb_luBind("libusb_get_bus_number", &l->get_bus_number) && fire_usb_luBind("libusb_get_device_address", &l->get_device_address)
        && fire_usb_luBind("libusb_ref_device", &l->ref_device) && fire_usb_luBind("libusb_unref_device", &l->unref_device) && fire_usb_luBind("libusb_open", &l->open)
        && fire_usb_luBind("libusb_close", &l->close) && fire_usb_luBind("libusb_get_string_descriptor_ascii", &l->get_string_descriptor_ascii)
        && fire_usb_luBind("libusb_get_active_config_descriptor", &l->get_active_config_descriptor)
        && fire_usb_luBind("libusb_free_config_descriptor", &l->free_config_descriptor) && fire_usb_luBind("libusb_claim_interface", &l->claim_interface)
        && fire_usb_luBind("libusb_release_interface", &l->release_interface) && fire_usb_luBind("libusb_set_interface_alt_setting", &l->set_interface_alt_setting)
        && fire_usb_luBind("libusb_control_transfer", &l->control_transfer) && fire_usb_luBind("libusb_bulk_transfer", &l->bulk_transfer)
        && fire_usb_luBind("libusb_interrupt_transfer", &l->interrupt_transfer) && fire_usb_luBind("libusb_clear_halt", &l->clear_halt);
    fire_usb_luBind("libusb_set_auto_detach_kernel_driver", &l->set_auto_detach_kernel_driver);   /* (not on every platform) */
    if (!all) { snprintf(l->error, sizeof l->error, "The libusb-1.0 found is too old."); return 0; }
    if (l->init(&l->ctx) != 0) { l->ctx = NULL; snprintf(l->error, sizeof l->error, "libusb could not be started."); return 0; }
    return 1;
}

/* The status of a libusb error code: always 0. */
static int fire_usb_luStatus(int r, const char* what, FireUsbStatus* st) {
    switch (r) {
        case -3: return fire_usb_fail(st, FIRE_USB_E_PERMISSION, "%s: no permission (on Linux a udev rule must give the user access to the device).", what);
        case -4: return fire_usb_fail(st, FIRE_USB_E_DISCONNECTED, "%s: the device is gone.", what);
        case -5: return fire_usb_fail(st, FIRE_USB_E_NOT_FOUND, "%s: not found.", what);
        case -6: return fire_usb_fail(st, FIRE_USB_E_BUSY, "%s: busy (another program or a driver holds it).", what);
        case -7: return fire_usb_fail(st, FIRE_USB_E_TIMEOUT, "%s: timed out.", what);
        case -8: return fire_usb_fail(st, FIRE_USB_E_OVERFLOW, "%s: the device sent more than was asked for (read whole packets).", what);
        case -9: return fire_usb_fail(st, FIRE_USB_E_STALL, "%s: the endpoint stalled.", what);
        case -2: return fire_usb_fail(st, FIRE_USB_E_INVALID_ARGUMENT, "%s: invalid argument.", what);
        case -12: return fire_usb_fail(st, FIRE_USB_E_UNSUPPORTED, "%s: not supported.", what);
        default: return fire_usb_fail(st, FIRE_USB_E_OTHER, "%s: error %d.", what, r);
    }
}

typedef struct FireUsbDevice {
    fire_lu_device* device;
    fire_lu_handle* handle;
} FireUsbDevice;

static void fire_usb_luIdOf(int bus, int address, char* text, size_t size) { snprintf(text, size, "%03d:%03d", bus, address); }

static int fire_usb_supported(void) {
    int r;
    pthread_mutex_lock(&g_fireUsbLock);
    r = fire_usb_luLoad();
    pthread_mutex_unlock(&g_fireUsbLock);
    return r;
}

/* (with the lock held) the string `index` of an opened device into `into` (stays empty when there is none) */
static void fire_usb_luString(fire_lu_handle* h, uint8_t index, char* into, size_t size) {
    unsigned char text[256];
    int k;
    if (index == 0) return;
    k = g_fireUsbLib.get_string_descriptor_ascii(h, index, text, (int)sizeof text - 1);
    if (k > 0) {
        size_t n = (size_t)k < size - 1 ? (size_t)k : size - 1;
        memcpy(into, text, n);
        into[n] = 0;
    }
}

static int fire_usb_devices(FireUsbDeviceList* out, FireUsbStatus* st) {
    FireUsbLib* l = &g_fireUsbLib;
    fire_lu_device** list = NULL;
    long n, i;
    out->count = 0;
    pthread_mutex_lock(&g_fireUsbLock);
    if (!fire_usb_luLoad()) { pthread_mutex_unlock(&g_fireUsbLock); return fire_usb_success(st); }   /* no libusb: no devices (opening says why) */
    n = l->get_device_list(l->ctx, &list);
    if (n < 0) { pthread_mutex_unlock(&g_fireUsbLock); return fire_usb_luStatus((int)n, "Listing the USB devices", st); }
    for (i = 0; i < n; i++) {
        fire_lu_device_descriptor d;
        FireUsbDeviceInfo* info;
        fire_lu_handle* h = NULL;
        memset(&d, 0, sizeof d);
        if (l->get_device_descriptor(list[i], &d) != 0) continue;
        info = fire_usb_deviceListAdd(out);
        if (info == NULL) break;
        info->vendorId = d.idVendor;
        info->productId = d.idProduct;
        info->deviceClass = d.bDeviceClass;
        info->bus = l->get_bus_number(list[i]);
        info->address = l->get_device_address(list[i]);
        fire_usb_luIdOf(info->bus, info->address, info->id, sizeof info->id);
        /* the strings need the device opened: without permission they stay empty */
        if (l->open(list[i], &h) == 0 && h) {
            fire_usb_luString(h, d.iManufacturer, info->manufacturer, sizeof info->manufacturer);
            fire_usb_luString(h, d.iProduct, info->product, sizeof info->product);
            fire_usb_luString(h, d.iSerialNumber, info->serial, sizeof info->serial);
            l->close(h);
        }
    }
    l->free_device_list(list, 1);
    pthread_mutex_unlock(&g_fireUsbLock);
    return fire_usb_success(st);
}

static int fire_usb_openDevice(const char* id, FireUsbDevice** out, FireUsbStatus* st) {
    FireUsbLib* l = &g_fireUsbLib;
    fire_lu_device** list = NULL;
    fire_lu_device* found = NULL;
    fire_lu_handle* h = NULL;
    char text[16], what[600];
    long n, i;
    int r;
    *out = NULL;
    pthread_mutex_lock(&g_fireUsbLock);
    if (!fire_usb_luLoad()) { pthread_mutex_unlock(&g_fireUsbLock); return fire_usb_fail(st, FIRE_USB_E_UNSUPPORTED, "%s", l->error); }
    n = l->get_device_list(l->ctx, &list);
    if (n < 0) { pthread_mutex_unlock(&g_fireUsbLock); return fire_usb_luStatus((int)n, "Listing the USB devices", st); }
    for (i = 0; i < n && !found; i++) {
        fire_usb_luIdOf(l->get_bus_number(list[i]), l->get_device_address(list[i]), text, sizeof text);
        if (strcmp(text, id) == 0) found = l->ref_device(list[i]);
    }
    l->free_device_list(list, 1);
    if (!found) { pthread_mutex_unlock(&g_fireUsbLock); return fire_usb_fail(st, FIRE_USB_E_NOT_FOUND, "There is no USB device %s.", id); }
    r = l->open(found, &h);
    if (r != 0) {
        l->unref_device(found);
        pthread_mutex_unlock(&g_fireUsbLock);
        snprintf(what, sizeof what, "Opening the USB device %s", id);
        return fire_usb_luStatus(r, what, st);
    }
    if (l->set_auto_detach_kernel_driver) l->set_auto_detach_kernel_driver(h, 1);
    *out = (FireUsbDevice*)calloc(1, sizeof(FireUsbDevice));
    if (*out == NULL) {
        l->close(h);
        l->unref_device(found);
        pthread_mutex_unlock(&g_fireUsbLock);
        return fire_usb_fail(st, FIRE_USB_E_OTHER, "Out of memory.");
    }
    (*out)->device = found;
    (*out)->handle = h;
    pthread_mutex_unlock(&g_fireUsbLock);
    return fire_usb_success(st);
}

static int fire_usb_endpoints(FireUsbDevice* d, FireUsbEndpointList* out, FireUsbStatus* st) {
    FireUsbLib* l = &g_fireUsbLib;
    fire_lu_config_descriptor* config = NULL;
    int r, i, a, e;
    out->count = 0;
    pthread_mutex_lock(&g_fireUsbLock);
    r = l->get_active_config_descriptor(d->device, &config);
    if (r != 0) { pthread_mutex_unlock(&g_fireUsbLock); return fire_usb_luStatus(r, "Reading the configuration", st); }
    for (i = 0; i < config->bNumInterfaces; i++) {
        const fire_lu_interface* itf = &config->interfaces[i];
        for (a = 0; a < itf->num_altsetting; a++) {
            const fire_lu_interface_descriptor* alt = &itf->altsetting[a];
            for (e = 0; e < alt->bNumEndpoints; e++) {
                FireUsbEndpointInfo info;
                info.interfaceNumber = alt->bInterfaceNumber;
                info.alternate = alt->bAlternateSetting;
                info.address = alt->endpoint[e].bEndpointAddress;
                info.type = alt->endpoint[e].bmAttributes & 3;
                info.maxPacket = alt->endpoint[e].wMaxPacketSize & 0x7FF;
                info.interfaceClass = alt->bInterfaceClass;
                fire_usb_endpointListAdd(out, &info);
            }
        }
    }
    l->free_config_descriptor(config);
    pthread_mutex_unlock(&g_fireUsbLock);
    return fire_usb_success(st);
}

static int fire_usb_claim(FireUsbDevice* d, int itf, FireUsbStatus* st) {
    char what[64];
    int r = g_fireUsbLib.claim_interface(d->handle, itf);
    if (r == 0) return fire_usb_success(st);
    snprintf(what, sizeof what, "Claiming interface %d", itf);
    return fire_usb_luStatus(r, what, st);
}
static int fire_usb_release(FireUsbDevice* d, int itf, FireUsbStatus* st) {
    char what[64];
    int r = g_fireUsbLib.release_interface(d->handle, itf);
    if (r == 0) return fire_usb_success(st);
    snprintf(what, sizeof what, "Releasing interface %d", itf);
    return fire_usb_luStatus(r, what, st);
}
static int fire_usb_setAlternate(FireUsbDevice* d, int itf, int alt, FireUsbStatus* st) {
    int r = g_fireUsbLib.set_interface_alt_setting(d->handle, itf, alt);
    return r == 0 ? fire_usb_success(st) : fire_usb_luStatus(r, "Setting the alternate setting", st);
}
static int fire_usb_control(FireUsbDevice* d, int requestType, int request, int value, int index, uint8_t* data, int length, int timeout, int* transferred,
                            FireUsbStatus* st) {
    int r;
    *transferred = 0;
    r = g_fireUsbLib.control_transfer(d->handle, (uint8_t)requestType, (uint8_t)request, (uint16_t)value, (uint16_t)index, data, (uint16_t)length, (unsigned)timeout);
    if (r < 0) return fire_usb_luStatus(r, "The control transfer", st);
    *transferred = r;
    return fire_usb_success(st);
}
static int fire_usb_transfer(FireUsbDevice* d, int type, int endpoint, uint8_t* data, int length, int timeout, int* transferred, FireUsbStatus* st) {
    int done = 0, r;
    char what[64];
    *transferred = 0;
    r = type == 3 ? g_fireUsbLib.interrupt_transfer(d->handle, (unsigned char)endpoint, data, length, &done, (unsigned)timeout)
                  : g_fireUsbLib.bulk_transfer(d->handle, (unsigned char)endpoint, data, length, &done, (unsigned)timeout);
    *transferred = done;
    if (r != 0) {
        snprintf(what, sizeof what, "%s%d", endpoint & 0x80 ? "Reading from endpoint " : "Writing to endpoint ", endpoint & 0x7F);
        return fire_usb_luStatus(r, what, st);
    }
    return fire_usb_success(st);
}
static int fire_usb_clearHalt(FireUsbDevice* d, int endpoint, FireUsbStatus* st) {
    int r = g_fireUsbLib.clear_halt(d->handle, (unsigned char)endpoint);
    return r == 0 ? fire_usb_success(st) : fire_usb_luStatus(r, "Clearing the halt", st);
}
static void fire_usb_closeDevice(FireUsbDevice* d) {
    if (!d) return;
    if (d->handle) g_fireUsbLib.close(d->handle);
    if (d->device) g_fireUsbLib.unref_device(d->device);
    free(d);
}

#endif
