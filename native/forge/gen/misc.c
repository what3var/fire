/* native/forge/gen/misc.c - the smaller parts of the generator (commit 808e9788): from CppGenerator.cs the constants (UnitId, Constant, DoubleLiteral,
 * CString); from CppGenerator.Modules.cs FunctionPrototypes; from CppGenerator.Debug.cs Nested; from CppGenerator.Reflection.cs what every program needs
 * (RegisterKeptTargets, KeepConstructors, AddFieldName, AddDispatcher - the reflection itself is not ported yet); from CppGenerator.Json.cs ProgramEnum;
 * from CppGenerator.Split.cs the marks; the parts of CppGenerator.PointerCalls.cs, CppGenerator.Reflection.cs, CppGenerator.Json.cs,
 * CppGenerator.Debug.cs and ProgramSplit.cs that are not ported yet refuse (FG_UNPORTED). */

/* ---- Constants (CppGenerator.cs) ---- */
static int UnitId(FgGen* g, const FcU* unit) {
    int i;
    if (!unit || fcUIsUnitless(unit)) return 0;
    for (i = 1; i < g->units->count; i++) {
        const FcU* u = (const FcU*)g->units->items[i];
        if (fcUEquals(u, unit) && streq0(u->display, unit->display)) return i;
    }
    fcListAdd(g->c, g->units, (void*)unit);
    {
        const char* converter = unit->u.converter ? unit->u.converter : unit->u.unitlessConverter;
        if (converter && !fcDictHas(g->unitConverters, converter)) {
            FcRtClass* rc = FindClass(g, converter);
            FgUnitConv* cv = (FgUnitConv*)fcAlloc(g->c, sizeof *cv);
            FcProto* to = FG_FIND(rc, "To", 1).proto;
            FcProto* from = FG_FIND(rc, "From", 1).proto;
            if (!to) FG_NOTSUP("the conversion function %s.To is missing", converter);
            cv->index = fcDictCount(g->unitConverters);
            cv->to = GetFunc(g, to, FK_Static, 0);
            if (!from) FG_NOTSUP("the conversion function %s.From is missing", converter);
            cv->from = GetFunc(g, from, FK_Static, 0);
            fcDictPut(g->unitConverters, converter, cv);
        }
    }
    return g->units->count - 1;
}

/* double.ToString("R", InvariantCulture) of .NET (Core 3.0 on): the shortest text that reads back as the number; `E+XX` when the digits before the
 * point are more than those of the number and more than 15 (a float: 9), `E-XX` below 0.0001 (Number.Formatting: FormatGeneral) */
