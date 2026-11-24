/* native/forge/units.c - the part of src/fire/Values/Unit.cs (commit a3e43117) that the parser needs: Unit.Parse (dimensions, scale, offset,
 * conversion functions; the names only as far as a literal shows them), CaretExponents, NiceFactor, the units of the program (CustomUnit,
 * UseCustomUnits, IsCustomUnitWord, BuiltInMeaning). Texts are fc8. */

#define FC_MAX_DIMS 16
typedef struct FcUnit {
    int dimCount;
    const char* dimSym[FC_MAX_DIMS];
    int dimExp[FC_MAX_DIMS];
    double scale, offset;
    const char* converter;
    const char* unitlessConverter;
} FcUnit;

struct FcCustomUnit {
    const char* name;
    const char* symbol;
    bool prefixed;
    FcUnit unit;   /* its dimensions, scale, offset, converter, unitless converter */
};

typedef struct FcNamedUnit { const char* symbol; const char* base; int exponent; double scale; } FcNamedUnit;
static const FcNamedUnit fcNamedUnits[] = {
    {"m", "m", 1, 1.0}, {"g", "g", 1, 1.0}, {"s", "s", 1, 1.0}, {"b", "b", 1, 1.0}, {"B", "b", 1, 8.0}, {"l", "m", 3, 0.001},
    {"min", "s", 1, 60.0}, {"h", "s", 1, 3600.0}, {"d", "s", 1, 86400.0},
};

static const FcNamedUnit* fcNamed(const char* s) {
    size_t i;
    for (i = 0; i < sizeof fcNamedUnits / sizeof fcNamedUnits[0]; i++) if (strcmp(fcNamedUnits[i].symbol, s) == 0) return &fcNamedUnits[i];
    return NULL;
}
static bool fcPrefixableBase(const char* s) { return !strcmp(s, "m") || !strcmp(s, "g") || !strcmp(s, "s") || !strcmp(s, "b") || !strcmp(s, "B") || !strcmp(s, "l"); }
static bool fcNonPrefixable(const char* s) { return !strcmp(s, "min") || !strcmp(s, "h") || !strcmp(s, "d"); }

/* the first UTF-16 unit of an fc8 text and the text behind it */
static uint16_t fc8First(const char* s, const char** rest) {
    const unsigned char* p = (const unsigned char*)s;
    uint16_t u = fc8Next(&p);
    *rest = (const char*)p;
    return u;
}

/* Unit.Prefixes */
static bool fcPrefix(uint16_t c, double* factor) {
    switch (c) {
        case 'f': *factor = 1e-15; return true;
        case 'p': *factor = 1e-12; return true;
        case 'n': *factor = 1e-9; return true;
        case 0xB5: case 0x3BC: case 'u': *factor = 1e-6; return true;
        case 'm': *factor = 1e-3; return true;
        case 'c': *factor = 1e-2; return true;
        case 'd': *factor = 1e-1; return true;
        case 'h': *factor = 1e2; return true;
        case 'k': *factor = 1e3; return true;
        case 'M': *factor = 1e6; return true;
        case 'G': *factor = 1e9; return true;
        default: return false;
    }
}
static bool fcPrefixAllowed(uint16_t prefix, const char* symbol) {
    return !((prefix == 'd' || prefix == 'h') && (!strcmp(symbol, "b") || !strcmp(symbol, "B")));
}

static bool fcIsUnitLetter(uint16_t c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == 0xB5 || c == 0x3BC; }

/* Unit.CaretExponents */
static const char* fcCaretExponents(FcCtx* c, const char* text) {
    FcBuf b = {0};
    const unsigned char* p = (const unsigned char*)text;
    uint16_t prev = 0;
    bool any = false, first = true;
    fcBufStr(&b, "");
    while (*p) {
        uint16_t u = fc8Next(&p);
        if (u >= '0' && u <= '9' && !first && fcIsUnitLetter(prev)) { fcBufChar(&b, '^'); any = true; }
        fcBufUnit(&b, u);
        prev = u;
        first = false;
    }
    if (!any) { fcBufFree(&b); return text; }
    return fcBufDone(c, &b);
}

/* Unit.NiceFactor: a factor a few units in the last place away from a number with 12 significant digits is that number */
static double fcNiceFactor(double factor) {
    char t[64];
    double nice;
    snprintf(t, sizeof t, "%.12g", factor);
    nice = strtod(t, NULL);
    return fabs(nice - factor) <= 8 * 2.220446049250313e-16 * fabs(factor) ? nice : factor;
}

