/* fire native bridge "process" (C): the natives behind `#import "process"` (docs/stdlib/process.md) - starting programs, their standard input, output and error as pipes,
 * waiting for them, their exit code, ending them. The fire side (Process, ProcessOptions, ProcessResult, ProcessStream - fire source) is the same in the VM and in a
 * native build; this file is what its `__Proc...` functions do.
 *
 * This file is the one implementation, in C99 ("language": "c"): the native build puts it into the generated program, the virtual machine runs it in a shared
 * library built from it (native/abi/fire_pkg_abi.h) - a C compiler such as TinyCC is enough. POSIX: fork + execvp (an error of exec comes back through a pipe),
 * pipes that do not block; Windows: CreateProcessW with anonymous pipes (PIPE_NOWAIT on the end of the program), a command line quoted by the rules of the C runtime
 * and a Unicode environment block. Where there are no processes (FreeRTOS, ESP32, the browser) every function reports NotSupported.
 *
 * Nothing here waits: reading what is not there yet gives -3, a full input pipe takes what fits (the fire code waits with Sleep between the attempts, so a program
 * that waits for a process does not hold the library and the other fire threads keep running). A process is an integer handle in a table of this bridge. Every
 * function reports an error the same way: a result of -1/false/undefined and the code and message of this thread (`__ProcLastError`, `__ProcLastErrorMessage`, in
 * the bridge error slot of the runtime), from which the fire code throws a ProcessException. The calls are serialised (the lock of the library in the VM, the
 * one lock of the fire threads in a native program): the table needs no lock of its own. */
#ifndef FIRE_BRIDGE_PROCESS_H
#define FIRE_BRIDGE_PROCESS_H

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define FIRE_PROC_WIN32 1
#elif (defined(__unix__) || defined(__APPLE__)) && !defined(__EMSCRIPTEN__) && !defined(FIRE_PLATFORM_FREERTOS_H) && !defined(ESP_PLATFORM)
#define FIRE_PROC_POSIX 1
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
extern char** environ;
#endif

enum { FIRE_PROC_NONE = 0, FIRE_PROC_INVALID_ARGUMENT = 1, FIRE_PROC_INVALID_HANDLE = 2, FIRE_PROC_FILE_NOT_FOUND = 3, FIRE_PROC_PERMISSION = 4,
       FIRE_PROC_NOT_SUPPORTED = 5, FIRE_PROC_OTHER = 6, FIRE_PROC_NOT_EXITED = 7, FIRE_PROC_DIRECTORY_NOT_FOUND = 8 };
enum { FIRE_PROC_REDIRECT_INPUT = 1, FIRE_PROC_REDIRECT_OUTPUT = 2, FIRE_PROC_REDIRECT_ERROR = 4, FIRE_PROC_MERGE_ERROR = 8 };
#define FIRE_PROC_WOULD_BLOCK (-3)

static int64_t fire_proc_fail(int code, const char* message) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s", message);
    return -1;
}
static int64_t fire_proc_fail2(int code, const char* a, const char* b, const char* c) {
    g_ioError.code = code;
    snprintf(g_ioError.message, sizeof g_ioError.message, "%s%s%s", a, b, c);
    return -1;
}
static void fire_proc_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }

/* ---- text: a fire string as UTF-8 (malloc; the caller frees it) ---------------------------------------------------------------------------- */
static char* fire_proc_utf8(Value v) {
    uint32_t n = v.kind == K_String ? strOf(v)->length : 0;
    char* out = (char*)malloc((size_t)n * 3 + 5);   /* (strToUtf8 keeps room for a whole sequence) */
    if (out == NULL) return NULL;
    strToUtf8(v, out, n * 3 + 5);
    return out;
}

/* the strings of an array as UTF-8 (a NULL-terminated array of malloc'd strings); `ok` false when an element is not a string */
static char** fire_proc_strings(Value array, uint32_t* count, int* ok) {
    char** out;
    uint32_t i;
    *count = 0;
    *ok = 1;
    if (array.kind != K_Array) { *ok = array.kind == K_Undefined; return NULL; }
    out = (char**)calloc((size_t)arrOf(array)->length + 1, sizeof(char*));
    if (out == NULL) { *ok = 0; return NULL; }
    for (i = 0; i < arrOf(array)->length; i++) {
        Value item = arrItems(arrOf(array))[i];
        if (item.kind != K_String) { *ok = 0; break; }
        out[i] = fire_proc_utf8(item);
    }
    *count = i;
    return out;
}
static void fire_proc_freeStrings(char** s) {
    size_t i;
    if (s == NULL) return;
    for (i = 0; s[i] != NULL; i++) free(s[i]);
    free(s);
}

