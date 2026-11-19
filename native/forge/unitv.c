/* native/forge/unitv.c - the unit as a value of the type checker: src/fire/Values/Unit.cs (commit c7f13d97) - Parse with the name of the unit
 * (ParseWith, ParseCombined, the names of combined units: TermsOfText, MergeTerms, SimplifyTerms, RenderTerms, CombinedName), Multiply, Divide,
 * Equals, IsCompatibleWith, IsLinear, ConversionFactorTo, ToString, DemandText. The dimensions, the factor and the conversions are a FcUnit of
 * units.c; Unit.Unitless is fcUnitlessU (the same object every time, as in C#). Not ported: DecodeDemand (a demand text is no source text), the
 * caches of the last product and quotient (they only save work). */

typedef struct FcTerm { const char* sym; int exp; } FcTerm;
typedef struct FcTerms { FcTerm* items; int count, cap; } FcTerms;
typedef struct FcU { FcUnit u; const char* display; FcTerms* terms; bool termsKnown; } FcU;

static const FcU fcUnitlessU = { { 0, {0}, {0}, 1.0, 0.0, NULL, NULL }, NULL, NULL, true };
#define FC_UNITLESS ((FcU*)&fcUnitlessU)

static FcU* fcUNew(FcCtx* c, const FcUnit* u, const char* display) {
    FcU* r = (FcU*)fcAlloc(c, sizeof *r);
    r->u = *u;
    r->display = display;
    return r;
}
static bool fcUIsUnitless(const FcU* u) { return u->u.dimCount == 0; }
static bool fcUIsLinear(const FcU* u) { return u->u.offset == 0 && u->u.converter == NULL; }
static bool fcUCompatible(const FcU* a, const FcU* b) { return fcUnitCompatible(&a->u, &b->u); }
static bool fcUEquals(const FcU* a, const FcU* b) {
    if (!b || !a) return false;
    if (a == b) return true;
    return fcUnitEquals(&a->u, &b->u);
}
/* OrderBy(k => k.Key) of the dimensions: the culture's order of the texts (letters before their upper case, otherwise alphabetical) */
static int fcCultureCmp(const char* a, const char* b) {
    const unsigned char* x = (const unsigned char*)a;
    const unsigned char* y = (const unsigned char*)b;
    for (; *x && *y; x++, y++) {
        int cx = tolower(*x), cy = tolower(*y);
        if (cx != cy) return cx - cy;
    }
    if (*x || *y) return *x ? 1 : -1;
    for (x = (const unsigned char*)a, y = (const unsigned char*)b; *x && *y; x++, y++)
        if (*x != *y) return islower(*x) ? -1 : 1;
    return 0;
}
static void fcSortedDims(const FcUnit* u, int* order, bool ordinal) {
    int i, k;
    for (i = 0; i < u->dimCount; i++) order[i] = i;
    for (i = 1; i < u->dimCount; i++)
        for (k = i; k > 0; k--) {
            int c = ordinal ? strcmp(u->dimSym[order[k - 1]], u->dimSym[order[k]]) : fcCultureCmp(u->dimSym[order[k - 1]], u->dimSym[order[k]]);
            if (c <= 0) break;
            { int t = order[k]; order[k] = order[k - 1]; order[k - 1] = t; }
        }
}

/* ---- the names of combined units ---- */
static FcTerms* fcTermsNew(FcCtx* c) { return (FcTerms*)fcAlloc(c, sizeof(FcTerms)); }
static void fcTermsAdd(FcCtx* c, FcTerms* t, const char* sym, int exp) {
    if (t->count == t->cap) {
        int ncap = t->cap ? t->cap * 2 : 4;
        FcTerm* n = (FcTerm*)fcAlloc(c, sizeof(FcTerm) * (size_t)ncap);
        if (t->count) memcpy(n, t->items, sizeof(FcTerm) * (size_t)t->count);
        t->items = n; t->cap = ncap;
    }
    t->items[t->count].sym = sym;
    t->items[t->count].exp = exp;
    t->count++;
}
static FcTerms* fcTermsCopy(FcCtx* c, const FcTerms* t) {
    FcTerms* r = fcTermsNew(c);
    int i;
    for (i = 0; i < t->count; i++) fcTermsAdd(c, r, t->items[i].sym, t->items[i].exp);
    return r;
}

