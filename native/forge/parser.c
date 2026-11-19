/* native/forge/parser.c - port of src/fire.Compiler/Parser.cs (commit f18c976c), method by method in the order of the C# file. The tree is
 * native/forge/ast.h. A ParseException is thrown on the context (FC_ERR_PARSE); the `finally` blocks of the C# code are entries of a
 * cleanup stack on the parser (pushed where the C# `try` starts, run when an exception is caught inside the same parser - only
 * AttributesBeforeClass catches one -, popped without running on the normal path, which does the work of the `finally` itself). */

typedef enum FcCleanupKind { CL_IN_DO_BODY, CL_SUPPRESS_COLON, CL_CAPTURE_USES, CL_UNTIL_SINGLE, CL_CLASS_NAME, CL_EXTENSION_NAME, CL_MEMBER_WARNINGS } FcCleanupKind;
typedef struct FcCleanup { FcCleanupKind kind; int i; bool b; const void* p; } FcCleanup;

typedef struct FcMemberWarning { int id; const char* text; int line; } FcMemberWarning;

typedef struct FcParser {
    FcCtx* ctx;
    FcToken* tokens;
    int count;
    int pos;
    const char* currentExternLib;
    FcList* usingNamespaces;
    const char* currentNamespace;
    const char* implicitNamespace;
    int namespaceDepth;
    const char* currentClassName;
    bool currentClassIsGeneric;
    int sourceIndex;
    const char* fileName;
    FcList* memberWarnings;      /* FcMemberWarning*, NULL outside of a member */
    const char* extensionClassName;
    FcList* withVarStack;        /* innermost last */
    int withCounter;
    int switchCounter;
    int bracketDepth;
    int suppressColonPostfixAtDepth;   /* FC_NULL_INT: none */
    FcList* enumDecls;
    FcList* hoistedInlineC;
    FcList* captureUses;         /* FcList* of FcCapture*, innermost last */
    FcList* clauseUntil;
    bool untilSingle;
    bool suppressShortLambda;
    bool inDoBody;
    FcList* braceArrayTokens;    /* token positions (intptr_t) */
    FcCleanup* cleanups;
    int cleanupCount, cleanupCap;
} FcParser;

/* ---- the cleanup stack (the `finally` blocks) ---- */
static void pPushCleanup(FcParser* p, FcCleanupKind kind, int i, bool b, const void* ptr) {
    if (p->cleanupCount == p->cleanupCap) {
        int cap = p->cleanupCap ? p->cleanupCap * 2 : 16;
        FcCleanup* n = (FcCleanup*)fcAlloc(p->ctx, sizeof(FcCleanup) * (size_t)cap);
        if (p->cleanupCount) memcpy(n, p->cleanups, sizeof(FcCleanup) * (size_t)p->cleanupCount);
        p->cleanups = n;
        p->cleanupCap = cap;
    }
    p->cleanups[p->cleanupCount].kind = kind;
    p->cleanups[p->cleanupCount].i = i;
    p->cleanups[p->cleanupCount].b = b;
    p->cleanups[p->cleanupCount].p = ptr;
    p->cleanupCount++;
}
static FcList* PopCaptureUses(FcParser* p);
static void pRunCleanupsTo(FcParser* p, int depth) {
    while (p->cleanupCount > depth) {
        FcCleanup cl = p->cleanups[--p->cleanupCount];
        switch (cl.kind) {
            case CL_IN_DO_BODY: p->inDoBody = cl.b; break;
            case CL_SUPPRESS_COLON: p->suppressColonPostfixAtDepth = cl.i; break;
            case CL_CAPTURE_USES: PopCaptureUses(p); break;
            case CL_UNTIL_SINGLE: p->untilSingle = cl.b; break;
            case CL_CLASS_NAME: p->currentClassName = (const char*)cl.p; p->currentClassIsGeneric = cl.b; break;
            case CL_EXTENSION_NAME: p->extensionClassName = (const char*)cl.p; break;
            case CL_MEMBER_WARNINGS: p->memberWarnings = (FcList*)cl.p; break;
        }
    }
}
static void pPopCleanup(FcParser* p) { p->cleanupCount--; }

/* ---- lists and nodes ---- */
#define C_ (p->ctx)
static FcList* L(FcParser* p) { return fcListNew(p->ctx); }
static void LA(FcParser* p, FcList* l, const void* item) { fcListAdd(p->ctx, l, (void*)item); }
static const char* S(FcParser* p, const char* fmt, ...) {
    FcBuf b = {0};
    va_list ap;
    char small[512];
    int n;
    va_start(ap, fmt);
    n = vsnprintf(small, sizeof small, fmt, ap);
    va_end(ap);
    if (n >= 0 && (size_t)n < sizeof small) return fcStr(p->ctx, small);
    {
        char* big = (char*)fcAlloc(p->ctx, (size_t)n + 1);
        va_start(ap, fmt);
        vsnprintf(big, (size_t)n + 1, fmt, ap);
        va_end(ap);
        (void)b;
        return big;
    }
}
static bool streq(const char* a, const char* b) { return a == b || (a && b && strcmp(a, b) == 0); }

static FcTypeRef* newTypeRef(FcParser* p, const char* baseName, int bitWidth, int pointerDepth) {
    FcTypeRef* t = (FcTypeRef*)fcAlloc(p->ctx, sizeof *t);
    t->baseName = baseName; t->bitWidth = bitWidth; t->pointerDepth = pointerDepth;
    return t;
}
static FcTypeRef* copyTypeRef(FcParser* p, const FcTypeRef* t) { return (FcTypeRef*)fcNodeDup(p->ctx, t, sizeof *t); }
static FcLambdaParam* newParam(FcParser* p, const char* name, FcTypeRef* type, FcList* ranks, FcExpr* def, bool byRef) {
    FcLambdaParam* lp = (FcLambdaParam*)fcAlloc(p->ctx, sizeof *lp);
    lp->name = name; lp->type = type; lp->arrayRanks = ranks; lp->defaultValue = def; lp->byRef = byRef;
    return lp;
}

static FcExpr* mkIdent(FcParser* p, int line, const char* name) { return (FcExpr*)FC_NEW(C_, FcIdentifierExpr, .h = {EK_IdentifierExpr, line}, .name = name); }
static FcExpr* mkMember(FcParser* p, int line, FcExpr* target, const char* name) { return (FcExpr*)FC_NEW(C_, FcMemberExpr, .h = {EK_MemberExpr, line}, .target = target, .name = name); }
static FcExpr* mkCall(FcParser* p, int line, FcExpr* callee, FcList* args, FcList* typeArgs, FcList* argNames) {
    return (FcExpr*)FC_NEW(C_, FcCallExpr, .h = {EK_CallExpr, line}, .callee = callee, .args = args, .typeArgs = typeArgs, .argNames = argNames);
}
static FcExpr* mkBinary(FcParser* p, int line, FcBinaryOp op, FcExpr* l, FcExpr* r) { return (FcExpr*)FC_NEW(C_, FcBinaryExpr, .h = {EK_BinaryExpr, line}, .op = op, .left = l, .right = r); }
static FcExpr* mkUnary(FcParser* p, int line, FcUnaryOp op, FcExpr* e) { return (FcExpr*)FC_NEW(C_, FcUnaryExpr, .h = {EK_UnaryExpr, line}, .op = op, .operand = e); }
static FcExpr* mkAssign(FcParser* p, int line, FcExpr* target, FcExpr* value) { return (FcExpr*)FC_NEW(C_, FcAssignExpr, .h = {EK_AssignExpr, line}, .target = target, .value = value); }
static FcExpr* mkUndefined(FcParser* p, int line) {
    FcValue v; memset(&v, 0, sizeof v); v.kind = VK_Undefined;
    return (FcExpr*)FC_NEW(C_, FcLiteralExpr, .h = {EK_LiteralExpr, line}, .value = v);
}
static FcExpr* mkLambda(FcParser* p, int line, FcList* params, FcExpr* onTarget, FcBlockStmt* body, bool autoCapture) {
    return (FcExpr*)FC_NEW(C_, FcLambdaExpr, .h = {EK_LambdaExpr, line}, .params = params, .onTarget = onTarget, .body = body, .autoCapture = autoCapture);
}
static FcBlockStmt* mkBlock(FcParser* p, int line, FcList* statements) { return FC_NEW(C_, FcBlockStmt, .h = {SK_BlockStmt, p->sourceIndex, line}, .statements = statements); }
static FcStmt* mkExprStmt(FcParser* p, int line, FcExpr* e) { return (FcStmt*)FC_NEW(C_, FcExprStmt, .h = {SK_ExprStmt, p->sourceIndex, line}, .expression = e); }
static FcStmt* mkReturn(FcParser* p, int line, FcExpr* e) { return (FcStmt*)FC_NEW(C_, FcReturnStmt, .h = {SK_ReturnStmt, p->sourceIndex, line}, .value = e); }
static FcStmt* mkVarDecl(FcParser* p, int line, const char* name, FcTypeRef* type, FcList* ranks, FcExpr* init, bool isReadonly) {
    return (FcStmt*)FC_NEW(C_, FcVarDeclStmt, .h = {SK_VarDeclStmt, p->sourceIndex, line}, .name = name, .type = type, .arrayRanks = ranks, .initializer = init,
        .isReadonly = isReadonly);
}
static FcList* L1(FcParser* p, const void* item) { FcList* l = L(p); LA(p, l, item); return l; }

/* ---- low-level token handling (the end of Parser.cs) ---- */
static FcToken* Peek(FcParser* p) { return &p->tokens[p->pos]; }
static FcToken* Previous(FcParser* p) { return &p->tokens[p->pos - 1]; }
static bool Check(FcParser* p, FcTokenType type) { return Peek(p)->type == type; }
static FcToken* PeekAt(FcParser* p, int offset) {
    int idx = p->pos + offset;
    return idx < p->count ? &p->tokens[idx] : &p->tokens[p->count - 1];
}
static FcToken* Advance(FcParser* p) {
    FcToken* t = &p->tokens[p->pos];
    if (t->type != TT_Eof) p->pos++;
    if (t->type == TT_LParen || t->type == TT_LBracket) p->bracketDepth++;
    else if (t->type == TT_RParen || t->type == TT_RBracket) p->bracketDepth = p->bracketDepth - 1 > 0 ? p->bracketDepth - 1 : 0;
    return t;
}
static bool Match(FcParser* p, FcTokenType type) {
    if (!Check(p, type)) return false;
    Advance(p);
    return true;
}

static const char* KeywordHint(FcParser* p, const char* message, const FcToken* at) {
    if (at->type != TT_Identifier && fcIsKeyword(at->lexeme) && fcStartsWith(message, "Expected") && (strstr(message, "name") || strstr(message, "identifier")))
        return S(p, " ('%s'%s", at->lexeme, FC_KEYWORD_HINT_TAIL);
    return "";
}
_Noreturn static void Error(FcParser* p, const char* message, const FcToken* at) {
    fcThrow(p->ctx, FC_ERR_PARSE, at->line, at->column, NULL, S(p, "%s%s", message, KeywordHint(p, message, at)));
}
_Noreturn static void ParseError(FcParser* p, const char* message, int line, int column) { fcThrow(p->ctx, FC_ERR_PARSE, line, column, NULL, message); }

static FcToken* Expect(FcParser* p, FcTokenType type, const char* message) {
    if (Check(p, type)) return Advance(p);
    Error(p, message, Peek(p));
}

static bool ContinuesAtLineStart(FcParser* p, FcTokenType type) {
    return (type == TT_Dot && p->withVarStack->count == 0) || type == TT_QuestionDot || type == TT_QuestionQuestionDot
        || type == TT_And || type == TT_Or || type == TT_Plus || type == TT_Minus || type == TT_Slash || type == TT_Percent || type == TT_Caret || type == TT_Pipe
        || type == TT_Eq || type == TT_NotEq || type == TT_Lt || type == TT_LtEq || type == TT_Gt || type == TT_GtEq || type == TT_Question;
}
static bool LineContinues(FcParser* p) { return p->bracketDepth > 0 || !Peek(p)->newlineBefore || ContinuesAtLineStart(p, Peek(p)->type); }

static void ExpectStatementTerminator(FcParser* p) {
    if (Match(p, TT_Semicolon)) return;
    if (p->inDoBody && Check(p, TT_While)) return;
    if (Peek(p)->newlineBefore) return;
    if (Check(p, TT_RBrace) || Check(p, TT_Eof)) return;
    Error(p, "Expected ';' or a line break between statements", Peek(p));
}

static bool IsTypeKeyword(FcTokenType t) {
    return t == TT_KwBool || t == TT_KwInt || t == TT_KwFloat || t == TT_KwChar || t == TT_KwString || t == TT_KwByte || t == TT_KwWord || t == TT_Class || t == TT_Undefined;
}

/* ---- forward declarations ---- */
static FcStmt* ParseStatement(FcParser* p);
static FcBlockStmt* ParseBlock(FcParser* p);
static FcExpr* ParseExpression(FcParser* p);
static FcExpr* ParseConditional(FcParser* p);
static FcExpr* ParseUnary(FcParser* p);
static FcExpr* ParsePostfix(FcParser* p);
static FcExpr* ParsePostfixRest(FcParser* p, FcExpr* expr);
static FcExpr* ParsePrimary(FcParser* p);
static FcExpr* ParseShift(FcParser* p);
static FcTypeRef* ParseTypeRef(FcParser* p, bool allowArray);
static FcList* ParseParamList(FcParser* p, bool allowRef);
static FcList* ParseArgList(FcParser* p, FcList** names, bool allowNames);
static FcList* ParseArrayRanks(FcParser* p);
static FcList* ParseClassMember(FcParser* p);
static FcList* ParseProgram(FcParser* p);
static FcExpr* ParseLambdaTail(FcParser* p, int line, FcList* parms, FcExpr* onTarget);
static FcExpr* ParseAwait(FcParser* p);
static FcExpr* ParseProbe(FcParser* p);
static bool IsUnitDeclStart(FcParser* p);
static FcStmt* ParseUnitDecl(FcParser* p);
static bool NextLooksLikeTypeThenName(FcParser* p);
static int TypeArgListEnd(FcParser* p, int offset);
static FcList* ParseTypeArgRefs(FcParser* p);
static const char* ParseTypeAnnotationName(FcParser* p);
static const char* ParseDottedName(FcParser* p, const char* what);
static bool IsCustomUnitWordAt(FcParser* p, int offset);
static FcStmt* ParseClassDecl(FcParser* p);
static FcStmt* ParseActorDecl(FcParser* p);
static FcStmt* ParseRecordDecl(FcParser* p);
static bool IsRecordStart(FcParser* p);
static FcList* ParseOptionalTypeParamNames(FcParser* p);
static FcList* ParseWhereClauses(FcParser* p, FcList* typeParamNames, int declLine);
static FcList* ParseAttributes(FcParser* p);
static bool IsProbeOperandNext(FcParser* p);
static bool IsParenLambda(FcParser* p);
static bool IsAwaitStart(FcParser* p);

static FcParser* fcParserNew(FcCtx* c, FcTokens tokens) {
    FcParser* p = (FcParser*)fcAlloc(c, sizeof *p);
    p->ctx = c;
    p->tokens = tokens.items;
    p->count = tokens.count;
    p->usingNamespaces = fcListNew(c);
    p->withVarStack = fcListNew(c);
    p->suppressColonPostfixAtDepth = FC_NULL_INT;
    p->enumDecls = fcListNew(c);
    p->hoistedInlineC = fcListNew(c);
    p->captureUses = fcListNew(c);
    p->braceArrayTokens = fcListNew(c);
    return p;
}

/* ---- namespaces of the program ---- */
static void FindNamespaces(FcCtx* c, FcTokens* tokens, FbMap* into) {
    int* openDepth = (int*)fcAlloc(c, sizeof(int) * (size_t)(tokens->count + 1));
    const char** openName = (const char**)fcAlloc(c, sizeof(char*) * (size_t)(tokens->count + 1));
    int open = 0, depth = 0, i;
    for (i = 0; i < tokens->count; i++) {
        FcToken* t = &tokens->items[i];
        FcList* parts;
        int j, k;
        const char* prefix;
        const char* full;
        if (t->type == TT_LBrace) { depth++; continue; }
        if (t->type == TT_RBrace) {
            depth--;
            while (open > 0 && openDepth[open - 1] > depth) open--;
            continue;
        }
        if (t->type != TT_Namespace) continue;
        parts = fcListNew(c);
        j = i + 1;
        while (j < tokens->count && tokens->items[j].type == TT_Identifier) {
            fcListAdd(c, parts, (void*)tokens->items[j].lexeme);
            if (j + 1 < tokens->count && tokens->items[j + 1].type == TT_Dot) j += 2; else { j++; break; }
        }
        if (parts->count == 0 || j >= tokens->count || tokens->items[j].type != TT_LBrace) continue;
        prefix = open > 0 ? fcFmt(c, "%s.", openName[open - 1]) : "";
        full = "";
        for (k = 0; k < parts->count; k++) {
            full = full[0] == 0 ? (const char*)parts->items[k] : fcFmt(c, "%s.%s", full, (const char*)parts->items[k]);
            fbMapPut(into, fcFmt(c, "%s%s", prefix, full), (void*)1);
        }
        openDepth[open] = depth + 1;
        openName[open] = fcFmt(c, "%s%s", prefix, full);
        open++;
        i = j - 1;
    }
}

static const char* fcParentNamespace(FcParser* p, const char* ns) {
    const char* dot = strrchr(ns, '.');
    return dot ? fcStrN(p->ctx, ns, (size_t)(dot - ns)) : NULL;
}

static bool IsNamespaceName(FcParser* p, const char* name) {
    FbMap* known = &p->ctx->declaredNamespaces;
    const char* ns;
    int i;
    if (known->count == 0) return false;
    if (fbMapHas(known, name)) return true;
    for (ns = p->currentNamespace; ns && ns[0]; ns = fcParentNamespace(p, ns))
        if (fbMapHas(known, S(p, "%s.%s", ns, name))) return true;
    for (i = 0; i < p->usingNamespaces->count; i++)
        if (fbMapHas(known, S(p, "%s.%s", (const char*)p->usingNamespaces->items[i], name))) return true;
    return false;
}

static FcParser* ForSource(FcCtx* c, FcTokens tokens, FcProcessed* src, int sourceIndex) {
    FcParser* p = fcParserNew(c, tokens);
    p->usingNamespaces = src->usings;
    p->sourceIndex = sourceIndex;
    p->fileName = src->name;
    /* (ProcessedSource.Qualify: none for the dumps; Parser.SourceImportNamespaces is for the resolver) */
    return p;
}

/* CurrentNamespaces: the current namespace first, then the `#using`s */
static FcList* CurrentNamespaces(FcParser* p) {
    FcList* l;
    int i;
    if (!p->currentNamespace) return p->usingNamespaces;
    l = L(p);
    l->own = true;
    LA(p, l, p->currentNamespace);
    for (i = 0; i < p->usingNamespaces->count; i++) LA(p, l, p->usingNamespaces->items[i]);
    return l;
}
static const char* QualifyDeclName(FcParser* p, const char* simpleName) {
    return p->currentNamespace == NULL ? simpleName : S(p, "%s.%s", p->currentNamespace, simpleName);
}

/* ---- units of the program ---- */
typedef struct FcUnitDeclAt { FcUnitDeclStmt* decl; const char* file; } FcUnitDeclAt;

static bool fcAllUnits(const char* s, bool (*pred)(uint16_t)) {
    const unsigned char* q = (const unsigned char*)s;
    while (*q) if (!pred(fc8Next(&q))) return false;
    return true;
}
static bool fcIsLetterOrDigitOrUnderscore(uint16_t c) { return fcIsLetterOrDigit(c) || c == '_'; }

static bool IsWord(const FcToken* t) {
    const char* rest;
    uint16_t first;
    if (t->type == TT_IntLiteral || t->type == TT_FloatLiteral || t->type == TT_StringLiteral || t->type == TT_Eof || !t->lexeme[0]) return false;
    first = fc8First(t->lexeme, &rest);
    return (fcIsLetter(first) || first == '_') && fcAllUnits(t->lexeme, fcIsLetterOrDigitOrUnderscore);
}

static bool IsCustomUnitWordAt(FcParser* p, int offset) {
    FcToken* t = PeekAt(p, offset);
    return t->type != TT_Identifier && IsWord(t) && fcIsCustomUnitWord(p->ctx, t->lexeme);
}

static FcList* FindUnitDecls(FcCtx* c, FcTokens tokens, int sourceIndex) {
    FcList* found = fcListNew(c);
    int depth = 0, i;
    for (i = 0; i < tokens.count; i++) {
        FcToken* t = &tokens.items[i];
        if (t->type == TT_LBrace) depth++;
        else if (t->type == TT_RBrace) depth--;
        else if (depth == 0 && t->type == TT_Identifier && (streq(t->lexeme, "unit") || streq(t->lexeme, "prefixed") || streq(t->lexeme, "unprefixed"))
                 && (i == 0 || t->newlineBefore || tokens.items[i - 1].type == TT_Semicolon || tokens.items[i - 1].type == TT_RBrace)) {
            FcParser* parser = fcParserNew(c, tokens);
            parser->pos = i;
            parser->sourceIndex = sourceIndex;
            if (!IsUnitDeclStart(parser)) continue;
            fcListAdd(c, found, ParseUnitDecl(parser));
            i = parser->pos - 1;
        }
    }
    return found;
}

_Noreturn static void UnitFail(FcCtx* c, FcUnitDeclStmt* d, const char* file, const char* message, int line) {
    fcThrow(c, FC_ERR_PARSE, line == FC_NULL_INT ? d->h.line : line, 0, file, message);
}

static bool MentionsValue(FcExpr* e) {
    switch (e->kind) {
        case EK_IdentifierExpr: return streq(((FcIdentifierExpr*)e)->name, "value");
        case EK_BinaryExpr: return MentionsValue(((FcBinaryExpr*)e)->left) || MentionsValue(((FcBinaryExpr*)e)->right);
        case EK_UnaryExpr: return MentionsValue(((FcUnaryExpr*)e)->operand);
        default: return false;
    }
}

/* EvalConversion; false where the C# code throws its InvalidOperationException */
static bool EvalConversion(FcExpr* e, double value, double* out) {
    double a, b;
    switch (e->kind) {
        case EK_LiteralExpr: {
            FcValue* v = &((FcLiteralExpr*)e)->value;
            if (v->kind == VK_Int && !v->unit) { *out = (double)v->i; return true; }
            if (v->kind == VK_Float && !v->unit) { *out = v->f; return true; }
            return false;
        }
        case EK_IdentifierExpr: if (streq(((FcIdentifierExpr*)e)->name, "value")) { *out = value; return true; } return false;
        case EK_MemberExpr: {
            FcMemberExpr* m = (FcMemberExpr*)e;
            if (m->target->kind == EK_IdentifierExpr && streq(((FcIdentifierExpr*)m->target)->name, "Math")) {
                if (streq(m->name, "PI")) { *out = 3.14159265358979323846; return true; }
                if (streq(m->name, "E")) { *out = 2.7182818284590452354; return true; }
            }
            return false;
        }
        case EK_UnaryExpr: {
            FcUnaryExpr* u = (FcUnaryExpr*)e;
            if (u->op != UOP_Negate || !EvalConversion(u->operand, value, &a)) return false;
            *out = -a;
            return true;
        }
        case EK_BinaryExpr: {
            FcBinaryExpr* x = (FcBinaryExpr*)e;
            if (x->op != BOP_Add && x->op != BOP_Sub && x->op != BOP_Mul && x->op != BOP_Div && x->op != BOP_Power) return false;
            if (!EvalConversion(x->left, value, &a) || !EvalConversion(x->right, value, &b)) return false;
            switch (x->op) {
                case BOP_Add: *out = a + b; break;
                case BOP_Sub: *out = a - b; break;
                case BOP_Mul: *out = a * b; break;
                case BOP_Div: *out = a / b; break;
                default: *out = pow(a, b); break;
            }
            return true;
        }
        default: return false;
    }
}

static bool AffineOf(FcExpr* conversion, double* A, double* B) {
    double at0, at2, at10, atFar, a, b, size;
    if (!MentionsValue(conversion)) {
        if (!EvalConversion(conversion, 0, &at0)) return false;
        *A = at0; *B = 0;
        return true;
    }
    if (!EvalConversion(conversion, 0, &at0) || !EvalConversion(conversion, 2, &at2) || !EvalConversion(conversion, 10, &at10) || !EvalConversion(conversion, 1024, &atFar))
        return false;
    a = fcNiceFactor((atFar - at0) / 1024);
    b = at0 == 0 ? 0 : fcNiceFactor(at0);
    size = fmax(1.0, fmax(fabs(a), fabs(b)));
    if (isnan(a) || isinf(a) || isnan(b) || isinf(b)) return false;
    if (fabs(at2 - (2 * a + b)) > 1e-9 * size || fabs(at10 - (10 * a + b)) > 1e-9 * size * 10) return false;
    *A = a; *B = b;
    return true;
}

static const char* UnitConverterClassName(FcCtx* c, const char* symbol) { return fcFmt(c, "__unit_%s", symbol); }

static FcCustomUnit* fcCustomUnitNew(FcCtx* c, FcUnitDeclStmt* d, bool prefixed) {
    FcCustomUnit* u = (FcCustomUnit*)fcAlloc(c, sizeof *u);
    u->name = d->name;
    u->symbol = d->symbol;
    u->prefixed = prefixed;
    fcUnitSetUnitless(&u->unit);
    return u;
}

static FcCustomUnit* DefineUnit(FcCtx* c, FcUnitDeclStmt* d, const char* file) {
    FcList* cs = d->coerces;
    int i;
    bool anyUnitless = false, anyNotUnitless = false, haveDims = false, haveFunctions = false;
    double scale = 0, offset = 0;
    FcUnitCoerce* to = NULL;
    FcUnitCoerce* from = NULL;
    FcUnit dims;
    FcCustomUnit* u;
    if (cs->count == 0) {
        u = fcCustomUnitNew(c, d, d->prefixed);
        fcUnitAddDim(&u->unit, d->symbol, 1);
        return u;
    }
    for (i = 0; i < cs->count; i++) {
        FcUnit t;
        fcUnitParse(c, ((FcUnitCoerce*)cs->items[i])->targetUnit, &t);
        if (fcUnitIsUnitless(&t)) anyUnitless = true; else anyNotUnitless = true;
    }
    if (anyUnitless) {
        FcUnitCoerce* toPlain = NULL;
        FcUnitCoerce* fromPlain = NULL;
        if (anyNotUnitless) UnitFail(c, d, file, "A unit that converts into a plain number ('coerce to unitless') converts into nothing else", d->h.line);
        for (i = 0; i < cs->count; i++) {
            FcUnitCoerce* cc = (FcUnitCoerce*)cs->items[i];
            if (cc->direction == COE_To && !toPlain) toPlain = cc;
            if (cc->direction == COE_From && !fromPlain) fromPlain = cc;
        }
        if (!toPlain || !fromPlain || cs->count != 2)
            UnitFail(c, d, file, "A unit that becomes a plain number needs exactly 'coerce to unitless => ...' and 'coerce from unitless => ...'", ((FcUnitCoerce*)cs->items[0])->line);
        if (d->prefixed) UnitFail(c, d, file, fcFmt(c, "A unit that becomes a plain number takes no prefixes - declare '%s' without 'prefixed'", d->symbol), d->h.line);
        u = fcCustomUnitNew(c, d, false);
        fcUnitAddDim(&u->unit, d->symbol, 1);
        u->unit.unitlessConverter = UnitConverterClassName(c, d->symbol);
        return u;
    }
    fcUnitSetUnitless(&dims);
    for (i = 0; i < cs->count; i++) {
        FcUnitCoerce* cc = (FcUnitCoerce*)cs->items[i];
        FcUnit target;
        double a, b, s1, o1, thisScale, thisOffset;
        fcUnitParse(c, cc->targetUnit, &target);
        if (fcUnitIsUnitless(&target)) UnitFail(c, d, file, fcFmt(c, "'coerce' needs a unit to convert from or to, not '%s'", cc->targetUnit), cc->line);
        if (fcUnitDim(&target, d->symbol) >= 0) UnitFail(c, d, file, fcFmt(c, "The unit '%s' cannot be converted into itself", d->symbol), cc->line);
        if (target.converter)
            UnitFail(c, d, file, fcFmt(c, "'%s' is converted by functions - convert '%s' into its reference unit instead", cc->targetUnit, d->symbol), cc->line);
        if (cc->direction == COE_To) to = cc;
        if (cc->direction == COE_From) from = cc;
        if (!AffineOf(cc->conversion, &a, &b)) {
            if (cc->direction == COE_Factor)
                UnitFail(c, d, file, fcFmt(c, "'coerce %s => ...' is a factor and an offset computed while compiling (numbers, 'value', + - * / ^, Math.PI, Math.E) - any other conversion is written 'coerce to %s => ...' and 'coerce from %s => ...'",
                    cc->targetUnit, cc->targetUnit, cc->targetUnit), cc->line);
            haveFunctions = true;
            continue;
        }
        if (a == 0) UnitFail(c, d, file, fcFmt(c, "The conversion of '%s' gives the same value for every value", d->symbol), cc->line);
        s1 = cc->direction == COE_From ? 1.0 / a : a;
        o1 = cc->direction == COE_From ? -b / a : b;
        thisScale = fcNiceFactor(s1 * target.scale);
        thisOffset = o1 * target.scale + target.offset;
        thisOffset = thisOffset == 0 ? 0 : fcNiceFactor(thisOffset);
        if (!haveDims) { dims = target; haveDims = true; scale = thisScale; offset = thisOffset; continue; }
        if (!fcUnitCompatible(&target, &dims)) UnitFail(c, d, file, fcFmt(c, "The conversions of '%s' name units of different dimensions", d->symbol), cc->line);
        if (fabs(thisScale - scale) > 1e-9 * scale || fabs(thisOffset - offset) > 1e-9 * fmax(1.0, fabs(offset)))
            UnitFail(c, d, file, fcFmt(c, "The conversions of '%s' do not agree (1%s is %s%s by the first one, %s%s by this one)", d->symbol, d->symbol,
                fcG10(c, (scale + offset - target.offset) / target.scale), cc->targetUnit, fcG10(c, s1 + o1), cc->targetUnit), cc->line);
    }
    if (haveFunctions) {
        FcUnit reference, other;
        if (!to || !from) {
            FcUnitCoerce* any = to ? to : from;
            UnitFail(c, d, file, fcFmt(c, "A conversion that is not a factor and an offset needs both directions: 'coerce to %s => ...' and 'coerce from %s => ...'",
                any->targetUnit, any->targetUnit), any->line);
        }
        if (cs->count != 2) UnitFail(c, d, file, "A unit with conversion functions has exactly one 'coerce to' and one 'coerce from'", ((FcUnitCoerce*)cs->items[2])->line);
        fcUnitParse(c, to->targetUnit, &reference);
        fcUnitParse(c, from->targetUnit, &other);
        if (!fcUnitEquals(&reference, &other))
            UnitFail(c, d, file, fcFmt(c, "'coerce to' and 'coerce from' of '%s' must name the same unit ('%s', '%s')", d->symbol, to->targetUnit, from->targetUnit), from->line);
        if (d->prefixed) UnitFail(c, d, file, fcFmt(c, "A unit with conversion functions takes no prefixes - declare '%s' without 'prefixed'", d->symbol), d->h.line);
        u = fcCustomUnitNew(c, d, false);
        u->unit = reference;
        u->unit.converter = UnitConverterClassName(c, d->symbol);
        u->unit.unitlessConverter = NULL;
        return u;
    }
    if (d->prefixed && offset != 0) UnitFail(c, d, file, fcFmt(c, "A unit with an offset takes no prefixes - declare '%s' without 'prefixed'", d->symbol), d->h.line);
    u = fcCustomUnitNew(c, d, d->prefixed);
    u->unit = dims;
    u->unit.scale = scale;
    u->unit.offset = offset;
    u->unit.converter = NULL;
    u->unit.unitlessConverter = NULL;
    return u;
}

