/* fire VM in C: threads, tasks, actors, `sync globals`, cancellation (docs/spec/VM_C.md "Threads", phase B step 3) - on the thread model of the runtime
 * (L1): fire threads hold the GIL while they run fire code (FIRE_GIL - one system thread per fire thread, the model of a debug build); the interpreter
 * gives it up where the generated code does (the safe points: the entry of a function - pollSignals - and a loop's jump back - pollLoop) and in the
 * waits of the runtime. Everything else is the runtime's: sections, jobs of the main program, mailboxes, channels, completions, cancellation, rule 10
 * (toMainGlobals), the end of the program (mainFinish).
 *
 * Every strand that runs code of the program has a Vm of its own (a fire thread, the home of an actor that is a thread; vmStrandNew): its operand stack,
 * frames, scopes. What belongs to the program - its classes, descriptors, interned type arguments - stays the main program's (`top`). g_vmCur is the
 * strand's (thread-local). A fire thread's global scope holds its own slots; the slots below `sharedCount` are the main program's globals, which the
 * thread reads directly and writes in a section (VM.cs LoadSharedGlobal/StoreSharedGlobal; the generator: G<slot> with sectionScopeEnter).
 *
 * The callbacks of the runtime that have no context (VM_SPIKE point 1): the body of a thread (`void (*)(const Value*)`) and the function of a message
 * (MsgFn) get what the VM needs in the values they are given - the function to run and the number of arguments as two numbers in front of the arguments
 * (inert values: the runtime copies, counts and releases them like any number); `catch threads`/`catch terminate` handlers are a fixed pool of
 * trampolines (VM_CATCHES), each with the function of its index. */

static void vmFreeDead(Vm* vm);
static Vm* g_vmMain;              /* the main program of the script that runs (its globals are the shared domain) */

/* What `fire` hands its thread besides the values: the flag that it runs, the line that ends its traces (malloc'ed, NULL: none). */
typedef struct VmFireStart { fire_atomic32 started; char* trailer; } VmFireStart;

/* ---- strands ---- */
static Vm* vmStrandNew(Vm* top, uint32_t sharedCount) {
    Vm* vm = (Vm*)malloc(sizeof(Vm));
    if (!vm) outOfMemory();
    *vm = *top;   /* (what belongs to the program: prog, descriptors, class ids, argv) */
    vm->stack = (Value*)malloc(VM_STACK * sizeof(Value));
    vm->frames = (VmFrame*)calloc(VM_FRAMES, sizeof(VmFrame));
    if (!vm->stack || !vm->frames) outOfMemory();
    vm->sp = 0; vm->nframes = 0;
    vm->freeScopes = NULL;
    vm->pinned = NULL;
    vm->dbg = NULL;   /* (the debugger: its own, vm_debug.c) */
    vm->failed = 0; vm->quiet = 0; vm->terminated = 0; vm->exitCode = 0;
    vm->ret = Undef();
    vm->copyMask = 0;
    vm->pendingTArgs = NULL; vm->pendingTArgsFor = 0; vm->pendingExplicit = 0; vm->pendingTArgsN = 0; vm->pendingArgNames = NULL;
    vm->initNext = false;
    vm->dead = NULL;
    vm->exitJump = NULL;
    vm->nfin = 0;
    vm->top = top;
    vm->sharedCount = sharedCount;
    vm->isStrand = 1;
    vm->awaitNext = 0;
    vm->threadTrailer = NULL;
    vm->dgSeen = NULL;
    vm->global = scNew(vm, NULL);
    scReserve(vm->global, sharedCount + 16);
    while (vm->global->n < sharedCount) vm->global->slots[vm->global->n++] = Undef();   /* (placeholders: the main program's slots) */
    vm->global->hi = vm->global->n;
    return vm;
}
/* The end of a strand: its frames, scopes and what they own go (destructors run - not after a failure, `keepGlobal`: the global scope's objects live on
 * in the graveyard, an exception among them is on its way to the main program). */
