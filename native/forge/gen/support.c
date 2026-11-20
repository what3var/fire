/* native/forge/gen/support.c - what the generator needs from the rest of fire (commit 808e9788): OpInfo.cs (Decode), src/fire/Bytecode/DeclaredType.cs
 * (Parse, ConditionOf, NumberDemand, DisplayOf, TypedElementOf, ArrayOfTypeArg, LambdaParamsOf), the queries of src/fire/Bytecode/RuntimeClass.cs
 * (FlattenedFieldNames, FieldIndex, the Find* of a field, Candidates, FindMethodWithAccess, FindConstructor, ComputedSources, ...), Overloads.Overrides,
 * src/fire/Values/ArrayElem.cs (the parts the generator asks), GenericClassNames.PlainName, and the helpers of the port: texts (Split, Join, the
 * culture's order of texts), sets of ints, sorted keys. */

/* ---- texts ---- */
#define FG_STREQ(a, b) (strcmp((a), (b)) == 0)
static FcList* fgSplit(FcCtx* c, const char* s, char sep) {
    FcList* l = fcListNew(c);
    const char* start = s;
    const char* p;
    for (p = s;; p++) {
        if (*p == sep || !*p) {
            fcListAdd(c, l, (void*)fcStrN(c, start, (size_t)(p - start)));
            if (!*p) break;
            start = p + 1;
        }
    }
    return l;
}
/* string.Split(sep) on a text separator */
static FcList* fgSplitStr(FcCtx* c, const char* s, const char* sep) {
    FcList* l = fcListNew(c);
    size_t n = strlen(sep);
    const char* start = s;
    for (;;) {
        const char* at = strstr(start, sep);
        if (!at) { fcListAdd(c, l, (void*)fcStr(c, start)); break; }
        fcListAdd(c, l, (void*)fcStrN(c, start, (size_t)(at - start)));
        start = at + n;
    }
    return l;
}
static const char* fgJoin(FcCtx* c, const FcList* l, const char* sep) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 0; l && i < l->count; i++) { if (i) fcBufStr(&b, sep); fcBufStr(&b, (const char*)l->items[i]); }
    return fcBufDone(c, &b);
}
static const char* fgSub(FcCtx* c, const char* s, int start, int len) { return fcStrN(c, s + start, (size_t)len); }
static int fgIndexOf(const char* s, char ch) { const char* p = strchr(s, ch); return p ? (int)(p - s) : -1; }
static bool fgContains(const char* s, const char* part) { return strstr(s, part) != NULL; }
static const char* fgTrimStart(const char* s, char ch) { while (*s == ch) s++; return s; }
/* string.Trim() of ASCII white space */
static const char* fgTrim(FcCtx* c, const char* s) {
    const char* e;
    while (*s == ' ' || *s == '\t' || *s == '\n' || *s == '\r') s++;
    e = s + strlen(s);
    while (e > s && (e[-1] == ' ' || e[-1] == '\t' || e[-1] == '\n' || e[-1] == '\r')) e--;
    return fcStrN(c, s, (size_t)(e - s));
}
static const char* fgUpper(FcCtx* c, const char* s) {
    char* r = (char*)fcStr(c, s);
    char* q;
    for (q = r; *q; q++) if (*q >= 'a' && *q <= 'z') *q = (char)(*q - 'a' + 'A');
    return r;
}
static const char* fgReplace(FcCtx* c, const char* s, const char* from, const char* to) {
    FcBuf b = {0};
    size_t n = strlen(from);
    const char* p = s;
    fcBufStr(&b, "");
    if (!n) return s;
    for (;;) {
        const char* at = strstr(p, from);
        if (!at) { fcBufStr(&b, p); break; }
        fcBufPut(&b, p, (size_t)(at - p));
        fcBufStr(&b, to);
        p = at + n;
    }
    return fcBufDone(c, &b);
}
/* the primary weight of an ASCII character in the order of the culture (ICU's root collation: punctuation, symbols, digits, letters) */
static int fgCultureWeight(unsigned char ch) {
    static const char* const order = "\t\n\r _-,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$0123456789aAbBcCdDeEfFgGhHiIjJkKlLmMnNoOpPqQrRsStTuUvVwWxXyYzZ";
    const char* at;
    if (ch >= 'A' && ch <= 'Z') ch = (unsigned char)(ch - 'A' + 'a');
    if (ch >= 0x80) return 1000 + ch;
    at = strchr(order, (char)ch);
    return at && ch ? (int)(at - order) : 500 + ch;
}
/* Comparer<string>.Default (the current culture, ICU): letters without regard to case first, then the lower case before the upper case */
static int fgCultureCmp(const char* a, const char* b) {
    const unsigned char* x = (const unsigned char*)a;
    const unsigned char* y = (const unsigned char*)b;
    for (; *x && *y; x++, y++) {
        int wx = fgCultureWeight(*x), wy = fgCultureWeight(*y);
        if (wx != wy) return wx - wy;
    }
    if (*x || *y) return *x ? 1 : -1;
    for (x = (const unsigned char*)a, y = (const unsigned char*)b; *x && *y; x++, y++)
        if (*x != *y) return (*x >= 'a' && *x <= 'z') ? -1 : 1;
    return 0;
}
static int fgCmpCulture(const void* a, const void* b) { return fgCultureCmp(*(const char* const*)a, *(const char* const*)b); }
static int fgCmpOrdinal(const void* a, const void* b) { return strcmp(*(const char* const*)a, *(const char* const*)b); }

