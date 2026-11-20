/* native/forge/serve.c - `forge-c serve`: the language services as a process (docs/spec/FORGE_SERVE.md, docs/ROADMAP_C_PORT.md L4 and Phase C step 6).
 * One JSON message per line over stdio. Three threads: the reader (stdin -> the queue of messages; a `cancel` marks a queued request), the main
 * thread (the documents, the projects, the answers to the requests - completion and the other questions run here, on the symbol index, in the
 * order of the messages), the analysis thread (the diagnostics: debounced, latest wins, one at a time, pushed as `diagnostics`).
 *
 *   serve_index.c     ScriptSymbolIndex.cs          the symbol index (token based)
 *   serve_types.c     ScriptSymbolIndex.Types.cs    the type derivation; BuiltinMembers.cs
 *   serve_complete.c  CompletionEngine.cs           the suggestions and their order
 *   serve_diag.c      LiveDiagnostics.cs            the analysis on the frontend in C
 *   serve_nav.c       NavigationEngine.cs           hover, signature, definition, references; outline, folding
 *   serve_markup.c    MarkupCompletion.cs           the completion of markup files (when the markup schema is ported)
 */

#ifdef _WIN32
#include <windows.h>
#include <process.h>
typedef CRITICAL_SECTION SvMutex;
typedef CONDITION_VARIABLE SvCond;
static void svMutexInit(SvMutex* m) { InitializeCriticalSection(m); }
static void svLock(SvMutex* m) { EnterCriticalSection(m); }
static void svUnlock(SvMutex* m) { LeaveCriticalSection(m); }
static void svCondInit(SvCond* c) { InitializeConditionVariable(c); }
static void svCondSignal(SvCond* c) { WakeAllConditionVariable(c); }
static void svCondWait(SvCond* c, SvMutex* m, long ms) { SleepConditionVariableCS(c, m, ms < 0 ? INFINITE : (DWORD)ms); }
static int64_t svNowMs(void) { return (int64_t)GetTickCount64(); }
typedef HANDLE SvThread;
static void svThreadStart(SvThread* t, unsigned (__stdcall* fn)(void*), void* arg) { *t = (HANDLE)_beginthreadex(NULL, 0, fn, arg, 0, NULL); }
#define SV_THREAD_FN(name) static unsigned __stdcall name(void* arg)
#define SV_THREAD_RET return 0
#else
#include <pthread.h>
#include <time.h>
#include <sys/time.h>
typedef pthread_mutex_t SvMutex;
typedef pthread_cond_t SvCond;
static void svMutexInit(SvMutex* m) { pthread_mutex_init(m, NULL); }
static void svLock(SvMutex* m) { pthread_mutex_lock(m); }
static void svUnlock(SvMutex* m) { pthread_mutex_unlock(m); }
static void svCondInit(SvCond* c) { pthread_cond_init(c, NULL); }
static void svCondSignal(SvCond* c) { pthread_cond_broadcast(c); }
static void svCondWait(SvCond* c, SvMutex* m, long ms) {
    if (ms < 0) { pthread_cond_wait(c, m); return; }
    {
        struct timeval now;
        struct timespec until;
        gettimeofday(&now, NULL);
        until.tv_sec = now.tv_sec + ms / 1000;
        until.tv_nsec = (long)now.tv_usec * 1000 + (ms % 1000) * 1000000L;
        if (until.tv_nsec >= 1000000000L) { until.tv_sec++; until.tv_nsec -= 1000000000L; }
        pthread_cond_timedwait(c, m, &until);
    }
}
static int64_t svNowMs(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (int64_t)t.tv_sec * 1000 + t.tv_nsec / 1000000; }
typedef pthread_t SvThread;
static void svThreadStart(SvThread* t, void* (*fn)(void*), void* arg) { pthread_create(t, NULL, fn, arg); }
#define SV_THREAD_FN(name) static void* name(void* arg)
#define SV_THREAD_RET return NULL
#endif

/* the markup schema (markupschema.c, Phase C step 5) is part of the program: the completion of markup files, too */
#ifdef FM_P
#define SV_HAVE_MARKUP
#endif

#include "serve_index.c"
#include "serve_types.c"
#include "serve_complete.c"
#include "serve_diag.c"

/* ---- texts shared between the threads (immutable, counted under the server's lock) ---- */
typedef struct SvText {
    int refs; int len; uint64_t hash; uint16_t* p;
    char** imports; int nimports; bool importsRead;   /* its `#import` names (read once, by the main thread only) */
} SvText;
static SvText* svTextNew(const uint16_t* p, int len) {
    SvText* t = (SvText*)malloc(sizeof *t);
    if (!t) fbOutOfMemory();
    t->refs = 1;
    t->len = len;
    t->imports = NULL; t->nimports = 0; t->importsRead = false;
    t->p = (uint16_t*)malloc(sizeof(uint16_t) * (size_t)(len + 1));
    if (!t->p) fbOutOfMemory();
    if (len) memcpy(t->p, p, sizeof(uint16_t) * (size_t)len);
    t->hash = svHash(p, len);
    return t;
}
static void svTextUnref(SvText* t) {
    int i;
    if (!t || --t->refs != 0) return;
    for (i = 0; i < t->nimports; i++) free(t->imports[i]);
    free(t->imports);
    free(t->p);
    free(t);
}
static char* svDup(const char* s);
/* the `#import` names of a text (FindImportNames), remembered with the text */
static void svTextImports(SvText* t, FcCtx* into, FcList* out) {
    int i;
    if (!t->importsRead) {
        FcCtx* c = svNewCtx();
        FcList* names = svFindImportNames(c, t->p, t->len);
        t->imports = (char**)calloc((size_t)names->count + 1, sizeof(char*));
        for (i = 0; i < names->count; i++) t->imports[i] = svDup((const char*)names->items[i]);
        t->nimports = names->count;
        t->importsRead = true;
        svFreeCtx(c);
    }
    for (i = 0; i < t->nimports; i++) fcListAdd(into, out, t->imports[i]);
}
static SvText* svTextRef(SvText* t) { if (t) t->refs++; return t; }

