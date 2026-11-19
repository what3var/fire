/* native/forge: the frontend of the compiler in C (docs/spec/FORGE_C.md, docs/ROADMAP_C_PORT.md Phase C) - a 1:1 port of the C# classes
 * (L3: file by file, same names, same order of output). This header: the context of one translation and what every part needs - texts,
 * lists, the exceptions of the C# code (as setjmp/longjmp on the context), the diagnostics.
 *
 * Texts: the C# code counts in UTF-16 units (columns, lengths). A source is decoded into UTF-16 units once (FcU16); every other text is "fc8":
 * each UTF-16 unit as its UTF-8 sequence (a surrogate on its own, 3 bytes), the unit 0 as C0 80 - NUL-terminated, lossless, compared with strcmp.
 * Printed, fc8 becomes UTF-8 (fcOut8). No global state (L6): everything hangs on FcCtx. */
#ifndef FORGE_H
#define FORGE_H

#include "../base/fire_base.h"
#include <setjmp.h>
#include <stdarg.h>
#include <math.h>
#include <inttypes.h>
#include <ctype.h>

/* ---- growing buffers (malloc; for building texts) ---- */
typedef struct FcBuf { char* p; size_t len, cap; } FcBuf;
void fcBufPut(FcBuf* b, const char* s, size_t n);
void fcBufStr(FcBuf* b, const char* s);
void fcBufChar(FcBuf* b, char c);
void fcBufFmt(FcBuf* b, const char* fmt, ...);
void fcBufUnit(FcBuf* b, uint16_t unit);   /* one UTF-16 unit as fc8 */
void fcBufFree(FcBuf* b);

typedef struct FcU16 { uint16_t* p; int len, cap; } FcU16;   /* UTF-16 units (malloc) */
void fcU16Put(FcU16* b, const uint16_t* s, int n);
void fcU16Unit(FcU16* b, uint16_t c);
void fcU16Free(FcU16* b);

/* ---- lists (in the arena of the context): NULL is C#'s null, an empty list is a list ---- */
typedef struct FcList { void** items; int count, cap; bool own; } FcList;   /* own: an OwnNamespaceList (TypeRef.Namespaces) */

/* ---- exceptions: the C# throw/catch/finally, on the context ---- */
typedef enum FcErrKind { FC_ERR_NONE, FC_ERR_LEX, FC_ERR_PRE, FC_ERR_PARSE, FC_ERR_INTERNAL } FcErrKind;
typedef struct FcError { FcErrKind kind; const char* message; int line, column; const char* file; } FcError;
typedef struct FcTry { jmp_buf jb; struct FcTry* prev; } FcTry;

/* ---- diagnostics (Diagnostics.cs) ---- */
typedef enum FcSeverity { FC_SEV_ERROR, FC_SEV_WARNING } FcSeverity;
typedef struct FcDiagnostic { int line; const char* message; FcSeverity severity; const char* code; const char* file; int column; } FcDiagnostic;
typedef enum FcWarnTarget { FC_WARN_CLASS, FC_WARN_CONSTRUCTOR, FC_WARN_METHOD, FC_WARN_MEMBER } FcWarnTarget;
typedef struct FcDeclaredWarning { const char* cls; FcWarnTarget target; const char* name; int minArity, maxArity; bool isStatic; int id; const char* text; int line; const char* file; } FcDeclaredWarning;
typedef enum FcArea { FC_AREA_LEXER, FC_AREA_PREPROCESSOR, FC_AREA_PARSER, FC_AREA_RESOLVER, FC_AREA_COMPILER, FC_AREA_LINKER, FC_AREA_NATIVE } FcArea;
typedef struct FcCatalogEntry { const char* code; FcArea area; int literalLength; int partCount; const char** parts; } FcCatalogEntry;

typedef struct FcCustomUnit FcCustomUnit;

