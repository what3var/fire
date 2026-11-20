/* native/forge/falstadimport.c - port of src/fire.UI.Markup/FalstadImport.cs (commit 7a594769): a circuit of Paul Falstad's simulator (the
 * text of File > Export As Text, or the XML format of the newer simulator, `<cir>`, read with xml.c) as a `.biscuit` schematic. The grid of the
 * schematic is 8 pixels of Falstad's; elements the package `circuits` does not have are left out and named in the warnings. The exceptions
 * C# catches while it reads an element (FormatException, IndexOutOfRangeException, OverflowException) are FC_ERR_INTERNAL thrown and caught here. */

static const char* const fmKernelChips[] = { "TFlipFlop", "Latch", "RingCounter", "Multiplexer", "Demultiplexer", "HalfAdder", "FullAdder",
    "SipoShift", "PisoShift", "Adc", "Dac", "SevenSegmentDecoder", "Monostable", "PhaseComparator", "Vco", "SevenSegment", NULL };

typedef struct FmFalstadResult { const char* biscuit; FcList* warnings; int elements; } FmFalstadResult;

typedef struct FmFElement {
    const char* type;
    int x1, y1, x2, y2;
    const char* name;
    bool hasFix;
    int fixX, fixY;        /* the second point when the package wants another one than Falstad's (a transformer, a chip) */
    FcList* keys;          /* Props: keys and values in order */
    FcList* values;
} FmFElement;

static FmFElement* fmFNew(FcCtx* c, const char* type) {
    FmFElement* e = (FmFElement*)fcAlloc(c, sizeof *e);
    e->type = type;
    e->name = "";
    e->keys = fcListNew(c);
    e->values = fcListNew(c);
    return e;
}
static void fmFSet(FcCtx* c, FmFElement* e, const char* key, const char* value) { fcListAdd(c, e->keys, (void*)key); fcListAdd(c, e->values, (void*)value); }

/* an item: a line of the text or an element of the XML (XML: the attributes as a dictionary by local name) */
typedef struct FmFItem { const char* type; FcList* t; FcList* xmlKeys; FcList* xmlValues; int lineNo; const char* line; } FmFItem;

/* (the exceptions of int.Parse and of an index outside the tokens) */
static _Noreturn void fmFormatError(FcCtx* c) { fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "FormatException"); }

/* int.Parse(s, CultureInfo.InvariantCulture) */
static int fmIntParse(FcCtx* c, const char* s) {
    int n;
    if (!fmTryParseInt(s, &n)) fmFormatError(c);
    return n;
}
/* double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) */
static bool fmParseFloat(const char* s, double* out) {
    const char* p;
    const char* start;
    const char* end;
    bool digits = false;
    char buf[512];
    if (!s) return false;
    p = s;
    while (fmNumWhite(*p)) p++;
    start = p;
    if (*p == '+' || *p == '-') p++;
    while (*p >= '0' && *p <= '9') { p++; digits = true; }
    if (*p == '.') { p++; while (*p >= '0' && *p <= '9') { p++; digits = true; } }
    if (digits && (*p == 'e' || *p == 'E')) {
        const char* q = p + 1;
        if (*q == '+' || *q == '-') q++;
        if (*q >= '0' && *q <= '9') { while (*q >= '0' && *q <= '9') q++; p = q; }
    }
    end = p;
    while (fmNumWhite(*p)) p++;
    if (digits && !*p && (size_t)(end - start) < sizeof buf) {
        memcpy(buf, start, (size_t)(end - start));
        buf[end - start] = 0;
        *out = strtod(buf, NULL);
        return true;
    }
    {
        /* the symbols (case does not matter) */
        const char* t = s;
        size_t n;
        while (fmNumWhite(*t)) t++;
        n = strlen(t);
        while (n > 0 && fmNumWhite(t[n - 1])) n--;
        if (n < sizeof buf) {
            memcpy(buf, t, n);
            buf[n] = 0;
            if (fmIEq(buf, "Infinity") || fmIEq(buf, "+Infinity")) { *out = INFINITY; return true; }
            if (fmIEq(buf, "-Infinity")) { *out = -INFINITY; return true; }
            if (fmIEq(buf, "NaN") || fmIEq(buf, "-NaN") || fmIEq(buf, "+NaN")) { *out = NAN; return true; }
        }
    }
    return false;
}
/* (int) of a double: saturating, NaN 0 (.NET 9 and later) */
static int fmSatInt(double v) {
    if (isnan(v)) return 0;
    if (v >= 2147483647.0) return 2147483647;
    if (v <= -2147483648.0) return (-2147483647 - 1);
    return (int)v;
}
static int fmSign(int v) { return v > 0 ? 1 : v < 0 ? -1 : 0; }

/* Math.Round(v, 6) (ToEven) */
static double fmRound6(double v) {
    if (fabs(v) < 1e16) { v *= 1e6; v = nearbyint(v); v /= 1e6; }
    return v;
}

/* Format: a number with an SI prefix ("4.7k", "10u") */
static const char* fmFFormat(FcCtx* c, double v) {
    static const char* const prefixes[] = { "p", "n", "u", "m", "", "k", "M", "G" };
    static const double factors[] = { 1e-12, 1e-9, 1e-6, 1e-3, 1, 1e3, 1e6, 1e9 };
    double a;
    int k = 4, i;
    if (v == 0) return "0";
    a = fabs(v);
    for (i = 0; i < 8; i++) if (a >= factors[i] * 0.9999999) k = i;
    if (a < 1e-12) k = 0;
    return fcFmt(c, "%s%s", fmFormatCustom(c, fmRound6(v / factors[k]), 0, 6), prefixes[k]);
}

/* Json: a text as a JSON string */
static const char* fmFJson(FcCtx* c, const char* s) {
    FcBuf b = {0};
    const char* p;
    fcBufChar(&b, '"');
    for (p = s; *p; p++) {
        switch (*p) {
            case '"': fcBufStr(&b, "\\\""); break;
            case '\\': fcBufStr(&b, "\\\\"); break;
            case '\n': fcBufStr(&b, "\\n"); break;
            case '\r': fcBufStr(&b, "\\r"); break;
            case '\t': fcBufStr(&b, "\\t"); break;
            default:
                if ((unsigned char)*p < ' ') fcBufFmt(&b, "\\u%04x", (unsigned char)*p);
                else if ((unsigned char)*p == 0xC0 && (unsigned char)p[1] == 0x80) { fcBufStr(&b, "\\u0000"); p++; }
                else fcBufChar(&b, *p);
        }
    }
    fcBufChar(&b, '"');
    return fcBufDone(c, &b);
}

/* Unescape */
static const char* fmFUnescape(FcCtx* c, const char* s) {
    return fmReplace(c, fmReplace(c, fmReplace(c, fmReplace(c, fmReplace(c, s, "\\s", " "), "\\p", "+"), "%2B", "+"), "%2b", "+"), "\\\\", "\\");
}

static int fmGrid(FcCtx* c, const char* s) { return (int)nearbyint(fmIntParse(c, s) / 8.0); }
static int fmGrid8(int pixels) { return (int)nearbyint(pixels / 8.0); }
static int fmPixels(FcCtx* c, const char* s) { return fmIntParse(c, s); }