static char* svDup(const char* s) { size_t n; char* r; if (!s) return NULL; n = strlen(s); r = (char*)malloc(n + 1); if (!r) fbOutOfMemory(); memcpy(r, s, n + 1); return r; }

/* ---- documents and projects ---- */
typedef struct SvPFile { char* path; SvText* text; } SvPFile;
typedef struct SvPLib { char** names; int nnames; char* import; char* name; char* ns; char** requires; int nrequires; SvPFile* files; int nfiles; } SvPLib;
typedef struct SvProject { char* key; SvPFile* files; int nfiles; SvPLib* libs; int nlibs; bool strict; char* ownImport; int stamp; } SvProject;

#define SV_REMEMBERED 4

typedef struct SvDoc {
    char* path;
    char* project;      /* NULL: alone */
    char* base;
    bool fxml;
    int version;
    SvText* text;
    SvPFile* includes;  /* generated scripts of `#include`d markup (for the index) */
    int nincludes;
    bool includesSet;   /* the client said what the index knows (else: the project's files and imported libraries) */
    /* the index (main thread) */
    SvIndex* index;
    uint64_t indexKey;
    /* the diagnostics (under the lock) */
    bool pending;       /* an analysis is waiting (for the version of the text then) */
    int64_t seq;        /* the request of the newest analysis (every schedule a new one) */
    int64_t deliveredSeq;   /* the request whose result was sent last */
    int64_t due;
    int64_t touched;
    uint64_t rememberedKey[SV_REMEMBERED];
    char* rememberedItems[SV_REMEMBERED];   /* the `items` JSON of the last analyses (newest first): a tab again, an undo back to a checked text */
} SvDoc;

typedef struct SvMsg { struct SvMsg* next; char* line; int64_t id; bool cancelled; bool eof; } SvMsg;

typedef struct SvServer {
    SvMutex mu;
    SvCond workCond;     /* the analysis thread waits on it */
    SvCond queueCond;    /* the main thread waits for messages */
    SvMutex outMu;
    SvDoc** docs; int ndocs, capDocs;
    SvProject** projects; int nprojects, capProjects;
    SvMsg* qHead; SvMsg* qTail;
    int debounce;
    int64_t clock;
    int64_t epoch;
    bool stopping;
    bool shutdown;
    const char* preludePath;
    const char* storeRoot;
    SvLang lang;         /* the index of the main thread */
    SvLang wlang;        /* the analysis thread's own */
    FILE* log;
    int analyses, remembered;
} SvServer;

static void svLog(SvServer* S, const char* fmt, ...) {
    va_list ap;
    if (!S->log) return;
    va_start(ap, fmt);
    vfprintf(S->log, fmt, ap);
    va_end(ap);
    fputc('\n', S->log);
    fflush(S->log);
}

static void svSend(SvServer* S, FcBuf* line) {
    svLock(&S->outMu);
    fwrite(line->p, 1, line->len, stdout);
    fputc('\n', stdout);
    fflush(stdout);
    svUnlock(&S->outMu);
}

static void svLangInit(SvLang* L, SvServer* S) {
    memset(L, 0, sizeof *L);
    fcCtxInit(&L->pctx);
    L->preludePath = S->preludePath;
    L->storeRoot = S->storeRoot;
    L->L = (FcLinker*)fcAlloc(&L->pctx, sizeof *L->L);
    L->L->c = &L->pctx;
    L->L->store.root = S->storeRoot;
    fcDictInit(&L->pctx, &L->importedPreludes, FC_KEY_STR);
    fcDictInit(&L->pctx, &L->importKeys, FC_KEY_STR);
    fcDictInit(&L->pctx, &L->projFiles, FC_KEY_STR);
    L->retired = fcListNew(&L->pctx);
}

static bool svSamePath(const char* a, const char* b) {
#ifdef _WIN32
    return svEqIC(a, b);
#else
    return strcmp(a, b) == 0;
#endif
}

static SvDoc* svFindDoc(SvServer* S, const char* path) {
    int i;
    if (!path) return NULL;
    for (i = 0; i < S->ndocs; i++) if (svSamePath(S->docs[i]->path, path)) return S->docs[i];
    return NULL;
}
static SvProject* svFindProject(SvServer* S, const char* key) {
    int i;
    if (!key) return NULL;
    for (i = 0; i < S->nprojects; i++) if (strcmp(S->projects[i]->key, key) == 0) return S->projects[i];
    return NULL;
}

/* ---- JSON ---- */
static const char* svJStr(FcJson* o, const char* key) { return fcJsonStr(o, key); }
static bool svJNum(FcJson* o, const char* key, double* out) { FcJson* v = fcJsonGet(o, key); if (!v || v->kind != FJ_NUM) return false; *out = v->n; return true; }
static bool svJBool(FcJson* o, const char* key) { FcJson* v = fcJsonGet(o, key); return v && v->kind == FJ_BOOL && v->b; }
static void svJsonEsc(FcBuf* b, const char* s) { if (!s) fcBufStr(b, "null"); else fcEsc(b, s); }

static SvText* svTextFromFc8(const char* s) {
    FcU16 u = {0};
    SvText* t;
    fcU16FromFc8(s, &u);
    t = svTextNew(u.p, u.len);
    fcU16Free(&u);
    return t;
}
static SvText* svTextFromFile(const char* path) {
    FcU16 u = {0};
    SvText* t;
    if (!fcReadText(path, &u)) return svTextNew(NULL, 0);
    t = svTextNew(u.p, u.len);
    fcU16Free(&u);
    return t;
}

