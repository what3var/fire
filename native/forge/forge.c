/* native/forge/forge.c - the program forge-c: one file that includes the whole frontend in C (docs/spec/FORGE_C.md).
 *
 *   cc -O2 native/forge/forge.c -o forge-c -lm      (gcc, clang; -Wall -Wextra without warnings; libm: Math.Pow of a unit conversion)
 *
 *   forge-c tokens <file.fire>                      the tokens of the preprocessed source (and the diagnostics)
 *   forge-c ast <file.fire>                         the syntax tree (and the diagnostics)
 *   forge-c diagnostics <file.fire>                 what `forge run` writes to stderr while it compiles, up to the type check (Phase C 3;
 *                                                   the prelude from FIRE_TABLES, the installed packages from FIRE_PACKAGES)
 *   forge-c tokens|ast|diagnostics --list <files.txt> --out <folder>   one output per line of the list: <folder>/<n>.txt
 *
 * The output is byte for byte what `forge tokens` / `forge ast` / `forge diagnostics` of the C# compiler prints (src/fire.Compiler/FrontendDump.cs);
 * tests/compare-frontend.sh compares the two over the corpus. */
#define FIRE_BASE_IMPLEMENTATION
#include "forge.h"
#ifdef _WIN32
#include <direct.h>
#else
#include <unistd.h>
#include <sys/stat.h>
#endif

#include "util.c"
#include "diag.c"
#include "lexer.h"
#include "lexer.c"
#include "conditional.c"
#include "preprocessor.c"
#include "units.c"
#include "unitv.c"
#include "ast.h"
#include "ast.c"
#include "parser.c"
#include "dict.c"
#include "typeref.c"
#include "resolver.c"
#include "typechecker.c"
#include "bytecode.c"
#include "compiler.c"
#include "compiler2.c"
#include "compilertrees.c"
#include "programfile.c"

void fbOutOfMemory(void) {
    fputs("forge-c: out of memory\n", stderr);
    abort();
}

static const char* fcBaseName(const char* path) {
    const char* last = path;
    const char* p;
    for (p = path; *p; p++) if (*p == '/' || *p == '\\') last = p + 1;
    return last;
}
static int fcCmpOrdinal(const void* a, const void* b) { return strcmp(*(const char* const*)a, *(const char* const*)b); }

#include "linker.c"

/* FrontendDump.Token */
static void fcDumpToken(FcBuf* o, const FcToken* t) {
    int i;
    fcBufFmt(o, "%d:%d %s ", t->line, t->column, fcTokenTypeName(t->type));
    fcEsc(o, t->lexeme);
    fcBufFmt(o, " len=%d", t->length);
    if (t->newlineBefore) fcBufStr(o, " nl");
    if (t->unitSuffix && t->unitSuffix[0]) { fcBufStr(o, " unit="); fcEsc(o, t->unitSuffix); }
    switch (t->literal) {
        case FC_LIT_INT: fcBufFmt(o, " int=%" PRId64, t->intValue); break;
        case FC_LIT_FLOAT: { uint64_t bits; memcpy(&bits, &t->floatValue, 8); fcBufFmt(o, " float=0x%016" PRIX64, bits); break; }
        case FC_LIT_STRING: case FC_LIT_CHAR: fcBufStr(o, " value="); fcEsc(o, t->stringValue); break;
        case FC_LIT_SEGMENTS:
            fcBufFmt(o, " segments=%d", t->segments->count);
            for (i = 0; i < t->segments->count; i++) {
                const FcSegment* s = (const FcSegment*)t->segments->items[i];
                fcBufStr(o, s->isExpression ? " E" : " T");
                fcEsc(o, s->text);
                if (s->format) { fcBufChar(o, ':'); fcEsc(o, s->format); }
            }
            break;
        default: break;
    }
    fcBufChar(o, '\n');
}

typedef struct FcDumpJob { const char* command; const char* path; FcBuf* out; const char* prelude; const char* store; } FcDumpJob;