static void vmStrandFree(Vm* vm, bool keepGlobal) {
    Handler* stop = NULL;
    while (g_handlers && g_handlers != stop) {
        Handler* h = g_handlers;
        popHandler();
        if (h->fn == vmCatchFn) free(h);
    }
    g_pending = NULL;
    clearUnwind();
    vmFreeDead(vm);
    while (vm->nframes > 0) {
        VmFrame* f = &vm->frames[vm->nframes - 1];
        vmLeaveTo(vm, f, vmStopOf(vm, f));
        vm->nframes--;
    }
    vm->sp = 0;
    if (keepGlobal) {
        uint32_t i;
        leave(&vm->global->temp);
        graveyardAdd(&vm->global->own);
        for (i = 0; i < vm->global->hi; i++) release(vm->global->slots[i]);
        vm->global->n = vm->global->hi = 0;
        vm->global->nextFree = vm->freeScopes; vm->freeScopes = vm->global;
    } else scLeave(vm, vm->global);
    vmFreePinned(vm);
    while (vm->freeScopes) { VmScope* s = vm->freeScopes; vm->freeScopes = s->nextFree; free(s->slots); free(s->names); free(s); }
    vmDbgFree(vm);
    vmDgSeenFree(vm);
    free(vm->stack);
    free(vm->frames);
    free(vm->threadTrailer);
    free(vm);
}

/* The body of a fire thread (the runtime's ThreadStart.fn, on the new thread with the GIL): args[0] the function, args[1] the main program's global
 * slots | the number of arguments << 32, args[2] its VmFireStart (the flag it sets when it runs, the line of its traces), then the arguments (the copies of `taking`, what `handover` moved, the `with` values). */
static void vmThreadBody(const Value* args) {
    VmProto* p = (VmProto*)(uintptr_t)args[0].i;
    uint32_t shared = (uint32_t)(args[1].i & 0xFFFFFFFF), argc = (uint32_t)(args[1].i >> 32), i;
    Vm* vm = vmStrandNew(g_vmMain, shared);
    jmp_buf jb;
    vm->threadTrailer = ((VmFireStart*)(uintptr_t)args[2].i)->trailer;   /* (its traces end with the line of its start: vm_trace.c) */
    g_vmCur = vm;
    g_globalOwn = &vm->global->own;
    adoptTravel(&vm->global->own);   /* (the copies `taking` made: the thread's own) */
    for (i = 0; i < argc; i++) scDefine(vm->global, args[3 + i]);
    fire_atomic_storeRelease(&((VmFireStart*)(uintptr_t)args[2].i)->started, 1);   /* (it runs: the `fire` that started it goes on) */
    {
        VmFrame* f = &vm->frames[0];
        memset(f, 0, sizeof *f);
        f->chunk = p->chunk; f->ip = p->chunk->code; f->scope = vm->global; f->base = vm->global; f->self = Undef();
        f->spBase = 0; f->kind = FR_TOP; f->retMode = RM_PUSH; f->proto = NULL;
        vm->nframes = 1;
    }
    vm->exitJump = &jb;
    if (setjmp(jb) == 0) {
        vmRun(vm, 0);
    } else {
        /* a run-time error of the runtime on this thread (fatal printed it): the thread ends, the program with it (FireRuntime: RequestTerminate(1)) */
        vm->failed = 2;
        if (!vm->quiet) { vm->quiet = 1; g_zombies.quiet++; }
        g_zombies.depth = 0;
        g_threadFailed = 1;
        terminateRequest(Int(1));
    }
    vm->exitJump = NULL;
    clearUnwind();
    g_leaving = 1;   /* (the thread's end: no more signals while its global scope goes) */
    {
        int quiet = vm->quiet;
        vmStrandFree(vm, g_threadFailed != 0);
        if (g_pool.top) poolRelease(0);   /* (what the thread still had in its temporary pool: threadEntry frees the pool as it is) */
        if (quiet) g_zombies.quiet--;
    }
    g_vmCur = NULL;
    g_globalOwn = NULL;
}

/* `fire [dedicated] [taking ...] [with ...] { body }` (VM.cs OpCode.Fire, CppGenerator OpCode.Fire): the values on the stack - the `taking` ones
 * (the last `handover` ones moved, the others copied), then the `with` ones (a channel: an end of its own). False: an exception (a [NoCopy] value). */
