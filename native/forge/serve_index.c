/* native/forge/serve_index.c - the symbol index of `forge serve` (docs/spec/FORGE_SERVE.md): src/fire.Editor/Language/ScriptSymbolIndex.cs and
 * ScriptSymbolIndex.Types.cs (commit 1c549476), method for method (L3). Token based on the real lexer, not the parser (which fails exactly where the user
 * types); the type derivation of expressions for the completion. Differences to the C#: the caches of the preludes, the imports and the other files of
 * the project hang on SvLang (the server), not on statics; a class that another index shares and that this index completes (a `class extends X`
 * placeholder) is copied first instead of changed in place; `AllDeclaredNames` is not collected (nothing reads it any more).
 *
 * Memory: every index has its own context (its arena: tokens, classes, members, the lazily made caches); what a question computes on the way (the
 * chains of an expression, the lists of a scan) goes to the scratch context of the language state, emptied after every request. */

typedef enum SvMemberKind { SM_FIELD, SM_METHOD, SM_PROPERTY, SM_CONSTRUCTOR } SvMemberKind;
typedef enum SvAccess { SA_PUBLIC, SA_PROTECTED, SA_PRIVATE } SvAccess;
typedef enum SvNsKind { SN_NAMESPACE, SN_CLASS, SN_INTERFACE, SN_ENUM } SvNsKind;
typedef enum SvTypeKind { TK_UNKNOWN, TK_INSTANCE, TK_STATIC, TK_ENUM, TK_PRIMITIVE, TK_ARRAY, TK_NAMESPACE } SvTypeKind;

typedef struct SvIndex SvIndex;
typedef struct SvLang SvLang;

typedef struct SvParam { const char* name; const char* type; } SvParam;

typedef struct SvMember {
    const char* name;
    SvMemberKind kind;
    int paramCount;
    int declLine;
    const char* typeName;      /* NULL: none */
    bool typeIsArray;
    bool isStatic;
    SvAccess access;
    const char* signature;     /* NULL: none */
    FcList* paramNames;        /* const char* */
    FcList* paramTypes;        /* const char* or NULL */
    const char* owner;
    SvIndex* source;
    int bodyStart, bodyEnd;
} SvMember;

typedef struct SvRange { int start, end; } SvRange;

typedef struct SvClass {
    const char* name;          /* fully qualified */
    const char* simpleName;
    const char* ns;
    FcList* context;           /* const char* */
    SvIndex* source;
    FcList* baseNames;         /* const char* */
    FcList* members;           /* SvMember* */
    FcList* bodyRanges;        /* SvRange* (token indexes in source) */
    int declLine;
    const char* preludeName;   /* NULL: in the document */
    bool isInterface;
    bool isGenerated;
} SvClass;

typedef struct SvNsRange { const char* name; int start, end; } SvNsRange;
typedef struct SvPending { const char* target; FcList* context; int bodyStart; } SvPending;
typedef struct SvFunc { int bodyStartTok, bodyStartOff, bodyEndOff, bodyEndTok; FcList* params; /* SvParam* */ } SvFunc;
typedef struct SvType { SvTypeKind kind; const char* name; bool viaThis; } SvType;
typedef struct SvInclude { const char* path; int line; } SvInclude;

static const SvType svUnknown = { TK_UNKNOWN, NULL, false };
static SvType svT(SvTypeKind k, const char* name) { SvType t; t.kind = k; t.name = name; t.viaThis = false; return t; }

struct SvIndex {
    FcCtx* c;                  /* own arena (malloc'd context) */
    SvLang* lang;
    FcDict classes;            /* name -> SvClass* */
    FcDict enumMembers;        /* name -> FcList* of const char* */
    FcDict enumDeclLines;      /* name -> FC_IVAL(line) */
    FcDict enumPreludes;       /* name -> const char* */
    FcDict enumFiles;          /* name -> path */
    const char* preludeName;   /* NULL: an ordinary document */
    const char* filePath;      /* a file of the project (NULL: the document, a prelude) */
    FcList* includes;          /* SvInclude* */
    FcToken* tok;
    int ntok;
    const uint16_t* src;
    int srcLen;
    int* lineStarts;
    int nLines;
    FcDict namespaces;         /* set */
    FcList* usings;            /* const char* */
    FcList* nsRanges;          /* SvNsRange* */
    FcList* classSpans;        /* SvNsRange* (key, start, end offsets) */
    FcList* nsStack;           /* SvNsRange* (name, endIdx) */
    FcList* pending;           /* SvPending* */
    int* tokenOffsets;
    FcList* functions;         /* SvFunc*, lazily */
    FcDict* scopeBodies;       /* FC_IVAL(start) -> FC_IVAL(end), lazily */
    FcList* uses;              /* the indexes of other files merged in (their reference counts) */
    int refs;                  /* (an index of a file of the project: the document indexes that use it) */
    bool retired;
};

/* ---- the language state: what C# keeps in statics ---- */
typedef struct SvProjFile { const char* path; uint64_t hash; int len; uint16_t* text; SvIndex* index; FcList* imports; } SvProjFile;

struct SvLang {
    FcCtx pctx;                /* persistent: the preludes' indexes, the caches */
    FcCtx* tmp;                /* scratch of the current request */
    const char* preludePath;
    const char* storeRoot;
    FcLinker* L;               /* the store and ParseImportName/WithDependencies */
    SvIndex* prelude;
    FcDict importedPreludes;   /* key -> SvIndex* (NULL value: none) */
    FcDict importKeys;         /* lower-case name -> FcList* (NULL: no extension) */
    FcDict projFiles;          /* lower-case path -> SvProjFile* */
    FcList* retired;           /* SvIndex* of files of the project replaced while documents still use them */
};

static const char* svLowerAscii(FcCtx* c, const char* s) { return fcAsciiLower(c, s); }

/* the context of a new index */
static FcCtx* svNewCtx(void) {
    FcCtx* c = (FcCtx*)malloc(sizeof *c);
    if (!c) fbOutOfMemory();
    fcCtxInit(c);
    return c;
}
static void svFreeCtx(FcCtx* c) { if (c) { fcCtxFree(c); free(c); } }

static uint64_t svHash(const uint16_t* s, int n) {
    uint64_t h = 1469598103934665603ULL;
    int i;
    for (i = 0; i < n; i++) { h ^= s[i]; h *= 1099511628211ULL; }
    return h;
}

/* ---- texts (UTF-16 source) ---- */
static bool svIsWord(uint16_t ch) { return fcIsLetterOrDigit(ch) || ch == '_'; }
static bool svIsSpaceTab(uint16_t ch) { return ch == ' ' || ch == '\t'; }
/* OrdinalIgnoreCase (ASCII folding: the names of the language) */
static int svFold(int ch) { return ch >= 'a' && ch <= 'z' ? ch - 32 : ch; }
static bool svStartsWithIC(const char* s, const char* prefix) {
    for (; *prefix; s++, prefix++) if (!*s || svFold((unsigned char)*s) != svFold((unsigned char)*prefix)) return false;
    return true;
}
static int svCmpIC(const char* a, const char* b) {
    for (;; a++, b++) {
        int x = svFold((unsigned char)*a), y = svFold((unsigned char)*b);
        if (x != y || !x) return x - y;
    }
}
static bool svEqIC(const char* a, const char* b) { return svCmpIC(a, b) == 0; }
static bool svContainsIC(const char* s, const char* part) {
    size_t n = strlen(part);
    if (n == 0) return true;
    for (; *s; s++) if (svStartsWithIC(s, part)) return true;
    (void)n;
    return false;
}
static bool svHasChar(const char* s, char ch) { return strchr(s, ch) != NULL; }
static const char* svAfterLastDot(const char* s) { const char* d = strrchr(s, '.'); return d ? d + 1 : s; }

/* ---- the token stream ---- */
#define SVT(x, i) (&(x)->tok[i])
#define SVTY(x, i) ((x)->tok[i].type)
static bool svIsTypeKeyword(FcTokenType t) { return t == TT_KwBool || t == TT_KwInt || t == TT_KwFloat || t == TT_KwChar || t == TT_KwString || t == TT_KwByte || t == TT_KwWord; }
static bool svLex(const SvIndex* x, int i, const char* s) { return strcmp(x->tok[i].lexeme, s) == 0; }

