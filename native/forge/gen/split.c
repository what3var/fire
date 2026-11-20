/* native/forge/gen/split.c - the program in several translation units (commit 808e9788; docs/spec/NATIVE_SPLIT.md, phase 1): src/fire.Native/ProgramSplit.cs
 * (Scan, Group, Describe, the declarations, Split), CppGenerator.Split.cs (SplitKeyOf, SplitNativesGroup, SplitNatives; the marks and WithoutSplitMarks are in
 * misc.c), from CppGenerator.Stats.cs ModuleOfStat and from StaticLinkage.cs what SplitNativesGroup asks (NativesStandAlone, NativeCImports, NativeCText,
 * NativePrefix). Positions are bytes of the fc8 text; what C# counts in UTF-16 units (the cost of a string, of the natives) is counted so (fgUnitsIn). */

#define PS_MarkPrefix SplitMarkPrefix
#define PS_HeaderName "fire_program.h"
#define PS_HelperWeight 2
enum { PS_Pre, PS_Func, PS_Proto, PS_Data, PS_Type, PS_Trivia, PS_Group };
enum { SUK_Header, SUK_Runtime, SUK_Helpers, SUK_Functions, SUK_Natives };
static const char* const fgSplitUnitKindNames[] = { "Header", "Runtime", "Helpers", "Functions", "Natives" };

typedef struct PsItem {
    int start, end, kind;
    FcList* names;              /* fc8 */
    bool isStatic, isInline, isExtern;
    int body;                   /* a function: its `{` */
    FcList* children;           /* a group: #if ... #endif (PsItem*) */
    FcDict* uses;               /* HashSet<string> */
    int64_t cost;
    const char* src;
    int srcLen;
} PsItem;

typedef struct FgNativeFn { const char* group; const char* function; const char* ret; FcList* params; } FgNativeFn;

/* the UTF-16 units of the fc8 bytes [from, to) */
static int64_t fgUnitsIn(const char* s, int from, int to) {
    int64_t n = 0;
    int i;
    for (i = from; i < to; i++) {
        unsigned char b = (unsigned char)s[i];
        if ((b & 0xC0) != 0x80) n += (b & 0xF8) == 0xF0 ? 2 : 1;
    }
    return n;
}

static bool psIdStart(char c) { return c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }
static bool psIdChar(char c) { return psIdStart(c) || (c >= '0' && c <= '9'); }
static int psIndexOf(const char* s, int n, char ch, int from) {
    const char* p;
    if (from >= n) return -1;
    p = (const char*)memchr(s + from, ch, (size_t)(n - from));
    return p ? (int)(p - s) : -1;
}
static int psIndexOfStr(const char* s, int n, const char* what, int from, int to) {
    size_t wl = strlen(what);
    int i;
    for (i = from; i + (int)wl <= to && i + (int)wl <= n; i++) if (memcmp(s + i, what, wl) == 0) return i;
    return -1;
}

/* The end of the preprocessor line that starts at i (after its newline; continued lines included). */
static int psLineEnd(const char* s, int n, int i) {
    for (;;) {
        int e = psIndexOf(s, n, '\n', i);
        if (e < 0) return n;
        if ((e > 0 && s[e - 1] == '\\') || (e > 1 && s[e - 1] == '\r' && s[e - 2] == '\\')) { i = e + 1; continue; }
        return e + 1;
    }
}
static int psSkipString(const char* s, int n, int i) {
    char q = s[i++];
    while (i < n && s[i] != q) { if (s[i] == '\\') i++; else if (s[i] == '\n') break; i++; }
    return i + 1 < n ? i + 1 : n;
}
static int psSkipComment(const char* s, int n, int i) {
    int end;
    if (s[i + 1] == '/') { int e = psIndexOf(s, n, '\n', i); return e < 0 ? n : e; }
    end = psIndexOfStr(s, n, "*/", i + 2, n);
    return end < 0 ? n : end + 2;
}

static PsItem* psItemNew(FcCtx* c, int start, int end, int kind) {
    PsItem* it = (PsItem*)fcAlloc(c, sizeof *it);
    it->start = start; it->end = end; it->kind = kind; it->body = -1;
    it->names = fcListNew(c);
    it->uses = fcDictNew(c, FC_KEY_STR);
    return it;
}

/* The end of an item with the comment after it on the same line (`static Value SF2 = ...; // Word.Bits`): the comment stays with it. */
static int psTrailing(const char* s, int n, int i) {
    int e = i;
    while (e < n && (s[e] == ' ' || s[e] == '\t')) e++;
    if (e + 1 < n && s[e] == '/' && s[e + 1] == '/') { int nl = psIndexOf(s, n, '\n', e); return nl < 0 ? n : nl; }
    if (e + 1 < n && s[e] == '/' && s[e + 1] == '*') {
        int close = psIndexOfStr(s, n, "*/", e + 2, n), nl = psIndexOf(s, n, '\n', e);
        if (close >= 0 && (nl < 0 || close < nl)) return close + 2;
    }
    return i;
}

/* The directive of a preprocessor item (`if`, `define`, `line`, ...). */
static const char* psDirective(FcCtx* c, const PsItem* item) {
    const char* s = item->src;
    int i = item->start, e;
    while (i < item->end && s[i] != '#') i++;
    i++;
    while (i < item->end && (s[i] == ' ' || s[i] == '\t')) i++;
    e = i;
    while (e < item->end && psIdChar(s[e])) e++;
    return fcStrN(c, s + i, (size_t)(e - i));
}

static bool psLineStartAt(const char* s, int i) {
    int b = i - 1;
    while (b >= 0 && (s[b] == ' ' || s[b] == '\t')) b--;
    return b < 0 || s[b] == '\n';
}

static void psCollectUses(FcCtx* c, PsItem* item) {
    const char* s = item->src;
    int64_t cost = 0;
    int i;
    for (i = item->start; i < item->end;) {
        char ch = s[i];
        if (ch == '\n') { i++; continue; }
        if (ch == '/' && i + 1 < item->end && (s[i + 1] == '/' || s[i + 1] == '*')) { i = psSkipComment(s, item->srcLen, i); continue; }
        if (ch == '"' || ch == '\'') { int e = psSkipString(s, item->srcLen, i); cost += fgUnitsIn(s, i, e); i = e; continue; }
        if (ch == '#' && psLineStartAt(s, i)) {
            int e = psLineEnd(s, item->srcLen, i), d = i + 1;
            /* (#define: its body may name symbols; #line, #include, #pragma, #if: none of the program) */
            while (d < e && (s[d] == ' ' || s[d] == '\t')) d++;
            if (strncmp(s + d, "define", 6) != 0) { i = e; continue; }
            i = d + 6; continue;
        }
        if (psIdStart(ch)) {
            int e = i + 1, b = i - 1;
            bool member;
            while (e < item->end && psIdChar(s[e])) e++;
            /* (a member after `.`/`->`: not a symbol) */
            while (b >= item->start && (s[b] == ' ' || s[b] == '\t')) b--;
            member = b >= item->start && (s[b] == '.' || (s[b] == '>' && b > item->start && s[b - 1] == '-'));
            if (!member) {
                const char* id = fcStrN(c, s + i, (size_t)(e - i));
                if (!fcListHasStr(item->names, id)) fcSetAdd(item->uses, id);
            }
            cost += e - i;
            i = e; continue;
        }
        if (ch >= '0' && ch <= '9') { int e = i + 1; while (e < item->end && (psIdChar(s[e]) || s[e] == '.')) e++; cost += e - i; i = e; continue; }
        if (ch != ' ' && ch != '\t' && ch != '\r') cost += fgUnitsIn(s, i, i + 1);
        i++;
    }
    item->cost = cost;
}

