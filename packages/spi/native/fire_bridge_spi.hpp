// fire native bridge "spi": the natives behind `#import "spi"` (docs/NETWORK.md, "SPI"): the SPI bus as a controller - a device is a bus with one chip select; a transfer sends and receives bytes at the same time. The fire side
// (Spi.Device, Spi.Board, Spi.Sim - fire source) is the same in the VM and in a native build; this file is what its `__Spi...` functions do.
//
// This file is the one implementation of the spi natives: the native build includes it (the package "spi" brings it as its C++ source) and the virtual machine runs it in a shared library built from it
// (native/abi/fire_pkg_abi.h). The devices come from the platform package (FIRE_PLATFORM_SPI_HEADER: plat::spi, see platform/std/fire_spi_none.hpp): /dev/spidevB.C on a Linux board, the SPI master driver of ESP-IDF on
// an ESP32. Where there is no hardware the list of devices is empty - but one device is always there:
//
//   "sim"   a simulated device. By default it is a loopback (MISO is tied to MOSI: what is sent comes back), which is what the usual wiring test does. `Reply` makes it a device that answers: the bytes
//           that were queued come back one per byte sent (zeros when the queue is empty); `Loopback` turns the loopback on again. Everything the program sent is kept (`SentCount`/`SentByte`), the
//           mode and speed that were set can be asked. For tests, and to develop a program on a PC that later talks to the real chip.
//
// A device is an integer handle in a table of this bridge. Every function reports an error the same way: a result of -1/false/undefined and the code and message of this thread (`__SpiLastError`,
// `__SpiLastErrorMessage`), from which the fire code throws a typed exception. A transfer is done in the call.
#pragma once

#include <cstddef>
#include <cstdio>
#include <cstring>
#include FIRE_PLATFORM_SPI_HEADER
#include <deque>
#include <string>
#include <vector>

namespace fire {
namespace spi {

enum Err { None = 0, InvalidArgument = 1, InvalidHandle = 2, NotFound = 3, Busy = 4, Permission = 5, Unsupported = 6, Other = 7, Timeout = 8 };

#if defined(FIRE_TLS_STRUCT)
#define FIRE_SPI_ERROR g_ioError
#else
struct ErrorSlot { int32_t code; char message[176]; };
inline thread_local ErrorSlot t_spiError = {0, {0}};
#define FIRE_SPI_ERROR ::fire::spi::t_spiError
#endif

inline int64_t fail(int code, const std::string& message) {
    FIRE_SPI_ERROR.code = code;
    size_t n = message.size() < sizeof FIRE_SPI_ERROR.message - 1 ? message.size() : sizeof FIRE_SPI_ERROR.message - 1;
    std::memcpy(FIRE_SPI_ERROR.message, message.data(), n);
    FIRE_SPI_ERROR.message[n] = 0;
    return -1;
}
inline void ok() { FIRE_SPI_ERROR.code = 0; FIRE_SPI_ERROR.message[0] = 0; }
inline int64_t fail(const plat::spi::Status& s) { return fail(s.code, s.message); }

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

// ---- the simulated device ---------------------------------------------------------------------------------------------------------------
enum { SentLimit = 65536 };
struct Sim {
    bool loopback = true;
    std::deque<uint8_t> replies;
    std::vector<uint8_t> sent;
    int mode = 0;
    int speed = 0;
    bool lsb = false;
    int64_t transfers = 0;

