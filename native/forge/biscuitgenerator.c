/* native/forge/biscuitgenerator.c - port of src/fire.UI.Markup/BiscuitGenerator.cs (commit 7a594769): MarkupFiles (which generator a file
 * takes), BiscuitDocument and BiscuitGenerator - the schematic of a circuit (`.biscuit`: JSON, json.c) as ONE class that derives from
 * Circuits.Circuit, with a field for every named element and a member for every port. */

/* MarkupFiles: Path.GetExtension(path) is `ext`, whatever its case */
static bool fmExtensionIs(const char* path, const char* ext) {
    const char* name = fmBaseNameOf(path);
    const char* dot = strrchr(name, '.');
    return dot && fmIEq(dot, ext);
}
static bool fmIsUiMarkup(const char* path) { return fmExtensionIs(path, ".fxml"); }
static bool fmIsBiscuit(const char* path) { return fmExtensionIs(path, ".biscuit"); }
static bool fmIsDataModel(const char* path) { return fmExtensionIs(path, ".fdata"); }
static bool fmIsGenerated(const char* path) { return fmIsUiMarkup(path) || fmIsBiscuit(path) || fmIsDataModel(path); }

static const char* fmBiscuitGenerate(FcCtx* c, const char* text, const char* sourceName);
static const char* fmDataGenerate(FcCtx* c, const char* text, const char* path);

/* MarkupFiles.GenerateScript: the script generated from the file (an FC_ERR_MARKUP when it has mistakes) */
static const char* fmGenerateScript(FcCtx* c, const char* path, const uint16_t* text, int n) {
    if (fmIsBiscuit(path)) return fmBiscuitGenerate(c, fc8FromU16(c, text, n), path);
    if (fmIsDataModel(path)) return fmDataGenerate(c, fc8FromU16(c, text, n), path);
    return fmUiGenerateScript(c, fmParse(c, text, n), path);
}

