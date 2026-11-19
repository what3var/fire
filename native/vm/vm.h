/* fire VM in C (spike, docs/spec/VM_SPIKE.md): the structures of a loaded program and of the interpreter.
 *
 * A program is what the .fbc file holds (docs/spec/BYTECODE.md, section 48), read into an arena (vm_load.c). The interpreter (vm_run.c) runs it on the
 * runtime of the native backend (native/runtime/fire_rt.h): its values are the runtime's `Value`s, its objects the runtime's `Obj`s with class descriptors that
 * the loader registers at run time, its scopes own through the runtime's `OwnList`s, its exceptions go through the runtime's handler stack (`Handler`,
 * `throwValue`, `resumeThrow`). What only the VM needs lives here, in side tables of the VM - nothing is added to the runtime's structures. */
#ifndef FIRE_VM_H
#define FIRE_VM_H

/* ---- the arena of a program: everything the loader makes, freed at once ---- */
typedef struct VmArenaBlock { struct VmArenaBlock* next; size_t used, cap; } VmArenaBlock;
typedef struct VmArena { VmArenaBlock* head; } VmArena;

typedef struct VmProto VmProto;
typedef struct VmClass VmClass;

/* A try's template (HandlerTemplate): its catch clauses (the type by name, the address of the catch block) and the HandlerInfo the runtime's throwValue reads. */
typedef struct VmHandlerT {
    uint32_t ncatch;
    const char** types;     /* NULL: no type (catches every exception) */
    uint32_t* addrs;
    int32_t* typeIds;       /* the catch type of the runtime (FIRE_EXC_ANY or the id of the class), set when the program is registered */
    int32_t finallyAddr;    /* -1: none (not supported by the spike) */
    HandlerInfo info;
} VmHandlerT;

typedef struct VmChunk {
    const uint8_t* code;
    uint32_t ncode;
    Value* k;               /* the constants as values of the runtime (a string: an immortal Str owned by the program) */
    const char** kname;     /* a string constant as an interned UTF-8 name (field, method and class names are string constants), else NULL */
    uint32_t nk;
    VmProto** fn;
    uint32_t nfn;
    VmHandlerT* handlers;
    uint32_t nhandlers;
    /* inline caches by code offset (made on first use): the class id seen last and what it resolved to */
    uint32_t* icCls;
    void** icPtr;
} VmChunk;

struct VmProto {
    VmChunk* chunk;
    int32_t nparams;
    VmProto** defaults;     /* per parameter its default value (a function without parameters), NULL entries: none */
    uint32_t ndefaults;
    uint8_t access, isStatic;
    VmClass* owner;         /* the class whose member it is (NULL: a lambda or the top level) */
};

typedef struct VmField { const char* name; VmProto* init; } VmField;
typedef struct VmMethod { const char* name; VmProto** ov; uint32_t nov; } VmMethod;
typedef struct VmCtor { int32_t arity; VmProto* p; } VmCtor;

struct VmClass {
    const char* name;
    uint32_t baseRef;       /* 0: none, else index + 1 in the program's classes */
    VmClass* base;
    uint8_t flags;
    VmField* fields;        /* its own instance fields */
    uint32_t nfields;
    const char** layout;    /* the fields of an object: the base's first (interned names; index = the Value slot of the runtime's Obj) */
    uint32_t nlayout;
    const char** snames;    /* static fields: staticValues, then staticFields */
    Value* svals;
    VmProto** sinit;
    uint32_t nstatic;
    VmMethod* methods;
    uint32_t nmethods;
    VmCtor* ctors;
    uint32_t nctors;
    VmProto* dtor;
    uint32_t id;            /* the class id of the runtime (index into g_classDescs) */
    bool isException;       /* Exception or derived from it */
    bool hasDtor;           /* it or a base has a destructor */
};

typedef struct VmProgram {
    VmArena arena;
    const char** names;     /* the table of names of the file, interned */
    uint32_t nnames;
    VmClass** classes;
    uint32_t nclasses;
    const char** classNames; /* the program's table: name -> class */
    VmClass** classByName;
    uint32_t nclassNames;
    VmChunk* top;
    uint8_t mode, floatWidth, intWidth, wordWidth;
    uint32_t nunits;
    /* the strings of the constants (immortal Str, freed with the program) */
    Str** strs;
    uint32_t nstrs, capStrs;
    /* interning of names */
    const char** internTab;
    uint32_t internCap, internCount;
} VmProgram;

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

typedef struct VmHandlerRec VmHandlerRec;
typedef struct VmFrame {
    VmChunk* chunk;
    const uint8_t* ip;
    VmScope* scope;         /* the current scope */
    VmScope* base;          /* the scope of the call (parent: the global scope); for a catch block the catch's scope */
    Value self;
    uint32_t spBase;
    uint8_t kind;
    uint8_t catchEnded;     /* a catch block: its scope is left - the next Jump leaves the catch */
    VmHandlerRec* rec;      /* a catch block: the handler it belongs to */
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
    VmHandlerRec* nextDead; /* given up (its catch block ended with another exception): freed at the next landing */
};

#define VM_STACK 65536u
#define VM_FRAMES 8192

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
    Value ret;              /* the result of a nested run */
    uint64_t copyMask;      /* CopyArgs: how the arguments of the next call go in */
    VmHandlerRec* dead;     /* handlers whose catch block was left by another exception (the runtime may still read them until its throwValue returns) */
    jmp_buf* exitJump;      /* the runtime's exitNow (FIRE_VM): back to the runner of the script */
    int exitCode;
    /* the class descriptors and catch types this program registered */
    FireClassDesc* descs;
    int32_t** excTypes;
    VmClass** byId;
    uint32_t nids;
    uint32_t firstId;
} Vm;

#endif