/* The names an item declares or defines, whether it is `static`/`inline`, its uses and its cost. */
static void psDescribe(FcCtx* c, PsItem* item) {
    const char* s = item->src;
    if (item->kind == PS_Func || item->kind == PS_Proto || item->kind == PS_Data || item->kind == PS_Type) {
        FcList* heads = fcListNew(c);   /* fc8 ids */
        int depth = 0, par = 0, limit = item->kind == PS_Func ? item->body : item->end, i, k;
        bool skipInit = false;
        FcList* declarators = fcListNew(c);
        const char* lastId = NULL;
        for (i = item->start; i < limit;) {
            char ch = s[i];
            if (ch == '/' && i + 1 < limit && (s[i + 1] == '/' || s[i + 1] == '*')) { i = psSkipComment(s, item->srcLen, i); continue; }
            if (ch == '"' || ch == '\'') { i = psSkipString(s, item->srcLen, i); continue; }
            if (ch == '#' && (i == item->start || s[i - 1] == '\n')) { i = psLineEnd(s, item->srcLen, i); continue; }
            if (psIdStart(ch)) {
                int e = i + 1;
                const char* id;
                while (e < limit && psIdChar(s[e])) e++;
                id = fcStrN(c, s + i, (size_t)(e - i));
                if (depth == 0 && par == 0 && !skipInit) { fcListAdd(c, heads, (void*)id); if (!streq0(id, "const") && !streq0(id, "volatile")) lastId = id; }
                i = e; continue;
            }
            switch (ch) {
                case '(':
                    /* (a function or prototype: the name is the identifier before the first parenthesis) */
                    if (depth == 0 && par == 0 && (item->kind == PS_Func || item->kind == PS_Proto) && declarators->count == 0 && lastId) fcListAdd(c, declarators, (void*)lastId);
                    par++; break;
                case ')': par--; break;
                case '{': depth++; break;
                case '}': depth--; break;
                case '[': depth++; break;
                case ']': depth--; break;
                case '=': if (depth == 0 && par == 0) { if (item->kind == PS_Data && lastId && !skipInit) fcListAdd(c, declarators, (void*)lastId); skipInit = true; } break;
                case ',': if (depth == 0 && par == 0 && item->kind == PS_Data) { if (!skipInit && lastId) fcListAdd(c, declarators, (void*)lastId); skipInit = false; lastId = NULL; } break;
                case ';': if (depth == 0 && par == 0 && item->kind == PS_Data && !skipInit && lastId) fcListAdd(c, declarators, (void*)lastId); break;
            }
            i++;
        }
        if (item->kind == PS_Type) {
            /* typedef ... Name; - the last identifier at depth 0; struct Tag { } ; - the tag */
            if (heads->count > 0 && streq0((const char*)heads->items[0], "typedef")) { if (lastId) fcListAdd(c, declarators, (void*)lastId); }
            else if (heads->count > 1) fcListAdd(c, declarators, heads->items[1]);
        }
        for (k = 0; k < declarators->count; k++) if (!fcListHasStr(item->names, (const char*)declarators->items[k])) fcListAdd(c, item->names, declarators->items[k]);
        for (k = 0; k < heads->count; k++) {
            const char* id = (const char*)heads->items[k];
            if (streq0(id, "static")) item->isStatic = true;
            if (streq0(id, "inline") || streq0(id, "__inline")) item->isInline = true;
            if (streq0(id, "extern")) item->isExtern = true;
        }
    }
    psCollectUses(c, item);
}

/* Folds `#if ... #endif` at the top level into group items (with their lines as children). */
static FcList* psGroup(FcCtx* c, FcList* items) {
    FcList* result = fcListNew(c);
    FcList* stack = fcListNew(c);
    int i;
    for (i = 0; i < items->count; i++) {
        PsItem* item = (PsItem*)items->items[i];
        const char* directive = item->kind == PS_Pre ? psDirective(c, item) : NULL;
        if (directive && (streq0(directive, "if") || streq0(directive, "ifdef") || streq0(directive, "ifndef"))) {
            PsItem* g = psItemNew(c, item->start, 0, PS_Group);
            g->children = fcListNew(c);
            fcListAdd(c, g->children, item);
            g->src = item->src; g->srcLen = item->srcLen;
            fcListAdd(c, stack, g);
            continue;
        }
        if (stack->count > 0) {
            PsItem* g = (PsItem*)stack->items[stack->count - 1];
            fcListAdd(c, g->children, item);
            if (directive && streq0(directive, "endif")) {
                stack->count--;
                g->end = item->end;
                if (stack->count > 0) fcListAdd(c, ((PsItem*)stack->items[stack->count - 1])->children, g); else fcListAdd(c, result, g);
            }
            continue;
        }
        fcListAdd(c, result, item);
    }
    while (stack->count > 0) {   /* (an unclosed #if: keep what it has) */
        PsItem* g = (PsItem*)stack->items[--stack->count];
        g->end = ((PsItem*)g->children->items[g->children->count - 1])->end;
        fcListAdd(c, result, g);
    }
    return result;
}

/* The top-level items of a C text: preprocessor lines, function definitions, declarations and definitions ending with `;`; leading whitespace and
 * comments belong to the item after them; what follows the last one is a trivia item. */
static FcList* psScan(FcCtx* c, const char* s) {
    FcList* items = fcListNew(c);
    int n = (int)strlen(s), i = 0, start = 0, depth = 0, par = 0, brk = 0, firstParen = -1, body = -1, k;
    bool lineStart = true, code = false, eq = false, isFunc = false;
    const char* first = NULL;
#define PS_RESET(at) do { start = (at); code = false; eq = false; isFunc = false; firstParen = -1; body = -1; first = NULL; brk = 0; } while (0)
    while (i < n) {
        char ch = s[i];
        if (lineStart) {
            int j = i;
            while (j < n && (s[j] == ' ' || s[j] == '\t')) j++;
            if (j < n && s[j] == '#') {
                int e = psLineEnd(s, n, j);
                if (depth == 0 && par == 0 && !code) { fcListAdd(c, items, psItemNew(c, start, e, PS_Pre)); PS_RESET(e); }
                i = e; lineStart = true; continue;
            }
            lineStart = false;
        }
        if (ch == '\n') { lineStart = true; i++; continue; }
        if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\f' || ch == '\v') { i++; continue; }
        if (ch == '/' && i + 1 < n && (s[i + 1] == '/' || s[i + 1] == '*')) { i = psSkipComment(s, n, i); continue; }
        code = true;
        if (ch == '"' || ch == '\'') { i = psSkipString(s, n, i); continue; }
        if (psIdStart(ch)) {
            int e = i + 1;
            while (e < n && psIdChar(s[e])) e++;
            if (!first && depth == 0 && par == 0) first = fcStrN(c, s + i, (size_t)(e - i));
            i = e; continue;
        }
        if (ch >= '0' && ch <= '9') { i++; while (i < n && (psIdChar(s[i]) || s[i] == '.')) i++; continue; }
        switch (ch) {
            case '(': if (depth == 0 && par == 0 && brk == 0 && firstParen < 0) firstParen = i; par++; break;
            case ')': par--; break;
            case '[': if (depth == 0) brk++; break;
            case ']': if (depth == 0) brk--; break;
            case '=': if (depth == 0 && par == 0 && brk == 0) eq = true; break;
            case '{':
                if (depth == 0 && par == 0) {
                    isFunc = !eq && firstParen >= 0 && !(first && (streq0(first, "typedef") || streq0(first, "struct") || streq0(first, "union") || streq0(first, "enum")));
                    body = i;
                }
                depth++;
                break;
            case '}':
                depth--;
                if (depth == 0 && par == 0 && isFunc) {
                    int end = psTrailing(s, n, i + 1);
                    PsItem* it = psItemNew(c, start, end, PS_Func);
                    it->body = body;
                    fcListAdd(c, items, it);
                    PS_RESET(end);
                    i = end;
                    continue;
                }
                break;
            case ';':
                if (depth == 0 && par == 0) {
                    int kind = first && (streq0(first, "typedef") || streq0(first, "struct") || streq0(first, "union") || streq0(first, "enum")) ? PS_Type : firstParen >= 0 && !eq ? PS_Proto : PS_Data;
                    int end = psTrailing(s, n, i + 1);
                    fcListAdd(c, items, psItemNew(c, start, end, kind));
                    PS_RESET(end);
                    i = end;
                    continue;
                }
                break;
        }
        i++;
    }
#undef PS_RESET
    if (start < n) fcListAdd(c, items, psItemNew(c, start, n, code ? PS_Data : PS_Trivia));
    for (k = 0; k < items->count; k++) { PsItem* it = (PsItem*)items->items[k]; it->src = s; it->srcLen = n; psDescribe(c, it); }
    return psGroup(c, items);
}

/* ---- declarations ---- */
/* The text of the item from its first code (without the comments and blank lines before it). */
static int psCodeStart(const PsItem* item) {
    const char* s = item->src;
    int i = item->start;
    while (i < item->end) {
        char ch = s[i];
        if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') { i++; continue; }
        if (ch == '/' && i + 1 < item->end && (s[i + 1] == '/' || s[i + 1] == '*')) { i = psSkipComment(s, item->srcLen, i); continue; }
        break;
    }
    return i;
}

