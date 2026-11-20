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
/* LibraryMethods.Normalize: without white space */
static const char* fgLibNormalize(FcCtx* c, const char* text) {
    FcBuf b = {0};
    const unsigned char* p = (const unsigned char*)text;
    fcBufStr(&b, "");
    while (*p) { const unsigned char* q = p; uint16_t u = fc8Next(&p); if (!fcIsWhiteSpace(u)) fcBufPut(&b, (const char*)q, (size_t)(p - q)); }
    return fcBufDone(c, &b);
}
/* LibraryMethods.Parse: a C signature - the result type (NULL: void) and the parameter types; false when it is not one */
static FcTypeRef* fgLibType(FcCtx* c, const char* s, int* pos, bool allowVoid, bool* isVoid, bool* bad) {
    int start = *pos, width = -1, depth = 0;
    const char* name;
    FcTypeRef* t;
    bool ok;
    *isVoid = false; *bad = false;
    while (s[*pos] >= 'a' && s[*pos] <= 'z') (*pos)++;
    name = fcStrN(c, s + start, (size_t)(*pos - start));
    if (s[*pos] == '[') {
        const char* close = strchr(s + *pos, ']');
        char* end;
        long w;
        if (!close || close == s + *pos + 1) { *bad = true; return NULL; }
        w = strtol(s + *pos + 1, &end, 10);
        if (end != close) { *bad = true; return NULL; }
        width = (int)w;
        *pos = (int)(close - s) + 1;
    }
    while (s[*pos] == '*') { depth++; (*pos)++; }
    if (streq0(name, "void")) {
        if (width != -1) { *bad = true; return NULL; }
        if (depth == 0) { *isVoid = allowVoid; if (!allowVoid) *bad = true; return NULL; }
        ok = true;
    }
    else if (streq0(name, "bool") || streq0(name, "char") || streq0(name, "string")) ok = width == -1;
    else if (streq0(name, "int")) ok = width == -1 || width == 8 || width == 16 || width == 32 || width == 64;
    else if (streq0(name, "float")) ok = width == -1 || width == 32 || width == 64;
    else ok = false;
    if (!ok) { *bad = true; return NULL; }
    t = (FcTypeRef*)fcAlloc(c, sizeof *t);
    t->baseName = name; t->bitWidth = width < 0 ? 0 : width; t->pointerDepth = depth;
    return t;
}
static bool fgLibParse(FcCtx* c, const char* text, FcTypeRef** ret, FcList** params) {
    const char* s = fgLibNormalize(c, text);
    int pos = 0;
    bool retVoid, bad;
    FcList* ps = fcListNew(c);
    *ret = fgLibType(c, s, &pos, true, &retVoid, &bad);
    if (!*ret && !retVoid) return false;
    if (s[pos] != '(') return false;
    pos++;
    if (s[pos] == ')') pos++;
    else
        for (;;) {
            bool pVoid;
            FcTypeRef* t = fgLibType(c, s, &pos, ps->count == 0, &pVoid, &bad);
            if (pVoid && s[pos] == ')') { pos++; break; }
            if (!t) return false;
            fcListAdd(c, ps, t);
            if (s[pos] == ',') { pos++; continue; }
            if (s[pos] == ')') { pos++; break; }
            return false;
        }
    *params = ps;
    return s[pos] == 0;
}
static void fgLibScan(FgGen* g, FcChunk* chunk, FcDict* seen, FcDict* signatures) {
    int i;
    if (!chunk || !fcSetAdd(seen, chunk)) return;
    for (i = 0; i < chunk->constants->count; i++) {
        const FcConst* k = (const FcConst*)chunk->constants->items[i];
        FcTypeRef* r;
        FcList* ps;
        int n;
        if (k->kind != CK_String) continue;
        n = fc8Units(k->s);
        if (n > 2 && n < 200 && strchr(k->s, '(') && fgLibParse(g->c, k->s, &r, &ps)) fcSetAdd(signatures, fgLibNormalize(g->c, k->s));
    }
    for (i = 0; i < chunk->functions->count; i++) fgLibScan(g, ((FcProto*)chunk->functions->items[i])->chunk, seen, signatures);
}
static void fgLibScanProto(FgGen* g, FcProto* p, FcDict* seen, FcDict* signatures) {
    int i;
    fgLibScan(g, p->chunk, seen, signatures);
    for (i = 0; p->paramDefaults && i < p->paramDefaults->count; i++) if (p->paramDefaults->items[i]) fgLibScanProto(g, (FcProto*)p->paramDefaults->items[i], seen, signatures);
}
/* LibraryThunks: the thunks of the C signatures that are texts of the program (`Library.Get`) */
static const char* LibraryThunks(FgGen* g) {
    FcDict* signatures = fcDictNew(g->c, FC_KEY_STR);
    FcDict* seen = fcDictNew(g->c, FC_KEY_PTR);
    FcList* sigs;
    FcList* rows = fcListNew(g->c);
    FcBuf sb = {0};
    int k, i;
    fgLibScan(g, g->program->program->topLevel, seen, signatures);
    FC_DICT_EACH(g->program->program->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        FC_DICT_EACH(rc->methods, m) { FcList* protos = (FcList*)m->value; for (i = 0; i < protos->count; i++) fgLibScanProto(g, (FcProto*)protos->items[i], seen, signatures); }
        for (i = 0; i < rc->ctorProtos->count; i++) fgLibScanProto(g, (FcProto*)rc->ctorProtos->items[i], seen, signatures);
        for (i = 0; i < rc->extraConstructors->count; i++) fgLibScanProto(g, (FcProto*)rc->extraConstructors->items[i], seen, signatures);
        if (rc->destructor) fgLibScanProto(g, rc->destructor, seen, signatures);
    }
    sigs = fgSortedKeys(g->c, signatures, true);
    fcBufStr(&sb, "/* ---- Library.Get: the thunks of the C signatures that are texts of the program ---- */\n");
    for (k = 0; k < sigs->count; k++) {
        const char* sig = (const char*)sigs->items[k];
        FcTypeRef* ret;
        FcList* ps;
        FcBuf locs = {0};
        FcList* frees = fcListNew(g->c);
        FcList* args = fcListNew(g->c);
        FcList* ctypes = fcListNew(g->c);
        const char* retC;
        const char* call;
        const char* body;
        fgLibParse(g->c, sig, &ret, &ps);
        fcBufStr(&locs, "");
        for (i = 0; i < ps->count; i++) {
            const FcTypeRef* t = (const FcTypeRef*)ps->items[i];
            const char* ctype = ExternCType(g, t, sig);
            fcListAdd(g->c, ctypes, (void*)ctype);
            if (t->pointerDepth > 0) { fcBufFmt(&locs, "ExternPtr p%d; externPtrInit(&p%d, a[%d]); ", i, i, i); fcListAdd(g->c, args, (void*)fcFmt(g->c, "p%d.raw", i)); fcListInsert(g->c, frees, 0, (void*)fcFmt(g->c, "externPtrFree(&p%d); ", i)); }
            else if (streq0(t->baseName, "string")) { fcBufFmt(&locs, "ExternString p%d; externStringInit(&p%d, a[%d]); ", i, i, i); fcListAdd(g->c, args, (void*)fcFmt(g->c, "p%d.p", i)); fcListInsert(g->c, frees, 0, (void*)fcFmt(g->c, "externStringFree(&p%d); ", i)); }
            else if (streq0(t->baseName, "float")) fcListAdd(g->c, args, (void*)fcFmt(g->c, "(%s)externFloat(a[%d])", ctype, i));
            else fcListAdd(g->c, args, (void*)fcFmt(g->c, "(%s)externInt(a[%d])", ctype, i));
        }
        retC = !ret ? "void" : ExternCType(g, ret, sig);
        call = fcFmt(g->c, "((%s (*)(%s))fn_)(%s)", retC, ctypes->count == 0 ? "void" : fgJoin(g->c, ctypes, ", "), fgJoin(g->c, args, ", "));
        if (!ret) body = fcFmt(g->c, "%s; %sreturn Undef();", call, fgJoin(g->c, frees, ""));
        else {
            const char* conv = ret->pointerDepth > 0 ? "Int((int64_t)(intptr_t)r_)" : streq0(ret->baseName, "bool") ? "Bool(r_ != 0)" : streq0(ret->baseName, "float") ? "Float((Real)r_)"
                : streq0(ret->baseName, "char") ? "Char((uint32_t)r_)" : streq0(ret->baseName, "string") ? "externResultString(r_, list); if (release_ && r_) ((void (*)(void*))release_)((void*)(uintptr_t)r_)" : "Int((int64_t)r_)";
            body = fcFmt(g->c, "%s r_ = %s; Value v_; %sv_ = %s; return v_;", retC, call, fgJoin(g->c, frees, ""), conv);
        }
        fcBufFmt(&sb, "static Value fire_lt_%d(void* fn_, const Value* a, void* release_, OwnList* list) { (void)a; (void)release_; (void)list; %s%s }   /* %s */\n", k, fcBufDone(g->c, &locs), body, sig);
        fcListAdd(g->c, rows, (void*)fcFmt(g->c, "{%s, fire_lt_%d}", CString(g, sig), k));
    }
    fcBufStr(&sb, "FireLibThunk fire_libThunk(const char* signature) {\n");
    fcListAdd(g->c, rows, (void*)"{NULL, NULL}");
    fcBufFmt(&sb, "    static const struct { const char* sig; FireLibThunk fn; } k[] = {%s};\n", fgJoin(g->c, rows, ", "));
    fcBufFmt(&sb, "    int i;\n    for (i = 0; i < %d; i++) if (strcmp(k[i].sig, signature) == 0) return k[i].fn;\n", rows->count - 1);
    fcBufStr(&sb, "    return NULL;\n");
    fcBufStr(&sb, "}\n");
    return fcBufDone(g->c, &sb);
}

