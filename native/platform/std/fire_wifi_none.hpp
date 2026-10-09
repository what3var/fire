// fire native platform layer, WiFi part for a platform without WiFi that the program can control (Windows, macOS, Linux - there the operating system owns the network -, a board without a package of its
// own): the list of interfaces is empty and every call fails with "not supported". The simulated interface `sim` of the bridge works everywhere. A platform package offers `fire_wifi.hpp` (the generated
// file defines FIRE_PLATFORM_WIFI_HEADER for it) that provides, in fire::plat::wifi:
//
//   enum Err                       the codes of the error slot (the same numbers as in the prelude: WiFi.WiFiException.code)
//   struct Status { int code; std::string message; }     code 0 = ok
//   struct Network { std::string ssid, bssid; int rssi; int channel; int auth; }   auth: 0 open, 1 WEP, 2 WPA, 3 WPA2, 4 WPA3, 5 other
//   bool supported()               false: there is no WiFi here (the list of interfaces is empty)
//   Status interfaces(std::vector<std::string>& names)    the WiFi interfaces ("wifi" on the ESP32)
//   Status init()                  starts the WiFi driver (once; later calls do nothing)
//   Status scanBegin() / scanStep(bool& done, std::vector<Network>& found)             a scan takes seconds: begin starts it, step asks (without waiting) and gives the networks when it is done
//   Status connectBegin(ssid, password)                    starts connecting the station (an empty password: an open network)
//   Status connectState(int& state, int& code, std::string& message)                state 0 idle, 1 connecting, 2 connected (the address is there), 3 failed (code: the Err, message: what happened)
//   Status disconnect()
//   Status stationInfo(std::string& ssid, std::string& ip, std::string& mac, int& rssi)   NotConnected when the station has no connection (mac is always given)
//   Status apStart(ssid, password, int channel, int maxClients) / apStop() / apInfo(std::string& ip, std::string& mac, int& clients)   the access point (an empty password: open)
// Nothing waits: the fire code asks again and again and sleeps in between, so the program stays abortable.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace wifi {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7, E_Auth = 8, E_Timeout = 9, E_NotConnected = 10 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

struct Network {
    std::string ssid;
    std::string bssid;
    int rssi = 0;
    int channel = 0;
    int auth = 0;
};

inline bool supported() { return false; }
inline Status none() { return fail(E_Unsupported, "This platform has no WiFi that a program can control (the simulated interface \"sim\" works everywhere)."); }

inline Status interfaces(std::vector<std::string>& names) { names.clear(); return success(); }
inline Status init() { return none(); }
inline Status scanBegin() { return none(); }
inline Status scanStep(bool&, std::vector<Network>&) { return none(); }
inline Status connectBegin(const std::string&, const std::string&) { return none(); }
inline Status connectState(int& state, int&, std::string&) { state = 0; return none(); }
inline Status disconnect() { return none(); }
inline Status stationInfo(std::string&, std::string&, std::string&, int&) { return none(); }
inline Status apStart(const std::string&, const std::string&, int, int) { return none(); }
inline Status apStop() { return none(); }
inline Status apInfo(std::string&, std::string&, int&) { return none(); }

}  // namespace wifi
}  // namespace plat
}  // namespace fire
