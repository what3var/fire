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
    /* (the builder, Phase D 2) */
    const char* const* linkLibs; int linkLibCount;
    const char* const* toolchains; int toolchainCount;   /* the kinds of toolchain that can build for it; NULL: any */
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
typedef struct FgFuncDebug FgFuncDebug;

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

/* ---- modules (CppGenerator.Modules.cs) ---- */
/* ModuleSpec: a library module of the program - its name (`core`, the import key, the import name of a library of the project), the C name, its sources
 * and the classes it declares (in a fixed order: their names) */
typedef struct FgModuleSpec FgModuleSpec;
struct FgModuleSpec {
    const char* name;
    const char* id;
    const char* idPx;           /* id + "_" (Px) */
    int index;
    FcDict* sources;            /* HashSet<int> ("%d" keys, ascending: added in the order of the sources) */
    FcList* deps;               /* FgModuleSpec*: the modules it needs, transitively (in dependency order) */
    FcList* classes;            /* names, sorted ordinal */
    int base;                   /* the program: the class id of the module's first class */
};
/* ModuleFlags: the switches of the generator that hold for the whole program, one bit each in the order of the record (ToString: '0'/'1') */
enum { MF_Exceptions, MF_Threads, MF_Actors, MF_Reflection, MF_Probes, MF_ElementPointers, MF_TryDepth, MF_GlobalOwn, MF_TakeUpwards, MF_TakeEnumerate,
    MF_Json, MF_TimeObj, MF_StringCall, MF_Graphics, MF_Windows, MF_Count };
