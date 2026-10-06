// fire native bridge "devices": the natives behind `#import "devices"` (SPEC 8.16) - the device manager, devices (serial ports, the loopback device), the receive
// buffer of a device and the waiting functions. The fire side (Device, DeviceManagerFacade - fire source, src/fire/Standard/DevicesPrelude.cs) calls the `__DEV...` functions
// this file implements.
//
// This file is the one implementation: the native build includes it (the package "devices" brings it as its C++ source), and the virtual machine runs it in a shared library built
// from it (native/abi/fire_pkg_abi.h). What differs is where the devices come from - the *drivers*:
//   - a native build: FIRE_DEVICES, a string with the drivers in the order they are registered: "serial" (the default: the serial ports of the platform), "loopback" (a simulated
//     device that sends back what it gets, identifier `loopback:echo`), e.g. -DFIRE_DEVICES="\"loopback,serial\""  (target configuration: defines);
//     FIRE_DEFAULT_DEVICE is the identifier of the default device (`Device.Default`); none by default. The serial ports come from the platform package
//     (FIRE_PLATFORM_DEV_HEADER: plat::dev::serialNames() and plat::dev::SerialPort, 115200 baud 8N1); a board without one defines nothing and has no `serial` devices.
//   - the library for the VM: the driver "host" - the devices are those of the host (the device manager of the editor with its drivers, sharing and packet trace; the callbacks
//     `dev_*` of `fire_host`), the identifiers are the host's. The receive buffer, the handles and the matching of `WaitFor` stay in this file.
//
// Differences between the two: a native build has no background threads - received data is collected when the program asks (HasData, Read..., the waiting functions, Connect), not
// while it does something else (the receive buffer of the operating system or of the UART driver holds it meanwhile); the device list is made when the program first uses a device
// function (and again at every Refresh). In the VM the waiting functions (`WaitFor`, `WaitForString`) are run by the VM itself (they have to be abortable and know the program's
// `#timeout`): it calls `WaitStep` until it succeeds.
#pragma once

#include <cstring>
#include FIRE_PLATFORM_DEV_HEADER
#include <string>
#include <vector>

#ifndef FIRE_DEVICES
#ifdef FIRE_LIBRARY
#define FIRE_DEVICES "host"
#else
#define FIRE_DEVICES "serial"
#endif
#endif
#ifndef FIRE_DEFAULT_DEVICE
#define FIRE_DEFAULT_DEVICE ""
#endif

