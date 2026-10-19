// fire native bridge "gpio": the natives behind `#import "gpio"` (docs/NETWORK.md, "Hardware"): digital pins - read, write, pull resistors, edge events. The fire side (Gpio.Pin, Gpio.Board,
// Gpio.Sim - fire source) is the same in the VM and in a native build; this file is what its `__Gpio...` functions do.
//
// This file is the one implementation of the gpio natives: the native build includes it (the package "gpio" brings it as its C++ source) and the virtual machine runs it in a shared
// library built from it (native/abi/fire_pkg_abi.h). The pins come from the platform package (FIRE_PLATFORM_GPIO_HEADER: plat::gpio, see platform/std/fire_gpio_none.hpp): the GPIO character
// device on a Linux board (Raspberry Pi, ...), ESP-IDF on an ESP32. Where there is no hardware the list of chips is empty - but one chip is always there:
//
//   "sim"   32 simulated lines (0..31) that need no hardware: write an output, read it back, connect two lines with a wire (`SimWire`), drive an input from outside (`SimDrive`, as a button
//           would), pull resistors and edge events behave as on the real thing. For tests, and to develop a program on a PC that later runs on the board.
//
// A pin is an integer handle in a table of this bridge. Opening a pin claims nothing; the first `Configure` does (a second handle to the same line then fails with Busy). Every function reports
// an error the same way: a result of -1/false/undefined and the code and message of this thread (`__GpioLastError`, `__GpioLastErrorMessage`), from which the fire code throws a typed exception.
// Nothing waits: edges are collected (the sim here, the driver or an interrupt on a board) and `PollEdge` hands them out one by one, the fire code sleeps between the questions.
#pragma once

#include <chrono>
#include <cstddef>
#include <cstring>
#include FIRE_PLATFORM_GPIO_HEADER
#include <string>
#include <vector>

