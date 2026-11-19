/* fire VM in C: the natives (phase B step 4).
 *
 * The natives every program has (native/tables/natives.def - NativeRegistry.CreateDefault) run on the functions of the runtime the generator calls for them
 * (CppGenerator, OpCode.CallNative): print, the methods of the base types by number (__StringCall -> stringCall, __CharCall -> charCall, __MathCall ->
 * mathCall, __BitsCall -> bitsCall, __EnvCall -> envCall, __SbCall -> sbCall, __ResourceCall -> resourceCall), __array_copy, __type_arg, __JsonCall
 * (fire_json.h on the VM's tables, vm_json.c), __LibCall (vm_lib.c). The threads, tasks and channels (step 3) are not here yet.
 *
 * They are a table by name (g_vmCoreNatives), bound to the indices of natives.def when a program starts.
 * Behind them come the natives of the imports, by index in the order the compiler registered them (ImportedPreludes.Insert): graphics and windows
 * (native/tables/bridges.def: the library of both, libfire_gfx), reflection (vm_reflect.c), then the natives of the packages the program file names
 * (`packageNatives`, each with its library). Each is bound by name when the program is loaded (vmNativesBind): to a function of the VM - the table
 * g_vmHostNatives: what a package leaves to its host (`"host": true`: Sleep, __time_unit_ticks, the waits of the devices) and reflection - or to its library (vm_pkg.c). */

/* The text of a type argument (__type_arg): the display text, undefined without one. */
static Value vmTypeArgText(VmFrame* fr, Value ix, OwnList* list) {
    TArgs ta = fr->ctargs;
    if (!ta || ix.kind != K_Int || ix.i < 0 || !ta[ix.i] || !ta[ix.i]->display) return Undef();
    return newStrFromUtf8((const uint8_t*)ta[ix.i]->display, strlen(ta[ix.i]->display), list);
}

/* ---- the natives of the imports ---- */
typedef Value (*VmHostFn)(Vm* vm, VmFrame* fr, const Value* a, OwnList* list);
typedef struct VmHostNative { const char* name; int8_t argc; VmHostFn fn; } VmHostNative;

enum { VN_UNBOUND, VN_HOST, VN_LIB };
typedef struct VmNative {
    const char* name;
    uint8_t kind;
    int8_t argc;            /* VN_HOST: its arity (-1 any); VN_LIB: the library's */
    VmHostFn fn;
    VmPkgLib* lib;
    const char* file;       /* the library's file name (VN_LIB, and an unbound one that has one) */
    int index;              /* in the library */
} VmNative;

static VmNative* g_vmNat = NULL;    /* the natives of the imports of the program that runs, by index - VM_NNATIVES */
static uint32_t g_vmNatCount = 0;

static const char* const g_vmBridgeGraphics[] = {
#define FIRE_BRIDGE_NATIVE(import, index, name) FIRE_BRIDGE_##import(name)
#define FIRE_BRIDGE_graphics(name) name,
#define FIRE_BRIDGE_windows(name)
#define FIRE_BRIDGE_reflection(name)
#include "../tables/bridges.def"
#undef FIRE_BRIDGE_graphics
#undef FIRE_BRIDGE_windows
#undef FIRE_BRIDGE_reflection
    NULL
};
static const char* const g_vmBridgeWindows[] = {
#define FIRE_BRIDGE_graphics(name)
#define FIRE_BRIDGE_windows(name) name,
#define FIRE_BRIDGE_reflection(name)
#include "../tables/bridges.def"
#undef FIRE_BRIDGE_graphics
#undef FIRE_BRIDGE_windows
#undef FIRE_BRIDGE_reflection
    NULL
};
static const char* const g_vmBridgeReflection[] = {
#define FIRE_BRIDGE_graphics(name)
#define FIRE_BRIDGE_windows(name)
#define FIRE_BRIDGE_reflection(name) name,
#include "../tables/bridges.def"
#undef FIRE_BRIDGE_graphics
#undef FIRE_BRIDGE_windows
#undef FIRE_BRIDGE_reflection
#undef FIRE_BRIDGE_NATIVE
    NULL
};

