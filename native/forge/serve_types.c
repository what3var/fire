/* native/forge/serve_types.c - the type derivation of the symbol index: src/fire.Editor/Language/ScriptSymbolIndex.Types.cs (commit 1c549476) and
 * BuiltinMembers.cs, method for method (L3). After `x.` the members of the class of `x`: `new X(...)`, `this`/`base`, variables, parameters, fields,
 * class and enum names, calls, fields and indexes on them (chained); the type of a variable from its declaration or what is assigned to it; the type
 * of a member from its declaration, the `return`s of its body or the assignments `this.f = ...` of its class. Everything else is Unknown. */

#define SV_MAX_DEPTH 8

static bool svIsPrimitiveName(const char* n) {
    return n && (strcmp(n, "bool") == 0 || strcmp(n, "int") == 0 || strcmp(n, "float") == 0 || strcmp(n, "char") == 0 || strcmp(n, "string") == 0
        || strcmp(n, "byte") == 0 || strcmp(n, "word") == 0 || strcmp(n, "any") == 0);
}

/* ---- BuiltinMembers ---- */
typedef struct SvBuiltin { const char* name; bool isProperty; const char* signature; const char* returnType; } SvBuiltin;
static const SvBuiltin svStringMembers[] = { { "Length", true, "", "int" }, { "ToBytes", false, "", "buffer" }, { "ToUnicode", false, "width", "buffer" } };
static const SvBuiltin svArrayMembers[] = { { "Length", true, "", "int" } };
static const SvBuiltin svBufferMembers[] = {
    { "Length", true, "", "int" }, { "littleEndian", true, "", "bool" },
    { "ToString", false, "", "string" }, { "ToUnicode", false, "[width]", "string" }, { "ToUnicodeChar", false, "[width]", "char" },
    { "ToLittleEndian", false, "", "buffer" }, { "ToBigEndian", false, "", "buffer" },
    { "ReadInt8", false, "at", "int" }, { "ReadUInt8", false, "at", "int" },
    { "ReadInt16", false, "at, [littleEndian]", "int" }, { "ReadUInt16", false, "at, [littleEndian]", "int" },
    { "ReadInt32", false, "at, [littleEndian]", "int" }, { "ReadUInt32", false, "at, [littleEndian]", "int" },
    { "ReadInt64", false, "at, [littleEndian]", "int" },
    { "ReadFloat16", false, "at, [littleEndian]", "float" }, { "ReadFloat32", false, "at, [littleEndian]", "float" }, { "ReadFloat64", false, "at, [littleEndian]", "float" },
    { "WriteInt8", false, "at, value", NULL }, { "WriteUInt8", false, "at, value", NULL },
    { "WriteInt16", false, "at, value, [littleEndian]", NULL }, { "WriteUInt16", false, "at, value, [littleEndian]", NULL },
    { "WriteInt32", false, "at, value, [littleEndian]", NULL }, { "WriteUInt32", false, "at, value, [littleEndian]", NULL },
    { "WriteInt64", false, "at, value, [littleEndian]", NULL },
    { "WriteFloat16", false, "at, value, [littleEndian]", NULL }, { "WriteFloat32", false, "at, value, [littleEndian]", NULL }, { "WriteFloat64", false, "at, value, [littleEndian]", NULL },
    { "Fill", false, "value, [start, count]", NULL },
    { "CopyTo", false, "target, targetAt, [start, count]", NULL },
    { "Slice", false, "start, [count]", "buffer" },
    { "IndexOf", false, "value, [start]", "int" },
    { "Compare", false, "other", "int" },
};
static const SvBuiltin svCharMembers[] = { { "ToByte", false, "", "int" }, { "ToUnicode", false, "width", "buffer" } };
static const SvBuiltin svIntMembers[] = { { "ToChar", false, "", "char" } };

static const SvBuiltin* svBuiltinsFor(SvType t, int* n) {
    *n = 0;
    if (t.kind == TK_PRIMITIVE && t.name) {
        if (strcmp(t.name, "string") == 0) { *n = 3; return svStringMembers; }
        if (strcmp(t.name, "char") == 0) { *n = 2; return svCharMembers; }
        if (strcmp(t.name, "int") == 0 || strcmp(t.name, "byte") == 0) { *n = 1; return svIntMembers; }
        return NULL;
    }
    if (t.kind == TK_ARRAY) {
        if (t.name && (strcmp(t.name, "byte") == 0 || strcmp(t.name, "word") == 0)) { *n = (int)(sizeof svBufferMembers / sizeof svBufferMembers[0]); return svBufferMembers; }
        *n = 1;
        return svArrayMembers;
    }
    return NULL;
}
static const char* svExtensionClassOf(SvType t) {
    if (t.kind != TK_PRIMITIVE || !t.name) return NULL;
    if (strcmp(t.name, "string") == 0) return "$string";
    if (strcmp(t.name, "char") == 0) return "$char";
    if (strcmp(t.name, "int") == 0 || strcmp(t.name, "byte") == 0) return "$int";
    if (strcmp(t.name, "float") == 0) return "$float";
    if (strcmp(t.name, "bool") == 0) return "$bool";
    return NULL;
}
static SvType svBuiltinToType(FcCtx* t, const char* rt) {
    size_t n;
    if (!rt) return svUnknown;
    if (strcmp(rt, "buffer") == 0) return svT(TK_ARRAY, "byte");
    n = strlen(rt);
    if (n >= 2 && strcmp(rt + n - 2, "[]") == 0) return svT(TK_ARRAY, fcStrN(t, rt, n - 2));
    return svT(TK_PRIMITIVE, rt);
}

/* ---- functions ---- */
static int svMatchForward(SvIndex* x, int open, FcTokenType o, FcTokenType cl) {
    int depth = 0, j;
    for (j = open; j < x->ntok; j++) {
        if (SVTY(x, j) == o) depth++;
        else if (SVTY(x, j) == cl && --depth == 0) return j;
    }
    return -1;
}
static int svMatchBackward(SvIndex* x, int close, FcTokenType o, FcTokenType cl) {
    int depth = 0, j;
    for (j = close; j >= 0; j--) {
        if (SVTY(x, j) == cl) depth++;
        else if (SVTY(x, j) == o && --depth == 0) return j;
    }
    return -1;
}

