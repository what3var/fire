/* native/forge/ast.h - the syntax tree: src/fire/Ast/Expr.cs, Stmt.cs, TypeRef.cs (commit f18c976c), as tagged structs from the arena.
 * Every record of the C# tree is a struct here with the same fields in the same order (the dump prints them in that order); a node kind
 * is the C# type. C#'s null: NULL for pointers and lists, FC_NULL_INT for an int?. */
#ifndef FORGE_AST_H
#define FORGE_AST_H

#define FC_NULL_INT INT32_MIN

typedef struct FcExpr FcExpr;
typedef struct FcStmt FcStmt;
typedef struct FcBlockStmt FcBlockStmt;

/* ---- enums of the tree (names printed as the C# enums) ---- */
typedef enum FcUnaryOp { UOP_Negate, UOP_LogicalNot, UOP_BitNot, UOP_Dereference, UOP_AddressOf, UOP_FlatCopy, UOP_DeepCopy, UOP_Take, UOP_Trust, UOP_TryTake, UOP__NONE } FcUnaryOp;
typedef enum FcBinaryOp { BOP_Add, BOP_Sub, BOP_Mul, BOP_Div, BOP_Mod, BOP_Eq, BOP_NotEq, BOP_Lt, BOP_LtEq, BOP_Gt, BOP_GtEq, BOP_And, BOP_Or, BOP_BitAnd, BOP_BitOr,
    BOP_BitXor, BOP_ShiftLeft, BOP_ShiftRight, BOP_Power, BOP_ShiftRightLogical, BOP__NONE } FcBinaryOp;
typedef enum FcAccess { ACC_Public, ACC_Private, ACC_Protected } FcAccess;
typedef enum FcCoerceDirection { COE_Factor, COE_From, COE_To } FcCoerceDirection;
typedef enum FcConstraintKind { TCK_IsOf, TCK_IsIn } FcConstraintKind;
typedef enum FcGlobalJobsMode { GJM_Sync, GJM_Safepoints, GJM_Sleep } FcGlobalJobsMode;

/* ---- values (a LiteralExpr's Value) ---- */
typedef enum FcValueKind { VK_Bool, VK_Int, VK_Float, VK_Char, VK_String, VK_Undefined } FcValueKind;
typedef struct FcValue { FcValueKind kind; int64_t i; double f; bool b; const char* s; const char* unit; const struct FcU* unitv; } FcValue;   /* unit: its text, NULL: unitless; unitv: Unit.Parse of the suffix (the type checker) */

/* ---- TypeRef, LambdaSignature ---- */
typedef struct FcLambdaSignature { const char* returnTypeName; FcList* paramTypeNames; bool isSelector; const char* selectorKind; bool isExpression; } FcLambdaSignature;
typedef struct FcTypeRef {
    const char* baseName; int bitWidth; int pointerDepth; FcLambdaSignature* lambdaSignature; FcList* namespaces; const char* unit;
    int arrayRank; int typeArgCount; FcList* typeArgs; bool isUnsigned; bool nonNull;
} FcTypeRef;

typedef struct FcLambdaParam { const char* name; FcTypeRef* type; FcList* arrayRanks; FcExpr* defaultValue; bool byRef; } FcLambdaParam;

/* ---- expressions ---- */
#define FC_EXPR_KINDS(X) \
    X(LiteralExpr) X(TypeBitsExpr) X(IdentifierExpr) X(ThisExpr) X(BaseExpr) X(SelfClassExpr) X(LambdaExpr) X(SyncExpr) X(SyncGlobalsExpr) \
    X(ProbeExpr) X(TryProcessExpr) X(TryCallExpr) X(ConditionalAccessExpr) X(ConditionalTargetExpr) X(NewExpr) X(NewArrayExpr) X(NewBufferExpr) \
    X(ArrayLiteralExpr) X(InterpolatedStringExpr) X(ThrowExpr) X(UnaryExpr) X(BinaryExpr) X(RebindExpr) X(ConditionalExpr) X(UnitCoerceExpr) \
    X(TypeCoerceExpr) X(IsInExpr) X(RangeExpr) X(IsOfExpr) X(IsFromExpr) X(UntilExpr) X(AwaitOnExpr) X(CallExpr) X(MemberExpr) X(IndexExpr) \
    X(AssignExpr) X(WithExpr) X(IncDecExpr)