static int svOffsetOf(const SvIndex* x, const FcToken* t) {
    int idx = t->line - 1;
    if (idx < 0 || idx >= x->nLines) return 0;
    return x->lineStarts[idx] + (t->column - 1);
}
static int svTokenOffset(SvIndex* x, int i) {
    if (!x->tokenOffsets) {
        int k;
        x->tokenOffsets = (int*)fcAlloc(x->c, sizeof(int) * (size_t)(x->ntok + 1));
        for (k = 0; k < x->ntok; k++) x->tokenOffsets[k] = svOffsetOf(x, &x->tok[k]);
    }
    return x->tokenOffsets[i];
}

static void svComputeLineStarts(SvIndex* x) {
    int i, n = 1;
    for (i = 0; i < x->srcLen; i++) if (x->src[i] == '\n') n++;
    x->lineStarts = (int*)fcAlloc(x->c, sizeof(int) * (size_t)n);
    x->nLines = 0;
    x->lineStarts[x->nLines++] = 0;
    for (i = 0; i < x->srcLen; i++) if (x->src[i] == '\n') x->lineStarts[x->nLines++] = i + 1;
}

static SvIndex* svIndexNew(SvLang* S, const uint16_t* src, int len) {
    FcCtx* c = svNewCtx();
    SvIndex* x = (SvIndex*)fcAlloc(c, sizeof *x);
    uint16_t* copy = (uint16_t*)fcAlloc(c, sizeof(uint16_t) * (size_t)(len + 1));
    volatile bool ok = false;
    x->c = c;
    x->lang = S;
    if (len) memcpy(copy, src, sizeof(uint16_t) * (size_t)len);
    x->src = copy;
    x->srcLen = len;
    fcDictInit(c, &x->classes, FC_KEY_STR);
    fcDictInit(c, &x->enumMembers, FC_KEY_STR);
    fcDictInit(c, &x->enumDeclLines, FC_KEY_STR);
    fcDictInit(c, &x->enumPreludes, FC_KEY_STR);
    fcDictInit(c, &x->enumFiles, FC_KEY_STR);
    fcDictInit(c, &x->namespaces, FC_KEY_STR);
    x->includes = fcListNew(c);
    x->usings = fcListNew(c);
    x->nsRanges = fcListNew(c);
    x->classSpans = fcListNew(c);
    x->nsStack = fcListNew(c);
    x->pending = fcListNew(c);
    x->uses = fcListNew(c);
    svComputeLineStarts(x);
    /* new Lexer(source).Tokenize(); a LexException: no tokens */
    FC_TRY(c) {
        FcTokens ts = fcTokenize(c, copy, len);
        x->tok = ts.items;
        x->ntok = ts.count;
        ok = true;
    } FC_CATCH(c) {
        ok = false;
    } FC_END_TRY;
    if (!ok) { x->tok = NULL; x->ntok = 0; }
    return x;
}

static void svIndexRelease(SvLang* S, SvIndex* x);
static void svIndexFree(SvLang* S, SvIndex* x) {
    int i;
    if (!x) return;
    for (i = 0; i < x->uses->count; i++) svIndexRelease(S, (SvIndex*)x->uses->items[i]);
    svFreeCtx(x->c);
}
/* a document index stops using the index of a file of the project */
static void svIndexRelease(SvLang* S, SvIndex* x) {
    int i;
    if (--x->refs > 0 || !x->retired) return;
    for (i = 0; i < S->retired->count; i++) if (S->retired->items[i] == x) { fcListRemoveAt(S->retired, i); break; }
    svIndexFree(S, x);
}

/* ---- regular expressions of the C#, by hand ---- */

/* UsingLine: ^[ \t]*#using[ \t]+(Name(\.Name)*)[ \t]*\r?$ (multiline, ignore case), distinct */
static void svFindUsings(SvIndex* x) {
    const uint16_t* s = x->src;
    int n = x->srcLen, i = 0;
    while (i <= n) {
        int e = i, p = i;
        while (e < n && s[e] != '\n') e++;
        while (p < e && svIsSpaceTab(s[p])) p++;
        if (p + 6 <= e && s[p] == '#' && svFold(s[p + 1]) == 'U' && svFold(s[p + 2]) == 'S' && svFold(s[p + 3]) == 'I' && svFold(s[p + 4]) == 'N' && svFold(s[p + 5]) == 'G') {
            int q = p + 6, ns, ne, r;
            bool ok = q < e && svIsSpaceTab(s[q]);
            while (q < e && svIsSpaceTab(s[q])) q++;
            ns = q;
            /* names */
            while (ok) {
                if (!(q < e && ((s[q] >= 'A' && s[q] <= 'Z') || (s[q] >= 'a' && s[q] <= 'z') || s[q] == '_'))) { ok = false; break; }
                while (q < e && ((s[q] >= 'A' && s[q] <= 'Z') || (s[q] >= 'a' && s[q] <= 'z') || (s[q] >= '0' && s[q] <= '9') || s[q] == '_')) q++;
                if (q + 1 < e && s[q] == '.' && ((s[q + 1] >= 'A' && s[q + 1] <= 'Z') || (s[q + 1] >= 'a' && s[q + 1] <= 'z') || s[q + 1] == '_')) { q++; continue; }
                break;
            }
            ne = q;
            r = q;
            while (r < e && svIsSpaceTab(s[r])) r++;
            if (r < e && s[r] == '\r' && r + 1 == e) r++;
            if (ok && r == e) {
                const char* name = fc8FromU16(x->c, s + ns, ne - ns);
                if (!fcListHasStr(x->usings, name)) fcListAdd(x->c, x->usings, (void*)name);
            }
        }
        i = e + 1;
    }
}

/* IncludeLine: ^\s*#include\s+"([^"]*)"\s*$ on each line (TrimEnd '\r') */
static void svHarvestIncludes(SvIndex* x) {
    const uint16_t* s = x->src;
    int n = x->srcLen, i = 0, line = 1;
    while (i <= n) {
        int e = i, p = i, end;
        while (e < n && s[e] != '\n') e++;
        end = e;
        while (end > i && s[end - 1] == '\r') end--;
        while (p < end && fcIsWhiteSpace(s[p])) p++;
        if (p + 8 <= end && s[p] == '#' && s[p + 1] == 'i' && s[p + 2] == 'n' && s[p + 3] == 'c' && s[p + 4] == 'l' && s[p + 5] == 'u' && s[p + 6] == 'd' && s[p + 7] == 'e') {
            int q = p + 8, a;
            if (q < end && fcIsWhiteSpace(s[q])) {
                while (q < end && fcIsWhiteSpace(s[q])) q++;
                if (q < end && s[q] == '"') {
                    a = ++q;
                    while (q < end && s[q] != '"') q++;
                    if (q < end) {
                        int r = q + 1;
                        while (r < end && fcIsWhiteSpace(s[r])) r++;
                        if (r == end) {
                            SvInclude* inc = (SvInclude*)fcAlloc(x->c, sizeof *inc);
                            inc->path = fc8FromU16(x->c, s + a, q - a);
                            inc->line = line;
                            fcListAdd(x->c, x->includes, inc);
                        }
                    }
                }
            }
        }
        i = e + 1;
        line++;
    }
}

/* ImportedPreludes.FindImportNames: `^[ \t]*#import[ \t]+"([^"\r\n]+)"` over the text with the inactive branches of `#if` blanked
 * (ConditionalSymbols.Apply with the symbols of the host), distinct ignoring case */