static int svFindBodyBrace(SvIndex* x, int afterParen) {
    int j = afterParen;
    while (j < x->ntok) {
        const FcToken* t = SVT(x, j);
        if (t->type == TT_LBrace) return j;
        if (t->newlineBefore || t->type == TT_Eof) return -1;
        switch (t->type) {
            case TT_Colon: case TT_Arrow: case TT_On: case TT_Base: case TT_This: case TT_Identifier: case TT_Dot: j++; break;
            case TT_LParen: {
                int close = svMatchForward(x, j, TT_LParen, TT_RParen);
                if (close < 0) return -1;
                j = close + 1;
                break;
            }
            default: return -1;
        }
    }
    return -1;
}

static FcList* svLambdaParamsBefore(SvIndex* x, int arrow, int* start) {
    int open;
    *start = arrow;
    if (arrow < 1) return NULL;
    if (SVTY(x, arrow - 1) == TT_Identifier) {
        FcList* r = fcListNew(x->c);
        SvParam* p = (SvParam*)fcAlloc(x->c, sizeof *p);
        p->name = SVT(x, arrow - 1)->lexeme; p->type = NULL;
        fcListAdd(x->c, r, p);
        *start = arrow - 1;
        return r;
    }
    if (SVTY(x, arrow - 1) != TT_RParen) return NULL;
    open = svMatchBackward(x, arrow - 1, TT_LParen, TT_RParen);
    if (open < 0) return NULL;
    *start = open;
    { int after; return svReadParamList(x, open, &after); }
}

static int svCmpFunc(const void* a, const void* b) {
    const SvFunc* p = *(const SvFunc* const*)a;
    const SvFunc* q = *(const SvFunc* const*)b;
    return p->bodyStartTok < q->bodyStartTok ? -1 : p->bodyStartTok > q->bodyStartTok;
}

/* AllFunctions: methods, constructors and lambdas with a block, in the order of their bodies (made once) */
static FcList* svAllFunctions(SvIndex* x) {
    FcList* result;
    FcDict bodies;
    int i;
    if (x->functions) return x->functions;
    result = fcListNew(x->c);
    fcDictInit(x->c, &bodies, FC_KEY_PTR);
    for (i = 0; i < x->ntok; i++) {
        FcTokenType type = SVTY(x, i);
        bool isKeyword, isNamed;
        int paren, afterParen, open, end, start;
        FcList* parms;
        SvFunc* f;
        if (type == TT_Arrow && i + 1 < x->ntok && SVTY(x, i + 1) == TT_LBrace && !fcDictHas(&bodies, FC_IVAL(i + 2))) {
            FcList* lp = svLambdaParamsBefore(x, i, &start);
            if (lp) {
                int lambdaEnd = svMatchBrace(x, i + 1);
                fcSetAdd(&bodies, FC_IVAL(i + 2));
                f = (SvFunc*)fcAlloc(x->c, sizeof *f);
                f->bodyStartTok = i + 1; f->bodyStartOff = svTokenOffset(x, i + 1); f->bodyEndOff = svTokenOffset(x, lambdaEnd) + 1; f->params = lp; f->bodyEndTok = lambdaEnd;
                fcListAdd(x->c, result, f);
                continue;
            }
        }
        isKeyword = type == TT_Construct || type == TT_Func;
        isNamed = type == TT_Identifier && (i == 0 || (SVTY(x, i - 1) != TT_Dot && SVTY(x, i - 1) != TT_New));
        if (!isKeyword && !isNamed) continue;
        paren = i + 1;
        if (isNamed && paren < x->ntok && SVTY(x, paren) == TT_Lt) paren = svSkipAngleBrackets(x, paren);
        if (paren >= x->ntok || SVTY(x, paren) != TT_LParen) continue;
        parms = svReadParamList(x, paren, &afterParen);
        open = svFindBodyBrace(x, afterParen);
        if (open < 0 || !fcSetAdd(&bodies, FC_IVAL(open + 1))) continue;   /* (keys +1: 0 is no pointer) */
        end = svMatchBrace(x, open);
        f = (SvFunc*)fcAlloc(x->c, sizeof *f);
        f->bodyStartTok = open; f->bodyStartOff = svTokenOffset(x, open); f->bodyEndOff = svTokenOffset(x, end) + 1; f->params = parms; f->bodyEndTok = end;
        fcListAdd(x->c, result, f);
    }
    /* List.Sort is not stable; the bodies are distinct, so the order is the same */
    if (result->count > 1) qsort(result->items, (size_t)result->count, sizeof(void*), svCmpFunc);
    return x->functions = result;
}

static FcList* svEnclosingFunctions(SvIndex* x, int offset) {
    FcList* all = svAllFunctions(x);
    FcList* r = fcListNew(x->lang->tmp);
    int i;
    for (i = 0; i < all->count; i++) {
        SvFunc* f = (SvFunc*)all->items[i];
        if (offset >= f->bodyStartOff && offset <= f->bodyEndOff) fcListAdd(x->lang->tmp, r, f);
    }
    return r;
}

static FcDict* svScopeBodies(SvIndex* x) {
    FcDict* r;
    FcList* all;
    int i, k;
    if (x->scopeBodies) return x->scopeBodies;
    r = fcDictNew(x->c, FC_KEY_PTR);
    all = svAllFunctions(x);
    for (i = 0; i < all->count; i++) {
        SvFunc* f = (SvFunc*)all->items[i];
        if (!fcDictHas(r, FC_IVAL(f->bodyStartTok + 1))) fcDictAdd(r, FC_IVAL(f->bodyStartTok + 1), FC_IVAL(f->bodyEndTok));
    }
    FC_DICT_EACH(&x->classes, e) {
        SvClass* k2 = (SvClass*)e->value;
        if (k2->source != x) continue;
        for (k = 0; k < k2->bodyRanges->count; k++) {
            SvRange* rg = (SvRange*)k2->bodyRanges->items[k];
            void* known;
            if (!fcDictTry(r, FC_IVAL(rg->start + 1), &known) || (int)(intptr_t)known < rg->end) fcDictPut(r, FC_IVAL(rg->start + 1), FC_IVAL(rg->end));
        }
    }
    return x->scopeBodies = r;
}
static bool svScopeBodyEnd(SvIndex* x, int k, int* end) {
    void* v;
    if (!fcDictTry(svScopeBodies(x), FC_IVAL(k + 1), &v)) return false;
    *end = (int)(intptr_t)v;
    return true;
}
static int svSkipForeignBody(SvIndex* x, int k, int offset) {
    int end;
    if (!svScopeBodyEnd(x, k, &end)) return k;
    if (offset >= svTokenOffset(x, k) && offset <= svTokenOffset(x, end)) return k;
    return end + 1;
}
static int svScopeStart(FcList* chain) { return chain->count > 0 ? ((SvFunc*)chain->items[0])->bodyStartTok : 0; }

