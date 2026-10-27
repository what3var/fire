// fire native bridge "usb": the natives behind `#import "usb"` (docs/USB.md): USB devices from the program - the list of devices, control transfers, bulk and interrupt transfers on the
// endpoints, and a bulk endpoint pair as a stream. The fire side (Usb.Device, Usb.Bus, Usb.UsbStream - fire source) is the same in the VM and in a native build; this file is what its
// `__Usb...` functions do.
//
// This file is the one implementation of the usb natives: the native build includes it (the package "usb" brings it as its C++ source) and the virtual machine runs it in a shared library
// built from it. The devices come from the platform package (FIRE_PLATFORM_USB_HEADER: plat::usb, see platform/std/fire_usb_common.hpp): libusb-1.0 on Linux and macOS, WinUSB on Windows.
// One device is always there:
//
//   "sim"   a simulated device (vendor 0x1209, product 0x0001): interface 0 with a bulk OUT endpoint 0x01, a bulk IN endpoint 0x81 (64-byte packets) and an interrupt IN endpoint
//           0x82. What is written to 0x01 comes back on 0x81 (a loopback; reading with nothing queued times out at once); 0x82 answers a counter byte. The vendor request 1 stores
//           (OUT, 0x40) and returns (IN, 0xC0) up to 64 bytes; the standard request GET_DESCRIPTOR (0x80, 6, 0x0100) returns its device descriptor. For tests, and to develop a program on a
//           machine without the device.
//
// A device is an integer handle in a table of this bridge. Every function reports an error the same way: a result of -1/false/undefined and the code and message of this thread
// (`__UsbLastError`, `__UsbLastErrorMessage`), from which the fire code throws a typed exception.
#pragma once

#include <cstddef>
#include <cstdio>
#include <cstring>
#include FIRE_PLATFORM_USB_HEADER
#include <deque>
#include <string>
#include <vector>

