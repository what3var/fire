/* native/forge/preprocessor.c - port of src/fire.Compiler/Preprocessor.cs (commit df5ad3c3): Preprocessor, DirectiveRegistry (the default one:
 * `#include` and the directives Linker registers, with their numbers of parameters - without their effects), ProcessedSource. Not ported: the resources of `new Resource("path")` (only with a ResourceTable, which
 * the frontend dumps do not have) and markup files included with `#include` (Phase C 5: an internal error). */

typedef struct FcProcessed {
    const uint16_t* source;
    int length;
    FcList* usings;           /* fc8 */
    FcList* importNamespaces; /* fc8 */
    const char* name;
    /* set by the linker (Phase C 3): ProcessedSource.NotSerializable, Module, Qualify */
    bool notSerializable;
    const char* module;
    const char* qualify;
} FcProcessed;

typedef struct FcPreprocessor {
    FcCtx* ctx;
    FcList* includeChain;     /* full paths */
    FcList* usings;
    FcList* importNamespaces;
    FcList* alreadyIncluded;  /* full paths (case-insensitive, as the C# set) */
} FcPreprocessor;

/* the directives of the registry: DirectiveRegistry.CreateDefault (`include` with its handler) and what Linker registers, with the number of
 * parameters (the dumps register them without effects, src/fire.Compiler/FrontendDump.cs LinkerDirectives) - the line of each disappears */
typedef struct FcDirectiveDef { const char* name; int paramCount; } FcDirectiveDef;
static const FcDirectiveDef fcDirectives[] = {
    {"import", 1}, {"include", 1}, {"name", 1}, {"codename", 1}, {"author", 1}, {"comments", 1}, {"description", 1}, {"icon", 1}, {"debug", 0},
    {"performance", 0}, {"floatwidth", 1}, {"noconsole", 0}, {"version", 1}, {"fileversion", 1}, {"intwidth", 1},
};

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

