/* fire devices package: serial ports for the devices bridge on POSIX (Linux, macOS): termios, 115200 baud 8N1, reading without blocking. The interface is
 * described in fire_dev_c.h. */
#ifndef FIRE_DEV_POSIX_H
#define FIRE_DEV_POSIX_H

#include "fire_dev_c.h"
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <termios.h>
#include <unistd.h>

typedef struct FireDevSerialPort { int fd; } FireDevSerialPort;

static void fire_dev_serialNames(FireDevNames* names) {
    DIR* d = opendir("/dev");
    struct dirent* e;
    if (!d) return;
    while ((e = readdir(d)) != NULL) {
        const char* n = e->d_name;
        char path[300];
#ifdef __APPLE__
        int match = strncmp(n, "tty.", 4) == 0 || strncmp(n, "cu.", 3) == 0;
#else
        int match = strncmp(n, "ttyS", 4) == 0 || strncmp(n, "ttyUSB", 6) == 0 || strncmp(n, "ttyACM", 6) == 0 || strncmp(n, "ttyAMA", 6) == 0 || strncmp(n, "rfcomm", 6) == 0;
#endif
        if (match) { snprintf(path, sizeof path, "/dev/%s", n); fire_dev_namesAdd(names, path); }
    }
    closedir(d);
    fire_dev_namesSort(names);
}

static void fire_dev_serialInit(FireDevSerialPort* p) { p->fd = -1; }
static void fire_dev_serialClose(FireDevSerialPort* p) {
    if (p->fd >= 0) { close(p->fd); p->fd = -1; }
}
static int fire_dev_serialOpen(FireDevSerialPort* p, const char* name) {
    struct termios tio;
    fire_dev_serialClose(p);
    p->fd = open(name, O_RDWR | O_NOCTTY | O_NONBLOCK);
    if (p->fd < 0) return 0;
    if (tcgetattr(p->fd, &tio) != 0) { fire_dev_serialClose(p); return 0; }
    /* raw: what cfmakeraw does (it is not POSIX, so the generated file with _POSIX_C_SOURCE does not have it) */
    tio.c_iflag &= ~(tcflag_t)(IGNBRK | BRKINT | PARMRK | ISTRIP | INLCR | IGNCR | ICRNL | IXON);
    tio.c_oflag &= ~(tcflag_t)OPOST;
    tio.c_lflag &= ~(tcflag_t)(ECHO | ECHONL | ICANON | ISIG | IEXTEN);
    tio.c_cflag &= ~(tcflag_t)(CSIZE | PARENB);
    tio.c_cflag |= CS8;
    cfsetispeed(&tio, B115200);
    cfsetospeed(&tio, B115200);
    tio.c_cflag |= (CLOCAL | CREAD);
    tio.c_cflag &= ~(tcflag_t)(PARENB | CSTOPB | CSIZE);
    tio.c_cflag |= CS8;
    tio.c_cc[VMIN] = 0;
    tio.c_cc[VTIME] = 0;
    if (tcsetattr(p->fd, TCSANOW, &tio) != 0) { fire_dev_serialClose(p); return 0; }
    return 1;
}
static int64_t fire_dev_serialRead(FireDevSerialPort* p, uint8_t* buffer, size_t n) {
    ssize_t r;
    if (p->fd < 0) return -1;
    r = read(p->fd, buffer, n);
    if (r > 0) return r;
    if (r == 0 || errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) return 0;
    return -1;
}
static int fire_dev_serialWrite(FireDevSerialPort* p, const uint8_t* data, size_t n) {
    size_t done = 0;
    if (p->fd < 0) return 0;
    while (done < n) {
        ssize_t w = write(p->fd, data + done, n - done);
        if (w > 0) { done += (size_t)w; continue; }
        if (w < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)) {
            struct pollfd pf;
            pf.fd = p->fd;
            pf.events = POLLOUT;
            pf.revents = 0;
            poll(&pf, 1, 100);
            continue;
        }
        return 0;
    }
    return 1;
}

#endif
