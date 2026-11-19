/* native/forge/diag.c - port of src/fire.Compiler/Diagnostics.cs (commit 8a0292a7): Diagnostic.ToLogLine, DiagnosticCollector, ErrorCatalog.CodeOf,
 * CompileDiagnostics.Of. The catalogue is native/tables/errors.def (written by `forge tables` from ErrorCatalog). */

typedef struct FcCatalogSource { const char* code; FcArea area; const char* text; } FcCatalogSource;
static const FcCatalogSource fcCatalogSource[] = {
#define FIRE_ERROR(code, area, text) { #code, FC_AREA_##area, text },
#include "../tables/errors.def"
#undef FIRE_ERROR
};

/* ErrorCatalog.Compiled_: the entries of the catalogue (not the general codes of the areas and the three of the native build that All() puts first),
 * "the most specific first" - ordered by the length of their fixed text, longest first, stable */
static void fcCatalogMake(FcCtx* c) {
    int n = (int)(sizeof fcCatalogSource / sizeof fcCatalogSource[0]), i, k, first = 0;
    /* (the first ten lines: E1000 ... E7000 and E7001 ... E7003, see Tables.cs) */
    while (first < n && first < 10) first++;
    c->catalog = (FcCatalogEntry*)fcAlloc(c, sizeof(FcCatalogEntry) * (size_t)(n - first));
    c->catalogCount = 0;
    for (i = first; i < n; i++) {
        FcCatalogEntry* e = &c->catalog[c->catalogCount++];
        const char* t = fcCatalogSource[i].text;
        const char* p;
        int parts = 1;
        for (p = t; (p = strstr(p, "{*}")) != NULL; p += 3) parts++;
        e->code = fcCatalogSource[i].code;
        e->area = fcCatalogSource[i].area;
        e->partCount = parts;
        e->parts = (const char**)fcAlloc(c, sizeof(char*) * (size_t)parts);
        e->literalLength = 0;
        for (k = 0, p = t; k < parts; k++) {
            const char* end = strstr(p, "{*}");
            size_t len = end ? (size_t)(end - p) : strlen(p);
            e->parts[k] = fcStrN(c, p, len);
            e->literalLength += fc8Units(e->parts[k]);
            p = end ? end + 3 : p + len;
        }
    }
    /* a stable sort (insertion: OrderByDescending keeps the order of equal ones) */
    for (i = 1; i < c->catalogCount; i++) {
        FcCatalogEntry e = c->catalog[i];
        for (k = i - 1; k >= 0 && c->catalog[k].literalLength < e.literalLength; k--) c->catalog[k + 1] = c->catalog[k];
        c->catalog[k + 1] = e;
    }
}

/* `^part0.+?part1.+?...partN$` (Singleline): every {*} stands for one character or more */
static bool fcMatchParts(const char* text, const char** parts, int count, int at) {
    size_t len = strlen(parts[at]);
    const char* p;
    if (at == count - 1) {
        size_t n = strlen(text);
        return n >= len + 1 && strcmp(text + n - len, parts[at]) == 0;
    }
    for (p = text + 1; *p; p++)
        if (strncmp(p, parts[at], len) == 0 && fcMatchParts(p + len, parts, count, at + 1)) return true;
    return false;
}
static bool fcCatalogMatches(const FcCatalogEntry* e, const char* text) {
    size_t len = strlen(e->parts[0]);
    if (e->partCount == 1) return strcmp(text, e->parts[0]) == 0;
    if (strncmp(text, e->parts[0], len) != 0) return false;
    return fcMatchParts(text + len, e->parts, e->partCount, 1);
}

static const char* fcGeneralCode(FcArea area) {
    switch (area) {
        case FC_AREA_LEXER: return "E1000";
        case FC_AREA_PREPROCESSOR: return "E2000";
        case FC_AREA_PARSER: return "E3000";
        case FC_AREA_RESOLVER: return "E4000";
        case FC_AREA_COMPILER: return "E5000";
        case FC_AREA_LINKER: return "E6000";
        default: return "E7000";
    }
}

const char* fcCodeOf(FcCtx* c, FcArea area, const char* rawMessage) {
    int i;
    if (fcEndsWith(rawMessage, FC_KEYWORD_HINT_TAIL)) {
        const char* hint = NULL;
        const char* p;
        for (p = rawMessage; (p = strstr(p, " ('")) != NULL; p++) hint = p;
        if (hint && hint > rawMessage) rawMessage = fcStrN(c, rawMessage, (size_t)(hint - rawMessage));
    }
    if (!c->catalog) fcCatalogMake(c);
    for (i = 0; i < c->catalogCount; i++)
        if (c->catalog[i].area == area && fcCatalogMatches(&c->catalog[i], rawMessage)) return c->catalog[i].code;
    return fcGeneralCode(area);
}