static void svReply(SvServer* S, int64_t id, const char* resultJson) {
    FcBuf b = {0};
    fcBufFmt(&b, "{\"id\":%" PRId64 ",\"result\":%s}", id, resultJson);
    svSend(S, &b);
    fcBufFree(&b);
}
static void svReplyError(SvServer* S, int64_t id, const char* code, const char* message) {
    FcBuf b = {0};
    fcBufFmt(&b, "{\"id\":%" PRId64 ",\"error\":{\"code\":\"%s\",\"message\":", id, code);
    svJsonEsc(&b, message);
    fcBufStr(&b, "}}");
    svSend(S, &b);
    fcBufFree(&b);
}

/* ---- the analysis thread ---- */
static void svFileList(FcCtx* c, FcJson* arr, SvPFile** out, int* n) {
    int i;
    *out = NULL;
    *n = 0;
    if (!arr || arr->kind != FJ_ARR) return;
    *out = (SvPFile*)calloc((size_t)arr->items->count + 1, sizeof(SvPFile));
    for (i = 0; i < arr->items->count; i++) {
        FcJson* f = (FcJson*)arr->items->items[i];
        const char* path = fcJsonStr(f, "path");
        const char* text = fcJsonStr(f, "text");
        if (!path) continue;
        (*out)[*n].path = svDup(path);
        (*out)[*n].text = text ? svTextFromFc8(text) : svTextFromFile(path);
        (*n)++;
    }
    (void)c;
}
static void svFreeFiles(SvPFile* f, int n) { int i; for (i = 0; i < n; i++) { free(f[i].path); svTextUnref(f[i].text); } free(f); }

/* the text of a file of the project: the open document's (overlay), else what the project says */
static SvText* svOverlay(SvServer* S, const SvPFile* f) {
    SvDoc* d = svFindDoc(S, f->path);
    return d ? d->text : f->text;
}

typedef struct SvJob {
    SvDoc* doc;
    int version;
    int64_t seq;
    char* path;
    char* base;
    SvText* text;
    SvText** others; char** otherPaths; int nothers;
    SvProject* project;          /* (only the scalar settings are read) */
    bool strict; char* ownImport;
    int nlibs;
    SvLib* libs;                 /* built in the job's context */
    SvText** libTexts; int nlibTexts;
    FcCtx* c;
    uint64_t key;
} SvJob;

/* the snapshot of an analysis, under the lock */
static SvJob* svTakeJob(SvServer* S, SvDoc* d) {
    SvJob* j = (SvJob*)calloc(1, sizeof *j);
    SvProject* p = svFindProject(S, d->project);
    uint64_t key = d->text->hash ^ ((uint64_t)S->epoch * 0x9E3779B97F4A7C15ULL);
    int i, k, nt = 0;
    j->c = svNewCtx();
    j->doc = d;
    j->version = d->version;
    j->seq = d->seq;
    j->path = svDup(d->path);
    j->base = svDup(d->base);
    j->text = svTextRef(d->text);
    if (p) {
        j->strict = p->strict;
        j->ownImport = svDup(p->ownImport);
        j->others = (SvText**)calloc((size_t)p->nfiles + 1, sizeof(SvText*));
        j->otherPaths = (char**)calloc((size_t)p->nfiles + 1, sizeof(char*));
        for (i = 0; i < p->nfiles; i++) {
            SvText* t;
            if (svSamePath(p->files[i].path, d->path)) continue;
            t = svOverlay(S, &p->files[i]);
            j->others[j->nothers] = svTextRef(t);
            j->otherPaths[j->nothers] = svDup(p->files[i].path);
            j->nothers++;
            key = (key * 1099511628211ULL) ^ t->hash ^ svHash((const uint16_t*)p->files[i].path, (int)(strlen(p->files[i].path) / 2));
        }
        j->nlibs = p->nlibs;
        j->libs = (SvLib*)fcAlloc(j->c, sizeof(SvLib) * (size_t)(p->nlibs + 1));
        for (i = 0; i < p->nlibs; i++) nt += p->libs[i].nfiles;
        j->libTexts = (SvText**)calloc((size_t)nt + 1, sizeof(SvText*));
        for (i = 0; i < p->nlibs; i++) {
            SvPLib* pl = &p->libs[i];
            SvLib* l = &j->libs[i];
            l->names = fcListNew(j->c);
            for (k = 0; k < pl->nnames; k++) fcListAdd(j->c, l->names, (void*)fcStr(j->c, pl->names[k]));
            l->import = fcStr(j->c, pl->import);
            l->name = fcStr(j->c, pl->name);
            l->ns = fcStr(j->c, pl->ns);
            l->requires = fcListNew(j->c);
            for (k = 0; k < pl->nrequires; k++) fcListAdd(j->c, l->requires, (void*)fcStr(j->c, pl->requires[k]));
            l->nfiles = pl->nfiles;
            l->files = (SvFile*)fcAlloc(j->c, sizeof(SvFile) * (size_t)(pl->nfiles + 1));
            for (k = 0; k < pl->nfiles; k++) {
                SvText* t = svTextRef(svOverlay(S, &pl->files[k]));
                j->libTexts[j->nlibTexts++] = t;
                l->files[k].path = fcStr(j->c, pl->files[k].path);
                l->files[k].text = t->p;
                l->files[k].len = t->len;
                key = (key * 1099511628211ULL) ^ t->hash;
            }
        }
        key ^= (uint64_t)p->stamp * 0xC2B2AE3D27D4EB4FULL;
        key ^= p->strict ? 0x5555 : 0;
    }
    j->key = key;
    return j;
}
static void svFreeJob(SvJob* j) {
    int i;
    svTextUnref(j->text);
    for (i = 0; i < j->nothers; i++) { svTextUnref(j->others[i]); free(j->otherPaths[i]); }
    for (i = 0; i < j->nlibTexts; i++) svTextUnref(j->libTexts[i]);
    free(j->others); free(j->otherPaths); free(j->libTexts);
    free(j->path); free(j->base); free(j->ownImport);
    svFreeCtx(j->c);
    free(j);
}

