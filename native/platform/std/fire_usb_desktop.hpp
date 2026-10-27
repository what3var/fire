// fire native platform layer, USB part for the hosted platforms: WinUSB on Windows, libusb-1.0 on Linux and macOS (both loaded at run time), nothing elsewhere.
#pragma once

#if defined(_WIN32) && !defined(FIRE_NO_USB)
#include "fire_usb_winusb.hpp"
#elif (defined(__linux__) || defined(__APPLE__)) && !defined(FIRE_NO_USB)
#include "fire_usb_libusb.hpp"
#else
#include "fire_usb_none.hpp"
#endif
