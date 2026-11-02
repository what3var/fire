// fire native bridge "process": the natives behind `#import "process"` (docs/PROCESS.md) - starting programs, their standard input, output and error as pipes, waiting for them,
// their exit code, ending them. The fire side (Process, ProcessOptions, ProcessResult, ProcessStream - fire source) is the same in the VM and in a native build; this file is what
// its `__Proc...` functions do.
//
// This file is the one implementation: the native build includes it (the package "process" brings it as its C++ source) and the virtual machine runs it in a shared library built
// from it (native/abi/fire_pkg_abi.h). POSIX: fork + execvp (an error of exec comes back through a pipe), pipes that do not block; Windows: CreateProcessW with anonymous pipes
// (PIPE_NOWAIT on the end of the program), a command line quoted by the rules of the C runtime and a Unicode environment block. Where there are no processes (FreeRTOS, ESP32, the
// browser) every function reports NotSupported.
//
// Nothing here waits: reading what is not there yet gives -3, a full input pipe takes what fits (the fire code waits with Sleep between the attempts, so a program that waits for a
// process does not hold the library and the other fire threads keep running). A process is an integer handle in a table of this bridge. Every function reports an error the same
// way: a result of -1/false/undefined and the code and message of this thread (`__ProcLastError`, `__ProcLastErrorMessage`), from which the fire code throws a ProcessException.
#pragma once

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#if defined(_WIN32)
#define FIRE_PROC_WIN32 1
#elif (defined(__unix__) || defined(__APPLE__)) && !defined(__EMSCRIPTEN__) && !defined(FIRE_PLATFORM_FREERTOS_H) && !defined(ESP_PLATFORM)
#define FIRE_PROC_POSIX 1
#include <cerrno>
#include <csignal>
#include <fcntl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
extern char** environ;
#endif