static SvFunc* svEnclosingFunction(SvIndex* x, int offset) {
    FcList* all = svAllFunctions(x);
    SvFunc* best = NULL;
    int i;
    for (i = 0; i < all->count; i++) {
        SvFunc* f = (SvFunc*)all->items[i];
        if (offset >= f->bodyStartOff && offset <= f->bodyEndOff) best = f;
    }
    return best;
}

static int svLastTokenBefore(SvIndex* x, int offset) {
    int lo = 0, hi = x->ntok - 1, best = -1;
    while (lo <= hi) {
        int mid = (lo + hi) / 2;
        if (svTokenOffset(x, mid) < offset) { best = mid; lo = mid + 1; } else hi = mid - 1;
    }
    return best;
}
static int svFindTokenIndex(SvIndex* x, int offset) {
    int lo = 0, hi = x->ntok - 1;
    while (lo <= hi) {
        int mid = (lo + hi) / 2, o = svTokenOffset(x, mid);
        if (o == offset) return mid;
        if (o < offset) lo = mid + 1; else hi = mid - 1;
    }
    return -1;
}

/* ---- chains `a.b(...).c[...]` ---- */
typedef enum SvSegKind { SG_NAME, SG_CALL, SG_INDEX, SG_NEW } SvSegKind;
typedef struct SvSeg { SvSegKind kind; const char* text; } SvSeg;

static SvSeg* svSeg(FcCtx* t, SvSegKind k, const char* text) { SvSeg* s = (SvSeg*)fcAlloc(t, sizeof *s); s->kind = k; s->text = text; return s; }

static void svMergeQualifiedNew(SvIndex* x, FcList* segs, int* startIdx) {
    FcCtx* t = x->lang->tmp;
    int core, q;
    bool hasIndex;
    SvSeg* last;
    FcBuf b = {0};
    SvSeg* idx;
    if (*startIdx <= 0 || SVTY(x, *startIdx - 1) != TT_New) return;
    core = segs->count > 0 && ((SvSeg*)segs->items[segs->count - 1])->kind == SG_INDEX ? segs->count - 1 : segs->count;
    if (core < 2) return;
    for (q = 0; q < core - 1; q++) {
        SvSeg* s = (SvSeg*)segs->items[q];
        if (s->kind != SG_NAME || strcmp(s->text, "this") == 0 || strcmp(s->text, "base") == 0) return;
    }
    last = (SvSeg*)segs->items[core - 1];
    hasIndex = core < segs->count;
    if (last->kind != (hasIndex ? SG_NAME : SG_CALL)) return;
    fcBufStr(&b, "");
    for (q = 0; q < core; q++) { if (q) fcBufChar(&b, '.'); fcBufStr(&b, ((SvSeg*)segs->items[q])->text); }
    idx = hasIndex ? (SvSeg*)segs->items[segs->count - 1] : NULL;
    segs->count = 0;
    fcListAdd(t, segs, svSeg(t, SG_NEW, fcBufDone(t, &b)));
    if (idx) fcListAdd(t, segs, idx);
    (*startIdx)--;
}

static bool svTryParseChainBackward(SvIndex* x, int endIdx, FcList** segsOut, int* startIdx) {
    FcCtx* t = x->lang->tmp;
    FcList* rev = fcListNew(t);
    int j = endIdx, i;
    *segsOut = rev;
    *startIdx = endIdx;
    for (;;) {
        const FcToken* tk;
        SvSeg* seg;
        int segStart;
        if (j < 0) return false;
        tk = SVT(x, j);
        if (tk->type == TT_RBracket) {
            int open = svMatchBackward(x, j, TT_LBracket, TT_RBracket);
            if (open < 0) return false;
            fcListAdd(t, rev, svSeg(t, SG_INDEX, ""));
            j = open - 1;
            continue;
        }
        if (tk->type == TT_RParen) {
            int open = svMatchBackward(x, j, TT_LParen, TT_RParen), k;
            bool isNew;
            if (open < 0) return false;
            k = open - 1;
            if (k >= 0 && SVTY(x, k) == TT_Gt) {
                int lt = k, depth = 0;
                for (; lt >= 0; lt--) {
                    if (SVTY(x, lt) == TT_Gt) depth++;
                    else if (SVTY(x, lt) == TT_Lt && --depth == 0) break;
                    else if (SVTY(x, lt) == TT_LParen || SVTY(x, lt) == TT_RParen) return false;
                }
                if (lt < 0) return false;
                k = lt - 1;
            }
            if (k < 0 || SVTY(x, k) != TT_Identifier) return false;
            isNew = k > 0 && SVTY(x, k - 1) == TT_New;
            seg = svSeg(t, isNew ? SG_NEW : SG_CALL, SVT(x, k)->lexeme);
            segStart = isNew ? k - 1 : k;
        } else if (tk->type == TT_Identifier || tk->type == TT_This || tk->type == TT_Base) {
            bool isNew = tk->type == TT_Identifier && j > 0 && SVTY(x, j - 1) == TT_New && !(j + 1 < x->ntok && SVTY(x, j + 1) == TT_Dot);
            seg = svSeg(t, isNew ? SG_NEW : SG_NAME, tk->lexeme);
            segStart = isNew ? j - 1 : j;
        } else return false;
        fcListAdd(t, rev, seg);
        *startIdx = segStart;
        if (seg->kind == SG_NEW) break;
        if (segStart - 1 >= 0 && SVTY(x, segStart - 1) == TT_Dot) { j = segStart - 2; continue; }
        break;
    }
    for (i = 0; i < rev->count / 2; i++) { void* a = rev->items[i]; rev->items[i] = rev->items[rev->count - 1 - i]; rev->items[rev->count - 1 - i] = a; }
    svMergeQualifiedNew(x, rev, startIdx);
    return true;
}

