/* fire usb package: USB part for a platform without USB host access (a board, the browser): the list of devices is empty and every call fails with
 * "not supported". The simulated device "sim" of the bridge works everywhere. The interface is described in fire_usb_c.h. */
#ifndef FIRE_USB_NONE_H
#define FIRE_USB_NONE_H
#if !defined(FIRE_USB_DESKTOP_H) || defined(FIRE_USB_USE_NONE)   /* (with fire_usb_desktop.h in the build: when it chose this one) */

#include "fire_usb_c.h"

typedef struct FireUsbDevice { int unused; } FireUsbDevice;

static int fire_usb_supported(void) { return 0; }
static int fire_usb_none(FireUsbStatus* st) { return fire_usb_fail(st, FIRE_USB_E_UNSUPPORTED, "This platform has no USB access (the simulated device \"sim\" works everywhere)."); }

static int fire_usb_devices(FireUsbDeviceList* out, FireUsbStatus* st) { out->count = 0; return fire_usb_success(st); }
static int fire_usb_openDevice(const char* id, FireUsbDevice** out, FireUsbStatus* st) {
    (void)id;
    *out = NULL;
    return fire_usb_fail(st, FIRE_USB_E_NOT_FOUND, "There is no such USB device on this platform.");
}
static int fire_usb_endpoints(FireUsbDevice* d, FireUsbEndpointList* out, FireUsbStatus* st) { (void)d; out->count = 0; return fire_usb_none(st); }
static int fire_usb_claim(FireUsbDevice* d, int itf, FireUsbStatus* st) { (void)d; (void)itf; return fire_usb_none(st); }
static int fire_usb_release(FireUsbDevice* d, int itf, FireUsbStatus* st) { (void)d; (void)itf; return fire_usb_none(st); }
static int fire_usb_setAlternate(FireUsbDevice* d, int itf, int alt, FireUsbStatus* st) { (void)d; (void)itf; (void)alt; return fire_usb_none(st); }
static int fire_usb_control(FireUsbDevice* d, int requestType, int request, int value, int index, uint8_t* data, int length, int timeout, int* transferred,
                            FireUsbStatus* st) {
    (void)d; (void)requestType; (void)request; (void)value; (void)index; (void)data; (void)length; (void)timeout;
    *transferred = 0;
    return fire_usb_none(st);
}
static int fire_usb_transfer(FireUsbDevice* d, int type, int endpoint, uint8_t* data, int length, int timeout, int* transferred, FireUsbStatus* st) {
    (void)d; (void)type; (void)endpoint; (void)data; (void)length; (void)timeout;
    *transferred = 0;
    return fire_usb_none(st);
}
static int fire_usb_clearHalt(FireUsbDevice* d, int endpoint, FireUsbStatus* st) { (void)d; (void)endpoint; return fire_usb_none(st); }
static void fire_usb_closeDevice(FireUsbDevice* d) { (void)d; }

#endif
#endif