/* the `items` of a list of diagnostics */
static void svDiagItems(FcBuf* b, FcList* diags) {
    int i;
    fcBufChar(b, '[');
    for (i = 0; diags && i < diags->count; i++) {
        const FcDiagnostic* d = (const FcDiagnostic*)diags->items[i];
        if (i) fcBufChar(b, ',');
        fcBufFmt(b, "{\"line\":%d,\"column\":%d,\"severity\":\"%s\",\"code\":", d->line, d->column, d->severity == FC_SEV_WARNING ? "warning" : "error");
        svJsonEsc(b, d->code);
        fcBufStr(b, ",\"message\":");
        svJsonEsc(b, d->message);
        fcBufStr(b, ",\"file\":");
        svJsonEsc(b, d->file);
        fcBufChar(b, '}');
    }
    fcBufChar(b, ']');
}

/* runs the analysis of a job; returns the `items` (malloc'd) */
static char* svRunJob(SvServer* S, SvLang* lang, SvJob* j) {
    SvAnalysisIn in;
    SvFile* others = (SvFile*)fcAlloc(j->c, sizeof(SvFile) * (size_t)(j->nothers + 1));
    FcList* diags;
    FcBuf b = {0};
    char* r;
    int i;
    for (i = 0; i < j->nothers; i++) { others[i].path = fcStr(j->c, j->otherPaths[i]); others[i].text = j->others[i]->p; others[i].len = j->others[i]->len; }
    memset(&in, 0, sizeof in);
    in.text = j->text->p;
    in.len = j->text->len;
    in.base = j->base;
    in.others = others;
    in.nothers = j->nothers;
    in.libs = j->libs;
    in.nlibs = j->nlibs;
    in.hasPlan = j->libs != NULL;
    in.strict = j->strict;
    in.ownImport = j->ownImport;
    lang->tmp = svNewCtx();
    diags = svAnalyzeInProject(j->c, lang, S->preludePath, S->storeRoot, &in);
    svFreeCtx(lang->tmp);
    lang->tmp = NULL;
    fcBufStr(&b, "");
    svDiagItems(&b, diags);
    r = svDup(b.p);
    fcBufFree(&b);
    return r;
}

SV_THREAD_FN(svAnalysisThread) {
    SvServer* S = (SvServer*)arg;
    svLock(&S->mu);
    while (!S->stopping) {
        int64_t now = svNowMs(), wait = -1;
        SvDoc* best = NULL;
        int i;
        for (i = 0; i < S->ndocs; i++) {
            SvDoc* d = S->docs[i];
            if (!d->pending || d->fxml) continue;
            if (d->due <= now) { if (!best || d->touched > best->touched) best = d; }
            else if (wait < 0 || d->due - now < wait) wait = d->due - now;
        }
        if (!best) { svCondWait(&S->workCond, &S->mu, (long)wait); continue; }
        {
            SvJob* j = svTakeJob(S, best);
            int64_t started = svNowMs();
            char* items;
            bool fromMemory;
            best->pending = false;
            fromMemory = false;
            items = NULL;
            for (i = 0; i < SV_REMEMBERED && !fromMemory; i++) {
                if (best->rememberedItems[i] && best->rememberedKey[i] == j->key) { items = svDup(best->rememberedItems[i]); fromMemory = true; S->remembered++; }
            }
            if (!fromMemory) {
                svUnlock(&S->mu);
                items = svRunJob(S, &S->wlang, j);
                svLock(&S->mu);
                S->analyses++;
            }
            /* the document may be closed meanwhile; a result older than the one sent is dropped */
            for (i = 0; i < S->ndocs; i++) if (S->docs[i] == j->doc) break;
            if (i < S->ndocs) {
                SvDoc* d = j->doc;
                if (!fromMemory) {
                    int k;
                    free(d->rememberedItems[SV_REMEMBERED - 1]);
                    for (k = SV_REMEMBERED - 1; k > 0; k--) { d->rememberedItems[k] = d->rememberedItems[k - 1]; d->rememberedKey[k] = d->rememberedKey[k - 1]; }
                    d->rememberedItems[0] = svDup(items);
                    d->rememberedKey[0] = j->key;
                }
                if (j->seq > d->deliveredSeq) {
                    FcBuf b = {0};
                    d->deliveredSeq = j->seq;
                    fcBufStr(&b, "{\"method\":\"diagnostics\",\"params\":{\"path\":");
                    svJsonEsc(&b, d->path);
                    fcBufFmt(&b, ",\"version\":%d,\"stale\":%s,\"ms\":%d,\"items\":%s}}", j->version, j->seq < d->seq ? "true" : "false", (int)(svNowMs() - started), items);
                    svSend(S, &b);
                    fcBufFree(&b);
                }
            }
            free(items);
            svFreeJob(j);
        }
    }
    svUnlock(&S->mu);
    SV_THREAD_RET;
}

static void svSchedule(SvServer* S, SvDoc* d) {
    if (d->fxml) return;
    d->pending = true;
    d->seq = ++S->clock;
    d->due = svNowMs() + S->debounce;
    d->touched = d->seq;
    svCondSignal(&S->workCond);
}

/* ---- the reader thread ---- */
static int64_t svPeekId(const char* line) {
    const char* p = strstr(line, "\"id\"");
    if (!p) return -1;
    p += 4;
    while (*p == ' ' || *p == ':') p++;
    return isdigit((unsigned char)*p) ? strtoll(p, NULL, 10) : -1;
}

SV_THREAD_FN(svReaderThread) {
    SvServer* S = (SvServer*)arg;
    FcBuf line = {0};
    int ch;
    for (;;) {
        SvMsg* m;
        bool eof = false;
        line.len = 0;
        while ((ch = fgetc(stdin)) != EOF && ch != '\n') fcBufChar(&line, (char)ch);
        if (ch == EOF && line.len == 0) eof = true;
        fcBufChar(&line, 0);
        if (!eof && line.len <= 1) continue;
        m = (SvMsg*)calloc(1, sizeof *m);
        m->eof = eof;
        m->line = eof ? NULL : svDup(line.p);
        m->id = eof ? -1 : svPeekId(line.p);
        svLock(&S->mu);
        /* `cancel`: a request still in the queue is answered with `cancelled` */
        if (!eof && strstr(line.p, "\"method\":\"cancel\"")) {
            const char* params = strstr(line.p, "\"params\"");
            int64_t id = params ? svPeekId(params) : -1;
            SvMsg* q;
            for (q = S->qHead; q; q = q->next) if (q->id == id && id >= 0) q->cancelled = true;
        }
        if (S->qTail) S->qTail->next = m; else S->qHead = m;
        S->qTail = m;
        svCondSignal(&S->queueCond);
        svUnlock(&S->mu);
        if (eof) break;
    }
    fcBufFree(&line);
    SV_THREAD_RET;
}

