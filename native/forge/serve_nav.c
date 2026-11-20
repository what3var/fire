/* native/forge/serve_nav.c - the questions about a place of `forge serve` (docs/spec/FORGE_SERVE.md): src/fire.Editor/Language/NavigationEngine.cs
 * (commit 1c549476: TryResolveSymbol, FindOpenCall, TryResolveCall, ResolvedSymbol.Header, PickOverload) for `hover`, `definition` and `signature`;
 * new on the same index: `references` (every identifier of the document whose resolution is the same declaration), `symbols` (the outline of the
 * document's own classes, enums and namespaces), `folding` (blocks, comment runs, `#if`), `preludeSource`. The `///` comments go out raw (the client
 * parses them: DocComments.Parse). Included in serve.c after the server's types. */

typedef struct SvResolved {
    const char* file;      /* the file of the declaration: NULL the document, else a file of the project or an #include */
    int line;
    const char* prelude;   /* a prelude: `standard` or an import */
    SvClass* cls;
    SvMember* member;
    const char* text;      /* a header of its own (a lambda parameter) */
    const char* doc;       /* a documentation of its own */
    bool include;          /* an #include line: `file` is the path as written */
} SvResolved;

static bool svNavIdent(uint16_t ch) { return fcIsLetterOrDigit(ch) || ch == '_'; }

static SvResolved* svResolvedNew(FcCtx* t, const char* file, int line, const char* prelude) {
    SvResolved* r = (SvResolved*)fcAlloc(t, sizeof *r);
    r->file = file; r->line = line; r->prelude = prelude;
    return r;
}
static SvResolved* svClassTarget(FcCtx* t, SvClass* k) { SvResolved* r = svResolvedNew(t, k->source ? k->source->filePath : NULL, k->declLine, k->preludeName); r->cls = k; return r; }
static SvResolved* svMemberTarget(FcCtx* t, SvMember* m) { SvResolved* r = svResolvedNew(t, m->source ? m->source->filePath : NULL, m->declLine, m->source ? m->source->preludeName : NULL); r->member = m; return r; }
static SvResolved* svEnumTarget(SvIndex* x, const char* key) {
    void* line = fcDictGet(&x->enumDeclLines, key);
    return svResolvedNew(x->lang->tmp, (const char*)fcDictGet(&x->enumFiles, key), (int)(intptr_t)line, (const char*)fcDictGet(&x->enumPreludes, key));
}
static bool svEnumHas(SvIndex* x, const char* key, const char* name) { FcList* l = (FcList*)fcDictGet(&x->enumMembers, key); return l && fcListHasStr(l, name); }

typedef struct SvNamePred { const char* name; } SvNamePred;
static bool svPredNameLine(const SvMember* m, const void* a) { return strcmp(m->name, ((const SvNamePred*)a)->name) == 0 && m->declLine > 0; }
static SvMember* svNavFindMember(SvIndex* x, const char* cls, const char* name) { SvNamePred p; p.name = name; return svFirstMember(x, cls, svPredNameLine, &p); }

static SvResolved* svResolveMemberOfType(SvIndex* x, SvType type, const char* identifier) {
    FcCtx* t = x->lang->tmp;
    switch (type.kind) {
        case TK_INSTANCE: case TK_STATIC: { SvMember* m = svNavFindMember(x, type.name, identifier); return m ? svMemberTarget(t, m) : NULL; }
        case TK_ENUM: return svEnumHas(x, type.name, identifier) && fcDictHas(&x->enumDeclLines, type.name) ? svEnumTarget(x, type.name) : NULL;
        case TK_NAMESPACE: {
            const char* full = fcFmt(t, "%s.%s", type.name, identifier);
            SvClass* k = svClassGet(x, full);
            if (k && k->declLine > 0) return svClassTarget(t, k);
            if (fcDictHas(&x->enumDeclLines, full)) return svEnumTarget(x, full);
            return NULL;
        }
        case TK_PRIMITIVE: case TK_ARRAY: {
            const char* key = svExtensionClassOf(type);
            if (key && fcDictHas(&x->classes, key)) { SvMember* m = svNavFindMember(x, key, identifier); if (m) return svMemberTarget(t, m); }
            return NULL;
        }
        default: return NULL;
    }
}

