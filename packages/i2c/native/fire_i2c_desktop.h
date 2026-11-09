/* fire i2c package: I2C part in C for the hosted platforms: /dev/i2c-N on Linux (when the kernel headers are there), nothing elsewhere. (A compiler
 * without __has_include, like TinyCC, takes <linux/i2c-dev.h> on Linux as given: the kernel headers come with the C library there.) */
#ifndef FIRE_I2C_DESKTOP_H
#define FIRE_I2C_DESKTOP_H

#if defined(__linux__) && !defined(FIRE_NO_I2C)
#if defined(__has_include)
#if __has_include(<linux/i2c-dev.h>)
#define FIRE_I2C_KERNEL_HEADER 1
#endif
#else
#define FIRE_I2C_KERNEL_HEADER 1
#endif
#endif
#if defined(FIRE_I2C_KERNEL_HEADER)
#include <linux/i2c-dev.h>
#if defined(I2C_RDWR) && defined(I2C_SMBUS)
#include "fire_i2c_linux.h"
#define FIRE_I2C_HAVE_BACKEND 1
#endif
#endif
#ifndef FIRE_I2C_HAVE_BACKEND
#include "fire_i2c_none.h"
#endif

#endif