/* ---- the index of a document (main thread) ---- */
static SvIndex* svDocIndex(SvServer* S, SvDoc* d) {
    SvProject* p = svFindProject(S, d->project);
    SvFile* files;
    int n = 0, cap, i, k;
    uint64_t key;
    FcList* imports;
    /* (no lock: the documents and projects change only on this thread) */
    key = d->text->hash;
    if (d->includesSet) p = NULL;   /* (the client's list instead of the project's) */
    cap = (p ? p->nfiles : 0) + d->nincludes + 1;
    if (p) for (i = 0; i < p->nlibs; i++) cap += p->libs[i].nfiles;
    files = (SvFile*)fcAlloc(S->lang.tmp, sizeof(SvFile) * (size_t)cap);
    if (p) {
        /* ProjectFilesFor: the other files, then the files of the libraries the program imports */
        imports = fcListNew(S->lang.tmp);
        svTextImports(d->text, S->lang.tmp, imports);
        for (i = 0; i < p->nfiles; i++) {
            SvText* t;
            if (svSamePath(p->files[i].path, d->path)) continue;
            t = svOverlay(S, &p->files[i]);
            files[n].path = fcStr(S->lang.tmp, p->files[i].path); files[n].text = t->p; files[n].len = t->len;
            n++;
            svTextImports(t, S->lang.tmp, imports);
            key = (key * 1099511628211ULL) ^ t->hash;
        }
        for (i = 0; i < p->nlibs; i++) {
            bool imported = false;
            for (k = 0; k < imports->count; k++) if (svEqIC((const char*)imports->items[k], p->libs[i].import)) imported = true;
            if (!imported) continue;
            for (k = 0; k < p->libs[i].nfiles; k++) {
                SvText* t = svOverlay(S, &p->libs[i].files[k]);
                files[n].path = fcStr(S->lang.tmp, p->libs[i].files[k].path); files[n].text = t->p; files[n].len = t->len;
                n++;
                key = (key * 1099511628211ULL) ^ t->hash;
            }
        }
    }
    for (i = 0; i < d->nincludes; i++) {
        files[n].path = fcStr(S->lang.tmp, d->includes[i].path); files[n].text = d->includes[i].text->p; files[n].len = d->includes[i].text->len;
        n++;
        key = (key * 1099511628211ULL) ^ d->includes[i].text->hash;
    }
    key ^= (uint64_t)S->epoch * 0x9E3779B97F4A7C15ULL;
    if (d->index && d->indexKey == key) return d->index;
    if (d->index) svIndexFree(&S->lang, d->index);
    d->index = svBuild(&S->lang, d->text->p, d->text->len, NULL, files, n);
    d->indexKey = key;
    return d->index;
}

/* ---- the messages ---- */
static void svDocFree(SvServer* S, SvDoc* d) {
    if (d->index) svIndexFree(&S->lang, d->index);
    svTextUnref(d->text);
    svFreeFiles(d->includes, d->nincludes);
    { int k; for (k = 0; k < SV_REMEMBERED; k++) free(d->rememberedItems[k]); }
    free(d->path); free(d->project); free(d->base);
    free(d);
}

static void svProjectFree(SvProject* p) {
    int i, k;
    svFreeFiles(p->files, p->nfiles);
    for (i = 0; i < p->nlibs; i++) {
        SvPLib* l = &p->libs[i];
        for (k = 0; k < l->nnames; k++) free(l->names[k]);
        for (k = 0; k < l->nrequires; k++) free(l->requires[k]);
        free(l->names); free(l->requires); free(l->import); free(l->name); free(l->ns);
        svFreeFiles(l->files, l->nfiles);
    }
    free(p->libs); free(p->key); free(p->ownImport);
    free(p);
}

static char** svStrArray(FcJson* arr, int* n) {
    char** r;
    int i;
    *n = 0;
    if (!arr || arr->kind != FJ_ARR) return NULL;
    r = (char**)calloc((size_t)arr->items->count + 1, sizeof(char*));
    for (i = 0; i < arr->items->count; i++) {
        FcJson* v = (FcJson*)arr->items->items[i];
        if (v->kind == FJ_STR) r[(*n)++] = svDup(v->s);
    }
    return r;
}

/* every document of the project (or every document) is checked again */
static void svRescheduleProject(SvServer* S, const char* key) {
    int i;
    for (i = 0; i < S->ndocs; i++) if (!key || (S->docs[i]->project && strcmp(S->docs[i]->project, key) == 0)) svSchedule(S, S->docs[i]);
}
/* a change of a document that is a file of a project: the other documents of that project see it */
static void svRescheduleDependents(SvServer* S, SvDoc* changed) {
    int i, k, f;
    for (i = 0; i < S->nprojects; i++) {
        SvProject* p = S->projects[i];
        bool member = false;
        for (f = 0; f < p->nfiles && !member; f++) if (svSamePath(p->files[f].path, changed->path)) member = true;
        for (k = 0; k < p->nlibs && !member; k++) for (f = 0; f < p->libs[k].nfiles; f++) if (svSamePath(p->libs[k].files[f].path, changed->path)) member = true;
        if (!member) continue;
        for (k = 0; k < S->ndocs; k++) if (S->docs[k] != changed && S->docs[k]->project && strcmp(S->docs[k]->project, p->key) == 0) {
            SvDoc* d = S->docs[k];
            /* (after the document typed in: it keeps the first place) */
            d->pending = true;
            d->seq = ++S->clock;
            d->due = svNowMs() + S->debounce;
            d->touched = changed->touched - 1;
        }
    }
    svCondSignal(&S->workCond);
}

