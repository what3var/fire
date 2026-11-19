/* native/forge/preprocessor.c - port of src/fire.Compiler/Preprocessor.cs (commit df5ad3c3): Preprocessor, DirectiveRegistry (the default one:
 * `#include` and the announced directives), ProcessedSource. Not ported: the resources of `new Resource("path")` (only with a ResourceTable, which
 * the frontend dumps do not have) and markup files included with `#include` (Phase C 5: an internal error). */

typedef struct FcProcessed {
    const uint16_t* source;
    int length;
    FcList* usings;           /* fc8 */
    FcList* importNamespaces; /* fc8 */
    const char* name;
} FcProcessed;

typedef struct FcPreprocessor {
    FcCtx* ctx;
    FcList* includeChain;     /* full paths */
    FcList* usings;
    FcList* importNamespaces;
    FcList* alreadyIncluded;  /* full paths (case-insensitive, as the C# set) */
} FcPreprocessor;

/* the directives the default registry knows (DirectiveRegistry constructor and CreateDefault): announced ones disappear, `include` has a handler */
static const char* const fcAnnounced[] = { "import", "include", "name", "codename", "author", "comments", "description", "icon", "debug", "performance",
    "floatwidth", "noconsole", "version", "fileversion" };

static bool fcAsciiIEq(const char* a, const char* b) {
    for (; *a && *b; a++, b++) {
        char x = *a, y = *b;
        if (x >= 'A' && x <= 'Z') x = (char)(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = (char)(y - 'A' + 'a');
        if (x != y) return false;
    }
    return *a == *b;
}
static bool fcListHasIStr(const FcList* l, const char* s) {
    int i;
    for (i = 0; i < l->count; i++) if (fcAsciiIEq((const char*)l->items[i], s)) return true;
    return false;
}

/* `^\s*#([A-Za-z_][A-Za-z0-9_]*)(?:[ \t]+(.*))?\s*$`: false when the line is no directive */
static bool fcDirectiveLine(FcCtx* c, FcSlice line, const char** name, FcSlice* argument, bool* hasArgument) {
    int i = 0, start;
    while (i < line.n && fcIsWhiteSpace(line.p[i])) i++;
    if (i >= line.n || line.p[i] != '#') return false;
    i++;
    if (i >= line.n || !fcIsNameStart(line.p[i])) return false;
    start = i;
    while (i < line.n && fcIsNamePart(line.p[i])) i++;
    *name = fc8FromU16(c, line.p + start, i - start);
    *hasArgument = false;
    argument->p = line.p + line.n;
    argument->n = 0;
    if (i < line.n && (line.p[i] == ' ' || line.p[i] == '\t')) {
        while (i < line.n && (line.p[i] == ' ' || line.p[i] == '\t')) i++;
        *hasArgument = true;
        argument->p = line.p + i;
        argument->n = line.n - i;
        return true;
    }
    for (; i < line.n; i++) if (!fcIsWhiteSpace(line.p[i])) return false;
    return true;
}

/* `^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$` */
static bool fcUsingName(FcSlice s) {
    int i = 0;
    for (;;) {
        if (i >= s.n || !fcIsNameStart(s.p[i])) return false;
        i++;
        while (i < s.n && fcIsNamePart(s.p[i])) i++;
        if (i == s.n) return true;
        if (s.p[i] != '.') return false;
        i++;
    }
}

/* ---- paths (Path.GetFullPath, Path.Combine, Path.GetDirectoryName) ---- */
static bool fcIsSep(char ch) {
#ifdef _WIN32
    return ch == '/' || ch == '\\';
#else
    return ch == '/';
#endif
}
static const char* fcFullPath(FcCtx* c, const char* basePath, const char* path) {
    FcBuf joined = {0};
    const char** parts;
    int count = 0, i;
    const char* p;
    FcBuf out = {0};
    if (!fcIsSep(path[0])) {
        if (!fcIsSep(basePath[0])) {
            char cwd[4096];
#ifdef _WIN32
            if (!_getcwd(cwd, sizeof cwd)) strcpy(cwd, ".");
#else
            if (!getcwd(cwd, sizeof cwd)) strcpy(cwd, ".");
#endif
            fcBufStr(&joined, cwd);
            fcBufChar(&joined, '/');
        }
        fcBufStr(&joined, basePath);
        fcBufChar(&joined, '/');
    }
    fcBufStr(&joined, path);
    parts = (const char**)fcAlloc(c, sizeof(char*) * (joined.len + 1));
    for (p = joined.p; *p;) {
        const char* s;
        while (*p && fcIsSep(*p)) p++;
        s = p;
        while (*p && !fcIsSep(*p)) p++;
        if (p == s) break;
        if (p - s == 1 && s[0] == '.') continue;
        if (p - s == 2 && s[0] == '.' && s[1] == '.') { if (count > 0) count--; continue; }
        parts[count++] = fcStrN(c, s, (size_t)(p - s));
    }
    fcBufStr(&out, "");
    for (i = 0; i < count; i++) { fcBufChar(&out, '/'); fcBufStr(&out, parts[i]); }
    if (count == 0) fcBufChar(&out, '/');
    fcBufFree(&joined);
    return fcBufDone(c, &out);
}
static const char* fcDirectoryName(FcCtx* c, const char* fullPath) {
    const char* last = NULL;
    const char* p;
    for (p = fullPath; *p; p++) if (fcIsSep(*p)) last = p;
    if (!last) return ".";
    if (last == fullPath) return "/";
    return fcStrN(c, fullPath, (size_t)(last - fullPath));
}
static bool fcFileExists(const char* path) {
    FILE* f = fopen(path, "rb");
    if (!f) return false;
    fclose(f);
    return true;
}

static void fcProcessInner(FcPreprocessor* pp, const uint16_t* source, int length, const char* basePath, FcU16* out);

/* Preprocessor.ProcessFileRecursive */
static void fcProcessFileRecursive(FcPreprocessor* pp, const char* fullPath, FcU16* out) {
    FcCtx* c = pp->ctx;
    FcU16 text = {0};
    const uint16_t* copy;
    int n;
    if (fcListHasIStr(pp->includeChain, fullPath)) {
        FcBuf chain = {0};
        int i;
        fcBufStr(&chain, "");
        for (i = 0; i < pp->includeChain->count; i++) { if (i) fcBufStr(&chain, " -> "); fcBufStr(&chain, (const char*)pp->includeChain->items[i]); }
        fcPreError(c, fcFmt(c, "Circular include of '%s' (chain: %s -> %s).", fullPath, fcBufDone(c, &chain), fullPath));
    }
    if (!fcReadText(fullPath, &text)) fcPreError(c, fcFmt(c, "'%s' could not be read: %s", fullPath, "the file cannot be opened"));
    if (fcEndsWith(fullPath, ".fxml") || fcEndsWith(fullPath, ".biscuit") || fcEndsWith(fullPath, ".fdata")) {
        fcU16Free(&text);
        fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "'%s': markup files are not ported yet (Phase C 5)", fullPath));
    }
    n = text.len;
    copy = (const uint16_t*)fcAlloc(c, sizeof(uint16_t) * (size_t)(n + 1));
    if (n) memcpy((void*)copy, text.p, sizeof(uint16_t) * (size_t)n);
    fcU16Free(&text);
    fcListAdd(c, pp->includeChain, (void*)fullPath);
    fcProcessInner(pp, copy, n, fcDirectoryName(c, fullPath), out);
    pp->includeChain->count--;
}