static const char* fgRoundTrip(FgGen* g, double d, bool single) {
    char buf[64], digits[40];
    int p, exp, nd = 0, i;
    char* e;
    bool neg = d < 0;
    FcBuf b = {0};
    for (p = 0; p <= 17; p++) {
        snprintf(buf, sizeof buf, "%.*e", p, d);
        if (single ? (float)strtod(buf, NULL) == (float)d : strtod(buf, NULL) == d) break;
    }
    /* buf: [-]d.ddde[+-]xx */
    e = strchr(buf, 'e');
    exp = atoi(e + 1);
    for (i = neg ? 1 : 0; buf + i < e; i++) if (buf[i] != '.') digits[nd++] = buf[i];
    while (nd > 1 && digits[nd - 1] == '0') nd--;
    digits[nd] = 0;
    if (neg) fcBufChar(&b, '-');
    if (exp + 1 > (nd > (single ? 9 : 15) ? nd : (single ? 9 : 15)) || exp < -4) {
        fcBufChar(&b, digits[0]);
        if (nd > 1) { fcBufChar(&b, '.'); fcBufStr(&b, digits + 1); }
        fcBufFmt(&b, "E%c%02d", exp < 0 ? '-' : '+', exp < 0 ? -exp : exp);
    } else if (exp < 0) {
        fcBufStr(&b, "0.");
        for (i = 0; i < -exp - 1; i++) fcBufChar(&b, '0');
        fcBufStr(&b, digits);
    } else {
        for (i = 0; i <= exp; i++) fcBufChar(&b, i < nd ? digits[i] : '0');
        if (nd > exp + 1) { fcBufChar(&b, '.'); fcBufStr(&b, digits + exp + 1); }
    }
    return fcBufDone(g->c, &b);
}
static const char* DoubleLiteral(FgGen* g, double d, bool single) {
    const char* text;
    if (isnan(d)) return single ? "nanf(\"\")" : "nan(\"\")";
    if (isinf(d)) return d > 0 ? (single ? "HUGE_VALF" : "HUGE_VAL") : (single ? "-HUGE_VALF" : "-HUGE_VAL");
    if (d == 0 && signbit(d)) return single ? "-0.0f" : "-0.0";
    text = fgRoundTrip(g, single ? (double)(float)d : d, single);
    if (!(strchr(text, '.') || strchr(text, 'E') || strchr(text, 'e'))) text = fcFmt(g->c, "%s.0", text);
    return single ? fcFmt(g->c, "%sf", text) : text;
}
/* A C string literal of the UTF-8 bytes (octal escapes for everything outside printable ASCII) */
static const char* CString(FgGen* g, const char* s) {
    FcBuf out = {0};
    FcBuf b = {0};
    size_t i;
    fcOut8(&out, s);
    fcBufChar(&b, '"');
    for (i = 0; i < out.len; i++) {
        unsigned char ch = (unsigned char)out.p[i];
        if (ch == '"' || ch == '\\') { fcBufChar(&b, '\\'); fcBufChar(&b, (char)ch); }
        else if (ch >= 0x20 && ch < 0x7F && ch != '?') fcBufChar(&b, (char)ch);
        else fcBufFmt(&b, "\\%03o", ch);
    }
    fcBufChar(&b, '"');
    fcBufFree(&out);
    return fcBufDone(g->c, &b);
}
static const char* Constant(FgGen* g, const FcConst* v) {
    switch (v->kind) {
        case CK_Int: {
            const char* lit = v->i == INT64_MIN ? "INT64_MIN" : fcFmt(g->c, "%" PRId64 "LL", v->i);
            int unit;
            if (v->en) return fcFmt(g->c, "EnumV(%s, %s)", lit, EnumC(g, EnumIndex(g, v->en)));
            unit = UnitId(g, v->unit);
            if (v->wide != 0) return fcFmt(g->c, "IntW(%s, %s, %d)", lit, unit == 0 ? "0" : UnitC(g, unit), v->wide);
            return unit == 0 ? fcFmt(g->c, "Int(%s)", lit) : fcFmt(g->c, "Int(%s, %s)", lit, UnitC(g, unit));
        }
        case CK_Float: {
            int unit = UnitId(g, v->unit);
            const char* lit = DoubleLiteral(g, v->f, g->program->floatWidth == 32);
            return unit == 0 ? fcFmt(g->c, "Float(%s)", lit) : fcFmt(g->c, "Float(%s, %s)", lit, UnitC(g, unit));
        }
        case CK_Bool: return v->i ? "Bool(true)" : "Bool(false)";
        case CK_Char: return fcFmt(g->c, "Char(%d)", (int)v->i);
        case CK_Undefined: { int unit = UnitId(g, v->unit); return unit == 0 ? "Undef()" : fcFmt(g->c, "Undef(%s)", UnitC(g, unit)); }
        case CK_String: {
            void* at;
            int index;
            if (fcDictTry(g->stringIndex, v->s, &at)) index = FC_VALI(at) - 1;
            else { index = g->strings->count; fcListAdd(g->c, g->strings, (void*)v->s); fcDictPut(g->stringIndex, v->s, FC_IVAL(index + 1)); g->version++; }
            return fcFmt(g->c, "StrV(&%sK%d)", Px, index);
        }
        default:
            FG_NOTSUP("constant of kind %s", v->kind == CK_Word ? "Word" : "?");
    }
}

