/* fire native platform package "freertos": the platform layer on FreeRTOS (mutex, condition variable, tasks, task-local storage), in C. The generated file
 * includes the FreeRTOS headers first - which ones and under which names is the target configuration's business (`includes`): `FreeRTOS.h`, `task.h`,
 * `semphr.h` (`freertos/FreeRTOS.h`, ... in ESP-IDF). Settings (all optional, set as defines in the target configuration):
 *   FIRE_THREAD_STACK_BYTES      stack of a fire thread in bytes (default 8192)
 *   FIRE_FREERTOS_STACK_BYTES    xTaskCreate takes the stack in bytes (ESP-IDF) and not in words of StackType_t
 *   FIRE_THREAD_PRIORITY         priority of a fire thread (default: idle priority + 1)
 *   FIRE_THREAD_CORE             pin fire threads to this core (needs xTaskCreatePinnedToCore, ESP-IDF)
 *   FIRE_TLS_INDEX               the slot of the task-local storage pointer that holds the thread state (default 0; needs
 *                                configNUM_THREAD_LOCAL_STORAGE_POINTERS > the index)
 * The program calls fire_main from a task (the entry point of the target configuration); there is no process to end: a fatal error aborts. */
#ifndef FIRE_PLATFORM_FREERTOS_H
#define FIRE_PLATFORM_FREERTOS_H

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include "../std/fire_platform_c.h"

#ifndef portMAX_DELAY
#error "fire platform freertos: include FreeRTOS.h, task.h and semphr.h first (list them under `includes` in the target configuration)"
#endif

#ifndef FIRE_THREAD_STACK_BYTES
#define FIRE_THREAD_STACK_BYTES 8192
#endif
#ifndef FIRE_TLS_INDEX
#define FIRE_TLS_INDEX 0
#endif
#ifndef FIRE_THREAD_PRIORITY
#define FIRE_THREAD_PRIORITY (tskIDLE_PRIORITY + 1)
#endif
#define FIRE_TLS_STRUCT 1   /* no thread-local storage of the compiler: the thread state hangs on the task */

/* A monotonic clock in milliseconds (the tick counter, widened to 64 bits). */
static inline int64_t fire_plat_nowMs(void) {
    static uint32_t last = 0;
    static uint64_t high = 0;
    uint32_t now = (uint32_t)xTaskGetTickCount();
    if (now < last) high += (uint64_t)1 << 32;
    last = now;
    return (int64_t)((high + now) * (uint64_t)portTICK_PERIOD_MS);
}

/* The wall clock: the C library's time(), which a board sets from an RTC or SNTP, refined by the tick counter (time() counts whole seconds). When time()
 * and the counter drift apart by more than a second (the clock was set), the counter is anchored again. */
static inline int64_t fire_plat_unixMicros(void) {
    static int64_t anchor = 0;
    static int anchored = 0;
    int64_t seconds = (int64_t)time(NULL) * 1000000;
    int64_t ticks = fire_plat_nowMs() * 1000;
    int64_t drift = seconds - (anchor + ticks);
    if (!anchored || drift > 1000000 || drift < -1000000) { anchor = seconds - ticks; anchored = 1; }
    return anchor + ticks;
}

/* The calling task sleeps (at least one tick). */
static inline void fire_plat_sleepMs(int64_t ms) {
    TickType_t ticks = pdMS_TO_TICKS(ms);
    vTaskDelay(ticks ? ticks : 1);
}

/* There is no process to end: the output is flushed and the program stops with a panic (ESP-IDF: reboot). */
FIRE_NORETURN static inline void fire_plat_exit(int code) {
    (void)code;
    abort();
}

#ifdef FIRE_THREADS
typedef struct { SemaphoreHandle_t h; } fire_mutex;
static inline void fire_mutex_init(fire_mutex* m) { m->h = xSemaphoreCreateMutex(); }
static inline void fire_mutex_lock(fire_mutex* m) { xSemaphoreTake(m->h, portMAX_DELAY); }
static inline void fire_mutex_unlock(fire_mutex* m) { xSemaphoreGive(m->h); }