/* `static` becomes FIRE_PS (and an exported function is not inline: other units call it). */
static const char* psExported(FcCtx* c, const char* head, int len, bool function) {
    FcBuf sb = {0};
    bool replaced = false;
    int i;
    fcBufStr(&sb, "");
    for (i = 0; i < len;) {
        char ch = head[i];
        if (ch == '/' && i + 1 < len && (head[i + 1] == '/' || head[i + 1] == '*')) { int e = psSkipComment(head, len, i); fcBufPut(&sb, head + i, (size_t)(e - i)); i = e; continue; }
        if (ch == '"' || ch == '\'') { int e = psSkipString(head, len, i); fcBufPut(&sb, head + i, (size_t)(e - i)); i = e; continue; }
        if (psIdStart(ch)) {
            int e = i + 1;
            while (e < len && psIdChar(head[e])) e++;
            if (e - i == 6 && strncmp(head + i, "static", 6) == 0 && !replaced) { fcBufStr(&sb, "FIRE_PS"); replaced = true; }
            else if (function && ((e - i == 6 && strncmp(head + i, "inline", 6) == 0) || (e - i == 18 && strncmp(head + i, "FIRE_ALWAYS_INLINE", 18) == 0))) {
                /* (the word and one space after it) */
                if (e < len && head[e] == ' ') e++;
            } else fcBufPut(&sb, head + i, (size_t)(e - i));
            i = e; continue;
        }
        fcBufChar(&sb, ch);
        i++;
    }
    return fcBufDone(c, &sb);
}

static int psInitializerStart(const PsItem* item) {
    const char* s = item->src;
    int depth = 0, i;
    for (i = item->start; i < item->end;) {
        char ch = s[i];
        if (ch == '/' && i + 1 < item->end && (s[i + 1] == '/' || s[i + 1] == '*')) { i = psSkipComment(s, item->srcLen, i); continue; }
        if (ch == '"' || ch == '\'') { i = psSkipString(s, item->srcLen, i); continue; }
        if (ch == '(' || ch == '[' || ch == '{') depth++;
        else if (ch == ')' || ch == ']' || ch == '}') depth--;
        else if (ch == '=' && depth == 0) return i;
        i++;
    }
    return item->end;
}

/* The definition of an exported item as its unit writes it. */
static const char* psExportedDefinition(FcCtx* c, const PsItem* item) {
    const char* s = item->src;
    if (item->kind == PS_Func) return fcFmt(c, "%s%.*s", psExported(c, s + item->start, item->body - item->start, true), item->end - item->body, s + item->body);
    if (item->kind == PS_Data) {
        int eq = psInitializerStart(item);
        return fcFmt(c, "%s%.*s", psExported(c, s + item->start, eq - item->start, false), item->end - eq, s + eq);
    }
    if (item->kind == PS_Proto) return psExported(c, s + item->start, item->end - item->start, true);
    return fcStrN(c, s + item->start, (size_t)(item->end - item->start));
}

/* The declaration of an item in the header: a function its prototype, data an `extern` declaration (without the initializers); `exported`: it is
 * FIRE_PS, otherwise it keeps its storage (a static prototype). */
static const char* psDeclaration(FcCtx* c, const PsItem* item, bool exported) {
    const char* s = item->src;
    int from = psCodeStart(item);
    switch (item->kind) {
        case PS_Func: {
            int e = item->body;
            while (e > from && isspace((unsigned char)s[e - 1])) e--;
            return fcFmt(c, "%s;", exported ? psExported(c, s + from, e - from, true) : fcStrN(c, s + from, (size_t)(e - from)));
        }
        case PS_Proto:
            return exported ? psExported(c, s + from, item->end - from, true) : fcStrN(c, s + from, (size_t)(item->end - from));
        case PS_Data: {
            /* the declarators without their initializers */
            FcBuf sb = {0};
            int depth = 0, i;
            bool skip = false;
            const char* decl;
            fcBufStr(&sb, "");
            for (i = from; i < item->end;) {
                char ch = s[i];
                if (ch == '/' && i + 1 < item->end && (s[i + 1] == '/' || s[i + 1] == '*')) { i = psSkipComment(s, item->srcLen, i); continue; }
                if (ch == '"' || ch == '\'') { int e = psSkipString(s, item->srcLen, i); if (!skip) fcBufPut(&sb, s + i, (size_t)(e - i)); i = e; continue; }
                if (ch == '(' || ch == '[' || ch == '{') depth++;
                else if (ch == ')' || ch == ']' || ch == '}') depth--;
                if (depth == 0 && ch == '=') { skip = true; while (sb.len > 0 && sb.p[sb.len - 1] == ' ') sb.len--; i++; continue; }
                if (depth == 0 && (ch == ',' || ch == ';')) skip = false;
                if (!skip) fcBufChar(&sb, ch == '\n' ? ' ' : ch);
                i++;
            }
            decl = fcBufDone(c, &sb);
            if (item->isStatic) return fgReplace(c, psExported(c, decl, (int)strlen(decl), false), "FIRE_PS", "FIRE_PS extern");
            return fcFmt(c, "extern %s", decl);
        }
        default:
            return fcStrN(c, s + item->start, (size_t)(item->end - item->start));
    }
}

/* ---- the split ---- */
typedef struct PsPlace {
    const char* name;
    int kind;
    FcList* items;              /* PsItem* */
    FcDict* uses;
    int64_t cost;
    const char* nativesText;
    const char* group;
} PsPlace;
static PsPlace* psPlaceNew(FcCtx* c, const char* name, int kind) {
    PsPlace* p = (PsPlace*)fcAlloc(c, sizeof *p);
    p->name = name; p->kind = kind; p->items = fcListNew(c); p->uses = fcDictNew(c, FC_KEY_STR);
    return p;
}
typedef struct PsHeaderItem { PsItem* item; int mode; } PsHeaderItem;   /* 0: in the header as it is, 1: a static inline function defined in the header, 2: in a unit */
typedef struct PsRegion { FgFuncKey* key; FcList* items; int order; } PsRegion;
typedef struct PsBlock { const char* group; const char* text; } PsBlock;

static void psUnion(FcDict* into, const FcDict* from) { FC_DICT_EACH(from, e) fcSetAdd(into, e->key); }
static bool psListHasPtr(const FcList* l, const void* p) { int i; for (i = 0; i < l->count; i++) if (l->items[i] == p) return true; return false; }
static void psListRemovePtr(FcList* l, const void* p) { int i; for (i = 0; i < l->count; i++) if (l->items[i] == p) { fcListRemoveAt(l, i); return; } }
static int64_t psCostOf(const FcList* items) { int64_t s = 0; int i; for (i = 0; i < items->count; i++) s += ((PsItem*)items->items[i])->cost; return s; }
static int psCmpRegion(const void* a, const void* b) {
    const PsRegion* x = *(const PsRegion* const*)a;
    const PsRegion* y = *(const PsRegion* const*)b;
    int r = strcmp(x->key->module, y->key->module);
    if (r) return r;
    if ((r = strcmp(x->key->key, y->key->key)) != 0) return r;
    return x->order < y->order ? -1 : x->order > y->order;
}

