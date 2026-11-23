/* fire VM in C: fire plugins (docs/spec/NATIVE_BACKEND.md "Plugins", docs/spec/VM_C.md "Plugins") - VM.Plugins.cs of the C# VM. `Library.Load` of a
 * `.fireplugin` file (or of a native library with one next to it: `kit.so`, `kit.dll`, `kit.dylib`) reads the plugin's bytecode (PluginBytecode: "FIREPLG2", the
 * length of a JSON header, the header - its module, the interfaces of the modules it needs, its classes, its natives by number, its natives in C++ and their
 * libraries, what its `class extends` adds, its own sources -, then a program file); `__LibCall(5, handle)` links its classes into the running program:
 *
 *   - the interfaces of the modules it was built against must be the program's (ROADMAP 9.6, the hashes only: the program file carries the interfaces of its
 *     modules - ProgramFile flag 8; a program file without them is not checked, like a packed program of the C# VM);
 *   - its classes must be new to the program, its base classes are the program's (the copies it was compiled with have the same interface);
 *   - its natives in C++ the program does not have come from the libraries next to its bytecode (C ABI, like those of a package), the native calls of its
 *     code get the program's numbers (by name); code that uses variables of the top level or tryable/extern natives does not load;
 *   - what its `class extends` adds to classes of the program, of libraries and of base types: the methods (a method the class has already: refused);
 *   - its units and enums become the program's (by dimensions, scale, symbol, offset and the classes of their conversion functions; by name), the units a text
 *     may name join the program's;
 *   - its sources get the next source indexes of the program (the debugger hears of them: event `sources`, fire_debug.h);
 *   - its classes get the next class ids of the runtime (vmRegisterMore), then its static initialisers run in the order of their dependencies
 *     (CompiledProgram.StaticInitSequence); what they make belongs to the plugin's own global scope (docs/spec/PLUGINS.md) - also when a fire thread loads
 *     it; another Load meanwhile waits for them; lib.Unload() destroys the scope (its code stays).
 *
 * What the static fields hold joins the globals' domain with its scope (markOwnedShared; the strings need nothing: FIRE_STR_CROSS is off).
 *
 * The errors are the texts of the C# VM ("The plugin 'Kit' cannot be loaded: ..."), which `Library.Load` throws as a LibraryException. Its code is not
 * unloaded while its program runs (neither in the C# VM); everything it brought goes when the program ends (vmPluginsEnd). */

#define VM_PLUGIN_FORMAT 1          /* ModuleInterface.Format */

/* ---- the header (a small reader of the JSON that PluginBytecode.Write writes) ---- */
typedef struct VmStrs { const char** s; uint32_t n, cap; } VmStrs;
typedef struct VmPluginFile {
    char* path;                 /* as Library.Load got it */
    char* dir;                  /* the folder of its bytecode (the libraries of its natives lie there) */
    char* error;                /* why it cannot be read (then nothing else is set) */
    FbArena arena;
    const char* name;
    int64_t format;
    VmStrs deps;                /* pairs: module, interface */
    VmStrs classes;
    VmStrs natives;
    VmStrs pkgNatives, pkgLibs;
    VmStrs exts;                /* triples: class, method, number of parameters */
    VmStrs sources;             /* triples: index in its bytecode, file, hash (NULL: none) */
    uint8_t* bytes;             /* the whole file (the program file behind the header) */
    size_t len, body;
    int64_t handle;
    struct VmPluginFile* next;
} VmPluginFile;

/* (`initing`: its static initialisers run - on the strand `initBy`; another `Library.Load` of it waits for them, as the lock of the C# VM makes it.
 * `scope`: the plugin's own global scope - what its static fields hold (docs/spec/PLUGINS.md); `unloaded`: lib.Unload() destroyed it, a Load fills it again) */
typedef struct VmLinked { VmPluginFile* file; VmProgram* prog; VmClass** own; uint32_t nown; fire_atomic32 initing; Vm* initBy; OwnList scope; bool unloaded;
    struct VmLinked* next; } VmLinked;
static bool vmPlInitDone(void* ctx) { return fire_atomic_loadAcquire(&((VmLinked*)ctx)->initing) == 0; }

static struct {
    VmPluginFile* files;
    int64_t nextHandle;
    VmLinked* linked;           /* the plugins linked into the program that runs */
    int32_t addedSources;       /* the sources they added (after the program's own) */
    void** garbage;             /* tables that were replaced while the program ran (another thread may still have read the old one): freed at its end */
    uint32_t ngarbage, capGarbage;
} g_vmPl = { NULL, -1000, NULL, 0, NULL, 0, 0 };

static void vmPlGarbage(void* p) {
    if (!p) return;
    if (g_vmPl.ngarbage == g_vmPl.capGarbage) {
        g_vmPl.capGarbage = g_vmPl.capGarbage ? g_vmPl.capGarbage * 2 : 32;
        g_vmPl.garbage = (void**)realloc(g_vmPl.garbage, g_vmPl.capGarbage * sizeof(void*));
        if (!g_vmPl.garbage) outOfMemory();
    }
    g_vmPl.garbage[g_vmPl.ngarbage++] = p;
}

static void vmStrsAdd(FbArena* a, VmStrs* l, const char* s) {
    if (l->n == l->cap) {
        const char** grown;
        l->cap = l->cap ? l->cap * 2 : 16;
        grown = (const char**)fbAlloc(a, l->cap * sizeof(char*));
        if (l->n) memcpy(grown, l->s, l->n * sizeof(char*));
        l->s = grown;
    }
    l->s[l->n++] = s;
}