/* the keys of a dictionary of texts, sorted (SortedSet/SortedDictionary: ordinal or the culture) */
static FcList* fgSortedKeys(FcCtx* c, const FcDict* d, bool ordinal) {
    FcList* l = fcListNew(c);
    FC_DICT_EACH(d, e) fcListAdd(c, l, (void*)e->key);
    if (l->count > 1) qsort(l->items, (size_t)l->count, sizeof(void*), ordinal ? fgCmpOrdinal : fgCmpCulture);
    return l;
}
/* the keys of a dictionary of texts in their order (Dictionary/HashSet) */
static FcList* fgKeys(FcCtx* c, const FcDict* d) {
    FcList* l = fcListNew(c);
    FC_DICT_EACH(d, e) fcListAdd(c, l, (void*)e->key);
    return l;
}

/* ---- sets and maps of ints: the int as the key text ---- */
static const char* fgIntKey(FcCtx* c, int v) { return fcFmt(c, "%d", v); }
static bool fgISetHas(FcCtx* c, const FcDict* d, int v) { char k[16]; (void)c; snprintf(k, sizeof k, "%d", v); return fcDictHas(d, k); }
static bool fgISetAdd(FcCtx* c, FcDict* d, int v) { char k[16]; snprintf(k, sizeof k, "%d", v); if (fcDictHas(d, k)) return false; fcDictPut(d, fcStr(c, k), (void*)1); return true; }
static bool fgISetRemove(FcDict* d, int v) { char k[16]; snprintf(k, sizeof k, "%d", v); return fcDictRemove(d, k); }
static bool fgIMapTry(const FcDict* d, int key, void** value) { char k[16]; snprintf(k, sizeof k, "%d", key); return fcDictTry(d, k, value); }
static void fgIMapPut(FcCtx* c, FcDict* d, int key, void* value) { char k[16]; void** at; snprintf(k, sizeof k, "%d", key); at = fcDictRef(d, k); if (at) *at = value; else fcDictPut(d, fcStr(c, k), value); }
static int fgCmpInt(const void* a, const void* b) { int x = *(const int*)a, y = *(const int*)b; return x < y ? -1 : x > y; }
/* the ints of a set, in its order (sorted: SortedSet<int>, OrderBy) */
static int* fgISetItems(FcCtx* c, const FcDict* d, bool sorted, int* count) {
    int n = fcDictCount(d), k = 0;
    int* items = (int*)fcAlloc(c, sizeof(int) * (size_t)(n + 1));
    FC_DICT_EACH(d, e) items[k++] = atoi((const char*)e->key);
    if (sorted && n > 1) qsort(items, (size_t)n, sizeof(int), fgCmpInt);
    *count = n;
    return items;
}

