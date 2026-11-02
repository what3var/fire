/* fire native platform layer, USB part in C (the HAL of the usb bridge): the types every backend shares - the error codes of the language, the status of an
 * operation, the lists of devices and endpoints.
 *
 * A platform package offers `fire_usb.h` (FIRE_PLATFORM_USB_C_HEADER points to it) with:
 *
 *   FIRE_USB_E_*                   the codes of the error slot (the same numbers as in the prelude: Usb.UsbException.code)
 *   FireUsbStatus { int code; char message[176]; }     code 0 = ok
 *   FireUsbDeviceInfo              id (what openDevice takes), vendor, product, class, bus, address, manufacturer, product name, serial number
 *   FireUsbEndpointInfo            interface, alternate setting, address (bit 7: IN), type (0 control, 1 isochronous, 2 bulk, 3 interrupt), max packet size, interface class
 *   FireUsbDevice                  an opened device (opaque)
 *   int  fire_usb_supported(void)  0: there is no USB access here (the list is empty)
 *   int  fire_usb_devices(FireUsbDeviceList*, FireUsbStatus*)
 *   int  fire_usb_openDevice(const char* id, FireUsbDevice**, st)
 *   int  fire_usb_endpoints(FireUsbDevice*, FireUsbEndpointList*, st)
 *   int  fire_usb_claim(FireUsbDevice*, int interface, st) / fire_usb_release(d, interface, st) / fire_usb_setAlternate(d, interface, alternate, st)
 *   int  fire_usb_control(d, requestType, request, value, index, uint8_t* data, int length, int timeoutMs, int* transferred, st)
 *   int  fire_usb_transfer(d, int type, int endpoint, uint8_t* data, int length, int timeoutMs, int* transferred, st)   type 2 bulk, 3 interrupt; the direction
 *        is bit 7 of the endpoint
 *   int  fire_usb_clearHalt(d, int endpoint, st)
 *   void fire_usb_closeDevice(FireUsbDevice*)
 * The functions that return int give 1 when done and 0 on an error (the status says why). A timeout of 0 waits without a limit. Transfers are done in the
 * call. */
#ifndef FIRE_USB_C_H
#define FIRE_USB_C_H

#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { FIRE_USB_E_NONE = 0, FIRE_USB_E_INVALID_ARGUMENT = 1, FIRE_USB_E_INVALID_HANDLE = 2, FIRE_USB_E_NOT_FOUND = 3, FIRE_USB_E_BUSY = 4, FIRE_USB_E_PERMISSION = 5,
       FIRE_USB_E_UNSUPPORTED = 6, FIRE_USB_E_OTHER = 7, FIRE_USB_E_TIMEOUT = 8, FIRE_USB_E_STALL = 9, FIRE_USB_E_DISCONNECTED = 10, FIRE_USB_E_OVERFLOW = 11 };

typedef struct FireUsbStatus { int code; char message[176]; } FireUsbStatus;

/* Records a failure (printf format): always 0. */
static int fire_usb_fail(FireUsbStatus* st, int code, const char* format, ...) {
    va_list args;
    st->code = code;
    va_start(args, format);
    vsnprintf(st->message, sizeof st->message, format, args);
    va_end(args);
    return 0;
}
static int fire_usb_success(FireUsbStatus* st) { st->code = 0; st->message[0] = 0; return 1; }

typedef struct FireUsbDeviceInfo {
    char id[512], manufacturer[256], product[256], serial[256];
    int vendorId, productId, deviceClass, bus, address;
} FireUsbDeviceInfo;
typedef struct FireUsbEndpointInfo {
    int interfaceNumber, alternate, address, type, maxPacket, interfaceClass;
} FireUsbEndpointInfo;

/* ---- the lists (malloc; fire_usb_*ListFree gives them back) ---- */
typedef struct FireUsbDeviceList { FireUsbDeviceInfo* items; size_t count, cap; } FireUsbDeviceList;
typedef struct FireUsbEndpointList { FireUsbEndpointInfo* items; size_t count, cap; } FireUsbEndpointList;

/* A new entry at the end (zeroed), NULL when out of memory. */
static FireUsbDeviceInfo* fire_usb_deviceListAdd(FireUsbDeviceList* l) {
    if (l->count == l->cap) {
        size_t cap = l->cap ? l->cap * 2 : 8;
        FireUsbDeviceInfo* grown = (FireUsbDeviceInfo*)realloc(l->items, sizeof(FireUsbDeviceInfo) * cap);
        if (grown == NULL) return NULL;
        l->items = grown;
        l->cap = cap;
    }
    memset(&l->items[l->count], 0, sizeof(FireUsbDeviceInfo));
    return &l->items[l->count++];
}
static void fire_usb_deviceListFree(FireUsbDeviceList* l) {
    free(l->items);
    l->items = NULL;
    l->count = l->cap = 0;
}
static int fire_usb_endpointListAdd(FireUsbEndpointList* l, const FireUsbEndpointInfo* e) {
    if (l->count == l->cap) {
        size_t cap = l->cap ? l->cap * 2 : 8;
        FireUsbEndpointInfo* grown = (FireUsbEndpointInfo*)realloc(l->items, sizeof(FireUsbEndpointInfo) * cap);
        if (grown == NULL) return 0;
        l->items = grown;
        l->cap = cap;
    }
    l->items[l->count++] = *e;
    return 1;
}
static void fire_usb_endpointListFree(FireUsbEndpointList* l) {
    free(l->items);
    l->items = NULL;
    l->count = l->cap = 0;
}

#endif
