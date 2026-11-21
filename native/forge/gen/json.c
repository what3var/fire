/* native/forge/gen/json.c - src/fire.Native/CppGenerator.Json.cs (commit 808e9788): JSON (`#import "json"`) in the native backend - UseJson,
 * RegisterJsonTargets, JsonReachType, JsonTypeArgTexts, JsonTypeParamCount, DefinedKindOf, DefinedTypeOf, JsonCountPlace, JsonRow, JsonCode; with
 * src/fire/Standard/JsonMethods.cs JsonFieldsOf (FieldsOf, ConstantString) and HasOnDeserialized. Not the plugins (PluginJson). */

typedef struct FgJsonField { const char* name; const char* jsonName; const char* type; const FcU* unit; } FgJsonField;

static void UseJson(FgGen* g) {
    static const char* const extra[] = { "DestroyedException", "ArgumentException" };
    size_t k;
    if (g->usesJson) return;
    g->usesJson = true;
    UseExceptions(g);
    if (!fcListHasStr(g->errorClasses, "JsonException")) fcListAdd(g->c, g->errorClasses, (void*)"JsonException");
    for (k = 0; k < 2; k++) if (!fcListHasStr(g->errorClasses, extra[k])) fcListAdd(g->c, g->errorClasses, (void*)extra[k]);
    g->version++;
}
static const char* JsonCallType(const FgFunc* f) { return f->hasCallTArgs ? "(ctargs && ctargs[0] ? jsonTArgText(ctargs[0]) : NULL)" : "NULL"; }

/* JsonMethods.ConstantString: the first argument of an attribute as a string constant (`[{"s":"x"}]`), NULL otherwise */
static const char* fgJsonConstantString(FcCtx* c, const char* argsJson) {
    FcJsonReader j;
    FcJson* v;
    FcJson* s;
    if (!argsJson) return NULL;
    j.c = c; j.p = argsJson; j.bad = false;
    v = fcJsonValue(&j);
    if (j.bad || v->kind != FJ_ARR || v->items->count == 0 || ((FcJson*)v->items->items[0])->kind != FJ_OBJ) return NULL;
    s = fcJsonGet((FcJson*)v->items->items[0], "s");
    return s && s->kind == FJ_STR ? s->s : NULL;
}
static FcFieldInfo* fgJsonFieldInfoOf(const FcRtClass* rc, const char* name) {
    for (; rc; rc = rc->base) { FcFieldInfo* f = (FcFieldInfo*)fcDictGet(rc->ownFieldInfo, name); if (f) return f; }
    return NULL;
}
static const FcU* fgSafeUnit(FcCtx* c, const char* demand) {
    const FcU* r = NULL;
    FC_TRY(c) { r = fcUParse(c, demand); }
    FC_CATCH(c) { r = NULL; }
    FC_END_TRY;
    return r;
}
static bool fgAttrNamed(const FcList* attrs, const char* name) { int i; for (i = 0; attrs && i < attrs->count; i++) if (streq0(((FcAttributeMeta*)attrs->items[i])->name, name)) return true; return false; }
/* JsonMethods.FieldsOf: the fields of a class that go to JSON, in declaration order (base class first) */
static FcList* JsonFieldsOf(FgGen* g, const FcRtClass* c) {
    FcList* result = (FcList*)fcDictGet(g->jsonFields, c);
    FcList* chain;
    FcDict* seen;
    const FcRtClass* r;
    int i, k, m;
    if (result) return result;
    result = fcListNew(g->c);
    seen = fcDictNew(g->c, FC_KEY_STR);
    chain = fcListNew(g->c);
    for (r = c; r; r = r->base) fcListInsert(g->c, chain, 0, (void*)r);
    for (i = 0; i < chain->count; i++) {
        r = (const FcRtClass*)chain->items[i];
        for (k = 0; k < r->fieldNames->count; k++) {
            const char* name = (const char*)r->fieldNames->items[k];
            FcFieldInfo* info = fgJsonFieldInfoOf(c, name);
            const char* jsonName = name;
            const FcU* unit = NULL;
            FgJsonField* jf;
            if (info && info->isStatic) continue;
            if (info && fgAttrNamed(info->attributes, "NonSerialized")) { fcSetAdd(seen, name); continue; }
            if (!fcSetAdd(seen, name)) {
                for (m = result->count - 1; m >= 0; m--) if (streq0(((FgJsonField*)result->items[m])->name, name)) fcListRemoveAt(result, m);
            }
            if (info && info->attributes)
                for (m = 0; m < info->attributes->count; m++) {
                    FcAttributeMeta* a = (FcAttributeMeta*)info->attributes->items[m];
                    if (streq0(a->name, "JsonName")) { const char* renamed = fgJsonConstantString(g->c, a->argsJson); if (renamed) jsonName = renamed; break; }
                }
            if (info && info->requiredUnit) {
                const char* demand = fgTrimStart(info->requiredUnit, '#');
                if (*demand && !fcStartsWith(demand, "any")) unit = fgSafeUnit(g->c, demand);
            }
            jf = (FgJsonField*)fcAlloc(g->c, sizeof *jf);
            jf->name = name; jf->jsonName = jsonName; jf->type = info && info->typeText ? info->typeText : "";
            jf->unit = unit && fcUIsUnitless(unit) ? NULL : unit;
            fcListAdd(g->c, result, jf);
        }
    }
    fcDictPut(g->jsonFields, c, result);
    return result;
}
static bool HasOnDeserialized(FgGen* g, const FcRtClass* rc) { FcProto* p = FG_FIND(rc, "OnDeserialized", 0).proto; return p && !p->isStatic; }

