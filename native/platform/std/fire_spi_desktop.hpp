// fire native platform layer, SPI part for the hosted platforms: spidev on Linux (when the kernel headers are there), nothing elsewhere.
#pragma once

#if defined(__linux__) && defined(__has_include) && !defined(FIRE_NO_SPI)
#if __has_include(<linux/spi/spidev.h>)
#include <linux/spi/spidev.h>
#if defined(SPI_IOC_MESSAGE)
#include "fire_spi_linux.hpp"
#define FIRE_SPI_HAVE_BACKEND 1
#endif
#endif
#endif
#ifndef FIRE_SPI_HAVE_BACKEND
#include "fire_spi_none.hpp"
#endif
