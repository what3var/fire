/* native/forge/gen/cppgenerator.c - src/fire.Native/CppGenerator.cs (commit 808e9788), part 1: the registries, the specialisations of generic
 * methods, classes, fields and their layout, the static types, the guards of declarations, the type arguments, `inline c`, access, the natives of
 * the packages, the units of a demand (the C# file's lines 146-1970, in its order). The roles of the modules (Modular, LibraryRole, Px) as in C#:
 * gen/modules.c. */

#define FG_SPEC(text) fgParseSpec(g->c, g->specCache, g->condCache, (text))
#define FG_NAMES(rc) fgFlattenedFieldNames(g->c, g->rtNames, (rc))
#define FG_INDEX(rc) fgFieldIndex(g->c, g->rtNames, g->rtIndex, (rc))
#define FG_FIND(rc, name, argc) fgFindMethodWithAccess(g->c, g->rtCands, (rc), (name), (argc))
#define FG_OVERLOADED(rc, name, argc) fgIsTypeOverloaded(g->c, g->rtCands, (rc), (name), (argc))
#define FG_NOTSUP(...) fcThrow(g->c, FC_ERR_NATIVE, 0, 0, NULL, fcFmt(g->c, __VA_ARGS__))

/* the kinds of the static types (see "Static types of values" below) */
enum { KindUnknown = 0, KindInt = 1, KindFloat = 2, KindBool = 3, KindObj = 4, KindWord = 5, KindBuf = 6, KindArr = 7 };
enum { BufBytes = 1, BufWords = 2 };
enum { TypeUnknown = 0, NullableBit = 0x10, TypeUndefined = 0x10, TypeNone = 0x20, NarrowBit = 0x40, CutBit = 0x80 };
enum { FunctionScope = -1, GlobalScope = -2 };
#define TempBase (1 << 24)

/* the role of the generator (CppGenerator.Modules.cs): Modular - the program is made of modules (linkage "static", FIRE_MODULES=1); LibraryRole - this
 * generator makes a library module; Px - the prefix of the names a library module keeps to itself (`core_`) */
#define Modular (g->role != 0)
#define LibraryRole (g->role == 1)
#define Px (g->self ? g->self->idPx : "")
#define HelperStorage "static "
#define HelperInline "static inline "
static bool fgDebugBuild(FgGen* g) { return g->program->mode == 0; }
#define DebugBuild fgDebugBuild(g)
#define Wrapped DebugBuild
/* (stack traces: CppGenerator.Trace.cs, gen/trace.c) */
#define TraceOn (g->program->stackTrace != STM_Off)
#define TraceLines (g->program->stackTrace == STM_Lines)
static bool fgCheckAccess(FgGen* g) { return g->program->mode != 2; }
#define CheckAccess fgCheckAccess(g)
#define ScalarsOn (!DebugBuild && (g->program->intWidth == 32 || g->program->intWidth == 64))
#define NarrowFieldsOn (!g->usesThreads && !DebugBuild && !Modular && !g->openWorld && !g->separate && !g->dll)
#define WrapDispatchers (g->usesActors || g->usesThreads)

/* forward declarations of what the other parts define */
/* (the modules: gen/modules.c, gen/moddispatch.c, gen/staticlinkage.c, gen/plugins.c) */
static FgModuleSpec* ModuleOf(FgGen* g, const FcRtClass* rc);
static bool IsOwnClass(FgGen* g, const FcRtClass* rc);
static bool ProtoKey(FgGen* g, const FcProto* proto, FgModuleSpec** module, const char** key);
static const char* ExportName(FgGen* g, const FgModuleSpec* module, const char* key);
static const char* ModuleBase(FgGen* g, const FgModuleSpec* m);
typedef struct FgClassInfo FgClassInfoT;
static FgClassInfo* ForeignClass(FgGen* g, const char* name, FcRtClass* rc);
static const char* DbgSrcOf(FgGen* g, int src);
static void NoteMadeBy(FgGen* g, const char* cls);
static const char* DemandOwnerOf(const FcChunk* chunk);
static uint32_t CurrentFlags(FgGen* g);
static void ApplyForcedFlags(FgGen* g);
static void AddLibraryNeeds(FgGen* g);
static void ReachLibraryClasses(FgGen* g);
static void KeepOwnModule(FgGen* g);
static bool KeepsPrivate(FgGen* g, const FcRtClass* rc, const char* name);
static const char* ModuleTables(FgGen* g);
static const char* ShareReflection(FgGen* g, const char* code);
static const char* LibraryBody(FgGen* g);
static const char* UnsupportedStub(FgGen* g, FgFunc* f, const char* why);
static const char* ModId(FgGen* g);
static const char* ModIdOf(FgGen* g, const FcRtClass* rc);
static bool OwnsProto(FgGen* g, const FcProto* p);
static void RegisterServedTargets(FgGen* g);
static const char* ModTArgOf(FgGen* g, const char* cls, int k);
static const char* ModIsOf(FgGen* g, const char* typeName);
static const char* ModClassScore(FgGen* g, const char* name);
static const char* ModularHelpers(FgGen* g);
static const char* ModularDescColumns(FgGen* g, const FcRtClass* rc, FcBuf* decls, int id);
static void fgBufInsert0(FcBuf* b, const char* text);
static FcList* fgClassesByName(FgGen* g);
static const char* LibraryHeadOf(FgGen* g, const char* prelude);
#define NativesOwner(g, import) (NativesOwnerSpec((g), (import)) != NULL)
static FgModuleSpec* NativesOwnerSpec(FgGen* g, const char* import);
static bool NativeIsHere(FgGen* g, const char* import);
static const char* SharedNative(FgGen* g, const char* function, int argc, bool needsList, const char* import);
static const char* SharedNativeThunks(FgGen* g);
static const char* SharedLibraryChecks(FgGen* g);
static const char* SharedLibraryCheckCalls(FgGen* g);
static const char* HostTable(FgGen* g, const char* programC);
static const char* PluginHostCode(FgGen* g);
static const char* InsertAfterRuntime(FgGen* g, const char* text, const char* insert);
static const char* RuntimeDllKey(FgGen* g, const char* head, const char* toolchainStamp);
static const char* DllHead(FgGen* g);
#define FG_SEPARATE_DIMS 16
static void UseExceptions(FgGen* g);
static void UseThreads(FgGen* g);
static int UnitId(FgGen* g, const FcU* unit);
static const char* Constant(FgGen* g, const FcConst* v);
static const char* CString(FgGen* g, const char* s);
static FgFunc* GetFunc(FgGen* g, FcProto* proto, FgFuncKind kind, int captureCount);
static bool HasProperty(FgGen* g, const char* name);
static const char* FieldKindCheck(FgGen* g, const char* name, int kind);
static const char* FieldArrayCheck(FgGen* g, const FcRtClass* rc, const char* name, const char* obj);
static const char* ScalarValueOf(FgGen* g, int kind, int unit, const char* c);
static int RawTypeAt(FgGen* g, const FcRtClass* rc, int index);
static const char* RawRef(FgGen* g, const FcRtClass* rc, int index, const char* obj);
static const char* RawFromValue(FgGen* g, int rawType, const char* v);
typedef struct FgRawLayout { int* off; int count; } FgRawLayout;
static FgRawLayout* RawLayoutOf(FgGen* g, const FcRtClass* rc);
static bool DefinedEverywhere(FgGen* g, const char* name);
static FcList* MethodsFor(FgGen* g, const FcRtClass* rc, const char* name, int argc);
static bool UseOperatorIn(FgGen* g, const char* method);
static const char* ArrClassIdText(FgGen* g, const char* text);
static int ArrClassId(FgGen* g, const char* text);
static const char* IsANames(FgGen* g, const char* cls, const char* name, FcBuf* sb);
static const char* LineFileLiteral(FgGen* g, int sourceIndex);
/* CppGenerator.Modules.cs: the C of a unit id and of an enum number in code (a library: through the tables of the program) */
static const char* UnitC(FgGen* g, int unit) { return LibraryRole && unit != 0 ? fcFmt(g->c, "fire_units_%s[%d]", g->self->id, unit) : fcFmt(g->c, "%d", unit); }
static const char* EnumC(FgGen* g, int index) { return LibraryRole && index != 0 ? fcFmt(g->c, "fire_enums_%s[%d]", g->self->id, index) : fcFmt(g->c, "%d", index); }
/* the entry of an array of a class in g_arrClasses in code (a library: from the program's table fire_arrc_M); in a static table (TArg, JsonFieldInfo) the
 * module's number stays and `arrMap` names the table (NULL: none) */
static const char* ArrClassC(FgGen* g, int id) { return LibraryRole && id != 0 ? fcFmt(g->c, "fire_arrc_%s[%d]", g->self->id, id) : fcFmt(g->c, "%d", id); }
static const char* ArrClassMap(FgGen* g, int id) { return LibraryRole && id != 0 ? fcFmt(g->c, ", fire_arrc_%s", g->self->id) : ", NULL"; }
static const char* ClassIdExpr(FgGen* g, const FgClassInfo* cls) { return cls->idText ? cls->idText : fcFmt(g->c, "%d", cls->id); }
static int EnumIndex(FgGen* g, const FcEnumType* type);
static const FcU* DemandUnit(FgGen* g, const char* demand);
static const char* AdaptUnitCall(FgGen* g, const char* value, const char* demand);
static const char* UnitDemandDisplay(FgGen* g, const char* demand);
static bool ProgramEnumKnown(FgGen* g, const char* name, const FcEnumType** type);
static FgCAbi* CAbiOf(FgGen* g, FgFunc* f);
static void NoteCReturn(FgGen* g, FgFunc* f);
static const char* CName(FgGen* g, const FgFunc* f);
static const char* CSignature(FgGen* g, FgFunc* f, FgCAbi* abi);
static const char* UndefReturnOf(const FgCAbi* abi);
static const char* CAbiWrapper(FgGen* g, FgFunc* f, FgCAbi* abi);

static const char* Mangle(FgGen* g, const char* name) {
    FcBuf b = {0};
    const unsigned char* p = (const unsigned char*)name;
    fcBufStr(&b, "");
    while (*p) {
        uint16_t ch = fc8Next(&p);
        if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_') fcBufChar(&b, (char)ch);
        else fcBufFmt(&b, "_x%04X", ch);
    }
    return fcBufDone(g->c, &b);
}
/* CppGenerator.ModuleDispatch.cs: the names of the helpers (with the prefix of a module) */
static const char* CallFn(FgGen* g, const char* name, int argc, const char* prefix) { return fcFmt(g->c, "%s%s%s_%d", Px, prefix, Mangle(g, name), argc); }
static const char* FieldFn(FgGen* g, const char* prefix, const char* field) { return fcFmt(g->c, "%s%s_%s", Px, prefix, Mangle(g, field)); }
static const char* IsOfFn(FgGen* g, const char* typeName) { return fcFmt(g->c, "%sisof_%s", Px, Mangle(g, typeName)); }
static const char* TArgOfFn(FgGen* g, const char* cls, int k) { return fcFmt(g->c, "%stargOf_%s_%d", Px, Mangle(g, cls), k); }
static const char* OperatorWrapper(FgGen* g, const char* method) { return fcFmt(g->c, "%sov_%s", Px, Mangle(g, method + strlen("operator"))); }

static const char* fgNameArgcKey(FgGen* g, const char* name, int argc) { return fcFmt(g->c, "%s\x01%d", name, argc); }
static void fgAddNameArgc(FgGen* g, FcDict* set, const char* name, int argc) {
    const char* key = fgNameArgcKey(g, name, argc);
    FgNameArgc* na;
    if (fcDictHas(set, key)) return;
    na = (FgNameArgc*)fcAlloc(g->c, sizeof *na);
    na->name = name; na->argc = argc;
    fcDictPut(set, key, na);
}
static bool fgHasNameArgc(FgGen* g, FcDict* set, const char* name, int argc) { return fcDictHas(set, fgNameArgcKey(g, name, argc)); }
/* the SortedSet<(string Name, int Argc)> in its order: the name in the culture's order, then the number */
static int fgCmpNameArgc(const void* a, const void* b) {
    const FgNameArgc* x = *(const FgNameArgc* const*)a;
    const FgNameArgc* y = *(const FgNameArgc* const*)b;
    int c = fgCultureCmp(x->name, y->name);
    return c ? c : (x->argc < y->argc ? -1 : x->argc > y->argc);
}
static FcList* fgSortedNameArgc(FgGen* g, const FcDict* set) {
    FcList* l = fcListNew(g->c);
    FC_DICT_EACH(set, e) fcListAdd(g->c, l, e->value);
    if (l->count > 1) qsort(l->items, (size_t)l->count, sizeof(void*), fgCmpNameArgc);
    return l;
}

/* ---- Func ---- */
static FgFunc* fgFuncNew(FgGen* g, FcProto* proto, FcChunk* chunk, FgFuncKind kind, int index) {
    FgFunc* f = (FgFunc*)fcAlloc(g->c, sizeof *f);
    FcCtx* c = g->c;
    f->proto = proto; f->chunk = chunk; f->kind = kind; f->index = index;
    f->prefix = Px;
    f->name = kind == FK_Main ? "fire_main" : fcFmt(c, "%sf%d", f->prefix, index);
    f->threadVars = fcDictNew(c, FC_KEY_STR);
    f->needsList = fcDictNew(c, FC_KEY_STR);
    f->hoists = fcListNew(c);
    f->code = "";
    f->regions = fcDictNew(c, FC_KEY_STR);
    f->finReturn = fcDictNew(c, FC_KEY_STR);
    f->finJumps = fcDictNew(c, FC_KEY_STR);
    f->guardAddr = fcDictNew(c, FC_KEY_STR);
    f->targets = fcDictNew(c, FC_KEY_STR);
    f->scalars = fcDictNew(c, FC_KEY_STR);
    return f;
}
static bool fgIsTop(const FgFunc* f) { return f->kind == FK_Main || f->kind == FK_FireBody; }
static bool fgHasSelf(const FgFunc* f) { return f->kind == FK_Method || f->kind == FK_Ctor || f->kind == FK_Init || f->kind == FK_Dtor; }
static int fgParamCount(const FgFunc* f) { return f->kind == FK_FireBody ? 0 : f->proto ? f->proto->paramCount : 0; }
static int fgThreadArgs(const FgFunc* f) { return f->kind == FK_FireBody ? f->proto->paramCount - f->globalSlotCount : 0; }
static int fgParamLike(const FgFunc* f) { return fgParamCount(f) + f->captureCount; }
static int fgFinSignature(FgGen* g, const FgFunc* f) {
    int n = fcDictCount(f->finReturn);
    (void)g;
    FC_DICT_EACH(f->finJumps, e) n += fcDictCount((FcDict*)e->value);
    return n;
}
/* ParamNames */
static FcList* fgParamNames(FgGen* g, const FgFunc* f) {
    FcList* l = fcListNew(g->c);
    int i;
    if (fgHasSelf(f)) fcListAdd(g->c, l, (void*)"self");
    else if (f->kind == FK_Lambda) fcListAdd(g->c, l, (void*)"lam");
    for (i = 0; i < fgParamCount(f); i++) fcListAdd(g->c, l, (void*)fcFmt(g->c, "P%d", i));
    return l;
}
/* SignatureOf(name, suffixOf) */
typedef const char* (*FgSuffixFn)(void* ctx, const char* param);
static const char* fgSignatureOfFn(FgGen* g, const FgFunc* f, const char* name, FgSuffixFn suffixOf, void* ctx) {
    FcBuf b = {0};
    FcList* ps;
    int i;
    if (f->kind == FK_Main) return fcFmt(g->c, "static void %s(void)", streq0(name, f->name) ? "fire_main" : name);
    if (f->kind == FK_FireBody) return fcFmt(g->c, "static void %s(const Value* args)", name);
    ps = fgParamNames(g, f);
    fcBufFmt(&b, "%s%sValue %s(", f->hasCallTArgs ? "FIRE_MAYBE_UNUSED " : "", f->exportName && streq0(name, f->name) ? "" : "static ", name);
    if (ps->count == 0) fcBufStr(&b, "void");
    for (i = 0; i < ps->count; i++) {
        const char* p = (const char*)ps->items[i];
        if (i) fcBufStr(&b, ", ");
        fcBufFmt(&b, "Value %s%s", p, suffixOf ? suffixOf(ctx, p) : "");
    }
    fcBufChar(&b, ')');
    return fcBufDone(g->c, &b);
}
static const char* fgSuffixConst(void* ctx, const char* param) { (void)param; return (const char*)ctx; }
static const char* fgSignatureOf(FgGen* g, const FgFunc* f, const char* name, const char* suffix) { return fgSignatureOfFn(g, f, name, fgSuffixConst, (void*)suffix); }
static const char* fgSignature(FgGen* g, const FgFunc* f) { return fgSignatureOf(g, f, f->name, ""); }
static const char* fgThunkSignature(FgGen* g, const FgFunc* f) { return fcFmt(g->c, "static Value %s_t(Value lam, const Value* a)", f->name); }

/* ---- the classes ---- */
static FcRtClass* fgClassOf(FgGen* g, const char* name) { return (FcRtClass*)fcDictGet(g->program->program->classes, name); }
static FcRtClass* FindClass(FgGen* g, const char* name) {
    FcRtClass* rc = fgClassOf(g, name);
    if (!rc) FG_NOTSUP("unknown class '%s'", name);
    return rc;
}
static int fgFieldIndexOf(FgGen* g, const FcRtClass* rc, const char* name) { void* v; return fcDictTry(FG_INDEX(rc), name, &v) ? FC_VALI(v) - 1 : -1; }
static bool fgHasField(FgGen* g, const FcRtClass* rc, const char* name) { return fcDictHas(FG_INDEX(rc), name); }

/* What the operator wrapper does when the left operand is not an object with that overload: the built-in operation. */
static const char* OperatorFallback(const char* m) {
    static const char* const t[][2] = {
        {"operator+", "addR(a, b, list)"}, {"operator-", "sub(a, b)"}, {"operator*", "mul(a, b)"}, {"operator/", "divide(a, b)"}, {"operator%", "modulo(a, b)"},
        {"operator^", "power(a, b)"}, {"operator&", "bitAnd(a, b)"}, {"operator|", "bitOr(a, b)"}, {"operator#", "bitXor(a, b)"}, {"operator<<", "shl(a, b)"}, {"operator>>", "shr(a, b)"}, {"operator>>>", "ushr(a, b)"},
        {"operator==", "Bool(opEq(a, b))"}, {"operator!=", "Bool(!opEq(a, b))"}, {"operator<", "Bool(lt(a, b))"}, {"operator<=", "Bool(le(a, b))"},
        {"operator>", "Bool(gt(a, b))"}, {"operator>=", "Bool(ge(a, b))"},
    };
    size_t i;
    for (i = 0; i < sizeof t / sizeof t[0]; i++) if (strcmp(t[i][0], m) == 0) return t[i][1];
    return NULL;
}

