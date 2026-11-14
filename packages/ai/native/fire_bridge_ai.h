/* fire native bridge "ai" (C): the natives behind `#import "ai"` (docs/ai/README.md). The engine is fire_ai_engine.h (our own, no library).
 *
 * Models and sessions are integer handles in a table of this bridge (index 0 unused, handles not used again); what a program leaves open is freed when it ends.
 * A session does its work - computing a prompt, generating, weighing choices - on a thread of its own: the program starts a job and fetches what it produced
 * (`__AiTake`, `__AiState`) when it likes, so a user interface never waits for the model. Errors: -1/false and the message of `__AiLastError`. */
#ifndef FIRE_BRIDGE_AI_H
#define FIRE_BRIDGE_AI_H

#include "fire_ai_engine.h"

enum { FIRE_AI_IDLE = 0, FIRE_AI_PROMPT = 1, FIRE_AI_GENERATING = 2, FIRE_AI_DONE = 3, FIRE_AI_FAILED = 4, FIRE_AI_CANCELLED = 5 };

typedef struct FireAiJob {
    int choose;                 /* 0 generate, 1 weigh the options */
    char* prompt;
    char** options; int optionCount;
    double* scores;             /* the log-probability of each option (per token when `perToken`) */
    int perToken;
    char** stops; int stopCount;
    int maxTokens;
    FaiSampling sampling;
} FireAiJob;

typedef struct FireAiSession {
    int64_t model;
    FaiSession* s;
    FaiPool pool;
    int threads;
    /* the job and its thread */
    FireAiJob job;
    volatile int state;
    volatile int stop;
    int running;               /* a thread was started and not joined */
#ifdef _WIN32
    HANDLE thread;
    CRITICAL_SECTION lock;
#else
    pthread_t thread;
    pthread_mutex_t lock;
#endif
    /* what the job produced: UTF-8, guarded by `lock` */
    char* out; size_t outLen, outCap, taken;
    char error[256];
    int promptTokens, promptDone, generated;
    double promptSeconds, genSeconds;
    int best;
} FireAiSession;

typedef struct FireAiItem { int kind; void* p; } FireAiItem;   /* kind 1 model, 2 session */
static FireAiItem* g_aiItems = NULL;
static size_t g_aiCount = 1, g_aiCap = 0;
static char g_aiError[512];

static void fire_aib_lock(FireAiSession* a) {
#ifdef _WIN32
    EnterCriticalSection(&a->lock);
#else
    pthread_mutex_lock(&a->lock);
#endif
}
static void fire_aib_unlock(FireAiSession* a) {
#ifdef _WIN32
    LeaveCriticalSection(&a->lock);
#else
    pthread_mutex_unlock(&a->lock);
#endif
}

static int64_t fire_aib_fail(const char* message) { snprintf(g_aiError, sizeof g_aiError, "%s", message); return -1; }
static void fire_aib_ok(void) { g_aiError[0] = 0; }

/* A fire string as UTF-8 (malloc; NULL when out of memory). */
static char* fire_aib_utf8(Value v, size_t* length) {
    const Str* s;
    char* out;
    size_t n = 0;
    uint32_t i;
    if (v.kind != K_String) { out = (char*)malloc(1); if (out) out[0] = 0; if (length) *length = 0; return out; }
    s = strOf(v);
    out = (char*)malloc((size_t)s->length * 3 + 1);
    if (out == NULL) return NULL;
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        n += fai_utf8Put(out + n, c);
    }
    out[n] = 0;
    if (length) *length = n;
    return out;
}
static Value fire_aib_str(const char* p, size_t n, OwnList* list) { return newStrFromUtf8((const uint8_t*)p, n, list); }

static int fire_aib_allowed(const char* full, char* reason, size_t size) {
#ifdef FIRE_LIBRARY
    const fire_host* host = libraryHost();
    if (!host || !host->io_allow) return 1;
    reason[0] = 0;
    return host->io_allow(full, 1, reason, (int)size) ? 1 : 0;
#elif defined(FIRE_IO_POLICY)
    return FIRE_IO_POLICY(full, 1, reason, size);
#else
    (void)full; (void)reason; (void)size;
    return 1;
#endif
}