static const char* svReadIdentifierAt(SvIndex* x, int offset, int* idStart) {
    const uint16_t* s = x->src;
    int start = offset, end = offset;
    *idStart = 0;
    while (start > 0 && svNavIdent(s[start - 1])) start--;
    while (end < x->srcLen && svNavIdent(s[end])) end++;
    if (start >= end || fcIsDigit(s[start])) return NULL;
    *idStart = start;
    return fc8FromU16(x->lang->tmp, s + start, end - start);
}
static const char* svReadDottedNameBefore(SvIndex* x, int dotIdx) {
    const uint16_t* s = x->src;
    int end = dotIdx, start = dotIdx, i;
    while (start > 0 && (svNavIdent(s[start - 1]) || s[start - 1] == '.')) start--;
    if (start >= end) return NULL;
    if (s[start] == '.' || s[end - 1] == '.' || fcIsDigit(s[start])) return NULL;
    for (i = start; i + 1 < end; i++) if (s[i] == '.' && s[i + 1] == '.') return NULL;
    return fc8FromU16(x->lang->tmp, s + start, end - start);
}
static const char* svReadIdentifierBeforeDot(SvIndex* x, int idStart) {
    const uint16_t* s = x->src;
    int end, start;
    if (idStart <= 0 || s[idStart - 1] != '.') return NULL;
    end = idStart - 1;
    start = end;
    while (start > 0 && svNavIdent(s[start - 1])) start--;
    if (start >= end) return NULL;
    return fc8FromU16(x->lang->tmp, s + start, end - start);
}
static int svLineOfOffset(SvIndex* x, int offset) {
    int line = 1, i;
    for (i = 0; i < offset && i < x->srcLen; i++) if (x->src[i] == '\n') line++;
    return line;
}

/* TryResolveSymbol */
static SvResolved* svResolveSymbol(SvIndex* x, int offset) {
    FcCtx* t = x->lang->tmp;
    int line, idStart, i;
    const char* identifier;
    const char* receiver;
    const char* key;
    SvLambdaParam* lp;
    SvClass* k;
    if (offset < 0 || offset > x->srcLen) return NULL;
    line = svLineOfOffset(x, offset);
    for (i = 0; i < x->includes->count; i++) {
        SvInclude* inc = (SvInclude*)x->includes->items[i];
        if (inc->line == line) { SvResolved* r = svResolvedNew(t, inc->path, 1, NULL); r->include = true; return r; }
    }
    identifier = svReadIdentifierAt(x, offset, &idStart);
    if (!identifier) return NULL;
    receiver = svReadIdentifierBeforeDot(x, idStart);
    if (!receiver && idStart > 0 && x->src[idStart - 1] == '.') receiver = "";
    if (receiver) {
        const char* dotted = svReadDottedNameBefore(x, idStart - 1);
        SvType type;
        SvResolved* via;
        const char* className;
        const char* receiverEnum;
        if (dotted) {
            const char* qualified = fcFmt(t, "%s.%s", dotted, identifier);
            const char* qc = svTryFindClass(x, qualified, offset);
            const char* qe;
            if (qc && (k = svClassGet(x, qc)) != NULL && k->declLine > 0) return svClassTarget(t, k);
            qe = svTryFindEnum(x, qualified, offset);
            if (qe && fcDictHas(&x->enumDeclLines, qe)) return svEnumTarget(x, qe);
        }
        type = svResolveReceiver(x, idStart - 1);
        via = svResolveMemberOfType(x, type, identifier);
        if (via) return via;
        if (!receiver[0]) return NULL;
        if (strcmp(receiver, "this") == 0) className = svEnclosingClassAt(x, offset);
        else { SvType rt = svResolveName(x, receiver, offset, 0); className = rt.kind == TK_INSTANCE ? rt.name : NULL; }
        if (!className) className = svTryFindClass(x, receiver, offset);
        if (className) { SvMember* m = svNavFindMember(x, className, identifier); if (m) return svMemberTarget(t, m); }
        receiverEnum = svTryFindEnum(x, receiver, offset);
        if (receiverEnum && svEnumHas(x, receiverEnum, identifier)) return svEnumTarget(x, receiverEnum);
        return NULL;
    }
    if ((lp = svLambdaParameterAt(x, identifier, idStart, 0)) != NULL) {
        SvResolved* r = svResolvedNew(t, NULL, line, NULL);
        const char* type = lp->typeName ? lp->typeName : "any";
        r->text = fcFmt(t, "(lambda parameter) %s : %s%s", lp->name, type, lp->isTree ? " - expression tree" : "");
        r->doc = lp->isTree
            ? fcFmt(t, "The lambda goes to %s as an expression tree (%s): the method gets what the body reads, compares and calls - next to its compiled code. Only an expression body; no assignment, ++ or take.", lp->method, lp->target)
            : lp->method ? fcFmt(t, "The lambda goes to %s (%s).", lp->method, lp->target) : "A parameter of the lambda.";
        return r;
    }
    key = svTryFindClass(x, identifier, offset);
    if (key && (k = svClassGet(x, key)) != NULL && k->declLine > 0) return svClassTarget(t, k);
    key = svTryFindEnum(x, identifier, offset);
    if (key && fcDictHas(&x->enumDeclLines, key)) return svEnumTarget(x, key);
    key = svEnclosingClassAt(x, offset);
    if (key) { SvMember* m = svNavFindMember(x, key, identifier); if (m) return svMemberTarget(t, m); }
    return NULL;
}