void fcDiagLogLine(FcBuf* out, const FcDiagnostic* d) {
    const char* kind = d->severity == FC_SEV_ERROR ? "error" : "warning";
    FcBuf b = {0};
    fcBufStr(&b, "");
    if (d->file == NULL) {
        fcBufStr(&b, kind);
        if (d->code[0]) { fcBufChar(&b, ' '); fcBufStr(&b, d->code); }
        fcBufStr(&b, ": ");
        fcBufStr(&b, d->message);
        if (d->line > 0) fcBufFmt(&b, " (line %d)", d->line);
    } else {
        fcBufStr(&b, d->file);
        if (d->line > 0) fcBufFmt(&b, "(%d)", d->line);
        fcBufStr(&b, ": ");
        fcBufStr(&b, kind);
        if (d->code[0]) { fcBufChar(&b, ' '); fcBufStr(&b, d->code); }
        fcBufStr(&b, ": ");
        fcBufStr(&b, d->message);
    }
    fcOut8(out, b.p);
    fcBufChar(out, '\n');
    fcBufFree(&b);
}

static FcDiagnostic* fcDiagNew(FcCtx* c, int line, const char* message, FcSeverity severity, const char* code, const char* file, int column) {
    FcDiagnostic* d = (FcDiagnostic*)fcAlloc(c, sizeof *d);
    d->line = line; d->message = message; d->severity = severity; d->code = code; d->file = file; d->column = column;
    return d;
}

static bool fcSameText(const char* a, const char* b) { return a == b || (a && b && strcmp(a, b) == 0); }

/* DiagnosticCollector.Add: the same message for the same place once */
static void fcCollect(FcCtx* c, FcDiagnostic* d) {
    int i;
    for (i = 0; i < c->diags->count; i++) {
        FcDiagnostic* o = (FcDiagnostic*)c->diags->items[i];
        if (o->line == d->line && o->severity == d->severity && o->column == d->column && fcSameText(o->message, d->message)
            && fcSameText(o->code, d->code) && fcSameText(o->file, d->file)) return;
    }
    fcListAdd(c, c->diags, d);
}

void fcWarn(FcCtx* c, int id, const char* message, int line, const char* file) {
    if (!c->collecting) return;
    fcCollect(c, fcDiagNew(c, line, message, FC_SEV_WARNING, fcFmt(c, "W%d", id), file, 0));
}

void fcDeclare(FcCtx* c, FcDeclaredWarning* w) {
    if (c->collecting) fcListAdd(c, c->declared, w);
}

/* CompileDiagnostics.Of for the exceptions of the frontend */
FcList* fcDiagnosticsOf(FcCtx* c, const FcError* e, const char* file) {
    FcList* r = fcListNew(c);
    switch (e->kind) {
        case FC_ERR_PARSE:
            fcListAdd(c, r, fcDiagNew(c, e->line, e->message, FC_SEV_ERROR, fcCodeOf(c, FC_AREA_PARSER, e->message), e->file ? e->file : file, e->column));
            break;
        case FC_ERR_LEX:
            fcListAdd(c, r, fcDiagNew(c, e->line, e->message, FC_SEV_ERROR, fcCodeOf(c, FC_AREA_LEXER, e->message), file, e->column));
            break;
        case FC_ERR_PRE: {
            /* the line: the first `(line n)` of the message, 1 without one */
            int line = 1;
            const char* p = e->message;
            while ((p = strstr(p, "(line ")) != NULL) {
                const char* q = p + 6;
                long n = 0;
                if (isdigit((unsigned char)*q)) {
                    while (isdigit((unsigned char)*q)) n = n * 10 + (*q++ - '0');
                    if (*q == ')') { line = (int)n; break; }
                }
                p++;
            }
            fcListAdd(c, r, fcDiagNew(c, line, e->message, FC_SEV_ERROR, fcCodeOf(c, FC_AREA_PREPROCESSOR, e->message), file, 0));
            break;
        }
        case FC_ERR_RESOLVE: {
            /* ResolverException: every error it carries (one thrown on its own: itself) */
            int i;
            if (e->list) for (i = 0; i < e->list->count; i++) {
                const FcError* x = (const FcError*)e->list->items[i];
                fcListAdd(c, r, fcDiagNew(c, x->line, x->message, FC_SEV_ERROR, fcCodeOf(c, FC_AREA_RESOLVER, x->message), file, 0));
            }
            else fcListAdd(c, r, fcDiagNew(c, e->line, e->message, FC_SEV_ERROR, fcCodeOf(c, FC_AREA_RESOLVER, e->message), file, 0));
            break;
        }
        case FC_ERR_TYPECHECK: {
            int i;
            for (i = 0; i < e->list->count; i++) {
                FcDiagnostic* d = (FcDiagnostic*)fcAlloc(c, sizeof *d);
                *d = *(const FcDiagnostic*)e->list->items[i];
                if (!d->file) d->file = file;
                fcListAdd(c, r, d);
            }
            break;
        }
        case FC_ERR_LINKER:
            fcListAdd(c, r, fcDiagNew(c, 1, e->message, FC_SEV_ERROR, fcCodeOf(c, FC_AREA_LINKER, e->message), file, 0));
            break;
        default:
            fcListAdd(c, r, fcDiagNew(c, 1, fcFmt(c, "internal: %s", e->message ? e->message : "?"), FC_SEV_ERROR, "E9999", file, 0));
            break;
    }
    return r;
}
