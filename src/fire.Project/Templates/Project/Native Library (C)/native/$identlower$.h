/* The natives of $name$: C functions on Value (C99, docs/PACKAGE_NATIVES.md).
 * Every `static inline Value name(Value a, ...)` below becomes the fire function `__name` that the prelude wraps. A last parameter `OwnList* list` lets a
 * function return what it allocates (a string, an array). All packages of a program share one C file: give every function and global a prefix of your own. */
#ifndef NATIVES_$identlower$_H
#define NATIVES_$identlower$_H

#include <stdint.h>

/* $identlower$_add(a, b) -> int */
static inline Value $identlower$_add(Value a, Value b) {
    return Int(a.i + b.i);
}

#endif
