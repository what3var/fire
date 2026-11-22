/* fire VM in C: the debug check for references that outlive their object (Debug mode; VM.cs NoteStore/ReportDangling, src/fire/Runtime/DanglingWatch.cs).
 *
 * An object that belongs to a call (a local, an argument passed with `take`) dies when the call ends - also when a field or an array was given only a
 * reference to it (`this.last = local`). The next use of that field throws a DestroyedException, far away from the cause. In Debug mode every store of
 * such an object into a field or an array element is noted (vmDgNote: the object, the holder, the field); when the object dies, the holders that still
 * point to it and live on are reported once per place, as a line of the program's output:
 *   warning (line 5): an Item is destroyed at the end of the call it belonged to, but the array in the field 'slots' of a Shelf still points to it (...).
 *
 * Where an object belongs: up the chain of its owners (OwnList.holder) to a list without a holder - the own list of a scope (marked by scNew:
 * OwnList.depth, which the runtime does not use) or the temporaries of one (`home`). The global scope (parent NULL) counts as no scope, like the C# VM's
 * IsGlobal. A holder "dies with" the scope when it is destroyed already or belongs to that scope or one inside it (the parents of a scope's own list).
 * The runtime calls fire_vmDangling when an object is destroyed while something is noted (g_vmDangling; destroy/destroyList behind FIRE_VM). */

#define VM_DG_REFS 8   /* (as many holders per object as the C# VM keeps) */
typedef struct VmDgRef { Value holder; const char* field; } VmDgRef;
typedef struct VmDgEntry { Obj* target; struct VmDgEntry* next; uint32_t n; VmDgRef refs[VM_DG_REFS]; } VmDgEntry;
#define VM_DG_BUCKETS 1024u
static VmDgEntry** g_vmDgTable;
uint32_t g_vmDangling;   /* (the objects with noted holders - the runtime asks only while there are any) */
/* the places a strand reported (each once: a loop would repeat it; per strand, as each thread has a VM of its own in C#) */
typedef struct VmDgSeen { char* key; struct VmDgSeen* next; } VmDgSeen;

static uint32_t vmDgHash(const Obj* o) { uintptr_t p = (uintptr_t)o; return (uint32_t)((p >> 4) ^ (p >> 14)) & (VM_DG_BUCKETS - 1); }

/* The list at the top of the chain of owners (a scope's own list for its temporaries), NULL when it has none (on its way as a return value). */
static OwnList* vmDgTop(const Owned* o) {
    OwnList* l = o->owner;
    while (l && l->holder) l = l->holder->owner;
    if (l && l->home) l = l->home;
    return l;
}
/* The scope `o` dies with: its own list, NULL for none or the global scope. */
static OwnList* vmDgScopeOf(const Owned* o) {
    OwnList* l = vmDgTop(o);
    return l && l->depth == VM_SCOPE_LIST ? l : NULL;
}
/* Does `holder` die no later than the scope (DanglingWatch.DiesWith)? */
static bool vmDgDiesWith(const Owned* holder, const OwnList* scope) {
    const Owned* n = holder;
    while (n) {
        OwnList* l;
        if (n->flags & 1) return true;
        l = n->owner;
        if (!l) return false;
        if (l->holder) { n = l->holder; continue; }
        if (l->home) l = l->home;
        for (; l; l = l->parent) if (l == scope) return true;
        return false;
    }
    return false;
}

/* `x` was stored in the field `field` of the object `holder`, or in the array `holder` (field NULL) - Debug mode (VM.NoteStore, DanglingWatch.Note). */
static void vmDgNote(Vm* vm, Value x, Value holder, const char* field) {
    Obj* t;
    Owned* h;
    OwnList* scope;
    VmDgEntry* e;
    uint32_t b, i;
    (void)vm;
    if (x.kind != K_Class || !leafAlive(x)) return;
    t = asObj(x);
    if (t->h.flags & 1) return;
    h = ownedOf(holder);
    if (!h || h == &t->h) return;
    if (!(scope = vmDgScopeOf(&t->h))) return;
    /* (a holder that dies with the same scope is no problem: a local list of local objects) */
    if (vmDgDiesWith(h, scope)) return;
    if (!g_vmDgTable) {
        g_vmDgTable = (VmDgEntry**)calloc(VM_DG_BUCKETS, sizeof(VmDgEntry*));
        if (!g_vmDgTable) outOfMemory();
    }
    b = vmDgHash(t);
    for (e = g_vmDgTable[b]; e && e->target != t; e = e->next) {}
    if (!e) {
        e = (VmDgEntry*)calloc(1, sizeof(VmDgEntry));
        if (!e) outOfMemory();
        e->target = t;
        e->next = g_vmDgTable[b];
        g_vmDgTable[b] = e;
        g_vmDangling++;
    }
    for (i = 0; i < e->n; i++) if (ownedOf(e->refs[i].holder) == h && e->refs[i].field == field) return;
    if (e->n < VM_DG_REFS) { e->refs[e->n].holder = holder; e->refs[e->n].field = field; e->n++; }
}

