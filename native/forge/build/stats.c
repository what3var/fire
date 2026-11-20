/* native/forge/build/stats.c - src/fire.Native/NativeStats.cs (commit 808e9788) and NativeBuilder.WriteStats (src/fire.Compiler/NativeBuilder.cs, commit
 * 6f21d1a4): the report of a native build with `--stats` - the code by origin (from the linker's map), the typing of the generated functions (gen/stats.c),
 * for DOS the memory the program needs to load - as text (into the log and <program>.stats.txt) and as JSON (<program>.stats.json, as System.Text.Json
 * writes the record indented with the default encoder). The regular expressions of the C# are written out by hand. Not ported: `forge stats a b` (Compare,
 * FromJson). */

typedef struct NsOrigin { const char* name; long long bytes; int functions; } NsOrigin;
typedef struct NsBiggest { const char* cname; const char* what; const char* origin; long long bytes; } NsBiggest;
typedef struct NsTyping { const char* module; int functions; long long bytes; int withCAbi, params, cparams, valueLocals, cLocals, stackSlots, shadows; } NsTyping;
typedef struct NsCount { const char* key; long long n; } NsCount;
typedef struct NsReport {
    const char* target;
    long long fileBytes;
    bool hasLoad; long long loadBytes;
    FcList* code;      /* NsOrigin* */
    FcList* data;      /* NsOrigin* */
    FcList* typing;    /* NsTyping* */
    FcList* noCAbi;    /* NsCount* */
    FcList* biggest;   /* NsBiggest* */
    FcList* generated; /* NsCount* */
    FcList* functions; /* FgFuncStat* */
} NsReport;
typedef struct NsSection { const char* name; long long size; const char* file; } NsSection;

/* MapOptions: the linker options that write the map (and keep each function in a section of its own); NULL for a toolchain without them */
static const char* nsMapOptions(FcCtx* c, const char* kind, const char* map) {
    if (streq0(kind, "watcom")) return fcFmt(c, "-Wl,option -Wl,map=\"%s\"", map);
    if (streq0(kind, "gcc") || streq0(kind, "clang") || streq0(kind, "mingw")) return fcFmt(c, "-ffunction-sections -Wl,-Map=\"%s\"", map);
    return NULL;
}
static const char* nsMapFileOf(FcCtx* c, const char* exe) { return fcFmt(c, "%s.map", exe); }

