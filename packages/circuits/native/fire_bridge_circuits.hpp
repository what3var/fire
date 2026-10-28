// fire native bridge "circuits": the kernel behind `#import "circuits"` (docs/CIRCUITS.md). The fire side (Circuits.Circuit and its parts) compiles a circuit into
// a plan the first time it steps after a change: the nodes and branches, the matrix of the parts that do not change from step to step, and tables of the parts
// that this kernel steps itself (capacitors, inductors, sources). This file runs that plan:
//
//  * Compile: decides the order of the pivots once (partial pivoting on the numbers), then turns the elimination into a fixed list of operations on the
//    entries that are not zero (with the fill-in). Solving a step walks only those entries: a chain of resistors and capacitors costs O(n) instead of O(n^2).
//    Refactor runs the same list again on new numbers (non-linear parts, later stages).
//  * Begin/Finish: one time step - the right side from the parts of the kernel (and what the fire side adds with RhsAdd for parts written in fire), the
//    solution, the new state of the capacitors and inductors, the probes of the scopes. Run does many steps in one call.
//  * Recorders keep what scopes record; they live apart from a plan, so a recording goes on when the circuit is compiled again.
//
// The native build includes this file; the virtual machine runs it in the shared library of the package (native/abi/fire_pkg_abi.h).
#pragma once

#include <algorithm>
#include <cctype>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <cmath>
#include <cstdint>
#include <limits>
#include <string>
#include <vector>

// The simulation in a thread of its own (Runner): on the hosted platforms (the library of the virtual machine, native programs for Linux, Windows, macOS).
// Elsewhere (the browser, microcontrollers) the program steps the circuit itself, as before.
#if !defined(CIR_NO_THREADS) && !defined(__EMSCRIPTEN__) && (defined(FIRE_LIBRARY) || defined(FIRE_HAL_POSIX) || defined(FIRE_HAL_WINDOWS))
#define CIR_THREADS 1
#include <atomic>
#include <chrono>
#include <mutex>
#include <thread>
#endif

