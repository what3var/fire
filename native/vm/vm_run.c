/* fire VM in C: the interpreter. It runs the bytecode of a loaded program (vm_load.c) on the runtime of the native backend: every operation is the
 * function the generated C calls for it (add, divide, opEq, concat, print, newObject, leave, claimToList, throwValue, resumeThrow, arrayGet, ...), the
 * frames, the scopes with their dynamic slots (BYTECODE 3) and the dispatch by name are the VM's own (vm_core.c). VM.cs is the reference for what each
 * opcode does; CppGenerator says which function of the runtime does it.
 *
 * Exceptions: the runtime runs a catch block on top of the throw site (a callback, Handler.fn); here the callback runs the catch block in a nested run of
 * the interpreter on a pseudo frame of the function that owns the try. Leaving the catch (its scope ends, the jump behind the try), `return` in it and
 * `resume` set the runtime's unwinding state (exitJump, exitReturn, resumeThrow) - the interpreter then unwinds its frames to the frame of the target, like
 * the generated code returns through its functions. A `finally` runs on the operand stack like in the C# VM (a payload and the kind of completion under it;
 * EndFinally goes on with it). A handler at the bottom of every script catches what nothing else catches: the script ends there, its process does not (L6). */

#define U16(p) ((uint32_t)(p)[0] | ((uint32_t)(p)[1] << 8))
#define SYNC() do { fr->ip = ip; vm->sp = (uint32_t)(sp - st); } while (0)
#define RELOAD() do { fr = &vm->frames[vm->nframes - 1]; ip = fr->ip; sp = st + vm->sp; } while (0)
#define CHECK() do { if (FIRE_UNLIKELY(g_unwind.active)) goto unwind; } while (0)
#define PUSH(v) (*sp++ = (v))
#define POP() (*--sp)
#define TOP() (sp[-1])
#define KNAME(i) (fr->chunk->kname[(i)])
#define PERF (prog->mode == 2)

enum { FIN_K_NORMAL = 0, FIN_K_THROW = 1, FIN_K_RETURN = 2, FIN_K_JUMP = 3, FIN_K_LEAVE = 4 };
/* A safe point (CppGenerator: a jump back is one with threads - pollLoop): the other threads get the GIL now and then, the main program serves its queue,
 * leave/terminate start here. `len`: the size of the jump instruction (its address is ip - len). */
#define VM_LOOP_POINT(to, len) do { if ((to) <= (uint32_t)(ip - (len) - fr->chunk->code)) { SYNC(); if (FIRE_UNLIKELY(pollLoop())) goto unwind; } } while (0)

static void* vmIc(VmChunk* c, const uint8_t* at, uint32_t cls, VmProgram* p, bool* hit) {
    uint32_t off = (uint32_t)(at - c->code);
    if (!c->icCls) {
        c->icCls = (uint32_t*)vmAlloc(&p->arena, (c->ncode + 1) * sizeof(uint32_t));
        c->icPtr = (void**)vmAlloc(&p->arena, (c->ncode + 1) * sizeof(void*));
        memset(c->icCls, 0xFF, (c->ncode + 1) * sizeof(uint32_t));
    }
    *hit = c->icCls[off] == cls;
    return c->icPtr[off];
}
static void vmIcSet(VmChunk* c, const uint8_t* at, uint32_t cls, void* v) { uint32_t off = (uint32_t)(at - c->code); c->icCls[off] = cls; c->icPtr[off] = v; }

static FIRE_COLD void vmMethodNotFound(VmClass* c, const char* name, int argc) {
    char text[400];
    char ar[200];
    VmClass* k;
    size_t at = 0;
    int seen[64], ns = 0, i, lo;
    bool exists = false;
    for (k = c; k; k = k->base) {
        uint32_t m, o;
        for (m = 0; m < k->nmethods; m++) if (k->methods[m].name == name) {
            exists = true;
            for (o = 0; o < k->methods[m].nov && ns < 64; o++) {
                int a = k->methods[m].ov[o]->nparams, j;
                bool dup = false;
                for (j = 0; j < ns; j++) if (seen[j] == a) dup = true;
                if (!dup) seen[ns++] = a;
            }
        }
    }
    if (!exists) { snprintf(text, sizeof text, "Method '%s' not found on '%s'.", name, c->name); fireError("MissingMemberException", text); return; }
    ar[0] = 0;
    for (lo = -1;;) {   /* (ascending) */
        int next = 0x7FFFFFFF;
        for (i = 0; i < ns; i++) if (seen[i] > lo && seen[i] < next) next = seen[i];
        if (next == 0x7FFFFFFF) break;
        at += (size_t)snprintf(ar + at, sizeof ar - at, "%s%d", at ? ", " : "", next);
        lo = next;
        if (at >= sizeof ar) break;
    }
    snprintf(text, sizeof text, "Method '%s' on '%s' has no overload with %d argument(s) (available: %s argument(s)).", name, c->name, argc, ar);
    fireError("ArgumentException", text);
}
static FIRE_COLD void vmCtorNotFound(VmClass* c, int argc) {
    char text[400], ar[200];
    size_t at = 0;
    int lo = -1;
    ar[0] = 0;
    for (;;) {
        int next = 0x7FFFFFFF;
        uint32_t i;
        for (i = 0; i < c->nctors; i++) if (c->ctors[i].arity > lo && c->ctors[i].arity < next) next = c->ctors[i].arity;
        if (next == 0x7FFFFFFF) break;
        at += (size_t)snprintf(ar + at, sizeof ar - at, "%s%d", at ? ", " : "", next);
        lo = next;
        if (at >= sizeof ar) break;
    }
    snprintf(text, sizeof text, "Class '%s' has no constructor with %d argument(s) (available: %s argument(s)).", c->name, argc, ar);
    fireError("ArgumentException", text);
}
static FIRE_COLD void vmAccessDenied(const char* what, const char* name, VmClass* declaring, uint8_t access, const char* tail) {
    char text[400];
    snprintf(text, sizeof text, "%s '%s' of '%s' is %s and cannot be %s from here.", what, name, declaring->name, vmAccessName(access), tail);
    accessDenied(text);
}

/* GuardLocal/GuardTop: a value going into a declaration int/float/bool/string (DeclaredKind) - the runtime's guardSlot does the conversion and the
 * exception. True when it threw. */
static const fire_char16 g_vmUnitlessD[] = {117, 110, 105, 116, 108, 101, 115, 115, 0};
static const Str g_vmUnitless = {{IMMORTAL, R_Str}, 8, g_vmUnitlessD};
static bool vmGuard(Value* v, uint32_t required, const char* what, bool slot) {
    static const uint8_t kinds[] = { K_Bool, K_Int, K_Float, K_Char, K_String, K_Word };
    uint32_t tag = (required & 0x1F);
    uint8_t kind;
    if (tag == 0 || tag > 6) return false;
    kind = kinds[tag - 1];
    if (kind == K_String) {
        if (v->kind == K_String || (v->kind == K_Undefined && !(required & 0x20))) return false;
        {   /* (a slot holds a count of the text: the old value lets go of it) */
            Value old = *v;
            Value t = guardStr(*v);
            if (g_unwind.active) return true;   /* (ToString() threw: the slot keeps what it held - the text it may have made is the pool's) */
            *v = t;
            if (slot) { retain(t); release(old); }
            return false;
        }
    }
    if (v->kind == kind && (!(required & 0x80) || v->unit == 0) && (kind != K_Int || v->reserved == 0)) return false;
    if (v->kind == K_Undefined && !(required & 0x20)) return false;
    return guardSlot(v, kind, (uint8_t)(((required & 0x80) ? 1 : 0) | ((required & 0x20) ? 2 : 0)), what ? what : "A value", &g_vmUnitless);
}

/* `x is of T` for a name of a class, an interface, an enum (VM.IsOfType) */
static bool vmIsOfName(Vm* vm, Value v, const char* t) {
    if (!strcmp(t, "bool")) return v.kind == K_Bool;
    if (!strcmp(t, "int")) return v.kind == K_Int && v.reserved == 0;
    if (!strcmp(t, "float")) return v.kind == K_Float;
    if (!strcmp(t, "char")) return v.kind == K_Char;
    if (!strcmp(t, "word")) return v.kind == K_Word;
    if (!strcmp(t, "string")) return v.kind == K_String;
    if (!strcmp(t, "undefined")) return v.kind == K_Undefined;
    if (!strcmp(t, "class")) return v.kind == K_Class;
    if (!strcmp(t, "lambda")) return v.kind == K_Lambda;
    if (v.kind == K_Int && v.reserved) { const FireEnum* e = enumOf(v); return e && !strcmp(e->name, t); }
    if ((v.kind == K_Array || v.kind == K_Buffer) && (!strcmp(t, "IEnumerable") || !strncmp(t, "IEnumerable`", 12))) return true;
    if (v.kind != K_Class) return false;
    if (!strcmp(t, "Exception")) return true;
    {
        VmClass* c = vmClassOfValue(vm, v);
        char plain[160];
        const char* lt = strchr(t, '<');
        if (!c) return false;
        if (vmClassIs(c, t)) return true;
        if (lt) {   /* `List<int>`: the class by its plain name (the type arguments: VM.TypeNameIsOf) */
            size_t n = (size_t)(lt - t);
            int commas = 1;
            const char* q;
            for (q = lt; *q; q++) if (*q == ',') commas++;
            if (n + 8 < sizeof plain) { memcpy(plain, t, n); snprintf(plain + n, sizeof plain - n, "`%d", commas); if (vmClassIs(c, plain)) return true; }
        }
    }
    return false;
}

/* DeclaredType (a declaration with a class, an interface or a type parameter: GuardTopType/GuardLocalType, FieldInfo.RequiredType, an overload `g:...`):
 * the text for messages up to \x01, then `@k@Class` \x03 for the k-th type parameter of a class (`@k@` of the call), then the alternatives (`|`) of
 * conditions (`&`, `in:` before a unit); \x04 in front: `defined`; \x05 behind: the type arguments of a generic class (`Bag<int> b`, DECISIONS D10:
 * separated by \x06, `$k@Class` for a type parameter of the code around, "" for one that is not known). */
typedef struct VmSpec { const char* text; char display[160]; bool nonNull; int typeParam; char owner[160]; const char* alts; const char* targs; struct VmSpec* next; } VmSpec;
static VmSpec* g_vmSpecs;
static VmSpec* vmSpec(Vm* vm, const char* text) {
    VmSpec* sp;
    const char* t = text;
    const char* end;
    size_t n;
    for (sp = g_vmSpecs; sp; sp = sp->next) if (sp->text == text) return sp;
    sp = (VmSpec*)vmAlloc(&vm->prog->arena, sizeof(VmSpec));
    sp->text = text;
    sp->typeParam = -1;
    if (*t == '\x04') { sp->nonNull = true; t++; }
    end = strchr(t, '\x01');
    n = end ? (size_t)(end - t) : strlen(t);
    if (n >= sizeof sp->display) n = sizeof sp->display - 1;
    memcpy(sp->display, t, n);
    sp->display[n] = 0;
    t = end ? end + 1 : t + n;
    if (*t == '@') {
        const char* at = strchr(t + 1, '@');
        const char* pe = strchr(t, '\x03');
        sp->typeParam = atoi(t + 1);
        if (at && pe && at < pe) { size_t k = (size_t)(pe - at - 1); if (k >= sizeof sp->owner) k = sizeof sp->owner - 1; memcpy(sp->owner, at + 1, k); sp->owner[k] = 0; }
        t = pe ? pe + 1 : t + strlen(t);
    }
    {   /* (the alternatives, without the type arguments behind \x05) */
        const char* ta = strchr(t, '\x05');
        const char* ek = strchr(t, '\x02');   /* (an enum's key behind its name: ArrayOfTypeArg) */
        if (ek && (!ta || ek < ta)) ta = ek;
        sp->alts = ta ? vmIntern(vm->prog, t, (size_t)(ta - t)) : t;
        sp->targs = ta && *ta == '\x05' ? ta + 1 : NULL;
    }
    sp->next = g_vmSpecs;
    g_vmSpecs = sp;
    return sp;
}
/* DeclaredType.Holds: one condition (`name`, `in:unit`, `lambda/n`, `int:mm`) of `len` bytes */
static bool vmHolds(Vm* vm, Value v, const char* c, size_t len) {
    char name[200];
    if (len >= sizeof name) return true;
    memcpy(name, c, len);
    name[len] = 0;
    if (!strncmp(name, "in:", 3)) return (v.kind == K_Int || v.kind == K_Float) && unitCompatible(v.unit, vmUnitOfName(vm, name + 3));
    if (!strncmp(name, "lambda/", 7)) return v.kind == K_Lambda && (int)lamOf(v)->nparams == atoi(name + 7);
    if ((!strncmp(name, "int:", 4) || !strncmp(name, "float:", 6))) { char* colon = strchr(name, ':'); *colon = 0; }
    if (!strcmp(name, "any") || !name[0]) return true;
    return vmIsOfName(vm, v, name);
}
static bool vmHoldsAlts(Vm* vm, Value v, const char* alts) {
    const char* a = alts;
    if (!*a) return true;
    for (;;) {
        const char* bar = strchr(a, '|');
        const char* aend = bar ? bar : a + strlen(a);
        bool all = true;
        const char* c = a;
        while (c < aend && all) {
            const char* amp = (const char*)memchr(c, '&', (size_t)(aend - c));
            const char* cend = amp ? amp : aend;
            if (!vmHolds(vm, v, c, (size_t)(cend - c))) all = false;
            c = cend + 1;
        }
        if (all) return true;
        if (!bar) return false;
        a = bar + 1;
    }
}
/* DeclaredType.TypeArgOf: the type argument (its text) that the k-th type parameter of `declaring` stands for in this object (the type arguments of its
 * class, through the type arguments its class gives its base classes); NULL when it is not known. */