/* ---- OpInfo.Decode ---- */
static const char* fgOperandSizes(int op) {
    switch (op) {
#define FIRE_OP(name, number, operands) case number: return operands;
#include "../../tables/opcodes.def"
#undef FIRE_OP
        default: return NULL;
    }
}
static const char* fgOpName(int op) {
    switch (op) {
#define FIRE_OP(name, number, operands) case number: return #name;
#include "../../tables/opcodes.def"
#undef FIRE_OP
        default: return "?";
    }
}
static FgCode fgDecode(FcCtx* c, const FcChunk* chunk) {
    FgCode r;
    int ip = 0, n = 0, cap = 16;
    r.p = (FgInstr*)fcAlloc(c, sizeof(FgInstr) * (size_t)cap);
    while (ip < chunk->len) {
        int op = chunk->code[ip], at = ip, i;
        const char* sizes = fgOperandSizes(op);
        FgInstr* ins;
        if (!sizes) fcThrow(c, FC_ERR_NOTSUPPORTED, 0, 0, NULL, fcFmt(c, "unknown opcode %d at %d", op, ip));
        if (n == cap) {
            FgInstr* m = (FgInstr*)fcAlloc(c, sizeof(FgInstr) * (size_t)(cap * 2));
            memcpy(m, r.p, sizeof(FgInstr) * (size_t)n);
            r.p = m;
            cap *= 2;
        }
        ins = &r.p[n++];
        memset(ins, 0, sizeof *ins);
        ip++;
        ins->addr = at;
        ins->op = op;
        ins->n = (int)strlen(sizes);
        for (i = 0; sizes[i]; i++) {
            /* `catch threads()` without a type has no type operand */
            if (op == OP_RegisterThreadsCatch && i == 2 && ins->a[1] == 0) break;
            if (sizes[i] == '1') ins->a[i] = chunk->code[ip];
            else ins->a[i] = chunk->code[ip] | (chunk->code[ip + 1] << 8);
            ip += sizes[i] - '0';
        }
        ins->next = ip;
    }
    r.n = n;
    return r;
}

/* Chunk.GetLocation */
static void fgLocation(const FcChunk* chunk, int ip, int* source, int* line) {
    int lo = 0, hi = chunk->lineCount - 1, found = -1;
    while (lo <= hi) {
        int mid = (lo + hi) >> 1;
        if (chunk->lines[mid].offset <= ip) { found = mid; lo = mid + 1; }
        else hi = mid - 1;
    }
    *source = found < 0 ? 0 : chunk->lines[found].source;
    *line = found < 0 ? 0 : chunk->lines[found].line;
}

static const char* fgConstStr(const FcChunk* chunk, int k) { const FcConst* v = (const FcConst*)chunk->constants->items[k]; return v->kind == CK_String ? v->s : ""; }
static const FcConst* fgConst(const FcChunk* chunk, int k) { return (const FcConst*)chunk->constants->items[k]; }

/* ---- DeclaredType ---- */
typedef struct FgCond { bool isUnit; const char* name; const FcU* unit; } FgCond;
typedef struct FgAlt { FgCond** conds; int count; } FgAlt;
typedef struct FgSpec {
    const char* display;
    FgAlt* alts; int altCount;
    int typeParamIndex;
    const char* typeParamClass;
    bool nonNull;
    FcList* typeArgs;   /* NULL: none */
} FgSpec;