/* `Sleep(time)` (time.fireproj: run by the host): the program waits - the libraries that pump windows on this thread are served meanwhile. */
static Value vmHostSleep(Vm* vm, VmFrame* fr, const Value* a, OwnList* list) {
    int64_t ticks;
    char text[200];
    VmPkgLib* lib;
    bool idle = false;
    (void)vm; (void)fr; (void)list;
    if (!timeTicksOf(a[0], &ticks)) {
        if (a[0].kind == K_Class) snprintf(text, sizeof text, "Sleep expects a TimeSpan, got: %s.", className(asObj(a[0])->cls));
        else if (a[0].kind == K_Int || a[0].kind == K_Float) snprintf(text, sizeof text, "'%s' is not a unit of time.", g_ud[a[0].unit].name);
        else snprintf(text, sizeof text, "Sleep expects a TimeSpan, a time value or milliseconds, got: %s.", kindName(a[0]));
        return fireError("TimeException", text);
    }
    for (lib = g_vmLibs; lib; lib = lib->next) if (!lib->failure && lib->idle) idle = true;
    if (!idle && !g_vmPkgPosted) { sleepTicks(ticks); return Undef(); }
    {
        int64_t deadline = fire_plat_nowMs() + (ticks + TICKS_PER_MS - 1) / TICKS_PER_MS, now;
        for (;;) {
            vmPkgIdle();
            if (g_unwind.active) return Undef();
            now = fire_plat_nowMs();
            if (now >= deadline) return Undef();
            fire_plat_sleepMs(deadline - now < 5 ? deadline - now : 5);
        }
    }
}

/* `__time_unit_ticks(value)`: a value with a unit of time in ticks, undefined without a unit (the units do not cross the ABI: the host does it). */
static Value vmHostUnitTicks(Vm* vm, VmFrame* fr, const Value* a, OwnList* list) {
    Value v = a[0];
    double perSecond, number;
    (void)vm; (void)fr; (void)list;
    if (!(v.kind == K_Int || v.kind == K_Float) || unitIsUnitless(v.unit)) return Undef();
    if (!unitOfTime(v.unit, &perSecond)) {
        char text[160];
        snprintf(text, sizeof text, "'%s' is not a unit of time.", g_ud[v.unit].name);
        return fireError("TimeException", text);
    }
    number = v.kind == K_Float ? (double)v.f : (double)v.i;
    return Int((int64_t)nearbyint(number * perSecond * 1e7));
}

/* The waiting functions of the devices package (`WaitFor`, `WaitForString`; run by the host - DeviceHostNatives): the VM waits - with the program's
 * `#timeout` as the default - and asks the library once per step whether the receive buffer of the device holds the pattern (`__DEVWaitStep`: 1 found,
 * -1 the device is gone). 1: found, 0: time up or the device is gone, -1: not a time. */
static VmNative* vmNativeNamed(const char* name);
static Value vmCallImported(Vm* vm, VmFrame* fr, VmNative* e, uint32_t argc, Value* a, OwnList* list);
static Value vmHostDevWait(Vm* vm, VmFrame* fr, Value handle, Value needle, Value timeout, OwnList* list) {
    VmNative* step = vmNativeNamed("__DEVWaitStep");
    int64_t ticks, deadline;
    Value args[2];
    if (timeout.kind == K_Undefined) ticks = g_defaultTimeoutTicks;
    else if (!timeTicksOf(timeout, &ticks)) return Int(-1);
    if (!step) fatal("The native function '__DEVWaitStep' of the devices package is not registered.");
    deadline = fire_plat_nowMs() + (ticks + TICKS_PER_MS - 1) / TICKS_PER_MS;
    args[0] = handle; args[1] = needle;
    for (;;) {
        Value r = vmCallImported(vm, fr, step, 2, args, list);
        if (g_unwind.active) return Undef();
        if (r.kind == K_Int && r.i != 0) return Int(r.i == 1 ? 1 : 0);
        if (fire_plat_nowMs() >= deadline) return Int(0);
        vmPkgIdle();
        fire_plat_sleepMs(2);
    }
}
static Value vmHostWaitForString(Vm* vm, VmFrame* fr, const Value* a, OwnList* list) {
    const Str* s;
    Buf* b;
    uint32_t i;
    if (a[1].kind != K_String) return fireError("ArgumentException", "WaitForString expects a text.");
    s = strOf(a[1]);
    b = allocBuf(s->length, list);   /* (one byte per character, Latin1 - like ReadString/WriteString) */
    for (i = 0; i < s->length; i++) b->data[i] = (uint8_t)s->data[i];
    return vmHostDevWait(vm, fr, a[0], BufV(b), a[2], list);
}
static Value vmHostWaitFor(Vm* vm, VmFrame* fr, const Value* a, OwnList* list) { return vmHostDevWait(vm, fr, a[0], a[1], a[2], list); }