namespace fire {
namespace process {

enum Err { None = 0, InvalidArgument = 1, InvalidHandle = 2, FileNotFound = 3, Permission = 4, NotSupported = 5, Other = 6, NotExited = 7, DirectoryNotFound = 8 };
enum Flags { RedirectInput = 1, RedirectOutput = 2, RedirectError = 4, MergeError = 8 };
constexpr int64_t WOULD_BLOCK = -3;

#if defined(FIRE_TLS_STRUCT)
#define FIRE_PROC_ERROR g_ioError
#else
struct ErrorSlot { int32_t code; char message[176]; };
inline thread_local ErrorSlot t_procError = {0, {0}};
#define FIRE_PROC_ERROR ::fire::process::t_procError
#endif

inline int64_t fail(int code, const std::string& message) {
    FIRE_PROC_ERROR.code = code;
    size_t n = message.size() < sizeof FIRE_PROC_ERROR.message - 1 ? message.size() : sizeof FIRE_PROC_ERROR.message - 1;
    std::memcpy(FIRE_PROC_ERROR.message, message.data(), n);
    FIRE_PROC_ERROR.message[n] = 0;
    return -1;
}
inline void ok() { FIRE_PROC_ERROR.code = 0; FIRE_PROC_ERROR.message[0] = 0; }

// ---- text: UTF-16 (fire strings) -> UTF-8 -----------------------------------------------------------------------------------------------
inline std::string toUtf8(Value v) {
    std::string out;
    if (v.kind != K_String) return out;
    const Str* s = strOf(v);
    out.reserve(s->length);
    for (uint32_t i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out.push_back((char)c);
        else if (c < 0x800) { out.push_back((char)(0xC0 | (c >> 6))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else if (c < 0x10000) { out.push_back((char)(0xE0 | (c >> 12))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
        else { out.push_back((char)(0xF0 | (c >> 18))); out.push_back((char)(0x80 | ((c >> 12) & 0x3F))); out.push_back((char)(0x80 | ((c >> 6) & 0x3F))); out.push_back((char)(0x80 | (c & 0x3F))); }
    }
    return out;
}

// the strings of an array (arguments; the environment: name, value, name, value, ...); false when an element is not a string
inline bool stringsOf(Value array, std::vector<std::string>& out) {
    if (array.kind == K_Undefined) return true;
    if (array.kind != K_Array) return false;
    Arr* a = arrOf(array);
    for (uint32_t i = 0; i < a->length; i++) {
        Value item = arrItems(a)[i];
        if (item.kind != K_String) return false;
        out.push_back(toUtf8(item));
    }
    return true;
}

// ---- the table of the processes ---------------------------------------------------------------------------------------------------------
struct Proc {
#if defined(FIRE_PROC_POSIX)
    pid_t pid = -1;
    int in = -1, out = -1, err = -1;
#elif defined(FIRE_PROC_WIN32)
    HANDLE process = NULL;
    DWORD pid = 0;
    HANDLE in = NULL, out = NULL, err = NULL;
#endif
    bool exited = false;
    int64_t code = 0;
};

inline std::mutex& tableLock() { static std::mutex m; return m; }
inline std::vector<Proc*>& table() { static std::vector<Proc*> t; return t; }
#if defined(FIRE_PROC_POSIX)
inline std::vector<pid_t>& orphans() { static std::vector<pid_t> o; return o; }   // closed before they ended: reaped later (no zombies)
inline void reapOrphans() {
    auto& o = orphans();
    for (size_t i = 0; i < o.size();) {
        int status;
        pid_t r = waitpid(o[i], &status, WNOHANG);
        if (r == o[i] || (r < 0 && errno == ECHILD)) o.erase(o.begin() + (ptrdiff_t)i);
        else i++;
    }
}
#endif

inline Proc* find(Value handle) {
    if (handle.kind != K_Int) { fail(InvalidHandle, "That is not a process handle."); return nullptr; }
    auto& t = table();
    if (handle.i < 0 || handle.i >= (int64_t)t.size() || t[(size_t)handle.i] == nullptr) { fail(InvalidHandle, "The process has been closed."); return nullptr; }
    return t[(size_t)handle.i];
}

inline int64_t add(Proc* p) {
    auto& t = table();
    for (size_t i = 0; i < t.size(); i++) if (t[i] == nullptr) { t[i] = p; return (int64_t)i; }
    t.push_back(p);
    return (int64_t)t.size() - 1;
}

inline void closeEnds(Proc* p) {
#if defined(FIRE_PROC_POSIX)
    if (p->in >= 0) { ::close(p->in); p->in = -1; }
    if (p->out >= 0) { ::close(p->out); p->out = -1; }
    if (p->err >= 0) { ::close(p->err); p->err = -1; }
#elif defined(FIRE_PROC_WIN32)
    if (p->in) { CloseHandle(p->in); p->in = NULL; }
    if (p->out) { CloseHandle(p->out); p->out = NULL; }
    if (p->err) { CloseHandle(p->err); p->err = NULL; }
#else
    (void)p;
#endif
}

// whether it has ended (then its exit code is kept)
inline bool poll(Proc* p) {
    if (p->exited) return true;
#if defined(FIRE_PROC_POSIX)
    int status = 0;
    pid_t r = waitpid(p->pid, &status, WNOHANG);
    if (r == 0) return false;
    if (r == p->pid) {
        p->exited = true;
        p->code = WIFEXITED(status) ? WEXITSTATUS(status) : WIFSIGNALED(status) ? 128 + WTERMSIG(status) : 1;   // (killed by a signal: 128 + its number, like the shells and .NET)
        return true;
    }
    if (errno == ECHILD) { p->exited = true; p->code = -1; return true; }
    return false;
#elif defined(FIRE_PROC_WIN32)
    if (WaitForSingleObject(p->process, 0) != WAIT_OBJECT_0) return false;
    DWORD code = 0;
    GetExitCodeProcess(p->process, &code);
    p->exited = true;
    p->code = (int32_t)code;
    return true;
#else
    return true;
#endif
}

inline void destroy(Proc* p) {
    closeEnds(p);
#if defined(FIRE_PROC_POSIX)
    if (!poll(p)) orphans().push_back(p->pid);
#elif defined(FIRE_PROC_WIN32)
    if (p->process) CloseHandle(p->process);
#endif
    delete p;
}

/// The program has ended (the library for the VM stays loaded for the next one): the handles are closed - the processes themselves go on (like .NET).
inline void reset() {
    std::lock_guard<std::mutex> lock(tableLock());
    for (auto*& p : table()) if (p != nullptr) { destroy(p); p = nullptr; }
    table().clear();
#if defined(FIRE_PROC_POSIX)
    reapOrphans();
#endif
}

// ---- starting -----------------------------------------------------------------------------------------------------------------------------
#if defined(FIRE_PROC_POSIX)
inline void cloexec(int fd) { fcntl(fd, F_SETFD, fcntl(fd, F_GETFD) | FD_CLOEXEC); }
inline void nonblocking(int fd) { fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK); }

inline int64_t start(const std::string& file, const std::vector<std::string>& args, const std::string* cwd, const std::vector<std::string>* env, int flags) {
    static bool ignored = false;
    if (!ignored) { signal(SIGPIPE, SIG_IGN); ignored = true; }   // (writing to a process that closed its input is an error, not the end of this program)
    std::vector<char*> argv;
    argv.push_back(const_cast<char*>(file.c_str()));
    for (auto& a : args) argv.push_back(const_cast<char*>(a.c_str()));
    argv.push_back(nullptr);
    std::vector<std::string> envText;
    std::vector<char*> envp;
    if (env != nullptr) {
        for (size_t i = 0; i + 1 < env->size(); i += 2) envText.push_back((*env)[i] + "=" + (*env)[i + 1]);
        for (auto& e : envText) envp.push_back(const_cast<char*>(e.c_str()));
        envp.push_back(nullptr);
    }
    int inP[2] = {-1, -1}, outP[2] = {-1, -1}, errP[2] = {-1, -1}, report[2] = {-1, -1};
    auto closeAll = [&]() { for (int fd : {inP[0], inP[1], outP[0], outP[1], errP[0], errP[1], report[0], report[1]}) if (fd >= 0) ::close(fd); };
    if (((flags & RedirectInput) && pipe(inP) != 0) || ((flags & RedirectOutput) && pipe(outP) != 0)
        || ((flags & RedirectError) && !(flags & MergeError) && pipe(errP) != 0) || pipe(report) != 0) {
        closeAll();
        return fail(Other, std::string("Could not create a pipe: ") + std::strerror(errno));
    }
    for (int fd : {inP[0], inP[1], outP[0], outP[1], errP[0], errP[1], report[0], report[1]}) if (fd >= 0) cloexec(fd);
    pid_t pid = fork();
    if (pid < 0) { closeAll(); return fail(Other, std::string("Could not start a process: ") + std::strerror(errno)); }
    if (pid == 0) {
        // the child: only what is safe after fork, then exec (dup2 gives fds without FD_CLOEXEC)
        if (inP[0] >= 0) dup2(inP[0], 0);
        if (outP[1] >= 0) dup2(outP[1], 1);
        if (flags & MergeError) { if (outP[1] >= 0) dup2(outP[1], 2); }
        else if (errP[1] >= 0) dup2(errP[1], 2);
        int what = 0;   // 0 exec, 1 chdir
        if (cwd != nullptr && chdir(cwd->c_str()) != 0) what = 1;
        else {
            if (env != nullptr) environ = envp.data();
            execvp(file.c_str(), argv.data());
        }
        int report2[2] = {errno, what};
        ssize_t written = write(report[1], report2, sizeof report2);
        (void)written;
        _exit(127);
    }
    ::close(report[1]); report[1] = -1;
    int reported[2] = {0, 0};
    ssize_t got;
    do got = read(report[0], reported, sizeof reported); while (got < 0 && errno == EINTR);
    ::close(report[0]); report[0] = -1;
    if (got == (ssize_t)sizeof reported) {
        int status;
        waitpid(pid, &status, 0);
        closeAll();
        int e = reported[0];
        if (reported[1] == 1) return fail(e == ENOENT || e == ENOTDIR ? DirectoryNotFound : e == EACCES ? Permission : Other, "The working directory '" + *cwd + "' cannot be used: " + std::strerror(e));
        return fail(e == ENOENT ? FileNotFound : e == EACCES ? Permission : Other, "Could not start '" + file + "': " + std::strerror(e));
    }
    auto* p = new Proc();
    p->pid = pid;
    if (inP[1] >= 0) { p->in = inP[1]; inP[1] = -1; nonblocking(p->in); }
    if (outP[0] >= 0) { p->out = outP[0]; outP[0] = -1; nonblocking(p->out); }
    if (errP[0] >= 0) { p->err = errP[0]; errP[0] = -1; nonblocking(p->err); }
    closeAll();   // (the ends of the child)
    return add(p);
}
#elif defined(FIRE_PROC_WIN32)
inline std::wstring wide(const std::string& s) {
    if (s.empty()) return std::wstring();
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), NULL, 0);
    std::wstring w((size_t)n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), &w[0], n);
    return w;
}
// one argument of a command line, quoted so that the C runtime of the program splits it back (backslashes count only before a quote)
inline void quoteArg(std::wstring& line, const std::wstring& arg) {
    if (!arg.empty() && arg.find_first_of(L" \t\n\v\"") == std::wstring::npos) { line += arg; return; }
    line += L'"';
    for (size_t i = 0;; i++) {
        size_t slashes = 0;
        while (i < arg.size() && arg[i] == L'\\') { i++; slashes++; }
        if (i == arg.size()) { line.append(slashes * 2, L'\\'); break; }
        if (arg[i] == L'"') { line.append(slashes * 2 + 1, L'\\'); line += L'"'; }
        else { line.append(slashes, L'\\'); line += arg[i]; }
    }
    line += L'"';
}
inline std::string lastErrorText() {
    wchar_t* text = nullptr;
    DWORD e = GetLastError();
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, NULL, e, 0, (LPWSTR)&text, 0, NULL);
    std::string out;
    if (text != nullptr) {
        int n = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
        out.resize(n > 0 ? (size_t)n - 1 : 0);
        if (n > 1) WideCharToMultiByte(CP_UTF8, 0, text, -1, &out[0], n, NULL, NULL);
        LocalFree(text);
        while (!out.empty() && (out.back() == '\n' || out.back() == '\r' || out.back() == ' ' || out.back() == '.')) out.pop_back();
    }
    return out;
}
inline int64_t start(const std::string& file, const std::vector<std::string>& args, const std::string* cwd, const std::vector<std::string>* env, int flags) {
    SECURITY_ATTRIBUTES sa = {sizeof(SECURITY_ATTRIBUTES), NULL, TRUE};
    HANDLE inR = NULL, inW = NULL, outR = NULL, outW = NULL, errR = NULL, errW = NULL;
    auto closeAll = [&]() { for (HANDLE h : {inR, inW, outR, outW, errR, errW}) if (h) CloseHandle(h); };
    if (((flags & RedirectInput) && !CreatePipe(&inR, &inW, &sa, 0)) || ((flags & RedirectOutput) && !CreatePipe(&outR, &outW, &sa, 0))
        || ((flags & RedirectError) && !(flags & MergeError) && !CreatePipe(&errR, &errW, &sa, 0))) {
        std::string why = lastErrorText();
        closeAll();
        return fail(Other, "Could not create a pipe: " + why);
    }
    // the ends of this program are not inherited, and do not wait
    DWORD noWait = PIPE_READMODE_BYTE | PIPE_NOWAIT;
    for (HANDLE h : {inW, outR, errR}) if (h) { SetHandleInformation(h, HANDLE_FLAG_INHERIT, 0); SetNamedPipeHandleState(h, &noWait, NULL, NULL); }
    STARTUPINFOW si;
    ZeroMemory(&si, sizeof si);
    si.cb = sizeof si;
    if (flags & (RedirectInput | RedirectOutput | RedirectError)) {
        si.dwFlags = STARTF_USESTDHANDLES;
        si.hStdInput = inR ? inR : GetStdHandle(STD_INPUT_HANDLE);
        si.hStdOutput = outW ? outW : GetStdHandle(STD_OUTPUT_HANDLE);
        si.hStdError = (flags & MergeError) && outW ? outW : errW ? errW : GetStdHandle(STD_ERROR_HANDLE);
    }
    std::wstring line;
    quoteArg(line, wide(file));
    for (auto& a : args) { line += L' '; quoteArg(line, wide(a)); }
    std::wstring block;
    if (env != nullptr) {
        for (size_t i = 0; i + 1 < env->size(); i += 2) { block += wide((*env)[i]); block += L'='; block += wide((*env)[i + 1]); block += L'\0'; }
        block += L'\0';
        if (block.size() == 1) block += L'\0';
    }
    std::wstring dir = cwd != nullptr ? wide(*cwd) : std::wstring();
    PROCESS_INFORMATION pi;
    ZeroMemory(&pi, sizeof pi);
    BOOL started = CreateProcessW(NULL, &line[0], NULL, NULL, TRUE, CREATE_UNICODE_ENVIRONMENT, env != nullptr ? (LPVOID)block.data() : NULL,
                                  cwd != nullptr ? dir.c_str() : NULL, &si, &pi);
    if (!started) {
        DWORD e = GetLastError();
        std::string why = lastErrorText();
        closeAll();
        if (cwd != nullptr && e == ERROR_DIRECTORY) return fail(DirectoryNotFound, "The working directory '" + *cwd + "' cannot be used: " + why);
        return fail(e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND ? FileNotFound : e == ERROR_ACCESS_DENIED ? Permission : Other, "Could not start '" + file + "': " + why);
    }
    CloseHandle(pi.hThread);
    for (HANDLE* h : {&inR, &outW, &errW}) if (*h) { CloseHandle(*h); *h = NULL; }   // (the ends of the child)
    auto* p = new Proc();
    p->process = pi.hProcess;
    p->pid = pi.dwProcessId;
    p->in = inW; p->out = outR; p->err = errR;
    return add(p);
}
#endif

// ---- the natives --------------------------------------------------------------------------------------------------------------------------
inline Value LastError() { return Int(FIRE_PROC_ERROR.code); }
inline Value LastErrorMessage(OwnList* list) { return newStrFromUtf8((const uint8_t*)FIRE_PROC_ERROR.message, std::strlen(FIRE_PROC_ERROR.message), list); }
inline Value OpenCount() {
    std::lock_guard<std::mutex> lock(tableLock());
    int64_t n = 0;
    for (auto* p : table()) if (p != nullptr) n++;
    return Int(n);
}
inline Value Supported() {
#if defined(FIRE_PROC_POSIX) || defined(FIRE_PROC_WIN32)
    return Bool(true);
#else
    return Bool(false);
#endif
}

/// Start(file, args[], workingDirectory or undefined, environment [name, value, ...] or undefined, flags) -> handle, -1 on an error.
inline Value Start(Value file, Value args, Value cwd, Value env, Value flags) {
#if defined(FIRE_PROC_POSIX) || defined(FIRE_PROC_WIN32)
    if (file.kind != K_String || strOf(file)->length == 0) return Int(fail(InvalidArgument, "The program to start is missing."));
    std::vector<std::string> argList, envList;
    if (!stringsOf(args, argList)) return Int(fail(InvalidArgument, "The arguments must be strings."));
    if (!stringsOf(env, envList)) return Int(fail(InvalidArgument, "The environment variables must be strings."));
    if (cwd.kind != K_Undefined && cwd.kind != K_String) return Int(fail(InvalidArgument, "The working directory must be a string."));
    std::string dir = toUtf8(cwd);
    std::lock_guard<std::mutex> lock(tableLock());
#if defined(FIRE_PROC_POSIX)
    reapOrphans();
#endif
    int64_t h = start(toUtf8(file), argList, cwd.kind == K_String ? &dir : nullptr, env.kind == K_Array ? &envList : nullptr, flags.kind == K_Int ? (int)flags.i : 0);
    if (h >= 0) ok();
    return Int(h);
#else
    (void)file; (void)args; (void)cwd; (void)env; (void)flags;
    return Int(fail(NotSupported, "This platform cannot start processes."));
#endif
}

inline Value Id(Value handle) {
    std::lock_guard<std::mutex> lock(tableLock());
    Proc* p = find(handle);
    if (p == nullptr) return Int(-1);
#if defined(FIRE_PROC_POSIX) || defined(FIRE_PROC_WIN32)
    return Int((int64_t)p->pid);
#else
    return Int(-1);
#endif
}

/// 1 when it has ended, 0 while it runs, -1 on an error.
inline Value Poll(Value handle) {
    std::lock_guard<std::mutex> lock(tableLock());
    Proc* p = find(handle);
    if (p == nullptr) return Int(-1);
    return Int(poll(p) ? 1 : 0);
}

inline Value ExitCode(Value handle) {
    std::lock_guard<std::mutex> lock(tableLock());
    Proc* p = find(handle);
    if (p == nullptr) return Undef();
    if (!poll(p)) { fail(NotExited, "The process has not ended yet."); return Undef(); }
    return Int(p->code);
}

/// Read(handle, 1 output / 2 error, buffer, offset, count) -> bytes read, 0 at the end, -3 when nothing is there now, -1 on an error.
inline Value Read(Value handle, Value which, Value buffer, Value offset, Value count) {
    std::lock_guard<std::mutex> lock(tableLock());
    Proc* p = find(handle);
    if (p == nullptr) return Int(-1);
    if (buffer.kind != K_Buffer || offset.kind != K_Int || count.kind != K_Int) return Int(fail(InvalidArgument, "Read needs a buffer, an offset and a count."));
    Buf* b = bufOf(buffer);
    if (offset.i < 0 || count.i < 0 || offset.i + count.i > (int64_t)b->length) return Int(fail(InvalidArgument, "Offset and count lie outside the buffer."));
    if (count.i == 0) return Int(0);
#if defined(FIRE_PROC_POSIX)
    int fd = which.kind == K_Int && which.i == 2 ? p->err : p->out;
    if (fd < 0) return Int(fail(InvalidArgument, "That output of the process is not redirected."));
    ssize_t n = ::read(fd, b->data + offset.i, (size_t)count.i);
    if (n > 0) return Int((int64_t)n);
    if (n == 0) return Int(0);
    if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) return Int(WOULD_BLOCK);
    return Int(fail(Other, std::string("Could not read from the process: ") + std::strerror(errno)));
#elif defined(FIRE_PROC_WIN32)
    HANDLE h = which.kind == K_Int && which.i == 2 ? p->err : p->out;
    if (h == NULL) return Int(fail(InvalidArgument, "That output of the process is not redirected."));
    DWORD got = 0;
    if (ReadFile(h, b->data + offset.i, (DWORD)count.i, &got, NULL)) return Int(got > 0 ? (int64_t)got : WOULD_BLOCK);
    DWORD e = GetLastError();
    if (e == ERROR_NO_DATA) return Int(WOULD_BLOCK);
    if (e == ERROR_BROKEN_PIPE || e == ERROR_HANDLE_EOF) return Int(0);
    return Int(fail(Other, "Could not read from the process: " + lastErrorText()));
#else
    (void)which;
    return Int(fail(NotSupported, "This platform cannot start processes."));
#endif
}