static const char* fcValueKindName(FcTokenType t) {
    switch (t) {
        case TT_StringLiteral: return "String";
        case TT_IntLiteral: return "Int";
        case TT_FloatLiteral: return "Float";
        case TT_CharLiteral: return "Char";
        case TT_True: case TT_False: return "Bool";
        default: return "Undefined";
    }
}

static void fcLexDirectiveArgs(FcCtx* c, FcSlice argText, const char* defName, int line, FcTokens* out) {
    FC_TRY(c) {
        *out = fcTokenize(c, argText.p, argText.n);
    } FC_CATCH(c) {
        if (c->err.kind != FC_ERR_LEX) fcRethrow(c);
        fcPreError(c, fcFmt(c, "Invalid parameters for '#%s' (line %d): %s (%d:%d)", defName, line, c->err.message, c->err.line, c->err.column));
    } FC_END_TRY;
}

/* ParseDirectiveArgs + ReadDirectiveLiteral for the one directive with parameters (`#include`, 1 parameter): the tokens of the values */
static FcList* fcParseDirectiveArgs(FcCtx* c, FcSlice argText, const char* defName, int paramCount, int line) {
    FcTokens tokens;
    FcList* values = fcListNew(c);
    int i = 0;
    fcLexDirectiveArgs(c, argText, defName, line, &tokens);
    while (i < tokens.count && tokens.items[i].type != TT_Eof) {
        FcToken* tok = &tokens.items[i];
        switch (tok->type) {
            case TT_StringLiteral: case TT_IntLiteral: case TT_FloatLiteral: case TT_CharLiteral: case TT_True: case TT_False: case TT_Undefined:
                fcListAdd(c, values, tok);
                i++;
                break;
            case TT_Minus:
                i++;
                if (i >= tokens.count || (tokens.items[i].type != TT_IntLiteral && tokens.items[i].type != TT_FloatLiteral))
                    fcPreError(c, fcFmt(c, "Expected a number after '-' in the parameters of '#%s' (line %d).", defName, line));
                fcListAdd(c, values, &tokens.items[i]);
                i++;
                break;
            default:
                fcPreError(c, fcFmt(c, "Expected a literal value (string/number/char/bool/undefined) in the parameters of '#%s', not '%s' (line %d).",
                    defName, tok->lexeme, line));
        }
        if (i < tokens.count && tokens.items[i].type == TT_Comma) { i++; continue; }
        break;
    }
    if (i < tokens.count && tokens.items[i].type != TT_Eof)
        fcPreError(c, fcFmt(c, "Unexpected token '%s' in the parameters of '#%s' (line %d).", tokens.items[i].lexeme, defName, line));
    if (values->count != paramCount)
        fcPreError(c, fcFmt(c, "'#%s' expects %d parameters, got %d (line %d).", defName, paramCount, values->count, line));
    return values;
}