typedef struct PsState {
    FcCtx* c;
    FcBuf* h;
    FcDict* declared;
    FcDict* exported;
    FcDict* inlineDefs;
    FcDict* definedIn;
} PsState;
static bool psIsExported(PsState* st, const PsItem* item) {
    int i;
    for (i = 0; i < item->names->count; i++) if (fcDictHas(st->exported, item->names->items[i])) return true;
    return false;
}
static bool psAllIn(const FcList* names, const FcDict* set) {
    int i;
    for (i = 0; i < names->count; i++) if (!fcDictHas(set, names->items[i])) return false;
    return true;
}
static void psNewLine(PsState* st) { if (st->h->len > 0 && st->h->p[st->h->len - 1] != '\n') fcBufChar(st->h, '\n'); }
static void psAddNames(PsState* st, const PsItem* item) { int i; for (i = 0; i < item->names->count; i++) fcSetAdd(st->declared, item->names->items[i]); }
static void psHeaderItem(PsState* st, PsItem* item, bool definition) {
    int i;
    if (item->kind == PS_Group) {
        for (i = 0; i < item->children->count; i++) psHeaderItem(st, (PsItem*)item->children->items[i], definition);
        return;
    }
    /* (a static inline helper of the program - the field helpers: thousands of them - is defined in each unit that uses it, the header only declares it) */
    if (definition) return;
    if (item->kind == PS_Proto && item->names->count > 0 && psAllIn(item->names, st->inlineDefs)) return;
    if (item->kind == PS_Pre || item->kind == PS_Type || item->kind == PS_Trivia) { fcBufPut(st->h, item->src + item->start, (size_t)(item->end - item->start)); psAddNames(st, item); return; }
    if (item->kind == PS_Proto) { bool ex = psIsExported(st, item); psNewLine(st); fcBufStr(st->h, psDeclaration(st->c, item, ex)); fcBufChar(st->h, '\n'); psAddNames(st, item); return; }
    fcBufPut(st->h, item->src + item->start, (size_t)(item->end - item->start));
}
static void psDeclare(PsState* st, PsItem* item) {
    int i;
    if (item->kind == PS_Group) {
        bool any = false;
        for (i = 0; i < item->children->count && !any; i++) {
            PsItem* ch = (PsItem*)item->children->items[i];
            if (((ch->kind == PS_Func || ch->kind == PS_Data) && psIsExported(st, ch)) || ch->kind == PS_Group) any = true;
        }
        if (!any) return;
        for (i = 0; i < item->children->count; i++) {
            PsItem* ch = (PsItem*)item->children->items[i];
            if (ch->kind == PS_Pre) { psNewLine(st); fcBufPut(st->h, ch->src + ch->start, (size_t)(ch->end - ch->start)); }
            else psDeclare(st, ch);
        }
        return;
    }
    if (!(item->kind == PS_Func || item->kind == PS_Data) || !psIsExported(st, item) || psAllIn(item->names, st->declared)) return;
    psNewLine(st);
    fcBufStr(st->h, psDeclaration(st->c, item, true));
    fcBufChar(st->h, '\n');
    psAddNames(st, item);
}
static void psDefines(PsState* st, PsPlace* p, PsItem* item) {
    int i;
    if (item->kind == PS_Group) { for (i = 0; i < item->children->count; i++) psDefines(st, p, (PsItem*)item->children->items[i]); return; }
    if (item->kind == PS_Func || (item->kind == PS_Data && !(item->isExtern && psInitializerStart(item) == item->end)))
        for (i = 0; i < item->names->count; i++) if (!fcDictHas(st->definedIn, item->names->items[i])) fcDictPut(st->definedIn, item->names->items[i], p);
}
static void psUsesOf(PsPlace* p, PsItem* item) {
    int i;
    if (item->kind == PS_Group) { for (i = 0; i < item->children->count; i++) psUsesOf(p, (PsItem*)item->children->items[i]); return; }
    psUnion(p->uses, item->uses);
}

typedef struct PsUnitCtx {
    PsState* st;
    FcBuf* sb;
} PsUnitCtx;
static void psWrite(PsUnitCtx* u, PsItem* item) {
    FcBuf* sb = u->sb;
    int i;
    if (item->kind == PS_Group) { for (i = 0; i < item->children->count; i++) psWrite(u, (PsItem*)item->children->items[i]); return; }
    /* (items of different places meet here: each starts on a line of its own) */
    if (sb->len > 0 && sb->p[sb->len - 1] != '\n' && item->src[item->start] != '\n') fcBufChar(sb, '\n');
    if (item->isStatic && (item->kind == PS_Func || item->kind == PS_Data || item->kind == PS_Proto) && psIsExported(u->st, item)) fcBufStr(sb, psExportedDefinition(u->st->c, item));
    else fcBufPut(sb, item->src + item->start, (size_t)(item->end - item->start));
    /* (a function with lines of the fire sources: the lines after it are this file's again - its own marker may have stayed with the item after it) */
    if (item->kind == PS_Func && psIndexOfStr(item->src, item->srcLen, "#line ", item->start, item->end) >= 0) fcBufStr(sb, "\n" LineResetMarker "\n");
}

static FgSplitUnit* psUnit(FcCtx* c, const char* name, const char* text, int kind, int64_t cost) {
    FgSplitUnit* u = (FgSplitUnit*)fcAlloc(c, sizeof *u);
    u->name = name; u->text = text; u->kind = kind; u->cost = cost;
    return u;
}

/* ProgramSplit.Split: the units of the text (the marks in it). funcs: the key of each `func N` mark (FgFuncKey*); natives: the natives in C the code may
 * call (FgNativeFn*); the last step on each file is ResolveLineResets with its name. */
static FcList* fgProgramSplit(FgGen* g, const char* text, FcList* funcs, FcList* natives, int64_t unitCost) {
    FcCtx* c = g->c;
    const char* head = NULL; const char* p1 = NULL; const char* tail = NULL;
    FcList* nativeBlocks = fcListNew(c);   /* PsBlock* */
    FcList* regions = fcListNew(c);        /* (int, text) as PsBlock with group = the number */
    FcList* atomics = fcListNew(c);
    PsPlace *header, *unit0;
    FcList *helperUnits, *funcUnits, *nativesUnits, *headLines, *p1Items, *helperFuncs, *headerItems, *atomicItems, *regionItems, *units, *movedTypes, *calledNatives, *result;
    const char* headText;
    bool rtImpl = false, json = false;
    PsItem* atomicItem = NULL;
    FcDict *typeIn, *programUses;
    int i, k;
    int64_t inlineCost = 0;
    const char* inlineHome;
    PsState st;
    FcBuf h = {0};
    size_t ml = strlen(PS_MarkPrefix);
    /* ---- the parts between the marks */
    {
        const char* pos = text;
        const char* current = "head";
        const char* arg = NULL;
        FcBuf buffer = {0};
        FcBuf p1Parts = {0};
        fcBufStr(&p1Parts, "");
#define PS_FLUSH() do { \
            const char* t_ = buffer.len ? fcStrN(c, buffer.p, buffer.len) : ""; \
            buffer.len = 0; \
            if (streq0(current, "head")) head = t_; \
            else if (streq0(current, "natives")) { PsBlock* b_ = (PsBlock*)fcAlloc(c, sizeof *b_); b_->group = arg; b_->text = t_; fcListAdd(c, nativeBlocks, b_); } \
            else if (streq0(current, "program")) fcBufStr(&p1Parts, t_); \
            else if (streq0(current, "atomic")) { fcListAdd(c, atomics, (void*)t_); fcBufFmt(&p1Parts, "%s\n", ProgramSplitMarker(g, fcFmt(c, "atomic %d", atomics->count - 1))); } \
            else if (streq0(current, "func")) { PsBlock* b_ = (PsBlock*)fcAlloc(c, sizeof *b_); b_->group = arg; b_->text = t_; fcListAdd(c, regions, b_); } \
            else if (streq0(current, "tail")) tail = t_; \
        } while (0)
        while (*pos) {
            const char* e = strchr(pos, '\n');
            const char* next = e ? e + 1 : pos + strlen(pos);
            if (strncmp(pos, PS_MarkPrefix, ml) == 0) {
                const char* m = pos + ml;
                size_t mlen = (size_t)(next - m);
                const char* mark;
                const char* sp;
                while (mlen > 0 && (m[mlen - 1] == '\n' || m[mlen - 1] == '\r')) mlen--;
                if (mlen >= 2 && m[mlen - 2] == '@' && m[mlen - 1] == '@') mlen -= 2;
                mark = fcStrN(c, m, mlen);
                PS_FLUSH();
                sp = strchr(mark, ' ');
                arg = sp ? sp + 1 : NULL;
                {
                    const char* word = sp ? fcStrN(c, mark, (size_t)(sp - mark)) : mark;
                    current = streq0(word, "atomic-end") || streq0(word, "head-end") ? "program" : word;
                }
                pos = next;
                continue;
            }
            fcBufPut(&buffer, pos, (size_t)(next - pos));
            pos = next;
        }
        PS_FLUSH();
#undef PS_FLUSH
        fcBufFree(&buffer);
        p1 = fcBufDone(c, &p1Parts);
    }
    if (!head) head = "";
    if (!p1) p1 = "";
    if (!tail) tail = "";

    header = psPlaceNew(c, PS_HeaderName, SUK_Header);
    unit0 = psPlaceNew(c, "fire_program.c", SUK_Runtime);
    helperUnits = fcListNew(c);
    funcUnits = fcListNew(c);
    nativesUnits = fcListNew(c);

    /* ---- the head: everything before the program, the same in every unit (the state of the runtime only in unit 0) */
    headLines = fgSplit(c, head, '\n');
    for (i = headLines->count - 1; i >= 0; i--)
        if (fcStartsWith((const char*)headLines->items[i], "#define FIRE_RT_IMPLEMENTATION 1")) { fcListRemoveAt(headLines, i); rtImpl = true; }
    headText = fgJoin(c, headLines, "\n");

    /* ---- the program before the functions */
    p1Items = psScan(c, p1);
    helperFuncs = fcListNew(c);
    headerItems = fcListNew(c);
    atomicItems = fcListNew(c);
