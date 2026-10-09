// fire native platform layer, GPIO part for the hosted platforms: the character device on Linux (when the kernel headers are there), nothing elsewhere.
#pragma once

#if defined(__linux__) && defined(__has_include) && !defined(FIRE_NO_GPIO)
#if __has_include(<linux/gpio.h>)
#include <linux/gpio.h>
#if defined(GPIO_V2_GET_LINE_IOCTL)
#include "fire_gpio_linux.hpp"
#define FIRE_GPIO_HAVE_BACKEND 1
#endif
#endif
#endif
#ifndef FIRE_GPIO_HAVE_BACKEND
#include "fire_gpio_none.hpp"
#endif
