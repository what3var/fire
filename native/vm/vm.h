/* fire VM in C (docs/spec/VM_C.md): the structures of a loaded program and of the interpreter.
 *
 * A program is what the .fbc file holds (docs/spec/BYTECODE.md, section 48), read into an arena (vm_load.c). The interpreter (vm_run.c) runs it on the
 * runtime of the native backend (native/runtime/fire_rt.h): its values are the runtime's `Value`s, its objects the runtime's `Obj`s with class descriptors that
 * the loader registers at run time, its scopes own through the runtime's `OwnList`s, its exceptions go through the runtime's handler stack (`Handler`,
 * `throwValue`, `resumeThrow`). What only the VM needs lives here, in side tables of the VM - nothing is added to the runtime's structures (L1).
 *
 * The names follow the C# VM (src/fire.Runtime/VM.cs) and the program model (src/fire/Bytecode: RuntimeClass, FunctionProto, Chunk), L3. */
#ifndef FIRE_VM_H
#define FIRE_VM_H

typedef struct VmProto VmProto;
typedef struct VmClass VmClass;
typedef struct VmProgram VmProgram;

/* A try's template (HandlerTemplate): its catch clauses (the type by name, the address of the catch block), its finally and the HandlerInfo the runtime's
 * throwValue reads. */
typedef struct VmHandlerT {
    uint32_t ncatch;
    const char** types;     /* NULL: no type (catches every exception) */
    uint32_t* addrs;
    int32_t* typeIds;       /* the catch type of the runtime (FIRE_EXC_ANY or the id of the class + 1), set on first use */
    int32_t finallyAddr;    /* -1: none */
    bool resolved;
    HandlerInfo info;
} VmHandlerT;

typedef struct VmChunk {
    const uint8_t* code;
    uint32_t ncode;
    Value* k;               /* the constants as values of the runtime (a string: an immortal Str owned by the program) */
    const char** kname;     /* a string constant as an interned UTF-8 name (field, method and class names are string constants), else NULL */
    uint32_t nk;
    uint32_t* units;        /* the chunk's unit table: the unit ids of the runtime */
    uint32_t nunits;
    VmProto** fn;
    uint32_t nfn;
    VmHandlerT* handlers;
    uint32_t nhandlers;
    int32_t* lines;         /* (offset, source, line) triples, NULL without line tables */
    uint32_t nlines;
    VmClass* owner;         /* Chunk.OwnerClass: the class whose code it is (access checks), NULL at the top level */
    /* inline caches by code offset (made on first use): the class id seen last and what it resolved to */
    uint32_t* icCls;
    void** icPtr;
} VmChunk;

struct VmProto {
    VmChunk* chunk;
    const char* name;       /* the method's name (for messages), NULL for a lambda */
    int32_t nparams;
    VmProto** defaults;     /* per parameter its default value (a function without parameters), NULL entries: none */
    uint32_t ndefaults;
    uint8_t access;         /* 0 none (public), 1 public, 2 private, 3 protected */
    uint8_t isStatic, readsOnly;
    uint64_t refMask, captureCopyMask, captureMovedMask;
    const char** paramTypes;   /* the overload descriptors (Overloads: "k:int", "c:Dog", "i:IShape", "g:T", ""), NULL: none */
    uint32_t nparamTypes;
    const char* fieldGetterOf;
    const char** paramNames;
    uint32_t nparamNames;
    int32_t typeParamCount;
    VmProto** specs;           /* specializations (`Add<int>`), NULL: none */
    uint32_t nspecs;
    const char* specOwner;
    const char** specArgs;
    uint32_t nspecArgs;
    int64_t specDefinedMask;
    VmClass* owner;            /* the class whose member it is (NULL: a lambda or the top level) */
    VmClass* declaring;        /* the class that declares it (for a method: owner) */
};

typedef struct VmField { const char* name; VmProto* init; } VmField;
/* FieldInfo: what a field declares (its unit, kind, class, typed array, access) */
typedef struct VmFieldInfo {
    const char* name;
    const char* requiredUnit;  /* the unit's text, NULL: none */
    uint32_t unitId;           /* the unit of the runtime (0xFFFFFFFF: none) */
    uint8_t requiredKind;      /* DeclaredKind */
    const char* requiredType;
    uint32_t requiredArray;
    const char* requiredArrayType;
    const char* arrayOfParam;
    uint8_t flags;             /* 1 derivedDefined, 2 static */
    uint8_t access;            /* 0 public, 1 private, 2 protected */
    const char* typeText;
} VmFieldInfo;
typedef struct VmMethod { const char* name; VmProto** ov; uint32_t nov; } VmMethod;
typedef struct VmCtor { int32_t arity; VmProto* p; } VmCtor;

