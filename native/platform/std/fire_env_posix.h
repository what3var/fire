/* fire native platform layer, the process (Environment, docs/language/19-standard-library.md) for POSIX C libraries - Linux, macOS, the BSDs, the browser,
 * and newlib under FreeRTOS/ESP-IDF: the name of the platform, the arguments, the path of the program and the environment variables, all in UTF-8.
 * A platform package that includes this names itself with FIRE_PLAT_NAME (before the include). */
#ifndef FIRE_ENV_POSIX_H
#define FIRE_ENV_POSIX_H

#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#if defined(__linux__) || defined(__APPLE__) || defined(__unix__)
#include <unistd.h>
#endif
#if defined(__APPLE__)
#include <mach-o/dyld.h>
#endif

extern char** environ;

#ifndef FIRE_PLAT_NAME
#if defined(__APPLE__)
#define FIRE_PLAT_NAME "macos"
#elif defined(__linux__)
#define FIRE_PLAT_NAME "linux"
#elif defined(__FreeBSD__)
#define FIRE_PLAT_NAME "freebsd"
#else
#define FIRE_PLAT_NAME "posix"
#endif
#endif

static inline const char* fire_plat_name(void) { return FIRE_PLAT_NAME; }

/* The arguments of the program without its name (argv[1...]); none where nothing passes any (an entry point of a board: argc 0). */
static inline void fire_plat_eachArg(int argc, char** argv, void (*fn)(const char* utf8, void* ctx), void* ctx) {
    int i;
    for (i = 1; i < argc && argv != NULL; i++) fn(argv[i], ctx);
}

/* The path of the running program into `out`: its length, 0 when the system does not tell it. */
static inline int fire_plat_programPath(char* out, size_t cap) {
#if defined(__linux__)
    ssize_t n = readlink("/proc/self/exe", out, cap - 1);
    if (n <= 0) return 0;
    out[n] = 0;
    return (int)n;
#elif defined(__APPLE__)
    uint32_t size = (uint32_t)cap;
    if (_NSGetExecutablePath(out, &size) != 0) return 0;
    return (int)strlen(out);
#else
    (void)out; (void)cap;
    return 0;
#endif
}

/* The value of a variable into `out` (cut to fit): its length, -1 when it is not set. */
static inline int fire_plat_getenv(const char* name, char* out, size_t cap) {
    const char* v = getenv(name);
    size_t n;
    if (v == NULL) return -1;
    n = strlen(v);
    if (n >= cap) n = cap - 1;
    memcpy(out, v, n);
    out[n] = 0;
    return (int)n;
}

/* Sets a variable of the process (and of the processes it starts); NULL removes it. */
static inline void fire_plat_setenv(const char* name, const char* value) {
    if (value != NULL) setenv(name, value, 1);
    else unsetenv(name);
}

/* Every variable: its name (not terminated: `nameLen`) and its value. */
static inline void fire_plat_envEach(void (*fn)(const char* name, size_t nameLen, const char* value, void* ctx), void* ctx) {
    char** e;
    for (e = environ; e != NULL && *e != NULL; e++) {
        const char* eq = strchr(*e, '=');
        if (eq == NULL || eq == *e) continue;
        fn(*e, (size_t)(eq - *e), eq + 1, ctx);
    }
}

/* `Library.Load` / `Get` (docs/language/18-native-code.md): a native library loaded at run time (a path, or a name the system finds) and a function of it;
 * NULL when it cannot. Without a dynamic loader (the boards, the browser) always NULL. */
#if (defined(__linux__) || defined(__APPLE__) || defined(__FreeBSD__)) && !defined(__EMSCRIPTEN__) && !defined(ESP_PLATFORM)
#include <dlfcn.h>
static inline void* fire_plat_dlopen(const char* path) { return dlopen(path, RTLD_NOW | RTLD_LOCAL); }
static inline void* fire_plat_dlsym(void* library, const char* name) { return library ? dlsym(library, name) : NULL; }
#else
static inline void* fire_plat_dlopen(const char* path) { (void)path; return NULL; }
static inline void* fire_plat_dlsym(void* library, const char* name) { (void)library; (void)name; return NULL; }
#endif

#endif