static bool vmFire(Vm* vm, VmFrame* fr, VmProto* body, uint32_t globalSlots, uint32_t taking, uint32_t flags, uint32_t handover) {
    uint32_t with = (flags & 1) == 0 ? 0 : ((flags >> 2) > 1 ? (flags >> 2) : 1);
    uint32_t argc = taking + with, copies = taking - handover, i;
    Value* first = vm->stack + vm->sp - argc;
    Value av[3 + 256];
    VmFireStart start;
    OwnList* travel;
    if (argc > 256) fatal("fire: too many values for the thread.");
    if (taking > 0 && !((copies == 0 || takingNoCopyFree(first, copies)) && (handover == 0 || handoverFree(first + copies, handover)))) return false;
    travel = newTravel();
    for (i = 0; i < copies; i++) av[3 + i] = takeCopy(first[i], travel);
    if (handover > 0) handOverAll(first + copies, av + 3 + copies, handover, travel);
    for (i = 0; i < with; i++) {
        Value w = first[taking + i];
        VmClass* c = vmClassOfValue(vm, w);
        if (c && leafAlive(w) && (!strcmp(c->name, "Channel") || !strcmp(c->name, "ChannelEnd"))) {
            /* (a channel: the thread gets an end of its own on the same queue - step 9) */
            VmProto* m = vmFindMethod(vm, c, vmName(vm->prog, "__End"), 0, NULL, w, NULL);
            if (m) {
                Value e = vmCallNested(vm, m, 0, NULL, w, FR_NESTED);
                Owned* eo;
                if (g_unwind.active) { destroyList(travel); free(travel); return false; }
                eo = ownedOf(e);
                if (eo) { if (eo->owner) ownUnlink(eo); ownLink(travel, eo); }
                release(e);   /* (the count of its trip: the thread's slot counts it) */
                w = e;
            }
        }
        av[3 + taking + i] = w;
    }
    (void)fr;
    av[0] = VM_PTRV(body);
    av[1] = Int((int64_t)globalSlots | ((int64_t)argc << 32));
    ensureDomain();
    av[2] = VM_PTRV(&start);
    fire_atomic_store(&start.started, 0);
    start.trailer = vmTraceThreadLine(vm, fr);   /* (the thread owns it from now on) */
    if (flags & 2) fireThreadDedicated(vmThreadBody, av, argc + 3, travel);
    else fireThread(vmThreadBody, av, argc + 3, travel);
    /* (the thread gets the GIL once before the one that started it goes on: what it does first does not race against how fast the starter runs - a
     * `terminate` at its start stops a short loop of the starter, as in the C# VM, whose threads begin while the interpreter is slower) */
    gilRelease();
    while (!fire_atomic_loadAcquire(&start.started)) fire_plat_sleepMs(0);
    gilAcquire();
    return true;
}

/* ---- the main program's globals from a fire thread ---- */
static VmScope* vmGlobalsOf(Vm* vm, uint32_t slot) { return slot < vm->sharedCount ? vm->top->global : vm->global; }
/* A store into a global variable of the main program from a thread (a slot below sharedCount): one section; an object goes there by rule 10 (fresh or
 * taken: moved, else the ArgumentException). False: the exception is set. */
static bool vmStoreShared(Vm* vm, VmScope* s, uint32_t slot, Value v, const char* what) {
    bool ss = sectionScopeEnter(true), ok = true;
    Value n = v;
    ok = toMainGlobals(&n, NULL, what);
    if (ok) {
        Value* x = scSlot(s, slot);   /* (after the section began: the main program may have grown its scope meanwhile) */
        Value old = *x;
        retain(n);
        *x = n;
        release(old);
    }
    sectionScopeExit(ss);
    (void)vm;
    return ok;
}