typedef enum FcExprKind {
#define FC_EK(n) EK_##n,
    FC_EXPR_KINDS(FC_EK)
#undef FC_EK
    EK__COUNT
} FcExprKind;

struct FcExpr { FcExprKind kind; int line; };

typedef struct FcCapture { const char* name; bool handover; } FcCapture;   /* an entry of LambdaExpr.CaptureModes */

typedef struct { FcExpr h; FcValue value; } FcLiteralExpr;
typedef struct { FcExpr h; const char* baseName; int width; } FcTypeBitsExpr;
typedef struct { FcExpr h; const char* name; } FcIdentifierExpr;
typedef struct { FcExpr h; } FcThisExpr, FcBaseExpr, FcSelfClassExpr, FcSyncGlobalsExpr, FcConditionalTargetExpr;
typedef struct { FcExpr h; FcList* params; FcExpr* onTarget; FcBlockStmt* body; bool autoCapture; FcList* captureModes; bool expressionBody; bool lendCaptures; } FcLambdaExpr;
typedef struct { FcExpr h; bool isTry; bool isFlat; FcExpr* target; } FcSyncExpr;
typedef struct { FcExpr h; FcExpr* target; const char* member; bool isChanging; FcExpr* handler; } FcProbeExpr;
typedef struct { FcExpr h; FcExpr* target; } FcTryProcessExpr;
typedef struct { FcExpr h; FcExpr* call; } FcTryCallExpr;
typedef struct { FcExpr h; FcExpr* target; FcExpr* inner; FcExpr* placeholder; bool checkMember; const char* member; } FcConditionalAccessExpr;
typedef struct { FcExpr h; FcTypeRef* classRef; FcList* args; FcList* typeArgs; FcList* argNames; } FcNewExpr;
typedef struct { FcExpr h; FcTypeRef* elementType; FcList* sizeExprs; } FcNewArrayExpr;
typedef struct { FcExpr h; FcExpr* sizeExpr; bool words; } FcNewBufferExpr;
typedef struct { FcExpr h; FcList* elements; } FcArrayLiteralExpr;
typedef struct FcInterpolationPart { bool isExpression; const char* text; FcExpr* expression; const char* format; } FcInterpolationPart;
typedef struct { FcExpr h; FcList* parts; } FcInterpolatedStringExpr;
typedef struct { FcExpr h; FcExpr* value; } FcThrowExpr;
typedef struct { FcExpr h; FcUnaryOp op; FcExpr* operand; } FcUnaryExpr;
typedef struct { FcExpr h; FcBinaryOp op; FcExpr* left; FcExpr* right; bool floatPower; } FcBinaryExpr;
typedef struct { FcExpr h; FcExpr* lambda; FcExpr* target; } FcRebindExpr;
typedef struct { FcExpr h; FcExpr* condition; FcExpr* then; FcExpr* otherwise; } FcConditionalExpr;
typedef struct { FcExpr h; FcExpr* operand; const char* targetUnitName; bool relabel; } FcUnitCoerceExpr;
typedef struct { FcExpr h; FcExpr* operand; int targetTypeKeyword; int width; bool isUnsigned; FcTypeRef* targetClass; } FcTypeCoerceExpr;   /* keyword: a FcTokenType, -1 null */
typedef struct { FcExpr h; FcExpr* operand; const char* unitName; } FcIsInExpr;
typedef struct { FcExpr h; FcExpr* from; FcExpr* to; } FcRangeExpr;
typedef struct { FcExpr h; FcExpr* operand; FcTypeRef* typeRef; } FcIsOfExpr;
typedef struct { FcExpr h; FcExpr* operand; FcExpr* ownerExpr; bool transitive; const char* place; } FcIsFromExpr;
typedef struct { FcExpr h; FcExpr* inner; FcList* limits; } FcUntilExpr;
typedef struct { FcExpr h; FcExpr* lambda; FcList* args; } FcAwaitOnExpr;
typedef struct { FcExpr h; FcExpr* callee; FcList* args; FcList* typeArgs; FcList* argNames; bool awaited; bool dedicated; } FcCallExpr;
typedef struct { FcExpr h; FcExpr* target; const char* name; } FcMemberExpr;
typedef struct { FcExpr h; FcExpr* target; FcExpr* index; } FcIndexExpr;
typedef struct { FcExpr h; FcExpr* target; FcExpr* value; bool replace; bool parenthesized; } FcAssignExpr;   /* parenthesized: Parser.ParenthesizedAssignments */
typedef struct FcWithSet { int line; const char* name; FcExpr* value; } FcWithSet;
typedef struct { FcExpr h; FcExpr* source; FcUnaryOp copy; FcList* sets; } FcWithExpr;   /* copy: UOP__NONE for null */
typedef struct { FcExpr h; FcExpr* target; bool isIncrement; bool isPrefix; } FcIncDecExpr;

