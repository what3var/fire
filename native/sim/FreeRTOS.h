/* A FreeRTOS simulator on top of pthreads: the part of the FreeRTOS API that the fire platform package "freertos" uses (tasks, binary semaphores,
 * mutexes, task-local storage pointers, ticks), with the semantics of FreeRTOS (time in ticks, `portMAX_DELAY`, a mutex is not recursive). To try a
 * FreeRTOS build of a fire program on a PC and for the tests - it is not FreeRTOS. 1 tick = 1 millisecond. */
#pragma once
/* (C11; it compiles as C++ as well: the natives of a FreeRTOS build in C++ include it too) */

#ifndef _POSIX_C_SOURCE
#define _POSIX_C_SOURCE 200809L
#endif
#include <pthread.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

#include <stdint.h>
#include <stdlib.h>

typedef uint32_t TickType_t;
typedef long BaseType_t;
typedef unsigned long UBaseType_t;
typedef uint8_t StackType_t;   /* (the stack depth of the simulator is in bytes) */
typedef void* TaskHandle_t;
typedef void (*TaskFunction_t)(void*);

#define pdTRUE 1
#define pdFALSE 0
#define pdPASS 1
#define pdFAIL 0
#define portMAX_DELAY 0xFFFFFFFFu
#define portTICK_PERIOD_MS 1
#define pdMS_TO_TICKS(ms) ((TickType_t)(ms))
#define tskIDLE_PRIORITY 0
#define configNUM_THREAD_LOCAL_STORAGE_POINTERS 4

#if defined(__cplusplus)
#define FIRE_SIM_TLS thread_local
#else
#define FIRE_SIM_TLS _Thread_local
#endif

typedef struct fire_sim_Sem {
    pthread_mutex_t m;
    pthread_cond_t cv;
    int count;
    int maxCount;
} fire_sim_Sem;

static inline fire_sim_Sem* fire_sim_semCreate(int initial, int maxCount) {
    fire_sim_Sem* s = (fire_sim_Sem*)calloc(1, sizeof(fire_sim_Sem));
    pthread_condattr_t attr;
    if (!s) abort();
    pthread_mutex_init(&s->m, NULL);
    pthread_condattr_init(&attr);
    pthread_condattr_setclock(&attr, CLOCK_MONOTONIC);
    pthread_cond_init(&s->cv, &attr);
    pthread_condattr_destroy(&attr);
    s->count = initial;
    s->maxCount = maxCount;
    return s;
}

static inline BaseType_t fire_sim_semTake(fire_sim_Sem* s, TickType_t ticks) {
    BaseType_t ok;
    pthread_mutex_lock(&s->m);
    if (ticks == portMAX_DELAY) {
        while (s->count == 0) pthread_cond_wait(&s->cv, &s->m);
    } else if (s->count == 0 && ticks > 0) {
        struct timespec until;
        clock_gettime(CLOCK_MONOTONIC, &until);
        until.tv_sec += ticks / 1000;
        until.tv_nsec += (long)(ticks % 1000) * 1000000L;
        if (until.tv_nsec >= 1000000000L) { until.tv_sec++; until.tv_nsec -= 1000000000L; }
        while (s->count == 0) if (pthread_cond_timedwait(&s->cv, &s->m, &until) != 0) break;
    }
    ok = s->count > 0;
    if (ok) s->count--;
    pthread_mutex_unlock(&s->m);
    return ok ? pdTRUE : pdFALSE;
}

static inline BaseType_t fire_sim_semGive(fire_sim_Sem* s) {
    BaseType_t ok;
    pthread_mutex_lock(&s->m);
    ok = s->count < s->maxCount;
    if (ok) { s->count++; pthread_cond_signal(&s->cv); }
    pthread_mutex_unlock(&s->m);
    return ok ? pdTRUE : pdFALSE;
}

typedef struct fire_sim_TaskStart { TaskFunction_t fn; void* arg; } fire_sim_TaskStart;
static inline void* fire_sim_taskThread(void* p) {
    fire_sim_TaskStart st = *(fire_sim_TaskStart*)p;
    free(p);
    st.fn(st.arg);
    return NULL;   /* (a task function that returns instead of calling vTaskDelete ends the thread) */
}

static inline void** fire_sim_tlsSlot(UBaseType_t index) {
    static FIRE_SIM_TLS void* slots[configNUM_THREAD_LOCAL_STORAGE_POINTERS];
    return &slots[index];
}

typedef fire_sim_Sem* SemaphoreHandle_t;

/* ---- semphr.h */
static inline SemaphoreHandle_t xSemaphoreCreateBinary(void) { return fire_sim_semCreate(0, 1); }
static inline SemaphoreHandle_t xSemaphoreCreateMutex(void) { return fire_sim_semCreate(1, 1); }
static inline BaseType_t xSemaphoreTake(SemaphoreHandle_t s, TickType_t ticks) { return fire_sim_semTake(s, ticks); }
static inline BaseType_t xSemaphoreGive(SemaphoreHandle_t s) { return fire_sim_semGive(s); }
static inline void vSemaphoreDelete(SemaphoreHandle_t s) { pthread_mutex_destroy(&s->m); pthread_cond_destroy(&s->cv); free(s); }

/* ---- task.h */
static inline BaseType_t xTaskCreate(TaskFunction_t fn, const char* name, uint32_t stackBytes, void* arg, UBaseType_t priority, TaskHandle_t* handle) {
    pthread_t t;
    pthread_attr_t attr;
    int rc;
    fire_sim_TaskStart* start = (fire_sim_TaskStart*)malloc(sizeof(fire_sim_TaskStart));
    (void)name; (void)stackBytes; (void)priority;
    if (!start) abort();
    start->fn = fn;
    start->arg = arg;
    pthread_attr_init(&attr);
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    rc = pthread_create(&t, &attr, fire_sim_taskThread, start);
    pthread_attr_destroy(&attr);
    if (handle) *handle = (TaskHandle_t)t;
    return rc == 0 ? pdPASS : pdFAIL;
}
static inline void vTaskDelete(TaskHandle_t task) { (void)task; pthread_exit(NULL); }   /* (only the calling task: vTaskDelete(NULL)) */
static inline TickType_t xTaskGetTickCount(void) {
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (TickType_t)((uint64_t)now.tv_sec * 1000u + (uint64_t)(now.tv_nsec / 1000000L));
}
static inline void vTaskDelay(TickType_t ticks) {
    struct timespec d;
    d.tv_sec = ticks / 1000;
    d.tv_nsec = (long)(ticks % 1000) * 1000000L;
    while (nanosleep(&d, &d) != 0) { }
}
static inline void* pvTaskGetThreadLocalStoragePointer(TaskHandle_t task, BaseType_t index) { (void)task; return *fire_sim_tlsSlot((UBaseType_t)index); }
static inline void vTaskSetThreadLocalStoragePointer(TaskHandle_t task, BaseType_t index, void* value) { (void)task; *fire_sim_tlsSlot((UBaseType_t)index) = value; }