/* TermsOfText: NULL when the text is no product of powers of words */
static FcTerms* fcTermsOfText(FcCtx* c, const char* text) {
    FcU16 u = {0};
    FcTerms* terms = fcTermsNew(c);
    bool divide = false;
    int i = 0, n;
    fcU16FromFc8(text, &u);
    n = u.len;
    for (;;) {
        int start = i, power = 1;
        const char* word;
        while (i < n && fcIsUnitLetter(u.p[i])) i++;
        word = fc8FromU16(c, u.p + start, i - start);
        if (i == start) {
            if (divide || terms->count > 0 || i >= n || u.p[i] != '1' || i + 1 >= n || u.p[i + 1] != '/') { fcU16Free(&u); return NULL; }
            i++;
        } else if (i < n && u.p[i] == '^') {
            int numberStart = ++i;
            bool neg = false, any = false;
            long v = 0;
            if (i < n && u.p[i] == '-') i++;
            while (i < n && u.p[i] >= '0' && u.p[i] <= '9') i++;
            /* int.TryParse with a leading sign */
            {
                int k = numberStart;
                if (k < i && (u.p[k] == '-' || u.p[k] == '+')) { neg = u.p[k] == '-'; k++; }
                for (; k < i; k++) { v = v * 10 + (u.p[k] - '0'); any = true; if (v > 2147483648L) { fcU16Free(&u); return NULL; } }
            }
            if (!any) { fcU16Free(&u); return NULL; }
            power = (int)(neg ? -v : v);
        }
        if (i > start) fcTermsAdd(c, terms, word, divide ? -power : power);
        if (i == n) { fcU16Free(&u); return terms; }
        if (u.p[i] == '/') divide = true;
        else if (u.p[i] != '*') { fcU16Free(&u); return NULL; }
        i++;
    }
}
static FcTerms* fcMergeTerms(FcCtx* c, FcTerms* terms, const FcTerms* more, int sign) {
    int i, k;
    for (i = 0; i < more->count; i++) {
        const char* sym = more->items[i].sym;
        int exp = more->items[i].exp;
        int at = -1;
        for (k = 0; k < terms->count; k++) if (strcmp(terms->items[k].sym, sym) == 0) { at = k; break; }
        if (at < 0) fcTermsAdd(c, terms, sym, sign * exp);
        else if (terms->items[at].exp + sign * exp == 0) { for (k = at; k + 1 < terms->count; k++) terms->items[k] = terms->items[k + 1]; terms->count--; }
        else terms->items[at].exp += sign * exp;
    }
    return terms;
}
static const struct { uint16_t prefix; double factor; } fcNamePrefixes[] = {
    {'f', 1e-15}, {'p', 1e-12}, {'n', 1e-9}, {0xB5, 1e-6}, {'m', 1e-3}, {'c', 1e-2}, {'d', 1e-1}, {'h', 1e2}, {'k', 1e3}, {'M', 1e6}, {'G', 1e9} };