/* ---- CppGenerator.PointerCalls.cs (only the PC targets of Open Watcom): the Values of a fire function by address; the regular expressions written out ---- */
static bool PointerCallsOnOf(FgGen* g) { return g->target->pointerCalls && !DebugBuild && !Wrapped && !Modular && !g->openWorld; }
typedef struct FgPDecl { const char* decl; const char* name; bool isValue; } FgPDecl;
typedef struct FgPFunc { FcList* names; bool* byAddress; int count; } FgPFunc;
static bool fgPcWord(char ch) { return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || (unsigned char)ch >= 0x80; }
static bool fgPcSpace(char ch) { return ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '\f' || ch == '\v'; }
/* ParamDecls: the parameters of a C parameter list */
static FcList* fgParamDecls(FgGen* g, const char* list) {
    FcList* r = fcListNew(g->c);
    FcList* parts;
    const char* t = fgTrim(g->c, list);
    int i;
    if (!*t || streq0(t, "void")) return r;
    parts = fgSplit(g->c, list, ',');
    for (i = 0; i < parts->count; i++) {
        const char* d = fgTrim(g->c, (const char*)parts->items[i]);
        const char* sp = NULL;
        const char* q;
        FgPDecl* p = (FgPDecl*)fcAlloc(g->c, sizeof *p);
        for (q = d; *q; q++) if (*q == ' ' || *q == '*') sp = q;
        p->decl = d; p->name = sp ? sp + 1 : d; p->isValue = fcStartsWith(d, "Value ");
        fcListAdd(g->c, r, p);
    }
    return r;
}
static const char* fgPointerParams(FgGen* g, FcList* ps) {
    FcList* l = fcListNew(g->c);
    int i;
    for (i = 0; i < ps->count; i++) { FgPDecl* p = (FgPDecl*)ps->items[i]; fcListAdd(g->c, l, (void*)(p->isValue ? fcFmt(g->c, "const Value* %s_p", p->name) : p->decl)); }
    return fgJoin(g->c, l, ", ");
}
/* `^\s*(?:=(?!=)|\+=|-=|\*=|/=|\|=|&=|\+\+|--)` */
static bool fgPcAssignOp(const char* p) {
    while (fgPcSpace(*p)) p++;
    if (p[0] == '=' && p[1] != '=') return true;
    if ((p[0] == '+' || p[0] == '-' || p[0] == '*' || p[0] == '/' || p[0] == '|' || p[0] == '&') && p[1] == '=') return true;
    return (p[0] == '+' && p[1] == '+') || (p[0] == '-' && p[1] == '-');
}
/* ReadOnlyAddressCall: `\b(?:leafAliveP|...)\(\s*&\s*$` on the text before (trimmed at its end) */
static bool fgPcReadOnlyAddressCall(const char* before, size_t n) {
    static const char* const names[] = { "leafAliveP", "destroyedErrorP", "retainP", "releaseP", "truthyP", "objAtP", "adoptP", "ctorEndP", "hoistFromP", "transferTreeP", "finishArgsP", "claimToListP" };
    size_t k, end;
    if (n == 0 || before[n - 1] != '&') return false;
    end = n - 1;
    while (end > 0 && fgPcSpace(before[end - 1])) end--;
    if (end == 0 || before[end - 1] != '(') return false;
    end--;
    for (k = 0; k < sizeof names / sizeof names[0]; k++) {
        size_t len = strlen(names[k]);
        if (end >= len && strncmp(before + end - len, names[k], len) == 0 && (end == len || !fgPcWord(before[end - len - 1]))) return true;
    }
    return false;
}
/* WritesParam: a store, `++`/`--`, a member store, or its address given to anything but a runtime function that only reads */
static bool fgWritesParam(const char* body, const char* p) {
    size_t plen = strlen(p), blen = strlen(body), at;
    for (at = 0; at + plen <= blen; at++) {
        size_t afterAt, bStart, bLen;
        char after[65];
        if (strncmp(body + at, p, plen) != 0) continue;
        if (at > 0 && (fgPcWord(body[at - 1]) || body[at - 1] == '.' || body[at - 1] == '>')) continue;
        if (at + plen < blen && fgPcWord(body[at + plen])) continue;
        afterAt = at + plen;
        { size_t n = blen - afterAt < 64 ? blen - afterAt : 64; memcpy(after, body + afterAt, n); after[n] = 0; }
        if (fgPcAssignOp(after)) return true;
        {
            const char* q = after;
            while (fgPcSpace(*q)) q++;
            if (*q == '.') {
                q++;
                while (fgPcSpace(*q)) q++;
                if (fgPcWord(*q) && !((unsigned char)*q >= 0x80)) {
                    while (fgPcWord(*q)) q++;
                    if (fgPcAssignOp(q)) return true;
                }
            }
        }
        bStart = at >= 64 ? at - 64 : 0;
        bLen = at - bStart;
        while (bLen > 0 && fgPcSpace(body[bStart + bLen - 1])) bLen--;
        if (bLen >= 2 && ((body[bStart + bLen - 2] == '+' && body[bStart + bLen - 1] == '+') || (body[bStart + bLen - 2] == '-' && body[bStart + bLen - 1] == '-'))) return true;
        if (bLen >= 1 && body[bStart + bLen - 1] == '&' && !(bLen >= 2 && body[bStart + bLen - 2] == '&') && !fgPcReadOnlyAddressCall(body + bStart, bLen)) return true;
    }
    return false;
}
/* PointerFunction: the function `name` in `code` with its Value parameters by address, or the text unchanged when it cannot have it */
static const char* PointerFunction(FgGen* g, const char* name, const char* code, const char* signature) {
    const char* openP;
    const char* sigP;
    const char* endP;
    FcList* decls;
    FcList* ps;
    FcDict* written;
    int i, open, sigAt, bodyAt, endAt;
    size_t slen = strlen(signature);
    const char* body;
    const char* rest;
    const char* head;
    const char* newSignature;
    const char* brace;
    FcBuf copies = {0};
    FcBuf sb = {0};
    FgPFunc* pf;
    fcDictRemove(g->pointerFuncs, name);
    openP = strstr(signature, fcFmt(g->c, "%s(", name));
    sigP = strstr(code, fcFmt(g->c, "%s\n", signature));
    if (!openP || !sigP || slen == 0 || signature[slen - 1] != ')') return code;
    open = (int)(openP - signature); sigAt = (int)(sigP - code);
    decls = fgParamDecls(g, fcStrN(g->c, signature + open + strlen(name) + 1, slen - (size_t)open - strlen(name) - 2));
    ps = fcListNew(g->c);
    for (i = 0; i < decls->count; i++) if (((FgPDecl*)decls->items[i])->isValue) fcListAdd(g->c, ps, (void*)((FgPDecl*)decls->items[i])->name);
    if (ps->count == 0) return code;
    bodyAt = sigAt + (int)slen + 1;
    endP = strstr(code + bodyAt - 1, "\n}\n");
    if (!endP) return code;
    endAt = (int)(endP - code);
    body = fcStrN(g->c, code + bodyAt, (size_t)(endAt + 3 - bodyAt));
    rest = code + endAt + 3;
    written = fcDictNew(g->c, FC_KEY_STR);
    for (i = 0; i < ps->count; i++) if (fgWritesParam(body, (const char*)ps->items[i])) fcSetAdd(written, ps->items[i]);
    head = fcStrN(g->c, signature, (size_t)open);
    newSignature = fcFmt(g->c, "%s%s_p(%s)", head, name, fgPointerParams(g, decls));
    if (!(brace = strstr(body, "{\n"))) return code;
    fcBufStr(&copies, "");
    for (i = 0; i < ps->count; i++) if (fcDictHas(written, ps->items[i])) fcBufFmt(&copies, "    Value %s = *%s_p;\n", (const char*)ps->items[i], (const char*)ps->items[i]);
    body = fcFmt(g->c, "%s%s%s", fcStrN(g->c, body, (size_t)(brace - body + 2)), fcBufDone(g->c, &copies), brace + 2);
    fcBufPut(&sb, code, (size_t)sigAt);
    for (i = 0; i < ps->count; i++) if (!fcDictHas(written, ps->items[i])) fcBufFmt(&sb, "#define %s (*%s_p)\n", (const char*)ps->items[i], (const char*)ps->items[i]);
    fcBufFmt(&sb, "%s\n%s", newSignature, body);
    for (i = 0; i < ps->count; i++) if (!fcDictHas(written, ps->items[i])) fcBufFmt(&sb, "#undef %s\n", (const char*)ps->items[i]);
    fcBufStr(&sb, rest);
    pf = (FgPFunc*)fcAlloc(g->c, sizeof *pf);
    pf->names = fcListNew(g->c); pf->count = decls->count;
    pf->byAddress = (bool*)fcAlloc(g->c, sizeof(bool) * (size_t)(decls->count + 1));
    for (i = 0; i < decls->count; i++) { fcListAdd(g->c, pf->names, (void*)((FgPDecl*)decls->items[i])->name); pf->byAddress[i] = ((FgPDecl*)decls->items[i])->isValue; }
    fcDictPut(g->pointerFuncs, name, pf);
    return fcBufDone(g->c, &sb);
}
/* AddressableArg: `^(?!(?:G|SF)\d+$)(?!g_)[A-Za-z_]\w*(?:\[\d+\])?$` */
static bool fgAddressableArg(const char* a) {
    const char* p = a;
    const char* d;
    if (a[0] == 'G' || (a[0] == 'S' && a[1] == 'F')) {
        d = a + (a[0] == 'G' ? 1 : 2);
        if (*d) { const char* q = d; while (*q >= '0' && *q <= '9') q++; if (!*q) return false; }
    }
    if (a[0] == 'g' && a[1] == '_') return false;
    if (!((*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z') || *p == '_')) return false;
    while (fgPcWord(*p)) p++;
    if (*p == '[') {
        const char* q = p + 1;
        while (*q >= '0' && *q <= '9') q++;
        if (q == p + 1 || *q != ']') return false;
        p = q + 1;
    }
    return *p == 0;
}
static int fgSkipLiteral(const char* s, int at) {
    char q = s[at];
    int i = at + 1, n = (int)strlen(s);
    while (i < n && s[i] != q) i += s[i] == '\\' ? 2 : 1;
    return i + 1 < n ? i + 1 : n;
}
static int fgMatchingParen(const char* s, int open) {
    int depth = 0, i;
    for (i = open; s[i]; i++) {
        char c = s[i];
        if (c == '"' || c == '\'') { i = fgSkipLiteral(s, i) - 1; continue; }
        if (c == '(' || c == '[' || c == '{') depth++;
        else if (c == ')' || c == ']' || c == '}') { if (--depth == 0) return c == ')' ? i : -1; }
    }
    return -1;
}
static FcList* fgSplitArgs(FgGen* g, const char* s) {
    FcList* r = fcListNew(g->c);
    int depth = 0, start = 0, i;
    if (!*fgTrim(g->c, s)) return r;
    for (i = 0; s[i]; i++) {
        char c = s[i];
        if (c == '"' || c == '\'') { i = fgSkipLiteral(s, i) - 1; continue; }
        if (c == '(' || c == '[' || c == '{') depth++;
        else if (c == ')' || c == ']' || c == '}') depth--;
        else if (c == ',' && depth == 0) { fcListAdd(g->c, r, (void*)fgTrim(g->c, fcStrN(g->c, s + start, (size_t)(i - start)))); start = i + 1; }
    }
    fcListAdd(g->c, r, (void*)fgTrim(g->c, s + start));
    return r;
}
/* RewriteCalls: one line - its calls of functions with the convention by address where every argument can be */
static const char* fgRewriteCalls(FgGen* g, const char* line, FcDict* needsValues) {
    const char* t = line;
    FcBuf sb = {0};
    bool any = false;
    int copied = 0, pos = 0, n = (int)strlen(line);
    while (*t == ' ' || *t == '\t') t++;
    if (*t == '#' && !fcStartsWith(t, "#line")) return line;
    while (pos < n) {
        char c = line[pos];
        int end, open, close, k;
        const char* fn;
        FgPFunc* pf;
        FcList* args;
        bool ok;
        if (c == '"' || c == '\'') { pos = fgSkipLiteral(line, pos); continue; }
        if (c == '/' && pos + 1 < n && line[pos + 1] == '/') break;
        if (!(fgPcWord(c) && !(c >= '0' && c <= '9')) || (pos > 0 && fgPcWord(line[pos - 1]))) { pos++; continue; }
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_') || (pos > 0 && (line[pos - 1] == '.' || line[pos - 1] == '>'))) { pos++; continue; }
        for (end = pos; end < n && fgPcWord(line[end]); end++) {}
        fn = fcStrN(g->c, line + pos, (size_t)(end - pos));
        if (!(pf = (FgPFunc*)fcDictGet(g->pointerFuncs, fn))) { pos = end; continue; }
        open = end;
        while (open < n && line[open] == ' ') open++;
        if (open >= n || line[open] != '(') { fcSetAdd(needsValues, fn); pos = end; continue; }
        close = fgMatchingParen(line, open);
        if (close < 0) { fcSetAdd(needsValues, fn); pos = end; continue; }
        args = fgSplitArgs(g, fcStrN(g->c, line + open + 1, (size_t)(close - open - 1)));
        ok = args->count == pf->count;
        for (k = 0; ok && k < args->count; k++) if (pf->byAddress[k] && !fgAddressableArg((const char*)args->items[k])) ok = false;
        if (!ok) { fcSetAdd(needsValues, fn); pos = open + 1; continue; }
        if (!any) { fcBufStr(&sb, ""); any = true; }
        fcBufPut(&sb, line + copied, (size_t)(pos - copied));
        fcBufFmt(&sb, "%s_p(", fn);
        for (k = 0; k < args->count; k++) {
            const char* a = (const char*)args->items[k];
            if (k) fcBufStr(&sb, ", ");
            if (pf->byAddress[k]) fcBufFmt(&sb, "&%s", a);
            else fcBufStr(&sb, fgRewriteCalls(g, a, needsValues));
        }
        fcBufChar(&sb, ')');
        copied = pos = close + 1;
    }
    if (!any) return line;
    fcBufStr(&sb, line + copied);
    return fcBufDone(g->c, &sb);
}
/* protoLine: `^((?:FIRE_MAYBE_UNUSED )?(?:FIRE_ALWAYS_INLINE )?static (?:inline )?[A-Za-z_]\w* )([A-Za-z_]\w*)\((.*)\);$` */
static bool fgProtoLine(FgGen* g, const char* l, const char** prefix, const char** fn, const char** params) {
    const char* p = l;
    const char* s;
    size_t n = strlen(l);
    if (fcStartsWith(p, "FIRE_MAYBE_UNUSED ")) p += 18;
    if (fcStartsWith(p, "FIRE_ALWAYS_INLINE ")) p += 19;
    if (!fcStartsWith(p, "static ")) return false;
    p += 7;
    if (fcStartsWith(p, "inline ")) {
        /* (`inline` may also be the type: the regular expression tries the optional word first) */
        const char* q = p + 7;
        if ((*q >= 'a' && *q <= 'z') || (*q >= 'A' && *q <= 'Z') || *q == '_') p = q;
    }
    if (!((*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z') || *p == '_')) return false;
    while (fgPcWord(*p)) p++;
    if (*p != ' ') return false;
    p++;
    *prefix = fcStrN(g->c, l, (size_t)(p - l));
    if (!((*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z') || *p == '_')) return false;
    for (s = p; fgPcWord(*p); p++) {}
    *fn = fcStrN(g->c, s, (size_t)(p - s));
    if (*p != '(' || n < 2 || l[n - 1] != ';' || l[n - 2] != ')' || (size_t)(p - l) + 1 > n - 2) return false;
    *params = fcStrN(g->c, p + 1, n - 2 - (size_t)(p + 1 - l));
    return true;
}
/* PointerCallsPass: the calls of the whole program (not the C of the packages) to the functions with the convention */
static const char* PointerCallsPass(FgGen* g, const char* text) {
    FcList* lines;
    bool* ours;
    bool inPackage = false;
    FcDict* needsValues;
    int i;
    if (fcDictCount(g->pointerFuncs) == 0) return text;
    lines = fgSplit(g->c, text, '\n');
    ours = (bool*)fcAlloc(g->c, sizeof(bool) * (size_t)(lines->count + 1));
    for (i = 0; i < lines->count; i++) {
        const char* l = (const char*)lines->items[i];
        if (fcStartsWith(l, "// ---- end of ")) { inPackage = false; ours[i] = false; continue; }
        if (fcStartsWith(l, "// ---- ") && strstr(l, "import \"")) { inPackage = true; ours[i] = false; continue; }
        ours[i] = !inPackage;
    }
    needsValues = fcDictNew(g->c, FC_KEY_STR);
    for (i = 0; i < lines->count; i++) {
        const char* prefix;
        const char* fn;
        const char* params;
        if (!ours[i]) continue;
        if (fgProtoLine(g, (const char*)lines->items[i], &prefix, &fn, &params) && fcDictHas(g->pointerFuncs, fn)) continue;
        lines->items[i] = (void*)fgRewriteCalls(g, (const char*)lines->items[i], needsValues);
    }
    for (i = 0; i < lines->count; i++) {
        const char* prefix;
        const char* fn;
        const char* params;
        FcList* decls;
        const char* proto;
        if (!ours[i] || !fgProtoLine(g, (const char*)lines->items[i], &prefix, &fn, &params) || !fcDictHas(g->pointerFuncs, fn)) continue;
        decls = fgParamDecls(g, params);
        proto = fcFmt(g->c, "%s%s_p(%s);", prefix, fn, fgPointerParams(g, decls));
        if (fcDictHas(needsValues, fn)) {
            FcList* args = fcListNew(g->c);
            int k;
            for (k = 0; k < decls->count; k++) { FgPDecl* p = (FgPDecl*)decls->items[k]; fcListAdd(g->c, args, (void*)(p->isValue ? fcFmt(g->c, "&%s", p->name) : p->name)); }
            proto = fcFmt(g->c, "%s\nFIRE_MAYBE_UNUSED %s%s(%s) { return %s_p(%s); }", proto, fgReplace(g->c, prefix, "FIRE_MAYBE_UNUSED ", ""), fn, params, fn, fgJoin(g->c, args, ", "));
        }
        lines->items[i] = (void*)proto;
    }
    return fgJoin(g->c, lines, "\n");
}

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