/* ResolvedSymbol.Header */
static const char* svHeader(FcCtx* t, const SvResolved* r) {
    if (r->text) return r->text;
    if (r->member) {
        const SvMember* m = r->member;
        const char* type = m->typeName ? fcFmt(t, "%s%s", m->typeName, m->typeIsArray ? "[]" : "") : "";
        const char* owner = m->owner[0] ? fcFmt(t, "%s.", m->owner) : "";
        switch (m->kind) {
            case SM_METHOD: return fcFmt(t, "(method) %s%s(%s)%s%s", owner, m->name, m->signature ? m->signature : "", type[0] ? " \xE2\x86\x92 " : "", type);
            case SM_CONSTRUCTOR: return fcFmt(t, "(constructor) new %s(%s)", m->owner, m->signature ? m->signature : "");
            case SM_PROPERTY: return fcFmt(t, "(property) %s%s%s%s", owner, m->name, type[0] ? " : " : "", type);
            default: return fcFmt(t, "(field) %s%s%s%s", owner, m->name, type[0] ? " : " : "", type);
        }
    }
    if (r->cls) {
        FcBuf b = {0};
        int i;
        fcBufFmt(&b, "%s%s", r->cls->isInterface ? "interface " : "class ", r->cls->simpleName);
        for (i = 0; i < r->cls->baseNames->count; i++) fcBufFmt(&b, "%s%s", i ? ", " : " : ", (const char*)r->cls->baseNames->items[i]);
        return fcBufDone(t, &b);
    }
    return "";
}
static const char* svResolvedDoc(FcCtx* t, const SvResolved* r) {
    if (r->doc) return r->doc;
    if (r->member) return svDocAt(r->member->source, t, r->member->declLine);
    if (r->cls) return svDocAt(r->cls->source, t, r->cls->declLine);
    return NULL;
}