/* applies `edits` to a text; false when they do not fit */
static bool svApplyEdits(FcJson* edits, const SvText* text, FcU16* out) {
    int i;
    fcU16Put(out, text->p, text->len);
    for (i = 0; i < edits->items->count; i++) {
        FcJson* e = (FcJson*)edits->items->items[i];
        double off, rem;
        const char* ins = fcJsonStr(e, "text");
        FcU16 u = {0};
        int o, r, n;
        if (!svJNum(e, "offset", &off) || !svJNum(e, "removed", &rem)) return false;
        o = (int)off; r = (int)rem;
        if (o < 0 || r < 0 || o + r > out->len) return false;
        n = ins ? fcU16FromFc8(ins, &u) : 0;
        {
            int tail = out->len - o - r, need = out->len - r + n;
            if (need > out->cap) {
                out->p = (uint16_t*)realloc(out->p, sizeof(uint16_t) * (size_t)(need + 16));
                if (!out->p) fbOutOfMemory();
                out->cap = need + 16;
            }
            memmove(out->p + o + n, out->p + o + r, sizeof(uint16_t) * (size_t)tail);
            if (n) memcpy(out->p + o, u.p, sizeof(uint16_t) * (size_t)n);
            out->len = need;
        }
        fcU16Free(&u);
    }
    return true;
}

static void svDefaultBase(SvDoc* d, FcCtx* t) {
    if (!d->base) d->base = svDup(fcDirectoryName(t, fcFullPath(t, ".", d->path)));
}

/* `completion` */
static void svCompletionResult(SvServer* S, SvDoc* d, int offset, FcBuf* b) {
    SvIndex* x = svDocIndex(S, d);
    int replaceStart, i;
    FcList* items = svGetSuggestions(&S->lang, x, offset, &replaceStart);
    fcBufFmt(b, "{\"replaceStart\":%d,\"items\":[", replaceStart);
    for (i = 0; i < items->count; i++) {
        SvItem* it = (SvItem*)items->items[i];
        if (i) fcBufChar(b, ',');
        fcBufStr(b, "{\"text\":");
        svJsonEsc(b, it->text);
        fcBufFmt(b, ",\"kind\":\"%s\",\"score\":%.9g", svItemKindNames[it->kind], (double)it->score);
        if (it->detail) { fcBufStr(b, ",\"detail\":"); svJsonEsc(b, it->detail); }
        if (it->doc) { fcBufStr(b, ",\"doc\":"); svJsonEsc(b, it->doc); }
        fcBufChar(b, '}');
    }
    fcBufStr(b, "]}");
}

static void svCapabilities(FcBuf* b) {
    fcBufStr(b, "[\"diagnostics\",\"completion\",\"hover\",\"signature\",\"definition\",\"references\",\"symbols\",\"folding\",\"preludeSource\"");
#ifdef SV_HAVE_MARKUP
    fcBufStr(b, ",\"markupCompletion\"");
#endif
    fcBufChar(b, ']');
}

/* serve_nav.c, serve_markup.c */
static void svNavRequest(SvServer* S, SvDoc* d, const char* method, int offset, FcBuf* b);
static void svPreludeSourceResult(SvServer* S, const char* name, FcBuf* b);
static void svMarkupCompletion(SvServer* S, SvDoc* d, int offset, FcBuf* b);