/* FieldTypes: the kinds of elements and the class of the field of a named one */
static const char* const fmBiscuitFieldTypes[] = {
    "Wire", "Circuits.Element", "Ground", "Circuits.Ground", "Resistor", "Circuits.Resistor", "Capacitor", "Circuits.Capacitor",
    "Inductor", "Circuits.Inductor", "Switch", "Circuits.Switch", "Source", "Circuits.VoltageSource", "Rail", "Circuits.VoltageSource",
    "CurrentSource", "Circuits.CurrentSource", "AudioInput", "Circuits.AudioInput", "AudioOutput", "Circuits.AudioOutput", "LogicInput", "Circuits.LogicInput",
    "Diode", "Circuits.Diode", "Zener", "Circuits.Zener", "Led", "Circuits.Led", "Transistor", "Circuits.Transistor", "Mosfet", "Circuits.Mosfet",
    "Formula", "Circuits.Formula", "Jfet", "Circuits.Jfet", "Darlington", "Circuits.Darlington", "Thyristor", "Circuits.Thyristor", "Triac", "Circuits.Triac", "Diac", "Circuits.Diac",
    "Varactor", "Circuits.Varactor", "TunnelDiode", "Circuits.TunnelDiode", "Triode", "Circuits.Triode", "Pentode", "Circuits.Pentode", "VacuumDiode", "Circuits.VacuumDiode", "VCVS", "Circuits.VCVS", "VCCS", "Circuits.VCCS",
    "CCCS", "Circuits.CCCS", "CCVS", "Circuits.CCVS", "CurrentConveyor", "Circuits.CurrentConveyor", "Ota", "Circuits.Ota", "Unijunction", "Circuits.Unijunction", "Optocoupler", "Circuits.Optocoupler", "OpAmp", "Circuits.OpAmp", "Comparator", "Circuits.Comparator",
    "And", "Circuits.Gate", "Nand", "Circuits.Gate", "Or", "Circuits.Gate", "Nor", "Circuits.Gate", "Xor", "Circuits.Gate", "Xnor", "Circuits.Gate",
    "Not", "Circuits.Gate", "Buffer", "Circuits.Gate", "DFlipFlop", "Circuits.FlipFlop", "SRLatch", "Circuits.SRLatch", "Counter", "Circuits.Counter",
    "Timer555", "Circuits.Timer555", "TFlipFlop", "Circuits.TFlipFlop", "Latch", "Circuits.Latch", "RingCounter", "Circuits.RingCounter",
    "Multiplexer", "Circuits.Multiplexer", "Demultiplexer", "Circuits.Demultiplexer", "HalfAdder", "Circuits.HalfAdder", "FullAdder", "Circuits.FullAdder",
    "SipoShift", "Circuits.SipoShift", "PisoShift", "Circuits.PisoShift", "Adc", "Circuits.Adc", "Dac", "Circuits.Dac",
    "SevenSegmentDecoder", "Circuits.SevenSegmentDecoder", "SevenSegment", "Circuits.SevenSegment", "Monostable", "Circuits.Monostable",
    "PhaseComparator", "Circuits.PhaseComparator", "Vco", "Circuits.Vco", "TriState", "Circuits.TriState", "GpioLine", "Circuits.GpioLine", "I2cAdc", "Circuits.I2cAdc", "I2cDac", "Circuits.I2cDac",
    "Potentiometer", "Circuits.Potentiometer", "Transformer", "Circuits.Transformer", "Relay", "Circuits.Relay", "JKFlipFlop", "Circuits.JKFlipFlop",
    "SchmittTrigger", "Circuits.SchmittTrigger", "Switch2", "Circuits.Switch2", "Lamp", "Circuits.Lamp", "Fuse", "Circuits.Fuse",
    "Crystal", "Circuits.Crystal", "LightResistor", "Circuits.LightResistor", "Thermistor", "Circuits.Thermistor", "Memristor", "Circuits.Memristor",
    "SparkGap", "Circuits.SparkGap", "TransmissionLine", "Circuits.TransmissionLine", "AnalogSwitch", "Circuits.AnalogSwitch",
    "AnalogSwitch2", "Circuits.AnalogSwitch2", "Sweep", "Circuits.SweepSource", "RelayCoil", "Circuits.RelayCoil", "RelayContact", "Circuits.CoilContact", "SpiShiftOut", "Circuits.SpiShiftOut", "SpiShiftIn", "Circuits.SpiShiftIn", "LogicOutput", "Circuits.Element", "Probe", "Circuits.Element", "Scope", "Circuits.Scope",
    "Label", "Circuits.Element", "Pin", "Circuits.Element", "Text", "Circuits.Element",
    NULL
};
static const char* fmBiscuitFieldType(const char* type) {
    int i;
    for (i = 0; fmBiscuitFieldTypes[i]; i += 2) if (strcmp(fmBiscuitFieldTypes[i], type) == 0) return fmBiscuitFieldTypes[i + 1];
    return NULL;
}

/* Reserved: the members of Circuits.Circuit (and Part) */
static const char* const fmBiscuitReserved[] = {
    "name", "ports", "owner", "stepsInFire", "commitsInFire", "parts", "instruments", "ground", "dt", "time", "solver", "dirty", "external", "fireSteps", "clipped",
    "paramNames", "paramValues", "bindings", "schematic", "branch", "index",
    "Name", "AddPort", "PortAt", "Ports", "Branches", "Compile", "Stamp", "StampStep", "Commit", "Save", "Probe", "Reset", "CurrentRaw", "Current", "Invalidate", "Plan",
    "Ground", "Schematic", "HasSchematic", "LoadSchematic", "OnBuilt", "RemovePart", "Detach", "Add", "Parts", "Parameter", "Get", "HasParameter", "Value", "Bind",
    "ApplyBindings", "TimeStep", "Time", "TimeRaw", "IsCompiled", "Release", "Attach", "Top", "Collect", "AllPorts", "CircuitPorts", "GroundAll", "Build", "Step",
    "FireStep", "Run", "ProcessSamples", "SampleAt", "ProcessAudio", "Clipped", "PlanInfo", "this", "base",
    NULL
};
static const char* const fmBiscuitKeywords[] = {
    "class", "var", "int", "float", "bool", "string", "char", "any", "undefined", "true", "false", "if", "else", "while", "for", "foreach", "in", "of", "from", "is",
    "return", "new", "delete", "namespace", "using", "func", "on", "off", "copy", "take", "try", "catch", "finally", "throw", "break", "continue", "switch", "case",
    "default", "static", "construct", "destruct", "fire", "sync", "unit", "enum", "interface", "extern", "native", "byte", "word", "do", "readonly", "flat", "override",
    NULL
};
static bool fmInSet(const char* const* set, const char* s) { int i; for (i = 0; set[i]; i++) if (strcmp(set[i], s) == 0) return true; return false; }