static int svFirstTokenOfChainEndingAt(SvIndex* x, int endIdx) {
    FcList* segs;
    int start;
    return svTryParseChainBackward(x, endIdx, &segs, &start) ? start : endIdx;
}

static SvType svEvalExprRange(SvIndex* x, int startIdx, int endIdx, int depth, bool keepViaThis);
static SvType svResolveName(SvIndex* x, const char* name, int offset, int depth);
static SvType svTypeOfMember(SvIndex* x, SvMember* m, int depth);
static SvType svFromTypeName(SvIndex* x, const char* name, bool isArray, FcList* context);

static SvType svFromTypeName(SvIndex* x, const char* name, bool isArray, FcList* context) {
    const char* key;
    if (!name) return svUnknown;
    if (svIsPrimitiveName(name)) return svT(isArray ? TK_ARRAY : TK_PRIMITIVE, name);
    if ((key = svResolveClassKey(x, name, context, true)) != NULL) return svT(isArray ? TK_ARRAY : TK_INSTANCE, key);
    if ((key = svResolveEnumKey(x, name, context)) != NULL) return svT(isArray ? TK_ARRAY : TK_PRIMITIVE, key);
    return svUnknown;
}

/* predicates for svFirstMember */
typedef struct SvNameKind { const char* name; int which; } SvNameKind;   /* which: 0 method, 1 field/property, 2 any of the name, 3 method with a lambda parameter at `arg` */
static bool svPredName(const SvMember* m, const void* a) {
    const SvNameKind* nk = (const SvNameKind*)a;
    if (strcmp(m->name, nk->name) != 0) return false;
    if (nk->which == 0) return m->kind == SM_METHOD;
    if (nk->which == 1) return m->kind == SM_FIELD || m->kind == SM_PROPERTY;
    return true;
}
static SvMember* svFindMember(SvIndex* x, const char* cls, const char* name, int which) {
    SvNameKind nk;
    nk.name = name; nk.which = which;
    return svFirstMember(x, cls, svPredName, &nk);
}

static SvType svMemberType(SvIndex* x, SvType receiver, const char* name, bool isCall, int depth) {
    switch (receiver.kind) {
        case TK_INSTANCE: case TK_STATIC: {
            SvMember* m = svFindMember(x, receiver.name, name, isCall ? 0 : 1);
            return m ? svTypeOfMember(x, m, depth) : svUnknown;
        }
        case TK_ENUM: {
            FcList* members = (FcList*)fcDictGet(&x->enumMembers, receiver.name);
            return members && fcListHasStr(members, name) ? svT(TK_PRIMITIVE, "int") : svUnknown;
        }
        case TK_NAMESPACE: {
            const char* full = fcFmt(x->lang->tmp, "%s.%s", receiver.name, name);
            const char* k;
            if (isCall) return svUnknown;
            if ((k = svDictKey(&x->classes, full)) != NULL) return svT(TK_STATIC, k);
            if ((k = svDictKey(&x->enumMembers, full)) != NULL) return svT(TK_ENUM, k);
            if ((k = svDictKey(&x->namespaces, full)) != NULL) return svT(TK_NAMESPACE, k);
            return svUnknown;
        }
        case TK_PRIMITIVE: case TK_ARRAY: {
            int n, i;
            const SvBuiltin* b = svBuiltinsFor(receiver, &n);
            const char* ext;
            for (i = 0; i < n; i++) if (strcmp(b[i].name, name) == 0 && b[i].isProperty == !isCall) return svBuiltinToType(x->lang->tmp, b[i].returnType);
            if (isCall && (ext = svExtensionClassOf(receiver)) != NULL && fcDictHas(&x->classes, ext)) {
                SvMember* m = svFindMember(x, ext, name, 0);
                if (m) return svTypeOfMember(x, m, depth);
            }
            return svUnknown;
        }
        default: return svUnknown;
    }
}

static SvType svElementType(SvIndex* x, SvType container, int depth) {
    switch (container.kind) {
        case TK_ARRAY: return svFromTypeName(x, container.name, false, NULL);
        case TK_PRIMITIVE: if (container.name && strcmp(container.name, "string") == 0) return svT(TK_PRIMITIVE, "char"); return svUnknown;
        case TK_INSTANCE: {
            SvMember* m = svFindMember(x, container.name, "operator[]", 2);
            return m ? svTypeOfMember(x, m, depth) : svUnknown;
        }
        default: return svUnknown;
    }
}

static SvType svEvalChain(SvIndex* x, FcList* segs, int scopeOffset, int depth) {
    SvType cur;
    int next = 1, i;
    SvSeg* root;
    if (segs->count == 0 || depth > SV_MAX_DEPTH) return svUnknown;
    root = (SvSeg*)segs->items[0];
    switch (root->kind) {
        case SG_NEW: {
            FcList* context = svContextAt(x, scopeOffset);
            cur = svFromTypeName(x, root->text, false, context);
            if (segs->count > 1 && ((SvSeg*)segs->items[1])->kind == SG_INDEX) { cur = svFromTypeName(x, root->text, true, context); next = 2; }
            break;
        }
        case SG_NAME:
            if (strcmp(root->text, "this") == 0) {
                const char* cls = svEnclosingClassAt(x, scopeOffset);
                cur = cls ? svT(TK_INSTANCE, cls) : svUnknown;
                cur.viaThis = cls != NULL;
            } else if (strcmp(root->text, "base") == 0) {
                const char* cls = svEnclosingClassAt(x, scopeOffset);
                SvClass* info = svClassGet(x, cls);
                const char* baseName = NULL;
                if (info) {
                    FcList* bases = svResolvedBases(x, info);
                    int b;
                    for (b = 0; b < bases->count; b++) if (!svClassGet(x, (const char*)bases->items[b])->isInterface) { baseName = (const char*)bases->items[b]; break; }
                }
                cur = baseName ? svT(TK_INSTANCE, baseName) : svUnknown;
                cur.viaThis = baseName != NULL;
            } else cur = svResolveName(x, root->text, scopeOffset, depth);
            break;
        case SG_CALL: {
            const char* cls = svEnclosingClassAt(x, scopeOffset);
            SvMember* m = cls ? svFindMember(x, cls, root->text, 0) : NULL;
            cur = m ? svTypeOfMember(x, m, depth) : svUnknown;
            break;
        }
        default: return svUnknown;
    }
    for (i = next; i < segs->count && cur.kind != TK_UNKNOWN; i++) {
        SvSeg* s = (SvSeg*)segs->items[i];
        switch (s->kind) {
            case SG_NAME: cur = svMemberType(x, cur, s->text, false, depth); break;
            case SG_CALL: cur = svMemberType(x, cur, s->text, true, depth); break;
            case SG_INDEX: cur = svElementType(x, cur, depth); break;
            default: cur = svUnknown; break;
        }
    }
    return cur;
}