/* Candidates (RuntimeClass.Candidates): the methods a call of `name` with `argc` arguments may reach, the first the one without overloading by type. */
typedef struct VmCands { const char* name; int32_t argc; VmProto** protos; VmClass** declaring; uint32_t n; bool runtimeChoice; struct VmCands* next; } VmCands;

enum { VC_ACTOR = 1, VC_RECORD = 2, VC_REFLHELPER = 4, VC_KEEP = 8, VC_NOTSER = 16, VC_NOCOPY = 32 };

struct VmClass {
    const char* name;
    uint32_t baseRef;       /* 0: none, else index + 1 in the program's classes */
    VmClass* base;
    uint8_t flags;          /* VC_* */
    const char** interfaces;
    uint32_t ninterfaces;
    const char** baseTypeArgs;
    uint32_t nbaseTypeArgs;
    VmField* fields;        /* its own instance fields */
    uint32_t nfields;
    VmFieldInfo* finfo;
    uint32_t nfinfo;
    const char** layout;    /* the fields of an object: the base's first (interned names; index = the Value slot of the runtime's Obj) */
    uint32_t nlayout;
    const char** snames;    /* static fields: staticValues, then staticFields */
    Value* svals;
    VmProto** sinit;
    uint32_t nstatic;
    const char** readonlyStatics;
    uint32_t nreadonly;
    VmMethod* methods;
    uint32_t nmethods;
    VmCtor* ctors;
    uint32_t nctors;
    VmProto** xctors;       /* extraConstructors (overloads by type) */
    uint32_t nxctors;
    VmProto* dtor;
    VmCands* cands;         /* the candidates computed so far */
    uint32_t id;            /* the class id of the runtime (index into g_classDescs) */
    int32_t typeParams;     /* the number of type parameters (a generic class `List<T>`: 1), from its mangled name or its base's */
    bool isException;       /* Exception or derived from it */
    bool hasDtor;           /* it or a base has a destructor */
    bool layoutDone;
};

typedef struct VmUnitDecl { const char* converter; const char* ulconverter; } VmUnitDecl;

struct VmProgram {
    FbArena arena;
    FbInterns interns;
    const char** names;     /* the table of names of the file, interned */
    uint32_t nnames;
    uint32_t* unitIds;      /* the file's unit table: index -> the unit id of the runtime */
    uint32_t nunits;
    UnitInit* unitInit;     /* (the table given to the runtime: unitRows rows) */
    uint32_t unitRows;
    const char* dimNames[FIRE_NDIMS];
    uint32_t ndims;
    FireEnum* enums;        /* the file's enum table: g_enums while the program runs */
    uint32_t nenums;
    VmClass** classes;
    uint32_t nclasses;
    FbMap classByName;      /* the program's table: name -> class */
    const char** nativeImports;
    uint32_t nnativeImports;
    struct { const char* name; const uint8_t* data; uint32_t len; } * resources;
    uint32_t nresources;
    VmChunk* top;
    uint8_t mode, floatWidth, intWidth, wordWidth;   /* mode: 0 Debug, 1 Release, 2 Performance */
    VmClass* extClass[16];  /* the base-type extensions by the runtime's kind (`class extends string`: "$string") */
    /* the strings of the constants (immortal Str, freed with the program) */
    Str** strs;
    uint32_t nstrs, capStrs;
};

/* ---- the interpreter ---- */

/* A scope (BYTECODE 3): dynamic slots, and as an owner two lists of the runtime - what it owns (`own`, marked with the height of the temporary pool when
 * it was entered) and the statement temporaries (`temp`, SPEC 2.1). */
typedef struct VmScope {
    struct VmScope* parent;
    Value* slots;
    uint32_t n, cap;
    OwnList own;
    OwnList temp;
    struct VmScope* nextFree;
} VmScope;

enum { FR_TOP, FR_CALL, FR_CTOR, FR_BASECTOR, FR_NESTED, FR_CATCH };
enum { RM_PUSH, RM_VALUE, RM_DROP };

