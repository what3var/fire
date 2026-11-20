/* native/forge/gen/packed.c - src/fire.Native/CppGenerator.Packed.cs (commit 808e9788): packed objects - a field that is `defined` in every class that
 * has it, with a static kind and unit, is a C number behind the slots of the object (objRaw in fire_rt.h). */

static const FcFieldInfo* DeclaringInfo(const FcRtClass* rc, const char* name) {
    const FcFieldInfo* info = NULL;
    for (; rc && !info; rc = rc->base) info = fgOwnField(rc, name);
    return info;
}
/* The static type a field declared like this is packed with; 0 for any other declaration. */
static int DeclaredRawType(FgGen* g, const FcFieldInfo* info) {
    int type, kind;
    if (!info || info->isStatic || info->requiredKind == 0 || !fgDkIsNonNull(info->requiredKind) || info->requiredArray != 0) return 0;
    type = DeclaredValueType(g, info->requiredKind, info->requiredUnit);
    kind = KindOfType(type);
    return IsNarrowIntType(type) || kind == KindFloat || kind == KindBool || kind == KindWord ? type : 0;
}
/* The static type of a packed field `name`, 0 when it is a Value slot: every class that has the field declares it alike. */
static int RawType(FgGen* g, const char* name) {
    void* known;
    int result = INT32_MIN;
    if (fcDictTry(g->rawType, name, &known)) return FC_VALI(known);
    if (HasProperty(g, name)) { fcDictPut(g->rawType, fcStr(g->c, name), FC_IVAL(0)); return 0; }
    FC_DICT_EACH(g->program->program->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        int type;
        if (!fgHasField(g, rc, name)) continue;
        type = DeclaredRawType(g, DeclaringInfo(rc, name));
        if (type == 0 || (result != INT32_MIN && result != type)) { result = 0; break; }
        result = type;
    }
    if (result == INT32_MIN) result = 0;
    fcDictPut(g->rawType, fcStr(g->c, name), FC_IVAL(result));
    return result;
}
/* Is the field `name` declared `defined` in every class that has it? */
static bool DefinedEverywhere(FgGen* g, const char* name) {
    bool any = false;
    FC_DICT_EACH(g->program->program->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        const FcFieldInfo* info;
        if (!fgHasField(g, rc, name)) continue;
        info = DeclaringInfo(rc, name);
        if (!info || info->isStatic || info->requiredKind == 0 || !fgDkIsNonNull(info->requiredKind)) return false;
        any = true;
    }
    return any;
}
/* Is the field `index` of `rc` packed, and its static type (0: a Value slot)? */
static int RawTypeAt(FgGen* g, const FcRtClass* rc, int index) {
    FcList* names = FG_NAMES(rc);
    if (index < 0 || index >= names->count) return 0;
    return RawType(g, (const char*)names->items[index]);
}
static const char* RawCType(int type) { int k = KindOfType(type); return k == KindFloat ? "Real" : k == KindWord ? "FireWord" : k == KindBool ? "uint8_t" : "FireInt"; }
static int RawSize(FgGen* g, int type) {
    int k = KindOfType(type);
    return k == KindFloat ? g->program->floatWidth / 8 : k == KindWord ? g->program->wordWidth / 8 : k == KindBool ? 1 : g->program->intWidth / 8;
}
/* The byte offset of each packed field of `rc` (by its index; -1: a Value slot) and the bytes of all of them. */
typedef struct FgRawLayoutX { FgRawLayout l; int bytes; } FgRawLayoutX;
static FgRawLayout* RawLayoutOf(FgGen* g, const FcRtClass* rc) {
    FgRawLayoutX* known = (FgRawLayoutX*)fcDictGet(g->rawLayouts, rc);
    FcList* names;
    int at = 0, i;
    if (known) return &known->l;
    names = FG_NAMES(rc);
    known = (FgRawLayoutX*)fcAlloc(g->c, sizeof *known);
    known->l.off = (int*)fcAlloc(g->c, sizeof(int) * (size_t)(names->count + 1));
    known->l.count = names->count;
    for (i = 0; i < names->count; i++) {
        int type = RawTypeAt(g, rc, i), size;
        if (type == 0) { known->l.off[i] = -1; continue; }
        size = RawSize(g, type);
        at = (at + size - 1) / size * size;
        known->l.off[i] = at;
        at += size;
    }
    known->bytes = (at + 7) / 8 * 8;
    fcDictPut(g->rawLayouts, rc, known);
    return &known->l;
}
static int RawBytesOf(FgGen* g, const FcRtClass* rc) { RawLayoutOf(g, rc); return ((FgRawLayoutX*)fcDictGet(g->rawLayouts, rc))->bytes; }
/* The C lvalue of the packed field `index` of the object `obj` (an Obj*), NULL when it is a Value slot. */
static const char* RawRef(FgGen* g, const FcRtClass* rc, int index, const char* obj) {
    const FcRtClass* cls = LayoutClass(g, rc, index);
    FgRawLayout* l = RawLayoutOf(g, cls);
    const char* type;
    FcList* inst;
    int slots = -1, i;
    bool one = true;
    if (index >= l->count || l->off[index] < 0) return NULL;
    type = RawCType(RawTypeAt(g, cls, index));
    inst = InstancesOf(g, rc);
    for (i = 0; i < inst->count; i++) {
        FgClassInfo* c = (FgClassInfo*)inst->items[i];
        int n;
        if (!(index < FG_NAMES(c->rc)->count)) continue;
        n = SlotCount(g, c);
        if (slots < 0) slots = n;
        else if (n != slots) one = false;
    }
    if (slots >= 0 && one && !Modular) return fcFmt(g->c, "(*(%s*)((char*)(%s) + FIRE_HDR(Obj) + %d * sizeof(Value) + %d))", type, obj, slots, l->off[index]);
    return fcFmt(g->c, "(*(%s*)(objRaw(%s) + %d))", type, obj, l->off[index]);
}
/* The C number of a checked Value `x` for a packed field of this static type. */
static const char* RawFromValue(FgGen* g, int type, const char* x) {
    int k = KindOfType(type);
    return k == KindFloat ? fcFmt(g->c, "(Real)%s.f", x) : k == KindWord ? fcFmt(g->c, "(FireWord)%s.w", x) : k == KindBool ? fcFmt(g->c, "(uint8_t)(%s.i != 0)", x) : fcFmt(g->c, "(FireInt)%s.i", x);
}
/* `&obj.name` of a field declared `defined`: a K_PtrField with its descriptor; NULL for any other field. */
static const char* FieldPtrExpr(FgGen* g, const FcRtClass* rc, int index, const char* addrOfSlot, const char* obj) {
    const char* name = (const char*)FG_NAMES(rc)->items[index];
    const FcFieldInfo* info = DeclaringInfo(rc, name);
    const char* raw;
    const char* demand;
    const char* d;
    const char* addr;
    const char* text;
    int rawType, unit;
    if (!info || info->isStatic || info->requiredKind == 0 || !fgDkIsNonNull(info->requiredKind)) return NULL;
    rawType = RawTypeAt(g, rc, index);
    raw = rawType == 0 ? "0" : KindOfType(rawType) == KindFloat ? "FIRE_RAW_REAL" : KindOfType(rawType) == KindBool ? "FIRE_RAW_BOOL" : KindOfType(rawType) == KindWord ? "FIRE_RAW_WORD" : "FIRE_RAW_INT";
    demand = info->requiredUnit;
    unit = demand ? DemandedUnitId(g, demand) : 0;
    addr = rawType != 0 ? fcFmt(g->c, "(void*)&%s", RawRef(g, rc, index, obj)) : addrOfSlot;
    d = demand ? fgTrimStart(demand, '#') : NULL;
    text = Constant(g, fcMakeString(g->c, UnitDemandDisplay(g, d && !fcStartsWith(d, "any") ? d : "unitless")));
    return fcFmt(g->c, "{ static FireFieldPtr d_ = {%s, %s, %s, %d, %d, 0, {0}, 0}; d_.unit = %s; d_.text = %s; return fieldPtrV(%s, &d_); }",
        CString(g, name), raw, KindConstant(info->requiredKind), fgDkIsUnitless(info->requiredKind) ? 1 : 0, unit > 0 ? 1 : 0, unit > 0 ? UnitC(g, unit) : "0", text, addr);
}
/* `newObject` of the class - with the bytes of its packed fields. */
static const char* NewObjectCall(FgGen* g, const FgClassInfo* cls, const char* owner) {
    int raw = RawBytesOf(g, cls->rc);
    return raw > 0 ? fcFmt(g->c, "newObjectR(%s, %d, %d, %s)", ClassIdExpr(g, cls), SlotCount(g, cls), raw, owner) : fcFmt(g->c, "newObject(%s, %d, %s)", ClassIdExpr(g, cls), SlotCount(g, cls), owner);
}