static const char* vmTypeArgOf(Vm* vm, Value obj, const char* declaring, int k) {
    VmClass* c = vmClassOfValue(vm, obj);
    const char* args[16];
    uint32_t n = 0, i;
    TArgs ta;
    char plainBuf[160];
    if (!c || !leafAlive(obj)) return NULL;
    ta = objTArgs(asObj(obj));
    {
        uint32_t len = ta ? vmTArgsLen(vm, ta) : 0;
        for (n = 0; n < len && n < 16; n++) args[n] = ta[n] ? ta[n]->display : NULL;
    }
    while (c && strcmp(c->name, declaring) != 0 && strcmp(vmPlainName(c->name, plainBuf, sizeof plainBuf), declaring) != 0) {
        const char* next[16];
        uint32_t m = 0;
        if (!c->baseTypeArgs) { n = 0; }
        else {
            for (i = 0; i < c->nbaseTypeArgs && i < 16; i++) {
                const char* t = c->baseTypeArgs[i];
                if (t && t[0] == '$') { uint32_t j = (uint32_t)atoi(t + 1); next[m++] = j < n ? args[j] : NULL; }
                else next[m++] = t;
            }
            memcpy(args, next, m * sizeof(char*));
            n = m;
        }
        c = c->base;
    }
    if (!c || (uint32_t)k >= n || !args[k] || !args[k][0]) return NULL;
    return args[k];
}
/* DeclaredType.WantedTypeArg: the type argument a declaration `Bag<int> b` asks for at place k (`$k@Class` of the code around resolved); NULL: not known. */
static const char* vmWantedTypeArg(Vm* vm, const char* w, size_t n, Value self, TArgs ctargs, char* buf, size_t cap) {
    const char* at;
    int k;
    if (n == 0) return NULL;
    if (n >= cap) n = cap - 1;
    memcpy(buf, w, n);
    buf[n] = 0;
    if (buf[0] != '$') return buf;
    at = strchr(buf, '@');
    if (!at) return NULL;
    k = atoi(buf + 1);
    if (!at[1]) return ctargs && ctargs[k] && ctargs[k]->display && ctargs[k]->display[0] ? ctargs[k]->display : NULL;
    return vmTypeArgOf(vm, self, at + 1, k);
}
/* The k-th type argument of the declaration's text (NULL: none or not known). */
static const char* vmSpecTArg(Vm* vm, const VmSpec* sp, int k, Value self, TArgs ctargs, char* buf, size_t cap) {
    const char* w = sp->targs;
    int i;
    for (i = 0; i < k && w; i++) { w = strchr(w, '\x06'); if (w) w++; }
    if (!w) return NULL;
    { const char* e = strchr(w, '\x06'); return vmWantedTypeArg(vm, w, e ? (size_t)(e - w) : strlen(w), self, ctargs, buf, cap); }
}
static int vmSpecTArgCount(const VmSpec* sp) { int n = 1; const char* w; if (!sp->targs) return 0; for (w = sp->targs; *w; w++) if (*w == '\x06') n++; return n; }
/* DeclaredType.TypeArgsFit: an object of the declared class whose type arguments are known has those the declaration names - or ones that are of them (a
 * `List<Dog>` is a `List<Animal>`: the list keeps checking what goes in). */
static bool vmTypeArgsFit(Vm* vm, Value v, const VmSpec* sp, Value self, TArgs ctargs) {
    int k, n = vmSpecTArgCount(sp);
    if (v.kind != K_Class || strpbrk(sp->alts, "|&") || !strncmp(sp->alts, "in:", 3)) return true;
    for (k = 0; k < n; k++) {
        char buf[160];
        const char* wanted = vmSpecTArg(vm, sp, k, self, ctargs, buf, sizeof buf);
        const char* has = wanted ? vmTypeArgOf(vm, v, sp->alts, k) : NULL;
        if (wanted && has && strcmp(has, wanted) != 0 && !vmTypeTextIsOf(vm, has, wanted)) return false;
    }
    return true;
}
static bool vmFitsDeclared(Vm* vm, Value v, const char* text, Value self, TArgs ctargs) {
    VmSpec* sp;
    if (!text) return true;
    sp = vmSpec(vm, text);
    if (v.kind == K_Undefined) return !sp->nonNull;
    if (v.kind == K_Class && !leafAlive(v)) return true;
    if (sp->typeParam >= 0) {
        const char* targ = NULL;
        if (sp->owner[0]) targ = vmTypeArgOf(vm, self, sp->owner, sp->typeParam);
        else if (ctargs && ctargs[sp->typeParam] && ctargs[sp->typeParam]->display && ctargs[sp->typeParam]->display[0]) targ = ctargs[sp->typeParam]->display;
        if (targ) return vmHolds(vm, v, targ, strlen(targ));
        if (!sp->alts[0]) return true;
    }
    if (!vmHoldsAlts(vm, v, sp->alts)) return false;
    return !sp->targs || vmTypeArgsFit(vm, v, sp, self, ctargs);
}
/* DeclaredType.DisplayOf a type argument (`int : cm`, `in cm`, the plain name of a class) */
static void vmTArgDisplay(const char* a, char* out, size_t cap) {
    const char* colon = strchr(a, ':');
    if (!strncmp(a, "in:", 3)) snprintf(out, cap, "in %s", a + 3);
    else if (colon && (!strncmp(a, "int:", 4) || !strncmp(a, "float:", 6))) snprintf(out, cap, "%.*s : %s", (int)(colon - a), a, colon + 1);
    else vmPlainName(a, out, cap);
}
/* DeclaredType.Mismatch of `Bag<int> b` given an object of the class with other type arguments: "... is a Bag<int>, not Bag<string>." True: it said so. */
static bool vmTypeArgsFailed(Vm* vm, Value v, const char* text, Value self, TArgs ctargs, const char* what) {
    VmSpec* sp = vmSpec(vm, text);
    char w[200], h[200], text2[500];
    size_t wn = 0, hn = 0;
    int k, n;
    if (!sp->targs || v.kind != K_Class || sp->typeParam >= 0 || !vmHoldsAlts(vm, v, sp->alts)) return false;
    n = vmSpecTArgCount(sp);
    w[0] = h[0] = 0;
    for (k = 0; k < n; k++) {
        char buf[160], d1[160], d2[160];
        const char* wanted = vmSpecTArg(vm, sp, k, self, ctargs, buf, sizeof buf);
        const char* has = vmTypeArgOf(vm, v, sp->alts, k);
        if (wanted) vmTArgDisplay(wanted, d1, sizeof d1); else snprintf(d1, sizeof d1, "any");
        if (has) vmTArgDisplay(has, d2, sizeof d2); else snprintf(d2, sizeof d2, "any");
        wn += (size_t)snprintf(w + wn, wn < sizeof w ? sizeof w - wn : 0, "%s%s", k ? ", " : "", d1);
        hn += (size_t)snprintf(h + hn, hn < sizeof h ? sizeof h - hn : 0, "%s%s", k ? ", " : "", d2);
        if (wn >= sizeof w) wn = sizeof w - 1;
        if (hn >= sizeof h) hn = sizeof h - 1;
    }
    snprintf(text2, sizeof text2, "%s is %s %s<%s>, not %s<%s>.", what, strchr("AEIOUaeiou", sp->display[0]) ? "an" : "a", sp->display, w, sp->display, h);
    fireError("ArgumentException", text2);
    return true;
}
/* the text of a declaration for its message ("... is a Dog, not Cat.", "... is a T (int), not String.") */
static const char* vmDeclaredDisplayFor(Vm* vm, const char* text, Value self, TArgs ctargs, char* buf, size_t cap) {
    VmSpec* sp;
    const char* targ = NULL;
    if (!text) return "?";
    sp = vmSpec(vm, text);
    if (sp->typeParam < 0) return sp->display;
    if (sp->owner[0]) targ = vmTypeArgOf(vm, self, sp->owner, sp->typeParam);
    else if (ctargs && ctargs[sp->typeParam] && ctargs[sp->typeParam]->display) targ = ctargs[sp->typeParam]->display;
    if (!targ || !targ[0]) return sp->display;
    {
        char plain[160];
        const char* colon = strchr(targ, ':');
        if (colon && (!strncmp(targ, "int:", 4) || !strncmp(targ, "float:", 6))) snprintf(plain, sizeof plain, "%.*s : %s", (int)(colon - targ), targ, colon + 1);
        else if (!strncmp(targ, "in:", 3)) snprintf(plain, sizeof plain, "in %s", targ + 3);
        else vmPlainName(targ, plain, sizeof plain);
        { const char* paren = strstr(sp->display, " ("); int dn = paren ? (int)(paren - sp->display) : (int)strlen(sp->display); snprintf(buf, cap, "%.*s (%s)", dn, sp->display, plain); }
    }
    return buf;
}

/* GuardLocalType/GuardTopType, a field declared with a class (VM.OpGuardLocalType): the value fits, or - a number for a `T` that stands for a number
 * (`int`, `float : cm`) - is converted into it (DeclaredType.FitsOrConvert), or becomes its text for a `T` that stands for `string` (DECISIONS D14); otherwise
 * the ArgumentException. True when it threw. */
static bool vmGuardType(Vm* vm, Value* v, const char* text, Value self, TArgs ctargs, const char* what, bool slot) {
    VmSpec* sp;
    const char* targ = NULL;
    char db[240];
    if (!text || vmFitsDeclared(vm, *v, text, self, ctargs)) return false;
    sp = vmSpec(vm, text);
    if (sp->typeParam >= 0) {
        if (sp->owner[0]) targ = vmTypeArgOf(vm, self, sp->owner, sp->typeParam);
        else if (ctargs && ctargs[sp->typeParam] && ctargs[sp->typeParam]->display && ctargs[sp->typeParam]->display[0]) targ = ctargs[sp->typeParam]->display;
    }
    if (targ && (v->kind == K_Int || v->kind == K_Float)) {
        bool isFloat = !strncmp(targ, "float", 5) && (!targ[5] || targ[5] == ':');
        bool isInt = !strncmp(targ, "int", 3) && (!targ[3] || targ[3] == ':');
        if (isFloat || (isInt && v->kind == K_Int)) {
            char demand[200];
            const char* colon = strchr(targ, ':');
            snprintf(demand, sizeof demand, "%s%s", isFloat ? "#" : "", colon ? colon + 1 : "unitless");
            *v = vmAdaptUnit(vm, *v, vmName(vm->prog, demand));
            return g_unwind.active;
        }
    }
    if (v->kind != K_String && v->kind != K_Undefined && (targ ? !strcmp(targ, "string") : (sp->typeParam < 0 && !strcmp(sp->alts, "string"))))
    {
        Value old = *v;
        *v = guardStr(*v);
        if (slot && !g_unwind.active) { retain(*v); release(old); }
        return g_unwind.active;
    }
    if (vmTypeArgsFailed(vm, *v, text, self, ctargs, what ? what : "A value")) return true;
    guardClassFailed(*v, vmDeclaredDisplayFor(vm, text, self, ctargs, db, sizeof db), what ? what : "A value");
    return true;
}

/* Arrays of a class, a string or an enum (ArrayElem.Class, ROADMAP 19): the runtime's g_arrClasses, whose `fits`/`zero` have no context - the VM
 * keeps a fixed pool of trampolines, one per DeclaredType text of the program (VM_SPIKE point 1). */
#define VM_ARRCLASSES 64
static ArrClass g_vmArrClasses[VM_ARRCLASSES];
static const char* g_vmArrTexts[VM_ARRCLASSES];
static const char** g_vmArrIsA[VM_ARRCLASSES];
static Value g_vmArrZero[VM_ARRCLASSES];
static uint32_t g_vmArrCount;
static bool vmArrFits(uint32_t k, Value v) { return vmFitsDeclared(g_vmCur, v, g_vmArrTexts[k], Undef(), NULL); }
static Value vmArrZero(uint32_t k) { return g_vmArrZero[k]; }
#define VM_AC(k) static bool vmAcf##k(Value v) { return vmArrFits(k, v); } static Value vmAcz##k(void) { return vmArrZero(k); }
#define VM_AC8(k) VM_AC(k##0) VM_AC(k##1) VM_AC(k##2) VM_AC(k##3) VM_AC(k##4) VM_AC(k##5) VM_AC(k##6) VM_AC(k##7)
VM_AC(0) VM_AC(1) VM_AC(2) VM_AC(3) VM_AC(4) VM_AC(5) VM_AC(6) VM_AC(7) VM_AC8(1) VM_AC8(2) VM_AC8(3) VM_AC8(4) VM_AC8(5) VM_AC8(6) VM_AC8(7)
#define VM_ACR(k) { vmAcf##k, vmAcz##k }
#define VM_ACR8(k) VM_ACR(k##0), VM_ACR(k##1), VM_ACR(k##2), VM_ACR(k##3), VM_ACR(k##4), VM_ACR(k##5), VM_ACR(k##6), VM_ACR(k##7)
static const struct { bool (*fits)(Value); Value (*zero)(void); } g_vmAcFns[VM_ARRCLASSES] = {
    VM_ACR(0), VM_ACR(1), VM_ACR(2), VM_ACR(3), VM_ACR(4), VM_ACR(5), VM_ACR(6), VM_ACR(7), VM_ACR8(1), VM_ACR8(2), VM_ACR8(3), VM_ACR8(4), VM_ACR8(5), VM_ACR8(6), VM_ACR8(7) };
/* the 1-based entry of a DeclaredType text in g_arrClasses (0: no room) */
static uint32_t vmArrClassOf(Vm* vm, const char* text) {
    uint32_t k;
    VmSpec* sp;
    for (k = 0; k < g_vmArrCount; k++) if (g_vmArrTexts[k] == text) return k + 1;
    if (g_vmArrCount >= VM_ARRCLASSES) return 0;
    k = g_vmArrCount++;
    sp = vmSpec(vm, text);
    g_vmArrTexts[k] = text;
    g_vmArrClasses[k].fits = g_vmAcFns[k].fits;
    g_vmArrClasses[k].zero = g_vmAcFns[k].zero;
    g_vmArrClasses[k].display = sp->display;
    g_vmArrZero[k] = Undef();
    if (!strcmp(sp->display, "string")) g_vmArrZero[k] = vmConstStrUtf8(vm->prog, "");
    else {
        uint32_t e;
        for (e = 0; e < vm->prog->nenums; e++)
            if (!strcmp(vm->prog->enums[e].name, sp->display) && vm->prog->enums[e].count) { g_vmArrZero[k] = EnumV(vm->prog->enums[e].members[0].value, (uint16_t)(e + 1)); break; }
    }
    {   /* (isA: the classes and interfaces of the chain - an array of a derived class goes where one of its base is declared) */
        VmClass* c = vmClassNamed(vm->prog, vmName(vm->prog, sp->alts));
        uint32_t n = 0, i;
        VmClass* q;
        for (q = c; q; q = q->base) n += 1 + q->ninterfaces;
        g_vmArrIsA[k] = (const char**)vmAlloc(&vm->prog->arena, (n + 2) * sizeof(char*));
        n = 0;
        for (q = c; q; q = q->base) { char pn[160]; g_vmArrIsA[k][n++] = vmName(vm->prog, vmPlainName(q->name, pn, sizeof pn)); for (i = 0; i < q->ninterfaces; i++) g_vmArrIsA[k][n++] = q->interfaces[i]; }
        g_vmArrIsA[k][n] = NULL;
        g_vmArrClasses[k].isA = c ? g_vmArrIsA[k] : NULL;
    }
    g_arrClasses = g_vmArrClasses;
    return k + 1;
}

/* `T items[]`, `new T[n]` (OpCode.TypeArrayOfParam, FieldInfo.ArrayOfParam): the array is typed by the type argument when that is int, float, bool or char
 * (DeclaredType.TypedElementOf; an array of a class: not yet). `text`: `$k@Class` or `$k@`. */