/* ---- the table of the processes ------------------------------------------------------------------------------------------------------------- */
typedef struct FireProc {
#if defined(FIRE_PROC_POSIX)
    pid_t pid;
    int in, out, err;
#elif defined(FIRE_PROC_WIN32)
    HANDLE process;
    DWORD pid;
    HANDLE in, out, err;
#endif
    int exited;
    int64_t code;
} FireProc;

static FireProc** g_procTable = NULL;
static size_t g_procCount = 0, g_procCap = 0;
#if defined(FIRE_PROC_POSIX)
static pid_t* g_procOrphans = NULL;   /* closed before they ended: reaped later (no zombies) */
static size_t g_procOrphanCount = 0, g_procOrphanCap = 0;
static void fire_proc_reapOrphans(void) {
    size_t i = 0;
    while (i < g_procOrphanCount) {
        int status;
        pid_t r = waitpid(g_procOrphans[i], &status, WNOHANG);
        if (r == g_procOrphans[i] || (r < 0 && errno == ECHILD)) g_procOrphans[i] = g_procOrphans[--g_procOrphanCount];
        else i++;
    }
}
static void fire_proc_addOrphan(pid_t pid) {
    if (g_procOrphanCount == g_procOrphanCap) {
        size_t cap = g_procOrphanCap ? g_procOrphanCap * 2 : 8;
        pid_t* grown = (pid_t*)realloc(g_procOrphans, sizeof(pid_t) * cap);
        if (grown == NULL) return;
        g_procOrphans = grown;
        g_procOrphanCap = cap;
    }
    g_procOrphans[g_procOrphanCount++] = pid;
}
#endif

static FireProc* fire_proc_find(Value handle) {
    if (handle.kind != K_Int) { fire_proc_fail(FIRE_PROC_INVALID_HANDLE, "That is not a process handle."); return NULL; }
    if (handle.i < 0 || (size_t)handle.i >= g_procCount || g_procTable[handle.i] == NULL) { fire_proc_fail(FIRE_PROC_INVALID_HANDLE, "The process has been closed."); return NULL; }
    return g_procTable[handle.i];
}

static int64_t fire_proc_add(FireProc* p) {
    size_t i;
    for (i = 0; i < g_procCount; i++) if (g_procTable[i] == NULL) { g_procTable[i] = p; return (int64_t)i; }
    if (g_procCount == g_procCap) {
        size_t cap = g_procCap ? g_procCap * 2 : 8;
        FireProc** grown = (FireProc**)realloc(g_procTable, sizeof(FireProc*) * cap);
        if (grown == NULL) return fire_proc_fail(FIRE_PROC_OTHER, "Out of memory.");
        g_procTable = grown;
        g_procCap = cap;
    }
    g_procTable[g_procCount] = p;
    return (int64_t)g_procCount++;
}

static void fire_proc_closeEnds(FireProc* p) {
#if defined(FIRE_PROC_POSIX)
    if (p->in >= 0) { close(p->in); p->in = -1; }
    if (p->out >= 0) { close(p->out); p->out = -1; }
    if (p->err >= 0) { close(p->err); p->err = -1; }
#elif defined(FIRE_PROC_WIN32)
    if (p->in) { CloseHandle(p->in); p->in = NULL; }
    if (p->out) { CloseHandle(p->out); p->out = NULL; }
    if (p->err) { CloseHandle(p->err); p->err = NULL; }
#else
    (void)p;
#endif
}

/* whether it has ended (then its exit code is kept) */
static int fire_proc_poll(FireProc* p) {
    if (p->exited) return 1;
#if defined(FIRE_PROC_POSIX)
    {
        int status = 0;
        pid_t r = waitpid(p->pid, &status, WNOHANG);
        if (r == 0) return 0;
        if (r == p->pid) {
            p->exited = 1;
            p->code = WIFEXITED(status) ? WEXITSTATUS(status) : WIFSIGNALED(status) ? 128 + WTERMSIG(status) : 1;   /* (a signal: 128 + its number, like the shells and .NET) */
            return 1;
        }
        if (errno == ECHILD) { p->exited = 1; p->code = -1; return 1; }
        return 0;
    }
#elif defined(FIRE_PROC_WIN32)
    {
        DWORD code = 0;
        if (WaitForSingleObject(p->process, 0) != WAIT_OBJECT_0) return 0;
        GetExitCodeProcess(p->process, &code);
        p->exited = 1;
        p->code = (int32_t)code;
        return 1;
    }
#else
    return 1;
#endif
}