/* a factor of a unit text (`in`, `kV`) that is a declared unit, by its declaration (the words: an ordered map in the arena of the context) */
static FcUnitDeclStmt* DeclaredFactor(FbMap* words, const char* factor) {
    FcUnitDeclStmt* exact = (FcUnitDeclStmt*)fbMapGet(words, factor);
    const char* rest;
    FcUnitDeclStmt* prefixed;
    if (exact) return exact;
    if (fc8Units(factor) < 2) return NULL;
    fc8First(factor, &rest);
    prefixed = (FcUnitDeclStmt*)fbMapGet(words, rest);
    if (prefixed && prefixed->prefixed && streq(prefixed->symbol, rest) && fcBuiltInMeaning(factor) == NULL) return prefixed;
    return NULL;
}

static bool UnitNeedsDefined(FcCtx* c, FbMap* words, FcUnitDeclStmt* d, FcList* defined) {
    int i, k;
    for (i = 0; i < d->coerces->count; i++) {
        const char* s = ((FcUnitCoerce*)d->coerces->items[i])->targetUnit;
        for (;;) {
            const char* e = s;
            FcUnitDeclStmt* need;
            while (*e && *e != '/' && *e != '*' && *e != '^') e++;
            need = DeclaredFactor(words, fcStrN(c, s, (size_t)(e - s)));
            if (need) {
                bool found = false;
                for (k = 0; k < defined->count; k++) if (streq(((FcCustomUnit*)defined->items[k])->symbol, need->symbol)) found = true;
                if (!found) return false;
            }
            if (!*e) break;
            s = e + 1;
        }
    }
    return true;
}

/* the map of the words of the units lives as long as the context (its memory is freed with it: kept on the context's list of maps) */
static void DefineUnits(FcCtx* c, FcList* decls) {
    FbMap* words;
    FcList* defined;
    FcList* pending;
    int i;
    fcUseCustomUnits(c, NULL);
    if (decls->count == 0) return;
    words = &c->unitWords;
    fbMapFree(words);
    for (i = 0; i < decls->count; i++) {
        FcUnitDeclAt* at = (FcUnitDeclAt*)decls->items[i];
        FcUnitDeclStmt* d = at->decl;
        const char* list[2];
        int n = 0, k;
        list[n++] = d->symbol;
        if (!streq(d->name, d->symbol)) list[n++] = d->name;
        for (k = 0; k < n; k++) {
            const char* word = list[k];
            FcUnitDeclStmt* other;
            if (fcBuiltInMeaning(word)) UnitFail(c, d, at->file, fcFmt(c, "'%s' is already a unit of fire - choose another name or symbol for '%s'", word, d->name), FC_NULL_INT);
            if ((other = (FcUnitDeclStmt*)fbMapGet(words, word)) != NULL)
                UnitFail(c, d, at->file, fcFmt(c, "The unit '%s' is declared twice (first in line %d)", word, other->h.line), FC_NULL_INT);
            fbMapPut(words, word, d);
        }
    }
    defined = fcListNew(c);
    pending = fcListCopy(c, decls);
    while (pending->count > 0) {
        int ready = -1;
        FcUnitDeclAt* r;
        for (i = 0; i < pending->count && ready < 0; i++)
            if (UnitNeedsDefined(c, words, ((FcUnitDeclAt*)pending->items[i])->decl, defined)) ready = i;
        if (ready < 0) {
            FcUnitDeclAt* first = (FcUnitDeclAt*)pending->items[0];
            FcBuf names = {0};
            fcBufStr(&names, "");
            for (i = 0; i < pending->count; i++) { if (i) fcBufStr(&names, ", "); fcBufStr(&names, ((FcUnitDeclAt*)pending->items[i])->decl->symbol); }
            UnitFail(c, first->decl, first->file, fcFmt(c, "The unit '%s' is defined by units that are defined by it (%s) - one of them needs a 'coerce' to a unit outside of the circle, or none",
                first->decl->symbol, fcBufDone(c, &names)), FC_NULL_INT);
        }
        r = (FcUnitDeclAt*)pending->items[ready];
        fcListRemoveAt(pending, ready);
        fcListAdd(c, defined, DefineUnit(c, r->decl, r->file));
        fcUseCustomUnits(c, defined);
    }
}

static FcList* UnitConverterClasses(FcCtx* c, FcList* decls) {
    FcList* result = fcListNew(c);
    int i, k;
    for (i = 0; i < decls->count; i++) {
        FcUnitDeclStmt* d = ((FcUnitDeclAt*)decls->items[i])->decl;
        FcCustomUnit* own = NULL;
        const char* className;
        FcUnitCoerce* cTo = NULL;
        FcUnitCoerce* cFrom = NULL;
        FcList* members;
        for (k = 0; k < c->customUnits->count && !own; k++) if (streq(((FcCustomUnit*)c->customUnits->items[k])->symbol, d->symbol)) own = (FcCustomUnit*)c->customUnits->items[k];
        if (!own) continue;
        className = own->unit.converter ? own->unit.converter : own->unit.unitlessConverter;
        if (!className) continue;
        for (k = 0; k < d->coerces->count; k++) {
            FcUnitCoerce* cc = (FcUnitCoerce*)d->coerces->items[k];
            if (cc->direction == COE_To && !cTo) cTo = cc;
            if (cc->direction == COE_From && !cFrom) cFrom = cc;
        }
        members = fcListNew(c);
        for (k = 0; k < 2; k++) {
            FcUnitCoerce* cc = k == 0 ? cTo : cFrom;
            FcTypeRef* floatType = (FcTypeRef*)fcAlloc(c, sizeof(FcTypeRef));
            FcLambdaParam* lp = (FcLambdaParam*)fcAlloc(c, sizeof(FcLambdaParam));
            FcList* params = fcListNew(c);
            FcList* body = fcListNew(c);
            floatType->baseName = "float"; floatType->bitWidth = FC_NULL_INT;
            lp->name = "value"; lp->type = floatType; lp->arrayRanks = fcListNew(c);
            fcListAdd(c, params, lp);
            fcListAdd(c, body, FC_NEW(c, FcReturnStmt, .h = {SK_ReturnStmt, d->h.source, cc->line}, .value = cc->conversion));
            fcListAdd(c, members, FC_NEW(c, FcMethodDecl, .h = {SK_MethodDecl, d->h.source, cc->line}, .returnType = floatType, .name = k == 0 ? "To" : "From",
                .params = params, .body = FC_NEW(c, FcBlockStmt, .h = {SK_BlockStmt, d->h.source, cc->line}, .statements = body), .isStatic = true));
        }
        fcListAdd(c, result, FC_NEW(c, FcClassDecl, .h = {SK_ClassDecl, d->h.source, d->h.line}, .name = className, .members = members));
    }
    return result;
}

/* ---- after parsing: the program of all sources ---- */
static const char* const ReplaceablePreludeClasses[] = { "Exception", "Library", "LibraryException" };

static FcList* LetOwnExceptionReplacePrelude(FcCtx* c, FcList* program, int sourceCount) {
    int i, k;
    bool prelude = false;
    FcList* own;
    FcList* result;
    if (sourceCount < 2) return program;
    for (i = 0; i < program->count; i++) {
        FcStmt* s = (FcStmt*)program->items[i];
        if (s->kind == SK_ClassDecl && s->source == 0 && streq(((FcClassDecl*)s)->name, "IndexOutOfBoundsException")) prelude = true;
    }
    if (!prelude) return program;
    own = fcListNew(c);
    for (k = 0; k < 3; k++)
        for (i = 0; i < program->count; i++) {
            FcStmt* s = (FcStmt*)program->items[i];
            if (s->kind == SK_ClassDecl && s->source > 0 && streq(((FcClassDecl*)s)->name, ReplaceablePreludeClasses[k])) { fcListAdd(c, own, (void*)ReplaceablePreludeClasses[k]); break; }
        }
    if (own->count == 0) return program;
    result = fcListNew(c);
    for (i = 0; i < program->count; i++) {
        FcStmt* s = (FcStmt*)program->items[i];
        if (s->kind == SK_ClassDecl && s->source == 0 && fcListHasStr(own, ((FcClassDecl*)s)->name)) continue;
        fcListAdd(c, result, s);
    }
    return result;
}

static bool fcNameIn(FcList* names, const char* name) { return fcListHasStr(names, name); }

static FcList* DisambiguateGenericClasses(FcCtx* c, FcList* program) {
    FcList* nonGenericNames = fcListNew(c);
    FcList* nonGenericInterfaces = fcListNew(c);
    FcList* result;
    int i;
    for (i = 0; i < program->count; i++) {
        FcStmt* s = (FcStmt*)program->items[i];
        if (s->kind == SK_ClassDecl && (!((FcClassDecl*)s)->typeParams || ((FcClassDecl*)s)->typeParams->count == 0)) fcListAdd(c, nonGenericNames, (void*)((FcClassDecl*)s)->name);
        if (s->kind == SK_InterfaceDecl && (!((FcInterfaceDecl*)s)->typeParams || ((FcInterfaceDecl*)s)->typeParams->count == 0)) fcListAdd(c, nonGenericInterfaces, (void*)((FcInterfaceDecl*)s)->name);
    }
    if (nonGenericNames->count == 0 && nonGenericInterfaces->count == 0) return program;
    result = fcListNew(c);
    for (i = 0; i < program->count; i++) {
        FcStmt* s = (FcStmt*)program->items[i];
        if (s->kind == SK_ClassDecl && ((FcClassDecl*)s)->typeParams && ((FcClassDecl*)s)->typeParams->count > 0 && fcNameIn(nonGenericNames, ((FcClassDecl*)s)->name)) {
            FcClassDecl* g = (FcClassDecl*)fcNodeDup(c, s, sizeof(FcClassDecl));
            g->name = fcFmt(c, "%s`%d", g->name, g->typeParams->count);
            fcListAdd(c, result, g);
        } else if (s->kind == SK_InterfaceDecl && ((FcInterfaceDecl*)s)->typeParams && ((FcInterfaceDecl*)s)->typeParams->count > 0
                   && fcNameIn(nonGenericInterfaces, ((FcInterfaceDecl*)s)->name)) {
            FcInterfaceDecl* g = (FcInterfaceDecl*)fcNodeDup(c, s, sizeof(FcInterfaceDecl));
            g->name = fcFmt(c, "%s`%d", g->name, g->typeParams->count);
            fcListAdd(c, result, g);
        } else fcListAdd(c, result, s);
    }
    return result;
}

static bool IsExtendable(const char* t) {
    return streq(t, "string") || streq(t, "char") || streq(t, "int") || streq(t, "float") || streq(t, "bool") || streq(t, "array");
}

/* TypeRef.ResolveBaseName against a set of names */
static const char* ResolveBaseName(FcCtx* c, const FcTypeRef* t, FcList* known) {
    int i;
    if (fcStartsWith(t->baseName, "global.")) return t->baseName + 7;
    if (t->namespaces && t->namespaces->own && !strchr(t->baseName, '.')) {
        const char* own = fcFmt(c, "%s.%s", (const char*)t->namespaces->items[0], t->baseName);
        if (fcNameIn(known, own)) return own;
    }
    if (fcNameIn(known, t->baseName)) return t->baseName;
    if (t->namespaces)
        for (i = 0; i < t->namespaces->count; i++) {
            const char* candidate = fcFmt(c, "%s.%s", (const char*)t->namespaces->items[i], t->baseName);
            if (fcNameIn(known, candidate)) return candidate;
        }
    return t->baseName;
}

static FcList* MergeClassExtensions(FcCtx* c, FcList* program) {
    FcList* extensions = fcListNew(c);
    FcList* baseTypeNames = fcListNew(c);
    FcList* baseTypeLists = fcListNew(c);
    FcList* rest = fcListNew(c);
    FcList* result;
    FcList* knownNames = fcListNew(c);
    FcList* extendedNames = fcListNew(c);
    int i, k, m;
    for (i = 0; i < program->count; i++) {
        FcStmt* s = (FcStmt*)program->items[i];
        if (s->kind == SK_ClassExtensionDecl && IsExtendable(((FcClassExtensionDecl*)s)->targetRef->baseName)) {
            const char* name = ((FcClassExtensionDecl*)s)->targetRef->baseName;
            int at = -1;
            for (k = 0; k < baseTypeNames->count; k++) if (streq((const char*)baseTypeNames->items[k], name)) at = k;
            if (at < 0) { fcListAdd(c, baseTypeNames, (void*)name); fcListAdd(c, baseTypeLists, fcListNew(c)); at = baseTypeNames->count - 1; }
            fcListAdd(c, (FcList*)baseTypeLists->items[at], s);
        } else if (s->kind == SK_ClassExtensionDecl) fcListAdd(c, extensions, s);
        else fcListAdd(c, rest, s);
    }
    for (k = 0; k < baseTypeNames->count; k++) {
        FcList* list = (FcList*)baseTypeLists->items[k];
        FcClassExtensionDecl* first = (FcClassExtensionDecl*)list->items[0];
        FcList* members = fcListNew(c);
        for (i = 0; i < list->count; i++) {
            FcList* ms = ((FcClassExtensionDecl*)list->items[i])->members;
            for (m = 0; m < ms->count; m++) fcListAdd(c, members, ms->items[m]);
        }
        fcListAdd(c, rest, FC_NEW(c, FcClassDecl, .h = {SK_ClassDecl, first->h.source, first->h.line}, .name = fcFmt(c, "$%s", (const char*)baseTypeNames->items[k]), .members = members));
    }
    if (extensions->count == 0) return rest;
    for (i = 0; i < rest->count; i++) if (((FcStmt*)rest->items[i])->kind == SK_ClassDecl) fcListAdd(c, knownNames, (void*)((FcClassDecl*)rest->items[i])->name);
    result = fcListNew(c);
    for (i = 0; i < rest->count; i++) {
        FcStmt* s = (FcStmt*)rest->items[i];
        if (s->kind == SK_ClassDecl) {
            FcClassDecl* cd = (FcClassDecl*)s;
            FcList* extra = fcListNew(c);
            for (k = 0; k < extensions->count; k++) {
                FcClassExtensionDecl* e = (FcClassExtensionDecl*)extensions->items[k];
                if (streq(ResolveBaseName(c, e->targetRef, knownNames), cd->name))
                    for (m = 0; m < e->members->count; m++) fcListAdd(c, extra, e->members->items[m]);
            }
            if (extra->count > 0) {
                FcList* members = fcListCopy(c, cd->members);
                for (m = 0; m < extra->count; m++) fcListAdd(c, members, extra->items[m]);
                cd = (FcClassDecl*)fcNodeDup(c, cd, sizeof *cd);
                cd->members = members;
                fcListAdd(c, extendedNames, (void*)cd->name);
            }
            fcListAdd(c, result, cd);
            continue;
        }
        fcListAdd(c, result, s);
    }
    for (k = 0; k < extensions->count; k++) {
        FcClassExtensionDecl* e = (FcClassExtensionDecl*)extensions->items[k];
        if (!fcNameIn(extendedNames, ResolveBaseName(c, e->targetRef, knownNames)))
            fcThrow(c, FC_ERR_PARSE, e->h.line, 1, NULL, fcFmt(c, "'class extends %s' - class '%s' is not known in the same program (extensions cannot create a new class).",
                e->targetRef->baseName, e->targetRef->baseName));
    }
    return result;
}

static void FlattenInto(FcCtx* c, FcList* program, FcList* result) {
    int i;
    for (i = 0; i < program->count; i++) {
        FcStmt* s = (FcStmt*)program->items[i];
        if (s->kind == SK_NamespaceDecl) FlattenInto(c, ((FcNamespaceDecl*)s)->members, result);
        else fcListAdd(c, result, s);
    }
}
static FcList* FlattenNamespaceWrappers(FcCtx* c, FcList* program) { FcList* r = fcListNew(c); FlattenInto(c, program, r); return r; }

/* ParseMultiple: a ParseException of a source without a file of its own gets the name of the source */
typedef struct FcMultiState { FcProcessed** sources; int count; FcTokens* tokenLists; FcList* unitDecls; FcList* combined; } FcMultiState;

static void fcMultiLex(FcCtx* c, FcMultiState* m, int i) {
    FC_TRY(c) {
        FcList* decls;
        int k;
        m->tokenLists[i] = fcTokenize(c, m->sources[i]->source, m->sources[i]->length);
        decls = FindUnitDecls(c, m->tokenLists[i], i);
        for (k = 0; k < decls->count; k++) {
            FcUnitDeclAt* at = (FcUnitDeclAt*)fcAlloc(c, sizeof *at);
            at->decl = (FcUnitDeclStmt*)decls->items[k];
            at->file = m->sources[i]->name;
            fcListAdd(c, m->unitDecls, at);
        }
        FindNamespaces(c, &m->tokenLists[i], &c->declaredNamespaces);
    } FC_CATCH(c) {
        if (c->err.kind == FC_ERR_PARSE && m->sources[i]->name && !c->err.file) c->err.file = m->sources[i]->name;
        fcRethrow(c);
    } FC_END_TRY;
}
static void fcMultiParse(FcCtx* c, FcMultiState* m, int i) {
    FC_TRY(c) {
        FcList* stmts = ParseProgram(ForSource(c, m->tokenLists[i], m->sources[i], i));
        int k;
        for (k = 0; k < stmts->count; k++) if (((FcStmt*)stmts->items[k])->kind != SK_UnitDeclStmt) fcListAdd(c, m->combined, stmts->items[k]);
    } FC_CATCH(c) {
        if (c->err.kind == FC_ERR_PARSE && m->sources[i]->name && !c->err.file) c->err.file = m->sources[i]->name;
        fcRethrow(c);
    } FC_END_TRY;
}

FcList* fcParseMultiple(FcCtx* c, FcProcessed** sources, int count) {
    FcMultiState m;
    FcList* converters;
    bool argumentException = false;
    int i;
    m.sources = sources;
    m.count = count;
    m.tokenLists = (FcTokens*)fcAlloc(c, sizeof(FcTokens) * (size_t)count);
    m.unitDecls = fcListNew(c);
    m.combined = fcListNew(c);
    fbMapFree(&c->declaredNamespaces);
    for (i = 0; i < count; i++) fcMultiLex(c, &m, i);
    DefineUnits(c, m.unitDecls);
    for (i = 0; i < count; i++) fcMultiParse(c, &m, i);
    converters = UnitConverterClasses(c, m.unitDecls);
    for (i = 0; i < converters->count; i++) fcListAdd(c, m.combined, converters->items[i]);
    for (i = 0; i < m.combined->count; i++) {
        FcStmt* s = (FcStmt*)m.combined->items[i];
        if (s->kind == SK_ClassDecl && streq(((FcClassDecl*)s)->name, "ArgumentException")) argumentException = true;
    }
    if (!argumentException) {
        FcList* kept = fcListNew(c);
        for (i = 0; i < m.combined->count; i++) {
            FcStmt* s = (FcStmt*)m.combined->items[i];
            if (s->kind == SK_ClassDecl && fcStartsWith(((FcClassDecl*)s)->name, "__enum_")) continue;
            fcListAdd(c, kept, s);
        }
        m.combined = kept;
    }
    return MergeClassExtensions(c, DisambiguateGenericClasses(c, LetOwnExceptionReplacePrelude(c, FlattenNamespaceWrappers(c, m.combined), count)));
}

/* ---- the program of one source ---- */
static FcList* EnumHelperClass(FcParser* p, FcEnumDecl* ed);

static bool TryParseWarn(FcParser* p);
static FcStmt* ParseGlobalHandlerDecl(FcParser* p);
static bool NextLooksLikeGlobalHandler(FcParser* p) {
    return PeekAt(p, 1)->type == TT_Terminate || (PeekAt(p, 1)->type == TT_Identifier && streq(PeekAt(p, 1)->lexeme, "threads"));
}

static FcList* ParseProgram(FcParser* p) {
    FcList* statements = L(p);
    int i, k;
    while (!Check(p, TT_Eof)) {
        if (Match(p, TT_Semicolon)) continue;
        if (TryParseWarn(p)) continue;
        if (Check(p, TT_Catch) && NextLooksLikeGlobalHandler(p)) LA(p, statements, ParseGlobalHandlerDecl(p));
        else if (IsUnitDeclStart(p)) LA(p, statements, ParseUnitDecl(p));
        else LA(p, statements, ParseStatement(p));
    }
    for (i = 0; i < p->enumDecls->count; i++) {
        FcList* helper = EnumHelperClass(p, (FcEnumDecl*)p->enumDecls->items[i]);
        for (k = 0; k < helper->count; k++) LA(p, statements, helper->items[k]);
    }
    for (i = 0; i < p->hoistedInlineC->count; i++) LA(p, statements, p->hoistedInlineC->items[i]);
    return statements;
}

static const char* EnumHelperClassName(FcParser* p, const char* enumName) {
    char* n = (char*)S(p, "__enum_%s", enumName);
    char* q;
    for (q = n; *q; q++) if (*q == '.') *q = '_';
    return n;
}

static FcList* EnumHelperClass(FcParser* p, FcEnumDecl* ed) {
    FcBuf name = {0}, parse = {0}, defined = {0}, values = {0}, source = {0};
    int64_t* seen = (int64_t*)fcAlloc(p->ctx, sizeof(int64_t) * (size_t)(ed->members->count + 1));
    int seenCount = 0, i, k;
    int64_t next = 0;
    FcParser* parser;
    fcBufStr(&name, ""); fcBufStr(&parse, ""); fcBufStr(&defined, ""); fcBufStr(&values, "");
    for (i = 0; i < ed->members->count; i++) {
        FcEnumMember* m = (FcEnumMember*)ed->members->items[i];
        int64_t value = m->valueExpr && m->valueExpr->kind == EK_LiteralExpr && ((FcLiteralExpr*)m->valueExpr)->value.kind == VK_Int ? ((FcLiteralExpr*)m->valueExpr)->value.i : next;
        bool isNew = true;
        next = (int64_t)((uint64_t)value + 1);
        for (k = 0; k < seenCount; k++) if (seen[k] == value) isNew = false;
        if (isNew) {
            seen[seenCount++] = value;
            fcBufFmt(&name, "if (v == %" PRId64 ") { return \"%s\" } ", value, m->name);
            fcBufFmt(&defined, "if (v == %" PRId64 ") { return true } ", value);
            fcBufFmt(&values, "%s%s.%s", values.len ? ", " : "", ed->name, m->name);
        }
        fcBufFmt(&parse, "if (s == \"%s\") { return %s.%s } ", m->name, ed->name, m->name);
    }
    fcBufFmt(&source, "class %s {\n", EnumHelperClassName(p, ed->name));
    fcBufFmt(&source, "    static string Name(any v) { %sreturn \"\" + (v!int) }\n", name.p);
    fcBufFmt(&source, "    static %s Parse(string s) { %sthrow new ArgumentException(\"'\" + s + \"' is not a member of '%s'.\") }\n", ed->name, parse.p, ed->name);
    fcBufFmt(&source, "    static bool IsDefined(any v) { %sreturn false }\n", defined.p);
    fcBufFmt(&source, "    static %s[] Values() { return [%s] }\n", ed->name, values.p);
    fcBufStr(&source, "}");
    fcBufFree(&name); fcBufFree(&parse); fcBufFree(&defined); fcBufFree(&values);
    parser = fcParserNew(p->ctx, fcTokenizeFc8(p->ctx, fcBufDone(p->ctx, &source)));
    parser->sourceIndex = p->sourceIndex;
    parser->fileName = p->fileName;
    return ParseProgram(parser);
}

/* ---- own units: `prefixed unit Volt: V`, `unit Inch: in { coerce m => 0.0254 }` ---- */
static bool IsUnitDeclStart(FcParser* p) {
    int i = 0;
    FcToken* u;
    if (Check(p, TT_Identifier) && (streq(Peek(p)->lexeme, "prefixed") || streq(Peek(p)->lexeme, "unprefixed"))) i = 1;
    u = PeekAt(p, i);
    return u->type == TT_Identifier && streq(u->lexeme, "unit") && IsWord(PeekAt(p, i + 1)) && PeekAt(p, i + 2)->type == TT_Colon
        && (i == 0 || PeekAt(p, 1)->line == Peek(p)->line);
}

static const char* UnitPowers(FcParser* p, const char* unit, const FcToken* at);
static const char* ParseCombinedUnitTail(FcParser* p);

static FcStmt* ParseUnitDecl(FcParser* p) {
    int line = Peek(p)->line;
    bool prefixed = false;
    FcToken* nameTok;
    FcToken* symbolTok;
    FcList* coerces = L(p);
    if (streq(Peek(p)->lexeme, "prefixed") || streq(Peek(p)->lexeme, "unprefixed")) prefixed = streq(Advance(p)->lexeme, "prefixed");
    Advance(p);
    nameTok = Advance(p);
    Expect(p, TT_Colon, "Expected ':' and the symbol of the unit after its name");
    symbolTok = Peek(p);
    if (!IsWord(symbolTok) || !fcAllUnits(symbolTok->lexeme, fcIsLetter) || symbolTok->line != nameTok->line)
        Error(p, "Expected the symbol of the unit after ':' - letters only ('in', 'V', 'Pa'), it stands directly after a number (5in)", symbolTok);
    Advance(p);
    if (Check(p, TT_LBrace)) {
        Advance(p);
        while (!Check(p, TT_RBrace)) {
            int coerceLine;
            FcCoerceDirection direction = COE_Factor;
            const char* target;
            FcUnitCoerce* uc;
            if (Match(p, TT_Semicolon)) continue;
            if (!(Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "coerce")))
                Error(p, "Expected 'coerce' in the body of a unit ('coerce m => 0.0254', 'coerce to m => value * 0.0254', 'coerce from m => value / 0.0254')", Peek(p));
            coerceLine = Advance(p)->line;
            if (Match(p, TT_From)) direction = COE_From;
            else if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "to") && PeekAt(p, 1)->type != TT_Arrow) { Advance(p); direction = COE_To; }
            if (!IsWord(Peek(p))) Error(p, "Expected the unit to convert from or to after 'coerce'", Peek(p));
            {
                const char* first = Advance(p)->lexeme;
                const char* tail = ParseCombinedUnitTail(p);
                target = UnitPowers(p, S(p, "%s%s", first, tail), Previous(p));
            }
            Expect(p, TT_Arrow, "Expected '=>' after the unit of 'coerce'");
            uc = (FcUnitCoerce*)fcAlloc(p->ctx, sizeof *uc);
            uc->line = coerceLine; uc->direction = direction; uc->targetUnit = target;
            uc->conversion = ParseExpression(p);
            LA(p, coerces, uc);
        }
        Expect(p, TT_RBrace, "Expected '}' after the conversions of the unit");
    }
    Match(p, TT_Semicolon);
    return (FcStmt*)FC_NEW(C_, FcUnitDeclStmt, .h = {SK_UnitDeclStmt, p->sourceIndex, line}, .name = nameTok->lexeme, .symbol = symbolTok->lexeme, .prefixed = prefixed,
        .coerces = coerces);
}

/* ---- statements ---- */
static const char* PlainName(FcParser* p, const char* name) {
    const char* tick = strchr(name, '`');
    return tick ? fcStrN(p->ctx, name, (size_t)(tick - name)) : name;
}