namespace fire {
namespace dev {

enum Avail { Unavailable = 0, Unchecked = 1, Available = 2 };
using Bytes = std::vector<uint8_t>;

// ---- devices ------------------------------------------------------------------------------------------------------------------------------
class Device {
public:
    virtual ~Device() {}
    virtual int availability() const = 0;
    virtual int testAvailability() = 0;
    virtual bool connected() const = 0;
    virtual std::string portName() const = 0;
    virtual bool connect() = 0;
    virtual void disconnect() = 0;
    virtual bool write(const uint8_t* data, size_t n) = 0;
    /// The bytes of a command line: the text and a line ending, in the encoding of the device.
    virtual Bytes encodeCommand(const std::u16string& text) const = 0;
    /// Sends a command line. (The device of the host encodes it itself.)
    virtual bool sendCommand(const std::u16string& text) { Bytes bytes = encodeCommand(text); return write(bytes.data(), bytes.size()); }
    /// Does the device belong to a manager that stays after the program (the editor's)?
    virtual bool shared() const { return false; }
    /// Collects what arrived since the last call, one packet per delivery.
    virtual void poll(std::vector<Bytes>& packets) = 0;
};

inline int64_t nowMs() { return plat::nowMs(); }

/// Sends back what it gets, 5 ms later and in order - like the loopback device of the VM, whose echo comes from another thread.
class Loopback : public Device {
public:
    explicit Loopback(std::string name) : name_(std::move(name)) {}
    int availability() const override { return Available; }
    int testAvailability() override { return Available; }
    bool connected() const override { return connected_; }
    std::string portName() const override { return name_; }
    bool connect() override { connected_ = true; return true; }
    void disconnect() override { connected_ = false; }
    bool write(const uint8_t* data, size_t n) override {
        if (!connected_) return false;
        int64_t due = nowMs() + 5;
        if (!queue_.empty() && queue_.back().due + 5 > due) due = queue_.back().due + 5;
        queue_.push_back({Bytes(data, data + n), due});
        return true;
    }
    Bytes encodeCommand(const std::u16string& text) const override {
        Bytes out;
        for (size_t i = 0; i < text.size(); i++) {   // UTF-8
            uint32_t c = text[i];
            if (c >= 0xD800 && c <= 0xDBFF && i + 1 < text.size() && text[i + 1] >= 0xDC00 && text[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (text[i + 1] - 0xDC00); i++; }
            else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
            if (c < 0x80) out.push_back((uint8_t)c);
            else if (c < 0x800) { out.push_back((uint8_t)(0xC0 | (c >> 6))); out.push_back((uint8_t)(0x80 | (c & 0x3F))); }
            else if (c < 0x10000) { out.push_back((uint8_t)(0xE0 | (c >> 12))); out.push_back((uint8_t)(0x80 | ((c >> 6) & 0x3F))); out.push_back((uint8_t)(0x80 | (c & 0x3F))); }
            else { out.push_back((uint8_t)(0xF0 | (c >> 18))); out.push_back((uint8_t)(0x80 | ((c >> 12) & 0x3F))); out.push_back((uint8_t)(0x80 | ((c >> 6) & 0x3F))); out.push_back((uint8_t)(0x80 | (c & 0x3F))); }
        }
        out.push_back('\n');
        return out;
    }
    void poll(std::vector<Bytes>& packets) override {
        int64_t now = nowMs();
        while (!queue_.empty() && queue_.front().due <= now) {
            if (connected_) packets.push_back(std::move(queue_.front().bytes));
            queue_.erase(queue_.begin());
        }
    }
private:
    struct Echo { Bytes bytes; int64_t due; };
    std::string name_;
    bool connected_ = false;
    std::vector<Echo> queue_;
};

/// A serial port of the platform (115200 baud, 8N1).
class Serial : public Device {
public:
    explicit Serial(std::string name) : name_(std::move(name)) {}
    ~Serial() override { disconnect(); }
    int availability() const override { return availability_; }
    int testAvailability() override {
        if (connected_) return availability_ = Available;   // a connected device holds its port: a second open would fail
        plat::dev::SerialPort probe;
        availability_ = probe.open(name_) ? Available : Unavailable;
        probe.close();
        return availability_;
    }
    bool connected() const override { return connected_; }
    std::string portName() const override { return name_; }
    bool connect() override {
        disconnect();
        if (!port_.open(name_)) return false;
        connected_ = true;
        availability_ = Available;
        return true;
    }
    void disconnect() override {
        port_.close();
        connected_ = false;
    }
    bool write(const uint8_t* data, size_t n) override { return connected_ && port_.write(data, n); }
    Bytes encodeCommand(const std::u16string& text) const override {
        Bytes out;
        for (char16_t c : text) out.push_back(c < 0x80 ? (uint8_t)c : (uint8_t)'?');   // the default encoding of a serial port is ASCII
        out.push_back('\n');
        return out;
    }
    void poll(std::vector<Bytes>& packets) override {
        if (!connected_) return;
        uint8_t buffer[512];
        while (true) {
            int64_t n = port_.read(buffer, sizeof buffer);
            if (n < 0) { disconnect(); return; }   // the port is gone (unplugged): the connection is lost
            if (n == 0) return;
            packets.push_back(Bytes(buffer, buffer + n));
            if (n < (int64_t)sizeof buffer) return;
        }
    }
private:
    std::string name_;
    plat::dev::SerialPort port_;
    bool connected_ = false;
    int availability_ = Unchecked;
};

#ifdef FIRE_LIBRARY
// ---- the devices of the host (the library for the VM) -----------------------------------------------------------------------------------------------
inline const fire_host* devHost() {
    const fire_host* host = libraryHost();
    return host && host->size >= (int32_t)sizeof(fire_host) && host->dev_count ? host : nullptr;
}
inline std::string hostText(int32_t (*get)(int32_t, char*, int32_t), int32_t handle) {
    char buffer[512];
    int32_t n = get(handle, buffer, (int32_t)sizeof buffer - 1);
    return n > 0 ? std::string(buffer, (size_t)(n < (int32_t)sizeof buffer ? n : (int32_t)sizeof buffer - 1)) : std::string();
}
inline std::string utf8Of(const std::u16string& text) {
    std::string out;
    for (size_t i = 0; i < text.size(); i++) {
        uint32_t c = text[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < text.size() && text[i + 1] >= 0xDC00 && text[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (text[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out.push_back((char)c);
        else if (c < 0x800) { out.push_back((char)(0xC0 | (c >> 6))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else if (c < 0x10000) { out.push_back((char)(0xE0 | (c >> 12))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else { out.push_back((char)(0xF0 | (c >> 18))); out.push_back((char)(0x80 | ((c >> 12) & 0x3F))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
    }
    return out;
}

/// A device of the host: everything is forwarded to the host's device manager.
class HostDevice : public Device {
public:
    explicit HostDevice(int32_t handle) : handle_(handle) {}
    int availability() const override { return devHost()->dev_availability(handle_); }
    int testAvailability() override { return devHost()->dev_test_availability(handle_); }
    bool connected() const override { return devHost()->dev_connected(handle_) != 0; }
    std::string portName() const override { return hostText(devHost()->dev_port_name, handle_); }
    bool connect() override { return devHost()->dev_connect(handle_) != 0; }
    void disconnect() override { devHost()->dev_disconnect(handle_); }
    bool write(const uint8_t* data, size_t n) override { return devHost()->dev_write(handle_, data, (int32_t)n) != 0; }
    Bytes encodeCommand(const std::u16string&) const override { return Bytes(); }   // (the host encodes: see sendCommand)
    bool sendCommand(const std::u16string& text) override { return devHost()->dev_send_command(handle_, utf8Of(text).c_str()) != 0; }
    bool shared() const override { return devHost()->dev_shared(handle_) != 0; }
    void poll(std::vector<Bytes>& packets) override {
        Bytes buffer(4096);
        while (true) {
            int32_t n = devHost()->dev_poll(handle_, buffer.data(), (int32_t)buffer.size());
            if (n < 0) return;
            if (n > (int32_t)buffer.size()) { buffer.resize((size_t)n); continue; }   // the packet is bigger: it was kept, ask again with room
            packets.push_back(Bytes(buffer.begin(), buffer.begin() + n));
        }
    }
private:
    int32_t handle_;
};
#endif

// ---- the receive buffer: packets, a consumed head --------------------------------------------------------------------------------------------
struct ReceiveBuffer {
    std::vector<Bytes> packets;
    size_t head = 0;   // bytes of the first packet that are used up

    bool hasData() const { return !packets.empty(); }
    /// The next packet (what is left of the first one).
    Bytes take() {
        if (packets.empty()) return Bytes();
        Bytes first = std::move(packets.front());
        packets.erase(packets.begin());
        Bytes result = head == 0 ? std::move(first) : Bytes(first.begin() + head, first.end());
        head = 0;
        return result;
    }
    /// Looks for `pattern` across the packets; found: everything up to and including it is used up. An empty pattern is always found.
    bool consumeThrough(const Bytes& pattern) {
        if (pattern.empty()) return true;
        if (packets.empty()) return false;
        Bytes all;
        for (size_t i = 0; i < packets.size(); i++) all.insert(all.end(), packets[i].begin() + (i == 0 ? head : 0), packets[i].end());
        size_t index = 0;
        bool found = false;
        for (; index + pattern.size() <= all.size() && !found; index++)
            if (std::memcmp(all.data() + index, pattern.data(), pattern.size()) == 0) { found = true; break; }
        if (!found) return false;
        size_t consumed = index + pattern.size();
        while (!packets.empty()) {
            size_t available = packets.front().size() - head;
            if (consumed >= available) { consumed -= available; packets.erase(packets.begin()); head = 0; }
            else { head += consumed; break; }
        }
        return true;
    }
};

// ---- the manager ------------------------------------------------------------------------------------------------------------------------------
struct Slot {
    int handle = 0;
    std::string identifier, driver;
    Device* device = nullptr;
    ReceiveBuffer buffer;
};
struct DriverSlot {
    int handle = 0;
    std::string id;
    bool verbatim = false;   // the identifiers of the devices are the names as they are (the host's), not `driver:name`
    std::vector<std::string> (*names)();
    Device* (*make)(const std::string&);
};

inline std::vector<std::string> loopbackNames() { return {"echo"}; }
inline Device* makeLoopback(const std::string& name) { return new Loopback(name); }
inline std::vector<std::string> serialNames() { return plat::dev::serialNames(); }
inline Device* makeSerial(const std::string& name) { return new Serial(name); }
#ifdef FIRE_LIBRARY
inline std::vector<std::string> hostNames() {
    std::vector<std::string> names;
    const fire_host* host = devHost();
    if (!host) return names;
    for (int32_t i = 0, n = host->dev_count(); i < n; i++) {
        int32_t handle = host->dev_handle_at(i);
        if (handle >= 0) names.push_back(hostText(host->dev_identifier, handle));
    }
    return names;
}
inline Device* makeHost(const std::string& name) {
    const fire_host* host = devHost();
    for (int32_t i = 0, n = host->dev_count(); i < n; i++) {
        int32_t handle = host->dev_handle_at(i);
        if (handle >= 0 && hostText(host->dev_identifier, handle) == name) return new HostDevice(handle);
    }
    return nullptr;
}
#endif

struct Manager {
    int counter = 0;
    bool ready = false;
    std::vector<DriverSlot> drivers;
    std::vector<Slot*> slots;
    ~Manager() { for (Slot* s : slots) { delete s->device; delete s; } }
};
inline Manager& manager() { static Manager m; return m; }

inline Slot* slotByHandle(int64_t handle) {
    for (Slot* s : manager().slots) if (s->handle == handle) return s;
    return nullptr;
}

inline void registerDriver(const std::string& id) {
    Manager& m = manager();
    for (const DriverSlot& d : m.drivers) if (d.id == id) return;
    DriverSlot slot;
    slot.id = id;
    slot.handle = m.counter++;
    if (id == "loopback") { slot.names = loopbackNames; slot.make = makeLoopback; }
#ifdef FIRE_LIBRARY
    else if (id == "host") { slot.names = hostNames; slot.make = makeHost; slot.verbatim = true; }
#else
    else if (id == "serial") { slot.names = serialNames; slot.make = makeSerial; }
#endif
    else return;
    m.drivers.push_back(slot);
}

/// RefreshDevices: new devices get handles, known ones stay; without `fast` every device is tested (also those that are gone).
inline void refresh(bool fast) {
    Manager& m = manager();
    for (const DriverSlot& driver : m.drivers) {
        std::vector<Slot*> toTest;
        for (const std::string& name : driver.names()) {
            std::string identifier = driver.verbatim ? name : driver.id + ":" + name;
            Slot* existing = nullptr;
            for (Slot* s : m.slots) if (s->identifier == identifier) { existing = s; break; }
            if (!existing) {
                Device* made = driver.make(name);
                if (!made) continue;
                existing = new Slot();
                existing->handle = m.counter++;
                existing->identifier = identifier;
                existing->driver = driver.id;
                existing->device = made;
                m.slots.push_back(existing);
            }
            toTest.push_back(existing);
        }
        if (fast) continue;
        for (Slot* s : m.slots) {
            bool listed = false;
            for (Slot* t : toTest) if (t == s) listed = true;
            if (s->driver == driver.id && !listed) toTest.push_back(s);
        }
        for (Slot* s : toTest) s->device->testAvailability();
    }
}

/// The drivers of FIRE_DEVICES (in this order) and the first device list.
inline void init() {
    Manager& m = manager();
    if (m.ready) return;
    m.ready = true;
    std::string list = FIRE_DEVICES;
    size_t i = 0;
    while (i <= list.size()) {
        size_t next = list.find(',', i);
        if (next == std::string::npos) next = list.size();
        std::string id = list.substr(i, next - i);
        while (!id.empty() && id.front() == ' ') id.erase(id.begin());
        while (!id.empty() && id.back() == ' ') id.pop_back();
        if (!id.empty()) registerDriver(id);
        i = next + 1;
    }
    refresh(true);
}

/// The slot of a handle, with what has arrived since collected into its receive buffer.
inline Slot* resolve(Value handle) {
    init();
    Slot* s = slotByHandle(handle.i);
    if (!s) return nullptr;
    std::vector<Bytes> arrived;
    s->device->poll(arrived);
    for (Bytes& b : arrived) if (!b.empty()) s->buffer.packets.push_back(std::move(b));
    return s;
}

#ifndef FIRE_LIBRARY
/// Waits until `pred` is true or the time is up; false then (also when `terminate` cuts the wait short: the main program keeps serving its queue).
template <class P> inline bool waitUntil(P pred, int64_t ticks) {
    int64_t ms = (ticks + TICKS_PER_MS - 1) / TICKS_PER_MS;
    if (ms < 0) ms = 0;
    int64_t deadline = nowMs() + ms;
#ifdef FIRE_THREADS
    return blockUntil(pred, true, deadline == 0 ? 1 : deadline);
#else
    while (true) {
        if (pred()) return true;
        if (nowMs() >= deadline) return false;
        plat::sleepMs(2);
    }
#endif
}
#endif

// ---- strings and buffers -------------------------------------------------------------------------------------------------------------------------
inline Value ascii(const std::string& text, OwnList* list) {
    Str* s = allocStr((uint32_t)text.size(), list);
    widenAscii(text.data(), (uint32_t)text.size(), strChars(s));
    return StrV(s);
}
/// One character per byte (Latin1): every byte value 0..255 maps to exactly one character.
inline Value latin1(const Bytes& bytes, OwnList* list) {
    Str* s = allocStr((uint32_t)bytes.size(), list);
    char16_t* out = strChars(s);
    for (size_t i = 0; i < bytes.size(); i++) out[i] = bytes[i];
    return StrV(s);
}
inline Bytes toLatin1(Value text) {
    const Str* s = strOf(text);
    Bytes out(s->length);
    for (uint32_t i = 0; i < s->length; i++) out[i] = s->data[i] < 256 ? (uint8_t)s->data[i] : (uint8_t)'?';
    return out;
}

// ---- the natives (called from the generated code as `dev::Name`: the part after `__DEV`) -------------------------------------------------
inline Value MgrRefresh(Value fast) {
    init();
#ifdef FIRE_LIBRARY
    if (const fire_host* host = devHost()) host->dev_refresh((int32_t)fast.i);   // the host looks at its drivers first
#endif
    refresh(fast.i != 0);
    return Undef();
}
inline Value MgrHandleForIdentifier(Value identifier) {
    init();
    std::string id;
    const Str* s = strOf(identifier);
    for (uint32_t i = 0; i < s->length; i++) id.push_back(s->data[i] < 128 ? (char)s->data[i] : '?');
    for (Slot* slot : manager().slots) if (slot->identifier == id) return Int(slot->handle);
    return Int(-1);
}
inline Value MgrCount() { init(); return Int((int64_t)manager().slots.size()); }
inline Value MgrHandleAt(Value index) {
    init();
    return Int(index.i >= 0 && index.i < (int64_t)manager().slots.size() ? manager().slots[(size_t)index.i]->handle : -1);
}
inline Value MgrIsShared() {
#ifdef FIRE_LIBRARY
    if (const fire_host* host = devHost()) return Bool(host->dev_manager_shared() != 0);
#endif
    return Bool(false);
}
inline Value MgrDefaultHandle() {
    init();
#ifdef FIRE_LIBRARY
    std::string id;
    if (const fire_host* host = devHost()) { int32_t handle = host->dev_default(); if (handle >= 0) id = hostText(host->dev_identifier, handle); }
#else
    std::string id = FIRE_DEFAULT_DEVICE;
#endif
    if (id.empty()) return Int(-1);
    for (Slot* slot : manager().slots) if (slot->identifier == id) return Int(slot->handle);
    return Int(-1);
}

inline Value Identifier(Value handle, OwnList* list) { init(); Slot* s = slotByHandle(handle.i); return ascii(s ? s->identifier : std::string(), list); }
inline Value IsShared(Value handle) { Slot* s = resolve(handle); return Bool(s && s->device->shared()); }
inline Value IsConnected(Value handle) { Slot* s = resolve(handle); return Bool(s && s->device->connected()); }
inline Value PortName(Value handle, OwnList* list) { Slot* s = resolve(handle); return ascii(s ? s->device->portName() : std::string(), list); }
inline Value Availability(Value handle) { Slot* s = resolve(handle); return Int(s ? s->device->availability() : (int)Unavailable); }
inline Value TestAvailability(Value handle) { Slot* s = resolve(handle); return Int(s ? s->device->testAvailability() : (int)Unavailable); }
inline Value Connect(Value handle) { Slot* s = resolve(handle); return Bool(s && s->device->connect()); }
inline Value Disconnect(Value handle) { Slot* s = resolve(handle); if (s) s->device->disconnect(); return Undef(); }
/// A line: the text and a line ending, in the encoding of the device (the prelude decides between a text and a command object).
inline Value DoCommand(Value handle, Value text) {
    Slot* s = resolve(handle);
    if (!s) return Bool(false);
    const Str* str = strOf(text);
    return Bool(s->device->sendCommand(std::u16string(str->data, str->length)));
}
inline Value HasData(Value handle) { Slot* s = resolve(handle); return Bool(s && s->buffer.hasData()); }
inline Value ReadString(Value handle, OwnList* list) {
    Slot* s = resolve(handle);
    return latin1(s ? s->buffer.take() : Bytes(), list);
}
inline Value Read(Value handle, OwnList* list) {
    Slot* s = resolve(handle);
    Bytes bytes = s ? s->buffer.take() : Bytes();
    Buf* b = allocBuf((uint32_t)bytes.size(), list);
    if (!bytes.empty()) std::memcpy(b->bytes(), bytes.data(), bytes.size());
    return BufV(b);
}
inline Value WriteString(Value handle, Value text) {
    Slot* s = resolve(handle);
    if (!s) return Bool(false);
    Bytes bytes = toLatin1(text);
    return Bool(s->device->write(bytes.data(), bytes.size()));
}
inline Value Write(Value handle, Value buffer) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    Slot* s = resolve(handle);
    if (!s) return Bool(false);
    Buf* b = bufOf(buffer);
    return Bool(s->device->write(b->bytes(), b->length));
}

#ifndef FIRE_LIBRARY
/// 1: found (the buffer is cut behind it), 0: time up / device disconnected / program ended, -1: not a time.
inline Value waitFor(Value handle, const Bytes& pattern, Value timeout) {
    Slot* s = resolve(handle);
    if (!s) return Int(0);
    int64_t ticks;
    if (timeout.kind == K_Undefined) ticks = g_defaultTimeoutTicks;
    else if (!timeTicksOf(timeout, ticks)) return Int(-1);
    bool found = false;
    waitUntil([&] {
        std::vector<Bytes> arrived;
        s->device->poll(arrived);
        for (Bytes& b : arrived) if (!b.empty()) s->buffer.packets.push_back(std::move(b));
        if (s->buffer.consumeThrough(pattern)) { found = true; return true; }
        return !s->device->connected();   // a disconnected device delivers nothing more: after a last look the wait ends
    }, ticks);
    return Int(found ? 1 : 0);
}
inline Value WaitForString(Value handle, Value text, Value timeout) { return waitFor(handle, toLatin1(text), timeout); }
inline Value WaitFor(Value handle, Value buffer, Value timeout) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    Buf* b = bufOf(buffer);
    return waitFor(handle, Bytes(b->bytes(), b->bytes() + b->length), timeout);
}
#endif

/// One step of a wait (the VM runs the waiting functions itself and calls this until it succeeds): looks at what has arrived and cuts the buffer behind `pattern` if it is there.
/// 1: found, 0: not yet, 2: not there and the device is disconnected (nothing more will come).
inline Value WaitStep(Value handle, Value buffer) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    Slot* s = resolve(handle);
    if (!s) return Int(2);
    Buf* b = bufOf(buffer);
    if (s->buffer.consumeThrough(Bytes(b->bytes(), b->bytes() + b->length))) return Int(1);
    return Int(s->device->connected() ? 0 : 2);
}

}  // namespace dev
}  // namespace fire