static FcList* svFindImportNames(FcCtx* c, const uint16_t* s, int n) {
    FcList* names = fcListNew(c);
    FcList* symbols = fcListNew(c);
    bool active[64];
    bool taken[64];
    int depth = 0, i = 0;
    fcSymbolsForHost(c, symbols);
    while (i <= n) {
        int e = i, p = i;
        bool on = true;
        int k;
        while (e < n && s[e] != '\n') e++;
        while (p < e && svIsSpaceTab(s[p])) p++;
        for (k = 0; k < depth; k++) if (!active[k]) on = false;
        if (p < e && s[p] == '#') {
            int q = p + 1, ws;
            const char* word;
            while (q < e && ((s[q] >= 'a' && s[q] <= 'z') || (s[q] >= 'A' && s[q] <= 'Z'))) q++;
            word = fc8FromU16(c, s + p + 1, q - p - 1);
            ws = q;
            while (ws < e && (svIsSpaceTab(s[ws]) || s[ws] == '\r')) ws++;
            if ((svEqIC(word, "if") || svEqIC(word, "elif")) && depth < 64) {
                FcSlice expr;
                volatile bool value = false;
                int ee = e;
                while (ee > ws && (s[ee - 1] == '\r' || svIsSpaceTab(s[ee - 1]))) ee--;
                expr.p = s + ws; expr.n = ee - ws;
                FC_TRY(c) { value = fcEvaluateCondition(c, expr, symbols, 1); } FC_CATCH(c) { value = false; } FC_END_TRY;
                if (svEqIC(word, "if")) { active[depth] = value; taken[depth] = value; depth++; }
                else if (depth > 0) { active[depth - 1] = !taken[depth - 1] && value; if (value) taken[depth - 1] = true; }
                i = e + 1;
                continue;
            }
            if (svEqIC(word, "else") && depth > 0) { active[depth - 1] = !taken[depth - 1]; taken[depth - 1] = true; i = e + 1; continue; }
            if (svEqIC(word, "endif") && depth > 0) { depth--; i = e + 1; continue; }
            if (on && strcmp(word, "import") == 0 && q < e && svIsSpaceTab(s[q])) {
                int a;
                while (q < e && svIsSpaceTab(s[q])) q++;
                if (q < e && s[q] == '"') {
                    a = ++q;
                    while (q < e && s[q] != '"' && s[q] != '\r') q++;
                    if (q < e && s[q] == '"' && q > a) {
                        const char* name = fc8FromU16(c, s + a, q - a);
                        bool dup = false;
                        for (k = 0; k < names->count; k++) if (svEqIC((const char*)names->items[k], name)) dup = true;
                        if (!dup) fcListAdd(c, names, (void*)name);
                    }
                }
            }
        }
        i = e + 1;
    }
    return names;
}

/* ---- harvest ---- */
static const char* svCurrentNamespace(SvIndex* x) { return x->nsStack->count > 0 ? ((SvNsRange*)x->nsStack->items[x->nsStack->count - 1])->name : ""; }
static const char* svQualify(SvIndex* x, const char* simple) { const char* ns = svCurrentNamespace(x); return ns[0] ? fcFmt(x->c, "%s.%s", ns, simple) : simple; }
static FcList* svCurrentContext(SvIndex* x) {
    const char* ns = svCurrentNamespace(x);
    FcList* r = fcListNew(x->c);
    int i;
    if (ns[0]) fcListAdd(x->c, r, (void*)ns);
    for (i = 0; i < x->usings->count; i++) fcListAdd(x->c, r, x->usings->items[i]);
    return r;
}

static int svMatchBrace(SvIndex* x, int open) {
    int depth = 0, j;
    for (j = open; j < x->ntok; j++) {
        if (SVTY(x, j) == TT_LBrace) depth++;
        else if (SVTY(x, j) == TT_RBrace && --depth == 0) return j;
    }
    return x->ntok - 1;
}

static int svHarvestNamespace(SvIndex* x, int i) {
    int j = i + 1, end;
    const char* name;
    const char* full;
    const char* part;
    SvNsRange* r;
    if (j >= x->ntok || SVTY(x, j) != TT_Identifier) return i + 1;
    name = SVT(x, j)->lexeme;
    j++;
    while (j + 1 < x->ntok && SVTY(x, j) == TT_Dot && SVTY(x, j + 1) == TT_Identifier) { name = fcFmt(x->c, "%s.%s", name, SVT(x, j + 1)->lexeme); j += 2; }
    if (j >= x->ntok || SVTY(x, j) != TT_LBrace) return i + 1;
    full = svQualify(x, name);
    end = svMatchBrace(x, j);
    r = (SvNsRange*)fcAlloc(x->c, sizeof *r); r->name = full; r->start = 0; r->end = end;
    fcListAdd(x->c, x->nsStack, r);
    r = (SvNsRange*)fcAlloc(x->c, sizeof *r); r->name = full; r->start = svTokenOffset(x, j); r->end = svTokenOffset(x, end) + 1;
    fcListAdd(x->c, x->nsRanges, r);
    for (part = full;;) {
        const char* dot;
        fcSetAdd(&x->namespaces, part);
        dot = strrchr(part, '.');
        if (!dot) break;
        part = fcStrN(x->c, part, (size_t)(dot - part));
    }
    return j + 1;
}

static SvClass* svClassNew(SvIndex* x, const char* name, int declLine) {
    SvClass* k = (SvClass*)fcAlloc(x->c, sizeof *k);
    const char* dot = strrchr(name, '.');
    k->name = name;
    k->declLine = declLine;
    k->ns = dot ? fcStrN(x->c, name, (size_t)(dot - name)) : "";
    k->simpleName = dot ? dot + 1 : name;
    k->context = fcListNew(x->c);
    k->baseNames = fcListNew(x->c);
    k->members = fcListNew(x->c);
    k->bodyRanges = fcListNew(x->c);
    k->source = x;
    return k;
}
static SvClass* svClassGet(const SvIndex* x, const char* name) { return name ? (SvClass*)fcDictGet(&x->classes, name) : NULL; }
static bool svClassFromPrelude(const SvClass* k) { return k->preludeName != NULL; }

static SvMember* svMemberNew(SvIndex* x, const char* name, SvMemberKind kind, int paramCount, int declLine) {
    SvMember* m = (SvMember*)fcAlloc(x->c, sizeof *m);
    m->name = name; m->kind = kind; m->paramCount = paramCount; m->declLine = declLine;
    m->access = SA_PUBLIC;
    m->paramNames = fcListNew(x->c);
    m->paramTypes = fcListNew(x->c);
    m->bodyStart = m->bodyEnd = -1;
    m->owner = "";
    return m;
}
static void svAddMember(SvIndex* x, SvClass* info, SvMember* m) { m->owner = info->name; m->source = x; fcListAdd(x->c, info->members, m); }

static int svSkipAngleBrackets(SvIndex* x, int lt) {
    int depth = 0, j;
    for (j = lt; j < x->ntok; j++) {
        FcTokenType t = SVTY(x, j);
        if (t == TT_Lt) depth++;
        else if (t == TT_Gt) { if (--depth == 0) return j + 1; }
        else if (t == TT_LBrace || t == TT_LParen || t == TT_Eof) return lt + 1;
    }
    return lt + 1;
}

static int svHarvestMembersBody(SvIndex* x, int bodyStart, SvClass* info);
static FcList* svReadParamList(SvIndex* x, int paren, int* afterParen);
static int svCountParams(SvIndex* x, int paren);
static int svFindBodyBrace(SvIndex* x, int afterParen);

static int svHarvestClassExtension(SvIndex* x, int i) {
    const char* target;
    SvPending* p;
    if (i >= x->ntok) return i;
    if (SVTY(x, i) == TT_KwString || SVTY(x, i) == TT_KwChar || SVTY(x, i) == TT_KwInt || SVTY(x, i) == TT_KwFloat || SVTY(x, i) == TT_KwBool) {
        const char* cls = fcFmt(x->c, "$%s", SVT(x, i)->lexeme);
        i++;
        if (i >= x->ntok || SVTY(x, i) != TT_LBrace) return i;
        p = (SvPending*)fcAlloc(x->c, sizeof *p); p->target = cls; p->context = svCurrentContext(x); p->bodyStart = i;
        fcListAdd(x->c, x->pending, p);
        return svMatchBrace(x, i) + 1;
    }
    if (SVTY(x, i) != TT_Identifier) return i;
    target = SVT(x, i)->lexeme;
    i++;
    while (i + 1 < x->ntok && SVTY(x, i) == TT_Dot && SVTY(x, i + 1) == TT_Identifier) { target = fcFmt(x->c, "%s.%s", target, SVT(x, i + 1)->lexeme); i += 2; }
    if (i >= x->ntok || SVTY(x, i) != TT_LBrace) return i;
    p = (SvPending*)fcAlloc(x->c, sizeof *p); p->target = target; p->context = svCurrentContext(x); p->bodyStart = i;
    fcListAdd(x->c, x->pending, p);
    return svMatchBrace(x, i) + 1;
}

