// A FreeRTOS simulator on top of pthreads: the part of the FreeRTOS API that the fire platform package "freertos" uses (tasks, binary semaphores,
// mutexes, task-local storage pointers, ticks), with the semantics of FreeRTOS (time in ticks, `portMAX_DELAY`, a mutex is not recursive). To try a
// FreeRTOS build of a fire program on a PC and for the tests - it is not FreeRTOS. 1 tick = 1 millisecond.
#pragma once

#include <pthread.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

#include <cstdint>
#include <cstdlib>

typedef uint32_t TickType_t;
typedef long BaseType_t;
typedef unsigned long UBaseType_t;
typedef uint8_t StackType_t;   // (the stack depth of the simulator is in bytes)
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

namespace fire_sim {

struct Sem {
    pthread_mutex_t m;
    pthread_cond_t cv;
    int count;
    int maxCount;
};

inline Sem* semCreate(int initial, int maxCount) {
    Sem* s = new Sem();
    pthread_mutex_init(&s->m, nullptr);
    pthread_condattr_t attr;
    pthread_condattr_init(&attr);
    pthread_condattr_setclock(&attr, CLOCK_MONOTONIC);
    pthread_cond_init(&s->cv, &attr);
    pthread_condattr_destroy(&attr);
    s->count = initial;
    s->maxCount = maxCount;
    return s;
}

inline BaseType_t semTake(Sem* s, TickType_t ticks) {
    pthread_mutex_lock(&s->m);
    if (ticks == portMAX_DELAY) {
        while (s->count == 0) pthread_cond_wait(&s->cv, &s->m);
    } else if (s->count == 0 && ticks > 0) {
        timespec until;
        clock_gettime(CLOCK_MONOTONIC, &until);
        until.tv_sec += ticks / 1000;
        until.tv_nsec += (long)(ticks % 1000) * 1000000L;
        if (until.tv_nsec >= 1000000000L) { until.tv_sec++; until.tv_nsec -= 1000000000L; }
        while (s->count == 0) if (pthread_cond_timedwait(&s->cv, &s->m, &until) != 0) break;
    }
    BaseType_t ok = s->count > 0;
    if (ok) s->count--;
    pthread_mutex_unlock(&s->m);
    return ok ? pdTRUE : pdFALSE;
}

inline BaseType_t semGive(Sem* s) {
    pthread_mutex_lock(&s->m);
    BaseType_t ok = s->count < s->maxCount;
    if (ok) { s->count++; pthread_cond_signal(&s->cv); }
    pthread_mutex_unlock(&s->m);
    return ok ? pdTRUE : pdFALSE;
}

struct TaskStart { TaskFunction_t fn; void* arg; };
inline void* taskThread(void* p) {
    TaskStart st = *static_cast<TaskStart*>(p);
    delete static_cast<TaskStart*>(p);
    st.fn(st.arg);
    return nullptr;   // (a task function that returns instead of calling vTaskDelete ends the thread)
}

inline void*& tlsSlot(UBaseType_t index) {
    static thread_local void* slots[configNUM_THREAD_LOCAL_STORAGE_POINTERS] = {};
    return slots[index];
}

}  // namespace fire_sim

typedef fire_sim::Sem* SemaphoreHandle_t;

// ---- semphr.h
inline SemaphoreHandle_t xSemaphoreCreateBinary() { return fire_sim::semCreate(0, 1); }
inline SemaphoreHandle_t xSemaphoreCreateMutex() { return fire_sim::semCreate(1, 1); }
inline BaseType_t xSemaphoreTake(SemaphoreHandle_t s, TickType_t ticks) { return fire_sim::semTake(s, ticks); }
inline BaseType_t xSemaphoreGive(SemaphoreHandle_t s) { return fire_sim::semGive(s); }
inline void vSemaphoreDelete(SemaphoreHandle_t s) { pthread_mutex_destroy(&s->m); pthread_cond_destroy(&s->cv); delete s; }

// ---- task.h
inline BaseType_t xTaskCreate(TaskFunction_t fn, const char*, uint32_t /*stackBytes*/, void* arg, UBaseType_t /*priority*/, TaskHandle_t* handle) {
    pthread_t t;
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    int rc = pthread_create(&t, &attr, fire_sim::taskThread, new fire_sim::TaskStart{fn, arg});
    pthread_attr_destroy(&attr);
    if (handle) *handle = (TaskHandle_t)t;
    return rc == 0 ? pdPASS : pdFAIL;
}
inline void vTaskDelete(TaskHandle_t) { pthread_exit(nullptr); }   // (only the calling task: vTaskDelete(NULL))
inline TickType_t xTaskGetTickCount() {
    timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (TickType_t)((uint64_t)now.tv_sec * 1000u + (uint64_t)(now.tv_nsec / 1000000L));
}
inline void vTaskDelay(TickType_t ticks) { usleep(ticks * 1000u); }
inline void* pvTaskGetThreadLocalStoragePointer(TaskHandle_t, BaseType_t index) { return fire_sim::tlsSlot((UBaseType_t)index); }
inline void vTaskSetThreadLocalStoragePointer(TaskHandle_t, BaseType_t index, void* value) { fire_sim::tlsSlot((UBaseType_t)index) = value; }