static bool TryParseWarn(FcParser* p) {
    FcToken* keyword;
    FcToken* id;
    FcToken* text;
    int64_t number;
    const char* warning;
    const char* className;
    if (!(Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "warn") && PeekAt(p, 1)->type == TT_IntLiteral && PeekAt(p, 2)->type == TT_Colon)) return false;
    keyword = Advance(p);
    id = Advance(p);
    Advance(p);
    text = Expect(p, TT_StringLiteral, "Expected the text of the warning (a string) after 'warn <id>:'");
    ExpectStatementTerminator(p);
    number = id->literal == FC_LIT_INT ? id->intValue : 0;
    if (number < 0 || number > 99999) Error(p, "The id of a warning ('warn <id>: \"text\"') is a whole number from 0 to 99999", id);
    warning = text->stringValue ? text->stringValue : text->lexeme;
    className = p->currentClassName ? p->currentClassName : p->extensionClassName;
    if (p->memberWarnings) {
        FcMemberWarning* w = (FcMemberWarning*)fcAlloc(p->ctx, sizeof *w);
        w->id = (int)number; w->text = warning; w->line = keyword->line;
        LA(p, p->memberWarnings, w);
    } else if (className) {
        FcDeclaredWarning* dw = (FcDeclaredWarning*)fcAlloc(p->ctx, sizeof *dw);
        dw->cls = PlainName(p, className); dw->target = FC_WARN_CLASS; dw->id = (int)number; dw->text = warning; dw->line = keyword->line; dw->file = p->fileName;
        fcDeclare(p->ctx, dw);
    } else fcWarn(p->ctx, (int)number, warning, keyword->line, p->fileName);
    return true;
}

static FcStmt* ParseSilence(FcParser* p);
static FcStmt* ParseDelete(FcParser* p);
static FcStmt* ParseReplace(FcParser* p);
static FcStmt* ParseVarDecl(FcParser* p);
static FcStmt* ParseReadonlyDecl(FcParser* p);
static FcStmt* ParseEnumDecl(FcParser* p, bool isFlags);
static FcStmt* AttributesBeforeClass(FcParser* p);
static FcStmt* ParseNamespaceDecl(FcParser* p);
static FcStmt* ParseInterfaceDecl(FcParser* p);
static FcStmt* ParseInlineCDecl(FcParser* p);
static FcStmt* ParseBareTypedDecl(FcParser* p, bool isReadonly, bool consumeTerminator);
static FcStmt* ParseIf(FcParser* p);
static FcStmt* ParseWhile(FcParser* p);
static FcStmt* ParseDoWhile(FcParser* p);
static FcStmt* ParseFor(FcParser* p);
static FcStmt* ParseForeach(FcParser* p);
static FcStmt* ParseReturn(FcParser* p);
static FcStmt* ParseThrowStmt(FcParser* p);
static FcStmt* ParseTry(FcParser* p);
static FcStmt* ParseDirective(FcParser* p);
static FcStmt* ParseExternDecl(FcParser* p);
static FcStmt* ParseUnsafeStmt(FcParser* p);
static FcStmt* ParseWithStmt(FcParser* p);
static FcStmt* ParseSwitchStmt(FcParser* p);
static FcStmt* ParseFireStmt(FcParser* p);
static FcStmt* ParseSyncGlobalBlock(FcParser* p);
static FcStmt* ParseTerminateStmt(FcParser* p);
static FcStmt* ParseProcessStmt(FcParser* p);
static int ParseLoopLevels(FcParser* p, const char* keyword);
static FcStmt* ParseImplicitCatchTail(FcParser* p);

static FcStmt* ParseStatement(FcParser* p) {
    FcExpr* expr;
    if (TryParseWarn(p)) return (FcStmt*)mkBlock(p, Previous(p)->line, L(p));
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "silence") && IsProbeOperandNext(p)) return ParseSilence(p);
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "probe") && IsProbeOperandNext(p)) {
        int probeLine = Peek(p)->line;
        FcExpr* probe = ParseProbe(p);
        ExpectStatementTerminator(p);
        return mkExprStmt(p, probeLine, probe);
    }
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "delete") && !PeekAt(p, 1)->newlineBefore
        && (PeekAt(p, 1)->type == TT_Identifier || PeekAt(p, 1)->type == TT_This || PeekAt(p, 1)->type == TT_Star))
        return ParseDelete(p);
    if (IsUnitDeclStart(p)) Error(p, "A unit is declared at the top level of a file, not inside a block, a class or a namespace", Peek(p));
    if (Check(p, TT_Replace)) return ParseReplace(p);
    if (Check(p, TT_Var)) return ParseVarDecl(p);
    if (Check(p, TT_Readonly)) return ParseReadonlyDecl(p);
    if (Check(p, TT_Enum)) return ParseEnumDecl(p, false);
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "flag") && PeekAt(p, 1)->type == TT_Enum) { Advance(p); return ParseEnumDecl(p, true); }
    if (Check(p, TT_Class)) return ParseClassDecl(p);
    if (Check(p, TT_LBracket) && PeekAt(p, 1)->type == TT_Identifier) {
        FcStmt* classAttributes = AttributesBeforeClass(p);
        if (classAttributes) return classAttributes;
    }
    if (Check(p, TT_Namespace)) return ParseNamespaceDecl(p);
    if (Check(p, TT_Actor)) return ParseActorDecl(p);
    if (IsRecordStart(p)) return ParseRecordDecl(p);
    if (Check(p, TT_Interface)) return ParseInterfaceDecl(p);
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "inline") && PeekAt(p, 1)->type == TT_Identifier && streq(PeekAt(p, 1)->lexeme, "c")
        && !PeekAt(p, 1)->newlineBefore && PeekAt(p, 2)->type != TT_Assign)
        return ParseInlineCDecl(p);
    if (IsAwaitStart(p)) {
        FcExpr* awaitExpr = ParseExpression(p);
        ExpectStatementTerminator(p);
        return mkExprStmt(p, awaitExpr->line, awaitExpr);
    }
    if (NextLooksLikeTypeThenName(p)) return ParseBareTypedDecl(p, false, true);
    if (Check(p, TT_If)) return ParseIf(p);
    if (Check(p, TT_While)) return ParseWhile(p);
    if (Check(p, TT_Do)) return ParseDoWhile(p);
    if (Check(p, TT_For)) return ParseFor(p);
    if (Check(p, TT_Foreach)) return ParseForeach(p);
    if (Check(p, TT_Return)) return ParseReturn(p);
    if (Check(p, TT_Throw)) return ParseThrowStmt(p);
    if (Check(p, TT_Try) && PeekAt(p, 1)->type == TT_LBrace) return ParseTry(p);
    if (Check(p, TT_Hash)) return ParseDirective(p);
    if (Check(p, TT_Extern)) return ParseExternDecl(p);
    if (Check(p, TT_Unsafe)) return ParseUnsafeStmt(p);
    if (Check(p, TT_With)) return ParseWithStmt(p);
    if (Check(p, TT_Switch)) return ParseSwitchStmt(p);
    if (Check(p, TT_Fire)) return ParseFireStmt(p);
    if (Check(p, TT_Sync) && PeekAt(p, 1)->type == TT_Identifier && streq(PeekAt(p, 1)->lexeme, "global") && PeekAt(p, 2)->type == TT_LBrace)
        return ParseSyncGlobalBlock(p);
    if (Check(p, TT_Break)) {
        int breakLine = Advance(p)->line;
        int levels = ParseLoopLevels(p, "break");
        ExpectStatementTerminator(p);
        return (FcStmt*)FC_NEW(C_, FcBreakStmt, .h = {SK_BreakStmt, p->sourceIndex, breakLine}, .levels = levels);
    }
    if (Check(p, TT_Continue)) {
        int continueLine = Advance(p)->line;
        int levels = ParseLoopLevels(p, "continue");
        ExpectStatementTerminator(p);
        return (FcStmt*)FC_NEW(C_, FcContinueStmt, .h = {SK_ContinueStmt, p->sourceIndex, continueLine}, .levels = levels);
    }
    if (Check(p, TT_Leave)) {
        int leaveLine = Advance(p)->line;
        ExpectStatementTerminator(p);
        return (FcStmt*)FC_NEW(C_, FcLeaveStmt, .h = {SK_LeaveStmt, p->sourceIndex, leaveLine});
    }
    if (Check(p, TT_Terminate)) return ParseTerminateStmt(p);
    if (Check(p, TT_Process)) return ParseProcessStmt(p);
    if (Check(p, TT_LBrace)) return (FcStmt*)ParseBlock(p);
    expr = ParseExpression(p);
    ExpectStatementTerminator(p);
    return mkExprStmt(p, expr->line, expr);
}

static FcBlockStmt* ParseBlock(FcParser* p) {
    int line = Peek(p)->line;
    FcList* statements = L(p);
    Expect(p, TT_LBrace, "Expected '{'");
    while (!Check(p, TT_RBrace) && !Check(p, TT_Eof)) {
        if (Match(p, TT_Semicolon)) continue;
        if (Check(p, TT_Catch)) { LA(p, statements, ParseImplicitCatchTail(p)); break; }
        if (TryParseWarn(p)) continue;
        LA(p, statements, ParseStatement(p));
    }
    Expect(p, TT_RBrace, "Expected '}' at the end of the block");
    return mkBlock(p, line, statements);
}

static FcCatchClause* ParseCatchClause(FcParser* p);

static FcStmt* ParseImplicitCatchTail(FcParser* p) {
    int line = Peek(p)->line;
    FcList* catches = L(p);
    FcBlockStmt* finallyBlock = NULL;
    FcList* restStatements = L(p);
    while (Check(p, TT_Catch)) LA(p, catches, ParseCatchClause(p));
    if (Match(p, TT_Finally)) finallyBlock = ParseBlock(p);
    while (!Check(p, TT_RBrace) && !Check(p, TT_Eof)) {
        if (Match(p, TT_Semicolon)) continue;
        if (Check(p, TT_Catch)) { LA(p, restStatements, ParseImplicitCatchTail(p)); break; }
        if (TryParseWarn(p)) continue;
        LA(p, restStatements, ParseStatement(p));
    }
    return (FcStmt*)FC_NEW(C_, FcTryStmt, .h = {SK_TryStmt, p->sourceIndex, line}, .tryBlock = mkBlock(p, line, restStatements), .catches = catches, .finallyBlock = finallyBlock);
}

static FcCatchClause* ParseCatchClause(FcParser* p) {
    int line = Peek(p)->line;
    FcTypeRef* typeRef = NULL;
    const char* varName;
    FcCatchClause* cc;
    Expect(p, TT_Catch, "Expected 'catch'");
    Expect(p, TT_LParen, "Expected '(' after 'catch'");
    if (NextLooksLikeTypeThenName(p)) {
        typeRef = newTypeRef(p, ParseDottedName(p, "type name in catch(...)"), FC_NULL_INT, 0);
        typeRef->namespaces = CurrentNamespaces(p);
    }
    varName = Expect(p, TT_Identifier, "Expected an identifier in catch(...)")->lexeme;
    Expect(p, TT_RParen, "Expected ')' after the catch parameters");
    cc = (FcCatchClause*)fcAlloc(p->ctx, sizeof *cc);
    cc->source = p->sourceIndex; cc->line = line; cc->typeRef = typeRef; cc->varName = varName;
    cc->body = ParseBlock(p);
    return cc;
}

static FcStmt* ParseTry(FcParser* p) {
    int line = Peek(p)->line;
    FcBlockStmt* tryBlock;
    FcList* catches = L(p);
    FcBlockStmt* finallyBlock = NULL;
    Expect(p, TT_Try, "Expected 'try'");
    tryBlock = ParseBlock(p);
    while (Check(p, TT_Catch)) LA(p, catches, ParseCatchClause(p));
    if (Match(p, TT_Finally)) finallyBlock = ParseBlock(p);
    if (catches->count == 0 && !finallyBlock) Error(p, "'try' needs at least one 'catch' block or 'finally'", Peek(p));
    return (FcStmt*)FC_NEW(C_, FcTryStmt, .h = {SK_TryStmt, p->sourceIndex, line}, .tryBlock = tryBlock, .catches = catches, .finallyBlock = finallyBlock);
}

static FcVarDeclStmt* ParseVarDeclCore(FcParser* p, bool isReadonly);

static FcStmt* ParseVarDecl(FcParser* p) {
    FcStmt* decl = (FcStmt*)ParseVarDeclCore(p, false);
    ExpectStatementTerminator(p);
    return decl;
}

static FcStmt* ParseReadonlyDecl(FcParser* p) {
    Expect(p, TT_Readonly, "Expected 'readonly'");
    if (Check(p, TT_Var)) {
        FcStmt* decl = (FcStmt*)ParseVarDeclCore(p, true);
        ExpectStatementTerminator(p);
        return decl;
    }
    if (NextLooksLikeTypeThenName(p)) return ParseBareTypedDecl(p, true, true);
    Error(p, "Expected 'var' or a type declaration after 'readonly'", Peek(p));
}

static const char* ParseUnitName(FcParser* p);

static FcVarDeclStmt* ParseVarDeclCore(FcParser* p, bool isReadonly) {
    int line = Peek(p)->line;
    const char* name;
    FcList* arrayRanks;
    FcTypeRef* type = NULL;
    FcExpr* initializer = NULL;
    Expect(p, TT_Var, "Expected 'var'");
    name = Expect(p, TT_Identifier, "Expected a variable name")->lexeme;
    arrayRanks = ParseArrayRanks(p);
    if (Match(p, TT_Colon)) {
        type = newTypeRef(p, "var", FC_NULL_INT, 0);
        type->unit = ParseUnitName(p);
    }
    if (Match(p, TT_Assign)) initializer = ParseExpression(p);
    return (FcVarDeclStmt*)mkVarDecl(p, line, name, type, arrayRanks, initializer, isReadonly);
}

static const char* ParseUnitName(FcParser* p) {
    const char* name = IsCustomUnitWordAt(p, 0) ? Advance(p)->lexeme : Expect(p, TT_Identifier, "Expected a unit name after ':'")->lexeme;
    if (streq(name, "any") && (Check(p, TT_Identifier) || IsCustomUnitWordAt(p, 0)) && Peek(p)->line == Previous(p)->line) {
        const char* next = Advance(p)->lexeme;
        const char* tail = ParseCombinedUnitTail(p);
        name = S(p, "%s %s%s", name, next, tail);
    } else name = S(p, "%s%s", name, ParseCombinedUnitTail(p));
    return UnitPowers(p, name, Previous(p));
}

static const char* UnitPowers(FcParser* p, const char* unit, const FcToken* at) {
    const unsigned char* q = (const unsigned char*)unit;
    uint16_t prev;
    if (!*q) return fcCaretExponents(p->ctx, unit);
    prev = fc8Next(&q);
    while (*q) {
        uint16_t u = fc8Next(&q);
        if (u == '0' && fcIsUnitLetter(prev)) Error(p, S(p, "The power of a unit written as digits must be 1 or more ('%s')", unit), at);
        prev = u;
    }
    return fcCaretExponents(p->ctx, unit);
}

static const char* ParseCombinedUnitTail(FcParser* p) {
    FcBuf sb = {0};
    fcBufStr(&sb, "");
    for (;;) {
        if (LineContinues(p) && Check(p, TT_Caret) && PeekAt(p, 1)->type == TT_IntLiteral) {
            Advance(p);
            fcBufFmt(&sb, "^%" PRId64, Advance(p)->intValue);
            continue;
        }
        if (LineContinues(p) && (Check(p, TT_Slash) || Check(p, TT_Star)) && (PeekAt(p, 1)->type == TT_Identifier || IsCustomUnitWordAt(p, 1)) && !PeekAt(p, 1)->newlineBefore) {
            fcBufStr(&sb, Advance(p)->lexeme);
            fcBufStr(&sb, Advance(p)->lexeme);
            continue;
        }
        return fcBufDone(p->ctx, &sb);
    }
}

static int CombinedUnitLengthAt(FcParser* p, int offset);

static bool UnitNameAt(FcParser* p, int offset) {
    return PeekAt(p, offset)->type == TT_Identifier || (PeekAt(p, offset)->type == TT_LBracket && PeekAt(p, offset + 1)->type == TT_RBracket);
}

static const char* ParseUnitAtType(FcParser* p) {
    if (!Check(p, TT_Colon) || (PeekAt(p, 1)->type != TT_Identifier && !IsCustomUnitWordAt(p, 1))) return NULL;
    if (streq(PeekAt(p, 1)->lexeme, "any") && (PeekAt(p, 2)->type == TT_Identifier || IsCustomUnitWordAt(p, 2))) {
        int anyLength = CombinedUnitLengthAt(p, 2);
        if (UnitNameAt(p, 2 + anyLength)) {
            const char* first;
            Advance(p); Advance(p);
            first = Advance(p)->lexeme;
            {
                const char* tail = ParseCombinedUnitTail(p);
                return UnitPowers(p, S(p, "any %s%s", first, tail), Previous(p));
            }
        }
    }
    if (!UnitNameAt(p, 1 + CombinedUnitLengthAt(p, 1))) return NULL;
    Advance(p);
    {
        const char* first = Advance(p)->lexeme;
        const char* tail = ParseCombinedUnitTail(p);
        return UnitPowers(p, S(p, "%s%s", first, tail), Previous(p));
    }
}

static int CombinedUnitLengthAt(FcParser* p, int offset) {
    int i = offset + 1;
    for (;;) {
        FcToken* token = PeekAt(p, i);
        if (token->newlineBefore && p->bracketDepth == 0) return i - offset;
        if (token->type == TT_Caret && PeekAt(p, i + 1)->type == TT_IntLiteral && !PeekAt(p, i + 1)->newlineBefore) { i += 2; continue; }
        if ((token->type == TT_Slash || token->type == TT_Star) && (PeekAt(p, i + 1)->type == TT_Identifier || IsCustomUnitWordAt(p, i + 1)) && !PeekAt(p, i + 1)->newlineBefore) {
            i += 2;
            continue;
        }
        return i - offset;
    }
}

static FcStmt* ParseBareTypedDecl(FcParser* p, bool isReadonly, bool consumeTerminator) {
    int line = Peek(p)->line;
    FcTypeRef* type = ParseTypeRef(p, false);
    const char* name = Expect(p, TT_Identifier, "Expected an identifier")->lexeme;
    FcList* arrayRanks = ParseArrayRanks(p);
    FcExpr* initializer = NULL;
    if (Match(p, TT_Colon)) {
        type = copyTypeRef(p, type);
        type->unit = ParseUnitName(p);
    }
    if (Match(p, TT_Assign)) initializer = ParseExpression(p);
    if (consumeTerminator) ExpectStatementTerminator(p);
    return mkVarDecl(p, line, name, type, arrayRanks, initializer, isReadonly);
}

static const char* ParseTypeAnnotationName(FcParser* p) {
    const char* name;
    if (IsTypeKeyword(Peek(p)->type)) return Advance(p)->lexeme;
    name = Expect(p, TT_Identifier, "Expected a type name")->lexeme;
    while (Check(p, TT_Dot) && PeekAt(p, 1)->type == TT_Identifier) {
        Advance(p);
        name = S(p, "%s.%s", name, Advance(p)->lexeme);
    }
    return name;
}

static bool DefinedAhead(FcParser* p, int offset);
static FcTypeRef* ParseTypeRefCore(FcParser* p, bool allowArray);

static FcTypeRef* ParseTypeRef(FcParser* p, bool allowArray) {
    FcToken* definedTok;
    FcTypeRef* type;
    if (!DefinedAhead(p, 0)) return ParseTypeRefCore(p, allowArray);
    definedTok = Advance(p);
    type = ParseTypeRefCore(p, allowArray);
    if (type->pointerDepth > 0) Error(p, S(p, "'defined' marks a value that never holds undefined - a pointer is written '%s*' without it", type->baseName), definedTok);
    type = copyTypeRef(p, type);
    type->nonNull = true;
    return type;
}

static bool DefinedAhead(FcParser* p, int offset) {
    FcTokenType t;
    int at;
    if (PeekAt(p, offset)->type != TT_Identifier || !streq(PeekAt(p, offset)->lexeme, "defined")) return false;
    t = PeekAt(p, offset + 1)->type;
    if (t == TT_KwInt || t == TT_KwFloat || t == TT_KwBool || t == TT_KwWord || t == TT_KwChar || t == TT_KwByte) return true;
    at = offset + 1;
    if (PeekAt(p, at)->type == TT_KwString) at++;
    else if (PeekAt(p, at)->type == TT_Identifier) {
        at++;
        while (PeekAt(p, at)->type == TT_Dot && PeekAt(p, at + 1)->type == TT_Identifier) at += 2;
    } else return false;
    return PeekAt(p, at)->type == TT_Identifier || PeekAt(p, at)->type == TT_Lt;
}

static FcLambdaSignature* ParseLambdaSignature(FcParser* p, const char* returnTypeName);
static int ParseArrayTypeSuffix(FcParser* p, bool allowArray);
static bool NextIsEmptyBrackets(FcParser* p) { return Check(p, TT_LBracket) && PeekAt(p, 1)->type == TT_RBracket; }
static bool CanBeNonNull(const char* b) { return streq(b, "int") || streq(b, "float") || streq(b, "bool") || streq(b, "word") || streq(b, "char"); }

static FcTypeRef* ParseTypeRefCore(FcParser* p, bool allowArray) {
    const char* baseName = ParseTypeAnnotationName(p);
    FcList* namespaces = CurrentNamespaces(p);
    FcTypeRef* t;
    int width = FC_NULL_INT, pointerDepth = 0;
    FcList* typeArgs = NULL;
    const char* unit;
    if (!streq(baseName, "lambda") && Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "lambda")) {
        Advance(p);
        t = newTypeRef(p, "lambda", FC_NULL_INT, 0);
        t->lambdaSignature = ParseLambdaSignature(p, baseName);
        t->namespaces = namespaces;
        return t;
    }
    if (streq(baseName, "lambda")) {
        if (Check(p, TT_Identifier) && (streq(Peek(p)->lexeme, "field") || streq(Peek(p)->lexeme, "property") || streq(Peek(p)->lexeme, "member")
                || streq(Peek(p)->lexeme, "method") || streq(Peek(p)->lexeme, "selector")) && PeekAt(p, 1)->type == TT_Lt) {
            const char* selectorKind = Advance(p)->lexeme;
            FcList* targetTypes = L(p);
            FcLambdaSignature* sig;
            Advance(p);
            if (!Check(p, TT_Gt)) LA(p, targetTypes, ParseTypeAnnotationName(p));
            Expect(p, TT_Gt, S(p, "Expected '>' after the type of 'lambda %s<...>'", selectorKind));
            sig = (FcLambdaSignature*)fcAlloc(p->ctx, sizeof *sig);
            sig->paramTypeNames = targetTypes; sig->isSelector = true; sig->selectorKind = selectorKind;
            t = newTypeRef(p, "lambda", FC_NULL_INT, 0);
            t->lambdaSignature = sig;
            t->namespaces = namespaces;
            return t;
        }
        t = newTypeRef(p, "lambda", FC_NULL_INT, 0);
        t->lambdaSignature = ParseLambdaSignature(p, NULL);
        t->namespaces = namespaces;
        return t;
    }
    if (streq(baseName, "word")) {
        int wordPointerDepth = 0;
        if (Check(p, TT_LBracket) && !NextIsEmptyBrackets(p))
            Error(p, "'word' has the width of the target's machine word - no '[...]' may follow (use int[16], int[32] or int for a fixed width)", Peek(p));
        while (Match(p, TT_Star)) wordPointerDepth++;
        t = newTypeRef(p, "word", FC_NULL_INT, wordPointerDepth);
        t->namespaces = namespaces;
        t->arrayRank = ParseArrayTypeSuffix(p, allowArray);
        return t;
    }
    if (streq(baseName, "byte")) {
        int bytePointerDepth = 0;
        if (Check(p, TT_LBracket) && !NextIsEmptyBrackets(p)) Error(p, "'byte' already has a fixed width of 8 bits - no additional '[...]' may follow", Peek(p));
        while (Match(p, TT_Star)) bytePointerDepth++;
        t = newTypeRef(p, "int", 8, bytePointerDepth);
        t->namespaces = namespaces;
        t->arrayRank = ParseArrayTypeSuffix(p, allowArray);
        t->isUnsigned = true;
        return t;
    }
    if (Check(p, TT_LBracket) && !NextIsEmptyBrackets(p)) {
        FcToken* widthTok;
        Advance(p);
        widthTok = Expect(p, TT_IntLiteral, "Expected a bit width (8/16/32/64)");
        width = (int)widthTok->intValue;
        Expect(p, TT_RBracket, "Expected ']' after the bit width");
    }
    if (width == FC_NULL_INT && !IsTypeKeyword(Previous(p)->type) && Check(p, TT_Lt) && TypeArgListEnd(p, 0) > 0) typeArgs = ParseTypeArgRefs(p);
    if (Check(p, TT_Bang) && PeekAt(p, 1)->type == TT_Identifier && CanBeNonNull(baseName))
        Error(p, S(p, "A value that is never undefined is declared 'defined %s', not '%s!'", baseName, baseName), Peek(p));
    unit = streq(baseName, "int") || streq(baseName, "float") ? ParseUnitAtType(p) : NULL;
    while (Match(p, TT_Star)) pointerDepth++;
    t = newTypeRef(p, baseName, width, pointerDepth);
    t->unit = unit;
    t->namespaces = namespaces;
    t->arrayRank = ParseArrayTypeSuffix(p, allowArray);
    t->typeArgCount = typeArgs ? typeArgs->count : 0;
    t->typeArgs = typeArgs;
    return t;
}

static FcList* ParseTypeArgRefs(FcParser* p) {
    FcList* args = L(p);
    Expect(p, TT_Lt, "Expected '<'");
    do {
        FcTypeRef* arg;
        if (DefinedAhead(p, 0))
            Error(p, S(p, "'defined %s' cannot be a type argument - 'defined' belongs to variables, parameters, fields, properties and return types", PeekAt(p, 1)->lexeme), Peek(p));
        arg = ParseTypeRef(p, true);
        if ((streq(arg->baseName, "int") || streq(arg->baseName, "float")) && !arg->unit && arg->arrayRank == 0 && Check(p, TT_Colon)
            && (PeekAt(p, 1)->type == TT_Identifier || IsCustomUnitWordAt(p, 1))) {
            const char* unit;
            Advance(p);
            unit = Advance(p)->lexeme;
            if (streq(unit, "any") && (Check(p, TT_Identifier) || IsCustomUnitWordAt(p, 0))) unit = S(p, "any %s", Advance(p)->lexeme);
            arg = copyTypeRef(p, arg);
            arg->unit = unit;
        } else if ((streq(arg->baseName, "int") || streq(arg->baseName, "float")) && streq(arg->unit, "any") && (Check(p, TT_Identifier) || IsCustomUnitWordAt(p, 0))) {
            const char* dim = Advance(p)->lexeme;
            arg = copyTypeRef(p, arg);
            arg->unit = S(p, "any %s", dim);
        }
        LA(p, args, arg);
    } while (Match(p, TT_Comma));
    Expect(p, TT_Gt, "Expected '>' after the type arguments");
    return args;
}

static const char* TypeArgName(const FcTypeRef* arg) { return streq(arg->baseName, "int") && arg->bitWidth == 8 ? "byte" : arg->baseName; }

static int TypeArgListEnd(FcParser* p, int offset) {
    int depth = 0, i;
    bool expectName = true;
    if (PeekAt(p, offset)->type != TT_Lt) return -1;
    for (i = offset; i < offset + 256; i++) {
        FcToken* token = PeekAt(p, i);
        switch (token->type) {
            case TT_Lt:
                if (i > offset && expectName) return -1;
                depth++;
                expectName = true;
                break;
            case TT_Gt:
                if (expectName) return -1;
                if (--depth == 0) return i + 1;
                break;
            case TT_Comma:
                if (expectName) return -1;
                expectName = true;
                break;
            case TT_Dot:
                if (expectName || PeekAt(p, i + 1)->type != TT_Identifier) return -1;
                expectName = true;
                break;
            case TT_LBracket:
                if (expectName || PeekAt(p, i + 1)->type != TT_RBracket) return -1;
                i++;
                break;
            case TT_Colon:
                if (expectName || !(streq(PeekAt(p, i - 1)->lexeme, "int") || streq(PeekAt(p, i - 1)->lexeme, "float"))
                    || (PeekAt(p, i + 1)->type != TT_Identifier && !IsCustomUnitWordAt(p, i + 1))) return -1;
                i++;
                if (streq(PeekAt(p, i)->lexeme, "any") && (PeekAt(p, i + 1)->type == TT_Identifier || IsCustomUnitWordAt(p, i + 1))) i++;
                break;
            default:
                if (token->type == TT_Identifier && !expectName && streq(token->lexeme, "lambda")) break;
                if (!(token->type == TT_Identifier || IsTypeKeyword(token->type)) || !expectName) return -1;
                expectName = false;
                break;
        }
    }
    return -1;
}

static int ParseArrayTypeSuffix(FcParser* p, bool allowArray) {
    int rank = 0;
    while (NextIsEmptyBrackets(p)) {
        if (!allowArray)
            Error(p, "For variables, fields and parameters an array is written with the brackets after the name ('int values[]'); 'int[]' as a type only exists as the return type of a method or property", Peek(p));
        Advance(p);
        Advance(p);
        rank++;
    }
    return rank;
}

static void SkipOptionalNestedTypeArgs(FcParser* p);

static FcLambdaSignature* ParseLambdaSignature(FcParser* p, const char* returnTypeName) {
    FcList* paramTypes = L(p);
    bool expression = Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "expression") && PeekAt(p, 1)->type == TT_Lt;
    FcLambdaSignature* sig;
    if (expression) Advance(p);
    if (Match(p, TT_Lt)) {
        if (!Check(p, TT_Gt)) {
            do {
                LA(p, paramTypes, ParseTypeAnnotationName(p));
                SkipOptionalNestedTypeArgs(p);
            } while (Match(p, TT_Comma));
        }
        Expect(p, TT_Gt, "Expected '>' after the lambda parameter types");
    }
    sig = (FcLambdaSignature*)fcAlloc(p->ctx, sizeof *sig);
    sig->returnTypeName = returnTypeName; sig->paramTypeNames = paramTypes; sig->selectorKind = "member"; sig->isExpression = expression;
    return sig;
}

