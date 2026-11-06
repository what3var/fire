/* fire spi package: SPI part in C on Linux: the spidev interface (/dev/spidevB.C, <linux/spi/spidev.h>) with the SPI_IOC_MESSAGE ioctl. A device is a
 * bus with one chip select (the kernel drives the chip select line and holds it low during a transfer). Permission: the user needs access to the device (group
 * `spi`, or a udev rule). The interface is the one of fire_spi_c.h. */
#ifndef FIRE_SPI_LINUX_H
#define FIRE_SPI_LINUX_H

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/spi/spidev.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include "fire_spi_c.h"

/* A failure from errno `e`: "<what>: <the text of the system>". */
static int fire_spi_failErrno(FireSpiStatus* st, int e, const char* what) {
    int code = FIRE_SPI_E_OTHER;
    if (e == ENOENT || e == ENODEV || e == ENXIO) code = FIRE_SPI_E_NOT_FOUND;
    else if (e == EBUSY) code = FIRE_SPI_E_BUSY;
    else if (e == EACCES || e == EPERM) code = FIRE_SPI_E_PERMISSION;
    else if (e == EINVAL) code = FIRE_SPI_E_INVALID_ARGUMENT;
    else if (e == ETIMEDOUT) code = FIRE_SPI_E_TIMEOUT;
    return fire_spi_fail(st, code, "%s: %s", what, strerror(e));
}

struct FireSpiDevice {
    int fd;
    char* name;
    int speed;
    int mode;
    int lsb;
};

static int fire_spi_supported(void) { return 1; }

static int fire_spi_compareNames(const void* a, const void* b) { return strcmp(*(char* const*)a, *(char* const*)b); }

static int fire_spi_devices(FireSpiList* names, FireSpiStatus* st) {
    DIR* dir;
    struct dirent* entry;
    fire_spi_listFree(names);
    dir = opendir("/dev");
    if (!dir) return fire_spi_success(st);
    while ((entry = readdir(dir)) != NULL)
        if (strncmp(entry->d_name, "spidev", 6) == 0 && entry->d_name[6] >= '0' && entry->d_name[6] <= '9') fire_spi_listAdd(names, entry->d_name);
    closedir(dir);
    if (names->count > 1) qsort(names->items, names->count, sizeof(char*), fire_spi_compareNames);
    return fire_spi_success(st);
}

static int fire_spi_apply(FireSpiDevice* d, FireSpiStatus* st) {
    uint32_t mode = (uint32_t)(d->mode & 3) | (d->lsb ? (uint32_t)SPI_LSB_FIRST : 0u);
    uint8_t bits = 8;
    uint32_t speed = (uint32_t)d->speed;
    if (ioctl(d->fd, SPI_IOC_WR_MODE32, &mode) < 0) {
        uint8_t mode8 = (uint8_t)mode;   /* (a kernel without the 32 bit modes) */
        if (ioctl(d->fd, SPI_IOC_WR_MODE, &mode8) < 0) return fire_spi_failErrno(st, errno, "set the SPI mode");
    }
    if (ioctl(d->fd, SPI_IOC_WR_BITS_PER_WORD, &bits) < 0) return fire_spi_failErrno(st, errno, "set the word size");
    if (ioctl(d->fd, SPI_IOC_WR_MAX_SPEED_HZ, &speed) < 0) return fire_spi_failErrno(st, errno, "set the SPI speed");
    return fire_spi_success(st);
}

static FireSpiDevice* fire_spi_openDevice(const char* name, int mode, int speedHz, int lsbFirst, FireSpiStatus* st) {
    size_t n = strlen(name);
    int valid = n > 6 && strncmp(name, "spidev", 6) == 0 && strchr(name, '/') == NULL && strstr(name, "..") == NULL;
    char* path;
    int fd;
    FireSpiDevice* d;
    if (!valid) { fire_spi_fail(st, FIRE_SPI_E_NOT_FOUND, "There is no SPI device '%s' (the devices are called \"spidev0.0\", ...).", name); return NULL; }
    path = (char*)malloc(n + 6);
    if (path == NULL) { fire_spi_fail(st, FIRE_SPI_E_OTHER, "Out of memory."); return NULL; }
    sprintf(path, "/dev/%s", name);
    fd = open(path, O_RDWR | O_CLOEXEC);
    if (fd < 0) {
        char what[176];
        int e = errno;
        snprintf(what, sizeof what, "open %s", path);
        free(path);
        fire_spi_failErrno(st, e, what);
        return NULL;
    }
    free(path);
    d = (FireSpiDevice*)calloc(1, sizeof(FireSpiDevice));
    if (d != NULL) d->name = (char*)malloc(n + 1);
    if (d == NULL || d->name == NULL) { free(d); close(fd); fire_spi_fail(st, FIRE_SPI_E_OTHER, "Out of memory."); return NULL; }
    d->fd = fd;
    memcpy(d->name, name, n + 1);
    d->mode = mode;
    d->speed = speedHz;
    d->lsb = lsbFirst;
    if (!fire_spi_apply(d, st)) { close(fd); free(d->name); free(d); return NULL; }
    return d;
}

static int fire_spi_configure(FireSpiDevice* d, int mode, int speedHz, int lsbFirst, FireSpiStatus* st) {
    FireSpiDevice copy = *d;
    d->mode = mode;
    d->speed = speedHz;
    d->lsb = lsbFirst;
    if (!fire_spi_apply(d, st)) { d->mode = copy.mode; d->speed = copy.speed; d->lsb = copy.lsb; return 0; }
    return 1;
}

static int fire_spi_transfer(FireSpiDevice* d, const uint8_t* out, uint8_t* in, int n, FireSpiStatus* st) {
    enum { PIECE = 4096 };   /* (the driver's buffer is one page by default: longer transfers are done piece by piece, the chip select stays low only within a piece) */
    static const uint8_t zeros[PIECE] = { 0 };
    uint8_t drop[PIECE];
    int done;
    for (done = 0; done < n; done += PIECE) {
        int piece = n - done < PIECE ? n - done : PIECE;
        struct spi_ioc_transfer t;
        memset(&t, 0, sizeof t);
        t.tx_buf = (unsigned long)(out ? out + done : zeros);
        t.rx_buf = (unsigned long)(in ? in + done : drop);
        t.len = (uint32_t)piece;
        t.speed_hz = (uint32_t)d->speed;
        t.bits_per_word = 8;
        if (ioctl(d->fd, SPI_IOC_MESSAGE(1), &t) < 0) return fire_spi_failErrno(st, errno, "SPI transfer");
    }
    return fire_spi_success(st);
}

static void fire_spi_closeDevice(FireSpiDevice* d) {
    if (!d) return;
    if (d->fd >= 0) close(d->fd);
    free(d->name);
    free(d);
}

#endif