static const char* fmDescribe(const char* type) {
    static const char* const d[] = {
        "155", "D flip-flop", "165", "555 timer", "164", "counter", "j", "JFET", "A", "antenna", "169", "tapped transformer", "200", "AM source",
        "201", "FM source", "418", "external voltage", "211", "audio output", "179", "current conveyor",
        "419", "decimal display", "403", "scope", "176", "varactor", "402", "OTA", "184", "multiplexer", "167", "ADC", "166", "DAC",
        "163", "ring counter", "168", "latch", "212", "voltage-controlled voltage source", "213", "voltage-controlled current source", "175", "tunnel diode",
        "400", "Darlington", "177", "SCR", "173", "triode", "423", "line", "185", "demultiplexer", "193", "T flip-flop", "203", "diac", "206", "triac",
        "208", "custom logic", "180", "tri-state buffer", "157", "7-segment display", "197", "7-segment decoder", "194", "monostable",
        "161", "phase comparator", "158", "VCO", "195", "half adder", "196", "full adder", "189", "shift register (SIPO)", "186", "shift register (PISO)", "430", "time delay relay", "417", "unijunction", "407", "optocoupler", "405", "LED array", "370", "ammeter",
        NULL
    };
    int i;
    for (i = 0; d[i]; i += 2) if (strcmp(d[i], type) == 0) return d[i + 1];
    return "unknown";
}
static const char* fmWaveName(int wf) {
    switch (wf) { case 3: return "triangle"; case 4: return "sawtooth"; case 5: return "pulse"; case 6: return "noise"; case 7: return "variable"; default: return "special"; }
}
static const char* fmKnownDiode(const char* model) {
    static const char* const known[] = { "1N4148", "1N4001", "1N5817", "1N4733", "1N4742", NULL };
    int i;
    for (i = 0; known[i]; i++) if (fmIEq(model, known[i])) return known[i];
    return "diode";
}
static const char* fmLedColor(double r, double g, double b) {
    if (r > 0.7 && g > 0.7 && b > 0.7) return "white";
    if (b > 0.7 && r < 0.5) return "blue";
    if (r > 0.7 && g > 0.6) return "yellow";
    if (g > 0.7 && r < 0.5) return "green";
    return "red";
}
static const char* fmPrefix(const char* type) {
    static const char* const p[] = {
        "Resistor", "R", "Capacitor", "C", "Inductor", "L", "Source", "V", "Rail", "V", "CurrentSource", "I", "Diode", "D", "Zener", "D", "Led", "D",
        "Transistor", "Q", "Mosfet", "Q", "Switch", "S", "OpAmp", "U", "Comparator", "U", "SchmittTrigger", "U", "JKFlipFlop", "U", "Probe", "P",
        "Potentiometer", "RV", "Transformer", "T", "Relay", "K", "Switch2", "S", "Lamp", "LA", "Fuse", "F",
        "LightResistor", "R", "Thermistor", "R", "Memristor", "R", "SparkGap", "E", "TransmissionLine", "TL", "AnalogSwitch", "U", "AnalogSwitch2", "U",
        "Sweep", "V", "RelayCoil", "K", "RelayContact", "S", "Jfet", "Q", "Darlington", "Q", "Thyristor", "D", "Triac", "D", "Diac", "D", "Varactor", "D", "TunnelDiode", "D",
        "Triode", "V", "Unijunction", "Q", "VCVS", "U", "VCCS", "U", "CCCS", "U", "CCVS", "U", "CurrentConveyor", "U", "Ota", "U", "Optocoupler", "U",
        NULL
    };
    int i;
    for (i = 0; p[i]; i += 2) if (strcmp(p[i], type) == 0) return p[i + 1];
    return "X";
}
static const char* fmUniqueName(FcCtx* c, FcList* elements, const char* prefix) {
    int i, k;
    for (i = 1; ; i++) {
        const char* name = fcFmt(c, "%s%d", prefix, i);
        bool taken = false;
        for (k = 0; k < elements->count; k++) if (strcmp(((FmFElement*)elements->items[k])->name, name) == 0) taken = true;
        if (!taken) return name;
    }
}

/* LinearGain: the factor k of a linear expression; false: none */
static bool fmLinearGain(FcCtx* c, const char* expr, bool difference, double* gain) {
    const char* x = fmToLower(c, fmReplace(c, expr, " ", ""));
    const char* core = difference ? "(a-b)" : "a";
    size_t cl = strlen(core), xl = strlen(x);
    double d;
    if (difference && strcmp(x, "a-b") == 0) { *gain = 1; return true; }
    if (strcmp(x, core) == 0) { *gain = 1; return true; }
    if (xl > cl && strncmp(x, core, cl) == 0 && x[cl] == '*') return fmParseFloat(x + cl + 1, gain);
    if (xl > cl && strncmp(x, core, cl) == 0 && x[cl] == '/') { if (fmParseFloat(x + cl + 1, &d) && d != 0) { *gain = 1 / d; return true; } return false; }
    if (xl > cl && strcmp(x + xl - cl, core) == 0 && x[xl - cl - 1] == '*') return fmParseFloat(fcStrN(c, x, xl - cl - 1), gain);
    return false;
}

/* ChipLayout: a chip of Falstad's (sizeX 2) */
static void fmChipLayout(FcCtx* c, FmFElement* e, int flags) {
    bool small = (flags & 1) != 0;
    if (!small) fmFSet(c, e, "spacing", "4");
    e->hasFix = true;
    e->fixX = small ? 6 : 12;
    e->fixY = 0;
}

typedef struct FmMake { FcCtx* c; FcList* t; FcList* xk; FcList* xv; FcList* warnings; int lineNo; } FmMake;

/* Tok: a value - the token at its position in a line of text, the attribute of its name in the XML format */
static const char* fmTok(FmMake* m, int i, const char* attr) {
    int k;
    if (m->xk) {
        if (attr[0]) for (k = 0; k < m->xk->count; k++) if (strcmp((const char*)m->xk->items[k], attr) == 0) return (const char*)m->xv->items[k];
        return "";
    }
    return i >= 0 && i < m->t->count ? (const char*)m->t->items[i] : "";
}
static double fmNum(FmMake* m, int i, double fallback, const char* attr) {
    double v;
    return fmParseFloat(fmTok(m, i, attr), &v) ? v : fallback;
}
static bool fmXmlHas(FmMake* m, const char* attr) {
    int k;
    for (k = 0; k < m->xk->count; k++) if (strcmp((const char*)m->xk->items[k], attr) == 0) return true;
    return false;
}
static const char* fmT(FmMake* m, int i) {
    if (i < 0 || i >= m->t->count) fcThrow(m->c, FC_ERR_INTERNAL, 0, 0, NULL, "IndexOutOfRangeException");
    return (const char*)m->t->items[i];
}
static void fmWarn(FmMake* m, const char* text) { fcListAdd(m->c, m->warnings, (void*)fcFmt(m->c, "Line %d: %s", m->lineNo, text)); }

