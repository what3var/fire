// fire native platform layer - the portable part of the hosted platforms (Linux, macOS, Windows): C++ standard library threads.
//
// A platform header offers the runtime (fire_rt.hpp) these, in namespace fire::plat:
//   int64_t nowMs()                    a monotonic clock in milliseconds
//   [[noreturn]] void exitProcess(int) ends the program (the streams are flushed already)
//   int64_t unixMicros()               the wall clock: microseconds since 1970-01-01 UTC (the time bridge)
//   void sleepMs(int64_t)              the calling thread sleeps (only without fire threads; with them the runtime waits on a CondVar)
// and with FIRE_THREADS (fire threads, see docs/NATIVE_BACKEND.md):
//   class Mutex      lock(), unlock()                      (not recursive)
//   class CondVar    wait(Mutex&), bool waitFor(Mutex&, ms) (false: timed out), notifyAll()   - always called with the mutex held
//   Thread*          threadStart(fn, arg, stackBytes) / threadJoin(Thread*)   (stackBytes 0: the default)
// and, where the compiler has no `thread_local` (FIRE_TLS_STRUCT, e.g. FreeRTOS tasks):
//   void* tlsGet() / void tlsSet(void*)   one pointer per thread of execution
#pragma once

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <thread>
#ifdef FIRE_THREADS
#include <condition_variable>
#include <mutex>
#endif

namespace fire {
namespace plat {

inline int64_t nowMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

inline int64_t unixMicros() {
    return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

inline void sleepMs(int64_t ms) { std::this_thread::sleep_for(std::chrono::milliseconds(ms)); }

/// Ends the program. With threads the other threads must not run static destructors under their feet: no cleanup.
[[noreturn]] inline void exitProcess(int code) {
#ifdef FIRE_THREADS
    std::_Exit(code);
#else
    std::exit(code);
#endif
}

#ifdef FIRE_THREADS
class Mutex {
public:
    void lock() { m_.lock(); }
    void unlock() { m_.unlock(); }
    std::mutex& native() { return m_; }
private:
    std::mutex m_;
};

class CondVar {
public:
    void wait(Mutex& m) {
        std::unique_lock<std::mutex> lk(m.native(), std::adopt_lock);
        cv_.wait(lk);
        lk.release();
    }
    bool waitFor(Mutex& m, int64_t ms) {
        std::unique_lock<std::mutex> lk(m.native(), std::adopt_lock);
        bool signalled = cv_.wait_for(lk, std::chrono::milliseconds(ms)) == std::cv_status::no_timeout;
        lk.release();
        return signalled;
    }
    void notifyAll() { cv_.notify_all(); }
private:
    std::condition_variable cv_;
};

using ThreadFn = void (*)(void*);
struct Thread { std::thread t; };
inline Thread* threadStart(ThreadFn fn, void* arg, uint32_t /*stackBytes*/) { return new Thread{std::thread(fn, arg)}; }
inline void threadJoin(Thread* t) { t->t.join(); delete t; }
#endif

}  // namespace plat
}  // namespace fire
