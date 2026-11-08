/* fire native platform layer: a scheduler of its own for targets whose system has no threads (ROADMAP 13.2b: DOS, BIOS, UEFI, Win16, boards without an RTOS).
 * It implements the threads of the platform interface (fire_platform_c.h: fire_mutex, fire_cond, fire_thread_start/join, fire_tls_get/set) with "green"
 * threads in one process: every fire thread has a stack of its own, and the switch happens only where the running thread gives up its turn -
 *   * when it waits: a mutex that another thread holds, a condition (also with a time limit), `join`, `Sleep` (fire_sched_sleepMs);
 *   * at the safepoints of the runtime: the GIL is handed over every 4096 of them (gilYield) - and earlier when a timer asked for it (fire_sched_preempt(),
 *     to be called from a timer interrupt: it only sets a flag; nothing is switched inside an interrupt - DOS and the C library are not reentrant).
 * A mutex hands itself to the first thread that waits for it (fair: the GIL goes round). When nobody can run, the platform waits for real (FIRE_SCHED_IDLE).
 *
 * The platform package defines before it includes this file:
 *   fire_plat_nowMs()            the clock
 *   FIRE_SCHED_IDLE(ms)          wait for real (nothing can run until then; `hlt`, delay(), nanosleep)
 *   the switch of the context: includes one of sched/fire_ctx_*.h (fire_ctx, fire_ctx_make, fire_ctx_swap)
 * and uses fire_sched_sleepMs as its fire_plat_sleepMs. */
#ifndef FIRE_SCHED_H
#define FIRE_SCHED_H

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#define FIRE_TLS_STRUCT 1   /* (the state of a fire thread hangs on its task: fire_tls_get/set) */

#ifndef FIRE_SCHED_STACK_BYTES
#define FIRE_SCHED_STACK_BYTES 65536   /* (a platform with less memory sets less: dos16 16 KB) */
#endif

typedef void (*fire_thread_fn)(void*);

typedef struct fire_task {
    fire_ctx ctx;
    void* stack;
    fire_thread_fn fn;
    void* arg;
    void* tls;
    int64_t wakeAt;              /* sleeping or waiting with a limit: when (-1: no limit) */
    struct fire_task* next;      /* in the run queue or in the list of what it waits for */
    struct fire_task* sleepNext; /* in the list of the sleepers (waits with a limit) */
    void* waitingOn;             /* the mutex or condition it waits for (NULL: none) */
    struct fire_task* joiner;    /* the thread that waits for its end */
    uint8_t done, signalled, sleeping;
} fire_task;

typedef struct { fire_task* head; fire_task* tail; } fire_taskq;
typedef struct { fire_task* owner; fire_taskq waiting; } fire_mutex;
typedef struct { fire_taskq waiting; } fire_cond;
typedef struct fire_thread { fire_task* task; } fire_thread;

/* The scheduler's state (one process, one copy: a static of the generated program). */
static fire_task fire_sched_main;           /* the program's own thread: its stack is the process's */
static fire_task* fire_sched_cur = NULL;
static fire_taskq fire_sched_ready;
static fire_task* fire_sched_sleepers = NULL;
static fire_task* fire_sched_dead = NULL;   /* a finished task whose stack is freed by the next switch (not on its own stack) */
#ifndef FIRE_SCHED_WANT_DECLARED   /* (a context that has a timer of its own declares it: the interrupt sets it) */
static volatile int fire_sched_wantSwitch = 0;
#endif
/* the stack of a task: the platform may allocate it itself (Windows 3.x: a segment of its own with the header of a stack) */
#ifndef FIRE_SCHED_STACK_ALLOC
#define FIRE_SCHED_STACK_ALLOC(size) malloc(size)
#define FIRE_SCHED_STACK_FREE(p) free(p)
#endif
#ifndef FIRE_SCHED_ON_FIRST_THREAD
#define FIRE_SCHED_ON_FIRST_THREAD() ((void)0)   /* (DOS: the timer interrupt is installed then) */
#endif

static inline void fire_taskq_push(fire_taskq* q, fire_task* t) { t->next = NULL; if (q->tail) q->tail->next = t; else q->head = t; q->tail = t; }
static inline fire_task* fire_taskq_pop(fire_taskq* q) { fire_task* t = q->head; if (t) { q->head = t->next; if (!q->head) q->tail = NULL; t->next = NULL; } return t; }
static inline void fire_taskq_remove(fire_taskq* q, fire_task* t) {
    fire_task* p = NULL;
    fire_task* c = q->head;
    while (c && c != t) { p = c; c = c->next; }
    if (!c) return;
    if (p) p->next = c->next; else q->head = c->next;
    if (q->tail == c) q->tail = p;
    c->next = NULL;
}