/* `//# types: strict` among the last 12 lines of the file (as tests/run-cases.sh runs it) */
static bool fcStrictTag(const char* path) {
    size_t len = 0;
    uint8_t* text = fbReadFile(path, &len);
    int lines = 0;
    size_t i, end;
    bool strict = false;
    if (!text) return false;
    end = len;
    if (end > 0 && text[end - 1] == '\n') end--;
    for (i = end; ; ) {
        size_t start = i;
        size_t a, b;
        while (start > 0 && text[start - 1] != '\n') start--;
        a = start; b = i;
        while (a < b && (text[a] == ' ' || text[a] == '\t' || text[a] == '\r')) a++;
        while (b > a && (text[b - 1] == ' ' || text[b - 1] == '\t' || text[b - 1] == '\r')) b--;
        if (b - a == 17 && memcmp(text + a, "//# types: strict", 17) == 0) strict = true;
        if (++lines >= 12 || start == 0) break;
        i = start - 1;
    }
    free(text);
    return strict;
}

static void fcDumpRun(FcCtx* c, FcDumpJob* job) {
    FcU16 text = {0};
    const uint16_t* source;
    FcProcessed* processed;
    char* full;
    int i, n;
    if (!fcReadText(job->path, &text)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "cannot read '%s'", job->path));
    n = text.len;
    source = (const uint16_t*)fcAlloc(c, sizeof(uint16_t) * (size_t)(n + 1));
    if (n) memcpy((void*)source, text.p, sizeof(uint16_t) * (size_t)n);
    fcU16Free(&text);
    if (strncmp(job->command, "fbc:", 4) == 0) {
        /* `fbc <mode>`: the program file `forge bytecode <file> -m <mode>` writes */
        const char* m = job->command + 4;
        int mode = strcmp(m, "debug") == 0 ? 0 : strcmp(m, "release") == 0 ? 1 : strcmp(m, "performance") == 0 ? 2 : -1;
        FcLinked* linked;
        c->collecting = true;   /* (the warnings: `forge-c bytecode` prints them as forge does) */
        linked = fcLinkFile(c, job->path, job->prelude, job->store, fcStrictTag(job->path), mode);
        fcProgramFileWrite(c, linked, true, job->out);
        return;
    }
    if (strcmp(job->command, "diagnostics") == 0) {
        fcU16Free(&text);
        c->collecting = true;
        fcCompileFrontend(c, job->path, job->prelude, job->store, fcStrictTag(job->path));
        for (i = 0; i < c->diags->count; i++) fcDiagLogLine(job->out, (const FcDiagnostic*)c->diags->items[i]);
        return;
    }
    fcSymbolsForHost(c, c->symbols);
    full = (char*)fcFullPath(c, ".", job->path);
    processed = fcPreprocess(c, source, n, fcDirectoryName(c, full));
    processed->name = fcStr(c, fcBaseName(job->path));
    if (strcmp(job->command, "tokens") == 0) {
        FcTokens tokens;
        for (i = 0; i < processed->usings->count; i++) { fcBufStr(job->out, "using "); fcEsc(job->out, (const char*)processed->usings->items[i]); fcBufChar(job->out, '\n'); }
        tokens = fcTokenize(c, processed->source, processed->length);
        for (i = 0; i < tokens.count; i++) fcDumpToken(job->out, &tokens.items[i]);
    } else {
        FcList* program;
        FcBuf tree = {0};
        c->collecting = true;
        program = fcParseMultiple(c, &processed, 1);
        c->collecting = false;
        fcBufStr(&tree, "");
        fcDumpProgram(c, &tree, program);
        fcBufPut(job->out, tree.p, tree.len);
        fcBufFree(&tree);
        for (i = 0; i < c->diags->count; i++) fcDiagLogLine(job->out, (const FcDiagnostic*)c->diags->items[i]);
        for (i = 0; i < c->declared->count; i++) fcDumpDeclared(job->out, (const FcDeclaredWarning*)c->declared->items[i]);
    }
}