static FcList* ParseArrayRanks(FcParser* p) {
    FcList* ranks = L(p);
    while (Check(p, TT_LBracket) && LineContinues(p)) {
        FcExpr* size;
        Advance(p);
        size = Check(p, TT_RBracket) ? NULL : ParseExpression(p);
        Expect(p, TT_RBracket, "Expected ']'");
        LA(p, ranks, size);
    }
    return ranks;
}

static bool NextLooksLikeTypeThenName(FcParser* p) {
    int offset = 1;
    if (IsTypeKeyword(Peek(p)->type) || DefinedAhead(p, 0)) return true;
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "lambda")) return true;
    if (!Check(p, TT_Identifier)) return false;
    while (PeekAt(p, offset)->type == TT_Dot && PeekAt(p, offset + 1)->type == TT_Identifier) offset += 2;
    if (PeekAt(p, offset)->type == TT_Lt) {
        int end = TypeArgListEnd(p, offset);
        if (end < 0) return false;
        offset = end;
    }
    while (PeekAt(p, offset)->type == TT_LBracket && PeekAt(p, offset + 1)->type == TT_RBracket) offset += 2;
    return PeekAt(p, offset)->type == TT_Identifier;
}

static bool TypeThenOperatorAhead(FcParser* p) {
    int offset;
    if (DefinedAhead(p, 0) && PeekAt(p, 2)->type == TT_Operator) return true;
    if (IsTypeKeyword(Peek(p)->type)) offset = 1;
    else if (Check(p, TT_Identifier)) {
        offset = 1;
        while (PeekAt(p, offset)->type == TT_Dot && PeekAt(p, offset + 1)->type == TT_Identifier) offset += 2;
        if (PeekAt(p, offset)->type == TT_Lt) {
            offset = TypeArgListEnd(p, offset);
            if (offset < 0) return false;
        }
    } else return false;
    while (PeekAt(p, offset)->type == TT_LBracket && PeekAt(p, offset + 1)->type == TT_RBracket) offset += 2;
    return PeekAt(p, offset)->type == TT_Operator;
}

static FcStmt* ParseIf(FcParser* p) {
    int line = Peek(p)->line;
    FcExpr* cond;
    FcStmt* thenBranch;
    FcStmt* elseBranch = NULL;
    Expect(p, TT_If, "Expected 'if'");
    Expect(p, TT_LParen, "Expected '(' after 'if'");
    cond = ParseExpression(p);
    Expect(p, TT_RParen, "Expected ')' after the if condition");
    thenBranch = ParseStatement(p);
    if (Match(p, TT_Else)) elseBranch = ParseStatement(p);
    return (FcStmt*)FC_NEW(C_, FcIfStmt, .h = {SK_IfStmt, p->sourceIndex, line}, .condition = cond, .then = thenBranch, .otherwise = elseBranch);
}

static int ParseLoopLevels(FcParser* p, const char* keyword) {
    FcToken* tok;
    int64_t n;
    if (!(LineContinues(p) && Check(p, TT_IntLiteral))) return 1;
    tok = Advance(p);
    n = tok->intValue;
    if (n < 1 || n > 64 || (tok->unitSuffix && tok->unitSuffix[0])) Error(p, S(p, "The number after '%s' counts the loops to leave: 1, 2, 3, ...", keyword), tok);
    return (int)n;
}

static FcStmt* ParseDoWhile(FcParser* p) {
    int line = Peek(p)->line;
    bool savedDoBody;
    FcStmt* body;
    FcExpr* cond;
    Expect(p, TT_Do, "Expected 'do'");
    savedDoBody = p->inDoBody;
    p->inDoBody = true;
    pPushCleanup(p, CL_IN_DO_BODY, 0, savedDoBody, NULL);
    body = ParseStatement(p);
    pPopCleanup(p);
    p->inDoBody = savedDoBody;
    Expect(p, TT_While, "Expected 'while' after the body of 'do'");
    Expect(p, TT_LParen, "Expected '(' after 'while'");
    cond = ParseExpression(p);
    Expect(p, TT_RParen, "Expected ')' after the while condition");
    ExpectStatementTerminator(p);
    return (FcStmt*)FC_NEW(C_, FcDoWhileStmt, .h = {SK_DoWhileStmt, p->sourceIndex, line}, .body = body, .condition = cond);
}

static FcStmt* ParseWhile(FcParser* p) {
    int line = Peek(p)->line;
    FcExpr* cond;
    Expect(p, TT_While, "Expected 'while'");
    Expect(p, TT_LParen, "Expected '(' after 'while'");
    cond = ParseExpression(p);
    Expect(p, TT_RParen, "Expected ')' after the while condition");
    return (FcStmt*)FC_NEW(C_, FcWhileStmt, .h = {SK_WhileStmt, p->sourceIndex, line}, .condition = cond, .body = ParseStatement(p));
}

static FcStmt* ParseFor(FcParser* p) {
    int line = Peek(p)->line;
    FcStmt* init = NULL;
    FcExpr* cond = NULL;
    FcExpr* incr = NULL;
    Expect(p, TT_For, "Expected 'for'");
    Expect(p, TT_LParen, "Expected '(' after 'for'");
    if (!Check(p, TT_Semicolon)) {
        if (Check(p, TT_Var)) init = (FcStmt*)ParseVarDeclCore(p, false);
        else if (NextLooksLikeTypeThenName(p)) init = ParseBareTypedDecl(p, false, false);
        else { int l = Peek(p)->line; init = mkExprStmt(p, l, ParseExpression(p)); }
    }
    Expect(p, TT_Semicolon, "Expected ';' after the for initializer");
    if (!Check(p, TT_Semicolon)) cond = ParseExpression(p);
    Expect(p, TT_Semicolon, "Expected ';' after the for condition");
    if (!Check(p, TT_RParen)) incr = ParseExpression(p);
    Expect(p, TT_RParen, "Expected ')' after the for clauses");
    return (FcStmt*)FC_NEW(C_, FcForStmt, .h = {SK_ForStmt, p->sourceIndex, line}, .init = init, .condition = cond, .increment = incr, .body = ParseStatement(p));
}

static FcStmt* ParseForeach(FcParser* p) {
    int line = Peek(p)->line;
    const char* varName;
    FcExpr* iterable;
    Expect(p, TT_Foreach, "Expected 'foreach'");
    Expect(p, TT_LParen, "Expected '(' after 'foreach'");
    Match(p, TT_Var);
    varName = Expect(p, TT_Identifier, "Expected a variable name")->lexeme;
    Expect(p, TT_In, "Expected 'in' in foreach");
    iterable = ParseExpression(p);
    Expect(p, TT_RParen, "Expected ')' after the foreach clauses");
    return (FcStmt*)FC_NEW(C_, FcForeachStmt, .h = {SK_ForeachStmt, p->sourceIndex, line}, .varName = varName, .iterable = iterable, .body = ParseStatement(p));
}

static FcStmt* ParseReturn(FcParser* p) {
    int line = Peek(p)->line;
    FcExpr* value = NULL;
    Expect(p, TT_Return, "Expected 'return'");
    if (!Check(p, TT_Semicolon) && !Check(p, TT_RBrace) && !Check(p, TT_Eof)) value = ParseExpression(p);
    ExpectStatementTerminator(p);
    return mkReturn(p, line, value);
}

static FcStmt* ParseReplace(FcParser* p) {
    FcToken* keyword = Advance(p);
    FcExpr* expr = ParseExpression(p);
    FcAssignExpr* assign;
    if (expr->kind != EK_AssignExpr
        || (((FcAssignExpr*)expr)->value->kind == EK_BinaryExpr && ((FcBinaryExpr*)((FcAssignExpr*)expr)->value)->left == ((FcAssignExpr*)expr)->target))
        Error(p, "'replace' needs an assignment with '=': replace this.field = value", keyword);
    assign = (FcAssignExpr*)expr;
    if (assign->target->kind != EK_MemberExpr && assign->target->kind != EK_IdentifierExpr)
        Error(p, "'replace' assigns a field (replace this.field = value, replace Class.field = value) - not an element or a pointer", keyword);
    ExpectStatementTerminator(p);
    assign = (FcAssignExpr*)fcNodeDup(p->ctx, assign, sizeof *assign);
    assign->replace = true;
    assign->parenthesized = false;   /* (a copy: not the object of Parser.ParenthesizedAssignments) */
    return mkExprStmt(p, keyword->line, (FcExpr*)assign);
}

static FcStmt* ParseDelete(FcParser* p) {
    int line = Peek(p)->line;
    FcExpr* target;
    Advance(p);
    target = ParseExpression(p);
    ExpectStatementTerminator(p);
    return (FcStmt*)FC_NEW(C_, FcDeleteStmt, .h = {SK_DeleteStmt, p->sourceIndex, line}, .target = target);
}

static FcStmt* ParseThrowStmt(FcParser* p) {
    int line = Peek(p)->line;
    FcExpr* value;
    Expect(p, TT_Throw, "Expected 'throw'");
    value = ParseExpression(p);
    ExpectStatementTerminator(p);
    return (FcStmt*)FC_NEW(C_, FcThrowStmt, .h = {SK_ThrowStmt, p->sourceIndex, line}, .value = value);
}

static FcStmt* ParseExternDecl(FcParser* p) {
    int line = Peek(p)->line;
    FcTypeRef* returnType = NULL;
    const char* name;
    FcList* parms;
    Expect(p, TT_Extern, "Expected 'extern'");
    if (NextLooksLikeTypeThenName(p)) returnType = ParseTypeRef(p, false);
    name = Expect(p, TT_Identifier, "Expected a function name after 'extern'")->lexeme;
    parms = ParseParamList(p, false);
    ExpectStatementTerminator(p);
    return (FcStmt*)FC_NEW(C_, FcExternDecl, .h = {SK_ExternDecl, p->sourceIndex, line}, .returnType = returnType, .name = name, .params = parms, .libName = p->currentExternLib);
}

static FcStmt* ParseInlineCDecl(FcParser* p) {
    int line = Peek(p)->line;
    FcTypeRef* returnType = NULL;
    const char* name;
    FcList* parms;
    FcToken* body;
    Advance(p);
    Advance(p);
    if (NextLooksLikeTypeThenName(p)) returnType = ParseTypeRef(p, false);
    name = Expect(p, TT_Identifier, "Expected a function name after 'inline c'")->lexeme;
    parms = ParseParamList(p, false);
    body = Expect(p, TT_InlineCode, "Expected the C body '{ ... }' of the 'inline c' function");
    return (FcStmt*)FC_NEW(C_, FcExternDecl, .h = {SK_ExternDecl, p->sourceIndex, line}, .returnType = returnType, .name = name, .params = parms,
        .inlineC = body->stringValue, .inlineLine = body->line);
}

static FcStmt* ParseInlineCMethod(FcParser* p, FcAccess access) {
    int line = Peek(p)->line;
    FcExternDecl* decl;
    FcExternDecl* hoisted;
    const char* cName;
    FcList* args = L(p);
    FcExpr* call;
    bool none;
    FcStmt* bodyStmt;
    int i;
    char* q;
    if (!p->currentClassName) Error(p, "An 'inline c' method belongs to a class", Peek(p));
    decl = (FcExternDecl*)ParseInlineCDecl(p);
    cName = S(p, "%s_%s", p->currentClassName, decl->name);   /* (the dots of the class name become '_') */
    for (q = (char*)cName; *q; q++) if (*q == '.') *q = '_';
    hoisted = (FcExternDecl*)fcNodeDup(p->ctx, decl, sizeof *decl);
    hoisted->name = cName;
    hoisted->displayName = S(p, "%s.%s", p->currentClassName, decl->name);
    LA(p, p->hoistedInlineC, hoisted);
    for (i = 0; i < decl->params->count; i++) LA(p, args, mkIdent(p, line, ((FcLambdaParam*)decl->params->items[i])->name));
    call = mkCall(p, line, mkIdent(p, line, cName), args, NULL, NULL);
    none = !decl->returnType || (streq(decl->returnType->baseName, "undefined") && decl->returnType->pointerDepth == 0);
    bodyStmt = none ? mkExprStmt(p, line, call) : mkReturn(p, line, call);
    return (FcStmt*)FC_NEW(C_, FcMethodDecl, .h = {SK_MethodDecl, p->sourceIndex, line}, .returnType = decl->returnType, .name = decl->name, .params = decl->params,
        .body = mkBlock(p, line, L1(p, bodyStmt)), .access = access, .isStatic = true);
}

static FcStmt* ParseDirective(FcParser* p) {
    int line = Peek(p)->line;
    Expect(p, TT_Hash, "Expected '#'");
    if (Match(p, TT_Extern)) {
        FcToken* libTok = Expect(p, TT_StringLiteral, "Expected a library name (string) after '#extern'");
        p->currentExternLib = libTok->stringValue;
        ExpectStatementTerminator(p);
        return (FcStmt*)FC_NEW(C_, FcNoOpStmt, .h = {SK_NoOpStmt, p->sourceIndex, line});
    }
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "noshadow")) {
        Advance(p);
        ExpectStatementTerminator(p);
        return (FcStmt*)FC_NEW(C_, FcNoShadowDirective, .h = {SK_NoShadowDirective, p->sourceIndex, line});
    }
    if (Check(p, TT_Identifier) && (streq(Peek(p)->lexeme, "lax") || streq(Peek(p)->lexeme, "strict"))) {
        bool strict = streq(Advance(p)->lexeme, "strict");
        ExpectStatementTerminator(p);
        return (FcStmt*)FC_NEW(C_, FcTypeCheckDirective, .h = {SK_TypeCheckDirective, p->sourceIndex, line}, .strict = strict);
    }
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "nosync")) {
        Advance(p);
        ExpectStatementTerminator(p);
        return (FcStmt*)FC_NEW(C_, FcGlobalJobsDirective, .h = {SK_GlobalJobsDirective, p->sourceIndex, line}, .mode = GJM_Sync);
    }
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "globaljobs")) {
        FcGlobalJobsMode mode;
        const char* w;
        char low[32];
        size_t k;
        Advance(p);
        /* GlobalJobsModes.Parse: the word trimmed, in lower case */
        w = Peek(p)->lexeme;
        for (k = 0; w[k] && k < sizeof low - 1; k++) low[k] = (char)(w[k] >= 'A' && w[k] <= 'Z' ? w[k] - 'A' + 'a' : w[k]);
        low[k] = 0;
        if (streq(low, "safepoints")) mode = GJM_Safepoints;
        else if (streq(low, "sleep")) mode = GJM_Sleep;
        else if (streq(low, "sync")) mode = GJM_Sync;
        else Error(p, "'#globaljobs' expects safepoints, sleep or sync", Peek(p));
        Advance(p);
        ExpectStatementTerminator(p);
        return (FcStmt*)FC_NEW(C_, FcGlobalJobsDirective, .h = {SK_GlobalJobsDirective, p->sourceIndex, line}, .mode = mode);
    }
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "endwait")) {
        FcExpr* limit;
        Advance(p);
        limit = ParseExpression(p);
        ExpectStatementTerminator(p);
        return (FcStmt*)FC_NEW(C_, FcEndWaitDirective, .h = {SK_EndWaitDirective, p->sourceIndex, line}, .value = limit);
    }
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "timeout")) {
        FcExpr* value;
        Advance(p);
        value = ParseExpression(p);
        ExpectStatementTerminator(p);
        return (FcStmt*)FC_NEW(C_, FcTimeoutDirective, .h = {SK_TimeoutDirective, p->sourceIndex, line}, .value = value);
    }
    Error(p, S(p, "Unknown preprocessor directive '#%s' (known: '#extern \"libName\"', '#noshadow', '#nosync', '#globaljobs safepoints|sleep|sync', '#timeout value', '#endwait value', '#lax', '#strict')",
        Peek(p)->lexeme), Peek(p));
}

static FcStmt* ParseUnsafeStmt(FcParser* p) {
    int line = Peek(p)->line;
    Expect(p, TT_Unsafe, "Expected 'unsafe'");
    return (FcStmt*)FC_NEW(C_, FcUnsafeStmt, .h = {SK_UnsafeStmt, p->sourceIndex, line}, .body = ParseBlock(p));
}

static FcStmt* ParseWithStmt(FcParser* p) {
    int line = Peek(p)->line;
    FcExpr* target;
    const char* tempName;
    FcList* statements;
    Expect(p, TT_With, "Expected 'with'");
    target = ParseExpression(p);
    tempName = S(p, "__with%d__", p->withCounter);
    p->withCounter++;
    LA(p, p->withVarStack, tempName);
    Expect(p, TT_LBrace, "Expected '{' after the with expression");
    statements = L1(p, mkVarDecl(p, line, tempName, NULL, L(p), target, false));
    while (!Check(p, TT_RBrace) && !Check(p, TT_Eof)) {
        if (Match(p, TT_Semicolon)) continue;
        if (Check(p, TT_Catch)) { LA(p, statements, ParseImplicitCatchTail(p)); break; }
        if (TryParseWarn(p)) continue;
        LA(p, statements, ParseStatement(p));
    }
    Expect(p, TT_RBrace, "Expected '}' at the end of the with block");
    p->withVarStack->count--;
    return (FcStmt*)mkBlock(p, line, statements);
}

static FcExpr* ParseSwitchCaseValue(FcParser* p) {
    int saved = p->suppressColonPostfixAtDepth;
    FcExpr* e;
    p->suppressColonPostfixAtDepth = p->bracketDepth;
    pPushCleanup(p, CL_SUPPRESS_COLON, saved, false, NULL);
    e = ParseExpression(p);
    pPopCleanup(p);
    p->suppressColonPostfixAtDepth = saved;
    return e;
}

static FcList* ParseSwitchCaseBody(FcParser* p) {
    FcList* body = L(p);
    while (!Check(p, TT_RBrace) && !Check(p, TT_Case) && !Check(p, TT_Eof)) {
        if (Match(p, TT_Semicolon)) continue;
        if (Check(p, TT_Break) && !(PeekAt(p, 1)->type == TT_IntLiteral && !PeekAt(p, 1)->newlineBefore)) {
            Advance(p);
            ExpectStatementTerminator(p);
            break;
        }
        if (TryParseWarn(p)) continue;
        LA(p, body, ParseStatement(p));
    }
    return body;
}

static FcStmt* ParseSwitchStmt(FcParser* p) {
    int line = Peek(p)->line, i;
    FcExpr* subject;
    const char* tempName;
    FcList* caseConditions = L(p);
    FcList* caseBodies = L(p);
    FcList* defaultBody = NULL;
    FcList* equalValues = L(p);
    FcStmt* chain;
    FcVarDeclStmt* subjectDecl;
    FcList* outerStatements;
    Expect(p, TT_Switch, "Expected 'switch'");
    Expect(p, TT_LParen, "Expected '(' after 'switch'");
    subject = ParseExpression(p);
    Expect(p, TT_RParen, "Expected ')' after the switch expression");
    Expect(p, TT_LBrace, "Expected '{' after the switch head");
    tempName = S(p, "__switch%d__", p->switchCounter);
    p->switchCounter++;
    while (!Check(p, TT_RBrace) && !Check(p, TT_Eof)) {
        FcExpr* condition = NULL;
        Expect(p, TT_Case, "Expected 'case' in the switch body");
        if (Match(p, TT_Default)) {
            if (defaultBody) Error(p, "Multiple 'case default' in one switch are not allowed", Previous(p));
            Expect(p, TT_Colon, "Expected ':' after 'case default'");
            defaultBody = ParseSwitchCaseBody(p);
            continue;
        }
        do {
            FcBinaryOp op = BOP_Eq;
            FcExpr* valueExpr;
            FcExpr* test;
            if (Check(p, TT_Lt) || Check(p, TT_LtEq) || Check(p, TT_Gt) || Check(p, TT_GtEq) || Check(p, TT_Eq) || Check(p, TT_NotEq)) {
                FcToken* opTok = Advance(p);
                switch (opTok->type) {
                    case TT_Lt: op = BOP_Lt; break;
                    case TT_LtEq: op = BOP_LtEq; break;
                    case TT_Gt: op = BOP_Gt; break;
                    case TT_GtEq: op = BOP_GtEq; break;
                    case TT_Eq: op = BOP_Eq; break;
                    default: op = BOP_NotEq; break;
                }
            }
            valueExpr = ParseSwitchCaseValue(p);
            if (op == BOP_Eq) LA(p, equalValues, valueExpr);
            test = mkBinary(p, line, op, mkIdent(p, line, tempName), valueExpr);
            condition = condition == NULL ? test : mkBinary(p, line, BOP_Or, condition, test);
        } while (Match(p, TT_Comma));
        Expect(p, TT_Colon, "Expected ':' after the case condition");
        LA(p, caseConditions, condition);
        LA(p, caseBodies, ParseSwitchCaseBody(p));
    }
    Expect(p, TT_RBrace, "Expected '}' at the end of the switch");
    chain = defaultBody ? (FcStmt*)mkBlock(p, line, defaultBody) : NULL;
    for (i = caseConditions->count - 1; i >= 0; i--)
        chain = (FcStmt*)FC_NEW(C_, FcIfStmt, .h = {SK_IfStmt, p->sourceIndex, line}, .condition = (FcExpr*)caseConditions->items[i],
            .then = (FcStmt*)mkBlock(p, line, (FcList*)caseBodies->items[i]), .otherwise = chain);
    subjectDecl = (FcVarDeclStmt*)mkVarDecl(p, line, tempName, NULL, L(p), subject, false);
    if (!defaultBody) subjectDecl->switchValues = equalValues;
    outerStatements = L1(p, subjectDecl);
    if (chain) LA(p, outerStatements, chain);
    return (FcStmt*)mkBlock(p, line, outerStatements);
}

/* ---- multithreading ---- */
static FcFireTakingCapture* newCapture(FcParser* p, const char* name, FcExpr* source, bool handover) {
    FcFireTakingCapture* c = (FcFireTakingCapture*)fcAlloc(p->ctx, sizeof *c);
    c->varName = name; c->source = source; c->handover = handover;
    return c;
}

/* the dictionary of a body being parsed: name -> handover, in the order of insertion */
static FcCapture* CaptureFind(FcList* uses, const char* name) {
    int i;
    for (i = 0; i < uses->count; i++) if (streq(((FcCapture*)uses->items[i])->name, name)) return (FcCapture*)uses->items[i];
    return NULL;
}
static void CaptureSet(FcParser* p, FcList* uses, const char* name, bool handover) {
    FcCapture* c = CaptureFind(uses, name);
    if (c) { c->handover = handover; return; }
    c = (FcCapture*)fcAlloc(p->ctx, sizeof *c);
    c->name = name; c->handover = handover;
    LA(p, uses, c);
}

static void NoteCaptureUse(FcParser* p, const char* name, bool handover, const FcToken* at) {
    FcList* uses = (FcList*)p->captureUses->items[p->captureUses->count - 1];
    FcCapture* before = CaptureFind(uses, name);
    if (before && before->handover != handover)
        Error(p, S(p, "'%s' is written with 'taking' and with 'handover' in the same block - one way only (a copy or moved)", name), at);
    CaptureSet(p, uses, name, handover);
}

static FcList* PopCaptureUses(FcParser* p) {
    FcList* uses = (FcList*)p->captureUses->items[p->captureUses->count - 1];
    int i;
    p->captureUses->count--;
    if (p->captureUses->count > 0) {
        FcList* outer = (FcList*)p->captureUses->items[p->captureUses->count - 1];
        for (i = 0; i < uses->count; i++) {
            FcCapture* c = (FcCapture*)uses->items[i];
            if (!CaptureFind(outer, c->name)) CaptureSet(p, outer, c->name, c->handover);
        }
    }
    return uses;
}

static bool IsUntilClause(FcParser* p) {
    FcTokenType t = PeekAt(p, 1)->type;
    return Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "until") && !Peek(p)->newlineBefore
        && (t == TT_Identifier || t == TT_This || t == TT_IntLiteral || t == TT_FloatLiteral || t == TT_New || t == TT_LParen);
}

static FcList* ParseUntilLimits(FcParser* p) {
    FcToken* untilTok = Advance(p);
    FcList* limits = L(p);
    do {
        FcExpr* limit = ParseConditional(p);
        LA(p, limits, mkCall(p, untilTok->line, mkMember(p, untilTok->line, mkIdent(p, untilTok->line, "__Cancel"), "Limit"), L1(p, limit), NULL, NULL));
    } while (!p->untilSingle && Match(p, TT_Comma));
    return limits;
}

static bool IsOnClause(FcParser* p) { return Check(p, TT_On) && (PeekAt(p, 1)->type == TT_Identifier || PeekAt(p, 1)->type == TT_This); }

static void ParseFireTakingWithClauses(FcParser* p, FcList** takingOut, FcList** withOut);

static FcExpr* ParseOnBlockWith(FcParser* p, const char* what, FcList** argsOut, int* withCount) {
    FcToken* onTok = Advance(p);
    FcExpr* target = ParseUnary(p);
    const char* name;
    FcList* captures;
    FcList* onWith;
    FcBlockStmt* body;
    FcList* parameters;
    FcList* args;
    int i, k;
    if (target->kind == EK_IdentifierExpr) name = ((FcIdentifierExpr*)target)->name;
    else if (target->kind == EK_MemberExpr) name = ((FcMemberExpr*)target)->name;
    else if (target->kind == EK_ThisExpr) Error(p, S(p, "'%s on this' - name the object ('%s on window')", what, what), onTok);
    else Error(p, S(p, "'%s on' expects the object a block runs on ('%s on window { ... }')", what, what), onTok);
    ParseFireTakingWithClauses(p, &captures, &onWith);
    if (onWith->count > 0 && !streq(what, "fire")) Error(p, S(p, "'with' does not exist for '%s on'", what), Peek(p));
    if (p->clauseUntil) Error(p, S(p, "'until' does not exist for '%s on' (the block runs in the main program)", what), Peek(p));
    for (i = 0; i < captures->count; i++) if (((FcFireTakingCapture*)captures->items[i])->handover)
        Error(p, S(p, "'handover' does not exist for '%s on' (the block gets copies: use 'taking')", what), Peek(p));
    body = ParseBlock(p);
    parameters = L1(p, newParam(p, name, NULL, L(p), NULL, false));
    for (i = 0; i < captures->count; i++) {
        FcFireTakingCapture* c = (FcFireTakingCapture*)captures->items[i];
        if (!streq(c->varName, name)) LA(p, parameters, newParam(p, c->varName, NULL, L(p), NULL, false));
    }
    args = L1(p, target);
    for (i = 0; i < captures->count; i++) {
        FcFireTakingCapture* c = (FcFireTakingCapture*)captures->items[i];
        if (!streq(c->varName, name)) LA(p, args, c->source);
    }
    for (i = 0; i < onWith->count; i++) {
        FcFireWith* w = (FcFireWith*)onWith->items[i];
        bool twice = streq(w->varName, name);
        for (k = 0; k < captures->count; k++) if (streq(((FcFireTakingCapture*)captures->items[k])->varName, w->varName)) twice = true;
        if (twice) Error(p, S(p, "'%s' is named twice in this '%s on'", w->varName, what), Peek(p));
        LA(p, parameters, newParam(p, w->varName, NULL, L(p), NULL, false));
        LA(p, args, w->source);
    }
    *argsOut = args;
    *withCount = onWith->count;
    return mkLambda(p, onTok->line, parameters, NULL, body, false);
}

static FcStmt* ParseFireOn(FcParser* p, int line) {
    FcList* args;
    int withCount;
    FcExpr* lambda = ParseOnBlockWith(p, "fire", &args, &withCount);
    return (FcStmt*)FC_NEW(C_, FcPostGlobalStmt, .h = {SK_PostGlobalStmt, p->sourceIndex, line}, .lambda = lambda, .args = args, .onTarget = true, .withCount = withCount);
}

static FcStmt* ParseFireGlobal(FcParser* p, int line) {
    FcList* captures;
    FcList* globalWith;
    FcBlockStmt* body;
    FcList* parameters = L(p);
    FcList* args = L(p);
    int i;
    Advance(p);
    ParseFireTakingWithClauses(p, &captures, &globalWith);
    if (globalWith->count > 0) Error(p, "'with' does not exist for 'fire global'", Peek(p));
    if (p->clauseUntil) Error(p, "'until' does not exist for 'fire global' (a job of the main program)", Peek(p));
    for (i = 0; i < captures->count; i++) if (((FcFireTakingCapture*)captures->items[i])->handover)
        Error(p, "'handover' does not exist for 'fire global' (a job gets copies: use 'taking')", Peek(p));
    body = ParseBlock(p);
    for (i = 0; i < captures->count; i++) {
        FcFireTakingCapture* c = (FcFireTakingCapture*)captures->items[i];
        LA(p, parameters, newParam(p, c->varName, NULL, L(p), NULL, false));
        LA(p, args, c->source);
    }
    return (FcStmt*)FC_NEW(C_, FcPostGlobalStmt, .h = {SK_PostGlobalStmt, p->sourceIndex, line}, .lambda = mkLambda(p, line, parameters, NULL, body, false), .args = args);
}