static Value vmReflNative(Vm* vm, VmFrame* fr, uint32_t which, const Value* a, OwnList* list);
#define VM_REFL(k) static Value vmRefl##k(Vm* vm, VmFrame* fr, const Value* a, OwnList* list) { return vmReflNative(vm, fr, k, a, list); }
VM_REFL(0) VM_REFL(1) VM_REFL(2) VM_REFL(3) VM_REFL(4) VM_REFL(5) VM_REFL(6) VM_REFL(7) VM_REFL(8) VM_REFL(9) VM_REFL(10) VM_REFL(11) VM_REFL(12) VM_REFL(13)
VM_REFL(14) VM_REFL(15) VM_REFL(16)
#undef VM_REFL

/* The natives the VM runs itself: those a package leaves to its host and those of reflection (by the order of bridges.def). */
static const VmHostNative g_vmHostNatives[] = {
    {"Sleep", 1, vmHostSleep},
    {"__time_unit_ticks", 1, vmHostUnitTicks},
    {"__DEVWaitForString", 3, vmHostWaitForString}, {"__DEVWaitFor", 3, vmHostWaitFor},
    {"__refl_class_name", 1, vmRefl0}, {"__refl_class_info", 1, vmRefl1}, {"__refl_members", 1, vmRefl2}, {"__refl_classes", 0, vmRefl3},
    {"__refl_is_sub", 2, vmRefl4}, {"__refl_get", 2, vmRefl5}, {"__refl_set", 3, vmRefl6}, {"__refl_call", 3, vmRefl7}, {"__refl_new", 2, vmRefl8},
    {"__refl_has", 2, vmRefl9}, {"__refl_selector_path", 1, vmRefl10}, {"__refl_member_kind", 2, vmRefl11}, {"__refl_probe", 4, vmRefl12},
    {"__refl_silence", 2, vmRefl13}, {"__refl_silence_handle", 1, vmRefl14}, {"__refl_enum_name", 1, vmRefl15}, {"__refl_enum_of", 2, vmRefl16},
    {NULL, 0, NULL}
};

static void vmCoreBind(void);
static bool vmImports(VmProgram* p, const char* name) {
    uint32_t i;
    for (i = 0; i < p->nnativeImports; i++) if (strcmp(p->nativeImports[i], name) == 0) return true;
    return false;
}

static void vmNatAdd(uint32_t* n, const char* name, const char* file) {
    VmNative* e = &g_vmNat[(*n)++];
    const VmHostNative* h;
    memset(e, 0, sizeof *e);
    e->name = name;
    e->file = file;
    e->index = -1;
    if (file && strcmp(file, "@host") != 0) return;   /* (bound on its first call: the library is loaded then) */
    for (h = g_vmHostNatives; h->name; h++)
        if (strcmp(h->name, name) == 0) { e->kind = VN_HOST; e->argc = h->argc; e->fn = h->fn; return; }
}

/* The natives of the program's imports, by index (the order of ImportedPreludes.Insert). */
static void vmNativesBind(Vm* vm) {
    VmProgram* p = vm->prog;
    uint32_t n = 0, cap = p->npackageNatives + 1, i;
    bool graphics = vmImports(p, "graphics"), windows = vmImports(p, "windows"), reflection = vmImports(p, "reflection");
    const char* gfx = VM_GFX_FILE;
    if (graphics) cap += (uint32_t)(sizeof g_vmBridgeGraphics / sizeof g_vmBridgeGraphics[0]);
    if (windows) cap += (uint32_t)(sizeof g_vmBridgeWindows / sizeof g_vmBridgeWindows[0]);
    if (reflection) cap += (uint32_t)(sizeof g_vmBridgeReflection / sizeof g_vmBridgeReflection[0]);
    g_vmNat = (VmNative*)calloc(cap, sizeof(VmNative));
    if (!g_vmNat) outOfMemory();
    vmCoreBind();
    g_vmPkgUsesGraphics = graphics;
    if (graphics) for (i = 0; g_vmBridgeGraphics[i]; i++) vmNatAdd(&n, g_vmBridgeGraphics[i], gfx);
    if (windows) for (i = 0; g_vmBridgeWindows[i]; i++) vmNatAdd(&n, g_vmBridgeWindows[i], gfx);
    if (reflection) for (i = 0; g_vmBridgeReflection[i]; i++) vmNatAdd(&n, g_vmBridgeReflection[i], NULL);
    for (i = 0; i < p->npackageNatives; i++) vmNatAdd(&n, p->packageNatives[i], i < p->npackageLibraries ? p->packageLibraries[i] : "");
    g_vmNatCount = n;
}