/* ---- messages (actors, the methods of the globals' objects from a thread): MsgFn with the method and the count in front of the arguments ---- */
static Value vmMsgFn(Value self, const Value* args, OwnList* list) {
    Vm* vm = g_vmCur;
    VmProto* m = (VmProto*)(uintptr_t)args[0].i;
    int argc = (int)args[1].i;
    Value r;
    (void)list;
    OwnList* al = g_argIn;
    int i;
    int32_t base;
    if (!vm) return Undef();
    g_argIn = NULL;
    if (vm->sp + (uint32_t)argc + 64 > VM_STACK) fatal("Stack overflow: the operand stack is full.");
    for (i = 0; i < argc; i++) vm->stack[vm->sp++] = args[2 + i];
    vmPushCall(vm, m, argc, 0, self, FR_NESTED);
    {   /* what came with the message (its unclaimed arguments - m.own of the runtime, the argument list of a job) is the call's: the method keeps what it
         * takes, the rest dies with its scope (SPEC 2.4; the generated code: the callee takes the argument list on entry) */
        VmScope* sc = vm->frames[vm->nframes - 1].base;
        for (i = 0; i < argc; i++) {
            Owned* o = ownedOf(args[2 + i]);
            if (o && o->owner && (o->owner == al || (o->owner->mark == 0xFFFFFFFFu && !o->owner->home && !o->owner->holder))) { ownUnlink(o); ownLink(&sc->own, o); }
        }
    }
    base = vm->nframes - 1;
    vm->ret = Undef();
    vmRun(vm, base);
    if (g_unwind.active) return Undef();
    r = vm->ret;
    return r;
}
/* A call of method `m` on `recv` (the stack: recv, then the arguments) where threads matter: an actor (a message - `await`: with a way back), an object of
 * the main program's globals from a fire thread (a job of the main program, the thread waits). 1: done, the result replaces receiver and arguments; 0: an
 * ordinary call; -1: an exception. */
/* The arguments of a call that goes to another strand (a message, a job): what is unclaimed - a `new`, a call's result, a copy, in the statement's
 * temporaries - travels in the argument list of the call (AL_MARK: actorPost and globalsCall take it from there, SPEC 2.4); what is left in it after the
 * call goes back to the temporaries. */
static void vmArgList(OwnList* al, const Value* a, uint32_t argc) {
    uint32_t i;
    *al = scopeList(AL_MARK);
    for (i = 0; i < argc; i++) {
        Owned* o = ownedOf(a[i]);
        if (o && o->owner && o->owner->mark == 0xFFFFFFFFu && o->owner != al) { ownUnlink(o); ownLink(al, o); }
    }
}
static void vmArgListEnd(OwnList* al, Vm* vm) {
    OwnList* temp = &vm->frames[vm->nframes - 1].scope->temp;
    while (al->head) { Owned* o = al->head; ownUnlink(o); ownLink(temp, o); }
}
/* CopyArgs of a call that goes to another strand (vmPushCall does it for an ordinary one): `copy x`, `take x` as an argument - the copy or what is
 * taken is an unclaimed temporary of the caller, it travels with the call (vmArgList). False: an exception. */
static bool vmThreadArgs(Vm* vm, VmProto* m, Value* a, uint32_t argc) {
    uint64_t mask = vm->copyMask;
    OwnList* temp = &vm->frames[vm->nframes - 1].scope->temp;
    uint32_t i;
    vm->copyMask = 0;
    for (i = 0; i < argc && i < 16 && mask; i++) {
        uint32_t bits = (uint32_t)(mask >> (4 * i)) & 15u;
        if (bits == 3) { if (!((m->refMask >> i) & 1)) a[i] = ptrRead(a[i]); }
        else if (bits == 1 || bits == 2) { a[i] = copyValue(a[i], bits == 2, temp); if (g_unwind.active) return false; }
        else if (bits == 5 && ownedOf(a[i]) && takeAlive(a[i])) takeToList(a[i], temp);
    }
    return !g_unwind.active;
}
static int vmThreadCall(Vm* vm, VmProto* m, Value recv, uint32_t argc, const char* name) {
    OwnList al;
    bool aw = vm->awaitNext != 0;
    Value av[2 + 64];
    Value* a = vm->stack + vm->sp - argc;
    Value r;
    uint32_t i;
    vm->awaitNext = 0;
    if (argc > 64) return 0;
    av[0] = VM_PTRV(m);
    av[1] = Int((int64_t)argc);
    if ((asObj(recv)->h.flags & 8) && leafAlive(recv)) {
        if (aw && actorAtHome(recv)) return 0;
        if (vm->pendingArgNames) {   /* (VM.RejectArgNames) */
            vm->pendingArgNames = NULL;
            fireError("ArgumentException", "Named arguments are not possible for a method of an actor.");
            return -1;
        }
        if (!vmThreadArgs(vm, m, a, argc)) return -1;
        for (i = 0; i < argc; i++) av[2 + i] = a[i];
        vmArgList(&al, a, argc);
        if (aw) r = actorAwait(recv, vmMsgFn, av, argc + 2);
        else { actorSend(recv, vmMsgFn, av, argc + 2); r = Undef(); }
        vmArgListEnd(&al, vm);
    } else if (g_isThread && g_secDepth == 0 && g_domainOn && leafAlive(recv) && sharedValue(recv) && !m->readsOnly && !m->isStatic) {
        if (vmMainAway()) {   /* (the main program is in a native: the call runs in a section of this thread - vm_pkg.c) */
            bool ss = sectionScopeEnter(true);
            r = vmCallNested(vm, m, (int)argc, a, recv, FR_NESTED);
            sectionScopeExit(ss);
        } else {
            if (!vmThreadArgs(vm, m, a, argc)) return -1;
            for (i = 0; i < argc; i++) av[2 + i] = a[i];
            vmArgList(&al, a, argc);
            g_argIn = &al;   /* (what is fresh goes with the call: rule 10) */
            r = globalsCall(recv, vmMsgFn, av, argc + 2, name);
            g_argIn = NULL;
            vmArgListEnd(&al, vm);
        }
    } else return 0;
    vm->sp -= argc + 1;
    if (g_unwind.active) return -1;
    vm->stack[vm->sp++] = r;
    adopt(r, &vm->frames[vm->nframes - 1].scope->temp);
    return 1;
}