/* UseOperator: does the program overload this operator in some class? Then the operation goes through the wrapper (and the dispatcher of the method). */
static bool UseOperator(FgGen* g, const char* method) {
    int i;
    bool any = false;
    if (!LibraryRole) {
        for (i = 0; i < g->classList->count && !any; i++) if (FG_FIND(((FgClassInfo*)g->classList->items[i])->rc, method, 1).proto) any = true;
        if (!any) return false;
    }
    fcSetAdd(g->operators, method);
    fgAddNameArgc(g, g->dispatchers, method, 1);
    return true;
}

/* DisplayLibraries: what fire's own display links on a target */
static FcList* DisplayLibraries(FgGen* g, const FgTarget* target) {
    FcList* l = fcListNew(g->c);
    if (streq0(target->platform, "windows")) { fcListAdd(g->c, l, (void*)"user32"); fcListAdd(g->c, l, (void*)"gdi32"); }
    else if (streq0(target->name, "macos")) { fcListAdd(g->c, l, (void*)"framework:Cocoa"); fcListAdd(g->c, l, (void*)"framework:QuartzCore"); fcListAdd(g->c, l, (void*)"framework:GameController"); }
    else { fcListAdd(g->c, l, (void*)"dl"); fcListAdd(g->c, l, (void*)"pthread"); }
    return l;
}

/* The number of an enum in the generated table (1-based). */
static int EnumIndex(FgGen* g, const FcEnumType* type) {
    int i;
    for (i = 0; i < g->enumList->count; i++) if (g->enumList->items[i] == type) return i + 1;
    fcListAdd(g->c, g->enumList, (void*)type);
    return g->enumList->count;
}
/* EnumType.Matches: the name, or the plain name of a qualified one */
static bool fgEnumMatches(const FcEnumType* e, const char* typeName) {
    const char* dot;
    if (streq0(e->name, typeName)) return true;
    dot = strrchr(e->name, '.');
    return dot && streq0(dot + 1, typeName) && !strchr(typeName, '.');
}
/* The number of the enum a type name means (0: no enum of the program). */
static int EnumIndexNamed(FgGen* g, const char* typeName) {
    int i;
    for (i = 0; i < g->enumList->count; i++) if (fgEnumMatches((const FcEnumType*)g->enumList->items[i], typeName)) return i + 1;
    return 0;
}

/* `Class.Method` to a function (for error messages), otherwise "a lambda or a function of the script" */
static const char* Describe(FgGen* g, const FcProto* proto) {
    FC_DICT_EACH(g->program->program->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        FC_DICT_EACH(rc->methods, m) {
            FcList* l = (FcList*)m->value;
            int i;
            for (i = 0; i < l->count; i++) if (l->items[i] == proto) return fcFmt(g->c, "'%s.%s'", rc->name, (const char*)m->key);
        }
    }
    return "a lambda or a function of the script";
}

/* ---- the static types ---- */
static int TypeOf(int kind, int unit) { return kind == KindUnknown ? TypeUnknown : kind | (kind == KindBool ? 0 : unit) << 8; }
static int KindOfType(int type) { return (type & (NullableBit | TypeNone)) != 0 ? KindUnknown : (type & 0x0F); }
static int BaseKindOfType(int type) { return type & 0x0F; }
static bool IsNullable(int type) { return (type & NullableBit) != 0; }
static int UnitOfType(int type) { return type >> 8; }
static int ArrElemKind(int elem) {
    switch (elem) {
        case AE_I8: case AE_I16: case AE_I32: case AE_I64: return KindInt;
        case AE_F8: case AE_F16: case AE_F32: case AE_F64: return KindFloat;
        case AE_Bool: return KindBool;
        default: return KindUnknown;
    }
}
static int ArrElemBits(int elem) { return elem == AE_I8 ? 8 : elem == AE_I16 ? 16 : elem == AE_I32 ? 32 : 64; }
static const char* ArrElemCType(int elem) {
    switch (elem) {
        case AE_I8: return "int8_t"; case AE_I16: return "int16_t"; case AE_I32: return "int32_t"; case AE_I64: return "FireInt";
        case AE_F8: case AE_F16: case AE_F32: return "float"; case AE_F64: return "Real"; case AE_Bool: return "uint8_t"; default: return "uint16_t";
    }
}
static bool IsPlainType(int type) {
    int k = BaseKindOfType(type);
    return type == TypeNone || type == TypeUndefined || k == KindInt || k == KindFloat || k == KindBool || k == KindWord;
}

/* DeclaredKind (src/fire/Bytecode/DeclaredKind.cs): the tag (TypeTag) of a declared kind, its bits */
static int fgDkTagOf(int declared) { return (declared & 0x1F) - 1; }
static bool fgDkIsNonNull(int declared) { return (declared & DK_NonNullBit) != 0; }
static bool fgDkIsUnitless(int declared) { return (declared & DK_UnitlessBit) != 0; }
static bool fgDkKeepsWidth(int declared) { return (declared & DK_KeepsWidthBit) != 0; }
static bool fgDkIsString(int declared) { return (declared & 0x1F) == DK_StringKind; }

static int ObjTypeNamed(FgGen* g, const char* name) {
    void* v;
    int index;
    if (fcDictTry(g->objTypeIndex, name, &v)) index = FC_VALI(v) - 1;
    else { index = g->objTypeNames->count; fcListAdd(g->c, g->objTypeNames, (void*)name); fcDictPut(g->objTypeIndex, name, FC_IVAL(index + 1)); }
    return TypeOf(KindObj, index);
}
static int ObjType(FgGen* g, const FcRtClass* rc) { return ObjTypeNamed(g, rc->name); }
static FcRtClass* ObjClass(FgGen* g, int type) { return FindClass(g, (const char*)g->objTypeNames->items[UnitOfType(type)]); }
/* The interface of a type "an object of a class that implements this interface", NULL for any other type. */
static const char* InterfaceOfType(FgGen* g, int type) {
    const char* name;
    if (BaseKindOfType(type) != KindObj || (type & TypeNone) != 0) return NULL;
    name = (const char*)g->objTypeNames->items[UnitOfType(type)];
    return !fcDictHas(g->program->program->classes, name) ? name : NULL;
}
/* InterfaceType: the type of a value that passed the check of a declaration with only this interface; -1: none */
static int InterfaceType(FgGen* g, const char* spec) {
    FgSpec* s = FG_SPEC(spec);
    const char* name = fgSpecSingleName(s);
    bool implemented = false;
    if (s->typeParamIndex >= 0 || !name || fcDictHas(g->program->program->classes, name) || streq0(fcPlainName(g->c, name), "IEnumerable")) return -1;
    FC_DICT_EACH(g->program->program->classes, e) if (fcListHasStr(((FcRtClass*)e->value)->interfaces, name)) { implemented = true; break; }
    return implemented ? ObjTypeNamed(g, name) | NullableBit : -1;
}
/* Is the class, a base class of it or an interface of it called `name`? (what a typed `catch` matches) */
static bool ClassIs(const FcRtClass* rc, const char* name) {
    for (; rc; rc = rc->base) if (streq0(rc->name, name) || fcListHasStr(rc->interfaces, name)) return true;
    return false;
}
/* The classes of the program (that are created) an object of type `rc` can be: rc and the classes derived from it. */
static FcList* InstancesOf(FgGen* g, const FcRtClass* rc) {
    FcList* l = fcListNew(g->c);
    int i;
    for (i = 0; i < g->classList->count; i++) { FgClassInfo* ci = (FgClassInfo*)g->classList->items[i]; if (ClassIs(ci->rc, rc->name)) fcListAdd(g->c, l, ci); }
    return l;
}

static bool VarRef(FgGen* g, const char* key) { return fcDictHas(g->varRef, key); }
static void MarkVarRef(FgGen* g, const char* key) {
    if (VarRef(g, key)) return;
    fcDictPut(g->varRef, fcStr(g->c, key), (void*)1);
    g->version++;
}
/* Key of a variable in _varRef: parameters and block locals belong to one function, globals and statics are shared. */
static const char* VarKey(FgGen* g, const FgFunc* f, const char* name) { return name[0] == 'G' || fcStartsWith(name, "SF") ? name : fcFmt(g->c, "%s:%s", f->name, name); }

/* Where two types meet: the same type, a type and `undefined` (nullable), else unknown. */
static int JoinTypes(int a, int b) {
    int narrow;
    if (a == b) return a;
    if (a == TypeNone) return b;
    if (b == TypeNone) return a;
    narrow = a & b & (NarrowBit | CutBit);
    a &= ~(NarrowBit | CutBit); b &= ~(NarrowBit | CutBit);
    if (a == b) return a | narrow;
    if (a == TypeUnknown || b == TypeUnknown) return TypeUnknown;
    if (a == TypeUndefined) return b | NullableBit;
    if (b == TypeUndefined) return a | NullableBit;
    return (a | NullableBit) == (b | NullableBit) ? a | NullableBit | narrow : TypeUnknown;
}
static const FcU* UnitById(FgGen* g, int id) { return id == 0 ? FC_UNITLESS : (const FcU*)g->units->items[id]; }
static bool fgVarTypeTry(FgGen* g, const char* key, int* type) { void* v; if (!fcDictTry(g->varType, key, &v)) return false; *type = FC_VALI(v); return true; }
static void JoinVarType(FgGen* g, const char* key, int type) {
    int old, joined;
    bool had;
    if (type == TypeNone) return;
    had = fgVarTypeTry(g, key, &old);
    joined = had ? JoinTypes(old, type) : type;
    if (had && joined == old) return;
    if (had) *fcDictRef(g->varType, key) = FC_IVAL(joined);
    else fcDictPut(g->varType, fcStr(g->c, key), FC_IVAL(joined));
    g->version++;
}

/* The unit a demand certainly leaves a number in; -1 when it leaves the unit open (`any`, `any m`). */
static int DemandedUnitId(FgGen* g, const char* demand) {
    const char* unit = fgTrimStart(demand, '#');
    if (streq0(unit, "any") || fcStartsWith(unit, "any ")) return -1;
    return UnitId(g, DemandUnit(g, unit));
}

/* ---- specialisations of generic methods ---- */
static int CutAll(int t) { return t & ~(NarrowBit | CutBit); }
/* SpecKind: the kind and unit a value of a type argument certainly has; false when its unit is open or it is no number */
static bool SpecKind(FgGen* g, const char* typeArg, int* kind, int* unit) {
    const char* nk;
    const char* nd;
    const char* demand;
    if (streq0(typeArg, "bool")) { *kind = KindBool; *unit = 0; return true; }
    if (!fgNumberDemand(g->c, typeArg, &nk, &nd)) return false;
    demand = fgTrimStart(nd, '#');
    if (streq0(demand, "any") || fcStartsWith(demand, "any ")) return false;
    *kind = streq0(nk, "int") ? KindInt : KindFloat;
    *unit = UnitId(g, DemandUnit(g, demand));
    return true;
}
/* A value that is a number of this kind in this unit (or a bool). */
static const char* KindTest(FgGen* g, const char* value, int kind, int unit) {
    return kind == KindBool ? fcFmt(g->c, "%s.kind == K_Bool", value) : fcFmt(g->c, "%s.kind == %s && %s.unit == %s", value, kind == KindInt ? "K_Int" : "K_Float", value, UnitC(g, unit));
}

/* The parameters of a generic method that a copy for these type arguments types; NULL when there is none. */
static FcList* SpecParamsFor(FgGen* g, const FcProto* proto, const char* owner, FcList* parts) {
    FcList* specParams = fcListNew(g->c);
    FgCode code = fgDecode(g->c, proto->chunk);
    int i;
    for (i = 0; i < code.n; i++) {
        FgInstr* c = &code.p[i];
        FgSpec* spec;
        int kind, unit;
        if (c->op != OP_GuardLocalType) continue;
        spec = FG_SPEC(fgConstStr(proto->chunk, c->a[1]));
        if (spec->typeParamIndex < 0 || !streq0(spec->typeParamClass, owner) || spec->typeParamIndex >= parts->count) continue;
        if (SpecKind(g, (const char*)parts->items[spec->typeParamIndex], &kind, &unit)) {
            FgSpecParam* p = (FgSpecParam*)fcAlloc(g->c, sizeof *p);
            p->slot = c->a[0]; p->kind = kind; p->unit = unit;
            fcListAdd(g->c, specParams, p);
        }
    }
    return specParams->count == 0 ? NULL : specParams;
}

/* The copy of a generic method for one set of type arguments (`key`: "m:" + those of the method, "c:Class:" + those of a class). */
static FgFunc* SpecFunc(FgGen* g, FgFunc* generic, const char* key, const char* methodTArgs, FcList* specParams) {
    const char* k = fcFmt(g->c, "%p\x01%s", (void*)generic->proto, key);
    FgFunc* spec = (FgFunc*)fcDictGet(g->specFuncs, k);
    if (spec) return spec;
    spec = fgFuncNew(g, generic->proto, generic->chunk, generic->kind, g->funcs->count);
    spec->specTArgs = methodTArgs;
    spec->specParams = specParams;
    fcListAdd(g->c, g->funcs, spec);
    fcDictPut(g->specFuncs, k, spec);
    g->version++;
    return spec;
}

/* The text of the SetTypeArgs (for a call) right before a call instruction (a CopyArgs may stand between them), or NULL. */
static const char* CallTArgsText(FgFunc* f, const FgInstr* ins) {
    int i = f->indexOf[ins->addr] - 2;
    if (i >= 0 && f->decoded.p[i].op == OP_CopyArgs) i--;
    return i >= 0 && f->decoded.p[i].op == OP_SetTypeArgs && f->decoded.p[i].a[1] != 0 ? fgConstStr(f->chunk, f->decoded.p[i].a[0]) : NULL;
}

/* Specialize: the hybrid of generics - a call of a generic method whose type arguments are constant goes to a copy of it. */
typedef const char* (*FgSlotFn)(void* ctx, int k);
typedef int (*FgSlotTypeFn)(void* ctx, int k);
static FgFunc* Specialize(FgGen* g, FgFunc* f, const FgInstr* ins, FgFunc* generic, int firstArg, int argc, FgSlotFn slot, FgSlotTypeFn slotType, void* ctx, const char** condition) {
    const char* text;
    FcList* specParams;
    FgFunc* spec;
    FcBuf b = {0};
    int i, n = 0;
    *condition = NULL;
    if (generic->external || !generic->proto || !(text = CallTArgsText(f, ins)) || strchr(text, '$')) return generic;
    specParams = SpecParamsFor(g, generic->proto, "", fgSplit(g->c, text, '|'));
    if (!specParams) return generic;
    for (i = 0; i < specParams->count; i++) if (((FgSpecParam*)specParams->items[i])->slot >= argc) return generic;   /* (a default value: its kind is not known here) */
    spec = SpecFunc(g, generic, fcFmt(g->c, "m:%s", text), text, specParams);
    for (i = 0; i < specParams->count; i++) {
        FgSpecParam* p = (FgSpecParam*)specParams->items[i];
        if ((slotType(ctx, firstArg + p->slot) & ~(NarrowBit | CutBit)) != TypeOf(p->kind, p->unit)) {
            if (n++) fcBufStr(&b, " && ");
            fcBufStr(&b, KindTest(g, slot(ctx, firstArg + p->slot), p->kind, p->unit));
        }
    }
    *condition = n == 0 ? NULL : fcBufDone(g->c, &b);
    if (n == 0) fcBufFree(&b);
    return spec;
}

/* ArrayOfParamParts (CppGenerator.cs below): the class and the place of the type parameter of a field `T items[]` */
static bool ArrayOfParamParts(FgGen* g, const FcRtClass* rc, const char* name, const char** cls, int* k);
static FcList* KnownSelfTypeArgs(FgGen* g, FgFunc* f, const char* owner);
static bool fgFieldAddressTaken(FgGen* g, const char* name) { return fcDictHas(g->fieldAddressTaken, name); }

/* SpecArrayOfParamType: a field `T items[]` of `this` in code for known type arguments of the class; -1 otherwise */
static int SpecArrayOfParamType(FgGen* g, FgFunc* f, const FcRtClass* rc, const char* field) {
    const char* owner = f->proto && f->proto->specOwner ? f->proto->specOwner : f->specClassOwner;
    FcList* args = f->proto && f->proto->specArgs ? f->proto->specArgs : f->specClassArgs;
    const char* ofCls;
    int ofK, code;
    if (!owner || !args || LibraryRole || HasProperty(g, field) || fgFieldAddressTaken(g, field)) return -1;
    if (!ArrayOfParamParts(g, rc, field, &ofCls, &ofK)) return -1;
    if (!streq0(ofCls, owner)) args = KnownSelfTypeArgs(g, f, ofCls);
    if (!args || ofK >= args->count) return -1;
    if ((code = fgTypedElementOf((const char*)args->items[ofK])) == 0) return -1;
    return TypeOf(KindArr, code & 0xFF) | NullableBit;
}

/* SpecFieldType: a field of `this` read in a copy for the type arguments of a class; -1 otherwise */
static int SpecFieldType(FgGen* g, FgFunc* f, const FgInstr* ins, const char* field) {
    int i = f->indexOf[ins->addr] - 2, arrayType, kind, unit;
    const char* required;
    FgSpec* spec;
    FcRtClass* rc = f->chunk->ownerClass;
    if (i < 0 || f->decoded.p[i].op != OP_LoadThis) return -1;
    if (rc && (arrayType = SpecArrayOfParamType(g, f, rc, field)) >= 0) return arrayType;
    if (!f->specClassOwner || !f->specClassArgs || !rc) return -1;
    if (!(required = fgFieldRequiredType(rc, field))) return -1;
    spec = FG_SPEC(required);
    if (spec->typeParamIndex < 0 || !streq0(spec->typeParamClass, f->specClassOwner) || spec->typeParamIndex >= f->specClassArgs->count
        || !SpecKind(g, (const char*)f->specClassArgs->items[spec->typeParamIndex], &kind, &unit)) return -1;
    return TypeOf(kind, unit) | NullableBit;
}