static bool fcSplitBuiltIn(FcCtx* c, const char* sym, const char** baseSymbol, double* prefix) {
    const char* rest;
    uint16_t first;
    *baseSymbol = sym;
    *prefix = 1;
    if (fcPrefixableBase(sym)) return true;
    if (fc8Units(sym) < 2 || fcCustomFind(c, sym) || fcNamed(sym)) return false;
    first = fc8First(sym, &rest);
    if (!fcPrefixableBase(rest) || !fcPrefix(first, prefix) || !fcPrefixAllowed(first, rest)) return false;
    *baseSymbol = rest;
    return true;
}
static FcTerms* fcSimplifyTerms(FcCtx* c, FcTerms* terms) {
    int n = terms->count, k, j;
    const char** bases = (const char**)fcAlloc(c, sizeof(char*) * (size_t)(n + 1));
    double* prefixes = (double*)fcAlloc(c, sizeof(double) * (size_t)(n + 1));
    bool* drop = (bool*)fcAlloc(c, sizeof(bool) * (size_t)(n + 1));
    double factor = 1;
    bool any = false;
    for (k = 0; k < n; k++) { const char* b; double p; if (fcSplitBuiltIn(c, terms->items[k].sym, &b, &p)) { bases[k] = b; prefixes[k] = p; } }
    for (k = 0; k < n; k++) {
        int sum = 0, count = 0;
        if (!bases[k] || drop[k]) continue;
        for (j = k; j < n; j++) if (bases[j] && strcmp(bases[j], bases[k]) == 0) { sum += terms->items[j].exp; count++; }
        if (sum != 0 || count < 2) continue;
        for (j = k; j < n; j++) if (bases[j] && strcmp(bases[j], bases[k]) == 0) { drop[j] = true; factor *= pow(prefixes[j], terms->items[j].exp); }
        any = true;
    }
    if (!any) return terms;
    for (k = 0; k < n; k++) {
        double wanted;
        const char* sym;
        size_t q;
        FcTerms* result;
        if (drop[k] || !bases[k] || fcNonPrefixable(bases[k])) continue;
        wanted = prefixes[k] * pow(factor, 1.0 / terms->items[k].exp);
        sym = fabs(wanted - 1) <= 1e-9 ? bases[k] : NULL;
        for (q = 0; q < sizeof fcNamePrefixes / sizeof fcNamePrefixes[0]; q++)
            if (!sym && fabs(wanted / fcNamePrefixes[q].factor - 1) <= 1e-9 && fcPrefixAllowed(fcNamePrefixes[q].prefix, bases[k])) {
                FcBuf b = {0};
                fcBufUnit(&b, fcNamePrefixes[q].prefix);
                fcBufStr(&b, bases[k]);
                sym = fcBufDone(c, &b);
            }
        if (!sym) continue;
        result = fcTermsNew(c);
        for (j = 0; j < n; j++) if (!drop[j]) fcTermsAdd(c, result, j == k ? sym : terms->items[j].sym, terms->items[j].exp);
        return fcMergeTerms(c, fcTermsNew(c), result, 1);
    }
    return terms;
}
static const char* fcRenderTerms(FcCtx* c, const FcTerms* terms) {
    FcBuf b = {0};
    bool allNeg = true, first = true;
    int i;
    if (terms->count == 0) return NULL;
    for (i = 0; i < terms->count; i++) if (terms->items[i].exp >= 0) allNeg = false;
    fcBufStr(&b, "");
    if (allNeg) {
        for (i = 0; i < terms->count; i++) { if (b.len > 0) fcBufChar(&b, '*'); fcBufFmt(&b, "%s^%d", terms->items[i].sym, terms->items[i].exp); }
        return fcBufDone(c, &b);
    }
    for (i = 0; i < terms->count; i++)
        if (terms->items[i].exp > 0) { if (b.len > 0) fcBufChar(&b, '*'); fcBufStr(&b, terms->items[i].sym); if (terms->items[i].exp != 1) fcBufFmt(&b, "^%d", terms->items[i].exp); }
    if (b.len == 0) fcBufChar(&b, '1');
    for (i = 0; i < terms->count; i++)
        if (terms->items[i].exp < 0) { fcBufChar(&b, first ? '/' : '*'); first = false; fcBufStr(&b, terms->items[i].sym); if (terms->items[i].exp != -1) fcBufFmt(&b, "^%d", -terms->items[i].exp); }
    return fcBufDone(c, &b);
}
/* Terms(): of the name, or of the dimensions when the factor is 1 */
static const FcTerms* fcUTerms(FcCtx* c, const FcU* uc) {
    FcU* u = (FcU*)uc;
    if (u->termsKnown) return u->terms;
    if (u->display) u->terms = fcTermsOfText(c, u->display);
    else if (fabs(u->u.scale - 1.0) <= 1e-12) {
        int order[FC_MAX_DIMS], i;
        u->terms = fcTermsNew(c);
        fcSortedDims(&u->u, order, false);
        for (i = 0; i < u->u.dimCount; i++) fcTermsAdd(c, u->terms, u->u.dimSym[order[i]], u->u.dimExp[order[i]]);
    } else u->terms = NULL;
    u->termsKnown = true;
    return u->terms;
}
static const char* fcCombinedName(FcCtx* c, const FcU* a, const FcU* b, int sign) {
    const FcTerms* ta = fcUTerms(c, a);
    const FcTerms* tb = fcUTerms(c, b);
    if (!ta || !tb) return NULL;
    return fcRenderTerms(c, fcSimplifyTerms(c, fcMergeTerms(c, fcTermsCopy(c, ta), tb, sign)));
}
static const FcU* fcULinearPart(FcCtx* c, const FcU* u) {
    FcUnit lin;
    if (fcUIsLinear(u)) return u;
    lin = u->u;
    lin.offset = 0; lin.converter = NULL; lin.unitlessConverter = NULL;
    return fcUNew(c, &lin, NULL);
}
/* Unit.Multiply / Unit.Divide */
static const FcU* fcUMulDiv(FcCtx* c, const FcU* a, const FcU* b, bool divide) {
    FcUnit dims;
    int i;
    if (a->u.unitlessConverter) a = FC_UNITLESS;
    if (b->u.unitlessConverter) b = FC_UNITLESS;
    if (fcUIsUnitless(b)) return a;
    if (!divide && fcUIsUnitless(a)) return b;
    a = fcULinearPart(c, a);
    b = fcULinearPart(c, b);
    dims = a->u;
    for (i = 0; i < b->u.dimCount; i++) fcUnitAddDim(&dims, b->u.dimSym[i], divide ? -b->u.dimExp[i] : b->u.dimExp[i]);
    dims.scale = divide ? a->u.scale / b->u.scale : a->u.scale * b->u.scale;
    dims.offset = 0; dims.converter = NULL; dims.unitlessConverter = NULL;
    return fcUNew(c, &dims, dims.dimCount == 0 ? NULL : fcCombinedName(c, a, b, divide ? -1 : 1));
}
static const FcU* fcUMultiply(FcCtx* c, const FcU* a, const FcU* b) { return fcUMulDiv(c, a, b, false); }
static const FcU* fcUDivide(FcCtx* c, const FcU* a, const FcU* b) { return fcUMulDiv(c, a, b, true); }

