/* fire native platform layer, I2C part in C on Linux: the i2c-dev interface (/dev/i2c-N, <linux/i2c-dev.h>) with the I2C_RDWR ioctl (a transfer of one or two
 * messages, the second with a repeated start). Permission: the user needs access to the device (group `i2c`, or a udev rule). The bus speed belongs to the adapter
 * (device tree); `fire_i2c_setSpeed` is accepted and has no effect. The interface is the one of fire_i2c_c.h. */
#ifndef FIRE_I2C_LINUX_H
#define FIRE_I2C_LINUX_H

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/i2c-dev.h>
#include <linux/i2c.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include "fire_i2c_c.h"

/* "0x48" */
static void fire_i2c_hex(char* text, size_t size, int address) { snprintf(text, size, "0x%02X", address & 0x7F); }

/* A failure from errno `e` (`address` -1: none): "<what> (address 0x..): <the text of the system>", or NoAck when nobody answered. */
static int fire_i2c_failErrno(FireI2cStatus* st, int e, const char* what, int address) {
    int code = FIRE_I2C_E_OTHER;
    char hex[8];
    char at[32];
    at[0] = 0;
    hex[0] = 0;
    if (address >= 0) { fire_i2c_hex(hex, sizeof hex, address); snprintf(at, sizeof at, " (address %s)", hex); }
    if (e == ENOENT || e == ENODEV) code = FIRE_I2C_E_NOT_FOUND;
    else if (e == EBUSY) code = FIRE_I2C_E_BUSY;
    else if (e == EACCES || e == EPERM) code = FIRE_I2C_E_PERMISSION;
    else if (e == EINVAL) code = FIRE_I2C_E_INVALID_ARGUMENT;
    else if (e == ENXIO || e == EREMOTEIO) return fire_i2c_fail(st, FIRE_I2C_E_NO_ACK, "No device answered%s%s.", address >= 0 ? " at address " : "", hex);
    else if (e == ETIMEDOUT) code = FIRE_I2C_E_TIMEOUT;
    return fire_i2c_fail(st, code, "%s%s: %s", what, at, strerror(e));
}

struct FireI2cBus {
    int fd;
    char* name;
};

static int fire_i2c_supported(void) { return 1; }

/* (shorter names first: i2c-2 before i2c-10) */
static int fire_i2c_compareNames(const void* a, const void* b) {
    const char* x = *(char* const*)a;
    const char* y = *(char* const*)b;
    size_t nx = strlen(x), ny = strlen(y);
    if (nx != ny) return nx < ny ? -1 : 1;
    return strcmp(x, y);
}

static int fire_i2c_buses(FireI2cList* names, FireI2cStatus* st) {
    DIR* dir;
    struct dirent* entry;
    fire_i2c_listFree(names);
    dir = opendir("/dev");
    if (!dir) return fire_i2c_success(st);   /* (no /dev: no buses) */
    while ((entry = readdir(dir)) != NULL) {
        const char* rest;
        const char* p;
        int digits;
        if (strncmp(entry->d_name, "i2c-", 4) != 0) continue;
        rest = entry->d_name + 4;
        digits = *rest != 0;
        for (p = rest; *p; p++) if (*p < '0' || *p > '9') digits = 0;
        if (digits) fire_i2c_listAdd(names, entry->d_name);
    }
    closedir(dir);
    if (names->count > 1) qsort(names->items, names->count, sizeof(char*), fire_i2c_compareNames);
    return fire_i2c_success(st);
}

static FireI2cBus* fire_i2c_openBus(const char* name, int speedHz, FireI2cStatus* st) {
    size_t n = strlen(name), i;
    int valid = n > 4 && strncmp(name, "i2c-", 4) == 0;
    char* path;
    int fd;
    unsigned long funcs = 0;
    FireI2cBus* b;
    (void)speedHz;
    for (i = 4; valid && i < n; i++) if (name[i] < '0' || name[i] > '9') valid = 0;
    if (!valid) { fire_i2c_fail(st, FIRE_I2C_E_NOT_FOUND, "There is no I2C bus '%s' (the buses are called \"i2c-1\", ...).", name); return NULL; }
    path = (char*)malloc(n + 6);
    if (path == NULL) { fire_i2c_fail(st, FIRE_I2C_E_OTHER, "Out of memory."); return NULL; }
    sprintf(path, "/dev/%s", name);
    fd = open(path, O_RDWR | O_CLOEXEC);
    if (fd < 0) {
        char what[176];
        int e = errno;
        snprintf(what, sizeof what, "open %s", path);
        free(path);
        fire_i2c_failErrno(st, e, what, -1);
        return NULL;
    }
    if (ioctl(fd, I2C_FUNCS, &funcs) < 0 || !(funcs & I2C_FUNC_I2C)) {
        close(fd);
        fire_i2c_fail(st, FIRE_I2C_E_UNSUPPORTED, "%s cannot do plain I2C transfers.", path);
        free(path);
        return NULL;
    }
    free(path);
    b = (FireI2cBus*)calloc(1, sizeof(FireI2cBus));
    if (b != NULL) b->name = (char*)malloc(n + 1);
    if (b == NULL || b->name == NULL) { free(b); close(fd); fire_i2c_fail(st, FIRE_I2C_E_OTHER, "Out of memory."); return NULL; }
    b->fd = fd;
    memcpy(b->name, name, n + 1);
    fire_i2c_success(st);
    return b;
}

