// fire native bridge "i2c": the natives behind `#import "i2c"` (docs/NETWORK.md, "I2C"): the I2C bus as a controller - write to a device, read from it, write then read with a repeated start, look
// which addresses answer. The fire side (I2c.Bus, I2c.Board, I2c.Sim - fire source) is the same in the VM and in a native build; this file is what its `__I2c...` functions do.
//
// This file is the one implementation of the i2c natives: the native build includes it (the package "i2c" brings it as its C++ source) and the virtual machine runs it in a shared library built from
// it (native/abi/fire_pkg_abi.h). The buses come from the platform package (FIRE_PLATFORM_I2C_HEADER: plat::i2c, see platform/std/fire_i2c_none.hpp): /dev/i2c-N on a Linux board, the master driver of
// ESP-IDF on an ESP32. Where there is no hardware the list of buses is empty - but one bus is always there:
//
//   "sim"   a simulated bus with devices that the program (or a test) puts on it: each device has a register file of 256 bytes that behaves like a typical sensor chip - the first byte written
//           is the register number, more bytes are stored from there on (the number counts up), a read returns the bytes from the current register on (counting up). An address with no
//           device does not answer (NoAck), as on a real bus. For tests, and to develop a program on a PC that later talks to the real device.
//
// A bus is an integer handle in a table of this bridge. Every function reports an error the same way: a result of -1/false/undefined and the code and message of this thread (`__I2cLastError`,
// `__I2cLastErrorMessage`), from which the fire code throws a typed exception. A transfer is short (a few bytes at 100 kHz take a millisecond), so it is done in the call and not polled.
#pragma once

#include <cstddef>
#include <cstdio>
#include <cstring>
#include FIRE_PLATFORM_I2C_HEADER
#include <string>
#include <vector>

namespace fire {
namespace i2c {

enum Err { None = 0, InvalidArgument = 1, InvalidHandle = 2, NotFound = 3, Busy = 4, Permission = 5, Unsupported = 6, Other = 7, NoAck = 8, Timeout = 9 };

#if defined(FIRE_TLS_STRUCT)
#define FIRE_I2C_ERROR g_ioError
#else
struct ErrorSlot { int32_t code; char message[176]; };
inline thread_local ErrorSlot t_i2cError = {0, {0}};
#define FIRE_I2C_ERROR ::fire::i2c::t_i2cError
#endif

inline int64_t fail(int code, const std::string& message) {
    FIRE_I2C_ERROR.code = code;
    size_t n = message.size() < sizeof FIRE_I2C_ERROR.message - 1 ? message.size() : sizeof FIRE_I2C_ERROR.message - 1;
    std::memcpy(FIRE_I2C_ERROR.message, message.data(), n);
    FIRE_I2C_ERROR.message[n] = 0;
    return -1;
}
inline void ok() { FIRE_I2C_ERROR.code = 0; FIRE_I2C_ERROR.message[0] = 0; }
inline int64_t fail(const plat::i2c::Status& s) { return fail(s.code, s.message); }

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
inline std::string hex(int64_t address) {
    char text[16];
    std::snprintf(text, sizeof text, "0x%02llX", (unsigned long long)(address & 0xFF));
    return text;
}

// ---- the simulated bus ------------------------------------------------------------------------------------------------------------------
enum { SimAddresses = 128, SimRegisters = 256 };
struct SimDevice {
    bool present = false;
    uint8_t regs[SimRegisters];
    int pointer = 0;     // the register the next byte is written to / read from
    SimDevice() { std::memset(regs, 0, sizeof regs); }
};
struct Sim {
    SimDevice device[SimAddresses];