/* IsIdentifier */
static bool fmBiscuitIsIdentifier(const char* s) {
    const unsigned char* p = (const unsigned char*)s;
    uint16_t u;
    if (!*p) return false;
    u = fc8Next(&p);
    if (!fcIsLetter(u) && u != '_') return false;
    while (*p) { u = fc8Next(&p); if (!fcIsLetterOrDigit(u) && u != '_') return false; }
    return !fmInSet(fmBiscuitKeywords, s);
}
/* Identifier: a file name as a class name ("my amp" is "my_amp", "2stage" is "_2stage") */
static const char* fmBiscuitIdentifier(FcCtx* c, const char* s) {
    FcBuf b = {0};
    const unsigned char* p = (const unsigned char*)s;
    const char* r;
    bool first = true, digitFirst = false;
    fcBufStr(&b, "");
    while (*p) {
        uint16_t u = fc8Next(&p);
        uint16_t o = fcIsLetterOrDigit(u) || u == '_' ? u : '_';
        if (first) digitFirst = fcIsDigit(o);
        first = false;
        fcBufUnit(&b, o);
    }
    r = fcBufDone(c, &b);
    if (digitFirst) r = fcFmt(c, "_%s", r);
    return fmInSet(fmBiscuitKeywords, r) ? fcFmt(c, "_%s", r) : r;
}

/* Path.GetFileNameWithoutExtension */
static const char* fmFileNameWithoutExtension(FcCtx* c, const char* path) {
    const char* name = fcBaseName(path);
    const char* dot = strrchr(name, '.');
    return dot ? fcStrN(c, name, (size_t)(dot - name)) : name;
}

/* LineStarts / LineOf (offsets in the bytes of the fc8 text: the lines are the same) */
static FcList* fmLineStarts(FcCtx* c, const char* text) {
    FcList* l = fcListNew(c);
    size_t i;
    fcListAdd(c, l, (void*)(intptr_t)0);
    for (i = 0; text[i]; i++) if (text[i] == '\n') fcListAdd(c, l, (void*)(intptr_t)(i + 1));
    return l;
}
static int fmLineOfOffset(FcList* starts, long offset) {
    int lo = 0, hi = starts->count - 1;
    if (offset < 0) return 1;
    while (lo <= hi) {
        int mid = (lo + hi) / 2;
        long v = (long)(intptr_t)starts->items[mid];
        if (v == offset) return mid + 1;
        if (v < offset) lo = mid + 1; else hi = mid - 1;
    }
    return lo;   /* ~line: the index of the next greater start */
}
/* ElementOffset: where the i-th object of the "elements" array starts */
static long fmElementOffset(const char* text, int index) {
    const char* found = strstr(text, "\"elements\"");
    long at, i, n = (long)strlen(text);
    int depth = 0, count = -1;
    bool inString = false;
    if (!found) return -1;
    at = (long)(found - text);
    {
        const char* br = strchr(text + at, '[');
        at = br ? (long)(br - text) : -1;
    }
    for (i = at + 1; i < n && at >= 0; i++) {
        char ch = text[i];
        if (inString) { if (ch == '\\') i++; else if (ch == '"') inString = false; continue; }
        if (ch == '"') inString = true;
        else if (ch == '{' || ch == '[') { if (depth == 0 && ch == '{') { count++; if (count == index) return i; } depth++; }
        else if (ch == '}' || ch == ']') { if (depth == 0) return -1; depth--; }
    }
    return -1;
}