static void fire_proc_destroy(FireProc* p) {
    fire_proc_closeEnds(p);
#if defined(FIRE_PROC_POSIX)
    if (!fire_proc_poll(p)) fire_proc_addOrphan(p->pid);
#elif defined(FIRE_PROC_WIN32)
    if (p->process) CloseHandle(p->process);
#endif
    free(p);
}

/* The program has ended (the library for the VM stays loaded for the next one): the handles are closed - the processes themselves go on (like .NET). */
static void fire_proc_reset(void) {
    size_t i;
    for (i = 0; i < g_procCount; i++) if (g_procTable[i] != NULL) { fire_proc_destroy(g_procTable[i]); g_procTable[i] = NULL; }
    g_procCount = 0;
#if defined(FIRE_PROC_POSIX)
    fire_proc_reapOrphans();
#endif
}

/* ---- starting ------------------------------------------------------------------------------------------------------------------------------- */
#if defined(FIRE_PROC_POSIX)
static void fire_proc_cloexec(int fd) { fcntl(fd, F_SETFD, fcntl(fd, F_GETFD) | FD_CLOEXEC); }
static void fire_proc_nonblocking(int fd) { fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK); }

static int64_t fire_proc_start(const char* file, char** args, uint32_t argc, const char* cwd, char** env, uint32_t envc, int flags) {
    static int ignored = 0;
    char** argv;
    char** envp = NULL;
    int fds[8] = {-1, -1, -1, -1, -1, -1, -1, -1};   /* in[0..1], out[2..3], err[4..5], report[6..7] */
    int reported[2] = {0, 0};
    ssize_t got;
    pid_t pid;
    uint32_t i;
    FireProc* p;
    int k;
    if (!ignored) { signal(SIGPIPE, SIG_IGN); ignored = 1; }   /* (writing to a process that closed its input is an error, not the end of this program) */
    argv = (char**)calloc((size_t)argc + 2, sizeof(char*));
    if (argv == NULL) return fire_proc_fail(FIRE_PROC_OTHER, "Out of memory.");
    argv[0] = (char*)file;
    for (i = 0; i < argc; i++) argv[i + 1] = args[i];
    if (env != NULL) {
        envp = (char**)calloc((size_t)envc / 2 + 1, sizeof(char*));
        if (envp == NULL) { free(argv); return fire_proc_fail(FIRE_PROC_OTHER, "Out of memory."); }
        for (i = 0; i + 1 < envc; i += 2) {
            size_t a = strlen(env[i]), b = strlen(env[i + 1]);
            char* e = (char*)malloc(a + b + 2);
            if (e == NULL) break;
            memcpy(e, env[i], a); e[a] = '='; memcpy(e + a + 1, env[i + 1], b + 1);
            envp[i / 2] = e;
        }
    }
    if (((flags & FIRE_PROC_REDIRECT_INPUT) && pipe(&fds[0]) != 0) || ((flags & FIRE_PROC_REDIRECT_OUTPUT) && pipe(&fds[2]) != 0)
        || ((flags & FIRE_PROC_REDIRECT_ERROR) && !(flags & FIRE_PROC_MERGE_ERROR) && pipe(&fds[4]) != 0) || pipe(&fds[6]) != 0) {
        for (k = 0; k < 8; k++) if (fds[k] >= 0) close(fds[k]);
        free(argv); fire_proc_freeStrings(envp);
        return fire_proc_fail2(FIRE_PROC_OTHER, "Could not create a pipe: ", strerror(errno), "");
    }
    for (k = 0; k < 8; k++) if (fds[k] >= 0) fire_proc_cloexec(fds[k]);
    pid = fork();
    if (pid < 0) {
        int e = errno;
        for (k = 0; k < 8; k++) if (fds[k] >= 0) close(fds[k]);
        free(argv); fire_proc_freeStrings(envp);
        return fire_proc_fail2(FIRE_PROC_OTHER, "Could not start a process: ", strerror(e), "");
    }
    if (pid == 0) {
        /* the child: only what is safe after fork, then exec (dup2 gives fds without FD_CLOEXEC) */
        int report[2];
        ssize_t written;
        if (fds[0] >= 0) dup2(fds[0], 0);
        if (fds[3] >= 0) dup2(fds[3], 1);
        if (flags & FIRE_PROC_MERGE_ERROR) { if (fds[3] >= 0) dup2(fds[3], 2); }
        else if (fds[5] >= 0) dup2(fds[5], 2);
        report[1] = 0;   /* 0 exec, 1 chdir */
        if (cwd != NULL && chdir(cwd) != 0) report[1] = 1;
        else {
            if (envp != NULL) environ = envp;
            execvp(file, argv);
        }
        report[0] = errno;
        written = write(fds[7], report, sizeof report);
        (void)written;
        _exit(127);
    }
    free(argv);
    fire_proc_freeStrings(envp);
    close(fds[7]); fds[7] = -1;
    do got = read(fds[6], reported, sizeof reported); while (got < 0 && errno == EINTR);
    close(fds[6]); fds[6] = -1;
    if (got == (ssize_t)sizeof reported) {
        int status;
        int e = reported[0];
        waitpid(pid, &status, 0);
        for (k = 0; k < 8; k++) if (fds[k] >= 0) close(fds[k]);
        if (reported[1] == 1)
            return fire_proc_fail2(e == ENOENT || e == ENOTDIR ? FIRE_PROC_DIRECTORY_NOT_FOUND : e == EACCES ? FIRE_PROC_PERMISSION : FIRE_PROC_OTHER,
                                   "The working directory cannot be used: ", strerror(e), "");
        g_ioError.code = e == ENOENT ? FIRE_PROC_FILE_NOT_FOUND : e == EACCES ? FIRE_PROC_PERMISSION : FIRE_PROC_OTHER;
        snprintf(g_ioError.message, sizeof g_ioError.message, "Could not start '%s': %s", file, strerror(e));
        return -1;
    }
    p = (FireProc*)calloc(1, sizeof(FireProc));
    if (p == NULL) { kill(pid, SIGKILL); for (k = 0; k < 8; k++) if (fds[k] >= 0) close(fds[k]); return fire_proc_fail(FIRE_PROC_OTHER, "Out of memory."); }
    p->pid = pid;
    p->in = fds[1]; fds[1] = -1;
    p->out = fds[2]; fds[2] = -1;
    p->err = fds[4]; fds[4] = -1;
    if (p->in >= 0) fire_proc_nonblocking(p->in);
    if (p->out >= 0) fire_proc_nonblocking(p->out);
    if (p->err >= 0) fire_proc_nonblocking(p->err);
    for (k = 0; k < 8; k++) if (fds[k] >= 0) close(fds[k]);   /* (the ends of the child) */
    return fire_proc_add(p);
}
#elif defined(FIRE_PROC_WIN32)
/* UTF-8 -> UTF-16 (malloc) */
static wchar_t* fire_proc_wide(const char* s) {
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
    wchar_t* w = (wchar_t*)malloc(sizeof(wchar_t) * (size_t)(n > 0 ? n : 1));
    if (w == NULL) return NULL;
    if (n <= 0) { w[0] = 0; return w; }
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n);
    return w;
}