namespace fire {
namespace usb {

enum Err { None = 0, InvalidArgument = 1, InvalidHandle = 2, NotFound = 3, Busy = 4, Permission = 5, Unsupported = 6, Other = 7, Timeout = 8, Stall = 9, Disconnected = 10, Overflow = 11 };

#if defined(FIRE_TLS_STRUCT)
#define FIRE_USB_ERROR g_ioError
#else
struct ErrorSlot { int32_t code; char message[176]; };
inline thread_local ErrorSlot t_usbError = {0, {0}};
#define FIRE_USB_ERROR ::fire::usb::t_usbError
#endif

inline int64_t fail(int code, const std::string& message) {
    FIRE_USB_ERROR.code = code;
    size_t n = message.size() < sizeof FIRE_USB_ERROR.message - 1 ? message.size() : sizeof FIRE_USB_ERROR.message - 1;
    std::memcpy(FIRE_USB_ERROR.message, message.data(), n);
    FIRE_USB_ERROR.message[n] = 0;
    return -1;
}
inline void ok() { FIRE_USB_ERROR.code = 0; FIRE_USB_ERROR.message[0] = 0; }
inline int64_t fail(const plat::usb::Status& s) { return fail(s.code, s.message); }

// ---- text -------------------------------------------------------------------------------------------------------------------------------
inline std::string toUtf8(Value v) {
    const Str* s = strOf(v);
    std::string out;
    out.reserve(s->length);
    for (uint32_t i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c < 0x80) out.push_back((char)c);
        else if (c < 0x800) { out.push_back((char)(0xC0 | (c >> 6))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else { out.push_back((char)(0xE0 | (c >> 12))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
    }
    return out;
}
inline Value str8(const std::string& text, OwnList* list) {
    Str* s = allocStr((uint32_t)text.size(), list);
    for (size_t i = 0; i < text.size(); i++) strChars(s)[i] = (char16_t)(uint8_t)text[i];
    return StrV(s);
}
inline Value lines(const std::vector<std::string>& items, OwnList* list) {
    Arr* a = allocArr((uint32_t)items.size(), list);
    for (size_t i = 0; i < items.size(); i++) { a->items()[i] = str8(items[i], list); retain(a->items()[i]); }
    return ArrV(a);
}
// a field of a line: '|' becomes '/'
inline std::string field(const std::string& s) {
    std::string out = s;
    for (char& c : out) if (c == '|' || c == '\n') c = '/';
    return out;
}

// ---- the simulated device ---------------------------------------------------------------------------------------------------------------
struct Sim {
    std::deque<uint8_t> loop;
    std::vector<uint8_t> stored;
    int counter = 0;
    int64_t written = 0, read = 0, controls = 0;
    bool claimed = false;
};
inline Sim& sim() { static Sim s; return s; }
constexpr int SimVendor = 0x1209, SimProduct = 0x0001;

inline int simControl(int requestType, int request, int value, uint8_t* data, int length) {
    Sim& s = sim();
    s.controls++;
    bool in = (requestType & 0x80) != 0;
    if ((requestType & 0x60) == 0x40 && request == 1) {
        if (in) {
            int n = (int)s.stored.size() < length ? (int)s.stored.size() : length;
            if (n > 0) std::memcpy(data, s.stored.data(), (size_t)n);
            return n;
        }
        int n = length > 64 ? 64 : length;
        s.stored.assign(data, data + n);
        return n;
    }
    if (requestType == 0x80 && request == 6 && (value >> 8) == 1) {
        const uint8_t descriptor[18] = {18, 1, 0x00, 0x02, 0xFF, 0, 0, 64, (uint8_t)(SimVendor & 0xFF), (uint8_t)(SimVendor >> 8), (uint8_t)(SimProduct & 0xFF), (uint8_t)(SimProduct >> 8),
                                        0x00, 0x01, 1, 2, 3, 1};
        int n = length < 18 ? length : 18;
        std::memcpy(data, descriptor, (size_t)n);
        return n;
    }
    return (int)fail(Stall, "The simulated device does not know this request (it stalls).");
}

inline int simTransfer(int type, int endpoint, uint8_t* data, int length) {
    Sim& s = sim();
    if (endpoint == 0x01 && type == 2) {
        for (int i = 0; i < length; i++) s.loop.push_back(data[i]);
        s.written += length;
        return length;
    }
    if (endpoint == 0x81 && type == 2) {
        if (s.loop.empty()) return (int)fail(Timeout, "Reading from endpoint 1: timed out (the simulated device has nothing to send).");
        // a whole packet at most, as a real device sends it
        int n = 0;
        while (n < length && n < 64 && !s.loop.empty()) { data[n++] = s.loop.front(); s.loop.pop_front(); }
        s.read += n;
        return n;
    }
    if (endpoint == 0x82 && type == 3) {
        if (length < 1) return 0;
        data[0] = (uint8_t)(s.counter++ & 0xFF);
        return 1;
    }
    return (int)fail(InvalidArgument, "The simulated device has no " + std::string(type == 3 ? "interrupt" : "bulk") + " endpoint 0x" + std::to_string(endpoint) + ".");
}

// ---- the devices ------------------------------------------------------------------------------------------------------------------------
struct Device {
    bool isSim = false;
    plat::usb::Device* device = nullptr;
    std::string id;
};
inline void release(Device* d) {
    if (!d) return;
    if (d->device) plat::usb::closeDevice(d->device);
    delete d;
}
struct DeviceTable {
    std::vector<Device*> items{1, nullptr};
    ~DeviceTable() { for (Device* d : items) release(d); }
};
inline std::vector<Device*>& devices() { static DeviceTable table; return table.items; }

/// The program has ended (the library for the VM stays loaded for the next one): closes the devices and resets the simulated one.
inline void reset() {
    std::vector<Device*>& all = devices();
    for (size_t i = 1; i < all.size(); i++) {
        release(all[i]);
        all[i] = nullptr;
    }
    sim() = Sim();
}

inline Device* find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < devices().size() && devices()[(size_t)i]) return devices()[(size_t)i];
    fail(InvalidHandle, "Invalid or already closed USB device handle.");
    return nullptr;
}
inline bool checkRange(Value bufferValue, int64_t offset, int64_t count) {
    int64_t length = bufOf(bufferValue)->length;
    if (offset < 0 || count < 0 || offset > length || count > length - offset) {
        fail(InvalidArgument, "offset/count (" + std::to_string(offset) + "/" + std::to_string(count) + ") are outside of the buffer (length " + std::to_string(length) + ").");
        return false;
    }
    return true;
}

// ---- natives ----------------------------------------------------------------------------------------------------------------------------
inline Value LastError() { return Int(FIRE_USB_ERROR.code); }
inline Value LastErrorMessage(OwnList* list) { return str8(FIRE_USB_ERROR.message, list); }
inline Value OpenCount() {
    int64_t n = 0;
    for (Device* d : devices()) if (d) n++;
    return Int(n);
}
/// 1 if this machine has USB access (libusb or WinUSB is there; the simulated device is there anyway), else 0.
inline Value Supported() { ok(); return Int(plat::usb::supported() ? 1 : 0); }

/// The devices, one line each: id|vendor|product|class|manufacturer|product name|serial|bus|address - "sim" first.
inline Value Devices(OwnList* list) {
    std::vector<plat::usb::DeviceInfo> found;
    plat::usb::Status st = plat::usb::devices(found);
    if (!st.ok()) { fail(st); return Undef(); }
    std::vector<std::string> out;
    out.push_back("sim|" + std::to_string(SimVendor) + "|" + std::to_string(SimProduct) + "|255|fire|Simulated loopback device|0001|0|0");
    for (const auto& d : found)
        out.push_back(field(d.id) + "|" + std::to_string(d.vendorId) + "|" + std::to_string(d.productId) + "|" + std::to_string(d.deviceClass) + "|" + field(d.manufacturer) + "|" +
                      field(d.product) + "|" + field(d.serial) + "|" + std::to_string(d.bus) + "|" + std::to_string(d.address));
    ok();
    return lines(out, list);
}

inline Value Open(Value id) {
    std::string n = toUtf8(id);
    Device* d = new Device();
    d->id = n;
    if (n == "sim") d->isSim = true;
    else {
        plat::usb::Status st = plat::usb::openDevice(n, d->device);
        if (!st.ok()) { delete d; return Int(fail(st)); }
    }
    devices().push_back(d);
    ok();
    return Int((int64_t)devices().size() - 1);
}

/// The endpoints of the device, one line each: interface|alternate|address|type|max packet|interface class.
inline Value Endpoints(Value h, OwnList* list) {
    Device* d = find(h);
    if (!d) return Undef();
    std::vector<plat::usb::EndpointInfo> eps;
    if (d->isSim) {
        eps.push_back({0, 0, 0x01, 2, 64, 255});
        eps.push_back({0, 0, 0x81, 2, 64, 255});
        eps.push_back({0, 0, 0x82, 3, 8, 255});
    } else {
        plat::usb::Status st = plat::usb::endpoints(d->device, eps);
        if (!st.ok()) { fail(st); return Undef(); }
    }
    std::vector<std::string> out;
    for (const auto& e : eps)
        out.push_back(std::to_string(e.interfaceNumber) + "|" + std::to_string(e.alternate) + "|" + std::to_string(e.address) + "|" + std::to_string(e.type) + "|" +
                      std::to_string(e.maxPacket) + "|" + std::to_string(e.interfaceClass));
    ok();
    return lines(out, list);
}

/// what 0 claims, 1 releases interface `itf`; 2 sets its alternate setting `alt`.
inline Value Interface(Value h, Value what, Value itf, Value alt) {
    Device* d = find(h);
    if (!d) return Bool(false);
    if (d->isSim) {
        if (itf.i != 0) { fail(NotFound, "The simulated device has only interface 0."); return Bool(false); }
        if (what.i == 0) sim().claimed = true;
        if (what.i == 1) sim().claimed = false;
        ok();
        return Bool(true);
    }
    plat::usb::Status st = what.i == 0 ? plat::usb::claim(d->device, (int)itf.i) : what.i == 1 ? plat::usb::release(d->device, (int)itf.i)
                                       : plat::usb::setAlternate(d->device, (int)itf.i, (int)alt.i);
    if (!st.ok()) { fail(st); return Bool(false); }
    ok();
    return Bool(true);
}

/// A control transfer: the setup (request type, request, value, index), the data stage in buffer[offset .. offset + length) (in or out as bit 7 of the request type says); the
/// bytes moved, -1 on an error.
inline Value Control(Value h, Value requestType, Value request, Value value, Value index, Value buffer, Value offset, Value length, Value timeout) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, length.i)) return Int(-1);
    if (length.i > 65535) return Int(fail(InvalidArgument, "A control transfer moves at most 65535 bytes."));
    Device* d = find(h);
    if (!d) return Int(-1);
    uint8_t* data = bufOf(buffer)->bytes() + offset.i;
    if (d->isSim) {
        int n = simControl((int)requestType.i, (int)request.i, (int)value.i, data, (int)length.i);
        if (n < 0) return Int(-1);
        ok();
        return Int(n);
    }
    int done = 0;
    plat::usb::Status st = plat::usb::control(d->device, (int)requestType.i, (int)request.i, (int)value.i, (int)index.i, data, (int)length.i, (int)timeout.i, done);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(done);
}