/* The type arguments the program uses with constants */
static void AddTArgText(FgGen* g, FcDict* into, const char* key, const char* text) {
    FcList* parts;
    FcDict* set;
    int i, kind, unit;
    bool any = false;
    if (strchr(text, '$')) return;
    parts = fgSplit(g->c, text, '|');
    for (i = 0; i < parts->count && !any; i++) if (SpecKind(g, (const char*)parts->items[i], &kind, &unit)) any = true;
    if (!any) return;
    if (!(set = (FcDict*)fcDictGet(into, key))) { set = fcDictNew(g->c, FC_KEY_STR); fcDictPut(into, fcStr(g->c, key), set); }
    if (fcSetAdd(set, fcStr(g->c, text))) g->version++;
}
/* The constant type arguments of a class instantiation, and those it gives its base classes. */
static void AddClassTArgs(FgGen* g, const char* className, const char* text) {
    FcRtClass* rc;
    FcList* args;
    FcList* given;
    FcList* out;
    int i;
    AddTArgText(g, g->classTArgTexts, className, text);
    if (!(rc = fgClassOf(g, className)) || !rc->base || !(given = rc->baseTypeArgs)) return;
    args = fgSplit(g->c, text, '|');
    out = fcListNew(g->c);
    for (i = 0; i < given->count; i++) {
        const char* t = (const char*)given->items[i];
        int j;
        if (t[0] != '$') { fcListAdd(g->c, out, (void*)t); continue; }
        j = atoi(t + 1);
        fcListAdd(g->c, out, (void*)(j < args->count ? (const char*)args->items[j] : ""));
    }
    AddClassTArgs(g, rc->base->name, fgJoin(g->c, out, "|"));
}
/* Collects the constant type arguments of the calls and `new`s of a chunk (and the base type arguments of the classes). */
static void CollectTArgTexts(FgGen* g, FgFunc* f) {
    FgCode code = f->decoded;
    int i;
    for (i = 0; i < code.n; i++) {
        const char* text;
        int n;
        FgInstr* next;
        if (code.p[i].op != OP_SetTypeArgs) continue;
        text = fgConstStr(f->chunk, code.p[i].a[0]);
        n = i + 1;
        if (n < code.n && code.p[n].op == OP_CopyArgs) n++;
        if (n >= code.n) continue;
        next = &code.p[n];
        if (code.p[i].a[1] == 0) {
            if (next->op == OP_NewObject || next->op == OP_NewObjectOwned) AddClassTArgs(g, fgConstStr(f->chunk, next->a[0]), text);
        }
        else if (next->op == OP_CallMethod) AddTArgText(g, g->callTArgTexts, fgNameArgcKey(g, fgConstStr(f->chunk, next->a[0]), next->a[1]), text);
        else if (next->op == OP_CallStaticMethod || next->op == OP_CallBaseMethod) AddTArgText(g, g->callTArgTexts, fgNameArgcKey(g, fgConstStr(f->chunk, next->a[1]), next->a[2]), text);
    }
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* ci = (FgClassInfo*)g->classList->items[i];
        FcList* given = ci->rc->baseTypeArgs;
        int k;
        bool dollar = false;
        if (!given || !ci->rc->base) continue;
        for (k = 0; k < given->count; k++) if (((const char*)given->items[k])[0] == '$') dollar = true;
        if (!dollar) AddClassTArgs(g, ci->rc->base->name, fgJoin(g->c, given, "|"));
    }
}

/* The method name and number of parameters of a method of a class (false for anything else). */
static bool MethodKey(FgGen* g, const FcProto* proto, const char** name, int* argc) {
    FC_DICT_EACH(g->program->program->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        FC_DICT_EACH(rc->methods, m) {
            FcList* l = (FcList*)m->value;
            int i;
            for (i = 0; i < l->count; i++) if (l->items[i] == proto) { *name = (const char*)m->key; *argc = proto->paramCount; return true; }
        }
    }
    return false;
}

static const char* TArgOfExpr(FgGen* g, const char* cls, int k, const char* ownerObj);
static const char* TArgRef(FgGen* g, const char* text);
static const char* SpecArgTest(FgGen* g, const FcProto* spec, int i, const char* v, int type);
static int KindOfDeclared(int declared);

static const char* ParamTests(FgGen* g, FcList* ps) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 0; i < ps->count; i++) { FgSpecParam* p = (FgSpecParam*)ps->items[i]; fcBufFmt(&b, " && %s", KindTest(g, fcFmt(g->c, "P%d", p->slot), p->kind, p->unit)); }
    return fcBufDone(g->c, &b);
}

/* The trampoline of a generic method: a call whose type arguments and arguments fit one of its copies goes there. */
static const char* Trampoline(FgGen* g, FgFunc* f) {
    FcBuf lines = {0};
    FcBuf ab = {0};
    FcProto* proto = f->proto;
    FcDict* handTexts;
    const char* args;
    const char* mname;
    int margc, i, k;
    if (f->specParams || !proto || !(f->kind == FK_Method || f->kind == FK_Static)) return "";
    fcBufStr(&ab, "");
    if (fgHasSelf(f)) fcBufStr(&ab, "self");
    for (i = 0; i < fgParamCount(f); i++) fcBufFmt(&ab, "%sP%d", ab.len ? ", " : "", i);
    args = fcBufDone(g->c, &ab);
    fcBufStr(&lines, "");
    /* the typed specializations written in the class (`Add<int>(int item)` beside `Add(T item)`, SPEC 5.8.1): first, by the type arguments of the object */
    handTexts = fcDictNew(g->c, FC_KEY_STR);
    if (fgHasSelf(f) && proto->specializations)
        for (i = 0; i < proto->specializations->count; i++) {
            FcProto* specProto = (FcProto*)proto->specializations->items[i];
            FgFunc* target;
            FcBuf tests = {0};
            if (!specProto->specOwner || !specProto->specArgs) continue;
            fcSetAdd(handTexts, fcFmt(g->c, "%s:%s", specProto->specOwner, fgJoin(g->c, specProto->specArgs, "|")));
            target = GetFunc(g, specProto, FK_Method, 0);
            fcBufStr(&tests, "");
            for (k = 0; k < specProto->specArgs->count; k++) {
                const char* t = (const char*)specProto->specArgs->items[k];
                if (!streq0(t, "any")) fcBufFmt(&tests, " && %s == %s", TArgOfExpr(g, specProto->specOwner, k, "objAt(self)"), TArgRef(g, t));
            }
            for (k = 0; k < fgParamCount(f); k++) fcBufStr(&tests, SpecArgTest(g, specProto, k, fcFmt(g->c, "P%d", k), TypeUnknown));
            fcBufFmt(&lines, "    if (self.kind == K_Class%s) return %s%s(%s);\n", fcBufDone(g->c, &tests), target->name, Wrapped ? "_b" : "", args);
        }
    /* the type arguments of the call of a generic method */
    if (f->hasCallTArgs && MethodKey(g, proto, &mname, &margc)) {
        FcDict* texts = (FcDict*)fcDictGet(g->callTArgTexts, fgNameArgcKey(g, mname, margc));
        if (texts) {
            FcList* sorted = fgSortedKeys(g->c, texts, true);
            for (i = 0; i < sorted->count; i++) {
                const char* text = (const char*)sorted->items[i];
                FcList* parts = fgSplit(g->c, text, '|');
                FcList* ps = SpecParamsFor(g, proto, "", parts);
                FgFunc* spec;
                FcBuf targs = {0};
                if (!ps) continue;
                spec = SpecFunc(g, f, fcFmt(g->c, "m:%s", text), text, ps);
                fcBufStr(&targs, "");
                for (k = 0; k < parts->count; k++) {
                    const char* t = (const char*)parts->items[k];
                    if (*t) fcBufFmt(&targs, " && ((TArgs)g_callTArgs)[%d] == %s", k, TArgRef(g, t));
                }
                fcBufFmt(&lines, "    if (g_callTArgs%s%s) return %s%s(%s);\n", fcBufDone(g->c, &targs), ParamTests(g, ps), spec->name, Wrapped ? "_b" : "", args);
            }
        }
    }
    /* the type arguments of the object (a method of a generic class, or of a class derived from one) */
    if (fgHasSelf(f)) {
        FgCode code = fgDecode(g->c, proto->chunk);
        FcList* owners = fcListNew(g->c);
        for (i = 0; i < code.n; i++) {
            const char* o;
            if (code.p[i].op != OP_GuardLocalType) continue;
            o = FG_SPEC(fgConstStr(proto->chunk, code.p[i].a[1]))->typeParamClass;
            if (o && *o && !fcListHasStr(owners, o)) fcListAdd(g->c, owners, (void*)o);
        }
        for (i = 0; i < owners->count; i++) {
            const char* owner = (const char*)owners->items[i];
            FcDict* classTexts = (FcDict*)fcDictGet(g->classTArgTexts, owner);
            FcList* sorted;
            int t;
            if (!classTexts) continue;
            sorted = fgSortedKeys(g->c, classTexts, true);
            for (t = 0; t < sorted->count; t++) {
                const char* text = (const char*)sorted->items[t];
                FcList* parts;
                FcList* ps;
                FgFunc* spec;
                FcBuf targs = {0};
                if (fcDictHas(handTexts, fcFmt(g->c, "%s:%s", owner, text))) continue;   /* (written by hand) */
                parts = fgSplit(g->c, text, '|');
                if (!(ps = SpecParamsFor(g, proto, owner, parts))) continue;
                spec = SpecFunc(g, f, fcFmt(g->c, "c:%s:%s", owner, text), NULL, ps);
                fcBufStr(&targs, "");
                for (k = 0; k < parts->count; k++) {
                    int kind, unit;
                    if (SpecKind(g, (const char*)parts->items[k], &kind, &unit)) fcBufFmt(&targs, " && %s == %s", TArgOfExpr(g, owner, k, "objAt(self)"), TArgRef(g, (const char*)parts->items[k]));
                }
                spec->specClassOwner = owner;
                spec->specClassArgs = parts;
                fcBufFmt(&lines, "    if (self.kind == K_Class%s%s) return %s%s(%s);\n", fcBufDone(g->c, &targs), ParamTests(g, ps), spec->name, Wrapped ? "_b" : "", args);
            }
        }
    }
    return fcBufDone(g->c, &lines);
}

/* SpecArgTest: the test that argument `i` (`v`, of static type `type`) fits parameter i of a specialization as it is */
static const char* SpecArgTest(FgGen* g, const FcProto* spec, int i, const char* v, int type) {
    int declared = spec->specParamKinds && spec->specParamKindCount >= 0 && i < spec->specParamKindCount ? spec->specParamKinds[i] : 0;
    int kind;
    bool nonNull, unitless;
    const char* kc;
    const char* fits;
    if (declared == 0) return "";
    kind = KindOfDeclared(declared);
    nonNull = fgDkIsNonNull(declared); unitless = fgDkIsUnitless(declared);
    if ((type & TypeNone) == 0 && type != TypeUnknown && BaseKindOfType(type) == kind && (!unitless || kind == KindBool || kind == KindWord || UnitOfType(type) == 0)
        && (!nonNull || !IsNullable(type)) && (kind != KindInt || (type & NarrowBit) != 0)) return "";
    kc = kind == KindInt ? "K_Int" : kind == KindFloat ? "K_Float" : kind == KindBool ? "K_Bool" : kind == KindWord ? "K_Word" : "K_Char";
    fits = fcFmt(g->c, "%s.kind == %s%s%s", v, kc, unitless && (kind == KindInt || kind == KindFloat) ? fcFmt(g->c, " && %s.unit == 0", v) : "",
        kind == KindInt ? fcFmt(g->c, " && %s.wide == 0 && %s.reserved == 0", v, v) : "");
    return nonNull ? fcFmt(g->c, " && (%s)", fits) : fcFmt(g->c, " && (%s.kind == K_Undefined || (%s))", v, fits);
}

typedef struct FgHandSpec { const char* condition; FgFunc* callee; FcProto* spec; } FgHandSpec;
static FcList* HandSpecCalls(FgGen* g, FcList* specs, const char* recv) {
    FcList* calls = fcListNew(g->c);
    int i, k;
    for (i = 0; i < specs->count; i++) {
        FcProto* spec = (FcProto*)specs->items[i];
        FcList* conds;
        FgHandSpec* h;
        if (!spec->specOwner || !spec->specArgs) continue;
        conds = fcListNew(g->c);
        for (k = 0; k < spec->specArgs->count; k++) {
            const char* t = (const char*)spec->specArgs->items[k];
            if (!streq0(t, "any")) fcListAdd(g->c, conds, (void*)fcFmt(g->c, "%s == %s", TArgOfExpr(g, spec->specOwner, k, fcFmt(g->c, "objAt(%s)", recv)), TArgRef(g, t)));
        }
        h = (FgHandSpec*)fcAlloc(g->c, sizeof *h);
        h->condition = fgJoin(g->c, conds, " && ");
        h->callee = GetFunc(g, spec, FK_Method, 0);
        h->spec = spec;
        fcListAdd(g->c, calls, h);
    }
    return calls;
}

/* The type arguments of `this` as the class `owner` sees them, when the running code is a specialization or a copy for known type arguments; NULL otherwise. */
static FcList* KnownSelfTypeArgs(FgGen* g, FgFunc* f, const char* owner) {
    FcList* args = f->proto && f->proto->specArgs ? f->proto->specArgs : f->specClassArgs;
    FcRtClass* rc = f->chunk->ownerClass;
    const char* specOwner = f->proto && f->proto->specOwner ? f->proto->specOwner : f->specClassOwner;
    if (!args || !rc || !streq0(specOwner, rc->name)) return NULL;
    while (rc && !streq0(rc->name, owner)) {
        FcList* given = rc->baseTypeArgs ? rc->baseTypeArgs : fcListNew(g->c);
        FcList* outer = args;
        int i;
        args = fcListNew(g->c);
        for (i = 0; i < given->count; i++) {
            const char* t = (const char*)given->items[i];
            if (t[0] != '$') { fcListAdd(g->c, args, (void*)t); continue; }
            {
                char* end;
                long j = strtol(t + 1, &end, 10);
                fcListAdd(g->c, args, (void*)(end != t + 1 && !*end && j < outer->count ? (const char*)outer->items[j] : "any"));
            }
        }
        rc = rc->base;
    }
    return rc ? args : NULL;
}

/* Is the specialization certainly the one for `self`? */
static bool SpecKnownForSelf(FgGen* g, FgFunc* f, const FcProto* spec) {
    FcList* known;
    int k;
    if (!spec->specOwner || !spec->specArgs || !(known = KnownSelfTypeArgs(g, f, spec->specOwner)) || known->count != spec->specArgs->count) return false;
    for (k = 0; k < spec->specArgs->count; k++) {
        const char* t = (const char*)spec->specArgs->items[k];
        const char* kn = (const char*)known->items[k];
        if (!(streq0(t, "any") || (streq0(t, kn) && !streq0(kn, "any")))) return false;
    }
    return true;
}
/* Do the type arguments of a specialization take those the type checker knows of a receiver ("int|string")? */
static bool SpecArgsFit(FgGen* g, FcList* specArgs, const char* known) {
    FcList* parts = fgSplit(g->c, known, '|');
    int k;
    if (parts->count != specArgs->count) return false;
    for (k = 0; k < specArgs->count; k++) if (!(streq0((const char*)specArgs->items[k], "any") || streq0((const char*)specArgs->items[k], (const char*)parts->items[k]))) return false;
    return true;
}

/* The declaration of what a method returns when it is a number or a bool: the DeclaredKind its return is checked against; -1 otherwise. */
static int DeclaredReturn(FgGen* g, const FcProto* proto) {
    FgCode code = fgDecode(g->c, proto->chunk);
    int declared = -1, i;
    for (i = 1; i < code.n; i++) {
        int d, before;
        if (code.p[i].op != OP_Return) continue;
        if (code.p[i - 1].op == OP_LoadConst && fgConst(proto->chunk, code.p[i - 1].a[0])->kind == CK_Undefined) continue;
        if (code.p[i - 1].op != OP_GuardTop) return -1;
        d = (code.p[i - 1].a[0] + 1) & 0xFF;
        before = i - 2;
        while (before >= 0 && code.p[before].op == OP_GuardTop) before--;
        if (before >= 0 && code.p[before].op == OP_LoadConst && fgConst(proto->chunk, code.p[before].a[0])->kind == CK_Undefined) continue;
        if (before >= 0 && code.p[before].op == OP_CheckUnit && streq0(fgTrimStart(fgConstStr(proto->chunk, code.p[before].a[0]), '#'), "unitless")) d |= DK_UnitlessBit;
        if (declared >= 0 && declared != d) return -1;
        declared = d;
    }
    return declared;
}

/* The static type of a value checked against a declared number kind without a unit, or bool (GuardTop): TypeUnknown for anything else. */
static int DeclaredResultType(int declared) {
    int kind = KindOfDeclared(declared);
    bool nonNull = fgDkIsNonNull(declared);
    if (kind == KindBool) return TypeOf(KindBool, 0) | (nonNull ? 0 : NullableBit);
    if (!(kind == KindInt || kind == KindFloat) || !fgDkIsUnitless(declared)) return TypeUnknown;
    if (kind == KindFloat) return TypeOf(KindFloat, 0) | (nonNull ? 0 : NullableBit);
    return TypeOf(KindInt, 0) | (nonNull ? (fgDkKeepsWidth(declared) ? 0 : NarrowBit) : NullableBit | CutBit);
}

static FgFunc* GetFunc(FgGen* g, FcProto* proto, FgFuncKind kind, int captureCount) {
    FgFunc* existing;
    FgFunc* func;
    FgModuleSpec* module;
    const char* key;
    static const char* const kindNames[] = { "Main", "Static", "Method", "Ctor", "Init", "Dtor", "Lambda", "FireBody" };
    if (g->demandOwner && !g->keeping) NoteMadeBy(g, DemandOwnerOf(proto->chunk));   /* (a library: the class whose code this calls is reached with the caller's) */
    existing = (FgFunc*)fcDictGet(g->funcByProto, proto);
    if (existing) {
        if (existing->kind != kind) FG_NOTSUP("a function is used both as %s and as %s: %s", kindNames[existing->kind], kindNames[kind], Describe(g, proto));
        if (existing->captureCount != captureCount) FG_NOTSUP("a lambda is created with different numbers of captures");
        return existing;
    }
    func = fgFuncNew(g, proto, proto->chunk, kind, g->funcs->count);
    func->captureCount = captureCount;
    if (Modular && ProtoKey(g, proto, &module, &key) && module) {
        /* a member of a class of a library module: called by its exported name - generated by its module, declared everywhere else */
        func->exportName = ExportName(g, module, key);
        func->external = module != g->self;
        if (kind != FK_Main) func->name = func->exportName;
    }
    fcListAdd(g->c, g->funcs, func);
    fcDictPut(g->funcByProto, proto, func);
    g->version++;
    return func;
}