#define PS_HI(it, m) do { PsHeaderItem* hi_ = (PsHeaderItem*)fcAlloc(c, sizeof *hi_); hi_->item = (it); hi_->mode = (m); fcListAdd(c, headerItems, hi_); } while (0)
    for (i = 0; i < p1Items->count; i++) {
        PsItem* item = (PsItem*)p1Items->items[i];
        int at;
        if (item->kind == PS_Pre) {
            const char* line = fcStrN(c, p1 + item->start, (size_t)(item->end - item->start));
            const char* trimmed = line;
            if (strstr(line, "#define FIRE_JSON_IMPLEMENTATION 1")) { json = true; continue; }
            if (strstr(line, "#include \"fire_json.h\"")) { json = true; continue; }
            while (*trimmed && isspace((unsigned char)*trimmed)) trimmed++;
            if (fcStartsWith(trimmed, PS_MarkPrefix "atomic ")) {
                FcList* inner = psScan(c, (const char*)atomics->items[atoi(trimmed + strlen(PS_MarkPrefix "atomic "))]);
                for (k = 0; k < inner->count; k++) { fcListAdd(c, atomicItems, inner->items[k]); PS_HI((PsItem*)inner->items[k], 2); }
                continue;
            }
            PS_HI(item, 0);
            continue;
        }
        /* (an atomic mark is a comment line: it is the leading trivia of the next item or a trivia item) */
        at = psIndexOfStr(item->src, item->srcLen, PS_MarkPrefix "atomic ", item->start, item->end);
        if (at >= 0) {
            FcList* inner = psScan(c, (const char*)atomics->items[atoi(item->src + at + strlen(PS_MarkPrefix "atomic "))]);
            for (k = 0; k < inner->count; k++) { fcListAdd(c, atomicItems, inner->items[k]); PS_HI((PsItem*)inner->items[k], 2); }
        }
        switch (item->kind) {
            case PS_Trivia: break;
            case PS_Group: {
                bool all = true;
                for (k = 0; k < item->children->count; k++) {
                    int ck = ((PsItem*)item->children->items[k])->kind;
                    if (!(ck == PS_Pre || ck == PS_Type || ck == PS_Proto || ck == PS_Trivia)) { all = false; break; }
                }
                if (all) PS_HI(item, 0);
                else { fcListAdd(c, unit0->items, item); PS_HI(item, 2); }
                break;
            }
            case PS_Func:
                if (item->isStatic && item->isInline) PS_HI(item, 1);
                else { fcListAdd(c, helperFuncs, item); PS_HI(item, 2); }
                break;
            case PS_Proto: case PS_Type: PS_HI(item, 0); break;
            default:
                if (item->kind == PS_Data && item->isExtern && psInitializerStart(item) == item->end) { PS_HI(item, 0); break; }   /* (a declaration) */
                fcListAdd(c, unit0->items, item); PS_HI(item, 2);
                break;
        }
    }
