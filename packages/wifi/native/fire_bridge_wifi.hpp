// fire native bridge "wifi": the natives behind `#import "wifi"` (docs/NETWORK.md, "WiFi"): scan for networks, join one as a station, be an access point. The fire side (WiFi.Station, WiFi.AccessPoint, WiFi.Board,
// WiFi.Sim - fire source) is the same in the VM and in a native build; this file is what its `__WiFi...` functions do.
//
// This file is the one implementation of the wifi natives: the native build includes it (the package "wifi" brings it as its C++ source) and the virtual machine runs it in a shared library built from it
// (native/abi/fire_pkg_abi.h). The radio comes from the platform package (FIRE_PLATFORM_WIFI_HEADER: plat::wifi, see platform/std/fire_wifi_none.hpp): the WiFi driver of ESP-IDF on an ESP32. Everywhere else the
// operating system owns the network and the list of interfaces is empty - but one interface is always there:
//
//   "sim"   a simulated radio with the networks that the program (or a test) puts in the air: `SimAddNetwork`. A scan finds them, a join succeeds with the right password (and fails with NotFound/Auth
//           otherwise), and the station gets an address. Joining and scanning take a few questions to answer (like the real thing takes seconds), so the code that waits is really tried. For tests, and to
//           develop a program on a PC that later runs on the board. (It is no network: the sockets of the net package use the network of the PC.)
//
// What a program does with the radio is asked in steps, nothing waits: `ScanBegin`/`ScanStep`, `ConnectBegin`/`ConnectState` - the fire code asks again and again and sleeps in between, so the program stays
// abortable. An interface is an integer handle in a table of this bridge. Every function reports an error the same way: a result of -1/false/undefined and the code and message of this thread
// (`__WiFiLastError`, `__WiFiLastErrorMessage`), from which the fire code throws a typed exception.
#pragma once

#include <cstddef>
#include <cstdio>
#include <cstring>
#include FIRE_PLATFORM_WIFI_HEADER
#include <string>
#include <vector>