static int fire_i2c_setSpeed(FireI2cBus* b, int speedHz, FireI2cStatus* st) { (void)b; (void)speedHz; return fire_i2c_success(st); }

static int fire_i2c_transfer(FireI2cBus* b, struct i2c_msg* messages, int count, int address, FireI2cStatus* st) {
    struct i2c_rdwr_ioctl_data data;
    data.msgs = messages;
    data.nmsgs = (__u32)count;
    if (ioctl(b->fd, I2C_RDWR, &data) < 0) return fire_i2c_failErrno(st, errno, "I2C transfer", address);
    return fire_i2c_success(st);
}

static int fire_i2c_write(FireI2cBus* b, int address, const uint8_t* bytes, int n, FireI2cStatus* st) {
    struct i2c_msg m;
    m.addr = (__u16)address;
    m.flags = 0;
    m.len = (__u16)n;
    m.buf = (__u8*)(uintptr_t)bytes;
    return fire_i2c_transfer(b, &m, 1, address, st);
}

static int fire_i2c_read(FireI2cBus* b, int address, uint8_t* bytes, int n, FireI2cStatus* st) {
    struct i2c_msg m;
    m.addr = (__u16)address;
    m.flags = I2C_M_RD;
    m.len = (__u16)n;
    m.buf = bytes;
    return fire_i2c_transfer(b, &m, 1, address, st);
}

static int fire_i2c_writeRead(FireI2cBus* b, int address, const uint8_t* out, int nout, uint8_t* in, int nin, FireI2cStatus* st) {
    struct i2c_msg m[2];
    m[0].addr = (__u16)address;
    m[0].flags = 0;
    m[0].len = (__u16)nout;
    m[0].buf = (__u8*)(uintptr_t)out;
    m[1].addr = (__u16)address;
    m[1].flags = I2C_M_RD;
    m[1].len = (__u16)nin;
    m[1].buf = in;
    return fire_i2c_transfer(b, m, 2, address, st);
}

/* Like i2cdetect: a quick write asks "is anybody there" without sending data; a one byte read where an adapter cannot do that. A device that a kernel driver owns
 * (EBUSY) is there. */
static int fire_i2c_probe(FireI2cBus* b, int address, int* present, FireI2cStatus* st) {
    struct i2c_smbus_ioctl_data args;
    union i2c_smbus_data data;
    *present = 0;
    if (ioctl(b->fd, I2C_SLAVE, address) < 0) {
        if (errno == EBUSY) { *present = 1; return fire_i2c_success(st); }
        return fire_i2c_failErrno(st, errno, "select address", address);
    }
    args.read_write = I2C_SMBUS_WRITE;
    args.command = 0;
    args.size = I2C_SMBUS_QUICK;
    args.data = NULL;
    if (ioctl(b->fd, I2C_SMBUS, &args) >= 0) { *present = 1; return fire_i2c_success(st); }
    if (errno == ENXIO || errno == EREMOTEIO || errno == EIO) return fire_i2c_success(st);
    args.read_write = I2C_SMBUS_READ;
    args.size = I2C_SMBUS_BYTE;
    args.data = &data;
    if (ioctl(b->fd, I2C_SMBUS, &args) >= 0) { *present = 1; return fire_i2c_success(st); }
    if (errno == ENXIO || errno == EREMOTEIO || errno == EIO) return fire_i2c_success(st);
    return fire_i2c_failErrno(st, errno, "probe", address);
}

static void fire_i2c_closeBus(FireI2cBus* b) {
    if (!b) return;
    if (b->fd >= 0) close(b->fd);
    free(b->name);
    free(b);
}

#endif
