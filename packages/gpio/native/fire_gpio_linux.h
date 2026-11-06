/* fire gpio package: GPIO part in C on Linux: the GPIO character device (/dev/gpiochipN) with the version 2 ioctl interface (kernel 5.10 and newer;
 * <linux/gpio.h>). Every pin gets a request of its own (a file descriptor), made when the pin is configured, so pins can be used and given back one by one. Edge
 * events are read from that descriptor without waiting. Permission: the user needs access to the device (group `gpio`, or a udev rule). The interface is the one
 * of fire_gpio_c.h. */
#ifndef FIRE_GPIO_LINUX_H
#define FIRE_GPIO_LINUX_H
#if !defined(FIRE_GPIO_DESKTOP_H) || defined(FIRE_GPIO_USE_LINUX)   /* (with fire_gpio_desktop.h in the build: when it chose this one) */

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/gpio.h>
#include <sys/ioctl.h>
#include <time.h>
#include <unistd.h>

#include "fire_gpio_c.h"

/* A failure from errno `e`: "<what>: <the text of the system>" (`what` as printf format). */
static int fire_gpio_failErrno(FireGpioStatus* st, int e, const char* format, ...) {
    char what[176];
    va_list args;
    int code = FIRE_GPIO_E_OTHER;
    va_start(args, format);
    vsnprintf(what, sizeof what, format, args);
    va_end(args);
    if (e == ENOENT || e == ENODEV || e == ENXIO) code = FIRE_GPIO_E_NOT_FOUND;
    else if (e == EBUSY) code = FIRE_GPIO_E_BUSY;
    else if (e == EACCES || e == EPERM) code = FIRE_GPIO_E_PERMISSION;
    else if (e == EINVAL) code = FIRE_GPIO_E_INVALID_ARGUMENT;
    return fire_gpio_fail(st, code, "%s: %s", what, strerror(e));
}

struct FireGpioLine {
    char* chip;
    int number;
    int chipFd;
    int fd;        /* the request of the pin (-1: not configured yet) */
    int direction;
};

static int fire_gpio_supported(void) { return 1; }

/* (the same clock as the times of the edges the kernel gives) */
static int64_t fire_gpio_nowMicros(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (int64_t)t.tv_sec * 1000000 + t.tv_nsec / 1000;
}

static int fire_gpio_compareNames(const void* a, const void* b) { return strcmp(*(char* const*)a, *(char* const*)b); }

static int fire_gpio_chips(FireGpioList* names, FireGpioStatus* st) {
    DIR* dir;
    struct dirent* entry;
    fire_gpio_listFree(names);
    dir = opendir("/dev");
    if (!dir) return fire_gpio_success(st);
    while ((entry = readdir(dir)) != NULL) {
        if (strncmp(entry->d_name, "gpiochip", 8) == 0) fire_gpio_listAdd(names, entry->d_name);
    }
    closedir(dir);
    if (names->count > 1) qsort(names->items, names->count, sizeof(char*), fire_gpio_compareNames);
    return fire_gpio_success(st);
}

static FireGpioLine* fire_gpio_openLine(const char* chip, int line, FireGpioStatus* st) {
    char* path;
    int chipFd;
    struct gpiochip_info info;
    FireGpioLine* l;
    if (chip[0] == 0 || strchr(chip, '/') != NULL || strncmp(chip, "gpiochip", 8) != 0) {
        fire_gpio_fail(st, FIRE_GPIO_E_NOT_FOUND, "There is no GPIO chip '%s'.", chip);
        return NULL;
    }
    path = (char*)malloc(strlen(chip) + 6);
    if (path == NULL) { fire_gpio_fail(st, FIRE_GPIO_E_OTHER, "Out of memory."); return NULL; }
    sprintf(path, "/dev/%s", chip);
    chipFd = open(path, O_RDWR | O_CLOEXEC);
    if (chipFd < 0) { int e = errno; free(path); fire_gpio_failErrno(st, e, "open /dev/%s", chip); return NULL; }
    free(path);
    memset(&info, 0, sizeof info);
    if (ioctl(chipFd, GPIO_GET_CHIPINFO_IOCTL, &info) != 0) {
        int e = errno;
        close(chipFd);
        fire_gpio_failErrno(st, e, "chip info of %s", chip);
        return NULL;
    }
    if (line < 0 || (unsigned)line >= info.lines) {
        close(chipFd);
        fire_gpio_fail(st, FIRE_GPIO_E_NOT_FOUND, "%s has %u lines: %d does not exist.", chip, (unsigned)info.lines, line);
        return NULL;
    }
    l = (FireGpioLine*)calloc(1, sizeof(FireGpioLine));
    if (l != NULL) l->chip = (char*)malloc(strlen(chip) + 1);
    if (l == NULL || l->chip == NULL) { free(l); close(chipFd); fire_gpio_fail(st, FIRE_GPIO_E_OTHER, "Out of memory."); return NULL; }
    strcpy(l->chip, chip);
    l->number = line;
    l->chipFd = chipFd;
    l->fd = -1;
    fire_gpio_success(st);
    return l;
}