typedef struct VmJson { const char* p; const char* end; FbArena* a; bool bad; } VmJson;
static void vjWs(VmJson* j) { while (j->p < j->end && (*j->p == ' ' || *j->p == '\t' || *j->p == '\n' || *j->p == '\r')) j->p++; }
static bool vjIs(VmJson* j, char c) { vjWs(j); if (j->p < j->end && *j->p == c) { j->p++; return true; } return false; }
static void vjPutUtf8(char* out, size_t* n, uint32_t c) {
    if (c < 0x80) out[(*n)++] = (char)c;
    else if (c < 0x800) { out[(*n)++] = (char)(0xC0 | (c >> 6)); out[(*n)++] = (char)(0x80 | (c & 63)); }
    else if (c < 0x10000) { out[(*n)++] = (char)(0xE0 | (c >> 12)); out[(*n)++] = (char)(0x80 | ((c >> 6) & 63)); out[(*n)++] = (char)(0x80 | (c & 63)); }
    else { out[(*n)++] = (char)(0xF0 | (c >> 18)); out[(*n)++] = (char)(0x80 | ((c >> 12) & 63)); out[(*n)++] = (char)(0x80 | ((c >> 6) & 63)); out[(*n)++] = (char)(0x80 | (c & 63)); }
}
static uint32_t vjHex4(VmJson* j) {
    uint32_t v = 0;
    int i;
    for (i = 0; i < 4; i++) {
        char c = j->p < j->end ? *j->p++ : 0;
        v <<= 4;
        if (c >= '0' && c <= '9') v |= (uint32_t)(c - '0');
        else if (c >= 'a' && c <= 'f') v |= (uint32_t)(c - 'a' + 10);
        else if (c >= 'A' && c <= 'F') v |= (uint32_t)(c - 'A' + 10);
        else j->bad = true;
    }
    return v;
}
/* A string (its UTF-8, in the arena), NULL for `null`. */
static const char* vjStr(VmJson* j) {
    const char* start;
    char* out;
    size_t n = 0;
    vjWs(j);
    if (j->end - j->p >= 4 && !memcmp(j->p, "null", 4)) { j->p += 4; return NULL; }
    if (!vjIs(j, '"')) { j->bad = true; return NULL; }
    start = j->p;
    while (j->p < j->end && *j->p != '"') { if (*j->p == '\\') j->p++; j->p++; }
    out = (char*)fbAlloc(j->a, (size_t)(j->p - start) * 3 + 1);
    j->p = start;
    while (j->p < j->end && *j->p != '"') {
        char c = *j->p++;
        if (c != '\\') { out[n++] = c; continue; }
        c = j->p < j->end ? *j->p++ : 0;
        switch (c) {
            case 'n': out[n++] = '\n'; break;
            case 't': out[n++] = '\t'; break;
            case 'r': out[n++] = '\r'; break;
            case 'b': out[n++] = '\b'; break;
            case 'f': out[n++] = '\f'; break;
            case 'u': {
                uint32_t u = vjHex4(j);
                if (u >= 0xD800 && u < 0xDC00 && j->end - j->p >= 6 && j->p[0] == '\\' && j->p[1] == 'u') {
                    uint32_t lo;
                    j->p += 2;
                    lo = vjHex4(j);
                    u = 0x10000 + ((u - 0xD800) << 10) + (lo - 0xDC00);
                }
                vjPutUtf8(out, &n, u);
                break;
            }
            default: out[n++] = c; break;
        }
    }
    if (!vjIs(j, '"')) j->bad = true;
    out[n] = 0;
    return out;
}
static int64_t vjInt(VmJson* j) {
    int64_t v = 0;
    bool neg = false;
    vjWs(j);
    if (j->p < j->end && *j->p == '-') { neg = true; j->p++; }
    if (j->p >= j->end || *j->p < '0' || *j->p > '9') { j->bad = true; return 0; }
    while (j->p < j->end && *j->p >= '0' && *j->p <= '9') v = v * 10 + (*j->p++ - '0');
    return neg ? -v : v;
}
static void vjSkip(VmJson* j);
static void vjSkip(VmJson* j) {
    vjWs(j);
    if (j->p >= j->end) { j->bad = true; return; }
    if (*j->p == '"' || *j->p == 'n') { (void)vjStr(j); return; }
    if (*j->p == '{' || *j->p == '[') {
        char close = *j->p == '{' ? '}' : ']';
        j->p++;
        if (vjIs(j, close)) return;
        do {
            if (close == '}') { (void)vjStr(j); if (!vjIs(j, ':')) { j->bad = true; return; } }
            vjSkip(j);
        } while (!j->bad && vjIs(j, ','));
        if (!vjIs(j, close)) j->bad = true;
        return;
    }
    while (j->p < j->end && *j->p != ',' && *j->p != '}' && *j->p != ']') j->p++;   /* (a number, true, false) */
}
/* An array of strings (`flat`), or of arrays of strings (each `width` long: shorter ones are padded with NULL, longer ones cut). */
static void vjList(VmJson* j, VmStrs* out, int width) {
    if (!vjIs(j, '[')) { vjSkip(j); return; }
    if (vjIs(j, ']')) return;
    do {
        if (width == 0) { vmStrsAdd(j->a, out, vjStr(j)); continue; }
        if (!vjIs(j, '[')) { j->bad = true; return; }
        {
            int k = 0;
            if (!vjIs(j, ']')) {
                do { const char* s = vjStr(j); if (k++ < width) vmStrsAdd(j->a, out, s); } while (!j->bad && vjIs(j, ','));
                if (!vjIs(j, ']')) j->bad = true;
            }
            for (; k < width; k++) vmStrsAdd(j->a, out, NULL);
        }
    } while (!j->bad && vjIs(j, ','));
    if (!vjIs(j, ']')) j->bad = true;
}
static bool vmPlHeader(VmPluginFile* f, const char* text, size_t n) {
    VmJson j;
    j.p = text; j.end = text + n; j.a = &f->arena; j.bad = false;
    f->format = VM_PLUGIN_FORMAT;
    f->name = "";
    if (!vjIs(&j, '{')) return false;
    if (vjIs(&j, '}')) return true;
    do {
        const char* key = vjStr(&j);
        if (!key || !vjIs(&j, ':')) return false;
        if (!strcmp(key, "Name")) { const char* s = vjStr(&j); f->name = s ? s : ""; }
        else if (!strcmp(key, "Format")) f->format = vjInt(&j);
        else if (!strcmp(key, "Deps")) vjList(&j, &f->deps, 2);
        else if (!strcmp(key, "Classes")) vjList(&j, &f->classes, 0);
        else if (!strcmp(key, "Natives")) vjList(&j, &f->natives, 0);
        else if (!strcmp(key, "PackageNatives")) vjList(&j, &f->pkgNatives, 0);
        else if (!strcmp(key, "PackageLibraries")) vjList(&j, &f->pkgLibs, 0);
        else if (!strcmp(key, "Extensions")) vjList(&j, &f->exts, 3);
        else if (!strcmp(key, "Sources")) vjList(&j, &f->sources, 3);
        else vjSkip(&j);
    } while (!j.bad && vjIs(&j, ','));
    return !j.bad && vjIs(&j, '}');
}

static char* vmPlDup(const char* s) { size_t n = strlen(s); char* d = (char*)malloc(n + 1); if (!d) outOfMemory(); memcpy(d, s, n + 1); return d; }
static char* vmPlFmt(const char* fmt, const char* a) { char t[1400]; snprintf(t, sizeof t, fmt, a); return vmPlDup(t); }
static bool vmPlEndsWith(const char* s, const char* tail) {
    size_t n = strlen(s), m = strlen(tail), i;
    if (n < m) return false;
    for (i = 0; i < m; i++) { char c = s[n - m + i]; if (c >= 'A' && c <= 'Z') c = (char)(c + 32); if (c != tail[i]) return false; }
    return true;
}
static bool vmPlExists(const char* path) { FILE* f = vmFopen(path, "rb"); if (!f) return false; fclose(f); return true; }

/* The bytecode of a fire plugin that `path` names (the `.fireplugin` file, or a native library with one next to it); NULL: none. Malloc'd. */
static char* vmPluginFileFor(const char* path) {
    static const char* const exts[] = { ".so", ".dll", ".dylib" };
    size_t n = strlen(path);
    int i;
    if (vmPlEndsWith(path, ".fireplugin")) return vmPlExists(path) ? vmPlDup(path) : NULL;
    for (i = 0; i < 3; i++)
        if (vmPlEndsWith(path, exts[i])) {
            size_t m = strlen(exts[i]);
            char* bc = (char*)malloc(n - m + 12);
            if (!bc) outOfMemory();
            memcpy(bc, path, n - m);
            memcpy(bc + n - m, ".fireplugin", 12);
            if (vmPlExists(bc)) return bc;
            free(bc);
        }
    return NULL;
}