static void vmTypeArrayOfParam(Vm* vm, Value arr, const char* text, Value self, TArgs ctargs) {
    const char* targ = NULL;
    int k;
    const char* at;
    uint8_t elem;
    if (arr.kind != K_Array || !text || text[0] != '$') return;
    k = atoi(text + 1);
    at = strchr(text, '@');
    if (at && at[1]) targ = vmTypeArgOf(vm, self, at + 1, k);
    else if (ctargs && ctargs[k]) targ = ctargs[k]->display;
    if (!targ) return;
    elem = !strcmp(targ, "int") ? 4 : !strcmp(targ, "float") ? 8 : !strcmp(targ, "bool") ? 9 : !strcmp(targ, "char") ? 10 : 0;
    if (!elem) return;
    typeArray(arr, elem, 1, 0, 0, vmConstStrUtf8(vm->prog, "(no unit)"), 1);
}

/* The lists of the scopes of the running call (innermost first: temporaries, then the scope's own) - what `try take`, `is from local` look at. */
static int vmCallLists(Vm* vm, VmFrame* fr, OwnList** lists, int cap) {
    VmScope* s;
    VmScope* stop = fr->kind == FR_TOP && vm->isStrand ? NULL : vmStopOf(vm, fr);   /* (a fire thread: its global scope is the scope of its body) */
    int n = 0;
    for (s = fr->scope; s && s != stop && n + 2 <= cap; s = s->parent) {
        lists[n++] = &s->temp; lists[n++] = &s->own;
        if (fr->kind == FR_TOP && s == vm->global) break;
    }
    return n;
}

/* The finally a return or a jump goes through: the innermost registered handler of this frame with a finally (it is unregistered). */
static VmHandlerRec* vmFinallyOf(Vm* vm) {
    Handler* h = g_handlers;
    if (h && h->fn == vmCatchFn) {
        VmHandlerRec* r = (VmHandlerRec*)h;
        if (r->frame == vm->nframes - 1 && r->t->finallyAddr >= 0) return r;
    }
    return NULL;
}

/* A lambda of the VM is called from the bytecode: its frame (the arguments, then the captures as the slots behind the parameters). */
static void vmLamEnter(Vm* vm, Lam* l) {
    VmProto* p = vmLamProto(l);
    uint32_t argc = vm->sp, ncaps = l->ncaps - 1, i;
    VmFrame* f;
    (void)argc;
    f = &vm->frames[vm->nframes - 1];
    f->ctargs = l->targs ? l->targs : f->ctargs;
    for (i = 0; i < ncaps; i++) {
        Value c = lamCaps(l)[i];
        if (((p->captureCopyMask >> i) & 1) && ownedOf(c) && leafAlive(c)) {
            c = copyValue(c, true, &f->base->own);
            if (g_unwind.active) return;
        }
        scDefine(f->base, c);
    }
}
static Value vmLamThunk(Value lam, const Value* args) {
    Vm* vm = g_vmCur;
    Lam* l = lamOf(lam);
    VmProto* p = vmLamProto(l);
    int i, argc = (int)l->nparams;
    int32_t base;
    if (vm->sp + (uint32_t)argc + 64 > VM_STACK) fatal("Stack overflow: the operand stack is full.");
    for (i = 0; i < argc; i++) vm->stack[vm->sp++] = args[i];
    vmPushCall(vm, p, argc, 0, l->on, FR_NESTED);
    vmLamEnter(vm, l);
    base = vm->nframes - 1;
    vm->ret = Undef();
    if (g_unwind.active) { vm->nframes--; return Undef(); }
    vmRun(vm, base);
    if (g_unwind.active) return Undef();
    return vm->ret;
}

/* A method of the program on an object or a value, called from C (an operator, GetIndex of a foreach over an object): its result (a count for the trip). */
static bool vmBinaryOperator(Vm* vm, const char* opName, int32_t base, Value* sp) {
    Value a = sp[-2];
    VmClass* c = vmClassOfValue(vm, a);
    VmProto* m;
    if (!c || !leafAlive(a)) return false;
    m = vmFindMethod(vm, c, vmName(vm->prog, opName), 1, &sp[-1], a, NULL);
    if (!m) return false;
    (void)base;
    vmPushCall(vm, m, 1, 1, a, FR_CALL);
    return true;
}