/* a growing UTF-16 text */
typedef struct FireWText { wchar_t* data; size_t length, cap; } FireWText;
static void fire_proc_wput(FireWText* t, const wchar_t* s, size_t n) {
    if (t->length + n + 1 > t->cap) {
        size_t cap = t->cap ? t->cap : 256;
        wchar_t* grown;
        while (cap < t->length + n + 1) cap *= 2;
        grown = (wchar_t*)realloc(t->data, sizeof(wchar_t) * cap);
        if (grown == NULL) return;
        t->data = grown;
        t->cap = cap;
    }
    memcpy(t->data + t->length, s, n * sizeof(wchar_t));
    t->length += n;
    t->data[t->length] = 0;
}
static void fire_proc_wputc(FireWText* t, wchar_t c, size_t times) { size_t i; for (i = 0; i < times; i++) fire_proc_wput(t, &c, 1); }

/* one argument of a command line, quoted so that the C runtime of the program splits it back (backslashes count only before a quote) */
static void fire_proc_quoteArg(FireWText* line, const char* utf8) {
    wchar_t* arg = fire_proc_wide(utf8);
    size_t n, i;
    if (arg == NULL) return;
    n = wcslen(arg);
    if (n > 0 && wcspbrk(arg, L" \t\n\v\"") == NULL) { fire_proc_wput(line, arg, n); free(arg); return; }
    fire_proc_wputc(line, L'"', 1);
    for (i = 0;; i++) {
        size_t slashes = 0;
        while (i < n && arg[i] == L'\\') { i++; slashes++; }
        if (i == n) { fire_proc_wputc(line, L'\\', slashes * 2); break; }
        if (arg[i] == L'"') { fire_proc_wputc(line, L'\\', slashes * 2 + 1); fire_proc_wputc(line, L'"', 1); }
        else { fire_proc_wputc(line, L'\\', slashes); fire_proc_wput(line, &arg[i], 1); }
    }
    fire_proc_wputc(line, L'"', 1);
    free(arg);
}