static int svHarvestClass(SvIndex* x, int i, bool isInterface) {
    const char* className;
    SvClass* info;
    i++;
    if (!isInterface && i < x->ntok && SVTY(x, i) == TT_Extends) return svHarvestClassExtension(x, i + 1);
    if (i >= x->ntok || SVTY(x, i) != TT_Identifier) return i;
    if (i + 1 < x->ntok && SVTY(x, i + 1) == TT_LParen) return i;
    className = svQualify(x, SVT(x, i)->lexeme);
    info = svClassGet(x, className);
    if (!info) { info = svClassNew(x, className, SVT(x, i)->line); info->context = svCurrentContext(x); }
    info->declLine = SVT(x, i)->line;
    info->isInterface = isInterface;
    i++;
    if (i < x->ntok && SVTY(x, i) == TT_Lt) i = svSkipAngleBrackets(x, i);
    if (i < x->ntok && SVTY(x, i) == TT_Colon) {
        i++;
        while (i < x->ntok && SVTY(x, i) != TT_LBrace && !(SVTY(x, i) == TT_Identifier && streq(SVT(x, i)->lexeme, "where")) && SVTY(x, i) != TT_Eof) {
            if (SVTY(x, i) == TT_Identifier) {
                const char* baseName = SVT(x, i)->lexeme;
                while (i + 2 < x->ntok && SVTY(x, i + 1) == TT_Dot && SVTY(x, i + 2) == TT_Identifier) { baseName = fcFmt(x->c, "%s.%s", baseName, SVT(x, i + 2)->lexeme); i += 2; }
                if (!fcListHasStr(info->baseNames, baseName)) fcListAdd(x->c, info->baseNames, (void*)baseName);
            }
            i++;
        }
    }
    while (i < x->ntok && SVTY(x, i) != TT_LBrace && SVTY(x, i) != TT_Eof) i++;
    fcDictPut(&x->classes, className, info);
    if (i >= x->ntok || SVTY(x, i) != TT_LBrace) return i;
    return svHarvestMembersBody(x, i, info);
}

static int svSkipBrackets(SvIndex* x, int open, int end) {
    int depth = 0, j;
    for (j = open; j < end; j++) {
        if (SVTY(x, j) == TT_LBracket) depth++;
        else if (SVTY(x, j) == TT_RBracket && --depth == 0) return j + 1;
    }
    return end;
}

static int svSkipTypeSuffix(SvIndex* x, int i, int end) {
    if (i + 2 < end && SVTY(x, i) == TT_LBracket && SVTY(x, i + 1) == TT_IntLiteral && SVTY(x, i + 2) == TT_RBracket) i += 3;
    while (i < end && SVTY(x, i) == TT_Star) i++;
    return i;
}
static int svSkipEmptyBrackets(SvIndex* x, int i, int end, bool* found) {
    while (i + 1 < end && SVTY(x, i) == TT_LBracket && SVTY(x, i + 1) == TT_RBracket) { *found = true; i += 2; }
    return i;
}

static int svReadTypeThenName(SvIndex* x, int i, int end, const char** typeName, bool* isArray) {
    int j = i;
    const char* baseName;
    *typeName = NULL;
    *isArray = false;
    if (j + 1 < end && SVTY(x, j) == TT_Identifier && svLex(x, j, "defined") && (svIsTypeKeyword(SVTY(x, j + 1)) || SVTY(x, j + 1) == TT_Identifier) && !SVT(x, j + 1)->newlineBefore) j++;
    if (j >= end) return -1;
    if (svIsTypeKeyword(SVTY(x, j))) {
        baseName = SVT(x, j)->lexeme;
        j = svSkipTypeSuffix(x, j + 1, end);
    } else if (SVTY(x, j) == TT_Identifier) {
        int typeEnd = j, k;
        FcBuf b = {0};
        while (typeEnd + 2 < end && SVTY(x, typeEnd + 1) == TT_Dot && SVTY(x, typeEnd + 2) == TT_Identifier) typeEnd += 2;
        fcBufStr(&b, "");
        for (k = j; k <= typeEnd; k++) fcBufStr(&b, SVT(x, k)->lexeme);
        baseName = fcBufDone(x->c, &b);
        j = typeEnd + 1;
    } else return -1;
    if (j < end && SVTY(x, j) == TT_Lt) {
        int after = svSkipAngleBrackets(x, j);
        if (after == j + 1) return -1;
        j = after;
    }
    if (strcmp(baseName, "lambda") != 0 && j + 1 < end && SVTY(x, j) == TT_Identifier && svLex(x, j, "lambda") && !SVT(x, j)->newlineBefore
        && (SVTY(x, j + 1) == TT_Identifier || SVTY(x, j + 1) == TT_Lt)) {
        baseName = "lambda";
        j++;
    }
    if (strcmp(baseName, "lambda") == 0) {
        if (j + 1 < end && SVTY(x, j) == TT_Identifier && SVTY(x, j + 1) == TT_Lt
            && (svLex(x, j, "expression") || svLex(x, j, "field") || svLex(x, j, "property") || svLex(x, j, "member") || svLex(x, j, "method") || svLex(x, j, "selector"))) j++;
        if (j < end && SVTY(x, j) == TT_Lt) j = svSkipAngleBrackets(x, j);
    }
    j = svSkipEmptyBrackets(x, j, end, isArray);
    if (j < end && SVTY(x, j) == TT_Identifier && !SVT(x, j)->newlineBefore) { *typeName = baseName; return j; }
    *isArray = false;
    return -1;
}

static const char* svFormatSignature(FcCtx* c, FcList* parms) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 0; i < parms->count; i++) {
        SvParam* p = (SvParam*)parms->items[i];
        if (i) fcBufStr(&b, ", ");
        if (p->type) { fcBufStr(&b, p->type); fcBufChar(&b, ' '); }
        fcBufStr(&b, p->name);
    }
    return fcBufDone(c, &b);
}
static void svSetParams(SvIndex* x, SvMember* m, FcList* parms, bool names) {
    int i;
    m->signature = svFormatSignature(x->c, parms);
    for (i = 0; i < parms->count; i++) {
        SvParam* p = (SvParam*)parms->items[i];
        fcListAdd(x->c, m->paramTypes, (void*)p->type);
        if (names) fcListAdd(x->c, m->paramNames, (void*)p->name);
    }
}

/* ExpressionEnd */
static int svExpressionEnd(SvIndex* x, int startIdx, int limit) {
    int depth = 0, k;
    for (k = startIdx; k < limit; k++) {
        const FcToken* t = SVT(x, k);
        if (t->type == TT_Eof) return k;
        if (depth == 0 && k > startIdx && (t->newlineBefore || t->type == TT_Semicolon || t->type == TT_RBrace || t->type == TT_RParen || t->type == TT_RBracket || t->type == TT_Comma)) return k;
        if (t->type == TT_LParen || t->type == TT_LBracket || t->type == TT_LBrace) depth++;
        else if (t->type == TT_RParen || t->type == TT_RBracket || t->type == TT_RBrace) depth--;
    }
    return limit;
}

