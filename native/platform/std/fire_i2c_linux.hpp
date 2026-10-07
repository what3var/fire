// fire native platform layer, I2C part on Linux: the i2c-dev interface (/dev/i2c-N, <linux/i2c-dev.h>) with the I2C_RDWR ioctl (a transfer of one or two messages, the second with a repeated start).
// Permission: the user needs access to the device (group `i2c`, or a udev rule). The bus speed belongs to the adapter (device tree); `setSpeed` is accepted and has no effect.
// The interface is the one of fire_i2c_none.hpp.
#pragma once

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/i2c-dev.h>
#include <linux/i2c.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace i2c {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7, E_NoAck = 8, E_Timeout = 9 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

inline std::string hex(int address) {
    char text[8];
    std::snprintf(text, sizeof text, "0x%02X", address & 0x7F);
    return text;
}

inline Status failErrno(int e, const std::string& what, int address = -1) {
    int code = E_Other;
    std::string at = address >= 0 ? " (address " + hex(address) + ")" : "";
    if (e == ENOENT || e == ENODEV) code = E_NotFound;
    else if (e == EBUSY) code = E_Busy;
    else if (e == EACCES || e == EPERM) code = E_Permission;
    else if (e == EINVAL) code = E_InvalidArgument;
    else if (e == ENXIO || e == EREMOTEIO) return fail(E_NoAck, "No device answered" + (address >= 0 ? " at address " + hex(address) : std::string()) + ".");
    else if (e == ETIMEDOUT) code = E_Timeout;
    return fail(code, what + at + ": " + std::strerror(e));
}

struct Bus {
    int fd = -1;
    std::string name;
};

inline bool supported() { return true; }

inline Status buses(std::vector<std::string>& names) {
    names.clear();
    DIR* dir = opendir("/dev");
    if (!dir) return success();   // (no /dev: no buses)
    while (dirent* entry = readdir(dir)) {
        if (std::strncmp(entry->d_name, "i2c-", 4) != 0) continue;
        const char* rest = entry->d_name + 4;
        bool digits = *rest != 0;
        for (const char* p = rest; *p; p++) if (*p < '0' || *p > '9') digits = false;
        if (digits) names.push_back(entry->d_name);
    }
    closedir(dir);
    std::sort(names.begin(), names.end(), [](const std::string& a, const std::string& b) { return a.size() != b.size() ? a.size() < b.size() : a < b; });
    return success();
}

inline Status openBus(const std::string& name, int, Bus*& out) {
    out = nullptr;
    bool valid = name.size() > 4 && name.compare(0, 4, "i2c-") == 0;
    for (size_t i = 4; valid && i < name.size(); i++) if (name[i] < '0' || name[i] > '9') valid = false;
    if (!valid) return fail(E_NotFound, "There is no I2C bus '" + name + "' (the buses are called \"i2c-1\", ...).");
    std::string path = "/dev/" + name;
    int fd = ::open(path.c_str(), O_RDWR | O_CLOEXEC);
    if (fd < 0) return failErrno(errno, "open " + path);
    unsigned long funcs = 0;
    if (ioctl(fd, I2C_FUNCS, &funcs) < 0 || !(funcs & I2C_FUNC_I2C)) {
        ::close(fd);
        return fail(E_Unsupported, path + " cannot do plain I2C transfers.");
    }
    Bus* b = new Bus();
    b->fd = fd;
    b->name = name;
    out = b;
    return success();
}

inline Status setSpeed(Bus*, int) { return success(); }

inline Status transfer(Bus* b, i2c_msg* messages, int count, int address) {
    i2c_rdwr_ioctl_data data;
    data.msgs = messages;
    data.nmsgs = (__u32)count;
    if (ioctl(b->fd, I2C_RDWR, &data) < 0) return failErrno(errno, "I2C transfer", address);
    return success();
}

inline Status write(Bus* b, int address, const uint8_t* bytes, int n) {
    i2c_msg m;
    m.addr = (__u16)address;
    m.flags = 0;
    m.len = (__u16)n;
    m.buf = const_cast<uint8_t*>(bytes);
    return transfer(b, &m, 1, address);
}

inline Status read(Bus* b, int address, uint8_t* bytes, int n) {
    i2c_msg m;
    m.addr = (__u16)address;
    m.flags = I2C_M_RD;
    m.len = (__u16)n;
    m.buf = bytes;
    return transfer(b, &m, 1, address);
}

inline Status writeRead(Bus* b, int address, const uint8_t* out, int nout, uint8_t* in, int nin) {
    i2c_msg m[2];
    m[0].addr = (__u16)address;
    m[0].flags = 0;
    m[0].len = (__u16)nout;
    m[0].buf = const_cast<uint8_t*>(out);
    m[1].addr = (__u16)address;
    m[1].flags = I2C_M_RD;
    m[1].len = (__u16)nin;
    m[1].buf = in;
    return transfer(b, m, 2, address);
}

/// Like i2cdetect: a quick write asks "is anybody there" without sending data; a one byte read where an adapter cannot do that. A device that a kernel driver owns (EBUSY) is there.
inline Status probe(Bus* b, int address, bool& present) {
    present = false;
    if (ioctl(b->fd, I2C_SLAVE, address) < 0) {
        if (errno == EBUSY) { present = true; return success(); }
        return failErrno(errno, "select address", address);
    }
    i2c_smbus_ioctl_data args;
    union i2c_smbus_data data;
    args.read_write = I2C_SMBUS_WRITE;
    args.command = 0;
    args.size = I2C_SMBUS_QUICK;
    args.data = nullptr;
    if (ioctl(b->fd, I2C_SMBUS, &args) >= 0) { present = true; return success(); }
    if (errno == ENXIO || errno == EREMOTEIO || errno == EIO) return success();
    args.read_write = I2C_SMBUS_READ;
    args.size = I2C_SMBUS_BYTE;
    args.data = &data;
    if (ioctl(b->fd, I2C_SMBUS, &args) >= 0) { present = true; return success(); }
    if (errno == ENXIO || errno == EREMOTEIO || errno == EIO) return success();
    return failErrno(errno, "probe", address);
}

inline void closeBus(Bus* b) {
    if (!b) return;
    if (b->fd >= 0) ::close(b->fd);
    delete b;
}

}  // namespace i2c
}  // namespace plat
}  // namespace fire