/* .NET's ToString("G10") of a double */
static const char* fcG10(FcCtx* c, double v) {
    char t[64];
    char* p;
    if (isnan(v)) return "NaN";
    if (isinf(v)) return v > 0 ? "\xE2\x88\x9E" : "-\xE2\x88\x9E";
    snprintf(t, sizeof t, "%.10g", v);
    for (p = t; *p; p++) if (*p == 'e') *p = 'E';
    return fcStr(c, t);
}

static void fcUnitSetUnitless(FcUnit* u) { memset(u, 0, sizeof *u); u->scale = 1.0; }
static bool fcUnitIsUnitless(const FcUnit* u) { return u->dimCount == 0; }
static int fcUnitDim(const FcUnit* u, const char* sym) {
    int i;
    for (i = 0; i < u->dimCount; i++) if (strcmp(u->dimSym[i], sym) == 0) return i;
    return -1;
}
static void fcUnitAddDim(FcUnit* u, const char* sym, int exp) {
    int i = fcUnitDim(u, sym);
    if (i < 0) {
        if (u->dimCount == FC_MAX_DIMS) return;
        u->dimSym[u->dimCount] = sym;
        u->dimExp[u->dimCount] = exp;
        u->dimCount++;
        i = u->dimCount - 1;
    } else u->dimExp[i] += exp;
    if (u->dimExp[i] == 0) {
        for (; i + 1 < u->dimCount; i++) { u->dimSym[i] = u->dimSym[i + 1]; u->dimExp[i] = u->dimExp[i + 1]; }
        u->dimCount--;
    }
}
static bool fcUnitCompatible(const FcUnit* a, const FcUnit* b) {
    int i;
    if (a->dimCount != b->dimCount) return false;
    for (i = 0; i < a->dimCount; i++) {
        int k = fcUnitDim(b, a->dimSym[i]);
        if (k < 0 || b->dimExp[k] != a->dimExp[i]) return false;
    }
    return true;
}
static bool fcSameOptText(const char* a, const char* b) { return a == b || (a && b && strcmp(a, b) == 0); }
static bool fcUnitEquals(const FcUnit* a, const FcUnit* b) {
    return fcUnitCompatible(a, b) && fabs(a->scale - b->scale) < 1e-12 && a->offset == b->offset && fcSameOptText(a->converter, b->converter)
        && fcSameOptText(a->unitlessConverter, b->unitlessConverter);
}

static FcCustomUnit* fcCustomFind(FcCtx* c, const char* key) { return (FcCustomUnit*)fbMapGet(&c->customTable, key); }

/* Unit.UseCustomUnits */
static void fcUseCustomUnits(FcCtx* c, FcList* units) {
    int i;
    fbMapFree(&c->customTable);
    c->customUnits = fcListNew(c);
    if (!units) return;
    for (i = 0; i < units->count; i++) {
        FcCustomUnit* u = (FcCustomUnit*)units->items[i];
        fcListAdd(c, c->customUnits, u);
        fbMapPut(&c->customTable, u->symbol, u);
        if (strcmp(u->name, u->symbol) != 0 && !fbMapHas(&c->customTable, u->name)) fbMapPut(&c->customTable, u->name, u);
    }
}

/* Unit.IsCustomUnitWord */
static bool fcIsCustomUnitWord(FcCtx* c, const char* word) {
    const char* rest;
    uint16_t first;
    double factor;
    FcCustomUnit* u;
    if (fcCustomFind(c, word)) return true;
    if (fc8Units(word) < 2) return false;
    first = fc8First(word, &rest);
    if (!fcPrefix(first, &factor)) return false;
    u = fcCustomFind(c, rest);
    return u && u->prefixed && strcmp(u->symbol, rest) == 0 && fcPrefixAllowed(first, u->symbol);
}

/* Unit.BuiltInMeaning */
static const char* fcBuiltInMeaning(const char* word) { return fcNamed(word) ? word : NULL; }

static bool fcTryBuiltIn(const char* symbol, FcUnit* unit) {
    const FcNamedUnit* named = fcNamed(symbol);
    fcUnitSetUnitless(unit);
    if (named) { fcUnitAddDim(unit, named->base, named->exponent); unit->scale = named->scale; return true; }
    if (fc8Units(symbol) >= 2) {
        const char* rest;
        uint16_t first = fc8First(symbol, &rest);
        double factor;
        const FcNamedUnit* base = fcNamed(rest);
        if (fcPrefix(first, &factor) && base && fcPrefixableBase(rest) && !fcNonPrefixable(rest) && fcPrefixAllowed(first, rest)) {
            fcUnitAddDim(unit, base->base, base->exponent);
            unit->scale = factor * base->scale;
            return true;
        }
    }
    return false;
}

