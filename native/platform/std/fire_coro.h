/* fire native platform layer: coroutines with a stack of their own for the tasks of the runtime (docs/ROADMAP_TASKS.md step 3) - on the hosts, where the
 * workers are real threads (the targets without threads have std/fire_sched.h with its own switch). Any worker may continue a coroutine that another one
 * switched away from.
 *
 *   fire_coro*  fire_coro_new(entry, arg, stackBytes)   a coroutine that will run entry(arg) on a stack of its own (stackBytes 0: FIRE_CORO_STACK_BYTES); NULL: no memory
 *   void        fire_coro_free(c)                        its stack and itself (not the running one)
 *   void        fire_coro_switch(from, to)               saves the running context in `from` (a coroutine, or the worker's own: fire_coro_self()) and runs `to`
 *   fire_coro*  fire_coro_self(void)                     the context of the calling worker thread itself (made on first use; one per thread)
 *   void        fire_coro_selfRelease(void)              lets go of it, at the end of a thread that ran coroutines for a while (fibers: back to a thread)
 *
 * The switch: a few instructions of assembler on x86-64 (System V: Linux, the BSDs, macOS on Intel) - the callee-saved registers, the control words of the
 * floating point; Windows: fibers (CreateFiberEx, SwitchToFiber); anywhere else ucontext. A stack is reserved with a guard page below it (mmap; the system
 * commits its pages when they are used). AddressSanitizer and ThreadSanitizer are told about every switch (__sanitizer_start_switch_fiber, __tsan_switch_to_fiber),
 * else they would see one stack jump into another.
 * An entry must never return: it ends with a switch to another context (the scheduler frees the finished coroutine from another one). */
#ifndef FIRE_CORO_H
#define FIRE_CORO_H

#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#ifndef FIRE_CORO_STACK_BYTES
#define FIRE_CORO_STACK_BYTES (1024u * 1024u)   /* (reserved; only what is used is committed) */
#endif

#if defined(__has_feature)
#if __has_feature(address_sanitizer)
#define FIRE_CORO_ASAN 1
#endif
#if __has_feature(thread_sanitizer)
#define FIRE_CORO_TSAN 1
#endif
#endif
#if defined(__SANITIZE_ADDRESS__) && !defined(FIRE_CORO_ASAN)
#define FIRE_CORO_ASAN 1
#endif
#if defined(__SANITIZE_THREAD__) && !defined(FIRE_CORO_TSAN)
#define FIRE_CORO_TSAN 1
#endif
#ifdef FIRE_CORO_ASAN
#ifdef __cplusplus
extern "C" {
#endif
void __sanitizer_start_switch_fiber(void** fake_stack_save, const void* bottom, size_t size);
void __sanitizer_finish_switch_fiber(void* fake_stack_save, const void** bottom_old, size_t* size_old);
#ifdef __cplusplus
}
#endif
#endif
#ifdef FIRE_CORO_TSAN
#ifdef __cplusplus
extern "C" {
#endif
void* __tsan_get_current_fiber(void);
void* __tsan_create_fiber(unsigned flags);
void __tsan_destroy_fiber(void* fiber);
void __tsan_switch_to_fiber(void* fiber, unsigned flags);
#ifdef __cplusplus
}
#endif
#endif

#if defined(_WIN32)
#define FIRE_CORO_FIBERS 1
#include <windows.h>
#elif (defined(__x86_64__) || defined(_M_X64)) && !defined(FIRE_CORO_UCONTEXT)
#define FIRE_CORO_X64 1
#include <sys/mman.h>
#include <unistd.h>
#else
#ifndef FIRE_CORO_UCONTEXT
#define FIRE_CORO_UCONTEXT 1
#endif
#if defined(__APPLE__) && !defined(_XOPEN_SOURCE)
#define _XOPEN_SOURCE 600
#endif
#include <ucontext.h>
#include <sys/mman.h>
#include <unistd.h>
#endif

#if defined(__GNUC__) || defined(__clang__)
#define FIRE_CORO_TLS __thread
#elif defined(_MSC_VER)
#define FIRE_CORO_TLS __declspec(thread)
#else
#define FIRE_CORO_TLS _Thread_local
#endif

typedef void (*fire_coro_fn)(void*);