/* The value of a `readonly` static field whose initializer is a constant: only its initializer sets it, so every read is that constant. NULL for any other field. */
static const FcConst* ConstantStatic(FgGen* g, const char* className, const char* field) {
    FcRtClass* rc = fgClassOf(g, className);
    const FcRtClass* owner;
    FcProto* init = NULL;
    FgCode code;
    const FcConst* c;
    int i;
    if (!rc || !(owner = fgFindStaticFieldOwner(rc, field)) || !owner->readonlyStatics || !fcDictHas(owner->readonlyStatics, field)) return NULL;
    for (i = 0; i < owner->staticFieldNames->count; i++) if (streq0((const char*)owner->staticFieldNames->items[i], field)) { init = (FcProto*)owner->staticFieldInits->items[i]; break; }
    if (!init) return NULL;
    code = fgDecode(g->c, init->chunk);
    if (code.n < 2 || code.p[0].op != OP_LoadConst || code.p[1].op != OP_Return) return NULL;
    c = fgConst(init->chunk, code.p[0].a[0]);
    return c->kind == CK_Int || c->kind == CK_Float || c->kind == CK_Bool || c->kind == CK_Char || c->kind == CK_Word || c->kind == CK_String ? c : NULL;
}

static FgClassInfo* RegisterClass(FgGen* g, const char* name) {
    FgClassInfo* info;
    FgClassInfo* foreign;
    FcRtClass* rc;
    void* fixedId;
    int id;
    if (LibraryRole && !g->keeping && g->self && g->classList->count >= g->self->classes->count) { fcSetAdd(g->demandedClasses, name); NoteMadeBy(g, name); }
    info = (FgClassInfo*)fcDictGet(g->classes, name);
    if (info) return info;
    rc = FindClass(g, name);
    if ((foreign = ForeignClass(g, name, rc)) != NULL) return foreign;
    id = !g->fixedIds ? g->classList->count : fcDictTry(g->fixedIds, name, &fixedId) ? FC_VALI(fixedId) - 1 : g->nextClassId++;
    info = (FgClassInfo*)fcAlloc(g->c, sizeof *info);
    info->rc = rc;
    info->id = id;
    info->idText = LibraryRole ? fcFmt(g->c, "(%s + %du)", ModuleBase(g, g->self), id) : NULL;
    fcDictPut(g->classes, rc->name, info);
    fcListAdd(g->c, g->classList, info);
    g->version++;
    return info;
}

static int StaticFieldIndex(FgGen* g, const char* className, const char* field) {
    const FcRtClass* owner = fgFindStaticFieldOwner(FindClass(g, className), field);
    const char* key;
    void* v;
    int index;
    if (!owner) FG_NOTSUP("static field '%s.%s' not found", className, field);
    key = fcFmt(g->c, "%s\x01%s", owner->name, field);
    if (fcDictTry(g->staticFields, key, &v)) return FC_VALI(v) - 1;
    index = fcDictCount(g->staticFields);
    fcDictPut(g->staticFields, key, FC_IVAL(index + 1));
    g->version++;
    return index;
}
/* The C variable of a static field: `SF{n}`, or with modules (ROADMAP 9) a name every module knows - defined by the module of the class, declared by the
 * others; it can hold anything (other modules write it). */
static const char* StaticFieldVar(FgGen* g, const char* className, const char* field) {
    int index = StaticFieldIndex(g, className, field);
    const FcRtClass* owner;
    FgModuleSpec* m;
    const char* name;
    if (!Modular) return fcFmt(g->c, "SF%d", index);
    owner = fgFindStaticFieldOwner(FindClass(g, className), field);
    m = ModuleOf(g, owner);
    name = fcFmt(g->c, "SFx_%s_%s_%s", m ? m->id : "prog", Mangle(g, owner->name), Mangle(g, field));
    if (!VarRef(g, name)) { MarkVarRef(g, name); JoinVarType(g, name, TypeUnknown); }
    return name;
}

/* ---- fields ---- */
static bool RestrictedField(FgGen* g, const char* name);
static bool RestrictedMethodName(FgGen* g, const char* name);
static bool AccessAllowed(const FcRtClass* caller, const FcRtClass* declaring, int access);

/* The index of field `name` in every object an `rc` can be; -1 when a property, an access check or the threads need the field helpers, or the index differs. */
static int DirectFieldIndex(FgGen* g, const FcRtClass* rc, const char* name, FgFunc* from) {
    bool restricted;
    int index = -1, rawOff = INT32_MIN, i;
    FcList* inst;
    if (g->usesThreads || HasProperty(g, name)) return -1;
    restricted = RestrictedField(g, name);
    if (restricted && !from) return -1;
    inst = InstancesOf(g, rc);
    for (i = 0; i < inst->count; i++) {
        FgClassInfo* c = (FgClassInfo*)inst->items[i];
        int k = fgFieldIndexOf(g, c->rc, name), access, off;
        const FcRtClass* declaring;
        if (k < 0 || (index >= 0 && index != k)) return -1;
        if (restricted && (declaring = fgFieldAccess(c->rc, name, &access)) != NULL && !AccessAllowed(from->chunk->ownerClass, declaring, access)) return -1;
        off = RawLayoutOf(g, c->rc)->off[k];
        if (rawOff != INT32_MIN && rawOff != off) return -1;
        rawOff = off;
        index = k;
    }
    return index;
}

/* NarrowName: is the field `name` narrow - in every class that has a field of this name? */
static bool NarrowName(FgGen* g, const char* name) {
    void* known;
    bool ok = true;
    int tag = -100;
    if (!NarrowFieldsOn || fgFieldAddressTaken(g, name)) return false;
    if (fcDictTry(g->narrowEligible, name, &known)) return FC_VALI(known) == 1;
    FC_DICT_EACH(g->program->program->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        FcFieldInfo* info = NULL;
        const FcRtClass* c;
        int kind, t;
        bool eligible;
        if (!fgHasField(g, rc, name)) continue;
        for (c = rc; c && !info; c = c->base) info = fgOwnField(c, name);
        kind = info && !info->isStatic ? info->requiredKind : 0;
        t = kind == 0 ? -1 : fgDkTagOf(kind);
        eligible = t == TAG_Int ? g->program->intWidth <= 32 && !fgDkKeepsWidth(kind) : t == TAG_Float ? g->program->floatWidth == 32 : t == TAG_Bool;
        if (!eligible || (tag != -100 && tag != t)) { ok = false; break; }
        tag = t;
    }
    fcDictPut(g->narrowEligible, fcStr(g->c, name), FC_IVAL(ok ? 1 : 2));
    return ok;
}

typedef struct FgLayout { int* slot; int* half; int count; int stampAddr; bool stampThreads; int names; } FgLayout;
static FgLayout* LayoutOfCore(FgGen* g, const FcRtClass* rc) {
    FcList* names = FG_NAMES(rc);
    FgLayout* l = (FgLayout*)fcAlloc(g->c, sizeof *l);
    int next = 0, open = -1, i;
    l->slot = (int*)fcAlloc(g->c, sizeof(int) * (size_t)(names->count + 1));
    l->half = (int*)fcAlloc(g->c, sizeof(int) * (size_t)(names->count + 1));
    for (i = 0; i < names->count; i++) {
        if (RawTypeAt(g, rc, i) != 0) { l->slot[i] = -1; l->half[i] = -1; continue; }
        if (NarrowName(g, (const char*)names->items[i])) {
            if (open >= 0) { l->slot[i] = open; l->half[i] = 1; open = -1; }
            else { l->slot[i] = next; l->half[i] = 0; open = next++; }
        }
        else { l->slot[i] = next++; l->half[i] = -1; }
    }
    l->count = next;
    return l;
}
static FgLayout* LayoutOf(FgGen* g, const FcRtClass* rc) {
    FgLayout* cached = (FgLayout*)fcDictGet(g->layouts, rc);
    FgLayout* layout;
    int names = FG_NAMES(rc)->count;
    if (cached && cached->stampAddr == fcDictCount(g->fieldAddressTaken) && cached->stampThreads == g->usesThreads && cached->names == names) return cached;
    layout = LayoutOfCore(g, rc);
    layout->stampAddr = fcDictCount(g->fieldAddressTaken);
    layout->stampThreads = g->usesThreads;
    layout->names = names;
    fcDictPut(g->layouts, rc, layout);
    return layout;
}
static int SlotCount(FgGen* g, const FgClassInfo* cls) { return LayoutOf(g, cls->rc)->count; }
/* A class whose layout has the field `index`: `rc` itself, or one of the classes an object of it can be. */
static const FcRtClass* LayoutClass(FgGen* g, const FcRtClass* rc, int index) {
    FcList* inst;
    int i;
    if (index < FG_NAMES(rc)->count) return rc;
    inst = InstancesOf(g, rc);
    for (i = 0; i < inst->count; i++) { FcRtClass* c = ((FgClassInfo*)inst->items[i])->rc; if (index < FG_NAMES(c)->count) return c; }
    return rc;
}
/* The field with the index `index` of an object of `rc` (`obj`: an Obj*) as a Value. */
static const char* FieldRead(FgGen* g, const FcRtClass* rc, int index, const char* obj) {
    const char* rawRef = RawRef(g, rc, index, obj);
    FgLayout* layout;
    if (rawRef) {
        int rawType = RawTypeAt(g, LayoutClass(g, rc, index), index);
        return ScalarValueOf(g, KindOfType(rawType), UnitOfType(rawType), rawRef);
    }
    layout = LayoutOf(g, LayoutClass(g, rc, index));
    return layout->half[index] < 0 ? fcFmt(g->c, "objFields(%s)[%d]", obj, layout->slot[index]) : fcFmt(g->c, "nvGet(&objFields(%s)[%d], %d)", obj, layout->slot[index], layout->half[index]);
}
static void FieldPlace(FgGen* g, const FcRtClass* rc, int index, int* slot, int* half) {
    FgLayout* layout = LayoutOf(g, LayoutClass(g, rc, index));
    *slot = layout->slot[index];
    *half = layout->half[index];
}

static bool MayBeProbed(FgGen* g, const FcRtClass* rc, const char* name);
static const char* FieldTail(FgGen* g, const char* name, FgFunc* f, const char* list);
static bool FitsClassType(FgGen* g, int type, const char* spec);
static const char* ClassGuard(FgGen* g, const char* value, const char* spec, const char* what, const char* ownerObj, const char* callTArgs, const char* afterText);

/* `obj.name = value` on an object of a known class: the checks of the field's declaration and the store by index, like sf_; NULL when the field helpers are needed. */
static const char* DirectSetField(FgGen* g, const FcRtClass* rc, const char* name, const char* obj, const char* value, int valueType, FgFunc* from, bool nullable, bool objAlive) {
    FcList* inst = InstancesOf(g, rc);
    const char* unit;
    const char* requiredType;
    int index, kind, declaredKind, requiredArray, fieldSlot, fieldHalf, i, specArr;
    bool cutWide, arrayKnown, checksThrow, mayBeProbed;
    const char* checks;
    const char* put;
    const char* rawPut;
    const char* store;
    const char* tail;
    const char* probed;
    const char* notObject;
    const char* dead;
    const char* ofParamCheck;
    for (i = 0; i < inst->count; i++) if (((FgClassInfo*)inst->items[i])->rc->isRecord) return NULL;
    if ((index = DirectFieldIndex(g, rc, name, from)) < 0) return NULL;
    unit = fgFieldRequiredUnit(rc, name);
    kind = fgFieldRequiredKind(rc, name);
    requiredType = fgFieldRequiredType(rc, name);
    if (requiredType && FitsClassType(g, valueType, requiredType)) requiredType = NULL;
    if (kind != 0 && !IsNullable(valueType) && KindOfType(valueType) == KindOfDeclared(kind) && valueType != TypeNone
        && (KindOfDeclared(kind) != KindInt || (valueType & (NarrowBit | CutBit)) != 0)) {
        int demanded = unit ? DemandedUnitId(g, unit) : fgDkIsUnitless(kind) ? 0 : -1;
        if (KindOfDeclared(kind) == KindBool || (demanded >= 0 && UnitOfType(valueType) == demanded)) { unit = NULL; kind = 0; }
    }
    declaredKind = fgFieldRequiredKind(rc, name);
    cutWide = kind == 0 && declaredKind != 0 && fgDkTagOf(declaredKind) == TAG_Int && !fgDkKeepsWidth(declaredKind) && g->program->intWidth < 64;
    requiredArray = fgFieldRequiredArray(rc, name);
    arrayKnown = requiredArray != 0 && (requiredArray >> 8) == 1 && fgFieldRequiredUnit(rc, name) == NULL && BaseKindOfType(valueType) == KindArr
        && (valueType & TypeNone) == 0 && UnitOfType(valueType) == (requiredArray & 0xFF);
    {
        /* (in the order of the C#: each part may register strings, units and type arguments) */
        const char* c1 = unit ? fcFmt(g->c, "x = %s; ", AdaptUnitCall(g, "x", unit)) : "";
        const char* c2 = kind != 0 ? fcFmt(g->c, "%s ", FieldKindCheck(g, name, kind)) : cutWide ? "x = intProgram(x); " : "";
        const char* c3 = requiredType ? fcFmt(g->c, "%s ", ClassGuard(g, "x", requiredType, fcFmt(g->c, "Field '%s'", name), fcFmt(g->c, "objAt(%s)", obj), NULL, "")) : "";
        const char* c4 = requiredArray != 0 && !arrayKnown ? fcFmt(g->c, "%s ", FieldArrayCheck(g, rc, name, NULL)) : "";
        ofParamCheck = "";
        if (requiredArray == 0 && !(streq0(obj, "self") && (specArr = SpecArrayOfParamType(g, from, rc, name)) >= 0 && (valueType & ~NullableBit) == (specArr & ~NullableBit))) {
            const char* chk = FieldArrayCheck(g, rc, name, fcFmt(g->c, "objAt(%s)", obj));
            ofParamCheck = *chk ? fcFmt(g->c, "%s ", chk) : "";
        }
        checks = fcFmt(g->c, "%s%s%s%s%s", c1, c2, c3, c4, ofParamCheck);
    }
    FieldPlace(g, rc, index, &fieldSlot, &fieldHalf);
    rawPut = RawRef(g, rc, index, fcFmt(g->c, "objAt(%s)", obj));
    put = rawPut ? fcFmt(g->c, "%s = %s;", rawPut, RawFromValue(g, RawTypeAt(g, LayoutClass(g, rc, index), index), "x"))
        : fieldHalf >= 0 ? fcFmt(g->c, "nvSet(&objFields(objAt(%s))[%d], %d, x);", obj, fieldSlot, fieldHalf)
        : declaredKind != 0 && !fgDkIsString(declaredKind) ? fcFmt(g->c, "objFields(objAt(%s))[%d] = x;", obj, fieldSlot) : fcFmt(g->c, "setFieldAt(%s, %d, x);", obj, fieldSlot);
    checksThrow = unit != NULL || kind != 0 || requiredType != NULL || (requiredArray != 0 && !arrayKnown);
    store = checksThrow && g->usesExceptions ? fcFmt(g->c, "if (FIRE_LIKELY(!g_unwind.active)) %s", put) : put;
    tail = FieldTail(g, name, from, "NULL");
    mayBeProbed = MayBeProbed(g, rc, name);
    probed = mayBeProbed ? fcFmt(g->c, "if (FIRE_UNLIKELY(objAt(%s)->h.flags & 2)) %s(%s, x%s); else ", obj, FieldFn(g, "sf", name), obj, tail) : "";
    notObject = nullable ? fcFmt(g->c, "if (FIRE_UNLIKELY(%s.kind != K_Class)) %s(%s, x%s); else ", obj, FieldFn(g, "sf", name), obj, tail) : "";
    dead = objAlive && !nullable ? "" : fcFmt(g->c, "if (FIRE_UNLIKELY(!leafAlive(%s))) destroyedError(%s); else ", obj, obj);
    g->lastSetMayThrow = checksThrow || nullable || *dead || mayBeProbed;
    return fcFmt(g->c, "{ Value x = %s; %s%s%s{ %s%s } }", value, notObject, dead, probed, checks, store);
}

static int OwnMethodIdKind(const char* name, int argc);
/* The one method `name(argc)` every object an `rc` can be runs; NULL when there is none, several, an access check, an ownership method or the threads need the dispatcher. */
static FcProto* DirectMethod(FgGen* g, const FcRtClass* rc, const char* name, int argc, FgFunc* from) {
    bool restricted;
    FcProto* proto = NULL;
    FcList* inst;
    int i;
    if (LibraryRole || g->openWorld || g->usesThreads || g->usesActors || OwnMethodIdKind(name, argc) != 0) return NULL;
    restricted = RestrictedMethodName(g, name);
    inst = InstancesOf(g, rc);
    for (i = 0; i < inst->count; i++) {
        FgClassInfo* c = (FgClassInfo*)inst->items[i];
        FgFound found;
        if (FG_OVERLOADED(c->rc, name, argc)) return NULL;
        found = FG_FIND(c->rc, name, argc);
        if (!found.proto || found.proto->isStatic || (proto && proto != found.proto)) return NULL;
        if (restricted && !AccessAllowed(from->chunk->ownerClass, found.declaring, found.access)) return NULL;
        proto = found.proto;
    }
    return proto;
}

/* What a probe can watch, as far as the code says */
typedef struct FgProbeTarget { const FcRtClass* cls; const char* member; } FgProbeTarget;
static void NoteProbeTarget(FgGen* g, const FcRtClass* cls, const char* member) {
    const char* key = fcFmt(g->c, "%p\x01%s", (const void*)cls, member ? member : "\x02");
    FgProbeTarget* t;
    if (fcDictHas(g->probeTargets, key)) return;
    t = (FgProbeTarget*)fcAlloc(g->c, sizeof *t);
    t->cls = cls; t->member = member;
    fcDictPut(g->probeTargets, key, t);
    g->version++;
}
static void NoteProbeAnything(FgGen* g) { if (!g->probeAnything) { g->probeAnything = true; g->version++; } }