static void fire_gpio_releaseRequest(FireGpioLine* l) {
    if (l->fd >= 0) { close(l->fd); l->fd = -1; }
}

static int fire_gpio_configure(FireGpioLine* l, int direction, int pull, int edge, int value, FireGpioStatus* st) {
    struct gpio_v2_line_request req;
    __u64 flags;
    int flagsFd;
    if (direction < 0 || direction > 1 || pull < 0 || pull > 2 || edge < 0 || edge > 3) return fire_gpio_fail(st, FIRE_GPIO_E_INVALID_ARGUMENT, "Invalid pin setup.");
    if (direction == 1 && edge != 0) return fire_gpio_fail(st, FIRE_GPIO_E_INVALID_ARGUMENT, "Edges can only be watched on an input.");
    fire_gpio_releaseRequest(l);   /* (the old request goes first: a line cannot be requested twice) */
    memset(&req, 0, sizeof req);
    req.offsets[0] = (__u32)l->number;
    req.num_lines = 1;
    strncpy(req.consumer, "fire", sizeof req.consumer - 1);
    flags = direction == 1 ? GPIO_V2_LINE_FLAG_OUTPUT : GPIO_V2_LINE_FLAG_INPUT;
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
    if (ioctl(l->chipFd, GPIO_V2_GET_LINE_IOCTL, &req) != 0) return fire_gpio_failErrno(st, errno, "request line %d of %s", l->number, l->chip);
    l->fd = req.fd;
    l->direction = direction;
    flagsFd = fcntl(l->fd, F_GETFL, 0);
    if (flagsFd >= 0) fcntl(l->fd, F_SETFL, flagsFd | O_NONBLOCK);   /* the events are read without waiting */
    return fire_gpio_success(st);
}

static int fire_gpio_read(FireGpioLine* l, int* value, FireGpioStatus* st) {
    struct gpio_v2_line_values v;
    *value = 0;
    if (l->fd < 0) return fire_gpio_fail(st, FIRE_GPIO_E_INVALID_ARGUMENT, "The pin is not set up yet (call Input or Output first).");
    v.bits = 0;
    v.mask = 1;
    if (ioctl(l->fd, GPIO_V2_LINE_GET_VALUES_IOCTL, &v) != 0) return fire_gpio_failErrno(st, errno, "read line %d", l->number);
    *value = (v.bits & 1) ? 1 : 0;
    return fire_gpio_success(st);
}

static int fire_gpio_write(FireGpioLine* l, int value, FireGpioStatus* st) {
    struct gpio_v2_line_values v;
    if (l->fd < 0) return fire_gpio_fail(st, FIRE_GPIO_E_INVALID_ARGUMENT, "The pin is not set up yet (call Input or Output first).");
    if (l->direction != 1) return fire_gpio_fail(st, FIRE_GPIO_E_INVALID_ARGUMENT, "The pin is an input.");
    v.bits = value ? 1 : 0;
    v.mask = 1;
    if (ioctl(l->fd, GPIO_V2_LINE_SET_VALUES_IOCTL, &v) != 0) return fire_gpio_failErrno(st, errno, "write line %d", l->number);
    return fire_gpio_success(st);
}

static int fire_gpio_pollEdge(FireGpioLine* l, int* event, int64_t* micros, FireGpioStatus* st) {
    struct gpio_v2_line_event ev;
    ssize_t n;
    *event = 0;
    *micros = 0;
    if (l->fd < 0) return fire_gpio_fail(st, FIRE_GPIO_E_INVALID_ARGUMENT, "The pin is not set up yet (call Input or Output first).");
    n = read(l->fd, &ev, sizeof ev);
    if (n < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) return fire_gpio_success(st);
        return fire_gpio_failErrno(st, errno, "edge of line %d", l->number);
    }
    if (n != (ssize_t)sizeof ev) return fire_gpio_success(st);
    *event = ev.id == GPIO_V2_LINE_EVENT_RISING_EDGE ? 1 : 2;
    *micros = (int64_t)(ev.timestamp_ns / 1000);
    return fire_gpio_success(st);
}

static void fire_gpio_closeLine(FireGpioLine* l) {
    if (!l) return;
    fire_gpio_releaseRequest(l);
    if (l->chipFd >= 0) close(l->chipFd);
    free(l->chip);
    free(l);
}

#endif
#endif