static void fire_proc_errorText(char* out, size_t cap) {
    wchar_t* text = NULL;
    DWORD e = GetLastError();
    size_t n;
    out[0] = 0;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, NULL, e, 0, (LPWSTR)&text, 0, NULL);
    if (text == NULL) return;
    WideCharToMultiByte(CP_UTF8, 0, text, -1, out, (int)cap, NULL, NULL);
    out[cap - 1] = 0;
    LocalFree(text);
    n = strlen(out);
    while (n > 0 && (out[n - 1] == '\n' || out[n - 1] == '\r' || out[n - 1] == ' ' || out[n - 1] == '.')) out[--n] = 0;
}

static int64_t fire_proc_start(const char* file, char** args, uint32_t argc, const char* cwd, char** env, uint32_t envc, int flags) {
    SECURITY_ATTRIBUTES sa;
    HANDLE ends[6] = {NULL, NULL, NULL, NULL, NULL, NULL};   /* in read/write, out read/write, err read/write */
    DWORD noWait = PIPE_READMODE_BYTE | PIPE_NOWAIT;
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    FireWText line = {NULL, 0, 0}, block = {NULL, 0, 0};
    wchar_t* dir = NULL;
    char why[160];
    uint32_t i;
    int k;
    BOOL started;
    FireProc* p;
    sa.nLength = sizeof sa; sa.lpSecurityDescriptor = NULL; sa.bInheritHandle = TRUE;
    if (((flags & FIRE_PROC_REDIRECT_INPUT) && !CreatePipe(&ends[0], &ends[1], &sa, 0)) || ((flags & FIRE_PROC_REDIRECT_OUTPUT) && !CreatePipe(&ends[2], &ends[3], &sa, 0))
        || ((flags & FIRE_PROC_REDIRECT_ERROR) && !(flags & FIRE_PROC_MERGE_ERROR) && !CreatePipe(&ends[4], &ends[5], &sa, 0))) {
        fire_proc_errorText(why, sizeof why);
        for (k = 0; k < 6; k++) if (ends[k]) CloseHandle(ends[k]);
        return fire_proc_fail2(FIRE_PROC_OTHER, "Could not create a pipe: ", why, "");
    }
    /* the ends of this program are not inherited, and do not wait */
    if (ends[1]) { SetHandleInformation(ends[1], HANDLE_FLAG_INHERIT, 0); SetNamedPipeHandleState(ends[1], &noWait, NULL, NULL); }
    if (ends[2]) { SetHandleInformation(ends[2], HANDLE_FLAG_INHERIT, 0); SetNamedPipeHandleState(ends[2], &noWait, NULL, NULL); }
    if (ends[4]) { SetHandleInformation(ends[4], HANDLE_FLAG_INHERIT, 0); SetNamedPipeHandleState(ends[4], &noWait, NULL, NULL); }
    ZeroMemory(&si, sizeof si);
    si.cb = sizeof si;
    if (flags & (FIRE_PROC_REDIRECT_INPUT | FIRE_PROC_REDIRECT_OUTPUT | FIRE_PROC_REDIRECT_ERROR)) {
        si.dwFlags = STARTF_USESTDHANDLES;
        si.hStdInput = ends[0] ? ends[0] : GetStdHandle(STD_INPUT_HANDLE);
        si.hStdOutput = ends[3] ? ends[3] : GetStdHandle(STD_OUTPUT_HANDLE);
        si.hStdError = (flags & FIRE_PROC_MERGE_ERROR) && ends[3] ? ends[3] : ends[5] ? ends[5] : GetStdHandle(STD_ERROR_HANDLE);
    }
    fire_proc_quoteArg(&line, file);
    for (i = 0; i < argc; i++) { fire_proc_wputc(&line, L' ', 1); fire_proc_quoteArg(&line, args[i]); }
    if (env != NULL) {
        for (i = 0; i + 1 < envc; i += 2) {
            wchar_t* name = fire_proc_wide(env[i]);
            wchar_t* value = fire_proc_wide(env[i + 1]);
            if (name != NULL && value != NULL) {
                fire_proc_wput(&block, name, wcslen(name));
                fire_proc_wputc(&block, L'=', 1);
                fire_proc_wput(&block, value, wcslen(value));
                fire_proc_wputc(&block, 0, 1);
            }
            free(name); free(value);
        }
        fire_proc_wputc(&block, 0, block.length == 0 ? 2 : 1);
    }
    if (cwd != NULL) dir = fire_proc_wide(cwd);
    ZeroMemory(&pi, sizeof pi);
    started = line.data != NULL && CreateProcessW(NULL, line.data, NULL, NULL, TRUE, CREATE_UNICODE_ENVIRONMENT, env != NULL ? (LPVOID)block.data : NULL, dir, &si, &pi);
    free(line.data); free(block.data); free(dir);
    if (!started) {
        DWORD e = GetLastError();
        fire_proc_errorText(why, sizeof why);
        for (k = 0; k < 6; k++) if (ends[k]) CloseHandle(ends[k]);
        if (cwd != NULL && e == ERROR_DIRECTORY) return fire_proc_fail2(FIRE_PROC_DIRECTORY_NOT_FOUND, "The working directory cannot be used: ", why, "");
        g_ioError.code = e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND ? FIRE_PROC_FILE_NOT_FOUND : e == ERROR_ACCESS_DENIED ? FIRE_PROC_PERMISSION : FIRE_PROC_OTHER;
        snprintf(g_ioError.message, sizeof g_ioError.message, "Could not start '%s': %s", file, why);
        return -1;
    }
    CloseHandle(pi.hThread);
    if (ends[0]) CloseHandle(ends[0]);   /* (the ends of the child) */
    if (ends[3]) CloseHandle(ends[3]);
    if (ends[5]) CloseHandle(ends[5]);
    p = (FireProc*)calloc(1, sizeof(FireProc));
    if (p == NULL) { TerminateProcess(pi.hProcess, 1); CloseHandle(pi.hProcess); return fire_proc_fail(FIRE_PROC_OTHER, "Out of memory."); }
    p->process = pi.hProcess;
    p->pid = pi.dwProcessId;
    p->in = ends[1]; p->out = ends[2]; p->err = ends[4];
    return fire_proc_add(p);
}
#endif

