// fire native platform layer, serial ports for the devices bridge on a platform without any: there are no `serial` devices (the loopback device still works).
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace dev {

inline std::vector<std::string> serialNames() { return {}; }

class SerialPort {
public:
    bool open(const std::string&) { return false; }
    void close() {}
    int64_t read(uint8_t*, size_t) { return -1; }
    bool write(const uint8_t*, size_t) { return false; }
};

}  // namespace dev
}  // namespace plat
}  // namespace fire