static FmFElement* fmMakeElement(FmMake* m, const char* type, int flags) {
    FcCtx* c = m->c;
    FmFElement* e;
    bool xml = m->xk != NULL;
#define FM_IS(s) (strcmp(type, s) == 0)
    if (FM_IS("w")) return fmFNew(c, "Wire");
    if (FM_IS("r")) { e = fmFNew(c, "Resistor"); fmFSet(c, e, "resistance", fmFFormat(c, fmNum(m, 6, 1000, "r"))); return e; }
    if (FM_IS("c")) { e = fmFNew(c, "Capacitor"); fmFSet(c, e, "capacitance", fmFFormat(c, fmNum(m, 6, 1e-5, "c"))); return e; }
    if (FM_IS("l")) { e = fmFNew(c, "Inductor"); fmFSet(c, e, "inductance", fmFFormat(c, fmNum(m, 6, 1, "l"))); return e; }
    if (FM_IS("v") || FM_IS("R")) {
        int wf;
        double freq, max, bias, phase, duty;
        e = fmFNew(c, FM_IS("v") ? "Source" : "Rail");
        wf = fmSatInt(fmNum(m, 6, 0, "wf"));
        freq = fmNum(m, 7, 40, "fr"); max = fmNum(m, 8, 5, "maxv"); bias = fmNum(m, 9, 0, "bias"); phase = fmNum(m, 10, 0, "phaseShift"); duty = fmNum(m, 11, 0.5, "dutyCycle");
        if (wf == 0) fmFSet(c, e, "voltage", fmFFormat(c, max));
        else if (wf == 1) {
            fmFSet(c, e, "waveform", "sine");
            fmFSet(c, e, "voltage", fmFFormat(c, max));
            fmFSet(c, e, "frequency", fmFFormat(c, freq));
            if (bias != 0) fmFSet(c, e, "offset", fmFFormat(c, bias));
            if (phase != 0) fmFSet(c, e, "phase", fmFFormat(c, phase));
        } else if (wf == 3 || wf == 4) {
            fmFSet(c, e, "waveform", wf == 3 ? "triangle" : "sawtooth");
            fmFSet(c, e, "voltage", fmFFormat(c, max));
            fmFSet(c, e, "frequency", fmFFormat(c, freq));
            if (bias != 0) fmFSet(c, e, "offset", fmFFormat(c, bias));
            if (phase != 0) fmFSet(c, e, "phase", fmFFormat(c, phase));
        } else if (wf == 6) {
            fmFSet(c, e, "waveform", "noise");
            fmFSet(c, e, "voltage", fmFFormat(c, max));
            if (bias != 0) fmFSet(c, e, "offset", fmFFormat(c, bias));
        } else if (wf == 2 || wf == 5) {
            /* a square between bias - max and bias + max; a pulse between bias and bias + max (its duty cycle) */
            fmFSet(c, e, "waveform", "square");
            fmFSet(c, e, "voltage", fmFFormat(c, bias + max));
            fmFSet(c, e, "offset", fmFFormat(c, wf == 5 ? bias : bias - max));
            fmFSet(c, e, "frequency", fmFFormat(c, freq));
            if (wf == 5 && duty != 0.5) fmFSet(c, e, "duty", fmFFormat(c, duty));
        } else {
            fmWarn(m, fcFmt(c, "a %s source is imported as DC.", fmWaveName(wf)));
            fmFSet(c, e, "voltage", fmFFormat(c, bias + max));
        }
        return e;
    }
    if (FM_IS("425")) {
        static const char* const modes[] = { "normal", "onDelay", "offDelay", "latching", "set", "reset" };
        int mode;
        e = fmFNew(c, "RelayCoil");
        fmFSet(c, e, "label", fmFUnescape(c, fmTok(m, 6, "lb")));
        fmFSet(c, e, "inductance", fmFFormat(c, fmNum(m, 7, 0.2, "in")));
        fmFSet(c, e, "onCurrent", fmFFormat(c, fmNum(m, 9, 0.02, "oc")));
        fmFSet(c, e, "coilResistance", fmFFormat(c, fmNum(m, 10, 20, "cr")));
        fmFSet(c, e, "offCurrent", fmFFormat(c, fmNum(m, 11, 0.015, "ofc")));
        mode = fmSatInt(fmNum(m, 13, 0, "tp"));
        if (mode > 0 && mode < 6) fmFSet(c, e, "mode", modes[mode]);
        if (mode == 1 || mode == 2) fmFSet(c, e, "delay", fmFFormat(c, fmNum(m, 12, 0.005, "swt")));
        return e;
    }
    if (FM_IS("426")) {
        e = fmFNew(c, "RelayContact");
        fmFSet(c, e, "label", fmFUnescape(c, fmTok(m, 6, "lb")));
        fmFSet(c, e, "onResistance", fmFFormat(c, fmNum(m, 7, 0.05, "ron")));
        fmFSet(c, e, "offResistance", fmFFormat(c, fmNum(m, 8, 1e6, "roff")));
        if ((flags & 2) != 0) fmFSet(c, e, "normallyClosed", "true");
        return e;
    }
    if (FM_IS("n")) {
        /* a rail of noise */
        e = fmFNew(c, "Rail");
        fmFSet(c, e, "waveform", "noise");
        fmFSet(c, e, "voltage", fmFFormat(c, fmNum(m, 8, 5, "maxv")));
        if (fmNum(m, 9, 0, "bias") != 0) fmFSet(c, e, "offset", fmFFormat(c, fmNum(m, 9, 0, "bias")));
        return e;
    }
    if (FM_IS("172")) {
        /* a rail with a slider: the voltage it was set to */
        e = fmFNew(c, "Rail");
        fmFSet(c, e, "voltage", fmFFormat(c, fmNum(m, 7, 5, "fr")));
        return e;
    }
    if (FM_IS("170")) {
        e = fmFNew(c, "Sweep");
        fmFSet(c, e, "minFrequency", fmFFormat(c, fmNum(m, 6, 20, "mi")));
        fmFSet(c, e, "maxFrequency", fmFFormat(c, fmNum(m, 7, 4000, "ma")));
        fmFSet(c, e, "amplitude", fmFFormat(c, fmNum(m, 8, 5, "mv")));
        fmFSet(c, e, "sweepTime", fmFFormat(c, fmNum(m, 9, 0.1, "sw")));
        if ((flags & 1) != 0) fmFSet(c, e, "logarithmic", "true");
        if ((flags & 2) == 0) fmFSet(c, e, "bidirectional", "false");
        return e;
    }
    if (FM_IS("S")) {
        int pos, throws, link;
        e = fmFNew(c, "Switch2");
        pos = fmSatInt(fmNum(m, 6, 0, "p"));
        throws = fmSatInt(fmNum(m, 9, 2, "th"));
        if (pos != 0) fmFSet(c, e, "position", fcFmt(c, "%d", pos));
        if (throws != 2) fmFSet(c, e, "throws", fcFmt(c, "%d", throws));
        if ((flags & 1) != 0) fmFSet(c, e, "centerOff", "true");
        link = fmSatInt(fmNum(m, 8, 0, "li"));
        if (link != 0) fmFSet(c, e, "link", fcFmt(c, "%d", link));
        if (strcmp(fmTok(m, 7, "mm"), "true") == 0) fmWarn(m, "a momentary two-way switch is imported as a normal one.");
        return e;
    }
    if (FM_IS("181")) {
        e = fmFNew(c, "Lamp");
        fmFSet(c, e, "power", fmFFormat(c, fmNum(m, 7, 100, "np")));
        fmFSet(c, e, "voltage", fmFFormat(c, fmNum(m, 8, 120, "nv")));
        fmFSet(c, e, "warmTime", fmFFormat(c, fmNum(m, 9, 0.4, "wa")));
        fmFSet(c, e, "coolTime", fmFFormat(c, fmNum(m, 10, 0.4, "co")));
        return e;
    }
    if (FM_IS("404")) {
        e = fmFNew(c, "Fuse");
        fmFSet(c, e, "resistance", fmFFormat(c, fmNum(m, 6, 0.0613, "re")));
        fmFSet(c, e, "i2t", fmFFormat(c, fmNum(m, 7, 6.73, "i2")));
        return e;
    }
    if (FM_IS("m")) {
        e = fmFNew(c, "Memristor");
        fmFSet(c, e, "onResistance", fmFFormat(c, fmNum(m, 6, 100, "ron")));
        fmFSet(c, e, "offResistance", fmFFormat(c, fmNum(m, 7, 16000, "rof")));
        fmFSet(c, e, "width", fmFFormat(c, fmNum(m, 9, 1e-8, "to")));
        fmFSet(c, e, "mobility", fmFFormat(c, fmNum(m, 10, 1e-10, "mo")));
        return e;
    }
    if (FM_IS("187")) {
        e = fmFNew(c, "SparkGap");
        fmFSet(c, e, "onResistance", fmFFormat(c, fmNum(m, 6, 1000, "on")));
        fmFSet(c, e, "offResistance", fmFFormat(c, fmNum(m, 7, 1e9, "of")));
        fmFSet(c, e, "breakdown", fmFFormat(c, fmNum(m, 8, 1000, "br")));
        fmFSet(c, e, "holdCurrent", fmFFormat(c, fmNum(m, 9, 0.001, "ho")));
        return e;
    }
    if (FM_IS("374")) {
        /* Falstad's light: 0.1 + 10000 * position lux */
        e = fmFNew(c, "LightResistor");
        fmFSet(c, e, "lux", fmFFormat(c, 10000 * fmNum(m, 6, 0.5, "ps") + 0.1));
        return e;
    }
    if (FM_IS("350")) {
        /* R25, R50 (the B value between them), the range of the slider and its position */
        double r25 = fmNum(m, 6, 10000, "r25"), r50 = fmNum(m, 7, 3588, "r50"), lo = fmNum(m, 8, -40, "mnt"), hi = fmNum(m, 9, 150, "mxt"), pos = fmNum(m, 10, 0.34, "ps");
        double beta = (log(r25) - log(r50)) / (1 / 298.15 - 1 / 323.15);
        e = fmFNew(c, "Thermistor");
        fmFSet(c, e, "resistance25", fmFFormat(c, r25));
        fmFSet(c, e, "beta", fmFFormat(c, nearbyint(beta)));
        fmFSet(c, e, "temperature", fmFFormat(c, nearbyint(pos * (hi - lo) + lo)));
        return e;
    }
    if (FM_IS("209")) { e = fmFNew(c, "Capacitor"); fmFSet(c, e, "capacitance", fmFFormat(c, fmNum(m, 6, 1e-5, "c"))); return e; }
    if (FM_IS("171")) {
        int w;
        e = fmFNew(c, "TransmissionLine");
        fmFSet(c, e, "delay", fmFFormat(c, fmNum(m, 6, 1e-6, "de")));
        fmFSet(c, e, "impedance", fmFFormat(c, fmNum(m, 7, 75, "im")));
        w = fmGrid8(fmSatInt(fmNum(m, 8, 32, "wi")));
        if (w != 4 && w > 0) fmFSet(c, e, "width", fcFmt(c, "%d", w));
        return e;
    }
    if (FM_IS("159") || FM_IS("160")) {
        int flips;
        e = fmFNew(c, FM_IS("159") ? "AnalogSwitch" : "AnalogSwitch2");
        if (xml ? fmXmlHas(m, "ron") && fmXmlHas(m, "roff") : m->t->count > 7) {
            fmFSet(c, e, "onResistance", fmFFormat(c, fmNum(m, 6, 20, "ron")));
            fmFSet(c, e, "offResistance", fmFFormat(c, fmNum(m, 7, 1e10, "roff")));
        }
        if (xml ? fmXmlHas(m, "th") : m->t->count > 8) fmFSet(c, e, "threshold", fmFFormat(c, fmNum(m, 8, 2.5, "th")));
        if ((flags & 1) != 0) { if (FM_IS("159")) fmFSet(c, e, "inverting", "true"); else fmFSet(c, e, "swap", "true"); }
        flips = ((flags & 4) != 0 ? 1 : 0) + ((flags & 8) != 0 ? 1 : 0) + ((flags & 16) != 0 ? 1 : 0);
        if (flips % 2 == 1) fmFSet(c, e, "flip", "true");
        return e;
    }
    if (FM_IS("i")) { e = fmFNew(c, "CurrentSource"); fmFSet(c, e, "current", fmFFormat(c, fmNum(m, 6, 0.01, "cu"))); return e; }
    if (FM_IS("g")) return fmFNew(c, "Ground");
    if (FM_IS("s")) {
        const char* pos = fmTok(m, 6, "p");
        e = fmFNew(c, "Switch");
        if (strcmp(pos, "true") == 0 || strcmp(pos, "0") == 0) fmFSet(c, e, "closed", "true");
        if (strcmp(fmTok(m, 7, "mm"), "true") == 0) fmFSet(c, e, "momentary", "true");
        return e;
    }
    if (FM_IS("d")) {
        const char* model = (flags & 2) != 0 ? fmTok(m, 6, "mo") : "";
        e = fmFNew(c, "Diode");
        fmFSet(c, e, "model", fmKnownDiode(model));
        return e;
    }
    if (FM_IS("z")) { e = fmFNew(c, "Zener"); fmFSet(c, e, "voltage", fmFFormat(c, (flags & 2) != 0 ? 5.6 : fmNum(m, (flags & 1) != 0 ? 7 : 6, 5.6, ""))); return e; }
    if (FM_IS("162")) {
        /* after the flags: (a model name with flag 2, a forward drop with flag 1), then R G B of the colour */
        int at = (flags & 3) != 0 ? 7 : 6;
        double r = fmNum(m, at, 1, "cr"), gg = fmNum(m, at + 1, 0, "cg"), b = fmNum(m, at + 2, 0, "cb");
        e = fmFNew(c, "Led");
        fmFSet(c, e, "color", fmLedColor(r, gg, b));
        return e;
    }
    if (FM_IS("t")) {
        e = fmFNew(c, "Transistor");
        fmFSet(c, e, "model", strcmp(fmTok(m, 6, "pn"), "-1") == 0 ? "pnp" : "npn");
        if ((flags & 1) != 0) fmFSet(c, e, "flip", "true");
        return e;
    }
    if (FM_IS("f")) {
        e = fmFNew(c, "Mosfet");
        fmFSet(c, e, "model", (flags & 1) != 0 ? "pmos" : "nmos");
        if ((flags & 8) != 0) fmFSet(c, e, "flip", "true");
        return e;
    }
    if (FM_IS("a")) {
        e = fmFNew(c, "OpAmp");
        fmFSet(c, e, "model", "opamp");
        if ((flags & 1) != 0) fmFSet(c, e, "flip", "true");
        if ((flags & 2) != 0) fmWarn(m, "a small op amp has its inputs 1 unit from the middle in Falstad's simulator; check its connections.");
        return e;
    }
    if (FM_IS("401")) { e = fmFNew(c, "Comparator"); if ((flags & 1) != 0) fmFSet(c, e, "flip", "true"); return e; }
    if (FM_IS("150") || FM_IS("151") || FM_IS("152") || FM_IS("153") || FM_IS("154")) {
        const char* gate = FM_IS("150") ? "And" : FM_IS("151") ? "Nand" : FM_IS("152") ? "Or" : FM_IS("153") ? "Nor" : "Xor";
        int inputs;
        e = fmFNew(c, gate);
        inputs = fmSatInt(fmNum(m, 6, 2, "in"));
        if (inputs != 2) fmFSet(c, e, "inputs", fcFmt(c, "%d", inputs));
        if ((flags & 1) != 0) fmWarn(m, "a small gate has its inputs closer together in Falstad's simulator; check its connections.");
        return e;
    }
    if (FM_IS("I")) return fmFNew(c, "Not");
    if (FM_IS("L")) {
        const char* pos = fmTok(m, 6, "p");
        double hi;
        e = fmFNew(c, "LogicInput");
        if (strcmp(pos, "1") == 0 || strcmp(pos, "true") == 0) fmFSet(c, e, "high", "true");
        hi = fmNum(m, 8, 5, "hi");
        if (hi != 5) fmFSet(c, e, "supply", fmFFormat(c, hi));
        return e;
    }
    if (FM_IS("M")) { double th; e = fmFNew(c, "LogicOutput"); th = fmNum(m, 6, 2.5, "th"); if (th != 2.5) fmFSet(c, e, "threshold", fmFFormat(c, th)); return e; }
    if (FM_IS("p")) {
        /* what the probe shows (Falstad's meter: 0 voltage, 1 rms, 2 max, 3 min, 4 peak to peak, 6 frequency) */
        int me = fmSatInt(fmNum(m, 6, 0, "me"));
        const char* measure = me == 1 ? "rms" : me == 2 ? "max" : me == 3 ? "min" : me == 4 ? "peakToPeak" : me == 6 ? "frequency" : "";
        e = fmFNew(c, "Probe");
        if (measure[0]) fmFSet(c, e, "measure", measure);
        return e;
    }
    if (FM_IS("207") || FM_IS("x")) {
        int from = FM_IS("207") ? 6 : 7;
        e = fmFNew(c, FM_IS("207") ? "Label" : "Text");
        if (xml) fmFSet(c, e, "text", fmTok(m, from, "te"));
        else {
            FcBuf b = {0};
            int k;
            fcBufStr(&b, "");
            for (k = from; k < m->t->count; k++) { if (k > from) fcBufChar(&b, ' '); fcBufStr(&b, (const char*)m->t->items[k]); }
            fmFSet(c, e, "text", fmFUnescape(c, fcBufDone(c, &b)));
        }
        return e;
    }
    if (FM_IS("174")) {
        /* the points stay: the second one says where the wiper is (as in the package) */
        double pos;
        const char* link;
        e = fmFNew(c, "Potentiometer");
        fmFSet(c, e, "resistance", fmFFormat(c, fmNum(m, 6, 1000, "ma")));
        pos = fmNum(m, 7, 0.5, "po");
        if (pos != 0.5) fmFSet(c, e, "position", fmFFormat(c, pos));
        /* pots linked in the XML format turn together: a group of the package */
        link = xml ? fmTok(m, 0, "li") : "";
        if (link[0] && strcmp(link, "0") != 0) fmFSet(c, e, "group", fcFmt(c, "link%s", link));
        return e;
    }
    if (FM_IS("T")) {
        double ratio, k;
        int x1, y1, x2, y2, width, dsF, flip, ex, ey, dsO, w;
        bool vertical;
        e = fmFNew(c, "Transformer");
        fmFSet(c, e, "inductance", fmFFormat(c, fmNum(m, 6, 4, "in")));
        ratio = fmNum(m, 7, 1, "ra");
        if (ratio != 1) fmFSet(c, e, "ratio", fmFFormat(c, ratio));
        k = fmNum(m, 10, 0.999, "co");
        if (k != 0.999) fmFSet(c, e, "coupling", fmFFormat(c, k));
        if ((flags & 4) != 0) fmFSet(c, e, "reverse", "true");
        if (fmNum(m, 11, 0, "isat") > 0) fmWarn(m, "the saturation of a transformer is not simulated.");
        /* the width (Falstad keeps it in the second point) and the side of the secondary: the points are set after Make (see Fix) */
        x1 = fmPixels(c, fmT(m, 1)); y1 = fmPixels(c, fmT(m, 2)); x2 = fmPixels(c, fmT(m, 3)); y2 = fmPixels(c, fmT(m, 4));
        vertical = (flags & 8) != 0;
        width = vertical ? -(abs(x2 - x1) > 32 ? abs(x2 - x1) : 32) : (abs(y2 - y1) > 32 ? abs(y2 - y1) : 32);
        dsF = (y2 - y1) == 0 ? fmSign(x2 - x1) : fmSign(y2 - y1);
        flip = (flags & 16) != 0 ? -1 : 1;
        ex = vertical ? x1 : x2; ey = vertical ? y2 : y1;
        dsO = (ey - y1) == 0 ? fmSign(ex - x1) : fmSign(ey - y1);
        if (dsO == 0) dsO = 1;
        if (dsF == 0) dsF = 1;
        w = (int)nearbyint(dsF * width * flip / (8.0 * dsO));
        if (w != 4) fmFSet(c, e, "width", fcFmt(c, "%d", w));
        e->hasFix = true;
        e->fixX = vertical ? x1 : x2;
        e->fixY = vertical ? y2 : y1;
        return e;
    }
    if (FM_IS("156")) {
        /* a chip: the pins go down from (x, y), 32 pixels apart (16 for a small one), the right side 96 (48) pixels to the right */
        bool small = (flags & 1) != 0;
        e = fmFNew(c, "JKFlipFlop");
        if ((flags & 4) == 0) fmFSet(c, e, "falling", "true");
        if ((flags & 2) != 0) fmFSet(c, e, "reset", "true");
        if ((flags & 8) != 0) fmWarn(m, "the inverted reset of a JK flip-flop is imported as an active-high reset.");
        if ((flags & (7 << 10)) != 0) fmWarn(m, "a flipped JK flip-flop is imported unflipped; check its connections.");
        if ((flags & (1 << 13)) != 0) { double hv = fmNum(m, 6, 5, "hv"); if (hv != 5) fmFSet(c, e, "supply", fmFFormat(c, hv)); }
        if (!small) fmFSet(c, e, "spacing", "4");
        e->hasFix = true;
        e->fixX = small ? 6 : 12;   /* (the width: the second point is set from the first) */
        e->fixY = 0;
        return e;
    }
    if (FM_IS("182") || FM_IS("183")) {
        e = fmFNew(c, "SchmittTrigger");
        if (FM_IS("183")) fmFSet(c, e, "inverting", "true");
        if (xml ? fmXmlHas(m, "lt") && fmXmlHas(m, "ut") && fmXmlHas(m, "lon") : m->t->count > 9) {
            double lo = fmNum(m, 7, 1.66, "lt"), hi = fmNum(m, 8, 3.33, "ut"), on = fmNum(m, 9, 5, "lon"), off = fmNum(m, 10, 0, "loff");
            if (lo != 1.66) fmFSet(c, e, "lower", fmFFormat(c, lo));
            if (hi != 3.33) fmFSet(c, e, "upper", fmFFormat(c, hi));
            if (on != 5) fmFSet(c, e, "supply", fmFFormat(c, on));
            if (off != 0) fmWarn(m, "a Schmitt trigger with a low level other than 0 V is imported with 0 V.");
        }
        return e;
    }
    if (FM_IS("178")) {
        int poles;
        double l, ron, onI, coilR, offI;
        e = fmFNew(c, "Relay");
        poles = fmSatInt(fmNum(m, 6, 1, "po"));
        if (poles > 1) fmWarn(m, fcFmt(c, "a relay with %d poles is imported with its first pole only.", poles));
        l = fmNum(m, 7, 0.2, "in"); ron = fmNum(m, 9, 0.05, "ron"); onI = fmNum(m, 11, 0.02, "on"); coilR = fmNum(m, 12, 20, "coR"); offI = fmNum(m, 13, onI, "of");
        if (l != 0.2) fmFSet(c, e, "inductance", fmFFormat(c, l));
        if (coilR != 20) fmFSet(c, e, "coilResistance", fmFFormat(c, coilR));
        if (onI != 0.02) fmFSet(c, e, "onCurrent", fmFFormat(c, onI));
        if (offI != 0.015) fmFSet(c, e, "offCurrent", fmFFormat(c, offI));
        if (ron != 0.05) fmWarn(m, "the contact resistance of a relay is 0.05 Ohm in the package.");
        /* the coil: on both sides (flag 4), at the second end (flag 1), else at the first */
        fmFSet(c, e, "coil", (flags & 4) != 0 ? "both" : (flags & 1) != 0 ? "end" : "start");
        if ((flags & 8) != 0) fmFSet(c, e, "flip", "true");
        return e;
    }
    if (FM_IS("O")) {
        fmWarn(m, "an output is imported as a logic output.");
        return fmFNew(c, "LogicOutput");
    }
    if (FM_IS("j")) {
        double vt, beta;
        e = fmFNew(c, "Jfet");
        if ((flags & 1) != 0) fmFSet(c, e, "pChannel", "true");
        if ((flags & 8) != 0) fmFSet(c, e, "flip", "true");
        vt = fmNum(m, 6, -4, "vt"); beta = fmNum(m, 7, 0.00125, "be");
        if (vt != -4) fmFSet(c, e, "threshold", fmFFormat(c, vt));
        if (beta != 0.00125) fmFSet(c, e, "beta", fmFFormat(c, beta));
        return e;
    }
    if (FM_IS("400")) {
        e = fmFNew(c, "Darlington");
        fmFSet(c, e, "model", strcmp(fmTok(m, m->t->count - 1, "pnp"), "-1") == 0 ? "pnp" : "npn");
        return e;
    }
    if (FM_IS("177")) {
        double trig, hold, gr;
        e = fmFNew(c, "Thyristor");
        if ((flags & 1) == 0) fmFSet(c, e, "legacy", "true");
        trig = fmNum(m, 8, 0.01, "tr"); hold = fmNum(m, 9, 0.0082, "ho"); gr = fmNum(m, 10, 50, "gr");
        if (trig != 0.01) fmFSet(c, e, "triggerCurrent", fmFFormat(c, trig));
        if (hold != 0.0082) fmFSet(c, e, "holdingCurrent", fmFFormat(c, hold));
        if (gr != 50) fmFSet(c, e, "gateResistance", fmFFormat(c, gr));
        return e;
    }
    if (FM_IS("206")) {
        double trig, hold, gr;
        e = fmFNew(c, "Triac");
        trig = fmNum(m, 6, 0.01, "ti"); hold = fmNum(m, 7, 0.0082, "hi"); gr = fmNum(m, 8, 100, "cr");
        if (trig != 0.01) fmFSet(c, e, "triggerCurrent", fmFFormat(c, trig));
        if (hold != 0.0082) fmFSet(c, e, "holdingCurrent", fmFFormat(c, hold));
        if (gr != 100) fmFSet(c, e, "gateResistance", fmFFormat(c, gr));
        return e;
    }
    if (FM_IS("203")) {
        e = fmFNew(c, "Diac");
        fmFSet(c, e, "onResistance", fmFFormat(c, fmNum(m, 6, 500, "ron")));
        fmFSet(c, e, "offResistance", fmFFormat(c, fmNum(m, 7, 1e8, "roff")));
        fmFSet(c, e, "breakdown", fmFFormat(c, fmNum(m, 8, 30, "bd")));
        fmFSet(c, e, "holdCurrent", fmFFormat(c, fmNum(m, 9, 0.01, "hc")));
        return e;
    }
    if (FM_IS("176")) {
        /* after the flags (and a forward drop with flag 1): the last voltage and the capacitance at 0 V */
        double cap = xml ? fmNum(m, 0, 4e-12, "ba") : m->t->count >= 8 ? fmNum(m, m->t->count - 1, 4e-12, "") : 4e-12;
        e = fmFNew(c, "Varactor");
        if (cap != 4e-12) fmFSet(c, e, "capacitance", fmFFormat(c, cap));
        return e;
    }
    if (FM_IS("175")) return fmFNew(c, "TunnelDiode");
    if (FM_IS("173")) {
        /* Falstad's tube is the model "falstad" (mu, kg1) of the package */
        double mu, kg1;
        FcBuf own = {0};
        int x1, y1, x2, y2, ds, side;
        e = fmFNew(c, "Triode");
        fmFSet(c, e, "model", "falstad");
        mu = fmNum(m, 6, 93, "mu"); kg1 = fmNum(m, 7, 680, "kg");
        fcBufStr(&own, "");
        if (mu != 93) fcBufFmt(&own, "mu=%s", fmFFormat(c, mu));
        if (kg1 != 680) { if (own.len) fcBufChar(&own, ' '); fcBufFmt(&own, "kg1=%s", fmFFormat(c, kg1)); }
        if (own.len > 0) fmFSet(c, e, "parameters", fcBufDone(c, &own)); else fcBufFree(&own);
        /* the side of the plate: flag 2 follows the direction, flag 1 flips it (the points are set after Make, see Fix) */
        x1 = fmPixels(c, fmT(m, 1)); y1 = fmPixels(c, fmT(m, 2)); x2 = fmPixels(c, fmT(m, 3)); y2 = fmPixels(c, fmT(m, 4));
        ds = y2 == y1 ? fmSign(x2 - x1) : fmSign(y2 - y1);
        if (ds == 0) ds = 1;
        side = ((flags & 2) != 0 ? ds : 1) * ((flags & 1) != 0 ? -1 : 1);
        e->hasFix = true;
        e->fixX = side;
        e->fixY = 0;
        return e;
    }
    if (FM_IS("179")) {
        double gain;
        e = fmFNew(c, "CurrentConveyor");
        gain = fmNum(m, 6, 1, "ga");
        if (gain != 1) fmFSet(c, e, "gain", fmFFormat(c, gain));
        fmChipLayout(c, e, flags);
        return e;
    }
    if (FM_IS("155") || FM_IS("165") || FM_IS("164")) {
        /* chips in Falstad's layout (pins on all four sides, a cell of 32 pixels or 16 for a small one) */
        const char* kind = FM_IS("155") ? "DFlipFlop" : FM_IS("165") ? "Timer555" : "Counter";
        bool small = (flags & 1) != 0;
        int next = 6, sizeX = 2;
        e = fmFNew(c, kind);
        fmFSet(c, e, "layout", "falstad");
        if (small) fmFSet(c, e, "spacing", "2");
        if (strcmp(kind, "Counter") == 0) {
            int bits = fmSatInt(fmNum(m, 6, 4, "bi"));
            if (bits != 4) fmFSet(c, e, "bits", fcFmt(c, "%d", bits));
            next = 7;
            if ((flags & 4) != 0) fmWarn(m, "an up/down counter is imported counting up.");
        }
        if ((flags & (1 << 13)) != 0) { double hv = fmNum(m, next, 5, "hv"); if (hv != 5) fmFSet(c, e, "supply", fmFFormat(c, hv)); }
        if (strcmp(kind, "DFlipFlop") == 0) {
            if ((flags & 4) != 0) fmWarn(m, "the set and reset of a D flip-flop are not simulated; check its connections.");
            else if ((flags & 2) != 0) { fmFSet(c, e, "resetPin", "true"); fmWarn(m, "the reset of a D flip-flop is not simulated."); }
        }
        if (strcmp(kind, "Timer555") == 0) {
            sizeX = 3;
            if ((flags & 4) != 0) fmFSet(c, e, "groundPin", "true");
        }
        if ((flags & (7 << 10)) != 0) fmWarn(m, "a flipped chip is imported unflipped; check its connections.");
        e->hasFix = true;
        e->fixX = (sizeX + 1) * (small ? 2 : 4);
        e->fixY = 0;
        return e;
    }
    {
        static const char* const chips[] = { "193", "TFlipFlop", "168", "Latch", "163", "RingCounter", "184", "Multiplexer", "185", "Demultiplexer", "195", "HalfAdder",
            "196", "FullAdder", "189", "SipoShift", "186", "PisoShift", "167", "Adc", "166", "Dac", "197", "SevenSegmentDecoder",
            "194", "Monostable", "161", "PhaseComparator", "158", "Vco", "157", "SevenSegment", NULL };
        const char* kind = NULL;
        int i;
        for (i = 0; chips[i]; i += 2) if (FM_IS(chips[i])) kind = chips[i + 1];
        if (kind) {
            /* the chips of the kernel's Chip: Falstad's layout; the numbers after the flags: bits (if the chip has them), the high level (with flag
             * 1 << 13), the values of the pins that keep a state, then its own */
            bool small = (flags & 1) != 0;
            int next = 6, bits = 0, sizeX = 2;
            bool hasBits;
#define FM_KIND(s) (strcmp(kind, s) == 0)
            e = fmFNew(c, kind);
            if (small) fmFSet(c, e, "spacing", "2");
            hasBits = FM_KIND("Latch") || FM_KIND("RingCounter") || FM_KIND("SipoShift") || FM_KIND("PisoShift") || FM_KIND("Adc") || FM_KIND("Dac") || (FM_KIND("FullAdder") && (flags & 2) != 0);
            if (hasBits) {
                int fallback = FM_KIND("RingCounter") ? 10 : FM_KIND("SipoShift") || FM_KIND("PisoShift") ? 8 : FM_KIND("FullAdder") ? 1 : 4;
                bits = fmSatInt(fmNum(m, 6, fallback, "bi"));
                next = 7;
                fmFSet(c, e, "bits", fcFmt(c, "%d", bits));
            }
            if ((flags & (1 << 13)) != 0) { double hv = fmNum(m, next, 5, "hv"); next++; if (hv != 5) fmFSet(c, e, "supply", fmFFormat(c, hv)); }
            /* the pins with a state: their values come next */
            next += FM_KIND("TFlipFlop") ? 1 : FM_KIND("RingCounter") ? bits : FM_KIND("Latch") ? ((flags & 2) != 0 ? bits : 0) : 0;
            if (FM_KIND("TFlipFlop")) {
                if ((flags & 2) != 0) fmFSet(c, e, "reset", "true");
                if ((flags & 4) != 0) fmFSet(c, e, "set", "true");
            } else if (FM_KIND("Latch")) {
                if ((flags & (32 | 64)) != 0) {
                    fmWarn(m, "a latch with enable pins is not in the package circuits and was left out.");
                    return NULL;
                }
                if ((flags & 4) != 0) fmFSet(c, e, "levelTriggered", "true");
                if ((flags & 8) != 0) fmFSet(c, e, "reset", "true");
                if ((flags & 16) != 0) fmFSet(c, e, "set", "true");
                if ((flags & 128) != 0) fmFSet(c, e, "resetActiveLow", "true");
            } else if (FM_KIND("RingCounter")) {
                sizeX = bits > 2 ? bits : 2;
                fmFSet(c, e, "clockInhibit", (flags & 2) != 0 ? "true" : "false");
                if ((flags & 4) != 0) fmFSet(c, e, "resetActiveHigh", "true");
            } else if (FM_KIND("Multiplexer") || FM_KIND("Demultiplexer")) {
                int sel = fmSatInt(fmNum(m, next, 2, "se"));
                if (sel != 2) fmFSet(c, e, "selectBits", fcFmt(c, "%d", sel));
                sizeX = sel + 1;
                if ((flags & 8) != 0) {
                    fmWarn(m, fcFmt(c, "a %s with a select bus is not in the package circuits and was left out.", fmDescribe(type)));
                    return NULL;
                }
                if (FM_KIND("Multiplexer")) {
                    if ((flags & 2) != 0) fmFSet(c, e, "invertedOutput", "true");
                    if ((flags & 4) != 0) fmFSet(c, e, "strobe", "true");
                } else if ((flags & 16) != 0) fmFSet(c, e, "invertedOutputs", "true");
            } else if (FM_KIND("SipoShift")) sizeX = bits + 1;
            else if (FM_KIND("PisoShift")) {
                sizeX = bits + 2;
                if ((flags & 2) != 0) fmFSet(c, e, "serialIn", "true");
            } else if (FM_KIND("SevenSegmentDecoder")) {
                sizeX = 3;
                if (fmNum(m, next, 0, "sgt") != 0) {
                    fmWarn(m, "a 14 or 16 segment decoder is not in the package circuits and was left out.");
                    return NULL;
                }
                if ((flags & 2) != 0) fmFSet(c, e, "blankInput", "true");
                if ((flags & 4) != 0) fmFSet(c, e, "blankOnF", "true");
            } else if (FM_KIND("Monostable")) {
                double delay;
                if (strcmp(fmTok(m, next, "rt"), "true") == 0) fmFSet(c, e, "retriggerable", "true");
                delay = fmNum(m, next + 1, 0.01, "dl");
                if (delay != 0.01) fmFSet(c, e, "time", fmFFormat(c, delay));
                if ((flags & 2) != 0) fmFSet(c, e, "invertTrigger", "true");
            } else if (FM_KIND("SevenSegment")) {
                double segs = fmNum(m, next, 7, "ba"), extra = fmNum(m, next + 1, 0, "ex"), diodes = fmNum(m, next + 2, 0, "di");
                if (segs != 7 || diodes != 0 || extra > 1) {
                    fmWarn(m, "only the 7-segment display with logic inputs (and a point) is in the package circuits; this one was left out.");
                    return NULL;
                }
                sizeX = 4;
                if (extra == 1) { fmFSet(c, e, "point", "true"); sizeX = 5; }
            }
#undef FM_KIND
            if ((flags & (7 << 10)) != 0) fmWarn(m, "a flipped chip is imported unflipped; check its connections.");
            e->hasFix = true;
            e->fixX = (sizeX + 1) * (small ? 2 : 4);
            e->fixY = 0;
            return e;
        }
    }
    if (FM_IS("180")) {
        double ron = fmNum(m, 6, 0.1, "ron"), roff = fmNum(m, 7, 1e10, "roff"), rground = fmNum(m, 8, 0, "rog"), hv = fmNum(m, 9, 5, "hi");
        e = fmFNew(c, "TriState");
        if (ron != 0.1) fmFSet(c, e, "onResistance", fmFFormat(c, ron));
        if (roff != 1e10) fmFSet(c, e, "offResistance", fmFFormat(c, roff));
        if (rground != 0) fmFSet(c, e, "offGroundResistance", fmFFormat(c, rground));
        if (hv != 5) fmFSet(c, e, "supply", fmFFormat(c, hv));
        if ((flags & 1) != 0) fmFSet(c, e, "flip", "true");
        return e;
    }
    if (FM_IS("402")) {
        /* the supplies are the fourth number of the first two parts of the composite (its rails) */
        double neg, pos;
        e = fmFNew(c, "Ota");
        if (xml) { neg = fmNum(m, 0, -9, "nv"); pos = fmNum(m, 0, 9, "pv"); }
        else {
            int k;
            for (k = 0; k < 2; k++) {
                /* Rail(i, fallback): the fourth part of Tok(i).Split('_') */
                const char* tok = fmTok(m, 6 + k, "");
                const char* p = tok;
                int part = 0;
                double v = k == 0 ? -9 : 9, parsed;
                while (part < 3 && (p = strchr(p, '_')) != NULL) { p++; part++; }
                if (part == 3 && p) {
                    const char* end = strchr(p, '_');
                    if (fmParseFloat(end ? fcStrN(c, p, (size_t)(end - p)) : p, &parsed)) v = parsed;
                }
                if (k == 0) neg = v; else pos = v;
            }
        }
        if (pos != 9) fmFSet(c, e, "positive", fmFFormat(c, pos));
        if (neg != -9) fmFSet(c, e, "negative", fmFFormat(c, neg));
        return e;
    }
    if (FM_IS("417")) { e = fmFNew(c, "Unijunction"); if ((flags & 2) != 0) fmFSet(c, e, "flip", "true"); return e; }
    if (FM_IS("407")) {
        e = fmFNew(c, "Optocoupler");
        if ((flags & (3 << 10)) != 0) fmWarn(m, "a flipped optocoupler is imported unflipped; check its connections.");
        e->hasFix = true;
        e->fixX = 12;
        e->fixY = 0;
        return e;
    }
    if (FM_IS("212") || FM_IS("213") || FM_IS("214") || FM_IS("215")) {
        /* Falstad's sources compute their output from an expression of their inputs; the package has the linear ones */
        const char* kind = FM_IS("212") ? "VCVS" : FM_IS("213") ? "VCCS" : FM_IS("214") ? "CCVS" : "CCCS";
        int inputs = fmSatInt(fmNum(m, 6, 2, "ic"));
        const char* expr = fmFUnescape(c, fmTok(m, 7, "ex"));
        double gain;
        if (!(inputs == 2 && fmLinearGain(c, expr, FM_IS("212") || FM_IS("213"), &gain))) {
            fmWarn(m, fcFmt(c, "the %s with the expression '%s' is not linear in the package circuits and was left out.", fmDescribe(type), expr));
            return NULL;
        }
        e = fmFNew(c, kind);
        fmFSet(c, e, "gain", fmFFormat(c, gain));
        fmChipLayout(c, e, flags);
        return e;
    }
#undef FM_IS
    fmWarn(m, fcFmt(c, "the element '%s' (%s) is not in the package circuits and was left out.", type, fmDescribe(type)));
    return NULL;
}

