/* native/forge/gen/gen.h - the generator bytecode -> C in C (docs/ROADMAP_C_PORT.md Phase D 1, docs/spec/FORGE_C.md "Phase D"): a 1:1 port of
 * src/fire.Native/CppGenerator*.cs and ProgramSplit.cs (commit 808e9788). This header: the types of the generator - CppGenerator's nested classes
 * (Func, ClassInfo, Flow, Out, Region, HEntry, StepResult), the decoded instruction (OpInfo.cs: Instr), the target (src/fire/Runtime/TargetProfile.cs),
 * and the generator itself (FgGen: the fields of the partial class CppGenerator, every file). Memory: the arena of the context; the flow states of
 * one chunk in an arena of their own (FgGen.flowArena), freed when the chunk is done (C#: garbage). Texts are fc8 as in the frontend; the C the
 * generator writes is ASCII except in comments and #line paths. */
#ifndef FIRE_GEN_H
#define FIRE_GEN_H

/* (one translation unit with the frontend: its macros that would meet names of the generator) */
#undef Check
#define MethodKey fgMethodKey
#define FieldType fgFieldType

/* ---- the target (TargetProfile, NativeTarget, EntryPoint) ---- */
typedef struct FgTarget {
    const char* name;
    const char* symbols[4]; int symbolCount;
    int floatWidth, intWidth, wordWidth;
    int optimize;                 /* OptimizeFor: 0 Speed, 1 Size */
    int defaultStackBytes;
    const char* const* imports; int importCount;   /* importCount < 0: all */
    const char* halPackage;
    bool isEmbedded;
    const char* programExtension;
    const char* displayName;
    /* NativeTarget */
    const char* platform;
    const char* const* includes; int includeCount;
    const char* platformPath;
    const char* const* defines; int defineCount;
    const char* entryName; int entryKind; bool entryExternC;   /* EntryKind: 0 Process, 1 Function */
    const char* const* compileArgs; int compileArgCount;
    const char* toolchain;
    bool lineDirectives, supportsThreads;
    bool pointerCalls;            /* NativeTarget.PointerCalls (the PC targets of Open Watcom) */
    const char* memoryModel;
} FgTarget;

/* ---- OpInfo.Decode ---- */
typedef struct FgInstr { int addr; int op; int a[6]; int n; int next; } FgInstr;
typedef struct FgCode { FgInstr* p; int n; } FgCode;

/* ---- the kinds of a generated function ---- */
typedef enum FgFuncKind { FK_Main, FK_Static, FK_Method, FK_Ctor, FK_Init, FK_Dtor, FK_Lambda, FK_FireBody } FgFuncKind;

/* a set of ints in the order of .NET's HashSet (insertion; dict.c with the int as its key text) */
typedef struct FgIntSet { FcDict* d; } FgIntSet;

typedef struct FgSpecParam { int slot; int kind; int unit; } FgSpecParam;
typedef struct FgHoist { int target; int* sources; int sourceCount; } FgHoist;

typedef struct FgFlow FgFlow;
typedef struct FgRegion FgRegion;
typedef struct FgOut FgOut;
typedef struct FgCAbi FgCAbi;
/* the C interface of a method (CppGenerator.CAbi.cs): per parameter the static type it has in C (0: a Value), the type of its C return value */
struct FgCAbi {
    int* paramType;
    const char** paramWhat;
    const char** paramDemand;
    int paramCount;
    int retType;
};
typedef struct FgFuncStat FgFuncStat;

typedef struct FgFunc {
    const char* name;           /* Name: fire_main, the export, or {Prefix}f{Index} */
    FcProto* proto;
    FcChunk* chunk;
    FgFuncKind kind;
    int index;
    const char* prefix;
    const char* exportName;
    bool external;
    int globalSlotCount;
    FcDict* threadVars;         /* SortedSet<int>: int keys, sorted when enumerated */
    int captureCount;
    FcDict* needsList;          /* HashSet<int> */
    FcList* hoists;             /* FgHoist* */
    bool needsArgList, accTrack, hasCallTArgs;
    const char* specTArgs;
    FcList* specParams;         /* FgSpecParam*, NULL: none */
    const char* specClassOwner;
    FcList* specClassArgs;      /* fc8, NULL: none */
    const char* code;
    FcDict* regions;            /* int (template) -> FgRegion* */
    FcDict* finReturn;          /* HashSet<int> */
    FcDict* finJumps;           /* int -> SortedSet<int> (FcDict of int keys) */
    FgCode decoded;
    FcDict* guardAddr;          /* int slot -> int addr */
    int* indexOf; int indexOfLen;   /* address -> index + 1 (0: no instruction there) */
    FcDict* targets;            /* HashSet<int> */
    FcDict* scalars;            /* SortedDictionary<string,string> (ordinal; sorted when enumerated) */
    FgFlow** entry;
    FgOut* root;
    FgCAbi* cabi;
    const char* csig;
    FgFuncStat* stat;
    bool usesTryTake;
} FgFunc;

typedef struct FgClassInfo { FcRtClass* rc; int id; const char* idText; } FgClassInfo;