/// A bulk (type 2) or interrupt (type 3) transfer on an endpoint (bit 7: in); the bytes moved, -1 on an error.
inline Value Transfer(Value h, Value type, Value endpoint, Value buffer, Value offset, Value length, Value timeout) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, length.i)) return Int(-1);
    if (type.i != 2 && type.i != 3) return Int(fail(InvalidArgument, "The transfer type is 2 (bulk) or 3 (interrupt)."));
    Device* d = find(h);
    if (!d) return Int(-1);
    uint8_t* data = bufOf(buffer)->bytes() + offset.i;
    if (d->isSim) {
        int n = simTransfer((int)type.i, (int)endpoint.i, data, (int)length.i);
        if (n < 0) return Int(-1);
        ok();
        return Int(n);
    }
    int done = 0;
    plat::usb::Status st = plat::usb::transfer(d->device, (int)type.i, (int)endpoint.i, data, (int)length.i, (int)timeout.i, done);
    // (a transfer that timed out after moving some bytes counts as done: the fire side goes on with the rest)
    if (!st.ok() && !(st.code == Timeout && done > 0)) return Int(fail(st));
    ok();
    return Int(done);
}

inline Value ClearHalt(Value h, Value endpoint) {
    Device* d = find(h);
    if (!d) return Bool(false);
    if (!d->isSim) {
        plat::usb::Status st = plat::usb::clearHalt(d->device, (int)endpoint.i);
        if (!st.ok()) { fail(st); return Bool(false); }
    }
    ok();
    return Bool(true);
}

inline Value Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= devices().size() || !devices()[(size_t)i]) { fail(InvalidHandle, "Invalid or already closed USB device handle."); return Bool(false); }
    release(devices()[(size_t)i]);
    devices()[(size_t)i] = nullptr;
    ok();
    return Bool(true);
}

/// What the simulated device has seen: 0 bytes written to it, 1 bytes read from it, 2 control transfers, 3 bytes still queued, 4 interface 0 claimed (1/0).
inline Value SimInfo(Value which) {
    ok();
    Sim& s = sim();
    switch (which.i) {
        case 0: return Int(s.written);
        case 1: return Int(s.read);
        case 2: return Int(s.controls);
        case 3: return Int((int64_t)s.loop.size());
        case 4: return Int(s.claimed ? 1 : 0);
        default: return Int(fail(InvalidArgument, "No such information."));
    }
}
inline Value SimClear() { sim() = Sim(); ok(); return Bool(true); }

}  // namespace usb
}  // namespace fire