namespace fire {
namespace circuits {

#ifdef CIR_THREADS
/// One lock for the kernel: every native takes it, and so does the thread of a running plan while it does a few steps - the program reads and changes
/// plans and recordings only between those.
inline std::mutex& kernelMutex() { static std::mutex m; return m; }
struct KernelLock {
    KernelLock() { kernelMutex().lock(); }
    ~KernelLock() { kernelMutex().unlock(); }
    KernelLock(const KernelLock&) = delete;
    KernelLock& operator=(const KernelLock&) = delete;
};
#else
struct KernelLock {};
#endif

inline std::string& lastError() { static thread_local std::string e; return e; }

/// A text of fire (UTF-16) as UTF-8 (the formulas and parameters are ASCII; other characters only show in messages).
inline std::string toUtf8(Value v) {
    const Str* s = strOf(v);
    std::string out;
    for (uint32_t i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c < 0x80) out.push_back((char)c);
        else if (c < 0x800) { out.push_back((char)(0xC0 | (c >> 6))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else { out.push_back((char)(0xE0 | (c >> 12))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
    }
    return out;
}

inline double D(Value v) { return v.kind == K_Float ? (double)v.f : (double)v.i; }

/// What a scope records: the times and one row of samples per channel; stops when `capacity` samples are there.
/// A rolling recorder (made with a negative capacity) drops the older half instead and goes on.
struct Recorder {
    int64_t capacity = 100000;
    bool rolling = false;
    std::vector<double> times;
    std::vector<std::vector<double>> channels;
    /// Is there room for one more sample (after dropping the older half of a full rolling recorder)?
    bool room() {
        if ((int64_t)times.size() < capacity) return true;
        if (!rolling) return false;
        size_t drop = (size_t)(capacity / 2);
        times.erase(times.begin(), times.begin() + (std::ptrdiff_t)std::min(drop, times.size()));
        for (auto& c : channels) c.erase(c.begin(), c.begin() + (std::ptrdiff_t)std::min(drop, c.size()));
        return true;
    }
};

enum ProbeKind { PrNodes = 0, PrCapacitor = 1, PrInductor = 2, PrBranch = 3, PrConductance = 4, PrCurrentSource = 5, PrNonlinear = 6, PrCoupled = 7 };

struct Probe { int rec, channel, kind, a, b; double g; };

// WSweep + flags (1: logarithmic, 2: up and down again): WSweep .. WSweep + 3
enum Wave { WDc = 0, WSine = 1, WSquare = 2, WTriangle = 3, WSawtooth = 4, WNoise = 5, WSweep = 6 };

struct Source {
    int a, b, branch;      // branch >= 0: a voltage source (row of its branch), else a current source from a through it to b
    int wave;
    double p0, p1, p2, p3;  // DC: level; sine: amplitude, frequency, offset, phase; square: high, frequency, low, duty
    bool fed = false;       // driven by Process before: the next piece of sound goes on from its last sample
    double lastSample = 0;
    // the waves with a state (noise, sweep): the value of the step, and the state of the sweep (frequency, phase, direction)
    double held = 0, freq = 0, phase = 0, dir = 1;
    uint64_t seed = 0x9E3779B97F4A7C15ull;
    bool started = false;
    double value(double t) const {
        const double pi = 3.14159265358979323846;
        switch (wave) {
            case WSine: return p2 + p0 * std::sin(2.0 * pi * p1 * t + p3);
            case WSquare: {
                double cycles = t * p1;
                return cycles - std::floor(cycles) < p3 ? p0 : p2;
            }
            case WTriangle: case WSawtooth: {
                // as Falstad: amplitude p0, frequency p1, offset p2, phase p3
                double w = std::fmod(2.0 * pi * p1 * t + p3, 2.0 * pi);
                if (w < 0) w += 2.0 * pi;
                if (wave == WSawtooth) return p2 + w * (p0 / pi) - p0;
                return p2 + (w < pi ? w * (2 / pi) - 1 : 1 - (w - pi) * (2 / pi)) * p0;
            }
            default: return wave >= WNoise ? held : p0;
        }
    }
    /// Before a step: the waves with a state move on (Falstad's SweepElm: amplitude p0 from p1 to p2 Hz in p3 seconds).
    void advance(double dt) {
        if (wave == WNoise) {
            seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17;
            double r = (double)(seed >> 11) / 9007199254740992.0;
            held = (r * 2 - 1) * p0 + p2;
        } else if (wave >= WSweep) {
            int flags = wave - WSweep;
            if (!started || freq < p1 || freq > p2) { freq = p1; phase = 0; dir = 1; started = true; }
            held = std::sin(phase) * p0;
            phase += freq * 2 * 3.14159265358979323846 * dt;
            if (flags & 1) freq *= std::pow(p2 / p1, dir * dt / p3); else freq += dir * dt * (p2 - p1) / p3;
            if (freq >= p2 && dir == 1) {
                if (flags & 2) dir = -1; else freq = p1;
            }
            if (freq <= p1 && dir == -1) dir = 1;
        }
    }
};

// a capacitor (2C/dt) or an inductor (dt/2L), trapezoidal rule; `last` and `flips` watch it for ringing (see damp)
struct Reactive { int a, b; double geq, vPrev, iPrev, ieq; double last = 0; int flips = 0; };

/// The trapezoidal rule does not damp what is much faster than the time step: a capacitor shorted by a switch (or an inductor left open) keeps a
/// current (a voltage) that changes its sign every step and never dies away. A value that alternates two steps in a row cannot be a signal the step
/// resolves, so it is replaced by the mean of the last two values - the ringing is gone, and a slower signal is not touched.
inline void damp(double& value, double& last, int& flips) {
    bool flipped = (value > 0 && last < 0) || (value < 0 && last > 0);
    flips = flipped ? flips + 1 : 0;
    if (flips >= 2) { value = (value + last) / 2; flips = 0; }
    last = value;
}

/// Two coupled inductors (a transformer): winding 1 from a1 to b1, winding 2 from a2 to b2. With L = [[L1, M], [M, L2]] the trapezoidal rule gives
/// i(n+1) = G v(n+1) + h, G = dt/2 L^-1, h = i(n) + G v(n) - a 2x2 conductance (with the cross terms as voltage-controlled currents) and two currents.
struct Coupled { int a1, b1, a2, b2; double g11, g12, g22, v1Prev, v2Prev, i1Prev, i2Prev, h1, h2; };

/// A lossless transmission line (Bergeron's method): each end (a: the conductor, b: the return) is its impedance z with a current source that brings the
/// wave the other end sent `delay` ago. h holds per step and end V/z + I (I: the current into the line at its conductor).
struct TLine {
    int a1, b1, a2, b2;
    double z;
    int steps;
    std::vector<double> h1, h2;
    int ptr = 0;
    double j1 = 0, j2 = 0, i1 = 0, i2 = 0;
};

enum NlKind { NlDiode = 0, NlNpn = 1, NlPnp = 2, NlNmos = 3, NlPmos = 4, NlOpAmp = 5, NlFormula = 6,
              // resistances that follow a state between the steps (Falstad's startIteration), and a switch driven by a voltage
              NlLamp = 7, NlFuse = 8, NlMemristor = 9, NlSparkGap = 10, NlAnalogSwitch = 11,
              // the switch of a thyristor (anode to its inner node; gate node2, cathode aux) and of a triac (inner node to MT1; gate node2)
              NlScr = 12, NlTriac = 13,
              // vacuum tubes (Circuits.VacuumDiode, Triode, Pentode): a diode after Child and Langmuir (plate, cathode), triode and pentode after Norman
              // Koren's models (plate, grid, cathode; plate, screen, grid, cathode)
              NlVacuumDiode = 14, NlTriode = 15, NlPentode = 16 };
inline bool isVarRes(int kind) { return (kind >= NlLamp && kind <= NlSparkGap) || kind == NlScr || kind == NlTriac; }

constexpr double kVt = 0.025852;     // the thermal voltage at 27 degrees Celsius
constexpr double kGmin = 1e-12;      // a conductance across every junction (keeps the matrix regular when a junction is off)

inline double expl(double x, double& derivative);

// ---- formulas ----------------------------------------------------------------------------------------------------------------------------
// A part given by formulas for its currents (Circuits.Formula): `Is*(exp(V/(n*Vt))-1)`. The text is compiled once into a small stack program;
// evaluating it on dual numbers gives the value and its derivatives by the controlling voltages at the same time - what Newton's method needs.

/// A number with its derivatives by the two controlling voltages.
struct Dual { double v, d1, d2; };

enum FOp { FConst, FVar, FAdd, FSub, FMul, FDiv, FPow, FNeg, FCall };
enum FFn { FnExp, FnLog, FnSqrt, FnAbs, FnSin, FnCos, FnTanh, FnMin, FnMax, FnIf, FnPow, FnAtan, FnLimit };
struct FInstr { int op; double value; int arg; };   // FConst: value; FVar: arg 0 V1, 1 V2, 2 t; FCall: arg = function, value = argument count

struct FormulaProgram {
    std::vector<FInstr> code;
    bool exponential = false;     // uses exp or pow: Newton's method moves its voltages carefully
};

struct FormulaCompiler {
    const std::string& text;
    const std::vector<std::pair<std::string, double>>& params;
    bool threeTerminals;
    size_t pos = 0;
    FormulaProgram out;
    std::string error;

    FormulaCompiler(const std::string& t, const std::vector<std::pair<std::string, double>>& p, bool three) : text(t), params(p), threeTerminals(three) {}

    void skip() { while (pos < text.size() && (text[pos] == ' ' || text[pos] == '\t')) pos++; }
    bool eat(char c) { skip(); if (pos < text.size() && text[pos] == c) { pos++; return true; } return false; }
    bool fail(const std::string& message) { if (error.empty()) error = message + " (at " + std::to_string(pos + 1) + " of '" + text + "')"; return false; }

    bool expression() {
        if (!term()) return false;
        for (;;) {
            if (eat('+')) { if (!term()) return false; out.code.push_back({FAdd, 0, 0}); }
            else if (eat('-')) { if (!term()) return false; out.code.push_back({FSub, 0, 0}); }
            else return true;
        }
    }
    bool term() {
        if (!unary()) return false;
        for (;;) {
            if (eat('*')) { if (!unary()) return false; out.code.push_back({FMul, 0, 0}); }
            else if (eat('/')) { if (!unary()) return false; out.code.push_back({FDiv, 0, 0}); }
            else return true;
        }
    }
    bool unary() {
        if (eat('-')) { if (!unary()) return false; out.code.push_back({FNeg, 0, 0}); return true; }
        if (eat('+')) return unary();
        return power();
    }
    bool power() {
        if (!primary()) return false;
        if (eat('^')) {   // right-associative, binds tighter than a sign on its left: -2^2 is -4
            if (!unary()) return false;
            out.code.push_back({FPow, 0, 0});
            out.exponential = true;
        }
        return true;
    }
    bool primary() {
        skip();
        if (pos >= text.size()) return fail("An expression is missing");
        char c = text[pos];
        if (c == '(') { pos++; if (!expression()) return false; if (!eat(')')) return fail("')' expected"); return true; }
        if ((c >= '0' && c <= '9') || c == '.') {
            const char* start = text.c_str() + pos;
            char* end = nullptr;
            double v = std::strtod(start, &end);
            if (end == start) return fail("A number expected");
            pos += (size_t)(end - start);
            out.code.push_back({FConst, v, 0});
            return true;
        }
        if (std::isalpha((unsigned char)c) || c == '_') {
            size_t start = pos;
            while (pos < text.size() && (std::isalnum((unsigned char)text[pos]) || text[pos] == '_')) pos++;
            std::string name = text.substr(start, pos - start);
            skip();
            if (pos < text.size() && text[pos] == '(') {
                pos++;
                static const char* names[] = {"exp", "log", "sqrt", "abs", "sin", "cos", "tanh", "min", "max", "if", "pow", "atan", "limit"};
                static const int arity[] = {1, 1, 1, 1, 1, 1, 1, 2, 2, 3, 2, 1, 3};
                int fn = -1;
                for (int i = 0; i < 13; i++) if (name == names[i]) fn = i;
                if (fn < 0) return fail("Unknown function '" + name + "'");
                int count = 0;
                if (!eat(')')) {
                    do { if (!expression()) return false; count++; } while (eat(','));
                    if (!eat(')')) return fail("')' expected after the arguments of " + name);
                }
                if (count != arity[fn]) return fail(name + " takes " + std::to_string(arity[fn]) + " argument(s)");
                if (fn == FnExp || fn == FnPow) out.exponential = true;
                out.code.push_back({FCall, (double)count, fn});
                return true;
            }
            for (const auto& prm : params) if (prm.first == name) { out.code.push_back({FConst, prm.second, 0}); return true; }
            if (!threeTerminals && name == "V") { out.code.push_back({FVar, 0, 0}); return true; }
            if (threeTerminals && name == "V1") { out.code.push_back({FVar, 0, 0}); return true; }
            if (threeTerminals && name == "V2") { out.code.push_back({FVar, 0, 1}); return true; }
            if (name == "t") { out.code.push_back({FVar, 0, 2}); return true; }
            if (name == "Vt") { out.code.push_back({FConst, kVt, 0}); return true; }
            if (name == "pi") { out.code.push_back({FConst, 3.14159265358979323846, 0}); return true; }
            if (name == "e") { out.code.push_back({FConst, 2.718281828459045, 0}); return true; }
            return fail("Unknown name '" + name + "'" + (threeTerminals ? " (the voltages are V1 and V2)" : " (the voltage is V)"));
        }
        return fail(std::string("Unexpected '") + c + "'");
    }
};

/// "Is=1e-12 n=1.5" (also with commas or semicolons, and with the prefixes f p n u m k M G after a number) as names and values; false and a message on a mistake.
inline bool parseParameters(const std::string& text, std::vector<std::pair<std::string, double>>& out, std::string& error) {
    size_t i = 0;
    auto sep = [&](char c) { return c == ' ' || c == '\t' || c == ',' || c == ';' || c == '\n'; };
    while (i < text.size()) {
        while (i < text.size() && sep(text[i])) i++;
        if (i >= text.size()) break;
        size_t start = i;
        while (i < text.size() && (std::isalnum((unsigned char)text[i]) || text[i] == '_')) i++;
        std::string name = text.substr(start, i - start);
        while (i < text.size() && text[i] == ' ') i++;
        if (name.empty() || i >= text.size() || text[i] != '=') { error = "Parameters are written name=value: '" + text + "'"; return false; }
        i++;
        while (i < text.size() && text[i] == ' ') i++;
        const char* begin = text.c_str() + i;
        char* end = nullptr;
        double v = std::strtod(begin, &end);
        if (end == begin) { error = "The value of '" + name + "' is not a number"; return false; }
        i += (size_t)(end - begin);
        if (i < text.size() && !sep(text[i])) {
            static const char prefixes[] = "fpnumkMG";
            static const double factors[] = {1e-15, 1e-12, 1e-9, 1e-6, 1e-3, 1e3, 1e6, 1e9};
            const char* f = std::strchr(prefixes, text[i]);
            if (!f || !*f) { error = "Unknown suffix after the value of '" + name + "'"; return false; }
            v *= factors[f - prefixes];
            i++;
            while (i < text.size() && !sep(text[i])) i++;   // a unit after the prefix (5mA) is ignored
        }
        bool replaced = false;
        for (auto& prm : out) if (prm.first == name) { prm.second = v; replaced = true; }
        if (!replaced) out.push_back({name, v});
    }
    return true;
}

inline Dual fcall(int fn, const Dual* a) {
    switch (fn) {
        case FnExp: { double d, e = expl(a[0].v, d); return {e, d * a[0].d1, d * a[0].d2}; }
        case FnLog: { double v = a[0].v > 1e-300 ? a[0].v : 1e-300; return {std::log(v), a[0].d1 / v, a[0].d2 / v}; }
        case FnSqrt: { double r = std::sqrt(a[0].v > 0 ? a[0].v : 0); double k = r > 0 ? 0.5 / r : 0; return {r, k * a[0].d1, k * a[0].d2}; }
        case FnAbs: { double s = a[0].v < 0 ? -1 : 1; return {std::fabs(a[0].v), s * a[0].d1, s * a[0].d2}; }
        case FnSin: { double c = std::cos(a[0].v); return {std::sin(a[0].v), c * a[0].d1, c * a[0].d2}; }
        case FnCos: { double s = -std::sin(a[0].v); return {std::cos(a[0].v), s * a[0].d1, s * a[0].d2}; }
        case FnTanh: { double t = std::tanh(a[0].v), k = 1 - t * t; return {t, k * a[0].d1, k * a[0].d2}; }
        case FnAtan: { double k = 1 / (1 + a[0].v * a[0].v); return {std::atan(a[0].v), k * a[0].d1, k * a[0].d2}; }
        case FnMin: return a[0].v <= a[1].v ? a[0] : a[1];
        case FnMax: return a[0].v >= a[1].v ? a[0] : a[1];
        case FnIf: return a[0].v > 0 ? a[1] : a[2];
        case FnLimit: { if (a[0].v < a[1].v) return {a[1].v, 0, 0}; if (a[0].v > a[2].v) return {a[2].v, 0, 0}; return a[0]; }
        case FnPow: default: {
            const Dual& x = a[0]; const Dual& y = a[1];
            double v = std::pow(x.v, y.v);
            double dx = x.v != 0 ? y.v * std::pow(x.v, y.v - 1) : 0;
            double dy = x.v > 0 ? v * std::log(x.v) : 0;
            return {v, dx * x.d1 + dy * y.d1, dx * x.d2 + dy * y.d2};
        }
    }
}

inline Dual evaluate(const FormulaProgram& prog, double v1, double v2, double t) {
    Dual stack[64];
    int sp = 0;
    for (const FInstr& in : prog.code) {
        switch (in.op) {
            case FConst: stack[sp++] = {in.value, 0, 0}; break;
            case FVar:
                if (in.arg == 0) stack[sp++] = {v1, 1, 0};
                else if (in.arg == 1) stack[sp++] = {v2, 0, 1};
                else stack[sp++] = {t, 0, 0};
                break;
            case FNeg: stack[sp - 1] = {-stack[sp - 1].v, -stack[sp - 1].d1, -stack[sp - 1].d2}; break;
            case FAdd: case FSub: case FMul: case FDiv: case FPow: {
                Dual b = stack[--sp], a = stack[sp - 1], r;
                if (in.op == FAdd) r = {a.v + b.v, a.d1 + b.d1, a.d2 + b.d2};
                else if (in.op == FSub) r = {a.v - b.v, a.d1 - b.d1, a.d2 - b.d2};
                else if (in.op == FMul) r = {a.v * b.v, a.d1 * b.v + a.v * b.d1, a.d2 * b.v + a.v * b.d2};
                else if (in.op == FDiv) { double q = b.v != 0 ? b.v : 1e-300; r = {a.v / q, (a.d1 * q - a.v * b.d1) / (q * q), (a.d2 * q - a.v * b.d2) / (q * q)}; }
                else { Dual args[2] = {a, b}; r = fcall(FnPow, args); }
                stack[sp - 1] = r;
                break;
            }
            case FCall: {
                int n = (int)in.value;
                sp -= n;
                stack[sp] = fcall(in.arg, stack + sp);
                sp++;
                break;
            }
        }
    }
    return sp > 0 ? stack[sp - 1] : Dual{0, 0, 0};
}

/// The deepest the stack of a program gets (it must fit the 64 places of evaluate).
inline int stackDepth(const FormulaProgram& prog) {
    int sp = 0, most = 0;
    for (const FInstr& in : prog.code) {
        if (in.op == FConst || in.op == FVar) sp++;
        else if (in.op == FNeg) {}
        else if (in.op == FCall) sp -= (int)in.value - 1;
        else sp--;
        if (sp > most) most = sp;
    }
    return most;
}

/// A part that is not linear: a diode (nodes anode, cathode), a bipolar transistor (collector, base, emitter), a MOSFET (drain, gate, source) or an operational
/// amplifier (in+, in-, out). Every
/// iteration of Newton's method linearizes it at the present guess: the currents into its terminals and their derivatives by the node voltages.
struct Nonlinear {
    int kind;
    int node[4];
    int terminals;
    std::shared_ptr<FormulaProgram> f1, f2;   // a formula part: the current into terminal 0 (and into terminal 1)
    // the capacitances: a junction between the terminals a and b with the depletion capacitance of SPICE (cj0, vj, m; fc = 0.5) and a diffusion charge
    // tt * (the current of the junction); integrated with the trapezoidal rule. Diode: anode-cathode; bipolar: base-emitter (TF), base-collector;
    // MOSFET: gate-source and gate-drain (m = 0: linear)
    struct Junction {
        int a = 0, b = 0;
        double cj0 = 0, vj = 0.7, m = 0.5, tt = 0;
        double id = 0, gd = 0;            // the current of the junction and its derivative (for the diffusion charge), set by the model
        double qPrev = 0, iPrev = 0;      // after the last step
        bool started = false;
        double v = 0, q = 0, c = 0;       // at the linearization of the last iteration
        // the constants of the forward branch of the depletion charge (they depend on vj and m only), made at the first use
        double f1 = 0, f2 = 1, f3 = 0, fm = -1, fvj = -1;
    } junction[3];
    int junctions = 0;
    double p[8];                  // diode: Is, n, breakdown voltage, current at the breakdown voltage; bipolar: Is, beta forward, beta reverse, -; MOSFET: threshold, beta (A/V^2), lambda, -;
                                  // op amp: gain, lowest output, highest output, output resistance
    double lim[2] = {0, 0};       // the junction voltages of the last iteration (limited)
    double cur[4] = {0, 0, 0, 0}; // the currents into its terminals after the last step
    int aux = -1;                 // a node a state reads besides its terminals (the cathode of a thyristor)
    double st[2] = {0, 0};        // the state of a resistance that follows a state: lamp temperature / resistance, fuse heat / blown, memristor
                                  // doped width / resistance, spark gap on; the analog switch: on
    bool limited = false;         // the last linearization used a limited junction voltage (Newton's method must go on)
    // the linearization: I(V) = i0 + J (V - v0)
    double i0[4], j[4][4], v0[4];
};

/// The junction limit of SPICE (pnjlim): an exponential junction may not jump by many thermal voltages from one iteration to the next.
inline double pnjlim(double vnew, double vold, double vt, double vcrit) {
    if (vnew > vcrit && std::fabs(vnew - vold) > 2 * vt) {
        if (vold > 0) {
            double arg = 1 + (vnew - vold) / vt;
            vnew = arg > 0 ? vold + vt * std::log(arg) : vcrit;
        } else vnew = vt * std::log(vnew / vt);
    }
    return vnew;
}

/// exp with a straight line above x = 80 (no overflow while Newton's method is still far away).
inline double expl(double x, double& derivative) {
    if (x > 80) { double e = std::exp(80.0); derivative = e; return e * (1 + (x - 80)); }
    double e = std::exp(x);
    derivative = e;
    return e;
}

/// The depletion charge of SPICE (cj0, vj, m, fc = 0.5) at the junction voltage v, and its capacitance. `j` keeps the constants of the forward branch.
template <typename J> inline void depletion(J& j, double v, double& q, double& c) {
    const double fc = 0.5;
    const double cj0 = j.cj0, vj = j.vj;
    if (cj0 == 0) { q = 0; c = 0; return; }
    double m = j.m >= 0.999 ? 0.999 : j.m;
    if (v < fc * vj) {
        double r = 1 - v / vj, rm = std::pow(r, -m);   // r^(1-m) = r * r^-m: one pow for both
        q = cj0 * vj * (1 - r * rm) / (1 - m);
        c = cj0 * rm;
    } else {
        if (j.fm != m || j.fvj != vj) {
            j.f1 = vj * (1 - std::pow(1 - fc, 1 - m)) / (1 - m);
            j.f2 = std::pow(1 - fc, 1 + m);
            j.f3 = 1 - fc * (1 + m);
            j.fm = m; j.fvj = vj;
        }
        double v1 = fc * vj;
        q = cj0 * (j.f1 + (j.f3 * (v - v1) + m / (2 * vj) * (v * v - v1 * v1)) / j.f2);
        c = cj0 / j.f2 * (j.f3 + m * v / vj);
    }
}

/// The polarity of a part for its junctions (1, or -1 for PNP and PMOS).
inline double polarity(const Nonlinear& e) { return e.kind == NlPnp || e.kind == NlPmos ? -1.0 : 1.0; }

/// Adds the currents of the capacitances to the linearization: i = (q(v) - q before) / dt, di/dv = C(v) / dt - backward Euler: the charges of the junctions
/// are stiff (a stored charge leaves within nanoseconds), the trapezoidal rule would make them ring from step to step.
inline void stampCharges(Nonlinear& e, double dt) {
    double pol = polarity(e);
    for (int k = 0; k < e.junctions; k++) {
        auto& j = e.junction[k];
        double v = pol * (e.v0[j.a] - e.v0[j.b]);
        double q, c;
        depletion(j, v, q, c);
        q += j.tt * j.id;
        c += j.tt * j.gd;
        if (!j.started) { j.qPrev = q; j.iPrev = 0; j.started = true; }
        j.v = v; j.q = q; j.c = c;
        double ic = (q - j.qPrev) / dt, gc = c / dt;
        e.i0[j.a] += pol * ic; e.i0[j.b] -= pol * ic;
        e.j[j.a][j.a] += gc; e.j[j.a][j.b] -= gc; e.j[j.b][j.a] -= gc; e.j[j.b][j.b] += gc;
    }
}

/// After a step: the charges and currents of the capacitances at the voltages found (from the linearization of the last iteration).
inline void commitCharges(Nonlinear& e, const std::vector<double>& x, double dt) {
    double pol = polarity(e);
    auto V = [&](int t) { int n = e.node[t]; return n < 0 ? 0.0 : x[(size_t)n]; };
    for (int k = 0; k < e.junctions; k++) {
        auto& j = e.junction[k];
        double v = pol * (V(j.a) - V(j.b));
        double q = j.q + j.c * (v - j.v);
        j.iPrev = (q - j.qPrev) / dt;
        j.qPrev = q;
    }
}

/// The resistance of a part that follows a state (NlLamp ... NlSparkGap) for the present step.
inline double varResistance(const Nonlinear& e) {
    switch (e.kind) {
        case NlLamp: return e.st[1];
        case NlFuse: return e.st[1] != 0 ? 1e9 : e.p[0];
        case NlMemristor: return e.st[1];
        case NlSparkGap: return e.st[0] != 0 ? e.p[0] : e.p[1];
        case NlScr: return e.st[0] != 0 ? 0.0105 : 1e6;
        case NlTriac: return e.st[0] != 0 ? 0.01 : 1e6;
        default: return 1.0;
    }
}

/// The resistance of a lamp at the temperature t (Falstad's LampElm, after http://www.intusoft.com/nlpdf/nl11.pdf): nominal power p0 at the voltage p1.
inline double lampResistance(double nomPower, double nomVolts, double t) {
    double nomR = nomVolts * nomVolts / nomPower;
    if (t > 5390) t = 5390;
    return nomR * (1.26104 - 4.90662 * std::sqrt(17.1839 / t - 0.00318794) - 7.8569 / (t - 187.56));
}

/// Before a step: the state of a part that follows a state moves on with the current and voltage of the last step (Falstad's startIteration);
/// `gate` is the voltage of node2 against node1 (triac) or against aux (thyristor).
inline void stepState(Nonlinear& e, double v, double i, double dt, double gate) {
    if (e.kind == NlScr || e.kind == NlTriac) {
        // p: trigger current, holding current, gate resistance
        double ig = gate / e.p[2];
        if (e.kind == NlScr) e.st[0] = ig / e.p[0] + i / e.p[1] > 1 ? 1 : 0;
        else {
            if (std::fabs(i) < e.p[1]) e.st[0] = 0;
            if (std::fabs(ig) > e.p[0]) e.st[0] = 1;
        }
        return;
    }
    if (e.kind == NlLamp) {
        double nomPower = e.p[0], warm = e.p[2], cool = e.p[3];
        e.st[1] = lampResistance(nomPower, e.p[1], e.st[0]);
        double cap = 1.57e-4 * nomPower, capw = cap * warm / 0.4, capc = cap * cool / 0.4, cr = 2600 / nomPower;
        e.st[0] += v * i * dt / capw;
        e.st[0] -= dt * (e.st[0] - 300) / (capc * cr);
    } else if (e.kind == NlFuse) {
        double i2t = e.p[1];
        e.st[0] += i * i * dt;
        e.st[0] -= dt * i2t / 3;   // it gives off its whole i2t in 3 seconds
        if (e.st[0] < 0) e.st[0] = 0;
        if (e.st[0] > i2t) e.st[1] = 1;
    } else if (e.kind == NlMemristor) {
        double ron = e.p[0], roff = e.p[1], width = e.p[2], mobility = e.p[3];
        double wd = e.st[0] / width;
        e.st[0] += dt * mobility * ron * i / width;
        if (e.st[0] < 0) e.st[0] = 0;
        if (e.st[0] > width) e.st[0] = width;
        e.st[1] = ron * wd + roff * (1 - wd);
    } else if (e.kind == NlSparkGap) {
        if (std::fabs(i) < e.p[3]) e.st[0] = 0;
        if (std::fabs(v) > e.p[2]) e.st[0] = 1;
    }
}

/// ln(1 + e^x) without overflow, and its derivative (the logistic function).
inline double softplus(double x, double& derivative) {
    if (x > 30) { derivative = 1; return x; }
    if (x < -30) { double e = std::exp(x); derivative = e; return e; }
    double e = std::exp(x);
    derivative = e / (1 + e);
    return std::log1p(e);
}

/// The grid current of a tube once the grid is positive against the cathode: about Vgk / rgi (Koren's grid diode with its series resistance), starting
/// smoothly at 0 V; and its derivative.
inline double gridCurrent(double vg, double rgi, double& derivative) {
    if (vg <= 0 || rgi <= 0) { derivative = 0; return 0; }
    double d = vg + 0.5;
    derivative = (vg * vg + vg) / (rgi * d * d);
    return vg * vg / (rgi * d);
}

/// Linearizes a vacuum tube. Diode: Ip = k * Vpk^ex (Child and Langmuir, ex = 1.5). Triode (Koren): E1 = Vpk / kp * ln(1 + exp(kp * (1/mu + Vgk /
/// sqrt(kvb + Vpk^2)))), Ip = 2 * E1^ex / kg1. Pentode (Koren): E1 = Vg2k / kp * ln(1 + exp(kp * (1/mu + Vg1k / Vg2k))), Ip = 2 * E1^ex / kg1 *
/// atan(Vpk / kvb), the screen current (Vg1k + Vg2k / mu)^ex / kg2. A positive grid draws current through rgi.
template <typename VF> inline void linearizeTube(Nonlinear& e, VF V) {
    if (e.kind == NlVacuumDiode) {
        double k = e.p[0], ex = e.p[1];
        double v = V(0) - V(1);
        double i = 0, g = 0;
        if (v > 0) { i = k * std::pow(v, ex); g = k * ex * std::pow(v, ex - 1); }
        i += kGmin * v; g += kGmin;
        e.i0[0] = i; e.i0[1] = -i;
        e.j[0][0] = g; e.j[0][1] = -g; e.j[1][0] = -g; e.j[1][1] = g;
        return;
    }
    if (e.kind == NlTriode) {
        double mu = e.p[0], ex = e.p[1], kg1 = e.p[2], kp = e.p[3], kvb = e.p[4], rgi = e.p[5];
        double vp = V(0) - V(2), vg = V(1) - V(2);
        double s = std::sqrt(kvb + vp * vp);
        double sig, sp = softplus(kp * (1 / mu + vg / s), sig);
        double e1 = vp / kp * sp;
        double de1p = sp / kp - sig * vg * vp * vp / (s * s * s), de1g = vp * sig / s;
        double ip = 0, dip = 0;
        if (e1 > 0) { ip = 2 * std::pow(e1, ex) / kg1; dip = 2 * ex * std::pow(e1, ex - 1) / kg1; }
        double gp = dip * de1p + kGmin, gg = dip * de1g;
        ip += kGmin * vp;
        double dig, ig = gridCurrent(vg, rgi, dig);
        ig += kGmin * vg; dig += kGmin;
        // rows: plate, grid, cathode; columns: the node voltages of plate, grid, cathode
        e.i0[0] = ip; e.i0[1] = ig; e.i0[2] = -(ip + ig);
        double r0[3] = {gp, gg, -(gp + gg)};
        double r1[3] = {0, dig, -dig};
        for (int b = 0; b < 3; b++) { e.j[0][b] = r0[b]; e.j[1][b] = r1[b]; e.j[2][b] = -(r0[b] + r1[b]); }
        return;
    }
    // the pentode: plate 0, screen 1, grid 2, cathode 3 (the suppressor is joined to the cathode)
    double mu = e.p[0], ex = e.p[1], kg1 = e.p[2], kg2 = e.p[3], kp = e.p[4], kvb = e.p[5], rgi = e.p[6];
    double vp = V(0) - V(3), vs = V(1) - V(3), vg = V(2) - V(3);
    double vsc = vs > 1e-3 ? vs : 1e-3;
    double sig, sp = softplus(kp * (1 / mu + vg / vsc), sig);
    double e1 = vsc / kp * sp;
    double de1s = vs > 1e-3 ? sp / kp - sig * vg / vsc : 0, de1g = sig;
    double a = 0, da = 0;
    if (vp > 0) { double r = vp / kvb; a = std::atan(r); da = 1 / (kvb * (1 + r * r)); }
    double ip = 0, dpp = 0, dps = 0, dpg = 0;
    if (e1 > 0) {
        double pw = std::pow(e1, ex), dpw = ex * std::pow(e1, ex - 1);
        ip = 2 * pw / kg1 * a;
        dpp = 2 * pw / kg1 * da;
        dps = 2 * dpw / kg1 * a * de1s;
        dpg = 2 * dpw / kg1 * a * de1g;
    }
    ip += kGmin * vp; dpp += kGmin;
    double w = vg + vs / mu, is = 0, dsg = 0, dss = 0;
    if (w > 0) { is = std::pow(w, ex) / kg2; dsg = ex * std::pow(w, ex - 1) / kg2; dss = dsg / mu; }
    is += kGmin * vs; dss += kGmin;
    double dig, ig = gridCurrent(vg, rgi, dig);
    ig += kGmin * vg; dig += kGmin;
    e.i0[0] = ip; e.i0[1] = is; e.i0[2] = ig; e.i0[3] = -(ip + is + ig);
    double r0[4] = {dpp, dps, dpg, -(dpp + dps + dpg)};
    double r1[4] = {0, dss, dsg, -(dss + dsg)};
    double r2[4] = {0, 0, dig, -dig};
    for (int b = 0; b < 4; b++) { e.j[0][b] = r0[b]; e.j[1][b] = r1[b]; e.j[2][b] = r2[b]; e.j[3][b] = -(r0[b] + r1[b] + r2[b]); }
}

/// Linearizes the part at the node voltages x (with limited junctions).
inline void linearize(Nonlinear& e, const std::vector<double>& x) {
    auto V = [&](int t) { int n = e.node[t]; return n < 0 ? 0.0 : x[(size_t)n]; };
    const int nt = e.terminals;
    for (int a = 0; a < nt; a++) { e.i0[a] = 0; for (int b = 0; b < nt; b++) e.j[a][b] = 0; e.v0[a] = V(a); }
    e.limited = false;
    auto check = [&e](double wanted, double used) { if (std::fabs(wanted - used) > 1e-9 + 1e-6 * std::fabs(wanted)) e.limited = true; };
    if (e.kind == NlDiode) {
        double is = e.p[0], nvt = e.p[1] * kVt, bv = e.p[2];
        double vcrit = nvt * std::log(nvt / (1.4142135623730951 * is));
        double vd = V(0) - V(1);
        double vl = pnjlim(vd, e.lim[0], nvt, vcrit);
        if (bv > 0 && vd < -bv) {   // in breakdown the same limit, seen from the other side
            double vr = -(vd + bv), vrOld = -(e.lim[0] + bv);
            vl = -(pnjlim(vr, vrOld, nvt, vcrit) + bv);
        }
        check(vd, vl);
        e.lim[0] = vl;
        double d1, ex = expl(vl / nvt, d1);
        double i = is * (ex - 1), g = is / nvt * d1;
        if (bv > 0) {
            // the breakdown of SPICE: IBV flows at -BV and grows with the same slope as the forward current
            double ibv = e.p[3] > 0 ? e.p[3] : 1e-3;
            double d2, ez = expl(-(vl + bv) / nvt, d2);
            i -= ibv * ez;
            g += ibv / nvt * d2;
        }
        if (e.junctions > 0) { e.junction[0].id = i; e.junction[0].gd = g; }
        i += kGmin * vl;
        g += kGmin;
        e.v0[0] = V(1) + vl;
        e.i0[0] = i; e.i0[1] = -i;
        e.j[0][0] = g; e.j[0][1] = -g; e.j[1][0] = -g; e.j[1][1] = g;
    } else if (e.kind == NlNpn || e.kind == NlPnp) {
        double pol = e.kind == NlNpn ? 1.0 : -1.0;
        double is = e.p[0], bf = e.p[1], br = e.p[2];
        double vcrit = kVt * std::log(kVt / (1.4142135623730951 * is));
        double vbe = pnjlim(pol * (V(1) - V(2)), e.lim[0], kVt, vcrit);
        double vbc = pnjlim(pol * (V(1) - V(0)), e.lim[1], kVt, vcrit);
        check(pol * (V(1) - V(2)), vbe);
        check(pol * (V(1) - V(0)), vbc);
        e.lim[0] = vbe; e.lim[1] = vbc;
        double df, dr, ef = expl(vbe / kVt, df), er = expl(vbc / kVt, dr);
        double iF = is * (ef - 1) + kGmin * vbe, iR = is * (er - 1) + kGmin * vbc;
        double gf = is / kVt * df + kGmin, gr = is / kVt * dr + kGmin;
        double ic = iF - iR - iR / br, ib = iF / bf + iR / br;
        if (e.junctions > 0) { e.junction[0].id = iF; e.junction[0].gd = gf; }
        if (e.junctions > 1) { e.junction[1].id = iR; e.junction[1].gd = gr; }
        double dicBe = gf, dicBc = -gr * (1 + 1 / br), dibBe = gf / bf, dibBc = gr / br;
        // currents into the terminals (collector, base, emitter) and their derivatives by the node voltages (the polarity cancels: pol * pol = 1)
        e.i0[0] = pol * ic; e.i0[1] = pol * ib; e.i0[2] = -pol * (ic + ib);
        double jc[3] = {-dicBc, dicBe + dicBc, -dicBe};
        double jb[3] = {-dibBc, dibBe + dibBc, -dibBe};
        for (int b = 0; b < 3; b++) { e.j[0][b] = jc[b]; e.j[1][b] = jb[b]; e.j[2][b] = -(jc[b] + jb[b]); }
        e.v0[1] = V(1);
        e.v0[2] = V(1) - pol * vbe;
        e.v0[0] = V(1) - pol * vbc;
    } else if (e.kind == NlVacuumDiode || e.kind == NlTriode || e.kind == NlPentode) {
        linearizeTube(e, V);
    } else if (e.kind == NlFormula) {
        bool three = e.terminals == 3;
        int ref = three ? 2 : 1;
        double want1 = V(0) - V(ref), want2 = three ? V(1) - V(ref) : 0;
        double v1 = want1, v2 = want2;
        // a formula with exp or pow: the voltages move at most 0.5 V per iteration (like the limits of the junctions)
        if (e.f1->exponential || (e.f2 && e.f2->exponential)) {
            if (std::fabs(v1 - e.lim[0]) > 0.5) v1 = e.lim[0] + (v1 > e.lim[0] ? 0.5 : -0.5);
            if (three && std::fabs(v2 - e.lim[1]) > 0.5) v2 = e.lim[1] + (v2 > e.lim[1] ? 0.5 : -0.5);
        }
        check(want1, v1);
        check(want2, v2);
        e.lim[0] = v1; e.lim[1] = v2;
        double t = e.p[0];   // (the time of the step, set by the kernel before Newton's method)
        Dual i1 = evaluate(*e.f1, v1, v2, t);
        i1.v += kGmin * v1; i1.d1 += kGmin;
        if (!three) {
            e.i0[0] = i1.v; e.i0[1] = -i1.v;
            e.j[0][0] = i1.d1; e.j[0][1] = -i1.d1; e.j[1][0] = -i1.d1; e.j[1][1] = i1.d1;
            e.v0[0] = V(1) + v1;
        } else {
            Dual i2 = e.f2 ? evaluate(*e.f2, v1, v2, t) : Dual{0, 0, 0};
            i2.v += kGmin * v2; i2.d2 += kGmin;
            e.i0[0] = i1.v; e.i0[1] = i2.v; e.i0[2] = -(i1.v + i2.v);
            double r0[3] = {i1.d1, i1.d2, -(i1.d1 + i1.d2)};
            double r1[3] = {i2.d1, i2.d2, -(i2.d1 + i2.d2)};
            for (int b = 0; b < 3; b++) { e.j[0][b] = r0[b]; e.j[1][b] = r1[b]; e.j[2][b] = -(r0[b] + r1[b]); }
            e.v0[0] = V(2) + v1; e.v0[1] = V(2) + v2;
        }
    } else if (isVarRes(e.kind) || e.kind == NlAnalogSwitch) {
        double g;
        if (e.kind == NlAnalogSwitch) {
            // on above the threshold (below it when inverted), read anew in every iteration (as Falstad does)
            bool on = V(2) >= e.p[2];
            if (e.p[3] != 0) on = !on;
            e.st[0] = on ? 1 : 0;
            g = 1.0 / (on ? e.p[0] : e.p[1]);
        } else g = 1.0 / varResistance(e);
        double v = V(0) - V(1);
        e.i0[0] = g * v; e.i0[1] = -g * v;
        e.j[0][0] = g; e.j[0][1] = -g; e.j[1][0] = -g; e.j[1][1] = g;
    } else if (e.kind == NlOpAmp) {
        // the output follows mid + swing * tanh(gain * (in+ - in-) / swing) through its output resistance: linear with a high gain, limited at the rails
        double gain = e.p[0], lo = e.p[1], hi = e.p[2], rout = e.p[3];
        double mid = (hi + lo) / 2, swing = (hi - lo) / 2;
        double vd = V(0) - V(1);
        double t = std::tanh(gain * vd / swing);
        double target = mid + swing * t;
        double slope = gain * (1 - t * t);
        double g = 1.0 / rout;
        // current into the output terminal: (Vout - target) / rout
        e.i0[2] = (V(2) - target) * g;
        e.j[2][0] = -slope * g; e.j[2][1] = slope * g; e.j[2][2] = g;
    } else {
        double pol = e.kind == NlNmos ? 1.0 : -1.0;
        double vth = e.p[0], beta = e.p[1], lambda = e.p[2];
        // drain and source may change places (the device is symmetric): d and s are the terminals that act as drain and source now
        int d = 0, s = 2;
        if (pol * (V(0) - V(2)) < 0) { d = 2; s = 0; }
        double vgs = pol * (V(1) - V(s)), vds = pol * (V(d) - V(s));
        // limits in the manner of fetlim: the gate voltage moves at most 0.5 V around the threshold per iteration
        double vgsOld = e.lim[0], vgsWanted = vgs;
        if (std::fabs(vgs - vgsOld) > 0.5 && (vgs > vth || vgsOld > vth)) vgs = vgsOld + (vgs > vgsOld ? 0.5 : -0.5);
        check(vgsWanted, vgs);
        e.lim[0] = vgs;
        double vov = vgs - vth, id = 0, gm = 0, gds = 0;
        if (vov > 0) {
            if (vds < vov) {
                id = beta * (vov * vds - vds * vds / 2) * (1 + lambda * vds);
                gm = beta * vds * (1 + lambda * vds);
                gds = beta * (vov - vds) * (1 + lambda * vds) + beta * (vov * vds - vds * vds / 2) * lambda;
            } else {
                id = beta / 2 * vov * vov * (1 + lambda * vds);
                gm = beta * vov * (1 + lambda * vds);
                gds = beta / 2 * vov * vov * lambda;
            }
        }
        id += kGmin * vds;
        gds += kGmin;
        // terminals: 0 drain, 1 gate, 2 source of the part; d/s are the ones acting now
        e.i0[d] = pol * id; e.i0[s] = -pol * id;
        e.j[d][1] = gm; e.j[d][d] = gds; e.j[d][s] = -(gm + gds);
        e.j[s][1] = -gm; e.j[s][d] = -gds; e.j[s][s] = gm + gds;
        e.v0[s] = V(s);
        e.v0[1] = V(s) + pol * vgs;
    }
}

enum GateKind { GAnd = 0, GOr = 1, GXor = 2, GNand = 3, GNor = 4, GXnor = 5, GNot = 6, GBuffer = 7, GDFlipFlop = 8, GComparator = 9, GSRLatch = 10,
                GJKFlipFlop = 11, GSchmitt = 12, GInvSchmitt = 13 };

/// A logic part: it reads its inputs after every step (high above `threshold`) and drives its outputs for the next one through `rout` (high: `high` volts,
/// low: 0 V). A D flip-flop has the inputs D and clock and the outputs Q and not Q; it takes D on the rising edge of the clock. A comparator is high
/// while its first input is above its second (analog). An SR latch has the inputs S, R and (optional) a reset that is active low; R wins over S.
/// A JK flip-flop has the inputs J, clock, K and (optional) a reset that is active high: on the edge of the clock (rising, or falling with `falling`)
/// J and K both high toggle it, J sets it, K clears it. A Schmitt trigger goes high above `threshold` and low below `low` (in between it keeps its state);
/// the inverting one the other way round.
struct Gate {
    int kind;
    std::vector<int> inputs;
    int out, outNot;
    double high, threshold, g;
    double low = 0;            // the lower threshold of a Schmitt trigger
    bool falling = false;      // a JK flip-flop that takes the falling edge
    bool state = false, clock = false;
    bool primed = false;   // a flip-flop has seen its clock once (the first step only notes it: no edge at power-on)
    bool eval(const std::vector<double>& x) {
        auto volts = [&](size_t i) { int n = inputs[i]; return n < 0 ? 0.0 : x[(size_t)n]; };
        auto in = [&](size_t i) { return volts(i) > threshold; };
        if (kind == GComparator) return inputs.size() > 1 && volts(0) > volts(1);
        if (kind == GSRLatch) {
            bool reset = inputs.size() > 2 && !in(2);
            if (reset || (inputs.size() > 1 && in(1))) state = false;
            else if (in(0)) state = true;
            return state;
        }
        if (kind == GJKFlipFlop) {
            bool c = inputs.size() > 1 && in(1);
            bool edge = falling ? (!c && clock) : (c && !clock);
            if (primed && edge) {
                bool j = in(0), k = inputs.size() > 2 && in(2);
                if (j && k) state = !state;
                else if (j) state = true;
                else if (k) state = false;
            }
            primed = true;
            clock = c;
            if (inputs.size() > 3 && in(3)) state = false;
            return state;
        }
        if (kind == GSchmitt || kind == GInvSchmitt) {
            // `clock` keeps whether the input was last seen above the upper threshold
            double v = volts(0);
            if (v > threshold) clock = true;
            else if (v < low) clock = false;
            return kind == GSchmitt ? clock : !clock;
        }
        if (kind == GDFlipFlop) {
            bool c = inputs.size() > 1 && in(1);
            if (primed && c && !clock) state = in(0);
            primed = true;
            clock = c;
            return state;
        }
        bool r = kind == GAnd || kind == GNand || kind == GXnor;
        if (kind == GXor || kind == GXnor) { r = false; for (size_t i = 0; i < inputs.size(); i++) r ^= in(i); if (kind == GXnor) r = !r; return r; }
        if (kind == GNot || kind == GBuffer) { bool v = !inputs.empty() && in(0); return kind == GNot ? !v : v; }
        for (size_t i = 0; i < inputs.size(); i++) {
            if (kind == GAnd || kind == GNand) r = r && in(i); else r = r || in(i);
        }
        if (kind == GNand || kind == GNor) r = !r;
        return r;
    }
};



// ---- chips: logic and mixed-signal ICs after Falstad's ChipElm ----

enum ChipKind { CTFlipFlop = 1, CLatch = 2, CRingCounter = 3, CMux = 4, CDemux = 5, CHalfAdder = 6, CFullAdder = 7, CSipo = 8, CPiso = 9, CAdc = 10,
                CDac = 11, CSevenSegDecoder = 12, CMonostable = 13, CPhaseComp = 14, CVco = 15 };
enum ChipPinMode { PinIn = 0, PinOut = 1, PinCurrent = 2 };

/// A chip: pins in the order of Falstad's chip (inputs, outputs that drive a level through `1/g` like the gates, and outputs that drive a current), its
/// logic run once per step from the voltages of the step before (`execute`, as in Falstad's startIteration). `bits` and `flags` say how it is made.
struct Chip {
    int kind = 0, bits = 0, flags = 0;
    std::vector<int> node;
    std::vector<char> mode;
    std::vector<char> val;          // the logic value of each pin (inputs: read; outputs: driven)
    std::vector<double> level;      // what an output drives this step: volts (PinOut) or amperes (PinCurrent)
    std::vector<double> st;         // the state of the chip (data of a shift register, times of a monostable, ...)
    double high = 5, threshold = 2.5, g = 0.1;
    double p[4] = {0, 0, 0, 0};
    bool lastClock = false, lastLoad = false;

    bool in(size_t i) const { return val[i] != 0; }
    void set(size_t i, bool v) { val[i] = v ? 1 : 0; }

    void execute(const std::vector<double>& x, double time) {
        auto volts = [&](size_t i) { int n = node[i]; return n < 0 ? 0.0 : x[(size_t)n]; };
        for (size_t i = 0; i < node.size(); i++) {
            if (mode[i] == PinIn) val[i] = volts(i) > threshold ? 1 : 0;
            level[i] = std::numeric_limits<double>::quiet_NaN();
        }
        const size_t n = (size_t)bits;
        switch (kind) {
            case CTFlipFlop: {
                // T, Q, /Q, clock [, R [, S]] - flags 1: reset, 2: set
                bool reset = (flags & 1) || (flags & 2);
                bool c = in(3);
                if (c && !lastClock && in(0)) set(1, !in(1));
                if ((flags & 2) && in(5)) set(1, true);
                if (reset && in(4)) set(1, false);
                set(2, !in(1));
                lastClock = c;
                break;
            }
            case CLatch: {
                // I0..In-1, O0..On-1, load [, R] [, S] - flags 1: level (not edge) triggered, 2: reset, 4: set, 8: reset active low
                size_t load = 2 * n, k = load + 1;
                size_t reset = (flags & 2) ? k++ : 0, setp = (flags & 4) ? k++ : 0;
                bool ld = in(load);
                if ((flags & 4) && in(setp)) { for (size_t i = 0; i < n; i++) set(n + i, true); }
                else if ((flags & 2) && in(reset) != ((flags & 8) != 0)) { for (size_t i = 0; i < n; i++) set(n + i, false); }
                else if (ld && ((flags & 1) || !lastLoad)) { for (size_t i = 0; i < n; i++) set(n + i, in(i)); }
                lastLoad = ld;
                break;
            }
            case CRingCounter: {
                // clock, R, Q0..Qn-1 [, CE] - flags 1: clock inhibit (CE, active low), 2: reset active high
                bool running = !((flags & 1) && in(n + 2));
                size_t i = 0;
                while (i < n && !in(i + 2)) i++;
                if (in(0) && !lastClock && running) {
                    if (i < n) set(2 + i++, false);
                    i %= n;
                    set(2 + i, true);
                }
                bool resetActive = (flags & 2) ? in(1) : !in(1);
                if (resetActive || i == n) {
                    for (size_t k = 1; k < n; k++) set(k + 2, false);
                    set(2, true);
                }
                lastClock = in(0);
                break;
            }
            case CMux: {
                // I0..I(2^s - 1), S0..Ss-1, Q [, /Q] [, strobe] - bits = s; flags 1: inverted output, 2: strobe
                size_t count = (size_t)1 << n, sel = 0;
                for (size_t i = 0; i < n; i++) if (in(count + i)) sel |= (size_t)1 << i;
                size_t out = count + n;
                bool v = in(sel);
                size_t strobe = out + 1 + ((flags & 1) ? 1 : 0);
                if ((flags & 2) && in(strobe)) v = false;
                set(out, v);
                if (flags & 1) set(out + 1, !v);
                break;
            }
            case CDemux: {
                // Q0..Q(2^s - 1), S0..Ss-1, input - flags 1: inverted outputs
                size_t count = (size_t)1 << n, sel = 0;
                for (size_t i = 0; i < n; i++) if (in(count + i)) sel |= (size_t)1 << i;
                bool idle = (flags & 1) != 0;
                for (size_t i = 0; i < count; i++) set(i, idle);
                set(sel, in(count + n));
                break;
            }
            case CHalfAdder:
                set(0, in(2) != in(3));
                set(1, in(2) && in(3));
                break;
            case CFullAdder: {
                // A0..An-1, B0..Bn-1, S0..Sn-1, carry in, carry out
                int c = in(3 * n) ? 1 : 0;
                for (size_t i = 0; i < n; i++) {
                    int v = (in(i) ? 1 : 0) + (in(i + n) ? 1 : 0) + c;
                    c = v > 1 ? 1 : 0;
                    set(i + 2 * n, (v & 1) == 1);
                }
                set(3 * n + 1, c == 1);
                break;
            }
            case CSipo: {
                // D, clock, Q0..Qn-1
                bool c = in(1);
                if (c != lastClock) {
                    lastClock = c;
                    if (c && n > 0) {
                        for (size_t i = n - 1; i >= 1; i--) set(2 + i, in(2 + i - 1));
                        set(2, in(0));
                    }
                }
                break;
            }
            case CPiso: {
                // load, clock, Q [, serial in], D(n-1)..D0 - flags 1: Falstad's new behavior (with serial in); st: n data bits, then the index
                bool modern = (flags & 1) != 0;
                size_t data = modern ? 4 : 3;
                if (st.size() < n + 1) st.assign(n + 1, 0.0);
                double& index = st[n];
                if (in(0) != lastLoad) {
                    lastLoad = in(0);
                    if (lastLoad && n > 0) {
                        if (modern) { set(2, in(data)); index = 0; } else index = -1;
                        for (size_t i = 0; i < n; i++) st[i] = in(data + i) ? 1 : 0;
                    }
                }
                if (in(1) != lastClock) {
                    lastClock = in(1);
                    if (lastClock && n > 0) {
                        if (index >= 0) st[(size_t)index] = (modern && in(3)) ? 1 : 0;
                        index += 1;
                        if (index >= (double)n) index = 0;
                        set(2, st[(size_t)index] != 0);
                    }
                }
                break;
            }
            case CAdc: {
                // D0..Dn-1, In, V+ (the reference)
                double imax = (double)(((int64_t)1 << n) - 1), ref = volts(n + 1);
                int64_t v = ref != 0 ? (int64_t)std::trunc(imax * volts(n) / ref) : 0;
                if (v < 0) v = 0;
                if (v > (int64_t)imax) v = (int64_t)imax;
                for (size_t i = 0; i < n; i++) set(i, (v >> i) & 1);
                break;
            }
            case CDac: {
                // D0..Dn-1, O (analog), V+ (the reference)
                int64_t v = 0;
                for (size_t i = 0; i < n; i++) if (in(i)) v |= (int64_t)1 << i;
                double imax = (double)(((int64_t)1 << n) - 1);
                level[n] = imax > 0 ? (double)v * volts(n + 1) / imax : 0;
                break;
            }
            case CSevenSegDecoder: {
                // a..g, I3, I2, I1, I0 [, BI] - flags 1: blanking input (active low), 2: blank on F
                static const char sym[16][7] = {
                    {1,1,1,1,1,1,0}, {0,1,1,0,0,0,0}, {1,1,0,1,1,0,1}, {1,1,1,1,0,0,1}, {0,1,1,0,0,1,1}, {1,0,1,1,0,1,1}, {1,0,1,1,1,1,1}, {1,1,1,0,0,0,0},
                    {1,1,1,1,1,1,1}, {1,1,1,1,0,1,1}, {1,1,1,0,1,1,1}, {0,0,1,1,1,1,1}, {1,0,0,1,1,1,0}, {0,1,1,1,1,0,1}, {1,0,0,1,1,1,1}, {1,0,0,0,1,1,1} };
                int v = (in(7) ? 8 : 0) + (in(8) ? 4 : 0) + (in(9) ? 2 : 0) + (in(10) ? 1 : 0);
                bool on = !((flags & 1) && !in(11)) && !(v == 15 && (flags & 2));
                for (size_t i = 0; i < 7; i++) set(i, on && sym[v][i]);
                break;
            }
            case CMonostable: {
                // trigger, Q, /Q - p0 the time; flags 1: inverted trigger, 2: retriggerable; st: triggered, the time of the edge
                if (st.size() < 2) st.assign(2, 0.0);
                bool trig = in(0) != ((flags & 1) != 0);
                if (trig && !lastClock && ((flags & 2) || st[0] == 0)) {
                    st[1] = time;
                    st[0] = 1;
                    set(1, true);
                    set(2, false);
                }
                if (st[0] != 0 && time > st[1] + p[0]) {
                    set(1, false);
                    set(2, true);
                    st[0] = 0;
                }
                lastClock = trig;
                break;
            }
            case CPhaseComp: {
                // I1, I2, the level, enable (the output is open while enable is low) - st: ff1, ff2, the last I1, the last I2
                if (st.size() < 4) st.assign(4, 0.0);
                bool v1 = volts(0) > threshold, v2 = volts(1) > threshold;
                if (v1 && st[2] == 0) st[0] = 1;
                if (v2 && st[3] == 0) st[1] = 1;
                if (st[0] != 0 && st[1] != 0) st[0] = st[1] = 0;
                st[2] = v1 ? 1 : 0;
                st[3] = v2 ? 1 : 0;
                set(2, st[0] != 0);
                set(3, st[0] != 0 || st[1] != 0);
                break;
            }
            case CVco: {
                // Vi, Vo, C1, C2, R1 (held at Vi), R2 (held at the high level): the currents of R1 and R2 charge the capacitor between C1 and C2 one way
                // or the other; Vo flips when the capacitor reaches 90 % or 10 % of the high level
                double vc = volts(3) - volts(2);
                bool vo = in(1);
                int dir = vo ? -1 : 1;
                if (!vo && vc > high * 0.9) { vo = true; dir = -1; }
                if (vo && vc < high * 0.1) { vo = false; dir = 1; }
                set(1, vo);
                level[4] = volts(0);
                level[5] = high;
                double i1 = g * (volts(0) - volts(4)), i2 = g * (high - volts(5));
                double i = i1 + i2;
                level[2] = -dir * i;
                level[3] = dir * i;
                break;
            }
            default: break;
        }
        for (size_t i = 0; i < node.size(); i++)
            if (std::isnan(level[i])) level[i] = mode[i] == PinOut ? (val[i] ? high : 0.0) : 0.0;
    }
};

/// The state of a plan between two steps: what a step changes, so that a step that does not converge can be done again in smaller steps.
/// What a step changes in a part that is not linear (the rest of it - kind, nodes, parameters, the formulas - stays): kept at the start of a step for a
/// retry, small enough to copy every step.
struct NlSaved {
    double lim[2], cur[4], st[2];
    double qPrev[3], iPrev[3];
    bool started[3];
};

struct Snapshot {
    double time = 0;
    std::vector<double> x, xPrev;
    std::vector<Reactive> capacitors, inductors;
    std::vector<Coupled> coupled;
    std::vector<Source> sources;
    std::vector<char> gates;      // per gate: state, clock, primed
    std::vector<NlSaved> nonlinear;
    std::vector<Chip> chips;
};

struct Runner;

/// A stream of samples at a fixed rate between the circuit and the program (or a sound device): a ring of volts. As an output (a tap) the plan writes the
/// voltage between two nodes into it at every point of its grid k / rate - interpolated between the steps around it, also when a step was done again in
/// smaller steps; as an input (a feed) it drives a source, the voltage interpolated between the samples. It lives apart from a plan (like a recorder), so
/// the stream goes on when the circuit is compiled again.
struct SignalStream {
    std::vector<float> ring;
    size_t head = 0, count = 0;
    double rate = 44100;
    int64_t overruns = 0, underruns = 0;
    // as a tap: the next sample index and the step before (time, voltage)
    int64_t nextIndex = -1;
    double prevT = 0, prevV = 0;
    bool havePrev = false;
    // as a feed: the two samples around the time of the step (index of the first)
    int64_t feedIndex = -1;
    double feedA = 0, feedB = 0;
    bool push(float v) {
        if (ring.empty()) return false;
        if (count == ring.size()) { head = (head + 1) % ring.size(); count--; overruns++; }
        ring[(head + count) % ring.size()] = v;
        count++;
        return true;
    }
    bool pop(float& v) {
        if (count == 0) return false;
        v = ring[head];
        head = (head + 1) % ring.size();
        count--;
        return true;
    }
};

struct Tap { int stream, a, b; };
struct Feed { int stream, source; };

struct Plan {
    int nodes = 0, size = 0;
    // the thread that simulates the plan in real time (StartRunner), or none
    Runner* runner = nullptr;
    double dt = 1e-5, time = 0;
    std::vector<double> g;            // the matrix of the parts that stay the same (size x size, row major)
    std::vector<char> pattern;        // the entries that can be other than zero (also where only the non-linear parts write)
    std::vector<double> m;            // the factorized matrix (rows in pivot order)
    std::vector<int> perm, rowOf;     // perm[i]: the row of g that is row i of m; rowOf: the other way round
    std::vector<int> entries;         // the indices of m that the elimination touches (to load the numbers of a new iteration)
    std::vector<double> rhsBase, xOld;
    std::vector<Nonlinear> nonlinear;
    std::vector<Gate> gates;
    std::vector<Chip> chips;
    int64_t iterations = 0, unconverged = 0;
    int maxIterations = 100;
    // a step whose Newton's method does not converge is done again in steps of half the time step, down to dt / 2^maxHalvings
    int maxHalvings = 6;
    int64_t retried = 0;
    bool retrying = false;
    Snapshot stepStart, subStart;
    std::vector<double> rhsBegun, gFull;
    std::vector<int> group;           // nodes joined by conductances and voltage sources (0: ground, node i: i + 1)
    // the elimination as a list: factor (index of m[r][k], index of m[k][k]) and update (m[r][c] -= m[r][k] * m[k][c])
    std::vector<int> factorOps, updateOps;
    // the triangles for solving: per row the entries left (L) and right (U) of the diagonal, as (column, index of m)
    std::vector<int> lStart, lCol, lIdx, uStart, uCol, uIdx, diag;
    std::vector<double> rhs, x, y;
    // the solution of the step before (x is that of the last step): the first iteration of a step starts from x + (x - xPrev)
    std::vector<double> xPrev;
    // a step may end after one iteration when the parts confirm their linearization (linearizationHolds); how many did
    bool oneIteration = true;
    int64_t shortcuts = 0;
    std::vector<double> limSave;
    // the matrix of the parts that stay the same in the layout of m (rows in pivot order), made again when g or the order changes (gDirty)
    std::vector<double> mLinear;
    bool gDirty = true;
    std::vector<Reactive> capacitors, inductors;
    std::vector<Coupled> coupled;
    std::vector<TLine> lines;
    std::vector<Source> sources;
    std::vector<Probe> probes;
    std::vector<Tap> taps;
    std::vector<Feed> feeds;
    bool compiled = false;

    int find(int i) { while (group[i] != i) { group[i] = group[group[i]]; i = group[i]; } return i; }
    void join(int i, int j) { int a = find(i + 1), b = find(j + 1); if (a != b) group[a] = b; }
    void add(int r, int c, double v) { if (r >= 0 && c >= 0) { g[(size_t)r * size + c] += v; pattern[(size_t)r * size + c] = 1; gDirty = true; } }
    void mark(int r, int c) { if (r >= 0 && c >= 0) pattern[(size_t)r * size + c] = 1; }
    void conductance(int i, int j, double v) {
        add(i, i, v); add(j, j, v); add(i, j, -v); add(j, i, -v);
        if (v != 0) join(i, j);
    }
    double node(int i) const { return i < 0 ? 0.0 : x[(size_t)i]; }
};

struct Tables {
    std::vector<Plan*> plans{nullptr};
    std::vector<Recorder*> recorders{nullptr};
    std::vector<SignalStream*> streams{nullptr};
    ~Tables() { for (Plan* p : plans) delete p; for (Recorder* r : recorders) delete r; for (SignalStream* s : streams) delete s; }
};
inline Tables& tables() { static Tables t; return t; }

/// The program has ended (the library of the VM stays loaded for the next one): drops the plans and recordings.
inline void stopRunner(Plan* p);

inline void reset() {
    Tables& t = tables();
    for (size_t i = 1; i < t.plans.size(); i++) stopRunner(t.plans[i]);
    KernelLock lock;
    for (size_t i = 1; i < t.plans.size(); i++) { delete t.plans[i]; t.plans[i] = nullptr; }
    for (size_t i = 1; i < t.recorders.size(); i++) { delete t.recorders[i]; t.recorders[i] = nullptr; }
    for (size_t i = 1; i < t.streams.size(); i++) { delete t.streams[i]; t.streams[i] = nullptr; }
}

inline SignalStream* stream(int64_t i) {
    Tables& t = tables();
    return i > 0 && (size_t)i < t.streams.size() ? t.streams[(size_t)i] : nullptr;
}

inline Plan* plan(Value h) {
    int64_t i = h.i;
    Tables& t = tables();
    return i > 0 && (size_t)i < t.plans.size() ? t.plans[(size_t)i] : nullptr;
}
inline Recorder* recorder(Value h) {
    int64_t i = h.i;
    Tables& t = tables();
    return i > 0 && (size_t)i < t.recorders.size() ? t.recorders[(size_t)i] : nullptr;
}

// ---- plans ----

inline Value New(Value nodes, Value branches, Value dt) { KernelLock kl_;
    if (nodes.i < 0 || branches.i < 0 || D(dt) <= 0) return Int(0);
    Plan* p = new Plan();
    p->nodes = (int)nodes.i;
    p->size = (int)(nodes.i + branches.i);
    p->dt = D(dt);
    p->g.assign((size_t)p->size * p->size, 0.0);
    p->pattern.assign((size_t)p->size * p->size, 0);
    p->group.resize((size_t)p->nodes + 1);
    for (int i = 0; i <= p->nodes; i++) p->group[(size_t)i] = i;
    p->rhs.assign((size_t)p->size, 0.0);
    p->x.assign((size_t)p->size, 0.0);
    p->y.assign((size_t)p->size, 0.0);
    tables().plans.push_back(p);
    return Int((int64_t)tables().plans.size() - 1);
}

inline Value Free(Value h) {
    Plan* p;
    { KernelLock lock; p = plan(h); }
    stopRunner(p);   // (outside the lock: the thread finishes its steps under it)
    KernelLock lock;
    int64_t i = h.i;
    Tables& t = tables();
    if (i > 0 && (size_t)i < t.plans.size()) { delete t.plans[(size_t)i]; t.plans[(size_t)i] = nullptr; }
    return Undef();
}

inline Value Conductance(Value h, Value i, Value j, Value v) { KernelLock kl_;
    Plan* p = plan(h);
    if (p) p->conductance((int)i.i, (int)j.i, D(v));
    return Undef();
}

/// The entries of a voltage source between the nodes plus and minus with the branch row k.
inline Value VoltageBranch(Value h, Value k, Value plus, Value minus) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p) return Undef();
    int r = (int)k.i, i = (int)plus.i, j = (int)minus.i;
    p->add(i, r, 1.0); p->add(j, r, -1.0); p->add(r, i, 1.0); p->add(r, j, -1.0);
    p->join(i, j);
    return Undef();
}

/// A capacitor (kind 0, value in farads, state: its voltage) or an inductor (kind 1, henries, state: its current); returns its index.
inline Value AddReactive(Value h, Value kind, Value a, Value b, Value value, Value vPrev, Value iPrev) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p) return Int(-1);
    Reactive r{(int)a.i, (int)b.i, 0, D(vPrev), D(iPrev), 0};
    r.geq = kind.i == 0 ? 2.0 * D(value) / p->dt : p->dt / (2.0 * D(value));
    p->conductance(r.a, r.b, r.geq);
    std::vector<Reactive>& list = kind.i == 0 ? p->capacitors : p->inductors;
    list.push_back(r);
    return Int((int64_t)list.size() - 1);
}

/// Two coupled inductors (a transformer, see Coupled): l1, l2 in henries, k the coupling (below 1), with their state at the start; returns its index.
inline Value AddCoupled(Value h, Value a1, Value b1, Value a2, Value b2, Value l1, Value l2, Value k, Value i1, Value i2, Value v1, Value v2) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p) return Int(-1);
    double L1 = D(l1), L2 = D(l2), kk = D(k);
    if (L1 <= 0 || L2 <= 0) { lastError() = "A transformer needs inductances above zero."; return Int(-1); }
    if (kk < 0) kk = 0;
    if (kk > 0.999999) kk = 0.999999;   // (k = 1 has no inverse)
    double m = kk * std::sqrt(L1 * L2), det = L1 * L2 - m * m, f = p->dt / 2.0 / det;
    Coupled c{(int)a1.i, (int)b1.i, (int)a2.i, (int)b2.i, f * L2, -f * m, f * L1, D(v1), D(v2), D(i1), D(i2), 0, 0};
    p->conductance(c.a1, c.b1, c.g11);
    p->conductance(c.a2, c.b2, c.g22);
    // the current of each winding depends on the voltage of the other
    p->add(c.a1, c.a2, c.g12); p->add(c.a1, c.b2, -c.g12); p->add(c.b1, c.a2, -c.g12); p->add(c.b1, c.b2, c.g12);
    p->add(c.a2, c.a1, c.g12); p->add(c.a2, c.b1, -c.g12); p->add(c.b2, c.a1, -c.g12); p->add(c.b2, c.b1, c.g12);
    p->coupled.push_back(c);
    return Int((int64_t)p->coupled.size() - 1);
}