static int64_t fire_aib_add(int kind, void* p) {
    if (g_aiCount >= g_aiCap) {
        size_t cap = g_aiCap ? g_aiCap * 2 : 16;
        FireAiItem* grown = (FireAiItem*)realloc(g_aiItems, sizeof(FireAiItem) * cap);
        if (grown == NULL) return fire_aib_fail("Out of memory.");
        g_aiItems = grown; g_aiCap = cap;
    }
    g_aiItems[g_aiCount].kind = kind;
    g_aiItems[g_aiCount].p = p;
    fire_aib_ok();
    return (int64_t)g_aiCount++;
}
static void* fire_aib_find(Value h, int kind) {
    int64_t i = h.kind == K_Int ? h.i : 0;
    if (i > 0 && (size_t)i < g_aiCount && g_aiItems[i].p != NULL && g_aiItems[i].kind == kind) return g_aiItems[i].p;
    fire_aib_fail(kind == 1 ? "The model is closed." : "The session is closed.");
    return NULL;
}

/* ---- the job of a session (on its thread) ---- */
static void fire_aib_append(FireAiSession* a, const char* p, size_t n) {
    fire_aib_lock(a);
    if (a->outLen + n + 1 > a->outCap) {
        size_t cap = a->outCap ? a->outCap * 2 : 1024;
        char* g;
        while (cap < a->outLen + n + 1) cap *= 2;
        g = (char*)realloc(a->out, cap);
        if (g == NULL) { fire_aib_unlock(a); return; }
        a->out = g; a->outCap = cap;
    }
    memcpy(a->out + a->outLen, p, n);
    a->outLen += n;
    a->out[a->outLen] = 0;
    fire_aib_unlock(a);
}

static double fire_aib_now(void) {
#ifdef _WIN32
    LARGE_INTEGER f, c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c); return (double)c.QuadPart / (double)f.QuadPart;
#else
    struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (double)t.tv_sec + (double)t.tv_nsec * 1e-9;
#endif
}