/* the tags of the XML format and the types of the text format */
static const char* const fmXmlTypes[] = {
    "w", "w", "r", "r", "c", "c", "l", "l", "v", "v", "R", "R", "g", "g", "i", "i", "s", "s", "S", "S", "d", "d", "z", "z",
    "t", "t", "f", "f", "j", "j", "a", "a", "m", "m", "p", "p", "x", "x", "T", "T", "I", "I", "L", "L", "M", "M", "O", "O",
    "And", "150", "Nand", "151", "Or", "152", "Nor", "153", "Xor", "154",
    "ADC", "167", "DAC", "166", "CC2", "179", "CC2Neg", "179", "CCCS", "215", "CCVS", "214", "VCCS", "213", "VCVS", "212",
    "Comparator", "401", "DFlipFlop", "155", "JKFlipFlop", "156", "TFlipFlop", "193", "Diac", "203", "Triac", "206", "SCR", "177",
    "FullAdder", "196", "HalfAdder", "195", "Fuse", "404", "LDR", "374", "LED", "162", "Lamp", "181", "Latch", "168",
    "Monostable", "194", "OTA", "402", "Optocoupler", "407", "PhaseComp", "161", "PisoShift", "186", "SipoShift", "189",
    "RelayCoil", "425", "RelayContact", "426", "RingCounter", "163", "Schmitt", "182", "InvertingSchmitt", "183",
    "SevenSegDecoder", "197", "SparkGap", "187", "ThermistorNTC", "350", "Timer", "165", "Triode", "173", "TunnelDiode", "175",
    "Unijunction", "417", "VCO", "158", "VarRail", "172", "Varactor", "176", "as", "159", "as2", "160", "ctr", "164",
    "dar", "400", "mux", "184", "dmux", "185", "ln", "207", "pc", "209", "pt", "174", "rl", "178", "ssd", "157",
    "sw", "170", "tl", "171", "ts", "180",
    NULL
};
static const char* const fmXmlOther[] = { "o", "h", "adj", "dm", "rlm", "tm", "mm", "clm", "ccm", "test", "switchevent", "scopedata", NULL };