/* ---- the natives --------------------------------------------------------------------------------------------------------------------------- */
static Value fire_proc_LastError(void) { return Int(g_ioError.code); }
static Value fire_proc_LastErrorMessage(OwnList* list) { return newStrFromUtf8((const uint8_t*)g_ioError.message, strlen(g_ioError.message), list); }
static Value fire_proc_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 0; i < g_procCount; i++) if (g_procTable[i] != NULL) n++;
    return Int(n);
}
static Value fire_proc_Supported(void) {
#if defined(FIRE_PROC_POSIX) || defined(FIRE_PROC_WIN32)
    return Bool(true);
#else
    return Bool(false);
#endif
}

/* Start(file, args[], workingDirectory or undefined, environment [name, value, ...] or undefined, flags) -> handle, -1 on an error. */
static Value fire_proc_Start(Value file, Value args, Value cwd, Value env, Value flags) {
#if defined(FIRE_PROC_POSIX) || defined(FIRE_PROC_WIN32)
    char* fileText;
    char* dir = NULL;
    char** argList;
    char** envList;
    uint32_t argc, envc;
    int argsOk, envOk;
    int64_t h;
    if (file.kind != K_String || strOf(file)->length == 0) return Int(fire_proc_fail(FIRE_PROC_INVALID_ARGUMENT, "The program to start is missing."));
    if (cwd.kind != K_Undefined && cwd.kind != K_String) return Int(fire_proc_fail(FIRE_PROC_INVALID_ARGUMENT, "The working directory must be a string."));
    argList = fire_proc_strings(args, &argc, &argsOk);
    envList = fire_proc_strings(env, &envc, &envOk);
    if (!argsOk || !envOk) {
        fire_proc_freeStrings(argList); fire_proc_freeStrings(envList);
        return Int(fire_proc_fail(FIRE_PROC_INVALID_ARGUMENT, argsOk ? "The environment variables must be strings." : "The arguments must be strings."));
    }
    fileText = fire_proc_utf8(file);
    if (cwd.kind == K_String) dir = fire_proc_utf8(cwd);
#if defined(FIRE_PROC_POSIX)
    fire_proc_reapOrphans();
#endif
    h = fileText != NULL ? fire_proc_start(fileText, argList, argc, dir, env.kind == K_Array ? envList : NULL, envc, flags.kind == K_Int ? (int)flags.i : 0)
                         : fire_proc_fail(FIRE_PROC_OTHER, "Out of memory.");
    free(fileText); free(dir);
    fire_proc_freeStrings(argList); fire_proc_freeStrings(envList);
    if (h >= 0) fire_proc_ok();
    return Int(h);
#else
    (void)file; (void)args; (void)cwd; (void)env; (void)flags;
    return Int(fire_proc_fail(FIRE_PROC_NOT_SUPPORTED, "This platform cannot start processes."));
#endif
}