static FcList* ComputedSources(FgGen* g, const FcRtClass* rc, const char* name);
static bool fgRelated(const FcRtClass* a, const FcRtClass* b) {
    const FcRtClass* c;
    for (c = a; c; c = c->base) if (c == b) return true;
    for (c = b; c; c = c->base) if (c == a) return true;
    return false;
}
/* Can a write of the member `name` of an object of the class `rc` (NULL: not known) reach a probe? */
static bool MayBeProbed(FgGen* g, const FcRtClass* rc, const char* name) {
    if (!g->usesProbes) return false;
    if (g->probeAnything || !rc || Modular || g->separate || g->openWorld || g->dll || LibraryRole) return true;
    FC_DICT_EACH(g->probeTargets, e) {
        FgProbeTarget* t = (FgProbeTarget*)e->value;
        FcList* sources;
        if (t->cls && !fgRelated(t->cls, rc)) continue;
        if (!t->member || streq0(t->member, name)) return true;
        if ((sources = ComputedSources(g, t->cls ? t->cls : rc, t->member)) != NULL && fcListHasStr(sources, name)) return true;
    }
    return false;
}

/* With probes: an object that has some takes the field helper instead of the direct store or setter call that follows; empty without probes. */
static const char* ProbedSetter(FgGen* g, const FcRtClass* rc, const char* obj, const char* field, const char* value, FgFunc* f, const char* list) {
    return MayBeProbed(g, rc, field) ? fcFmt(g->c, "if (FIRE_UNLIKELY(objAt(%s)->h.flags & 2)) %s(%s, %s%s); else ", obj, FieldFn(g, "sf", field), obj, value, FieldTail(g, field, f, list)) : "";
}

/* The one accessor of the property `name` that every object an `rc` can be runs, when the code of `from` may use it on each of them; NULL otherwise. */
static FcProto* DirectAccessor(FgGen* g, const FcRtClass* rc, const char* name, bool setter, FgFunc* from) {
    const char* method;
    int argc = setter ? 1 : 0, i;
    bool restricted;
    FcProto* proto = NULL;
    FcList* inst;
    if (LibraryRole || g->openWorld || g->usesThreads || g->usesActors) return NULL;
    method = fcFmt(g->c, "%s%s", setter ? "set_" : "get_", name);
    restricted = RestrictedMethodName(g, method);
    inst = InstancesOf(g, rc);
    for (i = 0; i < inst->count; i++) {
        FgClassInfo* c = (FgClassInfo*)inst->items[i];
        FgFound found;
        if (fgHasField(g, c->rc, name) || FG_OVERLOADED(c->rc, method, argc)) return NULL;
        found = FG_FIND(c->rc, method, argc);
        if (!found.proto || found.proto->isStatic || (proto && proto != found.proto)) return NULL;
        if (restricted && CheckAccess && !AccessAllowed(from->chunk->ownerClass, found.declaring, found.access)) return NULL;
        proto = found.proto;
    }
    return proto;
}

/* May the code of `from` call the method `name(argc)` on an object of class `rc` (every overload the values can pick)? */
static bool MethodAllowedOn(FgGen* g, const FcRtClass* rc, const char* name, int argc, FgFunc* from) {
    FgFound found;
    if (!RestrictedMethodName(g, name)) return true;
    if (FG_OVERLOADED(rc, name, argc)) {
        FgCands* set = fgCandidates(g->c, g->rtCands, rc, name, argc);
        int i;
        for (i = 0; i < set->count; i++) if (!AccessAllowed(from->chunk->ownerClass, set->declaring[i], fgAccessOf(set->protos[i]))) return false;
        return true;
    }
    found = FG_FIND(rc, name, argc);
    return !found.proto || AccessAllowed(from->chunk->ownerClass, found.declaring, found.access);
}

/* ---- the static types of fields ---- */
static int DeclaredValueType(FgGen* g, int declared, const char* demand);
static int ClassArrType(FgGen* g, const char* text);
static const FcRtClass* DeclaredClass(FgGen* g, const char* spec);

/* The kind of buffer a field is declared as (BufBytes, BufWords - one rank), 0: none. */
static int BufferFieldOf(const FcRtClass* rc, const char* name) {
    for (; rc; rc = rc->base) {
        FcFieldInfo* field = fgOwnField(rc, name);
        if (field) {
            if (field->isStatic) return 0;
            if (streq0(field->typeText, "byte[]") || streq0(field->typeText, "uint8[]")) return BufBytes;
            if (streq0(field->typeText, "word[]")) return BufWords;
            return 0;
        }
    }
    return 0;
}

/* FieldType for an object of the class `of` (NULL: of any class) */
static int FieldTypeIn(FgGen* g, const FcRtClass* of, const char* name) {
    FcList* list;
    int result = INT32_MIN, i;
    if (LibraryRole || HasProperty(g, name) || (fgFieldAddressTaken(g, name) && !DefinedEverywhere(g, name)) || streq0(name, "Length") || streq0(name, "length") || streq0(name, "littleEndian")) return TypeUnknown;
    list = of ? InstancesOf(g, of) : g->classList;
    for (i = 0; i < list->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)list->items[i];
        int declared, bufferUnit, requiredArray, type;
        if (!fgHasField(g, cls->rc, name)) continue;
        declared = fgFieldRequiredKind(cls->rc, name);
        if (declared == 0 && fgFieldRequiredArray(cls->rc, name) == 0 && (bufferUnit = BufferFieldOf(cls->rc, name)) != 0) {
            int bufType = TypeOf(KindBuf, bufferUnit) | NullableBit;
            if (result != INT32_MIN && result != bufType) return TypeUnknown;
            result = bufType;
            continue;
        }
        if (declared == 0 && (requiredArray = fgFieldRequiredArray(cls->rc, name)) != 0) {
            int arrType;
            const char* elemText;
            if ((requiredArray >> 8) != 1 || fgFieldRequiredUnit(cls->rc, name) != NULL) return TypeUnknown;
            if (fgAeTypeOf(requiredArray) != AE_Class) arrType = TypeOf(KindArr, requiredArray & 0xFF) | NullableBit;
            else if ((elemText = fgFieldRequiredArrayType(cls->rc, name)) != NULL && (arrType = ClassArrType(g, elemText)) != -1) {}
            else return TypeUnknown;
            if (result != INT32_MIN && result != arrType) return TypeUnknown;
            result = arrType;
            continue;
        }
        if (declared == 0) {
            const char* required = fgFieldRequiredType(cls->rc, name);
            const FcRtClass* requiredRc;
            int objType;
            if (!required || !(requiredRc = DeclaredClass(g, required))) return TypeUnknown;
            objType = ObjType(g, requiredRc) | NullableBit;
            if (result != INT32_MIN && result != objType) return TypeUnknown;
            result = objType;
            continue;
        }
        type = DeclaredValueType(g, declared, fgFieldRequiredUnit(cls->rc, name));
        if (type == TypeUnknown) return TypeUnknown;
        if (result != INT32_MIN && result != type) return TypeUnknown;
        result = type;
    }
    return result == INT32_MIN ? TypeUnknown : result;
}
static int FieldType(FgGen* g, const char* name) { return FieldTypeIn(g, NULL, name); }

/* ---- the guards of declarations ---- */
static const char* HoldsExpr(FgGen* g, const char* value, const FgCond* c);
static const char* TArgOfExpr(FgGen* g, const char* cls, int k, const char* ownerObj) {
    const char* key = fcFmt(g->c, "%s\x01%d", cls, k);
    if (!fcDictHas(g->targOfs, key)) { FgNameArgc* na = (FgNameArgc*)fcAlloc(g->c, sizeof *na); na->name = cls; na->argc = k; fcDictPut(g->targOfs, key, na); }
    return fcFmt(g->c, "%s(%s)", TArgOfFn(g, cls, k), ownerObj);
}

/* The check of a declaration with a class, an interface or a constrained type parameter (a parameter, a field) */
static const char* ClassGuard(FgGen* g, const char* value, const char* spec, const char* what, const char* ownerObj, const char* callTArgs, const char* afterText) {
    FgSpec* parsed = FG_SPEC(spec);
    const char* altCheck = "";
    const char* undefCheck;
    const FcRtClass* genericRc;
    const char* typeArg;
    const char* param;
    int k;
    if (!ownerObj) ownerObj = "NULL";
    if (!afterText) afterText = "";
    undefCheck = parsed->nonNull ? fcFmt(g->c, "if (FIRE_UNLIKELY(%s.kind == K_Undefined)) guardClassFailed(%s, %s, %s); ", value, value, CString(g, parsed->display), CString(g, what)) : "";
    if (parsed->altCount > 0) {
        FcBuf fits = {0};
        int a, i;
        for (a = 0; a < parsed->altCount; a++) {
            if (a) fcBufStr(&fits, " || ");
            fcBufChar(&fits, '(');
            for (i = 0; i < parsed->alts[a].count; i++) { if (i) fcBufStr(&fits, " && "); fcBufStr(&fits, HoldsExpr(g, value, parsed->alts[a].conds[i])); }
            fcBufChar(&fits, ')');
        }
        altCheck = fcFmt(g->c, "if (FIRE_UNLIKELY(%s.kind != K_Undefined && (%s.kind != K_Class || leafAlive(%s)) && !(%s))) guardClassFailed(%s, %s, %s);",
            value, value, value, fcBufDone(g->c, &fits), value, CString(g, parsed->display), CString(g, what));
    }
    altCheck = fcFmt(g->c, "%s%s", undefCheck, altCheck);
    if (parsed->typeParamIndex < 0 && parsed->typeArgs && (genericRc = DeclaredClass(g, spec)) != NULL) {
        FcList* wanted = fcListNew(g->c);
        bool anyKnown = false;
        int i;
        for (i = 0; i < parsed->typeArgs->count; i++) {
            const char* a = (const char*)parsed->typeArgs->items[i];
            const char* w;
            size_t al = strlen(a);
            if (al == 0) w = "NULL";
            else if (a[0] != '$') w = TArgRef(g, a);
            else if (a[al - 1] == '@') w = callTArgs ? fcFmt(g->c, "(%s ? %s[%d] : NULL)", callTArgs, callTArgs, atoi(a + 1)) : "NULL";
            else if (!streq0(ownerObj, "NULL")) { int at = fgIndexOf(a, '@'); w = TArgOfExpr(g, a + at + 1, atoi(a + 1), ownerObj); }
            else w = "NULL";
            fcListAdd(g->c, wanted, (void*)w);
            if (!streq0(w, "NULL")) anyKnown = true;
        }
        if (anyKnown) {
            FcBuf fill = {0};
            fcBufStr(&fill, "");
            for (k = 0; k < wanted->count; k++) fcBufFmt(&fill, " tw_[%d] = %s; th_[%d] = %s;", k, (const char*)wanted->items[k], k, TArgOfExpr(g, genericRc->name, k, fcFmt(g->c, "objAt(%s)", value)));
            altCheck = fcFmt(g->c, "%s if (%s.kind == K_Class && leafAlive(%s)) { const TArg* tw_[%d]; const TArg* th_[%d]; int ti_;%s for (ti_ = 0; ti_ < %d; ti_++) if (FIRE_UNLIKELY(tw_[ti_] && th_[ti_] && tw_[ti_] != th_[ti_]%s && !(th_[ti_]->isA && typeNameIn(tw_[ti_]->display, th_[ti_]->isA)))) { guardTypeArgsFailed(%s, tw_, th_, %d, %s); break; } }",
                altCheck, value, value, wanted->count, wanted->count, fcBufDone(g->c, &fill), wanted->count,
                /* (per module: with modules the texts count - a `Dictionary<string, string>` of the program in a field of a library) */
                Modular ? " && strcmp(tw_[ti_]->display, th_[ti_]->display) != 0" : "", CString(g, parsed->display), wanted->count, CString(g, what));
        }
    }
    if (parsed->typeParamIndex < 0) return altCheck;
    k = parsed->typeParamIndex;
    typeArg = *parsed->typeParamClass
        ? (!streq0(ownerObj, "NULL") ? TArgOfExpr(g, parsed->typeParamClass, k, ownerObj) : NULL)
        : (callTArgs ? fcFmt(g->c, "(%s ? %s[%d] : NULL)", callTArgs, callTArgs, k) : NULL);
    if (!typeArg) return altCheck;
    param = (const char*)fgSplit(g->c, parsed->display, ' ')->items[0];
    return fcFmt(g->c, "{ const TArg* tga = %s; if (tga) { if (FIRE_UNLIKELY(!fitsTArg(&%s, tga))) { if (!strcmp(tga->display, \"string\") && %s.kind != K_Undefined) { Value o_ = %s; %s = guardStr(%s);%s (void)o_; } else guardTArgFailed(%s, %s, tga, %s); } }%s }",
        typeArg, value, value, value, value, value, afterText, value, CString(g, param), CString(g, what), *altCheck ? fcFmt(g->c, " else { %s }", altCheck) : "");
}

/* The C++ test of one condition of a declaration (or of a type argument) on `value`. */
static const char* HoldsExpr(FgGen* g, const char* value, const FgCond* c) {
    int lambdaParams;
    const char* enumerable;
    if (c->isUnit) return fcFmt(g->c, "((%s.kind == K_Int || %s.kind == K_Float) && unitCompatible(unitOf(%s), %s))", value, value, value, UnitC(g, UnitId(g, c->unit)));
    if (streq0(c->name, "int")) return fcFmt(g->c, "(%s.kind == K_Int && %s.reserved == 0)", value, value);
    if (streq0(c->name, "float")) return fcFmt(g->c, "%s.kind == K_Float", value);
    if (streq0(c->name, "bool")) return fcFmt(g->c, "%s.kind == K_Bool", value);
    if (streq0(c->name, "char")) return fcFmt(g->c, "%s.kind == K_Char", value);
    if (streq0(c->name, "word")) return fcFmt(g->c, "%s.kind == K_Word", value);
    if (streq0(c->name, "string")) return fcFmt(g->c, "%s.kind == K_String", value);
    if (streq0(c->name, "class") || streq0(c->name, "Exception")) return fcFmt(g->c, "%s.kind == K_Class", value);
    if ((lambdaParams = fgLambdaParamsOf(c->name)) >= 0) return fcFmt(g->c, "(%s.kind == K_Lambda && lamOf(%s)->nparams == %du)", value, value, lambdaParams);
    fcSetAdd(g->isOfTypes, c->name);
    enumerable = streq0(fcPlainName(g->c, c->name), "IEnumerable") ? fcFmt(g->c, "%s.kind == K_Array || %s.kind == K_Buffer || %s.kind == K_String || ", value, value, value) : "";
    return fcFmt(g->c, "(%s%s(%s))", enumerable, IsOfFn(g, c->name), value);
}

/* ArrayOfTypeArg (DeclaredType): the element of an array of this type argument and the text of a class; false: none */
static bool ArrayOfTypeArg(FgGen* g, const char* typeArg, int* elem, const char** text) {
    int code;
    const FcEnumType* et;
    if (!typeArg || !*typeArg || typeArg[0] == '$' || fcStartsWith(typeArg, "in:")) return false;
    if ((code = fgTypedElementOf(typeArg)) != 0) { *elem = code & 0xFF; *text = NULL; return true; }
    if (fgNumberDemand(g->c, typeArg, NULL, NULL) || streq0(typeArg, "bool") || streq0(typeArg, "char") || streq0(typeArg, "word")) return false;
    if (ProgramEnumKnown(g, typeArg, &et)) { *elem = AE_Class; *text = fcFmt(g->c, "%s\x02%s", fcDtOfName(g->c, et->name), et->key); return true; }
    *elem = AE_Class;
    *text = fcDtOfName(g->c, typeArg);
    return true;
}

/* The TArg of a type argument text (`&ta_N`; NULL for "": not known). */
static const char* TArgRef(FgGen* g, const char* text) {
    void* v;
    int id;
    if (!*text) return "NULL";
    if (fcDictTry(g->targIds, text, &v)) id = FC_VALI(v) - 1;
    else {
        int elem;
        const char* elemText;
        id = fcDictCount(g->targIds);
        fcDictPut(g->targIds, fcStr(g->c, text), FC_IVAL(id + 1));
        HoldsExpr(g, "v", fgConditionOf(g->c, g->condCache, text));
        if (ArrayOfTypeArg(g, text, &elem, &elemText) && elemText) ArrClassId(g, elemText);
    }
    return fcFmt(g->c, "&%sta_%d", Px, id);
}

/* The object of the running method as an Obj* (NULL outside of a method). */
static const char* SelfObj(const FgFunc* f) { return fgHasSelf(f) ? "(self.kind == K_Class ? objAt(self) : NULL)" : "NULL"; }

/* `T is of X` (OpCode.TypeParamIsOf): the names of the types that are X */
static const char* TypeIsOfTable(FgGen* g, const char* target) {
    void* v;
    int id;
    if (fcDictTry(g->typeIsOfIds, target, &v)) id = FC_VALI(v) - 1;
    else { id = fcDictCount(g->typeIsOfIds); fcDictPut(g->typeIsOfIds, fcStr(g->c, target), FC_IVAL(id + 1)); }
    return fcFmt(g->c, "%stio_%d", Px, id);
}
static FcList* TypeIsOfNames(FgGen* g, const char* target) {
    const char* wanted = fcPlainName(g->c, (const char*)fgSplit(g->c, target, '<')->items[0]);
    FcDict* names = fcDictNew(g->c, FC_KEY_STR);
    fcSetAdd(names, wanted);
    FC_DICT_EACH(g->program->program->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        const FcRtClass* r;
        for (r = rc; r; r = r->base) {
            bool hit = streq0(fcPlainName(g->c, r->name), wanted);
            int i;
            for (i = 0; i < r->interfaces->count && !hit; i++) if (streq0(fcPlainName(g->c, (const char*)r->interfaces->items[i]), wanted)) hit = true;
            if (hit) { fcSetAdd(names, fcPlainName(g->c, rc->name)); break; }
        }
    }
    return fgSortedKeys(g->c, names, true);
}