/* ---- `catch threads (e) { }` / `catch terminate (v) { }`: the runtime calls a function without context - a pool of trampolines ---- */
#define VM_CATCHES 16
static VmProto* g_vmCatchProto[VM_CATCHES + 1];
static uint32_t g_vmCatchCount;
static Value vmCatchCall(uint32_t k, Value e) {
    Vm* vm = g_vmCur;
    VmProto* p = g_vmCatchProto[k];
    Value r;
    if (!vm || !p) return Undef();
    r = vmCallNested(vm, p, p->nparams >= 1 ? 1 : 0, &e, Undef(), FR_NESTED);
    release(r);
    return Undef();
}
#define VM_CATCH_FN(k) static Value vmCatch##k(Value e) { return vmCatchCall(k, e); }
VM_CATCH_FN(0) VM_CATCH_FN(1) VM_CATCH_FN(2) VM_CATCH_FN(3) VM_CATCH_FN(4) VM_CATCH_FN(5) VM_CATCH_FN(6) VM_CATCH_FN(7)
VM_CATCH_FN(8) VM_CATCH_FN(9) VM_CATCH_FN(10) VM_CATCH_FN(11) VM_CATCH_FN(12) VM_CATCH_FN(13) VM_CATCH_FN(14) VM_CATCH_FN(15) VM_CATCH_FN(16)
static Value (*const g_vmCatchFns[VM_CATCHES + 1])(Value) = { vmCatch0, vmCatch1, vmCatch2, vmCatch3, vmCatch4, vmCatch5, vmCatch6, vmCatch7,
    vmCatch8, vmCatch9, vmCatch10, vmCatch11, vmCatch12, vmCatch13, vmCatch14, vmCatch15, vmCatch16 };
static void vmRegisterCatch(Vm* vm, VmProto* p, bool threads, const char* typeName) {
    if (!threads) { g_vmCatchProto[VM_CATCHES] = p; registerTerminateCatch(g_vmCatchFns[VM_CATCHES]); return; }
    if (g_vmCatchCount >= VM_CATCHES) fatal("the VM in C takes at most 16 'catch threads' handlers.");
    g_vmCatchProto[g_vmCatchCount] = p;
    {
        int32_t type = -1;
        if (typeName && typeName != vmName(vm->prog, "Exception")) {
            VmClass* c = vmClassNamed(vm->prog, typeName);
            type = c ? (int32_t)c->id + 1 : 0x7FFFFFFF;
        }
        registerThreadsCatch(type, g_vmCatchFns[g_vmCatchCount++]);
    }
}