/* one message; false: exit */
static bool svHandle(SvServer* S, SvMsg* m, int* exitCode) {
    FcCtx mc;
    FcJsonReader jr;
    FcJson* msg;
    FcJson* params;
    const char* method;
    double num;
    volatile int64_t id = -1;
    volatile bool keep = true;
    volatile bool failed = false;
    fcCtxInit(&mc);
    jr.c = &mc; jr.p = m->line; jr.bad = false;
    msg = fcJsonValue(&jr);
    if (jr.bad || msg->kind != FJ_OBJ) {
        svLog(S, "bad line: %.200s", m->line);
        if (m->id >= 0) svReplyError(S, m->id, "parse", "not a JSON object");
        fcCtxFree(&mc);
        return true;
    }
    if (svJNum(msg, "id", &num)) id = (int64_t)num;
    method = svJStr(msg, "method");
    params = fcJsonGet(msg, "params");
    if (!method) { if (id >= 0) svReplyError(S, id, "method", "no method"); fcCtxFree(&mc); return true; }
    if (m->cancelled && id >= 0) { svReplyError(S, id, "cancelled", "cancelled"); fcCtxFree(&mc); return true; }
    if (S->shutdown && strcmp(method, "exit") != 0) { if (id >= 0) svReplyError(S, id, "method", "shut down"); fcCtxFree(&mc); return true; }
    S->lang.tmp = svNewCtx();
    FC_TRY(&mc) {
        const char* path = params ? svJStr(params, "path") : NULL;
        if (strcmp(method, "initialize") == 0) {
            FcBuf b = {0};
            fcBufStr(&b, "{\"server\":\"forge-c\",\"protocol\":1,\"capabilities\":");
            svCapabilities(&b);
            fcBufChar(&b, '}');
            if (params && svJNum(params, "debounce", &num)) S->debounce = (int)num;
            svReply(S, id, b.p);
            fcBufFree(&b);
        } else if (strcmp(method, "shutdown") == 0) {
            S->shutdown = true;
            svReply(S, id, "null");
        } else if (strcmp(method, "exit") == 0) {
            *exitCode = S->shutdown ? 0 : 1;
            keep = false;
        } else if (strcmp(method, "invalidate") == 0) {
            svLock(&S->mu);
            S->epoch++;
            svUnlock(&S->mu);
            svForgetImports(&S->lang);
            svLock(&S->mu);
            svForgetImports(&S->wlang);
            svRescheduleProject(S, NULL);
            svUnlock(&S->mu);
        } else if (strcmp(method, "cancel") == 0) {
            /* (a queued request was marked by the reader; a running analysis finishes, its result is marked stale) */
        } else if (strcmp(method, "open") == 0) {
            const char* text = svJStr(params, "text");
            SvDoc* d;
            if (!path || !text) fcThrow(&mc, FC_ERR_INTERNAL, 0, 0, NULL, "params");
            svLock(&S->mu);
            d = svFindDoc(S, path);
            if (!d) {
                d = (SvDoc*)calloc(1, sizeof *d);
                d->path = svDup(path);
                if (S->ndocs == S->capDocs) { S->capDocs = S->capDocs ? S->capDocs * 2 : 8; S->docs = (SvDoc**)realloc(S->docs, sizeof(SvDoc*) * (size_t)S->capDocs); }
                S->docs[S->ndocs++] = d;
            }
            svTextUnref(d->text);
            d->text = svTextFromFc8(text);
            d->version = svJNum(params, "version", &num) ? (int)num : d->version + 1;
            free(d->project); d->project = svDup(svJStr(params, "project"));
            free(d->base); d->base = svDup(svJStr(params, "base"));
            svDefaultBase(d, S->lang.tmp);
            d->fxml = svJStr(params, "kind") && strcmp(svJStr(params, "kind"), "fxml") == 0;
            if (fcJsonGet(params, "includes")) { svFreeFiles(d->includes, d->nincludes); svFileList(&mc, fcJsonGet(params, "includes"), &d->includes, &d->nincludes); d->includesSet = true; }
            svSchedule(S, d);
            svRescheduleDependents(S, d);
            svUnlock(&S->mu);
        } else if (strcmp(method, "change") == 0) {
            SvDoc* d;
            FcJson* edits = fcJsonGet(params, "edits");
            const char* text = svJStr(params, "text");
            svLock(&S->mu);
            d = svFindDoc(S, path);
            if (!d) { svUnlock(&S->mu); fcThrow(&mc, FC_ERR_INTERNAL, 0, 0, NULL, "document"); }
            if (text) { svTextUnref(d->text); d->text = svTextFromFc8(text); }
            else if (edits && edits->kind == FJ_ARR) {
                FcU16 u = {0};
                if (svApplyEdits(edits, d->text, &u)) { svTextUnref(d->text); d->text = svTextNew(u.p, u.len); }
                else {
                    FcBuf b = {0};
                    fcBufStr(&b, "{\"method\":\"resync\",\"params\":{\"path\":");
                    svJsonEsc(&b, d->path);
                    fcBufStr(&b, "}}");
                    svSend(S, &b);
                    fcBufFree(&b);
                }
                fcU16Free(&u);
            }
            d->version = svJNum(params, "version", &num) ? (int)num : d->version + 1;
            if (fcJsonGet(params, "includes")) { svFreeFiles(d->includes, d->nincludes); svFileList(&mc, fcJsonGet(params, "includes"), &d->includes, &d->nincludes); d->includesSet = true; }
            if (fcJsonGet(params, "project")) { free(d->project); d->project = svDup(svJStr(params, "project")); }
            svSchedule(S, d);
            svRescheduleDependents(S, d);
            svUnlock(&S->mu);
        } else if (strcmp(method, "close") == 0) {
            int i;
            svLock(&S->mu);
            for (i = 0; i < S->ndocs; i++)
                if (svSamePath(S->docs[i]->path, path ? path : "")) {
                    SvDoc* d = S->docs[i];
                    S->docs[i] = S->docs[--S->ndocs];
                    svDocFree(S, d);
                    break;
                }
            svUnlock(&S->mu);
        } else if (strcmp(method, "project") == 0 || strcmp(method, "dropProject") == 0) {
            const char* key = svJStr(params, "key");
            SvProject* p;
            int i, k;
            if (!key) fcThrow(&mc, FC_ERR_INTERNAL, 0, 0, NULL, "params");
            svLock(&S->mu);
            for (i = 0; i < S->nprojects; i++) if (strcmp(S->projects[i]->key, key) == 0) { svProjectFree(S->projects[i]); S->projects[i] = S->projects[--S->nprojects]; break; }
            if (strcmp(method, "project") == 0) {
                FcJson* libs = fcJsonGet(params, "libraries");
                const char* tc = svJStr(params, "typeChecking");
                p = (SvProject*)calloc(1, sizeof *p);
                p->key = svDup(key);
                p->stamp = (int)++S->clock;
                p->strict = tc && svEqIC(tc, "strict");
                p->ownImport = svJBool(params, "library") ? svDup(svJStr(params, "projectImport")) : NULL;
                svUnlock(&S->mu);   /* (reading the files from the disk without the lock) */
                svFileList(&mc, fcJsonGet(params, "files"), &p->files, &p->nfiles);
                if (libs && libs->kind == FJ_ARR) {
                    p->libs = (SvPLib*)calloc((size_t)libs->items->count + 1, sizeof(SvPLib));
                    for (k = 0; k < libs->items->count; k++) {
                        FcJson* l = (FcJson*)libs->items->items[k];
                        SvPLib* pl = &p->libs[p->nlibs];
                        if (!svJStr(l, "import")) continue;
                        pl->import = svDup(svJStr(l, "import"));
                        pl->name = svDup(svJStr(l, "name") ? svJStr(l, "name") : pl->import);
                        pl->ns = svDup(svJStr(l, "namespace"));
                        pl->names = svStrArray(fcJsonGet(l, "names"), &pl->nnames);
                        if (pl->nnames == 0) { free(pl->names); pl->names = (char**)calloc(2, sizeof(char*)); pl->names[0] = svDup(pl->import); pl->nnames = 1; }
                        pl->requires = svStrArray(fcJsonGet(l, "requires"), &pl->nrequires);
                        svFileList(&mc, fcJsonGet(l, "files"), &pl->files, &pl->nfiles);
                        p->nlibs++;
                    }
                }
                svLock(&S->mu);
                if (S->nprojects == S->capProjects) { S->capProjects = S->capProjects ? S->capProjects * 2 : 4; S->projects = (SvProject**)realloc(S->projects, sizeof(SvProject*) * (size_t)S->capProjects); }
                S->projects[S->nprojects++] = p;
            }
            svRescheduleProject(S, key);
            svUnlock(&S->mu);
        } else if (strcmp(method, "diagnose") == 0) {
            SvDoc* d;
            SvJob* j;
            char* items;
            FcBuf b = {0};
            int version;
            svLock(&S->mu);
            d = svFindDoc(S, path);
            if (!d) { svUnlock(&S->mu); fcThrow(&mc, FC_ERR_INTERNAL, 0, 0, NULL, "document"); }
            version = d->version;
            {
                j = svTakeJob(S, d);   /* (a scheduled analysis stays scheduled) */
            }
            svUnlock(&S->mu);
            items = svRunJob(S, &S->lang, j);
            fcBufFmt(&b, "{\"version\":%d,\"items\":%s}", version, items);
            svReply(S, id, b.p);
            fcBufFree(&b);
            free(items);
            svLock(&S->mu);
            svFreeJob(j);
            svUnlock(&S->mu);
        } else if (strcmp(method, "completion") == 0 || strcmp(method, "hover") == 0 || strcmp(method, "signature") == 0 || strcmp(method, "definition") == 0
                   || strcmp(method, "references") == 0 || strcmp(method, "symbols") == 0 || strcmp(method, "folding") == 0 || strcmp(method, "markupCompletion") == 0) {
            SvDoc* d = svFindDoc(S, path);
            FcBuf b = {0};
            int offset = params && svJNum(params, "offset", &num) ? (int)num : 0;
            if (!d) fcThrow(&mc, FC_ERR_INTERNAL, 0, 0, NULL, "document");
            fcBufStr(&b, "");
            if (strcmp(method, "completion") == 0) svCompletionResult(S, d, offset, &b);
            else if (strcmp(method, "markupCompletion") == 0) svMarkupCompletion(S, d, offset, &b);
            else svNavRequest(S, d, method, offset, &b);
            svReply(S, id, b.p);
            fcBufFree(&b);
        } else if (strcmp(method, "preludeSource") == 0) {
            FcBuf b = {0};
            svPreludeSourceResult(S, params ? svJStr(params, "name") : NULL, &b);
            svReply(S, id, b.p);
            fcBufFree(&b);
        } else {
            if (id >= 0) svReplyError(S, id, "method", fcFmt(&mc, "unknown method '%s'", method));
        }
    } FC_CATCH(&mc) {
        failed = true;
    } FC_END_TRY;
    if (failed && id >= 0) {
        const char* why = mc.err.message ? mc.err.message : "internal";
        svReplyError(S, id, strcmp(why, "document") == 0 ? "document" : strcmp(why, "params") == 0 ? "params" : "internal", why);
    }
    if (failed) svLog(S, "%s failed: %s", method, mc.err.message ? mc.err.message : "?");
    svFreeCtx(S->lang.tmp);
    S->lang.tmp = NULL;
    fcCtxFree(&mc);
    return keep;
}