/* the natives in C of other files a generator calls (linkage "static": the `fire_cn_` functions) */
typedef struct FgSharedNative { int argc; bool needsList; const char* import; } FgSharedNative;
/* ModuleNeeds: what a library module needs from the program */
typedef struct FgModuleNeeds {
    FcDict* dispatchers;        /* SortedSet<(string, int)>: "name\x01argc" -> FgNameArgc* */
    FcDict* fields; FcDict* fieldAddresses; FcDict* isOf;   /* SortedSet<string> ordinal */
    FcDict* targOfs;            /* SortedSet<(string Cls, int Index)> (as FgGen.targOfs) */
    FcDict* operators; FcDict* overloadClasses; FcDict* errorClasses;   /* SortedSet<string> ordinal */
    FcDict* refCallSites;       /* HashSet<(string, int)> */
    FcDict* staticFields;       /* SortedSet<(string Class, string Field)>: "class\x01field" */
    FcList* units;              /* const FcU* */
    FcList* enums;              /* const FcEnumType* */
    FcList* excTypes;           /* fc8 */
    FcDict* reflectionNatives;  /* SortedSet<string> ordinal */
    FcDict* classes;            /* SortedSet<string> ordinal */
    FcDict* madeBy;             /* class -> FcList* of names; NULL: an interface of before */
    FcDict* sharedNatives;      /* SortedDictionary ordinal: function -> FgSharedNative* */
} FgModuleNeeds;
/* ModuleOutput: a library module as generated */
typedef struct FgModuleOutput {
    FgModuleSpec* spec;
    const char* body;
    const char* nativeThunks;
    FcList* cppNatives;         /* fc8 */
    FgModuleNeeds* needs;
    uint32_t flags;
    const char* nativesC;
    FcList* exports;            /* fc8 */
    bool jsonEngine;
    FcDict* hostImports;        /* SortedDictionary ordinal: name -> declaration */
    const char* linkC;
} FgModuleOutput;
/* CppGenerator.GenerateSources (gen/native.c): the program in C, its natives in C++ (NULL: none), the units of a split program (NULL: one file), the stats */
typedef struct FgSources { const char* program; const char* natives; FcList* units; FcList* funcs; } FgSources;
/* (module, ABI): a plugin's dependencies, the interfaces of linkage "shared" */
typedef struct FgModuleDep { FgModuleSpec* spec; const char* module; const char* abi; } FgModuleDep;
/* ProtoKey: the module and the stable key of a member of a class */
typedef struct FgProtoKey { FgModuleSpec* module; const char* key; } FgProtoKey;
enum { FG_ROLE_SINGLE = 0, FG_ROLE_LIBRARY = 1, FG_ROLE_PROGRAM = 2 };   /* ModuleRole */

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
    FcDict* selectorPaths;      /* the path joined by \x01 -> FgSelectorPath* (Dictionary<string[], string>: insertion order) */
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
    FcList* packageImports;     /* FgPkgImportKey* (gen/packages.c) */
    FcStore* store;             /* the installed packages */
    FcDict* instImports;        /* FcPkgImport* -> FgInstImport* */
    FcDict* jsonFields;         /* FcRtClass* -> FcList* of FgJsonField* */
    FcDict* bridgeCache;        /* "bridge\x01import" -> FcDict* */
    /* CppGenerator.CAbi.cs */
    FcDict* cRet;               /* FgFunc* -> int + 1 */
    FcDict* cAbiParams;         /* FgFunc* -> FgCAbi* (NULL value: none) */
    /* CppGenerator.Debug.cs */
    FcDict* funcDebug;
    FcDict* globalNames;        /* int -> name */
    FcDict* hiddenProtos;
    FcDict* protoNames;
    FcDict* dbgLines;           /* SortedSet<(int Src, int Line)>: "src,line" (sorted when written) */
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
    FcDict* reflNativesUsed;    /* SortedSet<string> ordinal (CppGenerator.Modules.cs: the reflection natives a module calls) */
    /* CppGenerator.Split.cs */
    int splitMode;              /* SplitMode?: -1 null, 0 Off, 1 On, 2 Auto */
    FcList* splitUnits;         /* FgSplitUnit*, NULL: one file */
    /* CppGenerator.Stats.cs */
    bool collectStats;
    /* CppGenerator.PointerCalls.cs */
    FcDict* pointerFuncs;
    /* modules (CppGenerator.Modules.cs, ModuleDispatch.cs, StaticLinkage.cs, Plugins.cs: gen/modules.c, gen/moddispatch.c, gen/staticlinkage.c, gen/plugins.c) */
    int role;                   /* ModuleRole: 0 Single, 1 Library, 2 Program */
    bool separate, dll;
    const char* libraryHead;
    const char* moduleBodies;
    FgModuleSpec* self;         /* the module this generator makes (a library) */
    FcDict* modules;            /* name -> FgModuleSpec* (the program: all; a library: the modules it needs and itself) */
    FcDict* moduleOfSource;     /* int -> FgModuleSpec* */
    bool hasForced; uint32_t forced;   /* the switches the modules agreed on (ModuleFlags?) */
    FcList* libraries;          /* FgModuleOutput* (the program), NULL: none */
    FcDict* protoKeys;          /* FcProto* -> FgProtoKey*, NULL until made */
    FcDict* foreignClasses;     /* name -> FgClassInfo* */
    FcDict* usedModules;        /* FgModuleSpec* (pointer keys) */
    FcDict* fixedIds;           /* name -> id + 1, NULL: ids by demand */
    int nextClassId, progBase;
    FcDict* demandedClasses;    /* SortedSet<string> ordinal */
    FcDict* madeBy;             /* SortedDictionary<string, SortedSet<string>> ordinal: owner -> FcDict* */
    const char* demandOwner;    /* NULL: outside a function body */
    bool keeping;
    FcList* extServed;          /* FgClassInfo*, NULL until made */
    FcDict* libDispatchers;     /* HashSet<(string, int)> */
    FcDict* libFields; FcDict* libFieldAddresses;
    bool tablesAll;
    FcDict* externTables;       /* HashSet<string> */
    FcDict* libraryClassTables; /* "module\x01class" -> table name */
    FcBuf libraryTablesCode;
    FcDict* sharedNatives;      /* SortedDictionary ordinal: function -> FgSharedNative* */
    FcDict* hostImports;        /* SortedDictionary ordinal: name -> C declaration */
    FcDict* hostArrays;         /* HashSet<string> */
    const char* libraryThunks;
    FcList* pluginDeps;         /* FgModuleDep* (module, abi) */
    FcList* sharedAbis;         /* FgModuleDep* with the spec (linkage "shared"), NULL: none */
    FcList* dllModules;         /* FgModuleOutput*, NULL: none */
    const char* runtimeKey;
    const char* toolchainStamp;
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

typedef struct FgSelectorPath { const char* name; FcList* path; } FgSelectorPath;
typedef struct FgUnitConv { int index; FgFunc* to; FgFunc* from; } FgUnitConv;

/* an exception of the generator: NativeNotSupportedException (FC_ERR_NOTSUPPORTED with the message) */
#define FG_FMT(...) fcFmt(g->c, __VA_ARGS__)
/* a part of the generator that is not ported yet: the program is refused with `unported: ...` (FC_ERR_INTERNAL) */
#define FG_UNPORTED_C(c, ...) fcThrow((c), FC_ERR_INTERNAL, 0, 0, NULL, fcFmt((c), "unported: " __VA_ARGS__))

#endif
