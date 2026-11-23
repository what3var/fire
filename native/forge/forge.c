/* native/forge/forge.c - the program forge-c: one file that includes the whole frontend in C (docs/spec/FORGE_C.md).
 *
 *   cc -O2 native/forge/forge.c -o forge-c -lm      (gcc, clang; -Wall -Wextra without warnings; libm: Math.Pow of a unit conversion)
 *   x86_64-w64-mingw32-gcc -O2 native/forge/forge.c -o forge-c.exe -static   (Windows as the host: win32.h; MSVC: cl /O2 native\forge\forge.c)
 *
 *   forge-c tokens <file.fire>                      the tokens of the preprocessed source (and the diagnostics)
 *   forge-c ast <file.fire>                         the syntax tree (and the diagnostics)
 *   forge-c diagnostics <file.fire>                 what `forge run` writes to stderr while it compiles, up to the type check (Phase C 3;
 *                                                   the prelude from FIRE_TABLES, the installed packages from FIRE_PACKAGES)
 *   forge-c tokens|ast|diagnostics --list <files.txt> --out <folder>   one output per line of the list: <folder>/<n>.txt
 *   forge-c ui|biscuit|fdata <file>                 the script generated from a markup file (Phase C 5), as `forge ui|biscuit|fdata`
 *   forge-c falstad <circuit.txt>                   a circuit of Falstad's simulator as a schematic, as `forge falstad`
 *   forge-c markup --list <files.txt> --out <folder>   per file what forge writes for it (both streams; the command by the extension)
 *   forge-c build|publish <project|solution> ...    projects and solutions, the package file .fpk (Phase D 3, project/; -lpthread)
 *   forge-c projectplan <project|solution>          the model of a project or solution, as `forge projectplan` (docs/spec/FORGE_C.md)
 *   forge-c fpk <file.fpk>                          a package as it is read, as `forge fpk`
 *
 * The output is byte for byte what `forge tokens` / `forge ast` / `forge diagnostics` of the C# compiler prints (src/fire.Compiler/FrontendDump.cs);
 * tests/compare-frontend.sh compares the two over the corpus. */
#define FIRE_BASE_IMPLEMENTATION
#include "forge.h"
#ifdef _WIN32
#include "win32.h"   /* (Windows as the host: what the builder takes from POSIX, in terms of Win32 - Phase D 2/3) */
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
#include "programread.c"   /* ProgramFile.Read (`dump <program.fbc>`, Phase D 4) */

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
/* the generator bytecode -> C (Phase D 1, gen/) */
static bool fcStrictTag(const char* path);
#include "gen/gen.h"
#include "gen/support.c"
#include "gen/packages.c"
#include "gen/cppgenerator.c"
#include "gen/cppgenerator2.c"
#include "gen/cppgenerator3.c"
#include "gen/packed.c"
#include "gen/cabi.c"
#include "gen/overloads.c"
#include "gen/selectors.c"
#include "gen/json.c"
#include "gen/misc.c"
#include "gen/reflection.c"
#include "gen/debug.c"
#include "gen/trace.c"      /* stack traces (CppGenerator.Trace.cs) */
#include "gen/split.c"
#include "gen/stats.c"
#include "gen/cppgenerator4.c"
#include "gen/cppgenerator5.c"
#include "gen/cppgenerator6.c"
#include "gen/cppgenerator7.c"
#include "gen/cppgenerator8.c"
#include "gen/modules.c"      /* the library modules (linkage static/shared/plugin, FIRE_MODULES=1) */
#include "gen/moddispatch.c"
#include "gen/staticlinkage.c"
#include "gen/plugins.c"
#include "gen/native.c"

/* the native builder (Phase D 2, build/): POSIX, or Windows through win32.h */
#ifndef _WIN32
#include <dirent.h>
#include <errno.h>
#include <poll.h>
#include <pthread.h>
#include <sys/time.h>
#include <sys/wait.h>
#endif
#include "build/toolchain.c"
#include "build/builder.c"
#include "build/linkage.c"   /* linkage static/shared/plugin */
#include "build/stats.c"
#include "build/command.c"
#include "build/pkglib.c"
#include "build/provider.c"
#include "build/peres.c"
#include "build/bootimage.c"
#include "build/tools.c"
#include "build/cvm.c"   /* the engine vm (Phase D 3) */
#include "build/plugin.c"   /* linkage plugin (after pkglib.c and cvm.c) */
#include "build/embedlib.c"   /* libforge and libfirevm as shared libraries: `build-embed`, the packages forge and script (Phase D 7) */