static const FcU* fcUParseWith(FcCtx* c, const char* symbol);
static bool fcUTryBuiltIn(const char* symbol, FcUnit* unit) { return fcTryBuiltIn(symbol, unit); }
static const FcU* fcUParseCombined(FcCtx* c, const char* symbol) {
    const FcU* result = FC_UNITLESS;
    bool divide = false;
    size_t i = 0, n = strlen(symbol);
    FcTerms* terms;
    const char* name;
    FcUnit dims;
    while (i < n) {
        size_t start = i;
        const FcU* factor;
        int power = 1, p;
        while (i < n && symbol[i] != '/' && symbol[i] != '*' && symbol[i] != '^') i++;
        factor = fcUParseWith(c, fcStrN(c, symbol + start, i - start));
        if (i < n && symbol[i] == '^') {
            bool neg = false;
            int v = 0;
            ++i;
            if (i < n && symbol[i] == '-') { neg = true; i++; }
            while (i < n && symbol[i] >= '0' && symbol[i] <= '9') v = v * 10 + (symbol[i++] - '0');
            power = neg ? -v : v;
        }
        for (p = 0; p < abs(power); p++) result = (divide ^ (power < 0)) ? fcUDivide(c, result, factor) : fcUMultiply(c, result, factor);
        if (i < n) {
            if (symbol[i] == '/') divide = true;
            i++;
        }
    }
    terms = fcTermsOfText(c, symbol);
    name = terms == NULL ? symbol : fcRenderTerms(c, fcSimplifyTerms(c, fcMergeTerms(c, fcTermsNew(c), terms, 1)));
    if (!name) name = symbol;
    dims = result->u;
    dims.offset = 0; dims.converter = NULL; dims.unitlessConverter = NULL;
    return fcUNew(c, &dims, name);
}
static const FcU* fcUParseWith(FcCtx* c, const char* symbol) {
    FcUnit u;
    FcCustomUnit* own;
    if (!symbol[0]) return FC_UNITLESS;
    if (strpbrk(symbol, "/*^")) return fcUParseCombined(c, symbol);
    if (fcNamed(symbol) && fcUTryBuiltIn(symbol, &u)) return fcUNew(c, &u, symbol);
    if (c->customUnits->count > 0 && (own = fcCustomFind(c, symbol)) != NULL) return fcUNew(c, &own->unit, own->symbol);
    if (fcUTryBuiltIn(symbol, &u)) return fcUNew(c, &u, symbol);
    if (fc8Units(symbol) >= 2 && c->customUnits->count > 0) {
        const char* rest;
        uint16_t first = fc8First(symbol, &rest);
        double factor;
        FcCustomUnit* prefixable;
        if (fcPrefix(first, &factor) && (prefixable = fcCustomFind(c, rest)) != NULL && prefixable->prefixed && strcmp(prefixable->symbol, rest) == 0
            && fcPrefixAllowed(first, prefixable->symbol)) {
            fcUnitSetUnitless(&u);
            memcpy(u.dimSym, prefixable->unit.dimSym, sizeof u.dimSym);
            memcpy(u.dimExp, prefixable->unit.dimExp, sizeof u.dimExp);
            u.dimCount = prefixable->unit.dimCount;
            u.scale = factor * prefixable->unit.scale;
            return fcUNew(c, &u, symbol);
        }
    }
    fcUnitSetUnitless(&u);
    fcUnitAddDim(&u, symbol, 1);
    return fcUNew(c, &u, symbol);
}
/* Unit.Parse */
static const FcU* fcUParse(FcCtx* c, const char* symbol) {
    if (!symbol || !symbol[0] || strcmp(symbol, "unitless") == 0) return FC_UNITLESS;
    return fcUParseWith(c, fcCaretExponents(c, symbol));
}