/* IsPoint */
static bool fmIsPoint(const FjValue* e, const char* key) {
    const FjValue* p = fjProp(e, key);
    int i, n;
    if (!p || p->kind != FJK_ARRAY || p->items->count != 2) return false;
    for (i = 0; i < 2; i++) if (!fjTryGetInt32((const FjValue*)p->items->items[i], &n)) return false;
    return true;
}
static const char* fmJsonStr(const FjValue* o, const char* key) {
    const FjValue* v = fjProp(o, key);
    return v && v->kind == FJK_STRING ? v->text : NULL;
}

typedef struct FmBiscuitElement { const char* type; const char* name; int line; } FmBiscuitElement;
typedef struct FmBiscuitPort { const char* name; const char* direction; const char* pin; const char* color; int line; } FmBiscuitPort;
typedef struct FmBiscuitDocument { const char* className; const char* ns; FcList* elements; FcList* ports; const char* compact; } FmBiscuitDocument;

/* Read */
static FmBiscuitDocument* fmBiscuitRead(FcCtx* c, const char* text, const char* sourceName) {
    FcList* errors = fcListNew(c);
    FjError jerr;
    FjValue* root = fjParse(c, text, &jerr, NULL);
    const char* className;
    const char* ns;
    FcList* lines;
    FcList* elements = fcListNew(c);
    FcList* names = fcListNew(c);
    FcList* ports = fcListNew(c);
    const FjValue* list;
    const FjValue* plist;
    FmBiscuitDocument* d;
    int i;
    if (!root) fjThrowInvalid(c, &jerr);
    if (root->kind != FJK_OBJECT) fmThrow1(c, 1, "A .biscuit file is a JSON object.");
    className = fmJsonStr(root, "class");
    if (!className) className = "";
    if (!className[0]) className = fmBiscuitIdentifier(c, fmFileNameWithoutExtension(c, sourceName));
    if (!className[0]) className = "Schematic";
    if (!fmBiscuitIsIdentifier(className)) fcListAdd(c, errors, fmDiag(c, 1, fcFmt(c, "'%s' is no name for a class.", className)));
    ns = fmJsonStr(root, "namespace");
    if (!ns) ns = "";
    if (ns[0]) {
        bool ok = true;
        const char* p = ns;
        for (;;) {
            const char* dot = strchr(p, '.');
            if (!fmBiscuitIsIdentifier(dot ? fcStrN(c, p, (size_t)(dot - p)) : p)) ok = false;
            if (!dot) break;
            p = dot + 1;
        }
        if (!ok) fcListAdd(c, errors, fmDiag(c, 1, fcFmt(c, "'%s' is no namespace.", ns)));
    }

    lines = fmLineStarts(c, text);
    if ((list = fjProp(root, "elements")) != NULL) {
        if (list->kind != FJK_ARRAY) fcListAdd(c, errors, fmDiag(c, 1, "'elements' is a list."));
        else
            for (i = 0; i < list->items->count; i++) {
                const FjValue* e = (const FjValue*)list->items->items[i];
                int line = fmLineOfOffset(lines, fmElementOffset(text, i));
                const char* type;
                const char* name;
                FmBiscuitElement* be;
                if (e->kind != FJK_OBJECT) { fcListAdd(c, errors, fmDiag(c, line, "An element is a JSON object.")); continue; }
                type = fmJsonStr(e, "type");
                if (!type) type = "";
                if (!fmBiscuitFieldType(type)) { fcListAdd(c, errors, fmDiag(c, line, !type[0] ? "An element without a type." : fcFmt(c, "There is no element '%s'.", type))); continue; }
                if (!fmIsPoint(e, "from") || !fmIsPoint(e, "to")) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "The %s needs 'from' and 'to' as [x, y] (whole numbers).", type)));
                name = fmJsonStr(e, "name");
                if (!name) name = "";
                if (name[0]) {
                    if (!fmBiscuitIsIdentifier(name)) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' is no name for an element (letters, digits and '_', not a keyword).", name)));
                    else if (fmInSet(fmBiscuitReserved, name)) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' is a member of Circuits.Circuit: choose another name for the element.", name)));
                    else if (strcmp(name, className) == 0) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' is the name of the class.", name)));
                    else if (fcListHasStr(names, name)) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "Two elements are named '%s'.", name)));
                    else fcListAdd(c, names, (void*)name);
                }
                be = (FmBiscuitElement*)fcAlloc(c, sizeof *be);
                be->type = type;
                be->name = name;
                be->line = line;
                fcListAdd(c, elements, be);
            }
    }
    /* the inputs and outputs: a member each (named like no element and no member of Circuits.Circuit) */
    if ((plist = fjProp(root, "ports")) != NULL) {
        const char* found = strstr(text, "\"ports\"");
        int line = fmLineOfOffset(lines, found ? (long)(found - text) : -1);
        if (plist->kind != FJK_ARRAY) fcListAdd(c, errors, fmDiag(c, line, "'ports' is a list."));
        else
            for (i = 0; i < plist->items->count; i++) {
                const FjValue* p = (const FjValue*)plist->items->items[i];
                const char* name;
                const char* direction;
                const char* pin;
                const char* color;
                FmBiscuitPort* bp;
                if (p->kind != FJK_OBJECT) { fcListAdd(c, errors, fmDiag(c, line, "A port is a JSON object.")); continue; }
                name = fmJsonStr(p, "name"); if (!name) name = "";
                direction = fmJsonStr(p, "direction"); if (!direction) direction = "";
                pin = fmJsonStr(p, "pin"); if (!pin) pin = "";
                color = fmJsonStr(p, "color"); if (!color) color = "";
                if (!direction[0]) direction = "input";
                if (!fmBiscuitIsIdentifier(name)) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' is no name for a port (letters, digits and '_', not a keyword).", name)));
                else if (fmInSet(fmBiscuitReserved, name)) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' is a member of Circuits.Circuit: choose another name for the port.", name)));
                else if (strcmp(name, className) == 0) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "'%s' is the name of the class.", name)));
                else if (fcListHasStr(names, name)) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "The port '%s' is named like an element or another port.", name)));
                else fcListAdd(c, names, (void*)name);
                if (strcmp(direction, "input") != 0 && strcmp(direction, "output") != 0) fcListAdd(c, errors, fmDiag(c, line, fcFmt(c, "The port '%s': the direction is 'input' or 'output'.", name)));
                bp = (FmBiscuitPort*)fcAlloc(c, sizeof *bp);
                bp->name = name;
                bp->direction = direction;
                bp->pin = pin;
                bp->color = color;
                bp->line = line;
                fcListAdd(c, ports, bp);
            }
    }
    if (errors->count > 0) fmThrow(c, errors);
    d = (FmBiscuitDocument*)fcAlloc(c, sizeof *d);
    d->className = className;
    d->ns = ns;
    d->elements = elements;
    d->ports = ports;
    {
        FcBuf b = {0};
        fcBufStr(&b, "");
        fjWrite(&b, root);
        d->compact = fcBufDone(c, &b);
    }
    return d;
}