namespace fire {
namespace wifi {

enum Err { None = 0, InvalidArgument = 1, InvalidHandle = 2, NotFound = 3, Busy = 4, Permission = 5, Unsupported = 6, Other = 7, Auth = 8, Timeout = 9, NotConnected = 10 };
enum { Idle = 0, Connecting = 1, Connected = 2, Failed = 3 };

#if defined(FIRE_TLS_STRUCT)
#define FIRE_WIFI_ERROR g_ioError
#else
struct ErrorSlot { int32_t code; char message[176]; };
inline thread_local ErrorSlot t_wifiError = {0, {0}};
#define FIRE_WIFI_ERROR ::fire::wifi::t_wifiError
#endif

inline int64_t fail(int code, const std::string& message) {
    FIRE_WIFI_ERROR.code = code;
    size_t n = message.size() < sizeof FIRE_WIFI_ERROR.message - 1 ? message.size() : sizeof FIRE_WIFI_ERROR.message - 1;
    std::memcpy(FIRE_WIFI_ERROR.message, message.data(), n);
    FIRE_WIFI_ERROR.message[n] = 0;
    return -1;
}
inline void ok() { FIRE_WIFI_ERROR.code = 0; FIRE_WIFI_ERROR.message[0] = 0; }
inline int64_t fail(const plat::wifi::Status& s) { return fail(s.code, s.message); }

// ---- text -------------------------------------------------------------------------------------------------------------------------------
inline std::string toUtf8(Value v) {
    const Str* s = strOf(v);
    std::string out;
    out.reserve(s->length);
    for (uint32_t i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out.push_back((char)c);
        else if (c < 0x800) { out.push_back((char)(0xC0 | (c >> 6))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else if (c < 0x10000) { out.push_back((char)(0xE0 | (c >> 12))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else { out.push_back((char)(0xF0 | (c >> 18))); out.push_back((char)(0x80 | ((c >> 12) & 0x3F))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
    }
    return out;
}
inline Value str8(const std::string& text, OwnList* list) {
    Str* s = allocStr((uint32_t)text.size(), list);
    for (size_t i = 0; i < text.size(); i++) strChars(s)[i] = (char16_t)(uint8_t)text[i];
    return StrV(s);
}

// ---- the simulated radio ----------------------------------------------------------------------------------------------------------------
struct SimNetwork {
    std::string ssid;
    std::string password;
    int rssi = -60;
    int channel = 1;
};
struct Sim {
    std::vector<SimNetwork> networks;
    int connectPolls = 2;        // how many questions a join takes to answer
    int scanPolls = 1;           // ... and a scan
    // station
    int state = Idle;
    int failCode = 0;
    std::string failMessage;
    std::string target, targetPassword;
    int pollsLeft = 0;
    std::string ssid;
    int rssi = 0;
    // scan
    bool scanning = false;
    int scanLeft = 0;
    // access point
    bool apRunning = false;
    std::string apSsid, apPassword;
    int apChannel = 1, apMax = 4, apClients = 0;

    const SimNetwork* find(const std::string& name) const {
        for (const SimNetwork& n : networks) if (n.ssid == name) return &n;
        return nullptr;
    }
};
inline Sim& sim() { static Sim s; return s; }
inline const char* SimStationIp() { return "192.168.1.50"; }
inline const char* SimStationMac() { return "02:00:00:00:00:01"; }
inline const char* SimApIp() { return "192.168.4.1"; }
inline const char* SimApMac() { return "02:00:00:00:00:02"; }

// ---- the interfaces ---------------------------------------------------------------------------------------------------------------------
struct Iface {
    bool isSim = false;
    std::string name;
    std::vector<plat::wifi::Network> found;    // the networks of the last finished scan
};
inline void release(Iface* i) {
    if (!i) return;
    delete i;
}
struct IfaceTable {
    std::vector<Iface*> items{1, nullptr};
    ~IfaceTable() { for (Iface* i : items) release(i); }
};
inline std::vector<Iface*>& ifaces() { static IfaceTable table; return table.items; }

/// The program has ended (the library for the VM stays loaded for the next one): drops the handles, resets the simulated radio and (on a board) lets go of the connection and the access point.
inline void reset() {
    std::vector<Iface*>& all = ifaces();
    bool hardware = false;
    for (size_t i = 1; i < all.size(); i++) {
        if (all[i] && !all[i]->isSim) hardware = true;
        release(all[i]);
        all[i] = nullptr;
    }
    if (hardware) { plat::wifi::disconnect(); plat::wifi::apStop(); }
    sim() = Sim();
}

inline Iface* find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < ifaces().size() && ifaces()[(size_t)i]) return ifaces()[(size_t)i];
    fail(InvalidHandle, "Invalid or already closed WiFi handle.");
    return nullptr;
}

// ---- natives ----------------------------------------------------------------------------------------------------------------------------
inline Value LastError() { return Int(FIRE_WIFI_ERROR.code); }
inline Value LastErrorMessage(OwnList* list) { return str8(FIRE_WIFI_ERROR.message, list); }
inline Value OpenCount() {
    int64_t n = 0;
    for (Iface* i : ifaces()) if (i) n++;
    return Int(n);
}
/// 1 if this machine has a WiFi radio that a program can control (the simulated one is there anyway), else 0.
inline Value Supported() {
    std::vector<std::string> hardware;
    ok();
    return Int(plat::wifi::supported() && plat::wifi::interfaces(hardware).ok() && !hardware.empty() ? 1 : 0);
}

/// The interfaces: "sim" first, then those of the hardware.
inline Value Interfaces(OwnList* list) {
    std::vector<std::string> names{"sim"};
    std::vector<std::string> hardware;
    plat::wifi::Status st = plat::wifi::interfaces(hardware);
    if (!st.ok()) { fail(st); return Undef(); }
    for (const std::string& n : hardware) names.push_back(n);
    ok();
    Arr* a = allocArr((uint32_t)names.size(), list);
    for (size_t i = 0; i < names.size(); i++) { a->items()[i] = str8(names[i], list); retain(a->items()[i]); }
    return ArrV(a);
}

inline Value Open(Value name) {
    std::string n = toUtf8(name);
    Iface* i = new Iface();
    i->name = n;
    if (n == "sim") i->isSim = true;
    else {
        std::vector<std::string> hardware;
        plat::wifi::Status st = plat::wifi::interfaces(hardware);
        bool known = false;
        for (const std::string& h : hardware) if (h == n) known = true;
        if (!st.ok() || !known) { delete i; return Int(st.ok() ? fail(NotFound, "There is no WiFi interface '" + n + "'.") : fail(st)); }
        st = plat::wifi::init();
        if (!st.ok()) { delete i; return Int(fail(st)); }
    }
    ifaces().push_back(i);
    ok();
    return Int((int64_t)ifaces().size() - 1);
}

inline Value Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= ifaces().size() || !ifaces()[(size_t)i]) { fail(InvalidHandle, "Invalid or already closed WiFi handle."); return Bool(false); }
    release(ifaces()[(size_t)i]);
    ifaces()[(size_t)i] = nullptr;
    ok();
    return Bool(true);
}

// ---- scanning ---------------------------------------------------------------------------------------------------------------------------
/// Starts a scan; false on an error (Busy: the station is in the middle of joining).
inline Value ScanBegin(Value h) {
    Iface* i = find(h);
    if (!i) return Bool(false);
    if (i->isSim) {
        if (sim().state == Connecting) { fail(Busy, "The station is joining a network."); return Bool(false); }
        sim().scanning = true;
        sim().scanLeft = sim().scanPolls;
        i->found.clear();
    } else {
        plat::wifi::Status st = plat::wifi::scanBegin();
        if (!st.ok()) { fail(st); return Bool(false); }
    }
    ok();
    return Bool(true);
}

/// Asks without waiting: 1 the scan is done (`ScanCount` and the fields have the networks), 0 not yet, -1 on an error.
inline Value ScanStep(Value h) {
    Iface* i = find(h);
    if (!i) return Int(-1);
    if (i->isSim) {
        Sim& s = sim();
        if (!s.scanning) return Int(fail(InvalidArgument, "No scan is going on."));
        if (s.scanLeft > 0) { s.scanLeft--; ok(); return Int(0); }
        s.scanning = false;
        i->found.clear();
        int n = 0;
        for (const SimNetwork& net : s.networks) {
            plat::wifi::Network f;
            f.ssid = net.ssid;
            char mac[24];
            std::snprintf(mac, sizeof mac, "02:00:00:00:10:%02x", (unsigned)(n++ & 0xFF));
            f.bssid = mac;
            f.rssi = net.rssi;
            f.channel = net.channel;
            f.auth = net.password.empty() ? 0 : 3;
            i->found.push_back(f);
        }
        ok();
        return Int(1);
    }
    bool done = false;
    plat::wifi::Status st = plat::wifi::scanStep(done, i->found);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(done ? 1 : 0);
}
inline Value ScanCount(Value h) {
    Iface* i = find(h);
    if (!i) return Int(-1);
    ok();
    return Int((int64_t)i->found.size());
}
inline const plat::wifi::Network* scanItem(Value h, Value index) {
    Iface* i = find(h);
    if (!i) return nullptr;
    if (index.i < 0 || (size_t)index.i >= i->found.size()) { fail(InvalidArgument, "No such network in the scan."); return nullptr; }
    return &i->found[(size_t)index.i];
}
inline Value ScanSsid(Value h, Value index, OwnList* list) {
    const plat::wifi::Network* n = scanItem(h, index);
    if (!n) return Undef();
    ok();
    return str8(n->ssid, list);
}
inline Value ScanBssid(Value h, Value index, OwnList* list) {
    const plat::wifi::Network* n = scanItem(h, index);
    if (!n) return Undef();
    ok();
    return str8(n->bssid, list);
}
/// A number of a found network: 0 the signal strength (dBm), 1 the channel, 2 the security (0 open, 1 WEP, 2 WPA, 3 WPA2, 4 WPA3, 5 other); -1000 on an error.
inline Value ScanInfo(Value h, Value index, Value which) {
    const plat::wifi::Network* n = scanItem(h, index);
    if (!n) return Int(-1000);
    ok();
    switch (which.i) {
        case 0: return Int(n->rssi);
        case 1: return Int(n->channel);
        case 2: return Int(n->auth);
        default: fail(InvalidArgument, "No such information."); return Int(-1000);
    }
}

// ---- the station ------------------------------------------------------------------------------------------------------------------------
/// Starts joining a network (an empty password: an open network); false on an error.
inline Value ConnectBegin(Value h, Value ssid, Value password) {
    Iface* i = find(h);
    if (!i) return Bool(false);
    std::string s = toUtf8(ssid), p = toUtf8(password);
    if (i->isSim) {
        Sim& m = sim();
        if (s.empty() || s.size() > 32) { fail(InvalidArgument, "The network name must be 1 to 32 characters."); return Bool(false); }
        m.state = Connecting;
        m.target = s;
        m.targetPassword = p;
        m.pollsLeft = m.connectPolls;
        m.failCode = 0;
        m.failMessage.clear();
        m.ssid.clear();
    } else {
        plat::wifi::Status st = plat::wifi::connectBegin(s, p);
        if (!st.ok()) { fail(st); return Bool(false); }
    }
    ok();
    return Bool(true);
}

/// Asks without waiting: 0 idle (not connected, not trying), 1 still joining, 2 connected, -1 failed (the error slot says why: NotFound, Auth, ...).
inline Value ConnectState(Value h) {
    Iface* i = find(h);
    if (!i) return Int(-1);
    if (i->isSim) {
        Sim& m = sim();
        if (m.state == Connecting) {
            if (m.pollsLeft > 0) { m.pollsLeft--; ok(); return Int(Connecting); }
            const SimNetwork* net = m.find(m.target);
            if (!net) { m.state = Failed; m.failCode = NotFound; m.failMessage = "The network '" + m.target + "' was not found."; }
            else if (net->password != m.targetPassword) { m.state = Failed; m.failCode = Auth; m.failMessage = "The network refused the login (wrong password?)."; }
            else { m.state = Connected; m.ssid = net->ssid; m.rssi = net->rssi; }
        }
        if (m.state == Failed) return Int(fail(m.failCode, m.failMessage));   // (stays so until the next join or Disconnect)
        ok();
        return Int(m.state);
    }
    int state = 0, code = 0;
    std::string message;
    plat::wifi::Status st = plat::wifi::connectState(state, code, message);
    if (!st.ok()) return Int(fail(st));
    if (state == Failed) return Int(fail(code ? code : Other, message.empty() ? "Could not connect." : message));
    ok();
    return Int(state);
}

inline Value Disconnect(Value h) {
    Iface* i = find(h);
    if (!i) return Bool(false);
    if (i->isSim) { sim().state = Idle; sim().ssid.clear(); sim().failCode = 0; }
    else {
        plat::wifi::Status st = plat::wifi::disconnect();
        if (!st.ok()) { fail(st); return Bool(false); }
    }
    ok();
    return Bool(true);
}

/// A text of the station: 0 the name of the network it is joined to ("" if none), 1 its IP address ("" if none), 2 its own MAC address; undefined on an error.
inline Value StationText(Value h, Value which, OwnList* list) {
    Iface* i = find(h);
    if (!i) return Undef();
    std::string ssid, ip, mac;
    int rssi = 0;
    if (i->isSim) {
        Sim& m = sim();
        mac = SimStationMac();
        if (m.state == Connected) { ssid = m.ssid; ip = SimStationIp(); }
    } else {
        plat::wifi::Status st = plat::wifi::stationInfo(ssid, ip, mac, rssi);
        if (!st.ok() && st.code != plat::wifi::E_NotConnected) { fail(st); return Undef(); }
    }
    ok();
    switch (which.i) {
        case 0: return str8(ssid, list);
        case 1: return str8(ip, list);
        case 2: return str8(mac, list);
        default: fail(InvalidArgument, "No such information."); return Undef();
    }
}
/// The signal strength of the joined network in dBm (0 when not connected); -1000 on an error.
inline Value StationRssi(Value h) {
    Iface* i = find(h);
    if (!i) return Int(-1000);
    if (i->isSim) { ok(); return Int(sim().state == Connected ? sim().rssi : 0); }
    std::string ssid, ip, mac;
    int rssi = 0;
    plat::wifi::Status st = plat::wifi::stationInfo(ssid, ip, mac, rssi);
    if (!st.ok() && st.code != plat::wifi::E_NotConnected) { fail(st); return Int(-1000); }
    ok();
    return Int(rssi);
}

// ---- the access point -------------------------------------------------------------------------------------------------------------------
inline Value ApStart(Value h, Value ssid, Value password, Value channel, Value maxClients) {
    Iface* i = find(h);
    if (!i) return Bool(false);
    std::string s = toUtf8(ssid), p = toUtf8(password);
    if (i->isSim) {
        Sim& m = sim();
        if (s.empty() || s.size() > 32) { fail(InvalidArgument, "The network name must be 1 to 32 characters."); return Bool(false); }
        if (!p.empty() && (p.size() < 8 || p.size() > 63)) { fail(InvalidArgument, "The password must be 8 to 63 characters (or empty for an open network)."); return Bool(false); }
        if (channel.i < 1 || channel.i > 13) { fail(InvalidArgument, "The channel must be 1 to 13."); return Bool(false); }
        if (maxClients.i < 1 || maxClients.i > 10) { fail(InvalidArgument, "The number of clients must be 1 to 10."); return Bool(false); }
        m.apRunning = true;
        m.apSsid = s;
        m.apPassword = p;
        m.apChannel = (int)channel.i;
        m.apMax = (int)maxClients.i;
        m.apClients = 0;
    } else {
        plat::wifi::Status st = plat::wifi::apStart(s, p, (int)channel.i, (int)maxClients.i);
        if (!st.ok()) { fail(st); return Bool(false); }
    }
    ok();
    return Bool(true);
}
inline Value ApStop(Value h) {
    Iface* i = find(h);
    if (!i) return Bool(false);
    if (i->isSim) { sim().apRunning = false; sim().apClients = 0; }
    else {
        plat::wifi::Status st = plat::wifi::apStop();
        if (!st.ok()) { fail(st); return Bool(false); }
    }
    ok();
    return Bool(true);
}
/// 1 if the access point is running, else 0; -1 on an error.
inline Value ApRunning(Value h) {
    Iface* i = find(h);
    if (!i) return Int(-1);
    if (i->isSim) { ok(); return Int(sim().apRunning ? 1 : 0); }
    std::string ip, mac;
    int clients = 0;
    plat::wifi::Status st = plat::wifi::apInfo(ip, mac, clients);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(!ip.empty() ? 1 : 0);
}
/// A text of the access point: 0 its IP address, 1 its MAC address; undefined on an error.
inline Value ApText(Value h, Value which, OwnList* list) {
    Iface* i = find(h);
    if (!i) return Undef();
    std::string ip, mac;
    int clients = 0;
    if (i->isSim) { ip = SimApIp(); mac = SimApMac(); }
    else {
        plat::wifi::Status st = plat::wifi::apInfo(ip, mac, clients);
        if (!st.ok()) { fail(st); return Undef(); }
    }
    ok();
    switch (which.i) {
        case 0: return str8(ip, list);
        case 1: return str8(mac, list);
        default: fail(InvalidArgument, "No such information."); return Undef();
    }
}
/// How many stations are joined to the access point; -1 on an error.
inline Value ApClients(Value h) {
    Iface* i = find(h);
    if (!i) return Int(-1);
    if (i->isSim) { ok(); return Int(sim().apClients); }
    std::string ip, mac;
    int clients = 0;
    plat::wifi::Status st = plat::wifi::apInfo(ip, mac, clients);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(clients);
}

// ---- the simulated radio from the outside -----------------------------------------------------------------------------------------------
/// Puts a network in the air (an empty password: an open network); one with the same name is replaced.
inline Value SimAddNetwork(Value ssid, Value password, Value rssi, Value channel) {
    std::string s = toUtf8(ssid);
    if (s.empty() || s.size() > 32) { fail(InvalidArgument, "The network name must be 1 to 32 characters."); return Bool(false); }
    SimNetwork n;
    n.ssid = s;
    n.password = toUtf8(password);
    n.rssi = (int)rssi.i;
    n.channel = (int)channel.i;
    for (SimNetwork& e : sim().networks) if (e.ssid == s) { e = n; ok(); return Bool(true); }
    sim().networks.push_back(n);
    ok();
    return Bool(true);
}
inline Value SimRemoveNetwork(Value ssid) {
    std::string s = toUtf8(ssid);
    std::vector<SimNetwork>& all = sim().networks;
    for (size_t i = 0; i < all.size(); i++) if (all[i].ssid == s) { all.erase(all.begin() + (long)i); break; }
    if (sim().state == Connected && sim().ssid == s) { sim().state = Idle; sim().ssid.clear(); }   // (the network went away: the connection is lost)
    ok();
    return Bool(true);
}
/// The connection of the station is lost (the router went away).
inline Value SimDrop() {
    if (sim().state == Connected) { sim().state = Idle; sim().ssid.clear(); }
    ok();
    return Bool(true);
}
/// How many questions a join and a scan take to answer (default 2 and 1; 0: the first question already has the answer).
inline Value SimDelays(Value connectPolls, Value scanPolls) {
    if (connectPolls.i < 0 || scanPolls.i < 0 || connectPolls.i > 100000 || scanPolls.i > 100000) { fail(InvalidArgument, "Invalid number of questions."); return Bool(false); }
    sim().connectPolls = (int)connectPolls.i;
    sim().scanPolls = (int)scanPolls.i;
    ok();
    return Bool(true);
}
/// A station joins or leaves the simulated access point (up to the maximum); the new number of stations, -1 if it cannot be done.
inline Value SimApClients(Value delta) {
    Sim& m = sim();
    if (!m.apRunning) return Int(fail(NotConnected, "The access point is not running."));
    int n = m.apClients + (int)delta.i;
    if (n < 0 || n > m.apMax) return Int(fail(InvalidArgument, "The number of stations must stay between 0 and the maximum."));
    m.apClients = n;
    ok();
    return Int(n);
}
/// Removes all networks and takes the radio back to the start (open interfaces stay open).
inline Value SimReset() {
    sim() = Sim();
    ok();
    return Bool(true);
}

}  // namespace wifi
}  // namespace fire