namespace fire {
namespace gpio {

enum Err { None = 0, InvalidArgument = 1, InvalidHandle = 2, NotFound = 3, Busy = 4, Permission = 5, Unsupported = 6, Other = 7 };

#if defined(FIRE_TLS_STRUCT)
#define FIRE_GPIO_ERROR g_ioError
#else
struct ErrorSlot { int32_t code; char message[176]; };
inline thread_local ErrorSlot t_gpioError = {0, {0}};
#define FIRE_GPIO_ERROR ::fire::gpio::t_gpioError
#endif

inline int64_t fail(int code, const std::string& message) {
    FIRE_GPIO_ERROR.code = code;
    size_t n = message.size() < sizeof FIRE_GPIO_ERROR.message - 1 ? message.size() : sizeof FIRE_GPIO_ERROR.message - 1;
    std::memcpy(FIRE_GPIO_ERROR.message, message.data(), n);
    FIRE_GPIO_ERROR.message[n] = 0;
    return -1;
}
inline void ok() { FIRE_GPIO_ERROR.code = 0; FIRE_GPIO_ERROR.message[0] = 0; }
inline int64_t fail(const plat::gpio::Status& s) { return fail(s.code, s.message); }

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

inline int64_t nowMicros() {
    return (int64_t)std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

// ---- the simulated chip -----------------------------------------------------------------------------------------------------------------
enum { SimLines = 32, EventQueue = 16 };
enum Direction { D_Input = 0, D_Output = 1 };
enum Pull { P_None = 0, P_Up = 1, P_Down = 2 };
enum Edge { E_None = 0, E_Rising = 1, E_Falling = 2, E_Both = 3 };

struct SimLine {
    bool claimed = false;
    bool configured = false;
    int direction = D_Input;
    int pull = P_None;
    int edge = E_None;
    int out = 0;            // the level an output drives
    int drive = -1;         // the level somebody outside drives (-1 nobody)
    int level = 0;          // the level on the line now (what a read gives)
    int events[EventQueue];
    int64_t times[EventQueue];
    int head = 0, count = 0;
};
struct Sim {
    SimLine line[SimLines];
    bool wire[SimLines][SimLines] = {};

    /// The lines that are connected to `start` by wires (including itself).
    int net(int start, bool* in) const {
        int stack[SimLines], sp = 0, n = 0;
        for (int i = 0; i < SimLines; i++) in[i] = false;
        in[start] = true;
        stack[sp++] = start;
        while (sp > 0) {
            int a = stack[--sp];
            n++;
            for (int b = 0; b < SimLines; b++)
                if (wire[a][b] && !in[b]) { in[b] = true; stack[sp++] = b; }
        }
        return n;
    }
    /// The level of the net `in`: an output wins (when two outputs disagree, low wins - as a pulled-down open line would), else the outside drive, else the pulls, else low.
    int levelOf(const bool* in) const {
        bool anyOut = false, high = false, anyDrive = false, driveHigh = false, up = false, down = false;
        for (int i = 0; i < SimLines; i++) {
            if (!in[i]) continue;
            const SimLine& l = line[i];
            if (l.drive >= 0) { anyDrive = true; if (l.drive) driveHigh = true; }
            if (l.configured && l.direction == D_Output) { anyOut = true; if (l.out) high = true; }
            else if (l.configured) { if (l.pull == P_Up) up = true; else if (l.pull == P_Down) down = true; }
        }
        if (anyOut) {
            for (int i = 0; i < SimLines; i++)
                if (in[i] && line[i].configured && line[i].direction == D_Output && !line[i].out) return 0;
            return high ? 1 : 0;
        }
        if (anyDrive) return driveHigh ? 1 : 0;
        if (up && !down) return 1;
        return 0;
    }
    /// Computes the level of every line again; every input that watches for the change gets an event.
    void settle() {
        int64_t now = nowMicros();
        bool in[SimLines];
        for (int i = 0; i < SimLines; i++) {
            net(i, in);
            int lv = levelOf(in);
            SimLine& l = line[i];
            if (lv == l.level) continue;
            l.level = lv;
            if (!l.configured || l.direction != D_Input) continue;
            int kind = lv ? E_Rising : E_Falling;
            if (!(l.edge & kind)) continue;
            if (l.count == EventQueue) { l.head = (l.head + 1) % EventQueue; l.count--; }   // the oldest is lost when nobody asks
            int at = (l.head + l.count) % EventQueue;
            l.events[at] = kind;
            l.times[at] = now;
            l.count++;
        }
    }
};
inline Sim& sim() { static Sim s; return s; }

// ---- the pins ---------------------------------------------------------------------------------------------------------------------------
struct Pin {
    bool isSim = false;
    int simLine = 0;
    plat::gpio::Line* line = nullptr;
    std::string chip;
    int number = 0;
    bool configured = false;
    int direction = D_Input;
    int64_t lastMicros = 0;
};
inline void release(Pin* p) {
    if (!p) return;
    if (p->isSim) {
        SimLine& l = sim().line[p->simLine];
        if (p->configured) {
            l.claimed = false;
            l.configured = false;
            l.count = 0;
            sim().settle();
        }
    } else if (p->line) plat::gpio::closeLine(p->line);
    delete p;
}
struct PinTable {
    std::vector<Pin*> items{1, nullptr};
    ~PinTable() { for (Pin* p : items) release(p); }
};
inline std::vector<Pin*>& pins() { static PinTable table; return table.items; }

/// The program has ended (the library for the VM stays loaded for the next one): gives the pins back and resets the simulated chip.
inline void reset() {
    std::vector<Pin*>& all = pins();
    for (size_t i = 1; i < all.size(); i++) {
        release(all[i]);
        all[i] = nullptr;
    }
    sim() = Sim();
}

inline Pin* find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < pins().size() && pins()[(size_t)i]) return pins()[(size_t)i];
    fail(InvalidHandle, "Invalid or already closed pin handle.");
    return nullptr;
}
inline Pin* findConfigured(Value h) {
    Pin* p = find(h);
    if (!p) return nullptr;
    if (!p->configured) { fail(InvalidArgument, "The pin is not set up yet (call Input or Output first)."); return nullptr; }
    return p;
}

// ---- natives ----------------------------------------------------------------------------------------------------------------------------
inline Value LastError() { return Int(FIRE_GPIO_ERROR.code); }
inline Value LastErrorMessage(OwnList* list) { return str8(FIRE_GPIO_ERROR.message, list); }
inline Value OpenCount() {
    int64_t n = 0;
    for (Pin* p : pins()) if (p) n++;
    return Int(n);
}
/// 1 if this machine has GPIO hardware (a driver for it and at least one chip; the simulated chip is there anyway), else 0.
inline Value Supported() {
    std::vector<std::string> hardware;
    ok();
    return Int(plat::gpio::supported() && plat::gpio::chips(hardware).ok() && !hardware.empty() ? 1 : 0);
}

/// The chips: "sim" first, then those of the hardware.
inline Value Chips(OwnList* list) {
    std::vector<std::string> names{"sim"};
    std::vector<std::string> hardware;
    plat::gpio::Status st = plat::gpio::chips(hardware);
    if (!st.ok()) { fail(st); return Undef(); }
    for (const std::string& n : hardware) names.push_back(n);
    ok();
    Arr* a = allocArr((uint32_t)names.size(), list);
    for (size_t i = 0; i < names.size(); i++) { a->items()[i] = str8(names[i], list); retain(a->items()[i]); }
    return ArrV(a);
}

inline Value Open(Value chip, Value number) {
    std::string name = toUtf8(chip);
    if (number.i < 0 || number.i > 100000) return Int(fail(InvalidArgument, "Invalid line number " + std::to_string(number.i) + "."));
    Pin* p = new Pin();
    p->chip = name;
    p->number = (int)number.i;
    if (name == "sim") {
        if (number.i >= SimLines) { delete p; return Int(fail(NotFound, "The simulated chip has the lines 0.." + std::to_string(SimLines - 1) + ", not " + std::to_string(number.i) + ".")); }
        p->isSim = true;
        p->simLine = (int)number.i;
    } else {
        plat::gpio::Status st = plat::gpio::openLine(name, (int)number.i, p->line);
        if (!st.ok()) { delete p; return Int(fail(st)); }
    }
    pins().push_back(p);
    ok();
    return Int((int64_t)pins().size() - 1);
}

/// Sets the pin up: direction 0 input / 1 output, pull 0 none / 1 up / 2 down, edge 0 none / 1 rising / 2 falling / 3 both (inputs), value: the level an output starts with. true, or false on an error.
inline Value Configure(Value h, Value direction, Value pull, Value edge, Value value) {
    Pin* p = find(h);
    if (!p) return Bool(false);
    if (direction.i < 0 || direction.i > 1 || pull.i < 0 || pull.i > 2 || edge.i < 0 || edge.i > 3) { fail(InvalidArgument, "Invalid direction, pull or edge."); return Bool(false); }
    int v = value.i != 0 ? 1 : 0;
    if (p->isSim) {
        SimLine& l = sim().line[p->simLine];
        if (!p->configured && l.claimed) { fail(Busy, "Line " + std::to_string(p->simLine) + " of the chip sim is already in use."); return Bool(false); }
        if (!p->configured) { l.claimed = true; l.count = 0; }
        l.configured = true;
        l.direction = (int)direction.i;
        l.pull = (int)pull.i;
        l.edge = direction.i == D_Input ? (int)edge.i : 0;
        l.out = v;
        sim().settle();
        if (direction.i == D_Input) l.count = 0;   // what happened while setting up is not an edge
    } else {
        plat::gpio::Status st = plat::gpio::configure(p->line, (int)direction.i, (int)pull.i, (int)edge.i, v);
        if (!st.ok()) { fail(st); return Bool(false); }
    }
    p->configured = true;
    p->direction = (int)direction.i;
    ok();
    return Bool(true);
}

/// The level of the pin: 0 or 1; -1 on an error.
inline Value Read(Value h) {
    Pin* p = findConfigured(h);
    if (!p) return Int(-1);
    if (p->isSim) { ok(); return Int(sim().line[p->simLine].level); }
    int v = 0;
    plat::gpio::Status st = plat::gpio::read(p->line, v);
    if (!st.ok()) return Int(fail(st));
    ok();
    return Int(v ? 1 : 0);
}

inline Value Write(Value h, Value value) {
    Pin* p = findConfigured(h);
    if (!p) return Bool(false);
    if (p->direction != D_Output) { fail(InvalidArgument, "The pin is an input: it cannot be written."); return Bool(false); }
    int v = value.i != 0 ? 1 : 0;
    if (p->isSim) {
        sim().line[p->simLine].out = v;
        sim().settle();
    } else {
        plat::gpio::Status st = plat::gpio::write(p->line, v);
        if (!st.ok()) { fail(st); return Bool(false); }
    }
    ok();
    return Bool(true);
}

/// The next edge since the last question, without waiting: 0 none, 1 rising, 2 falling; -1 on an error. `EdgeTime` then says when it happened.
inline Value PollEdge(Value h) {
    Pin* p = findConfigured(h);
    if (!p) return Int(-1);
    if (p->direction != D_Input) return Int(fail(InvalidArgument, "The pin is an output: it has no edges."));
    int event = 0;
    int64_t micros = 0;
    if (p->isSim) {
        SimLine& l = sim().line[p->simLine];
        if (l.count > 0) {
            event = l.events[l.head];
            micros = l.times[l.head];
            l.head = (l.head + 1) % EventQueue;
            l.count--;
        }
    } else {
        plat::gpio::Status st = plat::gpio::pollEdge(p->line, event, micros);
        if (!st.ok()) return Int(fail(st));
    }
    if (event != 0) p->lastMicros = micros;
    ok();
    return Int(event);
}

/// When the last edge that `PollEdge` handed out happened (microseconds of a clock that only goes forward).
inline Value EdgeTime(Value h) {
    Pin* p = find(h);
    if (!p) return Int(-1);
    ok();
    return Int(p->lastMicros);
}

inline Value Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= pins().size() || !pins()[(size_t)i]) { fail(InvalidHandle, "Invalid or already closed pin handle."); return Bool(false); }
    release(pins()[(size_t)i]);
    pins()[(size_t)i] = nullptr;
    ok();
    return Bool(true);
}