/* FindOpenCall: the innermost `(` not closed yet before the caret; *required: the arguments started */
static int svFindOpenCall(SvIndex* x, int caret, int* required) {
    const uint16_t* s = x->src;
    int limit = caret - 3000 > 0 ? caret - 3000 : 0, start = limit, i, n = 0;
    int pos[256], commas[256];
    bool text[256];
    if (caret < 0 || caret > x->srcLen) return -1;
    for (i = caret - 1; i >= limit; i--) if (s[i] == ';' || s[i] == '{' || s[i] == '}') { start = i + 1; break; }
    for (i = start; i < caret; i++) {
        uint16_t c = s[i];
        if (c == '"' || c == '\'') {
            uint16_t q = c;
            i++;
            while (i < caret && s[i] != q && s[i] != '\n') { if (s[i] == '\\') i++; i++; }
            if (n > 0) text[n - 1] = true;
        } else if (c == '/' && i + 1 < caret && s[i + 1] == '/') {
            while (i < caret && s[i] != '\n') i++;
        } else if (c == '/' && i + 1 < caret && s[i + 1] == '*') {
            int close = -1, k;
            for (k = i + 2; k + 1 < x->srcLen; k++) if (s[k] == '*' && s[k + 1] == '/') { close = k; break; }
            if (close < 0 || close + 1 >= caret) return -1;
            i = close + 1;
        } else if (c == '(') {
            if (n > 0) text[n - 1] = true;
            if (n < 256) { pos[n] = i; commas[n] = 0; text[n] = false; n++; }
        } else if (c == ')') {
            if (n > 0) n--;
            if (n > 0) text[n - 1] = true;
        } else if (c == ',' && n > 0) {
            commas[n - 1]++;
            text[n - 1] = false;
        } else if (!fcIsWhiteSpace(c)) {
            if (n > 0) text[n - 1] = true;
        }
    }
    if (n == 0) return -1;
    *required = commas[n - 1] > 0 || text[n - 1] ? commas[n - 1] + 1 : 0;
    return pos[n - 1];
}

static SvMember* svPickOverload(FcList* overloads, int required) {
    SvMember* best = NULL;
    int i;
    for (i = 0; i < overloads->count; i++) {
        SvMember* m = (SvMember*)overloads->items[i];
        if (m->paramCount >= required && (!best || m->paramCount < best->paramCount)) best = m;
    }
    if (best) return best;
    for (i = 0; i < overloads->count; i++) {
        SvMember* m = (SvMember*)overloads->items[i];
        if (!best || m->paramCount > best->paramCount) best = m;
    }
    return best;
}

/* TryResolveCall */
static SvResolved* svResolveCall(SvIndex* x, int paren, int required) {
    FcCtx* t = x->lang->tmp;
    const uint16_t* s = x->src;
    int end = paren, nameStart, before, i;
    bool isNew;
    SvResolved* symbol;
    while (end > 0 && fcIsWhiteSpace(s[end - 1])) end--;
    if (end <= 0 || !svNavIdent(s[end - 1])) return NULL;
    symbol = svResolveSymbol(x, end - 1);
    if (!symbol) return NULL;
    nameStart = end;
    while (nameStart > 0 && (svNavIdent(s[nameStart - 1]) || s[nameStart - 1] == '.')) nameStart--;
    before = nameStart;
    while (before > 0 && fcIsWhiteSpace(s[before - 1])) before--;
    isNew = before >= 3 && s[before - 3] == 'n' && s[before - 2] == 'e' && s[before - 1] == 'w' && (before == 3 || !svNavIdent(s[before - 4]));
    if (isNew && symbol->cls) {
        FcList* ctors = fcListNew(t);
        SvMember* chosen;
        for (i = 0; i < symbol->cls->members->count; i++) {
            SvMember* m = (SvMember*)symbol->cls->members->items[i];
            if (m->kind == SM_CONSTRUCTOR && svDocAt(m->source, t, m->declLine)) fcListAdd(t, ctors, m);
        }
        chosen = svPickOverload(ctors, required);
        if (chosen) { SvResolved* r = svResolvedNew(t, symbol->file, symbol->line, symbol->prelude); r->member = chosen; return r; }
        return symbol;
    }
    if (symbol->member && symbol->member->kind == SM_METHOD) {
        FcList* all = svMembersOfWithDepth(x, symbol->member->owner);
        FcList* overloads = fcListNew(t);
        SvMember* chosen;
        for (i = 0; i < all->count; i++) {
            SvMember* m = ((SvMemberAt*)all->items[i])->m;
            if (strcmp(m->name, symbol->member->name) == 0 && m->kind == SM_METHOD && svDocAt(m->source, t, m->declLine)) fcListAdd(t, overloads, m);
        }
        chosen = svPickOverload(overloads, required);
        return chosen ? svMemberTarget(t, chosen) : symbol;
    }
    return symbol;
}

