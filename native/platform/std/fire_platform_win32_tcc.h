/* TinyCC on Windows: its winapi headers and kernel32.def are those of Windows XP - no slim reader/writer locks, no condition variables (Vista).
 * Windows has them all the same: the types are declared here and each function is looked up in kernel32 once, at its first call. Included by
 * fire_platform_win32.h right after <windows.h>; with every other compiler (and a TinyCC whose headers have them, SRWLOCK_INIT) it is empty. */
#ifndef FIRE_PLATFORM_WIN32_TCC_H
#define FIRE_PLATFORM_WIN32_TCC_H
#if defined(__TINYC__) && !defined(SRWLOCK_INIT)

typedef struct _RTL_SRWLOCK { PVOID Ptr; } SRWLOCK, *PSRWLOCK;
typedef struct _RTL_CONDITION_VARIABLE { PVOID Ptr; } CONDITION_VARIABLE, *PCONDITION_VARIABLE;
#define SRWLOCK_INIT { 0 }
#define CONDITION_VARIABLE_INIT { 0 }
#define CONDITION_VARIABLE_LOCKMODE_SHARED 0x1

static FARPROC fire_tccK32(const char* name)
{
    static HMODULE k32;
    FARPROC f;
    if (!k32) k32 = GetModuleHandleA("kernel32.dll");
    f = k32 ? GetProcAddress(k32, name) : NULL;
    if (!f) { fprintf(stderr, "fire: kernel32 has no %s (Windows Vista or newer is needed)\n", name); abort(); }
    return f;
}

typedef VOID (WINAPI* fire_tccSrwFn)(PSRWLOCK);
typedef BOOLEAN (WINAPI* fire_tccSrwTryFn)(PSRWLOCK);
typedef VOID (WINAPI* fire_tccCvFn)(PCONDITION_VARIABLE);
typedef BOOL (WINAPI* fire_tccCvSrwFn)(PCONDITION_VARIABLE, PSRWLOCK, DWORD, ULONG);
typedef BOOL (WINAPI* fire_tccCvCsFn)(PCONDITION_VARIABLE, PCRITICAL_SECTION, DWORD);

#define FIRE_TCC_K32(type, name) static type fire_tcc_##name(void) { static type f; if (!f) f = (type)fire_tccK32(#name); return f; }
FIRE_TCC_K32(fire_tccSrwFn, InitializeSRWLock)
FIRE_TCC_K32(fire_tccSrwFn, AcquireSRWLockExclusive)
FIRE_TCC_K32(fire_tccSrwFn, ReleaseSRWLockExclusive)
FIRE_TCC_K32(fire_tccSrwFn, AcquireSRWLockShared)
FIRE_TCC_K32(fire_tccSrwFn, ReleaseSRWLockShared)
FIRE_TCC_K32(fire_tccSrwTryFn, TryAcquireSRWLockExclusive)
FIRE_TCC_K32(fire_tccCvFn, InitializeConditionVariable)
FIRE_TCC_K32(fire_tccCvFn, WakeConditionVariable)
FIRE_TCC_K32(fire_tccCvFn, WakeAllConditionVariable)
FIRE_TCC_K32(fire_tccCvSrwFn, SleepConditionVariableSRW)
FIRE_TCC_K32(fire_tccCvCsFn, SleepConditionVariableCS)
#undef FIRE_TCC_K32

#define InitializeSRWLock(l) (fire_tcc_InitializeSRWLock()(l))
#define AcquireSRWLockExclusive(l) (fire_tcc_AcquireSRWLockExclusive()(l))
#define ReleaseSRWLockExclusive(l) (fire_tcc_ReleaseSRWLockExclusive()(l))
#define AcquireSRWLockShared(l) (fire_tcc_AcquireSRWLockShared()(l))
#define ReleaseSRWLockShared(l) (fire_tcc_ReleaseSRWLockShared()(l))
#define TryAcquireSRWLockExclusive(l) (fire_tcc_TryAcquireSRWLockExclusive()(l))
#define InitializeConditionVariable(c) (fire_tcc_InitializeConditionVariable()(c))
#define WakeConditionVariable(c) (fire_tcc_WakeConditionVariable()(c))
#define WakeAllConditionVariable(c) (fire_tcc_WakeAllConditionVariable()(c))
#define SleepConditionVariableSRW(c, l, ms, flags) (fire_tcc_SleepConditionVariableSRW()((c), (l), (ms), (flags)))
#define SleepConditionVariableCS(c, cs, ms) (fire_tcc_SleepConditionVariableCS()((c), (cs), (ms)))

#endif
#endif