static int svHarvestMember(SvIndex* x, int i, int bodyEnd, SvClass* info, bool isStatic, SvAccess access) {
    int start = i, nameAt, nameIdx, afterName;
    const char* typeName;
    const char* name;
    bool returnsArray, isArray;
    SvMember* m;
    if (SVTY(x, i) == TT_Readonly) i++;
    if (i >= bodyEnd) return bodyEnd;
    nameAt = svReadTypeThenName(x, i, bodyEnd, &typeName, &returnsArray);
    if (nameAt >= 0) i = nameAt;
    else if (svIsTypeKeyword(SVTY(x, i))) return start + 1;
    if (i >= bodyEnd || SVTY(x, i) != TT_Identifier) return start + 1;
    name = SVT(x, i)->lexeme;
    nameIdx = i;
    afterName = i + 1;
    if (afterName < bodyEnd && SVTY(x, afterName) == TT_Lt) afterName = svSkipAngleBrackets(x, afterName);
    if (afterName < bodyEnd && SVTY(x, afterName) == TT_LParen) {
        int paramCount = svCountParams(x, afterName), afterParams, bodyOpen, bodyClose;
        FcList* parms = svReadParamList(x, afterName, &afterParams);
        bodyOpen = svFindBodyBrace(x, afterParams);
        bodyClose = bodyOpen >= 0 && bodyOpen < bodyEnd ? svMatchBrace(x, bodyOpen) : -1;
        m = svMemberNew(x, name, SM_METHOD, paramCount, SVT(x, nameIdx)->line);
        m->typeName = typeName; m->typeIsArray = returnsArray; m->isStatic = isStatic; m->access = access;
        svSetParams(x, m, parms, true);
        m->bodyStart = bodyClose >= 0 ? bodyOpen : -1;
        m->bodyEnd = bodyClose;
        svAddMember(x, info, m);
        return bodyClose >= 0 ? bodyClose + 1 : (afterParams < bodyEnd ? afterParams : bodyEnd);
    }
    if (afterName < bodyEnd && SVTY(x, afterName) == TT_Colon) {
        int unitEnd = afterName + 1;
        while (unitEnd < bodyEnd && !SVT(x, unitEnd)->newlineBefore && SVTY(x, unitEnd) != TT_LBrace && SVTY(x, unitEnd) != TT_Assign && SVTY(x, unitEnd) != TT_Semicolon && SVTY(x, unitEnd) != TT_RBrace) unitEnd++;
        afterName = unitEnd;
    }
    if (afterName < bodyEnd && SVTY(x, afterName) == TT_LBrace) {
        m = svMemberNew(x, name, SM_PROPERTY, 0, SVT(x, nameIdx)->line);
        m->typeName = typeName; m->typeIsArray = returnsArray; m->isStatic = isStatic; m->access = access;
        svAddMember(x, info, m);
        return svMatchBrace(x, afterName) + 1;
    }
    isArray = false;
    while (afterName < bodyEnd && SVTY(x, afterName) == TT_LBracket) {
        int close = afterName, depth = 0;
        isArray = true;
        for (; close < bodyEnd; close++) {
            if (SVTY(x, close) == TT_LBracket) depth++;
            else if (SVTY(x, close) == TT_RBracket && --depth == 0) break;
        }
        afterName = close + 1;
    }
    if (afterName < bodyEnd && SVTY(x, afterName) == TT_Assign) {
        if (!typeName && afterName + 2 < bodyEnd && SVTY(x, afterName + 1) == TT_New && SVTY(x, afterName + 2) == TT_Identifier) {
            typeName = SVT(x, afterName + 2)->lexeme;
            isArray = afterName + 3 < bodyEnd && SVTY(x, afterName + 3) == TT_LBracket;
        }
        afterName = svExpressionEnd(x, afterName + 1, bodyEnd);
    }
    m = svMemberNew(x, name, SM_FIELD, 0, SVT(x, nameIdx)->line);
    m->typeName = typeName; m->typeIsArray = isArray; m->isStatic = isStatic; m->access = access;
    svAddMember(x, info, m);
    while (afterName + 1 < bodyEnd && SVTY(x, afterName) == TT_Comma && SVTY(x, afterName + 1) == TT_Identifier) {
        int more = afterName + 1;
        m = svMemberNew(x, SVT(x, more)->lexeme, SM_FIELD, 0, SVT(x, more)->line);
        m->typeName = typeName; m->typeIsArray = isArray; m->isStatic = isStatic; m->access = access;
        svAddMember(x, info, m);
        afterName = more + 1;
        while (afterName < bodyEnd && SVTY(x, afterName) == TT_LBracket) afterName = svSkipBrackets(x, afterName, bodyEnd);
        if (afterName < bodyEnd && SVTY(x, afterName) == TT_Assign) afterName = svExpressionEnd(x, afterName + 1, bodyEnd);
    }
    return afterName;
}

static int svHarvestOperatorMember(SvIndex* x, int i, int bodyEnd, SvClass* info) {
    int line = SVT(x, i)->line, paramCount, afterParams, bodyOpen, bodyClose;
    const char* symbol;
    FcList* parms;
    SvMember* m;
    i++;
    if (i >= bodyEnd) return bodyEnd;
    if (SVTY(x, i) == TT_LBracket && i + 1 < bodyEnd && SVTY(x, i + 1) == TT_RBracket) { symbol = "[]"; i += 2; }
    else if (SVTY(x, i) == TT_Gt && i + 2 < bodyEnd && SVTY(x, i + 1) == TT_Gt && SVTY(x, i + 2) == TT_Gt) { symbol = ">>>"; i += 3; }
    else if ((SVTY(x, i) == TT_Lt && i + 1 < bodyEnd && SVTY(x, i + 1) == TT_Lt) || (SVTY(x, i) == TT_Gt && i + 1 < bodyEnd && SVTY(x, i + 1) == TT_Gt)) {
        symbol = SVTY(x, i) == TT_Lt ? "<<" : ">>";
        i += 2;
    } else { symbol = SVT(x, i)->lexeme; i++; }
    if (i >= bodyEnd || SVTY(x, i) != TT_LParen) return i;
    paramCount = svCountParams(x, i);
    parms = svReadParamList(x, i, &afterParams);
    bodyOpen = svFindBodyBrace(x, afterParams);
    bodyClose = bodyOpen >= 0 && bodyOpen < bodyEnd ? svMatchBrace(x, bodyOpen) : -1;
    m = svMemberNew(x, fcFmt(x->c, "operator%s", symbol), SM_METHOD, paramCount, line);
    svSetParams(x, m, parms, false);
    m->bodyStart = bodyClose >= 0 ? bodyOpen : -1;
    m->bodyEnd = bodyClose;
    svAddMember(x, info, m);
    return bodyClose >= 0 ? bodyClose + 1 : (afterParams < bodyEnd ? afterParams : bodyEnd);
}

static int svSkipToNextMemberAfterBody(SvIndex* x, int from, int bodyEnd) {
    int j = from;
    while (j < bodyEnd && SVTY(x, j) != TT_LBrace) j++;
    if (j >= bodyEnd) return bodyEnd;
    return svMatchBrace(x, j) + 1;
}

static int svHarvestMembersBody(SvIndex* x, int bodyStart, SvClass* info) {
    int bodyEnd = svMatchBrace(x, bodyStart), i = bodyStart + 1;
    SvRange* rg = (SvRange*)fcAlloc(x->c, sizeof *rg);
    SvNsRange* span = (SvNsRange*)fcAlloc(x->c, sizeof *span);
    const char* className = info->simpleName;
    bool isStatic = false;
    SvAccess access = SA_PUBLIC;
    rg->start = bodyStart; rg->end = bodyEnd;
    fcListAdd(x->c, info->bodyRanges, rg);
    span->name = info->name; span->start = svTokenOffset(x, bodyStart); span->end = svTokenOffset(x, bodyEnd) + 1;
    fcListAdd(x->c, x->classSpans, span);
    while (i < bodyEnd) {
        FcTokenType type = SVTY(x, i);
        if (type == TT_Static) { isStatic = true; i++; continue; }
        if (type == TT_Override) { i++; continue; }
        if (type == TT_Public) { access = SA_PUBLIC; i++; continue; }
        if (type == TT_Private) { access = SA_PRIVATE; i++; continue; }
        if (type == TT_Protected) { access = SA_PROTECTED; i++; continue; }
        if (type == TT_LBracket) { i = svSkipBrackets(x, i, bodyEnd); continue; }
        if (type == TT_Identifier && svLex(x, i, "inline") && i + 1 < bodyEnd && SVTY(x, i + 1) == TT_Identifier && svLex(x, i + 1, "c") && !SVT(x, i + 1)->newlineBefore) {
            isStatic = true;
            i += 2;
            continue;
        }
        if (type == TT_Construct) {
            int paren = i + 1;
            if (paren < bodyEnd && SVTY(x, paren) == TT_LParen) {
                int count = svCountParams(x, paren), after;
                FcList* parms = svReadParamList(x, paren, &after);
                SvMember* m = svMemberNew(x, className, SM_CONSTRUCTOR, count, SVT(x, i)->line);
                m->access = access;
                svSetParams(x, m, parms, true);
                svAddMember(x, info, m);
            }
            i = svSkipToNextMemberAfterBody(x, i, bodyEnd);
        } else if (type == TT_Destruct) {
            i = svSkipToNextMemberAfterBody(x, i, bodyEnd);
        } else if (type == TT_Operator) {
            i = svHarvestOperatorMember(x, i, bodyEnd, info);
        } else if (type == TT_Identifier || svIsTypeKeyword(type) || type == TT_Readonly) {
            i = svHarvestMember(x, i, bodyEnd, info, isStatic, access);
        } else {
            i++;
            continue;
        }
        isStatic = false;
        access = SA_PUBLIC;
    }
    return bodyEnd + 1;
}

static int svHarvestEnum(SvIndex* x, int i) {
    const char* enumName;
    int bodyEnd;
    FcList* members;
    i++;
    if (i >= x->ntok || SVTY(x, i) != TT_Identifier) return i;
    enumName = svQualify(x, SVT(x, i)->lexeme);
    fcDictPut(&x->enumDeclLines, enumName, FC_IVAL(SVT(x, i)->line));
    i++;
    if (i >= x->ntok || SVTY(x, i) != TT_LBrace) return i;
    bodyEnd = svMatchBrace(x, i);
    members = fcListNew(x->c);
    i++;
    while (i < bodyEnd) {
        if (SVTY(x, i) == TT_Identifier) {
            fcListAdd(x->c, members, (void*)SVT(x, i)->lexeme);
            i++;
            while (i < bodyEnd && SVTY(x, i) != TT_Comma) i++;
        } else i++;
    }
    fcDictPut(&x->enumMembers, enumName, members);
    return bodyEnd + 1;
}