/* the handler of `#include` (DirectiveRegistry.CreateDefault) */
static void fcIncludeHandler(FcPreprocessor* pp, const char* basePath, FcList* args, int line, FcU16* out) {
    FcCtx* c = pp->ctx;
    FcToken* arg = (FcToken*)args->items[0];
    const char* relativePath;
    const char* fullPath;
    if (arg->type != TT_StringLiteral)
        fcPreError(c, fcFmt(c, "'#include' expects a string as the path, not %s (line %d).", fcValueKindName(arg->type), line));
    relativePath = arg->stringValue;
    fullPath = fcFullPath(c, basePath, relativePath);
    if (fcListHasIStr(pp->alreadyIncluded, fullPath)) return;   /* already inserted (anywhere in the whole composition) */
    fcListAdd(c, pp->alreadyIncluded, (void*)fullPath);
    if (!fcFileExists(fullPath))
        fcPreError(c, fcFmt(c, "'#include \"%s\"' - file not found: '%s' (line %d).", relativePath, fullPath, line));
    fcProcessFileRecursive(pp, fullPath, out);
}

static bool fcU16EndsWithNewline(const FcU16* b, int from) { return b->len > from && b->p[b->len - 1] == '\n'; }

static void fcProcessInner(FcPreprocessor* pp, const uint16_t* source, int length, const char* basePath, FcU16* out) {
    FcCtx* c = pp->ctx;
    FcCondState conditional = {0};
    FcInlineCScan inlineC = {0};
    int lineStart = 0, lineNo = 0;
    for (;;) {
        int end = lineStart;
        FcSlice line;
        const char* name;
        FcSlice argument;
        bool hasArgument, match;
        while (end < length && source[end] != '\n') end++;
        line.p = source + lineStart;
        line.n = end - lineStart;
        while (line.n > 0 && line.p[line.n - 1] == '\r') line.n--;
        if (fcInlineInBody(&inlineC)) {
            fcInlineFeed(&inlineC, line);
            fcU16Put(out, line.p, line.n);
            fcU16Unit(out, '\n');
            goto next;
        }
        match = fcDirectiveLine(c, line, &name, &argument, &hasArgument);
        if (!match && fcCondActive(&conditional)) fcInlineFeed(&inlineC, line);
        if (!match) {
            if (fcCondActive(&conditional)) fcU16Put(out, line.p, line.n);
            fcU16Unit(out, '\n');
            goto next;
        }
        if (fcCondTryHandle(c, &conditional, name, argument, lineNo + 1, c->symbols)) { fcU16Unit(out, '\n'); goto next; }
        if (!fcCondActive(&conditional)) { fcU16Unit(out, '\n'); goto next; }
        if (fcAsciiIEq(name, "using")) {
            FcSlice usingArg = fcSliceTrim(argument);
            if (!fcUsingName(usingArg))
                fcPreError(c, fcFmt(c, "'#using' expects a (possibly dotted) namespace name, not '%s' (line %d).", fcSliceText(c, usingArg), lineNo + 1));
            fcListAdd(c, pp->usings, (void*)fcSliceText(c, usingArg));
            fcU16Unit(out, '\n');
            goto next;
        }
        {
            size_t k;
            bool known = false;
            for (k = 0; k < sizeof fcAnnounced / sizeof fcAnnounced[0]; k++) if (fcAsciiIEq(name, fcAnnounced[k])) known = true;
            if (!known) { fcU16Put(out, line.p, line.n); fcU16Unit(out, '\n'); goto next; }
            if (!fcAsciiIEq(name, "include")) { fcU16Unit(out, '\n'); goto next; }
            {
                FcList* args = fcParseDirectiveArgs(c, argument, "include", 1, lineNo + 1);
                int before = out->len;
                fcIncludeHandler(pp, basePath, args, lineNo + 1, out);
                if (out->len > before) { if (!fcU16EndsWithNewline(out, before)) fcU16Unit(out, '\n'); }
                else fcU16Unit(out, '\n');
            }
        }
    next:
        lineNo++;
        if (end >= length) break;
        lineStart = end + 1;
    }
    fcCondEnsureClosed(c, &conditional);
    free(conditional.frames);
}

