/* fire native platform layer, the process (Environment) on DOS: the name of the platform, the arguments, the path of the program (the C library of Open
 * Watcom: _cmdname) and the environment variables of DOS (in the code page of the machine; ASCII is the same in UTF-8). No libraries loaded at run time. */
#ifndef FIRE_ENV_DOS_H
#define FIRE_ENV_DOS_H

#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <process.h>

#ifndef FIRE_PLAT_NAME
#define FIRE_PLAT_NAME "dos"
#endif

static inline const char* fire_plat_name(void) { return FIRE_PLAT_NAME; }

static inline void fire_plat_eachArg(int argc, char** argv, void (*fn)(const char* utf8, void* ctx), void* ctx) {
    int i;
    for (i = 1; i < argc && argv != NULL; i++) fn(argv[i], ctx);
}

static inline int fire_plat_programPath(char* out, size_t cap) {
    char path[144];
    size_t n;
    if (_cmdname(path) == NULL) return 0;
    n = strlen(path);
    if (n >= cap) return 0;
    memcpy(out, path, n + 1);
    return (int)n;
}

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

/* (the copy of DOS's environment that the program has; a program it starts gets it) */
static inline void fire_plat_setenv(const char* name, const char* value) {
    setenv(name, value, 1);   /* (Open Watcom: a NULL value removes the variable) */
}

static inline void fire_plat_envEach(void (*fn)(const char* name, size_t nameLen, const char* value, void* ctx), void* ctx) {
    char** e;
    for (e = environ; e != NULL && *e != NULL; e++) {
        const char* eq = strchr(*e, '=');
        if (eq == NULL || eq == *e) continue;
        fn(*e, (size_t)(eq - *e), eq + 1, ctx);
    }
}

static inline void* fire_plat_dlopen(const char* path) { (void)path; return NULL; }
static inline void* fire_plat_dlsym(void* library, const char* name) { (void)library; (void)name; return NULL; }
static inline const char* fire_plat_dlerror(void) { return "DOS loads no libraries at run time."; }

#endif