    void run(const uint8_t* out, uint8_t* in, int n) {
        transfers++;
        for (int i = 0; i < n; i++) {
            uint8_t o = out ? out[i] : 0;
            if (sent.size() < SentLimit) sent.push_back(o);
            uint8_t r = o;
            if (!loopback) {
                r = 0;
                if (!replies.empty()) { r = replies.front(); replies.pop_front(); }
            }
            if (in) in[i] = r;
        }
    }
};
inline Sim& sim() { static Sim s; return s; }

// ---- the devices ------------------------------------------------------------------------------------------------------------------------
struct Device {
    bool isSim = false;
    plat::spi::Device* device = nullptr;
    std::string name;
    int mode = 0;
    int speed = 1000000;
    bool lsb = false;
};
inline void release(Device* d) {
    if (!d) return;
    if (d->device) plat::spi::closeDevice(d->device);
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
    fail(InvalidHandle, "Invalid or already closed device handle.");
    return nullptr;
}
inline bool checkSetup(int64_t mode, int64_t speed) {
    if (mode < 0 || mode > 3) { fail(InvalidArgument, "The SPI mode is 0..3 (clock polarity and phase)."); return false; }
    if (speed < 1 || speed > 80000000) { fail(InvalidArgument, "The speed must be between 1 Hz and 80 MHz."); return false; }
    return true;
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
inline Value LastError() { return Int(FIRE_SPI_ERROR.code); }
inline Value LastErrorMessage(OwnList* list) { return str8(FIRE_SPI_ERROR.message, list); }
inline Value OpenCount() {
    int64_t n = 0;
    for (Device* d : devices()) if (d) n++;
    return Int(n);
}
/// 1 if this machine has SPI hardware (a driver for it and at least one device; the simulated device is there anyway), else 0.
inline Value Supported() {
    std::vector<std::string> hardware;
    ok();
    return Int(plat::spi::supported() && plat::spi::devices(hardware).ok() && !hardware.empty() ? 1 : 0);
}

/// The devices: "sim" first, then those of the hardware.
inline Value Devices(OwnList* list) {
    std::vector<std::string> names{"sim"};
    std::vector<std::string> hardware;
    plat::spi::Status st = plat::spi::devices(hardware);
    if (!st.ok()) { fail(st); return Undef(); }
    for (const std::string& n : hardware) names.push_back(n);
    ok();
    Arr* a = allocArr((uint32_t)names.size(), list);
    for (size_t i = 0; i < names.size(); i++) { a->items()[i] = str8(names[i], list); retain(a->items()[i]); }
    return ArrV(a);
}

/// Opens a device with mode 0..3, a clock in Hz and the bit order (lsb 1: least significant bit first).
inline Value Open(Value name, Value mode, Value speed, Value lsb) {
    if (!checkSetup(mode.i, speed.i)) return Int(-1);
    std::string n = toUtf8(name);
    Device* d = new Device();
    d->name = n;
    d->mode = (int)mode.i;
    d->speed = (int)speed.i;
    d->lsb = lsb.i != 0;
    if (n == "sim") {
        d->isSim = true;
        sim().mode = d->mode;
        sim().speed = d->speed;
        sim().lsb = d->lsb;
    } else {
        plat::spi::Status st = plat::spi::openDevice(n, d->mode, d->speed, d->lsb, d->device);
        if (!st.ok()) { delete d; return Int(fail(st)); }
    }
    devices().push_back(d);
    ok();
    return Int((int64_t)devices().size() - 1);
}

inline Value Configure(Value h, Value mode, Value speed, Value lsb) {
    Device* d = find(h);
    if (!d) return Bool(false);
    if (!checkSetup(mode.i, speed.i)) return Bool(false);
    if (d->isSim) {
        sim().mode = (int)mode.i;
        sim().speed = (int)speed.i;
        sim().lsb = lsb.i != 0;
    } else {
        plat::spi::Status st = plat::spi::configure(d->device, (int)mode.i, (int)speed.i, lsb.i != 0);
        if (!st.ok()) { fail(st); return Bool(false); }
    }
    d->mode = (int)mode.i;
    d->speed = (int)speed.i;
    d->lsb = lsb.i != 0;
    ok();
    return Bool(true);
}

inline int64_t run(Device* d, const uint8_t* out, uint8_t* in, int64_t count) {
    if (d->isSim) sim().run(out, in, (int)count);
    else {
        plat::spi::Status st = plat::spi::transfer(d->device, out, in, (int)count);
        if (!st.ok()) return fail(st);
    }
    ok();
    return count;
}

/// Sends `count` bytes and receives `count` bytes at the same time; the number of bytes, -1 on an error.
inline Value Transfer(Value h, Value outBuffer, Value outOffset, Value inBuffer, Value inOffset, Value count) {
    if (!leafAlive(outBuffer)) return destroyedError(outBuffer);
    if (!leafAlive(inBuffer)) return destroyedError(inBuffer);
    if (!checkRange(outBuffer, outOffset.i, count.i) || !checkRange(inBuffer, inOffset.i, count.i)) return Int(-1);
    Device* d = find(h);
    if (!d) return Int(-1);
    // (the buffers may be the same: what comes in replaces what was sent, byte by byte)
    return Int(run(d, bufOf(outBuffer)->bytes() + outOffset.i, bufOf(inBuffer)->bytes() + inOffset.i, count.i));
}

/// Sends `count` bytes and drops what comes in.
inline Value Write(Value h, Value buffer, Value offset, Value count) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    Device* d = find(h);
    if (!d) return Int(-1);
    return Int(run(d, bufOf(buffer)->bytes() + offset.i, nullptr, count.i));
}

/// Receives `count` bytes (the device is sent zeros meanwhile).
inline Value Read(Value h, Value buffer, Value offset, Value count) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Int(-1);
    Device* d = find(h);
    if (!d) return Int(-1);
    return Int(run(d, nullptr, bufOf(buffer)->bytes() + offset.i, count.i));
}

inline Value Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= devices().size() || !devices()[(size_t)i]) { fail(InvalidHandle, "Invalid or already closed device handle."); return Bool(false); }
    release(devices()[(size_t)i]);
    devices()[(size_t)i] = nullptr;
    ok();
    return Bool(true);
}