typedef struct fire_coro {
#if defined(FIRE_CORO_FIBERS)
    void* fiber;
#elif defined(FIRE_CORO_X64)
    void* sp;              /* the saved stack pointer (what the switch pushed lies above it) */
#else
    ucontext_t uc;
#endif
    void* base;            /* the reserved block: guard page + stack (NULL: a worker's own context) */
    size_t size;           /* ... its size */
    const void* stackLow;  /* the usable stack, for the sanitizers */
    size_t stackSize;
    fire_coro_fn fn;
    void* arg;
#ifdef FIRE_CORO_ASAN
    void* fakeStack;
#endif
#ifdef FIRE_CORO_TSAN
    void* tsanFiber;
#endif
} fire_coro;

#if defined(FIRE_CORO_X64)
/* fire_coro_x64_switch(&from->sp, to->sp): pushes the callee-saved registers and the control words of SSE and the x87, keeps the stack pointer in *from, loads
 * the one of `to` and takes everything back off its stack. fire_coro_x64_start: where a new coroutine begins - its argument in r12, the entry in r13. */
#ifdef __cplusplus
extern "C" {
#endif
void fire_coro_x64_switch(void** fromSp, void* toSp);
void fire_coro_x64_start(void);
#ifdef __cplusplus
}
#endif
#if defined(FIRE_RT_IMPLEMENTATION) || defined(FIRE_CORO_IMPLEMENTATION)
#if defined(__APPLE__)
#define FIRE_CORO_SYM(n) "_" #n
#define FIRE_CORO_FNTYPE(n) ""
#else
#define FIRE_CORO_SYM(n) #n
#define FIRE_CORO_FNTYPE(n) ".type " #n ",@function\n"
#endif
/* (the control words of SSE and x87; TinyCC's assembler has no stmxcsr/ldmxcsr: the same instructions as bytes) */
#ifdef __TINYC__
#define FIRE_CORO_SAVE_FP "    .byte 0x0f, 0xae, 0x1c, 0x24\n    .byte 0xd9, 0x7c, 0x24, 0x04\n"
#define FIRE_CORO_LOAD_FP "    .byte 0x0f, 0xae, 0x14, 0x24\n    .byte 0xd9, 0x6c, 0x24, 0x04\n"
#else
#define FIRE_CORO_SAVE_FP "    stmxcsr (%rsp)\n    fnstcw 4(%rsp)\n"
#define FIRE_CORO_LOAD_FP "    ldmxcsr (%rsp)\n    fldcw 4(%rsp)\n"
#endif
__asm__(
    ".text\n"
    ".globl " FIRE_CORO_SYM(fire_coro_x64_switch) "\n"
    FIRE_CORO_FNTYPE(fire_coro_x64_switch)
    FIRE_CORO_SYM(fire_coro_x64_switch) ":\n"
    "    pushq %rbp\n"
    "    pushq %rbx\n"
    "    pushq %r12\n"
    "    pushq %r13\n"
    "    pushq %r14\n"
    "    pushq %r15\n"
    "    subq $8, %rsp\n"
    FIRE_CORO_SAVE_FP
    "    movq %rsp, (%rdi)\n"
    "    movq %rsi, %rsp\n"
    FIRE_CORO_LOAD_FP
    "    addq $8, %rsp\n"
    "    popq %r15\n"
    "    popq %r14\n"
    "    popq %r13\n"
    "    popq %r12\n"
    "    popq %rbx\n"
    "    popq %rbp\n"
    "    ret\n"
    ".globl " FIRE_CORO_SYM(fire_coro_x64_start) "\n"
    FIRE_CORO_FNTYPE(fire_coro_x64_start)
    FIRE_CORO_SYM(fire_coro_x64_start) ":\n"
    "    movq %r12, %rdi\n"
    "    andq $-16, %rsp\n"
    "    call *%r13\n"
    "    .byte 0x0f, 0x0b\n"   /* (ud2: the main function never returns) */
);
#endif
#endif

