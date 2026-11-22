/* fire VM in C: `Library` (docs/language/18-native-code.md, `__LibCall`; phase B step 4) - a native library loaded while the program runs and its functions
 * by C signature. A native build generates a thunk per signature the program names (CppGenerator.LibraryThunks); the VM calls by a signature it reads at
 * run time: the arguments are sorted into whole numbers (pointers, bool, char, int of every width - sign-extended to 64 bits) and floats (a float[32] as the
 * bits of a float in the low half of the register), and the function is called as one with 8 whole-number and 8 float parameters - on the calling
 * conventions where whole numbers and floats go in registers of their own, in order (System V x86-64, AArch64: what a C function of up to 8 parameters
 * reads is the same). Elsewhere (Windows x64: the registers by position) a signature is "not known" (Get says so). The rest of `__LibCall` - loading,
 * looking up, the shape of a signature, releasing a NativeHandle - is the runtime's libCall. A fire plugin (1 of a `.fireplugin` file or of a library with
 * one next to it, 5) is the VM's: its bytecode linked into the program (vm_plugin.c). */

#if (defined(__x86_64__) && !defined(_WIN32)) || defined(__aarch64__)
#define VM_LIB_CALLS 1
#endif

/* (the runtime's libCall asks the program for a thunk by signature: the VM has none - it calls by itself, vmLibCall) */
FireLibThunk fire_libThunk(const char* signature) { (void)signature; return NULL; }

typedef struct VmLibType { char base; int width; int depth; } VmLibType;   /* base: b(ool) i(nt) f(loat) c(har) s(tring) v(oid) */
static bool vmLibType(const char** p, VmLibType* t) {
    const char* s = *p;
    const char* name = s;
    size_t len;
    t->width = -1; t->depth = 0;
    while (*s >= 'a' && *s <= 'z') s++;
    len = (size_t)(s - name);
    if (*s == '[') { t->width = (int)strtol(s + 1, (char**)&s, 10); if (*s == ']') s++; }
    while (*s == '*') { t->depth++; s++; }
    *p = s;
    if (len == 4 && !memcmp(name, "bool", 4)) t->base = 'b';
    else if (len == 3 && !memcmp(name, "int", 3)) t->base = 'i';
    else if (len == 5 && !memcmp(name, "float", 5)) t->base = 'f';
    else if (len == 4 && !memcmp(name, "char", 4)) t->base = 'c';
    else if (len == 6 && !memcmp(name, "string", 6)) t->base = 's';
    else if (len == 4 && !memcmp(name, "void", 4)) t->base = 'v';
    else return false;
    return true;
}

#ifdef VM_LIB_CALLS
typedef int64_t (*VmLibI)(int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, double, double, double, double, double, double, double, double);
typedef double (*VmLibF)(int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, double, double, double, double, double, double, double, double);
static double vmLibFloatBits(float f) { double d = 0; uint32_t b; memcpy(&b, &f, 4); { uint64_t w = b; memcpy(&d, &w, 8); } return d; }
static int64_t vmLibNarrow(int64_t x, const VmLibType* t) {
    if (t->base == 'b') return x != 0;
    if (t->base == 'c') return (int64_t)(uint16_t)x;
    switch (t->width) { case 8: return (int8_t)x; case 16: return (int16_t)x; case 32: return (int32_t)x; default: return x; }
}