/* the markup generators (Phase C 5) */
#include "markupmodel.c"
#include "markupschema.c"
#include "xml.c"
#include "json.c"
#include "markupparser.c"
#include "fireuigenerator.c"
#include "biscuitgenerator.c"
#include "datamodelgenerator.c"
#include "falstadimport.c"

/* the model converter (Phase D 4, model/): glTF, .fblocks, .fmesh -> .f3d, after json.c */
#include <sys/stat.h>
#include "model/modelconverter.c"
#include "model/blockmodel.c"
#include "model/meshmodel.c"
#include "model/command.c"

/* projects and solutions, the package file (Phase D 3, project/), with the builder */
#ifndef _WIN32
#include <fcntl.h>
#endif
#include "project/projectmodel.c"
#include "project/projectfiles.c"
#include "project/manifest.c"
#include "project/workspace.c"
#include "project/linkplan.c"
#include "project/fpk.c"
#include "project/command.c"
#include "project/publisher.c"
#include "project/cli.c"
#ifndef _WIN32
#include <sys/file.h>
#endif
#include "project/standard.c"   /* the standard packages installed from Source/ (Phase D 3) */
/* the rest of the command line (Phase D 4, tools/): `forge test` */
#include "tools/testdriver.c"
#include "tools/testrunner.c"
#include "tools/commands.c"   /* the rest of the command line: run, bytecode of projects, ... (Phase D 4) */
#include "tools/report.c"     /* build|publish|bytecode --report json (Phase D 4) */
#include "tools/setup.c"      /* the toolchains forge downloads (Phase D 5) */

/* `ui|biscuit|fdata <file>` (Program.cs: MarkupFiles.GenerateScript, the script on stdout, the diagnostics of a MarkupException on stderr) and
 * `falstad <file>` (FalstadImport.Convert: the warnings on stderr, the schematic on stdout); `out` and `err` get what forge writes there */
static int fcMarkupRun(const char* command, const char* path, FcBuf* out, FcBuf* err) {
    FcCtx ctx;
    FcU16 text = {0};
    volatile int result = 0;
    int i;
    fcCtxInit(&ctx);
    if (!fcReadText(path, &text)) {
        fcBufFmt(err, "Could not find file '%s'.\n", path);
        fcCtxFree(&ctx);
        return 1;
    }
    FC_TRY(&ctx) {
        if (strcmp(command, "falstad") == 0) {
            FmFalstadResult r = fmFalstadConvert(&ctx, fc8FromU16(&ctx, text.p, text.len), "");
            for (i = 0; i < r.warnings->count; i++) { fcOut8(err, fcBaseName(path)); fcBufStr(err, ": "); fcOut8(err, (const char*)r.warnings->items[i]); fcBufChar(err, '\n'); }
            fcOut8(out, r.biscuit);
        } else fcOut8(out, fmGenerateScript(&ctx, path, text.p, text.len));
    } FC_CATCH(&ctx) {
        if (ctx.err.kind == FC_ERR_MARKUP) {
            for (i = 0; i < ctx.err.list->count; i++) {
                fcOut8(err, fcBaseName(path));
                fcBufStr(err, ": ");
                fcOut8(err, fmDiagText(&ctx, (const FmDiag*)ctx.err.list->items[i]));
                fcBufChar(err, '\n');
            }
        } else {
            /* (an exception C# does not catch there: its first line - "Unhandled exception. ..." - without the stack) */
            if (!ctx.err.message || !fcStartsWith(ctx.err.message, "Unhandled exception.")) fcBufStr(err, "internal: ");
            fcOut8(err, ctx.err.message ? ctx.err.message : "?");
            fcBufChar(err, '\n');
        }
        result = 1;
    } FC_END_TRY;
    fcU16Free(&text);
    fcCtxFree(&ctx);
    return result;
}
static bool fcIsMarkupCommand(const char* s) { return strcmp(s, "ui") == 0 || strcmp(s, "biscuit") == 0 || strcmp(s, "fdata") == 0 || strcmp(s, "falstad") == 0; }

