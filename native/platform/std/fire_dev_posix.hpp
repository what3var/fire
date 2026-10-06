// fire native platform layer, serial ports for the devices bridge on POSIX (Linux, macOS): termios, 115200 baud 8N1, reading without blocking.
//
// A platform package offers `fire_dev.hpp` that provides, in fire::plat::dev:
//   std::vector<std::string> serialNames()   the serial ports of the machine (like SerialPort.GetPortNames)
//   class SerialPort { bool open(name); void close(); int64_t read(uint8_t*, size_t) /* >0 bytes, 0 nothing yet, -1 port gone */; bool write(const uint8_t*, size_t); }
#pragma once

#include <algorithm>
#include <cerrno>
#include <cstdint>
#include <cstring>
#include <dirent.h>
#include <fcntl.h>
#include <poll.h>
#include <string>
#include <termios.h>
#include <unistd.h>
#include <vector>

namespace fire {
namespace plat {
namespace dev {

inline std::vector<std::string> serialNames() {
    std::vector<std::string> names;
    DIR* d = ::opendir("/dev");
    if (!d) return names;
    while (struct dirent* e = ::readdir(d)) {
        std::string n = e->d_name;
#ifdef __APPLE__
        bool match = n.rfind("tty.", 0) == 0 || n.rfind("cu.", 0) == 0;
#else
        bool match = n.rfind("ttyS", 0) == 0 || n.rfind("ttyUSB", 0) == 0 || n.rfind("ttyACM", 0) == 0 || n.rfind("ttyAMA", 0) == 0 || n.rfind("rfcomm", 0) == 0;
#endif
        if (match) names.push_back("/dev/" + n);
    }
    ::closedir(d);
    std::sort(names.begin(), names.end());
    return names;
}

class SerialPort {
public:
    ~SerialPort() { close(); }
    bool open(const std::string& name) {
        close();
        fd_ = ::open(name.c_str(), O_RDWR | O_NOCTTY | O_NONBLOCK);
        if (fd_ < 0) return false;
        struct termios tio;
        if (::tcgetattr(fd_, &tio) != 0) { close(); return false; }
        ::cfmakeraw(&tio);
        ::cfsetispeed(&tio, B115200);
        ::cfsetospeed(&tio, B115200);
        tio.c_cflag |= (CLOCAL | CREAD);
        tio.c_cflag &= ~(PARENB | CSTOPB | CSIZE);
        tio.c_cflag |= CS8;
        tio.c_cc[VMIN] = 0;
        tio.c_cc[VTIME] = 0;
        if (::tcsetattr(fd_, TCSANOW, &tio) != 0) { close(); return false; }
        return true;
    }
    void close() {
        if (fd_ >= 0) { ::close(fd_); fd_ = -1; }
    }
    int64_t read(uint8_t* buffer, size_t n) {
        if (fd_ < 0) return -1;
        ssize_t r = ::read(fd_, buffer, n);
        if (r > 0) return r;
        if (r == 0 || errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) return 0;
        return -1;
    }
    bool write(const uint8_t* data, size_t n) {
        if (fd_ < 0) return false;
        size_t done = 0;
        while (done < n) {
            ssize_t w = ::write(fd_, data + done, n - done);
            if (w > 0) { done += (size_t)w; continue; }
            if (w < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)) {
                struct pollfd p = {fd_, POLLOUT, 0};
                ::poll(&p, 1, 100);
                continue;
            }
            return false;
        }
        return true;
    }
private:
    int fd_ = -1;
};

}  // namespace dev
}  // namespace plat
}  // namespace fire