/* `fire global [on x] f(args)` / `await on x f(args)` (OpCode.PostGlobal, CppGenerator): the lambda on top, the arguments below it. False: an exception. */
static bool vmPostGlobal(Vm* vm, uint32_t flags, uint32_t jobWith) {
    uint32_t argc = flags & 0x3F, i;
    bool onTarget = (flags & 0x80) != 0, awaitJob = (flags & 0x40) != 0;
    Value* first = vm->stack + vm->sp - argc - 1;
    Value lam = vm->stack[vm->sp - 1];
    Value av[64];
    OwnList* list = &vm->frames[vm->nframes - 1].scope->temp;
    if (argc > 64) fatal("fire global: too many arguments.");
    for (i = 0; i < argc; i++) av[i] = first[i];
    if (argc == 0) av[0] = Undef();
    if (awaitJob) {
        Value r = awaitOnJob(lam, av, argc);
        vm->sp -= argc + 1;
        if (g_unwind.active) return false;
        adopt(r, list);
        vm->stack[vm->sp++] = r;
        return true;
    }
    if (jobWith > 0) {
        for (i = argc - jobWith; i < argc; i++) {
            VmClass* c = vmClassOfValue(vm, av[i]);
            if (c && leafAlive(av[i]) && (!strcmp(c->name, "Channel") || !strcmp(c->name, "ChannelEnd"))) {
                VmProto* m = vmFindMethod(vm, c, vmName(vm->prog, "__End"), 0, NULL, av[i], NULL);
                if (m) {
                    Value e = vmCallNested(vm, m, 0, NULL, av[i], FR_NESTED);
                    if (g_unwind.active) { vm->sp -= argc + 1; return false; }
                    adopt(e, list);
                    av[i] = e;
                }
            }
        }
        postJobWith(lam, av, argc, onTarget, jobWith, list);
    } else postJobEx(lam, av, argc, onTarget, NULL);
    vm->sp -= argc + 1;
    return !g_unwind.active;
}

/* The natives of the threads (natives.def 9, 10, 12-20, 22-36: the generator calls the runtime's fire_* for them). False: not one of them. */
static bool vmThreadNative(Vm* vm, VmFrame* fr, uint32_t idx, uint32_t argc, Value* a, Value* result) {
    OwnList* list = &fr->scope->temp;
    (void)vm;
    switch (idx) {
        case 9: if (argc != 3) return false; *result = awaitRun(a[0], a[1], a[2], list); return true;
        case 10: if (argc != 1) return false; *result = fire_lambdaHandover(a[0], list); return true;
        case 12: if (argc != 1) return false; *result = fire_cancel_new(a[0]); return true;
        case 13: if (argc != 1) return false; *result = fire_cancel_release(a[0]); return true;
        case 14: if (argc != 1) return false; *result = fire_cancel_state(a[0]); return true;
        case 15: if (argc != 0) return false; *result = fire_cancel_current(); return true;
        case 16: if (argc != 1) return false; *result = fire_cancel_ref(a[0]); return true;
        case 17: if (argc != 1) return false; *result = fire_cancel_cancel(a[0]); return true;
        case 18: if (argc != 2) return false; *result = fire_cancel_after(a[0], a[1]); return true;
        case 19: if (argc != 1) return false; *result = fire_cancel_time(a[0]); return true;
        case 20: if (argc == 3) *result = fire_fd_wait(a[0], a[1], a[2]); else if (argc == 4) *result = fire_fd_wait_or(a[0], a[1], a[2], a[3]); else return false; return true;
        case 22: if (argc != 0) return false; *result = fire_completion_new(); return true;
        case 23: if (argc != 1) return false; *result = fire_completion_ref(a[0]); return true;
        case 24: if (argc != 1) return false; *result = fire_completion_release(a[0]); return true;
        case 25: if (argc != 1) return false; *result = fire_completion_done(a[0]); return true;
        case 26: if (argc != 3) return false; *result = fire_completion_set(a[0], a[1], a[2]); return true;
        case 27: if (argc != 2) return false; *result = fire_completion_wait(a[0], a[1], list); return true;
        case 28: if (argc != 1) return false; *result = fire_chan_new(a[0]); return true;
        case 29: if (argc != 1) return false; *result = fire_chan_ref(a[0]); return true;
        case 30: if (argc != 1) return false; *result = fire_chan_release(a[0]); return true;
        case 31: if (argc != 1) return false; *result = fire_chan_close(a[0]); return true;
        case 32: if (argc != 1) return false; *result = fire_chan_closed(a[0]); return true;
        case 33: if (argc != 1) return false; *result = fire_chan_count(a[0]); return true;
        case 34: if (argc != 3) return false; *result = fire_chan_send(a[0], a[1], a[2], list); return true;
        case 35: if (argc != 3) return false; *result = fire_chan_receive(a[0], a[1], a[2], list); return true;
        case 36: if (argc != 3) return false; *result = fire_chan_watch(a[0], a[1], a[2]); return true;
        default: break;
    }
    /* `Sleep` of the time package: a function the host runs itself (HostNatives: a wait that terminate ends, the main program serves its queue meanwhile;
     * packages/time fire_tm_sleep) */
    if (idx >= VM_NNATIVES && idx - VM_NNATIVES < vm->prog->npackageNatives && argc == 1 && !strcmp(vm->prog->packageNatives[idx - VM_NNATIVES], "Sleep")) {
        int64_t ticks;
        char text[160];
        *result = Undef();
        if (timeTicksOf(a[0], &ticks)) { sleepTicks(ticks); if (!g_unwind.active) (void)pollSignals(); return true; }
        if (a[0].kind == K_Class) snprintf(text, sizeof text, "Sleep expects a TimeSpan, got: %s.", className(asObj(a[0])->cls));
        else if (a[0].kind == K_Int || a[0].kind == K_Float) snprintf(text, sizeof text, "'%s' is not a unit of time.", g_ud[a[0].unit].name);
        else snprintf(text, sizeof text, "Sleep expects a TimeSpan, a time value or milliseconds, got: %s.", kindName(a[0]));
        fireError("TimeException", text);
        return true;
    }
    switch (idx) {
        default: return false;
    }
}