/* ---- the flow state (Flow) ---- */
typedef struct FgScope { int id, declared; } FgScope;
typedef struct FgHEntry { FgRegion* region; bool finOnly; } FgHEntry;
struct FgFlow {
    int depth;
    FgScope* scopes; int scopeCount, scopeCap;
    uint64_t refs;
    int types[64];
    uint64_t scalar;
    const char* alias[64];
    uint64_t tempsDirty;
    uint64_t borrowed;
    bool selfAlive;
    const char** arrays; int arrayCount;   /* immutable: replaced on change */
    FgHEntry* handlers; int handlerCount, handlerCap;
    FgRegion* region;
    int* fins; int finCount, finCap;
};

struct FgOut {
    FcBuf code;
    const char* lineDir;
    int dbgLv;
    FcBuf tail;
    FcDict* exits;              /* string -> int */
    int maxDepth;
};

typedef struct FgCatch { const char* type; int addr; } FgCatch;
struct FgRegion {
    int template_;
    FgRegion* outer;
    FgFlow* reg;
    int catchScopeId;
    bool hasFinally;
    int finallyAddr; bool hasFinallyAddr;
    FgCatch* catches; int catchCount, catchCap;
    int start, end;
    FcDict* exitLabels;         /* SortedSet<int> */
    bool deferCatchRelease, returnExit;
    FgOut* out;
    FcDict* slotNames;          /* int -> name */
};

/* a step of the flow (StepResult) */
typedef struct FgEdge { int addr; FgFlow* state; } FgEdge;
typedef struct FgStep { bool fallsThrough; bool hasJump; int jumpTarget; FgEdge* extra; int extraCount; } FgStep;

/* a list of (string, int) - the SortedSet<(string Name, int Argc)> of the C#: kept as a dict of "name\x01argc", sorted when enumerated */
typedef struct FgNameArgc { const char* name; int argc; } FgNameArgc;