static inline fire_task* fire_sched_self(void) {
    if (!fire_sched_cur) { fire_sched_cur = &fire_sched_main; fire_sched_main.wakeAt = -1; }
    return fire_sched_cur;
}

static inline void fire_sched_unsleep(fire_task* t) {
    fire_task** p = &fire_sched_sleepers;
    while (*p && *p != t) p = &(*p)->sleepNext;
    if (*p) *p = t->sleepNext;
    t->sleepNext = NULL;
    t->sleeping = 0;
}
static inline void fire_sched_addSleeper(fire_task* t, int64_t at) {
    t->wakeAt = at;
    t->sleeping = 1;
    t->sleepNext = fire_sched_sleepers;
    fire_sched_sleepers = t;
}

/* The sleepers whose time has come are ready again (a waiter with a limit leaves the list it waited in: it timed out). */
static inline void fire_sched_wakeDue(void) {
    int64_t now = fire_plat_nowMs();
    fire_task** p = &fire_sched_sleepers;
    while (*p) {
        fire_task* t = *p;
        if (t->wakeAt <= now) {
            *p = t->sleepNext;
            t->sleepNext = NULL;
            t->sleeping = 0;
            if (t->waitingOn) { fire_taskq_remove((fire_taskq*)t->waitingOn, t); t->waitingOn = NULL; t->signalled = 0; }
            fire_taskq_push(&fire_sched_ready, t);
        } else p = &t->sleepNext;
    }
}

/* Runs the next ready task; the running one must already be queued somewhere (ready, a wait list, the sleepers) or be done. */
static inline void fire_sched_switch(void) {
    fire_task* self = fire_sched_self();
    fire_task* next;
    fire_sched_wantSwitch = 0;
    for (;;) {
        fire_sched_wakeDue();
        next = fire_taskq_pop(&fire_sched_ready);
        if (next) break;
        if (!fire_sched_sleepers) {
            fputs("fire runtime error: every thread waits and none can wake up (a deadlock).\n", stderr);
            fflush(stderr);
            exit(1);
        }
        {
            int64_t first = fire_sched_sleepers->wakeAt, now = fire_plat_nowMs();
            fire_task* s;
            for (s = fire_sched_sleepers; s; s = s->sleepNext) if (s->wakeAt < first) first = s->wakeAt;
            if (first > now) FIRE_SCHED_IDLE(first - now);
        }
    }
    if (next == self) return;
    fire_sched_cur = next;
    fire_ctx_swap(&self->ctx, &next->ctx);
    /* (back here: this task runs again) */
    if (fire_sched_dead) { FIRE_SCHED_STACK_FREE(fire_sched_dead->stack); free(fire_sched_dead); fire_sched_dead = NULL; }
}

/* Gives the turn to the others (at a safepoint, or when a timer asked for it): the running task goes to the end of the queue. */
static inline void fire_sched_yield(void) {
    fire_task* self = fire_sched_self();
    if (!fire_sched_ready.head && !fire_sched_sleepers) return;
    fire_taskq_push(&fire_sched_ready, self);
    fire_sched_switch();
}

/* From a timer interrupt: the next safepoint gives the turn away. Only a flag - nothing else may happen inside an interrupt. */
static inline void fire_sched_preempt(void) { fire_sched_wantSwitch = 1; }

/* Waits in `q` (a mutex or a condition), up to `ms` milliseconds (-1: no limit). 1: woken by the other side, 0: the time ran out. */
static inline int fire_sched_waitIn(fire_taskq* q, int64_t ms) {
    fire_task* self = fire_sched_self();
    self->signalled = 0;
    self->waitingOn = q;
    fire_taskq_push(q, self);
    if (ms >= 0) fire_sched_addSleeper(self, fire_plat_nowMs() + ms);
    fire_sched_switch();
    if (self->sleeping) fire_sched_unsleep(self);
    self->waitingOn = NULL;
    return self->signalled;
}
static inline void fire_sched_wakeTask(fire_task* t) {
    t->signalled = 1;
    t->waitingOn = NULL;
    if (t->sleeping) fire_sched_unsleep(t);
    fire_taskq_push(&fire_sched_ready, t);
}