/* `Library.Load` of a fire plugin: its handle (0: no plugin there). The file is read now; what is wrong with it is said when it is linked. */
static int64_t vmPluginLoad(const char* path) {
    char* file = vmPluginFileFor(path);
    VmPluginFile* f;
    if (!file) return 0;
    f = (VmPluginFile*)calloc(1, sizeof(VmPluginFile));
    if (!f) outOfMemory();
    f->path = vmPlDup(path);
    {
        const char* slash = strrchr(file, '/');
#if defined(_WIN32)
        const char* back = strrchr(file, '\\');
        if (back && (!slash || back > slash)) slash = back;
#endif
        f->dir = slash ? (char*)malloc((size_t)(slash - file) + 1) : vmPlDup(".");
        if (!f->dir) outOfMemory();
        if (slash) { memcpy(f->dir, file, (size_t)(slash - file)); f->dir[slash - file] = 0; }
    }
    f->bytes = fbReadFile(file, &f->len);
    if (!f->bytes) f->error = vmPlFmt("The plugin '%s' cannot be read: the file cannot be opened.", path);
    else if (f->len < 12 || memcmp(f->bytes, "FIREPLG2", 8) != 0) f->error = vmPlFmt("The file '%s' is not a fire plugin.", path);
    else {
        uint32_t hl = (uint32_t)f->bytes[8] | ((uint32_t)f->bytes[9] << 8) | ((uint32_t)f->bytes[10] << 16) | ((uint32_t)f->bytes[11] << 24);
        if ((size_t)hl > f->len - 12 || !vmPlHeader(f, (const char*)f->bytes + 12, hl)) f->error = vmPlFmt("The plugin '%s' cannot be read: its header is damaged.", path);
        else if (!*f->name) f->error = vmPlFmt("The file '%s' is not a fire plugin.", path);
        f->body = 12 + (size_t)hl;
    }
    free(file);
    f->handle = --g_vmPl.nextHandle;
    f->next = g_vmPl.files;
    g_vmPl.files = f;
    return f->handle;
}

static VmPluginFile* vmPluginOf(int64_t handle) {
    VmPluginFile* f;
    for (f = g_vmPl.files; f; f = f->next) if (f->handle == handle) return f;
    return NULL;
}

/* ---- the native calls of its code: the program's numbers (Renumber) ---- */
static const char* const g_vmOpOperands[] = {
#define FIRE_OP(name, number, operands) operands,
#include "../tables/opcodes.def"
#undef FIRE_OP
};
#define VM_NOPS ((uint32_t)(sizeof g_vmOpOperands / sizeof g_vmOpOperands[0]))

/* The host's number of a native of the plugin (by name; -1: the program does not have it). */
static int32_t vmPlNativeIndex(const char* name) {
    uint32_t i;
    VmNative* e;
    if (!name) return -1;
    for (i = 0; i < VM_NNATIVES; i++) if (strcmp(g_vmNativeNames[i], name) == 0) return (int32_t)i;
    e = vmNativeNamed(name);
    return e ? (int32_t)(VM_NNATIVES + (uint32_t)(e - g_vmNat)) : -1;
}

static const char* vmPlRenumber(VmChunk* c, const VmPluginFile* f, char* why, size_t cap) {
    uint8_t* code = (uint8_t*)(uintptr_t)c->code;
    uint32_t ip = 0;
    while (ip < c->ncode) {
        uint32_t op = code[ip], at = ip, k;
        const char* sizes;
        uint32_t operands[8];
        if (op >= VM_NOPS) { snprintf(why, cap, "its code has an instruction this VM does not know (%u).", op); return why; }
        sizes = g_vmOpOperands[op];
        ip++;
        for (k = 0; sizes[k] && k < 8; k++) {
            if (op == OP_RegisterThreadsCatch && k == 2 && operands[1] == 0) break;
            operands[k] = sizes[k] == '1' ? code[ip] : (uint32_t)(code[ip] | (code[ip + 1] << 8));
            ip += sizes[k] == '1' ? 1u : 2u;
        }
        switch (op) {
            case OP_CallNative: {
                const char* name = operands[0] < f->natives.n ? f->natives.s[operands[0]] : NULL;
                int32_t index = vmPlNativeIndex(name);
                if (index < 0) {
                    char num[16];
                    snprintf(num, sizeof num, "%u", operands[0]);
                    snprintf(why, cap, "it calls the native function '%s', which this program does not have (an import the program lacks).", name ? name : num);
                    return why;
                }
                code[at + 1] = (uint8_t)(index & 0xFF);
                code[at + 2] = (uint8_t)(index >> 8);
                break;
            }
            case OP_CallTryableNative: case OP_CallExtern:
                return "its code calls natives that a plugin cannot call in the VM yet (tryable or extern functions).";
            case OP_LoadGlobal: case OP_StoreGlobal: case OP_StoreGlobalPop: case OP_AddressOfGlobal: case OP_ArithGlobalConstPop:
                return "its classes use variables of the top level of its file (a plugin has no top level).";
            default: break;
        }
    }
    return NULL;
}

/* (visited once per chunk: the chunks of the plugin carry a mark) */
typedef struct VmPlVisit { const VmPluginFile* f; VmChunk** seen; uint32_t nseen, capSeen; const char* problem; char why[400]; } VmPlVisit;
static bool vmPlSeen(VmPlVisit* v, VmChunk* c) {
    uint32_t i;
    for (i = 0; i < v->nseen; i++) if (v->seen[i] == c) return true;
    if (v->nseen == v->capSeen) {
        v->capSeen = v->capSeen ? v->capSeen * 2 : 256;
        v->seen = (VmChunk**)realloc(v->seen, v->capSeen * sizeof(VmChunk*));
        if (!v->seen) outOfMemory();
    }
    v->seen[v->nseen++] = c;
    return false;
}
static void vmPlVisit(VmPlVisit* v, VmProto* p) {
    uint32_t i;
    if (!p || v->problem || vmPlSeen(v, p->chunk)) return;
    v->problem = vmPlRenumber(p->chunk, v->f, v->why, sizeof v->why);
    for (i = 0; i < p->ndefaults; i++) vmPlVisit(v, p->defaults[i]);
    for (i = 0; i < p->chunk->nfn; i++) vmPlVisit(v, p->chunk->fn[i]);
    for (i = 0; i < p->nspecs; i++) vmPlVisit(v, p->specs[i]);
}
static void vmPlVisitClass(VmPlVisit* v, VmClass* c) {
    uint32_t i, k;
    for (i = 0; i < c->nfields; i++) vmPlVisit(v, c->fields[i].init);
    for (i = 0; i < c->nstatic; i++) vmPlVisit(v, c->sinit[i]);
    for (i = 0; i < c->nmethods; i++) for (k = 0; k < c->methods[i].nov; k++) vmPlVisit(v, c->methods[i].ov[k]);
    for (i = 0; i < c->nctors; i++) vmPlVisit(v, c->ctors[i].p);
    for (i = 0; i < c->nxctors; i++) vmPlVisit(v, c->xctors[i]);
    vmPlVisit(v, c->dtor);
}

/* An extension method of a plugin belongs to the class it extends: its access checks are that class's (Chunk.OwnerClass). */
static void vmPlOwnTo(VmProto* p, VmClass* c, int depth) {
    uint32_t i;
    if (!p || depth > 64) return;
    p->chunk->owner = c;
    p->owner = c;
    for (i = 0; i < p->chunk->nfn; i++) vmPlOwnTo(p->chunk->fn[i], c, depth + 1);
    for (i = 0; i < p->ndefaults; i++) vmPlOwnTo(p->defaults[i], c, depth + 1);
    for (i = 0; i < p->nspecs; i++) vmPlOwnTo(p->specs[i], c, depth + 1);
}