/// What a logic part has beyond AddGate: the lower threshold of a Schmitt trigger, the falling edge of a JK flip-flop.
inline Value GateExtra(Value h, Value index, Value low, Value falling) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->gates.size()) return Bool(false);
    Gate& g = p->gates[(size_t)index.i];
    g.low = D(low);
    g.falling = falling.i != 0;
    return Bool(true);
}

/// A source: branch >= 0 for a voltage source (its entries come with VoltageBranch), -1 for a current source from a through it to b; wave 0 DC, 1 sine, 2 square.
inline Value AddSource(Value h, Value a, Value b, Value branch, Value wave, Value p0, Value p1, Value p2, Value p3) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p) return Int(-1);
    p->sources.push_back(Source{(int)a.i, (int)b.i, (int)branch.i, (int)wave.i, D(p0), D(p1), D(p2), D(p3)});
    return Int((int64_t)p->sources.size() - 1);
}

/// New values for a source (Level, Amplitude, ...): no new plan needed.
inline Value SetSource(Value h, Value index, Value p0, Value p1, Value p2, Value p3) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->sources.size()) return Bool(false);
    Source& s = p->sources[(size_t)index.i];
    s.p0 = D(p0); s.p1 = D(p1); s.p2 = D(p2); s.p3 = D(p3);
    return Bool(true);
}