static void ParseFireTakingWithClauses(FcParser* p, FcList** takingOut, FcList** withOut) {
    FcList* takingCaptures = L(p);
    FcList* withVars = L(p);
    FcList* ordered = L(p);
    int i, k;
    p->clauseUntil = NULL;
    while (Check(p, TT_Taking) || Check(p, TT_Handover) || Check(p, TT_With) || IsUntilClause(p)) {
        if (IsUntilClause(p)) {
            if (p->clauseUntil) Error(p, "'until' twice", Peek(p));
            p->clauseUntil = ParseUntilLimits(p);
            continue;
        }
        if (Check(p, TT_Handover)) {
            FcToken* nameTok;
            Advance(p);
            nameTok = Expect(p, TT_Identifier, "Expected an identifier after 'handover'");
            LA(p, takingCaptures, newCapture(p, nameTok->lexeme, mkIdent(p, nameTok->line, nameTok->lexeme), true));
        } else if (Check(p, TT_Taking)) {
            FcToken* nameTok;
            Advance(p);
            nameTok = Expect(p, TT_Identifier, "Expected an identifier after 'taking'");
            LA(p, takingCaptures, newCapture(p, nameTok->lexeme, mkIdent(p, nameTok->line, nameTok->lexeme), false));
        } else {
            Advance(p);
            do {
                FcToken* nameTok = Expect(p, TT_Identifier, "Expected an identifier after 'with'");
                FcFireWith* w;
                for (k = 0; k < withVars->count; k++)
                    if (streq(((FcFireWith*)withVars->items[k])->varName, nameTok->lexeme)) Error(p, S(p, "'%s' is named twice after 'with'", nameTok->lexeme), nameTok);
                w = (FcFireWith*)fcAlloc(p->ctx, sizeof *w);
                w->varName = nameTok->lexeme;
                w->source = mkIdent(p, nameTok->line, nameTok->lexeme);
                LA(p, withVars, w);
            } while (Match(p, TT_Comma));
        }
    }
    for (i = 0; i < takingCaptures->count; i++) if (!((FcFireTakingCapture*)takingCaptures->items[i])->handover) LA(p, ordered, takingCaptures->items[i]);
    for (i = 0; i < takingCaptures->count; i++) if (((FcFireTakingCapture*)takingCaptures->items[i])->handover) LA(p, ordered, takingCaptures->items[i]);
    *takingOut = ordered;
    *withOut = withVars;
}

static FcStmt* WithHandoverReset(FcParser* p, FcFireStmt* fire) {
    FcList* statements;
    bool any = false;
    int i;
    for (i = 0; i < fire->takingCaptures->count; i++) {
        FcFireTakingCapture* c = (FcFireTakingCapture*)fire->takingCaptures->items[i];
        if (c->handover && c->source->kind == EK_IdentifierExpr) any = true;
    }
    if (!any) return (FcStmt*)fire;
    statements = L1(p, fire);
    for (i = 0; i < fire->takingCaptures->count; i++) {
        FcFireTakingCapture* c = (FcFireTakingCapture*)fire->takingCaptures->items[i];
        if (c->handover && c->source->kind == EK_IdentifierExpr)
            LA(p, statements, mkExprStmt(p, fire->h.line, mkAssign(p, fire->h.line, mkIdent(p, fire->h.line, c->varName), mkUndefined(p, fire->h.line))));
    }
    return (FcStmt*)mkBlock(p, fire->h.line, statements);
}

static FcStmt* ParseFireCallForm(FcParser* p, int line, bool dedicated);

static FcStmt* ParseFireStmt(FcParser* p) {
    int line = Peek(p)->line, i, k;
    bool dedicated;
    FcList* takingCaptures;
    FcList* withVars;
    FcList* until;
    FcBlockStmt* body;
    FcList* uses;
    Expect(p, TT_Fire, "Expected 'fire'");
    {
        FcTokenType t1 = PeekAt(p, 1)->type;
        dedicated = Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "dedicated")
            && (t1 == TT_LBrace || t1 == TT_Taking || t1 == TT_Handover || t1 == TT_With || (t1 == TT_Identifier && PeekAt(p, 2)->type == TT_LParen));
    }
    if (dedicated) Advance(p);
    if (Check(p, TT_Identifier) && PeekAt(p, 1)->type == TT_LParen) return ParseFireCallForm(p, line, dedicated);
    if (!dedicated && IsOnClause(p)) return ParseFireOn(p, line);
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "global")
        && (PeekAt(p, 1)->type == TT_LBrace || PeekAt(p, 1)->type == TT_Taking || PeekAt(p, 1)->type == TT_Handover))
        return ParseFireGlobal(p, line);
    ParseFireTakingWithClauses(p, &takingCaptures, &withVars);
    until = p->clauseUntil;
    LA(p, p->captureUses, L(p));
    pPushCleanup(p, CL_CAPTURE_USES, 0, false, NULL);
    body = ParseBlock(p);
    pPopCleanup(p);
    uses = PopCaptureUses(p);
    for (i = 0; i < uses->count; i++) {
        FcCapture* u = (FcCapture*)uses->items[i];
        int named = -1, firstMoved = -1;
        bool inWith = false;
        FcFireTakingCapture* capture;
        for (k = 0; k < takingCaptures->count && named < 0; k++) if (streq(((FcFireTakingCapture*)takingCaptures->items[k])->varName, u->name)) named = k;
        if (named >= 0) {
            bool h = ((FcFireTakingCapture*)takingCaptures->items[named])->handover;
            if (h != u->handover)
                Error(p, S(p, "'%s' is captured with '%s' and written with '%s' in the block", u->name, h ? "handover" : "taking", u->handover ? "handover" : "taking"), Peek(p));
            continue;
        }
        for (k = 0; k < withVars->count; k++) if (streq(((FcFireWith*)withVars->items[k])->varName, u->name)) inWith = true;
        if (inWith) continue;
        capture = newCapture(p, u->name, mkIdent(p, line, u->name), u->handover);
        if (u->handover) LA(p, takingCaptures, capture);
        else {
            for (k = 0; k < takingCaptures->count && firstMoved < 0; k++) if (((FcFireTakingCapture*)takingCaptures->items[k])->handover) firstMoved = k;
            if (firstMoved < 0) LA(p, takingCaptures, capture); else fcListInsert(p->ctx, takingCaptures, firstMoved, capture);
        }
    }
    return WithHandoverReset(p, FC_NEW(C_, FcFireStmt, .h = {SK_FireStmt, p->sourceIndex, line}, .takingCaptures = takingCaptures, .with = withVars, .body = body,
        .dedicated = dedicated, .until = until));
}

static FcStmt* ParseSyncGlobalBlock(FcParser* p) {
    int line = Peek(p)->line;
    FcBlockStmt* body;
    FcBlockStmt* exitBlock;
    FcList* statements;
    Expect(p, TT_Sync, "Expected 'sync'");
    Advance(p);
    body = ParseBlock(p);
    exitBlock = mkBlock(p, line, L1(p, FC_NEW(C_, FcSectionExitStmt, .h = {SK_SectionExitStmt, p->sourceIndex, line})));
    statements = L1(p, FC_NEW(C_, FcSectionEnterStmt, .h = {SK_SectionEnterStmt, p->sourceIndex, line}));
    LA(p, statements, FC_NEW(C_, FcTryStmt, .h = {SK_TryStmt, p->sourceIndex, line}, .tryBlock = body, .catches = L(p), .finallyBlock = exitBlock, .isSyncSection = true));
    return (FcStmt*)mkBlock(p, line, statements);
}

static FcStmt* ParseFireCallForm(FcParser* p, int line, bool dedicated) {
    const char* methodName = Advance(p)->lexeme;
    FcList* callArgs = L(p);
    FcList* explicitTaking;
    FcList* withVars;
    FcList* callUntil;
    FcList* allCaptures;
    FcList* argRefs = L(p);
    FcList* handedOver = L(p);
    FcExpr* callExpr;
    FcBlockStmt* body;
    int i;
    Expect(p, TT_LParen, S(p, "Expected '(' after '%s'", methodName));
    if (!Check(p, TT_RParen)) {
        do { LA(p, callArgs, ParseExpression(p)); } while (Match(p, TT_Comma));
    }
    Expect(p, TT_RParen, S(p, "Expected ')' after 'fire %s(...)'", methodName));
    ParseFireTakingWithClauses(p, &explicitTaking, &withVars);
    callUntil = p->clauseUntil;
    ExpectStatementTerminator(p);
    allCaptures = L1(p, newCapture(p, "__fire_this__", (FcExpr*)FC_NEW(C_, FcThisExpr, .h = {EK_ThisExpr, line}), false));
    for (i = 0; i < callArgs->count; i++) {
        const char* argCaptureName = S(p, "__fire_arg%d__", i);
        FcExpr* a = (FcExpr*)callArgs->items[i];
        if (a->kind == EK_NewExpr) LA(p, handedOver, newCapture(p, argCaptureName, a, true));
        else LA(p, allCaptures, newCapture(p, argCaptureName, a, false));
        LA(p, argRefs, mkIdent(p, line, argCaptureName));
    }
    for (i = 0; i < explicitTaking->count; i++) if (!((FcFireTakingCapture*)explicitTaking->items[i])->handover) LA(p, allCaptures, explicitTaking->items[i]);
    for (i = 0; i < handedOver->count; i++) LA(p, allCaptures, handedOver->items[i]);
    for (i = 0; i < explicitTaking->count; i++) if (((FcFireTakingCapture*)explicitTaking->items[i])->handover) LA(p, allCaptures, explicitTaking->items[i]);
    callExpr = mkCall(p, line, mkMember(p, line, mkIdent(p, line, "__fire_this__"), methodName), argRefs, NULL, NULL);
    body = mkBlock(p, line, L1(p, mkExprStmt(p, line, callExpr)));
    return WithHandoverReset(p, FC_NEW(C_, FcFireStmt, .h = {SK_FireStmt, p->sourceIndex, line}, .takingCaptures = allCaptures, .with = withVars, .body = body,
        .dedicated = dedicated, .until = callUntil));
}

static bool IsAwaitStart(FcParser* p) {
    FcTokenType t = PeekAt(p, 1)->type;
    return Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "await") && !PeekAt(p, 1)->newlineBefore
        && (t == TT_Identifier || t == TT_This || t == TT_LBracket || t == TT_Taking || t == TT_Handover || t == TT_LBrace || t == TT_New || t == TT_On);
}

static FcExpr* ParseAwaitAll(FcParser* p, FcToken* awaitTok);
static FcExpr* ParseAwaited(FcParser* p, FcToken* awaitTok);

static FcExpr* ParseAwait(FcParser* p) {
    FcToken* awaitTok = Advance(p);
    if (Check(p, TT_LBracket)) return ParseAwaitAll(p, awaitTok);
    return ParseAwaited(p, awaitTok);
}

static FcExpr* ParseAwaitAll(FcParser* p, FcToken* awaitTok) {
    int line = awaitTok->line;
    FcList* lambdas = L(p);
    FcExpr* all;
    Expect(p, TT_LBracket, "Expected '['");
    if (!Check(p, TT_RBracket)) {
        do {
            bool savedSingle;
            FcExpr* call;
            int at;
            FcExpr* res;
            FcExpr* fails;
            FcBlockStmt* tryBlock;
            FcBlockStmt* catchBody;
            FcCatchClause* cc;
            FcTypeRef* exceptionType;
            FcBlockStmt* body;
            FcList* parameters;
            FcLambdaExpr* lambda;
            if (Check(p, TT_RBracket)) break;
            savedSingle = p->untilSingle;
            p->untilSingle = true;
            pPushCleanup(p, CL_UNTIL_SINGLE, 0, savedSingle, NULL);
            call = ParseAwaited(p, Peek(p));
            pPopCleanup(p);
            p->untilSingle = savedSingle;
            at = call->line;
            res = (FcExpr*)FC_NEW(C_, FcIndexExpr, .h = {EK_IndexExpr, at}, .target = mkIdent(p, at, "__res"), .index = mkIdent(p, at, "__i"));
            fails = (FcExpr*)FC_NEW(C_, FcIndexExpr, .h = {EK_IndexExpr, at}, .target = mkIdent(p, at, "__fails"), .index = mkIdent(p, at, "__i"));
            tryBlock = mkBlock(p, at, L1(p, mkExprStmt(p, at, mkAssign(p, at, res, call))));
            catchBody = mkBlock(p, at, L1(p, mkExprStmt(p, at, mkAssign(p, at, fails, mkIdent(p, at, "__e")))));
            exceptionType = newTypeRef(p, "Exception", FC_NULL_INT, 0);
            cc = (FcCatchClause*)fcAlloc(p->ctx, sizeof *cc);
            cc->source = p->sourceIndex; cc->line = at; cc->typeRef = exceptionType; cc->varName = "__e"; cc->body = catchBody;
            body = mkBlock(p, at, L1(p, FC_NEW(C_, FcTryStmt, .h = {SK_TryStmt, p->sourceIndex, at}, .tryBlock = tryBlock, .catches = L1(p, cc))));
            parameters = L1(p, newParam(p, "__res", newTypeRef(p, "any", FC_NULL_INT, 0), L(p), NULL, false));
            LA(p, parameters, newParam(p, "__fails", newTypeRef(p, "any", FC_NULL_INT, 0), L(p), NULL, false));
            LA(p, parameters, newParam(p, "__i", newTypeRef(p, "int", FC_NULL_INT, 0), L(p), NULL, false));
            lambda = (FcLambdaExpr*)mkLambda(p, at, parameters, NULL, body, true);
            lambda->lendCaptures = true;
            LA(p, lambdas, lambda);
        } while (Match(p, TT_Comma));
    }
    Expect(p, TT_RBracket, "Expected ']' after the calls of 'await [ ... ]'");
    all = mkCall(p, line, mkMember(p, line, mkIdent(p, line, "__Await"), "All"), L1(p, FC_NEW(C_, FcArrayLiteralExpr, .h = {EK_ArrayLiteralExpr, line}, .elements = lambdas)), NULL, NULL);
    if (IsUntilClause(p)) {
        FcList* limits = ParseUntilLimits(p);
        return (FcExpr*)FC_NEW(C_, FcUntilExpr, .h = {EK_UntilExpr, line}, .inner = all, .limits = limits);
    }
    return all;
}

static FcExpr* ParseAwaitedPlain(FcParser* p, FcToken* awaitTok);

static FcExpr* ParseAwaited(FcParser* p, FcToken* awaitTok) {
    FcTokenType t1 = PeekAt(p, 1)->type;
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "dedicated")
        && (t1 == TT_Identifier || t1 == TT_This || t1 == TT_Taking || t1 == TT_Handover || t1 == TT_LBrace || t1 == TT_New)) {
        FcExpr* e;
        Advance(p);
        e = ParseAwaitedPlain(p, awaitTok);
        if (e->kind == EK_CallExpr) {
            FcCallExpr* dc = (FcCallExpr*)fcNodeDup(p->ctx, e, sizeof(FcCallExpr));
            dc->dedicated = true;
            return (FcExpr*)dc;
        }
        if (e->kind == EK_UntilExpr && ((FcUntilExpr*)e)->inner->kind == EK_CallExpr) {
            FcUntilExpr* du = (FcUntilExpr*)fcNodeDup(p->ctx, e, sizeof(FcUntilExpr));
            FcCallExpr* uc = (FcCallExpr*)fcNodeDup(p->ctx, du->inner, sizeof(FcCallExpr));
            uc->dedicated = true;
            du->inner = (FcExpr*)uc;
            return (FcExpr*)du;
        }
        Error(p, "'await dedicated' expects a call or a block", Peek(p));
    }
    return ParseAwaitedPlain(p, awaitTok);
}

static FcExpr* ApplyAwaitModes(FcParser* p, FcCallExpr* call, FcList* modeNames, FcList* modeOps, int line, FcToken* awaitTok);

static FcExpr* ParseAwaitedPlain(FcParser* p, FcToken* awaitTok) {
    int line = awaitTok->line, i;
    FcExpr* awaited;
    FcList* modeNames = L(p);
    FcList* modeOps = L(p);
    FcList* callUntilLimits = NULL;
    if (IsOnClause(p)) {
        FcList* onArgs;
        int withCount;
        FcExpr* onLambda = ParseOnBlockWith(p, "await", &onArgs, &withCount);
        return (FcExpr*)FC_NEW(C_, FcAwaitOnExpr, .h = {EK_AwaitOnExpr, line}, .lambda = onLambda, .args = onArgs);
    }
    if (Check(p, TT_Taking) || Check(p, TT_Handover) || Check(p, TT_LBrace)) {
        FcList* captures;
        FcList* awaitWith;
        FcList* blockUntil;
        FcBlockStmt* body;
        FcList* parameters = L(p);
        FcList* args = L(p);
        FcExpr* block;
        ParseFireTakingWithClauses(p, &captures, &awaitWith);
        if (awaitWith->count > 0) Error(p, "'with' does not exist for 'await' (an actor's method is awaited by calling it: 'await actor.Method()')", Peek(p));
        blockUntil = p->clauseUntil;
        body = ParseBlock(p);
        for (i = 0; i < captures->count; i++) {
            FcFireTakingCapture* c = (FcFireTakingCapture*)captures->items[i];
            LA(p, parameters, newParam(p, c->varName, newTypeRef(p, "any", FC_NULL_INT, 0), L(p), NULL, false));
            LA(p, args, mkUnary(p, line, c->handover ? UOP_Take : UOP_DeepCopy, c->source));
        }
        block = mkCall(p, line, mkLambda(p, line, parameters, NULL, body, false), args, NULL, NULL);
        return blockUntil ? (FcExpr*)FC_NEW(C_, FcUntilExpr, .h = {EK_UntilExpr, line}, .inner = block, .limits = blockUntil) : block;
    }
    awaited = ParseUnary(p);
    if (awaited->kind != EK_CallExpr)
        ParseError(p, "'await' expects a call ('await Count(list)') or a block ('await taking x { return ... }')", awaitTok->line, awaitTok->column);
    while (Check(p, TT_Taking) || Check(p, TT_Handover) || IsUntilClause(p)) {
        bool handover;
        FcToken* nameTok;
        int at = -1;
        if (IsUntilClause(p)) {
            if (callUntilLimits) Error(p, "'until' twice", Peek(p));
            callUntilLimits = ParseUntilLimits(p);
            continue;
        }
        handover = Advance(p)->type == TT_Handover;
        nameTok = Expect(p, TT_Identifier, handover ? "Expected an identifier after 'handover'" : "Expected an identifier after 'taking'");
        for (i = 0; i < modeNames->count; i++) if (streq((const char*)modeNames->items[i], nameTok->lexeme)) at = i;
        if (at < 0) { LA(p, modeNames, nameTok->lexeme); LA(p, modeOps, (void*)(intptr_t)(handover ? UOP_Take : UOP_DeepCopy)); }
        else modeOps->items[at] = (void*)(intptr_t)(handover ? UOP_Take : UOP_DeepCopy);
    }
    if (callUntilLimits) {
        FcExpr* limited = ApplyAwaitModes(p, (FcCallExpr*)awaited, modeNames, modeOps, line, awaitTok);
        return (FcExpr*)FC_NEW(C_, FcUntilExpr, .h = {EK_UntilExpr, line}, .inner = limited, .limits = callUntilLimits);
    }
    return ApplyAwaitModes(p, (FcCallExpr*)awaited, modeNames, modeOps, line, awaitTok);
}

static FcExpr* AwaitApply(FcParser* p, FcExpr* e, FcList* modeNames, FcList* modeOps, FcList* used) {
    int i;
    if (e->kind != EK_IdentifierExpr) return e;
    for (i = 0; i < modeNames->count; i++)
        if (streq((const char*)modeNames->items[i], ((FcIdentifierExpr*)e)->name)) {
            if (fcListHasStr(used, ((FcIdentifierExpr*)e)->name)) return e;
            LA(p, used, ((FcIdentifierExpr*)e)->name);
            return mkUnary(p, e->line, (FcUnaryOp)(intptr_t)modeOps->items[i], e);
        }
    return e;
}

static FcExpr* ApplyAwaitModes(FcParser* p, FcCallExpr* call, FcList* modeNames, FcList* modeOps, int line, FcToken* awaitTok) {
    FcList* used;
    FcExpr* callee;
    FcCallExpr* result;
    FcList* args;
    int i;
    if (call->callee->kind == EK_MemberExpr) {
        call = (FcCallExpr*)fcNodeDup(p->ctx, call, sizeof *call);
        call->awaited = true;
    }
    if (modeNames->count == 0) return (FcExpr*)call;
    used = L(p);
    callee = call->callee;
    if (callee->kind == EK_MemberExpr) {
        FcMemberExpr* m = (FcMemberExpr*)fcNodeDup(p->ctx, callee, sizeof(FcMemberExpr));
        m->target = AwaitApply(p, m->target, modeNames, modeOps, used);
        callee = (FcExpr*)m;
    }
    args = L(p);
    for (i = 0; i < call->args->count; i++) LA(p, args, AwaitApply(p, (FcExpr*)call->args->items[i], modeNames, modeOps, used));
    result = (FcCallExpr*)fcNodeDup(p->ctx, call, sizeof *call);
    result->callee = callee;
    result->args = args;
    for (i = 0; i < modeNames->count; i++)
        if (!fcListHasStr(used, (const char*)modeNames->items[i]))
            ParseError(p, S(p, "'%s' is not an argument of the awaited call", (const char*)modeNames->items[i]), line, awaitTok->column);
    return (FcExpr*)result;
}

static FcStmt* ParseTerminateStmt(FcParser* p) {
    int line = Peek(p)->line;
    FcExpr* value = NULL;
    Expect(p, TT_Terminate, "Expected 'terminate'");
    Expect(p, TT_LParen, "Expected '(' after 'terminate'");
    if (!Check(p, TT_RParen)) value = ParseExpression(p);
    Expect(p, TT_RParen, "Expected ')' after the terminate argument");
    ExpectStatementTerminator(p);
    return (FcStmt*)FC_NEW(C_, FcTerminateStmt, .h = {SK_TerminateStmt, p->sourceIndex, line}, .value = value);
}

static FcStmt* ParseProcessStmt(FcParser* p) {
    int line = Peek(p)->line;
    FcExpr* target;
    Expect(p, TT_Process, "Expected 'process'");
    target = ParsePostfix(p);
    ExpectStatementTerminator(p);
    return (FcStmt*)FC_NEW(C_, FcProcessStmt, .h = {SK_ProcessStmt, p->sourceIndex, line}, .target = target);
}

static FcStmt* ParseGlobalHandlerDecl(FcParser* p) {
    int line = Peek(p)->line;
    FcTypeRef* typeRef = NULL;
    const char* varName = NULL;
    Expect(p, TT_Catch, "Expected 'catch'");
    if (Match(p, TT_Terminate)) {
        const char* paramName = NULL;
        Expect(p, TT_LParen, "Expected '(' after 'catch terminate'");
        if (!Check(p, TT_RParen)) paramName = Expect(p, TT_Identifier, "Expected a parameter name in 'catch terminate(...)'")->lexeme;
        Expect(p, TT_RParen, "Expected ')' after 'catch terminate(...)'");
        return (FcStmt*)FC_NEW(C_, FcCatchTerminateDecl, .h = {SK_CatchTerminateDecl, p->sourceIndex, line}, .varName = paramName, .body = ParseBlock(p));
    }
    Expect(p, TT_Identifier, "Expected 'threads' or 'terminate' after 'catch'");
    Expect(p, TT_LParen, "Expected '(' after 'catch threads'");
    if (!Check(p, TT_RParen)) {
        const char* typeName = Expect(p, TT_Identifier, "Expected a type name in 'catch threads(...)'")->lexeme;
        typeRef = newTypeRef(p, typeName, FC_NULL_INT, 0);
        typeRef->namespaces = CurrentNamespaces(p);
        varName = Expect(p, TT_Identifier, "Expected a parameter name in 'catch threads(...)'")->lexeme;
    }
    Expect(p, TT_RParen, "Expected ')' after 'catch threads(...)'");
    return (FcStmt*)FC_NEW(C_, FcCatchThreadsDecl, .h = {SK_CatchThreadsDecl, p->sourceIndex, line}, .typeRef = typeRef, .varName = varName, .body = ParseBlock(p));
}

/* ---- classes ---- */
static FcStmt* ParseClassOrActorDecl(FcParser* p, bool isActor, bool isRecord);
static FcStmt* ParseClassDecl(FcParser* p) { return ParseClassOrActorDecl(p, false, false); }
static FcStmt* ParseActorDecl(FcParser* p) { return ParseClassOrActorDecl(p, true, false); }
static FcStmt* ParseRecordDecl(FcParser* p) { return ParseClassOrActorDecl(p, false, true); }
static bool IsRecordStart(FcParser* p) {
    FcTokenType t2 = PeekAt(p, 2)->type;
    return Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "record") && PeekAt(p, 1)->type == TT_Identifier && (t2 == TT_LBrace || t2 == TT_Colon || t2 == TT_Lt);
}

static FcStmt* ParseClassExtensionDecl(FcParser* p, int line);

/* the base list of a class or an interface: `: Name<Args>, Other` */
static FcTypeRef* BaseRef(FcParser* p, const char* baseName, const char* name, FcList* namespaces, FcList* baseTypeArgs) {
    FcTypeRef* t = newTypeRef(p, baseName, FC_NULL_INT, 0);
    t->namespaces = streq(baseName, name) && namespaces->own ? fcListCopy(p->ctx, namespaces) : namespaces;
    t->typeArgCount = baseTypeArgs ? baseTypeArgs->count : 0;
    t->typeArgs = baseTypeArgs;
    return t;
}

static FcStmt* ParseClassOrActorDecl(FcParser* p, bool isActor, bool isRecord) {
    int line = Peek(p)->line;
    const char* name;
    FcList* typeParamNames;
    FcList* namespaces;
    FcList* baseRefs = L(p);
    FcList* typeParams;
    FcList* members = L(p);
    const char* savedClassName;
    bool savedClassIsGeneric;
    if (isActor) Expect(p, TT_Actor, "Expected 'actor'");
    else if (isRecord) Advance(p);
    else Expect(p, TT_Class, "Expected 'class'");
    if (!isRecord && Check(p, TT_Extends)) return ParseClassExtensionDecl(p, line);
    name = Expect(p, TT_Identifier, "Expected a class name")->lexeme;
    typeParamNames = ParseOptionalTypeParamNames(p);
    namespaces = CurrentNamespaces(p);
    if (Match(p, TT_Colon)) {
        do {
            const char* baseName = ParseDottedName(p, "base class/interface names");
            FcList* baseTypeArgs = Check(p, TT_Lt) ? ParseTypeArgRefs(p) : NULL;
            LA(p, baseRefs, BaseRef(p, baseName, name, namespaces, baseTypeArgs));
        } while (Match(p, TT_Comma));
    }
    typeParams = ParseWhereClauses(p, typeParamNames, line);
    Expect(p, TT_LBrace, "Expected '{' after the class head");
    savedClassName = p->currentClassName;
    savedClassIsGeneric = p->currentClassIsGeneric;
    p->currentClassName = QualifyDeclName(p, name);
    p->currentClassIsGeneric = typeParamNames->count > 0;
    pPushCleanup(p, CL_CLASS_NAME, 0, savedClassIsGeneric, savedClassName);
    while (!Check(p, TT_RBrace) && !Check(p, TT_Eof)) {
        FcList* m;
        int i;
        if (TryParseWarn(p)) continue;
        m = ParseClassMember(p);
        for (i = 0; i < m->count; i++) LA(p, members, m->items[i]);
    }
    pPopCleanup(p);
    p->currentClassName = savedClassName;
    p->currentClassIsGeneric = savedClassIsGeneric;
    Expect(p, TT_RBrace, "Expected '}' at the end of the class");
    return (FcStmt*)FC_NEW(C_, FcClassDecl, .h = {SK_ClassDecl, p->sourceIndex, line}, .name = QualifyDeclName(p, name), .baseRefs = baseRefs, .members = members,
        .typeParams = typeParams, .isActor = isActor, .isRecord = isRecord);
}

static void SkipOptionalNestedTypeArgs(FcParser* p) {
    if (!Match(p, TT_Lt)) return;
    do {
        ParseTypeAnnotationName(p);
        SkipOptionalNestedTypeArgs(p);
    } while (Match(p, TT_Comma));
    Expect(p, TT_Gt, "Expected '>' after the nested type argument list");
}

static FcList* ParseOptionalTypeParamNames(FcParser* p) {
    FcList* names = L(p);
    if (!Match(p, TT_Lt)) return names;
    do {
        LA(p, names, ParseTypeAnnotationName(p));
        SkipOptionalNestedTypeArgs(p);
    } while (Match(p, TT_Comma));
    Expect(p, TT_Gt, "Expected '>' after the type parameter list");
    return names;
}

static FcTypeConstraint* ParseOneTypeConstraint(FcParser* p) {
    FcTypeConstraint* tc = (FcTypeConstraint*)fcAlloc(p->ctx, sizeof *tc);
    Expect(p, TT_Is, "Expected 'is' in a where condition");
    if (Match(p, TT_Of)) { tc->kind = TCK_IsOf; tc->name = ParseTypeAnnotationName(p); return tc; }
    if (Match(p, TT_In)) {
        FcToken* tok = Expect(p, TT_StringLiteral, "Expected a unit name (as a string) after 'is in'");
        tc->kind = TCK_IsIn; tc->name = tok->stringValue;
        return tc;
    }
    Error(p, "Expected 'of' or 'in' after 'is' in a where condition", Peek(p));
}

static FcList* ParseWhereClauses(FcParser* p, FcList* typeParamNames, int declLine) {
    FcList* names = L(p);
    FcList* groupsByName = L(p);
    FcList* result = L(p);
    int i, k;
    (void)declLine;
    while (Match(p, TT_Where)) {
        const char* tpName = Expect(p, TT_Identifier, "Expected a type parameter name after 'where'")->lexeme;
        FcList* groups = L(p);
        if (!fcListHasStr(typeParamNames, tpName)) {
            FcBuf declared = {0};
            fcBufStr(&declared, "");
            if (typeParamNames->count == 0) fcBufStr(&declared, "none");
            for (i = 0; i < typeParamNames->count; i++) { if (i) fcBufStr(&declared, ", "); fcBufStr(&declared, (const char*)typeParamNames->items[i]); }
            Error(p, S(p, "'where %s' does not refer to a declared type parameter (declared: %s)", tpName, fcBufDone(p->ctx, &declared)), Previous(p));
        }
        if (fcListHasStr(names, tpName)) Error(p, S(p, "Multiple 'where %s' clauses for the same type parameter", tpName), Previous(p));
        do {
            FcList* constraintsInGroup = L(p);
            FcTypeConstraintGroup* g = (FcTypeConstraintGroup*)fcAlloc(p->ctx, sizeof *g);
            do { LA(p, constraintsInGroup, ParseOneTypeConstraint(p)); } while (Match(p, TT_Colon));
            g->constraints = constraintsInGroup;
            LA(p, groups, g);
        } while (Match(p, TT_Comma));
        LA(p, names, tpName);
        LA(p, groupsByName, groups);
    }
    for (i = 0; i < typeParamNames->count; i++) {
        FcTypeParam* tp = (FcTypeParam*)fcAlloc(p->ctx, sizeof *tp);
        tp->name = (const char*)typeParamNames->items[i];
        tp->constraintGroups = L(p);
        for (k = 0; k < names->count; k++) if (streq((const char*)names->items[k], tp->name)) tp->constraintGroups = (FcList*)groupsByName->items[k];
        LA(p, result, tp);
    }
    return result;
}