static void fire_aib_runJob(FireAiSession* a) {
    FaiSession* s = a->s;
    const FaiModel* m = s->m;
    FireAiJob* j = &a->job;
    FaiTokens tk = {0};
    double t0;
    int i;
    if (!fai_tokenize(m, j->prompt, strlen(j->prompt), 1, 1, &tk)) { snprintf(a->error, sizeof a->error, "Out of memory."); a->state = FIRE_AI_FAILED; return; }
    a->promptTokens = (int)tk.n;
    if ((int)tk.n >= s->ctx) { snprintf(a->error, sizeof a->error, "The prompt (%d tokens) does not fit into the context (%d tokens).", (int)tk.n, s->ctx); free(tk.t); a->state = FIRE_AI_FAILED; return; }
    t0 = fire_aib_now();
    if (!j->choose) {
        if (!fai_feed(s, tk.t, (int)tk.n, &a->stop)) { free(tk.t); a->state = a->stop ? FIRE_AI_CANCELLED : FIRE_AI_FAILED; if (!a->stop) snprintf(a->error, sizeof a->error, "The prompt could not be computed."); return; }
        a->promptDone = (int)tk.n;
        a->promptSeconds = fire_aib_now() - t0;
        fai_checkpoint(s);   /* (a model with recurrent layers comes back here for the next question that starts with this prompt) */
        a->state = FIRE_AI_GENERATING;
        t0 = fire_aib_now();
        {
            char piece[1024];
            /* the text so far, to find the stop strings (they are not given out) */
            char* text = NULL; size_t textLen = 0, textCap = 0, given = 0;
            int hold = 0, stopped = 0;
            for (i = 0; j->maxTokens <= 0 || i < j->maxTokens; i++) {
                int32_t t;
                size_t n, k;
                int stopAt = -1;
                if (a->stop) { a->state = FIRE_AI_CANCELLED; break; }
                t = fai_sample(s, &j->sampling, tk.t, (int)tk.n);
                if (fai_isEnd(m, t)) break;
                if ((int)tk.n + 1 >= s->ctx || !fai_push(&tk, t)) break;
                a->generated++;
                n = fai_tokenBytes(m, t, 0, piece);
                if (textLen + n + 1 > textCap) {
                    size_t cap = textCap ? textCap * 2 : 1024;
                    char* g;
                    while (cap < textLen + n + 1) cap *= 2;
                    g = (char*)realloc(text, cap);
                    if (g == NULL) break;
                    text = g; textCap = cap;
                }
                memcpy(text + textLen, piece, n); textLen += n; text[textLen] = 0;
                /* a stop string ends the text (it is not given out); while the end of the text could still become one, that end is held back */
                hold = 0;
                for (k = 0; k < (size_t)j->stopCount; k++) {
                    const char* st = j->stops[k];
                    size_t sl = strlen(st), p, from;
                    if (sl == 0) continue;
                    from = given > sl ? given - sl : 0;
                    for (p = from; p + sl <= textLen; p++) if (memcmp(text + p, st, sl) == 0) { if (stopAt < 0 || (int)p < stopAt) stopAt = (int)p; break; }
                    for (p = sl - 1; p > 0; p--) if (p <= textLen && memcmp(text + textLen - p, st, p) == 0) { if ((int)p > hold) hold = (int)p; break; }
                }
                if (stopAt >= 0) {
                    if ((size_t)stopAt > given) fire_aib_append(a, text + given, (size_t)stopAt - given);
                    given = textLen;
                    stopped = 1;
                    break;
                }
                {
                    /* whole UTF-8 characters only */
                    size_t upTo = textLen - (size_t)hold;
                    if (upTo > given) {
                        size_t back = upTo, need;
                        while (back > given && ((uint8_t)text[back - 1] & 0xC0) == 0x80) back--;
                        if (back > given && ((uint8_t)text[back - 1] & 0x80)) {
                            uint8_t lead = (uint8_t)text[back - 1];
                            need = (lead & 0xE0) == 0xC0 ? 2 : (lead & 0xF0) == 0xE0 ? 3 : (lead & 0xF8) == 0xF0 ? 4 : 1;
                            if (upTo - (back - 1) < need) upTo = back - 1;
                        }
                    }
                    if (upTo > given) { fire_aib_append(a, text + given, upTo - given); given = upTo; }
                }
                if (!fai_feed(s, tk.t, (int)tk.n, &a->stop)) { a->state = a->stop ? FIRE_AI_CANCELLED : FIRE_AI_FAILED; break; }
            }
            if (a->state == FIRE_AI_GENERATING && !stopped && given < textLen) fire_aib_append(a, text + given, textLen - given);
            free(text);
        }
        a->genSeconds = fire_aib_now() - t0;
        if (a->state == FIRE_AI_GENERATING) a->state = FIRE_AI_DONE;
    }
    else {
        /* the options: each one after the prompt, the sum of the log-probabilities of its tokens */
        int o, best = -1;
        double bestScore = -1e300;
        if (!fai_feed(s, tk.t, (int)tk.n, &a->stop)) { free(tk.t); a->state = a->stop ? FIRE_AI_CANCELLED : FIRE_AI_FAILED; return; }
        a->promptDone = (int)tk.n;
        a->promptSeconds = fire_aib_now() - t0;
        fai_checkpoint(s);   /* every option starts from here */
        for (o = 0; o < j->optionCount; o++) {
            FaiTokens all = {0};
            size_t base = tk.n, k;
            double sum = 0;
            int count;
            for (k = 0; k < tk.n; k++) fai_push(&all, tk.t[k]);
            if (!fai_tokenize(m, j->options[o], strlen(j->options[o]), 0, 0, &all) || all.n == base || (int)all.n >= s->ctx) { free(all.t); j->scores[o] = -1e300; continue; }
            count = (int)(all.n - base);
            /* the prompt's logits, then each token of the option after the ones before it */
            if (!fai_feed(s, tk.t, (int)tk.n, &a->stop)) { free(all.t); break; }
            for (k = base; k < all.n; k++) {
                sum += fai_logProb(s, all.t[k]);
                if (k + 1 < all.n && !fai_feed(s, all.t, (int)k + 1, &a->stop)) break;
            }
            j->scores[o] = j->perToken ? sum / count : sum;
            if (j->scores[o] > bestScore) { bestScore = j->scores[o]; best = o; }
            free(all.t);
            if (a->stop) break;
        }
        a->best = best;
        a->state = a->stop ? FIRE_AI_CANCELLED : FIRE_AI_DONE;
    }
    free(tk.t);
}