/* ---- statements ---- */
#define FC_STMT_KINDS(X) \
    X(BlockStmt) X(ExprStmt) X(NoOpStmt) X(NoShadowDirective) X(GlobalJobsDirective) X(TypeCheckDirective) X(TimeoutDirective) X(EndWaitDirective) \
    X(EnumDecl) X(VarDeclStmt) X(IfStmt) X(WhileStmt) X(DoWhileStmt) X(UnitDeclStmt) X(ForStmt) X(ForeachStmt) X(ReturnStmt) X(ThrowStmt) \
    X(DeleteStmt) X(BreakStmt) X(ContinueStmt) X(FireStmt) X(SectionEnterStmt) X(SectionExitStmt) X(SilenceStmt) X(PostGlobalStmt) X(ProcessStmt) \
    X(LeaveStmt) X(TerminateStmt) X(CatchThreadsDecl) X(CatchTerminateDecl) X(TryStmt) X(FieldDecl) X(MethodDecl) X(ConstructorDecl) \
    X(DestructorDecl) X(ClassDecl) X(InterfaceDecl) X(ExternDecl) X(UnsafeStmt) X(PropertyDecl) X(ClassExtensionDecl) X(NamespaceDecl)
typedef enum FcStmtKind {
#define FC_SK(n) SK_##n,
    FC_STMT_KINDS(FC_SK)
#undef FC_SK
    SK__COUNT
} FcStmtKind;

struct FcStmt { FcStmtKind kind; int source; int line; };

struct FcBlockStmt { FcStmt h; FcList* statements; };
typedef struct { FcStmt h; FcExpr* expression; } FcExprStmt;
typedef struct { FcStmt h; } FcNoOpStmt, FcNoShadowDirective, FcSectionEnterStmt, FcSectionExitStmt, FcLeaveStmt;
typedef struct { FcStmt h; FcGlobalJobsMode mode; } FcGlobalJobsDirective;
typedef struct { FcStmt h; bool strict; } FcTypeCheckDirective;
typedef struct { FcStmt h; FcExpr* value; } FcTimeoutDirective, FcEndWaitDirective;
typedef struct FcEnumMember { const char* name; FcExpr* valueExpr; } FcEnumMember;
typedef struct { FcStmt h; const char* name; FcList* members; bool isFlags; } FcEnumDecl;
typedef struct { FcStmt h; const char* name; FcTypeRef* type; FcList* arrayRanks; FcExpr* initializer; bool isReadonly; FcList* switchValues; } FcVarDeclStmt;   /* switchValues: Parser.Switches */
typedef struct { FcStmt h; FcExpr* condition; FcStmt* then; FcStmt* otherwise; } FcIfStmt;
typedef struct { FcStmt h; FcExpr* condition; FcStmt* body; } FcWhileStmt;
typedef struct { FcStmt h; FcStmt* body; FcExpr* condition; } FcDoWhileStmt;
typedef struct FcUnitCoerce { int line; FcCoerceDirection direction; const char* targetUnit; FcExpr* conversion; } FcUnitCoerce;
typedef struct { FcStmt h; const char* name; const char* symbol; bool prefixed; FcList* coerces; } FcUnitDeclStmt;
typedef struct { FcStmt h; FcStmt* init; FcExpr* condition; FcExpr* increment; FcStmt* body; } FcForStmt;
typedef struct { FcStmt h; const char* varName; FcExpr* iterable; FcStmt* body; } FcForeachStmt;
typedef struct { FcStmt h; FcExpr* value; } FcReturnStmt, FcThrowStmt, FcTerminateStmt;
typedef struct { FcStmt h; FcExpr* target; } FcDeleteStmt, FcProcessStmt;
typedef struct { FcStmt h; int levels; } FcBreakStmt, FcContinueStmt;
typedef struct FcFireTakingCapture { const char* varName; FcExpr* source; bool handover; bool isImplicit; } FcFireTakingCapture;
typedef struct FcFireWith { const char* varName; FcExpr* source; } FcFireWith;
typedef struct { FcStmt h; FcList* takingCaptures; FcList* with; FcBlockStmt* body; bool dedicated; FcList* until; } FcFireStmt;
typedef struct { FcStmt h; FcExpr* target; const char* member; bool memberForm; } FcSilenceStmt;
typedef struct { FcStmt h; FcExpr* lambda; FcList* args; bool onTarget; int withCount; } FcPostGlobalStmt;
typedef struct { FcStmt h; FcTypeRef* typeRef; const char* varName; FcBlockStmt* body; } FcCatchThreadsDecl;
typedef struct { FcStmt h; const char* varName; FcBlockStmt* body; } FcCatchTerminateDecl;
typedef struct FcCatchClause { int source; int line; FcTypeRef* typeRef; const char* varName; FcBlockStmt* body; } FcCatchClause;
typedef struct { FcStmt h; FcBlockStmt* tryBlock; FcList* catches; FcBlockStmt* finallyBlock; bool isSyncSection; } FcTryStmt;
typedef struct FcAttributeDecl { int line; const char* name; FcList* args; FcList* argTexts; } FcAttributeDecl;
typedef struct { FcStmt h; FcTypeRef* type; FcList* arrayRanks; const char* name; FcExpr* initializer; bool isReadonly; FcAccess access; bool isStatic; FcList* attributes; } FcFieldDecl;
typedef struct FcTypeConstraint { FcConstraintKind kind; const char* name; } FcTypeConstraint;
typedef struct FcTypeConstraintGroup { FcList* constraints; } FcTypeConstraintGroup;
typedef struct FcTypeParam { const char* name; FcList* constraintGroups; } FcTypeParam;
typedef struct { FcStmt h; FcTypeRef* returnType; const char* name; FcList* params; FcBlockStmt* body; FcList* typeParams; FcAccess access; bool isStatic;
    FcList* attributes; bool isOverride; FcList* specialization; } FcMethodDecl;