/* The C++ expression (TArgs) of the type arguments of a SetTypeArgs text */
static const char* TArgsExpr(FgGen* g, FgFunc* f, const char* text) {
    FcList* parts = fgSplit(g->c, text, '|');
    FcBuf b = {0};
    int i;
    if (!strchr(text, '$')) {
        void* v;
        int id;
        if (fcDictTry(g->targListIds, text, &v)) id = FC_VALI(v) - 1;
        else {
            for (i = 0; i < parts->count; i++) TArgRef(g, (const char*)parts->items[i]);
            id = fcDictCount(g->targListIds);
            fcDictPut(g->targListIds, fcStr(g->c, text), FC_IVAL(id + 1));
        }
        return fcFmt(g->c, "%stl_%d", Px, id);
    }
    fcBufFmt(&b, "internTArgsV(%d", parts->count);
    for (i = 0; i < parts->count; i++) {
        const char* t = (const char*)parts->items[i];
        const char* part;
        fcBufStr(&b, ", ");
        if (t[0] != '$') part = TArgRef(g, t);
        else {
            int at = fgIndexOf(t, '@');
            int k = atoi(t + 1);
            if (at == (int)strlen(t) - 1) part = f->hasCallTArgs ? fcFmt(g->c, "(ctargs ? ctargs[%d] : NULL)", k) : "NULL";
            else part = fgHasSelf(f) ? TArgOfExpr(g, t + at + 1, k, SelfObj(f)) : "NULL";
        }
        fcBufStr(&b, part);
    }
    fcBufChar(&b, ')');
    return fcBufDone(g->c, &b);
}

/* A local array with its elements, C89 style (`T a[n]; a[0] = x; ...`). */
static const char* CArray(FgGen* g, const char* type, const char* name, FcList* items) {
    FcBuf b = {0};
    int i;
    fcBufFmt(&b, "%s %s[%d]; ", type, name, items->count > 1 ? items->count : 1);
    for (i = 0; i < items->count; i++) fcBufFmt(&b, "%s[%d] = %s; ", name, i, (const char*)items->items[i]);
    return fcBufDone(g->c, &b);
}

/* The type arguments' tables: a TArg per text, the constant lists and the `targOf_` functions. */
static int fgCmpTargOf(const void* a, const void* b) { return fgCmpNameArgc(a, b); }
static int fgCmpIdValue(const void* a, const void* b) {
    const FcDictEntry* x = *(const FcDictEntry* const*)a;
    const FcDictEntry* y = *(const FcDictEntry* const*)b;
    int vx = FC_VALI(x->value), vy = FC_VALI(y->value);
    return vx < vy ? -1 : vx > vy;
}
/* the entries of a dictionary text -> id + 1, ordered by the id (OrderBy(kv => kv.Value)) */
static FcList* fgByValue(FgGen* g, const FcDict* d) {
    FcList* l = fcListNew(g->c);
    FC_DICT_EACH(d, e) fcListAdd(g->c, l, e);
    if (l->count > 1) qsort(l->items, (size_t)l->count, sizeof(void*), fgCmpIdValue);
    return l;
}
static const char* TArgTables(FgGen* g, bool withTargOf) {
    FcBuf targOfCode = {0};
    FcBuf sb = {0};
    FcList* targOfs;
    FcList* byId;
    int i;
    fcBufStr(&targOfCode, "");
    fcBufStr(&sb, "");
    targOfs = fcListNew(g->c);
    if (withTargOf) { FC_DICT_EACH(g->targOfs, e) fcListAdd(g->c, targOfs, e->value); }
    if (targOfs->count > 1) qsort(targOfs->items, (size_t)targOfs->count, sizeof(void*), fgCmpTargOf);
    for (i = 0; i < targOfs->count; i++) {
        FgNameArgc* t = (FgNameArgc*)targOfs->items[i];
        const char* cls = t->name;
        int k = t->argc, ci;
        FcList* caseIds = fcListNew(g->c);
        FcList* caseExprs = fcListNew(g->c);
        FcList* groups = fcListNew(g->c);
        if (Modular) { fcBufStr(&targOfCode, ModTArgOf(g, cls, k)); continue; }
        for (ci = 0; ci < g->classList->count; ci++) {
            FgClassInfo* info = (FgClassInfo*)g->classList->items[ci];
            FcList* args = NULL;
            const FcRtClass* rc = info->rc;
            const char* token;
            const char* expr;
            while (rc && !streq0(rc->name, cls)) {
                FcList* given = rc->baseTypeArgs ? rc->baseTypeArgs : fcListNew(g->c);
                FcList* outer = args;
                int j;
                args = fcListNew(g->c);
                for (j = 0; j < given->count; j++) {
                    const char* tt = (const char*)given->items[j];
                    if (tt[0] != '$' || !outer) fcListAdd(g->c, args, (void*)tt);
                    else { int jj = atoi(tt + 1); fcListAdd(g->c, args, (void*)(jj < outer->count ? (const char*)outer->items[jj] : "")); }
                }
                rc = rc->base;
            }
            if (!rc) continue;
            token = !args ? fcFmt(g->c, "$%d", k) : k < args->count ? (const char*)args->items[k] : "";
            expr = !*token ? "NULL" : token[0] == '$' ? fcFmt(g->c, "(objTArgs(o) ? objTArgs(o)[%s] : NULL)", token + 1) : TArgRef(g, token);
            fcListAdd(g->c, caseIds, FC_IVAL(info->id));
            fcListAdd(g->c, caseExprs, (void*)expr);
            if (!fcListHasStr(groups, expr)) fcListAdd(g->c, groups, (void*)expr);
        }
        fcBufFmt(&targOfCode, "FIRE_MAYBE_UNUSED %sinline const TArg* targOf_%s_%d(Obj* o) {\n", HelperStorage, Mangle(g, cls), k);
        fcBufStr(&targOfCode, "    if (!o) return NULL;\n");
        fcBufStr(&targOfCode, "    switch (o->cls) {\n");
        for (ci = 0; ci < groups->count; ci++) {
            int m;
            fcBufStr(&targOfCode, "        ");
            for (m = 0; m < caseIds->count; m++) if (streq0((const char*)caseExprs->items[m], (const char*)groups->items[ci])) fcBufFmt(&targOfCode, "case %d: ", FC_VALI(caseIds->items[m]));
            fcBufFmt(&targOfCode, "return %s;\n", (const char*)groups->items[ci]);
        }
        fcBufStr(&targOfCode, "        default: return NULL;\n");
        fcBufStr(&targOfCode, "    }\n");
        fcBufStr(&targOfCode, "}\n");
    }
    byId = fgByValue(g, g->targIds);
    for (i = 0; i < byId->count; i++) {
        FcDictEntry* e = (FcDictEntry*)byId->items[i];
        const char* text = (const char*)e->key;
        int id = FC_VALI(e->value) - 1, arrElem = 0, arrClass = 0, elemOfArg;
        const FgCond* condition = fgConditionOf(g->c, g->condCache, text);
        const char* holds = HoldsExpr(g, "v", condition);
        const char* kind = "0";
        const char* adapt = "NULL";
        const char* nk;
        const char* nd;
        const char* elemText;
        const char* isA;
        if (fgNumberDemand(g->c, text, &nk, &nd)) {
            const char* demand = fgTrimStart(nd, '#');
            kind = streq0(nk, "int") ? "K_Int" : "K_Float";
            if (fcStartsWith(demand, "any ")) holds = fcFmt(g->c, "%s && unitCompatible(unitOf(v), %s)", holds, UnitC(g, UnitId(g, DemandUnit(g, demand + 4))));
            else if (!streq0(demand, "any")) holds = fcFmt(g->c, "%s && v.unit == %s", holds, UnitC(g, UnitId(g, DemandUnit(g, demand))));
            if (!streq0(nd, "any")) {
                fcBufFmt(&sb, "static Value %staa_%d(Value v) { return %s; }\n", Px, id, AdaptUnitCall(g, "v", nd));
                adapt = fcFmt(g->c, "%staa_%d", Px, id);
            }
        }
        fcBufFmt(&sb, "static bool %stah_%d(Value v) { return %s; }\n", Px, id, holds);
        if (ArrayOfTypeArg(g, text, &elemOfArg, &elemText)) { arrElem = elemOfArg + 1; arrClass = elemText ? ArrClassId(g, elemText) : 0; }
        isA = IsANames(g, text, fcFmt(g->c, "%stai_%d", Px, id), &sb);
        fcBufFmt(&sb, "static const TArg %sta_%d = {%s, %stah_%d, %s, %s, %s, %d, %d, %s%s};\n", Px, id, CString(g, fgDisplayOf(g->c, text)), Px, id,
            fcStartsWith(text, "float") && fgNumberDemand(g->c, text, NULL, NULL) ? "true" : "false", kind, adapt, arrElem, arrClass, isA, ArrClassMap(g, arrClass));
    }
    byId = fgByValue(g, g->targListIds);
    for (i = 0; i < byId->count; i++) {
        FcDictEntry* e = (FcDictEntry*)byId->items[i];
        FcList* parts = fgSplit(g->c, (const char*)e->key, '|');
        FcList* refs = fcListNew(g->c);
        int k;
        for (k = 0; k < parts->count; k++) fcListAdd(g->c, refs, (void*)TArgRef(g, (const char*)parts->items[k]));
        fcBufFmt(&sb, "static const TArg* const %stl_%d[] = {%s};\n", Px, FC_VALI(e->value) - 1, fgJoin(g->c, refs, ", "));
    }
    byId = fgByValue(g, g->typeIsOfIds);
    for (i = 0; i < byId->count; i++) {
        FcDictEntry* e = (FcDictEntry*)byId->items[i];
        FcList* names = TypeIsOfNames(g, (const char*)e->key);
        FcList* cs = fcListNew(g->c);
        int k;
        for (k = 0; k < names->count; k++) fcListAdd(g->c, cs, (void*)CString(g, (const char*)names->items[k]));
        fcBufFmt(&sb, "static const char* const %stio_%d[] = {%s, NULL};\n", Px, FC_VALI(e->value) - 1, fgJoin(g->c, cs, ", "));
    }
    fcBufStr(&sb, fcBufDone(g->c, &targOfCode));
    return fcBufDone(g->c, &sb);
}

/* The type of an array of a class or an interface (`Dog pets[]`); -1 for an array of strings, of an enum, of lambdas or of a type without an object type. */
static int ClassArrType(FgGen* g, const char* text) {
    const char* spec = fgAeClassText(g->c, text);
    const FcRtClass* rc = DeclaredClass(g, spec);
    int elem;
    if (rc) elem = ObjType(g, rc);
    else { int ofInterface = InterfaceType(g, spec); if (ofInterface == -1) return -1; elem = ofInterface & ~NullableBit; }
    return TypeOf(KindArr, AE_Class | (UnitOfType(elem) + 1) << 8) | NullableBit;
}
/* What an element read from an array of this type is (ClassArrType): an object of the class or undefined; -1 for any other type. */
static int ClassArrElemType(int arrType) {
    int index;
    if (!(BaseKindOfType(arrType) == KindArr && (arrType & TypeNone) == 0 && (UnitOfType(arrType) & 0xFF) == AE_Class)) return -1;
    index = UnitOfType(arrType) >> 8;
    return index > 0 ? TypeOf(KindObj, index - 1) | NullableBit : -1;
}
static const FcRtClass* DeclaredClass(FgGen* g, const char* spec) {
    const char* name = fgSpecSingleName(FG_SPEC(spec));
    return name ? fgClassOf(g, name) : NULL;
}
/* The names a declaration tests with `is of` (each gets an `isof_` function). */
static FcList* IsOfNames(FgGen* g, const char* spec) {
    FgSpec* s = FG_SPEC(spec);
    FcList* l = fcListNew(g->c);
    int a, i;
    for (a = 0; a < s->altCount; a++)
        for (i = 0; i < s->alts[a].count; i++) {
            const FgCond* c = s->alts[a].conds[i];
            const char* n = c->name;
            if (c->isUnit || streq0(n, "int") || streq0(n, "float") || streq0(n, "bool") || streq0(n, "char") || streq0(n, "word") || streq0(n, "string") || streq0(n, "class") || streq0(n, "Exception")) continue;
            fcListAdd(g->c, l, (void*)n);
        }
    return l;
}
/* Does a value of this static type pass the check of a declaration as it is? */
static bool FitsClassTypeCore(FgGen* g, int type, const char* spec) {
    FgSpec* s = FG_SPEC(spec);
    const char* iface;
    const FcRtClass* rc;
    if (type == TypeUndefined) return true;
    if (!(s->typeParamIndex < 0 && s->typeArgs == NULL && BaseKindOfType(type) == KindObj && (type & TypeNone) == 0)) return false;
    if ((iface = InterfaceOfType(g, type)) != NULL) return streq0(fgSpecSingleName(s), iface);
    return (rc = DeclaredClass(g, spec)) != NULL && ClassIs(ObjClass(g, type), rc->name);
}
static bool FitsClassType(FgGen* g, int type, const char* spec) {
    return FG_SPEC(spec)->nonNull ? !IsNullable(type) && type != TypeUndefined && FitsClassTypeCore(g, type, spec) : FitsClassTypeCore(g, type, spec);
}

/* The static return type of a generated function (joined by its `return` instructions; constructors return the object). */
static const char* ReturnKey(FgGen* g, const FgFunc* f) { return fcFmt(g->c, "ret:%s", f->name); }
static int ReturnType(FgGen* g, const FgFunc* f) {
    int type;
    if (f->kind == FK_Ctor || f->external) return TypeUnknown;
    return fgVarTypeTry(g, ReturnKey(g, f), &type) ? type : TypeNone;
}

static FcList* ExtensionMethods(FgGen* g, const char* name, int argc);
static int BuiltinMethodsCount(const char* name, int argc);
/* The static type of what a dispatcher call `x.name(...)` gives */
static int DispatchResultType(FgGen* g, const char* name, int argc) {
    int result = INT32_MIN, i, k;
    if (LibraryRole || g->usesActors || ExtensionMethods(g, name, argc)->count > 0 || BuiltinMethodsCount(name, argc) > 0 || OwnMethodIdKind(name, argc) != 0
        || (streq0(name, "GetEnumerator") && argc == 0)) return TypeUnknown;
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
        FcList* methods = MethodsFor(g, cls->rc, name, argc);
        for (k = 0; k < methods->count; k++) {
            FcProto* method = (FcProto*)methods->items[k];
            FgFunc* target;
            int type;
            if (method->isStatic && !FG_OVERLOADED(cls->rc, name, argc)) continue;
            if (!(target = (FgFunc*)fcDictGet(g->funcByProto, method))) return TypeUnknown;
            type = ReturnType(g, target);
            if (type == TypeUnknown) return TypeUnknown;
            result = result != INT32_MIN ? JoinTypes(result, type) : type;
            if (result == TypeUnknown) return TypeUnknown;
        }
    }
    return result == INT32_MIN ? TypeUnknown : result;
}

/* A whole number the program's int holds as it is. */
static bool FitsProgramInt(FgGen* g, int64_t v) { return g->program->intWidth >= 64 || (g->program->intWidth == 32 && v >= INT32_MIN && v <= INT32_MAX); }
/* The type a constant gives a stack slot (constants carry no bit width in the generated code). */
static int TypeOfConstant(FgGen* g, const FcConst* c) {
    switch (c->kind) {
        case CK_Int: return TypeOf(KindInt, UnitId(g, c->unit)) | (c->wide == 0 && c->en == NULL && FitsProgramInt(g, c->i) ? NarrowBit : 0);
        case CK_Float: return TypeOf(KindFloat, UnitId(g, c->unit));
        case CK_Bool: return TypeOf(KindBool, 0);
        case CK_Word: return TypeOf(KindWord, 0);
        case CK_Undefined: return TypeUndefined;
        default: return TypeUnknown;
    }
}

static bool IsNarrowIntType(int type) { return KindOfType(type) == KindInt && (type & NarrowBit) != 0; }
static const char* ScalarCType(int kind) { return kind == KindBool ? "bool" : kind == KindWord ? "FireWord" : kind == KindFloat ? "Real" : "FireInt"; }
/* A variable that is a C FireInt (Ebene 2) */
static bool ScalarVarOf(FgGen* g, FgFunc* f, const char* v);
static const char* ScalarVarCType(FgGen* g, FgFunc* f, const char* v) {
    int type;
    return fgVarTypeTry(g, VarKey(g, f, v), &type) ? ScalarCType(KindOfType(type)) : "FireInt";
}

/* ---- Whole program ---- */
/* ExtendableKinds: (the BaseTypeExtensions class, the C kind) */
static const char* const ExtendableKinds[][2] = { {"$string", "K_String"}, {"$char", "K_Char"}, {"$int", "K_Int"}, {"$float", "K_Float"}, {"$bool", "K_Bool"}, {"$array", "K_Array"} };

/* The C type of a parameter or result of an `extern` function */
static const char* ExternCType(FgGen* g, const FcTypeRef* t, const char* name) {
    if (t->pointerDepth > 0) return "void*";
    if (streq0(t->baseName, "bool")) return "bool";
    if (streq0(t->baseName, "int")) return t->bitWidth == 8 ? "int8_t" : t->bitWidth == 16 ? "int16_t" : t->bitWidth == 32 ? "int32_t" : t->bitWidth == 64 ? "int64_t" : "intptr_t";
    if (streq0(t->baseName, "float")) return t->bitWidth == 32 ? "float" : "double";
    if (streq0(t->baseName, "char")) return "fire_char16";
    if (streq0(t->baseName, "string")) return "const char*";
    FG_NOTSUP("extern function '%s': the type '%s' cannot be passed to C (supported: bool/int/float/char/string/pointer)", name, t->baseName);
}

