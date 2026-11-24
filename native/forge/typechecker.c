/* native/forge/typechecker.c - port of src/fire.Compiler/TypeChecker.cs, TypeChecker.TakeLocal.cs and SetterClaims.cs (commit 6bc618e7), the
 * part of Compiler.cs it uses (AnalyzeCoercion) and src/fire/Runtime/BuiltinMembers.cs. Method by method in the order of the C# file; the
 * TC0xx warnings and errors, E4052, the ownership hints TC031/TC035, TC018 (SetterClaims) are reported as there, and the tables the check leaves
 * for the bytecode compiler in ResolveResult (UnitChecks, LocalGuards, FixedCoercions, OverloadHints, MethodTypeArgs, ReceiverTypeArgs, the
 * expression trees, BitsOf, the proven members of `??.`, the derivation of `defined`: Derive) are filled as there (Phase C step 4).
 *
 * Not ported: the tools MemberLog, VarLog, FIRE_TYPECHECK_SUGGEST. A StaticType is an immutable FcST from the arena; `with` copies it. */

#include <limits.h>

/* (one translation unit with the parser and the resolver: the names of the C# methods they share get a prefix here) */
#define Check tcCheck
#define InterfaceClosure tcInterfaceClosure
#define PlainName tcPlainName
static const char* fcAsciiLower(FcCtx* c, const char* s);

typedef enum FcSKind { STK_Dynamic, STK_Bool, STK_Int, STK_Float, STK_Char, STK_String, STK_Lambda, STK_Undefined, STK_Class, STK_Object, STK_Array, STK_Enum, STK_Word } FcSKind;
static const char* const fcSKindLower[] = { "any", "bool", "int", "float", "char", "string", "lambda", "undefined", "class", "object", "array", "enum", "word" };
static const char* const fcSKindName[] = { "Dynamic", "Bool", "Int", "Float", "Char", "String", "Lambda", "Undefined", "Class", "Object", "Array", "Enum", "Word" };
typedef enum FcNull { NUL_Unknown, NUL_Defined, NUL_Inferred, NUL_MayBeUndefined } FcNull;

typedef struct FcST FcST;
struct FcST {
    FcSKind kind;
    const char* cls;
    const FcST* element;
    int paramCount;
    const FcST* ret;
    FcList* params;     /* FcST* (NULL: none) */
    FcList* args;       /* FcST* (NULL: none) */
    const FcU* unit;    /* NULL: not known */
    bool unitIsDimension;
    const char* typeParam;
    bool raw, declaredAny;
    int wide;
    FcNull nul;
    int bits;
    bool eitherNumber;
    bool anyUnit;       /* declared with ': any' (a type argument `int : any`): no unit is demanded - what a lambda's parameter typed by it gets (ContextParamType) */
    bool lambdaShape;   /* a lambda type of a declaration (DeclaredLambda), not a selector or a name alone: its number of parameters is the one `(lambda<int> g)` checks */
};
#define ST_INIT(k) { (k), NULL, NULL, -1, NULL, NULL, NULL, NULL, false, NULL, false, false, 0, NUL_Unknown, 0, false }
static const FcST fcSTDynamic = ST_INIT(STK_Dynamic);
static const FcST fcSTNumber = { STK_Dynamic, NULL, NULL, -1, NULL, NULL, NULL, NULL, false, NULL, false, false, 0, NUL_Unknown, 0, true };
static const FcST fcSTAny = { STK_Dynamic, NULL, NULL, -1, NULL, NULL, NULL, NULL, false, NULL, false, true, 0, NUL_Unknown, 0, false };
static const FcST fcSTBool = ST_INIT(STK_Bool);
static const FcST fcSTInt = ST_INIT(STK_Int);
static const FcST fcSTWord = { STK_Word, NULL, NULL, -1, NULL, NULL, NULL, &fcUnitlessU, false, NULL, true, false, 0, NUL_Unknown, 0, false };
static const FcST fcSTFloat = ST_INIT(STK_Float);
static const FcST fcSTChar = ST_INIT(STK_Char);
static const FcST fcSTString = ST_INIT(STK_String);
static const FcST fcSTLambda = ST_INIT(STK_Lambda);
static const FcST fcSTUndefined = ST_INIT(STK_Undefined);
static const FcST fcSTObject = ST_INIT(STK_Object);
#define ST_DYNAMIC (&fcSTDynamic)
#define ST_NUMBER (&fcSTNumber)
#define ST_ANY (&fcSTAny)
#define ST_BOOL (&fcSTBool)
#define ST_INT (&fcSTInt)
#define ST_WORD (&fcSTWord)
#define ST_FLOAT (&fcSTFloat)
#define ST_CHAR (&fcSTChar)
#define ST_STRING (&fcSTString)
#define ST_UNDEFINED (&fcSTUndefined)
#define ST_OBJECT (&fcSTObject)

