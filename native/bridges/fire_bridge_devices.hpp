// fire native bridge "devices": the natives behind `#import "devices"` (SPEC 8.16) - the device manager, devices (serial ports, the loopback device), the receive
// buffer of a device and the waiting functions (src/fire.Device.Bridge, src/fire.Device.Manager). The fire side (Device, DeviceManagerFacade - fire source) is the
// same as in the VM; this file is what its `__DEV...` functions do.
//
// Included by the generated file (after fire_rt.hpp) when the program imports "devices". Which devices exist is decided by the *drivers*:
//   FIRE_DEVICES          a string with the drivers in the order they are registered: "serial" (the default: the serial ports of the platform), "loopback" (a simulated
//                         device that sends back what it gets, identifier `loopback:echo`), e.g. -DFIRE_DEVICES="\"loopback,serial\""  (target configuration: defines)
//   FIRE_DEFAULT_DEVICE   the identifier of the default device (`Device.Default`); none by default
// The serial ports come from the platform package (FIRE_PLATFORM_DEV_HEADER: plat::dev::serialNames() and plat::dev::SerialPort, 115200 baud 8N1); a board without one
// defines nothing and has no `serial` devices.
//
// Differences to the VM: there are no background threads. Received data is collected when the program asks (HasData, Read..., the waiting functions, Connect),
// not while it does something else - the receive buffer of the operating system or of the UART driver holds it meanwhile. The device list is made when the program first uses
// a device function (and again at every Refresh), so `Count()` is not 0 before the first Refresh like in a standalone VM program.
#pragma once

#include <cstring>
#include FIRE_PLATFORM_DEV_HEADER
#include <string>
#include <vector>

#ifndef FIRE_DEVICES
#define FIRE_DEVICES "serial"
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
    std::vector<std::string> (*names)();
    Device* (*make)(const std::string&);
};

inline std::vector<std::string> loopbackNames() { return {"echo"}; }
inline Device* makeLoopback(const std::string& name) { return new Loopback(name); }
inline std::vector<std::string> serialNames() { return plat::dev::serialNames(); }
inline Device* makeSerial(const std::string& name) { return new Serial(name); }

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
    else if (id == "serial") { slot.names = serialNames; slot.make = makeSerial; }
    else return;
    m.drivers.push_back(slot);
}

/// RefreshDevices: new devices get handles, known ones stay; without `fast` every device is tested (also those that are gone).
inline void refresh(bool fast) {
    Manager& m = manager();
    for (const DriverSlot& driver : m.drivers) {
        std::vector<Slot*> toTest;
        for (const std::string& name : driver.names()) {
            std::string identifier = driver.id + ":" + name;
            Slot* existing = nullptr;
            for (Slot* s : m.slots) if (s->identifier == identifier) { existing = s; break; }
            if (!existing) {
                existing = new Slot();
                existing->handle = m.counter++;
                existing->identifier = identifier;
                existing->driver = driver.id;
                existing->device = driver.make(name);
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
inline Value MgrRefresh(Value fast) { init(); refresh(fast.i != 0); return Undef(); }
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
inline Value MgrIsShared() { return Bool(false); }
inline Value MgrDefaultHandle() {
    init();
    std::string id = FIRE_DEFAULT_DEVICE;
    if (id.empty()) return Int(-1);
    for (Slot* slot : manager().slots) if (slot->identifier == id) return Int(slot->handle);
    return Int(-1);
}

inline Value Identifier(Value handle, OwnList* list) { init(); Slot* s = slotByHandle(handle.i); return ascii(s ? s->identifier : std::string(), list); }
inline Value IsShared(Value) { return Bool(false); }
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
    Bytes bytes = s->device->encodeCommand(std::u16string(str->data, str->length));
    return Bool(s->device->write(bytes.data(), bytes.size()));
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

}  // namespace dev
}  // namespace fire