static void ValidateBaseTypeExtensionMember(FcParser* p, const char* typeName, FcStmt* member) {
    const char* prefix = S(p, "'class extends %s': ", typeName);
    switch (member->kind) {
        case SK_MethodDecl: {
            FcMethodDecl* m = (FcMethodDecl*)member;
            if (m->isStatic)
                ParseError(p, S(p, "%sstatic method '%s' is not allowed - extensions of base types consist of instance methods only.", prefix, m->name), m->h.line, 1);
            if (fcStartsWith(m->name, "operator") || streq(m->name, "GetIndex") || streq(m->name, "SetIndex"))
                ParseError(p, S(p, "%sOperators cannot be overloaded for base types.", prefix), m->h.line, 1);
            return;
        }
        case SK_FieldDecl:
            ParseError(p, S(p, "%sfield '%s' is not allowed - extensions of base types may only contain methods.", prefix, ((FcFieldDecl*)member)->name), member->line, 1);
        case SK_PropertyDecl:
            ParseError(p, S(p, "%sproperty '%s' is not allowed - extensions of base types may only contain methods.", prefix, ((FcPropertyDecl*)member)->name), member->line, 1);
        case SK_ConstructorDecl:
            ParseError(p, S(p, "%sa constructor is not allowed - extensions of base types may only contain methods.", prefix), member->line, 1);
        case SK_DestructorDecl:
            ParseError(p, S(p, "%sa destructor is not allowed - extensions of base types may only contain methods.", prefix), member->line, 1);
        default:
            ParseError(p, S(p, "%sonly methods are allowed.", prefix), member->line, 1);
    }
}

static FcStmt* ParseClassExtensionDecl(FcParser* p, int line) {
    bool isBaseType;
    const char* targetName;
    FcTypeRef* targetRef;
    FcList* members = L(p);
    const char* savedExtension;
    int i;
    Expect(p, TT_Extends, "Expected 'extends'");
    isBaseType = IsTypeKeyword(Peek(p)->type) && Peek(p)->type != TT_Class && Peek(p)->type != TT_Undefined;
    targetName = isBaseType ? Advance(p)->lexeme : Expect(p, TT_Identifier, "Expected the name of the class to extend")->lexeme;
    while (!isBaseType && Check(p, TT_Dot) && PeekAt(p, 1)->type == TT_Identifier) {
        Advance(p);
        targetName = S(p, "%s.%s", targetName, Advance(p)->lexeme);
    }
    if (isBaseType && !IsExtendable(targetName))
        Error(p, streq(targetName, "byte") ? "'byte' cannot be extended - a byte is an int at run time, extend 'int'" : S(p, "'%s' cannot be extended", targetName), Previous(p));
    targetRef = newTypeRef(p, targetName, FC_NULL_INT, 0);
    targetRef->namespaces = CurrentNamespaces(p);
    Expect(p, TT_LBrace, S(p, "Expected '{' after 'class extends %s'", targetName));
    savedExtension = p->extensionClassName;
    p->extensionClassName = targetName;
    pPushCleanup(p, CL_EXTENSION_NAME, 0, false, savedExtension);
    while (!Check(p, TT_RBrace) && !Check(p, TT_Eof)) {
        FcList* m;
        if (TryParseWarn(p)) continue;
        m = ParseClassMember(p);
        for (i = 0; i < m->count; i++) LA(p, members, m->items[i]);
    }
    pPopCleanup(p);
    p->extensionClassName = savedExtension;
    Expect(p, TT_RBrace, "Expected '}' at the end of the extension");
    if (isBaseType || IsExtendable(targetName))
        for (i = 0; i < members->count; i++) ValidateBaseTypeExtensionMember(p, targetName, (FcStmt*)members->items[i]);
    return (FcStmt*)FC_NEW(C_, FcClassExtensionDecl, .h = {SK_ClassExtensionDecl, p->sourceIndex, line}, .targetRef = targetRef, .members = members);
}

static FcStmt* ParseNamespaceDecl(FcParser* p) {
    int line = Peek(p)->line;
    const char* name;
    const char* savedNamespace;
    FcList* members = L(p);
    Expect(p, TT_Namespace, "Expected 'namespace'");
    name = ParseDottedName(p, "Namespace-Namen");
    Expect(p, TT_LBrace, "Expected '{' after the namespace name");
    savedNamespace = p->currentNamespace;
    p->currentNamespace = p->currentNamespace == NULL || (p->namespaceDepth == 0 && p->implicitNamespace != NULL) ? name : S(p, "%s.%s", p->currentNamespace, name);
    p->namespaceDepth++;
    while (!Check(p, TT_RBrace) && !Check(p, TT_Eof)) {
        if (TryParseWarn(p)) continue;
        LA(p, members, ParseStatement(p));
    }
    Expect(p, TT_RBrace, "Expected '}' at the end of the namespace");
    p->namespaceDepth--;
    p->currentNamespace = savedNamespace;
    return (FcStmt*)FC_NEW(C_, FcNamespaceDecl, .h = {SK_NamespaceDecl, p->sourceIndex, line}, .name = name, .members = members);
}

static const char* ParseDottedName(FcParser* p, const char* what) {
    const char* name = Expect(p, TT_Identifier, S(p, "Expected %s", what))->lexeme;
    while (Match(p, TT_Dot)) name = S(p, "%s.%s", name, Expect(p, TT_Identifier, S(p, "Expected %s after '.'", what))->lexeme);
    return name;
}

static FcInterfacePropertySig* ParseInterfacePropertyBody(FcParser* p, int line, FcTypeRef* type, const char* name) {
    bool hasGet = false, hasSet = false;
    FcInterfacePropertySig* sig;
    Expect(p, TT_LBrace, "Expected '{' after the property name");
    while (!Check(p, TT_RBrace) && !Check(p, TT_Eof)) {
        FcToken* accessor = Peek(p);
        bool isGet;
        if (accessor->type != TT_Identifier || !(streq(accessor->lexeme, "get") || streq(accessor->lexeme, "set")))
            Error(p, "Expected 'get' or 'set' in the property body", accessor);
        Advance(p);
        isGet = streq(accessor->lexeme, "get");
        if (isGet ? hasGet : hasSet) Error(p, S(p, "'%s' is already defined for property '%s'", accessor->lexeme, name), accessor);
        if (Check(p, TT_LBrace))
            Error(p, S(p, "A property of an interface has no body: write '%s;' (the class that implements it has the code)", accessor->lexeme), Peek(p));
        if (isGet) hasGet = true; else hasSet = true;
        ExpectStatementTerminator(p);
    }
    Expect(p, TT_RBrace, "Expected '}' at the end of the property");
    if (!hasGet && !hasSet) Error(p, S(p, "Property '%s' needs at least 'get' or 'set'", name), Peek(p));
    if (!type) Error(p, S(p, "Property '%s' of an interface needs a type (use 'any' if every type is allowed)", name), Peek(p));
    sig = (FcInterfacePropertySig*)fcAlloc(p->ctx, sizeof *sig);
    sig->line = line; sig->type = type; sig->name = name; sig->hasGet = hasGet; sig->hasSet = hasSet;
    return sig;
}

static const char* ParseUnitName(FcParser* p);

static FcStmt* ParseInterfaceDecl(FcParser* p) {
    int line = Peek(p)->line;
    const char* name;
    FcList* typeParamNames;
    FcList* baseRefs = NULL;
    FcList* typeParams;
    FcList* methods = L(p);
    FcList* properties = L(p);
    Expect(p, TT_Interface, "Expected 'interface'");
    name = Expect(p, TT_Identifier, "Expected an interface name")->lexeme;
    typeParamNames = ParseOptionalTypeParamNames(p);
    if (Match(p, TT_Colon)) {
        FcList* namespaces = CurrentNamespaces(p);
        baseRefs = L(p);
        do {
            const char* baseName = ParseDottedName(p, "interface names");
            FcList* baseTypeArgs = Check(p, TT_Lt) ? ParseTypeArgRefs(p) : NULL;
            LA(p, baseRefs, BaseRef(p, baseName, name, namespaces, baseTypeArgs));
        } while (Match(p, TT_Comma));
    }
    typeParams = typeParamNames->count > 0 ? ParseWhereClauses(p, typeParamNames, line) : NULL;
    Expect(p, TT_LBrace, "Expected '{' after the interface head");
    while (!Check(p, TT_RBrace) && !Check(p, TT_Eof)) {
        int mLine = Peek(p)->line;
        FcTypeRef* returnType = NULL;
        const char* methodName;
        FcList* parms;
        FcInterfaceMethodSig* sig;
        if (NextLooksLikeTypeThenName(p)) returnType = ParseTypeRef(p, true);
        methodName = Expect(p, TT_Identifier, "Expected a method name")->lexeme;
        if (returnType && Check(p, TT_Colon) && PeekAt(p, 1)->type == TT_Identifier
            && (PeekAt(p, 1 + CombinedUnitLengthAt(p, 1))->type == TT_LBrace
                || (streq(PeekAt(p, 1)->lexeme, "any") && PeekAt(p, 2)->type == TT_Identifier && PeekAt(p, 2 + CombinedUnitLengthAt(p, 2))->type == TT_LBrace))) {
            Advance(p);
            returnType = copyTypeRef(p, returnType);
            returnType->unit = ParseUnitName(p);
        }
        if (Check(p, TT_LBrace)) { LA(p, properties, ParseInterfacePropertyBody(p, mLine, returnType, methodName)); continue; }
        parms = ParseParamList(p, true);
        ExpectStatementTerminator(p);
        sig = (FcInterfaceMethodSig*)fcAlloc(p->ctx, sizeof *sig);
        sig->line = mLine; sig->returnType = returnType; sig->name = methodName; sig->params = parms;
        LA(p, methods, sig);
    }
    Expect(p, TT_RBrace, "Expected '}' at the end of the interface");
    return (FcStmt*)FC_NEW(C_, FcInterfaceDecl, .h = {SK_InterfaceDecl, p->sourceIndex, line}, .name = QualifyDeclName(p, name), .methods = methods, .typeParams = typeParams,
        .baseRefs = baseRefs, .properties = properties->count > 0 ? properties : NULL);
}

static FcStmt* ParseEnumDecl(FcParser* p, bool isFlags) {
    int line = Peek(p)->line;
    const char* name;
    FcList* members = L(p);
    FcEnumDecl* decl;
    Expect(p, TT_Enum, "Expected 'enum'");
    name = Expect(p, TT_Identifier, "Expected an enum name")->lexeme;
    Expect(p, TT_LBrace, "Expected '{' after the enum name");
    if (!Check(p, TT_RBrace)) {
        do {
            const char* memberName = Expect(p, TT_Identifier, "Expected an enum member name")->lexeme;
            FcExpr* valueExpr = NULL;
            FcEnumMember* m;
            if (Match(p, TT_Assign)) {
                valueExpr = ParseExpression(p);
                if (valueExpr->kind == EK_UnaryExpr && ((FcUnaryExpr*)valueExpr)->op == UOP_Negate && ((FcUnaryExpr*)valueExpr)->operand->kind == EK_LiteralExpr
                    && ((FcLiteralExpr*)((FcUnaryExpr*)valueExpr)->operand)->value.kind == VK_Int) {
                    FcLiteralExpr* negated = (FcLiteralExpr*)((FcUnaryExpr*)valueExpr)->operand;
                    FcValue v;
                    memset(&v, 0, sizeof v);
                    v.kind = VK_Int;
                    v.i = (int64_t)(0 - (uint64_t)negated->value.i);
                    valueExpr = (FcExpr*)FC_NEW(C_, FcLiteralExpr, .h = {EK_LiteralExpr, negated->h.line}, .value = v);
                }
            }
            m = (FcEnumMember*)fcAlloc(p->ctx, sizeof *m);
            m->name = memberName; m->valueExpr = valueExpr;
            LA(p, members, m);
        } while (Match(p, TT_Comma));
    }
    Expect(p, TT_RBrace, "Expected '}' after the enum members");
    decl = FC_NEW(C_, FcEnumDecl, .h = {SK_EnumDecl, p->sourceIndex, line}, .name = QualifyDeclName(p, name), .members = members, .isFlags = isFlags);
    LA(p, p->enumDecls, decl);
    return (FcStmt*)decl;
}

static FcList* ParsePropertyBody(FcParser* p, int line, FcTypeRef* type, const char* name, FcAccess access, bool isStatic) {
    FcBlockStmt* getter = NULL;
    FcBlockStmt* setter = NULL;
    bool getterIsAuto = false, setterIsAuto = false;
    FcExpr* initializer = NULL;
    FcList* result = L(p);
    Expect(p, TT_LBrace, "Expected '{' after the property name");
    while (!Check(p, TT_RBrace) && !Check(p, TT_Eof)) {
        if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "get")) {
            if (getter || getterIsAuto) Error(p, S(p, "'get' is already defined for property '%s'", name), Peek(p));
            Advance(p);
            if (Check(p, TT_LBrace)) getter = ParseBlock(p);
            else { ExpectStatementTerminator(p); getterIsAuto = true; }
        } else if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "set")) {
            if (setter || setterIsAuto) Error(p, S(p, "'set' is already defined for property '%s'", name), Peek(p));
            Advance(p);
            if (Check(p, TT_LBrace)) setter = ParseBlock(p);
            else { ExpectStatementTerminator(p); setterIsAuto = true; }
        } else Error(p, "Expected 'get' or 'set' in the property body", Peek(p));
    }
    Expect(p, TT_RBrace, "Expected '}' at the end of the property");
    if (!getter && !getterIsAuto && !setter && !setterIsAuto) Error(p, S(p, "Property '%s' needs at least 'get' or 'set'", name), Peek(p));
    if (Check(p, TT_Assign)) {
        if (!getterIsAuto && !setterIsAuto)
            Error(p, S(p, "Only an auto-property ('get;' or 'set;') can have an initial value - '%s' has no backing field", name), Peek(p));
        Advance(p);
        initializer = ParseExpression(p);
        ExpectStatementTerminator(p);
    }
    if (getterIsAuto || setterIsAuto) {
        const char* backingName = S(p, "_Auto%s", name);
        FcExpr* backingTarget;
        LA(p, result, FC_NEW(C_, FcFieldDecl, .h = {SK_FieldDecl, p->sourceIndex, line}, .type = type, .arrayRanks = L(p), .name = backingName, .initializer = initializer,
            .access = ACC_Private, .isStatic = isStatic));
        backingTarget = !isStatic ? (FcExpr*)FC_NEW(C_, FcThisExpr, .h = {EK_ThisExpr, line})
            : p->currentClassIsGeneric ? (FcExpr*)FC_NEW(C_, FcSelfClassExpr, .h = {EK_SelfClassExpr, line})
            : mkIdent(p, line, p->currentClassName ? p->currentClassName : name);
        if (getterIsAuto) getter = mkBlock(p, line, L1(p, mkReturn(p, line, mkMember(p, line, backingTarget, backingName))));
        if (setterIsAuto) setter = mkBlock(p, line, L1(p, mkExprStmt(p, line, mkAssign(p, line, mkMember(p, line, backingTarget, backingName), mkIdent(p, line, "value")))));
    }
    LA(p, result, FC_NEW(C_, FcPropertyDecl, .h = {SK_PropertyDecl, p->sourceIndex, line}, .type = type, .name = name, .getter = getter, .setter = setter, .access = access,
        .isStatic = isStatic));
    return result;
}

static FcAccess ParseOptionalAccessModifier(FcParser* p) {
    if (Match(p, TT_Public)) return ACC_Public;
    if (Match(p, TT_Private)) return ACC_Private;
    if (Match(p, TT_Protected)) return ACC_Protected;
    return ACC_Public;
}

static FcList* ParseClassMemberCore(FcParser* p);

static int CountRequired(FcList* params) {
    int n = 0, i;
    for (i = 0; i < params->count; i++) if (!((FcLambdaParam*)params->items[i])->defaultValue) n++;
    return n;
}

static FcList* ParseClassMember(FcParser* p) {
    FcList* outer = p->memberWarnings;
    FcList* mine = L(p);
    FcList* result;
    const char* className;
    int i, k;
    p->memberWarnings = mine;
    pPushCleanup(p, CL_MEMBER_WARNINGS, 0, false, outer);
    result = ParseClassMemberCore(p);
    pPopCleanup(p);
    p->memberWarnings = outer;
    if (mine->count == 0) return result;
    className = p->currentClassName ? p->currentClassName : p->extensionClassName;
    for (i = 0; i < mine->count; i++) {
        FcMemberWarning* w = (FcMemberWarning*)mine->items[i];
        const char* plain;
        if (!className) { fcWarn(p->ctx, w->id, w->text, w->line, p->fileName); continue; }
        plain = PlainName(p, className);
        for (k = 0; k < result->count; k++) {
            FcStmt* member = (FcStmt*)result->items[k];
            FcDeclaredWarning* dw = (FcDeclaredWarning*)fcAlloc(p->ctx, sizeof *dw);
            dw->cls = plain; dw->id = w->id; dw->text = w->text; dw->line = w->line; dw->file = p->fileName;
            switch (member->kind) {
                case SK_MethodDecl: {
                    FcMethodDecl* m = (FcMethodDecl*)member;
                    dw->target = FC_WARN_METHOD; dw->name = m->name; dw->minArity = CountRequired(m->params); dw->maxArity = m->params->count; dw->isStatic = m->isStatic;
                    break;
                }
                case SK_ConstructorDecl: {
                    FcConstructorDecl* cd = (FcConstructorDecl*)member;
                    dw->target = FC_WARN_CONSTRUCTOR; dw->minArity = CountRequired(cd->params); dw->maxArity = cd->params->count;
                    break;
                }
                case SK_PropertyDecl: dw->target = FC_WARN_MEMBER; dw->name = ((FcPropertyDecl*)member)->name; dw->isStatic = ((FcPropertyDecl*)member)->isStatic; break;
                case SK_FieldDecl: dw->target = FC_WARN_MEMBER; dw->name = ((FcFieldDecl*)member)->name; dw->isStatic = ((FcFieldDecl*)member)->isStatic; break;
                default: dw->target = FC_WARN_CLASS; break;
            }
            fcDeclare(p->ctx, dw);
        }
    }
    return result;
}

/* AttributesBeforeClass: a ParseException while the attributes are read means they are no attributes (the position goes back; what the
 * `finally` blocks of the code in between restore is restored, as in C#) */
static FcList* fcTryAttributes(FcParser* p, int start, int depth) {
    FcList* volatile attributes = NULL;
    FC_TRY(p->ctx) {
        attributes = ParseAttributes(p);
    } FC_CATCH(p->ctx) {
        if (p->ctx->err.kind != FC_ERR_PARSE) fcRethrow(p->ctx);
        pRunCleanupsTo(p, depth);
        p->pos = start;
        attributes = NULL;
    } FC_END_TRY;
    return attributes;
}

static FcStmt* AttributesBeforeClass(FcParser* p) {
    int start = p->pos;
    FcList* attributes = fcTryAttributes(p, start, p->cleanupCount);
    FcClassDecl* decl;
    if (!attributes) return NULL;
    if (attributes->count == 0 || !(Check(p, TT_Class) || Check(p, TT_Actor) || IsRecordStart(p))) { p->pos = start; return NULL; }
    decl = (FcClassDecl*)(Check(p, TT_Class) ? ParseClassDecl(p) : IsRecordStart(p) ? ParseRecordDecl(p) : ParseActorDecl(p));
    decl = (FcClassDecl*)fcNodeDup(p->ctx, decl, sizeof *decl);
    decl->attributes = attributes;
    return (FcStmt*)decl;
}

static FcExpr* ParseBraceArray(FcParser* p);
static const char* TokensText(FcParser* p, int first, int end);

static FcList* ParseAttributes(FcParser* p) {
    FcList* attributes = L(p);
    while (Check(p, TT_LBracket) && PeekAt(p, 1)->type == TT_Identifier) {
        int line = Advance(p)->line;
        const char* name = Advance(p)->lexeme;
        FcList* args = L(p);
        FcList* texts = L(p);
        FcAttributeDecl* a;
        if (!Check(p, TT_RBracket)) {
            do {
                int first = p->pos;
                LA(p, args, Check(p, TT_LBrace) ? ParseBraceArray(p) : ParseExpression(p));
                LA(p, texts, TokensText(p, first, p->pos));
            } while (Match(p, TT_Comma));
        }
        Expect(p, TT_RBracket, S(p, "Expected ']' after the attribute '%s'", name));
        a = (FcAttributeDecl*)fcAlloc(p->ctx, sizeof *a);
        a->line = line; a->name = name; a->args = args; a->argTexts = texts;
        LA(p, attributes, a);
    }
    return attributes;
}

static bool BraceArrayToken(FcParser* p, int at) {
    int i;
    for (i = 0; i < p->braceArrayTokens->count; i++) if ((intptr_t)p->braceArrayTokens->items[i] == at) return true;
    return false;
}

static FcExpr* ParseBraceArray(FcParser* p) {
    int line;
    FcList* elements = L(p);
    LA(p, p->braceArrayTokens, (void*)(intptr_t)p->pos);
    line = Advance(p)->line;
    if (!Check(p, TT_RBrace))
        do { LA(p, elements, Check(p, TT_LBrace) ? ParseBraceArray(p) : ParseExpression(p)); } while (Match(p, TT_Comma));
    LA(p, p->braceArrayTokens, (void*)(intptr_t)p->pos);
    Expect(p, TT_RBrace, "Expected '}' after the array of an attribute argument");
    return (FcExpr*)FC_NEW(C_, FcArrayLiteralExpr, .h = {EK_ArrayLiteralExpr, line}, .elements = elements);
}

static FcTokenType TokenKind(FcParser* p, int at) {
    FcTokenType t = p->tokens[at].type;
    if (t == TT_LBrace && BraceArrayToken(p, at)) return TT_LBracket;
    if (t == TT_RBrace && BraceArrayToken(p, at)) return TT_RBracket;
    return t;
}

static void EscapeInto(FcBuf* sb, const char* text, char quote) {
    const unsigned char* q = (const unsigned char*)text;
    while (*q) {
        uint16_t u = fc8Next(&q);
        if (u == '\\') fcBufStr(sb, "\\\\");
        else if (u == '\n') fcBufStr(sb, "\\n");
        else if (u == '\t') fcBufStr(sb, "\\t");
        else if (u == '\r') fcBufStr(sb, "\\r");
        else if (u == (uint16_t)quote) { fcBufChar(sb, '\\'); fcBufChar(sb, quote); }
        else fcBufUnit(sb, u);
    }
}

static const char* TokensText(FcParser* p, int first, int end) {
    FcBuf sb = {0};
    int i;
    fcBufStr(&sb, "");
    for (i = first; i < end; i++) {
        FcToken* t = &p->tokens[i];
        if (sb.len > 0) {
            FcTokenType type = TokenKind(p, i), before = TokenKind(p, i - 1);
            bool callOrIndex = (type == TT_LParen || type == TT_LBracket) && (before == TT_Identifier || before == TT_RParen || before == TT_RBracket);
            bool minusSign = before == TT_Minus && (i - 1 == first || TokenKind(p, i - 2) == TT_Comma || TokenKind(p, i - 2) == TT_LParen
                || TokenKind(p, i - 2) == TT_LBracket || TokenKind(p, i - 2) == TT_Assign);
            if (!callOrIndex && type != TT_Comma && type != TT_RParen && type != TT_RBracket && type != TT_Dot && type != TT_Semicolon
                && before != TT_LParen && before != TT_LBracket && before != TT_Dot && before != TT_Bang && !minusSign)
                fcBufChar(&sb, ' ');
        }
        switch (t->type) {
            case TT_StringLiteral: fcBufChar(&sb, '"'); EscapeInto(&sb, t->lexeme, '"'); fcBufChar(&sb, '"'); break;
            case TT_CharLiteral: fcBufChar(&sb, '\''); EscapeInto(&sb, t->lexeme, '\''); fcBufChar(&sb, '\''); break;
            case TT_InterpolatedStringLiteral: fcBufFree(&sb); Error(p, "An attribute cannot take an interpolated string", t);
            case TT_LBrace: if (BraceArrayToken(p, i)) { fcBufChar(&sb, '['); break; } fcBufStr(&sb, t->lexeme); break;
            case TT_RBrace: if (BraceArrayToken(p, i)) { fcBufChar(&sb, ']'); break; } fcBufStr(&sb, t->lexeme); break;
            default: fcBufStr(&sb, t->lexeme); break;
        }
    }
    return fcBufDone(p->ctx, &sb);
}

static FcList* ParseClassMemberAfterAttributes(FcParser* p, int line);

static FcList* ParseClassMemberCore(FcParser* p) {
    FcList* attributes = ParseAttributes(p);
    int line = Peek(p)->line, i;
    FcList* result = ParseClassMemberAfterAttributes(p, line);
    FcList* withAttributes;
    bool allMembers = true, anyNotField = false;
    if (attributes->count == 0) return result;
    for (i = 0; i < result->count; i++) {
        FcStmtKind k = ((FcStmt*)result->items[i])->kind;
        if (k != SK_MethodDecl && k != SK_FieldDecl && k != SK_PropertyDecl) allMembers = false;
        if (k != SK_FieldDecl) anyNotField = true;
    }
    if (result->count == 0 || !allMembers || (result->count > 1 && anyNotField))
        Error(p, "Attributes ('[Name ...]') can only stand before a method, a field or a property", &p->tokens[p->pos - 1 > 0 ? p->pos - 1 : 0]);
    withAttributes = L(p);
    for (i = 0; i < result->count; i++) {
        FcStmt* m = (FcStmt*)result->items[i];
        switch (m->kind) {
            case SK_MethodDecl: { FcMethodDecl* x = (FcMethodDecl*)fcNodeDup(p->ctx, m, sizeof *x); x->attributes = attributes; m = (FcStmt*)x; break; }
            case SK_FieldDecl: { FcFieldDecl* x = (FcFieldDecl*)fcNodeDup(p->ctx, m, sizeof *x); x->attributes = attributes; m = (FcStmt*)x; break; }
            case SK_PropertyDecl: { FcPropertyDecl* x = (FcPropertyDecl*)fcNodeDup(p->ctx, m, sizeof *x); x->attributes = attributes; m = (FcStmt*)x; break; }
            default: break;
        }
        LA(p, withAttributes, m);
    }
    return withAttributes;
}

static FcStmt* WithOverride(FcParser* p, FcStmt* member, bool isOverride) {
    FcMethodDecl* m;
    if (!isOverride || member->kind != SK_MethodDecl) return member;
    m = (FcMethodDecl*)fcNodeDup(p->ctx, member, sizeof *m);
    m->isOverride = true;
    return (FcStmt*)m;
}

static FcStmt* ParseConstructor(FcParser* p, FcAccess access);
static FcStmt* ParseDestructor(FcParser* p);
static FcStmt* ParseOperatorMember(FcParser* p, int line, FcTypeRef* returnType);
static FcList* TryParseSpecialization(FcParser* p, bool isStatic);
static const char* SpecializedName(FcParser* p, const char* name, FcList* specTypes);

