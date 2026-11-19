/* native/forge/forge.c - the program forge-c: one file that includes the whole frontend in C (docs/spec/FORGE_C.md).
 *
 *   cc -O2 native/forge/forge.c -o forge-c -lm
 *
 *   forge-c tokens <file.fire>                      the tokens of the preprocessed source (and the diagnostics)
 *   forge-c ast <file.fire>                         the syntax tree (and the diagnostics)
 *   forge-c tokens|ast --list <files.txt> --out <folder>   one output per line of the list: <folder>/<n>.txt
 *
 * The output is byte for byte what `forge tokens` / `forge ast` of the C# compiler prints (src/fire.Compiler/FrontendDump.cs);
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
#ifndef FC_TOKENS_ONLY
#include "units.c"
#include "ast.h"
#include "ast.c"
#include "parser.c"
#endif

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

typedef struct FcDumpJob { const char* command; const char* path; FcBuf* out; } FcDumpJob;

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
#ifndef FC_TOKENS_ONLY
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
#endif
    }
}

/* one file: what FrontendDump.Dump writes (on an exception of the frontend only the diagnostics after what was written before) */
static int fcDump(const char* command, const char* path, FILE* out) {
    FcCtx ctx;
    FcBuf o = {0};
    FcDumpJob job;
    volatile int result = 0;
    fcCtxInit(&ctx);
    fcBufStr(&o, "");
    job.command = command; job.path = path; job.out = &o;
    FC_TRY(&ctx) {
        fcDumpRun(&ctx, &job);
    } FC_CATCH(&ctx) {
        FcList* diags = fcDiagnosticsOf(&ctx, &ctx.err, NULL);
        int i;
        if (strcmp(command, "ast") == 0) o.len = 0, o.p[0] = 0;   /* (the tree is written only when the parse is done) */
        for (i = 0; i < diags->count; i++) fcDiagLogLine(&o, (const FcDiagnostic*)diags->items[i]);
        result = 1;
    } FC_END_TRY;
    fwrite(o.p, 1, o.len, out);
    fcBufFree(&o);
    fcCtxFree(&ctx);
    return result;
}

int main(int argc, char** argv) {
    if (argc == 3 && (strcmp(argv[1], "tokens") == 0 || strcmp(argv[1], "ast") == 0)) return fcDump(argv[1], argv[2], stdout);
    if (argc == 6 && (strcmp(argv[1], "tokens") == 0 || strcmp(argv[1], "ast") == 0) && strcmp(argv[2], "--list") == 0 && strcmp(argv[4], "--out") == 0) {
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
                if (f) { fcDump(argv[1], p, f); fclose(f); }
            }
            if (!e) break;
            p = e + 1;
        }
        free(list);
        return 0;
    }
    fprintf(stderr, "usage: forge-c tokens|ast <file.fire> | forge-c tokens|ast --list <files.txt> --out <folder>\n");
    return 2;
}
