// fire native platform layer, GPIO part on Linux: the GPIO character device (/dev/gpiochipN) with the version 2 ioctl interface (kernel 5.10 and newer; <linux/gpio.h>).
// Every pin gets a request of its own (a file descriptor), made when the pin is configured, so pins can be used and given back one by one. Edge events are read from that descriptor
// without waiting. Permission: the user needs access to the device (group `gpio`, or a udev rule). The interface is the one of fire_gpio_none.hpp.
#pragma once

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/gpio.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include <algorithm>
#include <cstring>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace gpio {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7 };

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
    return fail(code, what + ": " + std::strerror(e));
}

struct Line {
    std::string chip;
    int number = 0;
    int chipFd = -1;
    int fd = -1;        // the request of the pin (-1: not configured yet)
    int direction = 0;
};

inline bool supported() { return true; }

inline Status chips(std::vector<std::string>& names) {
    names.clear();
    DIR* dir = opendir("/dev");
    if (!dir) return success();
    while (dirent* entry = readdir(dir)) {
        if (std::strncmp(entry->d_name, "gpiochip", 8) == 0) names.push_back(entry->d_name);
    }
    closedir(dir);
    std::sort(names.begin(), names.end());
    return success();
}

inline Status openLine(const std::string& chip, int line, Line*& out) {
    out = nullptr;
    if (chip.empty() || chip.find('/') != std::string::npos || chip.find("gpiochip") != 0) return fail(E_NotFound, "There is no GPIO chip '" + chip + "'.");
    int chipFd = ::open(("/dev/" + chip).c_str(), O_RDWR | O_CLOEXEC);
    if (chipFd < 0) return failErrno(errno, "open /dev/" + chip);
    gpiochip_info info;
    std::memset(&info, 0, sizeof info);
    if (::ioctl(chipFd, GPIO_GET_CHIPINFO_IOCTL, &info) != 0) { int e = errno; ::close(chipFd); return failErrno(e, "chip info of " + chip); }
    if (line < 0 || (unsigned)line >= info.lines) { ::close(chipFd); return fail(E_NotFound, chip + " has " + std::to_string(info.lines) + " lines: " + std::to_string(line) + " does not exist."); }
    Line* l = new Line();
    l->chip = chip;
    l->number = line;
    l->chipFd = chipFd;
    out = l;
    return success();
}

inline void releaseRequest(Line* l) {
    if (l->fd >= 0) { ::close(l->fd); l->fd = -1; }
}

inline Status configure(Line* l, int direction, int pull, int edge, int value) {
    if (direction < 0 || direction > 1 || pull < 0 || pull > 2 || edge < 0 || edge > 3) return fail(E_InvalidArgument, "Invalid pin setup.");
    if (direction == 1 && edge != 0) return fail(E_InvalidArgument, "Edges can only be watched on an input.");
    releaseRequest(l);   // (the old request goes first: a line cannot be requested twice)
    gpio_v2_line_request req;
    std::memset(&req, 0, sizeof req);
    req.offsets[0] = (__u32)l->number;
    req.num_lines = 1;
    std::strncpy(req.consumer, "fire", sizeof req.consumer - 1);
    __u64 flags = direction == 1 ? GPIO_V2_LINE_FLAG_OUTPUT : GPIO_V2_LINE_FLAG_INPUT;
    if (pull == 1) flags |= GPIO_V2_LINE_FLAG_BIAS_PULL_UP;
    else if (pull == 2) flags |= GPIO_V2_LINE_FLAG_BIAS_PULL_DOWN;
    else flags |= GPIO_V2_LINE_FLAG_BIAS_DISABLED;
    if (edge & 1) flags |= GPIO_V2_LINE_FLAG_EDGE_RISING;
    if (edge & 2) flags |= GPIO_V2_LINE_FLAG_EDGE_FALLING;
    req.config.flags = flags;
    if (direction == 1) {
        req.config.num_attrs = 1;
        req.config.attrs[0].attr.id = GPIO_V2_LINE_ATTR_ID_OUTPUT_VALUES;
        req.config.attrs[0].attr.values = value ? 1 : 0;
        req.config.attrs[0].mask = 1;
    }
    if (::ioctl(l->chipFd, GPIO_V2_GET_LINE_IOCTL, &req) != 0) return failErrno(errno, "request line " + std::to_string(l->number) + " of " + l->chip);
    l->fd = req.fd;
    l->direction = direction;
    int flagsFd = ::fcntl(l->fd, F_GETFL, 0);
    if (flagsFd >= 0) ::fcntl(l->fd, F_SETFL, flagsFd | O_NONBLOCK);   // the events are read without waiting
    return success();
}

inline Status read(Line* l, int& value) {
    value = 0;
    if (l->fd < 0) return fail(E_InvalidArgument, "The pin is not set up yet (call Input or Output first).");
    gpio_v2_line_values v;
    v.bits = 0;
    v.mask = 1;
    if (::ioctl(l->fd, GPIO_V2_LINE_GET_VALUES_IOCTL, &v) != 0) return failErrno(errno, "read line " + std::to_string(l->number));
    value = (v.bits & 1) ? 1 : 0;
    return success();
}

inline Status write(Line* l, int value) {
    if (l->fd < 0) return fail(E_InvalidArgument, "The pin is not set up yet (call Input or Output first).");
    if (l->direction != 1) return fail(E_InvalidArgument, "The pin is an input.");
    gpio_v2_line_values v;
    v.bits = value ? 1 : 0;
    v.mask = 1;
    if (::ioctl(l->fd, GPIO_V2_LINE_SET_VALUES_IOCTL, &v) != 0) return failErrno(errno, "write line " + std::to_string(l->number));
    return success();
}

inline Status pollEdge(Line* l, int& event, int64_t& micros) {
    event = 0;
    micros = 0;
    if (l->fd < 0) return fail(E_InvalidArgument, "The pin is not set up yet (call Input or Output first).");
    gpio_v2_line_event ev;
    ssize_t n = ::read(l->fd, &ev, sizeof ev);
    if (n < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) return success();
        return failErrno(errno, "edge of line " + std::to_string(l->number));
    }
    if (n != (ssize_t)sizeof ev) return success();
    event = ev.id == GPIO_V2_LINE_EVENT_RISING_EDGE ? 1 : 2;
    micros = (int64_t)(ev.timestamp_ns / 1000);
    return success();
}

inline void closeLine(Line* l) {
    if (!l) return;
    releaseRequest(l);
    if (l->chipFd >= 0) ::close(l->chipFd);
    delete l;
}

}  // namespace gpio
}  // namespace plat
}  // namespace fire