#ifdef _WIN32
static DWORD WINAPI fire_aib_thread(LPVOID p) { fire_aib_runJob((FireAiSession*)p); return 0; }
#else
static void* fire_aib_thread(void* p) { fire_aib_runJob((FireAiSession*)p); return NULL; }
#endif

static void fire_aib_freeJob(FireAiJob* j) {
    int i;
    free(j->prompt);
    for (i = 0; i < j->optionCount; i++) free(j->options[i]);
    for (i = 0; i < j->stopCount; i++) free(j->stops[i]);
    free(j->options); free(j->stops); free(j->scores);
    memset(j, 0, sizeof *j);
}

/* Waits for the job of the session (after asking it to stop when `cancel`). */
static void fire_aib_join(FireAiSession* a, int cancel) {
    if (!a->running) return;
    if (cancel) a->stop = 1;
#ifdef _WIN32
    WaitForSingleObject(a->thread, INFINITE); CloseHandle(a->thread);
#else
    pthread_join(a->thread, NULL);
#endif
    a->running = 0;
}
static int fire_aib_busy(FireAiSession* a) { return a->running && (a->state == FIRE_AI_PROMPT || a->state == FIRE_AI_GENERATING); }

static void fire_aib_sessionFree(FireAiSession* a) {
    fire_aib_join(a, 1);
    fire_aib_freeJob(&a->job);
    fai_sessionFree(a->s);
    fai_poolStop(&a->pool);
#ifdef _WIN32
    DeleteCriticalSection(&a->lock);
#else
    pthread_mutex_destroy(&a->lock);
#endif
    free(a->out);
    free(a);
}

/* The program has ended (the library for the VM stays loaded for the next one): frees what it left open, the sessions first. */
static void fire_ai_reset(void) {
    size_t i;
    for (i = 1; i < g_aiCount; i++) if (g_aiItems[i].kind == 2 && g_aiItems[i].p) { fire_aib_sessionFree((FireAiSession*)g_aiItems[i].p); g_aiItems[i].p = NULL; }
    for (i = 1; i < g_aiCount; i++) if (g_aiItems[i].kind == 1 && g_aiItems[i].p) { fai_modelFree((FaiModel*)g_aiItems[i].p); g_aiItems[i].p = NULL; }
}

/* ---- the natives ---- */
static Value fire_ai_LastError(OwnList* list) { return fire_aib_str(g_aiError, strlen(g_aiError), list); }

/* The model in the GGUF file `path`: its handle, or -1. */
static Value fire_ai_Load(Value path) {
    char* raw = fire_aib_utf8(path, NULL);
    char* full;
    char reason[256];
    FaiError e;
    FaiModel* m;
    int64_t h;
    if (raw == NULL) return Int(fire_aib_fail("Out of memory."));
    full = fire_fs_fullPath(raw);
    free(raw);
    if (full == NULL) return Int(fire_aib_fail("Out of memory."));
    reason[0] = 0;
    if (!fire_aib_allowed(full, reason, sizeof reason)) {
        char text[512];
        if (reason[0]) snprintf(text, sizeof text, "%s", reason); else snprintf(text, sizeof text, "Access to '%s' is not allowed.", full);
        free(full);
        return Int(fire_aib_fail(text));
    }
    e.text[0] = 0;
    m = fai_modelLoad(full, &e);
    free(full);
    if (m == NULL) return Int(fire_aib_fail(e.text));
    h = fire_aib_add(1, m);
    if (h < 0) fai_modelFree(m);
    return Int(h);
}

