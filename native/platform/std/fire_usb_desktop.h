/* fire native platform layer, USB part for the hosted platforms: WinUSB on Windows, libusb-1.0 on Linux and macOS (both loaded at run time), nothing elsewhere. */
#ifndef FIRE_USB_DESKTOP_H
#define FIRE_USB_DESKTOP_H

#if defined(_WIN32) && !defined(FIRE_NO_USB)
#include "fire_usb_winusb.h"
#elif (defined(__linux__) || defined(__APPLE__)) && !defined(FIRE_NO_USB)
#include "fire_usb_libusb.h"
#else
#include "fire_usb_none.h"
#endif

#endif