static SvType svEvalExprRangeCore(SvIndex* x, int startIdx, int endIdx, int depth) {
    FcList* segs;
    int start;
    if (depth > SV_MAX_DEPTH || startIdx < 0 || startIdx > endIdx || endIdx >= x->ntok) return svUnknown;
    if (startIdx < endIdx && (SVTY(x, startIdx) == TT_Flat || SVTY(x, startIdx) == TT_Copy || SVTY(x, startIdx) == TT_Take)) return svEvalExprRangeCore(x, startIdx + 1, endIdx, depth + 1);
    if (startIdx == endIdx)
        switch (SVTY(x, startIdx)) {
            case TT_StringLiteral: case TT_InterpolatedStringLiteral: return svT(TK_PRIMITIVE, "string");
            case TT_IntLiteral: return svT(TK_PRIMITIVE, "int");
            case TT_FloatLiteral: return svT(TK_PRIMITIVE, "float");
            case TT_CharLiteral: return svT(TK_PRIMITIVE, "char");
            case TT_True: case TT_False: return svT(TK_PRIMITIVE, "bool");
            default: break;
        }
    if (!svTryParseChainBackward(x, endIdx, &segs, &start) || start != startIdx) return svUnknown;
    return svEvalChain(x, segs, svTokenOffset(x, startIdx), depth);
}
static SvType svEvalExprRange(SvIndex* x, int startIdx, int endIdx, int depth, bool keepViaThis) {
    SvType t = svEvalExprRangeCore(x, startIdx, endIdx, depth);
    if (t.viaThis && !keepViaThis) t.viaThis = false;
    return t;
}

static SvType svInferReturnType(SvIndex* x, SvMember* m, int depth);
static SvType svInferFieldType(SvIndex* x, SvMember* m, int depth);

static SvType svTypeOfMember(SvIndex* x, SvMember* m, int depth) {
    SvClass* owner;
    SvType declared;
    SvIndex* source;
    if (depth > SV_MAX_DEPTH) return svUnknown;
    owner = svClassGet(x, m->owner);
    declared = svFromTypeName(x, m->typeName, m->typeIsArray, owner ? owner->context : NULL);
    if (declared.kind != TK_UNKNOWN) return declared;
    source = m->source ? m->source : x;
    if (m->kind == SM_METHOD) return svInferReturnType(source, m, depth + 1);
    if (m->kind == SM_FIELD) return svInferFieldType(source, m, depth + 1);
    return svUnknown;
}

static SvType svInferReturnType(SvIndex* x, SvMember* m, int depth) {
    int k;
    if (depth > SV_MAX_DEPTH || m->bodyStart < 0) return svUnknown;
    for (k = m->bodyStart + 1; k < m->bodyEnd; k++) {
        int start, end;
        SvType t;
        if (SVTY(x, k) != TT_Return) continue;
        start = k + 1;
        if (start >= m->bodyEnd || SVT(x, start)->newlineBefore) continue;
        end = svExpressionEnd(x, start, m->bodyEnd);
        t = svEvalExprRange(x, start, end - 1, depth + 1, false);
        if (t.kind != TK_UNKNOWN) return t;
    }
    return svUnknown;
}

static SvType svInferFieldType(SvIndex* x, SvMember* field, int depth) {
    SvClass* owner;
    int r, k;
    if (depth > SV_MAX_DEPTH || !(owner = svClassGet(x, field->owner))) return svUnknown;
    for (r = 0; r < owner->bodyRanges->count; r++) {
        SvRange* rg = (SvRange*)owner->bodyRanges->items[r];
        for (k = rg->start + 1; k + 3 < rg->end; k++) {
            int vs, ve;
            SvType t;
            if (SVTY(x, k) != TT_This || SVTY(x, k + 1) != TT_Dot || SVTY(x, k + 2) != TT_Identifier || !svLex(x, k + 2, field->name) || SVTY(x, k + 3) != TT_Assign) continue;
            vs = k + 4;
            ve = svExpressionEnd(x, vs, rg->end);
            t = svEvalExprRange(x, vs, ve - 1, depth + 1, false);
            if (t.kind != TK_UNKNOWN) return t;
        }
    }
    return svUnknown;
}

/* ---- lambda parameters ---- */
typedef struct SvLambdaParam { const char* name; SvType type; const char* typeName; bool isTree; const char* target; const char* method; } SvLambdaParam;

static FcList* svAngleArgs(SvIndex* x, int lt) {
    FcList* r = fcListNew(x->lang->tmp);
    int level = 0, k;
    bool expectName = true;
    for (k = lt; k < x->ntok; k++) {
        const FcToken* t = SVT(x, k);
        if (t->type == TT_Lt) { level++; continue; }
        if (t->type == TT_Gt) { if (--level == 0) break; continue; }
        if (level == 1 && t->type == TT_Comma) { expectName = true; continue; }
        if (level == 1 && expectName && (t->type == TT_Identifier || svIsTypeKeyword(t->type))) { fcListAdd(x->lang->tmp, r, (void*)t->lexeme); expectName = false; }
    }
    return r;
}