/* Calls `f` with the C signature `text` (without white space, valid) and `count` arguments. */
static Value vmLibInvoke(void* f, const char* text, const Value* items, int count, Value release, OwnList* list) {
    const char* s = text;
    VmLibType ret, ps[8];
    int n = 0, i, ni = 0, nf = 0;
    int64_t iv[8] = {0};
    double fv[8] = {0};
    ExternString strs[8];
    ExternPtr ptrs[8];
    Value result;
    if (!vmLibType(&s, &ret) || *s++ != '(') return fireError("LibraryException", "The signature of a native function is not known to this native build.");
    while (*s && *s != ')' && n < 8) {
        if (!vmLibType(&s, &ps[n])) break;
        if (!(ps[n].base == 'v' && ps[n].depth == 0)) n++;
        if (*s == ',') s++;
    }
    if (count != n) {
        char t[160];
        snprintf(t, sizeof t, "The function expects %d argument(s), got %d.", n, count);
        return fireError("ArgumentException", t);
    }
    memset(strs, 0, sizeof strs);
    memset(ptrs, 0, sizeof ptrs);
    for (i = 0; i < n; i++) {
        Value v = items[i];
        if (ps[i].depth > 0) { externPtrInit(&ptrs[i], v); iv[ni++] = (int64_t)(intptr_t)ptrs[i].raw; }
        else if (ps[i].base == 's') { externStringInit(&strs[i], v); iv[ni++] = (int64_t)(intptr_t)strs[i].p; }
        else if (ps[i].base == 'f') fv[nf++] = ps[i].width == 32 ? vmLibFloatBits((float)externFloat(v)) : externFloat(v);
        else iv[ni++] = vmLibNarrow(externInt(v), &ps[i]);
        if (g_unwind.active) break;
    }
    if (!g_unwind.active) {
        if (ret.depth == 0 && ret.base == 'f') {
            double d = ((VmLibF)f)(iv[0], iv[1], iv[2], iv[3], iv[4], iv[5], iv[6], iv[7], fv[0], fv[1], fv[2], fv[3], fv[4], fv[5], fv[6], fv[7]);
            if (ret.width == 32) { uint64_t w; uint32_t b; float x; memcpy(&w, &d, 8); b = (uint32_t)w; memcpy(&x, &b, 4); d = x; }
            result = Float((Real)d);
        } else {
            int64_t r = ((VmLibI)f)(iv[0], iv[1], iv[2], iv[3], iv[4], iv[5], iv[6], iv[7], fv[0], fv[1], fv[2], fv[3], fv[4], fv[5], fv[6], fv[7]);
            if (ret.depth > 0) result = VM_PTRV(r);
            else switch (ret.base) {
                case 'v': result = Undef(); break;
                case 'b': result = Bool((r & 0xFF) != 0); break;
                case 'c': result = Char((uint32_t)(uint16_t)r); break;
                case 's': {
                    const char* p = (const char*)(intptr_t)r;
                    result = externResultString(p, list);
                    if (release.kind == K_Int && release.i && p) ((void (*)(void*))(intptr_t)release.i)((void*)p);
                    break;
                }
                default: result = Int(vmLibNarrow(r, &ret)); break;
            }
        }
    } else result = Undef();
    for (i = n; i-- > 0;) { if (ps[i].depth > 0) externPtrFree(&ptrs[i]); else if (ps[i].base == 's') externStringFree(&strs[i]); }
    return result;
}

/* `__LibCall(4, fn, signature, args, release)`: calls the function. */
static Value vmLibCall(Value fn, Value sig, Value args, Value release, OwnList* list) {
    char text[256];
    Arr* a = args.kind == K_Array ? arrOf(args) : NULL;
    if (!fire_libSigNorm(sig, text, sizeof text) || fire_libSigArity(text) < 0) return fireError("LibraryException", "The signature of a native function is not known to this native build.");
    return vmLibInvoke(fn.kind == K_Int ? (void*)(intptr_t)fn.i : NULL, text, a ? arrItems(a) : NULL, a ? (int)a->length : 0, release, list);
}
#endif

/* `extern string __HostCommand()` without a library: the next command of the host that runs the program (spark's live view, LiveSession: one line on the
 * program's standard input; "" when none has come). Never waits. (Windows: none yet.) */
#if !defined(_WIN32)
#include <poll.h>
#include <unistd.h>
static char g_vmHostCmd[4096];
static size_t g_vmHostCmdLen;
static Value vmHostCommand(OwnList* list) {
    for (;;) {
        char* nl = g_vmHostCmdLen ? (char*)memchr(g_vmHostCmd, '\n', g_vmHostCmdLen) : NULL;
        if (nl) {
            size_t n = (size_t)(nl - g_vmHostCmd);
            Value v;
            *nl = 0;
            if (n > 0 && g_vmHostCmd[n - 1] == '\r') g_vmHostCmd[n - 1] = 0;
            v = strFromUtf8(g_vmHostCmd, list);
            memmove(g_vmHostCmd, nl + 1, g_vmHostCmdLen - n - 1);
            g_vmHostCmdLen -= n + 1;
            return v;
        }
        if (g_vmHostCmdLen == sizeof g_vmHostCmd) g_vmHostCmdLen = 0;   /* (a line longer than the buffer: dropped) */
        {
            struct pollfd pfd;
            ssize_t got;
            pfd.fd = 0; pfd.events = POLLIN; pfd.revents = 0;
            if (poll(&pfd, 1, 0) <= 0 || !(pfd.revents & (POLLIN | POLLHUP))) return strFromUtf8("", list);
            got = read(0, g_vmHostCmd + g_vmHostCmdLen, sizeof g_vmHostCmd - g_vmHostCmdLen);
            if (got <= 0) return strFromUtf8("", list);
            g_vmHostCmdLen += (size_t)got;
        }
    }
}
#else
static Value vmHostCommand(OwnList* list) { return strFromUtf8("", list); }
#endif