static bool fgJsonPlainType(const char* n) {
    static const char* const t[] = { "int", "float", "bool", "string", "char", "byte", "word", "long", "short", "double", "buffer", "undefined",
        "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64", "float32", "float64" };
    size_t i;
    for (i = 0; i < sizeof t / sizeof t[0]; i++) if (streq0(n, t[i])) return true;
    return false;
}
static bool fgJsonNameStart(char ch) { return (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || ch == '_' || ch == '$'; }
static bool fgJsonNameChar(char ch) { return fgJsonNameStart(ch) || (ch >= '0' && ch <= '9') || ch == '.' || ch == '`'; }
static bool JsonReachType(FgGen* g, const char* type, FcDict* seen) {
    const char* bare = fgTrimStart(type, '#');
    const char* p = bare;
    bool any = false;
    if (!*bare) return true;
    while (*p) {
        const char* s;
        const char* name;
        FcList* named;
        int i;
        if (!fgJsonNameStart(*p)) { p++; continue; }
        for (s = p; fgJsonNameChar(*p); p++) {}
        name = fcStrN(g->c, s, (size_t)(p - s));
        if (name[0] == '$' || streq0(name, "any") || streq0(name, "class")) { any = true; continue; }
        if (fgJsonPlainType(name)) continue;
        named = fcListNew(g->c);
        FC_DICT_EACH(g->program->program->classes, e) {
            const FcRtClass* rc = (const FcRtClass*)e->value;
            if ((streq0(rc->name, name) || streq0(fcPlainName(g->c, rc->name), name)) && !fcListHasStr(named, rc->name)) fcListAdd(g->c, named, (void*)rc->name);
        }
        if (!fcListHasStr(named, name)) fcListAdd(g->c, named, (void*)name);
        FC_DICT_EACH(g->program->program->classes, e) {
            const FcRtClass* rc = (const FcRtClass*)e->value;
            bool is = false;
            FcList* fields;
            for (i = 0; i < named->count && !is; i++) if (ClassIs(rc, (const char*)named->items[i])) is = true;
            if (!is || fgIsNotSerializable(rc) || !fcSetAdd(seen, rc)) continue;
            RegisterClass(g, rc->name);
            if (streq0(rc->name, "List") || streq0(rc->name, "JsonObject")) continue;
            fields = JsonFieldsOf(g, rc);
            for (i = 0; i < fields->count; i++) any |= JsonReachType(g, ((FgJsonField*)fields->items[i])->type, seen);
        }
    }
    return any;
}
static FcList* JsonTypeArgTexts(FgGen* g, const char* type) {
    FcList* r = fcListNew(g->c);
    const char* lt = strchr(type, '<');
    size_t n = strlen(type);
    FcList* parts;
    int i;
    if (!lt || n == 0 || type[n - 1] != '>') return r;
    parts = fgSplit(g->c, fcStrN(g->c, lt + 1, (size_t)(type + n - 1 - (lt + 1))), ',');
    for (i = 0; i < parts->count; i++) {
        const char* arg = fgTrim(g->c, (const char*)parts->items[i]);
        if (!*arg || strchr(arg, '<') || strchr(arg, '$') || strchr(arg, '[')) continue;
        fcListAdd(g->c, r, (void*)arg);
    }
    return r;
}
/* RegisterJsonTargets: what reading JSON can make (see the C#) */
/* A library (it has no RegisterJsonTargets - the program makes the objects of its classes): what its code reads as JSON - Json.Parse, Json.Deserialize with the
 * class as a text (a slot without a class) or as the type argument; the calls of the class Json itself do not count (what the program calls of it, it sees). */
static void CollectJsonReads(FgGen* g) {
    int i, k;
    for (i = 0; i < g->funcs->count; i++) {
        FgFunc* f = (FgFunc*)g->funcs->items[i];
        FgCode code = f->decoded;
        if (f->external || (f->chunk->ownerClass && streq0(f->chunk->ownerClass->name, "Json"))) continue;
        for (k = 0; k < code.n; k++) {
            const FgInstr* ins = &code.p[k];
            const char* method;
            const char* text;
            int set;
            if (ins->op != OP_CallStaticMethod || !streq0(fgConstStr(f->chunk, ins->a[0]), "Json")) continue;
            method = fgConstStr(f->chunk, ins->a[1]);
            if (!streq0(method, "Deserialize") && !streq0(method, "Parse")) continue;
            fcSetAdd(g->jsonReads, "$reads");
            if (streq0(method, "Parse")) continue;
            if (ins->a[2] >= 2) { fcSetAdd(g->jsonReads, "$any"); continue; }
            set = k - 1;
            if (set >= 0 && code.p[set].op == OP_CopyArgs) set--;
            text = set >= 0 && code.p[set].op == OP_SetTypeArgs ? fgConstStr(f->chunk, code.p[set].a[0]) : NULL;
            fcSetAdd(g->jsonReads, !text || strchr(text, '$') ? "$any" : text);
        }
    }
}

static void RegisterJsonTargets(FgGen* g) {
    FcRtClass* json;
    int i, k;
    if (!g->usesJson) return;
    if ((json = (FcRtClass*)fcDictGet(g->program->program->classes, "Json")) != NULL) {
        bool reads = false, anySlot = false;
        FcDict* declared = fcDictNew(g->c, FC_KEY_STR);
        FcDict* libraryReads;
        FC_DICT_EACH(json->methods, e) {
            const char* name = (const char*)e->key;
            FcList* protos = (FcList*)e->value;
            for (k = 0; k < protos->count; k++) {
                FcProto* proto = (FcProto*)protos->items[k];
                if (!fcDictHas(g->funcByProto, proto)) continue;
                if (streq0(name, "Parse")) reads = true;
                if (!streq0(name, "Deserialize")) continue;
                reads = true;
                if (proto->paramCount >= 2) anySlot = true;
            }
        }
        /* (modules: what the libraries read - `db.QueryList("Person", ...)` of data reads into a slot without a class; the program makes the objects of its
         * classes, as with one file) */
        libraryReads = fcDictNew(g->c, FC_KEY_STR);
        if (g->libraries) for (i = 0; i < g->libraries->count; i++) FC_DICT_EACH(((FgModuleOutput*)g->libraries->items[i])->needs->jsonReads, e) fcSetAdd(libraryReads, e->key);
        if (fcDictHas(libraryReads, "$reads")) reads = true;
        if (reads) {
            FcList* funcs = fcListCopy(g->c, g->funcs);
            fcListAdd(g->c, funcs, g->main);
            for (i = 0; i < funcs->count; i++) {
                FgFunc* f = (FgFunc*)funcs->items[i];
                FgCode code = f->decoded;   /* (as the C#: Func.Decoded - empty until the function is generated; one GetFunc made in this round is read in the next) */
                for (k = 0; k < code.n; k++) {
                    const FgInstr* ins = &code.p[k];
                    int set;
                    const char* text;
                    if (ins->op != OP_CallStaticMethod || ins->a[2] != 1 || !streq0(fgConstStr(f->chunk, ins->a[0]), "Json") || !streq0(fgConstStr(f->chunk, ins->a[1]), "Deserialize")) continue;
                    set = k - 1;
                    if (set >= 0 && code.p[set].op == OP_CopyArgs) set--;
                    text = set >= 0 && code.p[set].op == OP_SetTypeArgs ? fgConstStr(f->chunk, code.p[set].a[0]) : NULL;
                    if (!text || strchr(text, '$')) anySlot = true;
                    else fcSetAdd(declared, text);
                }
            }
            if (fcDictHas(libraryReads, "$any")) anySlot = true;
            FC_DICT_EACH(libraryReads, e) if (((const char*)e->key)[0] != '$') fcSetAdd(declared, e->key);
            if (fcDictHas(g->program->program->classes, "JsonObject")) RegisterClass(g, "JsonObject");
            {
                FcDict* seen = fcDictNew(g->c, FC_KEY_PTR);
                FcList* texts = fgSortedKeys(g->c, declared, true);
                for (i = 0; i < texts->count; i++) anySlot |= JsonReachType(g, (const char*)texts->items[i], seen);
            }
            if (anySlot) {
                FcList* all = fcListNew(g->c);
                FC_DICT_EACH(g->program->program->classes, e) fcListAdd(g->c, all, e->value);
                for (i = 0; i < all->count; i++) if (!fgIsNotSerializable((FcRtClass*)all->items[i])) RegisterClass(g, ((FcRtClass*)all->items[i])->name);
            }
        }
    }
    {
        FcList* classes = fcListCopy(g->c, g->classList);
        for (i = 0; i < classes->count; i++) {
            FcList* fields = JsonFieldsOf(g, ((FgClassInfo*)classes->items[i])->rc);
            for (k = 0; k < fields->count; k++) {
                FgJsonField* field = (FgJsonField*)fields->items[k];
                FcList* args;
                const FcEnumType* enumType;
                int a;
                UnitId(g, field->unit);
                args = JsonTypeArgTexts(g, field->type);
                for (a = 0; a < args->count; a++) TArgRef(g, (const char*)args->items[a]);
                if (ProgramEnumKnown(g, fgReplace(g->c, fgTrimStart(field->type, '#'), "[]", ""), &enumType)) EnumIndex(g, enumType);
            }
        }
    }
}
static int JsonTypeParamCount(FgGen* g, const FcRtClass* rc) {
    int count = 0, i;
    FcDict* texts = (FcDict*)fcDictGet(g->classTArgTexts, rc->name);
    FcList* fields;
    if (texts) FC_DICT_EACH(texts, e) { int n = fgSplit(g->c, (const char*)e->key, '|')->count; if (n > count) count = n; }
    fields = JsonFieldsOf(g, rc);
    for (i = 0; i < fields->count; i++) {
        const char* t = ((FgJsonField*)fields->items[i])->type;
        if (t[0] == '$') {
            int end = 1;
            while (t[end] >= '0' && t[end] <= '9') end++;
            if (end > 1) { int v = atoi(fcStrN(g->c, t + 1, (size_t)(end - 1))) + 1; if (v > count) count = v; }
        }
    }
    return count < 255 ? count : 255;
}
static const char* DefinedKindOf(FgGen* g, int declared, bool derived) {
    return declared != 0 && fgDkIsNonNull(declared) ? fcFmt(g->c, "%s%s", KindConstant(declared), derived ? " | 0x80" : "") : "0";
}
static const char* DefinedTypeOf(FgGen* g, const FcRtClass* rc, const char* field) {
    const char* text = fgFieldRequiredType(rc, field);
    const char* name;
    const FcEnumType* enumType;
    if (!text || !fcDtIsNonNull(text)) return NULL;
    name = fgSpecSingleName(FG_SPEC(text));
    if (name && streq0(name, "string")) return "0x41";
    if (name && ProgramEnumKnown(g, name, &enumType)) { EnumIndex(g, enumType); return "0x42"; }
    return "0x43";
}
static void JsonCountPlace(FgGen* g, const FcRtClass* rc, int index, int* place, int* half) {
    FgRawLayout* l = RawLayoutOf(g, rc);
    if (index < l->count && l->off[index] >= 0) {
        if (KindOfType(RawTypeAt(g, rc, index)) != KindInt) FG_NOTSUP("the count of '%s' is a packed field that is no int", rc->name);
        *place = l->off[index]; *half = -2;
        return;
    }
    FieldPlace(g, rc, index, place, half);
}
static const char* fgRawPlaceOf(FgGen* g, const FcRtClass* rc, int index) {
    FgRawLayout* l = RawLayoutOf(g, rc);
    int type;
    if (index >= l->count || l->off[index] < 0) return "0, 0";
    type = RawTypeAt(g, rc, index);
    return fcFmt(g->c, "%s, %d", KindOfType(type) == KindFloat ? "FIRE_RAW_REAL" : KindOfType(type) == KindBool ? "FIRE_RAW_BOOL" : KindOfType(type) == KindWord ? "FIRE_RAW_WORD" : "FIRE_RAW_INT", l->off[index]);
}
static const char* JsonRow(FgGen* g, FgClassInfo* ci, const char* fieldTable, int flags, FcBuf* sb, FcList* unitFixups) {
    const FcRtClass* rc = ci->rc;
    FcDict* index = FG_INDEX(rc);
    FcList* all = JsonFieldsOf(g, rc);
    FcList* fields = fcListNew(g->c);
    FcList* rows;
    int i, k;
    for (i = 0; i < all->count; i++) if (fcDictHas(index, ((FgJsonField*)all->items[i])->name)) fcListAdd(g->c, fields, all->items[i]);
    rows = fcListCopy(g->c, fields);
    FC_DICT_EACH(index, e) {
        const char* n = (const char*)e->key;
        bool listed = false;
        int kd;
        for (k = 0; k < fields->count; k++) if (streq0(((FgJsonField*)fields->items[k])->name, n)) listed = true;
        if (listed) continue;
        kd = fgFieldRequiredKind(rc, n);
        if ((kd != 0 && fgDkIsNonNull(kd)) || DefinedTypeOf(g, rc, n)) {
            FgJsonField* jf = (FgJsonField*)fcAlloc(g->c, sizeof *jf);
            const char* demand = fgFieldRequiredUnit(rc, n) ? fgTrimStart(fgFieldRequiredUnit(rc, n), '#') : NULL;
            jf->name = n; jf->jsonName = n;
            if (DefinedTypeOf(g, rc, n)) { const char* single = fgSpecSingleName(FG_SPEC(fgFieldRequiredType(rc, n))); jf->type = single ? single : ""; }
            else jf->type = "";
            jf->unit = demand && *demand && !fcStartsWith(demand, "any") ? fcUParse(g->c, demand) : NULL;
            fcListAdd(g->c, rows, jf);
        }
    }
    if (rows->count > 0) {
        FcDict* derivedNames = DerivedFieldNames(g);
        fcBufFmt(sb, "static %sJsonFieldInfo %s[] = {", unitFixups ? "" : "const ", fieldTable);
        for (i = 0; i < rows->count; i++) {
            FgJsonField* f = (FgJsonField*)rows->items[i];
            int fi = FC_VALI(fcDictGet(index, f->name)) - 1, slot, half, slot2, unit, arrClass = 0;
            const char* jn = CString(g, f->jsonName);
            const char* tn;
            const char* defined;
            const char* raw;
            int reqArray;
            FieldPlace(g, rc, fi, &slot, &half);
            tn = CString(g, f->type);
            unit = unitFixups ? 0 : UnitId(g, f->unit);
            reqArray = !fgFieldRequiredUnit(rc, f->name) ? fgFieldRequiredArray(rc, f->name) : 0;
            defined = DefinedTypeOf(g, rc, f->name);
            if (!defined) defined = DefinedKindOf(g, fgFieldRequiredKind(rc, f->name), fcDictHas(derivedNames, f->name) && !streq0(fgFieldWhat(g->c, rc, f->name), fcFmt(g->c, "Field '%s'", f->name)));
            raw = fgRawPlaceOf(g, rc, fi);
            FieldPlace(g, rc, fi, &slot2, &half);
            if (!fgFieldRequiredUnit(rc, f->name) && fgFieldRequiredArrayType(rc, f->name)) arrClass = ArrClassId(g, fgFieldRequiredArrayType(rc, f->name));
            fcBufFmt(sb, "%s{%s, %d, %s, %d, %d, %s, %s, %d, %d%s}", i ? ", " : "", jn, slot > 0 ? slot : 0, tn, unit, reqArray, defined, raw, half, arrClass, ArrClassMap(g, arrClass));
        }
        fcBufStr(sb, ", {NULL, 0, NULL, 0, 0, 0, 0, 0, -1, 0, NULL}};\n");
        if (unitFixups)
            for (i = 0; i < rows->count; i++) {
                int u = UnitId(g, ((FgJsonField*)rows->items[i])->unit);
                if (u != 0) fcListAdd(g->c, unitFixups, (void*)fcFmt(g->c, "%s[%d].unit = %s;", fieldTable, i, UnitC(g, u)));
            }
    }
    else fieldTable = "NULL";
    if (rc->isActor) flags |= 1;
    if (fgIsNotSerializable(rc)) flags |= 16;
    if (HasOnDeserialized(g, rc)) flags |= 2;
    {
        const char* a = CString(g, rc->name);
        const char* b = CString(g, fcPlainName(g->c, rc->name));
        int slots = SlotCount(g, ci);
        int tparams = JsonTypeParamCount(g, rc);
        return fcFmt(g->c, "{%s, %s, %d, %s, %d, %d, %d, %d}", a, b, slots, fieldTable, fields->count, flags, tparams, RawBytesOf(g, rc));
    }
}
static const char* JsonCode(FgGen* g) {
    FcBuf sb = {0};
    FcList* rows = fcListNew(g->c);
    int listCls = -1, listItems = -1, listCount = -1, objCls = -1, objKeys = -1, objValues = -1, objCount = -1, listOwns = -1, ownsEnum = 0, listCountHalf = -1, objCountHalf = -1;
    int jsonIds = 0, jsonId, i, half;
    FcList* targs;
    FcList* excluded;
    /* (with library modules in one file it is included before their code; linkage "static": the engine is in the file of the module that calls it) */
    {
        bool inLibrary = false;
        if (g->separate && g->libraries) for (i = 0; i < g->libraries->count; i++) if (((FgModuleOutput*)g->libraries->items[i])->jsonEngine) inLibrary = true;
        if (!g->moduleBodies && !inLibrary) {
            fcBufStr(&sb, "#define FIRE_JSON_IMPLEMENTATION 1\n");
            fcBufStr(&sb, "#include \"fire_json.h\"\n");
        }
        else if (!g->moduleBodies) fcBufStr(&sb, "#include \"fire_json.h\"   // (the engine: in the library of the module that calls it)\n");
        else fcBufStr(&sb, "");
    }
    for (i = 0; i < g->classList->count; i++) if (((FgClassInfo*)g->classList->items[i])->id + 1 > jsonIds) jsonIds = ((FgClassInfo*)g->classList->items[i])->id + 1;
    for (jsonId = 0; jsonId < jsonIds; jsonId++) {
        FgClassInfo* ci = NULL;
        const FcRtClass* rc;
        FcDict* index;
        int flags = 0;
        void* v1;
        void* v2;
        void* v3;
        for (i = 0; i < g->classList->count; i++) if (((FgClassInfo*)g->classList->items[i])->id == jsonId) ci = (FgClassInfo*)g->classList->items[i];
        if (!ci) { fcListAdd(g->c, rows, (void*)"{\"\", \"\", 0, NULL, 0, 0, 0, 0}"); continue; }
        rc = ci->rc;
        index = FG_INDEX(rc);
        if (streq0(rc->name, "List") && fcDictTry(index, "items", &v1) && fcDictTry(index, "count", &v2)) {
            const FcEnumType* ownsType;
            flags |= 4;
            listCls = ci->id;
            FieldPlace(g, rc, FC_VALI(v1) - 1, &listItems, &half);
            JsonCountPlace(g, rc, FC_VALI(v2) - 1, &listCount, &listCountHalf);
            if (fcDictTry(index, "owns", &v3)) {
                int owns = FC_VALI(v3) - 1;
                FgRawLayout* l = RawLayoutOf(g, rc);
                if (!(owns < l->count && l->off[owns] >= 0) && ProgramEnumKnown(g, "Owns", &ownsType)) { FieldPlace(g, rc, owns, &listOwns, &half); ownsEnum = EnumIndex(g, ownsType); }
            }
        }
        if (streq0(rc->name, "JsonObject") && fcDictTry(index, "keys", &v1) && fcDictTry(index, "values", &v2) && fcDictTry(index, "count", &v3)) {
            flags |= 8;
            objCls = ci->id;
            FieldPlace(g, rc, FC_VALI(v1) - 1, &objKeys, &half);
            FieldPlace(g, rc, FC_VALI(v2) - 1, &objValues, &half);
            JsonCountPlace(g, rc, FC_VALI(v3) - 1, &objCount, &objCountHalf);
        }
        fcListAdd(g->c, rows, (void*)JsonRow(g, ci, fcFmt(g->c, "kJsonF%d", ci->id), flags, &sb, NULL));
    }
    fcBufFmt(&sb, "static const JsonClassInfo kJsonClasses[] = {%s};\n", rows->count == 0 ? "{\"\", \"\", 0, NULL, 0, 0, 0, 0}" : fgJoin(g->c, rows, ",\n    "));
    fcBufFmt(&sb, "static const uint32_t kJsonClassCount = %d;\n", rows->count);
    fcBufFmt(&sb, "static const JsonSpecial kJsonSpecial = {%d, %d, %d, %d, %d, %d, %d, %d, %d, %d, %d};\n", listCls, listItems, listCount, objCls, objKeys, objValues, objCount, listOwns, ownsEnum, listCountHalf, objCountHalf);
    /* _targIds.OrderBy(kv => kv.Value) */
    targs = fcListNew(g->c);
    {
        int n = fcDictCount(g->targIds), id;
        for (id = 0; id < n; id++) FC_DICT_EACH(g->targIds, e) if (FC_VALI(e->value) - 1 == id) fcListAdd(g->c, targs, e);
    }
    fcBufStr(&sb, "typedef struct JsonTArgText { const char* text; const TArg* targ; } JsonTArgText;\n");
    {
        FcList* items = fcListNew(g->c);
        for (i = 0; i < targs->count; i++) { FcDictEntry* e = (FcDictEntry*)targs->items[i]; fcListAdd(g->c, items, (void*)fcFmt(g->c, "{%s, &ta_%d}", CString(g, (const char*)e->key), FC_VALI(e->value) - 1)); }
        fcBufFmt(&sb, "static const JsonTArgText kJsonTArgs[] = {%s};\n", targs->count == 0 ? "{NULL, NULL}" : fgJoin(g->c, items, ", "));
    }
    fcBufFmt(&sb, "static const uint32_t kJsonTArgCount = %d;\n", targs->count);
    fcBufFmt(&sb, "const JsonClassInfo* jsonClassAt(uint32_t cls) { return cls < kJsonClassCount ? &kJsonClasses[cls] : %s; }\n", g->openWorld ? "(const JsonClassInfo*)firePluginJsonAt(cls)" : "NULL");
    fcBufStr(&sb, "int32_t jsonClassNamed(const char* name) {\n");
    fcBufStr(&sb, "    for (uint32_t i = 0; i < kJsonClassCount; i++) if (strcmp(kJsonClasses[i].name, name) == 0) return (int32_t)i;\n");
    fcBufFmt(&sb, "    return %s;\n", g->openWorld ? "firePluginJsonNamed(name)" : "-1");
    fcBufStr(&sb, "}\n");
    {
        FcDict* ex = fcDictNew(g->c, FC_KEY_STR);
        FC_DICT_EACH(g->program->program->classes, e) { const FcRtClass* rc = (const FcRtClass*)e->value; if (fgIsNotSerializable(rc) && !fcDictHas(g->classes, rc->name)) fcSetAdd(ex, rc->name); }
        excluded = fgSortedKeys(g->c, ex, true);
    }
    fcBufStr(&sb, "const char* jsonExcludedNamed(const char* name) {\n");
    if (excluded->count > 0) {
        FcList* lits = fcListNew(g->c);
        for (i = 0; i < excluded->count; i++) fcListAdd(g->c, lits, (void*)CString(g, (const char*)excluded->items[i]));
        fcBufFmt(&sb, "    static const char* const names[] = {%s};\n", fgJoin(g->c, lits, ", "));
        fcBufFmt(&sb, "    for (uint32_t i = 0; i < %du; i++) if (strcmp(names[i], name) == 0) return names[i];\n", excluded->count);
    }
    else fcBufStr(&sb, "    (void)name;\n");
    fcBufStr(&sb, "    return NULL;\n");
    fcBufStr(&sb, "}\n");
    fcBufStr(&sb, "const char* jsonTArgText(const TArg* t) {\n");
    fcBufStr(&sb, "    for (uint32_t i = 0; i < kJsonTArgCount; i++) if (kJsonTArgs[i].targ == t) return kJsonTArgs[i].text;\n");
    fcBufFmt(&sb, "    return %s;\n", g->openWorld ? "firePluginTArgText(t)" : "NULL");
    fcBufStr(&sb, "}\n");
    fcBufStr(&sb, "const TArg* jsonTArgFor(const char* text) {\n");
    fcBufStr(&sb, "    for (uint32_t i = 0; i < kJsonTArgCount; i++) if (kJsonTArgs[i].text && strcmp(text, kJsonTArgs[i].text) == 0) return kJsonTArgs[i].targ;\n");
    fcBufFmt(&sb, "    return %s;\n", g->openWorld ? "firePluginTArgFor(text)" : "NULL");
    fcBufStr(&sb, "}\n");
    fcBufStr(&sb, "const JsonSpecial* jsonSpecial(void) { return &kJsonSpecial; }\n");
    return fcBufDone(g->c, &sb);
}