static void vmNativesUnbind(void) { free(g_vmNat); g_vmNat = NULL; g_vmNatCount = 0; }

/* The native of an import of this name (NULL: none). */
static VmNative* vmNativeNamed(const char* name) {
    uint32_t i;
    for (i = 0; i < g_vmNatCount; i++) if (strcmp(g_vmNat[i].name, name) == 0) return &g_vmNat[i];
    return NULL;
}

/* The name of native `idx` (for messages). */
static const char* vmNativeName(uint32_t idx) {
    if (idx < VM_NNATIVES) return g_vmNativeNames[idx];
    idx -= VM_NNATIVES;
    return idx < g_vmNatCount ? g_vmNat[idx].name : "?";
}

/* A native of an import: bound on its first call (its library loaded), then called. */
static Value vmCallImported(Vm* vm, VmFrame* fr, VmNative* e, uint32_t argc, Value* a, OwnList* list) {
    char text[900];
    if (e->kind == VN_UNBOUND) {
        if (e->file && strcmp(e->file, "@host") == 0) {
            snprintf(text, sizeof text, "The native function '%s' is run by the host, and this host does not provide it.", e->name);
            fatal(text);
        }
        if (!e->file) { vmUnsupported("the native", e->name); }
        e->lib = vmPkgLoad(e->file);
        if (e->lib->failure) {
            snprintf(text, sizeof text, "The native function '%s' of a package cannot run in the virtual machine: its library '%s' could not be loaded (%s).", e->name, e->file, e->lib->failure);
            fatal(text);
        }
        e->index = vmPkgIndexOf(e->lib, e->name);
        if (e->index < 0) { snprintf(text, sizeof text, "The library '%s' does not export the native function '%s'.", e->file, e->name); fatal(text); }
        e->argc = (int8_t)e->lib->arity[e->index];
        e->kind = VN_LIB;
    }
    if (e->argc >= 0 && (uint32_t)e->argc != argc) {
        snprintf(text, sizeof text, "The native function '%s' takes %d argument(s), called with %u.", e->name, e->argc, argc);
        fatal(text);
    }
    if (e->kind == VN_HOST) return e->fn(vm, fr, a, list);
    return vmPkgCall(e->lib, e->index, e->name, (int)argc, a, list);
}

static Value vmJsonCall(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list);
/* (vm_reflect.c: probes - the opcodes and a write to an object that has them) */
static bool vmProbedSet(Vm* vm, VmChunk* caller, Value obj, const char* name, Value value);
static bool vmOpProbe(Vm* vm, Value target, const char* member, uint32_t flags, Value handler, Value* result);
static bool vmOpSilenceMember(Value target, const char* member);
static bool vmOpSilenceValue(Value v);

/* ---- the core natives: what the runtime has (natives.def), by name - the index of a program's call is bound to them when the VM starts ---- */
typedef Value (*VmCoreFn)(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list);
typedef struct VmCoreNative { const char* name; int8_t minArgs, maxArgs; VmCoreFn fn; } VmCoreNative;
#define VM_A(k) (argc > (k) ? a[k] : Undef())
/* FIRE_PRINT_RECORDS=1: one `print` is one line of stdout - a line break in its text is written as \x1F (spark's live view: LiveSession gets each print
 * as one message, also one of several lines, as from the C# VM in its process). */