static FcST* stCopy(FcCtx* c, const FcST* t) { FcST* r = (FcST*)fcAlloc(c, sizeof *r); *r = *t; return r; }
static bool stIsValueKind(const FcST* t) { return t->kind == STK_Int || t->kind == STK_Float || t->kind == STK_Bool || t->kind == STK_Word || t->kind == STK_Char; }
static bool stIsNumber(const FcST* t) { return t->kind == STK_Int || t->kind == STK_Float; }
static bool stIsDynamic(const FcST* t) { return t->kind == STK_Dynamic; }
static const FcST* stWithNull(FcCtx* c, const FcST* t, FcNull n) { FcST* r; if (n == t->nul) return t; r = stCopy(c, t); r->nul = n; return r; }
static const FcST* stWithUnit(FcCtx* c, const FcST* t, const FcU* unit, bool dimensionOnly) { FcST* r = stCopy(c, t); r->unit = unit; r->unitIsDimension = dimensionOnly; return r; }
static const FcST* stArrayOf(FcCtx* c, const FcST* element) { FcST* r = stCopy(c, ST_DYNAMIC); r->kind = STK_Array; r->element = element; return r; }
static const FcST* stClassOf(FcCtx* c, const char* name, FcList* args) { FcST* r = stCopy(c, ST_DYNAMIC); r->kind = STK_Class; r->cls = name; r->args = args; return r; }
static const FcST* stEnumOf(FcCtx* c, const char* name) { FcST* r = stCopy(c, ST_DYNAMIC); r->kind = STK_Enum; r->cls = name; return r; }
static const FcST* stLambdaOf(FcCtx* c, int paramCount, const FcST* returns, FcList* parameters) {
    FcST* r = stCopy(c, ST_DYNAMIC);
    r->kind = STK_Lambda; r->paramCount = paramCount; r->ret = returns; r->params = parameters;
    return r;
}
static bool stEq(const FcST* a, const FcST* b);
static bool stListEq(const FcList* a, const FcList* b) {
    int i;
    if (!a || !b) return a == b;
    if (a->count != b->count) return false;
    for (i = 0; i < a->count; i++) if (!stEq((const FcST*)a->items[i], (const FcST*)b->items[i])) return false;
    return true;
}
/* StaticType.Equals: the kind, the class, the element, the signature, the type arguments, the unit (not Null, Raw, Wide, ...) */
static bool stEq(const FcST* a, const FcST* b) {
    if (a == b) return true;
    if (!a || !b) return false;
    if (a->kind != b->kind || !streq0(a->cls, b->cls) || !stEq(a->element, b->element) || a->paramCount != b->paramCount || !stEq(a->ret, b->ret)) return false;
    if (!stListEq(a->params, b->params) || !stListEq(a->args, b->args)) return false;
    if (a->unit == NULL || b->unit == NULL) { if (a->unit != b->unit) return false; }
    else if (!fcUEquals(a->unit, b->unit)) return false;
    return a->unitIsDimension == b->unitIsDimension;
}
/* StaticType.ToString */
static void stText(FcCtx* c, FcBuf* b, const FcST* t) {
    int i;
    switch (t->kind) {
        case STK_Class:
            fcBufStr(b, fcPlainName(c, t->cls));
            if (t->args && t->args->count > 0) {
                fcBufChar(b, '<');
                for (i = 0; i < t->args->count; i++) { if (i) fcBufStr(b, ", "); stText(c, b, (const FcST*)t->args->items[i]); }
                fcBufChar(b, '>');
            }
            return;
        case STK_Object: fcBufStr(b, "class"); return;
        case STK_Enum: fcBufStr(b, t->cls); return;
        case STK_Dynamic: fcBufStr(b, "any"); return;
        case STK_Array: stText(c, b, t->element); fcBufStr(b, "[]"); return;
        case STK_Lambda:
            if (t->ret) { stText(c, b, t->ret); fcBufChar(b, ' '); }
            fcBufStr(b, "lambda");
            if (t->paramCount > 0) {
                fcBufChar(b, '<');
                for (i = 0; i < t->paramCount; i++) {
                    if (i) fcBufStr(b, ", ");
                    if (t->params) { if (i < t->params->count) stText(c, b, (const FcST*)t->params->items[i]); }
                    else fcBufStr(b, "any");
                }
                fcBufChar(b, '>');
            }
            return;
        default:
            if (t->kind == STK_Int && t->raw) { fcBufStr(b, "byte"); return; }
            fcBufStr(b, fcSKindLower[t->kind]);
            if (t->unit && !fcUIsUnitless(t->unit)) { fcBufStr(b, t->unitIsDimension ? " : any " : " : "); fcBufStr(b, fcUText(c, t->unit)); }
            return;
    }
}
static const char* stStr(FcCtx* c, const FcST* t) { FcBuf b = {0}; fcBufStr(&b, ""); stText(c, &b, t); return fcBufDone(c, &b); }