/* A condition variable out of binary semaphores: every waiter queues its own semaphore, `notifyAll` gives all of them. The list is protected by the mutex
 * that the caller holds (wait/waitFor/notifyAll are always called with it). */
typedef struct fire_cond_waiter { SemaphoreHandle_t sem; struct fire_cond_waiter* next; int queued; } fire_cond_waiter;
typedef struct { fire_cond_waiter* head; } fire_cond;
static inline void fire_cond_init(fire_cond* c) { c->head = NULL; }
static inline int fire_cond_block(fire_cond* c, fire_mutex* m, TickType_t ticks) {
    fire_cond_waiter w;
    int signalled;
    w.sem = xSemaphoreCreateBinary();
    w.next = c->head;
    w.queued = 1;
    c->head = &w;
    fire_mutex_unlock(m);
    signalled = xSemaphoreTake(w.sem, ticks) == pdTRUE;
    fire_mutex_lock(m);
    if (!signalled) {
        if (w.queued) {   /* timed out and nobody gave it: leave the queue */
            fire_cond_waiter** at;
            for (at = &c->head; *at; at = &(*at)->next)
                if (*at == &w) { *at = w.next; break; }
        } else signalled = xSemaphoreTake(w.sem, 0) == pdTRUE;   /* notified just as the timeout ran out */
    }
    vSemaphoreDelete(w.sem);
    return signalled;
}
static inline void fire_cond_wait(fire_cond* c, fire_mutex* m) { fire_cond_block(c, m, portMAX_DELAY); }
static inline int fire_cond_waitFor(fire_cond* c, fire_mutex* m, int64_t ms) {
    TickType_t ticks = pdMS_TO_TICKS(ms);
    if (ticks == 0) ticks = 1;
    return fire_cond_block(c, m, ticks);
}
static inline void fire_cond_notifyAll(fire_cond* c) {
    fire_cond_waiter* w = c->head;
    while (w) {
        fire_cond_waiter* next = w->next;
        w->queued = 0;
        xSemaphoreGive(w->sem);
        w = next;
    }
    c->head = NULL;
}

typedef void (*fire_thread_fn)(void*);
typedef struct fire_thread { fire_thread_fn fn; void* arg; SemaphoreHandle_t done; } fire_thread;

static inline void fire_task_body(void* p) {
    fire_thread* t = (fire_thread*)p;
    t->fn(t->arg);
    xSemaphoreGive(t->done);
    vTaskDelete(NULL);
}

static inline fire_thread* fire_thread_start(fire_thread_fn fn, void* arg, uint32_t stackBytes) {
    fire_thread* t = (fire_thread*)malloc(sizeof(fire_thread));
    uint32_t bytes = stackBytes ? stackBytes : (uint32_t)FIRE_THREAD_STACK_BYTES;
    uint32_t depth;
    BaseType_t ok;
    if (!t) { fputs("fire runtime error: Out of memory.\n", stderr); abort(); }
    t->fn = fn;
    t->arg = arg;
    t->done = xSemaphoreCreateBinary();
#ifdef FIRE_FREERTOS_STACK_BYTES
    depth = bytes;
#else
    depth = bytes / (uint32_t)sizeof(StackType_t);
#endif
#ifdef FIRE_THREAD_CORE
    ok = xTaskCreatePinnedToCore(fire_task_body, "fire", depth, t, FIRE_THREAD_PRIORITY, NULL, FIRE_THREAD_CORE);
#else
    ok = xTaskCreate(fire_task_body, "fire", depth, t, FIRE_THREAD_PRIORITY, NULL);
#endif
    if (ok != pdPASS) { fputs("fire runtime error: could not create a task (out of memory?).\n", stderr); abort(); }
    return t;
}

static inline void fire_thread_join(fire_thread* t) {
    xSemaphoreTake(t->done, portMAX_DELAY);
    vSemaphoreDelete(t->done);
    free(t);
}

static inline void* fire_tls_get(void) { return pvTaskGetThreadLocalStoragePointer(NULL, FIRE_TLS_INDEX); }
static inline void fire_tls_set(void* p) { vTaskSetThreadLocalStoragePointer(NULL, FIRE_TLS_INDEX, p); }
#endif

#endif