static FcList* ParseClassMemberAfterAttributes(FcParser* p, int line) {
    FcAccess access;
    FcToken* overrideToken;
    bool isOverride, isStatic, isReadonly;
    FcTypeRef* type = NULL;
    const char* name;
    FcList* specTypes;
    FcList* methodTypeParamNames;
    FcList* arrayRanks;
    FcExpr* initializer = NULL;
    if (Check(p, TT_Override) && (PeekAt(p, 1)->type == TT_Public || PeekAt(p, 1)->type == TT_Private || PeekAt(p, 1)->type == TT_Protected))
        Error(p, S(p, "The access modifier comes first: '%s override ...'", PeekAt(p, 1)->lexeme), Peek(p));
    access = ParseOptionalAccessModifier(p);
    overrideToken = Check(p, TT_Override) ? Advance(p) : NULL;
    isOverride = overrideToken != NULL;
    if (Check(p, TT_Construct)) {
        if (isOverride) Error(p, "A constructor is not inherited - it cannot be 'override'", overrideToken);
        return L1(p, ParseConstructor(p, access));
    }
    if (Check(p, TT_Destruct)) {
        if (isOverride) Error(p, "A destructor does not replace the one of the base class (both run) - it cannot be 'override'", overrideToken);
        return L1(p, ParseDestructor(p));
    }
    if (Check(p, TT_Operator)) return L1(p, WithOverride(p, ParseOperatorMember(p, line, NULL), isOverride));
    if (TypeThenOperatorAhead(p)) {
        FcTypeRef* operatorType = ParseTypeRef(p, true);
        return L1(p, WithOverride(p, ParseOperatorMember(p, line, operatorType), isOverride));
    }
    isStatic = Match(p, TT_Static);
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "inline") && PeekAt(p, 1)->type == TT_Identifier && streq(PeekAt(p, 1)->lexeme, "c") && !PeekAt(p, 1)->newlineBefore
        && PeekAt(p, 2)->type != TT_Assign) {
        if (isOverride) Error(p, "An 'inline c' method is static - it cannot be 'override'", overrideToken);
        return L1(p, ParseInlineCMethod(p, access));
    }
    if (isOverride && isStatic) Error(p, "A static member belongs to its class and is not inherited - it cannot be 'override'", overrideToken);
    if (!isOverride && Check(p, TT_Override))
        Error(p, "'static' and 'override' do not go together: a static member belongs to its class and is not inherited", Peek(p));
    isReadonly = Match(p, TT_Readonly);
    if (isOverride && isReadonly) Error(p, "A field cannot be 'override' - only methods, properties and operators replace inherited ones", overrideToken);
    if (Check(p, TT_Var)) {
        Advance(p);
        type = newTypeRef(p, "var", FC_NULL_INT, 0);
        type->namespaces = CurrentNamespaces(p);
    } else if (NextLooksLikeTypeThenName(p)) type = ParseTypeRef(p, true);
    name = Expect(p, TT_Identifier, "Expected a field or method name")->lexeme;
    if ((specTypes = TryParseSpecialization(p, isStatic)) != NULL) {
        FcList* specParms;
        FcBlockStmt* specBody;
        if (isReadonly) Error(p, "'readonly' is only valid for fields, not for methods", Peek(p));
        if (!Check(p, TT_LParen)) Error(p, "A specialization '<...>' must be followed by a parameter list '(...)'", Peek(p));
        specParms = ParseParamList(p, true);
        specBody = ParseBlock(p);
        return L1(p, FC_NEW(C_, FcMethodDecl, .h = {SK_MethodDecl, p->sourceIndex, line}, .returnType = type, .name = SpecializedName(p, name, specTypes), .params = specParms,
            .body = specBody, .access = access, .isOverride = isOverride, .specialization = specTypes));
    }
    methodTypeParamNames = Check(p, TT_Lt) ? ParseOptionalTypeParamNames(p) : L(p);
    if (Check(p, TT_LParen)) {
        FcList* parms;
        FcList* methodTypeParams;
        FcBlockStmt* body;
        if (isReadonly) Error(p, "'readonly' is only valid for fields, not for methods", Peek(p));
        parms = ParseParamList(p, true);
        methodTypeParams = ParseWhereClauses(p, methodTypeParamNames, line);
        body = ParseBlock(p);
        return L1(p, FC_NEW(C_, FcMethodDecl, .h = {SK_MethodDecl, p->sourceIndex, line}, .returnType = type, .name = name, .params = parms, .body = body,
            .typeParams = methodTypeParamNames->count > 0 ? methodTypeParams : NULL, .access = access, .isStatic = isStatic, .isOverride = isOverride));
    }
    if (methodTypeParamNames->count > 0) Error(p, "A generic type parameter head '<...>' must be followed by a parameter list '(...)'", Peek(p));
    if (type && Check(p, TT_Colon) && PeekAt(p, 1)->type == TT_Identifier
        && (PeekAt(p, 1 + CombinedUnitLengthAt(p, 1))->type == TT_LBrace
            || (streq(PeekAt(p, 1)->lexeme, "any") && PeekAt(p, 2)->type == TT_Identifier && PeekAt(p, 2 + CombinedUnitLengthAt(p, 2))->type == TT_LBrace))) {
        Advance(p);
        type = copyTypeRef(p, type);
        type->unit = ParseUnitName(p);
    }
    if (Check(p, TT_LBrace)) {
        FcList* property;
        int i;
        if (isReadonly) Error(p, "'readonly' is not valid for properties - a property without 'set' is already read-only", Peek(p));
        property = ParsePropertyBody(p, line, type, name, access, isStatic);
        if (isOverride)
            for (i = 0; i < property->count; i++)
                if (((FcStmt*)property->items[i])->kind == SK_PropertyDecl) {
                    FcPropertyDecl* pd = (FcPropertyDecl*)fcNodeDup(p->ctx, property->items[i], sizeof(FcPropertyDecl));
                    pd->isOverride = true;
                    property->items[i] = pd;
                }
        return property;
    }
    if (isOverride) Error(p, "A field cannot be 'override' - only methods, properties and operators replace inherited ones", overrideToken);
    if (type && type->arrayRank > 0)
        Error(p, "An array field is written with the brackets after the name ('int values[]'); 'int[]' as a type only exists as the return type of a method or property", Previous(p));
    arrayRanks = ParseArrayRanks(p);
    if (Match(p, TT_Colon)) {
        const char* unitName = ParseUnitName(p);
        if (type) { type = copyTypeRef(p, type); type->unit = unitName; }
        else { type = newTypeRef(p, "var", FC_NULL_INT, 0); type->namespaces = CurrentNamespaces(p); type->unit = unitName; }
    }
    if (Match(p, TT_Assign)) initializer = ParseExpression(p);
    ExpectStatementTerminator(p);
    return L1(p, FC_NEW(C_, FcFieldDecl, .h = {SK_FieldDecl, p->sourceIndex, line}, .type = type, .arrayRanks = arrayRanks, .name = name, .initializer = initializer,
        .isReadonly = isReadonly, .access = access, .isStatic = isStatic));
}

static FcList* TryParseSpecialization(FcParser* p, bool isStatic) {
    bool anyFirst = PeekAt(p, 1)->type == TT_Identifier && streq(PeekAt(p, 1)->lexeme, "any") && (PeekAt(p, 2)->type == TT_Comma || PeekAt(p, 2)->type == TT_Gt);
    FcToken* ltToken;
    FcList* specTypes = L(p);
    bool allAny = true;
    int i;
    if (!Check(p, TT_Lt) || (!anyFirst && (!IsTypeKeyword(PeekAt(p, 1)->type) || PeekAt(p, 1)->type == TT_Class || PeekAt(p, 1)->type == TT_Undefined))) return NULL;
    ltToken = Advance(p);
    do {
        FcTypeRef* specType = ParseTypeRef(p, false);
        const char* b = specType->baseName;
        if (specType->bitWidth != FC_NULL_INT || specType->unit || specType->nonNull
            || !(streq(b, "int") || streq(b, "float") || streq(b, "bool") || streq(b, "char") || streq(b, "string") || streq(b, "byte") || streq(b, "word") || streq(b, "any")))
            Error(p, "A specialization names plain types (int, float, bool, char, string, byte, word) or 'any' - no width, unit or 'defined'", ltToken);
        LA(p, specTypes, specType);
    } while (Match(p, TT_Comma));
    Expect(p, TT_Gt, "Expected '>' after the types of a specialization");
    for (i = 0; i < specTypes->count; i++) if (!streq(((FcTypeRef*)specTypes->items[i])->baseName, "any")) allAny = false;
    if (allAny) Error(p, "A specialization fixes at least one type argument ('any' leaves one open)", ltToken);
    if (isStatic) Error(p, "A specialization is chosen by the type arguments of the object - a static method has none", ltToken);
    return specTypes;
}

static const char* SpecializedName(FcParser* p, const char* name, FcList* specTypes) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, name);
    fcBufChar(&b, '<');
    for (i = 0; i < specTypes->count; i++) { if (i) fcBufChar(&b, ','); fcBufStr(&b, ((FcTypeRef*)specTypes->items[i])->baseName); }
    fcBufChar(&b, '>');
    return fcBufDone(p->ctx, &b);
}

static const char* ParseOperatorSymbol(FcParser* p);

static FcStmt* ParseOperatorMember(FcParser* p, int line, FcTypeRef* returnType) {
    const char* symbol;
    FcList* specTypes;
    FcList* parms;
    const char* internalName;
    FcBlockStmt* body;
    Expect(p, TT_Operator, "Expected 'operator'");
    symbol = ParseOperatorSymbol(p);
    specTypes = TryParseSpecialization(p, false);
    parms = ParseParamList(p, false);
    if (streq(symbol, "[]")) {
        if (parms->count == 1) internalName = "GetIndex";
        else if (parms->count == 2) internalName = "SetIndex";
        else Error(p, "'operator[]' needs either 1 parameter (read access: index) or 2 parameters (write access: index, value)", Peek(p));
    } else internalName = S(p, "operator%s", symbol);
    if (!streq(symbol, "[]") && parms->count != 1)
        Error(p, S(p, "'operator%s' needs exactly 1 parameter (the right operand - 'this' is the left one)", symbol), Peek(p));
    body = ParseBlock(p);
    if (specTypes)
        return (FcStmt*)FC_NEW(C_, FcMethodDecl, .h = {SK_MethodDecl, p->sourceIndex, line}, .returnType = returnType, .name = SpecializedName(p, internalName, specTypes),
            .params = parms, .body = body, .specialization = specTypes);
    return (FcStmt*)FC_NEW(C_, FcMethodDecl, .h = {SK_MethodDecl, p->sourceIndex, line}, .returnType = returnType, .name = internalName, .params = parms, .body = body);
}

static const char* ParseOperatorSymbol(FcParser* p) {
    if (Match(p, TT_LBracket)) { Expect(p, TT_RBracket, "Expected ']' after 'operator['"); return "[]"; }
    if (Check(p, TT_Lt) && PeekAt(p, 1)->type == TT_Lt) { Advance(p); Advance(p); return "<<"; }
    if (Check(p, TT_Gt) && PeekAt(p, 1)->type == TT_Gt && PeekAt(p, 2)->type == TT_Gt) { Advance(p); Advance(p); Advance(p); return ">>>"; }
    if (Check(p, TT_Gt) && PeekAt(p, 1)->type == TT_Gt) { Advance(p); Advance(p); return ">>"; }
    if (Match(p, TT_Plus)) return "+";
    if (Match(p, TT_Minus)) return "-";
    if (Match(p, TT_Star)) return "*";
    if (Match(p, TT_Slash)) return "/";
    if (Match(p, TT_Percent)) return "%";
    if (Match(p, TT_Caret)) return "^";
    if (Match(p, TT_Amp)) return "&";
    if (Match(p, TT_Pipe)) return "|";
    if (Match(p, TT_Hash)) return "#";
    if (Match(p, TT_Eq)) return "==";
    if (Match(p, TT_NotEq)) return "!=";
    if (Match(p, TT_LtEq)) return "<=";
    if (Match(p, TT_GtEq)) return ">=";
    if (Match(p, TT_Lt)) return "<";
    if (Match(p, TT_Gt)) return ">";
    Error(p, "Expected an overloadable operator after 'operator' ('[]', '+', '-', '*', '/', '%', '^', '&', '|', '#', '<<', '>>', '==', '!=', '<', '<=', '>', '>=')", Peek(p));
}

static FcStmt* ParseConstructor(FcParser* p, FcAccess access) {
    int line = Peek(p)->line;
    FcList* parms;
    FcList* baseArgs = NULL;
    Expect(p, TT_Construct, "Expected 'construct'");
    parms = ParseParamList(p, true);
    if (Match(p, TT_Colon)) {
        Expect(p, TT_Base, "Expected 'base' after ':' in the constructor");
        baseArgs = ParseArgList(p, NULL, false);
    }
    return (FcStmt*)FC_NEW(C_, FcConstructorDecl, .h = {SK_ConstructorDecl, p->sourceIndex, line}, .params = parms, .baseArgs = baseArgs, .body = ParseBlock(p), .access = access);
}

static FcStmt* ParseDestructor(FcParser* p) {
    int line = Peek(p)->line;
    Expect(p, TT_Destruct, "Expected 'destruct'");
    Expect(p, TT_LParen, "Expected '(' after 'destruct'");
    Expect(p, TT_RParen, "'destruct' takes no parameters");
    return (FcStmt*)FC_NEW(C_, FcDestructorDecl, .h = {SK_DestructorDecl, p->sourceIndex, line}, .body = ParseBlock(p));
}

static FcLambdaParam* ParseOneParam(FcParser* p, bool allowRef) {
    bool byRef = false;
    FcTypeRef* type = NULL;
    const char* pname;
    FcList* arrayRanks;
    FcExpr* defaultValue = NULL;
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "ref")) {
        FcTokenType t = PeekAt(p, 1)->type;
        if (!(t == TT_Comma || t == TT_RParen || t == TT_Assign || t == TT_Colon || t == TT_LBracket)) {
            if (!allowRef) Error(p, "'ref' parameters are only possible in methods and constructors", Peek(p));
            Advance(p);
            byRef = true;
        }
    }
    if (Check(p, TT_Var)) {
        Advance(p);
        type = newTypeRef(p, "var", FC_NULL_INT, 0);
        type->namespaces = CurrentNamespaces(p);
    } else if (NextLooksLikeTypeThenName(p)) type = ParseTypeRef(p, false);
    pname = Expect(p, TT_Identifier, "Expected a parameter name")->lexeme;
    arrayRanks = ParseArrayRanks(p);
    if (Match(p, TT_Colon)) {
        const char* unitName = ParseUnitName(p);
        if (type) { type = copyTypeRef(p, type); type->unit = unitName; }
        else { type = newTypeRef(p, "var", FC_NULL_INT, 0); type->namespaces = CurrentNamespaces(p); type->unit = unitName; }
    }
    if (Match(p, TT_Assign)) {
        if (byRef) Error(p, "A 'ref' parameter cannot have a default value", Previous(p));
        defaultValue = ParseExpression(p);
    }
    return newParam(p, pname, type, arrayRanks, defaultValue, byRef);
}

static FcList* ParseParamList(FcParser* p, bool allowRef) {
    FcList* parms = L(p);
    Expect(p, TT_LParen, "Expected '('");
    if (!Check(p, TT_RParen)) {
        do { LA(p, parms, ParseOneParam(p, allowRef)); } while (Match(p, TT_Comma));
    }
    Expect(p, TT_RParen, "Expected ')' after the parameter list");
    return parms;
}

static bool NoExpressionStart(FcTokenType t) {
    return t == TT_Plus || t == TT_Star || t == TT_Slash || t == TT_Percent || t == TT_Caret || t == TT_Pipe || t == TT_Hash
        || t == TT_And || t == TT_Or || t == TT_Eq || t == TT_NotEq || t == TT_Lt || t == TT_LtEq || t == TT_Gt || t == TT_GtEq
        || t == TT_Assign || t == TT_Question || t == TT_Dot || t == TT_DotDot || t == TT_Arrow || t == TT_Is
        || t == TT_RBracket || t == TT_RBrace || t == TT_Semicolon;
}

static bool IsNamedArgumentStart(FcParser* p) {
    FcToken* colon;
    FcToken* after;
    bool adjacent;
    if (!Check(p, TT_Identifier) || PeekAt(p, 1)->type != TT_Colon || PeekAt(p, 1)->line != Peek(p)->line) return false;
    colon = PeekAt(p, 1);
    after = PeekAt(p, 2);
    if (after->type == TT_Colon || after->type == TT_RParen || after->type == TT_Comma || after->type == TT_Eof) return false;
    if (NoExpressionStart(after->type)) return false;
    if (after->type == TT_Bang) {
        FcToken* negated = PeekAt(p, 3);
        if (negated->type == TT_RParen || negated->type == TT_Comma || negated->type == TT_Eof || negated->type == TT_Colon || negated->type == TT_Minus
            || NoExpressionStart(negated->type) || IsTypeKeyword(negated->type)) return false;
    }
    adjacent = after->line == colon->line && after->column == colon->column + 1 && !after->newlineBefore;
    return !(adjacent && (after->type == TT_Identifier || IsCustomUnitWordAt(p, 2)));
}

static FcList* ParseArgList(FcParser* p, FcList** namesOut, bool allowNames) {
    FcList* args = L(p);
    FcList* names = NULL;
    int i;
    Expect(p, TT_LParen, "Expected '('");
    if (!Check(p, TT_RParen)) {
        do {
            if (IsNamedArgumentStart(p)) {
                FcToken* nameTok = Advance(p);
                if (!allowNames) Error(p, "Named arguments are not possible here", nameTok);
                Advance(p);
                if (!names) { names = L(p); for (i = 0; i < args->count; i++) LA(p, names, NULL); }
                for (i = 0; i < names->count; i++)
                    if (names->items[i] && streq((const char*)names->items[i], nameTok->lexeme)) Error(p, S(p, "The argument '%s' is named twice", nameTok->lexeme), nameTok);
                LA(p, names, nameTok->lexeme);
                LA(p, args, ParseExpression(p));
                continue;
            }
            if (names) Error(p, "A positional argument cannot follow a named one", Peek(p));
            LA(p, args, ParseExpression(p));
        } while (Match(p, TT_Comma));
    }
    Expect(p, TT_RParen, "Expected ')' after the argument list");
    if (namesOut) *namesOut = names;
    return args;
}

/* ---- expressions (precedence from low to high) ---- */
static FcExpr* ParseAssignment(FcParser* p);
static FcExpr* ParseExpression(FcParser* p) { return ParseAssignment(p); }

static FcBinaryOp CompoundOperator(FcTokenType t) {
    switch (t) {
        case TT_PlusAssign: return BOP_Add;
        case TT_MinusAssign: return BOP_Sub;
        case TT_StarAssign: return BOP_Mul;
        case TT_SlashAssign: return BOP_Div;
        case TT_PercentAssign: return BOP_Mod;
        case TT_CaretAssign: return BOP_Power;
        case TT_AmpAssign: return BOP_BitAnd;
        case TT_PipeAssign: return BOP_BitOr;
        case TT_HashAssign: return BOP_BitXor;
        case TT_ShlAssign: return BOP_ShiftLeft;
        case TT_ShrAssign: return BOP_ShiftRight;
        case TT_UShrAssign: return BOP_ShiftRightLogical;
        default: return BOP__NONE;
    }
}

static bool IsPlainTarget(FcExpr* e) {
    switch (e->kind) {
        case EK_IdentifierExpr: case EK_ThisExpr: case EK_BaseExpr: case EK_LiteralExpr: return true;
        case EK_MemberExpr: return IsPlainTarget(((FcMemberExpr*)e)->target);
        case EK_IndexExpr: return IsPlainTarget(((FcIndexExpr*)e)->target) && IsPlainTarget(((FcIndexExpr*)e)->index);
        case EK_BinaryExpr: return IsPlainTarget(((FcBinaryExpr*)e)->left) && IsPlainTarget(((FcBinaryExpr*)e)->right);
        case EK_UnaryExpr: return IsPlainTarget(((FcUnaryExpr*)e)->operand);
        default: return false;
    }
}

static bool IsAssignTarget(FcExpr* e) {
    return e->kind == EK_IdentifierExpr || e->kind == EK_MemberExpr || e->kind == EK_IndexExpr || (e->kind == EK_UnaryExpr && ((FcUnaryExpr*)e)->op == UOP_Dereference);
}

static FcExpr* ParseAssignment(FcParser* p) {
    FcExpr* left = ParseConditional(p);
    FcBinaryOp op;
    if (LineContinues(p) && Check(p, TT_Assign)) {
        int line;
        FcExpr* value;
        Advance(p);
        line = Previous(p)->line;
        value = ParseAssignment(p);
        if (IsAssignTarget(left)) return mkAssign(p, line, left, value);
        Error(p, "Invalid assignment target", Previous(p));
    }
    if (LineContinues(p) && (op = CompoundOperator(Peek(p)->type)) != BOP__NONE) {
        FcToken* opTok = Advance(p);
        FcExpr* value = ParseAssignment(p);
        if (!IsAssignTarget(left)) Error(p, "Invalid assignment target", opTok);
        if (!IsPlainTarget(left))
            Error(p, S(p, "The target of '%s' must not contain a call, '++', '--' or an assignment (write the assignment out)", opTok->lexeme), opTok);
        return mkAssign(p, opTok->line, left, mkBinary(p, opTok->line, op, left, value));
    }
    return left;
}

static FcExpr* ParseOr(FcParser* p);

static FcExpr* ParseConditional(FcParser* p) {
    FcExpr* condition = ParseOr(p);
    int line, savedSuppress;
    FcExpr* then;
    FcExpr* otherwise;
    if (!(LineContinues(p) && Check(p, TT_Question))) return condition;
    line = Advance(p)->line;
    savedSuppress = p->suppressColonPostfixAtDepth;
    p->suppressColonPostfixAtDepth = p->bracketDepth;
    pPushCleanup(p, CL_SUPPRESS_COLON, savedSuppress, false, NULL);
    then = ParseConditional(p);
    pPopCleanup(p);
    p->suppressColonPostfixAtDepth = savedSuppress;
    Expect(p, TT_Colon, "Expected ':' in 'condition ? a : b'");
    otherwise = ParseConditional(p);
    return (FcExpr*)FC_NEW(C_, FcConditionalExpr, .h = {EK_ConditionalExpr, line}, .condition = condition, .then = then, .otherwise = otherwise);
}

static FcExpr* ParseAnd(FcParser* p);
static FcExpr* ParseBitwiseOr(FcParser* p);
static FcExpr* ParseBitwiseXor(FcParser* p);
static FcExpr* ParseBitwiseAnd(FcParser* p);
static FcExpr* ParseEquality(FcParser* p);
static FcExpr* ParseRelationalOrIs(FcParser* p);
static FcExpr* ParseRange(FcParser* p);
static FcExpr* ParseAdditive(FcParser* p);
static FcExpr* ParseMultiplicative(FcParser* p);
static FcExpr* ParsePower(FcParser* p);

static FcExpr* ParseOr(FcParser* p) {
    FcExpr* left = ParseAnd(p);
    while (LineContinues(p) && Check(p, TT_Or)) { int line = Advance(p)->line; FcExpr* r = ParseAnd(p); left = mkBinary(p, line, BOP_Or, left, r); }
    return left;
}
static FcExpr* ParseAnd(FcParser* p) {
    FcExpr* left = ParseBitwiseOr(p);
    while (LineContinues(p) && Check(p, TT_And)) { int line = Advance(p)->line; FcExpr* r = ParseBitwiseOr(p); left = mkBinary(p, line, BOP_And, left, r); }
    return left;
}
static FcExpr* ParseBitwiseOr(FcParser* p) {
    FcExpr* left = ParseBitwiseXor(p);
    while (LineContinues(p) && Check(p, TT_Pipe)) { int line = Advance(p)->line; FcExpr* r = ParseBitwiseXor(p); left = mkBinary(p, line, BOP_BitOr, left, r); }
    return left;
}
static FcExpr* ParseBitwiseXor(FcParser* p) {
    FcExpr* left = ParseBitwiseAnd(p);
    while (LineContinues(p) && Check(p, TT_Hash)) { int line = Advance(p)->line; FcExpr* r = ParseBitwiseAnd(p); left = mkBinary(p, line, BOP_BitXor, left, r); }
    return left;
}
static FcExpr* ParseBitwiseAnd(FcParser* p) {
    FcExpr* left = ParseEquality(p);
    while (LineContinues(p) && Check(p, TT_Amp)) { int line = Advance(p)->line; FcExpr* r = ParseEquality(p); left = mkBinary(p, line, BOP_BitAnd, left, r); }
    return left;
}
static FcExpr* ParseEquality(FcParser* p) {
    FcExpr* left = ParseRelationalOrIs(p);
    while (LineContinues(p) && (Check(p, TT_Eq) || Check(p, TT_NotEq))) {
        FcToken* opTok = Advance(p);
        FcBinaryOp op = opTok->type == TT_Eq ? BOP_Eq : BOP_NotEq;
        FcExpr* r = ParseRelationalOrIs(p);
        left = mkBinary(p, opTok->line, op, left, r);
    }
    return left;
}
static FcExpr* ParseRange(FcParser* p) {
    FcExpr* left = ParseShift(p);
    if (LineContinues(p) && Check(p, TT_DotDot)) {
        int line = Advance(p)->line;
        FcExpr* to = ParseShift(p);
        left = (FcExpr*)FC_NEW(C_, FcRangeExpr, .h = {EK_RangeExpr, line}, .from = left, .to = to);
    }
    return left;
}

static FcExpr* ParseRelationalOrIs(FcParser* p) {
    FcExpr* left = ParseRange(p);
    while (LineContinues(p)) {
        if (Check(p, TT_Lt) || Check(p, TT_LtEq) || Check(p, TT_Gt) || Check(p, TT_GtEq)) {
            FcToken* opTok = Advance(p);
            FcBinaryOp op = opTok->type == TT_Lt ? BOP_Lt : opTok->type == TT_LtEq ? BOP_LtEq : opTok->type == TT_Gt ? BOP_Gt : BOP_GtEq;
            FcExpr* r = ParseRange(p);
            left = mkBinary(p, opTok->line, op, left, r);
        } else if (Check(p, TT_Is)) {
            int line = Advance(p)->line;
            if (Match(p, TT_In)) {
                const char* unitName = IsCustomUnitWordAt(p, 0) ? Advance(p)->lexeme : Expect(p, TT_Identifier, "Expected a unit name after 'is in'")->lexeme;
                left = (FcExpr*)FC_NEW(C_, FcIsInExpr, .h = {EK_IsInExpr, line}, .operand = left, .unitName = unitName);
            } else if (Match(p, TT_Of)) {
                const char* typeName = ParseTypeAnnotationName(p);
                FcTypeRef* t = newTypeRef(p, typeName, FC_NULL_INT, 0);
                t->namespaces = CurrentNamespaces(p);
                left = (FcExpr*)FC_NEW(C_, FcIsOfExpr, .h = {EK_IsOfExpr, line}, .operand = left, .typeRef = t);
            } else if (Match(p, TT_From)) {
                FcTokenType t1 = PeekAt(p, 1)->type;
                if (Check(p, TT_Identifier) && (streq(Peek(p)->lexeme, "local") || streq(Peek(p)->lexeme, "global")) && !(t1 == TT_Dot || t1 == TT_LParen || t1 == TT_LBracket)) {
                    const char* place = Advance(p)->lexeme;
                    left = (FcExpr*)FC_NEW(C_, FcIsFromExpr, .h = {EK_IsFromExpr, line}, .operand = left, .ownerExpr = mkUndefined(p, line), .place = place);
                } else {
                    FcExpr* owner = ParseShift(p);
                    left = (FcExpr*)FC_NEW(C_, FcIsFromExpr, .h = {EK_IsFromExpr, line}, .operand = left, .ownerExpr = owner);
                }
            } else if (Match(p, TT_Under)) {
                FcExpr* owner = ParseShift(p);
                left = (FcExpr*)FC_NEW(C_, FcIsFromExpr, .h = {EK_IsFromExpr, line}, .operand = left, .ownerExpr = owner, .transitive = true);
            } else Error(p, "Expected 'in', 'of', 'from' or 'under' after 'is'", Peek(p));
        } else break;
    }
    return left;
}

static FcExpr* ParseShift(FcParser* p) {
    FcExpr* left = ParseAdditive(p);
    while (LineContinues(p)) {
        if (Check(p, TT_Lt) && PeekAt(p, 1)->type == TT_Lt) {
            int line = Peek(p)->line;
            FcExpr* r;
            Advance(p); Advance(p);
            r = ParseAdditive(p);
            left = mkBinary(p, line, BOP_ShiftLeft, left, r);
        } else if (Check(p, TT_Gt) && PeekAt(p, 1)->type == TT_Gt && PeekAt(p, 2)->type == TT_Gt) {
            int line = Peek(p)->line;
            FcExpr* r;
            Advance(p); Advance(p); Advance(p);
            r = ParseAdditive(p);
            left = mkBinary(p, line, BOP_ShiftRightLogical, left, r);
        } else if (Check(p, TT_Gt) && PeekAt(p, 1)->type == TT_Gt) {
            int line = Peek(p)->line;
            FcExpr* r;
            Advance(p); Advance(p);
            r = ParseAdditive(p);
            left = mkBinary(p, line, BOP_ShiftRight, left, r);
        } else break;
    }
    return left;
}

static FcExpr* ParseAdditive(FcParser* p) {
    FcExpr* left = ParseMultiplicative(p);
    while (LineContinues(p) && (Check(p, TT_Plus) || Check(p, TT_Minus))) {
        FcToken* opTok = Advance(p);
        FcBinaryOp op = opTok->type == TT_Plus ? BOP_Add : BOP_Sub;
        FcExpr* r = ParseMultiplicative(p);
        left = mkBinary(p, opTok->line, op, left, r);
    }
    return left;
}

static FcExpr* ParseMultiplicative(FcParser* p) {
    FcExpr* left = ParseUnary(p);
    while (LineContinues(p) && (Check(p, TT_Star) || Check(p, TT_Slash) || Check(p, TT_Percent))) {
        FcToken* opTok = Advance(p);
        FcBinaryOp op = opTok->type == TT_Star ? BOP_Mul : opTok->type == TT_Slash ? BOP_Div : BOP_Mod;
        FcExpr* r = ParseUnary(p);
        left = mkBinary(p, opTok->line, op, left, r);
    }
    return left;
}

static FcExpr* ParsePower(FcParser* p) {
    FcExpr* left = ParsePostfix(p);
    if (LineContinues(p) && Check(p, TT_Caret)) {
        int line = Advance(p)->line;
        FcExpr* right = ParseUnary(p);
        return mkBinary(p, line, BOP_Power, left, right);
    }
    return left;
}