/* one file: what FrontendDump.Dump writes (on an exception of the frontend only the diagnostics after what was written before) */
static int fcDump(const char* command, const char* path, FILE* out, const char* prelude, const char* store) {
    FcCtx ctx;
    FcBuf o = {0};
    FcDumpJob job;
    volatile int result = 0;
    fcCtxInit(&ctx);
    fcBufStr(&o, "");
    job.command = command; job.path = path; job.out = &o; job.prelude = prelude; job.store = store;
    FC_TRY(&ctx) {
        fcDumpRun(&ctx, &job);
    } FC_CATCH(&ctx) {
        FcList* diags = fcDiagnosticsOf(&ctx, &ctx.err, NULL);
        int i;
        if (strcmp(command, "ast") == 0) o.len = 0, o.p[0] = 0;   /* (the tree is written only when the parse is done) */
        if (strcmp(command, "diagnostics") == 0) { o.len = 0; o.p[0] = 0; for (i = 0; i < ctx.diags->count; i++) fcDiagLogLine(&o, (const FcDiagnostic*)ctx.diags->items[i]); }
        if (strncmp(command, "fbc:", 4) == 0) { o.len = 0; fcBufStr(&o, "error\n"); }
        for (i = 0; i < diags->count; i++) fcDiagLogLine(&o, (const FcDiagnostic*)diags->items[i]);
        result = 1;
    } FC_END_TRY;
    fwrite(o.p, 1, o.len, out);
    fcBufFree(&o);
    fcCtxFree(&ctx);
    return result;
}

/* where the prelude (native/tables/prelude.fire) and the installed packages are: FIRE_TABLES / FIRE_PACKAGES, else next to the program
 * (<dir>/../tables, <dir>/Packages), else native/tables of the working directory */
static const char* fcFindPrelude(const char* exe, char* buf, size_t n) {
    const char* env = getenv("FIRE_TABLES");
    const char* slash = strrchr(exe, '/');
    FILE* f;
    if (env) { snprintf(buf, n, "%s/prelude.fire", env); return buf; }
    if (slash) {
        snprintf(buf, n, "%.*s/../tables/prelude.fire", (int)(slash - exe), exe);
        if ((f = fopen(buf, "rb")) != NULL) { fclose(f); return buf; }
    }
    snprintf(buf, n, "native/tables/prelude.fire");
    return buf;
}
static const char* fcFindStore(const char* exe, char* buf, size_t n) {
    const char* env = getenv("FIRE_PACKAGES");
    const char* slash = strrchr(exe, '/');
    if (env) { snprintf(buf, n, "%s", env); return buf; }
    if (slash) snprintf(buf, n, "%.*s/Packages", (int)(slash - exe), exe); else snprintf(buf, n, "Packages");
    return buf;
}

/* forge serve: the language services (docs/spec/FORGE_SERVE.md) - after everything they use, the markup schema included */
#include "serve.c"

static bool fcIsCommand(const char* s) { return strcmp(s, "tokens") == 0 || strcmp(s, "ast") == 0 || strcmp(s, "diagnostics") == 0 || strncmp(s, "fbc:", 4) == 0; }