/* ---- CppGenerator.Modules.cs: FunctionPrototypes ---- */
static void FunctionPrototypes(FgGen* g, FcBuf* sb) {
    int i, k;
    for (i = 0; i < g->funcs->count; i++) fcBufFmt(sb, "%s;\n", fgSignature(g, (FgFunc*)g->funcs->items[i]));
    for (i = 0; i < g->funcs->count; i++) { FgFunc* f = (FgFunc*)g->funcs->items[i]; if (f->csig) fcBufFmt(sb, "%s;\n", f->csig); }
    {
        FcList* keys = fgSortedKeys(g->c, g->catchCallbacks, true);
        for (i = 0; i < keys->count; i++) fcBufFmt(sb, "static Value %s(Value e) { %s }\n", (const char*)keys->items[i], (const char*)fcDictGet(g->catchCallbacks, keys->items[i]));
    }
    if (Wrapped)
        for (i = 0; i < g->funcs->count; i++) { FgFunc* f = (FgFunc*)g->funcs->items[i]; if (f->kind != FK_Main && !f->external) fcBufFmt(sb, "%s;\n", fgSignatureOf(g, f, fcFmt(g->c, "%s_b", f->name), "")); }
    for (i = 0; i < g->funcs->count; i++) {
        FgFunc* f = (FgFunc*)g->funcs->items[i];
        if (f->kind != FK_Lambda) continue;
        fcBufFmt(sb, "%s { %sreturn %s(lam", fgThunkSignature(g, f), fgParamCount(f) == 0 ? "(void)a; " : "", f->name);
        for (k = 0; k < fgParamCount(f); k++) fcBufFmt(sb, ", a[%d]", k);
        fcBufStr(sb, "); }\n");
    }
    FC_DICT_EACH(g->lambdaDefaults, e) {
        FgFunc* f = (FgFunc*)e->key;
        fcBufFmt(sb, "static Value %s_d(Value lam, uint32_t i) {\n", f->name);
        fcBufStr(sb, "    switch (i) {\n");
        for (k = 0; k < f->proto->paramDefaults->count; k++) {
            FcProto* dp = (FcProto*)f->proto->paramDefaults->items[k];
            if (dp) fcBufFmt(sb, "        case %d: return %s(lamOn(lam));\n", k, ((FgFunc*)fcDictGet(g->funcByProto, dp))->name);
        }
        fcBufStr(sb, "        default: return Undef();\n");
        fcBufStr(sb, "    }\n");
        fcBufStr(sb, "}\n");
    }
}

/* ---- CppGenerator.Debug.cs ---- */
static const char* Nested(FgGen* g, const char* statement) {
    if (!DebugBuild) return statement;
    {
        size_t n = strlen(statement);
        while (n > 0 && statement[n - 1] == ';') n--;
        return fcFmt(g->c, "FIRE_DBG_NESTED(%s);", fcStrN(g->c, statement, n));
    }
}
static const char* DebugProgramTables(FgGen* g) { FG_UNPORTED("debug builds"); }

/* ---- CppGenerator.Reflection.cs: what every program needs ---- */
static bool KeptClass(FgGen* g, const FcRtClass* rc) { return rc->keep || fcDictHas(g->literalClasses, rc->name); }
static void AddDispatcher(FgGen* g, const char* name, int argc) { if (!fgHasNameArgc(g, g->dispatchers, name, argc)) { fgAddNameArgc(g, g->dispatchers, name, argc); g->version++; } }
static void AddFieldName(FgGen* g, const char* name) { if (fcSetAdd(g->fieldNames, name)) g->version++; }
/* The constructors of a class (with the functions of their default values): it can be made. */
static void KeepConstructors(FgGen* g, const FcRtClass* rc) {
    FcList* all;
    int i, argc;
    RegisterClass(g, rc->name);
    all = fgAllConstructors(g->c, rc);
    for (i = 0; i < all->count; i++) {
        FcProto* ctor = (FcProto*)all->items[i];
        int needed = ctor->paramCount;
        const char* args;
        GetFunc(g, ctor, FK_Ctor, 0);
        while (needed > 0 && needed - 1 < ctor->paramDefaults->count && ctor->paramDefaults->items[needed - 1]) needed--;
        for (argc = needed; argc < ctor->paramCount; argc++) DefaultArgsPre(g, ctor, argc, "self", "list", &args);
    }
}
/* An instance method that a call by name may reach: its dispatchers for every number of arguments it takes. */
static void KeepInstanceMethod(FgGen* g, const char* name, FcProto* m) {
    int required = m->paramCount, argc;
    GetFunc(g, m, FK_Method, 0);
    while (required > 0 && required - 1 < m->paramDefaults->count && m->paramDefaults->items[required - 1]) required--;
    for (argc = required; argc <= m->paramCount; argc++) AddDispatcher(g, name, argc);
    if (fcStartsWith(name, "get_") || fcStartsWith(name, "set_")) AddFieldName(g, name + 4);
}
static void KeepMethod(FgGen* g, const char* name, FcProto* m) {
    if (m->isStatic) GetFunc(g, m, FK_Static, 0);
    else KeepInstanceMethod(g, name, m);
}
static void RegisterReflectionTargets(FgGen* g) { if (g->usesReflection) FG_UNPORTED("reflection"); }
/* What stays although nothing may reach it (the stripping levels) */
static void RegisterKeptTargets(FgGen* g) {
    FcList* classes = fcListNew(g->c);
    int i, k;
    FC_DICT_EACH(g->program->program->classes, e) fcListAdd(g->c, classes, e->value);
    for (i = 0; i < classes->count; i++) {
        FcRtClass* rc = (FcRtClass*)classes->items[i];
        if (g->strip == 0 || KeptClass(g, rc)) {
            FcDict* index = FG_INDEX(rc);
            KeepConstructors(g, rc);
            FC_DICT_EACH(index, f) AddFieldName(g, (const char*)f->key);
            FC_DICT_EACH(rc->methods, m) { FcList* protos = (FcList*)m->value; for (k = 0; k < protos->count; k++) KeepMethod(g, (const char*)m->key, (FcProto*)protos->items[k]); }
            continue;
        }
        if (g->strip <= 2) KeepConstructors(g, rc);
        if (g->strip == 1)
            FC_DICT_EACH(rc->methods, m) { FcList* protos = (FcList*)m->value; for (k = 0; k < protos->count; k++) if (fgAccessOf((FcProto*)protos->items[k]) == ACC_Public) KeepMethod(g, (const char*)m->key, (FcProto*)protos->items[k]); }
        if (rc->keptMembers) {
            RegisterClass(g, rc->name);
            for (k = 0; k < rc->keptMembers->count; k++) {
                const char* name = (const char*)rc->keptMembers->items[k];
                const char* methodNames[3];
                int n, p;
                if (fgHasField(g, rc, name)) AddFieldName(g, name);
                methodNames[0] = name; methodNames[1] = fcFmt(g->c, "get_%s", name); methodNames[2] = fcFmt(g->c, "set_%s", name);
                for (n = 0; n < 3; n++) {
                    FcList* protos = (FcList*)fcDictGet(rc->methods, methodNames[n]);
                    if (protos) for (p = 0; p < protos->count; p++) KeepMethod(g, methodNames[n], (FcProto*)protos->items[p]);
                }
            }
        }
    }
}
static const char* WrapProbeSetter(FgGen* g, const char* name, const char* text) { (void)name; (void)text; FG_UNPORTED("probes"); }
static FcList* ComputedSources(FgGen* g, const FcRtClass* rc, const char* name) { (void)rc; (void)name; FG_UNPORTED("probes"); }
static const char* ReflectionCode(FgGen* g) { FG_UNPORTED("reflection"); }