static bool vmPlHasClass(const VmPluginFile* f, const char* name) {
    uint32_t i;
    for (i = 0; i < f->classes.n; i++) if (f->classes.s[i] && strcmp(f->classes.s[i], name) == 0) return true;
    return false;
}

/* ---- its units and enums: the program's (new ones for those it does not have) ---- */
static int32_t vmPlConvIndex(VmProgram* hp, const char* cls) {
    uint32_t c;
    for (c = 0; c < hp->nconv; c++) if (hp->convClasses[c] == cls) return (int32_t)c;
    if (hp->nconv >= 16) return -2;
    hp->convClasses[hp->nconv] = cls;
    return (int32_t)hp->nconv++;
}
/* The places of its dimensions in the program's (new ones in free places); false when there are not enough. */
static bool vmPlDims(VmProgram* pp, int32_t* place, bool apply) {
    uint32_t i;
    int k, next = 0;
    for (i = 0; i < pp->ndims; i++) {
        place[i] = -1;
        for (k = 0; k < FIRE_NDIMS; k++) if (g_dimNames[k] && g_dimNames[k][0] && strcmp(g_dimNames[k], pp->dimNames[i]) == 0) { place[i] = k; break; }
        if (place[i] >= 0) continue;
        while (next < FIRE_NDIMS && g_dimNames[next] && g_dimNames[next][0]) next++;
        if (next == FIRE_NDIMS) return false;
        place[i] = next;
        if (apply) { g_dimNames[next] = pp->dimNames[i]; g_dimsAdded = true; }
        next++;
    }
    return true;
}
static Value vmPlMapValue(Value v, const uint32_t* units, uint32_t nunits, const uint16_t* enums, uint32_t nenums) {
    if (v.kind == K_Int && v.reserved) { if (v.reserved <= nenums) v.reserved = enums[v.reserved]; return v; }
    if ((v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined) && v.unit && v.unit < nunits) v.unit = units[v.unit];
    return v;
}
/* Maps the units and enums of the plugin's program onto the program's; NULL: done, else why not. */
static const char* vmPlUnitsAndEnums(Vm* top, VmProgram* pp, const VmPluginFile* f) {
    VmProgram* hp = top->prog;
    int32_t place[FIRE_NDIMS];
    uint32_t* rows = (uint32_t*)calloc(pp->unitRows + 1, sizeof(uint32_t));
    uint16_t* emap = (uint16_t*)calloc(pp->nenums + 1, sizeof(uint16_t));
    uint32_t r, i, k;
    if (!rows || !emap) outOfMemory();
    if (pp->ndims > FIRE_NDIMS || !vmPlDims(pp, place, false)) { free(rows); free(emap); return "its units need more dimensions than this VM has room for (16 in all)."; }
    vmPlDims(pp, place, true);
    for (i = 0; i < FIRE_NDIMS; i++) if (g_dimNames[i] && g_dimNames[i][0] && i >= hp->ndims) { hp->dimNames[i] = g_dimNames[i]; hp->ndims = i + 1; }
    for (r = 1; r < pp->unitRows; r++) {
        const UnitInit* u = &pp->unitInit[r];
        int32_t dims[FIRE_NDIMS];
        const char* cn = u->conv >= 0 && (uint32_t)u->conv < pp->nconv ? pp->convClasses[u->conv] : NULL;
        const char* ucn = u->ulconv >= 0 && (uint32_t)u->ulconv < pp->nconv ? pp->convClasses[u->ulconv] : NULL;
        bool own = (cn && vmPlHasClass(f, cn)) || (ucn && vmPlHasClass(f, ucn));
        uint32_t found = 0, j;
        memset(dims, 0, sizeof dims);
        for (j = 0; j < pp->ndims; j++) dims[place[j]] = u->dims[j];
        if (!own)
            for (j = 1; j < g_udCount && !found; j++) {
                const UnitDef* e = &g_ud[j];
                const char* ec = e->conv >= 0 && (uint32_t)e->conv < hp->nconv ? hp->convClasses[e->conv] : NULL;
                const char* euc = e->ulconv >= 0 && (uint32_t)e->ulconv < hp->nconv ? hp->convClasses[e->ulconv] : NULL;
                if (e->scale == u->scale && e->offset == u->offset && memcmp(e->dims, dims, sizeof dims) == 0 && (e->conv >= 0) == (cn != NULL) && (e->ulconv >= 0) == (ucn != NULL)
                    && (!cn || ec == cn) && (!ucn || euc == ucn) && ((e->display == NULL) == (u->display == NULL)) && (!u->display || strcmp(e->display, u->display) == 0)) found = j;
            }
        if (!found) {
            int32_t c = cn ? vmPlConvIndex(hp, cn) : -1, uc = ucn ? vmPlConvIndex(hp, ucn) : -1;
            if (c == -2 || uc == -2) { free(rows); free(emap); return "it has more units with conversion functions than this VM has room for (16 in all)."; }
            found = unitAppend(dims, u->scale, u->display, u->offset, c, uc);
        }
        rows[r] = found;
    }
    /* its enums: the program's numbers by name, new ones at the end of the program's table */
    for (i = 0; i < pp->nenums; i++) {
        uint32_t e;
        for (e = 0; e < hp->nenums && strcmp(hp->enums[e].name, pp->enums[i].name) != 0; e++) { }
        if (e == hp->nenums) {
            FireEnum* grown = (FireEnum*)malloc((hp->nenums + 1) * sizeof(FireEnum));
            if (!grown) outOfMemory();
            if (hp->nenums) memcpy(grown, hp->enums, hp->nenums * sizeof(FireEnum));
            grown[hp->nenums] = pp->enums[i];
            vmPlGarbage(grown);   /* (freed at the end of the program; the table it replaces stays for another thread) */
            hp->enums = grown;
            hp->nenums++;
            g_enums = grown;
            g_enumCount = hp->nenums;
        }
        emap[i + 1] = (uint16_t)(e + 1);
    }
    /* the values of its code */
    for (i = 0; i < pp->nchunks; i++) {
        VmChunk* c = pp->chunks[i];
        for (k = 0; k < c->nk; k++) c->k[k] = vmPlMapValue(c->k[k], rows, pp->unitRows, emap, pp->nenums);
        for (k = 0; k < c->nunits; k++) if (c->units[k] < pp->unitRows) c->units[k] = rows[c->units[k]];
    }
    for (i = 0; i < pp->nclasses; i++)
        for (k = 0; k < pp->classes[i]->nstatic; k++) pp->classes[i]->svals[k] = vmPlMapValue(pp->classes[i]->svals[k], rows, pp->unitRows, emap, pp->nenums);
    /* the units a text may name (`"7in".ToFloat()`): those of the plugin the program does not know by that symbol */
    if (pp->ncustomUnits) {
        uint32_t nt = g_textUnitCount;
        TextUnit* text = (TextUnit*)malloc((nt + 2 * pp->ncustomUnits + 1) * sizeof(TextUnit));
        if (!text) outOfMemory();
        if (nt) memcpy(text, g_textUnits, nt * sizeof(TextUnit));
        for (k = 0; k < pp->ncustomUnits; k++) {
            struct VmCustomUnit* cu = &pp->customUnits[k];
            uint32_t u = 0, t, d;
            int32_t dims[FIRE_NDIMS];
            for (t = 0; t < g_textUnitCount && strcmp(g_textUnits[t].symbol, cu->symbol) != 0; t++) { }
            if (t < g_textUnitCount) continue;
            for (r = 1; r < pp->unitRows && !u; r++) if (pp->unitInit[r].display && !strcmp(pp->unitInit[r].display, cu->symbol)) u = rows[r];
            memset(dims, 0, sizeof dims);
            for (d = 0; d < pp->ndims; d++) dims[place[d]] = cu->dims[d];
            if (!u) u = cu->offset != 0.0 ? unitAppend(dims, cu->scale, cu->symbol, cu->offset, -1, -1) : unitIntern(dims, cu->scale, cu->symbol);
            text[nt].symbol = cu->symbol; text[nt].unit = u; text[nt].prefixable = cu->prefixed; nt++;
            if (strcmp(cu->name, cu->symbol) != 0) { text[nt].symbol = cu->name; text[nt].unit = u; text[nt].prefixable = false; nt++; }
        }
        if (g_textUnits == g_vmTextUnits) vmPlGarbage(g_vmTextUnits);   /* (the table before: freed at the end of the program, another thread may read it) */
        g_textUnits = text;
        g_textUnitCount = nt;
        g_vmTextUnits = text;
    }
    free(rows);
    free(emap);
    return NULL;
}