/* NumberDemand: kind "int"/"float" and the demand; false: no number type argument */
static bool fgNumberDemand(FcCtx* c, const char* typeArg, const char** kind, const char** demand) {
    const char* k = fcStartsWith(typeArg, "int") ? "int" : fcStartsWith(typeArg, "float") ? "float" : "";
    size_t kl = strlen(k);
    const char* unit;
    if (kl == 0 || (strlen(typeArg) > kl && typeArg[kl] != ':')) return false;
    unit = strlen(typeArg) > kl ? typeArg + kl + 1 : "unitless";
    if (kind) *kind = k;
    if (demand) *demand = streq0(k, "float") ? fcFmt(c, "#%s", unit) : unit;
    return true;
}
static const FcU* fgDemandUnitParse(FcCtx* c, const char* text);
static FgCond* fgConditionOf(FcCtx* c, FcDict* cache, const char* text) {
    FgCond* cond = (FgCond*)fcDictGet(cache, text);
    const char* kind;
    if (cond) return cond;
    cond = (FgCond*)fcAlloc(c, sizeof *cond);
    if (fcStartsWith(text, "in:")) { cond->isUnit = true; cond->name = text + 3; cond->unit = fgDemandUnitParse(c, text + 3); }
    else if (fgNumberDemand(c, text, &kind, NULL)) cond->name = kind;
    else cond->name = text;
    fcDictPut(cache, fcStr(c, text), cond);
    return cond;
}
static FgSpec* fgParseSpec(FcCtx* c, FcDict* cache, FcDict* condCache, const char* text) {
    FgSpec* spec = (FgSpec*)fcDictGet(cache, text);
    const char* t = text;
    const char* end;
    const char* rest;
    const char* marks;
    if (spec) return spec;
    spec = (FgSpec*)fcAlloc(c, sizeof *spec);
    spec->typeParamIndex = -1;
    spec->nonNull = t[0] == DT_NonNullMark;
    if (spec->nonNull) t++;
    if ((marks = strchr(t, DT_TypeArgsMark)) != NULL) {
        spec->typeArgs = fgSplitStr(c, marks + 1, DT_TypeArgSep);
        t = fcStrN(c, t, (size_t)(marks - t));
    }
    end = strchr(t, '\x01');
    if (!end) end = t + strlen(t);   /* (IndexOf -1: Substring(0) - the whole text is the rest) */
    rest = *end ? end + 1 : t;
    if (!*end) { spec->display = ""; rest = t; }   /* (never in a valid text) */
    else spec->display = fcStrN(c, t, (size_t)(end - t));
    if (*rest == '@') {
        const char* tpEnd = strchr(rest, DT_TypeParamEnd);
        const char* inner = fcStrN(c, rest + 1, (size_t)(tpEnd - rest - 1));
        const char* at = strchr(inner, '@');
        spec->typeParamIndex = atoi(inner);
        spec->typeParamClass = at ? at + 1 : "";
        rest = tpEnd + 1;
    }
    if (*rest) {
        FcList* alts = fgSplit(c, rest, '|');
        int i, k;
        spec->altCount = alts->count;
        spec->alts = (FgAlt*)fcAlloc(c, sizeof(FgAlt) * (size_t)alts->count);
        for (i = 0; i < alts->count; i++) {
            FcList* conds = fgSplit(c, (const char*)alts->items[i], '&');
            spec->alts[i].count = conds->count;
            spec->alts[i].conds = (FgCond**)fcAlloc(c, sizeof(FgCond*) * (size_t)conds->count);
            for (k = 0; k < conds->count; k++) spec->alts[i].conds[k] = fgConditionOf(c, condCache, (const char*)conds->items[k]);
        }
    }
    fcDictPut(cache, fcStr(c, text), spec);
    return spec;
}
static const char* fgSpecSingleName(const FgSpec* s) { return s->altCount == 1 && s->alts[0].count == 1 && !s->alts[0].conds[0]->isUnit ? s->alts[0].conds[0]->name : NULL; }
/* DisplayOf */
static const char* fgDisplayOf(FcCtx* c, const char* typeArg) {
    const char* kind;
    if (fcStartsWith(typeArg, "in:")) return fcFmt(c, "in %s", typeArg + 3);
    if (fgNumberDemand(c, typeArg, &kind, NULL) && strchr(typeArg, ':')) return fcFmt(c, "%s : %s", kind, typeArg + strlen(kind) + 1);
    return fcPlainName(c, typeArg);
}
static int fgLambdaParamsOf(const char* name) { return fcStartsWith(name, "lambda/") ? atoi(name + 7) : -1; }
/* TypedElementOf: the array width code of `int`/`float`/`bool`/`char` (TypeRef.ArrayWidthCode(1)); 0: none */
static int fgTypedElementOf(const char* typeArg) {
    if (FG_STREQ(typeArg, "int")) return 0x10000 | (1 << 8) | AE_I64;
    if (FG_STREQ(typeArg, "float")) return 0x10000 | (1 << 8) | AE_F64;
    if (FG_STREQ(typeArg, "bool")) return 0x10000 | (1 << 8) | AE_Bool;
    if (FG_STREQ(typeArg, "char")) return 0x10000 | (1 << 8) | AE_Char;
    return 0;
}

