/* fire VM in C: JSON (`#import "json"`, docs/stdlib/json.md; phase B step 5) - the engine of a native build (native/runtime/fire_json.h, the same as
 * fire.Standard.JsonMethods of the C# VM) on tables the VM builds from the program when JSON is first used, the way CppGenerator.Json.cs writes them:
 * per class its fields as JSON sees them (JsonMethods.JsonFieldsOf: base class first, `[NonSerialized]` left out, `[JsonName]`), the `defined` fields behind
 * them, the flags (actor, OnDeserialized, List, JsonObject, not serializable), the slots of List and JsonObject, and the texts of the type arguments (those of
 * the VM are texts: VmTArg). Every class of the program is in the table (`"$type"` can name any). */
#define FIRE_JSON_IMPLEMENTATION 1
#include "../runtime/fire_json.h"

static JsonClassInfo* g_vmJson = NULL;   /* by the VM's index of the class (id - firstId) */
static uint32_t g_vmJsonCount = 0, g_vmJsonFirst = 0;
static JsonSpecial g_vmJsonSpecial;

/* The value of a constant attribute argument (AttributeMeta.ArgsJson: `[{"s":"name"}]`) as a text, NULL when it is none. */
static const char* vmJsonAttrString(Vm* vm, const char* json) {
    const char* s = json ? strstr(json, "{\"s\":\"") : NULL;
    char buf[256];
    size_t n = 0;
    if (!s) return NULL;
    for (s += 6; *s && *s != '"' && n + 1 < sizeof buf; s++) {
        if (*s == '\\' && s[1]) s++;
        buf[n++] = *s;
    }
    buf[n] = 0;
    return vmName(vm->prog, buf);
}
static const VmAttr* vmFieldAttr(const VmFieldInfo* fi, const char* name) {
    uint32_t i;
    for (i = 0; fi && i < fi->nattrs; i++) if (strcmp(fi->attrs[i].name, name) == 0) return &fi->attrs[i];
    return NULL;
}
/* FieldInfoOf: the field's info, from the class up its chain. */
static const VmFieldInfo* vmFieldInfoOf(VmClass* c, const char* name) {
    VmClass* k;
    uint32_t i;
    for (k = c; k; k = k->base) for (i = 0; i < k->nfinfo; i++) if (k->finfo[i].name == name) return &k->finfo[i];
    return NULL;
}
static bool vmJsonEnumNamed(const char* name) {
    uint32_t i;
    for (i = 0; i < g_enumCount; i++) {
        const char* e = g_enums[i].name;
        const char* dot = strrchr(e, '.');
        if (strcmp(e, name) == 0 || (dot && strcmp(dot + 1, name) == 0)) return true;
    }
    return false;
}
/* DeclaredType.Parse(text).SingleName of a `defined` declaration (\x04 in front): the one class or base type it names, NULL for another. */
static const char* vmJsonSingleName(Vm* vm, const char* text) {
    VmSpec* sp = vmSpec(vm, text);
    if (sp->typeParam >= 0 || !sp->alts[0] || strpbrk(sp->alts, "|&") || !strncmp(sp->alts, "in:", 3)) return NULL;
    return sp->alts;
}
/* JsonFieldInfo.defined of a field `defined string`/`defined Color`/`defined Dog` (0x40 | 1, 2, 3), else that of its kind (`defined int`: the kind, 0x80 when derived). */
static uint8_t vmJsonDefined(Vm* vm, const VmFieldInfo* fi) {
    if (!fi) return 0;
    if (fi->requiredType && fi->requiredType[0] == '\x04') {
        const char* name = vmJsonSingleName(vm, fi->requiredType);
        if (name) return !strcmp(name, "string") ? 0x41 : vmJsonEnumNamed(name) ? 0x42 : 0x43;
    }
    if (fi->requiredKind && (fi->requiredKind & 0x20)) {
        static const uint8_t kinds[] = {K_Bool, K_Int, K_Float, K_Char, K_String, K_Word};
        uint32_t tag = (uint32_t)(fi->requiredKind & 0x1F) - 1;
        return (uint8_t)((tag < 6 ? kinds[tag] : K_Bool) | ((fi->flags & 1) ? 0x80 : 0));
    }
    return 0;
}
static uint32_t vmJsonUnit(Vm* vm, const VmFieldInfo* fi) {
    const char* u = fi ? fi->requiredUnit : NULL;
    if (!u) return 0;
    while (*u == '#') u++;
    if (!*u || !strncmp(u, "any", 3)) return 0;
    {
        uint32_t id = vmUnitOfName(vm, vmName(vm->prog, u));
        return unitIsUnitless(id) ? 0 : id;
    }
}
static int vmJsonParamCount(VmClass* c, const JsonFieldInfo* rows, uint32_t n) {
    int count = c->typeParams;
    uint32_t i;
    for (i = 0; i < n; i++) if (rows[i].type && rows[i].type[0] == '$' && rows[i].type[1] >= '0' && rows[i].type[1] <= '9') { int k = atoi(rows[i].type + 1) + 1; if (k > count) count = k; }
    return count > 255 ? 255 : count;
}