static FcList* svClassTypeParams(SvIndex* x, const char* classKey) {
    SvClass* info = svClassGet(x, classKey);
    const char* simple = info ? info->simpleName : classKey;
    int k;
    for (k = 0; k + 2 < x->ntok; k++) {
        if (SVTY(x, k) != TT_Class || !svLex(x, k + 1, simple) || SVTY(x, k + 2) != TT_Lt) continue;
        return svAngleArgs(x, k + 2);
    }
    return NULL;
}

static FcList* svReceiverTypeArgs(SvIndex* x, int start, int end, int offset) {
    const char* variable;
    FcList* found = NULL;
    int k;
    if (SVTY(x, start) == TT_New && start + 2 <= end && SVTY(x, start + 2) == TT_Lt) return svAngleArgs(x, start + 2);
    if (start != end || SVTY(x, start) != TT_Identifier) return NULL;
    variable = SVT(x, start)->lexeme;
    for (k = 0; k + 3 < x->ntok && svTokenOffset(x, k) < offset; k++) {
        if (svLex(x, k, variable) && SVTY(x, k) == TT_Identifier && SVTY(x, k + 1) == TT_Assign && SVTY(x, k + 2) == TT_New && k + 4 < x->ntok && SVTY(x, k + 4) == TT_Lt)
            found = svAngleArgs(x, k + 4);
        else if (SVTY(x, k) == TT_Identifier && SVTY(x, k + 1) == TT_Lt) {
            int close = svSkipAngleBrackets(x, k + 1);
            if (close < x->ntok && SVTY(x, close) == TT_Identifier && svLex(x, close, variable)) found = svAngleArgs(x, k + 1);
        }
    }
    return found;
}

/* `\blambda\s+expression\b` */
static bool svIsTreeTarget(const char* s) {
    const char* p;
    for (p = s; (p = strstr(p, "lambda")) != NULL; p += 6) {
        const char* q = p + 6;
        bool before = p == s || !(isalnum((unsigned char)p[-1]) || p[-1] == '_');
        if (!before || !(*q == ' ' || *q == '\t' || *q == '\n' || *q == '\r')) continue;
        while (*q == ' ' || *q == '\t' || *q == '\n' || *q == '\r') q++;
        if (strncmp(q, "expression", 10) == 0 && !(isalnum((unsigned char)q[10]) || q[10] == '_')) return true;
    }
    return false;
}

typedef struct SvLambdaPred { const char* name; int argIndex; } SvLambdaPred;
static bool svPredLambdaMethod(const SvMember* m, const void* a) {
    const SvLambdaPred* p = (const SvLambdaPred*)a;
    const char* pt;
    if (m->kind != SM_METHOD || strcmp(m->name, p->name) != 0 || m->paramTypes->count <= p->argIndex) return false;
    pt = (const char*)m->paramTypes->items[p->argIndex];
    return pt && strstr(pt, "lambda") != NULL;
}

static SvLambdaParam* svDescribeLambdaParam(SvIndex* x, const char* name, int index, const char* writtenType, int lambdaStart, int offset, int depth) {
    FcCtx* t = x->lang->tmp;
    FcList* context = svContextAt(x, offset);
    int argIndex = 0, level = 0, open = -1, k, receiverEnd = -1;
    SvMember* method = NULL;
    SvType receiver = svUnknown;
    const char* target;
    const char* typeName;
    SvLambdaParam* r;
    for (k = lambdaStart - 1; k >= 0; k--) {
        FcTokenType ty = SVTY(x, k);
        if (ty == TT_RParen || ty == TT_RBracket) level++;
        else if (ty == TT_LParen || ty == TT_LBracket) { if (level == 0) { open = ty == TT_LParen ? k : -1; break; } level--; }
        else if (ty == TT_Comma && level == 0) argIndex++;
        else if ((ty == TT_Semicolon || ty == TT_LBrace || ty == TT_RBrace) && level == 0) break;
    }
    if (open > 0 && SVTY(x, open - 1) == TT_Identifier) {
        const char* methodName = SVT(x, open - 1)->lexeme;
        const char* own;
        if (open >= 2 && SVTY(x, open - 2) == TT_Dot) {
            receiverEnd = open - 3;
            receiver = receiverEnd >= 0 ? svEvalExprRange(x, svFirstTokenOfChainEndingAt(x, receiverEnd), receiverEnd, depth + 1, false) : svUnknown;
        } else if ((own = svEnclosingClassAt(x, offset)) != NULL) receiver = svT(TK_INSTANCE, own);
        if ((receiver.kind == TK_INSTANCE || receiver.kind == TK_STATIC) && receiver.name) {
            SvLambdaPred p;
            p.name = methodName; p.argIndex = argIndex;
            method = svFirstMember(x, receiver.name, svPredLambdaMethod, &p);
        }
    }
    target = method ? (const char*)method->paramTypes->items[argIndex] : NULL;
    typeName = writtenType;
    if (!typeName && target) {
        const char* lt = strchr(target, '<');
        size_t tl = strlen(target);
        if (lt && tl > 0 && target[tl - 1] == '>') {
            /* the arguments between the first '<' and the last '>', split at ',' (Split(',').Trim()) */
            FcList* args = fcListNew(t);
            const char* p = lt + 1;
            const char* endp = target + tl - 1;
            while (p <= endp) {
                const char* comma = p;
                const char* a;
                const char* b;
                while (comma < endp && *comma != ',') comma++;
                a = p; b = comma;
                while (a < b && isspace((unsigned char)*a)) a++;
                while (b > a && isspace((unsigned char)b[-1])) b--;
                fcListAdd(t, args, (void*)fcStrN(t, a, (size_t)(b - a)));
                if (comma >= endp) break;
                p = comma + 1;
            }
            if (index < args->count) typeName = (const char*)args->items[index];
            if (typeName && receiver.name && !svResolveClassKey(x, typeName, context, true)) {
                FcList* typeParams = svClassTypeParams(x, receiver.name);
                int tp = -1, q;
                if (typeParams) for (q = 0; q < typeParams->count; q++) if (strcmp((const char*)typeParams->items[q], typeName) == 0) { tp = q; break; }
                if (tp >= 0 && receiverEnd >= 0) {
                    FcList* given = svReceiverTypeArgs(x, svFirstTokenOfChainEndingAt(x, receiverEnd), receiverEnd, offset);
                    if (given && tp < given->count) typeName = (const char*)given->items[tp];
                }
            }
        }
    }
    r = (SvLambdaParam*)fcAlloc(t, sizeof *r);
    r->name = name;
    r->type = svFromTypeName(x, typeName, false, context);
    r->typeName = typeName;
    r->isTree = target && svIsTreeTarget(target);
    r->target = target;
    r->method = method ? fcFmt(t, "%s.%s", method->owner, method->name) : NULL;
    return r;
}