static const char* svResolveClassKey(SvIndex* x, const char* name, FcList* context, bool lenient);

static void svHarvest(SvIndex* x) {
    int i = 0, p;
    while (i < x->ntok && SVTY(x, i) != TT_Eof) {
        while (x->nsStack->count > 0 && ((SvNsRange*)x->nsStack->items[x->nsStack->count - 1])->end < i) x->nsStack->count--;
        switch (SVTY(x, i)) {
            case TT_Namespace: i = svHarvestNamespace(x, i); break;
            case TT_Class: case TT_Actor: i = svHarvestClass(x, i, false); break;
            case TT_Interface: i = svHarvestClass(x, i, true); break;
            case TT_Enum: i = svHarvestEnum(x, i); break;
            default: i++; break;
        }
    }
    for (p = 0; p < x->pending->count; p++) {
        SvPending* e = (SvPending*)x->pending->items[p];
        const char* key = svResolveClassKey(x, e->target, e->context, true);
        SvClass* info;
        if (!key) key = e->target;
        info = svClassGet(x, key);
        if (!info) { info = svClassNew(x, key, 0); fcDictPut(&x->classes, key, info); }
        svHarvestMembersBody(x, e->bodyStart, info);
    }
    x->pending->count = 0;
}

static int svCountParams(SvIndex* x, int paren) {
    int depth = 0, commas = 0, j;
    bool any = false;
    for (j = paren; j < x->ntok; j++) {
        if (SVTY(x, j) == TT_LParen) depth++;
        else if (SVTY(x, j) == TT_RParen) { if (--depth == 0) break; }
        else if (depth == 1) { any = true; if (SVTY(x, j) == TT_Comma) commas++; }
    }
    return any ? commas + 1 : 0;
}

/* ReadParamList's FlushParam */
static void svFlushParam(SvIndex* x, FcToken** toks, int n, FcList* out) {
    int assign = -1, nameIdx = -1, k, lambdaAt = -1, t2;
    const char* type = NULL;
    SvParam* p;
    for (k = 0; k < n; k++) if (toks[k]->type == TT_Assign) { assign = k; break; }
    if (assign >= 0) n = assign;
    for (k = n - 1; k >= 0; k--) if (toks[k]->type == TT_Identifier) { nameIdx = k; break; }
    if (nameIdx < 0) return;
    for (k = 0; k < n; k++) if (toks[k]->type == TT_Identifier && strcmp(toks[k]->lexeme, "lambda") == 0) { lambdaAt = k; break; }
    p = (SvParam*)fcAlloc(x->c, sizeof *p);
    p->name = toks[nameIdx]->lexeme;
    if (lambdaAt >= 0 && lambdaAt < nameIdx) {
        FcBuf b = {0};
        fcBufStr(&b, "");
        for (k = 0; k < nameIdx; k++) {
            const FcToken* tk = toks[k];
            if (tk->type == TT_Lt || tk->type == TT_Gt) fcBufStr(&b, tk->lexeme);
            else if (tk->type == TT_Comma) fcBufStr(&b, ", ");
            else { if (b.len > 0 && b.p[b.len - 1] != '<' && b.p[b.len - 1] != ' ') fcBufChar(&b, ' '); fcBufStr(&b, tk->lexeme); }
        }
        p->type = fcBufDone(x->c, &b);
        fcListAdd(x->c, out, p);
        return;
    }
    t2 = nameIdx - 1;
    while (t2 >= 0 && toks[t2]->type == TT_Star) t2--;
    if (t2 >= 0 && toks[t2]->type == TT_RBracket) {
        while (t2 >= 0 && toks[t2]->type != TT_LBracket) t2--;
        t2--;
    }
    if (t2 >= 0 && (toks[t2]->type == TT_Identifier || svIsTypeKeyword(toks[t2]->type))) {
        type = toks[t2]->lexeme;
        while (t2 >= 2 && toks[t2 - 1]->type == TT_Dot && toks[t2 - 2]->type == TT_Identifier) { type = fcFmt(x->c, "%s.%s", toks[t2 - 2]->lexeme, type); t2 -= 2; }
    }
    p->type = type;
    fcListAdd(x->c, out, p);
}

static FcList* svReadParamList(SvIndex* x, int paren, int* afterParen) {
    FcList* result = fcListNew(x->c);
    int depth = 0, angles = 0, j = paren, n = 0, cap = 16;
    bool inDefault = false;
    FcToken** cur = (FcToken**)malloc(sizeof(FcToken*) * (size_t)cap);
    for (; j < x->ntok; j++) {
        FcTokenType t = SVTY(x, j);
        if (t == TT_LParen) { depth++; if (depth == 1) continue; }
        else if (t == TT_RParen) {
            depth--;
            if (depth == 0) { svFlushParam(x, cur, n, result); j++; break; }
        }
        if (depth == 1 && !inDefault) {
            if (t == TT_Lt) angles++;
            else if (t == TT_Gt && angles > 0) angles--;
            else if (t == TT_Assign) inDefault = true;
        }
        if (depth >= 1) {
            if (t == TT_Comma && depth == 1 && angles == 0) { svFlushParam(x, cur, n, result); n = 0; inDefault = false; }
            else if (!(depth == 1 && t == TT_LParen)) {
                if (n == cap) { cap *= 2; cur = (FcToken**)realloc(cur, sizeof(FcToken*) * (size_t)cap); if (!cur) fbOutOfMemory(); }
                cur[n++] = SVT(x, j);
            }
        }
    }
    free(cur);
    *afterParen = j;
    return result;
}

/* ---- prelude, imports and the other files of the project ---- */
static SvIndex* svBuildPrelude(SvLang* S, const uint16_t* src, int len, const char* preludeName) {
    SvIndex* x = svIndexNew(S, src, len);
    x->preludeName = fcStr(x->c, preludeName);
    svHarvest(x);
    FC_DICT_EACH(&x->classes, e) ((SvClass*)e->value)->preludeName = x->preludeName;
    return x;
}

static SvIndex* svPreludeIndex(SvLang* S) {
    if (!S->prelude) {
        FcU16 text = {0};
        fcReadText(S->preludePath, &text);   /* (no prelude: an empty one) */
        S->prelude = svBuildPrelude(S, text.p, text.len, "standard");
        fcU16Free(&text);
    }
    return S->prelude;
}

/* ImportedPreludes.TrySourceFor: the prelude of an import key (`graphics`, `pkg:name`) - NULL: none */
static bool svPreludeSourceOfImport(SvLang* S, const char* key, FcU16* out) {
    FcPkgImport* pi = fcStartsWith(key, "pkg:") ? fcStoreFindKey(&S->pctx, &S->L->store, key) : fcStoreFindImport(&S->pctx, &S->L->store, key);
    if (!pi || !pi->prelude || !pi->prelude[0]) return false;
    return fcReadText(fcFmt(S->tmp, "%s/%s", pi->dir, pi->prelude), out);
}

static SvIndex* svImportedPreludeIndex(SvLang* S, const char* key) {
    void* known;
    FcU16 text = {0};
    SvIndex* x = NULL;
    if (fcDictTry(&S->importedPreludes, key, &known)) return (SvIndex*)known;
    if (svPreludeSourceOfImport(S, key, &text)) x = svBuildPrelude(S, text.p, text.len, svLowerAscii(S->tmp, key));
    fcU16Free(&text);
    fcDictPut(&S->importedPreludes, fcStr(&S->pctx, key), x);
    return x;
}

/* ImportKeys: the preludes an `#import` switches on (with their dependencies), NULL for no extension; remembered */
static FcList* svImportKeys(SvLang* S, const char* name) {
    const char* lower = svLowerAscii(S->tmp, name);
    void* known;
    FcList* volatile keys = NULL;
    FcCtx* c = &S->pctx;
    if (fcDictTry(&S->importKeys, lower, &known)) return (FcList*)known;
    FC_TRY(c) {
        FcList* k = fcListNew(c);
        fcWithDependencies(S->L, fcParseImportName(S->L, name), fcDictNew(c, FC_KEY_STR), k);
        keys = k;
    } FC_CATCH(c) {
        keys = NULL;
    } FC_END_TRY;
    fcDictPut(&S->importKeys, fcStr(c, lower), keys);
    return keys;
}