/* ColorNumber: "#RRGGBB" as the six hex digits (upper case), NULL if it is no color */
static const char* fmColorNumber(FcCtx* c, const char* color) {
    const char* t = fmTrim(c, color);
    int i;
    if (t[0] == '#') t++;
    else if (t[0] == '0' && (t[1] == 'x' || t[1] == 'X')) t += 2;
    if (strlen(t) != 6) return NULL;
    for (i = 0; i < 6; i++) if (!isxdigit((unsigned char)t[i])) return NULL;
    return fmToUpper(c, t);
}

/* Generate */
static const char* fmBiscuitGenerate(FcCtx* c, const char* text, const char* sourceName) {
    FmBiscuitDocument* d = fmBiscuitRead(c, text, sourceName);
    FcBuf sb = {0};
    const char* file = fcBaseName(sourceName);
    const char* indent = "";
    FcList* named = fcListNew(c);
    int i;
    fcBufStr(&sb, "");
    fcBufFmt(&sb, "// Generated from %s (docs/circuits/README.md) - edit the schematic, not this script.\n", file[0] ? file : "a schematic");
    fcBufStr(&sb, "#import \"circuits\"\n");
    if (d->ns[0]) {
        fcBufFmt(&sb, "namespace %s {\n", d->ns);
        indent = "    ";
    }
    fcBufFmt(&sb, "%s/// <summary>The circuit of %s: the parts and connections of the drawing; a field for every element with a name.</summary>\n", indent, file[0] ? file : "the schematic");
    fcBufFmt(&sb, "%sclass %s : Circuits.Circuit {\n", indent, d->className);
    for (i = 0; i < d->elements->count; i++) { FmBiscuitElement* e = (FmBiscuitElement*)d->elements->items[i]; if (e->name[0]) fcListAdd(c, named, e); }
    for (i = 0; i < named->count; i++) { FmBiscuitElement* e = (FmBiscuitElement*)named->items[i]; fcBufFmt(&sb, "%s    %s %s\n", indent, fmBiscuitFieldType(e->type), e->name); }
    if (named->count > 0) fcBufChar(&sb, '\n');
    /* the inputs and outputs: a port each, in the color of the drawing (Colorize is read by the editor only) */
    for (i = 0; i < d->ports->count; i++) {
        FmBiscuitPort* p = (FmBiscuitPort*)d->ports->items[i];
        const char* rgb = fmColorNumber(c, p->color);
        fcBufFmt(&sb, "%s    /// <summary>The %s %s of the circuit%s.</summary>\n", indent, p->direction, p->name, !p->pin[0] ? " (bound to no pin)" : fcFmt(c, ", bound to %s", p->pin));
        if (rgb) fcBufFmt(&sb, "%s    [Colorize 0x%s]\n", indent, rgb);
        fcBufFmt(&sb, "%s    Circuits.Port %s\n", indent, p->name);
    }
    if (d->ports->count > 0) fcBufChar(&sb, '\n');
    fcBufFmt(&sb, "%s    construct() : base(%s) {\n", indent, fmUiQuote(c, d->className));
    fcBufFmt(&sb, "%s        this.LoadSchematic(%s)\n", indent, fmUiQuote(c, d->compact));
    fcBufFmt(&sb, "%s    }\n", indent);
    fcBufChar(&sb, '\n');
    fcBufFmt(&sb, "%s    /// <summary>The schematic made its parts (again): the fields point at them.</summary>\n", indent);
    fcBufFmt(&sb, "%s    override undefined OnBuilt() {\n", indent);
    if (named->count > 0) fcBufFmt(&sb, "%s        var s = this.Schematic\n", indent);
    for (i = 0; i < d->ports->count; i++) {
        FmBiscuitPort* p = (FmBiscuitPort*)d->ports->items[i];
        /* the port of the circuit is there once the schematic bound it to its pin */
        fcBufFmt(&sb, "%s        this.%s = undefined\n", indent, p->name);
        fcBufFmt(&sb, "%s        foreach (port in this.ports) { if (port.name == %s) { this.%s = port } }\n", indent, fmUiQuote(c, p->name), p->name);
    }
    for (i = 0; i < named->count; i++) {
        FmBiscuitElement* e = (FmBiscuitElement*)named->items[i];
        const char* type = fmBiscuitFieldType(e->type);
        const char* q = fmUiQuote(c, e->name);
        if (strcmp(type, "Circuits.Element") == 0) fcBufFmt(&sb, "%s        this.%s = s.Find(%s)\n", indent, e->name, q);
        else if (strcmp(type, "Circuits.Scope") == 0) fcBufFmt(&sb, "%s        this.%s = s.Find(%s).scope\n", indent, e->name, q);
        else {
            fcBufFmt(&sb, "%s        var p_%s = s.Find(%s).part\n", indent, e->name, q);
            fcBufFmt(&sb, "%s        if (p_%s != undefined) { this.%s = p_%s!%s } else { this.%s = undefined }\n", indent, e->name, e->name, e->name, type, e->name);
        }
    }
    fcBufFmt(&sb, "%s    }\n", indent);
    fcBufFmt(&sb, "%s}\n", indent);
    if (d->ns[0]) fcBufStr(&sb, "}\n");
    return fcBufDone(c, &sb);
}