/* .NET's shortest round-trip text of a double ("R", and "G" since .NET Core 3): the fewest digits that read back as the value, in plain notation
 * for a decimal exponent from -5 to 14, otherwise as d.dddE+xx */
static const char* fcDoubleR(FcCtx* c, double v) {
    char t[64], digits[32];
    int prec, exp10, nd, i;
    FcBuf b = {0};
    const char* m;
    if (isnan(v)) return "NaN";
    if (isinf(v)) return v > 0 ? "\xE2\x88\x9E" : "-\xE2\x88\x9E";
    if (v == 0) return signbit(v) ? "-0" : "0";
    for (prec = 1; prec <= 17; prec++) { snprintf(t, sizeof t, "%.*e", prec - 1, v); if (strtod(t, NULL) == v) break; }
    /* t: [-]d.ddde[+-]xx */
    m = t;
    if (*m == '-') { fcBufChar(&b, '-'); m++; }
    nd = 0;
    for (; *m && *m != 'e'; m++) if (*m != '.') digits[nd++] = *m;
    digits[nd] = 0;
    exp10 = atoi(m + 1);
    while (nd > 1 && digits[nd - 1] == '0') digits[--nd] = 0;
    if (exp10 >= -5 && exp10 < 15) {
        if (exp10 < 0) {
            fcBufStr(&b, "0.");
            for (i = 0; i < -exp10 - 1; i++) fcBufChar(&b, '0');
            fcBufStr(&b, digits);
        } else {
            for (i = 0; i <= exp10 || i < nd; i++) {
                if (i == exp10 + 1) fcBufChar(&b, '.');
                fcBufChar(&b, i < nd ? digits[i] : '0');
            }
        }
    } else {
        fcBufChar(&b, digits[0]);
        if (nd > 1) { fcBufChar(&b, '.'); fcBufStr(&b, digits + 1); }
        fcBufFmt(&b, "E%c%02d", exp10 < 0 ? '-' : '+', exp10 < 0 ? -exp10 : exp10);
    }
    return fcBufDone(c, &b);
}

