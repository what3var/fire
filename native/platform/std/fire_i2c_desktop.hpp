// fire native platform layer, I2C part for the hosted platforms: /dev/i2c-N on Linux (when the kernel headers are there), nothing elsewhere.
#pragma once

#if defined(__linux__) && defined(__has_include) && !defined(FIRE_NO_I2C)
#if __has_include(<linux/i2c-dev.h>)
#include <linux/i2c-dev.h>
#if defined(I2C_RDWR) && defined(I2C_SMBUS)
#include "fire_i2c_linux.hpp"
#define FIRE_I2C_HAVE_BACKEND 1
#endif
#endif
#endif
#ifndef FIRE_I2C_HAVE_BACKEND
#include "fire_i2c_none.hpp"
#endif