/* ---- RuntimeClass ---- */
static FcFieldInfo* fgOwnField(const FcRtClass* rc, const char* name) { return (FcFieldInfo*)fcDictGet(rc->ownFieldInfo, name); }
static FcFieldInfo* fgFindField(const FcRtClass* rc, const char* name) {
    for (; rc; rc = rc->base) { FcFieldInfo* f = fgOwnField(rc, name); if (f) return f; }
    return NULL;
}
static const FcRtClass* fgFindStaticFieldOwner(const FcRtClass* rc, const char* name) {
    for (; rc; rc = rc->base) { FcFieldInfo* f = fgOwnField(rc, name); if (f && f->isStatic) return rc; }
    return NULL;
}
static const char* fgFieldRequiredUnit(const FcRtClass* rc, const char* name) { FcFieldInfo* f = fgFindField(rc, name); return f ? f->requiredUnit : NULL; }
static const char* fgFieldRequiredType(const FcRtClass* rc, const char* name) { FcFieldInfo* f = fgFindField(rc, name); return f ? f->requiredType : NULL; }
static const char* fgFieldArrayOfParam(const FcRtClass* rc, const char* name) { FcFieldInfo* f = fgFindField(rc, name); return f ? f->arrayOfParam : NULL; }
static const char* fgFieldArrayUnit(const FcRtClass* rc, const char* name) { FcFieldInfo* f = fgFindField(rc, name); return f ? (f->requiredArrayType ? f->requiredArrayType : f->requiredUnit) : NULL; }
static const char* fgFieldRequiredArrayType(const FcRtClass* rc, const char* name) { FcFieldInfo* f = fgFindField(rc, name); return f ? f->requiredArrayType : NULL; }
static int fgFieldRequiredArray(const FcRtClass* rc, const char* name) { FcFieldInfo* f = fgFindField(rc, name); return f ? f->requiredArray : 0; }
static int fgFieldRequiredKind(const FcRtClass* rc, const char* name) { FcFieldInfo* f = fgFindField(rc, name); return f ? f->requiredKind : 0; }
static const char* fgDerivedFieldWhat(FcCtx* c, const char* name) { return fcFmt(c, "Field '%s' (derived as never undefined: nothing in the program stores undefined in it)", name); }
static const char* fgDerivedParameterWhat(FcCtx* c, const char* name) { return fcFmt(c, "Parameter '%s' (derived as never undefined: every call in the program gives a value)", name); }
static const char* fgFieldWhat(FcCtx* c, const FcRtClass* rc, const char* name) {
    FcFieldInfo* f = fgFindField(rc, name);
    return f && f->derivedDefined ? fgDerivedFieldWhat(c, name) : fcFmt(c, "Field '%s'", name);
}
/* FindFieldAccess: the declaring class and the access; NULL: none */
static const FcRtClass* fgFieldAccess(const FcRtClass* rc, const char* name, int* access) {
    for (; rc; rc = rc->base) { FcFieldInfo* f = fgOwnField(rc, name); if (f) { *access = f->access; return rc; } }
    return NULL;
}
static bool fgIsNoCopy(const FcRtClass* rc) { for (; rc; rc = rc->base) if (rc->noCopy) return true; return false; }
static bool fgIsNotSerializable(const FcRtClass* rc) { for (; rc; rc = rc->base) if (rc->notSerializable) return true; return false; }
static bool fgHasDestructorInChain(const FcRtClass* rc) { for (; rc; rc = rc->base) if (rc->destructor) return true; return false; }
static bool fgHasMethodNamed(const FcRtClass* rc, const char* name) { for (; rc; rc = rc->base) if (fcDictHas(rc->methods, name)) return true; return false; }