#undef PS_HI
    if (atomicItems->count > 0) {
        atomicItem = psItemNew(c, 0, 0, PS_Group);
        atomicItem->children = atomicItems;
        atomicItem->src = "";
    }

    /* helper units: the helper functions in their order, cut by cost (the inline c of the program in the first, all of it together) */
    {
        PsPlace* current = NULL;
        for (i = 0; i < helperFuncs->count; i++) {
            PsItem* item = (PsItem*)helperFuncs->items[i];
            if (!current || current->cost >= unitCost) { current = psPlaceNew(c, fcFmt(c, "fire_program_h%d.c", helperUnits->count + 1), SUK_Helpers); fcListAdd(c, helperUnits, current); }
            fcListAdd(c, current->items, item);
            /* (a helper costs about twice what a fire function of the same size costs) */
            current->cost += item->cost * PS_HelperWeight;
        }
        if (atomicItem) {
            PsPlace* first;
            if (helperUnits->count == 0) fcListAdd(c, helperUnits, psPlaceNew(c, "fire_program_h1.c", SUK_Helpers));
            first = (PsPlace*)helperUnits->items[0];
            for (k = atomicItem->children->count - 1; k >= 0; k--) fcListInsert(c, first->items, 0, atomicItem->children->items[k]);
            first->cost += psCostOf(atomicItem->children);
        }
    }

    /* ---- the functions: grouped by key, cut by cost (a module starts a unit once the unit has half of it) */
    regionItems = fcListNew(c);
    for (i = 0; i < regions->count; i++) {
        PsBlock* b = (PsBlock*)regions->items[i];
        PsRegion* r = (PsRegion*)fcAlloc(c, sizeof *r);
        int func = atoi(b->group);
        if (func < funcs->count) r->key = (FgFuncKey*)funcs->items[func];
        else { r->key = (FgFuncKey*)fcAlloc(c, sizeof *r->key); r->key->module = ""; r->key->key = fcFmt(c, "%08d", func); }
        r->items = psScan(c, b->text);
        r->order = func;
        fcListAdd(c, regionItems, r);
    }
    if (regionItems->count > 1) qsort(regionItems->items, (size_t)regionItems->count, sizeof(void*), psCmpRegion);
    {
        PsPlace* current = NULL;
        const char* module = NULL;
        for (i = 0; i < regionItems->count; i++) {
            PsRegion* r = (PsRegion*)regionItems->items[i];
            int64_t cost = psCostOf(r->items);
            bool newModule = module && !streq0(r->key->module, module);
            if (!current || current->cost >= unitCost || (newModule && current->cost >= unitCost / 2)) {
                current = psPlaceNew(c, fcFmt(c, "fire_program_%d.c", funcUnits->count + 1), SUK_Functions);
                fcListAdd(c, funcUnits, current);
            }
            module = r->key->module;
            for (k = 0; k < r->items->count; k++) fcListAdd(c, current->items, r->items->items[k]);
            current->cost += cost;
        }
    }

    /* ---- the tail: unit 0 */
    {
        FcList* t = psScan(c, tail);
        for (i = 0; i < t->count; i++) fcListAdd(c, unit0->items, t->items[i]);
    }

    /* ---- the natives in C: a unit per group */
    for (i = 0; i < nativeBlocks->count; i++) {
        PsBlock* b = (PsBlock*)nativeBlocks->items[i];
        FcBuf nt = {0};
        char* name;
        PsPlace* place;
        bool seen = false;
        for (k = 0; k < i && !seen; k++) if (streq0(((PsBlock*)nativeBlocks->items[k])->group, b->group)) seen = true;
        if (seen) continue;
        fcBufStr(&nt, "");
        for (k = i; k < nativeBlocks->count; k++) if (streq0(((PsBlock*)nativeBlocks->items[k])->group, b->group)) fcBufStr(&nt, ((PsBlock*)nativeBlocks->items[k])->text);
        name = (char*)fcStr(c, b->group);
        for (k = 0; name[k]; k++) if (!psIdChar(name[k])) name[k] = '_';
        place = psPlaceNew(c, fcFmt(c, "fire_natives_%s.c", name), SUK_Natives);
        place->nativesText = fcBufDone(c, &nt);
        place->group = b->group;
        place->cost = fgUnitsIn(place->nativesText, 0, (int)strlen(place->nativesText)) / 2;
        fcListAdd(c, nativesUnits, place);
    }

    /* ---- who defines what, who uses what */
    /* (the static inline helpers once, for a build without optimization: in the last helper unit - the smallest -, which counts their cost) */
    for (i = 0; i < headerItems->count; i++) { PsHeaderItem* x = (PsHeaderItem*)headerItems->items[i]; if (x->mode == 1) inlineCost += x->item->cost; }
    if (inlineCost > 0 && helperUnits->count == 0) fcListAdd(c, helperUnits, psPlaceNew(c, "fire_program_h1.c", SUK_Helpers));
    if (inlineCost > 0) ((PsPlace*)helperUnits->items[helperUnits->count - 1])->cost += inlineCost;
    units = fcListNew(c);
    fcListAdd(c, units, unit0);
    for (i = 0; i < helperUnits->count; i++) fcListAdd(c, units, helperUnits->items[i]);
    for (i = 0; i < funcUnits->count; i++) fcListAdd(c, units, funcUnits->items[i]);
    memset(&st, 0, sizeof st);
    st.c = c;
    st.h = &h;
    st.definedIn = fcDictNew(c, FC_KEY_STR);
    st.declared = fcDictNew(c, FC_KEY_STR);
    st.exported = fcDictNew(c, FC_KEY_STR);
    st.inlineDefs = fcDictNew(c, FC_KEY_STR);
    for (i = 0; i < units->count; i++) {
        PsPlace* u = (PsPlace*)units->items[i];
        for (k = 0; k < u->items->count; k++) { psDefines(&st, u, (PsItem*)u->items->items[k]); psUsesOf(u, (PsItem*)u->items->items[k]); }
    }
    for (i = 0; i < headerItems->count; i++) { PsHeaderItem* x = (PsHeaderItem*)headerItems->items[i]; if (x->mode != 2) psUsesOf(header, x->item); }
    /* types: a type of a function unit or of the tail that another unit uses moves to the header (with what it needs, until nothing more moves) */
    movedTypes = fcListNew(c);
    typeIn = fcDictNew(c, FC_KEY_STR);
    {
        typedef struct { PsPlace* place; PsItem* item; } PsTypeAt;
        for (i = 0; i < units->count; i++) {
            PsPlace* u = (PsPlace*)units->items[i];
            for (k = 0; k < u->items->count; k++) {
                PsItem* item = (PsItem*)u->items->items[k];
                int n;
                if (item->kind != PS_Type) continue;
                for (n = 0; n < item->names->count; n++)
                    if (!fcDictHas(typeIn, item->names->items[n])) { PsTypeAt* ta = (PsTypeAt*)fcAlloc(c, sizeof *ta); ta->place = u; ta->item = item; fcDictPut(typeIn, item->names->items[n], ta); }
            }
        }
        for (bool moved = true; moved;) {
            int count = 0, j;
            const char** names;
            PsTypeAt** ats;
            moved = false;
            names = (const char**)fcAlloc(c, sizeof(char*) * (size_t)(fcDictCount(typeIn) + 1));
            ats = (PsTypeAt**)fcAlloc(c, sizeof(void*) * (size_t)(fcDictCount(typeIn) + 1));
            FC_DICT_EACH(typeIn, e) { names[count] = (const char*)e->key; ats[count++] = (PsTypeAt*)e->value; }
            for (j = 0; j < count; j++) {
                PsTypeAt* ta = ats[j];
                bool used;
                if (!psListHasPtr(ta->place->items, ta->item)) continue;
                used = fcDictHas(header->uses, names[j]);
                for (k = 0; k < units->count && !used; k++) if (units->items[k] != ta->place && fcDictHas(((PsPlace*)units->items[k])->uses, names[j])) used = true;
                if (!used) continue;
                psListRemovePtr(ta->place->items, ta->item);
                fcListAdd(c, movedTypes, ta->item);
                psUnion(header->uses, ta->item->uses);
                moved = true;
            }
        }
    }
    /* the natives the program calls */
    programUses = fcDictNew(c, FC_KEY_STR);
    psUnion(programUses, header->uses);
    for (i = 0; i < units->count; i++) psUnion(programUses, ((PsPlace*)units->items[i])->uses);
    calledNatives = fcListNew(c);
    {
        FcDict* seen = fcDictNew(c, FC_KEY_STR);
        for (i = 0; i < natives->count; i++) {
            FgNativeFn* nf = (FgNativeFn*)natives->items[i];
            if (!fcDictHas(programUses, nf->function) || !fcSetAdd(seen, nf->function)) continue;
            fcListAdd(c, calledNatives, nf);
        }
    }

    /* exported: defined in one place, used in another (the header counts as every unit) */
    FC_DICT_EACH(st.definedIn, e) {
        PsPlace* place = (PsPlace*)e->value;
        bool used = fcDictHas(header->uses, e->key);
        for (k = 0; k < units->count && !used; k++) if (units->items[k] != place && fcDictHas(((PsPlace*)units->items[k])->uses, e->key)) used = true;
        if (used) fcSetAdd(st.exported, e->key);
    }
    for (i = 0; i < headerItems->count; i++) {
        PsHeaderItem* x = (PsHeaderItem*)headerItems->items[i];
        if (x->mode != 1) continue;
        for (k = 0; k < x->item->names->count; k++) if (!fcDictHas(st.inlineDefs, x->item->names->items[k])) fcDictPut(st.inlineDefs, x->item->names->items[k], x->item);
    }
    inlineHome = helperUnits->count > 0 ? ((PsPlace*)helperUnits->items[helperUnits->count - 1])->name : unit0->name;

    /* ---- the header */
    fcBufStr(&h, "// The declarations of the program, shared by its translation units (docs/spec/NATIVE_SPLIT.md) - generated, do not edit.\n");
    fcBufStr(&h, "#ifndef FIRE_PROGRAM_H\n#define FIRE_PROGRAM_H\n");
    fcBufStr(&h, "#define FIRE_SPLIT 1   // the program is several units: what the platform keeps once lives in unit 0 (fire_rt.h FIRE_ONCE)\n");
    fcBufStr(&h, headText);
    if (!fcEndsWith(headText, "\n")) fcBufChar(&h, '\n');
    fcBufStr(&h, "/* a symbol of the program that other units of it use: visible in the program, not exported (also not with -rdynamic) */\n");
    fcBufStr(&h, "#if defined(__GNUC__) || defined(__clang__)\n#pragma GCC diagnostic ignored \"-Wmisleading-indentation\"\n#endif\n");
    fcBufStr(&h, "#if (defined(__GNUC__) || defined(__clang__)) && !defined(_WIN32) && !defined(__CYGWIN__)\n#define FIRE_PS __attribute__((visibility(\"hidden\")))\n#else\n#define FIRE_PS\n#endif\n");
    fcBufStr(&h, "#if defined(__OPTIMIZE__) && !defined(FIRE_SPLIT_NO_INLINE)\n#define FIRE_SPLIT_INLINE 1\n#else\n#define FIRE_SPLIT_INLINE 0\n#endif\n");
    fcBufStr(&h, "#ifndef FIRE_NATIVES_UNIT\n");
    if (json) fcBufStr(&h, "#include \"fire_json.h\"\n");
    if (calledNatives->count > 0) {
        fcBufStr(&h, "/* the natives in C, in units of their own (fire_natives_*.c): called through these */\n");
        for (i = 0; i < calledNatives->count; i++) {
            FgNativeFn* nf = (FgNativeFn*)calledNatives->items[i];
            fcBufFmt(&h, "FIRE_PS %s fire_cn_%s(%s);\n#define %s fire_cn_%s\n", nf->ret, nf->function, nf->params->count == 0 ? "void" : fgJoin(c, nf->params, ", "), nf->function, nf->function);
        }
    }
    for (i = 0; i < headerItems->count; i++) {
        PsHeaderItem* x = (PsHeaderItem*)headerItems->items[i];
        if (x->mode == 2) psDeclare(&st, x->item);
        else psHeaderItem(&st, x->item, x->mode == 1);
    }
    fcBufChar(&h, '\n');
    if (fcDictCount(st.inlineDefs) > 0) {
        fcBufFmt(&h, "/* the static inline helpers of the program (FIRE_SPLIT_INLINE: in every unit that uses them; else once, in %s) */\n#if FIRE_SPLIT_INLINE\n", inlineHome);
        for (i = 0; i < headerItems->count; i++) { PsHeaderItem* x = (PsHeaderItem*)headerItems->items[i]; if (x->mode == 1) { psNewLine(&st); fcBufStr(&h, psDeclaration(c, x->item, false)); fcBufChar(&h, '\n'); } }
        fcBufStr(&h, "#else\n");
        for (i = 0; i < headerItems->count; i++) { PsHeaderItem* x = (PsHeaderItem*)headerItems->items[i]; if (x->mode == 1) { psNewLine(&st); fcBufStr(&h, psDeclaration(c, x->item, true)); fcBufChar(&h, '\n'); } }
        fcBufStr(&h, "#endif\n");
    }
    if (movedTypes->count > 0) {
        fcBufStr(&h, "/* types of the functions that other units use */\n");
        for (i = 0; i < movedTypes->count; i++) { PsItem* t = (PsItem*)movedTypes->items[i]; fcBufPut(&h, t->src + t->start, (size_t)(t->end - t->start)); fcBufChar(&h, '\n'); }
    }
    /* the declarations of the exported definitions that have no prototype in the header (data, functions of the units and the tail); a group keeps its #if */
    fcBufStr(&h, "/* what the units of the program define for each other */\n");
    for (i = 0; i < units->count; i++) {
        PsPlace* u = (PsPlace*)units->items[i];
        for (k = 0; k < u->items->count; k++) psDeclare(&st, (PsItem*)u->items->items[k]);
    }
    fcBufStr(&h, "#endif /* FIRE_NATIVES_UNIT */\n#endif /* FIRE_PROGRAM_H */\n");

    /* ---- the units */
    result = fcListNew(c);
    fcListAdd(c, result, psUnit(c, PS_HeaderName, ResolveLineResets(g, fcBufDone(c, &h), PS_HeaderName), SUK_Header, 0));
    for (i = 0; i < units->count; i++) {
        PsPlace* p = (PsPlace*)units->items[i];
        FcBuf sb = {0};
        FcDict* need = fcDictNew(c, FC_KEY_PTR);
        FcList* work = fcListNew(c);
        PsUnitCtx uc;
        fcBufStr(&sb, "");
        if (p == unit0) {
            /* (the head of the one file - the target, the switches of the runtime, what the build links - without its includes) */
            for (k = 0; k < headLines->count; k++) {
                const char* line = (const char*)headLines->items[k];
                if (fcStartsWith(line, "// ") || fcStartsWith(line, "#define ")) fcBufFmt(&sb, "%s\n", line);
            }
            if (rtImpl) fcBufStr(&sb, "#define FIRE_RT_IMPLEMENTATION 1   // the runtime's global state lives in this file\n");
            if (json) fcBufStr(&sb, "#define FIRE_JSON_IMPLEMENTATION 1   // the JSON engine too\n");
        }
        fcBufFmt(&sb, "#include \"%s\"\n", PS_HeaderName);
        /* the static inline helpers this unit uses (and those they use), in the order of the program */
        FC_DICT_EACH(p->uses, e) {
            PsItem* d = (PsItem*)fcDictGet(st.inlineDefs, e->key);
            if (d && fcSetAdd(need, d)) fcListAdd(c, work, d);
        }
        while (work->count > 0) {
            PsItem* d0 = (PsItem*)work->items[--work->count];
            FC_DICT_EACH(d0->uses, e) {
                PsItem* d = (PsItem*)fcDictGet(st.inlineDefs, e->key);
                if (d && fcSetAdd(need, d)) fcListAdd(c, work, d);
            }
        }
        if (fcDictCount(need) > 0) {
            fcBufStr(&sb, "#if FIRE_SPLIT_INLINE   /* the helpers of the program that this unit uses */");
            for (k = 0; k < headerItems->count; k++) {
                PsHeaderItem* x = (PsHeaderItem*)headerItems->items[k];
                if (x->mode == 1 && fcDictHas(need, x->item)) { if (x->item->src[x->item->start] != '\n') fcBufChar(&sb, '\n'); fcBufPut(&sb, x->item->src + x->item->start, (size_t)(x->item->end - x->item->start)); }
            }
            fcBufStr(&sb, "\n#endif\n");
        }
        uc.st = &st;
        uc.sb = &sb;
        for (k = 0; k < p->items->count; k++) psWrite(&uc, (PsItem*)p->items->items[k]);
        if (streq0(p->name, inlineHome) && fcDictCount(st.inlineDefs) > 0) {
            fcBufStr(&sb, "\n#if !FIRE_SPLIT_INLINE   /* the static inline helpers of the program, once (a build without optimization) */\n");
            for (k = 0; k < headerItems->count; k++) {
                PsHeaderItem* x = (PsHeaderItem*)headerItems->items[k];
                if (x->mode != 1) continue;
                fcBufStr(&sb, psExported(c, x->item->src + x->item->start, x->item->body - x->item->start, true));
                fcBufPut(&sb, x->item->src + x->item->body, (size_t)(x->item->end - x->item->body));
                fcBufChar(&sb, '\n');
            }
            fcBufStr(&sb, "#endif\n");
        }
        fcBufChar(&sb, '\n');
        fcListAdd(c, result, psUnit(c, p->name, ResolveLineResets(g, fcBufDone(c, &sb), p->name), p->kind, p == unit0 ? psCostOf(unit0->items) : p->cost));
    }
    for (i = 0; i < nativesUnits->count; i++) {
        PsPlace* p = (PsPlace*)nativesUnits->items[i];
        FcBuf sb = {0};
        bool any = false;
        fcBufStr(&sb, "#define FIRE_NATIVES_UNIT 1   // the natives in C of the program: the configuration of the runtime, not the declarations of the program\n");
        fcBufFmt(&sb, "#include \"%s\"\n", PS_HeaderName);
        fcBufStr(&sb, p->nativesText);
        for (k = 0; k < calledNatives->count; k++) {
            FgNativeFn* nf = (FgNativeFn*)calledNatives->items[k];
            FcList* ps;
            FcList* args;
            int a;
            if (!streq0(nf->group, p->group)) continue;
            if (!any) { fcBufStr(&sb, "\n/* the natives the program calls (its units see them under these names) */\n"); any = true; }
            ps = fcListNew(c);
            args = fcListNew(c);
            for (a = 0; a < nf->params->count; a++) {
                const char* t = (const char*)nf->params->items[a];
                bool list = streq0(t, "OwnList*");
                fcListAdd(c, ps, (void*)(list ? fcFmt(c, "%s list", t) : fcFmt(c, "%s a%d", t, a)));
                fcListAdd(c, args, (void*)(list ? "list" : fcFmt(c, "a%d", a)));
            }
            fcBufFmt(&sb, "FIRE_PS %s fire_cn_%s(%s) { %s%s(%s); }\n", nf->ret, nf->function, ps->count == 0 ? "void" : fgJoin(c, ps, ", "), streq0(nf->ret, "void") ? "" : "return ", nf->function, fgJoin(c, args, ", "));
        }
        fcListAdd(c, result, psUnit(c, p->name, fcBufDone(c, &sb), SUK_Natives, p->cost));
    }
    return result;
}