static void vmJsonRow(Vm* vm, VmClass* c, JsonClassInfo* row) {
    VmClass* chain[64];
    uint32_t nchain = 0, i, k, n = 0, njson;
    JsonFieldInfo* rows = (JsonFieldInfo*)vmAlloc(&vm->prog->arena, (c->nlayout + 1) * sizeof(JsonFieldInfo));
    const char** seen = (const char**)vmAlloc(&vm->prog->arena, (c->nlayout + 1) * sizeof(char*));
    const char** names = (const char**)vmAlloc(&vm->prog->arena, (c->nlayout + 1) * sizeof(char*));   /* (the field of each row) */
    uint32_t nseen = 0;
    VmClass* x;
    int flags = 0;
    for (x = c; x && nchain < 64; x = x->base) chain[nchain++] = x;
    /* JsonFieldsOf: base class first, each class's own fields in their order */
    while (nchain--) {
        VmClass* r = chain[nchain];
        for (i = 0; i < r->nfields; i++) {
            const char* name = r->fields[i].name;
            const VmFieldInfo* fi = vmFieldInfoOf(c, name);
            const VmAttr* renamed;
            int32_t slot;
            bool dup = false;
            if (fi && (fi->flags & 2)) continue;
            for (k = 0; k < nseen; k++) if (seen[k] == name) dup = true;
            if (vmFieldAttr(fi, "NonSerialized")) { if (!dup) seen[nseen++] = name; continue; }
            if (dup) {   /* (declared again: the later declaration counts) */
                for (k = 0; k < n; k++) if (names[k] == name) break;
                if (k < n) { memmove(&rows[k], &rows[k + 1], (n - k - 1) * sizeof(JsonFieldInfo)); memmove(&names[k], &names[k + 1], (n - k - 1) * sizeof(char*)); n--; }
            } else seen[nseen++] = name;
            slot = vmFieldIndex(c, name);
            if (slot < 0) continue;
            renamed = vmFieldAttr(fi, "JsonName");
            memset(&rows[n], 0, sizeof rows[n]);
            rows[n].name = renamed && vmJsonAttrString(vm, renamed->json) ? vmJsonAttrString(vm, renamed->json) : name;
            rows[n].slot = (uint32_t)slot;
            rows[n].type = fi && fi->typeText ? fi->typeText : "";
            rows[n].unit = vmJsonUnit(vm, fi);
            rows[n].half = -1;
            names[n++] = name;
        }
    }
    njson = n;
    /* the `defined` fields that do not go to JSON (a read object holds their zeros too) */
    for (i = 0; i < c->nlayout; i++) {
        const char* name = c->layout[i];
        const VmFieldInfo* fi = vmFieldInfoOf(c, name);
        uint8_t def = vmJsonDefined(vm, fi);
        for (k = 0; k < njson; k++) if (names[k] == name) break;
        if (k < njson || !def) continue;
        memset(&rows[n], 0, sizeof rows[n]);
        rows[n].name = name;
        rows[n].slot = i;
        rows[n].type = (def & 0x40) ? vmJsonSingleName(vm, fi->requiredType) : "";
        if (!rows[n].type) rows[n].type = "";
        rows[n].unit = vmJsonUnit(vm, fi);
        rows[n].half = -1;
        names[n++] = name;
    }
    for (k = 0; k < n; k++) {
        const char* name = names[k];
        const VmFieldInfo* fi = vmFieldInfoOf(c, name);
        bool unitless = !fi || !fi->requiredUnit;
        rows[k].defined = vmJsonDefined(vm, fi);
        rows[k].arr = (uint16_t)(unitless && fi ? fi->requiredArray : 0);
        rows[k].arrClass = unitless && fi && fi->requiredArrayType ? vmArrClassOf(vm, fi->requiredArrayType) : 0;
    }
    memset(&rows[n], 0, sizeof rows[n]);
    rows[n].half = -1;
    for (x = c; x; x = x->base) if (x->flags & VC_NOTSER) flags |= JSON_FLAG_EXCLUDED;
    if (c->flags & VC_ACTOR) flags |= JSON_FLAG_ACTOR;
    {
        VmProto* hook = vmFindMethod(vm, c, vmName(vm->prog, "OnDeserialized"), 0, NULL, Undef(), NULL);
        if (hook && !hook->isStatic) flags |= JSON_FLAG_HOOK;
    }
    row->name = c->name;
    row->plain = c->name;
    if (strchr(c->name, '`')) { char buf[160]; row->plain = vmName(vm->prog, vmPlainName(c->name, buf, sizeof buf)); }
    row->nfields = c->nlayout;
    row->fields = n ? rows : NULL;
    row->njson = njson;
    row->ntparams = (uint8_t)vmJsonParamCount(c, rows, n);
    row->nraw = 0;
    row->flags = (uint8_t)flags;
}