/* ---- the static initialisers: in the order of their dependencies (the plugin's own top level, CompiledProgram.StaticInitSequence), the rest after it ---- */
typedef struct VmPlInit { VmClass* c; uint32_t index; int32_t rank; uint32_t order; } VmPlInit;
static int32_t vmPlRank(const VmChunk* top, VmClass* c, const char* field) {
    uint32_t ip = 0;
    int32_t n = 0;
    if (!top) return 0x7FFFFFFF;
    while (ip < top->ncode) {
        uint32_t op = top->code[ip], k, operands[8];
        const char* sizes;
        if (op >= VM_NOPS) return 0x7FFFFFFF;
        sizes = g_vmOpOperands[op];
        ip++;
        for (k = 0; sizes[k] && k < 8; k++) {
            if (op == OP_RegisterThreadsCatch && k == 2 && operands[1] == 0) break;
            operands[k] = sizes[k] == '1' ? top->code[ip] : (uint32_t)(top->code[ip] | (top->code[ip + 1] << 8));
            ip += sizes[k] == '1' ? 1u : 2u;
        }
        if (op == OP_SetStaticFieldOnInit && operands[0] < top->nk && operands[1] < top->nk) {
            const char* cn = top->kname[operands[0]];
            const char* fn = top->kname[operands[1]];
            if (cn && fn && strcmp(cn, c->name) == 0 && strcmp(fn, field) == 0) return n;
            n++;
        }
    }
    return 0x7FFFFFFF;
}
static int vmPlInitCmp(const void* a, const void* b) {
    const VmPlInit* x = (const VmPlInit*)a;
    const VmPlInit* y = (const VmPlInit*)b;
    if (x->rank != y->rank) return x->rank < y->rank ? -1 : 1;
    return x->order < y->order ? -1 : x->order > y->order ? 1 : 0;
}

/* (a handler that catches what a static initialiser throws: the link says so) */
typedef struct VmPlCatch { Handler h; HandlerInfo info; int32_t types[1]; const char* thrown; } VmPlCatch;
static void vmPlCatchFn(void* ctx, uint32_t ci, Value e) {
    VmPlCatch* c = (VmPlCatch*)ctx;
    VmClass* k = g_vmCur ? vmClassOfValue(g_vmCur, e) : NULL;
    (void)ci;
    c->thrown = k ? k->name : "an exception";
    exitJump(&c->h, 0);
}

/* A value that goes into a static field of a plugin (its initialiser's result, a store): unclaimed - in the global scope where its ClaimToScope put it, or of
 * no one - it joins the plugin's scope (docs/spec/PLUGINS.md), in the globals' domain when there are threads; one somebody else owns stays a reference. From
 * a thread it moves there in a section like `x.TakeGlobal()` (the thread's references stay valid). */
static void vmPlClaim(Vm* vm, Value v, OwnList* scope) {
    Owned* o = ownedOf(v);
    Vm* top = vm->top ? vm->top : vm;
    bool ss;
    if (!o || o->owner == scope) return;
    if (o->owner && o->owner != &top->global->own && o->owner != g_globalOwn) return;
    if (g_isThread && o->owner && mainGlobalsKind(v, true) != 1) return;
    ss = sectionScopeEnter(g_isThread);
    if (o->owner) ownUnlink(o);
    ownLink(scope, o);
    if (g_domainOn) { markOwnedShared(o); if (o->okind == O_Lambda) refShare(v); }
    if (g_isThread) takeGlobalRehome(o, 0);
    sectionScopeExit(ss);
}

/* Runs the static initialisers of its classes; NULL: done, else why not. What they make goes to the plugin's scope. */
static const char* vmPlStatics(Vm* vm, VmProgram* pp, VmClass** own, uint32_t nown, OwnList* scope, char* why, size_t cap) {
    VmPlInit* inits;
    uint32_t n = 0, i, k;
    for (i = 0; i < nown; i++) for (k = 0; k < own[i]->nstatic; k++) if (own[i]->sinit[k]) n++;
    if (!n) return NULL;
    inits = (VmPlInit*)malloc(n * sizeof(VmPlInit));
    if (!inits) outOfMemory();
    n = 0;
    for (i = 0; i < nown; i++)
        for (k = 0; k < own[i]->nstatic; k++)
            if (own[i]->sinit[k]) { inits[n].c = own[i]; inits[n].index = k; inits[n].rank = vmPlRank(pp->top, own[i], own[i]->snames[k]); inits[n].order = n; n++; }
    qsort(inits, n, sizeof(VmPlInit), vmPlInitCmp);
    for (i = 0; i < n; i++) {
        VmClass* c = inits[i].c;
        VmPlCatch ca;
        Value r;
        memset(&ca, 0, sizeof ca);
        ca.types[0] = -1;
        ca.info.ncatch = 1; ca.info.types = ca.types; ca.info.hasFinally = 0; ca.info.typeMap = NULL;
        ca.h.fn = vmPlCatchFn; ca.h.ctx = &ca; ca.h.info = &ca.info;
        pushHandler(&ca.h);
        r = vmCallNested(vm, c->sinit[inits[i].index], 0, NULL, Undef(), FR_NESTED);
        if (g_handlers == &ca.h) popHandler();
        if (g_unwind.active) {
            if (g_unwind.target != &ca.h) { free(inits); return NULL; }   /* (leave, terminate: it goes on) */
            clearUnwind();
            g_pending = NULL;
            snprintf(why, cap, "the initialiser of '%s.%s' threw %s.", c->name, c->snames[inits[i].index], ca.thrown ? ca.thrown : "an exception");
            free(inits);
            return why;
        }
        /* (also when a fire thread loads it: the plugin's scope, not the thread's global scope - its end does not take it along; VM.Plugins.cs the same) */
        vmPlClaim(vm, r, scope);
        adopt(r, scope);   /* (a string: the count it came with goes to the pool) */
        {
            bool ss = sectionScopeEnter(g_isThread);   /* (a static field of the globals' domain: a thread writes it in a section) */
            Value old = c->svals[inits[i].index];
            retain(r);
            c->svals[inits[i].index] = r;
            release(old);
            sectionScopeExit(ss);
        }
    }
    free(inits);
    return NULL;
}