/* Program.cs: `-o <file>` of ui, biscuit, fdata, falstad - File.WriteAllText; its IOException is the message and exit 1 (Phase D 4) */
static void tcWriteAllText(const char* path, FcBuf* text, FcBuf* err, int* result) {
    FILE* f = fopen(path, "wb");
    FcCtx scratch;
    const char* full;
    fcCtxInit(&scratch);
    full = fcFullPath(&scratch, ".", path);
    if (!f) {
        if (errno == EACCES || errno == EISDIR || errno == EPERM || errno == EROFS) {
            /* (UnauthorizedAccessException is no IOException: the C# forge ends with it unhandled) */
            fcBufFmt(err, "Unhandled exception. System.UnauthorizedAccessException: Access to the path '%s' is denied.\n", full);
            *result = 134;
        } else {
            fcBufFmt(err, "Could not find a part of the path '%s'.\n", full);
            *result = 1;
        }
        fcCtxFree(&scratch);
        return;
    }
    fwrite(text->p, 1, text->len, f);
    fclose(f);
    fcCtxFree(&scratch);
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
    if (strncmp(job->command, "cgen:", 5) == 0) {
        /* `cgen <options>`: the C `forge native <file>` writes (Phase D 1, gen/native.c) */
        fgDumpNative(c, job->command + 5, job->path, job->prelude, job->store, job->out);
        return;
    }
    if (strncmp(job->command, "buildplan:", 10) == 0) {
        /* `buildplan <options>`: what a native build of the file would run (Phase D 2, build/) */
        nbDumpBuildPlan(c, job->command + 10, job->path, job->prelude, job->store, job->out);
        return;
    }
    if (strncmp(job->command, "fbc:", 4) == 0) {
        /* `fbc <mode>`: the program file `forge bytecode <file> -m <mode>` writes */
        const char* m = job->command + 4;
        int mode = strcmp(m, "debug") == 0 ? 0 : strcmp(m, "release") == 0 ? 1 : strcmp(m, "performance") == 0 ? 2 : -1;
        FcLinked* linked;
        c->collecting = true;   /* (the warnings: `forge-c bytecode` prints them as forge does) */
        if (pjIsProjectFile(job->path)) linked = pjLinkProjectFile(c, job->path, job->prelude, job->store, mode, strcmp(m, "native") == 0);   /* (a project, Phase D 3) */
        else
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
        if ((strncmp(command, "fbc:", 4) == 0 || strncmp(command, "cgen:", 5) == 0 || strncmp(command, "buildplan:", 10) == 0) && ctx.err.kind == FC_ERR_INTERNAL && ctx.err.message
            && fcStartsWith(ctx.err.message, "unhandled: ")) {
            /* (an exception of .NET the C# code does not catch - a model resource, Phase D 4: `internal: <type>: <message>`) */
            o.len = 0; fcBufStr(&o, "internal: "); fcOut8(&o, ctx.err.message + 11); fcBufChar(&o, '\n');
            fwrite(o.p, 1, o.len, out); fcBufFree(&o); fcCtxFree(&ctx);
            return 1;
        }
        if (strncmp(command, "cgen:", 5) == 0 || strncmp(command, "buildplan:", 10) == 0) {
            /* (FrontendDump.DumpNative: a program the backend refuses, a part of the generator in C that is not ported yet, a compile error) */
            o.len = 0;
            if (ctx.err.kind == FC_ERR_NATIVE) { fcBufStr(&o, "unsupported: "); fcOut8(&o, ctx.err.message); fcBufChar(&o, '\n'); diags = fcListNew(&ctx); }
            else if (ctx.err.kind == FC_ERR_INTERNAL && ctx.err.message && fcStartsWith(ctx.err.message, "unported: ")) { fcOut8(&o, ctx.err.message); fcBufChar(&o, '\n'); diags = fcListNew(&ctx); }
            else if (ctx.err.kind == FC_ERR_INTERNAL && ctx.err.message && fcStartsWith(ctx.err.message, "native-config: ")) {
                /* (a NativeConfigException of the builder: what FrontendDump prints for an exception that is no compile error) */
                fcBufStr(&o, "internal: NativeConfigException: "); fcOut8(&o, ctx.err.message + 15); fcBufChar(&o, '\n'); diags = fcListNew(&ctx);
            }
            else fcBufStr(&o, "error\n");
        }
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

static bool fcIsCommand(const char* s) { return strcmp(s, "tokens") == 0 || strcmp(s, "ast") == 0 || strcmp(s, "diagnostics") == 0 || strncmp(s, "fbc:", 4) == 0 || strncmp(s, "cgen:", 5) == 0 || strncmp(s, "buildplan:", 10) == 0; }

static int fcCliMain(int argc, char** argv, const char* prelude, const char* store);

/* forge.c as a library (FIRE_FORGE_LIBRARY: libforge, docs/spec/EMBED.md) has no main: fire_embed.h is its interface (embed.c, at the end). */
#ifndef FIRE_FORGE_LIBRARY
int main(int argc, char** argv) {
    char preludeBuf[4096], storeBuf[4096];
#ifdef _WIN32
    char selfBuf[4096];
    const char* self = fwSelfPath(selfBuf, sizeof selfBuf);   /* (the program with '/': argv[0] may have neither its folder nor .exe) */
    const char* prelude = fcFindPrelude(self ? self : argv[0], preludeBuf, sizeof preludeBuf);
    const char* store = fcFindStore(self ? self : argv[0], storeBuf, sizeof storeBuf);
#else
    const char* self = argv[0];
    const char* prelude = fcFindPrelude(argv[0], preludeBuf, sizeof preludeBuf);
    const char* store = fcFindStore(argv[0], storeBuf, sizeof storeBuf);
#endif
#ifdef _WIN32
    fwSlashes(preludeBuf);
    fwSlashes(storeBuf);
    _setmode(_fileno(stdout), _O_BINARY);   /* (the output byte for byte as on Linux: no CR before LF) */
    _setmode(_fileno(stderr), _O_BINARY);
#endif
    {
        /* (the builder, Phase D 2: Toolchain/ next to the program - AppContext.BaseDirectory -, the files of the runtime in native/ next to the tables) */
        char exeBuf[4096], tablesBuf[4096];
        const char* root = getenv("FIRE_NATIVE_ROOT");
        const char* slash;
        if (realpath(self ? self : argv[0], exeBuf) && (slash = strrchr(exeBuf, '/')) != NULL) { exeBuf[slash - exeBuf] = 0; nbBaseDirectory = strdup(exeBuf); }
        if (root && *root) nbNativeRoot = root;
        else {
            snprintf(tablesBuf, sizeof tablesBuf, "%s", prelude);
            if ((slash = strrchr(tablesBuf, '/')) != NULL) { tablesBuf[slash - tablesBuf] = 0; strncat(tablesBuf, "/..", sizeof tablesBuf - strlen(tablesBuf) - 1); }
            else snprintf(tablesBuf, sizeof tablesBuf, "..");
            if (realpath(tablesBuf, exeBuf)) nbNativeRoot = strdup(exeBuf);
        }
    }
    return fcCliMain(argc, argv, prelude, store);
}
#endif

/* The command line (fire_forge_main of the library, native/forge/embed.c): the prelude and the store found, the folders of the builder set. */
static int fcCliMain(int argc, char** argv, const char* prelude, const char* store) {
    char fbcCommand[256];
    nbProjectBuildPlan = pjProjectBuildPlanHook;   /* (buildplan of a project, Phase D 3) */
    { int r = tcMain(argc, argv, prelude, store); if (r >= 0) return r; }   /* Phase D 4: the rest of the command line, the report (tools/) */
    /* (Program.cs: the standard packages of Source/ next to the program packed and installed when they are missing or changed - project/standard.c) */
    if (argc >= 2 && strcmp(argv[1], "install-standard") == 0) { nsEnsureInstalled(prelude, store, stdout); return 0; }
    if (argc >= 2 && (strcmp(argv[1], "build") == 0 || strcmp(argv[1], "publish") == 0 || strcmp(argv[1], "bytecode") == 0)) nsEnsureInstalled(prelude, store, stderr);
    if (argc >= 2 && strcmp(argv[1], "build") == 0) {
        int i;
        for (i = 2; i < argc; i++) if (argv[i][0] != '-' && pjIsProjectFile(argv[i])) return pjBuildCommand(argc, argv, prelude, store);   /* Phase D 3 */
        return nbBuildMain(argc, argv, prelude, store);   /* Phase D 2 */
    }
    if (argc >= 2 && strcmp(argv[1], "publish") == 0) return pjPublishCommand(argc, argv, prelude, store);   /* Phase D 3 */
    if (argc >= 2 && strcmp(argv[1], "build-vm") == 0) return nvBuildVmMain(argc, argv, store);
    if (argc >= 2 && strcmp(argv[1], "build-embed") == 0) return neBuildEmbedMain(argc, argv);   /* Phase D 7: libforge and libfirevm (build/embedlib.c) */
    if (argc >= 2 && strcmp(argv[1], "toolchain-setup") == 0) return tsMain(argc, argv);   /* Phase D 5: a portable toolchain downloaded (tools/setup.c) */   /* Phase D 3: the VM in C into a folder (build/cvm.c) */
    if (argc == 3 && strcmp(argv[1], "projectplan") == 0) return pjProjectPlanDump(argv[2], stdout);
    if (argc == 3 && strcmp(argv[1], "fpk") == 0) return pjPackageDump(argv[2], stdout);
    { int r = nbToolsMain(argc, argv, prelude, store); if (r >= 0) return r; }   /* Phase D 2: pkglib and the measures of the tools (build/tools.c) */
    if (argc >= 3 && strcmp(argv[1], "model") == 0) return mdModelCommand(argc, argv);   /* Phase D 4: `forge model` (model/command.c) */
    /* `fbc <mode> ...`: the mode is part of the command */
    if (argc >= 4 && (strcmp(argv[1], "fbc") == 0 || strcmp(argv[1], "cgen") == 0 || strcmp(argv[1], "buildplan") == 0) && strlen(argv[2]) < 200) {
        snprintf(fbcCommand, sizeof fbcCommand, "%s:%s", argv[1], argv[2]);
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
    if (argc >= 3 && fcIsMarkupCommand(argv[1]) && strcmp(argv[2], "--list") != 0) {
        FcBuf out = {0}, err = {0};
        int result, i;
        const char* to = NULL;   /* (Program.cs: `-o <file>` anywhere after the file; without it to the output) */
        for (i = 3; i + 1 < argc; i++) if (strcmp(argv[i], "-o") == 0) { to = argv[i + 1]; break; }
        fcBufStr(&out, "");
        fcBufStr(&err, "");
        result = fcMarkupRun(argv[1], argv[2], &out, &err);
        if (result == 0 && to) { tcWriteAllText(to, &out, &err, &result); out.len = 0; }
        fwrite(err.p, 1, err.len, stderr);
        fwrite(out.p, 1, out.len, stdout);
        fcBufFree(&out);
        fcBufFree(&err);
        return result;
    }
    /* `markup --list <files.txt> --out <folder>`: per file what `forge ui|biscuit|fdata|falstad <file> 2>&1` writes (the command by the extension:
     * .fxml, .biscuit, .fdata, else falstad) - the measure of Phase C 5 */
    if (argc == 6 && (fcIsCommand(argv[1]) || strcmp(argv[1], "markup") == 0) && strcmp(argv[2], "--list") == 0 && strcmp(argv[4], "--out") == 0) {
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
                if (f && strcmp(argv[1], "markup") == 0) {
                    FcBuf out = {0}, err = {0};
                    const char* command = fmIsUiMarkup(p) ? "ui" : fmIsBiscuit(p) ? "biscuit" : fmIsDataModel(p) ? "fdata" : "falstad";
                    fcBufStr(&out, "");
                    fcBufStr(&err, "");
                    fcMarkupRun(command, p, &out, &err);
                    fwrite(err.p, 1, err.len, f);
                    fwrite(out.p, 1, out.len, f);
                    fcBufFree(&out);
                    fcBufFree(&err);
                    fclose(f);
                } else if (f) { fcDump(argv[1], p, f, prelude, store); fclose(f); }
            }
            if (!e) break;
            p = e + 1;
        }
        free(list);
        return 0;
    }
    if (argc >= 2) return tcUnknownCommand(argc, argv);   /* (CommandLineParser.Parse: "Unknown command ..." with the usage, Phase D 4) */
    fprintf(stderr, "usage: forge-c tokens|ast|diagnostics <file.fire> | forge-c tokens|ast|diagnostics --list <files.txt> --out <folder>\n");
    return 2;
}

/* libforge: the interface of native/abi/fire_embed.h (docs/spec/EMBED.md) */
#include "embed.c"
