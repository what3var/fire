// fire native platform layer, SPI part on Linux: the spidev interface (/dev/spidevB.C, <linux/spi/spidev.h>) with the SPI_IOC_MESSAGE ioctl. A device is a bus with one chip select (the kernel drives the
// chip select line and holds it low during a transfer). Permission: the user needs access to the device (group `spi`, or a udev rule). The interface is the one of fire_spi_none.hpp.
#pragma once

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/spi/spidev.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace spi {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7, E_Timeout = 8 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

inline Status failErrno(int e, const std::string& what) {
    int code = E_Other;
    if (e == ENOENT || e == ENODEV || e == ENXIO) code = E_NotFound;
    else if (e == EBUSY) code = E_Busy;
    else if (e == EACCES || e == EPERM) code = E_Permission;
    else if (e == EINVAL) code = E_InvalidArgument;
    else if (e == ETIMEDOUT) code = E_Timeout;
    return fail(code, what + ": " + std::strerror(e));
}

struct Device {
    int fd = -1;
    std::string name;
    int speed = 1000000;
    int mode = 0;
    bool lsb = false;
};

inline bool supported() { return true; }

inline Status devices(std::vector<std::string>& names) {
    names.clear();
    DIR* dir = opendir("/dev");
    if (!dir) return success();
    while (dirent* entry = readdir(dir))
        if (std::strncmp(entry->d_name, "spidev", 6) == 0 && entry->d_name[6] >= '0' && entry->d_name[6] <= '9') names.push_back(entry->d_name);
    closedir(dir);
    std::sort(names.begin(), names.end());
    return success();
}

inline Status apply(Device* d) {
    uint32_t mode = (uint32_t)(d->mode & 3) | (d->lsb ? (uint32_t)SPI_LSB_FIRST : 0u);
    uint8_t bits = 8;
    uint32_t speed = (uint32_t)d->speed;
    if (ioctl(d->fd, SPI_IOC_WR_MODE32, &mode) < 0) {
        uint8_t mode8 = (uint8_t)mode;   // (a kernel without the 32 bit modes)
        if (ioctl(d->fd, SPI_IOC_WR_MODE, &mode8) < 0) return failErrno(errno, "set the SPI mode");
    }
    if (ioctl(d->fd, SPI_IOC_WR_BITS_PER_WORD, &bits) < 0) return failErrno(errno, "set the word size");
    if (ioctl(d->fd, SPI_IOC_WR_MAX_SPEED_HZ, &speed) < 0) return failErrno(errno, "set the SPI speed");
    return success();
}

inline Status openDevice(const std::string& name, int mode, int speedHz, bool lsbFirst, Device*& out) {
    out = nullptr;
    bool valid = name.size() > 6 && name.compare(0, 6, "spidev") == 0 && name.find('/') == std::string::npos && name.find("..") == std::string::npos;
    if (!valid) return fail(E_NotFound, "There is no SPI device '" + name + "' (the devices are called \"spidev0.0\", ...).");
    std::string path = "/dev/" + name;
    int fd = ::open(path.c_str(), O_RDWR | O_CLOEXEC);
    if (fd < 0) return failErrno(errno, "open " + path);
    Device* d = new Device();
    d->fd = fd;
    d->name = name;
    d->mode = mode;
    d->speed = speedHz;
    d->lsb = lsbFirst;
    Status st = apply(d);
    if (!st.ok()) { ::close(fd); delete d; return st; }
    out = d;
    return success();
}

inline Status configure(Device* d, int mode, int speedHz, bool lsbFirst) {
    Device copy = *d;
    d->mode = mode;
    d->speed = speedHz;
    d->lsb = lsbFirst;
    Status st = apply(d);
    if (!st.ok()) { d->mode = copy.mode; d->speed = copy.speed; d->lsb = copy.lsb; }
    return st;
}

inline Status transfer(Device* d, const uint8_t* out, uint8_t* in, int n) {
    static const int kPiece = 4096;   // (the driver's buffer is one page by default: longer transfers are done piece by piece, the chip select stays low only within a piece)
    std::vector<uint8_t> zeros;
    if (!out) zeros.assign((size_t)std::min(n, kPiece), 0);
    std::vector<uint8_t> drop;
    if (!in) drop.resize((size_t)std::min(n, kPiece));
    for (int done = 0; done < n; done += kPiece) {
        int piece = std::min(kPiece, n - done);
        spi_ioc_transfer t;
        std::memset(&t, 0, sizeof t);
        t.tx_buf = (unsigned long)(out ? out + done : zeros.data());
        t.rx_buf = (unsigned long)(in ? in + done : drop.data());
        t.len = (uint32_t)piece;
        t.speed_hz = (uint32_t)d->speed;
        t.bits_per_word = 8;
        if (ioctl(d->fd, SPI_IOC_MESSAGE(1), &t) < 0) return failErrno(errno, "SPI transfer");
    }
    return success();
}

inline void closeDevice(Device* d) {
    if (!d) return;
    if (d->fd >= 0) ::close(d->fd);
    delete d;
}

}  // namespace spi
}  // namespace plat
}  // namespace fire