/// Write(handle, buffer, offset, count) -> bytes written (what fits into the pipe now; 0 when it is full), -1 on an error.
inline Value Write(Value handle, Value buffer, Value offset, Value count) {
    std::lock_guard<std::mutex> lock(tableLock());
    Proc* p = find(handle);
    if (p == nullptr) return Int(-1);
    if (buffer.kind != K_Buffer || offset.kind != K_Int || count.kind != K_Int) return Int(fail(InvalidArgument, "Write needs a buffer, an offset and a count."));
    Buf* b = bufOf(buffer);
    if (offset.i < 0 || count.i < 0 || offset.i + count.i > (int64_t)b->length) return Int(fail(InvalidArgument, "Offset and count lie outside the buffer."));
#if defined(FIRE_PROC_POSIX)
    if (p->in < 0) return Int(fail(InvalidArgument, "The input of the process is not redirected (or already closed)."));
    if (count.i == 0) return Int(0);
    ssize_t n = ::write(p->in, b->data + offset.i, (size_t)count.i);
    if (n >= 0) return Int((int64_t)n);
    if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) return Int(0);
    if (errno == EPIPE) return Int(fail(Other, "The process does not read its input any more."));
    return Int(fail(Other, std::string("Could not write to the process: ") + std::strerror(errno)));