/* (the context of the worker thread itself; the context a switch on this thread came from - the arriving side tells AddressSanitizer about it) */
#ifdef FIRE_TLS_EMULATED
#define fire_coro_selfCtx (*(fire_coro**)&FIRE_TLS_SLOT(2))   /* (TinyCC: a slot on a key of the platform - fire_platform_posix.h) */
#else
static FIRE_CORO_TLS fire_coro* fire_coro_selfCtx;
#endif
#ifdef FIRE_CORO_ASAN
static FIRE_CORO_TLS fire_coro* fire_coro_prev;
/* (the slot of the worker it runs on - never inlined: see fire_tlGet in fire_rt.h) */
#if defined(__GNUC__) || defined(__clang__)
__attribute__((noinline))
#endif
static fire_coro** fire_coro_prevSlot(void) { return &fire_coro_prev; }
#if defined(__GNUC__) || defined(__clang__)
__attribute__((noinline))
#endif
static void fire_coro_arrived(void* fakeStack) {
    const void* low;
    size_t size;
    fire_coro* prev = *fire_coro_prevSlot();
    __sanitizer_finish_switch_fiber(fakeStack, &low, &size);
    if (prev && !prev->base) { prev->stackLow = low; prev->stackSize = size; }   /* (a worker's own stack: learnt when it is left the first time) */
}
#endif

static inline fire_coro* fire_coro_self(void) {
    fire_coro* c = fire_coro_selfCtx;
    if (!c) {
        c = (fire_coro*)calloc(1, sizeof(fire_coro));
        if (!c) return NULL;
#if defined(FIRE_CORO_FIBERS)
        c->fiber = IsThreadAFiber() ? GetCurrentFiber() : ConvertThreadToFiberEx(NULL, FIBER_FLAG_FLOAT_SWITCH);
#endif
#ifdef FIRE_CORO_TSAN
        c->tsanFiber = __tsan_get_current_fiber();
#endif
        fire_coro_selfCtx = c;
    }
    return c;
}
static inline void fire_coro_selfRelease(void) {
    fire_coro* c = fire_coro_selfCtx;
    if (!c) return;
#if defined(FIRE_CORO_FIBERS)
    ConvertFiberToThread();
#endif
    fire_coro_selfCtx = NULL;
    free(c);
}

/* (where every coroutine starts: the sanitizers learn that the switch arrived, then the entry) */
static void fire_coro_main(fire_coro* c) {
#ifdef FIRE_CORO_ASAN
    fire_coro_arrived(NULL);
#endif
    c->fn(c->arg);
    abort();   /* (an entry must not return) */
}
#if defined(FIRE_CORO_FIBERS)
static VOID CALLBACK fire_coro_fiberMain(LPVOID p) { fire_coro_main((fire_coro*)p); }
#elif defined(FIRE_CORO_UCONTEXT)
static void fire_coro_ucMain(unsigned int hi, unsigned int lo) { fire_coro_main((fire_coro*)(((uintptr_t)hi << 16 << 16) | (uintptr_t)lo)); }
#endif

#if defined(FIRE_CORO_UCONTEXT)
/* (getcontext returns twice: in a function of its own, so that nothing of the caller is in question) */
static void fire_coro_ucInit(fire_coro* volatile c) {
    getcontext(&c->uc);
    c->uc.uc_stack.ss_sp = (void*)c->stackLow;
    c->uc.uc_stack.ss_size = c->stackSize;
    c->uc.uc_link = NULL;
    makecontext(&c->uc, (void (*)(void))fire_coro_ucMain, 2, (unsigned int)((uintptr_t)c >> 16 >> 16), (unsigned int)(uintptr_t)c);
}
#endif
static inline fire_coro* fire_coro_new(fire_coro_fn fn, void* arg, size_t stackBytes) {
    fire_coro* c = (fire_coro*)calloc(1, sizeof(fire_coro));
    if (!c) return NULL;
    c->fn = fn;
    c->arg = arg;
    if (!stackBytes) stackBytes = FIRE_CORO_STACK_BYTES;
#if defined(FIRE_CORO_FIBERS)
    c->fiber = CreateFiberEx(64 * 1024, stackBytes, FIBER_FLAG_FLOAT_SWITCH, fire_coro_fiberMain, c);
    if (!c->fiber) { free(c); return NULL; }
#else
    {
        size_t page = (size_t)sysconf(_SC_PAGESIZE);
        size_t size = (stackBytes + page - 1) / page * page + page;   /* (+ the guard page below) */
        void* base = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON
#ifdef MAP_NORESERVE
            | MAP_NORESERVE
#endif
#ifdef MAP_STACK
            | MAP_STACK
#endif
            , -1, 0);
        if (base == MAP_FAILED) { free(c); return NULL; }
        mprotect(base, page, PROT_NONE);
        c->base = base;
        c->size = size;
        c->stackLow = (char*)base + page;
        c->stackSize = size - page;
    }