static SvLambdaParam* svLambdaParameterAt(SvIndex* x, const char* name, int offset, int depth) {
    int j, level = 0;
    bool sibling = false;
    if (depth > SV_MAX_DEPTH) return NULL;
    for (j = svLastTokenBefore(x, offset); j >= 0; j--) {
        switch (SVTY(x, j)) {
            case TT_RParen: case TT_RBracket: level++; continue;
            case TT_LParen: case TT_LBracket: if (level > 0) level--; else sibling = false; continue;
            case TT_Semicolon: case TT_LBrace: case TT_RBrace: if (level == 0) return NULL; continue;
            case TT_Comma: if (level == 0) sibling = true; continue;
            case TT_Arrow:
                if (level == 0 && !sibling) {
                    int lambdaStart, at;
                    FcList* ps = svLambdaParamsBefore(x, j, &lambdaStart);
                    if (ps)
                        for (at = 0; at < ps->count; at++)
                            if (strcmp(((SvParam*)ps->items[at])->name, name) == 0)
                                return svDescribeLambdaParam(x, name, at, ((SvParam*)ps->items[at])->type, lambdaStart, offset, depth);
                }
                continue;
            default: continue;
        }
    }
    return NULL;
}

static FcList* svEnclosingFunctionParams(SvIndex* x, int offset) {
    SvFunc* f = svEnclosingFunction(x, offset);
    return f ? f->params : fcListNew(x->lang->tmp);
}

static bool svIsInStaticMethod(SvIndex* x, int offset) {
    const char* cls = svEnclosingClassAt(x, offset);
    SvClass* info = svClassGet(x, cls);
    int at, i;
    if (!info) return false;
    at = svLastTokenBefore(x, offset);
    for (i = 0; i < info->members->count; i++) {
        SvMember* m = (SvMember*)info->members->items[i];
        if (m->source == x && m->kind == SM_METHOD && m->bodyStart >= 0 && at >= m->bodyStart && at <= m->bodyEnd) return m->isStatic;
    }
    return false;
}

/* ---- public entry points ---- */
static SvType svResolveReceiver(SvIndex* x, int dotOffset) {
    int dotIdx = svFindTokenIndex(x, dotOffset);
    if (dotIdx <= 0 || SVTY(x, dotIdx) != TT_Dot) return svUnknown;
    return svEvalExprRange(x, svFirstTokenOfChainEndingAt(x, dotIdx - 1), dotIdx - 1, 0, true);
}

static void svCollectDeclaredNames(SvIndex* x, int from, int until, int offset, FcList* result, bool topLevelOnly) {
    int n = x->ntok, k;
    for (k = from; k < n - 1; k++) {
        int skipped, bodyEnd;
        const FcToken* t;
        const char* declared = NULL;
        if (svTokenOffset(x, k) >= until) break;
        skipped = topLevelOnly && svScopeBodyEnd(x, k, &bodyEnd) ? bodyEnd + 1 : svSkipForeignBody(x, k, offset);
        if (skipped != k) { k = skipped - 1; continue; }
        t = SVT(x, k);
        if (t->type == TT_Var && SVTY(x, k + 1) == TT_Identifier) declared = SVT(x, k + 1)->lexeme;
        else if (t->type == TT_Foreach && k + 3 < n && SVTY(x, k + 1) == TT_LParen && SVTY(x, k + 2) == TT_Identifier && SVTY(x, k + 3) == TT_In) declared = SVT(x, k + 2)->lexeme;
        else if ((t->type == TT_Identifier || svIsTypeKeyword(t->type)) && SVTY(x, k + 1) == TT_Identifier && (k == 0 || (SVTY(x, k - 1) != TT_Dot && SVTY(x, k - 1) != TT_New))
                 && !SVT(x, k + 1)->newlineBefore
                 && (svIsPrimitiveName(t->lexeme) || svResolveClassKey(x, t->lexeme, svContextAt(x, offset), true) || svResolveEnumKey(x, t->lexeme, svContextAt(x, offset))))
            declared = SVT(x, k + 1)->lexeme;
        if (declared && svTokenOffset(x, k + 1) < until && !fcListHasStr(result, declared)) fcListAdd(x->lang->tmp, result, (void*)declared);
    }
}

static FcList* svLocalVarsBeforeCursor(SvIndex* x, int offset) {
    FcList* result = fcListNew(x->lang->tmp);
    FcList* chain = svEnclosingFunctions(x, offset);
    int c2, p;
    for (c2 = 0; c2 + 1 < chain->count; c2++) {
        FcList* ps = ((SvFunc*)chain->items[c2])->params;
        for (p = 0; p < ps->count; p++) if (!fcListHasStr(result, ((SvParam*)ps->items[p])->name)) fcListAdd(x->lang->tmp, result, (void*)((SvParam*)ps->items[p])->name);
    }
    svCollectDeclaredNames(x, svScopeStart(chain), offset, offset, result, false);
    if (chain->count > 0) svCollectDeclaredNames(x, 0, INT32_MAX, offset, result, true);
    return result;
}

static bool svIsStatementStart(SvIndex* x, int k) {
    FcTokenType p;
    if (k == 0 || SVT(x, k)->newlineBefore) return true;
    p = SVTY(x, k - 1);
    return p == TT_Semicolon || p == TT_LBrace || p == TT_RBrace || p == TT_RParen || p == TT_Else;
}