/* ---- CppGenerator.Stats.cs: ModuleOfStat ---- */
static const char* ModuleOfStat(FgGen* g, FgFunc* f) {
    int i;
    const char* m;
    if (f->kind == FK_Main) return "program";   /* (the top level of the program; the top levels of the libraries run in it too) */
    if (f->chunk->lineCount == 0) return "(no source)";
    i = f->chunk->lines[0].source;
    if (i >= g->program->firstUserSource) return "program";
    m = g->program->sourceModules && i < g->program->sourceModules->count ? (const char*)g->program->sourceModules->items[i] : NULL;
    return m ? m : "core";
}

/* ---- CppGenerator.Split.cs ---- */
/* The key a function is grouped by: its module, then its source and first line, its class and its kind. */
static FgFuncKey* SplitKeyOf(FgGen* g, FgFunc* f) {
    FgFuncKey* k = (FgFuncKey*)fcAlloc(g->c, sizeof *k);
    int source = f->chunk->lineCount > 0 ? f->chunk->lines[0].source : 0, line = f->chunk->lineCount > 0 ? f->chunk->lines[0].line : 0;
    k->module = ModuleOfStat(g, f);
    k->key = fcFmt(g->c, "%05d:%07d:%s:%d", source, line, f->chunk->ownerClass ? f->chunk->ownerClass->name : "", (int)f->kind);
    return k;
}