int main(int argc, char** argv) {
    char preludeBuf[4096], storeBuf[4096];
    const char* prelude = fcFindPrelude(argv[0], preludeBuf, sizeof preludeBuf);
    const char* store = fcFindStore(argv[0], storeBuf, sizeof storeBuf);
    char fbcCommand[64];
    /* `fbc <mode> ...`: the mode is part of the command */
    if (argc >= 4 && strcmp(argv[1], "fbc") == 0 && strlen(argv[2]) < 32) {
        snprintf(fbcCommand, sizeof fbcCommand, "fbc:%s", argv[2]);
        argv[2] = fbcCommand;
        argv++;
        argc--;
    }
    /* `bytecode <file> -o <out> [-m MODE]`: the program file as forge bytecode writes it */
    if (argc >= 5 && strcmp(argv[1], "bytecode") == 0) {
        const char* file = NULL;
        const char* out = NULL;
        const char* mode = "default";
        int i;
        FILE* f;
        for (i = 2; i < argc; i++) {
            if (strcmp(argv[i], "-o") == 0 && i + 1 < argc) out = argv[++i];
            else if (strcmp(argv[i], "-m") == 0 && i + 1 < argc) { i++; mode = fcAsciiIEq(argv[i], "debug") ? "debug" : fcAsciiIEq(argv[i], "performance") ? "performance" : "release"; }
            else if (strcmp(argv[i], "--types") == 0 && i + 1 < argc) i++;
            else file = argv[i];
        }
        if (!file || !out) { fprintf(stderr, "usage: forge-c bytecode <file.fire> -o <file.fbc> [-m DEBUG|RELEASE|PERFORMANCE]\n"); return 2; }
        snprintf(fbcCommand, sizeof fbcCommand, "fbc:%s", mode);
        {
            FcCtx ctx;
            FcBuf o = {0};
            FcDumpJob job;
            volatile int result = 0;
            fcCtxInit(&ctx);
            fcBufStr(&o, "");
            job.command = fbcCommand; job.path = file; job.out = &o; job.prelude = prelude; job.store = store;
            FC_TRY(&ctx) {
                fcDumpRun(&ctx, &job);
            } FC_CATCH(&ctx) {
                result = 1;
            } FC_END_TRY;
            {
                /* the warnings as they came, then the errors of a failed compile (what `forge bytecode` writes to stderr) */
                FcBuf e = {0};
                fcBufStr(&e, "");
                for (i = 0; i < ctx.diags->count; i++) fcDiagLogLine(&e, (const FcDiagnostic*)ctx.diags->items[i]);
                if (result) {
                    FcList* diags = fcDiagnosticsOf(&ctx, &ctx.err, NULL);
                    for (i = 0; i < diags->count; i++) fcDiagLogLine(&e, (const FcDiagnostic*)diags->items[i]);
                }
                fwrite(e.p, 1, e.len, stderr);
                fcBufFree(&e);
            }
            if (result == 0) {
                f = fopen(out, "wb");
                if (!f) { fprintf(stderr, "forge-c: cannot write %s\n", out); result = 1; }
                else { fwrite(o.p, 1, o.len, f); fclose(f); printf("%s (%zu bytes)\n", fcFullPath(&ctx, ".", out), o.len); }
            }
            fcBufFree(&o);
            fcCtxFree(&ctx);
            return result;
        }
    }
    if (argc >= 2 && strcmp(argv[1], "serve") == 0) return svServe(argc, argv, prelude, store);   /* docs/spec/FORGE_SERVE.md */
    if (argc == 3 && fcIsCommand(argv[1])) return fcDump(argv[1], argv[2], stdout, prelude, store);
    if (argc == 6 && fcIsCommand(argv[1]) && strcmp(argv[2], "--list") == 0 && strcmp(argv[4], "--out") == 0) {
        size_t len = 0;
        uint8_t* list = fbReadFile(argv[3], &len);
        char* p;
        int n = 0;
        if (!list) { fprintf(stderr, "forge-c: cannot read %s\n", argv[3]); return 2; }
#ifdef _WIN32
        _mkdir(argv[5]);
#else
        mkdir(argv[5], 0777);
#endif
        list = (uint8_t*)realloc(list, len + 1);
        list[len] = 0;
        for (p = (char*)list; *p;) {
            char* e = strchr(p, '\n');
            char outPath[4096];
            FILE* f;
            if (e) *e = 0;
            if (e && e > p && e[-1] == '\r') e[-1] = 0;
            n++;
            if (*p) {
                snprintf(outPath, sizeof outPath, "%s/%d.txt", argv[5], n);
                f = fopen(outPath, "wb");
                if (f) { fcDump(argv[1], p, f, prelude, store); fclose(f); }
            }
            if (!e) break;
            p = e + 1;
        }
        free(list);
        return 0;
    }
    fprintf(stderr, "usage: forge-c tokens|ast|diagnostics <file.fire> | forge-c tokens|ast|diagnostics --list <files.txt> --out <folder>\n");
    return 2;
}
