/* fire_embed.h - embedding fire in a C program: the compiler (libforge) and the VM (libfirevm) as libraries (docs/ROADMAP_C_PORT.md L6, docs/spec/EMBED.md).
 *
 *   libforge   native/forge/forge.c built with FIRE_FORGE_LIBRARY (no main): fire source or a project to a program file (.fbc), the diagnostics as data,
 *              the command line of forge in the process (fire_forge_main).
 *   libfirevm  native/vm/fire.c built with FIRE_VM_LIBRARY (no main): a program file run in this process - its arguments, its output to a callback or
 *              collected, its exit code, an exception nothing caught as data.
 *
 *   forge build-embed <folder>     builds both as shared libraries (libfireforge.so / fireforge.dll, libfirevm.so / firevm.dll) into a folder;
 *                                  the package `script` loads libfirevm from there (packages/script).
 *   forge amalgamate <file.c>      libforge as one C file (packages/forge/native/libforge.c): the package `forge` links it into the program.
 *
 * The interface is plain C with opaque handles and getters (no structs to keep in step): a host may link the libraries or load them with dlopen/LoadLibrary and
 * look the functions up by name. Texts are UTF-8 and NUL-terminated; a text a getter returns belongs to the handle and lives until the handle is freed.
 *
 * Instances: every FireForge is independent - translations in several threads at once are fine (each one has its context). The VM runs on the runtime of fire
 * (native/runtime/fire_rt.h), whose state is that of the process (L1: the same runtime as a native program): a FireVm is independent in its settings, but one
 * program runs at a time in a copy of libfirevm - a second fire_vm_run waits for the first (in any thread). Nothing calls exit(): an exception nothing catches,
 * `terminate(code)` and an error of the runtime end the program, fire_vm_run returns. What is shared in the process is listed in docs/spec/EMBED.md. */
#ifndef FIRE_EMBED_H
#define FIRE_EMBED_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define FIRE_EMBED_API __declspec(dllexport)
#elif defined(__GNUC__)
#define FIRE_EMBED_API __attribute__((visibility("default")))
#else
#define FIRE_EMBED_API
#endif

#define FIRE_EMBED_VERSION 1

/* what a program or the command line of forge writes: stream 1 stdout, 2 stderr; `length` bytes of UTF-8 (not NUL-terminated) */
typedef void (*FireWriteFn)(void* user, int stream, const char* data, size_t length);

/* ---- forge (libforge) ---- */
typedef struct FireForge FireForge;
typedef struct FireCompilation FireCompilation;

/* the modes of fire_forge_compile (`forge bytecode -m`) */
enum { FIRE_MODE_DEFAULT = -1, FIRE_MODE_DEBUG = 0, FIRE_MODE_RELEASE = 1, FIRE_MODE_PERFORMANCE = 2 };
/* the texts of a diagnostic */
enum { FIRE_DIAG_SEVERITY = 0, FIRE_DIAG_CODE = 1, FIRE_DIAG_FILE = 2, FIRE_DIAG_MESSAGE = 3, FIRE_DIAG_TEXT = 4 };

FIRE_EMBED_API int fire_embed_version(void);   /* FIRE_EMBED_VERSION of the library */

/* A compiler: `tables` is the folder with prelude.fire (native/tables), `packages` the store of installed packages (forge's Packages/), `native` the folder
 * native/ with the runtime (for the libraries of packages it builds); NULL: FIRE_TABLES, FIRE_PACKAGES, FIRE_NATIVE_ROOT, else next to the library
 * (<dir>/../tables, <dir>/Packages, <tables>/..). NULL when there is no memory. */
FIRE_EMBED_API FireForge* fire_forge_new(const char* tables, const char* packages, const char* native);
FIRE_EMBED_API void fire_forge_free(FireForge* forge);

/* Translates a program: `source` NULL - the file `path` (a .fire file or a project/solution, `forge bytecode <path>`); else `source` (UTF-8, `length`
 * bytes) is the text of a file named `path` (its folder: #include, resources; a relative path is taken from the working folder). The libraries of the
 * natives of its packages are built for the VM as `forge run` does. Never NULL but without memory: check fire_compilation_ok. */