/* ---- CppGenerator.Json.cs ---- */
/* ProgramEnum: the enum of this name in the program; false: none */
static bool ProgramEnumKnown(FgGen* g, const char* name, const FcEnumType** type) {
    FcList* enums = g->program->program->enums;
    int i;
    for (i = 0; i < enums->count; i++) if (streq0(((const FcEnumType*)enums->items[i])->name, name)) { *type = (const FcEnumType*)enums->items[i]; return true; }
    for (i = 0; i < enums->count; i++) if (fgEnumMatches((const FcEnumType*)enums->items[i], name)) { *type = (const FcEnumType*)enums->items[i]; return true; }
    return false;
}

/* ---- the C signatures of `Library.Get` ---- */
static const char* LibraryThunks(FgGen* g) { FG_UNPORTED("Library.Get"); }

/* ---- CppGenerator.PointerCalls.cs (only the PC targets of Open Watcom) ---- */
static bool PointerCallsOnOf(FgGen* g) { return g->target->pointerCalls && !DebugBuild && !Wrapped && !Modular && !g->openWorld; }
static const char* PointerFunction(FgGen* g, const char* name, const char* code, const char* signature) { (void)name; (void)code; (void)signature; FG_UNPORTED("pointer calls"); }
static const char* PointerCallsPass(FgGen* g, const char* text) { (void)text; FG_UNPORTED("pointer calls"); }

/* ---- CppGenerator.Split.cs ---- */
#define SplitMarkPrefix "//@@fire-split "
static const char* ProgramSplitMarker(FgGen* g, const char* what) { return fcFmt(g->c, SplitMarkPrefix "%s@@", what); }
static const char* WithoutSplitMarks(FgGen* g, const char* text) {
    FcBuf b = {0};
    const char* p = text;
    size_t ml = strlen(SplitMarkPrefix);
    fcBufStr(&b, "");
    while (*p) {
        const char* e = strchr(p, '\n');
        const char* next = e ? e + 1 : p + strlen(p);
        if (strncmp(p, SplitMarkPrefix, ml) != 0) fcBufPut(&b, p, (size_t)(next - p));
        p = next;
    }
    return fcBufDone(g->c, &b);
}
static FgFuncKey* SplitKeyOf(FgGen* g, FgFunc* f) { (void)f; FG_UNPORTED("split units"); }
static FcList* ProgramSplitSplit(FgGen* g, const char* programText, FcList* splitKeys) { (void)programText; (void)splitKeys; FG_UNPORTED("split units"); }