/* Compiler.CoercionInfo / AnalyzeCoercion */
typedef struct FcCoercionInfo { FcExpr* inner; bool wantsUnit; const char* explicitUnit; bool wantsType; int explicitType; bool relabelAuto; } FcCoercionInfo;   /* explicitType: a token type, -1 null */
static bool ciUnitIsAuto(const FcCoercionInfo* i) { return i->wantsUnit && i->explicitUnit == NULL; }
static bool ciTypeIsAuto(const FcCoercionInfo* i) { return i->wantsType && i->explicitType < 0; }
static bool ciRequestsAnyAuto(const FcCoercionInfo* i) { return ciUnitIsAuto(i) || ciTypeIsAuto(i); }
static FcCoercionInfo fcAnalyzeCoercion(FcExpr* expr) {
    FcCoercionInfo info;
    FcExpr* current = expr;
    memset(&info, 0, sizeof info);
    info.explicitType = -1;
    for (;;) {
        if (current->kind == EK_UnitCoerceExpr && (!((FcUnitCoerceExpr*)current)->relabel || ((FcUnitCoerceExpr*)current)->targetUnitName == NULL)) {
            FcUnitCoerceExpr* uc = (FcUnitCoerceExpr*)current;
            info.wantsUnit = true;
            info.explicitUnit = uc->targetUnitName;
            if (uc->relabel) info.relabelAuto = true;
            current = uc->operand;
        } else if (current->kind == EK_TypeCoerceExpr && ((FcTypeCoerceExpr*)current)->targetClass == NULL && ((FcTypeCoerceExpr*)current)->width == 0
                   && ((FcTypeCoerceExpr*)current)->targetTypeKeyword != TT_KwWord) {
            FcTypeCoerceExpr* tc = (FcTypeCoerceExpr*)current;
            info.wantsType = true;
            info.explicitType = tc->targetTypeKeyword;
            current = tc->operand;
        } else break;
    }
    info.inner = current;
    return info;
}

/* BuiltinMembers.Has (ValueKind: the kinds asked here) */
typedef enum FcVK { FVK_String, FVK_Char, FVK_Int, FVK_Array, FVK_Buffer, FVK_Lambda } FcVK;
static bool fcIsOwnership(const char* n) { return streq0(n, "TakeTo") || streq0(n, "TakeLocal") || streq0(n, "TakeUpwards") || streq0(n, "TakeGlobal"); }
static bool fcBufferMethodsHas(const char* name);
static bool fcBuiltinHas(FcVK kind, bool isEnum, const char* name) {
    if (streq0(name, "ToString")) return true;
    switch (kind) {
        case FVK_String: return streq0(name, "Length") || streq0(name, "length") || streq0(name, "ToBytes") || streq0(name, "ToUnicode");
        case FVK_Char: return streq0(name, "ToByte") || streq0(name, "ToUnicode");
        case FVK_Int: return streq0(name, "ToChar") || (isEnum && (streq0(name, "ToString") || streq0(name, "HasFlag")));
        case FVK_Array: return fcIsOwnership(name) || streq0(name, "Length") || streq0(name, "length") || streq0(name, "GetEnumerator");
        case FVK_Buffer: return fcIsOwnership(name) || streq0(name, "Length") || streq0(name, "length") || streq0(name, "littleEndian") || streq0(name, "GetEnumerator")
            || streq0(name, "ToString") || streq0(name, "ToUnicode") || streq0(name, "ToUnicodeChar") || streq0(name, "ToLittleEndian") || streq0(name, "ToBigEndian") || fcBufferMethodsHas(name);
        case FVK_Lambda: return fcIsOwnership(name);
    }
    return false;
}

#include "typechecker1.c"