FIRE_EMBED_API FireCompilation* fire_forge_compile(FireForge* forge, const char* path, const char* source, size_t length, int mode);
FIRE_EMBED_API int fire_compilation_ok(const FireCompilation* c);                                      /* 1: a program, 0: errors */
FIRE_EMBED_API const uint8_t* fire_compilation_program(const FireCompilation* c, size_t* length);       /* the program file (.fbc); NULL after errors */
FIRE_EMBED_API const char* fire_compilation_package_path(const FireCompilation* c);                    /* the folders of its package libraries (FIRE_PKG_PATH: ':', Windows ';') */
FIRE_EMBED_API int fire_compilation_diagnostic_count(const FireCompilation* c);                        /* the warnings as they came, then the errors */
FIRE_EMBED_API const char* fire_compilation_diagnostic(const FireCompilation* c, int index, int what);  /* FIRE_DIAG_*: "error"/"warning", "TC005", the file ("" none), the message, the line as forge prints it */
FIRE_EMBED_API int fire_compilation_diagnostic_line(const FireCompilation* c, int index);              /* 0: none */
FIRE_EMBED_API int fire_compilation_diagnostic_column(const FireCompilation* c, int index);
FIRE_EMBED_API void fire_compilation_free(FireCompilation* c);

/* The command line of forge in this process (`forge-c <argv[1]> ...`; argv[0] is the program): what it writes goes to the output of the FireForge
 * (fire_forge_set_output), the result is its exit code. One command line at a time per process (the options of the command line are the process's, as in
 * forge); `serve` is refused. */
FIRE_EMBED_API int fire_forge_main(FireForge* forge, int argc, char** argv);
/* Where fire_forge_main writes: `write` gets it as it comes (also what the programs `forge run` starts write); NULL (the default) - this process's stdout and
 * stderr. */
FIRE_EMBED_API void fire_forge_set_output(FireForge* forge, FireWriteFn write, void* user);

/* ---- the VM (libfirevm) ---- */
typedef struct FireVm FireVm;
typedef struct FireRun FireRun;

/* how a run ended */
enum { FIRE_RUN_ENDED = 0, FIRE_RUN_EXCEPTION = 1, FIRE_RUN_ERROR = 2, FIRE_RUN_TERMINATED = 3, FIRE_RUN_NOT_LOADED = 4 };
/* the texts of a run */
enum { FIRE_RUN_OUTPUT = 0, FIRE_RUN_ERRORS = 1, FIRE_RUN_EXCEPTION_TYPE = 2, FIRE_RUN_EXCEPTION_MESSAGE = 3, FIRE_RUN_STACK_TRACE = 4, FIRE_RUN_LOAD_ERROR = 5 };

FIRE_EMBED_API FireVm* fire_vm_new(void);
FIRE_EMBED_API void fire_vm_free(FireVm* vm);
/* the folders of the libraries of packages (as FIRE_PKG_PATH, which is searched after them); NULL: none */
FIRE_EMBED_API void fire_vm_set_package_path(FireVm* vm, const char* folders);
/* the output of the programs: `write` gets it as it comes; NULL (the default) - collected into the run (FIRE_RUN_OUTPUT, FIRE_RUN_ERRORS). With
 * `passThrough` 1 it goes to this process's stdout/stderr instead (as the program `fire` does). */
FIRE_EMBED_API void fire_vm_set_output(FireVm* vm, FireWriteFn write, void* user, int passThrough);

/* Runs a program file (`forge bytecode`, fire_compilation_program) to its end; `argv` (`argc` texts) are its Environment.Args, `name` names it in messages.
 * Never NULL but without memory. One program at a time in the process: a second call waits. */
FIRE_EMBED_API FireRun* fire_vm_run(FireVm* vm, const char* name, const uint8_t* program, size_t length, int argc, const char* const* argv);
FIRE_EMBED_API int fire_run_status(const FireRun* r);                  /* FIRE_RUN_* */
FIRE_EMBED_API int fire_run_exit_code(const FireRun* r);               /* as the program `fire`: 0, 1 (an exception, an error), the code of terminate, 2 not loaded */
FIRE_EMBED_API const char* fire_run_text(const FireRun* r, int what);  /* FIRE_RUN_OUTPUT ...: "" when there is none */
FIRE_EMBED_API void fire_run_free(FireRun* r);

#ifdef __cplusplus
}
#endif

#endif