/* the first n UTF-16 units of an fc8 text */
static const char* fmUnitPrefix(FcCtx* c, const char* s, int n) {
    const unsigned char* p = (const unsigned char*)s;
    int k;
    for (k = 0; k < n && *p; k++) fc8Next(&p);
    return fcStrN(c, s, (size_t)((const char*)p - s));
}

typedef struct FmScope2 { int index; int value; } FmScopeRef;

/* ReadXml */
static bool fmFReadXml(FcCtx* c, const char* text, FcList* items, FcList* scopes, FcList* warnings, const char** timeStep, bool* smallGrid) {
    int n;
    uint16_t* u = fmUnits(c, text, &n);
    FcXmlError xerr;
    FcXNode* root = fcXmlParse(c, u, n, &xerr);
    double dt;
    int options, i, k;
    if (!root) { fcListAdd(c, warnings, (void*)fcFmt(c, "The XML could not be read: %s", xerr.message)); return false; }
    if (strcmp(root->local, "cir") != 0) { fcListAdd(c, warnings, (void*)fcFmt(c, "The XML is no circuit of Falstad's simulator (<cir>, not <%s>).", root->local)); return false; }
    if (fmParseFloat(fcxAttr(root, "ts"), &dt) && dt > 0) *timeStep = fcFmt(c, "%ss", fmFFormat(c, dt));
    if (fcxAttr(root, "f") && fmTryParseInt(fcxAttr(root, "f"), &options)) *smallGrid = (options & 2) != 0;
    for (i = 0; i < root->nodes->count; i++) {
        FcXNode* x = (FcXNode*)root->nodes->items[i];
        const char* tag;
        const char* points;
        const char* line;
        FcList* p;
        FcBuf lb = {0};
        FmFItem* item;
        const char* type = NULL;
        if (x->kind != FCX_ELEMENT) continue;
        tag = x->local;
        if (strcmp(tag, "o") == 0) {
            /* a scope: the element it looks at, the value of its first plot (0 voltage, 1 current) */
            int index, v = 0;
            const char* en = fcxAttr(x, "en");
            if (en && fmTryParseInt(en, &index)) {
                FmScopeRef* s = (FmScopeRef*)fcAlloc(c, sizeof *s);
                for (k = 0; k < x->nodes->count; k++) {
                    FcXNode* plot = (FcXNode*)x->nodes->items[k];
                    if (plot->kind == FCX_ELEMENT && strcmp(plot->local, "p") == 0 && plot->ns[0] == 0) {
                        const char* pv = fcxAttr(plot, "v");
                        if (!pv || !fmTryParseInt(pv, &v)) v = 0;
                        break;
                    }
                }
                s->index = index;
                s->value = v;
                fcListAdd(c, scopes, s);
            }
            continue;
        }
        if (fmInSet(fmXmlOther, tag) || (points = fcxAttr(x, "x")) == NULL) continue;
        p = fmSplitTrim(c, points, " ", true);
        {
            /* (Split(' ', RemoveEmptyEntries) does not trim: a token keeps other white space) */
            const char* q = points;
            p = fcListNew(c);
            for (;;) {
                const char* sp = strchr(q, ' ');
                const char* tok = sp ? fcStrN(c, q, (size_t)(sp - q)) : q;
                if (tok[0]) fcListAdd(c, p, (void*)tok);
                if (!sp) break;
                q = sp + 1;
            }
        }
        fcBufStr(&lb, "");
        fcxWrite(&lb, x);
        line = fcBufDone(c, &lb);
        if (fc8Units(line) > 80) line = fcFmt(c, "%s...", fmUnitPrefix(c, line, 77));
        item = (FmFItem*)fcAlloc(c, sizeof *item);
        item->lineNo = x->line;
        item->line = line;
        item->t = fcListNew(c);
        if (p->count < 4) {
            fcListAdd(c, warnings, (void*)fcFmt(c, "Line %d: '%s' has no points.", x->line, line));
            item->type = "";
            for (k = 0; k < 6; k++) fcListAdd(c, item->t, (void*)(k == 0 ? "" : "0"));
            fcListAdd(c, items, item);
            continue;
        }
        item->xmlKeys = fcListNew(c);
        item->xmlValues = fcListNew(c);
        for (k = 0; k < x->attrs->count; k++) {
            FcXAttr* a = (FcXAttr*)x->attrs->items[k];
            fcListAdd(c, item->xmlKeys, (void*)a->local);
            fcListAdd(c, item->xmlValues, (void*)a->value);
        }
        fcListAdd(c, item->t, (void*)tag);
        for (k = 0; k < 4; k++) fcListAdd(c, item->t, p->items[k]);
        fcListAdd(c, item->t, (void*)(fcxAttr(x, "f") ? fcxAttr(x, "f") : "0"));
        for (k = 0; fmXmlTypes[k]; k += 2) if (strcmp(fmXmlTypes[k], tag) == 0) type = fmXmlTypes[k + 1];
        if (!type) {
            fcListAdd(c, warnings, (void*)fcFmt(c, "Line %d: the element <%s> is not in the package circuits and was left out.", x->line, tag));
            type = "";
        }
        item->type = type;
        fcListAdd(c, items, item);
    }
    return true;
}