/* Unit.ToString */
static const char* fcUText(FcCtx* c, const FcU* u) {
    FcBuf num = {0}, den = {0}, out = {0};
    int order[FC_MAX_DIMS], i, numCount = 0, denCount = 0;
    if (fcUIsUnitless(u)) return "unitless";
    if (u->display) return u->display;
    fcSortedDims(&u->u, order, false);
    fcBufStr(&num, ""); fcBufStr(&den, "");
    for (i = 0; i < u->u.dimCount; i++) {
        const char* key = u->u.dimSym[order[i]];
        int exp = u->u.dimExp[order[i]];
        if (exp > 0) { if (numCount++) fcBufChar(&num, '*'); if (exp == 1) fcBufStr(&num, key); else fcBufFmt(&num, "%s^%d", key, exp); }
        else { if (denCount++) fcBufChar(&den, '*'); if (exp == -1) fcBufStr(&den, key); else fcBufFmt(&den, "%s^%d", key, -exp); }
    }
    fcBufStr(&out, numCount > 0 ? num.p : "1");
    if (denCount > 0) { fcBufChar(&out, '/'); if (denCount > 1) fcBufFmt(&out, "(%s)", den.p); else fcBufStr(&out, den.p); }
    fcBufFree(&num); fcBufFree(&den);
    if (fabs(u->u.scale - 1.0) > 1e-12) fcBufFmt(&out, "(\xC3\x97%s)", fcDoubleR(c, u->u.scale));
    return fcBufDone(c, &out);
}

static double fcUConversionFactorTo(const FcU* from, const FcU* to) {
    if (from == to) return 1.0;
    return fcNiceFactor(from->u.scale / to->u.scale);
}

/* Unit.DemandText (the text of a declaration's unit as the compiled program keeps it) */
static const char* fcUnitDemandText(FcCtx* c, const char* unitText) {
    const unsigned char* p;
    bool custom = false;
    const FcU* u;
    int order[FC_MAX_DIMS], i;
    FcBuf b = {0};
    if (fcStartsWith(unitText, "any ")) return fcFmt(c, "any %s", fcUnitDemandText(c, unitText + 4));
    if (c->customUnits->count == 0 || !unitText[0] || fcStartsWith(unitText, "\xC2\xA7")) return unitText;
    unitText = fcCaretExponents(c, unitText);
    for (p = (const unsigned char*)unitText; ;) {
        const unsigned char* start = p;
        while (*p && *p != '/' && *p != '*' && *p != '^') p++;
        if (fcIsCustomUnitWord(c, fcStrN(c, (const char*)start, (size_t)(p - start)))) custom = true;
        if (!*p) break;
        p++;
    }
    if (!custom) return unitText;
    u = fcUParse(c, unitText);
    fcBufFmt(&b, "\xC2\xA7%s\xC2\xA7", fcUText(c, u));
    fcSortedDims(&u->u, order, true);
    for (i = 0; i < u->u.dimCount; i++) { if (i) fcBufChar(&b, ','); fcBufFmt(&b, "%s:%d", u->u.dimSym[order[i]], u->u.dimExp[order[i]]); }
    fcBufFmt(&b, "\xC2\xA7%s\xC2\xA7%s\xC2\xA7%s\xC2\xA7%s", fcDoubleR(c, u->u.scale), fcDoubleR(c, u->u.offset), u->u.converter ? u->u.converter : "",
        u->u.unitlessConverter ? u->u.unitlessConverter : "");
    return fcBufDone(c, &b);
}