typedef struct FgGen FgGen;
struct FgGen {
    FcCtx* c;
    FcLinked* program;
    FcList* natives;            /* the native names */
    const FgTarget* target;
    FcList* units;              /* const FcU*, [0] Unitless */
    bool usesStringCall, usesLibCall;
    FcDict* unitConverters;     /* hidden class -> FgUnitConv* */
    FcList* strings;            /* fc8 */
    FcDict* stringIndex;        /* text -> index + 1 */
    FcDict* globals;            /* HashSet<int> */
    FcDict* funcByProto;        /* FcProto* -> FgFunc* */
    FcList* funcs;              /* FgFunc* */
    FcDict* specFuncs;          /* "proto-pointer\x01key" -> FgFunc* */
    FcDict* classes;            /* name -> FgClassInfo* */
    FcList* classList;          /* FgClassInfo* */
    FcDict* dispatchers;        /* SortedSet<(string, int)>: "name\x01argc" -> FgNameArgc* */
    FcDict* interfaceDispatchers;   /* "iface\x01name\x01argc" */
    FcDict* operators;          /* SortedSet<string> (culture order) */
    FcDict* isOfTypes;          /* SortedSet<string> (culture order) */
    FcDict* hasMembers;         /* SortedSet<string> ordinal */
    FcDict* targIds;            /* text -> id + 1 */
    FcDict* targListIds;        /* text -> id + 1 */
    FcDict* targOfs;            /* SortedSet<(string Cls, int Index)> */
    const char* pendingNewTArgs;
    const char* pendingLambdaTArgs;
    FcDict* selectorPaths;      /* joined path -> name */
    FcDict* fieldNames;         /* SortedSet<string> ordinal */
    FcDict* staticFields;       /* "class\x01field" -> index + 1 */
    FcDict* varRef;             /* key -> 1 */
    int version;
    bool usesExceptions, usesThreads, needsTimeObj, usesGraphics, usesWindows;
    int strip;                  /* StrippingLevel */
    bool usesTakeEnumerate;
    bool usesActors;
    FgFunc* cur;
    FgFunc* main;
    FcList* excTypes;           /* fc8 */
    bool usesTakeUpwards, usesElementPointers, usesTryDepth;
    FcBuf initCode;
    FcList* enumList;           /* const FcEnumType* */
    FcDict* unitFactorCaches;   /* SortedSet<(int, int)> */
    FcDict* catchCallbacks;     /* SortedDictionary<string, string> ordinal */
    const char* nativesCode;
    bool usesGlobalOwn;
    FcDict* refCallSites;       /* HashSet<(string, int)> */
    FcDict* lambdaDefaults;     /* HashSet<FgFunc*> */
    FcDict* callTArgTexts;      /* "method\x01argc" -> FcDict* (SortedSet<string> ordinal) */
    FcDict* classTArgTexts;     /* class -> FcDict* */
    FcList* objTypeNames;
    FcDict* objTypeIndex;       /* name -> index + 1 */
    FcDict* narrowEligible;     /* name -> 1 (true) / 2 (false) */
    FcDict* layouts;            /* FcRtClass* -> FgLayout* */
    bool lastSetMayThrow;
    FcDict* probeTargets;       /* "(cls ptr)\x01member" -> FgProbeTarget* */
    bool probeAnything;
    FcDict* varType;            /* key -> type (FC_IVAL(type) | a mark that it is there) */
    FcDict* fieldAddressTaken;  /* HashSet<string> */
    FcDict* typeIsOfIds;        /* text -> id + 1 */
    FcDict* externDecls;        /* SortedDictionary<string, string> (culture order of the keys) */
    bool usesInlineC;
    FcDict* restrictedMethods;  /* name -> 1/2 */
    FcDict* restrictedFields;   /* name -> 1/2 */
    FcDict* extensionProtos;    /* NULL until made */
    FcList* errorClasses;
    FcDict* pkgLocks;           /* native -> index + 1 */
    FcDict* arrClasses;         /* text -> id + 1 */
    FcDict* isOfWritten;        /* NULL until made */
    FcDict* derivedFieldNames;  /* NULL until made */
    FcDict* setterProtos;       /* NULL until made */
    FcDict* propertyNames;      /* NULL until made */
    bool initNext;
    const char* lineDir;
    bool lineFresh;
    FcDict* lineFiles;          /* int -> literal */
    bool kindsKept;
    int selfAliveAfter;         /* bool?: -1 null, 0, 1 */
    bool neutralAfter;
    FcDict* standardNativesWritten;   /* HashSet<string> OrdinalIgnoreCase (lower case keys) */
    FcDict* knownArrayElem;     /* "funcptr\x01var" -> elem + 1 */
    FcDict* needsDefaults;      /* "name\x01argc" -> 1/2 */
    FcDict* nativeThunks;       /* function -> FgNativeThunk* */
    FcDict* packageNatives;     /* native -> FgPkgNative* */
    FcList* packageImports;     /* FgPkgImportKey* */
    /* CppGenerator.CAbi.cs */
    FcDict* cRet;               /* FgFunc* -> int + 1 */
    FcDict* cAbiParams;         /* FgFunc* -> FgCAbi* (NULL value: none) */
    /* CppGenerator.Debug.cs */
    FcDict* funcDebug;
    FcDict* globalNames;        /* int -> name */
    FcDict* hiddenProtos;
    FcDict* protoNames;
    /* CppGenerator.Json.cs */
    bool usesJson, callsJsonEngine;
    /* CppGenerator.Overloads.cs */
    FcDict* overloadSelectors;
    FcDict* ovClassNames;       /* SortedSet<string> ordinal */
    FcDict* ovGenericTests;
    /* CppGenerator.Packed.cs */
    FcDict* rawType;            /* field name -> its packed type */
    FcDict* rawLayouts;         /* FcRtClass* -> the offsets of its packed fields */
    /* CppGenerator.Plugins.cs */
    bool openWorld, plugin;
    /* CppGenerator.Reflection.cs */
    bool usesReflection, usesProbes, reflMembers, reflCall, reflNew;
    FcDict* literalClasses;     /* HashSet<string> */
    FcDict* literalMembers;     /* HashSet<string> */
    FcDict* literalChunks;      /* HashSet<FcChunk*> */
    FcDict* reflectionMethodNames;   /* NULL until made */
    int rfListIndex;
    /* CppGenerator.Split.cs */
    int splitMode;              /* SplitMode?: -1 null, 0 Off, 1 On, 2 Auto */
    FcList* splitUnits;         /* FgSplitUnit*, NULL: one file */
    /* CppGenerator.Stats.cs */
    bool collectStats;
    /* CppGenerator.PointerCalls.cs */
    FcDict* pointerFuncs;
    /* modules (CppGenerator.Modules.cs): only the role of a single program is ported - the fields its code reads */
    int role;                   /* ModuleRole: 0 Single */
    bool separate, dll;
    const char* libraryHead;
    const char* moduleBodies;
    /* the caches of the queries (RuntimeClass, DeclaredType) */
    FcDict* rtNames;            /* FcRtClass* -> FcList* (FlattenedFieldNames) */
    FcDict* rtIndex;            /* FcRtClass* -> FcDict* (FieldIndex: name -> index + 1) */
    FcDict* rtCands;            /* "class\x01name\x01argc" -> FgCands* */
    FcDict* specCache;          /* text -> FgSpec* */
    FcDict* condCache;          /* text -> FgCond* */
    FcDict* nativeImportOf;     /* native -> import key */
    FcDict* typeIsOfNamesCache;
    /* the flow states of the chunk that is being generated */
    FbArena flowArena;
};

typedef struct FgUnitConv { int index; FgFunc* to; FgFunc* from; } FgUnitConv;

/* an exception of the generator: NativeNotSupportedException (FC_ERR_NOTSUPPORTED with the message) */
#define FG_FMT(...) fcFmt(g->c, __VA_ARGS__)
/* a part of the generator that is not ported yet: the program is refused with `unported: ...` (FC_ERR_INTERNAL) */
#define FG_UNPORTED_C(c, ...) fcThrow((c), FC_ERR_INTERNAL, 0, 0, NULL, fcFmt((c), "unported: " __VA_ARGS__))

#endif