/* Closes a model (its sessions must be closed before). */
static Value fire_ai_Close(Value h) {
    int64_t i = h.kind == K_Int ? h.i : 0;
    if (i <= 0 || (size_t)i >= g_aiCount || g_aiItems[i].p == NULL) return Bool(false);
    if (g_aiItems[i].kind == 2) fire_aib_sessionFree((FireAiSession*)g_aiItems[i].p);
    else {
        size_t k;
        for (k = 1; k < g_aiCount; k++)
            if (g_aiItems[k].kind == 2 && g_aiItems[k].p && ((FireAiSession*)g_aiItems[k].p)->model == i) { fire_aib_sessionFree((FireAiSession*)g_aiItems[k].p); g_aiItems[k].p = NULL; }
        fai_modelFree((FaiModel*)g_aiItems[i].p);
    }
    g_aiItems[i].p = NULL;
    return Bool(true);
}

/* A text about the model: 0 its name, 1 its architecture, 2 its chat template, 3 the kernels of the products (C, AVX2, NEON), 4 the text of its start token. */
static Value fire_ai_ModelText(Value h, Value what, OwnList* list) {
    FaiModel* m = (FaiModel*)fire_aib_find(h, 1);
    const char* t = "";
    if (m) switch (what.i) {
        case 0: t = m->name; break;
        case 1: t = m->arch; break;
        case 2: t = m->chatTemplate ? m->chatTemplate : ""; break;
        case 3: t = g_faiKernels; break;
        case 4:
            if (m->vocab.bos >= 0 && (uint32_t)m->vocab.bos < m->vocab.count) return fire_aib_str(m->vocab.tokens[m->vocab.bos].p, m->vocab.tokens[m->vocab.bos].n, list);
            break;
    }
    return fire_aib_str(t, strlen(t), list);
}
/* A number of the model: 0 the context it was trained with, 1 the tokens of its vocabulary, 2 the layers, 3 the width, 4 its parameters. */
static Value fire_ai_ModelNumber(Value h, Value what) {
    FaiModel* m = (FaiModel*)fire_aib_find(h, 1);
    int64_t v = 0;
    if (m) switch (what.i) {
        case 0: v = m->ctxTrain; break;
        case 1: v = m->vocab.count; break;
        case 2: v = m->nLayer; break;
        case 3: v = m->nEmbd; break;
        case 4: { uint64_t i; for (i = 0; i < m->tensorCount; i++) v += m->tensors[i].ne[0] * m->tensors[i].ne[1] * m->tensors[i].ne[2] * m->tensors[i].ne[3]; break; }
    }
    return Int(v);
}
/* The number of tokens of `text` (`special`: control tokens written in it count as one). */
static Value fire_ai_CountTokens(Value h, Value text, Value special) {
    FaiModel* m = (FaiModel*)fire_aib_find(h, 1);
    FaiTokens tk = {0};
    size_t n;
    char* s;
    int64_t r;
    if (m == NULL) return Int(-1);
    s = fire_aib_utf8(text, &n);
    if (s == NULL) return Int(fire_aib_fail("Out of memory."));
    r = fai_tokenize(m, s, n, special.kind == K_Bool ? special.i != 0 : 1, 0, &tk) ? (int64_t)tk.n : -1;
    free(s); free(tk.t);
    return Int(r);
}

/* A session of a model: `context` positions (0: 2048), `threads` for the products (0: as many as the machine has, 8 at most). */
static Value fire_ai_SessionNew(Value model, Value context, Value threads) {
    FaiModel* m = (FaiModel*)fire_aib_find(model, 1);
    FireAiSession* a;
    FaiError e;
    int64_t h;
    if (m == NULL) return Int(-1);
    a = (FireAiSession*)calloc(1, sizeof(FireAiSession));
    if (a == NULL) return Int(fire_aib_fail("Out of memory."));
    a->model = model.i;
    fai_poolStart(&a->pool, (int)threads.i);
    e.text[0] = 0;
    a->s = fai_sessionNew(m, &a->pool, (int)context.i, &e);
    if (a->s == NULL) { fai_poolStop(&a->pool); free(a); return Int(fire_aib_fail(e.text)); }
#ifdef _WIN32
    InitializeCriticalSection(&a->lock);
#else
    pthread_mutex_init(&a->lock, NULL);
#endif
    h = fire_aib_add(2, a);
    if (h < 0) fire_aib_sessionFree(a);
    return Int(h);
}