/* ---- the answers ---- */
static void svTargetJson(SvServer* S, SvDoc* d, const SvResolved* r, FcBuf* b) {
    if (r->prelude) { fcBufStr(b, "\"prelude\":"); svJsonEsc(b, r->prelude); }
    else if (r->include) {
        FcCtx* t = S->lang.tmp;
        fcBufStr(b, "\"path\":");
        svJsonEsc(b, fcFullPath(t, d->base ? d->base : fcDirectoryName(t, fcFullPath(t, ".", d->path)), r->file));
    } else { fcBufStr(b, "\"path\":"); svJsonEsc(b, r->file ? r->file : d->path); }
    fcBufFmt(b, ",\"line\":%d", r->line);
}

static void svSymbolJson(SvServer* S, SvDoc* d, const SvResolved* r, FcBuf* b) {
    FcCtx* t = S->lang.tmp;
    const char* doc = svResolvedDoc(t, r);
    const char* kind = r->text ? "lambdaParameter" : r->member ? (r->member->kind == SM_METHOD ? "method" : r->member->kind == SM_CONSTRUCTOR ? "constructor" : r->member->kind == SM_PROPERTY ? "property" : "field")
        : r->cls ? (r->cls->isInterface ? "interface" : "class") : r->include ? "include" : "enum";
    fcBufStr(b, "{\"header\":");
    svJsonEsc(b, svHeader(t, r));
    fcBufFmt(b, ",\"kind\":\"%s\",\"doc\":", kind);
    svJsonEsc(b, doc);
    fcBufChar(b, ',');
    svTargetJson(S, d, r, b);
    fcBufChar(b, '}');
}

/* the same declaration */
static bool svSameTarget(const SvResolved* a, const SvResolved* b) {
    if (a->member || b->member) return a->member == b->member;
    if (a->cls || b->cls) return a->cls == b->cls;
    return a->line == b->line && fcSameText(a->file, b->file) && fcSameText(a->prelude, b->prelude) && !a->text && !b->text;
}

static void svReferences(SvServer* S, SvDoc* d, SvIndex* x, int offset, FcBuf* b) {
    SvResolved* target = svResolveSymbol(x, offset);
    int idStart, i, n = 0;
    const char* name = svReadIdentifierAt(x, offset, &idStart);
    fcBufChar(b, '[');
    if (target && name && !target->include)
        for (i = 0; i < x->ntok; i++) {
            const FcToken* tk = SVT(x, i);
            SvResolved* r;
            if (tk->type != TT_Identifier || strcmp(tk->lexeme, name) != 0) continue;
            r = svResolveSymbol(x, svTokenOffset(x, i));
            if (!r || !svSameTarget(r, target)) continue;
            if (n++) fcBufChar(b, ',');
            fcBufStr(b, "{\"path\":");
            svJsonEsc(b, d->path);
            fcBufFmt(b, ",\"offset\":%d,\"line\":%d,\"column\":%d,\"length\":%d}", svTokenOffset(x, i), tk->line, tk->column, tk->length);
        }
    fcBufChar(b, ']');
    (void)S;
}