/* ---- linking ---- */
static Value vmPlText(const char* text, OwnList* list) { return newStrFromUtf8((const uint8_t*)text, strlen(text), list); }
static Value vmPlRefused(const VmPluginFile* f, const char* why, OwnList* list) {
    char t[1600];
    snprintf(t, sizeof t, "The plugin '%s' cannot be loaded: %s", f->name, why);
    return vmPlText(t, list);
}
static Value vmPlNames(const VmPluginFile* f, OwnList* list) {
    uint32_t i, n = 0;
    Arr* names;
    for (i = 0; i < f->classes.n; i++) if (f->classes.s[i] && strncmp(f->classes.s[i], "__", 2) != 0) n++;
    names = allocArr(n, list);
    for (i = 0, n = 0; i < f->classes.n; i++)
        if (f->classes.s[i] && strncmp(f->classes.s[i], "__", 2) != 0) { Value s = vmPlText(f->classes.s[i], list); arrItems(names)[n++] = s; retain(s); }
    return ArrV(names);
}
static bool vmPlSameClasses(const VmPluginFile* a, const VmPluginFile* b) {
    uint32_t i;
    if (a->classes.n != b->classes.n) return false;
    for (i = 0; i < a->classes.n; i++) if (!a->classes.s[i] || !b->classes.s[i] || strcmp(a->classes.s[i], b->classes.s[i]) != 0) return false;
    return true;
}
static void vmPlShort(char* out, const char* abi) { snprintf(out, 15, "%s", abi ? abi : ""); }

/* the debugger (vm_debug.c): a plugin's sources got these indexes */
static void vmDbgPluginSources(const char* plugin, int32_t first, uint32_t n, const char** files, const char** hashes);

/* Forgets what was found for every class (RuntimeClass.ForgetFoundMethods): the candidates of methods and the inline caches of the code. */
static void vmPlForget(VmProgram* hp) {
    VmLinked* l;
    uint32_t i;
    for (i = 0; i < hp->nclasses; i++) hp->classes[i]->cands = NULL;
    for (i = 0; i < hp->nchunks; i++) if (hp->chunks[i]->icCls) memset(hp->chunks[i]->icCls, 0xFF, (hp->chunks[i]->ncode + 1) * sizeof(uint32_t));
    for (l = g_vmPl.linked; l; l = l->next) {
        for (i = 0; i < l->prog->nclasses; i++) l->prog->classes[i]->cands = NULL;
        for (i = 0; i < l->prog->nchunks; i++) if (l->prog->chunks[i]->icCls) memset(l->prog->chunks[i]->icCls, 0xFF, (l->prog->chunks[i]->ncode + 1) * sizeof(uint32_t));
    }
}

static void vmRegisterMore(Vm* vm, VmClass** cls, uint32_t n);
static VmProgram* vmLoadWith(const uint8_t* in, size_t len, VmProgram* host, char* error, size_t errorCap);

/* `__LibCall(5, handle)`: the classes of the fire plugin loaded with this handle, linked into the program (an array of their names); undefined: a C library;
 * a string: why it does not fit. */