/* ---- `inline c` (ROADMAP 13.2) ---- */
static bool IsInlineBytes(const FcTypeRef* t) { return streq0(t->baseName, "int") && t->bitWidth == 8 && t->pointerDepth == 0 && t->arrayRank == 1; }
static const char* InlineCType(FgGen* g, const FcTypeRef* t) {
    if (t->pointerDepth > 0) return "void*";
    if (IsInlineBytes(t)) return "uint8_t*";
    if (streq0(t->baseName, "int")) return t->bitWidth == 8 || t->bitWidth == 16 || t->bitWidth == 32 || t->bitWidth == 64 ? fcFmt(g->c, "%sint%d_t", t->isUnsigned ? "u" : "", t->bitWidth) : "FireInt";
    if (streq0(t->baseName, "float")) return t->bitWidth == 32 ? "float" : t->bitWidth == 64 ? "double" : "Real";
    if (streq0(t->baseName, "bool")) return "bool";
    if (streq0(t->baseName, "char")) return "uint32_t";
    if (streq0(t->baseName, "string")) return "const char*";
    if (streq0(t->baseName, "any")) return "Value";
    FG_NOTSUP("inline C: the type '%s' has no C type", t->baseName);
}
static bool InlineReturnsNothing(const FcExternSig* sig) { return !sig->returnType || (streq0(sig->returnType->baseName, "undefined") && sig->returnType->pointerDepth == 0); }
#define LineResetMarker "//@@fire-line-reset@@"
static const char* InlineCFunctions(FgGen* g) {
    FcBuf protos = {0};
    FcBuf defs = {0};
    FcList* names = fcListCopy(g->c, g->program->program->externNames);
    FcList* order = fcListNew(g->c);
    int i, k;
    fcBufStr(&protos, "");
    fcBufStr(&defs, "");
    /* OrderBy(e => e.Key, StringComparer.Ordinal) of the ExternSignatures (names are unique) */
    for (i = 0; i < names->count; i++) fcListAdd(g->c, order, FC_IVAL(i));
    for (i = 1; i < order->count; i++)
        for (k = i; k > 0 && strcmp((const char*)names->items[FC_VALI(order->items[k - 1])], (const char*)names->items[FC_VALI(order->items[k])]) > 0; k--) {
            void* t = order->items[k]; order->items[k] = order->items[k - 1]; order->items[k - 1] = t;
        }
    for (i = 0; i < order->count; i++) {
        int at = FC_VALI(order->items[i]);
        const char* name = (const char*)names->items[at];
        const FcExternSig* sig = (const FcExternSig*)g->program->program->externSigs->items[at];
        FcList* ps;
        const char* head;
        if (!sig->inlineC) continue;
        ps = fcListNew(g->c);
        for (k = 0; k < sig->paramTypes->count; k++) {
            const FcTypeRef* t = (const FcTypeRef*)sig->paramTypes->items[k];
            const char* pn = (const char*)sig->paramNames->items[k];
            fcListAdd(g->c, ps, (void*)fcFmt(g->c, "%s %s", InlineCType(g, t), pn));
            if (IsInlineBytes(t)) fcListAdd(g->c, ps, (void*)fcFmt(g->c, "uint32_t %s_length", pn));
        }
        head = fcFmt(g->c, "static FIRE_MAYBE_UNUSED %s fire_inl_%s(%s)", InlineReturnsNothing(sig) ? "void" : InlineCType(g, sig->returnType), Mangle(g, name), ps->count == 0 ? "void" : fgJoin(g->c, ps, ", "));
        fcBufFmt(&protos, "%s;\n", head);
        fcBufFmt(&defs, "%s {   /* inline c %s */\n", head, name);
        fcBufFmt(&defs, "#line %d %s\n", sig->inlineLine, LineFileLiteral(g, sig->inlineSource));
        fcBufStr(&defs, sig->inlineC);
        fcBufChar(&defs, '\n');
        fcBufStr(&defs, LineResetMarker "\n");
        fcBufStr(&defs, "}\n");
    }
    return fcFmt(g->c, "/* ---- inline c: the functions with a C body (called as fire_inl_<name>) ---- */\n%s%s", fcBufDone(g->c, &protos), fcBufDone(g->c, &defs));
}

/* ---- Access modifiers (SPEC 5.7) ---- */
static const char* DescribeAccess(int access) { return access == ACC_Private ? "private" : access == ACC_Protected ? "protected" : "public"; }
/* May code of the class `caller` (NULL: top-level code) use a member that `declaring` declares with this access? */
static bool AccessAllowed(const FcRtClass* caller, const FcRtClass* declaring, int access) {
    const FcRtClass* rc;
    if (access == ACC_Public) return true;
    if (!caller) return false;
    if (access == ACC_Private) return streq0(caller->name, declaring->name);
    for (rc = caller; rc; rc = rc->base) if (streq0(rc->name, declaring->name)) return true;
    return false;
}
/* The class of the code of this function as a number for the checks at run time (no class: all ones). */
static const char* CallerId(FgGen* g, FgFunc* f) { return f->chunk->ownerClass && CheckAccess ? ClassIdExpr(g, RegisterClass(g, f->chunk->ownerClass->name)) : "0xFFFFFFFFu"; }
static bool RestrictedMethodName(FgGen* g, const char* name) {
    void* known;
    bool restricted = false;
    if (!CheckAccess) return false;
    if (fcDictTry(g->restrictedMethods, name, &known)) return FC_VALI(known) == 1;
    FC_DICT_EACH(g->program->program->classes, e) {
        FcList* list = (FcList*)fcDictGet(((FcRtClass*)e->value)->methods, name);
        int i;
        if (list) for (i = 0; i < list->count && !restricted; i++) { int a = ((FcProto*)list->items[i])->access; if (a >= 0 && a != ACC_Public) restricted = true; }
        if (restricted) break;
    }
    fcDictPut(g->restrictedMethods, fcStr(g->c, name), FC_IVAL(restricted ? 1 : 2));
    return restricted;
}
/* Is some field (or property accessor) of this name private or protected somewhere? */
static bool RestrictedField(FgGen* g, const char* name) {
    void* known;
    bool restricted = false;
    if (!CheckAccess) return false;
    if (fcDictTry(g->restrictedFields, name, &known)) return FC_VALI(known) == 1;
    FC_DICT_EACH(g->program->program->classes, e) { FcFieldInfo* info = fgOwnField((FcRtClass*)e->value, name); if (info && info->access != ACC_Public) { restricted = true; break; } }
    if (!restricted) restricted = RestrictedMethodName(g, fcFmt(g->c, "get_%s", name)) || RestrictedMethodName(g, fcFmt(g->c, "set_%s", name));
    fcDictPut(g->restrictedFields, fcStr(g->c, name), FC_IVAL(restricted ? 1 : 2));
    return restricted;
}
/* The condition (over `caller`) under which the member may be used, empty when everybody may. */
static const char* AllowedExpr(FgGen* g, const FcRtClass* declaring, int access) {
    FcList* ids;
    int i;
    if (access == ACC_Public) return "";
    /* (with modules a caller may be a class of another module: private is the declaring class, protected a class derived from it - asked at run time) */
    if (Modular) return access == ACC_Private ? fcFmt(g->c, "(caller == %s)", ClassIdExpr(g, RegisterClass(g, declaring->name))) : fcFmt(g->c, "fireClassIsSub(caller, %s)", CString(g, declaring->name));
    ids = fcListNew(g->c);
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* c = (FgClassInfo*)g->classList->items[i];
        if (AccessAllowed(c->rc, declaring, access)) fcListAdd(g->c, ids, (void*)fcFmt(g->c, "caller == %d", c->id));
    }
    return ids->count == 0 ? "false" : fcFmt(g->c, "(%s)", fgJoin(g->c, ids, " || "));
}
static bool FieldHasList(FgGen* g, const char* name) { return Modular || HasProperty(g, name); }
static bool FieldHasCaller(FgGen* g, const char* name) { return Modular || RestrictedField(g, name); }
/* The extra arguments of a field helper: the scope list when a class has a property of that name, the caller's class when the name is restricted. */
static const char* FieldTail(FgGen* g, const char* name, FgFunc* f, const char* list) {
    return fcFmt(g->c, "%s%s", FieldHasList(g, name) ? fcFmt(g->c, ", %s", list) : "", FieldHasCaller(g, name) ? fcFmt(g->c, ", %s", CallerId(g, f)) : "");
}

/* Is the function a method of a base-type extension (`class extends string`)? */
static bool IsExtensionMethod(FgGen* g, const FcProto* proto) {
    size_t k;
    if (!proto) return false;
    if (!g->extensionProtos) {
        g->extensionProtos = fcDictNew(g->c, FC_KEY_PTR);
        for (k = 0; k < sizeof ExtendableKinds / sizeof ExtendableKinds[0]; k++) {
            FcRtClass* rc = fgClassOf(g, ExtendableKinds[k][0]);
            if (!rc) continue;
            FC_DICT_EACH(rc->methods, m) { FcList* l = (FcList*)m->value; int i; for (i = 0; i < l->count; i++) fcSetAdd(g->extensionProtos, l->items[i]); }
        }
    }
    return fcDictHas(g->extensionProtos, proto);
}
/* The methods of a base-type extension for this name, per kind of receiver. */
typedef struct FgExtension { const char* cpp; FcProto* proto; const FcRtClass* rc; } FgExtension;
static FcList* ExtensionMethods(FgGen* g, const char* name, int argc) {
    FcList* l = fcListNew(g->c);
    size_t k;
    for (k = 0; k < sizeof ExtendableKinds / sizeof ExtendableKinds[0]; k++) {
        FcRtClass* rc = fgClassOf(g, ExtendableKinds[k][0]);
        FcProto* proto;
        FgExtension* x;
        if (!rc || !(proto = FG_FIND(rc, name, argc).proto)) continue;
        x = (FgExtension*)fcAlloc(g->c, sizeof *x);
        x->cpp = ExtendableKinds[k][1]; x->proto = proto; x->rc = rc;
        fcListAdd(g->c, l, x);
    }
    return l;
}

/* The runtime enum of a built-in ownership method (SPEC 2.2), or NULL. */
static const char* OwnMethodId(const char* name, int argc) {
    const char* n = fcStartsWith(name, "try") ? name + 3 : name;
    if (streq0(n, "TakeLocal") && (argc == 0 || argc == 1)) return "OM_TakeLocal";
    if (streq0(n, "TakeUpwards") && (argc == 0 || argc == 1)) return "OM_TakeUpwards";
    if (streq0(n, "TakeGlobal") && argc >= 0 && argc <= 2) return "OM_TakeGlobal";
    if (streq0(n, "TakeTo") && (argc == 1 || argc == 2)) return "OM_TakeTo";
    return NULL;
}
static int OwnMethodIdKind(const char* name, int argc) { return OwnMethodId(name, argc) ? 1 : 0; }
/* Has the class (or a base) a field, a property accessor or a method of this name (VM.ReflectHas)? */
static bool ClassHasMember(FgGen* g, const FcRtClass* rc, const char* name) {
    if (fcListHasStr(FG_NAMES(rc), name) || fgFindMethod(g->c, g->rtCands, rc, fcFmt(g->c, "get_%s", name), 0) || fgFindMethod(g->c, g->rtCands, rc, fcFmt(g->c, "set_%s", name), 1)) return true;
    return fgHasMethodNamed(rc, name);
}
static bool OwnMethodIsTry(const char* name) { return fcStartsWith(name, "tryTake"); }
/* The ownership method takes the target as its first argument (`TakeTo(obj)`), and optionally a `Takes` mode after it. */
static int OwnMethodBaseArgs(const char* ownId) { return streq0(ownId, "OM_TakeTo") ? 1 : 0; }
static bool AnyClassHasMethod(FgGen* g, const char* name, int argc) {
    int i;
    if (LibraryRole) {
        /* (a library: the classes it sees - an ownership method of a class of the program goes unnoticed) */
        FC_DICT_EACH(g->program->program->classes, e) if (FG_FIND((FcRtClass*)e->value, name, argc).proto) return true;
        return false;
    }
    for (i = 0; i < g->classList->count; i++) if (FG_FIND(((FgClassInfo*)g->classList->items[i])->rc, name, argc).proto) return true;
    return false;
}

/* ---- the natives of the packages ---- */
#define FG_UNPORTED(...) fcThrow(g->c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(g->c, "unported: " __VA_ARGS__))
typedef struct FgNativeThunk { int index, argc; bool needsList; } FgNativeThunk;
/* The name of the C function that calls the C++ native `cppFunction`. */
static const char* NativeThunk(FgGen* g, const char* cppFunction, int argc, bool needsList) {
    FgNativeThunk* t = (FgNativeThunk*)fcDictGet(g->nativeThunks, cppFunction);
    if (!t) {
        t = (FgNativeThunk*)fcAlloc(g->c, sizeof *t);
        t->index = fcDictCount(g->nativeThunks); t->argc = argc; t->needsList = needsList;
        fcDictPut(g->nativeThunks, cppFunction, t);
    }
    return fcFmt(g->c, "fire_nat_%s%d", Px, t->index);
}
/* The parameters of a thunk (`Value a0, ..., OwnList* list`). */
static const char* ThunkParams(FgGen* g, int argc, bool needsList) {
    FcList* l;
    int i;
    if (argc == 0 && !needsList) return "void";
    l = fcListNew(g->c);
    for (i = 0; i < argc; i++) fcListAdd(g->c, l, (void*)fcFmt(g->c, "Value a%d", i));
    if (needsList) fcListAdd(g->c, l, (void*)"OwnList* list");
    return fgJoin(g->c, l, ", ");
}
/* (the packages of the program: what CollectPackages finds) */
typedef struct FgPkgImportKey { FgInstImport* import; const char* key; } FgPkgImportKey;
typedef struct FgPkgNative { int argc; const char* function; bool needsList, returnsReference, host, throws, isC; const char* returns; FgPkgFn* fn; } FgPkgNative;
/* the keys of the platform of the target: its platform package and its name */
static FcList* fgPlatformKeys(FgGen* g) { FcList* l = fcListNew(g->c); fcListAdd(g->c, l, (void*)g->target->platform); fcListAdd(g->c, l, (void*)g->target->name); return l; }
/* The lock of the package of a native in a program with fire threads: its index in `fire_pkgM`, -1 for none (no threads, or `"threadSafe": true`). */
static int PkgLock(FgGen* g, const char* native) {
    const char* key;
    void* v;
    int i;
    if (!g->usesThreads || Modular || !(key = (const char*)fcDictGet(g->nativeImportOf, native))) return -1;
    for (i = 0; i < g->packageImports->count; i++) {
        FgPkgImportKey* p = (FgPkgImportKey*)g->packageImports->items[i];
        if (streq0(p->key, key)) { if (p->import->native && p->import->native->threadSafe) return -1; break; }
    }
    if (fcDictTry(g->pkgLocks, key, &v)) return FC_VALI(v) - 1;
    fcDictPut(g->pkgLocks, key, FC_IVAL(fcDictCount(g->pkgLocks) + 1));
    g->version++;
    return fcDictCount(g->pkgLocks) - 1;
}
/* Looks at the imports of packages the program uses: their native functions, and whether the target is one the C++ source is written for. */
static void CollectPackages(FgGen* g, FcStore* store) {
    FcDict* keySet = fcDictNew(g->c, FC_KEY_STR);
    FcList* keys;
    FcList* platformKeys = fgPlatformKeys(g);
    int i, k;
    FC_DICT_EACH(g->program->nativeImports, e) if (fcStartsWith((const char*)e->key, "pkg:")) fcSetAdd(keySet, e->key);
    keys = fgSortedKeys(g->c, keySet, true);
    for (i = 0; i < keys->count; i++) {
        const char* key = (const char*)keys->items[i];
        FcPkgImport* pi = fcStoreFindKey(g->c, store, key);
        FgInstImport* import;
        FgPkgImportKey* pk;
        FgPkgNativeM* native;
        if (!pi) FG_NOTSUP("the import '%s' belongs to a package that is not installed (`ember list`)", key + 4);
        import = fgInstalledImport(g->c, g->instImports, pi);
        pk = (FgPkgImportKey*)fcAlloc(g->c, sizeof *pk);
        pk->import = import; pk->key = key;
        fcListAdd(g->c, g->packageImports, pk);
        native = import->native;
        if (!native) continue;
        if (!fgSupportsAny(native, platformKeys))
            FG_NOTSUP("the package '%s' (import '%s') has native code for %s, not for the platform '%s' of the target '%s'", import->pkgName, import->name, fgJoin(g->c, native->platforms, ", "), g->target->platform, g->target->name);
        if (fgSourcesFor(g->c, native, platformKeys, false)->count == 0)
            FG_NOTSUP("the package '%s' (import '%s') has no C++ source for the target '%s' (platform '%s')", import->pkgName, import->name, g->target->name, g->target->platform);
        for (k = 0; k < native->functions->count; k++) {
            FgPkgFn* fn = (FgPkgFn*)native->functions->items[k];
            if (fn->parameters && (!native->isC || fn->parameters->count != fn->arguments))
                FG_NOTSUP("the native '%s' of the package '%s' lists %d parameter type(s) for %d argument(s)%s", fn->name, import->pkgName, fn->parameters->count, fn->arguments, native->isC ? "" : " - typed functions (\"parameters\") are for packages in C");
            if (!fcDictHas(g->packageNatives, fn->name)) {
                FgPkgNative* pn = (FgPkgNative*)fcAlloc(g->c, sizeof *pn);
                pn->argc = fn->arguments; pn->function = fgPkgFnSymbol(fn); pn->needsList = fn->needsList; pn->returnsReference = fn->returnsReference; pn->host = fn->host;
                pn->throws = native->exceptions->count > 0; pn->isC = native->isC; pn->returns = fn->returns ? fgTrim(g->c, fn->returns) : NULL; pn->fn = fn;
                fcDictPut(g->packageNatives, fn->name, pn);
            }
            if (!fcDictHas(g->nativeImportOf, fn->name)) fcDictPut(g->nativeImportOf, fn->name, (void*)key);
        }
        for (k = 0; k < native->exceptions->count; k++) if (!fcListHasStr(g->errorClasses, (const char*)native->exceptions->items[k])) fcListAdd(g->c, g->errorClasses, native->exceptions->items[k]);
    }
}

/* BridgeOf: the natives of the display that a package declares in `native.bridge` (graphics, windows), by their name without `prefix` (-> FgPkgFn*) */
static FcDict* BridgeOf(FgGen* g, const char* import, const char* prefix) {
    const char* key = fcFmt(g->c, "bridge\x01%s", import);
    FcDict* natives = (FcDict*)fcDictGet(g->bridgeCache, key);
    FcPkgImport* pi;
    FgInstImport* found;
    int i;
    if (natives) return natives;
    natives = fcDictNew(g->c, FC_KEY_STR);
    if ((pi = fcStoreFindImport(g->c, g->store, import)) != NULL && (found = fgInstalledImport(g->c, g->instImports, pi))->native)
        for (i = 0; i < found->native->bridge->count; i++) {
            FgPkgFn* fn = (FgPkgFn*)found->native->bridge->items[i];
            if (!fcStartsWith(fn->name, prefix)) continue;
            fcDictPut(natives, fn->name + strlen(prefix), fn);
        }
    fcDictPut(g->bridgeCache, key, natives);
    return natives;
}
/* The C of an import the compiler knows itself (`graphics`, `windows`): the source of the standard package, put into the generated file like that of any package. */
static void AppendStandardNatives(FgGen* g, FcBuf* sb, const char* import) {
    FgInstImport* found = NULL;
    FcList* texts;
    int i;
    fcSetAdd(g->standardNativesWritten, fcAsciiLower(g->c, import));
    if (!g->store->loaded) fcStoreLoad(g->c, g->store);
    for (i = 0; i < g->store->imports->count && !found; i++) {
        FcPkgImport* pi = (FcPkgImport*)g->store->imports->items[i];
        FgInstImport* x;
        if (!fcAsciiIEq(pi->name, import)) continue;
        x = fgInstalledImport(g->c, g->instImports, pi);
        if (x->native) found = x;
    }
    if (!found) FG_NOTSUP("the standard package 'fire-%s' with the C of `#import \"%s\"` is not installed (start ember or spark once)", import, import);
    texts = fgReadNativeSources(g->c, g->instImports, g->store, found, fgPlatformKeys(g));
    for (i = 0; i < texts->count; i++) {
        FgNamedText* t = (FgNamedText*)texts->items[i];
        fcBufFmt(sb, "// ---- %s %s, import \"%s\": %s\n", found->pkgName, found->pkgVersion, import, t->name);
        fcBufFmt(sb, "%s\n", t->text);
        fcBufFmt(sb, "// ---- end of %s\n", t->name);
    }
}