static void vmRun(Vm* vm, int32_t base) {
    Value* const st = vm->stack;
    VmFrame* fr;
    const uint8_t* ip;
    Value* sp;
    VmProgram* const prog = vm->prog;
    RELOAD();
    for (;;) {
        uint32_t op = *ip++;
        switch (op) {
        case OP_LoadConst: PUSH(fr->chunk->k[U16(ip)]); ip += 2; break;
        case OP_Pop: sp--; break;
        case OP_Dup: *sp = sp[-1]; sp++; break;
        case OP_Swap: { Value t = sp[-1]; sp[-1] = sp[-2]; sp[-2] = t; break; }
        case OP_RotateUnderTop: { Value c = sp[-1], b = sp[-2], a = sp[-3]; sp[-3] = b; sp[-2] = a; sp[-1] = c; break; }
        case OP_LoadLocal: {
            VmScope* s = scAncestor(fr->scope, U16(ip));
            uint32_t slot = U16(ip + 2);
            ip += 4;
            PUSH(slot < s->hi ? s->slots[slot] : Undef());
            break;
        }
        case OP_StoreLocal: case OP_StoreLocalPop: {
            VmScope* s = scAncestor(fr->scope, U16(ip));
            uint32_t slot = U16(ip + 2);
            Value v = sp[-1], old;
            Value* x;
            ip += 4;
            x = scSlot(s, slot);
            old = *x;
            retain(v);
            *x = v;
            if (FIRE_UNLIKELY(isRef(old))) { SYNC(); release(old); }
            if (op == OP_StoreLocalPop) sp--;
            break;
        }
        case OP_LoadGlobal: { uint32_t slot = U16(ip); Value v; VmScope* g = vmGlobalsOf(vm, slot); ip += 2; v = slot < g->hi ? g->slots[slot] : Undef(); if (FIRE_UNLIKELY(isRef(v))) holdTemp(v, &fr->scope->temp); PUSH(v); break; }
        case OP_StoreGlobal: case OP_StoreGlobalPop: {
            uint32_t slot = U16(ip);
            Value v = sp[-1], old;
            Value* x;
            ip += 2;
            if (FIRE_UNLIKELY(slot < vm->sharedCount)) {   /* (a fire thread writes a global of the main program: a section, rule 10) */
                SYNC();
                if (!vmStoreShared(vm, vm->top->global, slot, v, "A global variable")) goto unwind;
                if (op == OP_StoreGlobalPop) sp--;
                break;
            }
            x = scSlot(vm->global, slot);
            old = *x;
            retain(v);
            *x = v;
            if (FIRE_UNLIKELY(isRef(old))) { SYNC(); release(old); }
            if (op == OP_StoreGlobalPop) sp--;
            break;
        }
        case OP_DeclareLocal: scDefine(fr->scope, POP()); break;

        /* ---- arithmetic: the fast path of the generated code inline, the rest in the runtime; an object on the left: its operator ---- */
#define VM_OPERATOR(name) if (FIRE_UNLIKELY(sp[-2].kind == K_Class)) { SYNC(); if (vmBinaryOperator(vm, name, base, sp)) { RELOAD(); break; } CHECK(); }
        case OP_Add: case OP_StepAdd: {
            Value b, a;
            if (op == OP_StepAdd) sp[-1] = stepOf(sp[-2], sp[-1]);
            VM_OPERATOR("operator+")
            b = POP(); a = sp[-1];
            if (FIRE_LIKELY(a.kind == K_Int && b.kind == K_Int && a.unit == b.unit)) sp[-1] = IntOp((int64_t)((uint64_t)a.i + (uint64_t)b.i), a.unit, a, b);
            else { SYNC(); sp[-1] = addR(a, b, &fr->scope->temp); CHECK(); }
            break;
        }
        case OP_Sub: case OP_StepSub: {
            Value b, a;
            if (op == OP_StepSub) sp[-1] = stepOf(sp[-2], sp[-1]);
            VM_OPERATOR("operator-")
            b = POP(); a = sp[-1];
            if (FIRE_LIKELY(a.kind == K_Int && b.kind == K_Int && a.unit == b.unit)) sp[-1] = IntOp((int64_t)((uint64_t)a.i - (uint64_t)b.i), a.unit, a, b);
            else { SYNC(); sp[-1] = sub(a, b); CHECK(); }
            break;
        }
        case OP_Mul: {
            Value b, a;
            VM_OPERATOR("operator*")
            b = POP(); a = sp[-1];
            if (FIRE_LIKELY(a.kind == K_Int && b.kind == K_Int && a.unit == 0 && b.unit == 0)) sp[-1] = IntOp((int64_t)((uint64_t)a.i * (uint64_t)b.i), 0, a, b);
            else { SYNC(); sp[-1] = mul(a, b); CHECK(); }
            break;
        }
        case OP_Div: { Value b, a; VM_OPERATOR("operator/") b = POP(); a = sp[-1]; SYNC(); sp[-1] = divide(a, b); CHECK(); break; }
        case OP_Mod: {
            Value b, a;
            VM_OPERATOR("operator%")
            b = POP(); a = sp[-1];
            if (FIRE_LIKELY(a.kind == K_Int && b.kind == K_Int && a.unit == b.unit && b.i > 0)) sp[-1] = IntOp(a.i % b.i, a.unit, a, b);
            else { SYNC(); sp[-1] = modulo(a, b); CHECK(); }
            break;
        }
        case OP_Power: { Value b, a; VM_OPERATOR("operator^") b = POP(); a = sp[-1]; SYNC(); sp[-1] = power(a, b); CHECK(); break; }
        case OP_PowerFloat: { Value b, a; VM_OPERATOR("operator^") b = POP(); a = sp[-1]; SYNC(); sp[-1] = powerF(a, b); CHECK(); break; }
        case OP_BitAnd: { Value b, a; VM_OPERATOR("operator&") b = POP(); a = sp[-1]; SYNC(); sp[-1] = bitAnd(a, b); CHECK(); break; }
        case OP_BitOr: { Value b, a; VM_OPERATOR("operator|") b = POP(); a = sp[-1]; SYNC(); sp[-1] = bitOr(a, b); CHECK(); break; }
        case OP_BitXor: { Value b, a; VM_OPERATOR("operator#") b = POP(); a = sp[-1]; SYNC(); sp[-1] = bitXor(a, b); CHECK(); break; }
        case OP_ShiftLeft: { Value b, a; VM_OPERATOR("operator<<") b = POP(); a = sp[-1]; SYNC(); sp[-1] = shl(a, b); CHECK(); break; }
        case OP_ShiftRight: { Value b, a; VM_OPERATOR("operator>>") b = POP(); a = sp[-1]; SYNC(); sp[-1] = shr(a, b); CHECK(); break; }
        case OP_ShiftRightLogical: { Value b, a; VM_OPERATOR("operator>>>") b = POP(); a = sp[-1]; SYNC(); sp[-1] = ushr(a, b); CHECK(); break; }
        case OP_Neg: { SYNC(); sp[-1] = negate(sp[-1]); CHECK(); break; }
        case OP_BitNot: { SYNC(); sp[-1] = bitNot(sp[-1]); CHECK(); break; }
        case OP_LogicalNot: { Value v = sp[-1]; if (FIRE_LIKELY(v.kind == K_Bool)) sp[-1] = Bool(v.i == 0); else { SYNC(); sp[-1] = lnot(v); CHECK(); } break; }
        case OP_Eq: case OP_NotEq: {
            Value b, a;
            bool r;
            VM_OPERATOR(op == OP_Eq ? "operator==" : "operator!=")
            b = POP(); a = sp[-1];
            SYNC();
            r = opEq(a, b);
            CHECK();
            sp[-1] = Bool(op == OP_Eq ? r : !r);
            break;
        }
        case OP_Lt: case OP_LtEq: case OP_Gt: case OP_GtEq: {
            Value b, a;
            bool r;
            VM_OPERATOR(op == OP_Lt ? "operator<" : op == OP_LtEq ? "operator<=" : op == OP_Gt ? "operator>" : "operator>=")
            b = POP(); a = sp[-1];
            if (FIRE_LIKELY(a.kind == K_Int && b.kind == K_Int && a.unit == b.unit))
                r = op == OP_Lt ? a.i < b.i : op == OP_LtEq ? a.i <= b.i : op == OP_Gt ? a.i > b.i : a.i >= b.i;
            else { SYNC(); r = op == OP_Lt ? lt(a, b) : op == OP_LtEq ? le(a, b) : op == OP_Gt ? gt(a, b) : ge(a, b); CHECK(); }
            sp[-1] = Bool(r);
            break;
        }
        case OP_JumpIfNotLt: case OP_JumpIfNotLtEq: case OP_JumpIfNotGt: case OP_JumpIfNotGtEq: case OP_JumpIfNotEq: case OP_JumpIfNotNotEq: {
            Value b = sp[-1], a = sp[-2];
            bool r;
            uint32_t to = U16(ip);
            if (FIRE_UNLIKELY(a.kind == K_Class)) {
                /* (an object on the left: its operator, then the jump - rewritten as the compare and JumpIfFalse) */
                static const char* const names[] = { "operator<", "operator<=", "operator>", "operator>=", "operator==", "operator!=" };
                SYNC();
                if (vmBinaryOperator(vm, names[op - OP_JumpIfNotLt], base, sp)) {
                    /* the call returns here: the result is tested by a JumpIfFalse to the same place - emulated with a frame flag */
                    VmFrame* callee = &vm->frames[vm->nframes - 1];
                    VmFrame* caller = &vm->frames[vm->nframes - 2];
                    (void)callee;
                    caller->ip = ip + 2;
                    caller->catchEnded |= 2;   /* (bit 1: after the return a JumpIfFalse to `to` - opJump - tests the operator's result) */
                    caller->opJump = to;
                    RELOAD();
                    break;
                }
                CHECK();
            }
            sp -= 2;
            ip += 2;
            if (op == OP_JumpIfNotEq || op == OP_JumpIfNotNotEq) { SYNC(); r = opEq(a, b) == (op == OP_JumpIfNotEq); CHECK(); }
            else if (FIRE_LIKELY(a.kind == K_Int && b.kind == K_Int && a.unit == b.unit))
                r = op == OP_JumpIfNotLt ? a.i < b.i : op == OP_JumpIfNotLtEq ? a.i <= b.i : op == OP_JumpIfNotGt ? a.i > b.i : a.i >= b.i;
            else { SYNC(); r = op == OP_JumpIfNotLt ? lt(a, b) : op == OP_JumpIfNotLtEq ? le(a, b) : op == OP_JumpIfNotGt ? gt(a, b) : ge(a, b); CHECK(); }
            if (!r) { VM_LOOP_POINT(to, 3); ip = fr->chunk->code + to; }
            break;
        }
        case OP_ArithLocalConstPop: case OP_ArithGlobalConstPop: {
            VmScope* s;
            uint32_t slot;
            Value k;
            uint32_t mode;
            Value* x;
            if (op == OP_ArithLocalConstPop) { s = scAncestor(fr->scope, U16(ip)); slot = U16(ip + 2); k = fr->chunk->k[U16(ip + 4)]; mode = ip[6]; ip += 7; }
            else { slot = U16(ip); s = vmGlobalsOf(vm, slot); k = fr->chunk->k[U16(ip + 2)]; mode = ip[4]; ip += 5; }
            x = scSlot(s, slot);
            if (FIRE_LIKELY(x->kind == K_Int && k.kind == K_Int && x->unit == k.unit))
                *x = IntOp((int64_t)((mode & 1) ? (uint64_t)x->i - (uint64_t)k.i : (uint64_t)x->i + (uint64_t)k.i), x->unit, *x, k);
            else {
                Value r, old = *x;
                SYNC();
                if (mode & 2) k = stepOf(*x, k);
                r = (mode & 1) ? sub(*x, k) : addR(*x, k, &fr->scope->temp);
                CHECK();
                retain(r);
                *x = r;
                release(old);
            }
            break;
        }
        case OP_CoerceUnit: { uint32_t u = fr->chunk->units[U16(ip)]; ip += 2; if (!((sp[-1].kind == K_Int || sp[-1].kind == K_Float) && sp[-1].unit == u)) { SYNC(); sp[-1] = coerceUnit(sp[-1], u); CHECK(); } break; }
        case OP_CoerceUnitDynamic: { Value c = POP(); SYNC(); { Value r_ = coerceUnit(c, unitOf(sp[-1])); PUSH(r_); } CHECK(); break; }
        case OP_RelabelUnit: { uint32_t u = fr->chunk->units[U16(ip)]; ip += 2; SYNC(); sp[-1] = relabelUnit(sp[-1], u); CHECK(); break; }
        case OP_RelabelUnitDynamic: { Value c = POP(); SYNC(); { Value r_ = relabelUnit(c, unitOf(sp[-1])); PUSH(r_); } CHECK(); break; }
        case OP_CoerceType: {
            static const uint8_t kinds[] = { K_Bool, K_Int, K_Float, K_Char, K_String, K_Word };
            uint32_t tag = *ip++;
            SYNC();
            sp[-1] = coerceType(sp[-1], tag < 6 ? kinds[tag] : K_Undefined);
            CHECK();
            break;
        }
        case OP_CoerceTypeDynamic: { Value c = POP(); SYNC(); { Value r_ = coerceTypeTo(c, sp[-1].kind); PUSH(r_); } CHECK(); break; }
        case OP_ConvertWidth: {
            uint32_t bits = ip[0], flags = ip[1];
            Value v = sp[-1];
            ip += 2;
            SYNC();
            if (flags & 8) sp[-1] = wordOf(v, (flags & 4) != 0);
            else if (flags & 4) { if ((flags & 1) ? v.kind == K_Float : (v.kind == K_Int || v.kind == K_Word)) sp[-1] = convertWidth(v, (int)bits, (flags & 1) != 0, (flags & 2) != 0); }
            else sp[-1] = convertWidth(v, (int)bits, (flags & 1) != 0, (flags & 2) != 0);
            CHECK();
            break;
        }
        case OP_IsInUnit: { uint32_t u = fr->chunk->units[U16(ip)]; ip += 2; sp[-1] = Bool(unitCompatible(unitOf(sp[-1]), u)); break; }
        case OP_ToEnum: {
            Value k = fr->chunk->k[U16(ip)];
            ip += 2;
            SYNC();
            sp[-1] = toEnum(sp[-1], k.reserved);
            CHECK();
            break;
        }
        case OP_FormatValue: { Value spec = fr->chunk->k[U16(ip)]; ip += 2; SYNC(); sp[-1] = formatValue(sp[-1], spec, &fr->scope->temp); CHECK(); break; }

        /* ---- jumps ---- */
        case OP_Jump: {
            uint32_t to = U16(ip);
            ip += 2;
            VM_LOOP_POINT(to, 3);
            if (FIRE_UNLIKELY(fr->catchEnded & 1)) {   /* (the jump behind the try: the catch is left - what it pushed for a finally goes along) */
                VmHandlerRec* rec = fr->rec;
                uint32_t n = (uint32_t)(sp - st) - fr->spBase, i;
                rec->ncarry = n > 4 ? 4 : n;
                for (i = 0; i < rec->ncarry; i++) rec->carry[i] = st[fr->spBase + i];
                SYNC();
                exitJump(&rec->h, to);
                goto unwind;
            }
            ip = fr->chunk->code + to;
            break;
        }
        case OP_JumpIfFalse: case OP_JumpIfFalsePeek: case OP_JumpIfTruePeek: {
            Value c = op == OP_JumpIfFalse ? POP() : sp[-1];
            uint32_t to = U16(ip);
            bool t;
            ip += 2;
            if (FIRE_LIKELY(c.kind == K_Bool)) t = c.i != 0;
            else { SYNC(); t = truthy(c); CHECK(); }
            if (t == (op == OP_JumpIfTruePeek)) { VM_LOOP_POINT(to, 3); ip = fr->chunk->code + to; }
            break;
        }

        /* ---- scopes and ownership ---- */
        case OP_EnterScope: fr->scope = scNew(vm, fr->scope); break;
        case OP_ExitScope: {
            VmScope* s = fr->scope;
            if (FIRE_UNLIKELY(fr->catchEnded & 1)) { fr->rec->extraExits++; break; }   /* (break/continue out of a catch: the owner's scopes, left when it lands) */
            SYNC();
            if (fr->kind == FR_CATCH && s == fr->base) { fr->scope = fr->rec->scope; scLeave(vm, s); fr->catchEnded |= 1; break; }
            if (s == vm->global || s == fr->base) break;
            fr->scope = s->parent;
            scLeave(vm, s);
            break;
        }
        case OP_ReleaseTemps: {
            OwnList* t = &fr->scope->temp;
            if (FIRE_UNLIKELY(t->head != NULL)) { SYNC(); leave(t); }
            break;
        }
        case OP_ClaimToScope: {
            uint32_t depth = U16(ip), flags = ip[2];
            VmScope* target = depth == 0xFFFF ? (fr->kind == FR_TOP ? vm->global : NULL) : scAncestor(fr->scope, depth);
            Value v = sp[-1];
            OwnList* to = target ? &target->own : g_globalOwn;
            ip += 3;
            if (ownedOf(v)) {
                SYNC();
                if ((flags & 1) && depth == 0xFFFF) noteGlobalClaim(v, &fr->scope->temp);   /* (rule 10: a fire thread's store into a global moves it there) */
                if (flags & 1) claimToList(v, &fr->scope->temp, to);
                if ((flags & 2) && target) {
                    VmScope* s;
                    for (s = fr->scope; s && s != target; s = s->parent) {
                        hoistFrom(v, &s->own, to);
                        hoistFrom(v, &s->temp, to);
                        if (s == fr->base) break;
                    }
                }
            }
            break;
        }
        case OP_HoistValue: {
            /* `x = value` with a variable of an outer block: a value an inner block of the running call owns moves into the call's scope (the main
             * program: the global scope) - never out of the call (VM.HoistValue) */
            Owned* o = ownedOf(sp[-1]);
            if (o && o->owner) {
                VmScope* stop = vmStopOf(vm, fr);
                VmScope* target = fr->kind == FR_TOP ? vm->global : fr->base;
                VmScope* s;
                for (s = fr->scope; s && s != stop && s != target; s = s->parent)
                    if (o->owner == &s->own || o->owner == &s->temp) { ownUnlink(o); ownLink(&target->own, o); break; }
            }
            break;
        }
        case OP_ClaimToObject: { Value v = POP(), h = POP(); SYNC(); if (h.kind == K_Class && leafAlive(h)) claimToHolder(h, v, &fr->scope->temp); PUSH(v); break; }
        case OP_ClaimToArray: { SYNC(); if (sp[-3].kind == K_Array && leafAlive(sp[-3])) claimToHolder(sp[-3], sp[-1], &fr->scope->temp); break; }
        case OP_TakeToScope: case OP_TryTakeToScope: {
            uint32_t depth = U16(ip);
            OwnList* list = depth == 0xFFFF ? g_globalOwn : &scAncestor(fr->scope, depth)->own;
            Value v = sp[-1];
            ip += 2;
            SYNC();
            if (op == OP_TryTakeToScope) {
                OwnList* lists[128];
                int n = vmCallLists(vm, fr, lists, 128);
                bool took = tryTakeOwned(v, lists, n, fr->self, NULL);
                if (took) takeToList(v, list);
                vm->ret = Bool(took);
            } else if (takeAlive(v)) takeToList(v, list);
            if (depth == 0xFFFF) noteGlobalTake(v);   /* (rule 10, see ClaimToScope) */
            CHECK();
            break;
        }
        case OP_TakeToObject: case OP_TryTakeToObject: case OP_TakeToArray: case OP_TryTakeToArray: {
            bool arr = op == OP_TakeToArray || op == OP_TryTakeToArray, tr = op == OP_TryTakeToObject || op == OP_TryTakeToArray;
            Value h = arr ? sp[-3] : sp[-2], v = sp[-1];
            SYNC();
            if (!arr && h.kind != K_Class) takeHolderError(h);
            else if (tr) {
                OwnList* lists[128];
                int n = vmCallLists(vm, fr, lists, 128);
                bool took = tryTakeOwned(v, lists, n, fr->self, NULL);
                if (took) takeToHolder(h, v);
                vm->ret = Bool(took);
            } else if (takeAlive(v)) takeToHolder(h, v);
            CHECK();
            break;
        }
        case OP_TakeCheck: break;
        case OP_TookResult: PUSH(vm->ret.kind == K_Bool ? vm->ret : Bool(false)); break;
        case OP_OwnValue: {
            OwnList* lists[128];
            int n = vmCallLists(vm, fr, lists, 128);
            Value v = POP();
            SYNC();
            sp[-1] = ownValueN(sp[-1], v, lists, n);
            CHECK();
            break;
        }
        case OP_Delete: { Value v = POP(); SYNC(); deleteValue(v); CHECK(); break; }
        case OP_ReplaceOld: {
            bool isStatic = *ip++ == 1;
            Value old = isStatic ? sp[-2] : sp[-3], nv = sp[-1];
            SYNC();
            if (!sameRef(old, nv) && ownedOf(old)) {
                bool owned = isStatic ? isFromGlobal(old) : (sp[-2].kind == K_Class && isFrom(old, sp[-2], false));
                if (owned) deleteValue(old);
            }
            CHECK();
            /* [old, (holder,) value] -> [(holder,) value] */
            if (isStatic) { sp[-2] = sp[-1]; sp--; }
            else { sp[-3] = sp[-2]; sp[-2] = sp[-1]; sp--; }
            break;
        }
        case OP_CopyValue: {
            bool deep = (*ip++ & 1) != 0;
            SYNC();
            if (FIRE_UNLIKELY(sp[-1].kind == K_Class) && leafAlive(sp[-1]) && (asObj(sp[-1])->h.flags & 8)) {   /* (ObjectCloner.RequireCopyableRoot) */
                char text[300];
                snprintf(text, sizeof text, "An actor ('%s') cannot be copied - actor references are shared.", className(asObj(sp[-1])->cls));
                fireError("ArgumentException", text);
                goto unwind;
            }
            sp[-1] = copyValue(sp[-1], deep, &fr->scope->temp);
            CHECK();
            break;
        }
        case OP_CopyValueOwned: {
            bool deep = (*ip++ & 1) != 0;
            Value v = POP();
            SYNC();
            sp[-1] = copyOwned(sp[-1], v, deep);
            CHECK();
            break;
        }
        case OP_IsFrom: {
            uint32_t mode = *ip++;
            Value owner = POP(), v = sp[-1];
            SYNC();
            if (mode == 2) { OwnList* lists[128]; int n = vmCallLists(vm, fr, lists, 128); sp[-1] = Bool(isFromLocal(v, lists, n, NULL)); }
            else if (mode == 3) sp[-1] = Bool(isFromGlobal(v));
            else { bool r = isFrom(v, owner, mode != 0); CHECK(); sp[-1] = Bool(r); }
            break;
        }

        /* ---- natives ---- */
        case OP_CallNative: case OP_CallTryableNative: {
            uint32_t idx = U16(ip), argc = ip[2];
            Value r;
            ip += 3;
            SYNC();
            if (!vmCallNative(vm, fr, idx, argc, sp - argc, &r) && !vmThreadNative(vm, fr, idx, argc, sp - argc, &r)) {
                uint32_t imp = idx - VM_NNATIVES;
                vmUnsupported(op == OP_CallTryableNative ? "the tryable native" : "the native",
                    op == OP_CallTryableNative ? "?" : idx < VM_NNATIVES ? g_vmNativeNames[idx] : imp < prog->npackageNatives ? prog->packageNatives[imp] : "?");
            }
            sp -= argc;
            CHECK();
            PUSH(r);
            break;
        }

        /* ---- lambdas and calls ---- */
        case OP_MakeLambda: case OP_MakeLambdaCapturing: {
            VmProto* p = fr->chunk->fn[U16(ip)];
            bool hasOn = ip[2] != 0;
            uint32_t ncaps = op == OP_MakeLambdaCapturing ? ip[3] : 0, i;
            Value on;
            Lam* l;
            ip += op == OP_MakeLambdaCapturing ? 4 : 3;
            SYNC();
            on = hasOn ? POP() : Undef();
            /* the proto rides as a hidden last capture (no field is added to the runtime's lambda: L1) */
            l = allocLam((uint32_t)p->nparams, ncaps + 1, vmLamThunk, &fr->scope->temp);
            sp -= ncaps;
            for (i = 0; i < ncaps; i++) { lamCaps(l)[i] = sp[i]; retain(sp[i]); }
            lamCaps(l)[ncaps] = Int((int64_t)(intptr_t)p);
            l->on = on;
            if (vm->pendingTArgsFor == 2) { l->targs = vm->pendingTArgs; vm->pendingTArgs = NULL; vm->pendingTArgsFor = 0; }
            if (ncaps) lamOwnStore(l, &fr->scope->temp);
            PUSH(LamV(l));
            break;
        }
        case OP_RebindLambda: { Value on = POP(); SYNC(); sp[-1] = rebindLam(sp[-1], on, &fr->scope->temp); CHECK(); break; }
        case OP_CheckLambdaSignature: { uint32_t n = *ip++; SYNC(); checkLambda(sp[-1], (int)n); CHECK(); break; }
        case OP_Call: {
            uint32_t argc = *ip++;
            Value callee = sp[-(int)argc - 1];
            Lam* l;
            SYNC();
            if (FIRE_UNLIKELY(callee.kind != K_Lambda)) {
                char text[200];
                snprintf(text, sizeof text, "Call of a value of type %s, which is not a lambda.", kindName(callee));
                fireError("ArgumentException", text);
                goto unwind;
            }
            l = lamOf(callee);
            if (FIRE_UNLIKELY(l->o.flags & 1)) { destroyedError(callee); goto unwind; }
            if (l->fn == vmLamThunk && FIRE_LIKELY(l->home == 0 || (l->home == 1 && !g_isThread))) {   /* (a home elsewhere - its target in another thread, an actor: callLamU runs it there) */
                VmProto* lp = vmLamProto(l);
                if (FIRE_UNLIKELY((int)argc != lp->nparams) && ((int)argc > lp->nparams || !vmAllTrailingDefaults(lp, (int)argc))) {
                    char text[160];
                    snprintf(text, sizeof text, "Wrong number of arguments: expected %d%s, got %u.", lp->nparams, (int)argc < lp->nparams ? " (the missing parameters have no default value)" : "", argc);
                    fireError("ArgumentException", text);
                    goto unwind;
                }
                vmPushCall(vm, lp, (int)argc, 1, l->on, FR_CALL);
                vmLamEnter(vm, l);
                if (g_unwind.active) goto unwind;
            } else {
                Value r = callLamU(callee, (int)argc, sp - argc, &fr->scope->temp);
                vm->sp -= argc + 1;
                CHECK();
                vm->stack[vm->sp++] = r;
                adopt(r, &fr->scope->temp);
            }
            RELOAD();
            break;
        }
        case OP_Return: {
            Value r = POP();
            VmHandlerRec* fin;
            SYNC();
            if (FIRE_UNLIKELY(fr->kind == FR_CATCH)) {
                /* `return` in a catch: the owner returns - the value leaves the catch's scopes with a count for the trip (the landing hands it on) */
                OwnList* lists[64];
                int n = 0;
                VmScope* s;
                for (s = fr->scope; s && s != fr->rec->scope && n < 62; s = s->parent) { lists[n++] = &s->temp; lists[n++] = &s->own; }
                if (n) transferTree(r, lists, n);
                retain(r);
                exitReturn(&fr->rec->h, r);
                goto unwind;
            }
            if (FIRE_UNLIKELY((fin = vmFinallyOf(vm)) != NULL)) {
                /* a `return` in a try with a finally: the finally runs first, its EndFinally returns (DoReturn) */
                OwnList* lists[64];
                int n = 0;
                VmScope* s;
                popHandler();
                for (s = fr->scope; s && s != fin->scope && n < 62; s = s->parent) { lists[n++] = &s->temp; lists[n++] = &s->own; }
                if (n) transferTree(r, lists, n);
                retain(r);
                vmLeaveTo(vm, fr, fin->scope);
                sp = st + fin->sp;
                PUSH(r);
                PUSH(Int(FIN_K_RETURN));
                ip = fr->chunk->code + fin->t->finallyAddr;
                free(fin);
                break;
            }
            if (FIRE_UNLIKELY(fr->recInit != NULL)) { recordCtorEnd(fr->self, asObj(fr->self)->cls); CHECK(); }
            if (!vmReturn(vm, r, base)) return;
            RELOAD();
            goto afterReturn;
        }
        case OP_Halt: {
            SYNC();
            if (fr->kind == FR_TOP) { vm->nframes--; return; }
            if (!vmReturn(vm, Undef(), base)) return;   /* (fell off the end of a function) */
            RELOAD();
            goto afterReturn;
        }
        case OP_LoadThis: PUSH(fr->self); break;
        case OP_CallProtoWithThis: {
            VmProto* p = fr->chunk->fn[U16(ip)];
            uint32_t argc = ip[2];
            ip += 3;
            SYNC();
            vmPushCall(vm, p, (int)argc, 1, sp[-(int)argc - 1], FR_CALL);
            RELOAD();
            break;
        }
        case OP_CallMethod: {
            const uint8_t* at = ip - 1;
            const char* name = KNAME(U16(ip));
            uint32_t argc = ip[2];
            Value recv = sp[-(int)argc - 1];
            VmClass* c;
            VmClass* declaring = NULL;
            VmProto* m;
            bool hit;
            ip += 3;
            SYNC();
            if (FIRE_UNLIKELY(recv.kind != K_Class)) {
                VmClass* ext = recv.kind < 16 ? prog->extClass[recv.kind] : NULL;
                /* a base-type extension (`class extends string`): like an object call, `this` is the value */
                if (ext && (m = vmFindMethod(vm, ext, name, (int)argc, sp - argc, recv, &declaring)) != NULL) {
                    if (!PERF && !vmAccessAllowed(fr->chunk, declaring, vmProtoAccess(m))) {
                        char text[300];
                        snprintf(text, sizeof text, "Method '%s' of the extension of '%s' is %s and cannot be called from here.", name, ext->name + 1, vmAccessName(vmProtoAccess(m)));
                        accessDenied(text);
                        goto unwind;
                    }
                    vmPushCall(vm, m, (int)argc, 1, recv, FR_CALL);
                    RELOAD();
                    break;
                }
                if (!strcmp(name, "GetEnumerator") && argc == 0 && (recv.kind == K_Array || recv.kind == K_Buffer || recv.kind == K_String)) {
                    VmClass* le = vmClassNamed(prog, vmName(prog, "ListEnumerator"));
                    VmProto* ctor = le ? vmFindCtor(vm, le, 2, NULL) : NULL;
                    bool ok;
                    Value count = lengthOf(recv, &ok), o;
                    CHECK();
                    if (ctor) {
                        o = newObject(le->id, le->nlayout, &fr->scope->temp);
                        sp[-1] = count;
                        PUSH(recv);
                        sp[-2] = recv; sp[-1] = count;
                        vm->sp = (uint32_t)(sp - st);
                        vmPushCall(vm, ctor, 2, 0, o, FR_CTOR);
                        RELOAD();
                        break;
                    }
                }
                {
                    Value r = Undef();
                    bool known = true;
                    Value* a = sp - argc;
                    OwnList* list = &fr->scope->temp;
                    int ownId = -1;
                    const char* nm = !strncmp(name, "try", 3) ? name + 3 : name;
                    if (FIRE_UNLIKELY(vm->pendingArgNames != NULL)) {   /* (VM.RejectArgNames) */
                        char text[300];
                        vm->pendingArgNames = NULL;
                        snprintf(text, sizeof text, "Named arguments are not possible for the built-in method '%s'.", name);
                        fireError("ArgumentException", text);
                        goto unwind;
                    }
                    if (!strcmp(nm, "TakeLocal")) ownId = OM_TakeLocal; else if (!strcmp(nm, "TakeUpwards")) ownId = OM_TakeUpwards;
                    else if (!strcmp(nm, "TakeGlobal")) ownId = OM_TakeGlobal; else if (!strcmp(nm, "TakeTo")) ownId = OM_TakeTo;
                    if (ownId >= 0 && ownedOf(recv)) {
                        Value targetArg = ownId == OM_TakeTo ? (argc > 0 ? a[0] : Undef()) : Undef();
                        int baseArgs = ownId == OM_TakeTo ? 1 : 0;
                        if (name != nm) {
                            OwnList* lists[128];
                            int n = vmCallLists(vm, fr, lists, 128);
                            r = Bool(ownMethodTry(ownId, recv, targetArg, (int)argc > baseArgs ? a[baseArgs] : Undef(), &fr->scope->own, lists, n, fr->self, NULL));
                        } else if ((int)argc == baseArgs) ownMethod(ownId, recv, targetArg, &fr->scope->own);
                        else { OwnList* lists[128]; int n = vmCallLists(vm, fr, lists, 128); ownMethodT(ownId, recv, targetArg, a[baseArgs], &fr->scope->own, lists, n); }
                    }
                    else if (!strcmp(name, "ToString") && argc == 0 && recv.kind != K_Undefined) r = recv.kind == K_Buffer ? bufToString(recv, list) : toStringValue(recv, list);
                    else if (!strcmp(name, "ToBytes") && argc == 0 && recv.kind == K_String) r = strToBytes(recv, list);
                    else if (!strcmp(name, "ToUnicode") && argc == 1 && recv.kind == K_String) r = strToUnicode(recv, a[0], list);
                    else if (!strcmp(name, "ToUnicode") && argc == 1 && recv.kind == K_Char) r = charToUnicode(recv, a[0], list);
                    else if (!strcmp(name, "ToUnicode") && argc <= 1 && recv.kind == K_Buffer) r = bufToUnicode(recv, argc ? a[0] : Int(2), list);
                    else if (!strcmp(name, "ToByte") && argc == 0 && recv.kind == K_Char) r = charToByte(recv);
                    else if (!strcmp(name, "ToChar") && argc == 0 && recv.kind == K_Int) r = byteToChar(recv);
                    else if (!strcmp(name, "HasFlag") && argc == 1 && recv.kind == K_Int) r = enumHasFlag(recv, a[0]);
                    else if (!strcmp(name, "ToUnicodeChar") && argc <= 1 && recv.kind == K_Buffer) r = bufToUnicodeChar(recv, argc ? a[0] : Int(2));
                    else if (!strcmp(name, "ToLittleEndian") && argc == 0 && recv.kind == K_Buffer) r = bufToEndian(recv, true, list);
                    else if (!strcmp(name, "ToBigEndian") && argc == 0 && recv.kind == K_Buffer) r = bufToEndian(recv, false, list);
                    else if (!strcmp(name, "Fill") && (argc == 1 || argc == 3) && recv.kind == K_Buffer) r = bufFill(recv, a[0], argc > 1 ? a[1] : Undef(), argc > 2 ? a[2] : Undef());
                    else if (!strcmp(name, "CopyTo") && (argc == 2 || argc == 4) && recv.kind == K_Buffer) r = bufCopyTo(recv, a[0], a[1], argc > 2 ? a[2] : Undef(), argc > 3 ? a[3] : Undef());
                    else if (!strcmp(name, "Slice") && (argc == 1 || argc == 2) && recv.kind == K_Buffer) r = bufSlice(recv, a[0], argc > 1 ? a[1] : Undef(), list);
                    else if (!strcmp(name, "IndexOf") && (argc == 1 || argc == 2) && recv.kind == K_Buffer) r = bufIndexOf(recv, a[0], argc > 1 ? a[1] : Undef());
                    else if (!strcmp(name, "Compare") && argc == 1 && recv.kind == K_Buffer) r = bufCompare(recv, a[0]);
                    else if (recv.kind == K_Buffer && (!strncmp(name, "Read", 4) || !strncmp(name, "Write", 5))) {
                        static const char* const kinds[] = { "Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "Float16", "Float32", "Float64" };
                        bool rd = name[0] == 'R';
                        const char* k = name + (rd ? 4 : 5);
                        int ki;
                        known = false;
                        for (ki = 0; ki < 10; ki++) if (!strcmp(k, kinds[ki])) break;
                        if (ki < 10 && rd && (argc == 1 || argc == 2)) { r = bufRead(recv, ki, a[0], argc > 1 ? a[1] : Undef()); known = true; }
                        else if (ki < 10 && !rd && (argc == 2 || argc == 3)) { r = bufWrite(recv, ki, a[0], a[1], argc > 2 ? a[2] : Undef()); known = true; }
                    }
                    else known = false;
                    if (!known) { missingMethod(recv, name, (int)argc); goto unwind; }
                    sp -= argc + 1;
                    CHECK();
                    PUSH(r);
                    break;
                }
            }
            if (FIRE_UNLIKELY(!leafAlive(recv))) {
                const char* nm = !strncmp(name, "try", 3) ? name + 3 : name;
                if (strcmp(nm, "TakeLocal") && strcmp(nm, "TakeUpwards") && strcmp(nm, "TakeGlobal") && strcmp(nm, "TakeTo")) { destroyedError(recv); goto unwind; }
            }
            c = vmClassOf(vm, asObj(recv)->cls);
            if (!c) { missingMethod(recv, name, (int)argc); goto unwind; }
            m = (VmProto*)vmIc(fr->chunk, at, asObj(recv)->cls, prog, &hit);
            if (!hit) {
                VmCands* cs = vmCandidates(prog, c, name, (int)argc);
                m = vmFindMethod(vm, c, name, (int)argc, sp - argc, recv, &declaring);
                if (m && !cs->runtimeChoice && !vm->pendingArgNames) {
                    if (!PERF && !vmAccessAllowed(fr->chunk, declaring, vmProtoAccess(m))) { vmAccessDenied("Method", name, declaring, vmProtoAccess(m), "called"); goto unwind; }
                    vmIcSet(fr->chunk, at, asObj(recv)->cls, m);
                } else if (m && !PERF && !vmAccessAllowed(fr->chunk, declaring, vmProtoAccess(m))) { vmAccessDenied("Method", name, declaring, vmProtoAccess(m), "called"); goto unwind; }
            }
            if (FIRE_UNLIKELY(!m)) {
                /* the built-in ownership methods of every object, ToString() of every object (DECISIONS D14) */
                const char* nm = !strncmp(name, "try", 3) ? name + 3 : name;
                int ownId = !strcmp(nm, "TakeLocal") ? OM_TakeLocal : !strcmp(nm, "TakeUpwards") ? OM_TakeUpwards : !strcmp(nm, "TakeGlobal") ? OM_TakeGlobal : !strcmp(nm, "TakeTo") ? OM_TakeTo : -1;
                Value* a = sp - argc;
                if (ownId >= 0) {
                    Value r = Undef();
                    Value targetArg = ownId == OM_TakeTo ? (argc > 0 ? a[0] : Undef()) : ownId == OM_TakeGlobal && argc == 2 ? a[1] : Undef();
                    int baseArgs = ownId == OM_TakeTo ? 1 : 0;
                    if (name != nm) {
                        OwnList* lists[128];
                        int n = vmCallLists(vm, fr, lists, 128);
                        r = Bool(ownMethodTry(ownId, recv, targetArg, (int)argc > baseArgs ? a[baseArgs] : Undef(), &fr->scope->own, lists, n, fr->self, NULL));
                    } else if ((int)argc == baseArgs || (ownId == OM_TakeGlobal && argc == 1 && a[0].kind == K_Bool)) ownMethod(ownId, recv, argc == 1 && ownId == OM_TakeGlobal ? a[0] : targetArg, &fr->scope->own);   /* (`x.TakeGlobal(true)`: the flag) */
                    else { OwnList* lists[128]; int n = vmCallLists(vm, fr, lists, 128); ownMethodT(ownId, recv, targetArg, a[baseArgs], &fr->scope->own, lists, n); }
                    sp -= argc + 1;
                    CHECK();
                    PUSH(r);
                    break;
                }
                if (!strcmp(name, "ToString") && argc == 0) { Value r = toStringValue(recv, &fr->scope->temp); sp -= 1; CHECK(); PUSH(r); break; }
                vmMethodNotFound(c, name, (int)argc);
                goto unwind;
            }
            if (FIRE_UNLIKELY((asObj(recv)->h.flags & 8) || g_isThread || vm->awaitNext)) {
                int tc = vmThreadCall(vm, m, recv, argc, name);
                if (tc < 0) goto unwind;
                if (tc > 0) { RELOAD(); break; }
            }
            vmPushCall(vm, m, (int)argc, 1, recv, FR_CALL);
            if (g_unwind.active) goto unwind;
            RELOAD();
            break;
        }
        case OP_CallBaseMethod: {
            VmClass* c = vmClassNamed(prog, KNAME(U16(ip)));
            const char* name = KNAME(U16(ip + 2));
            uint32_t argc = ip[4];
            VmClass* declaring = NULL;
            VmProto* m;
            ip += 5;
            SYNC();
            if (!c) vmUnsupported("the class", KNAME(U16(ip - 5)));
            m = vmFindMethod(vm, c, name, (int)argc, sp - argc, fr->self, &declaring);
            if (!m && !strcmp(name, "ToString") && argc == 0) { PUSH(toStringValue(fr->self, &fr->scope->temp)); CHECK(); break; }
            if (!m) { vmMethodNotFound(c, name, (int)argc); goto unwind; }
            if (!PERF && !vmAccessAllowed(fr->chunk, declaring, vmProtoAccess(m))) { vmAccessDenied("Method", name, declaring, vmProtoAccess(m), "called"); goto unwind; }
            vmPushCall(vm, m, (int)argc, 0, fr->self, FR_CALL);
            if (g_unwind.active) goto unwind;
            RELOAD();
            break;
        }
        case OP_CallStaticMethod: {
            const uint8_t* at = ip - 1;
            VmChunk* ch = fr->chunk;
            uint32_t argc = ip[4];
            VmProto* m;
            bool hit;
            ip += 5;
            SYNC();
            m = (VmProto*)vmIc(ch, at, 0, prog, &hit);
            if (!hit) {
                VmClass* c = vmClassNamed(prog, ch->kname[U16(at + 1)]);
                const char* name = ch->kname[U16(at + 3)];
                VmClass* declaring = NULL;
                if (!c) { char text[300]; snprintf(text, sizeof text, "Class '%s' not found.", ch->kname[U16(at + 1)]); fireError("MissingMemberException", text); goto unwind; }
                m = vmFindMethod(vm, c, name, (int)argc, sp - argc, Undef(), &declaring);
                if (!m) { vmMethodNotFound(c, name, (int)argc); goto unwind; }
                if (!PERF && !vmAccessAllowed(fr->chunk, declaring, vmProtoAccess(m))) { vmAccessDenied("Method", name, declaring, vmProtoAccess(m), "called"); goto unwind; }
                if (!vmCandidates(prog, c, name, (int)argc)->runtimeChoice) vmIcSet(ch, at, 0, m);
            }
            vmPushCall(vm, m, (int)argc, 0, Undef(), FR_CALL);
            if (g_unwind.active) goto unwind;
            RELOAD();
            break;
        }
        case OP_NewObject: case OP_NewObjectOwned: {
            VmClass* c = vmClassNamed(prog, KNAME(U16(ip)));
            uint32_t argc = ip[2];
            VmProto* ctor;
            Value o;
            OwnList* owner = &fr->scope->temp;
            ip += 3;
            SYNC();
            if (!c) { char text[300]; snprintf(text, sizeof text, "Class '%s' not found.", KNAME(U16(ip - 3))); fireError("MissingMemberException", text); goto unwind; }
            ctor = vmFindCtor(vm, c, (int)argc, sp - argc);
            if (!ctor) { vmCtorNotFound(c, (int)argc); goto unwind; }
            if (!PERF && !vmAccessAllowed(fr->chunk, c, vmProtoAccess(ctor))) {
                char text[300];
                snprintf(text, sizeof text, "Constructor of '%s' is %s and cannot be called from here.", c->name, vmAccessName(vmProtoAccess(ctor)));
                accessDenied(text);
                goto unwind;
            }
            if (op == OP_NewObjectOwned) {
                Value h = sp[-(int)argc - 1];
                if (h.kind != K_Class) { notObjectAssign(h); goto unwind; }
                owner = objOwned(asObj(h));
            }
            o = newObject(c->id, c->nlayout, owner);   /* unclaimed: the statement temporaries (SPEC 2.1) */
            if (FIRE_UNLIKELY(c->flags & VC_ACTOR)) markActor(o);   /* (an actor: its calls are messages from now on) */
            if (vm->pendingTArgsFor == 0 && vm->pendingTArgs) {
                objSetTArgs(asObj(o), vm->pendingTArgs);
                vm->pendingTArgs = NULL;
            }
            vmPushCall(vm, ctor, (int)argc, op == OP_NewObjectOwned ? 1 : 0, o, FR_CTOR);
            if (c->flags & VC_RECORD) {
                RecInit* ri = (RecInit*)malloc(sizeof(RecInit));
                if (!ri) outOfMemory();
                ri->o = asObj(o); ri->next = g_recInit; g_recInit = ri;
                vm->frames[vm->nframes - 1].recInit = ri;
            }
            if (g_unwind.active) goto unwind;
            RELOAD();
            break;
        }
        case OP_ConstructBase: {
            VmClass* c = vmClassNamed(prog, KNAME(U16(ip)));
            uint32_t argc = ip[2];
            VmProto* ctor;
            ip += 3;
            SYNC();
            if (!c) vmUnsupported("the class", KNAME(U16(ip - 3)));
            ctor = vmFindCtor(vm, c, (int)argc, sp - argc);
            if (!ctor) { vmCtorNotFound(c, (int)argc); goto unwind; }
            vmPushCall(vm, ctor, (int)argc, 0, fr->self, FR_BASECTOR);
            if (g_unwind.active) goto unwind;
            RELOAD();
            break;
        }
        case OP_CopyArgs: {
            /* the way each argument of the next call goes in (4 bits each): kept for the call (vmPushCall) */
            vm->copyMask = (uint64_t)U16(ip) | ((uint64_t)U16(ip + 2) << 16) | ((uint64_t)U16(ip + 4) << 32) | ((uint64_t)U16(ip + 6) << 48);
            ip += 8;
            break;
        }
        case OP_OverloadHint: ip += 2; break;   /* (the scoring of the VM finds the same overload: the hint only spares it) */
        case OP_ArgNames: vm->pendingArgNames = KNAME(U16(ip)); ip += 2; break;
        case OP_SetTypeArgs: {
            const char* text = KNAME(U16(ip));
            uint32_t forCall = ip[2];
            ip += 3;
            vm->pendingTArgs = vmTArgsOf(vm, fr, text);
            { const char* q; vm->pendingTArgsN = 1; for (q = text; q && *q; q++) if (*q == '|') vm->pendingTArgsN++; }
            vm->pendingTArgsFor = (uint8_t)(forCall == 3 ? 1 : forCall);
            break;
        }
        case OP_TakeCallTypeArgs: break;   /* (the frame took them when the call began) */

        /* ---- fields ---- */
        case OP_GetField: {
            const uint8_t* at = ip - 1;
            const char* name = KNAME(U16(ip));
            Value v = sp[-1];
            VmClass* c;
            int32_t idx;
            bool hit;
            ip += 2;
            if (FIRE_UNLIKELY(v.kind != K_Class)) {
                bool ok;
                Value n;
                if (name[0] == 'L' || name[0] == 'l') {
                    if (!strcmp(name, "Length") || !strcmp(name, "length")) { SYNC(); n = lengthOf(v, &ok); CHECK(); if (ok) { sp[-1] = n; break; } }
                }
                if (v.kind == K_Buffer && !strcmp(name, "littleEndian")) { sp[-1] = Bool(bufOf(v)->little != 0); break; }
                SYNC(); missingField(v, name); goto unwind;
            }
            if (FIRE_UNLIKELY(!leafAlive(v))) { SYNC(); destroyedError(v); goto unwind; }
            idx = (int32_t)(intptr_t)vmIc(fr->chunk, at, asObj(v)->cls, prog, &hit);
            if (FIRE_LIKELY(hit)) { Value x = objFields(asObj(v))[idx]; if (FIRE_UNLIKELY(isRef(x))) holdTemp(x, &fr->scope->temp); sp[-1] = x; break; }
            SYNC();
            c = vmClassOf(vm, asObj(v)->cls);
            idx = c ? vmFieldIndex(c, name) : -1;
            if (idx >= 0) {
                VmClass* declaring = NULL;
                const VmFieldInfo* fi = vmFieldInfo(c, name, &declaring);
                if (!PERF && fi && fi->access && !vmAccessAllowed(fr->chunk, declaring, fi->access)) { vmAccessDenied("Field", name, declaring, fi->access, "accessed"); goto unwind; }
                vmIcSet(fr->chunk, at, asObj(v)->cls, (void*)(intptr_t)idx);
                sp[-1] = objFields(asObj(v))[idx];
                if (isRef(sp[-1])) holdTemp(sp[-1], &fr->scope->temp);
                break;
            }
            {   /* a property: its getter (`get_` + name) */
                char gname[200];
                VmClass* declaring = NULL;
                VmProto* g;
                snprintf(gname, sizeof gname, "get_%s", name);
                g = c ? vmFindMethod(vm, c, vmName(prog, gname), 0, NULL, v, &declaring) : NULL;
                if (!g) { missingField(v, name); goto unwind; }
                if (!PERF && !vmAccessAllowed(fr->chunk, declaring, vmProtoAccess(g))) {
                    char text[300];
                    snprintf(text, sizeof text, "'%s' of '%s' is %s and cannot be accessed from here.", gname, declaring->name, vmAccessName(vmProtoAccess(g)));
                    accessDenied(text);
                    goto unwind;
                }
                vmPushCall(vm, g, 0, 1, v, FR_CALL);
                if (g_unwind.active) goto unwind;
                RELOAD();
            }
            break;
        }
        case OP_SetField: case OP_SetFieldOnThis: case OP_SetFieldClaim: case OP_SetFieldAccessor: {
            const uint8_t* at = ip - 1;
            const char* name = KNAME(U16(ip));
            uint32_t flags = (op == OP_SetFieldClaim || op == OP_SetFieldAccessor) ? ip[2] : 0;
            Value x = POP();
            Value v = op == OP_SetFieldOnThis ? fr->self : POP();
            VmClass* c;
            int32_t idx;
            Value old;
            bool hit;
            ip += (op == OP_SetFieldClaim || op == OP_SetFieldAccessor) ? 3 : 2;
            SYNC();
            if (FIRE_UNLIKELY(v.kind != K_Class)) { notObjectAssign(v); goto unwind; }
            if (FIRE_UNLIKELY(!leafAlive(v))) { destroyedError(v); goto unwind; }
            c = vmClassOf(vm, asObj(v)->cls);
            idx = (int32_t)(intptr_t)vmIc(fr->chunk, at, asObj(v)->cls, prog, &hit);
            if (!hit) {
                idx = c ? vmFieldIndex(c, name) : -1;
                if (idx < 0) {
                    /* a property: its setter (`set_` + name); the assignment's value stays */
                    char sname[200];
                    VmClass* declaring = NULL;
                    VmProto* s;
                    snprintf(sname, sizeof sname, "set_%s", name);
                    s = c ? vmFindMethod(vm, c, vmName(prog, sname), 1, &x, v, &declaring) : NULL;
                    if (!s) {
                        char gname[200];
                        snprintf(gname, sizeof gname, "get_%s", name);
                        if (c && vmFindMethod(vm, c, vmName(prog, gname), 0, NULL, v, NULL)) noSetterError(v, name);
                        else missingFieldSet(v, name);
                        goto unwind;
                    }
                    if (!PERF && !vmAccessAllowed(fr->chunk, declaring, vmProtoAccess(s))) {
                        char text[300];
                        snprintf(text, sizeof text, "'%s' of '%s' is %s and cannot be accessed from here.", sname, declaring->name, vmAccessName(vmProtoAccess(s)));
                        accessDenied(text);
                        goto unwind;
                    }
                    PUSH(x);
                    vm->sp = (uint32_t)(sp - st);
                    vmPushCall(vm, s, 1, 0, v, FR_CALL);
                    if (g_unwind.active) goto unwind;
                    vm->frames[vm->nframes - 1].retMode = op == OP_SetFieldOnThis ? RM_DROP : RM_VALUE;
                    vm->frames[vm->nframes - 1].after = x;
                    RELOAD();
                    break;
                }
                {
                    VmClass* declaring = NULL;
                    const VmFieldInfo* fi = vmFieldInfo(c, name, &declaring);
                    bool checks = fi && (fi->requiredUnit || fi->requiredKind || fi->requiredType || fi->requiredArray || (c->flags & VC_RECORD));
                    if (!PERF && fi && fi->access && !vmAccessAllowed(fr->chunk, declaring, fi->access)) { vmAccessDenied("Field", name, declaring, fi->access, "accessed"); goto unwind; }
                    if (fi && fi->requiredUnit) { x = vmAdaptUnit(vm, x, fi->requiredUnit); CHECK(); }
                    if (fi && fi->requiredKind) {
                        char what[200];
                        snprintf(what, sizeof what, "Field '%s'", name);
                        if (vmGuard(&x, fi->requiredKind, what, false)) goto unwind;
                    }
                    if (fi && fi->requiredType) {
                        char what[200];
                        snprintf(what, sizeof what, "Field '%s'", name);
                        if (vmGuardType(vm, &x, fi->requiredType, v, NULL, what, false)) goto unwind;
                    }
                    if (fi && fi->arrayOfParam && x.kind == K_Array) { vmTypeArrayOfParam(vm, x, fi->arrayOfParam, v, NULL); CHECK(); }
                    if ((c->flags & VC_RECORD) && !recInit(asObj(v)) && !vm->initNext) { recordWriteError(asObj(v), name); goto unwind; }
                    vm->initNext = false;
                    if (!checks) vmIcSet(fr->chunk, at, asObj(v)->cls, (void*)(intptr_t)idx);
                }
            }
            if (FIRE_UNLIKELY(g_isThread) && g_domainOn) {   /* (a change of an object of the shared domain is one section, rule 10) */
                char what[240];
                bool ss = sectionScopeEnterV(v);
                if (op == OP_SetFieldClaim && !(flags & 2)) noteHolderClaim(v, x, &fr->scope->temp);
                snprintf(what, sizeof what, "The field '%s' of an object of the globals", name);
                if (sharedValue(v) && !toMainHolder(v, &x, what)) { sectionScopeExit(ss); goto unwind; }
                old = objFields(asObj(v))[idx];
                objFields(asObj(v))[idx] = x;
                retain(x);
                release(old);
                sectionScopeExit(ss);
            } else {
                old = objFields(asObj(v))[idx];
                objFields(asObj(v))[idx] = x;
                retain(x);
                release(old);
            }
            if ((op == OP_SetFieldClaim || op == OP_SetFieldAccessor) && !(flags & 2)) claimToHolder(v, x, &fr->scope->temp);
            if (op != OP_SetFieldOnThis) PUSH(x);
            break;
        }
        case OP_HasMember: {
            const char* name = KNAME(U16(ip));
            Value v = sp[-1];
            bool has = false;
            ip += 2;
            if (v.kind == K_Class) {
                VmClass* c = vmClassOfValue(vm, v);
                char acc[200];
                if (c && leafAlive(v)) {
                    has = vmFieldIndex(c, name) >= 0 || vmHasMethodNamed(c, name) || !strcmp(name, "TakeTo") || !strcmp(name, "TakeLocal") || !strcmp(name, "TakeUpwards") || !strcmp(name, "TakeGlobal");
                    if (!has) { snprintf(acc, sizeof acc, "get_%s", name); has = vmHasMethodNamed(c, vmName(prog, acc)); }
                }
            } else has = fire_builtinHas(v, name);
            sp[-1] = Bool(has);
            break;
        }

        /* ---- statics ---- */
        case OP_SetStaticFieldOnInit: case OP_SetStaticField: case OP_GetStaticField: {
            VmClass* c = vmClassNamed(prog, KNAME(U16(ip)));
            const char* name = KNAME(U16(ip + 2));
            VmClass* cls = c;
            int32_t i;
            ip += 4;
            SYNC();
            if (!c) { char text[300]; snprintf(text, sizeof text, "Class '%s' not found.", KNAME(U16(ip - 4))); fireError("MissingMemberException", text); goto unwind; }
            if ((i = vmStaticIndex(&c, name)) < 0) {
                /* a static property: `get_`/`set_` + name */
                char acc[200];
                VmProto* m;
                snprintf(acc, sizeof acc, "%s_%s", op == OP_GetStaticField ? "get" : "set", name);
                m = vmFindMethod(vm, cls, vmName(prog, acc), op == OP_GetStaticField ? 0 : 1, op == OP_GetStaticField ? NULL : sp - 1, Undef(), NULL);
                if (!m) {
                    char text[300];
                    snprintf(text, sizeof text, "'%s' has no static field '%s' (and no static 'get_%s' property).", cls->name, name, name);
                    fireError("MissingMemberException", text);
                    goto unwind;
                }
                if (op == OP_GetStaticField) vmPushCall(vm, m, 0, 0, Undef(), FR_CALL);
                else {
                    Value x = sp[-1];
                    vmPushCall(vm, m, 1, 0, Undef(), FR_CALL);
                    vm->frames[vm->nframes - 1].retMode = op == OP_SetStaticField ? RM_VALUE : RM_DROP;
                    vm->frames[vm->nframes - 1].after = x;
                }
                if (g_unwind.active) goto unwind;
                RELOAD();
                break;
            }
            if (!PERF && op != OP_SetStaticFieldOnInit) {
                VmClass* declaring = NULL;
                const VmFieldInfo* fi = vmFieldInfo(c, name, &declaring);
                if (fi && fi->access && !vmAccessAllowed(fr->chunk, declaring, fi->access)) { vmAccessDenied("Static field", name, declaring, fi->access, "accessed"); goto unwind; }
            }
            if (op == OP_GetStaticField) { if (isRef(c->svals[i])) holdTemp(c->svals[i], &fr->scope->temp); PUSH(c->svals[i]); }
            else {
                Value x = op == OP_SetStaticField ? sp[-1] : POP();
                Value old = c->svals[i];
                if (op == OP_SetStaticField) {
                    const VmFieldInfo* fi = vmFieldInfo(c, name, NULL);
                    if (fi && fi->requiredUnit) { x = vmAdaptUnit(vm, x, fi->requiredUnit); CHECK(); sp[-1] = x; }
                }
                if (FIRE_UNLIKELY(g_isThread)) {   /* (a static field of the main program from a fire thread: a section, rule 10) */
                    bool ss = sectionScopeEnter(true);
                    if (!toMainGlobals(&x, NULL, "A static field")) { sectionScopeExit(ss); goto unwind; }
                    old = c->svals[i];
                    retain(x);
                    c->svals[i] = x;
                    release(old);
                    sectionScopeExit(ss);
                    if (op == OP_SetStaticField) sp[-1] = x;
                    break;
                }
                retain(x);
                c->svals[i] = x;
                release(old);
            }
            break;
        }

        /* ---- the checks of declarations ---- */
        case OP_GuardLocal: {
            uint32_t slot = U16(ip), tag = ip[2];
            const char* what = KNAME(U16(ip + 3));
            uint32_t required = ((tag & 0x7F) + 1) | (tag & 0x80);
            ip += 7;
            if (slot < fr->scope->hi) { SYNC(); if (vmGuard(&fr->scope->slots[slot], required, what, true)) goto unwind; }
            break;
        }
        case OP_GuardTop: {
            uint32_t required = (uint32_t)ip[0] + 1;
            const char* what = KNAME(U16(ip + 1));
            ip += 3;
            SYNC();
            if (vmGuard(&sp[-1], required, what, false)) goto unwind;
            break;
        }
        case OP_GuardTopType: case OP_GuardLocalType: {
            const char* spec;
            const char* what;
            Value* v;
            if (op == OP_GuardTopType) { spec = KNAME(U16(ip)); what = KNAME(U16(ip + 2)); v = &sp[-1]; ip += 4; }
            else { uint32_t slot = U16(ip); spec = KNAME(U16(ip + 2)); what = KNAME(U16(ip + 4)); ip += 6; v = slot < fr->scope->hi ? &fr->scope->slots[slot] : NULL; }
            SYNC();
            if (v && spec && vmGuardType(vm, v, spec, fr->self, fr->ctargs, what, op == OP_GuardLocalType)) goto unwind;
            break;
        }
        case OP_CheckUnit: {
            const char* unitText = KNAME(U16(ip));
            ip += 2;
            SYNC();
            if (unitText) { sp[-1] = vmAdaptUnit(vm, sp[-1], unitText); CHECK(); }
            break;
        }
        case OP_RequireRefParam: {
            uint32_t slot = U16(ip);
            const char* name = KNAME(U16(ip + 2));
            ip += 4;
            SYNC();
            if (slot < fr->scope->hi) { requireRef(fr->scope->slots[slot], name ? name : "?"); CHECK(); }
            break;
        }
        case OP_CastTo: {
            const char* t = KNAME(U16(ip));
            Value v = sp[-1];
            ip += 2;
            SYNC();
            if (v.kind == K_Class && !leafAlive(v) && strcmp(t, "class")) { destroyedError(v); goto unwind; }
            if (v.kind != K_Undefined && !vmIsOfName(vm, v, t)) { castFailed(v, t); goto unwind; }
            break;
        }
        case OP_IsOfType: {
            const char* t = KNAME(U16(ip));
            Value v = sp[-1];
            ip += 2;
            if (v.kind == K_Class && !leafAlive(v) && strcmp(t, "class") && strcmp(t, "bool") && strcmp(t, "int") && strcmp(t, "float") && strcmp(t, "char")
                && strcmp(t, "word") && strcmp(t, "string") && strcmp(t, "undefined")) { SYNC(); destroyedError(v); goto unwind; }
            sp[-1] = Bool(vmIsOfName(vm, v, t));
            break;
        }
        case OP_TypeParamIsOf: {
            const char* param = KNAME(U16(ip));
            const char* target = KNAME(U16(ip + 2));
            TArgs ta = vmTArgsOf(vm, fr, param);
            ip += 4;
            PUSH(Bool(ta && ta[0] && ta[0]->display && vmTypeTextIsOf(vm, ta[0]->display, target)));
            break;
        }
        case OP_Trust: break;
        case OP_RecordCheck: { uint32_t m = *ip++; SYNC(); if (m == 1) recordRequire(sp[-1]); else (void)recordCheckV(sp[-1]); CHECK(); break; }
        case OP_InitNext: vm->initNext = true; break;

        /* ---- arrays ---- */
        case OP_NewArray: {
            Value n = sp[-1];
            SYNC();
            {   /* (`new byte[n]`, `new word[n]`: a buffer - the TypeArray behind it, maybe after the open mark) */
                const uint8_t* t = ip;
                if (t[0] == OP_TypeArray && (t[1] & 0x80) && t[2] == 1 && t[5] == OP_TypeArray) t += 5;
                if (t[0] == OP_TypeArray && t[2] == 1 && !(t[1] & 0x80) && ((t[1] & 0x3F) == 11 || (t[1] & 0x3F) == 12)) {
                    if ((t[1] & 0x3F) == 11) sp[-1] = n.kind == K_Int && n.i < 0 ? sizeError(n.i, false) : newBuffer(n, &fr->scope->temp);
                    else sp[-1] = newWordBuffer(n, &fr->scope->temp);
                    CHECK();
                    ip = t + 5;
                    break;
                }
                /* (`new int[n]`, `new Dog[n]`: the typed array at once - its elements are the zero of the type: 0, "", an enum's first value) */
                if (t[0] == OP_TypeArray && t[2] == 1 && !(t[1] & 0x80)) {
                    uint32_t elem = t[1] & 0x3F, unitIdx = U16(t + 3), unit = 0;
                    uint8_t fl = (t[1] & 0x40) ? 8 : 0;
                    Value unitText = vmConstStrUtf8(prog, "(no unit)");
                    const char* txt = unitIdx != 0xFFFF && unitIdx < fr->chunk->nk ? fr->chunk->kname[unitIdx] : NULL;
                    bool ok = true;
                    if (elem == 13) {
                        uint32_t ac = txt ? vmArrClassOf(vm, txt) : 0;
                        if (!ac) ok = false; else { unit = ac; fl |= 16; unitText = vmConstStrUtf8(prog, g_vmArrClasses[ac - 1].display); }
                    } else if (txt) {
                        VmDemand* d = vmDemand(vm, txt);
                        if (d->any) { fl |= 4; unitText = vmConstStrUtf8(prog, "any"); }
                        else { unit = d->unit; unitText = d->display; fl |= d->dimensionOnly ? 3 : 1; }
                    }
                    if (ok && elem >= 1 && elem <= 10) {
                        sp[-1] = newTypedArray(n, (uint8_t)elem, unit, fl, unitText, &fr->scope->temp);
                        CHECK();
                        ip = t + 5;
                        break;
                    }
                }
            }
            sp[-1] = newArray(n, &fr->scope->temp);
            CHECK();
            break;
        }
        case OP_MakeBuffer: { SYNC(); sp[-1] = newBuffer(sp[-1], &fr->scope->temp); CHECK(); break; }
        case OP_MakeArrayLiteral: {
            uint32_t count = U16(ip), i;
            Arr* a;
            ip += 2;
            SYNC();
            a = allocArr(count, &fr->scope->temp);
            sp -= count;
            for (i = 0; i < count; i++) { arrItems(a)[i] = sp[i]; retain(sp[i]); }
            PUSH(ArrV(a));
            claimElements(ArrV(a), &fr->scope->temp);
            break;
        }
        case OP_MakeArrayLiteralParts: {
            uint32_t count = U16(ip), mask = U16(ip + 2) | (U16(ip + 4) << 16), i;
            Arr* a;
            ip += 6;
            SYNC();
            a = allocArr(count, &fr->scope->temp);
            sp -= count;
            for (i = 0; i < count; i++) { arrItems(a)[i] = sp[i]; retain(sp[i]); }
            for (i = 0; i < count && i < 32; i++) if ((mask >> i) & 1) attachPart(ArrV(a), arrItems(a)[i]);
            PUSH(ArrV(a));
            claimElements(ArrV(a), &fr->scope->temp);
            break;
        }
        case OP_NewJagged: {
            uint32_t ranks = *ip++;
            Value r;
            SYNC();
            sp -= ranks;
            r = newJaggedV(sp, (int)ranks, &fr->scope->temp);
            CHECK();
            PUSH(r);
            break;
        }
        case OP_TypeArray: {
            /* ArrayElem: the type in the low 6 bits (11 U8: a byte buffer, 12 Word, 13 Class), 0x40 defined, 0x80 open (`new int[n]`) */
            uint32_t code = ip[0], rank = ip[1], unitIdx = U16(ip + 2), elem = code & 0x3F;
            Value v = sp[-1];
            ip += 4;
            SYNC();
            if (code & 0x80) { if (v.kind != K_Buffer) arrMarkOpen(v, (uint8_t)elem, (int)rank, 1); break; }
            if (elem == 11) { sp[-1] = typeBuffer(v, (int)rank, &fr->scope->temp); CHECK(); break; }
            if (elem == 12) { sp[-1] = typeWordBuffer(v, (int)rank, &fr->scope->temp); CHECK(); break; }
            if (elem == 13) {   /* an array of a class, a string or an enum: its entry of g_arrClasses */
                const char* t = unitIdx != 0xFFFF && unitIdx < fr->chunk->nk ? fr->chunk->kname[unitIdx] : NULL;
                uint32_t ac = t ? vmArrClassOf(vm, t) : 0;
                if (ac) { typeArray(v, 13, (int)rank, ac, (uint8_t)(16 | ((code & 0x40) ? 8 : 0)), vmConstStrUtf8(prog, g_vmArrClasses[ac - 1].display), 1); CHECK(); }
                break;
            }
            {
                uint32_t unit = 0;
                Value unitText = vmConstStrUtf8(prog, "(no unit)");
                uint8_t fl = (code & 0x40) ? 8 : 0;
                if (unitIdx != 0xFFFF && unitIdx < fr->chunk->nk && fr->chunk->kname[unitIdx]) {
                    const char* t = fr->chunk->kname[unitIdx];
                    VmDemand* d = vmDemand(vm, t);
                    if (d->any) { fl |= 4; unitText = vmConstStrUtf8(prog, "any"); }
                    else { unit = d->unit; unitText = d->display; fl |= d->dimensionOnly ? 3 : 1; }
                }
                typeArray(v, (uint8_t)elem, (int)rank, unit, fl, unitText, 1);
                CHECK();
            }
            break;
        }
        case OP_TypeArrayOfParam: { const char* t = KNAME(U16(ip)); ip += 2; SYNC(); vmTypeArrayOfParam(vm, sp[-1], t, fr->self, fr->ctargs); CHECK(); break; }
        case OP_ArrayGet: {
            Value i = sp[-1], a = sp[-2];
            if (FIRE_UNLIKELY(a.kind == K_Class)) {
                VmClass* c = vmClassOfValue(vm, a);
                VmProto* g;
                SYNC();
                if (!leafAlive(a)) { destroyedError(a); goto unwind; }
                g = c ? vmFindMethod(vm, c, vmName(prog, "GetIndex"), 1, &sp[-1], a, NULL) : NULL;
                if (!g) { char text[300]; snprintf(text, sizeof text, "Index access ('[]') on an instance of '%s', which has no 'GetIndex' method.", c ? c->name : "?"); fireError("ArgumentException", text); goto unwind; }
                vmPushCall(vm, g, 1, 1, a, FR_CALL);
                if (g_unwind.active) goto unwind;
                RELOAD();
                break;
            }
            SYNC();
            sp[-2] = arrayGet(a, i);
            sp--;
            CHECK();
            if (isRef(sp[-1])) holdTemp(sp[-1], &fr->scope->temp);
            break;
        }
        case OP_ArraySet: {
            Value v = sp[-1], i = sp[-2], a = sp[-3];
            if (FIRE_UNLIKELY(a.kind == K_Class)) {
                VmClass* c = vmClassOfValue(vm, a);
                VmProto* s;
                SYNC();
                if (!leafAlive(a)) { destroyedError(a); goto unwind; }
                s = c ? vmFindMethod(vm, c, vmName(prog, "SetIndex"), 2, &sp[-2], a, NULL) : NULL;
                if (!s) { char text[300]; snprintf(text, sizeof text, "Index assignment ('[]=') on an instance of '%s', which has no 'SetIndex' method.", c ? c->name : "?"); fireError("ArgumentException", text); goto unwind; }
                vmPushCall(vm, s, 2, 1, a, FR_CALL);
                if (g_unwind.active) goto unwind;
                vm->frames[vm->nframes - 1].retMode = RM_VALUE;
                vm->frames[vm->nframes - 1].after = v;
                RELOAD();
                break;
            }
            SYNC();
            if (FIRE_UNLIKELY(g_isThread) && g_domainOn) {
                bool ss = sectionScopeEnterV(a);
                if (sharedValue(a) && !toMainHolder(a, &v, "An element of an array of the globals")) { sectionScopeExit(ss); goto unwind; }
                sp[-3] = arraySet(a, i, v);
                sectionScopeExit(ss);
            } else sp[-3] = arraySet(a, i, v);
            sp -= 2;
            CHECK();
            break;
        }
        case OP_IncDecIndex: {
            bool inc = ip[0] != 0, prefix = ip[1] != 0;
            Value i = sp[-1], a = sp[-2], old, one, nv;
            ip += 2;
            SYNC();
            old = arrayGet(a, i);
            CHECK();
            one = stepOf(old, Int(1));
            nv = inc ? add(old, one) : sub(old, one);
            CHECK();
            nv = arraySet(a, i, nv);
            CHECK();
            sp -= 2;
            PUSH(prefix ? nv : old);
            break;
        }

        /* ---- pointers ---- */
        case OP_AddressOfLocal: { VmScope* s = scAncestor(fr->scope, U16(ip)); uint32_t slot = U16(ip + 2); ip += 4; PUSH(PtrV(scSlot(s, slot))); break; }
        case OP_AddressOfGlobal: { uint32_t slot = U16(ip); ip += 2; PUSH(PtrV(scSlot(vmGlobalsOf(vm, slot), slot))); break; }
        case OP_AddressOfField: {
            const char* name = KNAME(U16(ip));
            Value v = sp[-1];
            VmClass* c = vmClassOfValue(vm, v);
            int32_t idx;
            ip += 2;
            SYNC();
            if (!c) { missingField(v, name); goto unwind; }
            if (!leafAlive(v)) { destroyedError(v); goto unwind; }
            idx = vmFieldIndex(c, name);
            if (idx < 0) { missingField(v, name); goto unwind; }
            sp[-1] = PtrV(&objFields(asObj(v))[idx]);
            break;
        }
        case OP_AddressOfIndex: { Value i = POP(); SYNC(); sp[-1] = addressOfIndex(sp[-1], i); CHECK(); break; }
        case OP_PtrRead: { SYNC(); sp[-1] = ptrRead(sp[-1]); CHECK(); break; }
        case OP_PtrWrite: { Value v = POP(); SYNC(); ptrWrite(sp[-1], v); CHECK(); sp[-1] = v; break; }

        /* ---- probes (reflection, phase B step 5) ---- */
        case OP_Probe: case OP_SilenceMember: vmUnsupported("probes", NULL);
        case OP_SilenceValue: vmUnsupported("probes", NULL);

        /* ---- exceptions ---- */
        case OP_RegisterHandler: {
            VmHandlerT* t = &fr->chunk->handlers[U16(ip)];
            VmHandlerRec* r = (VmHandlerRec*)malloc(sizeof(VmHandlerRec));
            ip += 2;
            if (!r) outOfMemory();
            if (!t->resolved) vmResolveHandler(vm, t);
            r->vm = vm; r->t = t; r->frame = vm->nframes - 1; r->scope = fr->scope; r->sp = (uint32_t)(sp - st); r->extraExits = 0; r->inCatch = false; r->ncarry = 0;
            r->h.fn = vmCatchFn; r->h.ctx = r; r->h.info = &t->info;
            pushHandler(&r->h);
            break;
        }
        case OP_UnregisterHandler: {
            VmHandlerRec* r = (VmHandlerRec*)g_handlers;
            if (r && r->h.fn == vmCatchFn && r->frame == vm->nframes - 1) { popHandler(); free(r); }
            break;
        }
        case OP_Throw: {
            Value x = POP(), r;
            SYNC();
            r = throwValue(x);
            CHECK();
            PUSH(r);   /* (resumed: the value of the throw) */
            break;
        }
        case OP_ResumeException: { Value v = POP(), e = POP(); SYNC(); resumeThrow(e, v); CHECK(); PUSH(Undef()); break; }
        case OP_ClearPendingResume: { Value e = POP(); clearPending(e); break; }
        case OP_EnterFinallyNormal: PUSH(Undef()); PUSH(Int(FIN_K_NORMAL)); break;
        case OP_PushJump: { uint32_t to = U16(ip); ip += 2; PUSH(Int((int64_t)to)); PUSH(Int(FIN_K_JUMP)); break; }
        case OP_EndFinally: {
            Value kind = POP(), payload = POP();
            SYNC();
            switch ((int)kind.i) {
                case FIN_K_THROW: throwValue(payload, false); CHECK(); break;
                case FIN_K_JUMP: ip = fr->chunk->code + payload.i; break;
                case FIN_K_LEAVE: startLeave(); goto unwind;   /* (leave/terminate passed through: it goes on after the finally) */
                case FIN_K_RETURN: {
                    VmHandlerRec* fin = vmFinallyOf(vm);
                    if (fin) {
                        popHandler();
                        vmLeaveTo(vm, fr, fin->scope);
                        sp = st + fin->sp;
                        PUSH(payload);
                        PUSH(Int(FIN_K_RETURN));
                        ip = fr->chunk->code + fin->t->finallyAddr;
                        free(fin);
                        break;
                    }
                    {   /* (the count it carried through the finally goes once vmReturn counted it for its trip) */
                        bool more = vmReturn(vm, payload, base);
                        release(payload);
                        if (!more) return;
                    }
                    RELOAD();
                    goto afterReturn;
                }
                default: break;
            }
            break;
        }

        /* ---- threads, tasks, actors, the end of the program (phase B step 3, vm_threads.c) ---- */
        case OP_SetAutoSync: { uint32_t m = *ip++; if (m != GJ_SAFEPOINTS) { g_globalJobs = (uint8_t)m; g_attnMask &= ~(uint32_t)ATTN_SECT; } break; }
        case OP_SetEndWait: { Value t = POP(); SYNC(); fire_setEndWait(t); CHECK(); break; }
        case OP_SetTimeout: { Value t = POP(); SYNC(); setDefaultTimeout(t); CHECK(); break; }
        case OP_SyncGlobals: { int64_t n; SYNC(); n = syncGlobals(); RELOAD(); CHECK(); PUSH(Int(n)); break; }
        case OP_SectionEnter: SYNC(); sectionEnterOp(); break;
        case OP_SectionExit: SYNC(); sectionExitOp(); break;
        case OP_Dedicate: case OP_Undedicate: break;   /* (a fire thread of the VM is a system thread already) */
        case OP_AwaitReply: vm->awaitNext = 1; break;
        case OP_CancelPush: {
            uint32_t f = *ip++, n = f & 0x7F, i;
            Value lv[128];
            for (i = 0; i < n; i++) lv[i] = sp[-(int)n + (int)i];
            if (n == 0) lv[0] = Undef();
            sp -= n;
            SYNC();
            fire_untilPush(lv, n, (f & 0x80) != 0);
            CHECK();
            break;
        }
        case OP_CancelPop: SYNC(); fire_untilPop(); CHECK(); break;
        case OP_Fire: {
            VmProto* body = fr->chunk->fn[U16(ip)];
            uint32_t gs = U16(ip + 2), taking = ip[4], flags = ip[5], handover = ip[6];
            uint32_t with = (flags & 1) == 0 ? 0 : ((flags >> 2) > 1 ? (flags >> 2) : 1);
            bool ok;
            ip += 7;
            SYNC();
            ok = vmFire(vm, fr, body, gs, taking, flags, handover);
            RELOAD();
            sp -= taking + with;
            if (!ok) { SYNC(); goto unwind; }
            break;
        }
        case OP_Sync: { uint32_t f = *ip++; SYNC(); sp[-1] = syncValue(sp[-1], (f & 2) != 0); CHECK(); break; }
        case OP_Process: { Value v = POP(); SYNC(); processMsg(v, true, &fr->scope->temp); RELOAD(); CHECK(); break; }
        case OP_TryProcess: { Value v = sp[-1]; int n; SYNC(); n = processMsg(v, false, &fr->scope->temp); RELOAD(); CHECK(); sp[-1] = Bool(n > 0); break; }
        case OP_Leave: SYNC(); leaveNow(); goto unwind;
        case OP_Terminate: { Value code = POP(); SYNC(); terminateNow(code); goto unwind; }
        case OP_RegisterThreadsCatch: case OP_RegisterTerminateCatch: {
            VmProto* h = fr->chunk->fn[U16(ip)];
            bool threads = op == OP_RegisterThreadsCatch;
            const char* type = threads && ip[2] ? KNAME(U16(ip + 3)) : NULL;
            ip += threads ? (ip[2] ? 5 : 3) : 2;   /* (the type only with the flag: OpInfo) */
            vmRegisterCatch(vm, h, threads, type);
            break;
        }
        case OP_PostGlobal: {
            uint32_t f = ip[0], with = ip[1];
            bool ok;
            ip += 2;
            SYNC();
            ok = vmPostGlobal(vm, f, with);
            RELOAD();
            if (!ok) goto unwind;
            break;
        }
        case 255: SYNC(); goto unwind;   /* (g_vmUnwindStub: the safe point at a function's entry started an unwinding) */

        default:
            SYNC();
            vmUnsupported("the opcode", op < sizeof g_vmOpNames / sizeof g_vmOpNames[0] ? g_vmOpNames[op] : "?");
        }
        continue;
    afterReturn:
        /* (an operator of an object stood for a JumpIfNot: its result decides the jump now) */
        if (FIRE_UNLIKELY(fr->catchEnded & 2)) {
            Value c = POP();
            bool t;
            fr->catchEnded &= (uint8_t)~2;
            SYNC();
            t = truthy(c);
            CHECK();
            if (!t) { VM_LOOP_POINT(fr->opJump, 0); ip = fr->chunk->code + fr->opJump; }
        }
        continue;
    unwind:
        if (!vmUnwind(vm, base)) return;
        RELOAD();
    }
}

/* The unwinding state is set (an exception passes, a catch block left with a jump or a return, a resume): the frames go until the one the target belongs
 * to, like the generated functions return one by one. 1: the run goes on (at the landing place), 0: the base frame of this run is gone - the caller (a catch
 * block's callback, a nested call) goes on unwinding. */
static void vmFreeDead(Vm* vm) {
    while (vm->dead) { VmHandlerRec* r = vm->dead; vm->dead = r->nextDead; free(r); }
}
static int vmUnwind(Vm* vm, int32_t base) {
    for (;;) {
        VmFrame* f = &vm->frames[vm->nframes - 1];
        Handler* tg = g_unwind.target;
        VmHandlerRec* rec = tg && tg->fn == vmCatchFn ? (VmHandlerRec*)tg : NULL;
        if (f->kind != FR_CATCH && rec && rec->frame == vm->nframes - 1) {
            /* the frame that owns the try: leave the scopes inside it, land */
            uint8_t kind = g_unwind.kind;
            Value v = g_unwind.value;
            uint32_t label = g_unwind.label, extra = rec->extraExits, ncarry = rec->ncarry, ci;
            Value carry[4];
            int32_t fin = rec->t->finallyAddr;
            bool wasInCatch = rec->inCatch;
            for (ci = 0; ci < ncarry; ci++) carry[ci] = rec->carry[ci];
            vm->sp = rec->sp;
            vmLeaveTo(vm, f, rec->scope);
            while (extra--) { VmScope* s = f->scope; if (s == f->base || s == vm->global) break; f->scope = s->parent; scLeave(vm, s); }
            clearUnwind();
            free(rec);
            vmFreeDead(vm);
            switch (kind) {
                case UW_JUMP: for (ci = 0; ci < ncarry; ci++) vm->stack[vm->sp++] = carry[ci]; f->ip = f->chunk->code + label; return 1;
                case UW_RETHROW:
                    if (fin >= 0) {   /* the exception passes this try: its finally runs, EndFinally throws it again */
                        vm->stack[vm->sp++] = v;
                        vm->stack[vm->sp++] = Int(FIN_K_THROW);
                        f->ip = f->chunk->code + fin;
                        return 1;
                    }
                    throwValue(v, false);
                    if (!g_unwind.active) return 1;
                    continue;
                case UW_RETURN:
                    if (fin >= 0 && wasInCatch) {
                        vm->stack[vm->sp++] = v;   /* (with the count of its trip: EndFinally lets go of it) */
                        vm->stack[vm->sp++] = Int(FIN_K_RETURN);
                        f->ip = f->chunk->code + fin;
                        return 1;
                    }
                    {
                        bool more = vmReturn(vm, v, base);
                        release(v);   /* (the count of the trip out of the catch) */
                        return more ? 1 : 0;
                    }
                default: fatal("internal error of the VM: an unwinding of an unknown kind.");
            }
        }
        /* leave/terminate (or the end of a thread by an exception nothing caught) pass through: the finally blocks run (completion 4 - EndFinally
         * starts the leaving again), no catch block matches (CppGenerator Landing: UW_LEAVE) */
        if (g_unwind.kind == UW_LEAVE && f->kind != FR_CATCH) {
            while (g_handlers && g_handlers->fn == vmCatchFn && ((VmHandlerRec*)g_handlers)->frame == vm->nframes - 1) {
                VmHandlerRec* r = (VmHandlerRec*)g_handlers;
                popHandler();
                if (r->t->finallyAddr >= 0 && !r->inCatch) {
                    vm->sp = r->sp;
                    vmLeaveTo(vm, f, r->scope);
                    clearUnwind();
                    vm->stack[vm->sp++] = Undef();
                    vm->stack[vm->sp++] = Int(FIN_K_LEAVE);
                    f->ip = f->chunk->code + r->t->finallyAddr;
                    free(r);
                    vmFreeDead(vm);
                    return 1;
                }
                free(r);
            }
        }
        /* a frame above the target: it goes */
        if (f->kind == FR_CATCH && tg != &f->rec->h && !(g_unwind.kind == UW_RESUME && g_unwind.token == f->rec->token)) {
            /* a catch block left by another exception (or a resume of another throw): nothing comes back to its handler - it goes when the
             * runtime is done with it (after its throwValue returned: at the next landing), unless it still has a finally to run */
            if (!(f->rec->t->finallyAddr >= 0 && tg == &f->rec->h)) { f->rec->nextDead = vm->dead; vm->dead = f->rec; }
        }
        if (f->kind == FR_TOP) {
            /* (the bottom of the script: what nothing caught ends it) */
            vm->sp = 0;
            vmDropHandlers(vm, vm->nframes - 1);
            return 0;
        }
        vm->sp = f->spBase;
        vm->nfin = f->nfin;
        vmRecInitEnd(f);
        vmDropHandlers(vm, vm->nframes - 1);
        vmLeaveTo(vm, f, vmStopOf(vm, f));
        if (f->kind == FR_CTOR || f->kind == FR_BASECTOR) ctorEnd(f->self);   /* (a constructor that ended with an exception made no object: no destructor) */
        vm->nframes--;
        if (vm->nframes <= base) return 0;
    }
}