/* Convert: the text; `className` goes into the file (empty: the class is named after the file) */
static FmFalstadResult fmFalstadConvert(FcCtx* c, const char* text, const char* className) {
    FmFalstadResult result;
    FcList* warnings = fcListNew(c);
    FcList* elements = fcListNew(c);   /* by the index Falstad gives them (NULL: left out), for the scopes */
    FcList* scopes = fcListNew(c);
    const char* timeStep = "";
    bool smallGrid = false;   /* Falstad's grid: 16 pixels, 8 with flag 2 of the options */
    FcList* items = fcListNew(c);
    FcList* present;
    FcBuf sb = {0};
    int i, k, bottom = 0, left = 0, n = 0;
    const char* trimmedStart = text;
    {
        const unsigned char* p = (const unsigned char*)text;
        for (;;) { const unsigned char* q = p; if (!*q || !fcIsWhiteSpace(fc8Next(&q))) break; p = q; }
        trimmedStart = (const char*)p;
    }
    if (trimmedStart[0] == '<') {
        /* the XML format of the newer simulator (<cir> with an element per tag): read into the same items as the lines of text */
        if (!fmFReadXml(c, text, items, scopes, warnings, &timeStep, &smallGrid)) {
            result.biscuit = "";
            result.warnings = warnings;
            result.elements = 0;
            return result;
        }
    } else {
        int no = 0;
        const char* norm = fmReplace(c, fmReplace(c, text, "\r\n", "\n"), "\r", "\n");
        const char* p = norm;
        for (;;) {
            const char* nl = strchr(p, '\n');
            const char* raw = nl ? fcStrN(c, p, (size_t)(nl - p)) : p;
            const char* line;
            FcList* t;
            const char* type;
            no++;
            line = fmTrim(c, raw);
            if (line[0]) {
                /* Split(new[] { ' ', '+', '\t', '\f' }, RemoveEmptyEntries) */
                const char* q = line;
                t = fcListNew(c);
                for (;;) {
                    const char* e = q;
                    while (*e && *e != ' ' && *e != '+' && *e != '\t' && *e != '\f') e++;
                    if (e > q) fcListAdd(c, t, (void*)fcStrN(c, q, (size_t)(e - q)));
                    if (!*e) break;
                    q = e + 1;
                }
                if (t->count > 0) {
                    int code;
                    type = (const char*)t->items[0];
                    if (strcmp(type, "$") == 0) {
                        double dt;
                        int options;
                        if (t->count > 2 && fmParseFloat((const char*)t->items[2], &dt) && dt > 0) timeStep = fcFmt(c, "%ss", fmFFormat(c, dt));
                        if (t->count > 1 && fmTryParseInt((const char*)t->items[1], &options)) smallGrid = (options & 2) != 0;
                    } else if (strcmp(type, "o") == 0) {
                        int index, v;
                        if (t->count > 3 && fmTryParseInt((const char*)t->items[1], &index)) {
                            FmScopeRef* s = (FmScopeRef*)fcAlloc(c, sizeof *s);
                            s->index = index;
                            s->value = fmTryParseInt((const char*)t->items[3], &v) ? v : 0;
                            fcListAdd(c, scopes, s);
                        }
                    } else if (strcmp(type, "h") == 0 || strcmp(type, "!") == 0 || strcmp(type, "%") == 0 || strcmp(type, "?") == 0 || strcmp(type, "B") == 0
                               || strcmp(type, ".") == 0 || strcmp(type, "34") == 0 || strcmp(type, "32") == 0 || strcmp(type, "38") == 0) {
                        /* hints, models, sliders, subcircuits */
                    } else {
                        /* old files give the letters of the types as numbers ("82" is 'R') */
                        if (fmParseIntStyle(type, false, &code) && code >= 65 && code < 127 && fcIsLetter((uint16_t)code)) type = fcFmt(c, "%c", code);
                        if (t->count < 6) fcListAdd(c, warnings, (void*)fcFmt(c, "Line %d: '%s' is no element.", no, line));
                        else {
                            FmFItem* item = (FmFItem*)fcAlloc(c, sizeof *item);
                            item->type = type;
                            item->t = t;
                            item->lineNo = no;
                            item->line = line;
                            fcListAdd(c, items, item);
                        }
                    }
                }
            }
            if (!nl) break;
            p = nl + 1;
        }
    }
    for (i = 0; i < items->count; i++) {
        FmFItem* item = (FmFItem*)items->items[i];
        FmMake m;
        int warnMark = warnings->count;
        m.c = c;
        m.t = item->t;
        m.xk = item->xmlKeys;
        m.xv = item->xmlValues;
        m.warnings = warnings;
        m.lineNo = item->lineNo;
        FC_TRY(c) {
            int x1 = fmGrid(c, fmT(&m, 1)), y1 = fmGrid(c, fmT(&m, 2)), x2 = fmGrid(c, fmT(&m, 3)), y2 = fmGrid(c, fmT(&m, 4));
            int f, flags;
            FmFElement* e;
            flags = fmTryParseInt(fmT(&m, 5), &f) ? f : 0;
            e = item->type[0] == 0 ? NULL : fmMakeElement(&m, item->type, flags);
            if (e) {
                e->x1 = x1; e->y1 = y1; e->x2 = x2; e->y2 = y2;
                /* the gate of a thyristor or triac lies on Falstad's grid */
                if (smallGrid && (strcmp(e->type, "Thyristor") == 0 || strcmp(e->type, "Triac") == 0)) fmFSet(c, e, "smallGrid", "true");
                if (e->hasFix) {
                    if (strcmp(e->type, "JKFlipFlop") == 0 || strcmp(e->type, "VCVS") == 0 || strcmp(e->type, "VCCS") == 0 || strcmp(e->type, "CCCS") == 0
                        || strcmp(e->type, "CCVS") == 0 || strcmp(e->type, "CurrentConveyor") == 0 || strcmp(e->type, "Optocoupler") == 0
                        || strcmp(e->type, "DFlipFlop") == 0 || strcmp(e->type, "Timer555") == 0 || strcmp(e->type, "Counter") == 0 || fmInSet(fmKernelChips, e->type)) {
                        e->x2 = x1 + e->fixX;
                        e->y2 = y1;
                    } else if (strcmp(e->type, "Triode") == 0) {
                        /* Falstad's side of plate and cathode, against the one the package takes from the direction */
                        int dsign = y2 == y1 ? fmSign(x2 - x1) : fmSign(y2 - y1);
                        if (dsign == 0) dsign = 1;
                        if (e->fixX != dsign) fmFSet(c, e, "flip", "true");
                    } else { e->x2 = fmGrid8(e->fixX); e->y2 = fmGrid8(e->fixY); }
                }
            }
            fcListAdd(c, elements, e);
        } FC_CATCH(c) {
            if (c->err.kind != FC_ERR_INTERNAL || (strcmp(c->err.message, "FormatException") != 0 && strcmp(c->err.message, "IndexOutOfRangeException") != 0)) fcRethrow(c);
            (void)warnMark;
            fcListAdd(c, warnings, (void*)fcFmt(c, "Line %d: '%s' could not be read.", item->lineNo, item->line));
            fcListAdd(c, elements, NULL);
        } FC_END_TRY;
    }

    /* names for the elements that scopes look at, and the scopes below the drawing */
    present = fcListNew(c);
    for (i = 0; i < elements->count; i++) if (elements->items[i]) fcListAdd(c, present, elements->items[i]);
    for (i = 0; i < present->count; i++) {
        FmFElement* e = (FmFElement*)present->items[i];
        int ymax = e->y1 > e->y2 ? e->y1 : e->y2, xmin = e->x1 < e->x2 ? e->x1 : e->x2;
        if (i == 0 || ymax + 6 > bottom) bottom = ymax + 6;
        if (i == 0 || xmin < left) left = xmin;
    }
    for (i = 0; i < scopes->count; i++) {
        FmScopeRef* s = (FmScopeRef*)scopes->items[i];
        FmFElement* target;
        FmFElement* scope;
        if (s->index < 0 || s->index >= elements->count || !(target = (FmFElement*)elements->items[s->index])) { fcListAdd(c, warnings, (void*)"A scope looks at an element that was left out."); continue; }
        if (!target->name[0]) target->name = fmUniqueName(c, present, strcmp(target->type, "Wire") == 0 ? "W" : fmPrefix(target->type));
        scope = fmFNew(c, "Scope");
        scope->x1 = left + n * 34; scope->y1 = bottom; scope->x2 = left + n * 34 + 32; scope->y2 = bottom + 16;
        fmFSet(c, scope, "probe", target->name);
        if (s->value == 1) fmFSet(c, scope, "quantity", "current");
        fcListAdd(c, present, scope);
        n++;
    }

    fcBufStr(&sb, "{\n  \"biscuit\": 1");
    if (className[0]) fcBufFmt(&sb, ",\n  \"class\": %s", fmFJson(c, className));
    if (timeStep[0]) fcBufFmt(&sb, ",\n  \"timeStep\": %s", fmFJson(c, timeStep));
    fcBufStr(&sb, ",\n  \"elements\": [");
    for (i = 0; i < present->count; i++) {
        FmFElement* e = (FmFElement*)present->items[i];
        fcBufStr(&sb, i == 0 ? "\n    " : ",\n    ");
        fcBufFmt(&sb, "{\"type\": %s", fmFJson(c, e->type));
        if (e->name[0]) fcBufFmt(&sb, ", \"name\": %s", fmFJson(c, e->name));
        fcBufFmt(&sb, ", \"from\": [%d, %d], \"to\": [%d, %d]", e->x1, e->y1, e->x2, e->y2);
        for (k = 0; k < e->keys->count; k++) fcBufFmt(&sb, ", %s: %s", fmFJson(c, (const char*)e->keys->items[k]), fmFJson(c, (const char*)e->values->items[k]));
        fcBufChar(&sb, '}');
    }
    fcBufStr(&sb, "\n  ]\n}\n");
    result.biscuit = fcBufDone(c, &sb);
    result.warnings = warnings;
    result.elements = present->count;
    return result;
}