static void svForgetImports(SvLang* S) {
    FC_DICT_EACH(&S->importedPreludes, e) if (e->value) svIndexFree(S, (SvIndex*)e->value);
    fcDictClear(&S->importedPreludes);
    fcDictClear(&S->importKeys);
    S->L->store.loaded = false;
}

static void svCloneClassIfShared(SvIndex* x, SvClass** slot);

static void svMergeInPrelude(SvIndex* x, SvIndex* prelude, bool ownOnly) {
    FC_DICT_EACH(&prelude->classes, e) {
        SvClass* pc = (SvClass*)e->value;
        void** slot;
        if (ownOnly && svClassFromPrelude(pc)) continue;
        slot = fcDictRef(&x->classes, e->key);
        if (slot) {
            SvClass* existing = (SvClass*)*slot;
            int i;
            if (existing->declLine != 0) continue;
            svCloneClassIfShared(x, (SvClass**)slot);
            existing = (SvClass*)*slot;
            existing->declLine = pc->declLine;
            existing->preludeName = pc->preludeName;
            if (existing->baseNames->count == 0 && pc->baseNames->count > 0) fcListAdd(x->c, existing->baseNames, pc->baseNames->items[0]);
            for (i = 0; i < pc->members->count; i++) fcListAdd(x->c, existing->members, pc->members->items[i]);
        } else fcDictPut(&x->classes, e->key, pc);
    }
    FC_DICT_EACH(&prelude->namespaces, e) fcSetAdd(&x->namespaces, e->key);
    FC_DICT_EACH(&prelude->enumMembers, e) {
        void* line;
        if (fcDictHas(&x->enumMembers, e->key) || (ownOnly && fcDictHas(&prelude->enumPreludes, e->key))) continue;
        fcDictPut(&x->enumMembers, e->key, e->value);
        if (ownOnly && prelude->filePath) fcDictPut(&x->enumFiles, e->key, (void*)prelude->filePath);
        if (fcDictTry(&prelude->enumDeclLines, e->key, &line)) fcDictPut(&x->enumDeclLines, e->key, line);
        if (prelude->preludeName) fcDictPut(&x->enumPreludes, e->key, (void*)prelude->preludeName);
    }
}

/* a class of another index that this one is about to change: a copy of its own (the C# changed the shared one) */
static void svCloneClassIfShared(SvIndex* x, SvClass** slot) {
    SvClass* k = *slot;
    SvClass* copy;
    if (k->source == x) return;
    copy = (SvClass*)fcAlloc(x->c, sizeof *copy);
    *copy = *k;
    copy->baseNames = fcListCopy(x->c, k->baseNames);
    copy->members = fcListCopy(x->c, k->members);
    copy->bodyRanges = fcListCopy(x->c, k->bodyRanges);
    *slot = copy;
}

typedef struct SvFile { const char* path; const uint16_t* text; int len; } SvFile;

static SvIndex* svBuild(SvLang* S, const uint16_t* src, int len, FcList* extraImports, const SvFile* files, int nfiles);

/* IndexOfProjectFile: kept while its text does not change */
static SvProjFile* svIndexOfProjectFile(SvLang* S, const char* path, const uint16_t* text, int len) {
    const char* key = svLowerAscii(S->tmp, path);
    SvProjFile* pf = (SvProjFile*)fcDictGet(&S->projFiles, key);
    uint64_t h = svHash(text, len);
    if (pf && pf->hash == h && pf->len == len && (len == 0 || memcmp(pf->text, text, sizeof(uint16_t) * (size_t)len) == 0)) return pf;
    if (!pf) {
        pf = (SvProjFile*)fcAlloc(&S->pctx, sizeof *pf);
        pf->path = fcStr(&S->pctx, path);
        fcDictPut(&S->projFiles, fcStr(&S->pctx, key), pf);
    } else {
        free(pf->text);
        pf->index->retired = true;
        if (pf->index->refs == 0) svIndexFree(S, pf->index); else fcListAdd(&S->pctx, S->retired, pf->index);
    }
    pf->hash = h;
    pf->len = len;
    pf->text = (uint16_t*)malloc(sizeof(uint16_t) * (size_t)(len + 1));
    if (!pf->text) fbOutOfMemory();
    if (len) memcpy(pf->text, text, sizeof(uint16_t) * (size_t)len);
    pf->index = svBuild(S, text, len, NULL, NULL, 0);
    pf->index->filePath = fcStr(pf->index->c, path);
    pf->imports = svFindImportNames(pf->index->c, text, len);
    return pf;
}

static const char svGeneratedHeader[] = "// Generated from ";

static bool svStartsWithAscii(const uint16_t* s, int n, const char* prefix) {
    int i;
    for (i = 0; prefix[i]; i++) if (i >= n || s[i] != (uint16_t)(unsigned char)prefix[i]) return false;
    return true;
}

/* Build(source, extraImports, projectFiles) */
static SvIndex* svBuild(SvLang* S, const uint16_t* src, int len, FcList* extraImports, const SvFile* files, int nfiles) {
    SvIndex* x = svIndexNew(S, src, len);
    FcList* importNames;
    FcDict merged;
    FcDict seenNames;
    int f, i, k;
    svFindUsings(x);
    svHarvest(x);
    if (svStartsWithAscii(src, len, svGeneratedHeader)) FC_DICT_EACH(&x->classes, e) ((SvClass*)e->value)->isGenerated = true;
    svHarvestIncludes(x);
    importNames = svFindImportNames(x->c, src, len);
    if (extraImports) for (i = 0; i < extraImports->count; i++) fcListAdd(x->c, importNames, extraImports->items[i]);
    for (f = 0; f < nfiles; f++) {
        SvProjFile* pf = svIndexOfProjectFile(S, files[f].path, files[f].text, files[f].len);
        svMergeInPrelude(x, pf->index, true);
        pf->index->refs++;
        fcListAdd(x->c, x->uses, pf->index);
        for (i = 0; i < pf->imports->count; i++) fcListAdd(x->c, importNames, pf->imports->items[i]);
    }
    svMergeInPrelude(x, svPreludeIndex(S), false);
    fcDictInit(x->c, &merged, FC_KEY_STR);
    fcDictInit(x->c, &seenNames, FC_KEY_STR);
    for (i = 0; i < importNames->count; i++) {
        const char* name = (const char*)importNames->items[i];
        FcList* keys;
        if (!fcSetAdd(&seenNames, svLowerAscii(x->c, name))) continue;
        keys = svImportKeys(S, name);
        if (!keys) continue;
        for (k = 0; k < keys->count; k++) {
            SvIndex* imp;
            if (!fcSetAdd(&merged, keys->items[k])) continue;
            imp = svImportedPreludeIndex(S, (const char*)keys->items[k]);
            if (imp) svMergeInPrelude(x, imp, false);
        }
    }
    return x;
}

/* ---- members of a class with its bases ---- */
typedef struct SvMemberAt { SvMember* m; int depth; } SvMemberAt;

/* the key as the dictionary holds it (NULL: none) */
static const char* svDictKey(const FcDict* d, const char* key) {
    int at = fcDictFind(d, key, fcDictHashOf(d, key));
    return at < 0 ? NULL : (const char*)d->entries[d->index[at]].key;
}

static const char* svResolveClassKey(SvIndex* x, const char* name, FcList* context, bool lenient) {
    int i;
    const char* only = NULL;
    if (!name) return NULL;
    if (fcDictHas(&x->classes, name)) return name;
    if (context)
        for (i = 0; i < context->count; i++) {
            const char* q = fcFmt(x->lang->tmp, "%s.%s", (const char*)context->items[i], name);
            if (fcDictHas(&x->classes, q)) return ((SvClass*)fcDictGet(&x->classes, q))->name;
        }
    if (!lenient || svHasChar(name, '.')) return NULL;
    FC_DICT_EACH(&x->classes, e) {
        SvClass* k = (SvClass*)e->value;
        if (strcmp(k->simpleName, name) != 0) continue;
        if (only) return NULL;
        only = (const char*)e->key;
    }
    return only;
}
static const char* svResolveEnumKey(SvIndex* x, const char* name, FcList* context) {
    int i;
    if (!name) return NULL;
    if (fcDictHas(&x->enumMembers, name)) return name;
    if (context)
        for (i = 0; i < context->count; i++) {
            const char* q = fcFmt(x->lang->tmp, "%s.%s", (const char*)context->items[i], name);
            const char* k = svDictKey(&x->enumMembers, q);
            if (k) return k;
        }
    return NULL;
}