/// A part that is not linear (NlKind) on the nodes n0, n1, n2 with its numbers; returns its index.
inline Value AddNonlinear(Value h, Value kind, Value n0, Value n1, Value n2, Value p0, Value p1, Value p2, Value p3) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p) return Int(-1);
    Nonlinear e{};
    e.kind = (int)kind.i;
    bool two = kind.i == NlDiode || isVarRes((int)kind.i);
    bool reads = kind.i == NlScr || kind.i == NlTriac;   // two terminals, and the gate it only reads
    e.node[0] = (int)n0.i; e.node[1] = (int)n1.i; e.node[2] = two && !reads ? -1 : (int)n2.i;
    e.terminals = two ? 2 : 3;
    if (kind.i == NlOpAmp && (D(p0) <= 0 || D(p2) <= D(p1) || D(p3) <= 0)) { lastError() = "An op amp needs a gain, rails and an output resistance above zero."; return Int(-1); }
    e.p[0] = D(p0); e.p[1] = D(p1); e.p[2] = D(p2); e.p[3] = D(p3);
    if (e.kind == NlNmos || e.kind == NlPmos) e.lim[0] = 0;
    if (e.kind == NlLamp) { e.st[0] = 300; e.st[1] = lampResistance(e.p[0], e.p[1], 300); }
    if (e.kind == NlMemristor) e.st[1] = e.p[1];
    for (int a = 0; a < e.terminals; a++)
        for (int b = 0; b < e.terminals; b++) p->mark(e.node[a], e.node[b]);
    // (the junctions join their nodes for the tiny conductance to ground: through a junction a node is not hanging in the air; the output of an op amp is
    // tied to ground through its output stage, its inputs are not)
    if (e.kind == NlOpAmp) p->join(e.node[2], -1);
    else if (e.kind == NlAnalogSwitch) p->join(e.node[0], e.node[1]);
    else for (int a = 1; a < e.terminals; a++) p->join(e.node[0], e.node[a]);
    p->nonlinear.push_back(e);
    return Int((int64_t)p->nonlinear.size() - 1);
}

/// A vacuum tube (NlVacuumDiode, NlTriode, NlPentode) on its nodes (diode: plate, cathode; triode: plate, grid, cathode; pentode: plate, screen, grid,
/// cathode - unused ones -2) with its parameters as text ("mu=100 ex=1.4 kg1=1060 ..."): k ex for a diode; mu ex kg1 kp kvb rgi for a triode; mu ex kg1
/// kg2 kp kvb rgi for a pentode; and the capacitances between the electrodes (diode cpk; triode ccg cgp ccp; pentode cin cgp cout). Returns its index,
/// -1 with LastError.
inline Value AddTube(Value h, Value kind, Value n0, Value n1, Value n2, Value n3, Value parameters) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p) return Int(-1);
    std::vector<std::pair<std::string, double>> params;
    std::string error;
    if (!parseParameters(toUtf8(parameters), params, error)) { lastError() = error; return Int(-1); }
    auto get = [&](const char* name, double fallback) {
        for (const auto& prm : params) {
            std::string n = prm.first;
            for (auto& c : n) c = (char)std::tolower((unsigned char)c);
            if (n == name) return prm.second;
        }
        return fallback;
    };
    Nonlinear e{};
    e.kind = (int)kind.i;
    int nodes[4] = {(int)n0.i, (int)n1.i, (int)n2.i, (int)n3.i};
    auto cap = [&](int a, int b, double c) {
        if (c <= 0) return;
        auto& j = e.junction[e.junctions++];
        j.a = a; j.b = b; j.cj0 = c; j.vj = 1; j.m = 0; j.tt = 0;
    };
    if (e.kind == NlVacuumDiode) {
        e.terminals = 2;
        e.p[0] = get("k", 0.001); e.p[1] = get("ex", 1.5);
        cap(0, 1, get("cpk", 0));
    } else if (e.kind == NlTriode) {
        e.terminals = 3;
        e.p[0] = get("mu", 100); e.p[1] = get("ex", 1.4); e.p[2] = get("kg1", 1060); e.p[3] = get("kp", 600); e.p[4] = get("kvb", 300); e.p[5] = get("rgi", 2000);
        cap(1, 2, get("ccg", 0)); cap(1, 0, get("cgp", 0)); cap(0, 2, get("ccp", 0));
    } else if (e.kind == NlPentode) {
        e.terminals = 4;
        e.p[0] = get("mu", 11); e.p[1] = get("ex", 1.35); e.p[2] = get("kg1", 650); e.p[3] = get("kg2", 4200); e.p[4] = get("kp", 60); e.p[5] = get("kvb", 24);
        e.p[6] = get("rgi", 1000);
        cap(2, 3, get("cin", 0)); cap(2, 0, get("cgp", 0)); cap(0, 3, get("cout", 0));
    } else { lastError() = "Not a kind of tube."; return Int(-1); }
    int count = e.kind == NlVacuumDiode ? 2 : e.kind == NlTriode ? 6 : 7;
    for (int a = 0; a < count; a++)
        if (!(e.p[a] > 0)) { lastError() = "The parameters of a tube must be above zero."; return Int(-1); }
    for (int a = 0; a < e.terminals; a++) e.node[a] = nodes[a];
    for (int a = e.terminals; a < 4; a++) e.node[a] = -1;
    for (int a = 0; a < e.terminals; a++)
        for (int b = 0; b < e.terminals; b++) p->mark(e.node[a], e.node[b]);
    for (int a = 1; a < e.terminals; a++) p->join(e.node[0], e.node[a]);
    p->nonlinear.push_back(e);
    return Int((int64_t)p->nonlinear.size() - 1);
}