typedef struct VmHandlerRec VmHandlerRec;
typedef struct VmFrame {
    VmChunk* chunk;
    const uint8_t* ip;
    VmScope* scope;         /* the current scope */
    VmScope* base;          /* the scope of the call (parent: the global scope); for a catch block the catch's scope */
    Value self;
    VmProto* proto;         /* the function it runs (NULL: the top level, a catch block) */
    TArgs ctargs;           /* the type arguments of the call of a generic method (NULL: none) */
    uint32_t spBase;
    uint8_t kind;
    uint8_t catchEnded;     /* a catch block: its scope is left - the next Jump leaves the catch */
    uint8_t retMode;        /* what the caller gets on return: RM_PUSH the result, RM_VALUE `after` instead (a setter, SetIndex), RM_DROP nothing */
    Value after;
    uint32_t opJump;        /* an operator of an object stood for a JumpIfNot (catchEnded bit 1): where its false result jumps to */
    VmHandlerRec* rec;      /* a catch block: the handler it belongs to */
    uint32_t nfin;          /* the finally blocks this frame is in (their records on the VM's stack of finally records) */
} VmFrame;

/* A `try` that is registered: the runtime's Handler (first: the runtime hands it back as the target of an unwinding) and where the try lives. */
struct VmHandlerRec {
    Handler h;
    struct Vm* vm;
    VmHandlerT* t;
    int32_t frame;          /* the index of the frame that owns the try */
    VmScope* scope;         /* its scope when the try began */
    uint32_t sp;            /* the height of the operand stack then */
    uint32_t extraExits;    /* a catch that leaves more scopes after its own (break/continue out of a catch) */
    uint64_t token;         /* while its catch block runs: the throw it handles (the runtime's Pending.token) */
    bool inCatch;           /* its catch block runs (a finally of the try still runs after it) */
    VmHandlerRec* nextDead; /* given up (its catch block ended with another exception): freed at the next landing */
};

/* A finally block that runs (EnterFinallyNormal, or an exit through it): what happens at its EndFinally. */
enum { FIN_NORMAL, FIN_JUMP, FIN_RETURN, FIN_RETHROW };
typedef struct VmFinally { uint8_t kind; uint32_t label; Value value; int32_t frame; VmScope* scope; uint32_t sp; } VmFinally;

#define VM_STACK 65536u
#define VM_FRAMES 8192
#define VM_FINALLY 256

typedef struct Vm {
    VmProgram* prog;
    Value* stack;
    uint32_t sp;
    VmFrame* frames;
    int32_t nframes;
    VmScope* global;
    VmScope* freeScopes;
    Handler bottom;         /* catches what nothing else catches: the script ends (L6: no exit) */
    HandlerInfo bottomInfo;
    int32_t bottomTypes[1];
    int failed;             /* 1: an unhandled exception, 2: a run-time error of the runtime (fatal) */
    int quiet;              /* the script is being torn down after a failure: no destructors */
    int terminated;         /* terminate(code): the script ends with this code */
    int exitCode;
    Value ret;              /* the result of a nested run */
    uint64_t copyMask;      /* CopyArgs: how the arguments of the next call go in */
    TArgs pendingTArgs;     /* SetTypeArgs: the type arguments of the next `new` / call / lambda */
    uint8_t pendingTArgsFor;
    const char* pendingArgNames; /* ArgNames: the names of the next call's arguments */
    VmHandlerRec* dead;     /* handlers whose catch block was left by another exception (the runtime may still read them until its throwValue returns) */
    jmp_buf* exitJump;      /* the runtime's exitNow (FIRE_VM): back to the runner of the script */
    VmFinally fin[VM_FINALLY];
    uint32_t nfin;
    /* the class descriptors and catch types this program registered */
    FireClassDesc* descs;
    int32_t** excTypes;
    VmClass** byId;
    uint32_t nids;
    uint32_t firstId;
    /* the previous state of the runtime's tables this program replaced (units, enums) */
    const FireEnum* savedEnums;
    uint32_t savedEnumCount;
    /* the interned type arguments (VmTArg) */
    struct VmTArg* targs;
    int argc; char** argv;
} Vm;

/* A type argument of the VM: the runtime's TArg (its `display` is the text, `holds`/`adapt` stay NULL - the VM checks with its own fitsTArg, VM_SPIKE
 * point 1) and what the VM knows about it. */
typedef struct VmTArg { TArg ta; const char* text; VmClass* cls; struct VmTArg* next; } VmTArg;

#endif