/* ResolvedBases */
static FcList* svResolvedBases(SvIndex* x, SvClass* info) {
    FcList* r = fcListNew(x->lang->tmp);
    int i;
    for (i = 0; i < info->baseNames->count; i++) {
        const char* key = svResolveClassKey(x, (const char*)info->baseNames->items[i], info->context, true);
        if (key) fcListAdd(x->lang->tmp, r, (void*)key);
    }
    return r;
}

/* MembersOfWithDepth: breadth first over all base names; a member a nearer class declares appears only there */
static FcList* svMembersOfWithDepth(SvIndex* x, const char* className) {
    FcCtx* t = x->lang->tmp;
    FcList* out = fcListNew(t);
    FcDict seen, visited;
    FcList* queue = fcListNew(t);
    int qi = 0, i;
    fcDictInit(t, &seen, FC_KEY_STR);
    fcDictInit(t, &visited, FC_KEY_STR);
    fcSetAdd(&visited, className);
    fcListAdd(t, queue, (void*)className);
    fcListAdd(t, queue, FC_IVAL(0));
    while (qi < queue->count) {
        const char* cur = (const char*)queue->items[qi];
        int depth = (int)(intptr_t)queue->items[qi + 1];
        SvClass* info = svClassGet(x, cur);
        FcList* bases;
        qi += 2;
        if (!info) continue;
        for (i = 0; i < info->members->count; i++) {
            SvMember* m = (SvMember*)info->members->items[i];
            if (fcSetAdd(&seen, fcFmt(t, "%d:%s/%d", (int)m->kind, m->name, m->paramCount))) {
                SvMemberAt* at = (SvMemberAt*)fcAlloc(t, sizeof *at);
                at->m = m; at->depth = depth;
                fcListAdd(t, out, at);
            }
        }
        bases = svResolvedBases(x, info);
        for (i = 0; i < bases->count; i++)
            if (fcSetAdd(&visited, bases->items[i])) { fcListAdd(t, queue, bases->items[i]); fcListAdd(t, queue, FC_IVAL(depth + 1)); }
    }
    return out;
}

/* the first member of MembersOf(className) that `pred` accepts */
typedef bool (*SvMemberPred)(const SvMember* m, const void* arg);
static SvMember* svFirstMember(SvIndex* x, const char* className, SvMemberPred pred, const void* arg) {
    FcList* all = svMembersOfWithDepth(x, className);
    int i;
    for (i = 0; i < all->count; i++) { SvMember* m = ((SvMemberAt*)all->items[i])->m; if (pred(m, arg)) return m; }
    return NULL;
}

static const char* svNamespaceAt(SvIndex* x, int offset) {
    const char* best = NULL;
    int bestStart = -1, i;
    for (i = 0; i < x->nsRanges->count; i++) {
        SvNsRange* r = (SvNsRange*)x->nsRanges->items[i];
        if (offset >= r->start && offset <= r->end && r->start > bestStart) { best = r->name; bestStart = r->start; }
    }
    return best;
}
static FcList* svContextAt(SvIndex* x, int offset) {
    const char* ns = svNamespaceAt(x, offset);
    FcList* r;
    int i;
    if (!ns) return x->usings;
    r = fcListNew(x->lang->tmp);
    fcListAdd(x->lang->tmp, r, (void*)ns);
    for (i = 0; i < x->usings->count; i++) fcListAdd(x->lang->tmp, r, x->usings->items[i]);
    return r;
}
static const char* svTryFindClass(SvIndex* x, const char* name, int offset) { return svResolveClassKey(x, name, offset < 0 ? x->usings : svContextAt(x, offset), true); }
static FC_LATER const char* svTryFindEnum(SvIndex* x, const char* name, int offset) {
    const char* r = svResolveEnumKey(x, name, offset < 0 ? x->usings : svContextAt(x, offset));
    if (r) return r;
    FC_DICT_EACH(&x->enumMembers, e) {
        const char* k = (const char*)e->key;
        size_t kl = strlen(k), nl = strlen(name);
        if (kl > nl && k[kl - nl - 1] == '.' && strcmp(k + kl - nl, name) == 0) return k;
    }
    return NULL;
}

typedef struct SvNsMember { const char* name; SvNsKind kind; const char* fullName; } SvNsMember;
static FcList* svMembersOfNamespace(SvIndex* x, const char* ns) {
    FcCtx* t = x->lang->tmp;
    FcList* r = fcListNew(t);
    size_t pl = strlen(ns) ? strlen(ns) + 1 : 0;
    const char* prefix = pl ? fcFmt(t, "%s.", ns) : "";
    FC_DICT_EACH(&x->namespaces, e) {
        const char* name = (const char*)e->key;
        if (fcStartsWith(name, prefix) && strlen(name) > pl && !svHasChar(name + pl, '.')) {
            SvNsMember* m = (SvNsMember*)fcAlloc(t, sizeof *m);
            m->name = name + pl; m->kind = SN_NAMESPACE; m->fullName = name;
            fcListAdd(t, r, m);
        }
    }
    FC_DICT_EACH(&x->classes, e) {
        SvClass* k = (SvClass*)e->value;
        if (strcmp(k->ns, ns) == 0) {
            SvNsMember* m = (SvNsMember*)fcAlloc(t, sizeof *m);
            m->name = k->simpleName; m->kind = k->isInterface ? SN_INTERFACE : SN_CLASS; m->fullName = k->name;
            fcListAdd(t, r, m);
        }
    }
    FC_DICT_EACH(&x->enumMembers, e) {
        const char* key = (const char*)e->key;
        const char* dot = strrchr(key, '.');
        const char* enumNs = dot ? fcStrN(t, key, (size_t)(dot - key)) : "";
        if (strcmp(enumNs, ns) == 0) {
            SvNsMember* m = (SvNsMember*)fcAlloc(t, sizeof *m);
            m->name = dot ? dot + 1 : key; m->kind = SN_ENUM; m->fullName = key;
            fcListAdd(t, r, m);
        }
    }
    return r;
}

static bool svDerivesFrom(SvIndex* x, const char* className, const char* ancestor) {
    FcCtx* t = x->lang->tmp;
    FcDict visited;
    FcList* queue = fcListNew(t);
    int qi = 0, i;
    fcDictInit(t, &visited, FC_KEY_STR);
    fcSetAdd(&visited, className);
    fcListAdd(t, queue, (void*)className);
    while (qi < queue->count) {
        SvClass* info = svClassGet(x, (const char*)queue->items[qi++]);
        FcList* bases;
        if (!info) continue;
        bases = svResolvedBases(x, info);
        for (i = 0; i < bases->count; i++) {
            if (strcmp((const char*)bases->items[i], ancestor) == 0) return true;
            if (fcSetAdd(&visited, bases->items[i])) fcListAdd(t, queue, bases->items[i]);
        }
    }
    return false;
}

static const char* svEnclosingClassAt(SvIndex* x, int offset) {
    const char* best = NULL;
    int bestStart = -1, i;
    for (i = 0; i < x->classSpans->count; i++) {
        SvNsRange* r = (SvNsRange*)x->classSpans->items[i];
        if (offset >= r->start && offset <= r->end && r->start > bestStart) { best = r->name; bestStart = r->start; }
    }
    return best;
}

/* the `///` lines above `declLine` of this index's source (DocComments.ExtractRaw), NULL: none */
static const char* svDocAt(SvIndex* x, FcCtx* out, int declLine) {
    FcList* lines = fcListNew(out);
    int line, i;
    FcBuf b = {0};
    if (!x || declLine <= 0) return NULL;
    for (line = declLine - 1; line >= 1 && line <= x->nLines; line--) {
        int start = x->lineStarts[line - 1];
        int end = line < x->nLines ? x->lineStarts[line] : x->srcLen;
        while (start < end && fcIsWhiteSpace(x->src[start])) start++;
        while (end > start && fcIsWhiteSpace(x->src[end - 1])) end--;
        if (end - start < 3 || x->src[start] != '/' || x->src[start + 1] != '/' || x->src[start + 2] != '/') break;
        start += 3;
        if (start < end && x->src[start] == ' ') start++;
        fcListAdd(out, lines, (void*)fc8FromU16(out, x->src + start, end - start));
    }
    if (lines->count == 0) return NULL;
    fcBufStr(&b, "");
    for (i = lines->count - 1; i >= 0; i--) { fcBufStr(&b, (const char*)lines->items[i]); if (i) fcBufChar(&b, '\n'); }
    return fcBufDone(out, &b);
}
