/* native/forge/gen/trace.c - src/fire.Native/CppGenerator.Trace.cs: stack traces of exceptions in the native build (docs/spec/STACKTRACE.md section 13).
 * A fire function that can throw keeps a frame on the C stack (`FireTFrame TF`, fire_rt.h), pushed at its start and taken off at every `return`; the code
 * stores the line where a line of the line table begins and at every jump target, the descriptor where a function spans files. The descriptors
 * (`kFI_fN`: the name by TraceNames, the file by LinkedProgram.TraceFiles) are static data of the module that has the function. A catch block makes the
 * frame of its function the top while it runs (FIRE_TF_CATCH). A function that cannot throw has none. `off` generates none of it, `calls` no lines. */


static bool fgTrIdStart(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_'; }
static bool fgTrIdChar(char c) { return fgTrIdStart(c) || (c >= '0' && c <= '9'); }
static bool fgTrSpace(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v'; }
static bool fgTrSame(const char* a, const char* b) { return a == b || (a && b && strcmp(a, b) == 0); }

/* TraceNames.Build (src/fire/Bytecode/TraceNames.cs): chunk -> name; the first name a chunk gets is its name */
static const char* fgTraceClassDisplay(FgGen* g, const char* name) {
    size_t cut = strcspn(name, "`<");
    return cut > 0 && name[cut] ? fcStrN(g->c, name, cut) : name;
}
static void fgTraceAdd(FgGen* g, const FcProto* p, const char* name) {
    int k;
    if (!fcDictHas(g->traceNames, p->chunk)) fcDictPut(g->traceNames, p->chunk, (void*)name);
    if (p->specializations)
        for (k = 0; k < p->specializations->count; k++) {
            const FcProto* s = (const FcProto*)p->specializations->items[k];
            if (s && !fcDictHas(g->traceNames, s->chunk)) fcDictPut(g->traceNames, s->chunk, (void*)name);
        }
}
static void fgTraceDefaults(FgGen* g, const FcProto* p, const char* init) {
    int k;
    for (k = 0; k < p->paramDefaults->count; k++) {
        const FcProto* d = (const FcProto*)p->paramDefaults->items[k];
        if (d && !fcDictHas(g->traceNames, d->chunk)) fcDictPut(g->traceNames, d->chunk, (void*)init);
    }
}
static void fgTraceNamesBuild(FgGen* g) {
    g->traceNames = fcDictNew(g->c, FC_KEY_PTR);
    fcDictPut(g->traceNames, g->program->program->topLevel, (void*)"<main>");
    FC_DICT_EACH(g->program->program->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        const char* cls = fgTraceClassDisplay(g, rc->name);
        int k;
        FC_DICT_EACH(rc->methods, m) {
            FcList* protos = (FcList*)m->value;
            const char* name = fcFmt(g->c, "%s.%s", cls, (const char*)m->key);
            for (k = 0; k < protos->count; k++) fgTraceAdd(g, (const FcProto*)protos->items[k], name);
        }
        for (k = 0; k < rc->ctorProtos->count; k++) fgTraceAdd(g, (const FcProto*)rc->ctorProtos->items[k], fcFmt(g->c, "%s.construct", cls));
        for (k = 0; k < rc->extraConstructors->count; k++) fgTraceAdd(g, (const FcProto*)rc->extraConstructors->items[k], fcFmt(g->c, "%s.construct", cls));
        if (rc->destructor) fgTraceAdd(g, rc->destructor, fcFmt(g->c, "%s.destruct", cls));
    }
    /* (the initializers after every member: a member that is also a default value keeps its own name) */
    FC_DICT_EACH(g->program->program->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        const char* init = fcFmt(g->c, "%s.<init>", fgTraceClassDisplay(g, rc->name));
        int k;
        for (k = 0; k < rc->fieldInits->count; k++) if (rc->fieldInits->items[k]) fgTraceAdd(g, (const FcProto*)rc->fieldInits->items[k], init);
        for (k = 0; k < rc->staticFieldInits->count; k++) if (rc->staticFieldInits->items[k]) fgTraceAdd(g, (const FcProto*)rc->staticFieldInits->items[k], init);
        FC_DICT_EACH(rc->methods, m) {
            FcList* protos = (FcList*)m->value;
            for (k = 0; k < protos->count; k++) fgTraceDefaults(g, (const FcProto*)protos->items[k], init);
        }
        for (k = 0; k < rc->ctorProtos->count; k++) fgTraceDefaults(g, (const FcProto*)rc->ctorProtos->items[k], init);
        for (k = 0; k < rc->extraConstructors->count; k++) fgTraceDefaults(g, (const FcProto*)rc->extraConstructors->items[k], init);
    }
}
/* The name of a function in a trace (a fire body that is no member: `<fire>`). */
static const char* TraceNameOf(FgGen* g, FgFunc* f) {
    const char* name;
    if (f->kind == FK_Main) return "<main>";
    if (!g->traceNames) fgTraceNamesBuild(g);
    if ((name = (const char*)fcDictGet(g->traceNames, f->chunk)) != NULL) return name;
    return f->kind == FK_FireBody ? "<fire>" : "<lambda>";
}

/* The file of a source as a trace shows it (NULL: the prelude). */
static const char* TraceFileOf(FgGen* g, int src) {
    FcList* files = g->program->traceFiles;
    return files && src >= 0 && src < files->count ? (const char*)files->items[src] : NULL;
}
static int fgTraceIndexOf(FcList* files, const char* file) {
    int i;
    for (i = 0; i < files->count; i++) if (fgTrSame((const char*)files->items[i], file)) return i;
    return -1;
}
/* Where an instruction is for the trace: its source and line - before the first line of a function that is not the top level (the code it runs on entry:
 * the checks of its parameters) the source of that first line and the line negative (`(entry)`, fire_rt.h fire_traceFormat; Chunk.EntryLocation). */
static void TraceLocationOf(FgFunc* f, const FcChunk* chunk, int addr, int* src, int* line) {
    if (f->kind != FK_Main && chunk->lineCount > 0 && addr < chunk->lines[0].offset) { *src = chunk->lines[0].source; *line = -chunk->lines[0].line; return; }
    fgLocation(chunk, addr, src, line);
}
/* The descriptors of a function: one per file its instructions are in, in the order of the code. */
static FcList* TraceFilesOf(FgGen* g, FgFunc* f, FcChunk* chunk, FgCode code) {
    FcList* files = fcListNew(g->c);
    int i, src, line;
    for (i = 0; i < code.n; i++) {
        const char* file;
        TraceLocationOf(f, chunk, code.p[i].addr, &src, &line);
        file = TraceFileOf(g, src);
        if (fgTraceIndexOf(files, file) < 0) fcListAdd(g->c, files, (void*)file);
    }
    if (files->count == 0) fcListAdd(g->c, files, NULL);
    return files;
}

/* Before instruction `i` (pass 2): where a line begins or a jump lands, the line (and the file, if the function has several). */
static void TraceBeforeInstruction(FgGen* g, FgFunc* f, FcChunk* chunk, FgInstr* ins, int i, FgOut* o, bool isTarget, FcList* files) {
    int lo = 0, hi = chunk->lineCount - 1, found = -1, start, src, line;
    const char* file;
    FcBuf text = {0};
    while (lo <= hi) {   /* (Chunk.GetLocationRange) */
        int mid = (lo + hi) >> 1;
        if (chunk->lines[mid].offset <= ins->addr) { found = mid; lo = mid + 1; }
        else hi = mid - 1;
    }
    start = found < 0 ? 0 : chunk->lines[found].offset;
    TraceLocationOf(f, chunk, ins->addr, &src, &line);
    if (!(i == 0 || start == ins->addr || isTarget)) return;
    file = TraceFileOf(g, src);
    fcBufStr(&text, "");
    if (files->count > 1) fcBufFmt(&text, "TF.fn = &kFI_%s[%d]; ", f->name, fgTraceIndexOf(files, file));
    if (TraceLines && file) fcBufFmt(&text, "TF.line = %d; ", line);
    if (text.len > 0) {
        while (text.len > 0 && fgTrSpace(text.p[text.len - 1])) text.len--;
        fcBufStr(&o->code, "    ");
        fcBufPut(&o->code, text.p, text.len);
        fcBufStr(&o->code, "\n");
    }
    fcBufFree(&text);
}

/* The descriptors of a function (before it). */
static const char* TraceTable(FgGen* g, FgFunc* f, FcList* files) {
    FcBuf b = {0};
    const char* name = CString(g, TraceNameOf(g, f));
    int i;
    fcBufFmt(&b, "static const FireFnInfo kFI_%s[] = {", f->name);
    for (i = 0; i < files->count; i++) {
        const char* file = (const char*)files->items[i];
        fcBufFmt(&b, "%s{%s, %s}", i ? ", " : "", name, file ? CString(g, file) : "NULL");
    }
    fcBufStr(&b, "};\n");
    return fcBufDone(g->c, &b);
}

/* The lines that store a position out of the code of a function without a frame (the buffer in place). */
static void TraceStripLines(FcBuf* code) {
    size_t r = 0, w = 0;
    if (!code->p) return;
    while (r < code->len) {
        size_t e = r;
        while (e < code->len && code->p[e] != '\n') e++;
        if (e < code->len) e++;   /* (with its line feed) */
        if (!(e - r >= 7 && memcmp(code->p + r, "    TF.", 7) == 0)) {
            if (w != r) memmove(code->p + w, code->p + r, e - r);
            w += e - r;
        }
        r = e;
    }
    code->len = w;
    if (code->p) code->p[w] = 0;
}

/* The function cannot throw: what its code calls is a C keyword, a constructor of a scalar Value, itself, or functions without frames. */
static bool TraceCannotThrow(FgGen* g, FgFunc* f, const char* code) {
    static const char* const pure[] = { "if", "while", "for", "switch", "return", "sizeof", "Int", "Float", "Bool", "Undef", "FIRE_LIKELY", "FIRE_UNLIKELY" };
    size_t i = 0, n = strlen(code), k;
    if (!g->traceCallees || g->traceCalleesCount != g->funcs->count) {
        int j;
        g->traceCallees = fcDictNew(g->c, FC_KEY_STR);
        for (j = 0; j < g->funcs->count; j++) {
            FgFunc* h = (FgFunc*)g->funcs->items[j];
            const char* cn = fcFmt(g->c, "%s_c", h->name);
            if (!fcDictHas(g->traceCallees, h->name)) fcDictPut(g->traceCallees, h->name, (void*)h->name);
            if (!fcDictHas(g->traceCallees, cn)) fcDictPut(g->traceCallees, cn, (void*)h->name);
        }
        g->traceCalleesCount = g->funcs->count;
    }
    while (i < n) {
        char c = code[i];
        size_t s, j;
        const char* id;
        const char* callee;
        void* frameless;
        bool known;
        if (c == '"' || c == '\'') {
            for (i++; i < n && code[i] != c; i++) if (code[i] == '\\') i++;
            i++;
            continue;
        }
        if (c == '/' && i + 1 < n && code[i + 1] == '/') { while (i < n && code[i] != '\n') i++; continue; }
        if (c == '#' && (i == 0 || code[i - 1] == '\n')) { while (i < n && code[i] != '\n') i++; continue; }
        if (!fgTrIdStart(c)) { i++; continue; }
        s = i;
        while (i < n && fgTrIdChar(code[i])) i++;
        j = i;
        while (j < n && code[j] == ' ') j++;
        if (j >= n || code[j] != '(') continue;
        known = false;
        for (k = 0; k < sizeof pure / sizeof pure[0]; k++) if (strlen(pure[k]) == i - s && memcmp(pure[k], code + s, i - s) == 0) known = true;
        if (known) continue;
        id = fcStrN(g->c, code + s, i - s);
        if (!(callee = (const char*)fcDictGet(g->traceCallees, id))) return false;
        if (streq0(callee, f->name)) continue;
        if (!g->traceFrameless || !fcDictTry(g->traceFrameless, callee, &frameless) || !FC_VALI(frameless)) return false;
    }
    return true;
}

/* Notes whether the function has a frame; a change is another round. */
static void TraceNoteFrameless(FgGen* g, FgFunc* f, bool frameless) {
    void* known;
    bool before;
    if (!g->traceFrameless) g->traceFrameless = fcDictNew(g->c, FC_KEY_STR);
    before = fcDictTry(g->traceFrameless, f->name, &known) && FC_VALI(known);
    fcDictPut(g->traceFrameless, f->name, FC_IVAL(frameless ? 1 : 0));
    if (before != frameless) g->version++;
}

/* `^\s*(?:[A-Za-z_][A-Za-z0-9_]*|Undef\(\)|-?[0-9][0-9A-Za-z.]*|\([A-Za-z_][A-Za-z0-9_ ]*\)\s*-?[0-9][0-9A-Za-z.]*)\s*$` (CppGenerator.Trace.cs TraceSimpleReturn) */
static bool fgTrEndsHere(const char* p, const char* end) { while (p < end && fgTrSpace(*p)) p++; return p == end; }
static const char* fgTrNumber(const char* p, const char* end) {
    if (p < end && *p == '-') p++;
    if (!(p < end && *p >= '0' && *p <= '9')) return NULL;
    while (p < end && (fgTrIdChar(*p) || *p == '.')) p++;
    return p;
}
static bool TraceSimpleReturn(const char* s, size_t n) {
    const char* p = s;
    const char* end = s + n;
    const char* q;
    while (p < end && fgTrSpace(*p)) p++;
    if (p < end && fgTrIdStart(*p)) {
        q = p;
        while (q < end && fgTrIdChar(*q)) q++;
        if (fgTrEndsHere(q, end)) return true;
    }
    if ((size_t)(end - p) >= 7 && memcmp(p, "Undef()", 7) == 0 && fgTrEndsHere(p + 7, end)) return true;
    if ((q = fgTrNumber(p, end)) != NULL && fgTrEndsHere(q, end)) return true;
    if (p < end && *p == '(' && p + 1 < end && fgTrIdStart(p[1])) {
        q = p + 2;
        while (q < end && (fgTrIdChar(*q) || *q == ' ')) q++;
        if (q < end && *q == ')') {
            q++;
            while (q < end && fgTrSpace(*q)) q++;
            if ((q = fgTrNumber(q, end)) != NULL && fgTrEndsHere(q, end)) return true;
        }
    }
    return false;
}

/* Every `return x;` of `code` takes the frame off first: `{ leave; return x; }` - a value that is no variable or constant is computed before
 * (`{ type r_tf = x; leave; return r_tf; }`: a call in it still has the frame below it). */
static const char* TraceReturns(FgGen* g, const char* code, const char* leave, const char* retType) {
    FcBuf sb = {0};
    size_t i = 0, n = strlen(code), copied = 0;
    if (!strstr(code, "return")) return code;
    fcBufStr(&sb, "");
    while (i < n) {
        char c = code[i];
        size_t s, j;
        int depth = 0;
        if (c == '"' || c == '\'') {
            for (i++; i < n && code[i] != c; i++) if (code[i] == '\\') i++;
            i++;
            continue;
        }
        if (c == '/' && i + 1 < n && code[i + 1] == '/') { while (i < n && code[i] != '\n') i++; continue; }
        if (c == '#' && (i == 0 || code[i - 1] == '\n')) { while (i < n && code[i] != '\n') i++; continue; }
        if (!fgTrIdStart(c)) { i++; continue; }
        s = i;
        while (i < n && fgTrIdChar(code[i])) i++;
        if (i - s != 6 || memcmp(code + s, "return", 6) != 0) continue;
        /* the end of the statement: the `;` outside of brackets and literals */
        j = i;
        while (j < n) {
            char d = code[j];
            if (d == '"' || d == '\'') {
                for (j++; j < n && code[j] != d; j++) if (code[j] == '\\') j++;
                j++;
                continue;
            }
            if (d == '(' || d == '[' || d == '{') depth++;
            else if (d == ')' || d == ']' || d == '}') depth--;
            else if (d == ';' && depth <= 0) break;
            j++;
        }
        if (j >= n) break;
        fcBufPut(&sb, code + copied, s - copied);
        if (fgTrEndsHere(code + i, code + j)) fcBufFmt(&sb, "{ %s; return; }", leave);
        else if (TraceSimpleReturn(code + i, j - i) || !retType) { fcBufFmt(&sb, "{ %s; return", leave); fcBufPut(&sb, code + i, j - i); fcBufStr(&sb, "; }"); }
        else { fcBufFmt(&sb, "{ %s r_tf =", retType); fcBufPut(&sb, code + i, j - i); fcBufFmt(&sb, "; %s; return r_tf; }", leave); }
        copied = j + 1;
        i = j + 1;
    }
    fcBufPut(&sb, code + copied, n - copied);
    return fcBufDone(g->c, &sb);
}

/* Around the call of a native of a package or of the windows bridge (they can call back into fire: fire_cbCall): the native whose callbacks get its frame -
 * `at __GRPHWinTick (native)` (fire_rt.h FIRE_TF_NAT). Not for a typed native whose C number is a scalar of the code, not for the graphics bridge. */
static const char* TraceNat(FgGen* g, const char* native) { return TraceOn ? fcFmt(g->c, "FIRE_TF_NAT(%s); ", CString(g, native)) : ""; }
static const char* TraceNatEnd(FgGen* g) { return TraceOn ? " FIRE_TF_NAT_END();" : ""; }

/* `fire`: where it stands, as the line of the thread names it (`main.fire:40`; `prelude`; without the line with `calls`). */
static const char* TraceStartAt(FgGen* g, FcChunk* chunk, FgInstr* ins) {
    int src, line;
    const char* file;
    const char* where;
    if (!TraceOn) return "";
    fgLocation(chunk, ins->addr, &src, &line);
    file = TraceFileOf(g, src);
    where = !file ? "prelude" : TraceLines && line > 0 ? fcFmt(g->c, "%s:%d", file, line) : file;
    return fcFmt(g->c, "g_traceStartAt = %s; ", CString(g, where));
}