static int svCmpClassLine(const void* a, const void* b) {
    const SvClass* p = *(const SvClass* const*)a;
    const SvClass* q = *(const SvClass* const*)b;
    return p->declLine - q->declLine;
}
static void svSymbols(SvServer* S, SvIndex* x, FcBuf* b) {
    FcCtx* t = S->lang.tmp;
    FcList* own = fcListNew(t);
    int i, k, n = 0;
    FC_DICT_EACH(&x->classes, e) { SvClass* c = (SvClass*)e->value; if (c->source == x && c->declLine > 0) fcListAdd(t, own, c); }
    if (own->count > 1) qsort(own->items, (size_t)own->count, sizeof(void*), svCmpClassLine);
    fcBufChar(b, '[');
    FC_DICT_EACH(&x->namespaces, e) {
        for (i = 0; i < x->nsRanges->count; i++) {
            SvNsRange* r = (SvNsRange*)x->nsRanges->items[i];
            if (strcmp(r->name, (const char*)e->key) != 0) continue;
            if (n++) fcBufChar(b, ',');
            fcBufStr(b, "{\"name\":");
            svJsonEsc(b, r->name);
            fcBufFmt(b, ",\"kind\":\"namespace\",\"offset\":%d,\"children\":[]}", r->start);
            break;
        }
    }
    for (i = 0; i < own->count; i++) {
        SvClass* c = (SvClass*)own->items[i];
        int m = 0;
        if (n++) fcBufChar(b, ',');
        fcBufStr(b, "{\"name\":");
        svJsonEsc(b, c->name);
        fcBufFmt(b, ",\"kind\":\"%s\",\"line\":%d,\"children\":[", c->isInterface ? "interface" : "class", c->declLine);
        for (k = 0; k < c->members->count; k++) {
            SvMember* mb = (SvMember*)c->members->items[k];
            SvResolved r;
            if (mb->source != x) continue;
            memset(&r, 0, sizeof r);
            r.member = mb;
            if (m++) fcBufChar(b, ',');
            fcBufStr(b, "{\"name\":");
            svJsonEsc(b, mb->name);
            fcBufFmt(b, ",\"kind\":\"%s\",\"line\":%d,\"detail\":", mb->kind == SM_METHOD ? "method" : mb->kind == SM_CONSTRUCTOR ? "constructor" : mb->kind == SM_PROPERTY ? "property" : "field", mb->declLine);
            svJsonEsc(b, svHeader(t, &r));
            fcBufStr(b, "}");
        }
        fcBufStr(b, "]}");
    }
    FC_DICT_EACH(&x->enumMembers, e) {
        void* line;
        if (fcDictHas(&x->enumPreludes, e->key) || fcDictHas(&x->enumFiles, e->key) || !fcDictTry(&x->enumDeclLines, e->key, &line)) continue;
        if (n++) fcBufChar(b, ',');
        fcBufStr(b, "{\"name\":");
        svJsonEsc(b, (const char*)e->key);
        fcBufFmt(b, ",\"kind\":\"enum\",\"line\":%d,\"children\":[]}", (int)(intptr_t)line);
    }
    fcBufChar(b, ']');
}

static void svFolding(SvIndex* x, FcBuf* b) {
    int* stack = (int*)malloc(sizeof(int) * (size_t)(x->ntok + 1));
    int sp = 0, i, n = 0, line;
    fcBufChar(b, '[');
    for (i = 0; i < x->ntok; i++) {
        if (SVTY(x, i) == TT_LBrace) stack[sp++] = i;
        else if (SVTY(x, i) == TT_RBrace && sp > 0) {
            int open = stack[--sp];
            if (SVT(x, i)->line > SVT(x, open)->line) {
                if (n++) fcBufChar(b, ',');
                fcBufFmt(b, "{\"start\":%d,\"end\":%d,\"kind\":\"block\"}", SVT(x, open)->line, SVT(x, i)->line);
            }
        }
    }
    free(stack);
    /* comment runs and #if ... #endif, by lines */
    {
        int runStart = 0, ifStack[64], ifs = 0;
        for (line = 1; line <= x->nLines + 1; line++) {
            bool comment = false;
            if (line <= x->nLines) {
                int s = x->lineStarts[line - 1], e = line < x->nLines ? x->lineStarts[line] : x->srcLen;
                while (s < e && fcIsWhiteSpace(x->src[s])) s++;
                comment = s + 1 < e && x->src[s] == '/' && x->src[s + 1] == '/';
                if (s < e && x->src[s] == '#') {
                    if (s + 3 <= e && x->src[s + 1] == 'i' && x->src[s + 2] == 'f' && (s + 3 == e || !svNavIdent(x->src[s + 3]))) { if (ifs < 64) ifStack[ifs++] = line; }
                    else if (s + 6 <= e && x->src[s + 1] == 'e' && x->src[s + 2] == 'n' && x->src[s + 3] == 'd' && x->src[s + 4] == 'i' && x->src[s + 5] == 'f' && ifs > 0) {
                        int from = ifStack[--ifs];
                        if (n++) fcBufChar(b, ',');
                        fcBufFmt(b, "{\"start\":%d,\"end\":%d,\"kind\":\"region\"}", from, line);
                    }
                }
            }
            if (comment && !runStart) runStart = line;
            if (!comment && runStart) {
                if (line - 1 > runStart) { if (n++) fcBufChar(b, ','); fcBufFmt(b, "{\"start\":%d,\"end\":%d,\"kind\":\"comment\"}", runStart, line - 1); }
                runStart = 0;
            }
        }
    }
    fcBufChar(b, ']');
}