static Value vmPluginLink(Vm* vm, int64_t handle, OwnList* list) {
    VmPluginFile* f = vmPluginOf(handle);
    Vm* top;
    VmProgram* hp;
    VmProgram* pp;
    VmLinked* l;
    VmClass** own;
    VmClass** reg;
    uint32_t nown, nreg = 0, i, k;
    char error[400], t[900];
    const char* problem;
    VmPlVisit visit;
    struct VmPlExt { VmClass* target; VmClass* copy; VmProto** protos; const char** names; uint32_t n; } * exts = NULL;
    uint32_t nexts = 0;
    if (!f) return Undef();
    if (f->error) return vmPlText(f->error, list);
    top = vm->top ? vm->top : vm;
    hp = top->prog;
    for (l = g_vmPl.linked; l; l = l->next)
        if (strcmp(l->file->name, f->name) == 0) {
            if (l->file != f && !vmPlSameClasses(l->file, f)) return vmPlRefused(f, "another plugin of that name is loaded already.", list);
            /* (another strand links it right now: its classes are there, its static fields not yet - this one waits for its initialisers, serving the
             * sections and jobs of the threads meanwhile; its own initialiser that loads it again does not wait for itself) */
            if (!vmPlInitDone(l) && l->initBy != vm && !(g_isThread ? blockUntil(vmPlInitDone, l, true, 0) : blockUntilServing(vmPlInitDone, l, true, 0))) return Undef();
            if (l->unloaded) {   /* (unloaded before: its static initialisers run again, into its scope) */
                l->unloaded = false;
                l->initBy = vm;
                fire_atomic_storeRelease(&l->initing, 1);
                problem = vmPlStatics(vm, l->prog, l->own, l->nown, &l->scope, t, sizeof t);
                l->initBy = NULL;
                fire_atomic_storeRelease(&l->initing, 0);
                if (problem) return vmPlRefused(f, problem, list);
                if (g_unwind.active) return Undef();
            }
            return vmPlNames(f, list);
        }
    if (f->format != VM_PLUGIN_FORMAT) return vmPlRefused(f, "it is a fire plugin of another version of fire - build it again.", list);
    /* the interfaces of the modules it was built against: the program's (a program file without them - an older one - is not checked) */
    if (hp->hasModules)
        for (i = 0; i + 1 < f->deps.n; i += 2) {
            const char* m = f->deps.s[i];
            const char* has = NULL;
            char a[16], b[16];
            for (k = 0; k < hp->nmodules; k++) if (m && strcmp(hp->moduleNames[k], m) == 0) has = hp->moduleAbis[k];
            if (!has) { snprintf(t, sizeof t, "it needs the module '%s', which this program does not have.", m ? m : ""); return vmPlRefused(f, t, list); }
            if (!f->deps.s[i + 1] || strcmp(has, f->deps.s[i + 1]) != 0) {
                vmPlShort(a, f->deps.s[i + 1]); vmPlShort(b, has);
                snprintf(t, sizeof t, "it was built against another interface of the module '%s' (%s, the program has %s) - build it again against the libraries of this program.", m, a, b);
                return vmPlRefused(f, t, list);
            }
        }
    for (i = 0; i < f->classes.n; i++)
        if (f->classes.s[i] && fbMapGet(&hp->classByName, f->classes.s[i])) { snprintf(t, sizeof t, "the program has a class '%s' already.", f->classes.s[i]); return vmPlRefused(f, t, list); }
    /* its program: read with the names of the program (a name compares by its pointer) */
    pp = vmLoadWith(f->bytes + f->body, f->len - f->body, hp, error, sizeof error);
    if (!pp) { snprintf(t, sizeof t, "The plugin '%s' cannot be read: %s", f->path, error); return vmPlText(t, list); }
    nown = f->classes.n;
    own = (VmClass**)calloc(nown + 1, sizeof(VmClass*));
    reg = (VmClass**)calloc(nown + 17, sizeof(VmClass*));
    if (!own || !reg) outOfMemory();
    for (i = 0; i < nown; i++) {
        own[i] = f->classes.s[i] ? (VmClass*)fbMapGet(&pp->classByName, f->classes.s[i]) : NULL;
        if (!own[i]) { snprintf(t, sizeof t, "The plugin '%s' cannot be read: its class '%s' is missing.", f->path, f->classes.s[i] ? f->classes.s[i] : ""); goto refusedText; }
    }
    /* its base classes are the program's */
    for (i = 0; i < nown; i++)
        if (own[i]->base && !vmPlHasClass(f, own[i]->base->name)) {
            VmClass* hb = (VmClass*)fbMapGet(&hp->classByName, own[i]->base->name);
            if (!hb) { snprintf(t, sizeof t, "the program has no class '%s'.", own[i]->base->name); goto refused; }
            own[i]->base = hb;
        }
    /* its natives in C++ the program does not have: from the libraries next to its bytecode (C ABI, like those of a package) */
    for (i = 0; i < f->pkgNatives.n; i++) {
        const char* name = f->pkgNatives.s[i];
        const char* lib = i < f->pkgLibs.n && f->pkgLibs.s[i] ? f->pkgLibs.s[i] : "";
        uint32_t n = g_vmNatCount;
        if (!name || vmPlNativeIndex(name) >= 0 || g_vmNatCount >= g_vmNatCap) continue;
        if (*lib && strcmp(lib, "@host") != 0) {
            bool absolute = lib[0] == '/' || lib[0] == '\\' || (lib[0] && lib[1] == ':');
            char path[1400];
            snprintf(path, sizeof path, "%s/%s", f->dir, lib);
            lib = absolute ? lib : vmPlExists(path) ? fbStrDup(&hp->arena, path, strlen(path)) : lib;
        }
        vmNatAdd(&n, vmName(hp, name), lib);
        g_vmNatCount = n;
    }
    memset(&visit, 0, sizeof visit);
    visit.f = f;
    for (i = 0; i < nown && !visit.problem; i++) vmPlVisitClass(&visit, own[i]);
    /* what its `class extends` adds to classes of the program, of libraries and of base types (no fields: the build refuses them) */
    if (f->exts.n >= 3) {
        const char** groups = (const char**)calloc(f->exts.n / 3 + 1, sizeof(char*));
        uint32_t ng = 0, g;
        if (!groups) outOfMemory();
        for (i = 0; i + 2 < f->exts.n; i += 3) {
            const char* cn = f->exts.s[i];
            bool seen = false;
            if (!cn) continue;
            for (g = 0; g < ng && !seen; g++) if (!strcmp(groups[g], cn)) seen = true;
            if (seen) continue;
            for (g = ng; g > 0 && strcmp(groups[g - 1], cn) > 0; g--) groups[g] = groups[g - 1];
            groups[g] = cn;
            ng++;
        }
        exts = (struct VmPlExt*)calloc(ng + 1, sizeof *exts);
        if (!exts) outOfMemory();
        for (g = 0; g < ng; g++) {
            VmClass* rc;
            VmClass* target;
            struct VmPlExt* x = &exts[nexts];
            if (vmPlHasClass(f, groups[g]) || !(rc = (VmClass*)fbMapGet(&pp->classByName, groups[g]))) continue;
            x->protos = (VmProto**)calloc(f->exts.n / 3 + 1, sizeof(VmProto*));
            x->names = (const char**)calloc(f->exts.n / 3 + 1, sizeof(char*));
            if (!x->protos || !x->names) outOfMemory();
            for (i = 0; i + 2 < f->exts.n; i += 3) {
                uint32_t m, o;
                int argc;
                if (!f->exts.s[i] || strcmp(f->exts.s[i], groups[g]) != 0 || !f->exts.s[i + 1] || !f->exts.s[i + 2]) continue;
                argc = atoi(f->exts.s[i + 2]);
                for (m = 0; m < rc->nmethods; m++)
                    if (!strcmp(rc->methods[m].name, f->exts.s[i + 1])) {
                        for (o = 0; o < rc->methods[m].nov; o++) if (rc->methods[m].ov[o]->nparams == argc) break;
                        if (o < rc->methods[m].nov) { x->names[x->n] = rc->methods[m].name; x->protos[x->n++] = rc->methods[m].ov[o]; }
                        break;
                    }
            }
            if (x->n == 0) { free(x->protos); free(x->names); x->protos = NULL; continue; }
            target = (VmClass*)fbMapGet(&hp->classByName, rc->name);
            for (k = 0; k < x->n && target; k++) {
                uint32_t m, o;
                for (m = 0; m < target->nmethods; m++)
                    if (target->methods[m].name == x->names[k])
                        for (o = 0; o < target->methods[m].nov; o++)
                            if (target->methods[m].ov[o]->nparams == x->protos[k]->nparams) {
                                snprintf(t, sizeof t, "it adds the method '%s' (%d arguments) to '%s', which has it already (another plugin added it, or the class of the program has it).",
                                    x->names[k], (int)x->protos[k]->nparams, rc->name[0] == '$' ? rc->name + 1 : rc->name);
                                free(groups);
                                goto refused;
                            }
            }
            /* (a base type the program does not extend at all: the plugin's collecting class itself - all of its methods) */
            if (!target) { uint32_t m, o; for (m = 0; m < rc->nmethods; m++) for (o = 0; o < rc->methods[m].nov; o++) vmPlVisit(&visit, rc->methods[m].ov[o]); }
            else for (k = 0; k < x->n; k++) vmPlVisit(&visit, x->protos[k]);
            x->target = target;
            x->copy = rc;
            nexts++;
        }
        free(groups);
    }
    if (visit.problem) { snprintf(t, sizeof t, "%s", visit.problem); goto refused; }
    /* its units and enums: the program's */
    {
        uint32_t udBefore = g_udCount;
        problem = vmPlUnitsAndEnums(top, pp, f);
        vmUnitsLent(udBefore);   /* (the rows it made: the texts are the plugin's) */
    }
    if (problem) { snprintf(t, sizeof t, "%s", problem); goto refused; }
    /* its sources: the next indexes after the program's (and the plugins' before); the sources of its libraries 0 (their code is the program's) */
    if (hp->nsources > 0 && f->sources.n >= 3) {
        uint32_t nsrc = 0;
        int32_t first;
        const char** files = (const char**)calloc(f->sources.n / 3 + 1, sizeof(char*));
        const char** hashes = (const char**)calloc(f->sources.n / 3 + 1, sizeof(char*));
        int32_t* from = (int32_t*)calloc(f->sources.n / 3 + 1, sizeof(int32_t));
        if (!files || !hashes || !from) outOfMemory();
        for (i = 0; i + 2 < f->sources.n; i += 3) {
            if (!f->sources.s[i]) continue;
            from[nsrc] = atoi(f->sources.s[i]);
            files[nsrc] = f->sources.s[i + 1];
            hashes[nsrc] = f->sources.s[i + 2];
            nsrc++;
        }
        first = (int32_t)hp->nsources + g_vmPl.addedSources;
        g_vmPl.addedSources += (int32_t)nsrc;
        for (i = 0; i < pp->nchunks; i++) {
            VmChunk* c = pp->chunks[i];
            for (k = 0; k < c->nlines; k++) {
                int32_t s = c->lines[3 * k + 1], to = 0;
                uint32_t q;
                for (q = 0; q < nsrc; q++) if (from[q] == s) to = first + (int32_t)q;
                c->lines[3 * k + 1] = to;
            }
        }
        if (nsrc) vmDbgPluginSources(f->name, first, nsrc, files, hashes);
        free(files); free(hashes); free(from);
    }
    /* its classes become the program's */
    for (i = 0; i < nown; i++) { fbMapPut(&hp->classByName, own[i]->name, own[i]); reg[nreg++] = own[i]; }
    for (k = 0; k < nexts; k++) {
        struct VmPlExt* x = &exts[k];
        if (!x->target) {
            static const uint8_t kinds[] = { K_String, K_Char, K_Int, K_Float, K_Bool, K_Array };
            static const char* const names[] = { "$string", "$char", "$int", "$float", "$bool", "$array" };
            uint32_t q;
            fbMapPut(&hp->classByName, x->copy->name, x->copy);
            for (q = 0; q < 6; q++) if (!strcmp(x->copy->name, names[q])) hp->extClass[kinds[q]] = x->copy;
            if (x->copy->base && !vmPlHasClass(f, x->copy->base->name)) { VmClass* hb = (VmClass*)fbMapGet(&hp->classByName, x->copy->base->name); if (hb) x->copy->base = hb; }
            if (nreg < nown + 16) reg[nreg++] = x->copy;
            continue;
        }
        {
            VmMethod* grown = (VmMethod*)vmAlloc(&hp->arena, (x->target->nmethods + x->n + 1) * sizeof(VmMethod));
            uint32_t nm = x->target->nmethods;
            if (nm) memcpy(grown, x->target->methods, nm * sizeof(VmMethod));
            for (i = 0; i < x->n; i++) {
                uint32_t m;
                for (m = 0; m < nm && grown[m].name != x->names[i]; m++) { }
                if (m == nm) { grown[nm].name = x->names[i]; grown[nm].ov = NULL; grown[nm].nov = 0; nm++; }
                {
                    VmProto** ov = (VmProto**)vmAlloc(&hp->arena, (grown[m].nov + 2) * sizeof(VmProto*));
                    if (grown[m].nov) memcpy(ov, grown[m].ov, grown[m].nov * sizeof(VmProto*));
                    ov[grown[m].nov] = x->protos[i];
                    grown[m].ov = ov;
                    grown[m].nov++;
                }
                x->protos[i]->declaring = x->target;
                vmPlOwnTo(x->protos[i], x->target, 0);
            }
            x->target->methods = grown;
            x->target->nmethods = nm;
        }
    }
    if (nexts > 0) vmPlForget(hp);
    vmRegisterMore(top, reg, nreg);
    l = (VmLinked*)calloc(1, sizeof(VmLinked));
    if (!l) outOfMemory();
    l->file = f; l->prog = pp; l->own = own; l->nown = nown;
    ownListInit(&l->scope, 0, NULL);
    for (i = 0; i < nown; i++) own[i]->plOwn = &l->scope;
    l->initBy = vm;
    fire_atomic_storeRelease(&l->initing, 1);
    l->next = g_vmPl.linked;
    g_vmPl.linked = l;
    for (k = 0; k < nexts; k++) { free(exts[k].protos); free((void*)exts[k].names); }
    free(exts);
    free(reg);
    free(visit.seen);
    /* the static initialisers of its classes; what they make belongs to the plugin's scope */
    problem = vmPlStatics(vm, pp, own, nown, &l->scope, t, sizeof t);
    l->initBy = NULL;
    fire_atomic_storeRelease(&l->initing, 0);
    if (problem) return vmPlRefused(f, problem, list);
    if (g_unwind.active) return Undef();
    return vmPlNames(f, list);
refused:
    {
        Value r = vmPlRefused(f, t, list);
        if (exts) for (k = 0; k <= nexts; k++) { free(exts[k].protos); free((void*)exts[k].names); }   /* (and the one that was refused) */
        free(exts); free(own); free(reg); free(visit.seen);
        vmUnload(pp);
        return r;
    }
refusedText:
    free(own); free(reg);
    vmUnload(pp);
    return vmPlText(t, list);
}