static Value fire_proc_Id(Value handle) {
    FireProc* p = fire_proc_find(handle);
    if (p == NULL) return Int(-1);
#if defined(FIRE_PROC_POSIX) || defined(FIRE_PROC_WIN32)
    return Int((int64_t)p->pid);
#else
    return Int(-1);
#endif
}

/* 1 when it has ended, 0 while it runs, -1 on an error. */
static Value fire_proc_Poll(Value handle) {
    FireProc* p = fire_proc_find(handle);
    if (p == NULL) return Int(-1);
    return Int(fire_proc_poll(p) ? 1 : 0);
}

static Value fire_proc_ExitCode(Value handle) {
    FireProc* p = fire_proc_find(handle);
    if (p == NULL) return Undef();
    if (!fire_proc_poll(p)) { fire_proc_fail(FIRE_PROC_NOT_EXITED, "The process has not ended yet."); return Undef(); }
    return Int(p->code);
}

/* Read(handle, 1 output / 2 error, buffer, offset, count) -> bytes read, 0 at the end, -3 when nothing is there now, -1 on an error. */
static Value fire_proc_Read(Value handle, Value which, Value buffer, Value offset, Value count) {
    FireProc* p = fire_proc_find(handle);
    Buf* b;
    if (p == NULL) return Int(-1);
    if ((b = bufferArg(buffer, "Process.Read")) == NULL) return Undef();
    if (offset.kind != K_Int || count.kind != K_Int) return Int(fire_proc_fail(FIRE_PROC_INVALID_ARGUMENT, "Read needs a buffer, an offset and a count."));
    if (offset.i < 0 || count.i < 0 || offset.i + count.i > (int64_t)b->length) return Int(fire_proc_fail(FIRE_PROC_INVALID_ARGUMENT, "Offset and count lie outside the buffer."));
    if (count.i == 0) return Int(0);
#if defined(FIRE_PROC_POSIX)
    {
        int fd = which.kind == K_Int && which.i == 2 ? p->err : p->out;
        ssize_t n;
        if (fd < 0) return Int(fire_proc_fail(FIRE_PROC_INVALID_ARGUMENT, "That output of the process is not redirected."));
        n = read(fd, b->data + offset.i, (size_t)count.i);
        if (n > 0) return Int((int64_t)n);
        if (n == 0) return Int(0);
        if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) return Int(FIRE_PROC_WOULD_BLOCK);
        return Int(fire_proc_fail2(FIRE_PROC_OTHER, "Could not read from the process: ", strerror(errno), ""));
    }
#elif defined(FIRE_PROC_WIN32)
    {
        HANDLE h = which.kind == K_Int && which.i == 2 ? p->err : p->out;
        DWORD got = 0, e;
        char why[160];
        if (h == NULL) return Int(fire_proc_fail(FIRE_PROC_INVALID_ARGUMENT, "That output of the process is not redirected."));
        if (ReadFile(h, b->data + offset.i, (DWORD)count.i, &got, NULL)) return Int(got > 0 ? (int64_t)got : FIRE_PROC_WOULD_BLOCK);
        e = GetLastError();
        if (e == ERROR_NO_DATA) return Int(FIRE_PROC_WOULD_BLOCK);
        if (e == ERROR_BROKEN_PIPE || e == ERROR_HANDLE_EOF) return Int(0);
        fire_proc_errorText(why, sizeof why);
        return Int(fire_proc_fail2(FIRE_PROC_OTHER, "Could not read from the process: ", why, ""));
    }
#else
    (void)which;
    return Int(fire_proc_fail(FIRE_PROC_NOT_SUPPORTED, "This platform cannot start processes."));
