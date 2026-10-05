// fire native platform layer, serial ports for the devices bridge on Windows: the Win32 communications API, 115200 baud 8N1, reading without waiting.
// (Not tried on a Windows machine yet.)
#pragma once

#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace fire {
namespace plat {
namespace dev {

/// The ports of HKLM\HARDWARE\DEVICEMAP\SERIALCOMM (`COM3`, ...), like SerialPort.GetPortNames.
inline std::vector<std::string> serialNames() {
    std::vector<std::string> names;
    HKEY key;
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, "HARDWARE\\DEVICEMAP\\SERIALCOMM", 0, KEY_READ, &key) != ERROR_SUCCESS) return names;
    for (DWORD i = 0;; i++) {
        char valueName[256], data[256];
        DWORD valueLen = sizeof valueName, dataLen = sizeof data - 1, type = 0;
        if (RegEnumValueA(key, i, valueName, &valueLen, nullptr, &type, reinterpret_cast<LPBYTE>(data), &dataLen) != ERROR_SUCCESS) break;
        if (type == REG_SZ) { data[dataLen] = 0; names.push_back(data); }
    }
    RegCloseKey(key);
    std::sort(names.begin(), names.end());
    return names;
}

class SerialPort {
public:
    ~SerialPort() { close(); }
    bool open(const std::string& name) {
        close();
        std::string path = name.rfind("\\\\.\\", 0) == 0 ? name : "\\\\.\\" + name;   // COM10 and above need the device namespace
        h_ = ::CreateFileA(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (h_ == INVALID_HANDLE_VALUE) return false;
        DCB dcb;
        std::memset(&dcb, 0, sizeof dcb);
        dcb.DCBlength = sizeof dcb;
        if (!::GetCommState(h_, &dcb)) { close(); return false; }
        dcb.BaudRate = CBR_115200;
        dcb.ByteSize = 8;
        dcb.Parity = NOPARITY;
        dcb.StopBits = ONESTOPBIT;
        if (!::SetCommState(h_, &dcb)) { close(); return false; }
        COMMTIMEOUTS timeouts;
        std::memset(&timeouts, 0, sizeof timeouts);
        timeouts.ReadIntervalTimeout = MAXDWORD;   // a read returns at once with what is there
        timeouts.WriteTotalTimeoutConstant = 1000;
        ::SetCommTimeouts(h_, &timeouts);
        return true;
    }
    void close() {
        if (h_ != INVALID_HANDLE_VALUE) { ::CloseHandle(h_); h_ = INVALID_HANDLE_VALUE; }
    }
    int64_t read(uint8_t* buffer, size_t n) {
        if (h_ == INVALID_HANDLE_VALUE) return -1;
        DWORD got = 0;
        if (!::ReadFile(h_, buffer, (DWORD)n, &got, nullptr)) return -1;
        return (int64_t)got;
    }
    bool write(const uint8_t* data, size_t n) {
        if (h_ == INVALID_HANDLE_VALUE) return false;
        DWORD put = 0;
        return ::WriteFile(h_, data, (DWORD)n, &put, nullptr) && put == n;
    }
private:
    HANDLE h_ = INVALID_HANDLE_VALUE;
};

}  // namespace dev
}  // namespace plat
}  // namespace fire