// ---- the simulated chip from the outside ------------------------------------------------------------------------------------------------
inline bool simLine(int64_t n) {
    if (n < 0 || n >= SimLines) { fail(NotFound, "The simulated chip has the lines 0.." + std::to_string(SimLines - 1) + ", not " + std::to_string(n) + "."); return false; }
    return true;
}
/// Connects two lines of the simulated chip by a wire (what one drives, the other sees).
inline Value SimWire(Value a, Value b) {
    if (!simLine(a.i) || !simLine(b.i)) return Bool(false);
    if (a.i == b.i) { fail(InvalidArgument, "A line cannot be wired to itself."); return Bool(false); }
    sim().wire[a.i][b.i] = sim().wire[b.i][a.i] = true;
    sim().settle();
    ok();
    return Bool(true);
}
inline Value SimUnwire(Value a, Value b) {
    if (!simLine(a.i) || !simLine(b.i)) return Bool(false);
    sim().wire[a.i][b.i] = sim().wire[b.i][a.i] = false;
    sim().settle();
    ok();
    return Bool(true);
}
/// Drives a line of the simulated chip from outside (a button, a sensor): 0 or 1, -1 lets go.
inline Value SimDrive(Value line, Value value) {
    if (!simLine(line.i)) return Bool(false);
    sim().line[line.i].drive = value.i < 0 ? -1 : (value.i != 0 ? 1 : 0);
    sim().settle();
    ok();
    return Bool(true);
}
/// The level on a line of the simulated chip, whoever drives it (it can be looked at without opening the line).
inline Value SimLevel(Value line) {
    if (!simLine(line.i)) return Int(-1);
    ok();
    return Int(sim().line[line.i].level);
}
/// Takes all wires and drives away (the pins that are open stay open).
inline Value SimReset() {
    Sim& s = sim();
    for (int i = 0; i < SimLines; i++) {
        s.line[i].drive = -1;
        for (int j = 0; j < SimLines; j++) s.wire[i][j] = false;
    }
    s.settle();
    ok();
    return Bool(true);
}

}  // namespace gpio
}  // namespace fire