/* ---- the interface of the platform ---- */
static inline void fire_mutex_init(fire_mutex* m) { m->owner = NULL; m->waiting.head = m->waiting.tail = NULL; }
static inline void fire_mutex_lock(fire_mutex* m) {
    fire_task* self = fire_sched_self();
    if (!m->owner) { m->owner = self; return; }
    fire_sched_waitIn(&m->waiting, -1);   /* (the owner hands it over: fire_mutex_unlock) */
}
static inline void fire_mutex_unlock(fire_mutex* m) {
    fire_task* next = fire_taskq_pop(&m->waiting);
    m->owner = next;
    if (next) fire_sched_wakeTask(next);
}

static inline void fire_cond_init(fire_cond* c) { c->waiting.head = c->waiting.tail = NULL; }
/* (the mutex is held: released while waiting, held again afterwards) */
static inline int fire_cond_waitFor(fire_cond* c, fire_mutex* m, int64_t ms) {
    int woken;
    fire_mutex_unlock(m);
    woken = fire_sched_waitIn(&c->waiting, ms < 0 ? 0 : ms);
    fire_mutex_lock(m);
    return woken;
}
static inline void fire_cond_wait(fire_cond* c, fire_mutex* m) {
    fire_mutex_unlock(m);
    fire_sched_waitIn(&c->waiting, -1);
    fire_mutex_lock(m);
}
static inline void fire_cond_notifyAll(fire_cond* c) {
    fire_task* t;
    while ((t = fire_taskq_pop(&c->waiting)) != NULL) fire_sched_wakeTask(t);
}

static inline void fire_sched_entry(void) {
    fire_task* self = fire_sched_cur;
    self->fn(self->arg);
    self->done = 1;
    if (self->joiner) { fire_sched_wakeTask(self->joiner); self->joiner = NULL; }
    fire_sched_switch();   /* (never back: the task is in no queue) */
}

static inline fire_thread* fire_thread_start(fire_thread_fn fn, void* arg, uint32_t stackBytes) {
    fire_thread* t = (fire_thread*)malloc(sizeof(fire_thread));
    fire_task* k = (fire_task*)calloc(1, sizeof(fire_task));
    size_t size = stackBytes ? stackBytes : FIRE_SCHED_STACK_BYTES;
    fire_sched_self();
    FIRE_SCHED_ON_FIRST_THREAD();
    if (!t || !k || !(k->stack = FIRE_SCHED_STACK_ALLOC(size))) { fputs("fire runtime error: Out of memory (the stack of a thread).\n", stderr); exit(1); }
    k->fn = fn;
    k->arg = arg;
    k->wakeAt = -1;
    fire_ctx_make(&k->ctx, k->stack, size, fire_sched_entry);
    t->task = k;
    fire_taskq_push(&fire_sched_ready, k);
    return t;
}
static inline void fire_thread_join(fire_thread* t) {
    fire_task* k = t->task;
    if (!k->done) {
        fire_task* self = fire_sched_self();
        k->joiner = self;
        fire_sched_switch();   /* (the end of k wakes it: fire_sched_entry) */
    }
    if (k != fire_sched_cur) { if (fire_sched_dead == k) fire_sched_dead = NULL; FIRE_SCHED_STACK_FREE(k->stack); free(k); }
    free(t);
}

static inline void* fire_tls_get(void) { return fire_sched_self()->tls; }
static inline void fire_tls_set(void* p) { fire_sched_self()->tls = p; }

/* `Sleep` of a fire thread: the others run meanwhile. */
static inline void fire_sched_sleepMs(int64_t ms) {
    fire_task* self = fire_sched_self();
    if (ms < 0) ms = 0;
    fire_sched_addSleeper(self, fire_plat_nowMs() + ms);
    fire_sched_switch();
}

/* A safepoint asks: has a timer asked for a switch? */
static inline int fire_sched_wanted(void) { return fire_sched_wantSwitch; }

/* the hooks of the runtime's safepoints (fire_platform_c.h) */
#undef FIRE_PLAT_YIELD
#undef FIRE_PLAT_PREEMPTED
#define FIRE_PLAT_YIELD() fire_sched_yield()
#ifdef FIRE_SCHED_PREEMPTED
#define FIRE_PLAT_PREEMPTED() FIRE_SCHED_PREEMPTED()   /* (a platform without a timer interrupt asks its clock) */
#else
#define FIRE_PLAT_PREEMPTED() fire_sched_wantSwitch
#endif

#endif
