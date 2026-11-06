/* fire gpio package: GPIO part in C for the hosted platforms: the character device on Linux (when the kernel headers are there), nothing elsewhere.
 * (A compiler without __has_include, like TinyCC, takes <linux/gpio.h> on Linux as given: the kernel headers come with the C library there.) */
#ifndef FIRE_GPIO_DESKTOP_H
#define FIRE_GPIO_DESKTOP_H

#if defined(__linux__) && !defined(FIRE_NO_GPIO)
#if defined(__has_include)
#if __has_include(<linux/gpio.h>)
#define FIRE_GPIO_KERNEL_HEADER 1
#endif
#else
#define FIRE_GPIO_KERNEL_HEADER 1
#endif
#endif
#if defined(FIRE_GPIO_KERNEL_HEADER)
#include <linux/gpio.h>
#if defined(GPIO_V2_GET_LINE_IOCTL)
#include "fire_gpio_linux.h"
#define FIRE_GPIO_HAVE_BACKEND 1
#endif
#endif
#ifndef FIRE_GPIO_HAVE_BACKEND
#include "fire_gpio_none.h"
#endif

#endif