/// A logic part (GateKind) with its inputs (an array of nodes), its outputs, the high level, the threshold and the output resistance; returns its index.
inline Value AddGate(Value h, Value kind, Value inputs, Value out, Value outNot, Value high, Value threshold, Value rout) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || inputs.kind != K_Array || D(rout) <= 0) return Int(-1);
    Gate gate;
    gate.kind = (int)kind.i;
    Arr* a = arrOf(inputs);
    for (uint32_t i = 0; i < a->length; i++) gate.inputs.push_back((int)a->items()[i].i);
    gate.out = (int)out.i;
    gate.outNot = (int)outNot.i;
    gate.high = D(high);
    gate.threshold = D(threshold);
    gate.g = 1.0 / D(rout);
    p->conductance(gate.out, -1, gate.g);
    if (gate.outNot >= 0) p->conductance(gate.outNot, -1, gate.g);
    p->gates.push_back(gate);
    return Int((int64_t)p->gates.size() - 1);
}

/// A chip (see Chip): its kind, the nodes of its pins in Falstad's order and how each is used (0 input, 1 output of a level, 2 output of a current),
/// the high level, the threshold of the inputs, the resistance of the outputs, bits, flags and four parameters; returns its index.
inline Value AddChip(Value h, Value kind, Value nodes, Value modes, Value high, Value threshold, Value rout, Value bits, Value flags, Value p0, Value p1) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || nodes.kind != K_Array || modes.kind != K_Array || D(rout) <= 0) return Int(-1);
    Arr* a = arrOf(nodes);
    Arr* m = arrOf(modes);
    if (a->length != m->length) return Int(-1);
    Chip c;
    c.kind = (int)kind.i;
    c.bits = (int)bits.i;
    c.flags = (int)flags.i;
    c.high = D(high);
    c.threshold = D(threshold);
    c.g = 1.0 / D(rout);
    c.p[0] = D(p0);
    c.p[1] = D(p1);
    for (uint32_t i = 0; i < a->length; i++) {
        c.node.push_back((int)a->items()[i].i);
        c.mode.push_back((char)m->items()[i].i);
        if (c.mode.back() == PinOut) p->conductance(c.node.back(), -1, c.g);
    }
    c.val.assign(c.node.size(), 0);
    c.level.assign(c.node.size(), 0.0);
    p->chips.push_back(c);
    return Int((int64_t)p->chips.size() - 1);
}

/// Reads a chip: what 0 the logic value of pin k, 1 the state value k, 2 the number of state values, 3 the level (volts or amperes) pin k drives.
inline Value ChipGet(Value h, Value index, Value what, Value k) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->chips.size()) return Float(0);
    const Chip& c = p->chips[(size_t)index.i];
    size_t i = (size_t)k.i;
    switch (what.i) {
        case 0: return Float(i < c.val.size() && c.val[i] ? 1 : 0);
        case 1: return Float(i < c.st.size() ? (Real)c.st[i] : 0);
        case 2: return Float((Real)c.st.size());
        case 3: return Float(i < c.level.size() ? (Real)c.level[i] : 0);
        case 4: return Float(c.lastClock ? 1 : 0);
        case 5: return Float(c.lastLoad ? 1 : 0);
        default: return Float(0);
    }
}

/// Sets what ChipGet reads (0 the logic value of pin k, 1 the state value k, 4 the last clock, 5 the last load): the state a chip had before the circuit
/// was compiled again.
inline Value ChipSet(Value h, Value index, Value what, Value k, Value value) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->chips.size()) return Bool(false);
    Chip& c = p->chips[(size_t)index.i];
    size_t i = (size_t)k.i;
    double v = D(value);
    if (what.i == 0 && i < c.val.size()) c.val[i] = v != 0 ? 1 : 0;
    else if (what.i == 1) { if (i >= c.st.size()) c.st.resize(i + 1, 0.0); c.st[i] = v; }
    else if (what.i == 4) c.lastClock = v != 0;
    else if (what.i == 5) c.lastLoad = v != 0;
    else return Bool(false);
    return Bool(true);
}

/// The output of a logic part (true: high).
inline Value GateState(Value h, Value index) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->gates.size()) return Bool(false);
    return Bool(p->gates[(size_t)index.i].state);
}

/// The capacitances of a part that is not linear: cjA for the diode, the base-emitter junction or gate-source; cjB for base-collector or gate-drain;
/// vj and m of the depletion (m = 0: linear, the MOSFETs); tt the transit time of the diode or the forward one of the bipolar transistor, tr the
/// reverse one (the diffusion charges; tr makes the storage time of a saturated transistor).
inline Value SetCaps(Value h, Value index, Value cjA, Value cjB, Value vj, Value m, Value tt, Value tr) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->nonlinear.size()) return Bool(false);
    Nonlinear& e = p->nonlinear[(size_t)index.i];
    double a = D(cjA), b = D(cjB), t = D(tt);
    e.junctions = 0;
    auto add = [&](int ta, int tb, double cj, double transit, double mm) {
        if (cj <= 0 && transit <= 0) return;
        auto& j = e.junction[e.junctions++];
        j.a = ta; j.b = tb; j.cj0 = cj; j.vj = D(vj) > 0 ? D(vj) : 0.7; j.m = mm; j.tt = transit;
    };
    if (e.kind == NlDiode) add(0, 1, a, t, D(m));
    else if (e.kind == NlNpn || e.kind == NlPnp) { add(1, 2, a, t, D(m)); add(1, 0, b, D(tr), D(m)); }
    else if (e.kind == NlNmos || e.kind == NlPmos) { add(1, 2, a, 0, 0); add(1, 0, b, 0, 0); }
    return Bool(true);
}

/// A part given by formulas: the current into its first terminal as a function of V (two terminals: V = V(a) - V(b)), or the currents into the first and
/// the second terminal as functions of V1 and V2 (three terminals: against the third); `parameters`: "name=value ...". Returns its index, -1 with LastError.
inline Value AddFormula(Value h, Value n0, Value n1, Value n2, Value current1, Value current2, Value parameters) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p) return Int(-1);
    std::vector<std::pair<std::string, double>> params;
    std::string error;
    if (!parseParameters(toUtf8(parameters), params, error)) { lastError() = error; return Int(-1); }
    bool three = n2.i != -2;
    auto compile = [&](const std::string& text, std::shared_ptr<FormulaProgram>& into) {
        FormulaCompiler c(text, params, three);
        bool ok = c.expression();
        c.skip();
        if (ok && c.pos < text.size()) ok = c.fail("Unexpected text");
        if (!ok) { lastError() = c.error; return false; }
        if (stackDepth(c.out) > 60) { lastError() = "The formula is too deeply nested: '" + text + "'"; return false; }
        into = std::make_shared<FormulaProgram>(c.out);
        return true;
    };
    Nonlinear e{};
    e.kind = NlFormula;
    e.terminals = three ? 3 : 2;
    e.node[0] = (int)n0.i; e.node[1] = (int)n1.i; e.node[2] = three ? (int)n2.i : -1;
    if (!compile(toUtf8(current1), e.f1)) return Int(-1);
    if (three) {
        std::string second = toUtf8(current2);
        if (!second.empty() && !compile(second, e.f2)) return Int(-1);
    }
    for (int a = 0; a < e.terminals; a++)
        for (int b = 0; b < e.terminals; b++) p->mark(e.node[a], e.node[b]);
    for (int a = 1; a < e.terminals; a++) p->join(e.node[0], e.node[a]);
    p->nonlinear.push_back(e);
    return Int((int64_t)p->nonlinear.size() - 1);
}

/// Checks a formula without a circuit: "" when it compiles, else the message.
inline Value CheckFormula(Value text, Value parameters, Value threeTerminals, OwnList* list) { KernelLock kl_;
    std::vector<std::pair<std::string, double>> params;
    std::string error, t = toUtf8(text);
    if (parseParameters(toUtf8(parameters), params, error)) {
        FormulaCompiler c(t, params, threeTerminals.i != 0);
        bool ok = c.expression();
        c.skip();
        if (ok && c.pos < t.size()) c.fail("Unexpected text");
        error = c.error;
    }
    Str* s = allocStr((uint32_t)error.size(), list);
    for (size_t i = 0; i < error.size(); i++) strChars(s)[i] = (char16_t)(uint8_t)error[i];
    return StrV(s);
}

/// The value of a formula (two terminals: at V = v1; three: at V1, V2) - for tests and plots of a model.
inline Value EvalFormula(Value text, Value parameters, Value v1, Value v2) { KernelLock kl_;
    std::vector<std::pair<std::string, double>> params;
    std::string error, t = toUtf8(text);
    if (!parseParameters(toUtf8(parameters), params, error)) return Float(0);
    bool three = t.find("V1") != std::string::npos || t.find("V2") != std::string::npos;
    FormulaCompiler c(t, params, three);
    if (!c.expression()) return Float(0);
    return Float((Real)evaluate(c.out, D(v1), D(v2), 0).v);
}

/// Sets the state of a part that follows a state (what 4 or 5: st[0], st[1]) - its state from the plan before, or a reset; what 6: the node it reads
/// besides its terminals (aux).
inline Value NlSet(Value h, Value index, Value what, Value value) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->nonlinear.size() || what.i < 4 || what.i > 6) return Bool(false);
    Nonlinear& e = p->nonlinear[(size_t)index.i];
    if (what.i == 6) e.aux = (int)D(value); else e.st[what.i - 4] = D(value);
    return Bool(true);
}

/// Adds `value` to the entry (row, col) of the matrix of the parts that stay the same (-1: ground, nothing) - the controlled sources of fire use it.
inline Value Entry(Value h, Value row, Value col, Value value) { KernelLock kl_;
    Plan* p = plan(h);
    if (p && row.i < p->size && col.i < p->size) p->add((int)row.i, (int)col.i, D(value));
    return Undef();
}

/// The currents into the terminals of a part that is not linear after the last step (what: terminal 0..2), the voltage of a diode (what 3), the state of
/// a part that follows one (what 4 and 5).
inline Value NlState(Value h, Value index, Value what) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->nonlinear.size()) return Float(0);
    const Nonlinear& e = p->nonlinear[(size_t)index.i];
    if (what.i >= 0 && what.i < 3) return Float((Real)e.cur[what.i]);
    if (what.i == 4 || what.i == 5) return Float((Real)e.st[what.i - 4]);
    if (what.i == 7) return Float((Real)e.cur[3]);
    return Float((Real)(p->node(e.node[0]) - p->node(e.node[1])));
}

/// Runs the list of operations on m (the list keeps the order of the elimination: each factor is followed by the updates that use it).
inline void eliminate(Plan* p) {
    double* m = p->m.data();
    const int* f = p->factorOps.data();
    const int* u = p->updateOps.data();
    const size_t fn = p->factorOps.size(), un = p->updateOps.size();
    size_t ui = 0;
    for (size_t fi = 0; fi < fn; fi += 2) {
        const int rk = f[fi];
        m[rk] /= m[f[fi + 1]];
        const double factor = m[rk];
        while (ui < un && u[ui + 1] == rk) { m[u[ui]] -= factor * m[u[ui + 2]]; ui += 3; }
    }
}

/// The order of the pivots for the matrix `a` (rows as in g): Gaussian elimination with partial pivoting on a copy; then the elimination in that order as a
/// list of operations on the entries of the pattern (and the ones that fill in), and the triangles for solving.
inline bool order(Plan* p, std::vector<double> a) {
    const int n = p->size;
    std::vector<int> perm((size_t)n);
    for (int i = 0; i < n; i++) perm[(size_t)i] = i;
    for (int k = 0; k < n; k++) {
        int best = k;
        double bestValue = std::fabs(a[(size_t)k * n + k]);
        for (int r = k + 1; r < n; r++) {
            double v = std::fabs(a[(size_t)r * n + k]);
            if (v > bestValue) { best = r; bestValue = v; }
        }
        if (bestValue == 0.0) return false;
        if (best != k) {
            for (int c = 0; c < n; c++) std::swap(a[(size_t)k * n + c], a[(size_t)best * n + c]);
            std::swap(perm[(size_t)k], perm[(size_t)best]);
        }
        double d = a[(size_t)k * n + k];
        for (int r = k + 1; r < n; r++) {
            double f = a[(size_t)r * n + k];
            if (f == 0.0) continue;
            f /= d;
            a[(size_t)r * n + k] = f;
            for (int c = k + 1; c < n; c++) a[(size_t)r * n + c] -= f * a[(size_t)k * n + c];
        }
    }
    p->perm = perm;
    p->gDirty = true;
    p->rowOf.assign((size_t)n, 0);
    for (int i = 0; i < n; i++) p->rowOf[(size_t)perm[(size_t)i]] = i;
    std::vector<char> nz((size_t)n * n, 0);
    for (int i = 0; i < n; i++)
        for (int c = 0; c < n; c++) nz[(size_t)i * n + c] = p->pattern[(size_t)perm[(size_t)i] * n + c];
    p->factorOps.clear();
    p->updateOps.clear();
    std::vector<int> rowCols;
    for (int k = 0; k < n; k++) {
        rowCols.clear();
        for (int c = k + 1; c < n; c++) if (nz[(size_t)k * n + c]) rowCols.push_back(c);
        for (int r = k + 1; r < n; r++) {
            if (!nz[(size_t)r * n + k]) continue;
            p->factorOps.push_back(r * n + k);
            p->factorOps.push_back(k * n + k);
            for (int c : rowCols) {
                nz[(size_t)r * n + c] = 1;
                p->updateOps.push_back(r * n + c);
                p->updateOps.push_back(r * n + k);
                p->updateOps.push_back(k * n + c);
            }
        }
    }
    p->lStart.assign((size_t)n + 1, 0); p->uStart.assign((size_t)n + 1, 0);
    p->lCol.clear(); p->lIdx.clear(); p->uCol.clear(); p->uIdx.clear(); p->entries.clear();
    p->diag.assign((size_t)n, 0);
    for (int i = 0; i < n; i++) {
        p->lStart[(size_t)i] = (int)p->lCol.size();
        p->uStart[(size_t)i] = (int)p->uCol.size();
        for (int c = 0; c < n; c++) {
            if (!nz[(size_t)i * n + c] && c != i) continue;
            p->entries.push_back(i * n + c);
            if (c < i) { p->lCol.push_back(c); p->lIdx.push_back(i * n + c); }
            else if (c > i) { p->uCol.push_back(c); p->uIdx.push_back(i * n + c); }
        }
        p->diag[(size_t)i] = i * n + i;
    }
    p->lStart[(size_t)n] = (int)p->lCol.size();
    p->uStart[(size_t)n] = (int)p->uCol.size();
    p->m.assign((size_t)n * n, 0.0);
    return true;
}

/// Loads the numbers of the parts that stay the same into m (rows in pivot order); only the entries of the pattern.
inline void load(Plan* p) {
    const int n = p->size;
    double* m = p->m.data();
    if (p->gDirty || p->mLinear.size() != p->m.size()) {
        p->mLinear.assign(p->m.size(), 0.0);
        for (int idx : p->entries) {
            int i = idx / n, c = idx % n;
            p->mLinear[(size_t)idx] = p->g[(size_t)p->perm[(size_t)i] * n + c];
        }
        p->gDirty = false;
    }
    const double* lin = p->mLinear.data();
    for (int idx : p->entries) m[idx] = lin[idx];
}

inline bool pivotsOk(Plan* p) {
    for (int d : p->diag) {
        double v = p->m[(size_t)d];
        if (!(std::fabs(v) > 1e-300) || !std::isfinite(v)) return false;
    }
    return true;
}

/// Writes the linearization of the parts that are not linear into m (via rowOf) - or, with `dense`, into a matrix with the rows of g - and into rhs.
inline void stampNonlinear(Plan* p, std::vector<double>* dense) {
    const int n = p->size;
    for (Nonlinear& e : p->nonlinear) {
        linearize(e, p->x);
        if (e.junctions > 0) stampCharges(e, p->dt);
        for (int a = 0; a < e.terminals; a++) {
            int r = e.node[a];
            if (r < 0) continue;
            double constant = e.i0[a];
            for (int b = 0; b < e.terminals; b++) {
                constant -= e.j[a][b] * e.v0[b];
                int c = e.node[b];
                if (c < 0) continue;
                if (dense) (*dense)[(size_t)r * n + c] += e.j[a][b];
                else p->m[(size_t)p->rowOf[(size_t)r] * n + c] += e.j[a][b];
            }
            p->rhs[(size_t)r] -= constant;
        }
    }
}

/// Makes the plan: a tiny conductance for nodes without a path to ground, the order of the pivots, the list of operations, the factorization.
/// True, or false with LastError when the circuit cannot be solved.
inline Value Compile(Value h, Value time) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p) { lastError() = "Invalid plan."; return Bool(false); }
    p->time = D(time);
    const int n = p->size;
    int ground = p->find(0);
    // a node with no path to ground gets a tiny conductance to it - relative to what it has (next to a closed switch of 1 MS, 1e-12 S would vanish)
    for (int i = 0; i < p->nodes; i++)
        if (p->find(i + 1) != ground) p->add(i, i, std::max(1e-12, std::fabs(p->g[(size_t)i * n + i]) * 1e-9));
    for (int i = 0; i < n; i++) p->pattern[(size_t)i * n + i] = 1;
    // the pivots are chosen with the parts that are not linear standing in as conductances of 1 mS between their terminals and from each terminal to
    // ground (without the ground ties, an op amp whose input is only fed by a current source would make the stand-in singular)
    std::vector<double> a = p->g;
    for (const Nonlinear& e : p->nonlinear)
        for (int x = 0; x < e.terminals; x++)
            for (int y = 0; y < e.terminals; y++) {
                int r = e.node[x], c = e.node[y];
                if (r >= 0 && c >= 0) a[(size_t)r * n + c] += x == y ? 1e-3 * e.terminals : -1e-3;
            }
    const char* unsolvable = "The circuit cannot be solved (a loop of voltage sources, or a source without a path).";
    if (!order(p, a)) { lastError() = unsolvable; return Bool(false); }
    p->rhsBase.assign((size_t)n, 0.0);
    p->xOld.assign((size_t)n, 0.0);
    // the outputs of the logic parts start at their level (a counter must not see the outputs come up as edges at power-on); the voltages the circuit
    // had before a change are set after this (SetNode)
    for (const Gate& gate : p->gates) {
        int node = gate.state ? gate.out : gate.outNot;
        if (node >= 0) p->x[(size_t)node] = gate.high;
    }
    for (const Chip& c : p->chips)
        for (size_t i = 0; i < c.node.size(); i++)
            if (c.mode[i] == PinOut && c.val[i] && c.node[i] >= 0) p->x[(size_t)c.node[i]] = c.high;
    if (p->nonlinear.empty()) {
        load(p);
        eliminate(p);
        if (!pivotsOk(p)) { lastError() = unsolvable; return Bool(false); }
    }
    p->xPrev.clear();
    p->compiled = true;
    return Bool(true);
}