static const char* vmDgClassName(Vm* vm, const Obj* o) {
    VmClass* c = vmClassOf(vm, o->cls);
    return c && c->name ? c->name : "object";
}
static const char* vmDgA(const char* name) { return strchr("AEIOUaeiou", name[0]) && name[0] ? "an" : "a"; }
/* The name of the field of `owner` that holds `v` (NULL: none). */
static const char* vmDgFieldHolding(Vm* vm, Obj* owner, const Owned* v) {
    VmClass* c = vmClassOf(vm, owner->cls);
    uint32_t i;
    if (!c) return NULL;
    for (i = 0; i < c->nlayout && i < owner->nfields; i++) {
        Value f = objFields(owner)[i];
        if ((f.kind == K_Class || f.kind == K_Array) && leafAlive(f) && ownedOf(f) == v) return c->layout[i];
    }
    return NULL;
}
static Obj* vmDgOwnerObject(const Owned* o) {
    Owned* h = o->owner ? o->owner->holder : NULL;
    return h && h->okind == O_Object ? (Obj*)h : NULL;
}
/* "the List in the field 'items' of a Shelf", else "a List" (DanglingWatch.Place) */
static void vmDgPlace(Vm* vm, Obj* obj, char* out, size_t cap) {
    Obj* parent = vmDgOwnerObject(&obj->h);
    const char* name = vmDgClassName(vm, obj);
    const char* f = parent ? vmDgFieldHolding(vm, parent, &obj->h) : NULL;
    if (f) { const char* pn = vmDgClassName(vm, parent); snprintf(out, cap, "the %s in the field '%s' of %s %s", name, f, vmDgA(pn), pn); }
    else snprintf(out, cap, "%s %s", vmDgA(name), name);
}
/* Where the holder is (DanglingWatch.Describe/ArrayPlace). */
static void vmDgWhere(Vm* vm, Value holder, const char* field, char* out, size_t cap) {
    if (holder.kind == K_Class) {
        const char* hn = vmDgClassName(vm, asObj(holder));
        snprintf(out, cap, "the field '%s' of %s %s", field ? field : "?", vmDgA(hn), hn);
        return;
    }
    {
        Owned* a = ownedOf(holder);
        Obj* owner = a ? vmDgOwnerObject(a) : NULL;
        const char* on;
        const char* f;
        bool list;
        if (!owner) { snprintf(out, cap, "a list or array"); return; }
        on = vmDgClassName(vm, owner);
        list = strncmp(on, "List", 4) == 0;
        if (list && vmDgOwnerObject(&owner->h)) { vmDgPlace(vm, owner, out, cap); return; }
        f = vmDgFieldHolding(vm, owner, a);
        if (f && list) snprintf(out, cap, "%s %s", vmDgA(on), on);
        else if (f) snprintf(out, cap, "the array in the field '%s' of %s %s", f, vmDgA(on), on);
        else snprintf(out, cap, "an array of %s %s", vmDgA(on), on);
    }
}
/* Does `holder` still point to `t` (DanglingWatch.StillHolds)? */
static bool vmDgStillHolds(Vm* vm, Value holder, const char* field, const Obj* t) {
    if (!leafAlive(holder)) return false;
    if (holder.kind == K_Class) {
        VmClass* c = vmClassOf(vm, asObj(holder)->cls);
        int32_t i = c && field ? vmFieldIndex(c, field) : -1;
        Value v;
        if (i < 0 || (uint32_t)i >= asObj(holder)->nfields) return false;
        v = objFields(asObj(holder))[i];
        return v.kind == K_Class && leafAlive(v) && asObj(v) == t;
    }
    if (holder.kind == K_Array) {
        Arr* a = (Arr*)holder.p;
        Value* items;
        uint32_t i;
        if (a->typed && a->typed->packed) return false;
        items = arrItems(a);
        for (i = 0; i < a->length; i++) if (items[i].kind == K_Class && leafAlive(items[i]) && asObj(items[i]) == t) return true;
    }
    return false;
}