/* ParseDirectiveArgs + ReadDirectiveLiteral: the tokens of the values (the kind of a value is all a handler here asks) */
static FcList* fcParseDirectiveArgs(FcCtx* c, FcSlice argText, const char* defName, int paramCount, int line) {
    FcTokens tokens;
    FcList* values = fcListNew(c);
    int i = 0;
    if (paramCount == 0) {
        if (fcSliceTrim(argText).n > 0) fcPreError(c, fcFmt(c, "'#%s' expects no parameters (line %d).", defName, line));
        return values;
    }
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

/* Preprocessor.IsCode: the index is not in a string or behind `//` */
static bool fcIsCodeAt(const uint16_t* line, int n, int index) {
    bool inString = false;
    int i;
    for (i = 0; i < index; i++) {
        uint16_t ch = line[i];
        if (inString) { if (ch == '\\') i++; else if (ch == '"') inString = false; }
        else if (ch == '"') inString = true;
        else if (ch == '/' && i + 1 < n && line[i + 1] == '/') return false;
    }
    return !inString;
}
static bool fcIsWordUnit(uint16_t ch) { return fcIsLetterOrDigit(ch) || ch == '_'; }
static bool fcIsRegexSpace(uint16_t ch) { return ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == '\f' || ch == '\v' || fcIsWhiteSpace(ch); }
/* Preprocessor.ResolveResources: `new Resource("path")` in the code of a line becomes `new Resource(<id>)` (the regex
 * \bnew\s+Resource\s*\(\s*"((?:[^"\\]|\\.)*)"\s*\)) */
static void fcResolveResources(FcCtx* c, FcSlice line, const char* basePath, int lineNo, FcU16* out) {
    static const uint16_t kResource[] = { 'R', 'e', 's', 'o', 'u', 'r', 'c', 'e' };
    const uint16_t* p = line.p;
    int n = line.n, i = 0, copied = 0;
    while (i + 3 <= n) {
        int j, strStart, strEnd;
        if (!(p[i] == 'n' && p[i + 1] == 'e' && p[i + 2] == 'w' && (i == 0 || !fcIsWordUnit(p[i - 1])) && (i + 3 >= n || !fcIsWordUnit(p[i + 3])))) { i++; continue; }
        j = i + 3;
        if (j >= n || !fcIsRegexSpace(p[j])) { i++; continue; }
        while (j < n && fcIsRegexSpace(p[j])) j++;
        if (j + 8 > n || memcmp(p + j, kResource, sizeof kResource) != 0) { i++; continue; }
        j += 8;
        while (j < n && fcIsRegexSpace(p[j])) j++;
        if (j >= n || p[j] != '(') { i++; continue; }
        j++;
        while (j < n && fcIsRegexSpace(p[j])) j++;
        if (j >= n || p[j] != '"') { i++; continue; }
        strStart = ++j;
        while (j < n && p[j] != '"') { if (p[j] == '\\') { if (j + 1 >= n) break; j += 2; } else j++; }
        if (j >= n || p[j] != '"') { i++; continue; }
        strEnd = j++;
        while (j < n && fcIsRegexSpace(p[j])) j++;
        if (j >= n || p[j] != ')') { i++; continue; }
        j++;
        if (fcIsCodeAt(p, n, i)) {
            /* Regex.Unescape of the path */
            FcU16 path = {0};
            const char* rel;
            const char* full;
            int k, id = -1;
            char num[32];
            for (k = strStart; k < strEnd; k++) {
                uint16_t ch = p[k];
                if (ch == '\\' && k + 1 < strEnd) {
                    uint16_t e = p[++k];
                    fcU16Unit(&path, e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e);
                } else fcU16Unit(&path, ch);
            }
            rel = fc8FromU16(c, path.p, path.len);
            fcU16Free(&path);
            full = fcFullPath(c, basePath, rel);
            if (!fcFileExists(full)) fcPreError(c, fcFmt(c, "Resource file not found: '%s' ('%s', line %d).", rel, full, lineNo));
            for (k = 0; k < c->resourcePaths->count; k++) if (strcmp((const char*)c->resourcePaths->items[k], full) == 0) id = k;
            if (id < 0) { id = c->resourcePaths->count; fcListAdd(c, c->resourcePaths, (void*)full); if (c->resourceNames) fcListAdd(c, c->resourceNames, (void*)rel); }
            fcU16Put(out, p + copied, i - copied);
            snprintf(num, sizeof num, "new Resource(%d)", id);
            for (k = 0; num[k]; k++) fcU16Unit(out, (uint16_t)num[k]);
            copied = j;
        }
        i = j;
    }
    fcU16Put(out, p + copied, n - copied);
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
            if (fcCondActive(&conditional)) {
                if (c->resourcePaths) fcResolveResources(c, line, basePath, lineNo + 1, out);
                else fcU16Put(out, line.p, line.n);
            }
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
            const FcDirectiveDef* def = NULL;
            for (k = 0; k < sizeof fcDirectives / sizeof fcDirectives[0]; k++) if (fcAsciiIEq(name, fcDirectives[k].name)) def = &fcDirectives[k];
            if (!def) { fcU16Put(out, line.p, line.n); fcU16Unit(out, '\n'); goto next; }
            {
                FcList* args = fcParseDirectiveArgs(c, argument, def->name, def->paramCount, lineNo + 1);
                int before = out->len;
                if (strcmp(def->name, "include") == 0) fcIncludeHandler(pp, basePath, args, lineNo + 1, out);
                else if (c->directiveHook) c->directiveHook(c, c->directiveState, def->name, args, lineNo + 1, pp->importNamespaces);
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

/* fcProcessInner, and on an exception the buffer freed (an error of a directive's handler, Phase C 3) */
static void fcProcessGuarded(FcPreprocessor* pp, const uint16_t* source, int length, const char* basePath, FcU16* out) {
    FcCtx* c = pp->ctx;
    FC_TRY(c) {
        fcProcessInner(pp, source, length, basePath, out);
    } FC_CATCH(c) {
        fcU16Free(out);
        fcRethrow(c);
    } FC_END_TRY;
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
    pp.alreadyIncluded = c->sharedIncluded ? c->sharedIncluded : fcListNew(c);
    fcU16Put(&out, NULL, 0);
    fcProcessGuarded(&pp, source, length, basePath, &out);
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