/* ---- units of a demand ---- */
/* Unit.Parse of a demand: a text that starts with the mark of an encoded unit (`§in§m:1§0.0254§0§§`) is that unit (Unit.DecodeDemand), every other text Unit.Parse */
#define FG_DEMAND_MARK "\xC2\xA7"
static const FcU* fgDemandUnitParse(FcCtx* c, const char* text) {
    if (fcStartsWith(text, FG_DEMAND_MARK)) {
        FcList* parts = fgSplitStr(c, text, FG_DEMAND_MARK);   /* "", symbol, dimensions, scale, offset, converter, unitless converter */
        FcUnit u;
        FcList* dims;
        int i;
        memset(&u, 0, sizeof u);
        dims = fgSplit(c, (const char*)parts->items[2], ',');
        for (i = 0; i < dims->count; i++) {
            const char* entry = (const char*)dims->items[i];
            const char* colon = strrchr(entry, ':');
            if (!*entry || !colon) continue;
            fcUnitAddDim(&u, fcStrN(c, entry, (size_t)(colon - entry)), atoi(colon + 1));
        }
        u.scale = strtod((const char*)parts->items[3], NULL);
        u.offset = strtod((const char*)parts->items[4], NULL);
        u.converter = *(const char*)parts->items[5] ? (const char*)parts->items[5] : NULL;
        u.unitlessConverter = parts->count > 6 && *(const char*)parts->items[6] ? (const char*)parts->items[6] : NULL;
        return fcUNew(c, &u, (const char*)parts->items[1]);
    }
    return fcUParse(c, text);
}
/* The unit a unit declaration names: `mm`, or of `any m` the `m` (Value.AdaptToUnit). */
static const FcU* DemandUnit(FgGen* g, const char* demand) {
    demand = fgTrimStart(demand, '#');
    return fgDemandUnitParse(g->c, fcStartsWith(demand, "any ") ? demand + 4 : demand);
}
/* Unit.DemandDisplay */
static const char* UnitDemandDisplay(FgGen* g, const char* demand) {
    demand = fgTrimStart(demand, '#');
    if (fcStartsWith(demand, "any ")) return fcFmt(g->c, "any %s", UnitDemandDisplay(g, demand + 4));
    return fcStartsWith(demand, FG_DEMAND_MARK) ? (const char*)fgSplitStr(g->c, demand, FG_DEMAND_MARK)->items[1] : demand;
}

/* The unit arguments of typeArray/newTypedArray for a unit demand (NULL: none) - and TU_DEFINED for the code of a defined array. */
static const char* TypeArrayUnitArgsOf(FgGen* g, const char* demand, int elemCode) {
    int defined = fgAeIsDefined(elemCode) ? 8 : 0;
    const char* unit;
    bool dimensionOnly;
    if (fgAeTypeOf(elemCode) == AE_Class && demand) {
        int id = ArrClassId(g, demand);
        return fcFmt(g->c, "%s, %d, %s", ArrClassC(g, id), 16 | defined, Constant(g, fcMakeString(g->c, FG_SPEC(fgAeClassText(g->c, demand))->display)));
    }
    if (!demand) return fcFmt(g->c, "0, %d, %s", defined, Constant(g, fcMakeString(g->c, "(no unit)")));
    unit = fgTrimStart(demand, '#');
    if (streq0(unit, "any")) return fcFmt(g->c, "0, %d, %s", 4 | defined, Constant(g, fcMakeString(g->c, "any")));
    dimensionOnly = fcStartsWith(unit, "any ");
    {
        const char* unitC = UnitC(g, UnitId(g, DemandUnit(g, unit)));
        return fcFmt(g->c, "%s, %d, %s", unitC, (dimensionOnly ? 3 : 1) | defined, Constant(g, fcMakeString(g->c, UnitDemandDisplay(g, unit))));
    }
}
static const char* TypeArrayUnitArgs(FgGen* g, const FcChunk* chunk, int constIdx, int elemCode) { return TypeArrayUnitArgsOf(g, constIdx == 0xFFFF ? NULL : fgConstStr(chunk, constIdx), elemCode); }

/* ArrayElem.Zero(Class, text): what an element of a defined array of a class starts with */
static const FcConst* fgArrayClassZero(FgGen* g, const char* text) {
    const char* at = strchr(text, '\x02');
    if (at) {
        const FcEnumType* et = NULL;
        int i;
        for (i = 0; i < g->program->program->enums->count; i++) if (streq0(((const FcEnumType*)g->program->program->enums->items[i])->key, at + 1)) { et = (const FcEnumType*)g->program->program->enums->items[i]; break; }
        if (et && et->names->count > 0) return fcMakeEnum(g->c, *(int64_t*)et->values->items[0], et);
    }
    return streq0(fgSpecSingleName(FG_SPEC(fgAeClassText(g->c, text))), "string") ? fcMakeString(g->c, "") : fcMakeUndefined(g->c);
}

/* The arrays of a class, a string or an enum by their DeclaredType text: the 1-based index of each in g_arrClasses. */
static int ArrClassId(FgGen* g, const char* text) {
    void* v;
    int id, a, i;
    FgSpec* spec;
    if (fcDictTry(g->arrClasses, text, &v)) return FC_VALI(v);
    id = fcDictCount(g->arrClasses) + 1;
    fcDictPut(g->arrClasses, fcStr(g->c, text), FC_IVAL(id));
    spec = FG_SPEC(fgAeClassText(g->c, text));
    for (a = 0; a < spec->altCount; a++) for (i = 0; i < spec->alts[a].count; i++) HoldsExpr(g, "v", spec->alts[a].conds[i]);
    Constant(g, fgArrayClassZero(g, text));
    g->version++;
    return id;
}

/* The names of the types a class is as a NULL-terminated table `name` written to `sb`; "NULL" for what is no class of the program. */
static const char* IsANames(FgGen* g, const char* cls, const char* name, FcBuf* sb) {
    FcRtClass* rc = fgClassOf(g, cls);
    FcDict* names;
    FcList* sorted;
    FcList* cs;
    const FcRtClass* r;
    int i;
    if (!rc) return "NULL";
    names = fcDictNew(g->c, FC_KEY_STR);
    for (r = rc; r; r = r->base) {
        fcSetAdd(names, fcPlainName(g->c, r->name));
        for (i = 0; i < r->interfaces->count; i++) fcSetAdd(names, fcPlainName(g->c, (const char*)r->interfaces->items[i]));
    }
    sorted = fgSortedKeys(g->c, names, true);
    cs = fcListNew(g->c);
    for (i = 0; i < sorted->count; i++) fcListAdd(g->c, cs, (void*)CString(g, (const char*)sorted->items[i]));
    fcBufFmt(sb, "static const char* const %s[] = {%s, NULL};\n", name, fgJoin(g->c, cs, ", "));
    return name;
}

/* The `is of` test of a program of one piece (`isof_Name`). */
static const char* IsOfFunction(FgGen* g, const char* typeName) {
    FcBuf sb = {0};
    FcList* ids = fcListNew(g->c);
    int i, enumIndex;
    for (i = 0; i < g->classList->count; i++) { FgClassInfo* c = (FgClassInfo*)g->classList->items[i]; if (ClassIs(c->rc, typeName)) fcListAdd(g->c, ids, FC_IVAL(c->id)); }
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED %sbool isof_%s(Value v) {\n", HelperStorage, Mangle(g, typeName));
    if ((enumIndex = EnumIndexNamed(g, typeName)) > 0) fcBufFmt(&sb, "    if (v.kind == K_Int) return v.reserved == %d;   // (the enum %s)\n", enumIndex, typeName);
    if (streq0(typeName, "IEnumerable")) fcBufStr(&sb, "    if (v.kind == K_Array || v.kind == K_Buffer) return true;\n");
    fcBufStr(&sb, "    if (v.kind != K_Class) return false;\n");
    fcBufStr(&sb, "    if (FIRE_UNLIKELY(!leafAlive(v))) return false;   /* (destroyed: its class is not known any more - `is of` and `!` throw before) */\n");
    if (ids->count == 0) fcBufStr(&sb, "    return false;\n");
    else {
        fcBufStr(&sb, "    switch (asObj(v)->cls) { ");
        for (i = 0; i < ids->count; i++) fcBufFmt(&sb, "case %d: ", FC_VALI(ids->items[i]));
        fcBufStr(&sb, "return true; default: return false; }\n");
    }
    fcBufStr(&sb, "}\n");
    return fcBufDone(g->c, &sb);
}

/* g_arrClasses: for each array of a class what fits, its name and what a defined one starts with. */
static const char* ArrClassTable(FgGen* g) {
    FcBuf sb = {0};
    FcList* byId;
    FcList* isA;
    FcList* rows;
    int i, a, k;
    if (fcDictCount(g->arrClasses) == 0) return "";
    fcBufStr(&sb, "");
    FC_DICT_EACH(g->arrClasses, e) {
        FgSpec* spec = FG_SPEC(fgAeClassText(g->c, (const char*)e->key));
        for (a = 0; a < spec->altCount; a++) for (k = 0; k < spec->alts[a].count; k++) HoldsExpr(g, "v", spec->alts[a].conds[k]);
    }
    if (g->isOfWritten) {
        FcList* types = fgSortedKeys(g->c, g->isOfTypes, false);
        for (i = 0; i < types->count; i++) {
            const char* typeName = (const char*)types->items[i];
            if (fcDictHas(g->isOfWritten, typeName)) continue;
            fcBufStr(&sb, Modular ? ModIsOf(g, typeName) : IsOfFunction(g, typeName));
            fcSetAdd(g->isOfWritten, typeName);
        }
    }
    byId = fgByValue(g, g->arrClasses);
    for (i = 0; i < byId->count; i++) {
        FcDictEntry* e = (FcDictEntry*)byId->items[i];
        FgSpec* spec = FG_SPEC(fgAeClassText(g->c, (const char*)e->key));
        FcBuf fits = {0};
        int id = FC_VALI(e->value);
        if (spec->altCount == 0) fcBufStr(&fits, "true");
        for (a = 0; a < spec->altCount; a++) {
            if (a) fcBufStr(&fits, " || ");
            fcBufChar(&fits, '(');
            for (k = 0; k < spec->alts[a].count; k++) { if (k) fcBufStr(&fits, " && "); fcBufStr(&fits, HoldsExpr(g, "v", spec->alts[a].conds[k])); }
            fcBufChar(&fits, ')');
        }
        fcBufFmt(&sb, "static bool %sacf_%d(Value v) { return %s; }\n", Px, id, fcBufDone(g->c, &fits));
        fcBufFmt(&sb, "static Value %sacz_%d(void) { return %s; }\n", Px, id, Constant(g, fgArrayClassZero(g, (const char*)e->key)));
    }
    isA = fcListNew(g->c);
    for (i = 0; i < byId->count; i++) {
        FcDictEntry* e = (FcDictEntry*)byId->items[i];
        const char* single = fgSpecSingleName(FG_SPEC(fgAeClassText(g->c, (const char*)e->key)));
        fcListAdd(g->c, isA, (void*)IsANames(g, single ? single : "", fcFmt(g->c, "%saci_%d", Px, FC_VALI(e->value)), &sb));
    }
    rows = fcListNew(g->c);
    for (i = 0; i < byId->count; i++) {
        FcDictEntry* e = (FcDictEntry*)byId->items[i];
        int id = FC_VALI(e->value);
        fcListAdd(g->c, rows, (void*)fcFmt(g->c, "{%sacf_%d, %s, %sacz_%d, %s}", Px, id, CString(g, FG_SPEC(fgAeClassText(g->c, (const char*)e->key))->display), Px, id, (const char*)isA->items[i]));
    }
    fcBufFmt(&sb, "static const ArrClass %skArrClasses[] = {%s};\n", Px, fgJoin(g->c, rows, ", "));
    return fcBufDone(g->c, &sb);
}

/* CheckUnit on a Value variable as a statement: adaptSlot in place; NULL for a demand without a unit (`: any`), which AdaptUnitCall writes. */
static const char* AdaptUnitStmt(FgGen* g, const char* v, const char* demand, const char* exit) {
    bool toFloat = demand[0] == '#';
    const char* unit = fgTrimStart(demand, '#');
    const char* text;
    const char* call;
    int flags;
    if (streq0(unit, "any")) return NULL;
    text = Constant(g, fcMakeString(g->c, UnitDemandDisplay(g, unit)));   /* StrV(&K..) */
    flags = (fcStartsWith(unit, "any ") ? 1 : 0) | (toFloat ? 2 : 0);
    call = fcFmt(g->c, "adaptSlot(&%s, %s, %d, %s)", v, UnitC(g, UnitId(g, DemandUnit(g, unit))), flags, fcStrN(g->c, text + 5, strlen(text) - 6));
    return exit ? fcFmt(g->c, "if (%s) goto %s;", call, exit) : fcFmt(g->c, "(void)%s;", call);
}
static bool fgIsIdentifier(const char* s) {
    const char* p;
    if (!((*s >= 'A' && *s <= 'Z') || (*s >= 'a' && *s <= 'z') || *s == '_')) return false;
    for (p = s + 1; *p; p++) if (!((*p >= 'A' && *p <= 'Z') || (*p >= 'a' && *p <= 'z') || (*p >= '0' && *p <= '9') || *p == '_')) return false;
    return true;
}
static const char* AdaptUnitCall(FgGen* g, const char* value, const char* demand) {
    bool toFloat = demand[0] == '#';
    const char* unit = fgTrimStart(demand, '#');
    bool dimensionOnly;
    const char* unitC;
    const char* call;
    if (streq0(unit, "any")) return toFloat ? fcFmt(g->c, "intToFloat(%s)", value) : value;
    dimensionOnly = fcStartsWith(unit, "any ");
    unitC = UnitC(g, UnitId(g, DemandUnit(g, unit)));
    call = fcFmt(g->c, "adaptUnit(%s, %s, %s, %s, %s)", value, unitC, dimensionOnly ? "true" : "false", toFloat ? "true" : "false", Constant(g, fcMakeString(g->c, UnitDemandDisplay(g, unit))));
    if (dimensionOnly || !fgIsIdentifier(value)) return call;
    return fcFmt(g->c, "(FIRE_LIKELY(%s.unit == %s && (%s.kind == K_Float%s)) ? %s : %s)", value, unitC, value, toFloat ? "" : fcFmt(g->c, " || %s.kind == K_Int", value), value, call);
}

/* ---- the dispatchers of interfaces (CppGenerator.cs) ---- */
typedef struct FgIDisp { const char* iface; const char* name; int argc; } FgIDisp;
typedef struct FgImpl { FgClassInfo* cls; FcProto* proto; } FgImpl;
static const char* InterfaceDispatcherName(FgGen* g, const char* iface, const char* name, int argc) { return fcFmt(g->c, "icall_%s_%s_%d", Mangle(g, iface), Mangle(g, name), argc); }
static void fgAddIDispatch(FgGen* g, const char* iface, const char* name, int argc) {
    const char* key = fcFmt(g->c, "%s\x01%s\x01%d", iface, name, argc);
    FgIDisp* x;
    if (fcDictHas(g->interfaceDispatchers, key)) return;
    x = (FgIDisp*)fcAlloc(g->c, sizeof *x);
    x->iface = iface; x->name = name; x->argc = argc;
    fcDictPut(g->interfaceDispatchers, key, x);
}
/* SortedSet<(string Interface, string Name, int Argc)>: the default comparer of the strings (culture), then the count */
static int fgIDispCmp(const FgIDisp* a, const FgIDisp* b) {
    int r = fgCultureCmp(a->iface, b->iface);
    if (r) return r;
    if ((r = fgCultureCmp(a->name, b->name)) != 0) return r;
    return a->argc < b->argc ? -1 : a->argc > b->argc;
}
static FcList* fgSortedIDispatch(FgGen* g) {
    FcList* l = fcListNew(g->c);
    int i, k;
    FC_DICT_EACH(g->interfaceDispatchers, e) fcListAdd(g->c, l, e->value);
    for (i = 1; i < l->count; i++)
        for (k = i; k > 0 && fgIDispCmp((FgIDisp*)l->items[k - 1], (FgIDisp*)l->items[k]) > 0; k--) { void* t = l->items[k]; l->items[k] = l->items[k - 1]; l->items[k - 1] = t; }
    return l;
}
/* The classes of the program that implement the interface, each with its method `name(argc)` - when every one has exactly one, public, not static and
 * without default values; NULL otherwise. */
static FcList* InterfaceImplementations(FgGen* g, const char* iface, const char* name, int argc) {
    FcList* r = fcListNew(g->c);
    int i;
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* c = (FgClassInfo*)g->classList->items[i];
        FgFound found;
        FgImpl* t;
        if (!ClassIs(c->rc, iface)) continue;
        if (FG_OVERLOADED(c->rc, name, argc)) return NULL;
        found = FG_FIND(c->rc, name, argc);
        if (!found.proto || found.proto->isStatic || found.access != ACC_Public || found.proto->paramCount != argc) return NULL;
        t = (FgImpl*)fcAlloc(g->c, sizeof *t);
        t->cls = c; t->proto = found.proto;
        fcListAdd(g->c, r, t);
    }
    return r->count > 0 ? r : NULL;
}
static bool DispatchNeedsList(FgGen* g, const char* name, int argc);
static bool InterfaceDispatchWorthwhile(FgGen* g, const char* iface, const char* name, int argc) {
    int i;
    if (LibraryRole || g->openWorld || DispatchNeedsList(g, name, argc) || ExtensionMethods(g, name, argc)->count > 0 || !InterfaceImplementations(g, iface, name, argc)) return false;
    if (RestrictedMethodName(g, name)) return true;
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* c = (FgClassInfo*)g->classList->items[i];
        if (!ClassIs(c->rc, iface) && FG_FIND(c->rc, name, argc).proto) return true;
    }
    return false;
}