#endif
}

/* Write(handle, buffer, offset, count) -> bytes written (what fits into the pipe now; 0 when it is full), -1 on an error. */
static Value fire_proc_Write(Value handle, Value buffer, Value offset, Value count) {
    FireProc* p = fire_proc_find(handle);
    FireBytes b;
    if (p == NULL) return Int(-1);
    if (!bytesArg(buffer, "Process.Write", &b)) return Undef();
    if (offset.kind != K_Int || count.kind != K_Int) return Int(fire_proc_fail(FIRE_PROC_INVALID_ARGUMENT, "Write needs a buffer, an offset and a count."));
    if (offset.i < 0 || count.i < 0 || offset.i + count.i > (int64_t)b.length) return Int(fire_proc_fail(FIRE_PROC_INVALID_ARGUMENT, "Offset and count lie outside the buffer."));
#if defined(FIRE_PROC_POSIX)
    {
        ssize_t n;
        if (p->in < 0) return Int(fire_proc_fail(FIRE_PROC_INVALID_ARGUMENT, "The input of the process is not redirected (or already closed)."));
        if (count.i == 0) return Int(0);
        n = write(p->in, b.data + offset.i, (size_t)count.i);
        if (n >= 0) return Int((int64_t)n);
        if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) return Int(0);
        if (errno == EPIPE) return Int(fire_proc_fail(FIRE_PROC_OTHER, "The process does not read its input any more."));
        return Int(fire_proc_fail2(FIRE_PROC_OTHER, "Could not write to the process: ", strerror(errno), ""));
    }
#elif defined(FIRE_PROC_WIN32)
    {
        DWORD put = 0, e;
        char why[160];
        if (p->in == NULL) return Int(fire_proc_fail(FIRE_PROC_INVALID_ARGUMENT, "The input of the process is not redirected (or already closed)."));
        if (count.i == 0) return Int(0);
        if (WriteFile(p->in, b.data + offset.i, (DWORD)count.i, &put, NULL)) return Int((int64_t)put);
        e = GetLastError();
        if (e == ERROR_NO_DATA || e == ERROR_BROKEN_PIPE) return Int(fire_proc_fail(FIRE_PROC_OTHER, "The process does not read its input any more."));
        fire_proc_errorText(why, sizeof why);
        return Int(fire_proc_fail2(FIRE_PROC_OTHER, "Could not write to the process: ", why, ""));
    }
#else
    return Int(fire_proc_fail(FIRE_PROC_NOT_SUPPORTED, "This platform cannot start processes."));
#endif
}

/* Closes the input of the process (it reads the end of it). */
static Value fire_proc_CloseInput(Value handle) {
    FireProc* p = fire_proc_find(handle);
    if (p == NULL) return Bool(false);
#if defined(FIRE_PROC_POSIX)
    if (p->in >= 0) { close(p->in); p->in = -1; }
#elif defined(FIRE_PROC_WIN32)
    if (p->in) { CloseHandle(p->in); p->in = NULL; }
#endif
    fire_proc_ok();
    return Bool(true);
}

/* Ends the process at once (SIGKILL; TerminateProcess). false on an error; a process that has already ended is fine. */
static Value fire_proc_Kill(Value handle) {
    FireProc* p = fire_proc_find(handle);
    if (p == NULL) return Bool(false);
    if (fire_proc_poll(p)) { fire_proc_ok(); return Bool(true); }
#if defined(FIRE_PROC_POSIX)
    if (kill(p->pid, SIGKILL) != 0 && errno != ESRCH) { fire_proc_fail2(FIRE_PROC_OTHER, "Could not end the process: ", strerror(errno), ""); return Bool(false); }
#elif defined(FIRE_PROC_WIN32)
    if (!TerminateProcess(p->process, (UINT)-1) && !fire_proc_poll(p)) {
        char why[160];
        fire_proc_errorText(why, sizeof why);
        fire_proc_fail2(FIRE_PROC_OTHER, "Could not end the process: ", why, "");
        return Bool(false);
    }
#endif
    fire_proc_ok();
    return Bool(true);
}

/* Releases the handle and the pipes (the process itself goes on if it still runs). */
static Value fire_proc_Close(Value handle) {
    FireProc* p = fire_proc_find(handle);
    if (p == NULL) return Bool(false);
    g_procTable[handle.i] = NULL;
    fire_proc_destroy(p);
    fire_proc_ok();
    return Bool(true);
}

#endif
