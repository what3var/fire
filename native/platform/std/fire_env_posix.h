/* fire native platform layer, the process (Environment, docs/language/19-standard-library.md) for POSIX C libraries - Linux, macOS, the BSDs, the browser,
 * and newlib under FreeRTOS/ESP-IDF: the name of the platform, the arguments, the path of the program and the environment variables, all in UTF-8.
 * A platform package that includes this names itself with FIRE_PLAT_NAME (before the include). */
#ifndef FIRE_ENV_POSIX_H
#define FIRE_ENV_POSIX_H

#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
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

/* The memory (Environment.TotalMemory / FreeMemory / ProcessMemory): 1 what the machine has, 2 what is free for programs (Linux: MemAvailable), 3 what this
 * program takes (its resident set); bytes, -1 where the system does not tell. On a board: the heap of FreeRTOS / ESP-IDF; in the browser: the heap of wasm. */
#if defined(__APPLE__)
#include <sys/sysctl.h>
#include <mach/mach.h>
#elif defined(__EMSCRIPTEN__)
#include <emscripten/heap.h>
#include <malloc.h>
#elif defined(ESP_PLATFORM)
#include <esp_heap_caps.h>
#endif
static inline int64_t fire_plat_memory(int which) {
#if defined(__linux__)
    if (which == 1) { long pages = sysconf(_SC_PHYS_PAGES), size = sysconf(_SC_PAGESIZE); return pages > 0 && size > 0 ? (int64_t)pages * size : -1; }
    if (which == 2 || which == 3) {
        /* (MemAvailable: what programs can still get without swapping - more than MemFree, which leaves out the caches; statm: the resident pages) */
        char line[256];
        int64_t r = -1;
        FILE* f = fopen(which == 2 ? "/proc/meminfo" : "/proc/self/statm", "r");
        if (f == NULL) return -1;
        if (which == 2) { while (fgets(line, sizeof line, f)) if (strncmp(line, "MemAvailable:", 13) == 0) { r = (int64_t)strtoll(line + 13, NULL, 10) * 1024; break; } }
        else if (fgets(line, sizeof line, f)) { char* p; strtoll(line, &p, 10); r = (int64_t)strtoll(p, NULL, 10) * sysconf(_SC_PAGESIZE); }
        fclose(f);
        if (which == 2 && r < 0) { long pages = sysconf(_SC_AVPHYS_PAGES), size = sysconf(_SC_PAGESIZE); if (pages > 0 && size > 0) r = (int64_t)pages * size; }
        return r;
    }
    return -1;
#elif defined(__APPLE__)
    if (which == 1) { uint64_t mem = 0; size_t len = sizeof mem; return sysctlbyname("hw.memsize", &mem, &len, NULL, 0) == 0 ? (int64_t)mem : -1; }
    if (which == 2) {
        vm_statistics64_data_t vm;
        mach_msg_type_number_t count = HOST_VM_INFO64_COUNT;
        vm_size_t page = 0;
        if (host_page_size(mach_host_self(), &page) != KERN_SUCCESS || host_statistics64(mach_host_self(), HOST_VM_INFO64, (host_info64_t)&vm, &count) != KERN_SUCCESS) return -1;
        return (int64_t)(vm.free_count + vm.inactive_count + vm.purgeable_count) * (int64_t)page;
    }
    if (which == 3) {
        mach_task_basic_info_data_t info;
        mach_msg_type_number_t count = MACH_TASK_BASIC_INFO_COUNT;
        return task_info(mach_task_self(), MACH_TASK_BASIC_INFO, (task_info_t)&info, &count) == KERN_SUCCESS ? (int64_t)info.resident_size : -1;
    }
    return -1;
#elif defined(__EMSCRIPTEN__)
    if (which == 1) return (int64_t)emscripten_get_heap_max();
    if (which == 2) return (int64_t)emscripten_get_heap_max() - (int64_t)mallinfo().uordblks;
    if (which == 3) return (int64_t)emscripten_get_heap_size();
    return -1;
#elif defined(ESP_PLATFORM)
    if (which == 1) return (int64_t)heap_caps_get_total_size(MALLOC_CAP_DEFAULT);
    if (which == 2) return (int64_t)heap_caps_get_free_size(MALLOC_CAP_DEFAULT);
    if (which == 3) return (int64_t)heap_caps_get_total_size(MALLOC_CAP_DEFAULT) - (int64_t)heap_caps_get_free_size(MALLOC_CAP_DEFAULT);
    return -1;
#elif defined(configTOTAL_HEAP_SIZE) && (defined(FIRE_FREERTOS) || defined(INC_FREERTOS_H))
    /* (FreeRTOS with one of its heaps: the whole program lives in it) */
    if (which == 1) return (int64_t)configTOTAL_HEAP_SIZE;
    if (which == 2) return (int64_t)xPortGetFreeHeapSize();
    if (which == 3) return (int64_t)configTOTAL_HEAP_SIZE - (int64_t)xPortGetFreeHeapSize();
    return -1;
#else
    (void)which;
    return -1;
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
/* why the last fire_plat_dlopen failed (NULL: not known) */
static inline const char* fire_plat_dlerror(void) { return dlerror(); }
#else
static inline void* fire_plat_dlopen(const char* path) { (void)path; return NULL; }
static inline void* fire_plat_dlsym(void* library, const char* name) { (void)library; (void)name; return NULL; }
static inline const char* fire_plat_dlerror(void) { return NULL; }
#endif

#endif