static void vmJsonTables(Vm* vm) {
    VmProgram* p = vm->prog;
    uint32_t i;
    if (g_vmJson && g_vmJsonFirst == vm->firstId && g_vmJsonCount == vm->nids) return;
    free(g_vmJson);
    g_vmJsonCount = vm->nids;
    g_vmJsonFirst = vm->firstId;
    g_vmJson = (JsonClassInfo*)calloc(g_vmJsonCount + 1, sizeof(JsonClassInfo));
    if (!g_vmJson) outOfMemory();
    g_vmJsonSpecial.listCls = g_vmJsonSpecial.listItems = g_vmJsonSpecial.listCount = -1;
    g_vmJsonSpecial.objCls = g_vmJsonSpecial.objKeys = g_vmJsonSpecial.objValues = g_vmJsonSpecial.objCount = -1;
    g_vmJsonSpecial.listOwns = -1; g_vmJsonSpecial.ownsEnum = 0;
    g_vmJsonSpecial.listCountHalf = g_vmJsonSpecial.objCountHalf = -1;
    for (i = 0; i < g_vmJsonCount; i++) {
        VmClass* c = vm->byId[i];
        vmJsonRow(vm, c, &g_vmJson[i]);
        if (!strcmp(c->name, "List") && vmFieldIndex(c, vmName(p, "items")) >= 0 && vmFieldIndex(c, vmName(p, "count")) >= 0) {
            g_vmJson[i].flags |= JSON_FLAG_LIST;
            g_vmJsonSpecial.listCls = (int32_t)c->id;
            g_vmJsonSpecial.listItems = vmFieldIndex(c, vmName(p, "items"));
            g_vmJsonSpecial.listCount = vmFieldIndex(c, vmName(p, "count"));
            if (vmFieldIndex(c, vmName(p, "owns")) >= 0) {
                uint32_t e;
                for (e = 0; e < g_enumCount; e++) if (!strcmp(g_enums[e].name, "Owns")) { g_vmJsonSpecial.listOwns = vmFieldIndex(c, vmName(p, "owns")); g_vmJsonSpecial.ownsEnum = (int32_t)e + 1; }
            }
        }
        if (!strcmp(c->name, "JsonObject") && vmFieldIndex(c, vmName(p, "keys")) >= 0 && vmFieldIndex(c, vmName(p, "values")) >= 0 && vmFieldIndex(c, vmName(p, "count")) >= 0) {
            g_vmJson[i].flags |= JSON_FLAG_OBJECT;
            g_vmJsonSpecial.objCls = (int32_t)c->id;
            g_vmJsonSpecial.objKeys = vmFieldIndex(c, vmName(p, "keys"));
            g_vmJsonSpecial.objValues = vmFieldIndex(c, vmName(p, "values"));
            g_vmJsonSpecial.objCount = vmFieldIndex(c, vmName(p, "count"));
        }
    }
}

/* ---- what fire_json.h asks the program ---- */
const JsonClassInfo* jsonClassAt(uint32_t cls) { return cls >= g_vmJsonFirst && cls - g_vmJsonFirst < g_vmJsonCount ? &g_vmJson[cls - g_vmJsonFirst] : NULL; }
int32_t jsonClassNamed(const char* name) {
    Vm* vm = g_vmCur;
    VmClass* c = vm ? vmClassNamed(vm->prog, vmName(vm->prog, name)) : NULL;
    return c ? (int32_t)c->id : -1;
}
const char* jsonExcludedNamed(const char* name) { (void)name; return NULL; }   /* (every class is in the table, one not serializable with its flag) */
const char* jsonTArgText(const TArg* t) { return t ? t->display : NULL; }
const TArg* jsonTArgFor(const char* text) { Vm* vm = g_vmCur; return vm && text && *text ? vmTArgNamed(vm, vmName(vm->prog, text)) : NULL; }
const JsonSpecial* jsonSpecial(void) { return &g_vmJsonSpecial; }

static Value vmJsonCall(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) {
    const char* callType = fr->ctargs && fr->ctargs[0] ? jsonTArgText(fr->ctargs[0]) : NULL;
    vmJsonTables(vm);
    return fire_jsonCall((int)argc, a[0], a[1], argc > 2 ? a[2] : Undef(), callType, list);
}

static void vmJsonEnd(void) { free(g_vmJson); g_vmJson = NULL; g_vmJsonCount = 0; }