static SvType svScanDeclarations(SvIndex* x, const char* name, int from, int until, int offset, int depth, FcList* context, bool* found, SvType result, bool explicitType, bool topLevelOnly) {
    int n = x->ntok, k;
    for (k = from; k < n; k++) {
        int skipped, bodyEnd, loopVar;
        const FcToken* t;
        if (svTokenOffset(x, k) >= until) break;
        skipped = topLevelOnly && svScopeBodyEnd(x, k, &bodyEnd) ? bodyEnd + 1 : svSkipForeignBody(x, k, offset);
        if (skipped != k) { k = skipped - 1; continue; }
        t = SVT(x, k);
        if (t->type == TT_Var && k + 1 < n && SVTY(x, k + 1) == TT_Identifier && svLex(x, k + 1, name) && !(k >= 2 && SVTY(x, k - 1) == TT_LParen && SVTY(x, k - 2) == TT_Foreach)) {
            int statementEnd = svExpressionEnd(x, k + 1, n), q;
            *found = true;
            result = svUnknown;
            explicitType = false;
            for (q = k + 2; q < statementEnd; q++)
                if (SVTY(x, q) == TT_Assign) { result = svEvalExprRange(x, q + 1, svExpressionEnd(x, q + 1, n) - 1, depth + 1, false); break; }
            continue;
        }
        loopVar = t->type == TT_Foreach && k + 3 < n && SVTY(x, k + 1) == TT_LParen ? (SVTY(x, k + 2) == TT_Var ? k + 3 : k + 2) : -1;
        if (loopVar > 0 && loopVar + 1 < n && SVTY(x, loopVar) == TT_Identifier && svLex(x, loopVar, name) && SVTY(x, loopVar + 1) == TT_In) {
            int close = svMatchForward(x, k + 1, TT_LParen, TT_RParen);
            SvType iterable = close > loopVar + 2 ? svEvalExprRange(x, loopVar + 2, close - 1, depth + 1, false) : svUnknown;
            *found = true;
            explicitType = false;
            result = iterable.kind == TK_ARRAY ? svFromTypeName(x, iterable.name, false, NULL) : svUnknown;
            continue;
        }
        if ((t->type == TT_Identifier || svIsTypeKeyword(t->type)) && k + 1 < n && SVTY(x, k + 1) == TT_Identifier && svLex(x, k + 1, name) && !SVT(x, k + 1)->newlineBefore) {
            int typeStart = k;
            while (typeStart >= 2 && SVTY(x, typeStart - 1) == TT_Dot && SVTY(x, typeStart - 2) == TT_Identifier) typeStart -= 2;
            if (typeStart == 0 || (SVTY(x, typeStart - 1) != TT_Dot && SVTY(x, typeStart - 1) != TT_New)) {
                bool isArray = k + 2 < n && SVTY(x, k + 2) == TT_LBracket;
                FcBuf b = {0};
                int q;
                fcBufStr(&b, "");
                for (q = typeStart; q <= k; q++) fcBufStr(&b, SVT(x, q)->lexeme);
                *found = true;
                result = svFromTypeName(x, fcBufDone(x->lang->tmp, &b), isArray, context);
                explicitType = result.kind != TK_UNKNOWN;
                continue;
            }
        }
        if (t->type == TT_Gt && k + 1 < n && SVTY(x, k + 1) == TT_Identifier && svLex(x, k + 1, name) && !SVT(x, k + 1)->newlineBefore) {
            int lt = svMatchBackward(x, k, TT_Lt, TT_Gt);
            if (lt > 0 && SVTY(x, lt - 1) == TT_Identifier && svIsStatementStart(x, lt - 1)) {
                SvType generic = svFromTypeName(x, SVT(x, lt - 1)->lexeme, k + 2 < n && SVTY(x, k + 2) == TT_LBracket, context);
                if (generic.kind != TK_UNKNOWN) { *found = true; result = generic; explicitType = true; continue; }
            }
        }
        if (!explicitType && t->type == TT_Identifier && svLex(x, k, name) && k + 1 < n && SVTY(x, k + 1) == TT_Assign && svIsStatementStart(x, k)) {
            SvType assigned = svEvalExprRange(x, k + 2, svExpressionEnd(x, k + 2, n) - 1, depth + 1, false);
            if (assigned.kind != TK_UNKNOWN) { *found = true; result = assigned; }
        }
    }
    return result;
}

static SvType svFindLocal(SvIndex* x, const char* name, int offset, int depth, bool* found) {
    SvType result = svUnknown;
    bool explicitType = false;
    SvLambdaParam* lp;
    FcList* context;
    FcList* chain;
    int f, p;
    *found = false;
    if ((lp = svLambdaParameterAt(x, name, offset, depth)) != NULL) { *found = true; return lp->type; }
    context = svContextAt(x, offset);
    chain = svEnclosingFunctions(x, offset);
    for (f = 0; f < chain->count; f++) {
        FcList* ps = ((SvFunc*)chain->items[f])->params;
        for (p = 0; p < ps->count; p++) {
            SvParam* prm = (SvParam*)ps->items[p];
            if (strcmp(prm->name, name) != 0) continue;
            *found = true;
            result = svFromTypeName(x, prm->type, false, context);
            explicitType = result.kind != TK_UNKNOWN;
        }
    }
    return svScanDeclarations(x, name, svScopeStart(chain), offset, offset, depth, context, found, result, explicitType, false);
}

static SvType svFindGlobal(SvIndex* x, const char* name, int offset, int depth, bool* found) {
    *found = false;
    if (svEnclosingFunctions(x, offset)->count == 0) return svUnknown;
    return svScanDeclarations(x, name, 0, INT32_MAX, offset, depth, x->usings, found, svUnknown, false, true);
}

static SvType svResolveName(SvIndex* x, const char* name, int offset, int depth) {
    bool found;
    SvType local, global;
    const char* enclosing;
    const char* k;
    if (depth > SV_MAX_DEPTH) return svUnknown;
    local = svFindLocal(x, name, offset, depth, &found);
    if (found) return local;
    enclosing = svEnclosingClassAt(x, offset);
    if (enclosing) {
        SvMember* m = svFindMember(x, enclosing, name, 1);
        if (m) return svTypeOfMember(x, m, depth);
    }
    global = svFindGlobal(x, name, offset, depth, &found);
    if (found) return global;
    if ((k = svDictKey(&x->classes, name)) != NULL) return svT(TK_STATIC, k);
    if ((k = svDictKey(&x->enumMembers, name)) != NULL) return svT(TK_ENUM, k);
    if ((k = svDictKey(&x->namespaces, name)) != NULL) return svT(TK_NAMESPACE, k);
    return svUnknown;
}