typedef struct FcCtx {
    FbArena arena;
    FcTry* tryTop;
    FcError err;
    /* DirectiveRegistry.Symbols (case-insensitive: kept in lower case) */
    FcList* symbols;
    /* DiagnosticCollector: active when `collecting` */
    bool collecting;
    FcList* diags;      /* FcDiagnostic* */
    FcList* declared;   /* FcDeclaredWarning* */
    /* ErrorCatalog, made on first use */
    FcCatalogEntry* catalog;
    int catalogCount;
    /* the units of the program (Unit.UseCustomUnits) */
    FcList* customUnits;   /* FcCustomUnit* in the order of their declarations */
    FbMap customTable;     /* symbol and name -> FcCustomUnit* */
    /* Parser._declaredNamespaces */
    FbMap declaredNamespaces;
    bool hasDeclaredNamespaces;
} FcCtx;

void fcCtxInit(FcCtx* c);
void fcCtxFree(FcCtx* c);

void* fcAlloc(FcCtx* c, size_t n);
const char* fcStr(FcCtx* c, const char* s);                 /* a copy in the arena */
const char* fcStrN(FcCtx* c, const char* s, size_t n);
const char* fcFmt(FcCtx* c, const char* fmt, ...);          /* printf into the arena */
const char* fcBufDone(FcCtx* c, FcBuf* b);                  /* the buffer's text into the arena, the buffer freed */

FcList* fcListNew(FcCtx* c);
void fcListAdd(FcCtx* c, FcList* l, void* item);
void fcListInsert(FcCtx* c, FcList* l, int at, void* item);
void fcListRemoveAt(FcList* l, int at);
FcList* fcListCopy(FcCtx* c, const FcList* l);
bool fcListHasStr(const FcList* l, const char* s);
#define FC_AT(l, i) ((l)->items[i])

/* texts */
const char* fc8FromU16(FcCtx* c, const uint16_t* u, int n);
int fcU16FromFc8(const char* s, FcU16* out);       /* appends the units of s; returns how many */
int fc8Units(const char* s);                       /* the length of s in UTF-16 units */
void fcOut8(FcBuf* out, const char* s);            /* fc8 as UTF-8 (a lone surrogate: U+FFFD, as .NET's encoder) */
void fcEsc(FcBuf* out, const char* s);             /* "text": printable ASCII as it is, \\ and \", every other UTF-16 unit \uXXXX */
bool fcStartsWith(const char* s, const char* prefix);
bool fcEndsWith(const char* s, const char* suffix);

/* the classification of a UTF-16 unit as .NET does it (char.IsLetter, IsDigit, IsLetterOrDigit, IsWhiteSpace) */
bool fcIsLetter(uint16_t c);
bool fcIsDigit(uint16_t c);
bool fcIsLetterOrDigit(uint16_t c);
bool fcIsWhiteSpace(uint16_t c);

/* a file as .NET's File.ReadAllText reads it (BOM, UTF-8 with U+FFFD for what is not) */
bool fcReadText(const char* path, FcU16* out);

/* exceptions */
#define FC_TRY(ctx) do { FcTry fcTry_; fcTry_.prev = (ctx)->tryTop; (ctx)->tryTop = &fcTry_; if (setjmp(fcTry_.jb) == 0) {
#define FC_CATCH(ctx) (ctx)->tryTop = fcTry_.prev; } else { (ctx)->tryTop = fcTry_.prev;
#define FC_END_TRY } } while (0)
_Noreturn void fcRethrow(FcCtx* c);
_Noreturn void fcThrow(FcCtx* c, FcErrKind kind, int line, int column, const char* file, const char* message);
_Noreturn void fcThrowF(FcCtx* c, FcErrKind kind, int line, int column, const char* file, const char* fmt, ...);

/* diagnostics */
const char* fcCodeOf(FcCtx* c, FcArea area, const char* rawMessage);
void fcDiagLogLine(FcBuf* out, const FcDiagnostic* d);           /* Diagnostic.ToLogLine, then '\n' */
FcList* fcDiagnosticsOf(FcCtx* c, const FcError* e, const char* file);   /* CompileDiagnostics.Of */
void fcWarn(FcCtx* c, int id, const char* message, int line, const char* file);   /* DiagnosticCollector.Warn */
void fcDeclare(FcCtx* c, FcDeclaredWarning* w);
#define FC_KEYWORD_HINT_TAIL " is a keyword and cannot be a name)"

#endif