inline void solve(Plan* p) {
    const int n = p->size;
    const double* m = p->m.data();
    double* y = p->y.data();
    for (int i = 0; i < n; i++) {
        double s = p->rhs[(size_t)p->perm[(size_t)i]];
        for (int e = p->lStart[(size_t)i]; e < p->lStart[(size_t)i + 1]; e++) s -= m[p->lIdx[(size_t)e]] * y[p->lCol[(size_t)e]];
        y[i] = s;
    }
    double* x = p->x.data();
    for (int i = n - 1; i >= 0; i--) {
        double s = y[i];
        for (int e = p->uStart[(size_t)i]; e < p->uStart[(size_t)i + 1]; e++) s -= m[p->uIdx[(size_t)e]] * x[p->uCol[(size_t)e]];
        x[i] = s / m[p->diag[(size_t)i]];
    }
}

inline bool canRetry(const Plan* p) { return !p->nonlinear.empty() && p->lines.empty() && p->maxHalvings > 0 && !p->retrying; }

inline void save(const Plan* p, Snapshot& s) {
    s.time = p->time;
    s.x = p->x;
    s.capacitors = p->capacitors;
    s.inductors = p->inductors;
    s.coupled = p->coupled;
    s.sources = p->sources;
    s.gates.resize(p->gates.size() * 3);
    for (size_t i = 0; i < p->gates.size(); i++) {
        s.gates[i * 3] = p->gates[i].state; s.gates[i * 3 + 1] = p->gates[i].clock; s.gates[i * 3 + 2] = p->gates[i].primed;
    }
    s.nonlinear.resize(p->nonlinear.size());
    for (size_t i = 0; i < p->nonlinear.size(); i++) {
        const Nonlinear& e = p->nonlinear[i];
        NlSaved& v = s.nonlinear[i];
        v.lim[0] = e.lim[0]; v.lim[1] = e.lim[1];
        for (int k = 0; k < 4; k++) v.cur[k] = e.cur[k];
        v.st[0] = e.st[0]; v.st[1] = e.st[1];
        for (int k = 0; k < e.junctions; k++) { v.qPrev[k] = e.junction[k].qPrev; v.iPrev[k] = e.junction[k].iPrev; v.started[k] = e.junction[k].started; }
    }
    s.xPrev = p->xPrev;
    if (!p->chips.empty()) s.chips = p->chips;
}

inline void restore(Plan* p, const Snapshot& s) {
    p->time = s.time;
    p->x = s.x;
    p->capacitors = s.capacitors;
    p->inductors = s.inductors;
    p->coupled = s.coupled;
    p->sources = s.sources;
    for (size_t i = 0; i < p->gates.size(); i++) {
        p->gates[i].state = s.gates[i * 3] != 0; p->gates[i].clock = s.gates[i * 3 + 1] != 0; p->gates[i].primed = s.gates[i * 3 + 2] != 0;
    }
    for (size_t i = 0; i < p->nonlinear.size() && i < s.nonlinear.size(); i++) {
        Nonlinear& e = p->nonlinear[i];
        const NlSaved& v = s.nonlinear[i];
        e.lim[0] = v.lim[0]; e.lim[1] = v.lim[1];
        for (int k = 0; k < 4; k++) e.cur[k] = v.cur[k];
        e.st[0] = v.st[0]; e.st[1] = v.st[1];
        for (int k = 0; k < e.junctions; k++) { e.junction[k].qPrev = v.qPrev[k]; e.junction[k].iPrev = v.iPrev[k]; e.junction[k].started = v.started[k]; }
    }
    p->xPrev = s.xPrev;
    if (!p->chips.empty()) p->chips = s.chips;
}

/// Sets the time step to h for the steps of a retry: the conductances of the capacitors and inductors (made for `full`, as in stepStart) change with it.
inline void setDt(Plan* p, double h, double full) {
    if (p->dt == h) return;
    p->dt = h;
    p->g = p->gFull;
    p->gDirty = true;
    double f = full / h;
    auto cond = [p](int i, int j, double v) { p->add(i, i, v); p->add(j, j, v); p->add(i, j, -v); p->add(j, i, -v); };
    for (size_t i = 0; i < p->capacitors.size(); i++) {
        double g0 = p->stepStart.capacitors[i].geq;
        p->capacitors[i].geq = g0 * f;
        cond(p->capacitors[i].a, p->capacitors[i].b, g0 * f - g0);
    }
    for (size_t i = 0; i < p->inductors.size(); i++) {
        double g0 = p->stepStart.inductors[i].geq;
        p->inductors[i].geq = g0 / f;
        cond(p->inductors[i].a, p->inductors[i].b, g0 / f - g0);
    }
    for (size_t i = 0; i < p->coupled.size(); i++) {
        const Coupled& c0 = p->stepStart.coupled[i];
        Coupled& c = p->coupled[i];
        c.g11 = c0.g11 / f; c.g12 = c0.g12 / f; c.g22 = c0.g22 / f;
        cond(c.a1, c.b1, c.g11 - c0.g11);
        cond(c.a2, c.b2, c.g22 - c0.g22);
        double d = c.g12 - c0.g12;
        p->add(c.a1, c.a2, d); p->add(c.a1, c.b2, -d); p->add(c.b1, c.a2, -d); p->add(c.b1, c.b2, d);
        p->add(c.a2, c.a1, d); p->add(c.a2, c.b1, -d); p->add(c.b2, c.a1, -d); p->add(c.b2, c.b1, d);
    }
}

inline void begin(Plan* p) {
    if (canRetry(p)) save(p, p->stepStart);
    std::fill(p->rhs.begin(), p->rhs.end(), 0.0);
    double t = p->time + p->dt;
    auto into = [p](int node, double amps) { if (node >= 0) p->rhs[(size_t)node] += amps; };
    for (Reactive& c : p->capacitors) { c.ieq = c.geq * c.vPrev + c.iPrev; into(c.a, c.ieq); into(c.b, -c.ieq); }
    for (Reactive& l : p->inductors) { l.ieq = l.iPrev + l.geq * l.vPrev; into(l.a, -l.ieq); into(l.b, l.ieq); }
    for (Coupled& c : p->coupled) {
        c.h1 = c.i1Prev + c.g11 * c.v1Prev + c.g12 * c.v2Prev;
        c.h2 = c.i2Prev + c.g12 * c.v1Prev + c.g22 * c.v2Prev;
        into(c.a1, -c.h1); into(c.b1, c.h1); into(c.a2, -c.h2); into(c.b2, c.h2);
    }
    for (TLine& l : p->lines) {
        l.j1 = l.h2[(size_t)l.ptr]; l.j2 = l.h1[(size_t)l.ptr];
        into(l.a1, l.j1); into(l.b1, -l.j1); into(l.a2, l.j2); into(l.b2, -l.j2);
    }
    // the sources fed by streams: the voltage at the end of the step, between the two samples around it (a missing sample: the last one holds)
    for (const Feed& f : p->feeds) {
        SignalStream* st = stream(f.stream);
        if (!st || f.source < 0 || (size_t)f.source >= p->sources.size()) continue;
        double at = t * st->rate;
        if (st->feedIndex < 0) {
            st->feedIndex = (int64_t)std::floor(at);
            float v;
            st->feedA = st->pop(v) ? v : st->feedA;
            st->feedB = st->pop(v) ? v : st->feedA;
        }
        while ((double)(st->feedIndex + 1) <= at) {
            st->feedIndex++;
            st->feedA = st->feedB;
            float v;
            if (st->pop(v)) st->feedB = v; else st->underruns++;
        }
        double frac = at - (double)st->feedIndex;
        if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
        Source& src = p->sources[(size_t)f.source];
        src.wave = WDc;
        src.p0 = st->feedA + (st->feedB - st->feedA) * frac;
    }
    for (Source& s : p->sources) {
        if (s.wave >= WNoise) s.advance(p->dt);
        double v = s.value(t);
        if (s.branch >= 0) p->rhs[(size_t)s.branch] = v;
        else { into(s.a, -v); into(s.b, v); }
    }
    for (Gate& gate : p->gates) {
        bool high = gate.eval(p->x);
        if (gate.kind != GDFlipFlop && gate.kind != GSRLatch && gate.kind != GJKFlipFlop) gate.state = high;
        if (high) into(gate.out, gate.high * gate.g);
        else if (gate.outNot >= 0) into(gate.outNot, gate.high * gate.g);
    }
    for (Chip& c : p->chips) {
        c.execute(p->x, p->time);
        for (size_t i = 0; i < c.node.size(); i++) {
            if (c.mode[i] == PinOut) into(c.node[i], c.level[i] * c.g);
            else if (c.mode[i] == PinCurrent) into(c.node[i], c.level[i]);
        }
    }
    // (what parts in fire add between Begin and Finish is the difference to this)
    if (canRetry(p)) p->rhsBegun = p->rhs;
}

inline double probeValue(Plan* p, const Probe& pr) {
    switch (pr.kind) {
        case PrNodes: return p->node(pr.a) - p->node(pr.b);
        case PrCapacitor: return p->capacitors[(size_t)pr.a].iPrev;
        case PrInductor: return p->inductors[(size_t)pr.a].iPrev;
        case PrBranch: return -p->x[(size_t)pr.a];
        case PrConductance: return (p->node(pr.a) - p->node(pr.b)) * pr.g;
        case PrCurrentSource: return p->sources[(size_t)pr.a].value(p->time);
        case PrNonlinear: return p->nonlinear[(size_t)pr.a].cur[pr.b];
        case PrCoupled: return pr.b == 0 ? p->coupled[(size_t)pr.a].i1Prev : p->coupled[(size_t)pr.a].i2Prev;
        default: return 0.0;
    }
}

/// After the first iteration of a step (it started from the predicted voltages): does the linearization still hold at the solution? Each part that is not
/// linear is linearized again there; when its currents are those its old linearization predicted (within 1e-4 and 1 nA), the solution is that of the
/// non-linear equations, and the second iteration - load, factorize, solve only to confirm it - is saved. The convergence check of SPICE on the currents
/// of the devices. When it fails, the limits of the junctions are as before (the next iteration limits as it would have).
inline bool linearizationHolds(Plan* p) {
    bool holds = true;
    double* saved = p->limSave.data();
    for (size_t k = 0; k < p->nonlinear.size(); k++) {
        Nonlinear& e = p->nonlinear[k];
        saved[2 * k] = e.lim[0]; saved[2 * k + 1] = e.lim[1];
        if (!holds) continue;
        double want[4];
        for (int a = 0; a < e.terminals; a++) {
            double c = e.i0[a];
            for (int b = 0; b < e.terminals; b++) c += e.j[a][b] * (p->node(e.node[b]) - e.v0[b]);
            want[a] = c;
        }
        linearize(e, p->x);
        if (e.junctions > 0) stampCharges(e, p->dt);
        if (e.limited) { holds = false; continue; }
        for (int a = 0; a < e.terminals; a++) {
            double d = std::fabs(e.i0[a] - want[a]);
            if (!(d <= 1e-9 + 1e-4 * std::max(std::fabs(e.i0[a]), std::fabs(want[a])))) { holds = false; break; }
        }
    }
    if (!holds)
        for (size_t k = 0; k < p->nonlinear.size(); k++) { p->nonlinear[k].lim[0] = saved[2 * k]; p->nonlinear[k].lim[1] = saved[2 * k + 1]; }
    return holds;
}

/// Newton's method for a step with parts that are not linear: load, linearize, factorize, solve - until the node voltages stop moving.
inline bool newton(Plan* p) {
    const int n = p->size;
    p->rhsBase = p->rhs;
    for (Nonlinear& e : p->nonlinear) {
        if (e.kind == NlFormula) e.p[0] = p->time + p->dt;
        else if (isVarRes(e.kind)) {
            double gate = e.kind == NlScr ? p->node(e.node[2]) - p->node(e.aux) : e.kind == NlTriac ? p->node(e.node[2]) - p->node(e.node[1]) : 0;
            stepState(e, p->node(e.node[0]) - p->node(e.node[1]), e.cur[0], p->dt, gate);
        }
    }
    // the first iteration starts where the step before points to (x + (x - x before)): Newton's method needs fewer iterations while the voltages move
    // (sound); not in the half steps of a retry (another time step) and not right after the plan was made or a node was set
    bool predicted = !p->retrying && p->xPrev.size() == (size_t)n;
    if (predicted) {
        double* x = p->x.data();
        double* xp = p->xPrev.data();
        for (int i = 0; i < n; i++) { double now = x[i]; x[i] = now + (now - xp[i]); xp[i] = now; }
    } else p->xPrev = p->x;
    p->limSave.resize(p->nonlinear.size() * 2);
    bool converged = false;
    for (int it = 0; it < p->maxIterations && !converged; it++) {
        p->iterations++;
        p->rhs = p->rhsBase;
        load(p);
        stampNonlinear(p, nullptr);
        eliminate(p);
        if (!pivotsOk(p)) {
            // the order of the pivots does not fit the numbers of this iteration: choose it again for them
            std::vector<double> dense = p->g;
            p->rhs = p->rhsBase;
            stampNonlinear(p, &dense);
            if (!order(p, dense)) return false;
            p->rhs = p->rhsBase;
            load(p);
            stampNonlinear(p, nullptr);
            eliminate(p);
        }
        p->xOld = p->x;
        solve(p);
        // from a good prediction one iteration is often enough: the parts tell whether their linearization still holds
        if (it == 0 && predicted && p->oneIteration) {
            bool anyLimited = false;
            for (const Nonlinear& e : p->nonlinear) if (e.limited) anyLimited = true;
            if (!anyLimited && linearizationHolds(p)) { converged = true; p->shortcuts++; break; }
        }
        converged = it > 0;
        for (const Nonlinear& e : p->nonlinear) if (e.limited) converged = false;
        for (int i = 0; i < n && converged; i++) {
            double dx = std::fabs(p->x[(size_t)i] - p->xOld[(size_t)i]);
            if (!(dx <= 1e-6 + 1e-4 * std::fabs(p->x[(size_t)i]))) converged = false;
        }
    }
    p->rhs = p->rhsBase;
    for (Nonlinear& e : p->nonlinear) {
        if (e.junctions > 0) commitCharges(e, p->x, p->dt);
        // the currents at the solution (the linearization of the last iteration, at the node voltages found)
        for (int a = 0; a < e.terminals; a++) {
            double c = e.i0[a];
            for (int b = 0; b < e.terminals; b++) c += e.j[a][b] * (p->node(e.node[b]) - e.v0[b]);
            e.cur[a] = c;
        }
    }
    return converged;
}

/// After the solution of a step: the capacitors, inductors and lines take their new state, the time moves on.
inline void settle(Plan* p) {
    for (Reactive& c : p->capacitors) { double v = p->node(c.a) - p->node(c.b); c.iPrev = c.geq * v - c.ieq; c.vPrev = v; damp(c.iPrev, c.last, c.flips); }
    for (Reactive& l : p->inductors) { double v = p->node(l.a) - p->node(l.b); l.iPrev = l.geq * v + l.ieq; l.vPrev = v; damp(l.vPrev, l.last, l.flips); }
    for (Coupled& c : p->coupled) {
        double v1 = p->node(c.a1) - p->node(c.b1), v2 = p->node(c.a2) - p->node(c.b2);
        c.i1Prev = c.g11 * v1 + c.g12 * v2 + c.h1;
        c.i2Prev = c.g12 * v1 + c.g22 * v2 + c.h2;
        c.v1Prev = v1;
        c.v2Prev = v2;
    }
    for (TLine& l : p->lines) {
        double v1 = p->node(l.a1) - p->node(l.b1), v2 = p->node(l.a2) - p->node(l.b2);
        l.i1 = v1 / l.z - l.j1; l.i2 = v2 / l.z - l.j2;
        l.h1[(size_t)l.ptr] = v1 / l.z + l.i1; l.h2[(size_t)l.ptr] = v2 / l.z + l.i2;
        l.ptr = (l.ptr + 1) % l.steps;
    }
    p->time += p->dt;
}

/// The step did not converge: back to its start and again in steps of half the time step (halved again where one does not converge, and doubled
/// again up to dt / 2 after one that did). What parts in fire added to the right side stays for all of them.
inline void substeps(Plan* p) {
    p->retried++;
    const int n = p->size;
    std::vector<double> extra((size_t)n);
    for (int i = 0; i < n; i++) extra[(size_t)i] = p->rhsBase[(size_t)i] - p->rhsBegun[(size_t)i];
    const double full = p->dt, end = p->stepStart.time + full, minStep = full / (double)(1 << p->maxHalvings);
    restore(p, p->stepStart);
    p->gFull = p->g;
    p->retrying = true;
    double h = full / 2;
    while (p->time < end - full * 1e-9) {
        double step = std::min(h, end - p->time);
        setDt(p, step, full);
        save(p, p->subStart);
        begin(p);
        for (int i = 0; i < n; i++) p->rhs[(size_t)i] += extra[(size_t)i];
        bool ok = newton(p);
        if (!ok && step > minStep * 1.5) { restore(p, p->subStart); h = step / 2; continue; }
        if (!ok) p->unconverged++;
        settle(p);
        if (ok) h = std::min(full / 2, h * 2);
    }
    setDt(p, full, full);
    p->g = p->gFull;
    p->gDirty = true;
    p->time = end;
    p->retrying = false;
}

inline void finish(Plan* p) {
    bool done = false;
    if (p->nonlinear.empty()) solve(p);
    else if (!newton(p)) {
        if (canRetry(p)) { substeps(p); done = true; }
        else p->unconverged++;
    }
    if (!done) settle(p);
    Recorder* last = nullptr;
    bool full = false;
    for (const Probe& pr : p->probes) {
        Recorder* r = (size_t)pr.rec < tables().recorders.size() ? tables().recorders[(size_t)pr.rec] : nullptr;
        if (!r) continue;
        if (r != last) {
            last = r;
            full = !r->room();
            if (!full) r->times.push_back(p->time);
        }
        if (!full && (size_t)pr.channel < r->channels.size()) r->channels[(size_t)pr.channel].push_back(probeValue(p, pr));
    }
    // the taps: a sample at every point k / rate of their grid that this step passed, between the voltages before and after it
    for (const Tap& tp : p->taps) {
        SignalStream* st = stream(tp.stream);
        if (!st) continue;
        double t1 = p->time, v1 = p->node(tp.a) - p->node(tp.b);
        if (!st->havePrev || st->nextIndex < 0) {
            st->nextIndex = (int64_t)std::ceil(t1 * st->rate - 1e-9);
            st->prevT = t1; st->prevV = v1; st->havePrev = true;
        }
        while ((double)st->nextIndex / st->rate <= t1 + 1e-12) {
            double tk = (double)st->nextIndex / st->rate;
            double frac = t1 > st->prevT ? (tk - st->prevT) / (t1 - st->prevT) : 1.0;
            if (frac < 0) frac = 0;
            st->push((float)(st->prevV + (v1 - st->prevV) * frac));
            st->nextIndex++;
        }
        st->prevT = t1; st->prevV = v1;
    }
}

