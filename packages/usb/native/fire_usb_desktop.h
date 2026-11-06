/* fire usb package: USB part for the hosted platforms: WinUSB on Windows, libusb-1.0 on Linux and macOS (both loaded at run time), nothing elsewhere. */
#ifndef FIRE_USB_DESKTOP_H
#define FIRE_USB_DESKTOP_H

#if defined(_WIN32) && !defined(FIRE_NO_USB)
#define FIRE_USB_USE_WINUSB 1
#elif (defined(__linux__) || defined(__APPLE__)) && !defined(FIRE_NO_USB)
#define FIRE_USB_USE_LIBUSB 1
#else
#define FIRE_USB_USE_NONE 1
#endif

#endif