/* `o` is destroyed (the runtime, before it leaves `owner`): the holders that still point to it and live on are reported (DanglingWatch.Check). */
void fire_vmDangling(Obj* o, OwnList* owner) {
    VmDgEntry** p;
    VmDgEntry* e;
    Vm* vm = g_vmCur;
    OwnList* scope;
    uint32_t i;
    if (!g_vmDgTable) return;
    for (p = &g_vmDgTable[vmDgHash(o)]; *p && (*p)->target != o; p = &(*p)->next) {}
    if (!(e = *p)) return;
    *p = e->next;
    g_vmDangling--;
    scope = owner;
    while (scope && scope->holder) scope = scope->holder->owner;
    if (scope && scope->home) scope = scope->home;
    if (vm && scope && scope->depth == VM_SCOPE_LIST) {
        for (i = 0; i < e->n; i++) {
            VmDgRef* r = &e->refs[i];
            Owned* h;
            char where[400], key[600], text[1200];
            int32_t src = 0, line = 0;
            VmDgSeen* s;
            const char* name;
            if (!leafAlive(r->holder) || !(h = ownedOf(r->holder)) || vmDgDiesWith(h, scope) || !vmDgStillHolds(vm, r->holder, r->field, o)) continue;
            if (g_unwind.active && g_unwind.target && g_unwind.target->fn == vmCatchFn && ((VmHandlerRec*)g_unwind.target)->exitChunk) {
                /* (the throw site's frames below a catch block that is left: where the catch block was left - the C# VM runs it without them) */
                VmHandlerRec* rec = (VmHandlerRec*)g_unwind.target;
                vmTraceLocation(rec->exitChunk, (uint32_t)(rec->exitIp - rec->exitChunk->code), &src, &line);
            } else if (vm->nframes > 0) {
                VmFrame* f = &vm->frames[vm->nframes - 1];
                vmTraceLocation(f->chunk, (uint32_t)(f->ip - f->chunk->code), &src, &line);   /* (the next instruction's, as VM.CurrentLocation) */
            }
            name = vmDgClassName(vm, o);
            snprintf(key, sizeof key, "%s|%s|%d:%d", name,
                r->holder.kind == K_Class ? vmDgClassName(vm, asObj(r->holder)) : "[]", (int)src, (int)line);
            if (r->holder.kind == K_Class && r->field) { size_t k = strlen(key); snprintf(key + k, sizeof key - k, ".%s", r->field); }
            for (s = vm->dgSeen; s && strcmp(s->key, key) != 0; s = s->next) {}
            if (s) continue;
            s = (VmDgSeen*)malloc(sizeof(VmDgSeen));
            if (!s || !(s->key = strdup(key))) outOfMemory();
            s->next = vm->dgSeen;
            vm->dgSeen = s;
            vmDgWhere(vm, r->holder, r->field, where, sizeof where);
            snprintf(text, sizeof text, "warning (line %d): %s %s is destroyed at the end of the call it belonged to, but %s still points to it (it was given "
                "only a reference: pass the object on with `take`, e.g. `list.Add(take item)`, or let a longer-living owner take it).",
                (int)line, vmDgA(name), name, where);
            fputs(text, stdout);
            fputc('\n', stdout);
        }
    }
    free(e);
}

/* The end of a script: what is still noted goes. */
static void vmDgReset(void) {
    uint32_t b;
    if (g_vmDgTable) {
        for (b = 0; b < VM_DG_BUCKETS; b++) { VmDgEntry* e = g_vmDgTable[b]; while (e) { VmDgEntry* n = e->next; free(e); e = n; } }
        free(g_vmDgTable);
        g_vmDgTable = NULL;
    }
    g_vmDangling = 0;
}
static void vmDgSeenFree(Vm* vm) {
    while (vm->dgSeen) { VmDgSeen* n = vm->dgSeen->next; free(vm->dgSeen->key); free(vm->dgSeen); vm->dgSeen = n; }
}