/* the caches of the queries of the classes (FlattenedFieldNames, FieldIndex, Candidates): per context */
typedef struct FgCands { FcProto** protos; const FcRtClass** declaring; int count; bool runtimeChoice; } FgCands;
static FcList* fgFlattenedFieldNames(FcCtx* c, FcDict* cache, const FcRtClass* rc) {
    FcList* names = (FcList*)fcDictGet(cache, rc);
    int i;
    if (names) return names;
    names = rc->base ? fcListCopy(c, fgFlattenedFieldNames(c, cache, rc->base)) : fcListNew(c);
    for (i = 0; i < rc->fieldNames->count; i++) fcListAdd(c, names, rc->fieldNames->items[i]);
    fcDictPut(cache, rc, names);
    return names;
}
/* FieldIndex: name -> index + 1 (the last of a name wins, as the C# dictionary's indexer) */
static FcDict* fgFieldIndex(FcCtx* c, FcDict* namesCache, FcDict* cache, const FcRtClass* rc) {
    FcDict* index = (FcDict*)fcDictGet(cache, rc);
    FcList* names;
    int i;
    if (index) return index;
    names = fgFlattenedFieldNames(c, namesCache, rc);
    index = fcDictNew(c, FC_KEY_STR);
    for (i = 0; i < names->count; i++) fcDictPut(index, names->items[i], FC_IVAL(i + 1));
    fcDictPut(cache, rc, index);
    return index;
}

