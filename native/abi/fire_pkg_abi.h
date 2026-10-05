/* fire package ABI (version 1): the C interface between the fire virtual machine (C#) and the natives of a package.
 *
 * A package that brings natives as C++ source (the way they are written for the native backend, see docs/PACKAGE_NATIVES.md) is built by the compiler into a shared
 * library (.dll, .so, .dylib) with a generated wrapper around the functions. The wrapper exports the functions below; the virtual machine loads the library and calls
 * them. A package may also bring a prebuilt library (any language) for a platform: it only has to export these functions.
 *
 * Values cross the boundary as plain data - numbers, text, byte buffers and arrays of these are COPIED: a native that changes an array it was given does not change the
 * array of the caller (return the changed array instead). Objects, lambdas and pointers cannot cross. Units are not carried.
 */
#ifndef FIRE_PKG_ABI_H
#define FIRE_PKG_ABI_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

enum {
    FIRE_UNDEFINED = 0,
    FIRE_BOOL = 1,      /* v.i: 0 or 1 */
    FIRE_INT = 2,       /* v.i */
    FIRE_FLOAT = 3,     /* v.f */
    FIRE_CHAR = 4,      /* v.i: the code point */
    FIRE_STRING = 5,    /* v.p: UTF-16 (length UTF-16 code units, not zero terminated) */
    FIRE_ARRAY = 6,     /* v.p: `length` fire_val */
    FIRE_BUFFER = 7     /* v.p: `length` bytes */
};

typedef struct fire_val {
    int32_t kind;
    int32_t length;
    union { int64_t i; double f; const void* p; } v;
} fire_val;

#define FIRE_PKG_ABI_VERSION 1

/* The entry points of a package library. */
int fire_pkg_abi_version(void);                    /* FIRE_PKG_ABI_VERSION */
int fire_pkg_function_count(void);
const char* fire_pkg_function_name(int index);     /* the name that fire code calls, e.g. "__sk_read" */
int fire_pkg_function_arity(int index);
/* Calls function `index`. Returns 0, then `*result` is the result; or not 0, then `error` holds a message (zero terminated, at most errorSize bytes).
 * Memory behind `*result` (text, array, buffer) belongs to the library and stays valid until the next call from the same thread: the caller copies it. */
int fire_pkg_call(int index, const fire_val* args, int argc, fire_val* result, char* error, int errorSize);

#ifdef __cplusplus
}
#endif

#endif
