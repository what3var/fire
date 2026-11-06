/* fire spi package: SPI part in C for the hosted platforms: spidev on Linux (when the kernel headers are there), nothing elsewhere. (A compiler without
 * __has_include, like TinyCC, takes <linux/spi/spidev.h> on Linux as given: the kernel headers come with the C library there.) */
#ifndef FIRE_SPI_DESKTOP_H
#define FIRE_SPI_DESKTOP_H

#if defined(__linux__) && !defined(FIRE_NO_SPI)
#if defined(__has_include)
#if __has_include(<linux/spi/spidev.h>)
#define FIRE_SPI_KERNEL_HEADER 1
#endif
#else
#define FIRE_SPI_KERNEL_HEADER 1
#endif
#endif
#if defined(FIRE_SPI_KERNEL_HEADER)
#include <linux/spi/spidev.h>
#if defined(SPI_IOC_MESSAGE)
#define FIRE_SPI_USE_LINUX 1
#define FIRE_SPI_HAVE_BACKEND 1
#endif
#endif
#ifndef FIRE_SPI_HAVE_BACKEND
#define FIRE_SPI_USE_NONE 1
#endif

#endif