/* Overloads.IsLoose / Overrides */
static bool fgIsLoose(const char* d) { return !d || !*d || d[0] == 'g'; }
static bool fgOverrides(const FcProto* derived, const FcProto* baseProto) {
    int i;
    if (derived->paramCount != baseProto->paramCount) return false;
    for (i = 0; i < derived->paramCount; i++) {
        const char* a = fcOverloadTypeAt(derived, i);
        const char* b = fcOverloadTypeAt(baseProto, i);
        if (strcmp(a, b) == 0 || fgIsLoose(a) || fgIsLoose(b)) continue;
        return false;
    }
    return true;
}
static bool fgAllTrailingHaveDefaults(const FcProto* proto, int supplied) {
    int i;
    for (i = supplied; i < proto->paramCount; i++)
        if (i >= proto->paramDefaults->count || proto->paramDefaults->items[i] == NULL) return false;
    return true;
}
static bool fgNeedsRuntimeChoice(FcProto** protos, int count, int argc) {
    int k, i;
    if (count < 2) return false;
    for (k = 0; k < count; k++) {
        const FcProto* p = protos[k];
        if (p->typeParamCount > 0) return true;
        for (i = 0; i < argc && i < p->paramCount; i++) if (*fcOverloadTypeAt(p, i)) return true;
    }
    return false;
}
static FgCands* fgCandidates(FcCtx* c, FcDict* cache, const FcRtClass* self, const char* name, int argc) {
    const char* key = fcFmt(c, "%p\x01%s\x01%d", (const void*)self, name, argc);
    FgCands* r = (FgCands*)fcDictGet(cache, key);
    FcList* protos;
    FcList* declaring;
    FcList* seen;
    const FcRtClass* rc;
    int i;
    if (r) return r;
    protos = fcListNew(c); declaring = fcListNew(c); seen = fcListNew(c);
    for (rc = self; rc; rc = rc->base) {
        FcList* overloads = (FcList*)fcDictGet(rc->methods, name);
        FcList* fitting;
        int k, s;
        if (!overloads) continue;
        fitting = fcListNew(c);
        for (k = 0; k < overloads->count; k++) {
            FcProto* p = (FcProto*)overloads->items[k];
            bool overridden = false;
            if (!(p->paramCount == argc || (p->paramCount > argc && fgAllTrailingHaveDefaults(p, argc)))) continue;
            for (s = 0; s < seen->count && !overridden; s++) if (fgOverrides((FcProto*)seen->items[s], p)) overridden = true;
            if (!overridden) fcListAdd(c, fitting, p);
        }
        /* OrderBy(exact first).ThenBy(ParamCount): a stable sort */
        for (k = 1; k < fitting->count; k++)
            for (s = k; s > 0; s--) {
                FcProto* a = (FcProto*)fitting->items[s - 1];
                FcProto* b = (FcProto*)fitting->items[s];
                int ka = a->paramCount == argc ? 0 : 1, kb = b->paramCount == argc ? 0 : 1;
                if (ka < kb || (ka == kb && a->paramCount <= b->paramCount)) break;
                fitting->items[s - 1] = b; fitting->items[s] = a;
            }
        for (k = 0; k < fitting->count; k++) { fcListAdd(c, protos, fitting->items[k]); fcListAdd(c, declaring, (void*)rc); }
        for (k = 0; k < overloads->count; k++) fcListAdd(c, seen, overloads->items[k]);
    }
    r = (FgCands*)fcAlloc(c, sizeof *r);
    r->count = protos->count;
    r->protos = (FcProto**)fcAlloc(c, sizeof(void*) * (size_t)(protos->count + 1));
    r->declaring = (const FcRtClass**)fcAlloc(c, sizeof(void*) * (size_t)(protos->count + 1));
    for (i = 0; i < protos->count; i++) { r->protos[i] = (FcProto*)protos->items[i]; r->declaring[i] = (const FcRtClass*)declaring->items[i]; }
    r->runtimeChoice = fgNeedsRuntimeChoice(r->protos, r->count, argc);
    fcDictPut(cache, key, r);
    return r;
}
static int fgAccessOf(const FcProto* p) { return p->access < 0 ? ACC_Public : p->access; }
/* FindMethodWithAccess(name, argc): the first candidate */
typedef struct FgFound { FcProto* proto; const FcRtClass* declaring; int access; } FgFound;
static FgFound fgFindMethodWithAccess(FcCtx* c, FcDict* cache, const FcRtClass* rc, const char* name, int argc) {
    FgCands* set = fgCandidates(c, cache, rc, name, argc);
    FgFound r;
    if (set->count == 0) { r.proto = NULL; r.declaring = NULL; r.access = ACC_Public; return r; }
    r.proto = set->protos[0]; r.declaring = set->declaring[0]; r.access = fgAccessOf(set->protos[0]);
    return r;
}
static FcProto* fgFindMethod(FcCtx* c, FcDict* cache, const FcRtClass* rc, const char* name, int argc) { return fgFindMethodWithAccess(c, cache, rc, name, argc).proto; }
static bool fgIsTypeOverloaded(FcCtx* c, FcDict* cache, const FcRtClass* rc, const char* name, int argc) { return fgCandidates(c, cache, rc, name, argc)->runtimeChoice; }
/* AllConstructors */
static FcList* fgAllConstructors(FcCtx* c, const FcRtClass* rc) {
    FcList* l = fcListCopy(c, rc->ctorProtos);
    int i;
    for (i = 0; i < rc->extraConstructors->count; i++) fcListAdd(c, l, rc->extraConstructors->items[i]);
    return l;
}
/* ConstructorCandidates */
static FcList* fgConstructorCandidates(FcCtx* c, const FcRtClass* rc, int argc) {
    FcList* all = fgAllConstructors(c, rc);
    FcList* l = fcListNew(c);
    int i, s;
    for (i = 0; i < all->count; i++) {
        FcProto* p = (FcProto*)all->items[i];
        if (p->paramCount == argc || (p->paramCount > argc && fgAllTrailingHaveDefaults(p, argc))) fcListAdd(c, l, p);
    }
    for (i = 1; i < l->count; i++)
        for (s = i; s > 0; s--) {
            FcProto* a = (FcProto*)l->items[s - 1];
            FcProto* b = (FcProto*)l->items[s];
            int ka = a->paramCount == argc ? 0 : 1, kb = b->paramCount == argc ? 0 : 1;
            if (ka < kb || (ka == kb && a->paramCount <= b->paramCount)) break;
            l->items[s - 1] = b; l->items[s] = a;
        }
    return l;
}
static bool fgIsConstructorTypeOverloaded(FcCtx* c, const FcRtClass* rc, int argc) { return fgConstructorCandidates(c, rc, argc)->count > 1 && rc->extraConstructors->count > 0; }
/* FindConstructor(argc) */
static FcProto* fgFindConstructor(const FcRtClass* rc, int argc) {
    FcProto* exact = NULL;
    FcProto* best = NULL;
    int i;
    for (i = 0; i < rc->ctorArities->count; i++) if (FC_VALI(rc->ctorArities->items[i]) == argc) return (FcProto*)rc->ctorProtos->items[i];
    for (i = 0; i < rc->ctorProtos->count; i++) {
        FcProto* p = (FcProto*)rc->ctorProtos->items[i];
        if (p->paramCount == argc) { exact = p; continue; }
        if (p->paramCount > argc && fgAllTrailingHaveDefaults(p, argc) && (!best || p->paramCount < best->paramCount)) best = p;
    }
    return exact ? exact : best;
}
/* ReflectedInterfaces(false) */
static FcList* fgReflectedInterfaces(FcCtx* c, const FcRtClass* rc, bool declaredOnly) {
    FcList* r = fcListNew(c);
    int i;
    if (declaredOnly) {
        if (rc->meta && rc->meta->declaredInterfaces)
            for (i = 0; i < rc->meta->declaredInterfaces->count; i++) { const char* p = fcPlainName(c, (const char*)rc->meta->declaredInterfaces->items[i]); if (!fcListHasStr(r, p)) fcListAdd(c, r, (void*)p); }
        return r;
    }
    for (; rc; rc = rc->base)
        for (i = 0; i < rc->interfaces->count; i++) { const char* p = fcPlainName(c, (const char*)rc->interfaces->items[i]); if (!fcListHasStr(r, p)) fcListAdd(c, r, (void*)p); }
    return r;
}

/* ---- ArrayElem ---- */
static int fgAeTypeOf(int code) { return code & 0x3F; }
static bool fgAeIsDefined(int code) { return (code & 0x40) != 0; }
static const char* fgAeClassText(FcCtx* c, const char* unit) { const char* at = strchr(unit, '\x02'); return at ? fcStrN(c, unit, (size_t)(at - unit)) : unit; }
static const char* fgAeName0(int type) {
    switch (type) {
        case AE_I8: return "int[8]"; case AE_I16: return "int[16]"; case AE_I32: return "int[32]"; case AE_I64: return "int";
        case AE_F8: return "float[8]"; case AE_F16: return "float[16]"; case AE_F32: return "float[32]"; case AE_F64: return "float";
        case AE_Bool: return "bool"; case AE_Char: return "char"; case AE_U8: return "byte"; case AE_Word: return "word"; default: return "any";
    }
}
static bool fgAeIsFloat(int type) { return type == AE_F8 || type == AE_F16 || type == AE_F32 || type == AE_F64; }
