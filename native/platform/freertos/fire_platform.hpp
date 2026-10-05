// fire native platform package "freertos": the platform layer on FreeRTOS (mutex, condition variable, tasks, task-local storage). The generated file includes
// the FreeRTOS headers first - which ones and under which names is the target configuration's business (`includes`): `FreeRTOS.h`, `task.h`, `semphr.h`
// (`freertos/FreeRTOS.h`, ... in ESP-IDF). Settings (all optional, set as defines in the target configuration):
//   FIRE_THREAD_STACK_BYTES      stack of a fire thread in bytes (default 8192)
//   FIRE_FREERTOS_STACK_BYTES    xTaskCreate takes the stack in bytes (ESP-IDF) and not in words of StackType_t
//   FIRE_THREAD_PRIORITY         priority of a fire thread (default: idle priority + 1)
//   FIRE_THREAD_CORE             pin fire threads to this core (needs xTaskCreatePinnedToCore, ESP-IDF)
//   FIRE_TLS_INDEX               the slot of the task-local storage pointer that holds the thread state (default 0; needs
//                                configNUM_THREAD_LOCAL_STORAGE_POINTERS > the index)
// The program calls fire_main from a task (the entry point of the target configuration); there is no process to end: a fatal error aborts.
#pragma once

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <ctime>

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
#define FIRE_TLS_STRUCT 1   // no `thread_local`: the thread state hangs on the task

namespace fire {
namespace plat {

/// A monotonic clock in milliseconds (the tick counter, widened to 64 bits).
inline int64_t nowMs() {
    static uint32_t last = 0;
    static uint64_t high = 0;
    uint32_t now = (uint32_t)xTaskGetTickCount();
    if (now < last) high += (uint64_t)1 << 32;
    last = now;
    return (int64_t)((high + now) * (uint64_t)portTICK_PERIOD_MS);
}

/// The wall clock (microseconds since 1970-01-01 UTC): the C library's time(), which a board sets from an RTC or SNTP, refined by the tick counter (time() counts
/// whole seconds). When time() and the counter drift apart by more than a second (the clock was set), the counter is anchored again.
inline int64_t unixMicros() {
    static int64_t anchor = 0;
    static bool anchored = false;
    int64_t seconds = (int64_t)std::time(nullptr) * 1000000;
    int64_t ticks = nowMs() * 1000;
    int64_t drift = seconds - (anchor + ticks);
    if (!anchored || drift > 1000000 || drift < -1000000) { anchor = seconds - ticks; anchored = true; }
    return anchor + ticks;
}

/// The calling task sleeps (at least one tick).
inline void sleepMs(int64_t ms) {
    TickType_t ticks = pdMS_TO_TICKS(ms);
    vTaskDelay(ticks ? ticks : 1);
}

/// There is no process to end: the output is flushed and the program stops with a panic (ESP-IDF: reboot).
[[noreturn]] inline void exitProcess(int /*code*/) {
    std::abort();
}

#ifdef FIRE_THREADS
class Mutex {
public:
    Mutex() : h_(xSemaphoreCreateMutex()) {}
    ~Mutex() { vSemaphoreDelete(h_); }
    Mutex(const Mutex&) = delete;
    Mutex& operator=(const Mutex&) = delete;
    void lock() { xSemaphoreTake(h_, portMAX_DELAY); }
    void unlock() { xSemaphoreGive(h_); }
private:
    SemaphoreHandle_t h_;
};

/// A condition variable out of binary semaphores: every waiter queues its own semaphore, `notifyAll` gives all of them. The list is protected
/// by the mutex that the caller holds (wait/waitFor/notifyAll are always called with it).
class CondVar {
public:
    void wait(Mutex& m) { block(m, portMAX_DELAY); }
    bool waitFor(Mutex& m, int64_t ms) {
        TickType_t ticks = pdMS_TO_TICKS(ms);
        if (ticks == 0) ticks = 1;
        return block(m, ticks);
    }
    void notifyAll() {
        for (Waiter* w = head_; w;) {
            Waiter* next = w->next;
            w->queued = false;
            xSemaphoreGive(w->sem);
            w = next;
        }
        head_ = nullptr;
    }
private:
    struct Waiter {
        SemaphoreHandle_t sem;
        Waiter* next;
        bool queued;
    };
    bool block(Mutex& m, TickType_t ticks) {
        Waiter w = {xSemaphoreCreateBinary(), head_, true};
        head_ = &w;
        m.unlock();
        bool signalled = xSemaphoreTake(w.sem, ticks) == pdTRUE;
        m.lock();
        if (!signalled) {
            if (w.queued) {   // timed out and nobody gave it: leave the queue
                for (Waiter** at = &head_; *at; at = &(*at)->next)
                    if (*at == &w) { *at = w.next; break; }
            } else signalled = xSemaphoreTake(w.sem, 0) == pdTRUE;   // notified just as the timeout ran out
        }
        vSemaphoreDelete(w.sem);
        return signalled;
    }
    Waiter* head_ = nullptr;
};

using ThreadFn = void (*)(void*);
struct Thread {
    ThreadFn fn;
    void* arg;
    SemaphoreHandle_t done;
};

inline void taskBody(void* p) {
    Thread* t = static_cast<Thread*>(p);
    t->fn(t->arg);
    xSemaphoreGive(t->done);
    vTaskDelete(nullptr);
}

inline Thread* threadStart(ThreadFn fn, void* arg, uint32_t stackBytes) {
    Thread* t = new Thread{fn, arg, xSemaphoreCreateBinary()};
    uint32_t bytes = stackBytes ? stackBytes : (uint32_t)FIRE_THREAD_STACK_BYTES;
#ifdef FIRE_FREERTOS_STACK_BYTES
    uint32_t depth = bytes;
#else
    uint32_t depth = bytes / (uint32_t)sizeof(StackType_t);
#endif
#ifdef FIRE_THREAD_CORE
    BaseType_t ok = xTaskCreatePinnedToCore(taskBody, "fire", depth, t, FIRE_THREAD_PRIORITY, nullptr, FIRE_THREAD_CORE);
#else
    BaseType_t ok = xTaskCreate(taskBody, "fire", depth, t, FIRE_THREAD_PRIORITY, nullptr);
#endif
    if (ok != pdPASS) { std::fputs("fire runtime error: could not create a task (out of memory?).\n", stderr); std::abort(); }
    return t;
}

inline void threadJoin(Thread* t) {
    xSemaphoreTake(t->done, portMAX_DELAY);
    vSemaphoreDelete(t->done);
    delete t;
}

inline void* tlsGet() { return pvTaskGetThreadLocalStoragePointer(nullptr, FIRE_TLS_INDEX); }
inline void tlsSet(void* p) { vTaskSetThreadLocalStoragePointer(nullptr, FIRE_TLS_INDEX, p); }
#endif

}  // namespace plat
}  // namespace fire