/* CallExtern: a function of a C library (`#extern "lib"` + `extern int f(int)`, SPEC 8.1) - by its signature, like Library (VM.ResolveDynamicExtern). An
 * `inline c` function needs a C compiler while the program runs: not in this VM. */
static Value vmCallExtern(VmProgram* p, const char* name, int argc, const Value* args, OwnList* list) {
    VmExtern* e = NULL;
    uint32_t i;
    char text[600];
    for (i = 0; i < p->nexterns; i++) if (p->externs[i].name == name) e = &p->externs[i];
    if (!e) { snprintf(text, sizeof text, "The extern function '%s' is not declared.", name); fatal(text); }
    if (!e->lib && !e->inlineC && argc == 0 && strcmp(name, "__HostCommand") == 0) return vmHostCommand(list);
    if (e->inlineC || !e->lib) { snprintf(text, sizeof text, "the inline C function %s", name); vmUnsupported(text, NULL); }
#ifdef VM_LIB_CALLS
    if (!e->fn) {
        void* h = fire_plat_dlopen(e->lib);
        if (!h) {
            const char* why = fire_plat_dlerror();
            snprintf(text, sizeof text, "Native library '%s' could not be loaded (for extern '%s', declared with '#extern \"%s\"'): %s", e->lib, name, e->lib, why ? why : "not found");
            fatal(text);
        }
        e->fn = fire_plat_dlsym(h, name);
        if (!e->fn) { snprintf(text, sizeof text, "Function '%s' was not found in '%s'.", name, e->lib); fatal(text); }
    }
    return vmLibInvoke(e->fn, e->signature, args, argc, Undef(), list);
#else
    (void)argc; (void)args; (void)list;
    vmUnsupported("extern functions on this machine", name);
#endif
}

/* fire plugins (vm_plugin.c) */
static int64_t vmPluginLoad(const char* path);
static bool vmPlEndsWith(const char* s, const char* tail);
static Value vmPluginLink(Vm* vm, int64_t handle, OwnList* list);
static Value vmPluginUnload(Vm* vm, int64_t handle, OwnList* list);
static Value vmPlText(const char* text, OwnList* list);
#define VM_PLUGIN_HANDLE(v) ((v).kind == K_Int && (v).i <= -1000)

/* `__LibCall(id, a, b, c, d)`. */
static Value vmLibraryCall(Vm* vm, uint32_t argc, const Value* a, OwnList* list) {
    Value u = Undef();
    Value b = argc > 2 ? a[2] : u, c = argc > 3 ? a[3] : u, d = argc > 4 ? a[4] : u;
    switch (a[0].kind == K_Int ? (int)a[0].i : 0) {
        case 1:
            /* (a fire plugin: its bytecode - the `.fireplugin` file, or the one next to the library) */
            if (a[1].kind == K_String) {
                char path[4096];
                int64_t h;
                strToUtf8(a[1], path, sizeof path);
                if ((h = vmPluginLoad(path)) != 0) return Int(h);
                if (vmPlEndsWith(path, ".fireplugin")) return Int(0);   /* (no such file: not the library next to it either - the C# VM's NativeLibrary.Load) */
            }
            return libCall(a[0], a[1], b, c, d, list);
        case 2:
            if (VM_PLUGIN_HANDLE(a[1])) return Int(0);   /* (a fire plugin: no C functions) */
            return libCall(a[0], a[1], b, c, d, list);
        case 3: {
            char text[256];
            int n;
            if (!fire_libSigNorm(a[1], text, sizeof text) || (n = fire_libSigArity(text)) < 0) return Int(-1);
#ifdef VM_LIB_CALLS
            return Int(n);
#else
            return Int(-2);
#endif
        }
#ifdef VM_LIB_CALLS
        case 4: return vmLibCall(a[1], b, c, d, list);
#endif
        case 5: return VM_PLUGIN_HANDLE(a[1]) ? vmPluginLink(vm, a[1].i, list) : Undef();   /* (undefined: a C library) */
        case 8: return VM_PLUGIN_HANDLE(a[1]) ? vmPluginUnload(vm, a[1].i, list) : vmPlText("The library is no fire plugin: only a fire plugin can be unloaded.", list);
        default: return libCall(a[0], a[1], b, c, d, list);
    }
}