// ---- the runner: a plan simulated in real time by a thread of its own ----
//
// The thread keeps the time of the plan at anchorSim + (clock - anchorWall) * speed: it does the steps that are due, a few at a time under the kernel lock,
// and sleeps when it is ahead. The parts written in fire are not called by it: what they added to the right side the last time the program serviced the
// circuit (HoldBegin ... HoldEnd) is added again in every step until the next service - the simulation runs on with the last values until new ones come.
// After a pause, or when the computer cannot keep up, at most a quarter of a second of the clock is made up (Lost tells how much was given up).
struct Runner {
    double speed = 1.0;
    double anchorWall = 0, anchorSim = 0;
    std::vector<std::pair<int, double>> holdAdd, holdSet, capAdd, capSet;
    bool capturing = false;
    int64_t steps = 0;
    double lost = 0;                       // simulated seconds given up (behind by more than a quarter of a second)
    double share = 1.0, load = 0.0;        // the part of the wanted time it managed, the part of the clock it computed (over the last half second)
    double windowWall = 0, windowWant = 0, windowDone = 0, windowBusy = 0;
#ifdef CIR_THREADS
    std::thread thread;
    std::atomic<bool> quit{false};
#endif
};

inline double wallSeconds() {
#ifdef CIR_THREADS
    return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count();
#else
    return 0;
#endif
}

/// One step with what the parts in fire added at their last service.
inline void stepHeld(Plan* p) {
    begin(p);
    Runner* r = p->runner;
    if (r) {
        for (const auto& e : r->holdSet) p->rhs[(size_t)e.first] = e.second;
        for (const auto& e : r->holdAdd) p->rhs[(size_t)e.first] += e.second;
    }
    finish(p);
}

#ifdef CIR_THREADS
inline void runnerLoop(Plan* p) {
    Runner* r = p->runner;
    double lastWall = wallSeconds();
    while (!r->quit.load(std::memory_order_relaxed)) {
        double sleepFor = 0;
        {
            KernelLock lock;
            double wall = wallSeconds();
            double target = r->anchorSim + (wall - r->anchorWall) * r->speed;
            double maxBehind = 0.25 * r->speed;
            if (target - p->time > maxBehind) {
                double give = target - p->time - maxBehind;
                r->lost += give;
                r->anchorSim -= give;
                target -= give;
            }
            int64_t need = (int64_t)std::floor((target - p->time) / p->dt + 1e-9);
            double t0 = wall;
            int64_t ran = 0;
            while (need > 0 && p->compiled) {
                stepHeld(p);
                need--; ran++;
                if ((ran & 31) == 0 && wallSeconds() - t0 > 0.001) break;   // let the program in now and then
            }
            r->steps += ran;
            double now = wallSeconds();
            r->windowWant += (now - lastWall) * r->speed;
            r->windowDone += (double)ran * p->dt;
            r->windowBusy += now - t0;
            if (now - r->windowWall >= 0.5) {
                double span = now - r->windowWall;
                r->share = r->windowWant > 0 ? std::min(1.0, r->windowDone / r->windowWant) : 1.0;
                if (r->share > 0.995) r->share = 1.0;
                r->load = span > 0 ? std::min(1.0, r->windowBusy / span) : 0.0;
                r->windowWall = now; r->windowWant = r->windowDone = r->windowBusy = 0;
            }
            lastWall = now;
            if (need <= 0) {
                // ahead: until the next step is due, at most 2 ms (a change of the speed or the plan is seen soon)
                double next = (p->time + p->dt - target) / r->speed;
                sleepFor = std::min(0.002, std::max(0.0, next));
            }
        }
        if (sleepFor > 0) std::this_thread::sleep_for(std::chrono::duration<double>(sleepFor));
        else std::this_thread::yield();
    }
}
#endif

/// Stops the thread of a plan (not under the kernel lock: the thread needs it to finish its steps).
inline void stopRunner(Plan* p) {
    if (!p) return;
    Runner* r;
    { KernelLock lock; r = p->runner; }
    if (!r) return;
#ifdef CIR_THREADS
    r->quit.store(true);
    if (r->thread.joinable()) r->thread.join();
#endif
    KernelLock lock;
    p->runner = nullptr;
    delete r;
}

inline Value Begin(Value h) { KernelLock kl_; Plan* p = plan(h); if (p && p->compiled) begin(p); return Undef(); }
inline Value RhsAdd(Value h, Value row, Value amps) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || row.i < 0 || row.i >= p->size) return Undef();
    if (p->runner && p->runner->capturing) p->runner->capAdd.push_back({(int)row.i, D(amps)});
    else p->rhs[(size_t)row.i] += D(amps);
    return Undef();
}
/// Sets the right side of a row (a branch of a voltage source written in fire).
inline Value RhsSet(Value h, Value row, Value value) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || row.i < 0 || row.i >= p->size) return Undef();
    if (p->runner && p->runner->capturing) p->runner->capSet.push_back({(int)row.i, D(value)});
    else p->rhs[(size_t)row.i] = D(value);
    return Undef();
}
/// The end of a step (after Begin and what parts in fire added): solves it; returns the time after the step.
inline Value Finish(Value h) { KernelLock kl_; Plan* p = plan(h); if (p && p->compiled) finish(p); return Float(p ? (Real)p->time : (Real)0); }

/// Sends sound through the plan: `frames` frames of 16 bit samples (little endian, `channels` interleaved; stereo is mixed to mono) from `inOffset` of the
/// buffer `in` drive the source `source` (a sample of 1.0 is `amplitude` volts, linear in between), `oversample` steps per sample (the time step of the
/// plan must be 1 / (rate * oversample)); after each sample the voltage between the nodes `out` and `ref`, divided by `scale`, goes as a 16 bit sample
/// into the buffer `outBuf` from `outOffset` (clipped at full scale). Returns the number of samples clipped, -1 on an error.
inline Value Process(Value h, Value source, Value in, Value inOffset, Value frames, Value channels, Value amplitude, Value oversample, Value out, Value ref,
                     Value scale, Value outBuf, Value outOffset) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || !p->compiled || source.i < 0 || (size_t)source.i >= p->sources.size()) { lastError() = "The circuit is not compiled or the input is not in it."; return Int(-1); }
    if (!leafAlive(in) || !leafAlive(outBuf)) return destroyedError(in);
    int ch = (int)channels.i, n = (int)oversample.i;
    int64_t count = frames.i;
    if (ch < 1 || ch > 2 || n < 1 || count < 0) { lastError() = "channels 1 or 2, oversample 1 or more."; return Int(-1); }
    if (inOffset.i < 0 || inOffset.i + count * ch * 2 > (int64_t)bufOf(in)->length || outOffset.i < 0 || outOffset.i + count * 2 > (int64_t)bufOf(outBuf)->length) {
        lastError() = "The frames do not fit into the buffers.";
        return Int(-1);
    }
    const uint8_t* src = bufOf(in)->bytes() + inOffset.i;
    uint8_t* dst = bufOf(outBuf)->bytes() + outOffset.i;
    auto sample = [&](int64_t i) {
        if (i >= count) i = count - 1;
        double sum = 0;
        for (int c = 0; c < ch; c++) {
            const uint8_t* b = src + (i * ch + c) * 2;
            sum += (int16_t)(uint16_t)(b[0] | (b[1] << 8)) / 32768.0;
        }
        return sum / ch;
    };
    Source& s = p->sources[(size_t)source.i];
    double amp = D(amplitude), k = D(scale) != 0 ? 1.0 / D(scale) : 1.0;
    int outNode = (int)out.i, refNode = (int)ref.i;
    int64_t clipped = 0;
    double previous = s.fed ? s.lastSample : count > 0 ? sample(0) : 0;
    for (int64_t i = 0; i < count; i++) {
        double next = sample(i);
        for (int step = 1; step <= n; step++) {
            s.wave = WDc;
            s.p0 = amp * (previous + (next - previous) * step / n);
            begin(p);
            finish(p);
        }
        previous = next;
        s.fed = true;
        s.lastSample = next;
        double y = (p->node(outNode) - p->node(refNode)) * k;
        if (y > 1) { y = 1; clipped++; } else if (y < -1) { y = -1; clipped++; }
        int v = (int)std::lround(y * 32767);
        dst[i * 2] = (uint8_t)(v & 255);
        dst[i * 2 + 1] = (uint8_t)((v >> 8) & 255);
    }
    return Int(clipped);
}

/// `steps` time steps of a plan without parts written in fire.
inline Value Run(Value h, Value steps) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || !p->compiled) return Bool(false);
    for (int64_t i = 0; i < steps.i; i++) { begin(p); finish(p); }
    return Bool(true);
}

inline Value Time(Value h) { KernelLock kl_; Plan* p = plan(h); return Float(p ? (Real)p->time : (Real)0); }
inline Value Node(Value h, Value i) { KernelLock kl_; Plan* p = plan(h); return Float(p && i.i < p->size ? (Real)p->node((int)i.i) : (Real)0); }
/// Sets an unknown before the first step (the node voltages of the plan before, when the circuit is compiled again).
inline Value SetNode(Value h, Value i, Value v) { KernelLock kl_;
    Plan* p = plan(h);
    if (p && i.i >= 0 && i.i < p->size) { p->x[(size_t)i.i] = D(v); p->xPrev.clear(); }
    return Undef();
}
inline Value Unknown(Value h, Value k) { KernelLock kl_; Plan* p = plan(h); return Float(p && k.i >= 0 && k.i < p->size ? (Real)p->x[(size_t)k.i] : (Real)0); }

/// The state of a capacitor (kind 0) or inductor (kind 1): what 0 its voltage, 1 its current; of a transformer (kind 2): what 0 and 1 voltage and current
/// of the first winding, 2 and 3 of the second.
inline Value State(Value h, Value kind, Value index, Value what) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p) return Float(0);
    if (kind.i == 3) {
        if (index.i < 0 || (size_t)index.i >= p->lines.size()) return Float(0);
        const TLine& l = p->lines[(size_t)index.i];
        return Float((Real)(what.i == 0 ? l.i1 : l.i2));
    }
    if (kind.i == 2) {
        if (index.i < 0 || (size_t)index.i >= p->coupled.size()) return Float(0);
        const Coupled& c = p->coupled[(size_t)index.i];
        double v[4] = {c.v1Prev, c.i1Prev, c.v2Prev, c.i2Prev};
        return Float((Real)(what.i >= 0 && what.i < 4 ? v[what.i] : 0));
    }
    std::vector<Reactive>& list = kind.i == 0 ? p->capacitors : p->inductors;
    if (index.i < 0 || (size_t)index.i >= list.size()) return Float(0);
    const Reactive& r = list[(size_t)index.i];
    return Float((Real)(what.i == 0 ? r.vPrev : r.iPrev));
}

/// A transmission line between the ends (a1, b1) and (a2, b2): impedance z (ohms), delay in seconds (at least one step). Returns its index.
inline Value AddLine(Value h, Value a1, Value b1, Value a2, Value b2, Value z, Value delay) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p) return Int(-1);
    if (D(z) <= 0) { lastError() = "A transmission line needs an impedance above zero."; return Int(-1); }
    double n = std::round(D(delay) / p->dt);
    if (n > 10000000) { lastError() = "The delay of the transmission line is too long for the time step."; return Int(-1); }
    TLine l;
    l.a1 = (int)a1.i; l.b1 = (int)b1.i; l.a2 = (int)a2.i; l.b2 = (int)b2.i;
    l.z = D(z);
    l.steps = n < 1 ? 1 : (int)n;
    l.h1.assign((size_t)l.steps, 0.0); l.h2.assign((size_t)l.steps, 0.0);
    p->conductance(l.a1, l.b1, 1.0 / l.z);
    p->conductance(l.a2, l.b2, 1.0 / l.z);
    p->lines.push_back(std::move(l));
    return Int((int64_t)p->lines.size() - 1);
}

/// The value of a source at the time of the plan.
inline Value SourceValue(Value h, Value index) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->sources.size()) return Float(0);
    return Float((Real)p->sources[(size_t)index.i].value(p->time));
}

/// A probe of a scope: kind (ProbeKind), a, b, g; it records into channel `channel` of the recorder.
inline Value AddProbe(Value h, Value rec, Value channel, Value kind, Value a, Value b, Value g) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p) return Bool(false);
    p->probes.push_back(Probe{(int)rec.i, (int)channel.i, (int)kind.i, (int)a.i, (int)b.i, D(g)});
    return Bool(true);
}

inline Value LastError(OwnList* list) { KernelLock kl_;
    const std::string& e = lastError();
    Str* s = allocStr((uint32_t)e.size(), list);
    for (size_t i = 0; i < e.size(); i++) strChars(s)[i] = (char16_t)(uint8_t)e[i];
    return StrV(s);
}

/// Statistics of a plan for tests and the editor: 0 size, 1 entries of L and U, 2 update operations, 3 capacitors + inductors, 4 sources.
inline Value Info(Value h, Value what) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p) return Int(-1);
    switch (what.i) {
        case 0: return Int(p->size);
        case 1: return Int((int64_t)(p->lCol.size() + p->uCol.size() + (size_t)p->size));
        case 2: return Int((int64_t)p->updateOps.size() / 3);
        case 3: return Int((int64_t)(p->capacitors.size() + p->inductors.size() + p->coupled.size()));
        case 4: return Int((int64_t)p->sources.size());
        case 6: return Int((int64_t)(p->nonlinear.size() + p->gates.size()));
        case 7: return Int(p->iterations);
        case 8: return Int(p->unconverged);
        case 9: return Int(p->retried);
        case 10: return Int(p->shortcuts);
        default: return Int(-1);
    }
}

/// Starts the thread that simulates the plan in real time at `speed` (simulated seconds per second), from the time it has now - or, when it runs, gives it
/// the speed and starts its clock anew. False where there are no threads (the program steps the plan itself).
inline Value StartRunner(Value h, Value speed) { KernelLock kl_;
#ifdef CIR_THREADS
    Plan* p = plan(h);
    if (!p || !p->compiled || D(speed) <= 0) return Bool(false);
    bool fresh = p->runner == nullptr;
    if (fresh) p->runner = new Runner();
    Runner* r = p->runner;
    r->speed = D(speed);
    r->anchorWall = wallSeconds();
    r->anchorSim = p->time;
    r->windowWall = r->anchorWall;
    r->windowWant = r->windowDone = r->windowBusy = 0;
    if (fresh) r->thread = std::thread(runnerLoop, p);
    return Bool(true);
#else
    (void)h; (void)speed;
    return Bool(false);
#endif
}

/// Stops the thread of the plan (the plan stays where it is).
inline Value StopRunner(Value h) {
    Plan* p;
    { KernelLock lock; p = plan(h); }
    stopRunner(p);
    return Undef();
}

/// The thread of a plan: 0 runs (1/0), 1 the share of the wanted time it manages, 2 steps done, 3 the share of the clock it computes, 4 simulated seconds
/// given up because it fell behind by more than a quarter of a second.
inline Value RunnerInfo(Value h, Value what) { KernelLock kl_;
    Plan* p = plan(h);
    Runner* r = p ? p->runner : nullptr;
    switch (what.i) {
        case 0: return Int(r ? 1 : 0);
        case 1: return Float((Real)(r ? r->share : 0.0));
        case 2: return Int(r ? r->steps : 0);
        case 3: return Float((Real)(r ? r->load : 0.0));
        case 4: return Float((Real)(r ? r->lost : 0.0));
        default: return Int(-1);
    }
}

/// The parts written in fire are serviced: what they add to the right side from here (RhsAdd, RhsSet) ...
inline Value HoldBegin(Value h) { KernelLock kl_;
    Plan* p = plan(h);
    if (p && p->runner) { p->runner->capturing = true; p->runner->capAdd.clear(); p->runner->capSet.clear(); }
    return Undef();
}

/// ... until here is what the thread adds in every step from now on.
inline Value HoldEnd(Value h) { KernelLock kl_;
    Plan* p = plan(h);
    if (p && p->runner) {
        Runner* r = p->runner;
        r->capturing = false;
        r->holdAdd.swap(r->capAdd);
        r->holdSet.swap(r->capSet);
    }
    return Undef();
}

// ---- signal streams ----

/// A new stream of `capacity` samples at `rate` per second; its handle.
inline Value StreamNew(Value rate, Value capacity) { KernelLock kl_;
    if (D(rate) <= 0 || capacity.i < 1) return Int(0);
    SignalStream* st = new SignalStream();
    st->rate = D(rate);
    st->ring.assign((size_t)capacity.i, 0.0f);
    Tables& t = tables();
    t.streams.push_back(st);
    return Int((int64_t)t.streams.size() - 1);
}

inline Value StreamFree(Value h) { KernelLock kl_;
    Tables& t = tables();
    if (h.i > 0 && (size_t)h.i < t.streams.size()) { delete t.streams[(size_t)h.i]; t.streams[(size_t)h.i] = nullptr; }
    return Undef();
}

/// Writes volts into a stream (an input of the circuit): as many as fit; how many were taken.
inline Value StreamWrite(Value h, Value values) { KernelLock kl_;
    SignalStream* st = stream(h.i);
    if (!st || values.kind != K_Array) return Int(0);
    Arr* a = arrOf(values);
    int64_t n = 0;
    for (uint32_t i = 0; i < a->length && st->count < st->ring.size(); i++, n++) st->push((float)D(a->items()[i]));
    return Int(n);
}

/// Takes up to `max` samples (volts) out of a stream (an output of the circuit).
inline Value StreamRead(Value h, Value max, OwnList* list) { KernelLock kl_;
    SignalStream* st = stream(h.i);
    size_t n = st ? std::min(st->count, (size_t)std::max<int64_t>(0, max.i)) : 0;
    Arr* a = allocArr((uint32_t)n, list);
    for (size_t i = 0; i < n; i++) { float v = 0; st->pop(v); a->items()[i] = Float((Real)v); }
    return ArrV(a);
}

/// Writes 16 bit samples (little endian, `channels` interleaved and mixed to one) from `offset` of a buffer into a stream: a full-scale sample is
/// `fullScale` volts. How many frames were taken (as many as fit).
inline Value StreamWritePcm(Value h, Value buf, Value offset, Value frames, Value channels, Value fullScale) { KernelLock kl_;
    SignalStream* st = stream(h.i);
    if (!st) return Int(0);
    if (!leafAlive(buf)) return destroyedError(buf);
    int ch = (int)std::max<int64_t>(1, channels.i);
    int64_t n = frames.i;
    if (offset.i < 0 || n < 0 || offset.i + n * ch * 2 > (int64_t)bufOf(buf)->length) { lastError() = "The frames do not fit into the buffer."; return Int(-1); }
    const uint8_t* src = bufOf(buf)->bytes() + offset.i;
    double k = D(fullScale) / 32768.0;
    int64_t done = 0;
    for (; done < n && st->count < st->ring.size(); done++) {
        double sum = 0;
        for (int c = 0; c < ch; c++) { const uint8_t* b = src + (done * ch + c) * 2; sum += (int16_t)(uint16_t)(b[0] | (b[1] << 8)); }
        st->push((float)(sum / ch * k));
    }
    return Int(done);
}