static int g_vmPrintRecords = -1;
static Value vmPrintRecord(Value v, OwnList* list) {
    Piece p;
    fire_char16 small[256];
    fire_char16* buf;
    uint32_t i;
    pieceOf(v, &p, list);
    if (FIRE_UNLIKELY(g_unwind.active)) return Undef();   /* ToString() threw */
    buf = p.n <= 256 ? small : (fire_char16*)malloc((size_t)p.n * sizeof(fire_char16));
    if (!buf) outOfMemory();
    for (i = 0; i < p.n; i++) buf[i] = p.p[i] == '\n' ? (fire_char16)0x1F : p.p[i];
    writeUtf16(buf, p.n, stdout);
    fputc('\n', stdout);
    if (buf != small) free(buf);
    return Undef();
}
static Value vmcPrint(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) {
    (void)vm; (void)fr; (void)argc;
    if (g_vmPrintRecords < 0) { const char* e = getenv("FIRE_PRINT_RECORDS"); g_vmPrintRecords = e != NULL && *e == '1'; }
    return g_vmPrintRecords ? vmPrintRecord(a[0], list) : print(a[0], list);
}
static Value vmcString(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) {
    (void)vm; (void)fr;
    if (a[1].kind != K_String) return fireError("ArgumentException", "__StringCall(id, text, ...) expects the string as the second argument.");
    return stringCall(a[0].i, a[1], (int)argc - 2, VM_A(2), VM_A(3), list);
}
static Value vmcChar(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) { (void)vm; (void)fr; (void)argc; return charCall(a[0].i, a[1], list); }
static Value vmcResource(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) { (void)vm; (void)fr; (void)argc; return resourceCall(a[0].i, a[1], list); }
static Value vmcMath(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) { (void)vm; (void)fr; (void)list; return mathCall(a[0].i, a[1], VM_A(2)); }
static Value vmcJson(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) { return vmJsonCall(vm, fr, argc, a, list); }
static Value vmcEnv(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) { (void)vm; (void)fr; return envCall(a[0].i, VM_A(1), VM_A(2), list); }
static Value vmcLib(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) { (void)vm; (void)fr; return vmLibraryCall(argc, a, list); }
static Value vmcBits(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) { (void)vm; (void)fr; (void)list; return bitsCall(a[0].i, a[1], VM_A(2), VM_A(3)); }
static Value vmcArrayCopy(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) { (void)vm; (void)fr; (void)argc; (void)list; return fire_arrayCopy(a[0], a[1], a[2]); }
static Value vmcTypeArg(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) { (void)vm; (void)argc; return vmTypeArgText(fr, a[0], list); }
static Value vmcSb(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) { (void)vm; (void)fr; return sbCall(a[0].i, a[1], VM_A(2), list); }
#undef VM_A
static const VmCoreNative g_vmCoreNatives[] = {
    {"print", 1, 1, vmcPrint}, {"__StringCall", 2, 4, vmcString}, {"__CharCall", 2, 2, vmcChar}, {"__ResourceCall", 2, 2, vmcResource},
    {"__MathCall", 2, 3, vmcMath}, {"__JsonCall", 2, 3, vmcJson}, {"__EnvCall", 1, 3, vmcEnv}, {"__LibCall", 2, 5, vmcLib}, {"__BitsCall", 2, 4, vmcBits},
    {"__array_copy", 3, 3, vmcArrayCopy}, {"__type_arg", 1, 1, vmcTypeArg}, {"__SbCall", 2, 3, vmcSb},
    /* (the natives of the threads, tasks, channels and completions: phase B step 3) */
    {NULL, 0, 0, NULL}
};
static const VmCoreNative* g_vmCore[VM_NNATIVES];   /* by the index of natives.def (NULL: not in the VM yet) */
static void vmCoreBind(void) {
    uint32_t i;
    const VmCoreNative* c;
    for (i = 0; i < VM_NNATIVES; i++) {
        g_vmCore[i] = NULL;
        for (c = g_vmCoreNatives; c->name; c++) if (strcmp(c->name, g_vmNativeNames[i]) == 0) g_vmCore[i] = c;
    }
}

/* CallNative `idx` with the top `argc` values of the stack as its arguments: the result replaces them. False: the native is not one of the VM's. */
static bool vmCallNative(Vm* vm, VmFrame* fr, uint32_t idx, uint32_t argc, Value* a, Value* result) {
    OwnList* list = &fr->scope->temp;
    if (idx < VM_NNATIVES) {
        const VmCoreNative* c = g_vmCore[idx];
        if (!c || (int)argc < c->minArgs || (int)argc > c->maxArgs) return false;
        *result = c->fn(vm, fr, argc, a, list);
        return true;
    }
    if (idx - VM_NNATIVES < g_vmNatCount) { *result = vmCallImported(vm, fr, &g_vmNat[idx - VM_NNATIVES], argc, a, list); return true; }
    return false;
}
