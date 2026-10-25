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
#include <string>
#include <vector>

namespace fire {
namespace circuits {

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
struct Recorder {
    int64_t capacity = 100000;
    std::vector<double> times;
    std::vector<std::vector<double>> channels;
};

enum ProbeKind { PrNodes = 0, PrCapacitor = 1, PrInductor = 2, PrBranch = 3, PrConductance = 4, PrCurrentSource = 5, PrNonlinear = 6 };

struct Probe { int rec, channel, kind, a, b; double g; };

enum Wave { WDc = 0, WSine = 1, WSquare = 2 };

struct Source {
    int a, b, branch;      // branch >= 0: a voltage source (row of its branch), else a current source from a through it to b
    int wave;
    double p0, p1, p2, p3;  // DC: level; sine: amplitude, frequency, offset, phase; square: high, frequency, low, duty
    bool fed = false;       // driven by Process before: the next piece of sound goes on from its last sample
    double lastSample = 0;
    double value(double t) const {
        switch (wave) {
            case WSine: return p2 + p0 * std::sin(2.0 * 3.14159265358979323846 * p1 * t + p3);
            case WSquare: {
                double cycles = t * p1;
                return cycles - std::floor(cycles) < p3 ? p0 : p2;
            }
            default: return p0;
        }
    }
};

struct Reactive { int a, b; double geq, vPrev, iPrev, ieq; };   // a capacitor (2C/dt) or an inductor (dt/2L), trapezoidal rule

enum NlKind { NlDiode = 0, NlNpn = 1, NlPnp = 2, NlNmos = 3, NlPmos = 4, NlOpAmp = 5, NlFormula = 6 };

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
    int node[3];
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
    } junction[2];
    int junctions = 0;
    double p[4];                  // diode: Is, n, breakdown voltage, current at the breakdown voltage; bipolar: Is, beta forward, beta reverse, -; MOSFET: threshold, beta (A/V^2), lambda, -;
                                  // op amp: gain, lowest output, highest output, output resistance
    double lim[2] = {0, 0};       // the junction voltages of the last iteration (limited)
    double cur[3] = {0, 0, 0};    // the currents into its terminals after the last step
    bool limited = false;         // the last linearization used a limited junction voltage (Newton's method must go on)
    // the linearization: I(V) = i0 + J (V - v0)
    double i0[3], j[3][3], v0[3];
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

/// The depletion charge of SPICE (cj0, vj, m, fc = 0.5) at the junction voltage v, and its capacitance.
inline void depletion(double cj0, double vj, double m, double v, double& q, double& c) {
    const double fc = 0.5;
    if (cj0 == 0) { q = 0; c = 0; return; }
    if (m >= 0.999) m = 0.999;
    if (v < fc * vj) {
        double r = 1 - v / vj;
        q = cj0 * vj * (1 - std::pow(r, 1 - m)) / (1 - m);
        c = cj0 * std::pow(r, -m);
    } else {
        double f1 = vj * (1 - std::pow(1 - fc, 1 - m)) / (1 - m), f2 = std::pow(1 - fc, 1 + m), f3 = 1 - fc * (1 + m);
        double v1 = fc * vj;
        q = cj0 * (f1 + (f3 * (v - v1) + m / (2 * vj) * (v * v - v1 * v1)) / f2);
        c = cj0 / f2 * (f3 + m * v / vj);
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
        depletion(j.cj0, j.vj, j.m, v, q, c);
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

/// Linearizes the part at the node voltages x (with limited junctions).
inline void linearize(Nonlinear& e, const std::vector<double>& x) {
    auto V = [&](int t) { int n = e.node[t]; return n < 0 ? 0.0 : x[(size_t)n]; };
    for (int a = 0; a < 3; a++) { e.i0[a] = 0; for (int b = 0; b < 3; b++) e.j[a][b] = 0; e.v0[a] = V(a); }
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

enum GateKind { GAnd = 0, GOr = 1, GXor = 2, GNand = 3, GNor = 4, GXnor = 5, GNot = 6, GBuffer = 7, GDFlipFlop = 8, GComparator = 9, GSRLatch = 10 };

/// A logic part: it reads its inputs after every step (high above `threshold`) and drives its outputs for the next one through `rout` (high: `high` volts,
/// low: 0 V). A D flip-flop has the inputs D and clock and the outputs Q and not Q; it takes D on the rising edge of the clock. A comparator is high
/// while its first input is above its second (analog). An SR latch has the inputs S, R and (optional) a reset that is active low; R wins over S.
struct Gate {
    int kind;
    std::vector<int> inputs;
    int out, outNot;
    double high, threshold, g;
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

struct Plan {
    int nodes = 0, size = 0;
    double dt = 1e-5, time = 0;
    std::vector<double> g;            // the matrix of the parts that stay the same (size x size, row major)
    std::vector<char> pattern;        // the entries that can be other than zero (also where only the non-linear parts write)
    std::vector<double> m;            // the factorized matrix (rows in pivot order)
    std::vector<int> perm, rowOf;     // perm[i]: the row of g that is row i of m; rowOf: the other way round
    std::vector<int> entries;         // the indices of m that the elimination touches (to load the numbers of a new iteration)
    std::vector<double> rhsBase, xOld;
    std::vector<Nonlinear> nonlinear;
    std::vector<Gate> gates;
    int64_t iterations = 0, unconverged = 0;
    int maxIterations = 100;
    std::vector<int> group;           // nodes joined by conductances and voltage sources (0: ground, node i: i + 1)
    // the elimination as a list: factor (index of m[r][k], index of m[k][k]) and update (m[r][c] -= m[r][k] * m[k][c])
    std::vector<int> factorOps, updateOps;
    // the triangles for solving: per row the entries left (L) and right (U) of the diagonal, as (column, index of m)
    std::vector<int> lStart, lCol, lIdx, uStart, uCol, uIdx, diag;
    std::vector<double> rhs, x, y;
    std::vector<Reactive> capacitors, inductors;
    std::vector<Source> sources;
    std::vector<Probe> probes;
    bool compiled = false;

    int find(int i) { while (group[i] != i) { group[i] = group[group[i]]; i = group[i]; } return i; }
    void join(int i, int j) { int a = find(i + 1), b = find(j + 1); if (a != b) group[a] = b; }
    void add(int r, int c, double v) { if (r >= 0 && c >= 0) { g[(size_t)r * size + c] += v; pattern[(size_t)r * size + c] = 1; } }
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
    ~Tables() { for (Plan* p : plans) delete p; for (Recorder* r : recorders) delete r; }
};
inline Tables& tables() { static Tables t; return t; }

/// The program has ended (the library of the VM stays loaded for the next one): drops the plans and recordings.
inline void reset() {
    Tables& t = tables();
    for (size_t i = 1; i < t.plans.size(); i++) { delete t.plans[i]; t.plans[i] = nullptr; }
    for (size_t i = 1; i < t.recorders.size(); i++) { delete t.recorders[i]; t.recorders[i] = nullptr; }
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

inline Value New(Value nodes, Value branches, Value dt) {
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
    int64_t i = h.i;
    Tables& t = tables();
    if (i > 0 && (size_t)i < t.plans.size()) { delete t.plans[(size_t)i]; t.plans[(size_t)i] = nullptr; }
    return Undef();
}

inline Value Conductance(Value h, Value i, Value j, Value v) {
    Plan* p = plan(h);
    if (p) p->conductance((int)i.i, (int)j.i, D(v));
    return Undef();
}

/// The entries of a voltage source between the nodes plus and minus with the branch row k.
inline Value VoltageBranch(Value h, Value k, Value plus, Value minus) {
    Plan* p = plan(h);
    if (!p) return Undef();
    int r = (int)k.i, i = (int)plus.i, j = (int)minus.i;
    p->add(i, r, 1.0); p->add(j, r, -1.0); p->add(r, i, 1.0); p->add(r, j, -1.0);
    p->join(i, j);
    return Undef();
}

/// A capacitor (kind 0, value in farads, state: its voltage) or an inductor (kind 1, henries, state: its current); returns its index.
inline Value AddReactive(Value h, Value kind, Value a, Value b, Value value, Value vPrev, Value iPrev) {
    Plan* p = plan(h);
    if (!p) return Int(-1);
    Reactive r{(int)a.i, (int)b.i, 0, D(vPrev), D(iPrev), 0};
    r.geq = kind.i == 0 ? 2.0 * D(value) / p->dt : p->dt / (2.0 * D(value));
    p->conductance(r.a, r.b, r.geq);
    std::vector<Reactive>& list = kind.i == 0 ? p->capacitors : p->inductors;
    list.push_back(r);
    return Int((int64_t)list.size() - 1);
}

/// A source: branch >= 0 for a voltage source (its entries come with VoltageBranch), -1 for a current source from a through it to b; wave 0 DC, 1 sine, 2 square.
inline Value AddSource(Value h, Value a, Value b, Value branch, Value wave, Value p0, Value p1, Value p2, Value p3) {
    Plan* p = plan(h);
    if (!p) return Int(-1);
    p->sources.push_back(Source{(int)a.i, (int)b.i, (int)branch.i, (int)wave.i, D(p0), D(p1), D(p2), D(p3)});
    return Int((int64_t)p->sources.size() - 1);
}

/// New values for a source (Level, Amplitude, ...): no new plan needed.
inline Value SetSource(Value h, Value index, Value p0, Value p1, Value p2, Value p3) {
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->sources.size()) return Bool(false);
    Source& s = p->sources[(size_t)index.i];
    s.p0 = D(p0); s.p1 = D(p1); s.p2 = D(p2); s.p3 = D(p3);
    return Bool(true);
}

/// A part that is not linear (NlKind) on the nodes n0, n1, n2 with its numbers; returns its index.
inline Value AddNonlinear(Value h, Value kind, Value n0, Value n1, Value n2, Value p0, Value p1, Value p2, Value p3) {
    Plan* p = plan(h);
    if (!p) return Int(-1);
    Nonlinear e{};
    e.kind = (int)kind.i;
    e.node[0] = (int)n0.i; e.node[1] = (int)n1.i; e.node[2] = kind.i == NlDiode ? -1 : (int)n2.i;
    e.terminals = kind.i == NlDiode ? 2 : 3;
    if (kind.i == NlOpAmp && (D(p0) <= 0 || D(p2) <= D(p1) || D(p3) <= 0)) { lastError() = "An op amp needs a gain, rails and an output resistance above zero."; return Int(-1); }
    e.p[0] = D(p0); e.p[1] = D(p1); e.p[2] = D(p2); e.p[3] = D(p3);
    if (e.kind == NlNmos || e.kind == NlPmos) e.lim[0] = 0;
    for (int a = 0; a < e.terminals; a++)
        for (int b = 0; b < e.terminals; b++) p->mark(e.node[a], e.node[b]);
    // (the junctions join their nodes for the tiny conductance to ground: through a junction a node is not hanging in the air; the output of an op amp is
    // tied to ground through its output stage, its inputs are not)
    if (e.kind == NlOpAmp) p->join(e.node[2], -1);
    else for (int a = 1; a < e.terminals; a++) p->join(e.node[0], e.node[a]);
    p->nonlinear.push_back(e);
    return Int((int64_t)p->nonlinear.size() - 1);
}

/// A logic part (GateKind) with its inputs (an array of nodes), its outputs, the high level, the threshold and the output resistance; returns its index.
inline Value AddGate(Value h, Value kind, Value inputs, Value out, Value outNot, Value high, Value threshold, Value rout) {
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

/// The output of a logic part (true: high).
inline Value GateState(Value h, Value index) {
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->gates.size()) return Bool(false);
    return Bool(p->gates[(size_t)index.i].state);
}

/// The capacitances of a part that is not linear: cjA for the diode, the base-emitter junction or gate-source; cjB for base-collector or gate-drain;
/// vj and m of the depletion (m = 0: linear, the MOSFETs); tt the transit time of the diode or the forward one of the bipolar transistor, tr the
/// reverse one (the diffusion charges; tr makes the storage time of a saturated transistor).
inline Value SetCaps(Value h, Value index, Value cjA, Value cjB, Value vj, Value m, Value tt, Value tr) {
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
inline Value AddFormula(Value h, Value n0, Value n1, Value n2, Value current1, Value current2, Value parameters) {
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
inline Value CheckFormula(Value text, Value parameters, Value threeTerminals, OwnList* list) {
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
inline Value EvalFormula(Value text, Value parameters, Value v1, Value v2) {
    std::vector<std::pair<std::string, double>> params;
    std::string error, t = toUtf8(text);
    if (!parseParameters(toUtf8(parameters), params, error)) return Float(0);
    bool three = t.find("V1") != std::string::npos || t.find("V2") != std::string::npos;
    FormulaCompiler c(t, params, three);
    if (!c.expression()) return Float(0);
    return Float((Real)evaluate(c.out, D(v1), D(v2), 0).v);
}

/// The currents into the terminals of a part that is not linear after the last step (what: terminal 0..2), or the voltage of a diode (what 3).
inline Value NlState(Value h, Value index, Value what) {
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->nonlinear.size()) return Float(0);
    const Nonlinear& e = p->nonlinear[(size_t)index.i];
    if (what.i >= 0 && what.i < 3) return Float((Real)e.cur[what.i]);
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
    for (int idx : p->entries) {
        int i = idx / n, c = idx % n;
        m[idx] = p->g[(size_t)p->perm[(size_t)i] * n + c];
    }
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
inline Value Compile(Value h, Value time) {
    Plan* p = plan(h);
    if (!p) { lastError() = "Invalid plan."; return Bool(false); }
    p->time = D(time);
    const int n = p->size;
    int ground = p->find(0);
    for (int i = 0; i < p->nodes; i++)
        if (p->find(i + 1) != ground) p->add(i, i, 1e-12);
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
    if (p->nonlinear.empty()) {
        load(p);
        eliminate(p);
        if (!pivotsOk(p)) { lastError() = unsolvable; return Bool(false); }
    }
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

inline void begin(Plan* p) {
    std::fill(p->rhs.begin(), p->rhs.end(), 0.0);
    double t = p->time + p->dt;
    auto into = [p](int node, double amps) { if (node >= 0) p->rhs[(size_t)node] += amps; };
    for (Reactive& c : p->capacitors) { c.ieq = c.geq * c.vPrev + c.iPrev; into(c.a, c.ieq); into(c.b, -c.ieq); }
    for (Reactive& l : p->inductors) { l.ieq = l.iPrev + l.geq * l.vPrev; into(l.a, -l.ieq); into(l.b, l.ieq); }
    for (const Source& s : p->sources) {
        double v = s.value(t);
        if (s.branch >= 0) p->rhs[(size_t)s.branch] = v;
        else { into(s.a, -v); into(s.b, v); }
    }
    for (Gate& gate : p->gates) {
        bool high = gate.eval(p->x);
        if (gate.kind != GDFlipFlop && gate.kind != GSRLatch) gate.state = high;
        if (high) into(gate.out, gate.high * gate.g);
        else if (gate.outNot >= 0) into(gate.outNot, gate.high * gate.g);
    }
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
        default: return 0.0;
    }
}

/// Newton's method for a step with parts that are not linear: load, linearize, factorize, solve - until the node voltages stop moving.
inline void newton(Plan* p) {
    const int n = p->size;
    p->rhsBase = p->rhs;
    for (Nonlinear& e : p->nonlinear) if (e.kind == NlFormula) e.p[0] = p->time + p->dt;
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
            if (!order(p, dense)) { p->unconverged++; return; }
            p->rhs = p->rhsBase;
            load(p);
            stampNonlinear(p, nullptr);
            eliminate(p);
        }
        p->xOld = p->x;
        solve(p);
        converged = it > 0;
        for (const Nonlinear& e : p->nonlinear) if (e.limited) converged = false;
        for (int i = 0; i < n && converged; i++) {
            double dx = std::fabs(p->x[(size_t)i] - p->xOld[(size_t)i]);
            if (!(dx <= 1e-6 + 1e-4 * std::fabs(p->x[(size_t)i]))) converged = false;
        }
    }
    if (!converged) p->unconverged++;
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
}

inline void finish(Plan* p) {
    if (p->nonlinear.empty()) solve(p); else newton(p);
    for (Reactive& c : p->capacitors) { double v = p->node(c.a) - p->node(c.b); c.iPrev = c.geq * v - c.ieq; c.vPrev = v; }
    for (Reactive& l : p->inductors) { double v = p->node(l.a) - p->node(l.b); l.iPrev = l.geq * v + l.ieq; l.vPrev = v; }
    p->time += p->dt;
    Recorder* last = nullptr;
    bool full = false;
    for (const Probe& pr : p->probes) {
        Recorder* r = (size_t)pr.rec < tables().recorders.size() ? tables().recorders[(size_t)pr.rec] : nullptr;
        if (!r) continue;
        if (r != last) {
            last = r;
            full = (int64_t)r->times.size() >= r->capacity;
            if (!full) r->times.push_back(p->time);
        }
        if (!full && (size_t)pr.channel < r->channels.size()) r->channels[(size_t)pr.channel].push_back(probeValue(p, pr));
    }
}

inline Value Begin(Value h) { Plan* p = plan(h); if (p && p->compiled) begin(p); return Undef(); }
inline Value RhsAdd(Value h, Value row, Value amps) {
    Plan* p = plan(h);
    if (p && row.i >= 0 && row.i < p->size) p->rhs[(size_t)row.i] += D(amps);
    return Undef();
}
/// Sets the right side of a row (a branch of a voltage source written in fire).
inline Value RhsSet(Value h, Value row, Value value) {
    Plan* p = plan(h);
    if (p && row.i >= 0 && row.i < p->size) p->rhs[(size_t)row.i] = D(value);
    return Undef();
}
inline Value Finish(Value h) { Plan* p = plan(h); if (p && p->compiled) finish(p); return Undef(); }

/// Sends sound through the plan: `frames` frames of 16 bit samples (little endian, `channels` interleaved; stereo is mixed to mono) from `inOffset` of the
/// buffer `in` drive the source `source` (a sample of 1.0 is `amplitude` volts, linear in between), `oversample` steps per sample (the time step of the
/// plan must be 1 / (rate * oversample)); after each sample the voltage between the nodes `out` and `ref`, divided by `scale`, goes as a 16 bit sample
/// into the buffer `outBuf` from `outOffset` (clipped at full scale). Returns the number of samples clipped, -1 on an error.
inline Value Process(Value h, Value source, Value in, Value inOffset, Value frames, Value channels, Value amplitude, Value oversample, Value out, Value ref,
                     Value scale, Value outBuf, Value outOffset) {
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
inline Value Run(Value h, Value steps) {
    Plan* p = plan(h);
    if (!p || !p->compiled) return Bool(false);
    for (int64_t i = 0; i < steps.i; i++) { begin(p); finish(p); }
    return Bool(true);
}

inline Value Time(Value h) { Plan* p = plan(h); return Float(p ? (Real)p->time : (Real)0); }
inline Value Node(Value h, Value i) { Plan* p = plan(h); return Float(p && i.i < p->size ? (Real)p->node((int)i.i) : (Real)0); }
/// Sets an unknown before the first step (the node voltages of the plan before, when the circuit is compiled again).
inline Value SetNode(Value h, Value i, Value v) {
    Plan* p = plan(h);
    if (p && i.i >= 0 && i.i < p->size) p->x[(size_t)i.i] = D(v);
    return Undef();
}
inline Value Unknown(Value h, Value k) { Plan* p = plan(h); return Float(p && k.i >= 0 && k.i < p->size ? (Real)p->x[(size_t)k.i] : (Real)0); }

/// The state of a capacitor (kind 0) or inductor (kind 1): what 0 its voltage, 1 its current.
inline Value State(Value h, Value kind, Value index, Value what) {
    Plan* p = plan(h);
    if (!p) return Float(0);
    std::vector<Reactive>& list = kind.i == 0 ? p->capacitors : p->inductors;
    if (index.i < 0 || (size_t)index.i >= list.size()) return Float(0);
    const Reactive& r = list[(size_t)index.i];
    return Float((Real)(what.i == 0 ? r.vPrev : r.iPrev));
}

/// The value of a source at the time of the plan.
inline Value SourceValue(Value h, Value index) {
    Plan* p = plan(h);
    if (!p || index.i < 0 || (size_t)index.i >= p->sources.size()) return Float(0);
    return Float((Real)p->sources[(size_t)index.i].value(p->time));
}

/// A probe of a scope: kind (ProbeKind), a, b, g; it records into channel `channel` of the recorder.
inline Value AddProbe(Value h, Value rec, Value channel, Value kind, Value a, Value b, Value g) {
    Plan* p = plan(h);
    if (!p) return Bool(false);
    p->probes.push_back(Probe{(int)rec.i, (int)channel.i, (int)kind.i, (int)a.i, (int)b.i, D(g)});
    return Bool(true);
}

inline Value LastError(OwnList* list) {
    const std::string& e = lastError();
    Str* s = allocStr((uint32_t)e.size(), list);
    for (size_t i = 0; i < e.size(); i++) strChars(s)[i] = (char16_t)(uint8_t)e[i];
    return StrV(s);
}

/// Statistics of a plan for tests and the editor: 0 size, 1 entries of L and U, 2 update operations, 3 capacitors + inductors, 4 sources.
inline Value Info(Value h, Value what) {
    Plan* p = plan(h);
    if (!p) return Int(-1);
    switch (what.i) {
        case 0: return Int(p->size);
        case 1: return Int((int64_t)(p->lCol.size() + p->uCol.size() + (size_t)p->size));
        case 2: return Int((int64_t)p->updateOps.size() / 3);
        case 3: return Int((int64_t)(p->capacitors.size() + p->inductors.size()));
        case 4: return Int((int64_t)p->sources.size());
        case 6: return Int((int64_t)(p->nonlinear.size() + p->gates.size()));
        case 7: return Int(p->iterations);
        case 8: return Int(p->unconverged);
        default: return Int(-1);
    }
}

// ---- recorders ----

inline Value RecNew(Value capacity) {
    Recorder* r = new Recorder();
    r->capacity = capacity.i > 0 ? capacity.i : 100000;
    tables().recorders.push_back(r);
    return Int((int64_t)tables().recorders.size() - 1);
}
inline Value RecFree(Value h) {
    int64_t i = h.i;
    Tables& t = tables();
    if (i > 0 && (size_t)i < t.recorders.size()) { delete t.recorders[(size_t)i]; t.recorders[(size_t)i] = nullptr; }
    return Undef();
}
inline Value RecChannel(Value h) {
    Recorder* r = recorder(h);
    if (!r) return Int(-1);
    r->channels.emplace_back();
    return Int((int64_t)r->channels.size() - 1);
}
/// A sample that the fire side records (a channel of a part written in fire), and the time of a step that only such channels record.
inline Value RecPut(Value h, Value channel, Value v) {
    Recorder* r = recorder(h);
    if (r && channel.i >= 0 && (size_t)channel.i < r->channels.size()) {
        std::vector<double>& c = r->channels[(size_t)channel.i];
        if (c.size() < r->times.size()) c.push_back(D(v));
    }
    return Undef();
}
inline Value RecTime(Value h, Value t) {
    Recorder* r = recorder(h);
    if (r && (int64_t)r->times.size() < r->capacity) r->times.push_back(D(t));
    return Undef();
}
inline Value RecCount(Value h) { Recorder* r = recorder(h); return Int(r ? (int64_t)r->times.size() : 0); }
inline Value RecClear(Value h) {
    Recorder* r = recorder(h);
    if (r) { r->times.clear(); for (auto& c : r->channels) c.clear(); }
    return Undef();
}
inline Value RecLast(Value h, Value channel) {
    Recorder* r = recorder(h);
    if (!r || channel.i < 0 || (size_t)channel.i >= r->channels.size() || r->channels[(size_t)channel.i].empty()) return Float(0);
    return Float((Real)r->channels[(size_t)channel.i].back());
}
/// The samples of a channel (-1: the times) as a float array.
inline Value RecSamples(Value h, Value channel, OwnList* list) {
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

}  // namespace circuits
}  // namespace fire