typedef struct { FcStmt h; FcList* params; FcList* baseArgs; FcBlockStmt* body; FcAccess access; } FcConstructorDecl;
typedef struct { FcStmt h; FcBlockStmt* body; } FcDestructorDecl, FcUnsafeStmt;
typedef struct { FcStmt h; const char* name; FcList* baseRefs; FcList* members; FcList* typeParams; bool isActor; FcList* attributes; bool isRecord; } FcClassDecl;
typedef struct FcInterfaceMethodSig { int line; FcTypeRef* returnType; const char* name; FcList* params; } FcInterfaceMethodSig;
typedef struct FcInterfacePropertySig { int line; FcTypeRef* type; const char* name; bool hasGet; bool hasSet; } FcInterfacePropertySig;
typedef struct { FcStmt h; const char* name; FcList* methods; FcList* typeParams; FcList* baseRefs; FcList* properties; } FcInterfaceDecl;
typedef struct { FcStmt h; FcTypeRef* returnType; const char* name; FcList* params; const char* libName; const char* inlineC; int inlineLine; const char* displayName; } FcExternDecl;
typedef struct { FcStmt h; FcTypeRef* type; const char* name; FcBlockStmt* getter; FcBlockStmt* setter; FcAccess access; bool isStatic; FcList* attributes; bool isOverride; } FcPropertyDecl;
typedef struct { FcStmt h; FcTypeRef* targetRef; FcList* members; } FcClassExtensionDecl;
typedef struct { FcStmt h; const char* name; FcList* members; } FcNamespaceDecl;

/* a node from the arena: the fields from a compound literal (`FC_NEW(c, FcIfStmt, .h = {SK_IfStmt, src, line}, .condition = ...)`) */
#define FC_NEW(ctx, T, ...) ((T*)fcNodeDup((ctx), &(T){ __VA_ARGS__ }, sizeof(T)))
static inline void* fcNodeDup(FcCtx* c, const void* node, size_t size) { void* n = fcAlloc(c, size); memcpy(n, node, size); return n; }

void fcDumpProgram(FcCtx* c, FcBuf* out, FcList* program);
void fcDumpDeclared(FcBuf* out, const FcDeclaredWarning* w);

#endif