/* The scope of a plugin goes (lib.Unload(), the end of the program): its objects in the order they came (their destructors run), then its static fields are
 * undefined - its code stays (docs/spec/PLUGINS.md). */
static void vmPlScopeEnd(VmLinked* l) {
    uint32_t i, k;
    destroyList(&l->scope);
    for (i = 0; i < l->prog->nclasses; i++)
        for (k = 0; k < l->prog->classes[i]->nstatic; k++) { Value old = l->prog->classes[i]->svals[k]; l->prog->classes[i]->svals[k] = Undef(); release(old); }
}
/* `__LibCall(8, handle)`: lib.Unload() of a fire plugin - undefined (done, or unloaded already), a string: why not. */
static Value vmPluginUnload(Vm* vm, int64_t handle, OwnList* list) {
    VmPluginFile* f = vmPluginOf(handle);
    VmLinked* l;
    bool ss;
    if (!f) return vmPlText("The library is no fire plugin: only a fire plugin can be unloaded.", list);
    for (l = g_vmPl.linked; l && strcmp(l->file->name, f->name) != 0; l = l->next) { }
    if (!l) return Undef();
    if (!vmPlInitDone(l) && l->initBy != vm && !(g_isThread ? blockUntil(vmPlInitDone, l, true, 0) : blockUntilServing(vmPlInitDone, l, true, 0))) return Undef();
    if (l->unloaded) return Undef();
    l->unloaded = true;
    ss = sectionScopeEnter(g_isThread);   /* (the threads see its scope whole or gone) */
    vmPlScopeEnd(l);
    sectionScopeExit(ss);
    return Undef();
}
/* The first `fire`: the objects of the plugins' scopes join the globals' domain (as ensureDomain does with the program's global scope). */
static void vmPluginsShare(void) {
    VmLinked* l;
    Owned* o;
    for (l = g_vmPl.linked; l; l = l->next) for (o = l->scope.head; o; o = o->next) markOwnedShared(o);
}
/* The end of the program, after its global scope: the scopes of its plugins in the order they were loaded (see fire.c). */
static void vmPluginsReleaseStatics(void) {
    VmLinked* l;
    uint32_t n = 0, i;
    VmLinked** order;
    for (l = g_vmPl.linked; l; l = l->next) n++;
    if (!n) return;
    order = (VmLinked**)malloc(n * sizeof(VmLinked*));
    if (!order) outOfMemory();
    for (l = g_vmPl.linked, i = n; l; l = l->next) order[--i] = l;
    for (i = 0; i < n; i++) if (!order[i]->unloaded) { order[i]->unloaded = true; vmPlScopeEnd(order[i]); }
    free(order);
}
static void vmPluginsEnd(void) {
    VmPluginFile* f;
    uint32_t i;
    while (g_vmPl.linked) {
        VmLinked* l = g_vmPl.linked;
        g_vmPl.linked = l->next;
        vmUnload(l->prog);
        free(l->own);
        free(l);
    }
    while ((f = g_vmPl.files) != NULL) {
        g_vmPl.files = f->next;
        free(f->path); free(f->dir); free(f->error); free(f->bytes);
        fbArenaFree(&f->arena);
        free(f);
    }
    for (i = 0; i < g_vmPl.ngarbage; i++) free(g_vmPl.garbage[i]);
    free(g_vmPl.garbage);
    g_vmPl.garbage = NULL; g_vmPl.ngarbage = g_vmPl.capGarbage = 0;
    g_vmPl.addedSources = 0;
}