/* Splits `list` at the character 0x1F (the unit separator) into strings. */
static char** fire_aib_split(Value list, int* count) {
    size_t n, i, start = 0;
    char* all = fire_aib_utf8(list, &n);
    char** out;
    int c = 0;
    *count = 0;
    if (all == NULL) return NULL;
    if (n == 0) { free(all); return (char**)calloc(1, sizeof(char*)); }
    out = (char**)calloc(n + 2, sizeof(char*));
    if (out == NULL) { free(all); return NULL; }
    for (i = 0; i <= n; i++)
        if (i == n || all[i] == 0x1F) {
            out[c] = (char*)malloc(i - start + 1);
            if (out[c]) { memcpy(out[c], all + start, i - start); out[c][i - start] = 0; c++; }
            start = i + 1;
        }
    free(all);
    *count = c;
    return out;
}

static int fire_aib_startJob(FireAiSession* a) {
    a->state = FIRE_AI_PROMPT; a->stop = 0;
    a->outLen = 0; a->taken = 0; a->error[0] = 0;
    a->promptTokens = a->promptDone = a->generated = 0; a->promptSeconds = a->genSeconds = 0; a->best = -1;
#ifdef _WIN32
    a->thread = CreateThread(NULL, 0, fire_aib_thread, a, 0, NULL);
    if (a->thread == NULL) { a->state = FIRE_AI_IDLE; return 0; }
#else
    if (pthread_create(&a->thread, NULL, fire_aib_thread, a) != 0) { a->state = FIRE_AI_IDLE; return 0; }
#endif
    a->running = 1;
    return 1;
}

/* Starts generating after `prompt` (its control tokens written as text: <|im_start|> ...). `maxTokens` 0: until the model ends; `stops`: strings (0x1F between
 * them) that end the text; the sampling: temperature (0: the most likely token), top-k, top-p, min-p, the repetition penalty and a seed (0: a random one). */
static Value fire_ai_Generate(Value session, Value prompt, Value maxTokens, Value stops, Value temperature, Value topK, Value topP, Value minP, Value penalty, Value seed) {
    FireAiSession* a = (FireAiSession*)fire_aib_find(session, 2);
    FireAiJob* j;
    if (a == NULL) return Bool(false);
    if (fire_aib_busy(a)) { fire_aib_fail("The session is still working (Cancel stops it)."); return Bool(false); }
    fire_aib_join(a, 0);
    fire_aib_freeJob(&a->job);
    j = &a->job;
    j->prompt = fire_aib_utf8(prompt, NULL);
    j->stops = fire_aib_split(stops, &j->stopCount);
    if (j->prompt == NULL || j->stops == NULL) { fire_aib_fail("Out of memory."); return Bool(false); }
    j->maxTokens = (int)maxTokens.i;
    j->sampling.temperature = (float)toR(temperature);
    j->sampling.topK = (int)topK.i;
    j->sampling.topP = (float)toR(topP);
    j->sampling.minP = (float)toR(minP);
    j->sampling.repeatPenalty = (float)toR(penalty);
    j->sampling.repeatLast = 64;
    j->sampling.seed = (uint64_t)seed.i;
    a->s->rng = j->sampling.seed ? (uint64_t)j->sampling.seed * 0x9E3779B97F4A7C15ull + 1 : ((uint64_t)time(NULL) ^ (uint64_t)(uintptr_t)a) * 0x9E3779B97F4A7C15ull + 1;
    if (!fire_aib_startJob(a)) { fire_aib_fail("No thread could be started."); return Bool(false); }
    fire_aib_ok();
    return Bool(true);
}