static FcExpr* ParseUnary(FcParser* p) {
    if (Check(p, TT_PlusPlus) || Check(p, TT_MinusMinus)) {
        FcToken* opTok = Advance(p);
        bool isIncrement = opTok->type == TT_PlusPlus;
        FcExpr* target = ParsePostfix(p);
        return (FcExpr*)FC_NEW(C_, FcIncDecExpr, .h = {EK_IncDecExpr, opTok->line}, .target = target, .isIncrement = isIncrement, .isPrefix = true);
    }
    if ((Check(p, TT_Taking) || Check(p, TT_Handover)) && p->captureUses->count > 0 && PeekAt(p, 1)->type == TT_Identifier) {
        FcToken* modeTok = Advance(p);
        FcToken* nameTok = Advance(p);
        NoteCaptureUse(p, nameTok->lexeme, modeTok->type == TT_Handover, modeTok);
        return mkIdent(p, nameTok->line, nameTok->lexeme);
    }
    if (Check(p, TT_Flat) || Check(p, TT_Copy)) {
        FcToken* copyTok = Advance(p);
        FcUnaryOp copyOp = copyTok->type == TT_Flat ? UOP_FlatCopy : UOP_DeepCopy;
        FcExpr* copied = ParseUnary(p);
        if (copied->kind == EK_WithExpr && ((FcWithExpr*)copied)->copy == UOP__NONE) {
            FcWithExpr* w = (FcWithExpr*)fcNodeDup(p->ctx, copied, sizeof(FcWithExpr));
            w->h.line = copyTok->line;
            w->copy = copyOp;
            return (FcExpr*)w;
        }
        return mkUnary(p, copyTok->line, copyOp, copied);
    }
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "trust") && !PeekAt(p, 1)->newlineBefore) {
        FcTokenType t = PeekAt(p, 1)->type;
        if (t == TT_Identifier || t == TT_This || t == TT_IntLiteral || t == TT_FloatLiteral || t == TT_True || t == TT_False || t == TT_CharLiteral) {
            FcToken* trustTok = Advance(p);
            FcExpr* e = ParseUnary(p);
            return mkUnary(p, trustTok->line, UOP_Trust, e);
        }
    }
    if (IsAwaitStart(p)) return ParseAwait(p);
    if (Check(p, TT_Try) && PeekAt(p, 1)->type == TT_Take) {
        FcToken* tryTok = Advance(p);
        FcExpr* e;
        Advance(p);
        e = ParseUnary(p);
        return mkUnary(p, tryTok->line, UOP_TryTake, e);
    }
    if (Check(p, TT_Take)) {
        FcToken* takeTok = Advance(p);
        FcExpr* e = ParseUnary(p);
        return mkUnary(p, takeTok->line, UOP_Take, e);
    }
    if (Check(p, TT_Minus) || Check(p, TT_Bang) || Check(p, TT_Tilde) || Check(p, TT_Star) || Check(p, TT_Amp)) {
        FcToken* opTok = Advance(p);
        FcUnaryOp op = opTok->type == TT_Minus ? UOP_Negate : opTok->type == TT_Bang ? UOP_LogicalNot : opTok->type == TT_Tilde ? UOP_BitNot
            : opTok->type == TT_Star ? UOP_Dereference : UOP_AddressOf;
        FcExpr* e = ParseUnary(p);
        return mkUnary(p, opTok->line, op, e);
    }
    return ParsePower(p);
}

static FcExpr* ParsePostfix(FcParser* p) {
    FcExpr* expr = ParsePostfixRest(p, ParsePrimary(p));
    while (Check(p, TT_With) && PeekAt(p, 1)->type == TT_LBrace) {
        int line = Advance(p)->line;
        FcList* sets = L(p);
        Advance(p);
        while (!Check(p, TT_RBrace) && !Check(p, TT_Eof)) {
            FcToken* dot = Expect(p, TT_Dot, "Expected '.name = value' in 'with { ... }'");
            const char* name = Expect(p, TT_Identifier, "Expected the name of a field after '.' in 'with { ... }'")->lexeme;
            FcWithSet* s;
            Expect(p, TT_Assign, "Expected '=' after the name of the field in 'with { ... }'");
            s = (FcWithSet*)fcAlloc(p->ctx, sizeof *s);
            s->line = dot->line; s->name = name;
            s->value = ParseExpression(p);
            LA(p, sets, s);
            if (!Match(p, TT_Comma) && !Check(p, TT_RBrace) && !Peek(p)->newlineBefore) Error(p, "Expected ',' or '}' in 'with { ... }'", Peek(p));
        }
        Expect(p, TT_RBrace, "Expected '}' at the end of 'with { ... }'");
        expr = (FcExpr*)FC_NEW(C_, FcWithExpr, .h = {EK_WithExpr, line}, .source = expr, .copy = UOP__NONE, .sets = sets);
    }
    return expr;
}

static FcExpr* ParsePostfixRest(FcParser* p, FcExpr* expr) {
    while (LineContinues(p)) {
        int typeArgsEnd;
        if (Check(p, TT_QuestionDot) || Check(p, TT_QuestionQuestionDot)) {
            FcToken* qTok = Advance(p);
            const char* member = Expect(p, TT_Identifier, S(p, "Expected a name after '%s'", qTok->lexeme))->lexeme;
            FcExpr* placeholder = (FcExpr*)FC_NEW(C_, FcConditionalTargetExpr, .h = {EK_ConditionalTargetExpr, qTok->line});
            FcExpr* inner = ParsePostfixRest(p, mkMember(p, qTok->line, placeholder, member));
            if (inner->kind == EK_IncDecExpr) Error(p, S(p, "'%s' cannot be used with '++'/'--' (write an if)", qTok->lexeme), qTok);
            return (FcExpr*)FC_NEW(C_, FcConditionalAccessExpr, .h = {EK_ConditionalAccessExpr, qTok->line}, .target = expr, .inner = inner, .placeholder = placeholder,
                .checkMember = qTok->type == TT_QuestionQuestionDot, .member = member);
        }
        if (Check(p, TT_Dot)) {
            int line = Advance(p)->line;
            const char* name = Expect(p, TT_Identifier, "Expected a name after '.'")->lexeme;
            expr = mkMember(p, line, expr, name);
        } else if (Check(p, TT_LParen)) {
            int line = Peek(p)->line;
            FcList* argNames;
            FcList* args = ParseArgList(p, &argNames, true);
            expr = mkCall(p, line, expr, args, NULL, argNames);
        } else if (Check(p, TT_Lt) && (expr->kind == EK_IdentifierExpr || expr->kind == EK_MemberExpr) && (typeArgsEnd = TypeArgListEnd(p, 0)) > 0
                   && PeekAt(p, typeArgsEnd)->type == TT_LParen && PeekAt(p, typeArgsEnd)->line == Peek(p)->line) {
            int line = Peek(p)->line;
            FcList* typeArgs = ParseTypeArgRefs(p);
            FcList* argNames;
            FcList* args = ParseArgList(p, &argNames, true);
            expr = mkCall(p, line, expr, args, typeArgs, argNames);
        } else if (Check(p, TT_LBracket)) {
            int line = Advance(p)->line;
            FcExpr* index = ParseExpression(p);
            Expect(p, TT_RBracket, "Expected ']' after the index");
            expr = (FcExpr*)FC_NEW(C_, FcIndexExpr, .h = {EK_IndexExpr, line}, .target = expr, .index = index);
        } else if (Check(p, TT_Colon) && PeekAt(p, 1)->type == TT_Colon && PeekAt(p, 1)->line == Peek(p)->line && p->suppressColonPostfixAtDepth != p->bracketDepth) {
            int line = Advance(p)->line;
            const char* relabelUnit;
            Advance(p);
            relabelUnit = LineContinues(p) && (Check(p, TT_Identifier) || IsCustomUnitWordAt(p, 0)) ? Advance(p)->lexeme : NULL;
            expr = (FcExpr*)FC_NEW(C_, FcUnitCoerceExpr, .h = {EK_UnitCoerceExpr, line}, .operand = expr, .targetUnitName = relabelUnit, .relabel = true);
        } else if (Check(p, TT_Colon) && p->suppressColonPostfixAtDepth != p->bracketDepth) {
            int line = Advance(p)->line;
            const char* unitName = LineContinues(p) && (Check(p, TT_Identifier) || IsCustomUnitWordAt(p, 0)) ? Advance(p)->lexeme : NULL;
            expr = (FcExpr*)FC_NEW(C_, FcUnitCoerceExpr, .h = {EK_UnitCoerceExpr, line}, .operand = expr, .targetUnitName = unitName);
        } else if (Check(p, TT_On)) {
            int line = Advance(p)->line;
            FcExpr* target = ParsePostfix(p);
            expr = (FcExpr*)FC_NEW(C_, FcRebindExpr, .h = {EK_RebindExpr, line}, .lambda = expr, .target = target);
        } else if (Check(p, TT_Bang)) {
            int line = Advance(p)->line;
            int typeKw = -1;
            if (LineContinues(p) && Check(p, TT_Identifier)) {
                const char* className = Advance(p)->lexeme;
                FcTypeRef* t;
                while (Check(p, TT_Dot) && PeekAt(p, 1)->type == TT_Identifier && IsNamespaceName(p, className)) {
                    Advance(p);
                    className = S(p, "%s.%s", className, Advance(p)->lexeme);
                }
                t = newTypeRef(p, className, FC_NULL_INT, 0);
                t->namespaces = CurrentNamespaces(p);
                expr = (FcExpr*)FC_NEW(C_, FcTypeCoerceExpr, .h = {EK_TypeCoerceExpr, line}, .operand = expr, .targetTypeKeyword = -1, .targetClass = t);
                continue;
            }
            if (LineContinues(p) && IsTypeKeyword(Peek(p)->type)) typeKw = (int)Advance(p)->type;
            if (typeKw == TT_KwByte) {
                expr = (FcExpr*)FC_NEW(C_, FcTypeCoerceExpr, .h = {EK_TypeCoerceExpr, line}, .operand = expr, .targetTypeKeyword = TT_KwInt, .width = 8, .isUnsigned = true);
                continue;
            }
            if (typeKw == TT_KwWord) {
                expr = (FcExpr*)FC_NEW(C_, FcTypeCoerceExpr, .h = {EK_TypeCoerceExpr, line}, .operand = expr, .targetTypeKeyword = TT_KwWord);
                continue;
            }
            if ((typeKw == TT_KwInt || typeKw == TT_KwFloat) && Check(p, TT_LBracket) && PeekAt(p, 1)->type == TT_IntLiteral && PeekAt(p, 2)->type == TT_RBracket) {
                FcToken* widthTok;
                int64_t w;
                Advance(p);
                widthTok = Advance(p);
                Advance(p);
                w = widthTok->intValue;
                if (w < INT32_MIN || w > INT32_MAX) fcThrow(p->ctx, FC_ERR_INTERNAL, widthTok->line, widthTok->column, NULL, "OverflowException: Value was either too large or too small for an Int32.");
                if (!(w == 8 || w == 16 || w == 32 || w == 64))
                    Error(p, typeKw == TT_KwInt ? "A bit width after '!int' is 8, 16, 32 or 64" : "A bit width after '!float' is 8, 16, 32 or 64", widthTok);
                expr = (FcExpr*)FC_NEW(C_, FcTypeCoerceExpr, .h = {EK_TypeCoerceExpr, line}, .operand = expr, .targetTypeKeyword = typeKw, .width = (int)w);
                continue;
            }
            expr = (FcExpr*)FC_NEW(C_, FcTypeCoerceExpr, .h = {EK_TypeCoerceExpr, line}, .operand = expr, .targetTypeKeyword = typeKw);
        } else break;
    }
    if (LineContinues(p) && (Check(p, TT_PlusPlus) || Check(p, TT_MinusMinus))) {
        FcToken* opTok = Advance(p);
        expr = (FcExpr*)FC_NEW(C_, FcIncDecExpr, .h = {EK_IncDecExpr, opTok->line}, .target = expr, .isIncrement = opTok->type == TT_PlusPlus, .isPrefix = false);
    }
    return expr;
}

static FcExpr* ParseSyncExpr(FcParser* p) {
    int line = Peek(p)->line;
    bool isTry = Match(p, TT_Try), isFlat;
    FcExpr* target;
    Expect(p, TT_Sync, "Expected 'sync'");
    if (Check(p, TT_Identifier) && streq(Peek(p)->lexeme, "globals")) {
        if (isTry) Error(p, "'try sync globals' does not exist", Peek(p));
        Advance(p);
        return (FcExpr*)FC_NEW(C_, FcSyncGlobalsExpr, .h = {EK_SyncGlobalsExpr, line});
    }
    isFlat = Match(p, TT_Flat);
    target = ParsePostfix(p);
    return (FcExpr*)FC_NEW(C_, FcSyncExpr, .h = {EK_SyncExpr, line}, .isTry = isTry, .isFlat = isFlat, .target = target);
}

static FcExpr* ParseInterpolatedString(FcParser* p, FcToken* tok);
static FcExpr* ParseLambda(FcParser* p);

static FcValue LiteralValue(FcParser* p, FcToken* tok) {
    FcValue v;
    memset(&v, 0, sizeof v);
    switch (tok->type) {
        case TT_IntLiteral: v.kind = VK_Int; v.i = tok->intValue; v.unit = fcLiteralUnitText(p->ctx, tok->unitSuffix); break;
        case TT_FloatLiteral: v.kind = VK_Float; v.f = tok->floatValue; v.unit = fcLiteralUnitText(p->ctx, tok->unitSuffix); break;
        case TT_StringLiteral: v.kind = VK_String; v.s = tok->stringValue; break;
        case TT_CharLiteral: v.kind = VK_Char; v.s = tok->stringValue; break;
        case TT_True: v.kind = VK_Bool; v.b = true; break;
        case TT_False: v.kind = VK_Bool; v.b = false; break;
        default: v.kind = VK_Undefined; break;
    }
    return v;
}

static FcExpr* ParsePrimary(FcParser* p) {
    bool noShortLambda = p->suppressShortLambda;
    FcToken* tok;
    p->suppressShortLambda = false;
    tok = Peek(p);
    if (tok->type == TT_KwInt || tok->type == TT_KwFloat || tok->type == TT_KwBool || tok->type == TT_KwChar || tok->type == TT_KwByte || tok->type == TT_KwWord) {
        int dot = PeekAt(p, 1)->type == TT_Dot ? 1
            : PeekAt(p, 1)->type == TT_LBracket && PeekAt(p, 2)->type == TT_IntLiteral && PeekAt(p, 3)->type == TT_RBracket && PeekAt(p, 4)->type == TT_Dot ? 4 : 0;
        if (dot > 0 && PeekAt(p, dot + 1)->type == TT_Identifier && streq(PeekAt(p, dot + 1)->lexeme, "Bits")) {
            int width = FC_NULL_INT;
            Advance(p);
            if (dot == 4) {
                FcToken* widthToken;
                const char* q;
                int64_t w = 0;
                Advance(p);
                widthToken = Advance(p);
                /* int.Parse(lexeme): digits only (a `_`, a prefix or a unit is a FormatException in C#) */
                for (q = widthToken->lexeme; *q; q++) {
                    if (*q < '0' || *q > '9' || w > 100000000) fcThrow(p->ctx, FC_ERR_INTERNAL, widthToken->line, widthToken->column, NULL, "FormatException: int.Parse of the width");
                    w = w * 10 + (*q - '0');
                }
                width = (int)w;
                if (!(tok->type == TT_KwInt || tok->type == TT_KwFloat) || !(width == 8 || width == 16 || width == 32 || width == 64) || (tok->type == TT_KwFloat && width == 8))
                    Error(p, S(p, "'%s[%d]' is no type: a width in brackets is 8, 16, 32 or 64 for int and 16, 32 or 64 for float", tok->lexeme, width), widthToken);
                Advance(p);
            }
            Advance(p);
            Advance(p);
            return (FcExpr*)FC_NEW(C_, FcTypeBitsExpr, .h = {EK_TypeBitsExpr, tok->line}, .baseName = tok->lexeme, .width = width);
        }
    }
    switch (tok->type) {
        case TT_Sync: return ParseSyncExpr(p);
        case TT_Try: {
            FcExpr* innerCall;
            if (PeekAt(p, 1)->type == TT_Process) {
                FcExpr* procTarget;
                Advance(p); Advance(p);
                procTarget = ParsePostfix(p);
                return (FcExpr*)FC_NEW(C_, FcTryProcessExpr, .h = {EK_TryProcessExpr, tok->line}, .target = procTarget);
            }
            if (PeekAt(p, 1)->type == TT_Sync) return ParseSyncExpr(p);
            Advance(p);
            innerCall = ParsePostfix(p);
            if (innerCall->kind == EK_ConditionalAccessExpr && ((FcConditionalAccessExpr*)innerCall)->inner->kind == EK_CallExpr) {
                FcConditionalAccessExpr* ca = (FcConditionalAccessExpr*)fcNodeDup(p->ctx, innerCall, sizeof(FcConditionalAccessExpr));
                ca->inner = (FcExpr*)FC_NEW(C_, FcTryCallExpr, .h = {EK_TryCallExpr, tok->line}, .call = ca->inner);
                return (FcExpr*)ca;
            }
            if (innerCall->kind != EK_CallExpr) Error(p, "Expected 'sync', 'process' or a function call after 'try'", tok);
            return (FcExpr*)FC_NEW(C_, FcTryCallExpr, .h = {EK_TryCallExpr, tok->line}, .call = innerCall);
        }
        case TT_Dot: {
            const char* memberName;
            if (p->withVarStack->count == 0) Error(p, "'.' at the start of an expression is only valid inside a 'with' block", tok);
            Advance(p);
            memberName = Expect(p, TT_Identifier, "Expected a name after '.'")->lexeme;
            return mkMember(p, tok->line, mkIdent(p, tok->line, (const char*)p->withVarStack->items[p->withVarStack->count - 1]), memberName);
        }
        case TT_IntLiteral: case TT_FloatLiteral: case TT_StringLiteral: case TT_CharLiteral: case TT_True: case TT_False: case TT_Undefined: {
            FcValue v;
            Advance(p);
            v = LiteralValue(p, tok);
            return (FcExpr*)FC_NEW(C_, FcLiteralExpr, .h = {EK_LiteralExpr, tok->line}, .value = v);
        }
        case TT_InterpolatedStringLiteral:
            Advance(p);
            return ParseInterpolatedString(p, tok);
        case TT_Identifier:
            if (streq(tok->lexeme, "probe") && IsProbeOperandNext(p)) return ParseProbe(p);
            if (!noShortLambda && PeekAt(p, 1)->type == TT_Arrow) {
                Advance(p);
                return ParseLambdaTail(p, tok->line, L1(p, newParam(p, tok->lexeme, NULL, L(p), NULL, false)), NULL);
            }
            Advance(p);
            return mkIdent(p, tok->line, tok->lexeme);
        case TT_This: Advance(p); return (FcExpr*)FC_NEW(C_, FcThisExpr, .h = {EK_ThisExpr, tok->line});
        case TT_Base: Advance(p); return (FcExpr*)FC_NEW(C_, FcBaseExpr, .h = {EK_BaseExpr, tok->line});
        case TT_LBracket: {
            FcList* elements = L(p);
            Advance(p);
            if (!Check(p, TT_RBracket)) {
                do { LA(p, elements, ParseExpression(p)); } while (Match(p, TT_Comma));
            }
            Expect(p, TT_RBracket, "Expected ']' after the array literal");
            return (FcExpr*)FC_NEW(C_, FcArrayLiteralExpr, .h = {EK_ArrayLiteralExpr, tok->line}, .elements = elements);
        }
        case TT_New: {
            const char* className;
            FcList* typeArgRefs;
            FcList* typeArgs;
            FcList* argNames;
            FcList* args;
            FcTypeRef* classRef;
            int i;
            Advance(p);
            if (IsTypeKeyword(Peek(p)->type) || (Check(p, TT_Identifier) && PeekAt(p, 1)->type == TT_LBracket)) {
                const char* elementTypeName;
                FcTypeRef* elementType;
                FcList* sizeExprs;
                if (Check(p, TT_KwByte) || Check(p, TT_KwWord)) {
                    bool words = Advance(p)->type == TT_KwWord;
                    FcList* bufSizeExprs = ParseArrayRanks(p);
                    if (bufSizeExprs->count == 0) Error(p, "Expected '[' after 'byte' in 'new'", Peek(p));
                    if (bufSizeExprs->count > 1 || bufSizeExprs->items[0] == NULL)
                        Error(p, "'new byte[...]' expects exactly ONE size (no multidimensional byte buffer, no unspecified size)", Peek(p));
                    return (FcExpr*)FC_NEW(C_, FcNewBufferExpr, .h = {EK_NewBufferExpr, tok->line}, .sizeExpr = (FcExpr*)bufSizeExprs->items[0], .words = words);
                }
                elementTypeName = ParseTypeAnnotationName(p);
                elementType = newTypeRef(p, elementTypeName, FC_NULL_INT, 0);
                elementType->namespaces = CurrentNamespaces(p);
                sizeExprs = ParseArrayRanks(p);
                if (sizeExprs->count == 0) Error(p, "Expected '[' after the array element type in 'new'", Peek(p));
                return (FcExpr*)FC_NEW(C_, FcNewArrayExpr, .h = {EK_NewArrayExpr, tok->line}, .elementType = elementType, .sizeExprs = sizeExprs);
            }
            className = ParseTypeAnnotationName(p);
            typeArgRefs = Check(p, TT_Lt) ? ParseTypeArgRefs(p) : NULL;
            typeArgs = L(p);
            if (typeArgRefs) for (i = 0; i < typeArgRefs->count; i++) LA(p, typeArgs, TypeArgName((FcTypeRef*)typeArgRefs->items[i]));
            args = ParseArgList(p, &argNames, true);
            classRef = newTypeRef(p, className, FC_NULL_INT, 0);
            classRef->namespaces = CurrentNamespaces(p);
            classRef->typeArgCount = typeArgs->count;
            classRef->typeArgs = typeArgRefs;
            return (FcExpr*)FC_NEW(C_, FcNewExpr, .h = {EK_NewExpr, tok->line}, .classRef = classRef, .args = args, .typeArgs = typeArgs, .argNames = argNames);
        }
        case TT_Throw: {
            FcExpr* value;
            Advance(p);
            value = ParseExpression(p);
            return (FcExpr*)FC_NEW(C_, FcThrowExpr, .h = {EK_ThrowExpr, tok->line}, .value = value);
        }
        case TT_Func: return ParseLambda(p);
        case TT_LParen: {
            FcExpr* inner;
            if (!noShortLambda && IsParenLambda(p)) {
                int lambdaLine = tok->line;
                FcList* parms = ParseParamList(p, false);
                return ParseLambdaTail(p, lambdaLine, parms, NULL);
            }
            Advance(p);
            inner = ParseExpression(p);
            Expect(p, TT_RParen, "Expected ')' after the parenthesized expression");
            if (inner->kind == EK_AssignExpr) ((FcAssignExpr*)inner)->parenthesized = true;
            return inner;
        }
        default:
            Error(p, S(p, "Unexpected token '%s' (%s)", tok->lexeme, fcTokenTypeName(tok->type)), tok);
    }
}

/* the expression of a `{...}` of a format string: lexed and parsed on its own; an error becomes one of the format string */
typedef struct FcInterpolationJob { FcParser* outer; FcToken* tok; FcSegment* seg; FcExpr* result; FcTokens innerTokens; } FcInterpolationJob;

static void fcParseInterpolationExpr(FcInterpolationJob* job) {
    FcParser* p = job->outer;
    FcCtx* c = p->ctx;
    const char* volatile message = NULL;
    FC_TRY(c) {
        job->innerTokens = fcTokenizeFc8(c, job->seg->text);
    } FC_CATCH(c) {
        if (c->err.kind != FC_ERR_LEX) fcRethrow(c);
        message = fcFmt(c, "Invalid expression in a format string: %s (%d:%d)", c->err.message, c->err.line, c->err.column);
    } FC_END_TRY;
    if (message) Error(p, message, job->tok);
    FC_TRY(c) {
        FcParser* innerParser = fcParserNew(c, job->innerTokens);
        job->result = ParseExpression(innerParser);
        if (!Check(innerParser, TT_Eof))
            Error(innerParser, "Unexpected further tokens after the expression in a format string interpolation (is an operator missing, or is a ':' not enclosed in parentheses by mistake?)",
                Peek(innerParser));
    } FC_CATCH(c) {
        if (c->err.kind != FC_ERR_PARSE) fcRethrow(c);
        message = c->err.file ? fcFmt(c, "Invalid expression in a format string: %s: %s (%d:%d)", c->err.file, c->err.message, c->err.line, c->err.column)
            : fcFmt(c, "Invalid expression in a format string: %s (%d:%d)", c->err.message, c->err.line, c->err.column);
    } FC_END_TRY;
    if (message) Error(p, message, job->tok);
}

static FcExpr* ParseInterpolatedString(FcParser* p, FcToken* tok) {
    FcList* parts = L(p);
    int i;
    for (i = 0; i < tok->segments->count; i++) {
        FcSegment* seg = (FcSegment*)tok->segments->items[i];
        FcInterpolationPart* part = (FcInterpolationPart*)fcAlloc(p->ctx, sizeof *part);
        if (!seg->isExpression) {
            part->text = seg->text;
            LA(p, parts, part);
            continue;
        }
        {
            FcInterpolationJob job;
            job.outer = p; job.tok = tok; job.seg = seg; job.result = NULL;
            fcParseInterpolationExpr(&job);
            part->isExpression = true;
            part->expression = job.result;
            part->format = seg->format;
            LA(p, parts, part);
        }
    }
    return (FcExpr*)FC_NEW(C_, FcInterpolatedStringExpr, .h = {EK_InterpolatedStringExpr, tok->line}, .parts = parts);
}

static FcExpr* ParseLambda(FcParser* p) {
    int line = Peek(p)->line;
    FcList* parms;
    FcExpr* onTarget = NULL;
    Expect(p, TT_Func, "Expected 'func'");
    parms = ParseParamList(p, false);
    if (Match(p, TT_On)) {
        p->suppressShortLambda = true;
        onTarget = ParsePostfix(p);
        p->suppressShortLambda = false;
    }
    return ParseLambdaTail(p, line, parms, onTarget);
}

static bool IsProbeOperandNext(FcParser* p) { return PeekAt(p, 1)->type == TT_Identifier || PeekAt(p, 1)->type == TT_This; }

static FcExpr* ParseProbePath(FcParser* p, const char** memberOut, bool* hasMember) {
    FcExpr* target = ParsePrimary(p);
    const char* member = NULL;
    if (!Check(p, TT_Dot)) { *memberOut = NULL; *hasMember = false; return target; }
    while (Check(p, TT_Dot)) {
        FcToken* nameTok;
        Advance(p);
        if (Check(p, TT_Star)) {
            FcToken* starTok = Advance(p);
            if (member) target = mkMember(p, starTok->line, target, member);
            member = NULL;
            break;
        }
        nameTok = Expect(p, TT_Identifier, "Expected a member name after '.'");
        if (member) target = mkMember(p, nameTok->line, target, member);
        member = nameTok->lexeme;
    }
    *memberOut = member;
    *hasMember = true;
    return target;
}

static FcList* ImplicitProbeParams(FcParser* p) {
    static const char* const names[] = { "sender", "name", "old", "value" };
    FcList* l = L(p);
    int i;
    for (i = 0; i < 4; i++) LA(p, l, newParam(p, names[i], NULL, L(p), NULL, false));
    return l;
}

static FcExpr* ParseProbe(FcParser* p) {
    int line = Peek(p)->line;
    const char* member;
    bool hasMember, changing;
    FcExpr* target;
    FcExpr* handler;
    Advance(p);
    target = ParseProbePath(p, &member, &hasMember);
    if (!hasMember) Error(p, "'probe' expects a member: 'probe object.member changed ...' (or 'object.*' for all)", Peek(p));
    if (!Check(p, TT_Identifier) || !(streq(Peek(p)->lexeme, "changed") || streq(Peek(p)->lexeme, "changing")))
        Error(p, "Expected 'changed' or 'changing' after the target of 'probe'", Peek(p));
    changing = streq(Advance(p)->lexeme, "changing");
    if (Check(p, TT_LBrace)) {
        FcBlockStmt* body = ParseBlock(p);
        handler = mkLambda(p, line, ImplicitProbeParams(p), NULL, body, true);
    } else if (Check(p, TT_Arrow)) handler = ParseLambdaTail(p, line, ImplicitProbeParams(p), NULL);
    else if (IsParenLambda(p)) { FcList* parms = ParseParamList(p, false); handler = ParseLambdaTail(p, line, parms, NULL); }
    else handler = ParseExpression(p);
    return (FcExpr*)FC_NEW(C_, FcProbeExpr, .h = {EK_ProbeExpr, line}, .target = target, .member = member, .isChanging = changing, .handler = handler);
}

static FcStmt* ParseSilence(FcParser* p) {
    int line = Peek(p)->line;
    const char* member;
    bool hasMember;
    FcExpr* target;
    Advance(p);
    target = ParseProbePath(p, &member, &hasMember);
    ExpectStatementTerminator(p);
    return (FcStmt*)FC_NEW(C_, FcSilenceStmt, .h = {SK_SilenceStmt, p->sourceIndex, line}, .target = target, .member = member, .memberForm = hasMember);
}

static bool IsParenLambda(FcParser* p) {
    int depth = 0, i;
    for (i = 0;; i++) {
        FcToken* t = PeekAt(p, i);
        if (t->type == TT_Eof) return false;
        if (t->type == TT_LParen) depth++;
        else if (t->type == TT_RParen && --depth == 0) return PeekAt(p, i + 1)->type == TT_Arrow;
    }
}

static FcExpr* ParseLambdaTail(FcParser* p, int line, FcList* parms, FcExpr* onTarget) {
    FcBlockStmt* body;
    FcList* uses;
    bool expressionBody;
    FcLambdaExpr* lambda;
    Expect(p, TT_Arrow, "Expected '=>' in the lambda");
    LA(p, p->captureUses, L(p));
    pPushCleanup(p, CL_CAPTURE_USES, 0, false, NULL);
    expressionBody = !Check(p, TT_LBrace);
    if (Check(p, TT_LBrace)) body = ParseBlock(p);
    else {
        int exprLine = Peek(p)->line;
        FcExpr* value = ParseExpression(p);
        body = mkBlock(p, exprLine, L1(p, mkReturn(p, exprLine, value)));
    }
    pPopCleanup(p);
    uses = PopCaptureUses(p);
    lambda = (FcLambdaExpr*)mkLambda(p, line, parms, onTarget, body, true);
    lambda->captureModes = uses->count > 0 ? uses : NULL;
    lambda->expressionBody = expressionBody;
    return (FcExpr*)lambda;
}