/// Takes up to `maxFrames` samples out of a stream as 16 bit mono samples into a buffer from `offset` (full scale at `fullScale` volts, clipped there);
/// how many frames were written.
inline Value StreamReadPcm(Value h, Value buf, Value offset, Value maxFrames, Value fullScale) { KernelLock kl_;
    SignalStream* st = stream(h.i);
    if (!st) return Int(0);
    if (!leafAlive(buf)) return destroyedError(buf);
    int64_t room = ((int64_t)bufOf(buf)->length - offset.i) / 2;
    int64_t n = std::min<int64_t>({maxFrames.i, room, (int64_t)st->count});
    if (offset.i < 0 || n < 0) return Int(0);
    uint8_t* dst = bufOf(buf)->bytes() + offset.i;
    double k = D(fullScale) != 0 ? 32767.0 / D(fullScale) : 32767.0;
    for (int64_t i = 0; i < n; i++) {
        float v = 0;
        st->pop(v);
        double y = v * k;
        if (y > 32767) y = 32767; else if (y < -32768) y = -32768;
        int s16 = (int)std::lround(y);
        dst[i * 2] = (uint8_t)(s16 & 255);
        dst[i * 2 + 1] = (uint8_t)((s16 >> 8) & 255);
    }
    return Int(n);
}

/// A stream: 0 samples in it, 1 capacity, 2 samples lost because it was full, 3 samples missing when a source needed one, 4 rate.
inline Value StreamInfo(Value h, Value what) { KernelLock kl_;
    SignalStream* st = stream(h.i);
    if (!st) return Int(-1);
    switch (what.i) {
        case 0: return Int((int64_t)st->count);
        case 1: return Int((int64_t)st->ring.size());
        case 2: return Int(st->overruns);
        case 3: return Int(st->underruns);
        case 4: return Float((Real)st->rate);
        default: return Int(-1);
    }
}

/// Empties a stream (and its position on the grid starts anew).
inline Value StreamClear(Value h) { KernelLock kl_;
    SignalStream* st = stream(h.i);
    if (st) { st->head = st->count = 0; st->nextIndex = -1; st->havePrev = false; st->feedIndex = -1; }
    return Undef();
}

/// The plan writes the voltage between the nodes a and b (-1: ground) into the stream at the points of its grid.
inline Value AddTap(Value h, Value st, Value a, Value b) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || !stream(st.i)) return Bool(false);
    p->taps.push_back({(int)st.i, (int)a.i, (int)b.i});
    return Bool(true);
}

/// The stream drives the source `source` of the plan (its voltage, interpolated between the samples).
inline Value AddFeed(Value h, Value st, Value source) { KernelLock kl_;
    Plan* p = plan(h);
    if (!p || !stream(st.i)) return Bool(false);
    p->feeds.push_back({(int)st.i, (int)source.i});
    return Bool(true);
}

// ---- recorders ----

inline Value RecNew(Value capacity) { KernelLock kl_;
    Recorder* r = new Recorder();
    r->rolling = capacity.i < 0;
    r->capacity = capacity.i > 0 ? capacity.i : capacity.i < -1 ? -capacity.i : 100000;
    tables().recorders.push_back(r);
    return Int((int64_t)tables().recorders.size() - 1);
}
inline Value RecFree(Value h) { KernelLock kl_;
    int64_t i = h.i;
    Tables& t = tables();
    if (i > 0 && (size_t)i < t.recorders.size()) { delete t.recorders[(size_t)i]; t.recorders[(size_t)i] = nullptr; }
    return Undef();
}
inline Value RecChannel(Value h) { KernelLock kl_;
    Recorder* r = recorder(h);
    if (!r) return Int(-1);
    r->channels.emplace_back();
    return Int((int64_t)r->channels.size() - 1);
}
/// A sample that the fire side records (a channel of a part written in fire), and the time of a step that only such channels record.
inline Value RecPut(Value h, Value channel, Value v) { KernelLock kl_;
    Recorder* r = recorder(h);
    if (r && channel.i >= 0 && (size_t)channel.i < r->channels.size()) {
        std::vector<double>& c = r->channels[(size_t)channel.i];
        if (c.size() < r->times.size()) c.push_back(D(v));
    }
    return Undef();
}
inline Value RecTime(Value h, Value t) { KernelLock kl_;
    Recorder* r = recorder(h);
    if (r && r->room()) r->times.push_back(D(t));
    return Undef();
}
inline Value RecCount(Value h) { KernelLock kl_; Recorder* r = recorder(h); return Int(r ? (int64_t)r->times.size() : 0); }
inline Value RecClear(Value h) { KernelLock kl_;
    Recorder* r = recorder(h);
    if (r) { r->times.clear(); for (auto& c : r->channels) c.clear(); }
    return Undef();
}
inline Value RecLast(Value h, Value channel) { KernelLock kl_;
    Recorder* r = recorder(h);
    if (!r || channel.i < 0 || (size_t)channel.i >= r->channels.size() || r->channels[(size_t)channel.i].empty()) return Float(0);
    return Float((Real)r->channels[(size_t)channel.i].back());
}
/// The samples of a channel (-1: the times) as a float array.
inline Value RecSamples(Value h, Value channel, OwnList* list) { KernelLock kl_;
    Recorder* r = recorder(h);
    const std::vector<double>* src = nullptr;
    static const std::vector<double> none;
    if (!r) src = &none;
    else if (channel.i < 0) src = &r->times;
    else if ((size_t)channel.i < r->channels.size()) src = &r->channels[(size_t)channel.i];
    else src = &none;
    Arr* a = allocArr((uint32_t)src->size(), list);
    for (size_t i = 0; i < src->size(); i++) a->items()[i] = Float((Real)(*src)[i]);
    return ArrV(a);
}

/// The trace of a scope over `columns` columns of the screen. Channel y (times channel x when x >= 0: the power v * i). `span` <= 0: the last `columns` samples, one
/// per column; else a window of `span` seconds - with a trigger (1 rising, 2 falling through `level`) it starts at the latest crossing that leaves room for the
/// whole window, without one (or when there is no crossing) it ends now. Result: [first column with data, value of column 0, ..., of column columns-1].
inline Value RecTrace(Value h, Value chY, Value chX, Value spanV, Value columnsV, Value triggerV, Value levelV, OwnList* list) { KernelLock kl_;
    Recorder* r = recorder(h);
    int64_t columns = std::max<int64_t>(1, std::min<int64_t>(16384, columnsV.i));
    Arr* out = allocArr((uint32_t)(columns + 1), list);
    Value* o = out->items();
    o[0] = Float((Real)columns);
    for (int64_t c = 0; c < columns; c++) o[c + 1] = Float(0);
    if (!r || chY.i < 0 || (size_t)chY.i >= r->channels.size()) return ArrV(out);
    const std::vector<double>& ys = r->channels[(size_t)chY.i];
    const std::vector<double>* xs = (chX.i >= 0 && (size_t)chX.i < r->channels.size()) ? &r->channels[(size_t)chX.i] : nullptr;
    size_t count = std::min(ys.size(), r->times.size());
    if (xs) count = std::min(count, xs->size());
    if (count == 0) return ArrV(out);
    auto value = [&](size_t i) { return xs ? ys[i] * (*xs)[i] : ys[i]; };
    double span = D(spanV);
    if (span <= 0) {
        size_t n = std::min<size_t>(count, (size_t)columns);
        int64_t first = columns - (int64_t)n;
        o[0] = Float((Real)first);
        for (size_t k = 0; k < n; k++) o[first + (int64_t)k + 1] = Float((Real)value(count - n + k));
        return ArrV(out);
    }
    const std::vector<double>& times = r->times;
    double end = times[count - 1];
    double start = end - span;
    int64_t trigger = triggerV.i;
    double level = D(levelV);
    if (trigger == 1 || trigger == 2) {
        // the latest crossing at or before end - span
        for (size_t i = count - 1; i > 0; i--) {
            if (times[i] > end - span) continue;
            double a = value(i - 1), b = value(i);
            bool hit = trigger == 1 ? (a < level && b >= level) : (a > level && b <= level);
            if (hit) {
                double f = b != a ? (level - a) / (b - a) : 0.0;
                start = times[i - 1] + (times[i] - times[i - 1]) * f;
                break;
            }
        }
    }
    // each column: the sample at its time (linear between the samples)
    size_t idx = 0;
    while (idx + 1 < count && times[idx + 1] < start) idx++;
    int64_t first = columns;
    for (int64_t c = 0; c < columns; c++) {
        double tc = start + span * (double)c / (double)std::max<int64_t>(1, columns - 1);
        if (tc < times[0] || tc > end + 1e-15) continue;
        while (idx + 1 < count && times[idx + 1] < tc) idx++;
        double v;
        if (idx + 1 < count && times[idx + 1] > times[idx]) {
            double f = (tc - times[idx]) / (times[idx + 1] - times[idx]);
            f = std::max(0.0, std::min(1.0, f));
            v = value(idx) + (value(idx + 1) - value(idx)) * f;
        } else v = value(idx);
        o[c + 1] = Float((Real)v);
        if (c < first) first = c;
    }
    o[0] = Float((Real)first);
    return ArrV(out);
}

/// The last pairs of two channels for an XY display: [x0, y0, x1, y1, ...] of at most `count` samples (the newest last).
/// The scale of a trace of RecTrace: [lo, hi] - with `range` > 0 -range .. range, otherwise -m .. m with m the largest |value| from the first column with data
/// (at least ±1 for a flat trace).
inline Value TraceRange(Value traceV, Value rangeV, OwnList* list) { KernelLock kl_;
    Arr* trace = arrOf(traceV);
    int64_t columns = trace->length > 0 ? (int64_t)trace->length - 1 : 0;
    int64_t first = trace->length > 0 ? std::max<int64_t>(0, std::min<int64_t>(columns, (int64_t)D(trace->items()[0]))) : 0;
    double lo = 0, hi = 0, range = D(rangeV);
    if (range > 0) { lo = -range; hi = range; }
    else {
        for (int64_t c = first; c < columns; c++) {
            double v = D(trace->items()[c + 1]);
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        if (hi - lo < 1e-9) { hi += 1.0; lo -= 1.0; }
        double big = std::max(-lo, hi);
        lo = -big; hi = big;
    }
    Arr* out = allocArr(2, list);
    out->items()[0] = Float((Real)lo);
    out->items()[1] = Float((Real)hi);
    return ArrV(out);
}

/// A trace of RecTrace as the points of a path [x, y, x, y, ...] in a box at (x0, y0) of height h scaled to lo .. hi: column c at x0 + 1 + c, 0 in the
/// middle - what the view did in a loop of the script per column, in one call.
inline Value TracePoints(Value traceV, Value x0V, Value y0V, Value hV, Value loV, Value hiV, OwnList* list) { KernelLock kl_;
    Arr* trace = arrOf(traceV);
    int64_t columns = trace->length > 0 ? (int64_t)trace->length - 1 : 0;
    int64_t first = trace->length > 0 ? std::max<int64_t>(0, std::min<int64_t>(columns, (int64_t)D(trace->items()[0]))) : 0;
    int64_t n = columns - first;
    Arr* out = allocArr((uint32_t)(2 * n), list);
    Value* o = out->items();
    double x0 = D(x0V), y0 = D(y0V), h = D(hV), span = D(hiV) - D(loV);
    if (span == 0) span = 1;
    for (int64_t c = first; c < columns; c++) {
        int64_t k = c - first;
        o[k * 2] = Float((Real)(x0 + 1.0 + (double)c));
        o[k * 2 + 1] = Float((Real)(y0 + h / 2.0 - (D(trace->items()[c + 1]) / span * 2.0) * (h / 2.0 - 2.0)));
    }
    return ArrV(out);
}

inline Value RecPairs(Value h, Value chX, Value chY, Value countV, OwnList* list) { KernelLock kl_;
    Recorder* r = recorder(h);
    if (!r || chX.i < 0 || chY.i < 0 || (size_t)chX.i >= r->channels.size() || (size_t)chY.i >= r->channels.size()) return ArrV(allocArr(0, list));
    const std::vector<double>& xs = r->channels[(size_t)chX.i];
    const std::vector<double>& ys = r->channels[(size_t)chY.i];
    size_t n = std::min(xs.size(), ys.size());
    size_t take = std::min<size_t>(n, (size_t)std::max<int64_t>(0, countV.i));
    Arr* out = allocArr((uint32_t)(take * 2), list);
    for (size_t k = 0; k < take; k++) {
        out->items()[k * 2] = Float((Real)xs[n - take + k]);
        out->items()[k * 2 + 1] = Float((Real)ys[n - take + k]);
    }
    return ArrV(out);
}

/// Measurements of a channel (times channel x when x >= 0: the power v * i) over what the recorder holds, like the info of a scope in Falstad's simulator:
/// [count, min, max, average, rms, frequency (0: none found), duration in seconds]. Average and rms over whole periods when there are two rising crossings
/// of the middle (with a hysteresis of a tenth of peak to peak), otherwise over all samples.
inline Value RecStats(Value h, Value chY, Value chX, OwnList* list) { KernelLock kl_;
    Recorder* r = recorder(h);
    Arr* out = allocArr(7, list);
    Value* o = out->items();
    for (int i = 0; i < 7; i++) o[i] = Float(0);
    if (!r || chY.i < 0 || (size_t)chY.i >= r->channels.size()) return ArrV(out);
    const std::vector<double>& y = r->channels[(size_t)chY.i];
    const std::vector<double>* x = chX.i >= 0 && (size_t)chX.i < r->channels.size() ? &r->channels[(size_t)chX.i] : nullptr;
    size_t n = std::min(y.size(), r->times.size());
    if (x) n = std::min(n, x->size());
    if (n == 0) return ArrV(out);
    auto at = [&](size_t i) { return x ? y[i] * (*x)[i] : y[i]; };
    double lo = at(0), hi = lo;
    for (size_t i = 1; i < n; i++) { double v = at(i); lo = std::min(lo, v); hi = std::max(hi, v); }
    // rising crossings of the middle (linear between the samples), with hysteresis against noise
    double mid = (lo + hi) / 2, band = (hi - lo) / 10;
    std::vector<double> rises;
    size_t firstI = 0, lastI = 0;
    if (hi - lo > 1e-12) {
        bool below = at(0) < mid;
        for (size_t i = 1; i < n; i++) {
            double v = at(i);
            if (v < mid - band) below = true;
            else if (below && v >= mid) {
                double p = at(i - 1), f = v != p ? (mid - p) / (v - p) : 0;
                rises.push_back(r->times[i - 1] + f * (r->times[i] - r->times[i - 1]));
                if (rises.size() == 1) firstI = i;
                lastI = i;
                below = false;
            }
        }
    }
    size_t a = 0, b = n;
    double freq = 0;
    if (rises.size() >= 2 && rises.back() > rises.front()) {
        freq = (double)(rises.size() - 1) / (rises.back() - rises.front());
        a = firstI; b = lastI;
    }
    double sum = 0, sq = 0;
    for (size_t i = a; i < b; i++) { double v = at(i); sum += v; sq += v * v; }
    double cnt = (double)(b - a);
    o[0] = Float((Real)n);
    o[1] = Float((Real)lo);
    o[2] = Float((Real)hi);
    o[3] = Float((Real)(sum / cnt));
    o[4] = Float((Real)std::sqrt(sq / cnt));
    o[5] = Float((Real)freq);
    o[6] = Float((Real)(r->times[n - 1] - r->times[0]));
    return ArrV(out);
}

/// The spectrum of a channel for an FFT analyzer: the last `size` samples (a power of two, 16 .. 8192), taken at the rate 2 * fmax (so the bins reach up to
/// fmax; fmax <= 0 or a coarser simulation: the rate of the simulation), the mean taken away, a Hann window, then the FFT. Result: [bin width in Hz,
/// amplitude of bin 1, ..., amplitude of bin size/2] (amplitudes of a sine as its peak value); empty if there are too few samples.
inline Value RecSpectrum(Value h, Value channel, Value sizeV, Value fmaxV, OwnList* list) { KernelLock kl_;
    Recorder* r = recorder(h);
    const std::vector<double>* src = nullptr;
    if (r && channel.i >= 0 && (size_t)channel.i < r->channels.size()) src = &r->channels[(size_t)channel.i];
    size_t count = src ? std::min(src->size(), r->times.size()) : 0;
    size_t n = 16;
    while (n < (size_t)std::max<int64_t>(16, std::min<int64_t>(8192, sizeV.i))) n <<= 1;
    if (count < 16) return ArrV(allocArr(0, list));
    const std::vector<double>& times = r->times;
    const std::vector<double>& v = *src;
    // the step of the simulation (over the last samples) and the rate of the analysis
    size_t look = std::min<size_t>(count - 1, 64);
    double simDt = (times[count - 1] - times[count - 1 - look]) / (double)look;
    if (!(simDt > 0)) return ArrV(allocArr(0, list));
    double fmax = D(fmaxV);
    double dt = simDt;
    if (fmax > 0 && 1.0 / (2.0 * fmax) > simDt) dt = 1.0 / (2.0 * fmax);
    // resample the last n points (linear between the recorded ones); before the first sample: none
    std::vector<double> re(n, 0.0), im(n, 0.0);
    double end = times[count - 1];
    size_t idx = count - 1;
    size_t valid = 0;
    double mean = 0;
    std::vector<double> x(n, 0.0);
    std::vector<bool> has(n, false);
    for (size_t k = n; k-- > 0;) {
        double tk = end - (double)(n - 1 - k) * dt;
        while (idx > 0 && times[idx - 1] >= tk) idx--;
        if (idx == 0 && times[0] > tk) break;
        double y;
        if (times[idx] <= tk || idx == 0) y = v[idx];
        else {
            double t0 = times[idx - 1], t1 = times[idx];
            double a = t1 > t0 ? (tk - t0) / (t1 - t0) : 1.0;
            y = v[idx - 1] + (v[idx] - v[idx - 1]) * a;
        }
        x[k] = y;
        has[k] = true;
        mean += y;
        valid++;
    }
    if (valid < 16) return ArrV(allocArr(0, list));
    mean /= (double)valid;
    const double pi = 3.14159265358979323846;
    double wsum = 0;
    for (size_t k = 0; k < n; k++) {
        double w = 0.5 - 0.5 * std::cos(2.0 * pi * (double)k / (double)(n - 1));
        wsum += w;
        re[k] = has[k] ? (x[k] - mean) * w : 0.0;
    }
    // iterative radix-2 FFT
    for (size_t i = 1, j = 0; i < n; i++) {
        size_t bit = n >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) { std::swap(re[i], re[j]); std::swap(im[i], im[j]); }
    }
    for (size_t len = 2; len <= n; len <<= 1) {
        double ang = -2.0 * pi / (double)len;
        double wr = std::cos(ang), wi = std::sin(ang);
        for (size_t s = 0; s < n; s += len) {
            double cr = 1, ci = 0;
            for (size_t k = 0; k < len / 2; k++) {
                size_t a = s + k, b = s + k + len / 2;
                double br = re[b] * cr - im[b] * ci, bi = re[b] * ci + im[b] * cr;
                re[b] = re[a] - br; im[b] = im[a] - bi;
                re[a] += br; im[a] += bi;
                double ncr = cr * wr - ci * wi;
                ci = cr * wi + ci * wr;
                cr = ncr;
            }
        }
    }
    size_t half = n / 2;
    Arr* out = allocArr((uint32_t)(half + 1), list);
    out->items()[0] = Float((Real)(1.0 / (dt * (double)n)));
    double scale = wsum > 0 ? 2.0 / wsum : 0.0;
    for (size_t k = 1; k <= half; k++) out->items()[k] = Float((Real)(std::sqrt(re[k] * re[k] + im[k] * im[k]) * scale));
    return ArrV(out);
}

}  // namespace circuits
}  // namespace fire
