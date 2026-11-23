#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdbool.h>
#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT
#endif
EXPORT int64_t add(int64_t a, int64_t b) { return a + b; }
EXPORT int neg32(int a) { return -a; }
EXPORT double scale(double x, float f) { return x * f; }
EXPORT int64_t len(const char* s) { return (int64_t)strlen(s); }
EXPORT const char* greet(void) { return "hello from C"; }
EXPORT bool isodd(int64_t x) { return x % 2 != 0; }
EXPORT void twice(int64_t* p) { *p = *p * 2; }
EXPORT uint16_t upper(uint16_t c) { return c >= 97 && c <= 122 ? c - 32 : c; }
/* release: (Get's release function) - every allocation counted */
static int live;
EXPORT int live_count(void) { return live; }
EXPORT void* obj_new(int64_t v) { int64_t* p = (int64_t*)malloc(sizeof *p); *p = v; live++; return p; }
EXPORT void* obj_none(void) { return NULL; }
EXPORT int64_t obj_get(void* p) { return *(int64_t*)p; }
EXPORT int64_t obj_sum(void* a, int64_t k, void* b) { return *(int64_t*)a + k + *(int64_t*)b; }
EXPORT void obj_free(void* p) { live--; free(p); }
EXPORT char* text_dup(const char* s) { char* r = (char*)malloc(strlen(s) + 1); strcpy(r, s); live++; return r; }
EXPORT char* text_none(void) { return NULL; }