static int svServe(int argc, char** argv, const char* prelude, const char* store) {
    SvServer* S = (SvServer*)calloc(1, sizeof *S);
    SvThread reader, worker;
    int exitCode = 1, i;
    char preludeBuf[4096];
    S->debounce = 300;
    S->preludePath = prelude;
    S->storeRoot = store;
    for (i = 2; i < argc; i++) {
        if (strcmp(argv[i], "--tables") == 0 && i + 1 < argc) { snprintf(preludeBuf, sizeof preludeBuf, "%s/prelude.fire", argv[++i]); S->preludePath = preludeBuf; }
        else if (strcmp(argv[i], "--packages") == 0 && i + 1 < argc) S->storeRoot = argv[++i];
        else if (strcmp(argv[i], "--debounce") == 0 && i + 1 < argc) S->debounce = atoi(argv[++i]);
        else if (strcmp(argv[i], "--log") == 0 && i + 1 < argc) S->log = fopen(argv[++i], "a");
    }
    svMutexInit(&S->mu);
    svMutexInit(&S->outMu);
    svCondInit(&S->workCond);
    svCondInit(&S->queueCond);
    svLangInit(&S->lang, S);
    svLangInit(&S->wlang, S);
#ifdef _WIN32
    _setmode(_fileno(stdin), 0x8000);
    _setmode(_fileno(stdout), 0x8000);
#endif
    svThreadStart(&reader, svReaderThread, S);
    svThreadStart(&worker, svAnalysisThread, S);
    for (;;) {
        SvMsg* m;
        bool go;
        svLock(&S->mu);
        while (!S->qHead) svCondWait(&S->queueCond, &S->mu, -1);
        m = S->qHead;
        S->qHead = m->next;
        if (!S->qHead) S->qTail = NULL;
        svUnlock(&S->mu);
        if (m->eof) { free(m); break; }
        go = svHandle(S, m, &exitCode);
        free(m->line);
        free(m);
        if (!go) break;
    }
    svLock(&S->mu);
    S->stopping = true;
    svCondSignal(&S->workCond);
    svUnlock(&S->mu);
    svLog(S, "exit %d (%d analyses, %d remembered)", exitCode, S->analyses, S->remembered);
    fflush(stdout);
    /* the threads are not joined: a running analysis or the reader blocked in stdin end with the process (exit() would wait for the lock of
     * stdin the reader holds) */
    (void)reader; (void)worker;
    if (S->log) fclose(S->log);
#ifdef _WIN32
    ExitProcess((UINT)exitCode);
#else
    _exit(exitCode);
#endif
    return exitCode;
}

#include "serve_nav.c"
#ifdef SV_HAVE_MARKUP
#include "serve_markup.c"
#endif