/* ---- StaticLinkage.cs: whether the natives in C of an import stand alone ---- */
/* The C of the natives of an import (empty: none). */
static const char* fgNativeCText(FgGen* g, const char* import) {
    const char* key = fcFmt(g->c, "ctext\x01%s", fcAsciiLower(g->c, import));
    const char* known = (const char*)fcDictGet(g->bridgeCache, key);
    FgInstImport* found = NULL;
    FcBuf b = {0};
    FcList* texts;
    int i;
    if (known) return known;
    if (fcStartsWith(import, "pkg:")) {
        FcPkgImport* pi = fcStoreFindKey(g->c, g->store, import);
        if (pi) found = fgInstalledImport(g->c, g->instImports, pi);
    } else {
        if (!g->store->loaded) fcStoreLoad(g->c, g->store);
        for (i = 0; i < g->store->imports->count && !found; i++) {
            FcPkgImport* pi = (FcPkgImport*)g->store->imports->items[i];
            FgInstImport* x;
            if (!fcAsciiIEq(pi->name, import)) continue;
            x = fgInstalledImport(g->c, g->instImports, pi);
            if (x->native) found = x;
        }
    }
    fcBufStr(&b, "");
    if (found && found->native) {
        texts = fgReadNativeSources(g->c, g->instImports, g->store, found, fgPlatformKeys(g));
        for (i = 0; i < texts->count; i++) fcBufFmt(&b, "%s\n", ((FgNamedText*)texts->items[i])->text);
    }
    known = fcBufDone(g->c, &b);
    fcDictPut(g->bridgeCache, key, (void*)known);
    return known;
}
/* The prefix of the C functions of an import's natives (`fire_hash_`, `fire_gfx_`, `fire_win_`). */
static const char* fgNativePrefix(FgGen* g, const char* import) {
    FcPkgImport* pi;
    FgInstImport* x;
    int i;
    if (fcAsciiIEq(import, "graphics")) return "fire_gfx_";
    if (fcAsciiIEq(import, "windows")) return "fire_win_";
    if (!(pi = fcStoreFindKey(g->c, g->store, import)) || !(x = fgInstalledImport(g->c, g->instImports, pi))->native) return "";
    for (i = 0; i < x->native->functions->count; i++) {
        const char* symbol = fgPkgFnSymbol((FgPkgFn*)x->native->functions->items[i]);
        const char* second;
        if (!fcStartsWith(symbol, "fire_")) continue;
        second = strchr(symbol + 5, '_');
        return second && second - symbol > 5 ? fcStrN(g->c, symbol, (size_t)(second - symbol + 1)) : "";
    }
    return "";
}
/* The imports of the program with natives in C: graphics and windows (their modules), the packages in C. */
static FcList* fgNativeCImports(FgGen* g) {
    FcList* result = fcListNew(g->c);
    static const char* const standard[] = { "graphics", "windows" };
    int s, i;
    for (s = 0; s < 2; s++) {
        bool has = fcDictHas(g->program->nativeImports, standard[s]);
        if (g->program->sourceModules)
            for (i = 0; i < g->program->sourceModules->count && !has; i++) {
                const char* m = (const char*)g->program->sourceModules->items[i];
                if (m && fcAsciiIEq(m, standard[s])) has = true;
            }
        if (has) fcListAdd(g->c, result, (void*)standard[s]);
    }
    FC_DICT_EACH(g->program->nativeImports, e) {
        FcPkgImport* pi;
        if (!fcStartsWith((const char*)e->key, "pkg:")) continue;
        if ((pi = fcStoreFindKey(g->c, g->store, (const char*)e->key)) != NULL) {
            FgInstImport* x = fgInstalledImport(g->c, g->instImports, pi);
            if (x->native && x->native->isC) fcListAdd(g->c, result, (void*)e->key);
        }
    }
    return result;
}
static bool NativesStandAlone(FgGen* g, const char* import) {
    FcList* imports = fgNativeCImports(g);
    const char *own, *ownPrefix;
    bool has = false;
    int i;
    for (i = 0; i < imports->count && !has; i++) if (fcAsciiIEq((const char*)imports->items[i], import)) has = true;
    if (!has) return false;
    own = fgNativeCText(g, import);
    ownPrefix = fgNativePrefix(g, import);
    for (i = 0; i < imports->count; i++) {
        const char* other = (const char*)imports->items[i];
        const char* prefix;
        if (fcAsciiIEq(other, import)) continue;
        prefix = fgNativePrefix(g, other);
        if (*prefix && strstr(own, prefix)) return false;
        if (*ownPrefix && strstr(fgNativeCText(g, other), ownPrefix)) return false;
    }
    return true;
}

/* The unit the natives in C of an import go to: their own when they stand alone, else the one they share with the others. */
static const char* SplitNativesGroup(FgGen* g, const char* import) {
    int i;
    if (!NativesStandAlone(g, import)) return "shared";
    for (i = 0; i < g->packageImports->count; i++) {
        FgPkgImportKey* p = (FgPkgImportKey*)g->packageImports->items[i];
        if (streq0(p->key, import)) return fcAsciiLower(g->c, p->import->name);
    }
    return fcAsciiLower(g->c, import);
}

static FcList* fgValues(FgGen* g, int argc, bool needsList) {
    FcList* l = fcListNew(g->c);
    int i;
    for (i = 0; i < argc; i++) fcListAdd(g->c, l, (void*)"Value");
    if (needsList) fcListAdd(g->c, l, (void*)"OwnList*");
    return l;
}
static void fgAddNativeFn(FgGen* g, FcList* result, const char* group, const char* function, const char* ret, FcList* params) {
    FgNativeFn* nf = (FgNativeFn*)fcAlloc(g->c, sizeof *nf);
    nf->group = group; nf->function = function; nf->ret = ret; nf->params = params;
    fcListAdd(g->c, result, nf);
}
/* The natives in C that the code of the program may call, with their C signatures (a typed one with its C numbers). */
static FcList* SplitNatives(FgGen* g) {
    FcList* result = fcListNew(g->c);
    int i, k;
    if (g->usesGraphics && fcDictHas(g->standardNativesWritten, "graphics")) {
        const char* group = SplitNativesGroup(g, "graphics");
        FC_DICT_EACH(BridgeOf(g, "graphics", "__GRPH"), e) {
            FgPkgFn* fn = (FgPkgFn*)e->value;
            fgAddNativeFn(g, result, group, fcFmt(g->c, "fire_gfx_%s", (const char*)e->key), "Value", fgValues(g, fn->arguments, fn->needsList));
        }
    }
    if (g->usesWindows && fcDictHas(g->standardNativesWritten, "windows")) {
        const char* group = SplitNativesGroup(g, "windows");
        FC_DICT_EACH(BridgeOf(g, "windows", "__GRPHWin"), e) {
            FgPkgFn* fn = (FgPkgFn*)e->value;
            fgAddNativeFn(g, result, group, fcFmt(g->c, "fire_win_Win%s", (const char*)e->key), "Value", fgValues(g, fn->arguments, fn->needsList));
        }
    }
    for (i = 0; i < g->packageImports->count; i++) {
        FgPkgImportKey* p = (FgPkgImportKey*)g->packageImports->items[i];
        const char* group;
        if (!(p->import->native && p->import->native->isC) || fcDictHas(g->standardNativesWritten, fcAsciiLower(g->c, p->import->name))) continue;
        group = SplitNativesGroup(g, p->key);
        for (k = 0; k < p->import->native->functions->count; k++) {
            FgPkgFn* fn = (FgPkgFn*)p->import->native->functions->items[k];
            bool typed = fgNativeIsTyped(fn, true);
            FcList* ps = fcListNew(g->c);
            const char* ret = typed ? fgNativeReturnCType(fn) : NULL;
            int a;
            for (a = 0; a < fn->arguments; a++) { const char* t = typed ? fgNativeParamCType(fn, a) : NULL; fcListAdd(g->c, ps, (void*)(t ? t : "Value")); }
            if (fn->needsList) fcListAdd(g->c, ps, (void*)"OwnList*");
            fgAddNativeFn(g, result, group, fgPkgFnSymbol(fn), ret ? ret : "Value", ps);
        }
    }
    return result;
}

/* FIRE_SPLIT_UNIT_KB, else 600 KB */
static int64_t fgSplitUnitCost(void) {
    const char* kb = getenv("FIRE_SPLIT_UNIT_KB");
    long long k = kb ? atoll(kb) : 0;
    return k > 0 ? (int64_t)k * 1024 : 600 * 1024;
}
static FcList* ProgramSplitSplit(FgGen* g, const char* programText, FcList* splitKeys) {
    return fgProgramSplit(g, programText, splitKeys, SplitNatives(g), fgSplitUnitCost());
}
