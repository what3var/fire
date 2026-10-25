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
#include <cmath>
#include <cstdint>
#include <string>
#include <vector>

namespace fire {
namespace circuits {

inline std::string& lastError() { static thread_local std::string e; return e; }

inline double D(Value v) { return v.kind == K_Float ? (double)v.f : (double)v.i; }

/// What a scope records: the times and one row of samples per channel; stops when `capacity` samples are there.
struct Recorder {
    int64_t capacity = 100000;
    std::vector<double> times;
    std::vector<std::vector<double>> channels;
};

enum ProbeKind { PrNodes = 0, PrCapacitor = 1, PrInductor = 2, PrBranch = 3, PrConductance = 4, PrCurrentSource = 5 };

struct Probe { int rec, channel, kind, a, b; double g; };

enum Wave { WDc = 0, WSine = 1, WSquare = 2 };

struct Source {
    int a, b, branch;      // branch >= 0: a voltage source (row of its branch), else a current source from a through it to b
    int wave;
    double p0, p1, p2, p3;  // DC: level; sine: amplitude, frequency, offset, phase; square: high, frequency, low, duty
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

struct Plan {
    int nodes = 0, size = 0;
    double dt = 1e-5, time = 0;
    std::vector<double> g;            // the matrix of the parts that stay the same (size x size, row major)
    std::vector<double> m;            // the factorized matrix (rows in pivot order)
    std::vector<int> perm;            // perm[i]: the row of g that is row i of m
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
    void add(int r, int c, double v) { if (r >= 0 && c >= 0) g[(size_t)r * size + c] += v; }
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

/// Makes the plan: a tiny conductance for nodes without a path to ground, the order of the pivots, the list of operations, the factorization.
/// True, or false with LastError when the circuit cannot be solved.
inline Value Compile(Value h, Value time) {
    Plan* p = plan(h);
    if (!p) { lastError() = "Invalid plan."; return Bool(false); }
    p->time = D(time);
    const int n = p->size;
    int ground = p->find(0);
    for (int i = 0; i < p->nodes; i++)
        if (p->find(i + 1) != ground) p->g[(size_t)i * n + i] += 1e-12;
    // 1. the order of the pivots: Gaussian elimination with partial pivoting on a copy
    std::vector<double> a = p->g;
    std::vector<int> perm((size_t)n);
    for (int i = 0; i < n; i++) perm[(size_t)i] = i;
    for (int k = 0; k < n; k++) {
        int best = k;
        double bestValue = std::fabs(a[(size_t)k * n + k]);
        for (int r = k + 1; r < n; r++) {
            double v = std::fabs(a[(size_t)r * n + k]);
            if (v > bestValue) { best = r; bestValue = v; }
        }
        if (bestValue == 0.0) { lastError() = "The circuit cannot be solved (a loop of voltage sources, or a source without a path)."; return Bool(false); }
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
    // 2. the elimination in that order as a list of operations on the entries that are not zero (and the ones that fill in)
    p->perm = perm;
    std::vector<char> nz((size_t)n * n, 0);
    for (int i = 0; i < n; i++)
        for (int c = 0; c < n; c++) nz[(size_t)i * n + c] = p->g[(size_t)perm[(size_t)i] * n + c] != 0.0;
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
    p->lCol.clear(); p->lIdx.clear(); p->uCol.clear(); p->uIdx.clear();
    p->diag.assign((size_t)n, 0);
    for (int i = 0; i < n; i++) {
        p->lStart[(size_t)i] = (int)p->lCol.size();
        p->uStart[(size_t)i] = (int)p->uCol.size();
        for (int c = 0; c < i; c++) if (nz[(size_t)i * n + c]) { p->lCol.push_back(c); p->lIdx.push_back(i * n + c); }
        for (int c = i + 1; c < n; c++) if (nz[(size_t)i * n + c]) { p->uCol.push_back(c); p->uIdx.push_back(i * n + c); }
        p->diag[(size_t)i] = i * n + i;
    }
    p->lStart[(size_t)n] = (int)p->lCol.size();
    p->uStart[(size_t)n] = (int)p->uCol.size();
    // 3. the numbers
    p->m.assign((size_t)n * n, 0.0);
    for (int i = 0; i < n; i++)
        for (int c = 0; c < n; c++) p->m[(size_t)i * n + c] = p->g[(size_t)perm[(size_t)i] * n + c];
    eliminate(p);
    for (int i = 0; i < n; i++)
        if (p->m[(size_t)i * n + i] == 0.0) { lastError() = "The circuit cannot be solved (a loop of voltage sources, or a source without a path)."; return Bool(false); }
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
}

inline double probeValue(Plan* p, const Probe& pr) {
    switch (pr.kind) {
        case PrNodes: return p->node(pr.a) - p->node(pr.b);
        case PrCapacitor: return p->capacitors[(size_t)pr.a].iPrev;
        case PrInductor: return p->inductors[(size_t)pr.a].iPrev;
        case PrBranch: return -p->x[(size_t)pr.a];
        case PrConductance: return (p->node(pr.a) - p->node(pr.b)) * pr.g;
        case PrCurrentSource: return p->sources[(size_t)pr.a].value(p->time);
        default: return 0.0;
    }
}

inline void finish(Plan* p) {
    solve(p);
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