/* Preprocessor.Process(source, basePath, registry): the default registry with the symbols of the context */
FcProcessed* fcPreprocess(FcCtx* c, const uint16_t* source, int length, const char* basePath) {
    FcPreprocessor pp;
    FcU16 out = {0};
    FcProcessed* r = (FcProcessed*)fcAlloc(c, sizeof *r);
    uint16_t* copy;
    int i;
    pp.ctx = c;
    pp.includeChain = fcListNew(c);
    pp.usings = fcListNew(c);
    pp.importNamespaces = fcListNew(c);
    pp.alreadyIncluded = fcListNew(c);
    fcU16Put(&out, NULL, 0);
    fcProcessInner(&pp, source, length, basePath, &out);
    copy = (uint16_t*)fcAlloc(c, sizeof(uint16_t) * (size_t)(out.len + 1));
    if (out.len) memcpy(copy, out.p, sizeof(uint16_t) * (size_t)out.len);
    r->source = copy;
    r->length = out.len;
    fcU16Free(&out);
    r->usings = pp.usings;
    for (i = 0; i < pp.importNamespaces->count; i++)
        if (!fcListHasStr(r->usings, (const char*)pp.importNamespaces->items[i])) fcListAdd(c, r->usings, pp.importNamespaces->items[i]);
    r->importNamespaces = pp.importNamespaces;
    return r;
}