    /// A write: the first byte selects the register, the others are stored from there on (an empty write only asks "is anybody there").
    void write(SimDevice& d, const uint8_t* bytes, int n) {
        if (n == 0) return;
        d.pointer = bytes[0];
        for (int i = 1; i < n; i++) {
            d.regs[d.pointer] = bytes[i];
            d.pointer = (d.pointer + 1) % SimRegisters;
        }
    }
    void read(SimDevice& d, uint8_t* bytes, int n) {
        for (int i = 0; i < n; i++) {
            bytes[i] = d.regs[d.pointer];
            d.pointer = (d.pointer + 1) % SimRegisters;
        }
    }
};
inline Sim& sim() { static Sim s; return s; }

// ---- the buses --------------------------------------------------------------------------------------------------------------------------
struct Bus {
    bool isSim = false;
    plat::i2c::Bus* bus = nullptr;
    std::string name;
};
inline void release(Bus* b) {
    if (!b) return;
    if (b->bus) plat::i2c::closeBus(b->bus);
    delete b;
}
struct BusTable {
    std::vector<Bus*> items{1, nullptr};
    ~BusTable() { for (Bus* b : items) release(b); }
};
inline std::vector<Bus*>& buses() { static BusTable table; return table.items; }

/// The program has ended (the library for the VM stays loaded for the next one): closes the buses and takes the devices off the simulated bus.
inline void reset() {
    std::vector<Bus*>& all = buses();
    for (size_t i = 1; i < all.size(); i++) {
        release(all[i]);
        all[i] = nullptr;
    }
    sim() = Sim();
}

inline Bus* find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < buses().size() && buses()[(size_t)i]) return buses()[(size_t)i];
    fail(InvalidHandle, "Invalid or already closed bus handle.");
    return nullptr;
}
inline bool checkAddress(int64_t address) {
    if (address < 0 || address > 127) { fail(InvalidArgument, "Invalid address " + std::to_string(address) + " (7 bits: 0..127)."); return false; }
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
inline bool checkCount(int64_t count) {
    if (count > 65535) { fail(InvalidArgument, "A transfer is at most 65535 bytes."); return false; }
    return true;
}
inline int64_t noAck(int64_t address) { return fail(NoAck, "No device answered at address " + hex(address) + "."); }

// ---- natives ----------------------------------------------------------------------------------------------------------------------------
inline Value LastError() { return Int(FIRE_I2C_ERROR.code); }
inline Value LastErrorMessage(OwnList* list) { return str8(FIRE_I2C_ERROR.message, list); }
inline Value OpenCount() {
    int64_t n = 0;
    for (Bus* b : buses()) if (b) n++;
    return Int(n);
}
/// 1 if this machine has I2C hardware (a driver for it and at least one bus; the simulated bus is there anyway), else 0.
inline Value Supported() {
    std::vector<std::string> hardware;
    ok();
    return Int(plat::i2c::supported() && plat::i2c::buses(hardware).ok() && !hardware.empty() ? 1 : 0);
}

/// The buses: "sim" first, then those of the hardware.
inline Value Buses(OwnList* list) {
    std::vector<std::string> names{"sim"};
    std::vector<std::string> hardware;
    plat::i2c::Status st = plat::i2c::buses(hardware);
    if (!st.ok()) { fail(st); return Undef(); }
    for (const std::string& n : hardware) names.push_back(n);
    ok();
    Arr* a = allocArr((uint32_t)names.size(), list);
    for (size_t i = 0; i < names.size(); i++) { a->items()[i] = str8(names[i], list); retain(a->items()[i]); }
    return ArrV(a);
}

inline Value Open(Value name, Value speed) {
    std::string n = toUtf8(name);
    if (speed.i < 1000 || speed.i > 5000000) return Int(fail(InvalidArgument, "The speed must be between 1 kHz and 5 MHz."));
    Bus* b = new Bus();
    b->name = n;
    if (n == "sim") b->isSim = true;
    else {
        plat::i2c::Status st = plat::i2c::openBus(n, (int)speed.i, b->bus);
        if (!st.ok()) { delete b; return Int(fail(st)); }
    }
    buses().push_back(b);
    ok();
    return Int((int64_t)buses().size() - 1);
}

/// Sets the clock speed in Hz (the simulated bus accepts any; Linux leaves it to the device tree).
inline Value SetSpeed(Value h, Value speed) {
    Bus* b = find(h);
    if (!b) return Bool(false);
    if (speed.i < 1000 || speed.i > 5000000) { fail(InvalidArgument, "The speed must be between 1 kHz and 5 MHz."); return Bool(false); }
    if (!b->isSim) {
        plat::i2c::Status st = plat::i2c::setSpeed(b->bus, (int)speed.i);
        if (!st.ok()) { fail(st); return Bool(false); }
    }
    ok();
    return Bool(true);
}

/// Writes `count` bytes to the device; the number written, -1 on an error (NoAck: nobody answered).
inline Value Write(Value h, Value address, Value buffer, Value offset, Value count) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkAddress(address.i) || !checkRange(buffer, offset.i, count.i) || !checkCount(count.i)) return Int(-1);
    Bus* b = find(h);
    if (!b) return Int(-1);
    const uint8_t* bytes = bufOf(buffer)->bytes() + offset.i;
    if (b->isSim) {
        SimDevice& d = sim().device[address.i];
        if (!d.present) return Int(noAck(address.i));
        sim().write(d, bytes, (int)count.i);
    } else {
        plat::i2c::Status st = plat::i2c::write(b->bus, (int)address.i, bytes, (int)count.i);
        if (!st.ok()) return Int(fail(st));
    }
    ok();
    return Int(count.i);
}