static void fcUnitParseWith(FcCtx* c, const char* symbol, FcUnit* out);

static void fcUnitMultiply(FcUnit* result, const FcUnit* aIn, const FcUnit* bIn, bool divide) {
    FcUnit a = *aIn, b = *bIn, r;
    int i;
    if (a.unitlessConverter) fcUnitSetUnitless(&a);
    if (b.unitlessConverter) fcUnitSetUnitless(&b);
    if (fcUnitIsUnitless(&b)) { *result = a; return; }
    if (!divide && fcUnitIsUnitless(&a)) { *result = b; return; }
    r = a;
    r.offset = 0; r.converter = NULL; r.unitlessConverter = NULL;
    for (i = 0; i < b.dimCount; i++) fcUnitAddDim(&r, b.dimSym[i], divide ? -b.dimExp[i] : b.dimExp[i]);
    r.scale = divide ? a.scale / b.scale : a.scale * b.scale;
    *result = r;
}

/* Unit.ParseCombined (knownOnly: false) - the dimensions and the scale; the result is linear */
static void fcUnitParseCombined(FcCtx* c, const char* symbol, FcUnit* out) {
    FcUnit result;
    bool divide = false;
    size_t i = 0, n = strlen(symbol);
    fcUnitSetUnitless(&result);
    while (i < n) {
        size_t start = i;
        FcUnit factor;
        int power = 1, p;
        while (i < n && symbol[i] != '/' && symbol[i] != '*' && symbol[i] != '^') i++;
        fcUnitParseWith(c, fcStrN(c, symbol + start, i - start), &factor);
        if (i < n && symbol[i] == '^') {
            size_t numberStart = ++i;
            bool neg = false;
            int v = 0;
            if (i < n && symbol[i] == '-') { neg = true; i++; }
            while (i < n && symbol[i] >= '0' && symbol[i] <= '9') v = v * 10 + (symbol[i++] - '0');
            (void)numberStart;
            power = neg ? -v : v;
        }
        for (p = 0; p < abs(power); p++) fcUnitMultiply(&result, &result, &factor, divide ^ (power < 0));
        if (i < n) {
            if (symbol[i] == '/') divide = true;
            i++;
        }
    }
    *out = result;
    out->offset = 0; out->converter = NULL; out->unitlessConverter = NULL;
}

static void fcUnitParseWith(FcCtx* c, const char* symbol, FcUnit* out) {
    FcCustomUnit* own;
    fcUnitSetUnitless(out);
    if (!symbol[0]) return;
    if (strpbrk(symbol, "/*^")) { fcUnitParseCombined(c, symbol, out); return; }
    if (fcNamed(symbol) && fcTryBuiltIn(symbol, out)) return;
    if (c->customUnits->count > 0 && (own = fcCustomFind(c, symbol)) != NULL) { *out = own->unit; return; }
    if (fcTryBuiltIn(symbol, out)) return;
    if (fc8Units(symbol) >= 2 && c->customUnits->count > 0) {
        const char* rest;
        uint16_t first = fc8First(symbol, &rest);
        double factor;
        FcCustomUnit* prefixable;
        if (fcPrefix(first, &factor) && (prefixable = fcCustomFind(c, rest)) != NULL && prefixable->prefixed && strcmp(prefixable->symbol, rest) == 0
            && fcPrefixAllowed(first, prefixable->symbol)) {
            fcUnitSetUnitless(out);
            memcpy((void*)out->dimSym, (const void*)prefixable->unit.dimSym, sizeof out->dimSym);   /* (casts: MSVC warns C4090 for an array of const char*) */
            memcpy(out->dimExp, prefixable->unit.dimExp, sizeof out->dimExp);
            out->dimCount = prefixable->unit.dimCount;
            out->scale = factor * prefixable->unit.scale;
            return;
        }
    }
    fcUnitSetUnitless(out);
    fcUnitAddDim(out, symbol, 1);
}

/* Unit.Parse */
static void fcUnitParse(FcCtx* c, const char* symbol, FcUnit* out) {
    if (!symbol || !symbol[0] || strcmp(symbol, "unitless") == 0) { fcUnitSetUnitless(out); return; }
    fcUnitParseWith(c, fcCaretExponents(c, symbol), out);
}

/* the text of Unit.Parse(suffix).ToString() for the unit suffix of a number literal (NULL for unitless): fcLiteralUnitText in unitv.c */
static const char* fcLiteralUnitText(FcCtx* c, const char* suffix);