static void svNavRequest(SvServer* S, SvDoc* d, const char* method, int offset, FcBuf* b) {
    SvIndex* x = svDocIndex(S, d);
    FcCtx* t = S->lang.tmp;
    if (strcmp(method, "hover") == 0) {
        SvResolved* r = svResolveSymbol(x, offset);
        if (!r || r->include) fcBufStr(b, "null"); else svSymbolJson(S, d, r, b);
    } else if (strcmp(method, "definition") == 0) {
        SvResolved* r = svResolveSymbol(x, offset);
        if (!r || r->line <= 0) fcBufStr(b, "null");
        else { fcBufChar(b, '{'); svTargetJson(S, d, r, b); fcBufChar(b, '}'); }
    } else if (strcmp(method, "signature") == 0) {
        int required = 0, paren = svFindOpenCall(x, offset, &required);
        SvResolved* r = paren >= 0 ? svResolveCall(x, paren, required) : NULL;
        if (!r) fcBufStr(b, "null");
        else {
            int i;
            fcBufFmt(b, "{\"paren\":%d,\"active\":%d,\"symbol\":", paren, required > 0 ? required - 1 : 0);
            svSymbolJson(S, d, r, b);
            fcBufStr(b, ",\"parameters\":[");
            if (r->member) for (i = 0; i < r->member->paramNames->count; i++) { if (i) fcBufChar(b, ','); svJsonEsc(b, (const char*)r->member->paramNames->items[i]); }
            fcBufStr(b, "]}");
        }
    } else if (strcmp(method, "references") == 0) svReferences(S, d, x, offset, b);
    else if (strcmp(method, "symbols") == 0) svSymbols(S, x, b);
    else if (strcmp(method, "folding") == 0) svFolding(x, b);
    else fcBufStr(b, "null");
    (void)t;
}

/* PreludeSourceOf / PreludeTitleOf */
static void svPreludeSourceResult(SvServer* S, const char* name, FcBuf* b) {
    FcU16 text = {0};
    bool ok;
    FcCtx* t = S->lang.tmp;
    if (!name) { fcBufStr(b, "null"); return; }
    ok = strcmp(name, "standard") == 0 ? fcReadText(S->preludePath, &text) : svPreludeSourceOfImport(&S->lang, name, &text);
    if (!ok) { fcBufStr(b, "null"); fcU16Free(&text); return; }
    fcBufStr(b, "{\"title\":");
    svJsonEsc(b, strcmp(name, "standard") == 0 ? "Standard library (prelude)" : fcFmt(t, "Prelude '%s'", fcStartsWith(name, "pkg:") ? name + 4 : name));
    fcBufStr(b, ",\"text\":");
    svJsonEsc(b, fc8FromU16(t, text.p, text.len));
    fcBufChar(b, '}');
    fcU16Free(&text);
}

#ifndef SV_HAVE_MARKUP
static void svMarkupCompletion(SvServer* S, SvDoc* d, int offset, FcBuf* b) { (void)S; (void)d; (void)offset; fcBufStr(b, "{\"replaceStart\":0,\"items\":[]}"); }
#endif