/// Reads `count` bytes from the device; the number read, -1 on an error.
inline Value Read(Value h, Value address, Value buffer, Value offset, Value count) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (!checkAddress(address.i) || !checkRange(buffer, offset.i, count.i) || !checkCount(count.i)) return Int(-1);
    Bus* b = find(h);
    if (!b) return Int(-1);
    uint8_t* bytes = bufOf(buffer)->bytes() + offset.i;
    if (b->isSim) {
        SimDevice& d = sim().device[address.i];
        if (!d.present) return Int(noAck(address.i));
        sim().read(d, bytes, (int)count.i);
    } else {
        plat::i2c::Status st = plat::i2c::read(b->bus, (int)address.i, bytes, (int)count.i);
        if (!st.ok()) return Int(fail(st));
    }
    ok();
    return Int(count.i);
}

/// Writes, then reads with a repeated start in between (the usual way to read a register): the number read, -1 on an error.
inline Value WriteRead(Value h, Value address, Value outBuffer, Value outOffset, Value outCount, Value inBuffer, Value inOffset, Value inCount) {
    if (!leafAlive(outBuffer)) return destroyedError(outBuffer);
    if (!leafAlive(inBuffer)) return destroyedError(inBuffer);
    if (!checkAddress(address.i) || !checkRange(outBuffer, outOffset.i, outCount.i) || !checkRange(inBuffer, inOffset.i, inCount.i) || !checkCount(outCount.i) || !checkCount(inCount.i)) return Int(-1);
    Bus* b = find(h);
    if (!b) return Int(-1);
    const uint8_t* out = bufOf(outBuffer)->bytes() + outOffset.i;
    uint8_t* in = bufOf(inBuffer)->bytes() + inOffset.i;
    if (b->isSim) {
        SimDevice& d = sim().device[address.i];
        if (!d.present) return Int(noAck(address.i));
        sim().write(d, out, (int)outCount.i);
        sim().read(d, in, (int)inCount.i);
    } else {
        plat::i2c::Status st = plat::i2c::writeRead(b->bus, (int)address.i, out, (int)outCount.i, in, (int)inCount.i);
        if (!st.ok()) return Int(fail(st));
    }
    ok();
    return Int(inCount.i);
}

/// 1 if a device answers at the address, 0 if not; -1 on an error.
inline Value Probe(Value h, Value address) {
    if (!checkAddress(address.i)) return Int(-1);
    Bus* b = find(h);
    if (!b) return Int(-1);
    bool present = false;
    if (b->isSim) present = sim().device[address.i].present;
    else {
        plat::i2c::Status st = plat::i2c::probe(b->bus, (int)address.i, present);
        if (!st.ok()) return Int(fail(st));
    }
    ok();
    return Int(present ? 1 : 0);
}

inline Value Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= buses().size() || !buses()[(size_t)i]) { fail(InvalidHandle, "Invalid or already closed bus handle."); return Bool(false); }
    release(buses()[(size_t)i]);
    buses()[(size_t)i] = nullptr;
    ok();
    return Bool(true);
}

// ---- the simulated bus from the outside -------------------------------------------------------------------------------------------------
/// Puts a device on the simulated bus (its registers are all 0); an existing one is replaced.
inline Value SimAdd(Value address) {
    if (!checkAddress(address.i)) return Bool(false);
    sim().device[address.i] = SimDevice();
    sim().device[address.i].present = true;
    ok();
    return Bool(true);
}
inline Value SimRemove(Value address) {
    if (!checkAddress(address.i)) return Bool(false);
    sim().device[address.i] = SimDevice();
    ok();
    return Bool(true);
}
inline SimDevice* simDevice(Value address, Value reg) {
    if (!checkAddress(address.i)) return nullptr;
    if (reg.i < 0 || reg.i >= SimRegisters) { fail(InvalidArgument, "Invalid register " + std::to_string(reg.i) + " (0..255)."); return nullptr; }
    SimDevice& d = sim().device[address.i];
    if (!d.present) { fail(NoAck, "There is no simulated device at address " + hex(address.i) + "."); return nullptr; }
    return &d;
}
/// Sets a register of a simulated device from the outside (a sensor value that changed).
inline Value SimSetRegister(Value address, Value reg, Value value) {
    SimDevice* d = simDevice(address, reg);
    if (!d) return Bool(false);
    d->regs[reg.i] = (uint8_t)(value.i & 0xFF);
    ok();
    return Bool(true);
}
/// A register of a simulated device (what a program wrote there); -1 on an error.
inline Value SimGetRegister(Value address, Value reg) {
    SimDevice* d = simDevice(address, reg);
    if (!d) return Int(-1);
    ok();
    return Int(d->regs[reg.i]);
}
/// Takes all devices off the simulated bus (open buses stay open).
inline Value SimReset() {
    sim() = Sim();
    ok();
    return Bool(true);
}

}  // namespace i2c
}  // namespace fire