#if defined(FIRE_CORO_X64)
    {
        uintptr_t top = ((uintptr_t)c->stackLow + c->stackSize) & ~(uintptr_t)15;
        void** sp = (void**)top;
        *--sp = NULL;                                     /* (the return address of fire_coro_x64_start: never used) */
        *--sp = (void*)(uintptr_t)fire_coro_x64_start;    /* where the first switch returns to */
        *--sp = NULL;                                     /* rbp */
        *--sp = NULL;                                     /* rbx */
        *--sp = (void*)c;                                 /* r12: the argument */
        *--sp = (void*)(uintptr_t)fire_coro_main;         /* r13: the entry */
        *--sp = NULL;                                     /* r14 */
        *--sp = NULL;                                     /* r15 */
        --sp;
        ((uint32_t*)sp)[0] = 0x1F80u;                     /* MXCSR: the default */
        ((uint32_t*)sp)[1] = 0x037Fu;                     /* the x87 control word: the default */
        c->sp = sp;
    }
#else
    fire_coro_ucInit(c);
#endif
#endif
#ifdef FIRE_CORO_TSAN
    c->tsanFiber = __tsan_create_fiber(0);
#endif
    return c;
}

/* Makes a coroutine that has ended (its entry switched away for the last time) start anew with entry(arg) on the same stack - cheaper than a new one. */
static inline void fire_coro_reset(fire_coro* c, fire_coro_fn fn, void* arg) {
    c->fn = fn;
    c->arg = arg;
#if defined(FIRE_CORO_FIBERS)
    DeleteFiber(c->fiber);
    c->fiber = CreateFiberEx(64 * 1024, c->size ? c->size : FIRE_CORO_STACK_BYTES, FIBER_FLAG_FLOAT_SWITCH, fire_coro_fiberMain, c);
#elif defined(FIRE_CORO_X64)
    {
        uintptr_t top = ((uintptr_t)c->stackLow + c->stackSize) & ~(uintptr_t)15;
        void** sp = (void**)top;
        *--sp = NULL;
        *--sp = (void*)(uintptr_t)fire_coro_x64_start;
        *--sp = NULL;
        *--sp = NULL;
        *--sp = (void*)c;
        *--sp = (void*)(uintptr_t)fire_coro_main;
        *--sp = NULL;
        *--sp = NULL;
        --sp;
        ((uint32_t*)sp)[0] = 0x1F80u;
        ((uint32_t*)sp)[1] = 0x037Fu;
        c->sp = sp;
    }
#else
    fire_coro_ucInit(c);
#endif
#ifdef FIRE_CORO_TSAN
    __tsan_destroy_fiber(c->tsanFiber);
    c->tsanFiber = __tsan_create_fiber(0);
#endif
}

static inline void fire_coro_free(fire_coro* c) {
    if (!c) return;
#ifdef FIRE_CORO_TSAN
    if (c->base || c->fn) __tsan_destroy_fiber(c->tsanFiber);
#endif
#if defined(FIRE_CORO_FIBERS)
    if (c->fn) DeleteFiber(c->fiber);
#else
    if (c->base) munmap(c->base, c->size);
#endif
    free(c);
}

static inline void fire_coro_switch(fire_coro* from, fire_coro* to) {
#ifdef FIRE_CORO_ASAN
    *fire_coro_prevSlot() = from;
    __sanitizer_start_switch_fiber(&from->fakeStack, to->stackLow, to->stackSize);
#endif
#ifdef FIRE_CORO_TSAN
    __tsan_switch_to_fiber(to->tsanFiber, 0);
#endif
#if defined(FIRE_CORO_FIBERS)
    (void)from;   /* (the fiber keeps its own context) */
    SwitchToFiber(to->fiber);
#elif defined(FIRE_CORO_X64)
    fire_coro_x64_switch(&from->sp, to->sp);
#else
    swapcontext(&from->uc, &to->uc);
#endif
#ifdef FIRE_CORO_ASAN
    fire_coro_arrived(from->fakeStack);   /* (back in `from`, perhaps on another worker) */
#endif
}

#endif