/* Starts weighing the `options` (0x1F between them) as continuations of `prompt`. `perToken`: the average per token (long options are not punished). */
static Value fire_ai_Choose(Value session, Value prompt, Value options, Value perToken) {
    FireAiSession* a = (FireAiSession*)fire_aib_find(session, 2);
    FireAiJob* j;
    if (a == NULL) return Bool(false);
    if (fire_aib_busy(a)) { fire_aib_fail("The session is still working (Cancel stops it)."); return Bool(false); }
    fire_aib_join(a, 0);
    fire_aib_freeJob(&a->job);
    j = &a->job;
    j->choose = 1;
    j->prompt = fire_aib_utf8(prompt, NULL);
    j->options = fire_aib_split(options, &j->optionCount);
    j->scores = (double*)calloc((size_t)j->optionCount + 1, sizeof(double));
    j->perToken = perToken.i != 0;
    if (j->prompt == NULL || j->options == NULL || j->scores == NULL) { fire_aib_fail("Out of memory."); return Bool(false); }
    if (j->optionCount == 0) { fire_aib_fail("There are no options to choose from."); return Bool(false); }
    if (!fire_aib_startJob(a)) { fire_aib_fail("No thread could be started."); return Bool(false); }
    fire_aib_ok();
    return Bool(true);
}

/* The state of the job: 0 none, 1 computing the prompt, 2 generating, 3 done, 4 failed (Error), 5 cancelled. */
static Value fire_ai_State(Value session) {
    FireAiSession* a = (FireAiSession*)fire_aib_find(session, 2);
    int st;
    if (a == NULL) return Int(-1);
    st = a->state;
    if (a->running && st != FIRE_AI_PROMPT && st != FIRE_AI_GENERATING) fire_aib_join(a, 0);
    return Int(st);
}
/* The text generated since the last call. */
static Value fire_ai_Take(Value session, OwnList* list) {
    FireAiSession* a = (FireAiSession*)fire_aib_find(session, 2);
    Value v;
    if (a == NULL) return fire_aib_str("", 0, list);
    fire_aib_lock(a);
    v = fire_aib_str(a->out ? a->out + a->taken : "", a->outLen - a->taken, list);
    a->taken = a->outLen;
    fire_aib_unlock(a);
    return v;
}
/* Asks the job to stop (it stops after the token it is working on) and waits for it. */
static Value fire_ai_Cancel(Value session) {
    FireAiSession* a = (FireAiSession*)fire_aib_find(session, 2);
    if (a == NULL) return Bool(false);
    fire_aib_join(a, 1);
    if (a->state == FIRE_AI_PROMPT || a->state == FIRE_AI_GENERATING) a->state = FIRE_AI_CANCELLED;
    return Bool(true);
}
static Value fire_ai_Error(Value session, OwnList* list) {
    FireAiSession* a = (FireAiSession*)fire_aib_find(session, 2);
    const char* t = a ? a->error : g_aiError;
    return fire_aib_str(t, strlen(t), list);
}
/* A number of the last job: 0 the tokens of the prompt, 1 those computed of it, 2 the tokens generated, 3 the milliseconds of the prompt, 4 those of the
 * generation, 5 the option chosen (-1: none), 6 the tokens in the cache. */
static Value fire_ai_Stat(Value session, Value what) {
    FireAiSession* a = (FireAiSession*)fire_aib_find(session, 2);
    int64_t v = 0;
    if (a) switch (what.i) {
        case 0: v = a->promptTokens; break;
        case 1: v = a->promptDone; break;
        case 2: v = a->generated; break;
        case 3: v = (int64_t)(a->promptSeconds * 1000); break;
        case 4: v = (int64_t)(a->genSeconds * 1000); break;
        case 5: v = a->best; break;
        case 6: v = a->s->cachedCount; break;
    }
    return Int(v);
}
/* The score (log-probability) of the option `index` of the last choice. */
static Value fire_ai_Score(Value session, Value index) {
    FireAiSession* a = (FireAiSession*)fire_aib_find(session, 2);
    if (a == NULL || !a->job.choose || index.i < 0 || index.i >= a->job.optionCount || fire_aib_busy(a)) return Float((Real)-1e300);
    return Float((Real)a->job.scores[index.i]);
}
/* Forgets what the cache holds (the next prompt is computed from its start). */
static Value fire_ai_Forget(Value session) {
    FireAiSession* a = (FireAiSession*)fire_aib_find(session, 2);
    if (a == NULL || fire_aib_busy(a)) return Bool(false);
    a->s->cachedCount = 0; a->s->logitsValid = 0;
    return Bool(true);
}
static Value fire_ai_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 1; i < g_aiCount; i++) if (g_aiItems[i].p != NULL) n++;
    return Int(n);
}

#endif