/* ---- the program's start and end ---- */
static void vmThreadsBegin(Vm* vm) {
    static bool once;
    if (!once) { once = true; fire_threadsInit(); }
    gilAcquire();
    g_vmMain = vm;
    vm->top = vm;
    g_domainRoot = &vm->global->own;
    g_attnMask = 7u;
    g_tick = 4096;
}
/* The end of the main program (HaltCode): it waits for its threads (mainFinish - `catch terminate` runs there), then the state of the threads goes,
 * for the next script of this process. */
static void vmThreadsFinish(Vm* vm) {
    if (vm->failed && !terminateRequested()) terminateRequest(Int(1));   /* (an exception nothing caught ends the program: its threads leave) */
    mainFinish();
}
static bool vmNoThreads(void* ctx) { (void)ctx; return fire_atomic_load(&g_threadsLive) == 0; }
static void vmThreadsEnd(void) {
    uint32_t i;
    /* (threads still there - the end of the main program jumped out of mainFinish, a fatal error: they leave like at terminate, then they are joined) */
    if (fire_atomic_load(&g_threadsLive) > 0) {
        if (!terminateRequested()) terminateRequest(Int(1));
        clearUnwind();
        (void)blockUntil(vmNoThreads, NULL, false, steadyMs() + 10000);
    }
    {
        PtrVec list;
        fire_mutex_lock(&g_thrM);
        list = g_threadList;
        g_threadList.items = NULL; g_threadList.count = 0; g_threadList.cap = 0;
        fire_mutex_unlock(&g_thrM);
        gilRelease();   /* (a thread that ends gives the GIL back last: the join waits for it without it) */
        for (i = 0; i < list.count; i++) fire_thread_join((fire_thread*)list.items[i]);
        gilAcquire();
        free(list.items);
    }
    { Value e; while (pendingExcPop(&e)) { (void)e; } }   /* (what no `catch threads` took: it lives in the graveyard, which goes now) */
    /* (the graveyard: what threads that failed left - their exceptions went to the main program) */
    for (i = 0; i < g_graveyard.count; i++) { OwnList* l = (OwnList*)g_graveyard.items[i]; destroyList(l); free(l); }
    free(g_graveyard.items); g_graveyard.items = NULL; g_graveyard.count = g_graveyard.cap = 0;
    if (terminateRequested()) { Value v = g_terminate.value; if (!ownedOf(v)) release(v); }
    destroyList(&g_terminateOwn);
    fire_atomic_store(&g_terminate.requested, 0);
    g_terminate.value = Undef();
    fire_atomic_store(&g_attn, 0);
    g_leaving = 0;
    g_domainOn = false;
    g_domainRoot = NULL;
    free(g_threadsCatches); g_threadsCatches = NULL; g_threadsCatchCount = g_threadsCatchCap = 0;
    g_terminateFn = NULL;
    g_vmCatchCount = 0;
    for (i = 0; i <= VM_CATCHES; i++) g_vmCatchProto[i] = NULL;
    g_globalJobs = GJ_SAFEPOINTS;
    fire_atomic_store(&g_standstill, 0);
    g_vmMain = NULL;
    gilRelease();
}