/* File.ReadAllLines: the lines of a file (\n, \r\n and \r end a line; NULL when it cannot be read) */
static FcList* nsReadLines(FcCtx* c, const char* path) {
    size_t n = 0, i = 0;
    uint8_t* data = fbReadFile(path, &n);
    FcList* lines;
    if (!data) return NULL;
    lines = fcListNew(c);
    if (n >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;
    while (i < n) {
        size_t e = i;
        while (e < n && data[e] != '\n' && data[e] != '\r') e++;
        fcListAdd(c, lines, (void*)fcStrN(c, (const char*)data + i, e - i));
        if (e < n && data[e] == '\r' && e + 1 < n && data[e + 1] == '\n') e++;
        i = e + 1;
    }
    free(data);
    return lines;
}

static bool nsWordChar(unsigned char ch) { return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || ch >= 0x80; }
static bool nsSpace(unsigned char ch) { return ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '\f' || ch == '\v'; }
/* DefinitionRegex: ^(?!typedef|return|else|if|for|while|#|//|/\*|\s)[A-Za-z_][\w \*]*?\b([A-Za-z_]\w*)\s*\(([^;]*)$ - the name of group 1, NULL: no match */
static const char* nsDefinitionMatch(FcCtx* c, const char* l) {
    static const char* const not[] = { "typedef", "return", "else", "if", "for", "while", "#", "//", "/*" };
    const char* paren = strchr(l, '(');
    const char* end;
    const char* start;
    const char* p;
    size_t k;
    for (k = 0; k < sizeof not / sizeof not[0]; k++) if (fcStartsWith(l, not[k])) return NULL;
    if (!*l || nsSpace((unsigned char)*l)) return NULL;
    if (!((*l >= 'A' && *l <= 'Z') || (*l >= 'a' && *l <= 'z') || *l == '_')) return NULL;
    if (!paren || strchr(paren, ';')) return NULL;
    end = paren;
    while (end > l && nsSpace((unsigned char)end[-1])) end--;
    start = end;
    while (start > l && nsWordChar((unsigned char)start[-1])) start--;
    /* (the name: after the first character, at a word boundary, starting with a letter; before it only [\w \*]) */
    if (start == end || start == l) return NULL;   /* (the first character is not part of the name, and the name starts at a word boundary) */
    if (!((*start >= 'A' && *start <= 'Z') || (*start >= 'a' && *start <= 'z') || *start == '_')) return NULL;
    for (p = l + 1; p < start; p++) if (!(nsWordChar((unsigned char)*p) || *p == ' ' || *p == '*')) return NULL;
    return fcStrN(c, start, (size_t)(end - start));
}
/* DefinitionAt: the name of the function whose definition starts at line i (its `{` on this or the next line), else NULL */
static const char* nsDefinitionAt(FcCtx* c, FcList* lines, int i) {
    const char* name = nsDefinitionMatch(c, (const char*)lines->items[i]);
    const char* rest;
    size_t n;
    int j;
    if (!name) return NULL;
    rest = (const char*)lines->items[i];
    n = strlen(rest);
    while (n > 0 && nsSpace((unsigned char)rest[n - 1])) n--;
    if (n > 0 && rest[n - 1] == '{') return name;
    for (j = i + 1; j < lines->count && j < i + 6; j++) {
        const char* t = fgTrim(c, (const char*)lines->items[j]);
        size_t tn = strlen(t);
        if (t[0] == '{') return name;
        if (tn == 0 || t[tn - 1] == ';') return NULL;
    }
    return NULL;
}

/* Directory.EnumerateFiles(dir, "*.h*", AllDirectories): the files of a folder as they are read, then its folders in that order (relative paths) */
static void nsHeaderFiles(FcCtx* c, const char* root, FcList* out) {
    FcList* queue = fcListNew(c);
    int head = 0;
    fcListAdd(c, queue, (void*)"");
    while (head < queue->count) {
        const char* rel = (const char*)queue->items[head++];
        const char* dir = *rel ? nbCombine(c, root, rel) : root;
        DIR* d = opendir(dir);
        struct dirent* e;
        if (!d) continue;
        while ((e = readdir(d)) != NULL) {
            const char* name;
            const char* full;
            if (streq0(e->d_name, ".") || streq0(e->d_name, "..")) continue;
            name = *rel ? fcFmt(c, "%s/%s", rel, e->d_name) : fcStr(c, e->d_name);
            full = nbCombine(c, root, name);
            if (nbDirExists(full)) fcListAdd(c, queue, (void*)name);
            else if (strstr(e->d_name, ".h")) fcListAdd(c, out, (void*)name);
        }
        closedir(d);
    }
}

/* OriginsOf: where each C function is defined - the runtime, the C of a package, the generated code ("fire: helpers") */
static FcDict* nsOriginsOf(FcCtx* c, const char* workDir, const char* cFile) {
    FcDict* where = fcDictNew(c, FC_KEY_STR);
    FcList* files = fcListNew(c);
    FcList* lines;
    FcList* stack = fcListNew(c);
    int f, i;
    nsHeaderFiles(c, workDir, files);
    for (f = 0; f < files->count; f++) {
        const char* rel = (const char*)files->items[f];
        const char* origin;
        FcList* hl = nsReadLines(c, nbCombine(c, workDir, rel));
        if (fcStartsWith(rel, "pkg/")) { FcList* parts = fgSplit(c, rel, '/'); origin = fcFmt(c, "C of package %s", (const char*)parts->items[1]); }
        else origin = fcStartsWith(rel, "platform/") || fcStartsWith(rel, "cc/") ? "runtime (platform)" : "runtime";
        if (!hl) continue;
        for (i = 0; i < hl->count; i++) { const char* name = nsDefinitionAt(c, hl, i); if (name && !fcDictHas(where, name)) fcDictPut(where, name, (void*)origin); }
    }
    lines = nsReadLines(c, cFile);
    if (!lines) lines = fcListNew(c);
    for (i = 0; i < lines->count; i++) {
        const char* l = (const char*)lines->items[i];
        const char* name;
        /* start: ^// ---- (?:package )?(\S+) [^,]*, import "([^"]+)" */
        if (fcStartsWith(l, "// ---- end of ")) { if (stack->count > 0) stack->count--; continue; }
        if (fcStartsWith(l, "// ---- ")) {
            const char* p = l + 8;
            const char* q;
            const char* comma;
            if (fcStartsWith(p, "package ") && p[8] && !nsSpace((unsigned char)p[8])) {
                /* (the optional word: taken when the rest matches after it, else \S+ takes "package") */
                const char* tryAfter = p + 8;
                q = tryAfter; while (*q && !nsSpace((unsigned char)*q)) q++;
                if (*q == ' ' && (comma = strchr(q + 1, ',')) != NULL && fcStartsWith(comma, ", import \"") && comma[10] && comma[10] != '"' && strchr(comma + 10, '"')) p = tryAfter;
            }
            q = p; while (*q && !nsSpace((unsigned char)*q)) q++;
            if (q > p && *q == ' ' && (comma = strchr(q + 1, ',')) != NULL && fcStartsWith(comma, ", import \"")) {
                const char* s = comma + 10;
                const char* e = strchr(s, '"');
                if (e && e > s) { fcListAdd(c, stack, (void*)fcFmt(c, "C of package %s", fcStrN(c, s, (size_t)(e - s)))); continue; }
            }
        }
        if ((name = nsDefinitionAt(c, lines, i)) != NULL) fcDictPut(where, name, (void*)(stack->count > 0 ? (const char*)stack->items[stack->count - 1] : "fire: helpers"));
    }
    return where;
}

static bool nsHex(const char* s, const char** end, long long* v) {
    const char* p = s;
    *v = 0;
    while ((*p >= '0' && *p <= '9') || (*p >= 'a' && *p <= 'f')) { *v = *v * 16 + (*p <= '9' ? *p - '0' : *p - 'a' + 10); p++; }
    *end = p;
    return p > s;
}
static bool nsSectionName(const char* s, const char** end) {
    const char* p = s;
    if (*p != '.') return false;
    p++;
    while (nsWordChar((unsigned char)*p) || *p == '.' || *p == '$') p++;
    *end = p;
    return p > s + 1;
}
/* gnu: ^ (\.[\w.$]+)\s+0x[0-9a-f]+\s+0x([0-9a-f]+)\s+(.*)$ ; gnuNext: ^\s+0x[0-9a-f]+\s+0x([0-9a-f]+)\s+(.*)$ (from `p`, after the name) */
static bool nsAddrSize(const char* p, bool needSpace, long long* size, const char** file) {
    const char* q;
    long long addr;
    if (needSpace) { if (!nsSpace((unsigned char)*p)) return false; }
    while (nsSpace((unsigned char)*p)) p++;
    if (!fcStartsWith(p, "0x") || !nsHex(p + 2, &q, &addr)) return false;
    p = q;
    if (!nsSpace((unsigned char)*p)) return false;
    while (nsSpace((unsigned char)*p)) p++;
    if (!fcStartsWith(p, "0x") || !nsHex(p + 2, &q, size)) return false;
    p = q;
    if (!nsSpace((unsigned char)*p)) return false;
    while (nsSpace((unsigned char)*p)) p++;
    *file = p;
    return true;
}
static void nsAddData(FcCtx* c, FcList* data, const char* key, long long n) {
    int i;
    for (i = 0; i < data->count; i++) if (streq0(((NsOrigin*)data->items[i])->name, key)) { ((NsOrigin*)data->items[i])->bytes += n; return; }
    {
        NsOrigin* o = (NsOrigin*)fcAlloc(c, sizeof *o);
        o->name = key; o->bytes = n;
        fcListAdd(c, data, o);
    }
}
/* ReadMap: the code sections (function, bytes, file) and the data of a map of Open Watcom or of GNU ld */
static void nsReadMap(FcCtx* c, const char* mapFile, FcList* code, FcList* data) {
    FcList* lines = nsReadLines(c, mapFile);
    bool inSegments = false, discarded = false;
    int i;
    if (!lines) return;
    for (i = 0; i < lines->count; i++) {
        const char* l = (const char*)lines->items[i];
        const char* section = NULL;
        long long bytes = 0;
        const char* file = "";
        const char* e;
        if (fcStartsWith(l, "Discarded input sections")) { discarded = true; continue; }
        if (discarded) { if (fcStartsWith(l, "Memory Configuration")) discarded = false; continue; }
        if (strstr(l, "|   Segments   |")) { inSegments = true; continue; }
        if (inSegments && strstr(l, "|   Memory Map   |")) inSegments = false;
        if (inSegments) {
            /* watcom: ^(\S+)\s+(\w+)\s+(\w+)\s+[0-9a-f]{4}:[0-9a-f]{4}\s+([0-9a-f]{8})\s*$ */
            const char* p = l;
            const char* f[3][2];
            int k;
            long long size;
            bool okw = true;
            for (k = 0; k < 3 && okw; k++) {
                const char* s = p;
                if (k == 0) { while (*p && !nsSpace((unsigned char)*p)) p++; }
                else { while (nsWordChar((unsigned char)*p) && (unsigned char)*p < 0x80) p++; }
                if (p == s || !nsSpace((unsigned char)*p)) okw = false;
                f[k][0] = s; f[k][1] = p;
                while (nsSpace((unsigned char)*p)) p++;
            }
            if (okw) {
                int h;
                for (h = 0; h < 9 && okw; h++) { char ch = p[h]; if (h == 4) okw = ch == ':'; else okw = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'); }
                p += 9;
                if (okw && !nsSpace((unsigned char)*p)) okw = false;
                while (okw && nsSpace((unsigned char)*p)) p++;
                for (h = 0; okw && h < 8; h++) okw = (p[h] >= '0' && p[h] <= '9') || (p[h] >= 'a' && p[h] <= 'f');
                if (okw) { const char* q; nsHex(p, &q, &size); okw = q == p + 8; p = q; while (okw && nsSpace((unsigned char)*p)) p++; if (*p) okw = false; }
            }
            if (!okw) continue;
            {
                const char* seg = fcStrN(c, f[0][0], (size_t)(f[0][1] - f[0][0]));
                const char* cls = fcStrN(c, f[1][0], (size_t)(f[1][1] - f[1][0]));
                const char* grp = fcStrN(c, f[2][0], (size_t)(f[2][1] - f[2][0]));
                if (streq0(cls, "CODE")) {
                    NsSection* s = (NsSection*)fcAlloc(c, sizeof *s);
                    size_t sl = strlen(seg);
                    s->name = sl >= 5 && streq0(seg + sl - 5, "_TEXT") ? fcStrN(c, seg, sl - 5) : seg;
                    s->size = size; s->file = "";
                    fcListAdd(c, code, s);
                }
                else nsAddData(c, data, streq0(cls, "STACK") ? "stack" : streq0(grp, "DGROUP") ? "DGROUP (near data)" : streq0(cls, "FAR_DATA") ? "far data" : cls, size);
            }
            continue;
        }
        if (l[0] == ' ' && nsSectionName(l + 1, &e)) {
            if (nsAddrSize(e, true, &bytes, &file)) section = fcStrN(c, l + 1, (size_t)(e - l - 1));
            else {
                /* gnuName: ^ (\.[\w.$]+)\s*$ and the next line gnuNext */
                const char* t = e;
                while (nsSpace((unsigned char)*t)) t++;
                if (!*t && i + 1 < lines->count && nsAddrSize((const char*)lines->items[i + 1], true, &bytes, &file)) { section = fcStrN(c, l + 1, (size_t)(e - l - 1)); i++; }
            }
        }
        if (!section || bytes == 0) continue;
        {
            size_t fl = strlen(file);
            while (fl > 0 && nsSpace((unsigned char)file[fl - 1])) fl--;
            file = fcStrN(c, file, fl);
        }
        if (fcStartsWith(section, ".text.") || streq0(section, ".text")) {
            NsSection* s = (NsSection*)fcAlloc(c, sizeof *s);
            s->name = streq0(section, ".text") ? "" : section + 6; s->size = bytes; s->file = file;
            fcListAdd(c, code, s);
        }
        else if (fcStartsWith(section, ".rodata")) nsAddData(c, data, "read-only data", bytes);
        else if (fcStartsWith(section, ".data")) nsAddData(c, data, "data", bytes);
        else if (fcStartsWith(section, ".bss")) nsAddData(c, data, "bss", bytes);
    }
}

/* BaseName: a C body by address (`fN_c_p`, `fN_b`, `fN_c`, `fN_p`): fN.  ^(.*f\d+)(?:_[bcp]|_c_p)$ */
static const char* nsBaseName(FcCtx* c, const char* fn) {
    size_t n = strlen(fn), cut = 0;
    if (n >= 4 && streq0(fn + n - 4, "_c_p")) cut = 4;
    else if (n >= 2 && fn[n - 2] == '_' && (fn[n - 1] == 'b' || fn[n - 1] == 'c' || fn[n - 1] == 'p')) cut = 2;
    if (cut) {
        size_t m = n - cut, d = m;
        while (d > 0 && fn[d - 1] >= '0' && fn[d - 1] <= '9') d--;
        if (d < m && d > 0 && fn[d - 1] == 'f') return fcStrN(c, fn, m);
        /* (the alternation: `_c_p` failing, `_p` is tried) */
        if (cut == 4) {
            m = n - 2; d = m;
            while (d > 0 && fn[d - 1] >= '0' && fn[d - 1] <= '9') d--;
            if (d < m && d > 0 && fn[d - 1] == 'f') return fcStrN(c, fn, m);
        }
    }
    return fn;
}
/* HelperKind: ^([a-zA-Z]+?)(?:_|[A-Z0-9]) */
static const char* nsHelperKind(FcCtx* c, const char* name) {
    size_t i;
    for (i = 1; name[i - 1] && ((name[i - 1] >= 'a' && name[i - 1] <= 'z') || (name[i - 1] >= 'A' && name[i - 1] <= 'Z')); i++) {
        char ch = name[i];
        if (ch == '_' || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) return fcStrN(c, name, i);
    }
    return name;
}
/* fire_m(\w+?)(?:_[0-9a-f]+)?\.a\(  - the module of a library of linkage "static" in the file of a section; NULL: none */
static const char* nsLibraryOf(FcCtx* c, const char* file) {
    const char* p;
    for (p = strstr(file, "fire_m"); p; p = strstr(p + 1, "fire_m")) {
        const char* s = p + 6;
        const char* e = s;
        while (nsWordChar((unsigned char)*e) && (unsigned char)*e < 0x80) e++;
        /* the shortest \w+ after which (?:_[0-9a-f]+)?\.a\( matches */
        {
            const char* t;
            for (t = s + 1; t <= e; t++) {
                const char* u = t;
                if (fcStartsWith(u, ".a(")) return fcStrN(c, s, (size_t)(t - s));
                if (*u == '_') {
                    const char* h = u + 1;
                    while ((*h >= '0' && *h <= '9') || (*h >= 'a' && *h <= 'f')) h++;
                    if (h > u + 1 && fcStartsWith(h, ".a(")) return fcStrN(c, s, (size_t)(t - s));
                }
            }
        }
    }
    return NULL;
}

static long long* nsCountRef(FcCtx* c, FcList* counts, const char* key) {
    int i;
    NsCount* n;
    for (i = 0; i < counts->count; i++) if (streq0(((NsCount*)counts->items[i])->key, key)) return &((NsCount*)counts->items[i])->n;
    n = (NsCount*)fcAlloc(c, sizeof *n);
    n->key = key;
    fcListAdd(c, counts, n);
    return &n->n;
}
/* OrderByDescending: stable */
static int nsCmpOriginDesc(const void* a, const void* b) { long long x = (*(NsOrigin* const*)a)->bytes, y = (*(NsOrigin* const*)b)->bytes; return x < y ? 1 : x > y ? -1 : 0; }
static int nsCmpTypingDesc(const void* a, const void* b) { long long x = (*(NsTyping* const*)a)->bytes, y = (*(NsTyping* const*)b)->bytes; return x < y ? 1 : x > y ? -1 : 0; }
static int nsCmpCountDesc(const void* a, const void* b) { long long x = (*(NsCount* const*)a)->n, y = (*(NsCount* const*)b)->n; return x < y ? 1 : x > y ? -1 : 0; }
static int nsCmpBiggestDesc(const void* a, const void* b) { long long x = (*(NsBiggest* const*)a)->bytes, y = (*(NsBiggest* const*)b)->bytes; return x < y ? 1 : x > y ? -1 : 0; }

/* DosLoadBytes: a DOS program (MZ without a PE part): the bytes it takes when it is loaded; false otherwise */
static bool nsDosLoadBytes(const char* exe, long long* out) {
    unsigned char h[0x40];
    FILE* f = fopen(exe, "rb");
    size_t got;
    long long length;
    int last, pages, header, min;
    long lfanew;
    if (!f) return false;
    memset(h, 0, sizeof h);
    got = fread(h, 1, sizeof h, f);
    if (got < 0x1C || h[0] != 'M' || h[1] != 'Z') { fclose(f); return false; }
    fseek(f, 0, SEEK_END);
    length = ftell(f);
    last = h[2] | h[3] << 8; pages = h[4] | h[5] << 8; header = h[8] | h[9] << 8; min = h[10] | h[11] << 8;
    lfanew = (long)(h[0x3C] | h[0x3D] << 8 | h[0x3E] << 16 | (long)h[0x3F] << 24);
    if (lfanew > 0 && lfanew < length - 2) {
        int a, b;
        fseek(f, lfanew, SEEK_SET);
        a = fgetc(f); b = fgetc(f);
        if ((a == 'P' && b == 'E') || (a == 'N' && b == 'E') || (a == 'L' && b == 'E')) { fclose(f); return false; }
    }
    fclose(f);
    *out = (long long)pages * 512 - (last == 0 ? 0 : 512 - last) - header * 16LL + min * 16LL;
    return true;
}

/* Analyze: the report of the program exe built from the C files in workDir (cFile the program) */
static NsReport* nsAnalyze(FcCtx* c, const char* target, const char* exe, const char* workDir, const char* cFile, const char* mapFile, FcList* funcs) {
    NsReport* r = (NsReport*)fcAlloc(c, sizeof *r);
    FcDict* origins = nsOriginsOf(c, workDir, cFile);
    FcList* code = fcListNew(c);
    FcList* data = fcListNew(c);
    FcDict* byFunc = fcDictNew(c, FC_KEY_STR);
    FcList* codeBy = fcListNew(c);        /* NsOrigin* (insertion order) */
    FcList* moduleBytes = fcListNew(c);   /* NsCount* */
    FcList* generated = fcListNew(c);
    FcList* biggest = fcListNew(c);
    int i, k;
    struct stat st;
    nsReadMap(c, mapFile, code, data);
    for (i = 0; i < funcs->count; i++) fcDictPut(byFunc, ((FgFuncStat*)funcs->items[i])->cname, funcs->items[i]);
    for (i = 0; i < code->count; i++) {
        NsSection* sec = (NsSection*)code->items[i];
        const char* dot = strchr(sec->name, '.');
        const char* name = dot ? fcStrN(c, sec->name, (size_t)(dot - sec->name)) : sec->name;   /* (gcc: `f.isra.0`, `f.part.1` are parts of f) */
        const char* fn = nsBaseName(c, name);
        const char* origin;
        const char* lib;
        FgFuncStat* stat = (FgFuncStat*)fcDictGet(byFunc, fn);
        NsOrigin* o = NULL;
        NsBiggest* b;
        if (stat) origin = fcFmt(c, "fire: %s", stat->module);
        else if ((lib = nsLibraryOf(c, sec->file)) != NULL) {
            const char* ro = (const char*)fcDictGet(origins, name);
            origin = fcStartsWith(name, "fx_") ? fcFmt(c, "fire: %s (library)", lib) : ro && fcStartsWith(ro, "runtime") ? fcFmt(c, "%s (copy in %s)", ro, lib) : fcFmt(c, "fire: helpers of %s (library)", lib);
        }
        else { const char* ro = (const char*)fcDictGet(origins, name); origin = ro ? ro : "C library"; }
        if (stat) *nsCountRef(c, moduleBytes, stat->module) += sec->size;
        if (streq0(origin, "fire: helpers")) *nsCountRef(c, generated, nsHelperKind(c, name)) += (int)sec->size;
        for (k = 0; k < codeBy->count; k++) if (streq0(((NsOrigin*)codeBy->items[k])->name, origin)) o = (NsOrigin*)codeBy->items[k];
        if (!o) { o = (NsOrigin*)fcAlloc(c, sizeof *o); o->name = origin; fcListAdd(c, codeBy, o); }
        o->bytes += sec->size; o->functions++;
        b = (NsBiggest*)fcAlloc(c, sizeof *b);
        b->cname = *name ? name : "_TEXT"; b->what = stat ? stat->fireName : ""; b->origin = origin; b->bytes = sec->size;
        fcListAdd(c, biggest, b);
    }
    /* (funcs.GroupBy(Module): in the order of the first function of each module) */
    r->typing = fcListNew(c);
    for (i = 0; i < funcs->count; i++) {
        FgFuncStat* f = (FgFuncStat*)funcs->items[i];
        NsTyping* t = NULL;
        for (k = 0; k < r->typing->count; k++) if (streq0(((NsTyping*)r->typing->items[k])->module, f->module)) t = (NsTyping*)r->typing->items[k];
        if (!t) {
            t = (NsTyping*)fcAlloc(c, sizeof *t);
            t->module = f->module;
            for (k = 0; k < moduleBytes->count; k++) if (streq0(((NsCount*)moduleBytes->items[k])->key, f->module)) t->bytes = ((NsCount*)moduleBytes->items[k])->n;
            fcListAdd(c, r->typing, t);
        }
        t->functions++;
        if (!f->noCAbi) t->withCAbi++;
        t->params += f->params; t->cparams += f->cparams; t->valueLocals += f->valueLocals; t->cLocals += f->cLocals; t->stackSlots += f->stackSlots; t->shadows += f->shadows;
    }
    nbStableSort(r->typing, nsCmpTypingDesc);
    r->noCAbi = fcListNew(c);
    for (i = 0; i < funcs->count; i++) { FgFuncStat* f = (FgFuncStat*)funcs->items[i]; if (f->noCAbi) (*nsCountRef(c, r->noCAbi, f->noCAbi))++; }
    nbStableSort(r->noCAbi, nsCmpCountDesc);
    r->target = target;
    r->fileBytes = stat(exe, &st) == 0 ? (long long)st.st_size : 0;
    r->hasLoad = nsDosLoadBytes(exe, &r->loadBytes);
    nbStableSort(codeBy, nsCmpOriginDesc);
    r->code = codeBy;
    nbStableSort(data, nsCmpOriginDesc);
    r->data = data;
    nbStableSort(biggest, nsCmpBiggestDesc);
    if (biggest->count > 25) biggest->count = 25;
    r->biggest = biggest;
    nbStableSort(generated, nsCmpCountDesc);
    r->generated = generated;
    r->functions = funcs;
    return r;
}

/* .NET's custom numeric format of a double ("0", "0.0"): 15 significant digits, then rounded half away from zero at the place */
static const char* nsFixed(FcCtx* c, double v, int decimals) {
    char digits[40];
    char buf[64];
    int exp10, nd, point, i, keep;
    bool neg = v < 0;
    char* e;
    FcBuf b = {0};
    if (neg) v = -v;
    snprintf(buf, sizeof buf, "%.14e", v);   /* d.dddddddddddddde+XX */
    e = strchr(buf, 'e');
    exp10 = atoi(e + 1);
    nd = 0;
    for (i = 0; buf + i < e; i++) if (buf[i] >= '0' && buf[i] <= '9') digits[nd++] = buf[i];
    /* the value is 0.d1d2... * 10^(exp10+1): digits before the point = exp10 + 1 */
    point = exp10 + 1;
    keep = point + decimals;   /* how many digits stay */
    if (keep < 0) { nd = 0; point = 0; }
    else if (keep < nd) {
        bool up = digits[keep] >= '5';
        nd = keep;
        for (i = nd - 1; up && i >= 0; i--) { if (digits[i] == '9') digits[i] = '0'; else { digits[i]++; up = false; } }
        if (up) { memmove(digits + 1, digits, (size_t)nd); digits[0] = '1'; nd++; point++; }
    }
    fcBufStr(&b, "");
    {
        bool any = false;
        for (i = 0; i < nd; i++) if (digits[i] != '0') any = true;
        if (neg && any) fcBufChar(&b, '-');
    }
    if (point <= 0) fcBufChar(&b, '0');
    else for (i = 0; i < point; i++) fcBufChar(&b, i < nd ? digits[i] : '0');
    if (decimals > 0) {
        fcBufChar(&b, '.');
        for (i = 0; i < decimals; i++) { int at = point + i; fcBufChar(&b, at >= 0 && at < nd ? digits[at] : '0'); }
    }
    return fcBufDone(c, &b);
}
static const char* nsK(FcCtx* c, long long b) { return b >= 10240 ? fcFmt(c, "%s KB", nsFixed(c, b / 1024.0, 0)) : fcFmt(c, "%lld B", b); }
/* {x,n}: right-aligned in n, {x,-n}: left-aligned */
static const char* nsPad(FcCtx* c, const char* s, int width) {
    int n = (int)fc8Units(s);
    if (width > 0 && n < width) return fcFmt(c, "%*s%s", width - n, "", s);
    if (width < 0 && n < -width) return fcFmt(c, "%s%*s", s, -width - n, "");
    return s;
}

/* Text: the report as text */
static const char* nsText(FcCtx* c, const NsReport* r) {
    FcBuf sb = {0};
    long long total = 0;
    int i;
    fcBufFmt(&sb, "Statistics (%s): program %s", r->target, nsK(c, r->fileBytes));
    if (r->hasLoad) fcBufFmt(&sb, ", loaded %s%s", nsK(c, r->loadBytes), r->loadBytes > 640 * 1024 ? " - more than the 640 KB of conventional memory" : "");
    fcBufChar(&sb, '\n');
    for (i = 0; i < r->code->count; i++) total += ((NsOrigin*)r->code->items[i])->bytes;
    fcBufFmt(&sb, "  code %s:\n", nsK(c, total));
    for (i = 0; i < r->code->count; i++) {
        NsOrigin* o = (NsOrigin*)r->code->items[i];
        fcBufFmt(&sb, "    %s  %s%%  %s (%d functions)\n", nsPad(c, fcFmt(c, "%lld", o->bytes), 9), nsPad(c, nsFixed(c, 100.0 * (double)o->bytes / (double)(total > 1 ? total : 1), 1), 5), o->name, o->functions);
    }
    if (r->generated->count > 0) {
        fcBufStr(&sb, "  generated helpers: ");
        for (i = 0; i < r->generated->count && i < 8; i++) { NsCount* n = (NsCount*)r->generated->items[i]; fcBufFmt(&sb, "%s%s %s", i ? ", " : "", n->key, nsK(c, n->n)); }
        fcBufChar(&sb, '\n');
    }
    if (r->data->count > 0) {
        fcBufStr(&sb, "  data: ");
        for (i = 0; i < r->data->count; i++) { NsOrigin* o = (NsOrigin*)r->data->items[i]; fcBufFmt(&sb, "%s%s %s", i ? ", " : "", o->name, nsK(c, o->bytes)); }
        fcBufChar(&sb, '\n');
    }
    fcBufStr(&sb, "  fire functions by module (C numbers / all):\n");
    for (i = 0; i < r->typing->count; i++) {
        NsTyping* m = (NsTyping*)r->typing->items[i];
        fcBufFmt(&sb, "    %s %s functions %s  C interface %d/%d  parameters %d/%d  variables %d/%d  stack slots %d (+%d C shadows)\n", nsPad(c, m->module, -18),
            nsPad(c, fcFmt(c, "%d", m->functions), 4), nsPad(c, nsK(c, m->bytes), 8), m->withCAbi, m->functions, m->cparams, m->params, m->cLocals, m->cLocals + m->valueLocals, m->stackSlots, m->shadows);
    }
    if (r->noCAbi->count > 0) {
        fcBufStr(&sb, "  no C interface: ");
        for (i = 0; i < r->noCAbi->count; i++) { NsCount* n = (NsCount*)r->noCAbi->items[i]; fcBufFmt(&sb, "%s%s (%lld)", i ? ", " : "", n->key, n->n); }
        fcBufChar(&sb, '\n');
    }
    fcBufStr(&sb, "  biggest functions:\n");
    for (i = 0; i < r->biggest->count && i < 15; i++) {
        NsBiggest* b = (NsBiggest*)r->biggest->items[i];
        fcBufFmt(&sb, "    %s  %s%s%s  [%s]\n", nsPad(c, fcFmt(c, "%lld", b->bytes), 9), b->cname, *b->what ? " " : "", b->what, b->origin);
    }
    return fcBufDone(c, &sb);
}

/* JSON: a string as System.Text.Json writes it with the default encoder (JavaScriptEncoder.Default: what is not safe ASCII, and " \ < > & ' + `, as \uXXXX) */
static void nsJsonString(FcBuf* b, const char* s) {
    const unsigned char* p = (const unsigned char*)s;
    fcBufChar(b, '"');
    while (*p) {
        uint16_t u = fc8Next(&p);
        switch (u) {
            case '\n': fcBufStr(b, "\\n"); break;
            case '\r': fcBufStr(b, "\\r"); break;
            case '\t': fcBufStr(b, "\\t"); break;
            case '\b': fcBufStr(b, "\\b"); break;
            case '\f': fcBufStr(b, "\\f"); break;
            case '"': fcBufStr(b, "\\u0022"); break;
            case '\\': fcBufStr(b, "\\\\"); break;
            default:
                if (u < 0x20 || u >= 0x7F || u == '<' || u == '>' || u == '&' || u == '\'' || u == '+' || u == '`') fcBufFmt(b, "\\u%04X", u);
                else fcBufChar(b, (char)u);
        }
    }
    fcBufChar(b, '"');
}
static void nsJsonIndent(FcBuf* b, int depth) { int i; fcBufChar(b, '\n'); for (i = 0; i < depth; i++) fcBufStr(b, "  "); }
static void nsJsonKey(FcBuf* b, int depth, bool first, const char* key) { if (!first) fcBufChar(b, ','); nsJsonIndent(b, depth); nsJsonString(b, key); fcBufStr(b, ": "); }
static const char* nsJson(FcCtx* c, const NsReport* r) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "{");
    nsJsonKey(&b, 1, true, "Target"); nsJsonString(&b, r->target);
    nsJsonKey(&b, 1, false, "FileBytes"); fcBufFmt(&b, "%lld", r->fileBytes);
    nsJsonKey(&b, 1, false, "LoadBytes"); if (r->hasLoad) fcBufFmt(&b, "%lld", r->loadBytes); else fcBufStr(&b, "null");
#define NS_ORIGINS(KEY, LIST) \
    nsJsonKey(&b, 1, false, KEY); \
    if ((LIST)->count == 0) fcBufStr(&b, "[]"); \
    else { \
        fcBufChar(&b, '['); \
        for (i = 0; i < (LIST)->count; i++) { \
            NsOrigin* o = (NsOrigin*)(LIST)->items[i]; \
            if (i) fcBufChar(&b, ','); \
            nsJsonIndent(&b, 2); fcBufChar(&b, '{'); \
            nsJsonKey(&b, 3, true, "Name"); nsJsonString(&b, o->name); \
            nsJsonKey(&b, 3, false, "Bytes"); fcBufFmt(&b, "%lld", o->bytes); \
            nsJsonKey(&b, 3, false, "Functions"); fcBufFmt(&b, "%d", o->functions); \
            nsJsonIndent(&b, 2); fcBufChar(&b, '}'); \
        } \
        nsJsonIndent(&b, 1); fcBufChar(&b, ']'); \
    }
    NS_ORIGINS("Code", r->code);
    NS_ORIGINS("Data", r->data);
#undef NS_ORIGINS
    nsJsonKey(&b, 1, false, "Typing");
    if (r->typing->count == 0) fcBufStr(&b, "[]");
    else {
        fcBufChar(&b, '[');
        for (i = 0; i < r->typing->count; i++) {
            NsTyping* m = (NsTyping*)r->typing->items[i];
            if (i) fcBufChar(&b, ',');
            nsJsonIndent(&b, 2); fcBufChar(&b, '{');
            nsJsonKey(&b, 3, true, "Module"); nsJsonString(&b, m->module);
            nsJsonKey(&b, 3, false, "Functions"); fcBufFmt(&b, "%d", m->functions);
            nsJsonKey(&b, 3, false, "Bytes"); fcBufFmt(&b, "%lld", m->bytes);
            nsJsonKey(&b, 3, false, "WithCAbi"); fcBufFmt(&b, "%d", m->withCAbi);
            nsJsonKey(&b, 3, false, "Params"); fcBufFmt(&b, "%d", m->params);
            nsJsonKey(&b, 3, false, "CParams"); fcBufFmt(&b, "%d", m->cparams);
            nsJsonKey(&b, 3, false, "ValueLocals"); fcBufFmt(&b, "%d", m->valueLocals);
            nsJsonKey(&b, 3, false, "CLocals"); fcBufFmt(&b, "%d", m->cLocals);
            nsJsonKey(&b, 3, false, "StackSlots"); fcBufFmt(&b, "%d", m->stackSlots);
            nsJsonKey(&b, 3, false, "Shadows"); fcBufFmt(&b, "%d", m->shadows);
            nsJsonIndent(&b, 2); fcBufChar(&b, '}');
        }
        nsJsonIndent(&b, 1); fcBufChar(&b, ']');
    }
#define NS_COUNTS(KEY, LIST) \
    nsJsonKey(&b, 1, false, KEY); \
    if ((LIST)->count == 0) fcBufStr(&b, "{}"); \
    else { \
        fcBufChar(&b, '{'); \
        for (i = 0; i < (LIST)->count; i++) { NsCount* n = (NsCount*)(LIST)->items[i]; nsJsonKey(&b, 2, i == 0, n->key); fcBufFmt(&b, "%lld", n->n); } \
        nsJsonIndent(&b, 1); fcBufChar(&b, '}'); \
    }
    NS_COUNTS("NoCAbi", r->noCAbi);
    nsJsonKey(&b, 1, false, "Biggest");
    if (r->biggest->count == 0) fcBufStr(&b, "[]");
    else {
        fcBufChar(&b, '[');
        for (i = 0; i < r->biggest->count; i++) {
            NsBiggest* x = (NsBiggest*)r->biggest->items[i];
            if (i) fcBufChar(&b, ',');
            nsJsonIndent(&b, 2); fcBufChar(&b, '{');
            nsJsonKey(&b, 3, true, "CName"); nsJsonString(&b, x->cname);
            nsJsonKey(&b, 3, false, "What"); nsJsonString(&b, x->what);
            nsJsonKey(&b, 3, false, "Origin"); nsJsonString(&b, x->origin);
            nsJsonKey(&b, 3, false, "Bytes"); fcBufFmt(&b, "%lld", x->bytes);
            nsJsonIndent(&b, 2); fcBufChar(&b, '}');
        }
        nsJsonIndent(&b, 1); fcBufChar(&b, ']');
    }
    NS_COUNTS("Generated", r->generated);
#undef NS_COUNTS
    nsJsonKey(&b, 1, false, "Functions");
    if (r->functions->count == 0) fcBufStr(&b, "[]");
    else {
        fcBufChar(&b, '[');
        for (i = 0; i < r->functions->count; i++) {
            FgFuncStat* f = (FgFuncStat*)r->functions->items[i];
            if (i) fcBufChar(&b, ',');
            nsJsonIndent(&b, 2); fcBufChar(&b, '{');
            nsJsonKey(&b, 3, true, "CName"); nsJsonString(&b, f->cname);
            nsJsonKey(&b, 3, false, "FireName"); nsJsonString(&b, f->fireName);
            nsJsonKey(&b, 3, false, "Module"); nsJsonString(&b, f->module);
            nsJsonKey(&b, 3, false, "Params"); fcBufFmt(&b, "%d", f->params);
            nsJsonKey(&b, 3, false, "CParams"); fcBufFmt(&b, "%d", f->cparams);
            nsJsonKey(&b, 3, false, "CReturn"); fcBufStr(&b, f->cReturn ? "true" : "false");
            nsJsonKey(&b, 3, false, "ValueLocals"); fcBufFmt(&b, "%d", f->valueLocals);
            nsJsonKey(&b, 3, false, "CLocals"); fcBufFmt(&b, "%d", f->cLocals);
            nsJsonKey(&b, 3, false, "StackSlots"); fcBufFmt(&b, "%d", f->stackSlots);
            nsJsonKey(&b, 3, false, "Shadows"); fcBufFmt(&b, "%d", f->shadows);
            nsJsonKey(&b, 3, false, "NoCAbi"); if (f->noCAbi) nsJsonString(&b, f->noCAbi); else fcBufStr(&b, "null");
            nsJsonIndent(&b, 2); fcBufChar(&b, '}');
        }
        nsJsonIndent(&b, 1); fcBufChar(&b, ']');
    }
    nsJsonIndent(&b, 0); fcBufChar(&b, '}');
    return fcBufDone(c, &b);
}

/* NativeBuilder.WriteStats: the report of the build into the log and next to the program (.stats.txt, .stats.json) */
static void nbWriteStats(FcCtx* c, const FgTarget* target, const char* exe, const char* workDir, const char* cFile, const char* mapFile, FcList* funcs, FcBuf* log) {
    NsReport* report;
    const char* text;
    if (!mapFile || !nbFileExists(mapFile)) { fcBufStr(log, "--stats: this toolchain writes no map of the program (Open Watcom, gcc and clang do).\n"); return; }
    report = nsAnalyze(c, target->name, exe, workDir, cFile, mapFile, funcs ? funcs : fcListNew(c));
    text = nsText(c, report);
    nbWriteText(c, fcFmt(c, "%s.stats.txt", exe), text);
    nbWriteText(c, fcFmt(c, "%s.stats.json", exe), nsJson(c, report));
    fcBufStr(log, text);
    fcBufFmt(log, "(the report: %s.stats.txt and .stats.json, the map: %s)\n", exe, mapFile);
}