// ---- the simulated device from the outside ----------------------------------------------------------------------------------------------
/// Queues bytes that the simulated device answers with (one per byte sent); it stops being a loopback.
inline Value SimReply(Value buffer, Value offset, Value count) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkRange(buffer, offset.i, count.i)) return Bool(false);
    const uint8_t* bytes = bufOf(buffer)->bytes() + offset.i;
    sim().loopback = false;
    for (int64_t i = 0; i < count.i; i++) sim().replies.push_back(bytes[i]);
    ok();
    return Bool(true);
}
/// The simulated device is a loopback again (MISO tied to MOSI); the queued answers are dropped.
inline Value SimLoopback() {
    sim().loopback = true;
    sim().replies.clear();
    ok();
    return Bool(true);
}
/// How many bytes the program has sent to the simulated device (up to 65536 are kept).
inline Value SimSentCount() { ok(); return Int((int64_t)sim().sent.size()); }
inline Value SimSentByte(Value index) {
    if (index.i < 0 || (size_t)index.i >= sim().sent.size()) return Int(fail(InvalidArgument, "No such sent byte."));
    ok();
    return Int(sim().sent[(size_t)index.i]);
}
/// What was last set up on the simulated device: 0 the mode, 1 the speed, 2 the bit order (1: least significant first), 3 the number of transfers done.
inline Value SimInfo(Value which) {
    ok();
    switch (which.i) {
        case 0: return Int(sim().mode);
        case 1: return Int(sim().speed);
        case 2: return Int(sim().lsb ? 1 : 0);
        case 3: return Int(sim().transfers);
        default: return Int(fail(InvalidArgument, "No such information."));
    }
}
/// Forgets what was sent and the queued answers, keeps the mode of the loopback.
inline Value SimClear() {
    sim().sent.clear();
    sim().replies.clear();
    sim().transfers = 0;
    ok();
    return Bool(true);
}

}  // namespace spi
}  // namespace fire