#elif defined(FIRE_PROC_WIN32)
    if (p->in == NULL) return Int(fail(InvalidArgument, "The input of the process is not redirected (or already closed)."));
    if (count.i == 0) return Int(0);
    DWORD put = 0;
    if (WriteFile(p->in, b->data + offset.i, (DWORD)count.i, &put, NULL)) return Int((int64_t)put);
    DWORD e = GetLastError();
    if (e == ERROR_NO_DATA || e == ERROR_BROKEN_PIPE) return Int(fail(Other, "The process does not read its input any more."));
    return Int(fail(Other, "Could not write to the process: " + lastErrorText()));
#else
    return Int(fail(NotSupported, "This platform cannot start processes."));
#endif
}

/// Closes the input of the process (it reads the end of it).
inline Value CloseInput(Value handle) {
    std::lock_guard<std::mutex> lock(tableLock());
    Proc* p = find(handle);
    if (p == nullptr) return Bool(false);
#if defined(FIRE_PROC_POSIX)
    if (p->in >= 0) { ::close(p->in); p->in = -1; }
#elif defined(FIRE_PROC_WIN32)
    if (p->in) { CloseHandle(p->in); p->in = NULL; }
#endif
    ok();
    return Bool(true);
}

/// Ends the process at once (SIGKILL; TerminateProcess). false on an error; a process that has already ended is fine.
inline Value Kill(Value handle) {
    std::lock_guard<std::mutex> lock(tableLock());
    Proc* p = find(handle);
    if (p == nullptr) return Bool(false);
    if (poll(p)) { ok(); return Bool(true); }
#if defined(FIRE_PROC_POSIX)
    if (::kill(p->pid, SIGKILL) != 0 && errno != ESRCH) { fail(Other, std::string("Could not end the process: ") + std::strerror(errno)); return Bool(false); }
#elif defined(FIRE_PROC_WIN32)
    if (!TerminateProcess(p->process, (UINT)-1) && !poll(p)) { fail(Other, "Could not end the process: " + lastErrorText()); return Bool(false); }
#endif
    ok();
    return Bool(true);
}

/// Releases the handle and the pipes (the process itself goes on if it still runs).
inline Value Close(Value handle) {
    std::lock_guard<std::mutex> lock(tableLock());
    Proc* p = find(handle);
    if (p == nullptr) return Bool(false);
    table()[(size_t)handle.i] = nullptr;
    destroy(p);
    ok();
    return Bool(true);
}

}  // namespace process
}  // namespace fire
