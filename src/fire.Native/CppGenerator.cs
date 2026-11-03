using System.Globalization;
using System.Text;
using fire.Package.Manager;
using fire.Ast;
using fire.Bytecode;
using fire.Runtime;
using fire.Standard;
using fire.Values;

namespace fire.Native
{
    /// <summary>The generated sources of a native program: the program in C, and its natives in C++ (null: none).</summary>
    public sealed record NativeSources(string Program, string? Natives);

    /// <summary>
    /// Translates a linked program (bytecode) into one C source file that includes <c>fire_rt.h</c> (and, when the program calls natives written in C++ - the
    /// natives of packages that are still C++ - a second file in C++ with them, see <see cref="NativesFileName"/>; the bridges of graphics and windows are C, part of the program).
    ///
    /// Every chunk (the top-level code and each function, method, constructor, field initializer and destructor) becomes a
    /// C function. The operand stack disappears: its depth is known at every instruction, so slot <c>k</c> of the stack is
    /// the C local <c>s{k}</c>, and the C compiler turns those into registers. Scopes disappear as far as they hold
    /// nothing but values: a variable that lives in a scope is a C local (<c>B{scope}_{slot}</c>, parameters
    /// <c>P{slot}</c>, globals <c>G{slot}</c>). Jumps become <c>goto</c>.
    ///
    /// Objects: a class is a class id plus a field layout (base fields first, like the VM), an object is a
    /// <c>fire::Obj</c> with its fields as <c>Value</c>s. A scope that can own objects or strings/arrays gets a local
    /// <c>OwnList</c>; leaving the scope - or <c>return</c> - destroys what is on it (SPEC 2.3), a returned object moves to
    /// the caller's innermost scope. Method calls are dispatched over the classes that exist in the program (it is a closed
    /// world); field access goes through small generated helpers.
    ///
    /// Strings, arrays and byte buffers are reference counted (see fire_rt.h): a variable, parameter, field or array
    /// element holds a count; a fresh value is registered on its creator's scope. To keep numeric code free of that, the
    /// generator infers for every variable and stack slot whether it can hold such a value ("may be a reference") and
    /// emits counting - and the string-capable <c>addR</c> instead of <c>add</c> - only where it can.
    ///
    /// What is translated is a growing subset of the ISA (see <see cref="NativeNotSupportedException"/> for the rest):
    /// the generator never produces code with different semantics, it refuses.
    /// </summary>
    public sealed partial class CppGenerator
    {
        private enum FuncKind { Main, Static, Method, Ctor, Init, Dtor, Lambda, FireBody }

        private sealed class Func
        {
            public required FunctionProto? Proto;
            public required Chunk Chunk;
            public required FuncKind Kind;
            public int Index;
            /// <summary>Modules (ROADMAP 9): the prefix of the names a library keeps to itself; the exported name of a member of a library's class (other modules call
            /// it by that name); a function of another module (only declared here, not generated).</summary>
            public string Prefix = "";
            public string? Export;
            public bool External;
            public string Name => Kind == FuncKind.Main ? "fire_main" : Export ?? $"{Prefix}f{Index}";
            /// <summary>The top level of the program or of a fire thread: its variables are global slots, it owns the global scope.</summary>
            public bool IsTop => Kind is FuncKind.Main or FuncKind.FireBody;
            /// <summary>Fire bodies: the number of global slots of the main program. They are shared (`G{n}`); the slots from there on belong to the thread (`T{n}`).</summary>
            public int GlobalSlotCount;
            /// <summary>Fire bodies: the thread-local variables (global slots of the body itself).</summary>
            public readonly SortedSet<int> ThreadVars = new();
            public bool HasSelf => Kind is FuncKind.Method or FuncKind.Ctor or FuncKind.Init or FuncKind.Dtor;
            public int ParamCount => Kind == FuncKind.FireBody ? 0 : Proto?.ParamCount ?? 0;
            /// <summary>Fire bodies: how many values the thread starts with (the `taking` copies and the `with` actor; they are the global slots behind the shared ones).</summary>
            public int ThreadArgs => Kind == FuncKind.FireBody ? Proto!.ParamCount - GlobalSlotCount : 0;
            /// <summary>Lambdas: the number of values captured when the lambda was created; they are variables behind the parameters.</summary>
            public int CaptureCount;
            /// <summary>Parameters plus captures: the variables of the function scope that exist on entry.</summary>
            public int ParamLike => ParamCount + CaptureCount;
            /// <summary>Scopes of this function that can own objects or reference values (they get an OwnList).</summary>
            public readonly HashSet<int> NeedsList = new();
            /// <summary>The HoistValue instructions seen in pass 1: the scope they hoist into and the scopes they hoist from - the target needs a list
            /// only when one of the sources has one for something else (see HoistValue).</summary>
            public readonly List<(int Target, int[] Sources)> Hoists = new();
            /// <summary>A call passes the result of another call on (`f(g())`): the function has the argument list `AL` (see finishArgs in fire_rt.h).</summary>
            public bool NeedsArgList;
            /// <summary>A generic method: it takes the type arguments of its call (TakeCallTypeArgs) into `ctargs`.</summary>
            public bool HasCallTArgs;
            /// <summary>A copy of a generic method for constant type arguments (the hybrid of generics): their SetTypeArgs text, and the parameters
            /// (slot, kind) that are an int/float/bool type parameter - the call site makes sure they hold that kind without a unit.</summary>
            public string? SpecTArgs;
            public List<(int Slot, byte Kind, int Unit)>? SpecParams;
            /// <summary>A copy for the type arguments of a class (`Box&lt;int&gt;`): that class and its type arguments - a field declared with one of
            /// its type parameters holds a number of that kind and unit (its setter converts every value into it) or undefined.</summary>
            public string? SpecClassOwner;
            public string[]? SpecClassArgs;
            public string Code = "";
            // Per generation of the chunk (see GenerateChunk): the catch regions, the finally bookkeeping and the decoded code.
            public Dictionary<int, Region> Regions = new();
            public readonly HashSet<int> FinReturn = new();                      // finally blocks that a `return` can run through
            public readonly Dictionary<int, SortedSet<int>> FinJumps = new();    // finally block -> the jump targets that `break`/`continue` leave through it
            public List<Instr> Decoded = new();
            /// <summary>Parameters checked by GuardLocal at the end of the prologue: slot -> address of the check. Before it a parameter holds what the caller
            /// passed (its loads are untyped, its stores do not count); from there on the declared kind or undefined.</summary>
            public Dictionary<int, int> GuardAddr = new();
            public Dictionary<int, int> IndexOf = new();
            public Flow?[] Entry = Array.Empty<Flow?>();
            public Out Root = new();
            public int FinSignature => FinReturn.Count + FinJumps.Values.Sum(v => v.Count);
            public string Signature => SignatureOf(Name, "");
            /// <summary>The C signature with the function's name `name` and `suffix` after the names of its parameters (a function with a frame copies them in).</summary>
            public string SignatureOf(string name, string suffix) => SignatureOf(name, _ => suffix);
            /// <summary>The C signature with `suffixOf(parameter)` after the name of each parameter.</summary>
            public string SignatureOf(string name, System.Func<string, string> suffixOf)
            {
                if (Kind == FuncKind.Main) return $"static void {(name == Name ? "fire_main" : name)}(void)";
                if (Kind == FuncKind.FireBody) return $"static void {name}(const Value* args)";   // (`args` is read at the start only: never part of a frame)
                var ps = ParamNames.Select(n => $"Value {n}{suffixOf(n)}").ToList();
                return $"{(HasCallTArgs ? "FIRE_MAYBE_UNUSED " : "")}{(Export != null && name == Name ? "" : "static ")}Value {name}(" + (ps.Count == 0 ? "void" : string.Join(", ", ps)) + ")";
            }
            /// <summary>The names of the parameters (without the thread body's `args`).</summary>
            public IEnumerable<string> ParamNames => (HasSelf ? new[] { "self" } : Kind == FuncKind.Lambda ? new[] { "lam" } : Array.Empty<string>()).Concat(Enumerable.Range(0, ParamCount).Select(i => $"P{i}"));
            /// <summary>Lambdas are called through a pointer with a uniform signature.</summary>
            public string ThunkSignature => $"static Value {Name}_t(Value lam, const Value* a)";
        }

        private sealed class ClassInfo
        {
            public required RuntimeClass Rc;
            public required int Id;
            /// <summary>A library module (ROADMAP 9): the C expression of the id (its module's base plus its place), null: <see cref="Id"/>.</summary>
            public string? IdText;
            public IReadOnlyList<string> Fields => Rc.FlattenedFieldNames;
        }

        private readonly LinkedProgram _program;
        private readonly IReadOnlyList<string> _natives;
        private readonly TargetProfile _target;
        private readonly List<Unit> _units = new() { Unit.Unitless };
        /// <summary>The program calls the methods of `string` (`"5mm".ToFloat()` needs the table of the units a text may name, g_textUnits).</summary>
        private bool _usesStringCall;
        /// <summary>The units with conversion functions: the hidden class -> its index in the tables of the runtime (g_unitTo/g_unitFrom) and the functions.</summary>
        private readonly Dictionary<string, (int Index, Func To, Func From)> _unitConverters = new();
        private readonly List<string> _strings = new();
        private readonly Dictionary<string, int> _stringIndex = new();
        private readonly HashSet<int> _globals = new();
        private readonly Dictionary<FunctionProto, Func> _funcByProto = new(ReferenceEqualityComparer.Instance);
        private readonly List<Func> _funcs = new();
        /// <summary>The specialised copies of generic methods, by method and type arguments (see Specialize).</summary>
        private readonly Dictionary<(FunctionProto, string Key), Func> _specFuncs = new();
        private readonly Dictionary<string, ClassInfo> _classes = new();
        private readonly List<ClassInfo> _classList = new();
        private readonly SortedSet<(string Name, int Argc)> _dispatchers = new();
        /// <summary>Calls `x.Name(...)` on a value of an interface type whose classes all have a public method for it: each gets a dispatcher of its own
        /// over those classes only (InterfaceDispatcher) - no access check, no other class of the program that has a method of that name.</summary>
        private readonly SortedSet<(string Interface, string Name, int Argc)> _interfaceDispatchers = new();
        /// <summary>Operators (method names `operator+`, ...) that the program overloads and that an operation of it uses: each gets a wrapper `ov_...` (SPEC 5.11).</summary>
        private readonly SortedSet<string> _operators = new();
        /// <summary>Class and interface names tested with `is of`: each gets a function `isof_...`.</summary>
        private readonly SortedSet<string> _isOfTypes = new();
        /// <summary>Type arguments (DeclaredType texts: `int`, `Dog`, `in:cm`) by id: each is a TArg `ta_N` with its test `tah_N`.</summary>
        private readonly Dictionary<string, int> _targIds = new();
        /// <summary>Constant lists of type arguments (`new Box&lt;int&gt;()`), by their SetTypeArgs text: each is an array `tl_N`.</summary>
        private readonly Dictionary<string, int> _targListIds = new();
        /// <summary>The functions `targOf_C_k` (the type argument of the k-th type parameter of class C for an object, through its base classes).</summary>
        private readonly SortedSet<(string Cls, int Index)> _targOfs = new();
        /// <summary>The type arguments of the next NewObject (a SetTypeArgs right before it): a C++ expression of TArgs.</summary>
        private string? _pendingNewTArgs;
        /// <summary>The type arguments the next lambda takes along (SetTypeArgs with `forCall` 2).</summary>
        private string? _pendingLambdaTArgs;
        /// <summary>The member chains of selector lambdas (`c => c.radius`), each a static array.</summary>
        private readonly Dictionary<string[], string> _selectorPaths = new(new SelectorPathComparer());
        private sealed class SelectorPathComparer : IEqualityComparer<string[]>
        {
            public bool Equals(string[]? a, string[]? b) => a != null && b != null && a.SequenceEqual(b);
            public int GetHashCode(string[] p) => string.Join("\u0001", p).GetHashCode();
        }

        /// <summary>What the operator wrapper does when the left operand is not an object with that overload: the built-in operation.</summary>
        private static readonly Dictionary<string, string> OperatorFallback = new()
        {
            ["operator+"] = "addR(a, b, list)", ["operator-"] = "sub(a, b)", ["operator*"] = "mul(a, b)", ["operator/"] = "divide(a, b)", ["operator%"] = "modulo(a, b)",
            ["operator^"] = "power(a, b)", ["operator&"] = "bitAnd(a, b)", ["operator|"] = "bitOr(a, b)", ["operator#"] = "bitXor(a, b)", ["operator<<"] = "shl(a, b)", ["operator>>"] = "shr(a, b)",
            ["operator=="] = "Bool(opEq(a, b))", ["operator!="] = "Bool(!opEq(a, b))", ["operator<"] = "Bool(lt(a, b))", ["operator<="] = "Bool(le(a, b))",
            ["operator>"] = "Bool(gt(a, b))", ["operator>="] = "Bool(ge(a, b))",
        };

        // (OperatorWrapper: CppGenerator.ModuleDispatch.cs - the name carries the module's prefix)

        /// <summary>Does the program overload this operator in some class? Then the operation goes through the wrapper (and the dispatcher of the method).</summary>
        private bool UseOperator(string method)
        {
            // only a class that the program uses (is generated) can be the operand; a class of a library that nothing creates does not count (registering one repeats the generation)
            // (a library module: a class of the program may overload it - the program's wrapper decides)
            if (!LibraryRole && !_classList.Any(c => c.Rc.FindMethodWithAccess(method, 1).Proto != null)) return false;
            _operators.Add(method);
            _dispatchers.Add((method, 1));
            return true;
        }
        private readonly SortedSet<string> _fieldNames = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Class, string Field), int> _staticFields = new();

        // Which variables can hold a string/array/buffer ("may be a reference"). Only ever grows; generation is repeated
        // until nothing changes (a store in one function can change what a load in another one has to do).
        private readonly Dictionary<string, bool> _varRef = new();
        private int _version;   // bumped whenever something that earlier generated code depended on changes
        /// <summary>The program throws or catches exceptions: calls are followed by a check of the unwinding flag, and the exception hooks exist.</summary>
        private bool _usesExceptions;
        /// <summary>The program uses fire threads (`fire`, `leave`, `terminate`, actors, ...): FIRE_THREADS, safe points that look at signals.</summary>
        private bool _usesThreads;
        /// <summary>The runtime has to read the `ticks` of a TimeSpan (the time functions, the waiting functions of the devices, `#timeout`): `timeObjTicks` is generated.</summary>
        private bool _needsTimeObj;
        private bool _usesGraphics;
        private bool _usesWindows;
        /// <summary>Which SDL the window is built with when the target does not say (see <see cref="GenerateSources"/>).</summary>
        private Func<int>? _chooseSdl;
        /// <summary>How much is left out (see <see cref="EffectiveStripping"/>).</summary>
        private StrippingLevel _strip;
        private int? _sdlChosen;
        /// <summary>A `Takes` mode is used: `Takes.Children` needs the enumerator of the IEnumerable classes (fire_enumerateItems).</summary>
        private bool _usesTakeEnumerate;

        /// <summary>The natives of `#import "graphics"` (`__GRPH` + name; `Fb` framebuffer, `Rnd` renderer, `Bsh` brush, `Pen` pen, `Slc` slicer, `Fnt` font): arguments and whether the result is a string/buffer/array.</summary>
        private static readonly Dictionary<string, (int Argc, bool Reference)> GraphicsBridgeNatives = new()
        {
            ["FbCreate"] = (3, false), ["FbDestroy"] = (1, false), ["FbWidth"] = (1, false), ["FbHeight"] = (1, false), ["FbReadByte"] = (2, false), ["FbWriteByte"] = (3, false),
            ["FbMode"] = (1, false), ["FbByteCount"] = (1, false), ["FbReadBytes"] = (1, true), ["FbWriteBytes"] = (2, false), ["FbGetPaletteColor"] = (2, false), ["FbSetPaletteColor"] = (3, false),
            ["FbReadPalette"] = (2, true), ["FbWritePalette"] = (2, false), ["FbLoadImage"] = (2, false), ["FbLoadFile"] = (2, false), ["FbFromPixels"] = (5, false), ["FbLastError"] = (0, true), ["FbToPng"] = (1, true),
            ["FbResize"] = (3, false), ["FbGetTransparentIndex"] = (1, false), ["FbSetTransparentIndex"] = (2, false), ["FbToMask"] = (4, false),
            ["RndCreate"] = (1, false), ["RndDestroy"] = (1, false), ["RndPrint"] = (2, false), ["RndLocate"] = (3, false), ["RndClear"] = (1, false),
            ["RndClearTo"] = (2, false), ["RndSetColor"] = (3, false), ["RndSetPixel"] = (4, false), ["RndGetPixel"] = (3, false), ["RndGetPixelIndex"] = (3, false),
            ["RndCellWidth"] = (1, false), ["RndCellHeight"] = (1, false), ["RndGetAlphaBlending"] = (1, false), ["RndSetAlphaBlending"] = (2, false),
            ["RndDrawText"] = (6, false), ["RndFillRect"] = (6, false), ["RndFill"] = (2, false), ["RndFillCircle"] = (5, false), ["RndFillEllipse"] = (6, false),
            ["RndFillTriangle"] = (8, false), ["RndFillPolygon"] = (3, false), ["RndFloodFill"] = (4, false), ["RndFloodFillBorder"] = (5, false),
            ["RndDrawPoint"] = (4, false), ["RndDrawLine"] = (6, false), ["RndDrawPath"] = (4, false), ["RndDrawRect"] = (6, false), ["RndDrawCircle"] = (5, false),
            ["RndDrawEllipse"] = (6, false), ["RndDrawTriangle"] = (8, false), ["RndDrawPolygon"] = (4, false), ["RndBlit"] = (12, false), ["RndSetClip"] = (5, false), ["RndResetClip"] = (1, false), ["RndDrawTextFont"] = (8, false), ["BshCreateSolid"] = (1, false),
            ["BshDestroy"] = (1, false), ["BshGetColor"] = (1, false), ["BshSetColor"] = (2, false), ["PenCreate"] = (3, false), ["PenDestroy"] = (1, false),
            ["PenGetColor"] = (1, false), ["PenSetColor"] = (2, false), ["PenGetWidth"] = (1, false), ["PenSetWidth"] = (2, false), ["PenGetShape"] = (1, false),
            ["PenSetShape"] = (2, false),
            ["SlcSlice"] = (7, true),
            ["FntLoad"] = (1, false), ["FntAdd"] = (2, false), ["FntOpen"] = (1, false), ["FntDestroy"] = (1, false), ["FntName"] = (1, true), ["FntIsBitmap"] = (1, false),
            ["FntAscent"] = (2, false), ["FntHeight"] = (2, false), ["FntMeasure"] = (3, false),
        };
        /// <summary>The SDL a window is built with, or null for fire's own display (the default): the target's `sdl` (2 or 3; 0: the build probes the toolchain, SDL3 when it
        /// has it), else the environment variable FIRE_SDL (2, 3, or "auto" like 0).</summary>
        private int? SdlOfTarget()
        {
            if (_target.Native.Sdl is int fixedSdl and (2 or 3)) return fixedSdl;
            string? env = _target.Native.Sdl == 0 ? "auto" : Environment.GetEnvironmentVariable("FIRE_SDL");
            if (env == "2") return 2;
            if (env == "3") return 3;
            if (env == "auto") return _sdlChosen ??= _chooseSdl?.Invoke() ?? 2;
            return null;
        }

        /// <summary>What fire's own display links: Linux loads its window systems at run time (dl, pthread for the clipboard's thread); Windows links what every Windows has;
        /// macOS its frameworks (`framework:` - the build makes `-framework` of it).</summary>
        private IEnumerable<string> OwnDisplayLibraries()
        {
            if (_target.Native.Platform == "windows") return new[] { "user32", "gdi32" };   // (imm32, dwmapi, ole32 are loaded at run time)
            if (_target.Name == "macos") return new[] { "framework:Cocoa", "framework:QuartzCore", "framework:GameController" };
            return new[] { "dl", "pthread" };
        }

        /// <summary>The natives of `#import "windows"` (`__GRPHWin` + name): arguments and whether the result is an array (it belongs to the innermost scope).</summary>
        private static readonly Dictionary<string, (int Argc, bool Reference)> WindowsBridgeNatives = new()
        {
            ["Create"] = (2, false), ["Destroy"] = (1, false), ["Tick"] = (1, false), ["EnableEvents"] = (1, false), ["NextEvent"] = (1, true), ["RegisterEvent"] = (3, false),
            ["SetVSync"] = (2, false), ["GetVSync"] = (1, false), ["SetAutoResize"] = (2, false), ["GetAutoResize"] = (1, false), ["SetTouchMouse"] = (2, false), ["GetTouchMouse"] = (1, false),
            ["Pump"] = (1, false), ["Present"] = (1, false), ["MgrCreate"] = (0, false), ["MgrAttach"] = (2, false), ["MgrDetach"] = (2, false), ["MgrStart"] = (2, false), ["MgrWait"] = (2, false),
            ["SetBlocked"] = (2, false), ["MgrServed"] = (1, false), ["SetAutoClose"] = (2, false), ["GetAutoClose"] = (1, false), ["SetSize"] = (3, false), ["SystemTheme"] = (0, true),
            ["SetCursor"] = (2, false), ["ClipboardSetText"] = (1, false), ["ClipboardGetText"] = (0, true), ["ClipboardHasText"] = (0, false),
            ["ClipboardGetImage"] = (0, true), ["ClipboardSetImage"] = (1, false), ["ClipboardHasImage"] = (0, false),
            ["ClipboardGetFiles"] = (0, true), ["ClipboardSetFiles"] = (1, false), ["ClipboardHasFiles"] = (0, false), ["StartDrag"] = (3, false),
            ["CanStartDrag"] = (1, false), ["SetTextInputRect"] = (5, false), ["SetBackdrop"] = (2, false), ["GetBackdrop"] = (1, false), ["GetScale"] = (1, false),
        };

        private static readonly HashSet<string> GraphicsNeedsList = new() { "FbReadBytes", "FbReadPalette", "FbLastError", "SlcSlice", "FntName", "FbToPng" };

        /// <summary>The program declares actors (classes with IsActor): calls of their methods are messages.</summary>
        private bool _usesActors;
        private Func _cur = null!;   // the function that is being generated
        private Func? _main;   // the top level (not in _funcs)
        /// <summary>The catch types (`catch (T e)`) by id; id 0 is `Exception` (FIRE_EXC_ANY: catches every exception).</summary>
        private readonly List<string> _excTypes = new() { "Exception" };
        /// <summary>The program uses TakeUpwards: every scope then knows its parent scope (and has an owner list).</summary>
        private bool _usesTakeUpwards;
        /// <summary>The program takes the address of an array or buffer element (`ref a[i]`): the handle table keeps the headers, so that `p + n` can be checked against the bounds.</summary>
        private bool _usesElementPointers;
        /// <summary>The program has `try x.Take...(...)`: every function counts the call depth (`CallDepth`), so that the callee knows which argument list is its own.</summary>
        private bool _usesTryDepth;
        /// <summary>What runs before the program (fire_init): the tables of the runtime that C++ set with static initializers.</summary>
        private readonly StringBuilder _initCode = new();
        /// <summary>The enums of the program in the order of their numbers (1-based in the generated table, `Value.reserved` of a value of the enum).</summary>
        private readonly List<EnumType> _enumList = new();

        /// <summary>The number of an enum in the generated table (1-based).</summary>
        private int EnumIndex(EnumType type)
        {
            int at = _enumList.IndexOf(type);
            if (at < 0) { _enumList.Add(type); at = _enumList.Count - 1; }
            return at + 1;
        }

        /// <summary>The number of the enum a type name means (0: no enum of the program).</summary>
        private int EnumIndexNamed(string typeName)
        {
            for (int i = 0; i < _enumList.Count; i++) if (_enumList[i].Matches(typeName)) return i + 1;
            return 0;
        }
        /// <summary>The factors between two units the program converts between at fixed places: one cache each (`kUF_from_to`, 0 until it is looked up).</summary>
        private readonly SortedSet<(int, int)> _unitFactorCaches = new();
        /// <summary>The functions that `catch threads`/`catch terminate` register (name -> body): the runtime calls them with the value.</summary>
        private readonly SortedDictionary<string, string> _catchCallbacks = new(StringComparer.Ordinal);
        /// <summary>The natives in C++ of the program (null: it has none).</summary>
        private string? _nativesCode;
        /// <summary>The name of the file with the natives in C++, next to the program.</summary>
        public const string NativesFileName = "fire_natives.cpp";
        /// <summary>Something needs the list of the global scope from other functions (`g_globalOwn`): exceptions, TakeGlobal, TakeUpwards.</summary>
        private bool _usesGlobalOwn;
        /// <summary>Methods (name, number of arguments) that a call site passes an address to (`ref` parameters): their dispatcher hands the value to the implementations without `ref`.</summary>
        private readonly HashSet<(string Name, int Argc)> _refCallSites = new();
        /// <summary>Lambdas with default parameter values: each gets a function `fN_d(lam, index)` that evaluates the default of a parameter.</summary>
        private readonly HashSet<Func> _lambdaDefaults = new();   // catch types, as numbered in the HandlerInfo tables

        private CppGenerator(LinkedProgram program, TargetProfile target)
        {
            _program = program;
            _target = target;
            _natives = program.NativeNames
                ?? throw new NativeNotSupportedException("the program does not carry its native function names (link it with Linker.CompileAndLink)");
        }

        /// <summary>Generates the C++ source of the program for <paramref name="target"/> (default: the machine this runs on).
        /// Link the program with the same target (<c>Linker.CompileAndLink(..., target:)</c>) so that its libraries are checked.</summary>
        public static string Generate(LinkedProgram program, TargetProfile? target = null, StrippingLevel? stripping = null) => GenerateSources(program, target, stripping: stripping).Program;

        /// <summary>A debug build keeps everything (0: the debugger sees every class and member, docs/ROADMAP.md section 8); otherwise what is asked for, by default
        /// level 3 (only what is reached).</summary>
        public static StrippingLevel EffectiveStripping(LinkedProgram program, StrippingLevel? stripping) =>
            program.ExecutionMode == VmExecutionMode.Debug ? StrippingLevel.None : stripping ?? StrippingLevel.All;

        /// <summary>The program as C (<see cref="NativeSources.Program"/>) and its natives in C++ (<see cref="NativeSources.Natives"/>, null when it has none; written as
        /// <see cref="NativesFileName"/> next to the program, compiled with a C++ compiler and linked with it).</summary>
        /// <param name="chooseSdl">Asked once when the program shows a window on a desktop and the target does not name the SDL (<see cref="NativeTarget.Sdl"/>): 2 or 3
        /// (the build probes the toolchain); null: SDL2.</param>
        /// <param name="stripping">How much is left out (<see cref="StrippingLevel"/>); null: level 3, what the program does not reach - a debug build (`#debug`, `-m DEBUG`)
        /// keeps everything.</param>
        public static NativeSources GenerateSources(LinkedProgram program, TargetProfile? target = null, Func<int>? chooseSdl = null, StrippingLevel? stripping = null)
        {
            if (ModulesFromEnvironment && program.SourceModules != null)
            {
                // (tests, ROADMAP 9 phase A: the same program from modules; a program that cannot be split - it declares a class a library declares too - is built whole)
                try { return GenerateModular(program, target, chooseSdl, stripping); }
                catch (NativeNotSupportedException ex) when (ex.Message.StartsWith("module '", StringComparison.Ordinal))
                {
                    if (Environment.GetEnvironmentVariable("FIRE_MODULES_LOG") is { Length: > 0 } logFile)
                        lock (typeof(CppGenerator)) File.AppendAllText(logFile, ex.Message + Environment.NewLine);
                }
            }
            var generator = new CppGenerator(program, target ?? TargetProfile.Host) { _chooseSdl = chooseSdl, _strip = EffectiveStripping(program, stripping) };
            string c = generator.Run();
            return new NativeSources(c, generator._nativesCode);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Registries
        // -------------------------------------------------------------------------------------------------------------
        // `Class.Method` to a function (for error messages), otherwise "a lambda or a function of the script"
        private string Describe(FunctionProto proto)
        {
            foreach (var rc in _program.Program.Classes.Values)
                foreach (var m in rc.Methods)
                    if (m.Value.Contains(proto)) return $"'{rc.Name}.{m.Key}'";
            return "a lambda or a function of the script";
        }

        /// <summary>The hybrid of generics: a call of a generic method whose type arguments are constant (`Max(3, 4)`: `int`) goes to a copy of it in
        /// which the parameters of an int/float/bool type parameter are numbers of that kind without a unit - typed operations instead of generic
        /// ones. The call site tests the arguments (`Condition`, null when their types already say so) and calls the generic method otherwise.</summary>
        private (Func Target, string? Condition) Specialize(Func f, Instr ins, Func generic, int firstArg, int argc, System.Func<int, string> slot, System.Func<int, int> slotType)
        {
            if (generic.External || generic.Proto is not { } proto || CallTArgsText(f, ins) is not { } text || text.Contains('$')) return (generic, null);
            var specParams = SpecParamsFor(proto, "", text.Split('|'));
            if (specParams == null || specParams.Any(p => p.Slot >= argc)) return (generic, null);   // (a default value: its kind is not known here)
            var spec = SpecFunc(generic, "m:" + text, text, specParams);
            var conditions = new List<string>();
            foreach (var (param, kind, unit) in specParams)
                if (slotType(firstArg + param) != TypeOf(kind, unit)) conditions.Add(KindTest(slot(firstArg + param), kind, unit));
            return (spec, conditions.Count == 0 ? null : string.Join(" && ", conditions));
        }

        /// <summary>The parameters of a generic method that a copy for these type arguments types: those declared with a type parameter of the method
        /// (`owner` "") or of class `owner` whose type argument is int or float in a known unit (`int`: none, `int : cm`), or bool; null when there is none.</summary>
        private List<(int Slot, byte Kind, int Unit)>? SpecParamsFor(FunctionProto proto, string owner, string[] parts)
        {
            var specParams = new List<(int Slot, byte Kind, int Unit)>();
            foreach (var c in OpInfo.Decode(proto.Chunk.Code))
            {
                if (c.Op != OpCode.GuardLocalType) continue;
                var spec = DeclaredType.Parse(proto.Chunk.Constants[c.A[1]].AsString());
                if (spec.TypeParamIndex < 0 || spec.TypeParamClass != owner || spec.TypeParamIndex >= parts.Length) continue;
                if (SpecKind(parts[spec.TypeParamIndex]) is { } typed) specParams.Add((c.A[0], typed.Kind, typed.Unit));
            }
            return specParams.Count == 0 ? null : specParams;
        }

        /// <summary>The copy of a generic method for one set of type arguments (`key`: "m:" + those of the method, "c:Class:" + those of a class).</summary>
        private Func SpecFunc(Func generic, string key, string? methodTArgs, List<(int Slot, byte Kind, int Unit)> specParams)
        {
            if (_specFuncs.TryGetValue((generic.Proto!, key), out var spec)) return spec;
            spec = new Func { Proto = generic.Proto, Chunk = generic.Chunk, Kind = generic.Kind, Index = _funcs.Count, SpecTArgs = methodTArgs, SpecParams = specParams, Prefix = Px };
            _funcs.Add(spec);
            _specFuncs[(generic.Proto!, key)] = spec;
            _version++;
            return spec;
        }

        /// <summary>A field of `this` read in a copy for the type arguments of a class (`this.value` of a `Box&lt;int&gt;`): a field declared with a type
        /// parameter whose type argument is a number in a known unit holds that or undefined; null otherwise.</summary>
        private int? SpecFieldType(Func f, Instr ins, string field)
        {
            if (f.SpecClassOwner is not { } owner || f.SpecClassArgs is not { } args || f.Chunk.OwnerClass is not { } rc) return null;
            int i = f.IndexOf[ins.Addr] - 1;
            if (i < 0 || f.Decoded[i].Op != OpCode.LoadThis) return null;   // (the object of the method itself, not another one of the class)
            if (rc.FindFieldRequiredType(field) is not { } required || DeclaredType.Parse(required) is not { TypeParamIndex: >= 0 } spec
                || spec.TypeParamClass != owner || spec.TypeParamIndex >= args.Length || SpecKind(args[spec.TypeParamIndex]) is not { } typed) return null;
            return TypeOf(typed.Kind, typed.Unit) | NullableBit;
        }

        /// <summary>The kind and unit a value of a type argument certainly has (`int`, `int:cm`, `float`, `bool`); null when its unit is open (`int:any`) or it is no number.</summary>
        private (byte Kind, int Unit)? SpecKind(string typeArg)
        {
            if (typeArg == "bool") return (KindBool, 0);
            if (DeclaredType.NumberDemand(typeArg) is not { } number) return null;
            string demand = number.Demand.TrimStart('#');
            if (demand == "any" || demand.StartsWith("any ", StringComparison.Ordinal)) return null;
            return (number.Kind == "int" ? KindInt : KindFloat, UnitId(DemandUnit(demand)));
        }

        /// <summary>A value that is a number of this kind in this unit (or a bool).</summary>
        private string KindTest(string value, byte kind, int unit) =>
            kind == KindBool ? $"{value}.kind == K_Bool" : $"{value}.kind == {(kind == KindInt ? "K_Int" : "K_Float")} && {value}.unit == {UnitC(unit)}";

        /// <summary>The type arguments the program uses with constants: of calls of generic methods (by method name and number of arguments) and of
        /// classes (`new Box&lt;int&gt;()`, `class IntBox : Box&lt;int&gt;`; given on to the base classes) - the copies of generic methods are made for them.</summary>
        private readonly Dictionary<(string Method, int Argc), SortedSet<string>> _callTArgTexts = new();
        private readonly Dictionary<string, SortedSet<string>> _classTArgTexts = new();

        private void AddTArgText<TKey>(Dictionary<TKey, SortedSet<string>> into, TKey key, string text) where TKey : notnull
        {
            if (text.Contains('$') || !text.Split('|').Any(t => SpecKind(t) != null)) return;
            if (!into.TryGetValue(key, out var set)) into[key] = set = new SortedSet<string>(StringComparer.Ordinal);
            if (set.Add(text)) _version++;
        }

        /// <summary>The constant type arguments of a class instantiation, and those it gives its base classes.</summary>
        private void AddClassTArgs(string className, string text)
        {
            AddTArgText(_classTArgTexts, className, text);
            if (!_program.Program.Classes.TryGetValue(className, out var rc) || rc.Base == null || rc.BaseTypeArgs is not { } given) return;
            var args = text.Split('|');
            AddClassTArgs(rc.Base.Name, string.Join("|", given.Select(t => !t.StartsWith('$') ? t : int.Parse(t.AsSpan(1), CultureInfo.InvariantCulture) is var j && j < args.Length ? args[j] : "")));
        }

        /// <summary>Collects the constant type arguments of the calls and `new`s of a chunk (and the base type arguments of the classes).</summary>
        private void CollectTArgTexts(Func f)
        {
            var code = f.Decoded!;
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].Op != OpCode.SetTypeArgs) continue;
                string text = f.Chunk.Constants[code[i].A[0]].AsString();
                int n = i + 1;
                if (n < code.Count && code[n].Op == OpCode.CopyArgs) n++;
                if (n >= code.Count) continue;
                var next = code[n];
                string Const(int k) => f.Chunk.Constants[next.A[k]].AsString();
                if (code[i].A[1] == 0)
                {
                    if (next.Op is OpCode.NewObject or OpCode.NewObjectOwned) AddClassTArgs(Const(0), text);
                }
                else if (next.Op == OpCode.CallMethod) AddTArgText(_callTArgTexts, (Const(0), next.A[1]), text);
                else if (next.Op is OpCode.CallStaticMethod or OpCode.CallBaseMethod) AddTArgText(_callTArgTexts, (Const(1), next.A[2]), text);
            }
            foreach (var ci in _classList)
                if (ci.Rc.BaseTypeArgs is { } given && ci.Rc.Base != null && !given.Any(t => t.StartsWith('$'))) AddClassTArgs(ci.Rc.Base.Name, string.Join("|", given));
        }

        /// <summary>The method name and number of parameters of a method of a class (null for anything else).</summary>
        private (string Name, int Argc)? MethodKey(FunctionProto proto)
        {
            foreach (var rc in _program.Program.Classes.Values)
                foreach (var m in rc.Methods)
                    if (m.Value.Contains(proto)) return (m.Key, proto.ParamCount);
            return null;
        }

        /// <summary>The trampoline of a generic method: a call whose type arguments and arguments fit one of its copies (Specialize, SpecFunc) goes there -
        /// also when it comes through the dispatcher, a lambda or the generic code of another method.</summary>
        private string Trampoline(Func f)
        {
            if (f.SpecParams != null || f.Proto is not { } proto || f.Kind is not (FuncKind.Method or FuncKind.Static)) return "";
            var lines = new StringBuilder();
            string args = string.Join(", ", (f.HasSelf ? new[] { "self" } : Array.Empty<string>()).Concat(Enumerable.Range(0, f.ParamCount).Select(i => $"P{i}")));
            string ParamTests(List<(int Slot, byte Kind, int Unit)> ps) => string.Concat(ps.Select(p => " && " + KindTest($"P{p.Slot}", p.Kind, p.Unit)));
            // the type arguments of the call of a generic method
            if (f.HasCallTArgs && MethodKey(proto) is { } key && _callTArgTexts.TryGetValue(key, out var texts))
                foreach (var text in texts)
                {
                    var parts = text.Split('|');
                    if (SpecParamsFor(proto, "", parts) is not { } ps) continue;
                    var spec = SpecFunc(f, "m:" + text, text, ps);
                    string targs = string.Concat(parts.Select((t, i) => t.Length == 0 ? "" : $" && ((TArgs)g_callTArgs)[{i}] == {TArgRef(t)}"));
                    lines.AppendLine($"    if (g_callTArgs{targs}{ParamTests(ps)}) return {spec.Name}{(Wrapped ? "_b" : "")}({args});");
                }
            // the type arguments of the object (a method of a generic class, or of a class derived from one)
            if (f.HasSelf)
            {
                var owners = OpInfo.Decode(proto.Chunk.Code).Where(c => c.Op == OpCode.GuardLocalType)
                    .Select(c => DeclaredType.Parse(proto.Chunk.Constants[c.A[1]].AsString()).TypeParamClass).Where(o => !string.IsNullOrEmpty(o)).Distinct();
                foreach (var owner in owners)
                {
                    if (!_classTArgTexts.TryGetValue(owner!, out var classTexts)) continue;
                    foreach (var text in classTexts)
                    {
                        var parts = text.Split('|');
                        if (SpecParamsFor(proto, owner!, parts) is not { } ps) continue;
                        var spec = SpecFunc(f, $"c:{owner}:{text}", null, ps);
                        string targs = string.Concat(parts.Select((t, k) => SpecKind(t) != null ? $" && {TArgOfExpr(owner!, k, "objAt(self)")} == {TArgRef(t)}" : ""));
                        spec.SpecClassOwner = owner;
                        spec.SpecClassArgs = parts;
                        lines.AppendLine($"    if (self.kind == K_Class{targs}{ParamTests(ps)}) return {spec.Name}{(Wrapped ? "_b" : "")}({args});");
                    }
                }
            }
            return lines.ToString();
        }

        /// <summary>The text of the SetTypeArgs (for a call) right before a call instruction (a CopyArgs may stand between them), or null.</summary>
        private static string? CallTArgsText(Func f, Instr ins)
        {
            int i = f.IndexOf[ins.Addr] - 1;
            if (i >= 0 && f.Decoded[i].Op == OpCode.CopyArgs) i--;
            return i >= 0 && f.Decoded[i] is { Op: OpCode.SetTypeArgs } set && set.A[1] != 0 ? f.Chunk.Constants[set.A[0]].AsString() : null;
        }

        private Func GetFunc(FunctionProto proto, FuncKind kind, int captureCount = 0)
        {
            if (_funcByProto.TryGetValue(proto, out var existing))
            {
                if (existing.Kind != kind) throw new NativeNotSupportedException($"a function is used both as {existing.Kind} and as {kind}: {Describe(proto)}");
                if (existing.CaptureCount != captureCount) throw new NativeNotSupportedException("a lambda is created with different numbers of captures");
                return existing;
            }
            var func = new Func { Proto = proto, Chunk = proto.Chunk, Kind = kind, Index = _funcs.Count, CaptureCount = captureCount, Prefix = Px };
            if (Modular && ProtoKey(proto, out var module, out var key) && module != null)
            {
                // a member of a class of a library module: called by its exported name - generated by its module, declared everywhere else
                func.Export = ExportName(module, key);
                func.External = !ReferenceEquals(module, _self);
            }
            _funcs.Add(func);
            _funcByProto[proto] = func;
            _version++;
            return func;
        }

        private RuntimeClass FindClass(string name) =>
            _program.Program.Classes.TryGetValue(name, out var rc) ? rc : throw new NativeNotSupportedException($"unknown class '{name}'");

        private ClassInfo RegisterClass(string name)
        {
            if (LibraryRole && !_keeping && _self != null && _classList.Count >= _self.Classes.Count) _demandedClasses.Add(name);
            if (_classes.TryGetValue(name, out var info)) return info;
            var rc = FindClass(name);
            if (ForeignClass(name, rc) is { } foreign) return foreign;
            int id = _fixedIds == null ? _classList.Count : _fixedIds.TryGetValue(name, out int fixedId) ? fixedId : _nextClassId++;
            info = new ClassInfo { Rc = rc, Id = id, IdText = LibraryRole ? $"({ModuleBase(_self!)} + {id}u)" : null };
            _classes[name] = info;
            _classList.Add(info);
            _version++;
            return info;
        }

        private int StaticFieldIndex(string className, string field)
        {
            var owner = FindClass(className).FindStaticFieldOwner(field)
                ?? throw new NativeNotSupportedException($"static field '{className}.{field}' not found");
            var key = (owner.Name, field);
            if (!_staticFields.TryGetValue(key, out int index)) { index = _staticFields.Count; _staticFields[key] = index; _version++; }
            return index;
        }

        /// <summary>The C variable of a static field: `SF{n}`, or with modules (ROADMAP 9) a name every module knows - defined by the module of the class, declared by
        /// the others; it can hold anything (other modules write it).</summary>
        private string StaticFieldVar(string className, string field)
        {
            int index = StaticFieldIndex(className, field);
            if (!Modular) return $"SF{index}";
            var owner = FindClass(className).FindStaticFieldOwner(field)!;
            string name = $"SFx_{ModuleOf(owner)?.Id ?? "prog"}_{Mangle(owner.Name)}_{Mangle(field)}";
            if (!VarRef(name)) { MarkVarRef(name); JoinVarType(name, TypeUnknown); }
            return name;
        }

        private static string Mangle(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name)
                if (char.IsAsciiLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append("_x").Append(((int)c).ToString("X4"));
            return sb.ToString();
        }

        private bool VarRef(string key) => _varRef.TryGetValue(key, out bool r) && r;

        // Static types of values: what a stack slot or a variable certainly holds - an int, a float or a bool without a bit width, and the unit
        // of the number (its UnitId, 0: none), packed as `kind | unit << 8`; with NullableBit it may also be `undefined` (a parameter or field
        // declared int/float/bool, DeclaredKind); TypeUndefined is `undefined` only; TypeUnknown (0) is everything else.
        // KindObj: an object of a class or of a class derived from it (the class is an index into _objTypeNames, where a number has its unit)
        private const byte KindInt = 1, KindFloat = 2, KindBool = 3, KindObj = 4, KindUnknown = 0;
        // TypeNone: nothing yet - the result of a call of a function none of whose `return`s was seen yet (in the last round: one that never returns,
        // the code after the call does not run); it joins to the other side, and whatever is computed from it is TypeNone, too.
        private const int TypeUnknown = 0, NullableBit = 0x10, TypeUndefined = NullableBit, TypeNone = 0x20;
        private static int TypeOf(byte kind, int unit) => kind == KindUnknown ? TypeUnknown : kind | (kind == KindBool ? 0 : unit) << 8;
        /// <summary>The kind of a value that is certainly no `undefined` (KindUnknown for a nullable type).</summary>
        private static byte KindOfType(int type) => (type & (NullableBit | TypeNone)) != 0 ? KindUnknown : (byte)(type & 0x0F);
        /// <summary>The kind of a value of the type when it is not `undefined`.</summary>
        private static byte BaseKindOfType(int type) => (byte)(type & 0x0F);
        private static bool IsNullable(int type) => (type & NullableBit) != 0;
        private static int UnitOfType(int type) => type >> 8;
        /// <summary>A number, a bool, undefined or nothing yet: a value that belongs to no scope (no ownership bookkeeping).</summary>
        private static bool IsPlainType(int type) => type is TypeNone or TypeUndefined || BaseKindOfType(type) is KindInt or KindFloat or KindBool;

        private readonly List<string> _objTypeNames = new();
        private readonly Dictionary<string, int> _objTypeIndex = new();
        /// <summary>The type "an object of this class or of a class derived from it".</summary>
        private int ObjType(RuntimeClass rc) => ObjTypeNamed(rc.Name);
        private int ObjTypeNamed(string name)
        {
            if (!_objTypeIndex.TryGetValue(name, out int index)) { index = _objTypeNames.Count; _objTypeNames.Add(name); _objTypeIndex[name] = index; }
            return TypeOf(KindObj, index);
        }
        private RuntimeClass ObjClass(int type) => FindClass(_objTypeNames[UnitOfType(type)]);
        /// <summary>The interface of a type "an object of a class that implements this interface" (a parameter declared with it), null for any other type. Such a
        /// type is KindObj too, but no class: ObjClass is not asked for it.</summary>
        private string? InterfaceOfType(int type) =>
            BaseKindOfType(type) == KindObj && (type & TypeNone) == 0 && _objTypeNames[UnitOfType(type)] is var name && !_program.Program.Classes.ContainsKey(name) ? name : null;

        /// <summary>The type of a value that passed the check of a declaration with only this interface (the guard of a parameter): an object of a class that
        /// implements it, or undefined. Not for `IEnumerable` (arrays, buffers and strings pass it too) or a name no class implements.</summary>
        private int? InterfaceType(string spec) =>
            DeclaredType.Parse(spec) is { TypeParamIndex: < 0, SingleName: { } name } && !_program.Program.Classes.ContainsKey(name)
                && GenericClassNames.PlainName(name) != "IEnumerable" && _program.Program.Classes.Values.Any(rc => rc.Interfaces.Contains(name))
                ? ObjTypeNamed(name) | NullableBit : null;
        /// <summary>The classes of the program (that are created) an object of type `rc` can be: rc and the classes derived from it.</summary>
        private IEnumerable<ClassInfo> InstancesOf(RuntimeClass rc) => _classList.Where(c => ClassIs(c.Rc, rc.Name));

        /// <summary>The index of field `name` in every object an `rc` can be (the fields of a base class come first, so it is the same in derived
        /// classes); null when a property, an access check or the threads (sections) need the field helpers, or the index differs.</summary>
        private int? DirectFieldIndex(RuntimeClass rc, string name, Func? from = null)
        {
            if (_usesThreads || HasProperty(name)) return null;
            // a name that is private or protected somewhere: direct only when the code of `from` may use the field on every class the object can be
            // (the check is then known at compile time - e.g. `this.count` in a method of the class that declares it)
            bool restricted = RestrictedField(name);
            if (restricted && from == null) return null;
            int? index = null;
            foreach (var c in InstancesOf(rc))
            {
                if (!c.Rc.FieldIndex.TryGetValue(name, out int i) || index is { } known && known != i) return null;
                if (restricted && c.Rc.FindFieldAccess(name) is { } access && !AccessAllowed(from!.Chunk.OwnerClass, access.DeclaringClass, access.Access)) return null;
                index = i;
            }
            return index;
        }

        /// <summary>`obj.name = value` on an object of a known class: the checks of the field's declaration (unit, kind) and the store by index, like sf_
        /// (a destroyed object throws first); null when the field helpers are needed.</summary>
        private string? DirectSetField(RuntimeClass rc, string name, string obj, string value, int valueType, Func from, bool nullable = false)
        {
            if (DirectFieldIndex(rc, name, from) is not int index) return null;
            string? unit = rc.FindFieldRequiredUnit(name);
            byte kind = rc.FindFieldRequiredKind(name);
            string? requiredType = rc.FindFieldRequiredType(name);
            if (requiredType != null && FitsClassType(valueType, requiredType)) requiredType = null;
            // a value whose type already is what the declaration demands needs no check (a number of the declared kind in its unit, or a bool)
            if (kind != 0 && !IsNullable(valueType) && KindOfType(valueType) == KindOfDeclared(kind) && valueType != TypeNone)
            {
                int demanded = unit != null ? DemandedUnitId(unit) : DeclaredKind.IsUnitless(kind) ? 0 : -1;
                if (KindOfDeclared(kind) == KindBool || demanded >= 0 && UnitOfType(valueType) == demanded) { unit = null; kind = 0; }
            }
            string checks = (unit != null ? $"x = {AdaptUnitCall("x", unit)}; " : "")
                + (kind != 0 ? FieldKindCheck(name, kind) + " " : "")
                + (requiredType != null ? ClassGuard("x", requiredType, $"Field '{name}'", $"objAt({obj})") + " " : "");
            string store = checks.Length > 0 && _usesExceptions ? $"if (FIRE_LIKELY(!g_unwind.active)) setFieldAt({obj}, {index}, x);" : $"setFieldAt({obj}, {index}, x);";
            // an object with probes (flag 2) takes the setter, which tells them; every other one is written by the index
            // (the setter of a restricted name takes the caller's class: here it may, see DirectFieldIndex)
            string tail = FieldTail(name, from, () => "NULL");
            string probed = _usesProbes ? $"else if (FIRE_UNLIKELY(objAt({obj})->h.flags & 2)) {FieldFn("sf", name)}({obj}, x{tail}); " : "";
            // (a receiver that may be undefined: the setter reports that)
            string notObject = nullable ? $"if (FIRE_UNLIKELY({obj}.kind != K_Class)) {FieldFn("sf", name)}({obj}, x{tail}); else " : "";
            return $"{{ Value x = {value}; {notObject}if (FIRE_UNLIKELY(!leafAlive({obj}))) destroyedError({obj}); {probed}else {{ {checks}{store} }} }}";
        }

        /// <summary>The one method `name(argc)` every object an `rc` can be runs (no derived class overrides it); null when there is none, several, an
        /// access check, an ownership method or the threads (actors, sections) need the dispatcher.</summary>
        private FunctionProto? DirectMethod(RuntimeClass rc, string name, int argc, Func from)
        {
            if (LibraryRole || _usesThreads || _usesActors || OwnMethodId(name, argc) != null) return null;   // (a library: a class of the program may override it)
            // a name that is private or protected somewhere: direct only when the code of `from` may call the method on every class the object can be
            // (the check is then known at compile time - e.g. `this.Helper()` in the class that declares the private Helper)
            bool restricted = RestrictedMethodName(name);
            FunctionProto? proto = null;
            foreach (var c in InstancesOf(rc))
            {
                if (c.Rc.IsTypeOverloaded(name, argc)) return null;   // (the values decide: the dispatcher scores them)
                var found = c.Rc.FindMethodWithAccess(name, argc);
                var m = found.Proto;
                if (m is not { IsStatic: false } || proto != null && !ReferenceEquals(proto, m)) return null;
                if (restricted && !AccessAllowed(from.Chunk.OwnerClass, found.DeclaringClass!, found.Access)) return null;
                proto = m;
            }
            return proto;
        }

        /// <summary>The one accessor (`get_name`, or `set_name` for <paramref name="setter"/>) of the property `name` that every object an `rc` can be runs, when
        /// the code of `from` may use it on each of them (known at compile time); null when a class has a field of that name, the accessors differ, or the
        /// threads (sections) or probes need the field helpers.</summary>
        private FunctionProto? DirectAccessor(RuntimeClass rc, string name, bool setter, Func from)
        {
            if (LibraryRole || _usesThreads || _usesActors || setter && _usesProbes) return null;
            string method = (setter ? "set_" : "get_") + name;
            int argc = setter ? 1 : 0;
            bool restricted = RestrictedMethodName(method);
            FunctionProto? proto = null;
            foreach (var c in InstancesOf(rc))
            {
                if (c.Rc.FieldIndex.ContainsKey(name) || c.Rc.IsTypeOverloaded(method, argc)) return null;
                var found = c.Rc.FindMethodWithAccess(method, argc);
                if (found.Proto is not { IsStatic: false } m || proto != null && !ReferenceEquals(proto, m)) return null;
                if (restricted && CheckAccess && !AccessAllowed(from.Chunk.OwnerClass, found.DeclaringClass!, found.Access)) return null;
                proto = m;
            }
            return proto;
        }

        /// <summary>May the code of `from` call the method `name(argc)` on an object of class `rc` (every overload the values can pick)? Known at compile time.</summary>
        private bool MethodAllowedOn(RuntimeClass rc, string name, int argc, Func from)
        {
            if (!RestrictedMethodName(name)) return true;
            if (rc.IsTypeOverloaded(name, argc))
            {
                var set = rc.Candidates(name, argc);
                for (int i = 0; i < set.Protos.Length; i++)
                    if (!AccessAllowed(from.Chunk.OwnerClass, set.Declaring[i], set.Protos[i].Access ?? AccessModifier.Public)) return false;
                return true;
            }
            var found = rc.FindMethodWithAccess(name, argc);
            return found.Proto == null || AccessAllowed(from.Chunk.OwnerClass, found.DeclaringClass!, found.Access);
        }

        /// <summary>Where two types meet: the same type, a type and `undefined` (nullable), else unknown.</summary>
        private static int JoinTypes(int a, int b)
        {
            if (a == b) return a;
            if (a == TypeNone) return b;
            if (b == TypeNone) return a;
            if (a == TypeUnknown || b == TypeUnknown) return TypeUnknown;
            if (a == TypeUndefined) return b | NullableBit;
            if (b == TypeUndefined) return a | NullableBit;
            return (a | NullableBit) == (b | NullableBit) ? a | NullableBit : TypeUnknown;
        }
        private Unit UnitById(int id) => id == 0 ? Unit.Unitless : _units[id];

        // The static types of variables, found like VarRef: every store joins the type of its value; a variable that is stored with two types,
        // an unknown value or through a pointer is TypeUnknown (absent: nothing stored yet). A change makes the generator run another round, so
        // the code of the last round sees the final types.
        private readonly Dictionary<string, int> _varType = new();
        private void JoinVarType(string key, int type)
        {
            if (type == TypeNone) return;   // (nothing yet)
            int joined = _varType.TryGetValue(key, out int old) ? JoinTypes(old, type) : type;
            if (_varType.ContainsKey(key) && joined == old) return;
            _varType[key] = joined;
            _version++;
        }

        /// <summary>The unit a demand (TypeRef.UnitDemand, a leading `#` for a float) certainly leaves a number in; -1 when it leaves the unit open
        /// (`any`, `any m`).</summary>
        private int DemandedUnitId(string demand)
        {
            string unit = demand.TrimStart('#');
            if (unit == "any" || unit.StartsWith("any ", StringComparison.Ordinal)) return -1;
            return UnitId(DemandUnit(unit));
        }

        /// <summary>Fields whose address is taken (`&obj.f`): what is written through the pointer is not checked.</summary>
        private readonly HashSet<string> _fieldAddressTaken = new();

        /// <summary>The static type of `obj.name`: every class of the program that has the field declares it int/float/bool with an exact unit (its setter
        /// checks every assignment, DeclaredKind) - the kind or undefined; TypeUnknown for a property, a field whose address is taken, or anything else.</summary>
        private int FieldType(string name)
        {
            if (LibraryRole || HasProperty(name) || _fieldAddressTaken.Contains(name) || name is "Length" or "length" or "littleEndian") return TypeUnknown;
            int? result = null;
            foreach (var cls in _classList)
            {
                if (!cls.Rc.FieldIndex.ContainsKey(name)) continue;
                byte declared = cls.Rc.FindFieldRequiredKind(name);
                if (declared == 0)
                {
                    // a field declared with a class: an object of it or undefined (its setter checks every assignment, DeclaredType)
                    if (cls.Rc.FindFieldRequiredType(name) is not { } required || DeclaredClass(required) is not { } requiredRc) return TypeUnknown;
                    int objType = ObjType(requiredRc) | NullableBit;
                    if (result is { } known && known != objType) return TypeUnknown;
                    result = objType;
                    continue;
                }
                byte kind = KindOfDeclared(declared);
                int unit = kind == KindBool ? 0 : cls.Rc.FindFieldRequiredUnit(name) is { } demand ? DemandedUnitId(demand) : DeclaredKind.IsUnitless(declared) ? 0 : -1;
                if (unit < 0) return TypeUnknown;
                int type = TypeOf(kind, unit) | NullableBit;
                if (result is { } r && r != type) return TypeUnknown;
                result = type;
            }
            return result ?? TypeUnknown;
        }

        /// <summary>The check of a declaration with a class, an interface or a constrained type parameter (a parameter, a field; DeclaredType, `spec` its
        /// encoded form): `undefined` or a value that fits one alternative passes (and a destroyed object: every use of it reports that), anything else
        /// is an ArgumentException.</summary>
        private string ClassGuard(string value, string spec, string what, string ownerObj = "NULL", string? callTArgs = null)
        {
            var parsed = DeclaredType.Parse(spec);
            string altCheck = "";
            if (parsed.Alternatives.Length > 0)
            {
                string fits = string.Join(" || ", parsed.Alternatives.Select(alt => "(" + string.Join(" && ", alt.Select(c => HoldsExpr(value, c))) + ")"));
                // (a destroyed object first: its memory is gone, `is of` must not read its class)
                altCheck = $"if (FIRE_UNLIKELY({value}.kind != K_Undefined && ({value}.kind != K_Class || leafAlive({value})) && !({fits}))) guardClassFailed({value}, {CString(parsed.Display)}, {CString(what)});";
            }
            if (parsed.TypeParamIndex < 0) return altCheck;
            // a type parameter: the type argument of the object (class) or of the call (method) decides when it is known, its `where` otherwise
            int k = parsed.TypeParamIndex;
            string? typeArg = parsed.TypeParamClass!.Length > 0
                ? (ownerObj != "NULL" ? TArgOfExpr(parsed.TypeParamClass, k, ownerObj) : null)
                : (callTArgs != null ? $"({callTArgs} ? {callTArgs}[{k}] : NULL)" : null);
            if (typeArg == null) return altCheck;
            string param = parsed.Display.Split(' ')[0];
            return $"{{ const TArg* tga = {typeArg}; if (tga) {{ if (FIRE_UNLIKELY(!fitsTArg(&{value}, tga))) guardTArgFailed({value}, {CString(param)}, tga, {CString(what)}); }}"
                + (altCheck.Length > 0 ? $" else {{ {altCheck} }}" : "") + " }";
        }

        /// <summary>The C++ test of one condition of a declaration (or of a type argument) on `value`.</summary>
        private string HoldsExpr(string value, DeclaredType.Condition c)
        {
            if (c.IsUnit) return $"(({value}.kind == K_Int || {value}.kind == K_Float) && unitCompatible(unitOf({value}), {UnitC(UnitId(c.Unit))}))";
            switch (c.Name)
            {
                case "int": return $"({value}.kind == K_Int && {value}.reserved == 0)";
                case "float": return $"{value}.kind == K_Float";
                case "bool": return $"{value}.kind == K_Bool";
                case "char": return $"{value}.kind == K_Char";
                case "string": return $"{value}.kind == K_String";
                case "class": case "Exception": return $"{value}.kind == K_Class";
            }
            _isOfTypes.Add(c.Name);
            string enumerable = GenericClassNames.PlainName(c.Name) == "IEnumerable" ? $"{value}.kind == K_Array || {value}.kind == K_Buffer || {value}.kind == K_String || " : "";
            return $"({enumerable}{IsOfFn(c.Name)}({value}))";
        }

        /// <summary>The TArg of a type argument text (`&amp;ta_N`; NULL for "": not known).</summary>
        private string TArgRef(string text)
        {
            if (text.Length == 0) return "NULL";
            if (!_targIds.TryGetValue(text, out int id))
            {
                _targIds[text] = id = _targIds.Count;
                HoldsExpr("v", DeclaredType.ConditionOf(text));   // (registers the `isof_` it needs)
            }
            return $"&{Px}ta_{id}";
        }

        /// <summary>The type argument of the k-th type parameter of class `cls` for the object `ownerObj` (an Obj*, may be null): `targOf_C_k`.</summary>
        private string TArgOfExpr(string cls, int k, string ownerObj)
        {
            _targOfs.Add((cls, k));
            return $"{TArgOfFn(cls, k)}({ownerObj})";
        }

        /// <summary>The object of the running method as an Obj* (null outside of a method or for an extension method's receiver that is no object).</summary>
        private string SelfObj(Func f) => f.HasSelf ? "(self.kind == K_Class ? objAt(self) : NULL)" : "NULL";

        /// <summary>The C++ expression (TArgs) of the type arguments of a SetTypeArgs text: a constant list, or one put together at run time when it
        /// names a type parameter of the running code (`$k@Class` of the object, `$k@` of the call).</summary>
        private string TArgsExpr(Func f, string text)
        {
            var parts = text.Split('|');
            if (!text.Contains('$'))
            {
                if (!_targListIds.TryGetValue(text, out int id))
                {
                    foreach (var part in parts) TArgRef(part);
                    _targListIds[text] = id = _targListIds.Count;
                }
                return $"{Px}tl_{id}";
            }
            string Part(string t)
            {
                if (!t.StartsWith('$')) return TArgRef(t);
                int at = t.IndexOf('@');
                int k = int.Parse(t.AsSpan(1, at - 1), CultureInfo.InvariantCulture);
                if (at == t.Length - 1) return f.HasCallTArgs ? $"(ctargs ? ctargs[{k}] : NULL)" : "NULL";
                return f.HasSelf ? TArgOfExpr(t.Substring(at + 1), k, SelfObj(f)) : "NULL";
            }
            return $"internTArgsN((const TArg* const[]){{{string.Join(", ", parts.Select(Part))}}}, {parts.Length})";
        }

        /// <summary>The type arguments' tables: a TArg per text, the constant lists and the `targOf_` functions (declared before the helpers that check fields).</summary>
        private string TArgTables(bool withTargOf = true)
        {
            // the targOf_ functions first: their constant type arguments are registered as they are made
            var targOfCode = new StringBuilder();
            foreach (var (cls, k) in withTargOf ? _targOfs : new SortedSet<(string Cls, int Index)>())
            {
                if (Modular) { targOfCode.Append(ModTArgOf(cls, k)); continue; }
                var cases = new List<(int Id, string Expr)>();
                foreach (var ci in _classList)
                {
                    string[]? args = null;   // (null: the class's own type arguments; "$j" is its j-th)
                    var rc = ci.Rc;
                    while (rc != null && rc.Name != cls)
                    {
                        var given = rc.BaseTypeArgs ?? Array.Empty<string>();
                        var outer = args;
                        args = given.Select(t => !t.StartsWith('$') ? t
                            : outer == null ? t
                            : int.Parse(t.AsSpan(1), CultureInfo.InvariantCulture) is var j && j < outer.Length ? outer[j] : "").ToArray();
                        rc = rc.Base;
                    }
                    if (rc == null) continue;
                    string token = args == null ? "$" + k : k < args.Length ? args[k] : "";
                    string expr = token.Length == 0 ? "NULL"
                        : token.StartsWith('$') ? $"(o->targs ? o->targs[{token.Substring(1)}] : NULL)"
                        : TArgRef(token);
                    cases.Add((ci.Id, expr));
                }
                targOfCode.AppendLine($"FIRE_MAYBE_UNUSED {HelperStorage}const TArg* targOf_{Mangle(cls)}_{k}(Obj* o) {{");
                targOfCode.AppendLine("    if (!o) return NULL;");
                targOfCode.AppendLine("    switch (o->cls) {");
                foreach (var group in cases.GroupBy(c => c.Expr))
                    targOfCode.AppendLine("        " + string.Concat(group.Select(c => $"case {c.Id}: ")) + $"return {group.Key};");
                targOfCode.AppendLine("        default: return NULL;");
                targOfCode.AppendLine("    }");
                targOfCode.AppendLine("}");
            }
            var sb = new StringBuilder();
            foreach (var (text, id) in _targIds.OrderBy(kv => kv.Value))
            {
                var condition = DeclaredType.ConditionOf(text);
                string holds = HoldsExpr("v", condition);
                string kind = "0", adapt = "NULL";
                if (DeclaredType.NumberDemand(text) is { } number)
                {
                    // a number type argument: of its kind and in its unit already (anything else of the kind is converted: taa_)
                    kind = number.Kind == "int" ? "K_Int" : "K_Float";
                    string demand = number.Demand.TrimStart('#');
                    if (demand.StartsWith("any ", StringComparison.Ordinal)) holds += $" && unitCompatible(unitOf(v), {UnitC(UnitId(DemandUnit(demand.Substring(4))))})";
                    else if (demand != "any") holds += $" && v.unit == {UnitC(UnitId(DemandUnit(demand)))}";
                    if (number.Demand != "any")
                    {
                        sb.AppendLine($"static Value {Px}taa_{id}(Value v) {{ return {AdaptUnitCall("v", number.Demand)}; }}");
                        adapt = $"{Px}taa_{id}";
                    }
                }
                sb.AppendLine($"static bool {Px}tah_{id}(Value v) {{ return {holds}; }}");
                sb.AppendLine($"static const TArg {Px}ta_{id} = {{{CString(DeclaredType.DisplayOf(text))}, {Px}tah_{id}, {(text.StartsWith("float", StringComparison.Ordinal) && DeclaredType.NumberDemand(text) != null ? "true" : "false")}, {kind}, {adapt}}};");
            }
            foreach (var (text, id) in _targListIds.OrderBy(kv => kv.Value))
                sb.AppendLine($"static const TArg* const {Px}tl_{id}[] = {{{string.Join(", ", text.Split('|').Select(TArgRef))}}};");
            sb.Append(targOfCode);
            return sb.ToString();
        }

        /// <summary>The class a declaration (DeclaredType, encoded) certainly holds an object of (or undefined): a single `is of` a class of the program.</summary>
        private RuntimeClass? DeclaredClass(string spec) =>
            DeclaredType.Parse(spec).SingleName is { } name && _program.Program.Classes.TryGetValue(name, out var rc) ? rc : null;

        /// <summary>The names a declaration tests with `is of` (each gets an `isof_` function).</summary>
        private static IEnumerable<string> IsOfNames(string spec) =>
            DeclaredType.Parse(spec).Alternatives.SelectMany(a => a).Where(c => !c.IsUnit && c.Name is not ("int" or "float" or "bool" or "char" or "string" or "class" or "Exception")).Select(c => c.Name);

        /// <summary>Does a value of this static type pass the check of a declaration (DeclaredType, encoded) as it is?</summary>
        private bool FitsClassType(int type, string spec) =>
            type == TypeUndefined || DeclaredType.Parse(spec).TypeParamIndex < 0 && BaseKindOfType(type) == KindObj && (type & TypeNone) == 0
                && (InterfaceOfType(type) is { } iface ? DeclaredType.Parse(spec).SingleName == iface : DeclaredClass(spec) is { } rc && ClassIs(ObjClass(type), rc.Name));

        /// <summary>The static return type of a generated function (joined by its `return` instructions; constructors return the object).</summary>
        private static string ReturnKey(Func f) => "ret:" + f.Name;
        private int ReturnType(Func f) => f.Kind == FuncKind.Ctor || f.External ? TypeUnknown : _varType.TryGetValue(ReturnKey(f), out int type) ? type : TypeNone;

        /// <summary>The static type of what a dispatcher call `x.name(...)` gives: known when every method of that name and arity in the classes of the
        /// program returns the same type and nothing else can be called (no extension, built-in or ownership method, no actors); TypeUnknown otherwise.
        /// A receiver that has no such method throws, and the code after the call does not run then.</summary>
        private int DispatchResultType(string name, int argc)
        {
            if (LibraryRole || _usesActors || ExtensionMethods(name, argc).Any() || BuiltinMethods(name, argc).Any() || OwnMethodId(name, argc) != null
                || name == "GetEnumerator" && argc == 0) return TypeUnknown;
            int? result = null;
            foreach (var cls in _classList)
                foreach (var method in MethodsFor(cls.Rc, name, argc))
                {
                    if (method.IsStatic && !cls.Rc.IsTypeOverloaded(name, argc)) continue;
                    if (!_funcByProto.TryGetValue(method, out var target)) return TypeUnknown;
                    int type = ReturnType(target);
                    if (type == TypeUnknown) return TypeUnknown;
                    result = result is { } r ? JoinTypes(r, type) : type;
                    if (result == TypeUnknown) return TypeUnknown;
                }
            return result ?? TypeUnknown;
        }

        /// <summary>The type a constant gives a stack slot (constants carry no bit width in the generated code).</summary>
        private int TypeOfConstant(Value c) => c.Kind switch
        {
            ValueKind.Int => TypeOf(KindInt, UnitId(c.Unit)),
            ValueKind.Float => TypeOf(KindFloat, UnitId(c.Unit)),
            ValueKind.Bool => TypeOf(KindBool, 0),
            ValueKind.Undefined => TypeUndefined,
            _ => TypeUnknown,
        };

        private void MarkVarRef(string key)
        {
            if (VarRef(key)) return;
            _varRef[key] = true;
            _version++;
        }

        /// <summary>Key of a variable in <see cref="_varRef"/>: parameters and block locals belong to one function, globals and statics are shared.</summary>
        private static string VarKey(Func f, string name) => name[0] == 'G' || name.StartsWith("SF", StringComparison.Ordinal) ? name : f.Name + ":" + name;

        // -------------------------------------------------------------------------------------------------------------
        // Whole program
        // -------------------------------------------------------------------------------------------------------------
        private static readonly (ValueKind Kind, string Cpp)[] ExtendableKinds =
        {
            (ValueKind.String, "K_String"), (ValueKind.Char, "K_Char"), (ValueKind.Int, "K_Int"),
            (ValueKind.Float, "K_Float"), (ValueKind.Bool, "K_Bool"), (ValueKind.Array, "K_Array"),
        };

        // ---- Access modifiers (SPEC 5.7): checked in Debug and Release, not in Performance ----------------------------------------------
        private readonly SortedDictionary<string, string> _externDecls = new();

        /// <summary>The C type of a parameter or result of an `extern` function: `int` is as wide as the declaration says (`int[32]`), else as wide as a pointer.</summary>
        private static string ExternCType(TypeRef t, string name)
        {
            if (t.PointerDepth > 0) return "void*";
            return t.BaseName switch
            {
                "bool" => "bool",
                "int" => t.BitWidth switch { 8 => "int8_t", 16 => "int16_t", 32 => "int32_t", 64 => "int64_t", _ => "intptr_t" },
                "float" => t.BitWidth == 32 ? "float" : "double",
                "char" => "fire_char16",
                "string" => "const char*",
                _ => throw new NativeNotSupportedException($"extern function '{name}': the type '{t.BaseName}' cannot be passed to C (supported: bool/int/float/char/string/pointer)"),
            };
        }

        private bool CheckAccess => _program.ExecutionMode != VmExecutionMode.Performance;
        private static string DescribeAccess(AccessModifier access) => access switch { AccessModifier.Private => "private", AccessModifier.Protected => "protected", _ => "public" };

        /// <summary>May code of the class <paramref name="caller"/> (null: top-level code) use a member that <paramref name="declaring"/> declares with this access?</summary>
        private static bool AccessAllowed(RuntimeClass? caller, RuntimeClass declaring, AccessModifier access)
        {
            if (access == AccessModifier.Public) return true;
            if (caller == null) return false;
            // classes are compared by name: the linked program need not keep one object per class
            if (access == AccessModifier.Private) return caller.Name == declaring.Name;
            for (var rc = caller; rc != null; rc = rc.Base) if (rc.Name == declaring.Name) return true;
            return false;
        }

        /// <summary>The class of the code of this function as a number for the checks at run time (no class: all ones).</summary>
        private string CallerId(Func f) => f.Chunk.OwnerClass is { } owner && CheckAccess ? ClassIdExpr(RegisterClass(owner.Name)) : "0xFFFFFFFFu";

        private bool RestrictedMethodName(string name) =>
            CheckAccess && _program.Program.Classes.Values.Any(rc => rc.Methods.TryGetValue(name, out var list) && list.Any(m => m.Access is { } a && a != AccessModifier.Public));

        /// <summary>Is some field (or property accessor) of this name private or protected somewhere? Then the field helpers check the class of the caller.</summary>
        private bool RestrictedField(string name) =>
            CheckAccess && (_program.Program.Classes.Values.Any(rc => rc.OwnFieldInfo.TryGetValue(name, out var info) && info.AccessModifier != AccessModifier.Public)
                || RestrictedMethodName("get_" + name) || RestrictedMethodName("set_" + name));

        /// <summary>The condition (over `caller`) under which the member may be used, empty when everybody may.</summary>
        private string AllowedExpr(RuntimeClass declaring, AccessModifier access)
        {
            if (access == AccessModifier.Public) return "";
            // (with modules a caller may be a class of another module: private is the declaring class, protected a class derived from it - asked at run time)
            if (Modular) return access == AccessModifier.Private ? $"(caller == {ClassIdExpr(RegisterClass(declaring.Name))})" : $"fireClassIsSub(caller, {CString(declaring.Name)})";
            var ids = _classList.Where(c => AccessAllowed(c.Rc, declaring, access)).Select(c => c.Id).ToList();
            return ids.Count == 0 ? "false" : "(" + string.Join(" || ", ids.Select(id => $"caller == {id}")) + ")";
        }

        /// <summary>The extra arguments of a field helper: the scope list when a class has a property of that name, the caller's class when the name is restricted.</summary>
        private string FieldTail(string name, Func f, System.Func<string> list) =>
            (FieldHasList(name) ? ", " + list() : "") + (FieldHasCaller(name) ? ", " + CallerId(f) : "");

        private HashSet<FunctionProto>? _extensionProtos;
        /// <summary>Is the function a method of a base-type extension (`class extends string`)? Its `this` is then a value that can be a string or an array (reference counted).</summary>
        private bool IsExtensionMethod(FunctionProto? proto)
        {
            if (proto == null) return false;
            if (_extensionProtos == null)
            {
                _extensionProtos = new HashSet<FunctionProto>();
                foreach (var (kind, _) in ExtendableKinds)
                    if (BaseTypeExtensions.ClassNameFor(kind) is { } className && _program.Program.Classes.TryGetValue(className, out var rc))
                        foreach (var list in rc.Methods.Values) foreach (var m in list) _extensionProtos.Add(m);
            }
            return _extensionProtos.Contains(proto);
        }

        /// <summary>The methods of a base-type extension (`class extends string`) for this name, per kind of receiver.</summary>
        private IEnumerable<(string Cpp, FunctionProto Proto, RuntimeClass Rc)> ExtensionMethods(string name, int argc)
        {
            foreach (var (kind, cpp) in ExtendableKinds)
                if (BaseTypeExtensions.ClassNameFor(kind) is { } className
                    && _program.Program.Classes.TryGetValue(className, out var rc)
                    && rc.FindMethodWithAccess(name, argc).Proto is { } proto)
                    yield return (cpp, proto, rc);
        }

        /// <summary>Is the class <paramref name="rc"/>, a base class of it or an interface of it called <paramref name="name"/>? (what a typed `catch` matches)</summary>
        private static bool ClassIs(RuntimeClass rc, string name)
        {
            for (var c = rc; c != null; c = c.Base)
                if (c.Name == name || c.Interfaces.Contains(name)) return true;
            return false;
        }

        /// <summary>The runtime enum of a built-in ownership method (SPEC 2.2), or null.</summary>
        private static string? OwnMethodId(string name, int argc) => (name.StartsWith("try", StringComparison.Ordinal) ? name.Substring(3) : name, argc) switch
        {
            ("TakeLocal", 0 or 1) => "OM_TakeLocal",
            ("TakeUpwards", 0 or 1) => "OM_TakeUpwards",
            ("TakeGlobal", 0 or 1) => "OM_TakeGlobal",
            ("TakeTo", 1 or 2) => "OM_TakeTo",
            _ => null,
        };

        /// <summary>`try x.Take...(...)` (the compiler turns it into a call of `tryTake...`): only the owner moves it.</summary>
        private static bool OwnMethodIsTry(string name) => name.StartsWith("tryTake", StringComparison.Ordinal);

        /// <summary>The ownership method takes the target as its first argument (`TakeTo(obj)`), and optionally a `Takes` mode after it.</summary>
        private static int OwnMethodBaseArgs(string ownId) => ownId == "OM_TakeTo" ? 1 : 0;

        private bool AnyClassHasMethod(string name, int argc) => LibraryRole
            ? _program.Program.Classes.Values.Any(rc => rc.FindMethodWithAccess(name, argc).Proto != null)   // (a library: the classes it sees - an ownership method of a class of the program goes unnoticed)
            : _classList.Any(c => c.Rc.FindMethodWithAccess(name, argc).Proto != null);

        /// <summary>The natives of the imports of packages (C++ source that is put into the generated file): the fire name -> argument count and the C++ function.</summary>
        private readonly Dictionary<string, (int Argc, string Function, bool NeedsList, bool ReturnsReference, bool Host, bool Throws, bool IsC)> _packageNatives = new();
        /// <summary>The exception classes that natives of the imported packages throw (`exceptions` of the package): the runtime constructs them by name (`fireError`).</summary>
        private readonly List<string> _errorClasses = new();
        private readonly List<(InstalledImport Import, string Key)> _packageImports = new();

        /// <summary>The natives in C++ (graphics, windows, the packages) that the program calls: they are in a file of their own (C++), the program (C) calls them
        /// through a function with C linkage each - `fire_nat_N`. The C++ function -> (N, number of arguments, the list of the scope as the last argument).</summary>
        private readonly Dictionary<string, (int Index, int Argc, bool NeedsList)> _nativeThunks = new(StringComparer.Ordinal);

        /// <summary>The name of the C function that calls the C++ native <paramref name="cppFunction"/>.</summary>
        private string NativeThunk(string cppFunction, int argc, bool needsList)
        {
            if (!_nativeThunks.TryGetValue(cppFunction, out var thunk))
                _nativeThunks[cppFunction] = thunk = (_nativeThunks.Count, argc, needsList);
            return $"fire_nat_{Px}{thunk.Index}";
        }

        /// <summary>The parameters of a thunk (`Value a0, ..., OwnList* list`).</summary>
        private static string ThunkParams(int argc, bool needsList) =>
            argc == 0 && !needsList ? "void" : string.Join(", ", Enumerable.Range(0, argc).Select(i => $"Value a{i}").Concat(needsList ? new[] { "OwnList* list" } : Array.Empty<string>()));

        /// <summary>Looks at the imports of packages the program uses: their native functions, and whether the target is one the C++ source is written for.</summary>
        private void CollectPackages()
        {
            foreach (var key in _program.NativeImports.Where(k => k.StartsWith(PackageStore.KeyPrefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
            {
                var import = PackageStore.Default.FindKey(key) ?? throw new NativeNotSupportedException($"the import '{key.Substring(PackageStore.KeyPrefix.Length)}' belongs to a package that is not installed (`ember list`)");
                _packageImports.Add((import, key));
                var native = import.Import.Native;
                if (native == null) continue;
                if (!native.SupportsAny(new[] { _target.Native.Platform, _target.Name }))
                    throw new NativeNotSupportedException($"the package '{import.Package.Name}' (import '{import.Name}') has native code for {string.Join(", ", native.Platforms)}, not for the platform '{_target.Native.Platform}' of the target '{_target.Name}'");
                if (native.SourcesFor(new[] { _target.Native.Platform, _target.Name }).Count == 0)
                    throw new NativeNotSupportedException($"the package '{import.Package.Name}' (import '{import.Name}') has no C++ source for the target '{_target.Name}' (platform '{_target.Native.Platform}')");
                foreach (var fn in native.Functions)
                {
                    _packageNatives.TryAdd(fn.Name, (fn.Arguments, fn.Symbol, fn.NeedsList, fn.ReturnsReference, fn.Host, native.Exceptions.Count > 0, native.IsC));
                    _nativeImportOf.TryAdd(fn.Name, key);
                }
                foreach (var name in native.Exceptions) if (!_errorClasses.Contains(name)) _errorClasses.Add(name);
            }
        }

        /// <summary>The unit a unit declaration names: `mm`, or of `any m` the `m` (Value.AdaptToUnit).</summary>
        private static Unit DemandUnit(string demand)
        {
            demand = demand.TrimStart('#');
            return Unit.Parse(demand.StartsWith("any ", StringComparison.Ordinal) ? demand.Substring(4) : demand);
        }

        /// <summary>The C that converts `value` into the unit a declaration demands (fire_rt.h adaptUnit; a leading `#`: the declaration is a float).</summary>
        private string AdaptUnitCall(string value, string demand)
        {
            bool toFloat = demand.StartsWith('#');
            string unit = demand.TrimStart('#');
            if (unit == "any") return toFloat ? $"intToFloat({value})" : value;
            return $"adaptUnit({value}, {UnitC(UnitId(DemandUnit(unit)))}, {(unit.StartsWith("any ", StringComparison.Ordinal) ? "true" : "false")}, {(toFloat ? "true" : "false")}, {Constant(Value.MakeString(Unit.DemandDisplay(unit)))})";
        }

        private string Run()
        {
            CollectPackages();
            var main = _main = new Func { Proto = null, Chunk = _program.Program.TopLevel, Kind = FuncKind.Main };
            if (DebugBuild) UseExceptions();   // (a debug build: every run-time error is an exception the debugger can stop at)
            if (_program.Program.Classes.Values.Any(rc => rc.IsActor)) { _usesActors = true; UseThreads(); }
            ApplyForcedFlags();
            AddLibraryNeeds();

            // Fixpoint: generating code can create classes, functions and method calls, and can show that a variable may
            // hold a reference; all of that changes what the code generated before it has to look like.
            for (int round = 0; ; round++)
            {
                int before = _version;
                if (LibraryRole) KeepOwnModule();
                else GenerateChunk(main);
                for (int i = 0; i < _funcs.Count; i++)
                {
                    if (!LibraryRole) { GenerateChunk(_funcs[i]); continue; }
                    // a library module keeps every member: one the target cannot translate (fire threads on wasm) becomes a function that fails when it is called
                    try { GenerateChunk(_funcs[i]); }
                    catch (NativeNotSupportedException ex) when (_funcs[i].Kind is not FuncKind.Main) { _funcs[i].Code = UnsupportedStub(_funcs[i], ex.Message); }
                }
                RegisterTargets();
                // (made once more when the file is put together: the strings, units and type arguments they use are registered by now)
                if (Modular) ModularHelpers();   // (modules: all of its helpers - their checks register strings, units, `is of` tests, type arguments and functions)
                else
                {
                    foreach (var name in _fieldNames.ToList()) FieldHelpers(name);
                    TArgTables();
                }
                // (the units a text may name, see textUnits below: registered here, so that the conversion functions of a unit (`coerce`) are generated too)
                if (!LibraryRole && (_usesStringCall || _program.Program.CustomUnits.Count > 0))
                {
                    foreach (var (symbol, _) in Unit.BuiltInSymbols) UnitId(Unit.Parse(symbol));
                    foreach (var own in _program.Program.CustomUnits) UnitId(Unit.OfCustom(own));
                }
                if (_version == before) break;
                if (round > 40) throw new NativeNotSupportedException("code generation does not settle");
            }

            _initCode.Clear();
            if (LibraryRole) return LibraryBody();
            var sb = new StringBuilder();
            sb.AppendLine("// Generated by fire.Native from the bytecode - do not edit.");
            sb.AppendLine($"// target: {_target.Name}");
            sb.AppendLine($"#define FIRE_TARGET \"{_target.Name}\"");
            sb.AppendLine($"#define FIRE_TARGET_{_target.Name.ToUpperInvariant()} 1");
            sb.AppendLine($"#define FIRE_HAL_{_target.HalPackage.ToUpperInvariant()} 1");
            sb.AppendLine($"#define FIRE_DEFAULT_STACK_BYTES {_target.DefaultStackBytes}");
            // The target section: what the target configuration says - definitions, the headers that come before the platform package, and which package it is.
            foreach (var define in _target.Native.Defines)
            {
                int eq = define.IndexOf('=');
                sb.AppendLine(eq < 0 ? $"#define {define}" : $"#define {define.Substring(0, eq)} {define.Substring(eq + 1)}");
            }
            if (_usesExceptions) sb.AppendLine("#define FIRE_EXCEPTIONS 1");
            if (Modular) sb.AppendLine("#define FIRE_MODULES 1   // modules (ROADMAP 9): the members of the classes of other modules by name, through the class descriptors");
            if (DebugBuild) sb.AppendLine("#define FIRE_DEBUG 1   // a debug build: the shadow stack and the hooks of the debugger (fire_debug.h)");
            if (DebugBuild && DebugAgent) sb.AppendLine("#define FIRE_DEBUG_AGENT 1   // the debug agent (docs/NATIVE_DEBUG_PROTOCOL.md; the desktop: FIRE_DEBUG=tcp:127.0.0.1:PORT;token=T)");
            if (DebugBuild && DebugAgent && DebugTransportDefine is { } transport)
                sb.AppendLine($"#define {transport} 1   // the agent talks framed over {(transport.EndsWith("UART", StringComparison.Ordinal) ? "the console UART" : "stdio")} (docs/NATIVE_DEBUG_PROTOCOL.md, Transport)");
            if (_usesThreads) sb.AppendLine("#define FIRE_THREADS 1");
            if (_usesReflection) sb.AppendLine("#define FIRE_REFLECTION 1");
            if (_usesElementPointers && _program.ExecutionMode != VmExecutionMode.Performance) sb.AppendLine("#define FIRE_PTRBOUNDS 1 // pointers into arrays and buffers (`ref a[i]`, `p + n`) know the bounds");
            if (_program.ExecutionMode == VmExecutionMode.Performance) sb.AppendLine("#define FIRE_UNCHECKED 1 // #performance: destroyed arrays and buffers are not detected");
            if (_program.FloatWidth == 32) sb.AppendLine("#define FIRE_FLOAT32 1 // #floatwidth 32: float is a 32-bit float, like in the VM");
            // The units a text may name (`"5mm".ToFloat()`, Unit.ParseKnown): those of fire without a prefix, then those of the program by symbol and name
            var textUnits = new List<(string Symbol, int Unit, bool Prefixable)>();
            if (_usesStringCall || _program.Program.CustomUnits.Count > 0)   // (the names of combined units look at the symbols of the program too)
            {
                foreach (var (symbol, prefixable) in Unit.BuiltInSymbols) textUnits.Add((symbol, UnitId(Unit.Parse(symbol)), prefixable));
                foreach (var own in _program.Program.CustomUnits)
                {
                    int id = UnitId(Unit.OfCustom(own));
                    textUnits.Add((own.Symbol, id, own.Prefixed));
                    if (own.Name != own.Symbol) textUnits.Add((own.Name, id, false));
                }
            }
            // The unit table: the base symbols of all units of the program (sorted like the VM prints them) and one row per unit.
            var dimNames = _units.SelectMany(u => u.Dimensions.Keys).Distinct().OrderBy(k => k).ToList();
            if (_separate && dimNames.Count > SeparateDims) throw new NativeNotSupportedException($"the program uses {dimNames.Count} dimensions of units; with linkage \"static\" at most {SeparateDims}");
            sb.AppendLine($"#define FIRE_NDIMS {(_separate ? SeparateDims : Math.Max(1, dimNames.Count))}");
            foreach (var include in _target.Native.Includes) sb.AppendLine($"#include <{include}>");
            // what both files say first (the natives in C++ see the same configuration of the runtime as the program)
            string prelude = sb.ToString();
            // the system libraries that packages need on this platform (`linkLibraries`): the build links them
            foreach (var (import, _) in _packageImports)
                if (import.Import.Native is { } packageNative)
                    foreach (string lib in packageNative.LinkLibrariesFor(new[] { _target.Native.Platform, _target.Name })) sb.AppendLine($"// fire-link: {lib}");
            // the display of these platforms: fire's own (native/platform/std/fire_display_desktop.h - what it links is what the operating system always has), or SDL3/SDL2
            // when the target or FIRE_SDL asks for it (fire_display.h of the platform picks it by FIRE_DISPLAY_SDL and FIRE_SDL3): the build links it
            if (_usesWindows && _target.Native.Platform == "wasm") { if (SdlOfTarget() is int) { sb.AppendLine("#define FIRE_DISPLAY_SDL 1"); sb.AppendLine("// fire-link: SDL2"); } }   // (Emscripten's own SDL2: -sUSE_SDL=2; fire's own display: the canvas, nothing to link)
            else if (_usesWindows && _target.Native.Platform is "posix" or "windows")
            {
                if (SdlOfTarget() is int sdl)
                {
                    sb.AppendLine("#define FIRE_DISPLAY_SDL 1");
                    if (sdl == 3) { sb.AppendLine("// fire-link: SDL3"); sb.AppendLine("#define FIRE_SDL3 1"); }
                    else sb.AppendLine("// fire-link: SDL2");
                }
                else foreach (string lib in OwnDisplayLibraries()) sb.AppendLine($"// fire-link: {lib}");
            }
            if (DebugBuild && DebugAgent && _target.Native.Platform == "windows") sb.AppendLine("// fire-link: ws2_32");   // (the debug agent talks TCP)
            if (_separate)
            {
                // the head of the file of each library module: the same configuration of the runtime, without its state (that lives in the program's file)
                var head = new StringBuilder(prelude);
                if (_usesWindows && _target.Native.Platform is "posix" or "windows" && SdlOfTarget() is int headSdl) { head.AppendLine("#define FIRE_DISPLAY_SDL 1"); if (headSdl == 3) head.AppendLine("#define FIRE_SDL3 1"); }
                head.AppendLine($"#define FIRE_PLATFORM_HEADER \"platform/{_target.Native.Platform}/fire_platform.h\"");
                head.AppendLine("#include \"fire_rt.h\"");
                head.AppendLine("#if defined(__GNUC__) || defined(__clang__)\n#pragma GCC diagnostic ignored \"-Wunused-but-set-variable\"\n#pragma GCC diagnostic ignored \"-Wunused-function\"\n#endif");
                foreach (string line in CPlatformHeaderDefines(_target.Native.Platform)) head.AppendLine(line);
                if (_usesJson) head.AppendLine("#include \"fire_json.h\"");
                _libraryHead = head.ToString();
            }
            sb.AppendLine($"#define FIRE_PLATFORM_HEADER \"platform/{_target.Native.Platform}/fire_platform.h\"");
            sb.AppendLine("#define FIRE_RT_IMPLEMENTATION 1   // the runtime's global state lives in this file");
            sb.AppendLine("#include \"fire_rt.h\"");
            // (generated code: a slot that is written and not read again, or a helper nothing calls, is no mistake)
            sb.AppendLine("#if defined(__GNUC__) || defined(__clang__)");
            sb.AppendLine("#pragma GCC diagnostic ignored \"-Wunused-but-set-variable\"");
            sb.AppendLine("#pragma GCC diagnostic ignored \"-Wunused-function\"");
            sb.AppendLine("#endif");

            // The natives in C of the packages ("language": "c") and the bridges of graphics and windows: part of this file, after the runtime - a C compiler is enough
            // (TinyCC, a board without C++). The platform layers in C for them (FIRE_PLATFORM_FS_C_HEADER, ...).
            if (_usesGraphics || _packageImports.Any(p => p.Import.Import.Native is { IsC: true }))
                foreach (string line in CPlatformHeaderDefines(_target.Native.Platform)) sb.AppendLine(line);
            // (linkage "static": the natives of an import that is a library module live in the file of that module)
            if (_usesGraphics && NativesOwner(NativeImports.Graphics) == null) AppendStandardNatives(sb, "graphics");
            if (_usesWindows && NativesOwner(NativeImports.Windows) == null) AppendStandardNatives(sb, "windows");
            foreach (var (import, _) in _packageImports.Where(p => p.Import.Import.Native is { IsC: true } && !_standardNativesWritten.Contains(p.Import.Name) && NativesOwner(p.Key) == null))
                foreach (var (name, text) in import.ReadNativeSources(new[] { _target.Native.Platform, _target.Name }))
                {
                    sb.AppendLine($"// ---- package {import.Package.Name} {import.Package.Version}, import \"{import.Name}\" (C): {name}");
                    sb.AppendLine(text);
                    sb.AppendLine($"// ---- end of {name}");
                }

            // linkage "static": the natives in C live in this file only (their state once); the library modules call them through these
            sb.Append(SharedNativeThunks());
            // the library modules (ROADMAP 9, one file): their code after the runtime and the natives in C, before the program's own (the program defines what they declare)
            if (_moduleBodies != null)
            {
                if (_usesJson) sb.AppendLine("#define FIRE_JSON_IMPLEMENTATION 1\n#include \"fire_json.h\"");
                sb.Append(_moduleBodies);
            }

            // The natives in C++ (the packages that are still C++): a file of their own, built with a C++ compiler and linked with the program.
            var nat = new StringBuilder();
            nat.Append(prelude.Replace("// Generated by fire.Native from the bytecode - do not edit.", "// Generated by fire.Native: the natives in C++ of the program " + NativesFileName + " belongs to - do not edit."));
            nat.AppendLine($"#define FIRE_PLATFORM_HEADER \"platform/{_target.Native.Platform}/fire_platform.h\"");
            nat.AppendLine($"#define FIRE_PLATFORM_CPP_HEADER \"platform/{_target.Native.Platform}/fire_platform.hpp\"");
            nat.AppendLine("#include \"fire_rt_cpp.hpp\"   // the runtime (fire_rt.h, the program has its state) and what C++ code expects of it");
            // the platform layers in C (FIRE_PLATFORM_AUDIO_C_HEADER, ...): packages in C++ use them as well (the circuits package plays through the audio layer)
            if (_packageImports.Count > 0) foreach (string line in CPlatformHeaderDefines(_target.Native.Platform)) nat.AppendLine(line);
            bool natSources = false;
            // the C++ source of the packages the program imports: after the runtime, at the top level (the files include what they need and put their functions into namespace fire)
            foreach (var (import, _) in _packageImports.Where(p => p.Import.Import.Native is not { IsC: true } && !_standardNativesWritten.Contains(p.Import.Name)))
                foreach (var (name, text) in import.ReadNativeSources(new[] { _target.Native.Platform, _target.Name }))
                {
                    nat.AppendLine($"// ---- package {import.Package.Name} {import.Package.Version}, import \"{import.Name}\": {name}");
                    nat.AppendLine(text);
                    nat.AppendLine($"// ---- end of {name}");
                    natSources = true;
                }
            string libraryThunks = _libraries == null ? "" : string.Concat(_libraries.Select(l => l.NativeThunks));
            if (natSources || _nativeThunks.Count > 0 || libraryThunks.Length > 0)
            {
                // the thunks: C functions that call the C++ natives (the program declares them)
                nat.AppendLine();
                sb.AppendLine("// the natives in C++ (" + NativesFileName + ")");
                foreach (var (function, (index, argc, needsList)) in _nativeThunks.OrderBy(t => t.Value.Index))
                {
                    string ps = ThunkParams(argc, needsList);
                    string args = string.Join(", ", Enumerable.Range(0, argc).Select(i => $"a{i}").Concat(needsList ? new[] { "list" } : Array.Empty<string>()));
                    sb.AppendLine($"Value fire_nat_{Px}{index}({ps});   // {function}");
                    nat.AppendLine($"extern \"C\" fire::Value fire_nat_{Px}{index}({ps.Replace("Value", "fire::Value").Replace("OwnList", "fire::OwnList")}) {{ using namespace fire; return {function}({args}); }}");
                }
                nat.Append(libraryThunks);
                _nativesCode = nat.ToString();
            }
            else _nativesCode = null;
            sb.AppendLine();
            // (linkage "static": FIRE_NDIMS is fixed - the names of the dimensions the program does not use are empty)
            var dimTexts = dimNames.Select(CString).Concat(Enumerable.Repeat("\"\"", _separate ? SeparateDims - dimNames.Count : dimNames.Count == 0 ? 1 : 0)).ToList();
            sb.AppendLine($"const char* const g_dimNames[] = {{{string.Join(", ", dimTexts)}}};");
            sb.AppendLine("static const UnitInit kUnitInit[] = {");
            foreach (var u in _units)
            {
                var dims = dimNames.Count == 0 ? "0" : string.Join(", ", dimNames.Select(n => u.Dimensions.TryGetValue(n, out int e) ? e.ToString(CultureInfo.InvariantCulture) : "0"));
                string display = u.DisplaySymbol is { } symbol ? CString(symbol) : "NULL";
                int conv = u.Converter is { } converter ? _unitConverters[converter].Index : -1;
                int ulconv = u.UnitlessConverter is { } plainConverter ? _unitConverters[plainConverter].Index : -1;
                sb.AppendLine($"    {{{{{dims}}}, {DoubleLiteral(u.Scale, false)}, {display}, {DoubleLiteral(u.Offset, false)}, {conv}, {ulconv}}},");
            }
            sb.AppendLine("};");
            if (_unitConverters.Count > 0)
            {
                // the conversion functions of the units (`coerce to`/`coerce from`), in the order of their indices
                var converters = _unitConverters.Values.OrderBy(c => c.Index).ToList();
                foreach (var (_, to, from) in converters) sb.AppendLine($"{to.Signature};\n{from.Signature};");
                sb.AppendLine($"static const UnitConvFn kUnitTo[] = {{{string.Join(", ", converters.Select(c => c.To.Name))}}};");
                sb.AppendLine($"static const UnitConvFn kUnitFrom[] = {{{string.Join(", ", converters.Select(c => c.From.Name))}}};");
                _initCode.AppendLine("    unitsInit(kUnitInit, " + _units.Count + ", kUnitTo, kUnitFrom);");
            }
            else
                _initCode.AppendLine("    unitsInit(kUnitInit, " + _units.Count + ", NULL, NULL);");
            if (textUnits.Count > 0)
            {
                sb.AppendLine("static const TextUnit kTextUnits[] = {");
                foreach (var (symbol, unit, prefixable) in textUnits) sb.AppendLine($"    {{{CString(symbol)}, {unit}, {(prefixable ? "true" : "false")}}},");
                sb.AppendLine("};");
                _initCode.AppendLine($"    g_textUnits = kTextUnits; g_textUnitCount = {textUnits.Count};");
            }
            // the files of `new Resource("path")` are part of the binary (docs/RESOURCES.md)
            var resources = _program.Program.Resources;
            if (resources.Count > 0)
            {
                for (int r = 0; r < resources.Count; r++)
                {
                    var data = resources[r].Data;
                    sb.Append($"static const uint8_t kRes{r}[] = {{");
                    if (data.Length == 0) sb.Append('0');
                    for (int b = 0; b < data.Length; b++)
                    {
                        if (b % 32 == 0) sb.Append("\n    ");
                        sb.Append(data[b]).Append(',');
                    }
                    sb.AppendLine("};");
                }
                sb.AppendLine("static const ResEntry kResources[] = {");
                for (int r = 0; r < resources.Count; r++)
                    sb.AppendLine($"    {{{CString(resources[r].Name)}, kRes{r}, {resources[r].Data.Length}u}},");
                sb.AppendLine("};");
                _initCode.AppendLine($"    g_resources = kResources; g_resourceCount = {resources.Count};");
            }
            if (_externDecls.Count > 0)
            {
                sb.AppendLine("#ifdef __APPLE__");
                sb.AppendLine("#define FIRE_SYM(n) \"_\" n");
                sb.AppendLine("#else");
                sb.AppendLine("#define FIRE_SYM(n) n");
                sb.AppendLine("#endif");
                foreach (var decl in _externDecls.Values) sb.AppendLine(decl);
            }
            sb.AppendLine();
            for (int i = 0; i < _strings.Count; i++)
            {
                string text = _strings[i];
                // the code units as numbers (C99: TinyCC and older compilers know no u"..." literals); a plain text stands next to them as a comment
                bool plain = text.All(c => c >= 0x20 && c < 0x7F) && !text.Contains("*/") && !text.Contains("/*");
                string data = "{" + string.Join(", ", text.Select(c => ((int)c).ToString(CultureInfo.InvariantCulture)).Append("0")) + "}";
                sb.AppendLine($"static const fire_char16 {Px}K{i}_d[] = {data};{(plain ? "   /* " + CString(text) + " */" : "")}");
                sb.AppendLine($"static const Str {Px}K{i} = {{{{IMMORTAL, R_Str}}, {text.Length}, {Px}K{i}_d}};");
            }
            if (_strings.Count > 0) sb.AppendLine();
            if (_globals.Count > 0)
            {
                sb.AppendLine("static Value " + string.Join(", ", _globals.OrderBy(g => g).Select(g => $"G{g} = FIRE_UNDEF_INIT")) + ";");
                sb.AppendLine();
            }
            foreach (var (key, index) in _staticFields.OrderBy(kv => kv.Value))
                if (!Modular) sb.AppendLine($"static Value SF{index} = FIRE_UNDEF_INIT; // {key.Class}.{key.Field}");
                else if (IsOwnClass(FindClass(key.Class))) sb.AppendLine($"Value {StaticFieldVar(key.Class, key.Field)} = FIRE_UNDEF_INIT; // {key.Class}.{key.Field}");
                else sb.AppendLine($"extern Value {StaticFieldVar(key.Class, key.Field)}; // {key.Class}.{key.Field}");
            if (_staticFields.Count > 0) sb.AppendLine();

            sb.Append(ModuleTables());
            foreach (var (from, to) in _unitFactorCaches) sb.AppendLine($"static double {Px}kUF_{from}_{to} = 0;   // the factor between unit {from} and unit {to}, looked up once");
            // Prototypes of everything, then the helpers (they call functions), then the functions (they call helpers).
            FunctionPrototypes(sb);
            // (modules, ROADMAP 9 B2: the helpers serve the program's own classes; the others go through the tables of the runtime)
            if (Modular) sb.Append(ModularHelpers());
            else
            {
                // the field helpers are made first: their checks register the `is of` tests and type arguments they use
                var fieldHelperCode = _fieldNames.Select(FieldHelpers).ToList();
                // the checks of fields declared with a class (sf_) test `is of` - declared before the helpers that use them
                foreach (var name in _fieldNames)
                    foreach (var cls in _classList)
                        if (cls.Rc.FindFieldRequiredType(name) is { } requiredType) _isOfTypes.UnionWith(IsOfNames(requiredType));
                // (the dispatchers and selectors first: their scores register the `is of` tests, class scores and type arguments they use)
                var dispatcherCode = _dispatchers.Select(x => Dispatcher(x.Name, x.Argc)).Concat(_interfaceDispatchers.Select(x => InterfaceDispatcher(x.Interface, x.Name, x.Argc))).ToList();
                var selectorCode = _overloadSelectors.Values.Select(SelectorFunction).ToList();
                string targTables = TArgTables();
                foreach (var typeName in _isOfTypes) sb.AppendLine($"FIRE_MAYBE_UNUSED {HelperStorage}bool isof_{Mangle(typeName)}(Value v);");
                sb.Append(targTables);
                foreach (var name in _fieldNames)
                {
                    string lp = (FieldHasList(name) ? ", OwnList* list" : "") + (FieldHasCaller(name) ? ", uint32_t caller" : "");
                    sb.AppendLine($"FIRE_MAYBE_UNUSED {HelperInline}Value gf_{Mangle(name)}(Value v{lp});").AppendLine($"FIRE_MAYBE_UNUSED {HelperInline}void sf_{Mangle(name)}(Value v, Value x{lp});");
                    if (_fieldAddressTaken.Contains(name)) sb.AppendLine($"FIRE_MAYBE_UNUSED {HelperInline}Value fp_{Mangle(name)}(Value v);");
                }
                foreach (var (name, argc) in _dispatchers)
                {
                    sb.AppendLine(DispatcherSignature(name, argc) + ";");
                    if (WrapDispatchers) sb.AppendLine(DispatcherSignature(name, argc, "direct_") + ";");
                    if (_usesActors) sb.AppendLine($"static Value msg_{Mangle(name)}_{argc}(Value self, const Value* a, OwnList* list);");
                }
                foreach (var (iface, name, argc) in _interfaceDispatchers) sb.AppendLine(InterfaceDispatcherSignature(iface, name, argc) + ";");
                foreach (var method in _operators) sb.AppendLine($"{HelperStorage}Value {OperatorWrapper(method)}(Value a, Value b, OwnList* list);");
                sb.AppendLine($"FIRE_MAYBE_UNUSED {HelperInline}Value aget_g(Value a, Value i, OwnList* list);");
                sb.AppendLine($"FIRE_MAYBE_UNUSED {HelperInline}void aset_g(Value a, Value i, Value v, OwnList* list);");
                sb.AppendLine();

                foreach (var selector in _overloadSelectors.Values) sb.AppendLine(SelectorSignature(selector) + ";");
                sb.Append(OverloadClassFunctions());
                sb.AppendLine();

                foreach (var code in fieldHelperCode) sb.AppendLine(code);
                foreach (var code in dispatcherCode) sb.AppendLine(code);
                foreach (var code in selectorCode) sb.AppendLine(code);
                if (_usesTakeEnumerate) sb.Append(EnumerateItemsFunction());
                foreach (var method in _operators)
                {
                    var ids = _classList.Where(c => c.Rc.FindMethodWithAccess(method, 1).Proto != null).Select(c => c.Id).ToList();
                    sb.AppendLine($"{HelperStorage}Value {OperatorWrapper(method)}(Value a, Value b, OwnList* list) {{");
                    if (ids.Count == 0) sb.AppendLine("    (void)list;   // (a library asked for it: no class of the program overloads it)");
                    if (ids.Count > 0)
                    {
                        sb.AppendLine("    if (a.kind == K_Class) {");
                        sb.AppendLine("        switch (asObj(a)->cls) {");
                        sb.AppendLine("            " + string.Concat(ids.Select(id => $"case {id}: ")) + $"{{ Value r = call_{Mangle(method)}_1(a, b{DispatchTail(method, 1, "list", "0xFFFFFFFFu")}); adopt(r, list); return r; }}");
                        sb.AppendLine("            default: break;");
                        sb.AppendLine("        }");
                        sb.AppendLine("    }");
                    }
                    sb.AppendLine($"    return {OperatorFallback[method]};");
                    sb.AppendLine("}");
                }
                foreach (var typeName in _isOfTypes)
                {
                    var ids = _classList.Where(c => ClassIs(c.Rc, typeName)).Select(c => c.Id).ToList();
                    sb.AppendLine($"FIRE_MAYBE_UNUSED {HelperStorage}bool isof_{Mangle(typeName)}(Value v) {{");
                    if (EnumIndexNamed(typeName) is var enumIndex and > 0) sb.AppendLine($"    if (v.kind == K_Int) return v.reserved == {enumIndex};   // (the enum {typeName})");
                    if (typeName == "IEnumerable") sb.AppendLine("    if (v.kind == K_Array || v.kind == K_Buffer) return true;");
                    sb.AppendLine("    if (v.kind != K_Class) return false;");
                    sb.AppendLine(ids.Count == 0 ? "    return false;" : "    switch (asObj(v)->cls) { " + string.Concat(ids.Select(id => $"case {id}: ")) + "return true; default: return false; }");
                    sb.AppendLine("}");
                }
            }
            if (_usesReflection)
            {
                foreach (var (path, name) in _selectorPaths) sb.AppendLine($"static const char* const {name}[] = {{{string.Join(", ", path.Select(CString))}}};");
                sb.AppendLine(ShareReflection(ReflectionCode()));
            }
            if (!Modular) sb.AppendLine(IndexHelpers());
            if (_usesJson) sb.AppendLine(JsonCode());
            sb.AppendLine(RuntimeHooks());

            foreach (var f in _funcs) sb.AppendLine(f.Code);
            sb.AppendLine(main.Code);
            // The entry point is the target configuration's: a process (`int main()`, the exit code is `terminate(n)`) or a function that the board's startup
            // code calls once the scheduler runs (`app_main` in ESP-IDF; there is no process to end).
            var entry = _target.Native.Entry;
            string externC = "";   // (C: the name of the entry point is not mangled)
            // what C++ did with static initializers: the tables of the runtime (units, resources, hooks), before anything runs
            // the enums of the program: their names and members (the text of a value, `is of`)
            if (_enumList.Count > 0)
            {
                for (int i = 0; i < _enumList.Count; i++)
                    sb.AppendLine($"static const FireEnumMember kEnumM{i}[] = {{{string.Join(", ", _enumList[i].Members.Select(m => $"{{{CString(m.Name)}, {(m.Value == long.MinValue ? "INT64_MIN" : m.Value.ToString(CultureInfo.InvariantCulture) + "LL")}}}").DefaultIfEmpty("{\"\", 0}"))}}};");
                sb.AppendLine($"static const FireEnum kEnums[] = {{{string.Join(", ", _enumList.Select((e, i) => $"{{{CString(e.Name)}, {(e.IsFlags ? 1 : 0)}, kEnumM{i}, {e.Members.Count}}}"))}}};");
                _initCode.AppendLine($"    g_enums = kEnums; g_enumCount = {_enumList.Count};");
            }
            if (DebugBuild) sb.Append(DebugProgramTables());
            sb.Append(SharedLibraryChecks());
            sb.AppendLine("static void fire_init(void) {");
            sb.Append(SharedLibraryCheckCalls());   // (linkage "shared": the libraries first)
            if (_usesThreads) sb.AppendLine("    fire_threadsInit();   // the locks of the threads, before anything else");
            sb.Append(_initCode);
            sb.AppendLine("}");
            if (entry.Kind == EntryKind.Function && _target.Native.Platform == "esp32")
            {
                // ESP-IDF calls app_main on its main task, whose stack is small (3.5 KB by default): the program runs on a task of its own with a stack that
                // FIRE_MAIN_STACK_BYTES chooses (bytes, like xTaskCreate of ESP-IDF counts them); app_main returns at once, the task ends with the program
                // (a debug build: frames without optimisation and the shadow stack need more)
                sb.AppendLine($"#ifndef FIRE_MAIN_STACK_BYTES\n#define FIRE_MAIN_STACK_BYTES {(DebugBuild ? 32768 : 16384)}\n#endif");
                if (DebugBuild)
                    sb.AppendLine("static void fire_mainTask(void* arg) {\n    (void)arg;\n    fire_init();\n    fire_dbgStart(&kDbgProgram);   // the debugger on the console UART: hello, then at most 3 s for its first command\n    fire_main();\n    fflush(stdout);\n"
                        + (_usesThreads ? "    fire_dbgExited(mainExitCode());\n" : "    fire_dbgExited(g_unwind.active ? 1 : 0);\n") + "    vTaskDelete(NULL);\n}");
                else
                    sb.AppendLine("static void fire_mainTask(void* arg) {\n    (void)arg;\n    fire_init();\n    fire_main();\n    fflush(stdout);\n    vTaskDelete(NULL);\n}");
                sb.AppendLine($"{externC}void {entry.Name}(void) {{");
                sb.AppendLine("    if (xTaskCreate(fire_mainTask, \"fire_main\", FIRE_MAIN_STACK_BYTES, NULL, 1, NULL) != pdPASS) { fire_init(); fire_main(); fflush(stdout); }   // (no memory for the task: on the main task)");
                sb.AppendLine("}");
            }
            else if (entry.Kind == EntryKind.Function)
            {
                sb.AppendLine($"{externC}void {entry.Name}(void) {{");
                sb.AppendLine("    fire_init();");
                if (DebugBuild) sb.AppendLine("    fire_dbgStart(&kDbgProgram);   // the debugger (FreeRTOS: framed over stdio)");
                sb.AppendLine("    fire_main();");
                sb.AppendLine("    fflush(stdout);");
                if (DebugBuild) sb.AppendLine(_usesThreads ? "    fire_dbgExited(mainExitCode());" : "    fire_dbgExited(g_unwind.active ? 1 : 0);");
                sb.AppendLine("}");
            }
            else if (DebugBuild)
            {
                sb.AppendLine($"{externC}int {entry.Name}(int argc, char** argv) {{");
                sb.AppendLine("    int code_;");
                sb.AppendLine("    fire_setArgs(argc, argv);   // (Environment.Args, ProgramPath)");
                sb.AppendLine("    fire_init();");
                sb.AppendLine("    fire_dbgStart(&kDbgProgram);   // FIRE_DEBUG: connect to the debugger and wait for it");
                sb.AppendLine("    fire_main();");
                sb.AppendLine("    fflush(stdout);");
                sb.AppendLine(_usesThreads ? "    code_ = mainExitCode();   // `terminate(n)` gives the exit code" : "    code_ = g_unwind.active ? 1 : 0;   // an exception that nothing caught");
                sb.AppendLine("    fire_dbgExited(code_);");
                sb.AppendLine("    return code_;");
                sb.AppendLine("}");
            }
            else
            {
                sb.AppendLine($"{externC}int {entry.Name}(int argc, char** argv) {{");
                sb.AppendLine("    fire_setArgs(argc, argv);   // (Environment.Args, ProgramPath)");
                sb.AppendLine("    fire_init();\n    fire_main();");
                sb.AppendLine("    fflush(stdout);");
                sb.AppendLine(_usesThreads ? "    return mainExitCode();   // `terminate(n)` gives the exit code" : _usesExceptions ? "    return g_unwind.active ? 1 : 0;   // an exception that nothing caught" : "    return 0;");
                sb.AppendLine("}");
            }
            return ResolveLineResets(sb.ToString());
        }

        /// <summary>Pulls in what dispatching needs: the method of every class for every called name, destructors, ToString (for
        /// text conversion), the index methods, and the enumerator class that `foreach` over an array creates.</summary>
        private void RegisterTargets()
        {
            if (LibraryRole) { RegisterServedTargets(); return; }   // (a library module keeps its own members; its dispatchers pull in those of the classes of other modules it serves)
            if (_classList.Count > 0 || _dispatchers.Count > 0)
                foreach (var cls in _classList.ToList())
                    if (cls.Rc.FindMethodWithAccess("ToString", 0).Proto is { } toString && toString.ParamCount == 0)
                        GetFunc(toString, FuncKind.Method);
            if (_usesExceptions && _program.Program.Classes.ContainsKey("IndexOutOfBoundsException"))
            {
                // run-time errors (index out of range) are thrown as this prelude class
                var indexError = RegisterClass("IndexOutOfBoundsException");
                if (indexError.Rc.FindConstructor(3) is { } indexCtor && indexCtor.ParamCount == 3) GetFunc(indexCtor, FuncKind.Ctor);
                var divideError = RegisterClass("DivideByZeroException");
                if (divideError.Rc.FindConstructor(1) is { } divideCtor && divideCtor.ParamCount == 1) GetFunc(divideCtor, FuncKind.Ctor);
                var destroyedError = RegisterClass("DestroyedException");
                if (destroyedError.Rc.FindConstructor(1) is { } destroyedCtor && destroyedCtor.ParamCount == 1) GetFunc(destroyedCtor, FuncKind.Ctor);
                var unitError = RegisterClass("UnitMismatchException");
                if (unitError.Rc.FindConstructor(3) is { } unitCtor && unitCtor.ParamCount == 3) GetFunc(unitCtor, FuncKind.Ctor);
                if (_usesReflection && _program.Program.Classes.ContainsKey("ReflectionException"))
                {
                    var reflectError = RegisterClass("ReflectionException");
                    if (reflectError.Rc.FindConstructor(1) is { } reflectCtor && reflectCtor.ParamCount == 1) GetFunc(reflectCtor, FuncKind.Ctor);
                }
                var accessError = RegisterClass("AccessDeniedException");
                if (accessError.Rc.FindConstructor(1) is { } accessCtor && accessCtor.ParamCount == 1) GetFunc(accessCtor, FuncKind.Ctor);
                // a lambda with another number of parameters than its declaration: an ArgumentException (checkLambda)
                if (!_errorClasses.Contains("ArgumentException")) _errorClasses.Add("ArgumentException");
                if (!_errorClasses.Contains("OverflowException")) _errorClasses.Add("OverflowException");   // (adaptUnit)
                if (!_errorClasses.Contains("MissingMemberException")) _errorClasses.Add("MissingMemberException");   // (missingMethod, missingField)
                foreach (var name in _errorClasses)
                    if (_program.Program.Classes.ContainsKey(name))
                    {
                        var errorClass = RegisterClass(name);
                        if (errorClass.Rc.FindConstructor(1) is { } errorCtor && errorCtor.ParamCount == 1) GetFunc(errorCtor, FuncKind.Ctor);
                    }
            }
            foreach (var field in Modular ? new List<string>() : _fieldNames.ToList())
                foreach (var cls in _classList.ToList())
                    if (cls.Rc.FieldIndex.ContainsKey(field) && cls.Rc.FindFieldRequiredUnit(field) is { } requiredUnit)
                    {
                        UnitId(DemandUnit(requiredUnit));   // the unit and its text for the check in the field setter
                        Constant(Value.MakeString(Unit.DemandDisplay(requiredUnit)));
                    }
            foreach (var field in Modular ? new List<string>() : _fieldNames.ToList())
                foreach (var cls in _classList.ToList())
                {
                    if (cls.Rc.FieldIndex.ContainsKey(field)) continue;
                    // (a static property is not a member of the object, see PropertyHelpers)
                    if (cls.Rc.FindMethodWithAccess("get_" + field, 0).Proto is { IsStatic: false } getter) GetFunc(getter, FuncKind.Method);
                    if (cls.Rc.FindMethodWithAccess("set_" + field, 1).Proto is { IsStatic: false } setter) GetFunc(setter, FuncKind.Method);
                }
            if (_usesTakeEnumerate && _classList.Any(c => ClassIs(c.Rc, "IEnumerable")))
            {
                _dispatchers.Add(("GetEnumerator", 0));
                _dispatchers.Add(("MoveNext", 0));
                _dispatchers.Add(("GetCurrent", 0));
            }
            if (AnyClassHasMethod("GetIndex", 1)) _dispatchers.Add(("GetIndex", 1));
            if (AnyClassHasMethod("SetIndex", 2)) _dispatchers.Add(("SetIndex", 2));
            if (_dispatchers.Contains(("GetEnumerator", 0)) && _program.Program.Classes.ContainsKey("ListEnumerator"))
            {
                var enumerator = RegisterClass("ListEnumerator");
                if (enumerator.Rc.FindConstructor(2) is { } ctor && ctor.ParamCount == 2) GetFunc(ctor, FuncKind.Ctor);
            }

            foreach (var (iface, name, argc) in _interfaceDispatchers.ToList())
                foreach (var (_, method) in InterfaceImplementations(iface, name, argc) ?? new())
                    GetFunc(method, FuncKind.Method);
            foreach (var (name, argc) in Modular ? new List<(string Name, int Argc)>() : _dispatchers.ToList())
            {
                foreach (var cls in _classList.ToList())
                {
                    var methods = MethodsFor(cls.Rc, name, argc);
                    bool overloaded = methods.Count > 1 && methods.Any(m => !m.IsStatic);
                    foreach (var method in methods)
                        if (!method.IsStatic)   // a static method of the same name (`Reflect.Set`) is not what a call on an object means
                        {
                            GetFunc(method, FuncKind.Method);
                            DefaultArgs(method, argc, "self", "list");   // registers the functions of the default values
                        }
                        else if (overloaded)
                        {
                            // (unless it is one of several overloads the values decide between)
                            GetFunc(method, FuncKind.Static);
                            DefaultArgs(method, argc, "Undef()", "list");
                        }
                }
                foreach (var (_, _, extensionRc) in ExtensionMethods(name, argc))
                    foreach (var method in MethodsFor(extensionRc, name, argc))
                    {
                        GetFunc(method, FuncKind.Method);
                        DefaultArgs(method, argc, "self", "list");
                    }
            }
            RegisterReflectionTargets();
            RegisterKeptTargets();
            RegisterJsonTargets();
            if (Modular) RegisterServedTargets();
            foreach (var cls in _classList.ToList())
                for (var rc = cls.Rc; rc != null; rc = rc.Base)
                    if (rc.Destructor != null) GetFunc(rc.Destructor, FuncKind.Dtor);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Helpers generated after the fixpoint (their content depends on all classes of the program)
        // -------------------------------------------------------------------------------------------------------------
        /// <summary>The values for the parameters beyond the `argc` that a call supplies (SPEC 5.2): each default value is a function of its own
        /// (it sees `this` of the callee, `self`); the fresh value belongs to the caller's scope `list`. `Pre` evaluates them in order into locals,
        /// `Args` passes them on. Both are empty when nothing is missing.</summary>
        private (string Pre, string Args) DefaultArgs(FunctionProto proto, int argc, string self, string list)
        {
            if (argc >= proto.ParamCount) return ("", "");
            var pre = new StringBuilder();
            var args = new StringBuilder();
            for (int i = argc; i < proto.ParamCount; i++)
            {
                var defaultProto = i < proto.ParamDefaults.Count ? proto.ParamDefaults[i] : null;
                if (defaultProto == null) throw new NativeNotSupportedException($"call with {argc} argument(s) of a function with {proto.ParamCount} parameters");
                pre.Append($"Value dv{i} = adoptV({GetFunc(defaultProto, FuncKind.Init).Name}({self}), {list}); ");
                args.Append($", dv{i}");
            }
            return (pre.ToString(), args.ToString());
        }

        /// <summary>Does a method call of this name with this many arguments reach an implementation that fills in default values? Then its dispatcher gets the scope list.</summary>
        private bool DispatchNeedsDefaults(string name, int argc) =>
            _program.Program.Classes.Values.Any(rc => MethodsFor(rc, name, argc).Any(m => m.ParamCount != argc))
            || ExtensionMethods(name, argc).Any(e => MethodsFor(e.Rc, name, argc).Any(m => m.ParamCount != argc));

        /// <summary>The conversions of the base types that a method call reaches when no class defines the method (SPEC 8.10): per kind of receiver the C++ expression.</summary>
        private static IEnumerable<(string Kind, string Expr)> BuiltinMethods(string name, int argc)
        {
            switch ((name, argc))
            {
                case ("ToBytes", 0): yield return ("K_String", "strToBytes(self, list)"); break;
                case ("ToUnicode", 1): yield return ("K_String", "strToUnicode(self, a0, list)"); yield return ("K_Char", "charToUnicode(self, a0, list)"); yield return ("K_Buffer", "retainedV(bufToUnicode(self, a0, list))"); break;
                case ("ToUnicode", 0): yield return ("K_Buffer", "retainedV(bufToUnicode(self, Int(2), list))"); break;
                case ("ToByte", 0): yield return ("K_Char", "charToByte(self)"); break;
                case ("ToChar", 0): yield return ("K_Int", "byteToChar(self)"); break;
                // (a string made in the pool goes to the caller with a count of its own: retainedV; an enum value gives its name)
                case ("ToString", 0): yield return ("K_Buffer", "retainedV(bufToString(self, list))"); yield return ("K_Int", "retainedV(toStringValue(self, list))"); break;
                case ("HasFlag", 1): yield return ("K_Int", "enumHasFlag(self, a0)"); break;
                case ("ToUnicodeChar", 0): yield return ("K_Buffer", "bufToUnicodeChar(self, Int(2))"); break;
                case ("ToUnicodeChar", 1): yield return ("K_Buffer", "bufToUnicodeChar(self, a0)"); break;
                case ("ToLittleEndian", 0): yield return ("K_Buffer", "bufToEndian(self, true, list)"); break;
                case ("ToBigEndian", 0): yield return ("K_Buffer", "bufToEndian(self, false, list)"); break;
            }
        }

        private bool DispatchNeedsList(string name, int argc) => Modular ||
            name == "GetEnumerator" && argc == 0 || OwnMethodId(name, argc) != null || DispatchNeedsDefaults(name, argc) || BuiltinMethods(name, argc).Any();

        /// <summary>The arguments after the operands of a dispatcher call (see <see cref="DispatcherSignature"/>).</summary>
        private string DispatchTail(string name, int argc, string list, string caller) =>
            (DispatchNeedsList(name, argc) ? ", " + list : "") + (DispatchHasCaller(name) ? ", " + caller : "");

        /// <summary>Does the dispatcher of the name take the caller's class (a restricted name; with modules every dispatcher does - one signature for all).</summary>
        private bool DispatchHasCaller(string name) => Modular || RestrictedMethodName(name);
        /// <summary>Do the field helpers of the name take the scope list (a property of that name somewhere) and the caller's class (a restricted name)? With modules always.</summary>
        private bool FieldHasList(string name) => Modular || HasProperty(name);
        private bool FieldHasCaller(string name) => Modular || RestrictedField(name);
        /// <summary>`static inline ` for a small helper (every module has its own, ROADMAP 9 B2).</summary>
        private string HelperInline => "static inline ";

        private string DispatcherSignature(string name, int argc, string prefix = "call_")
        {
            var parameters = new List<string> { "Value self" };
            parameters.AddRange(Enumerable.Range(0, argc).Select(i => $"Value a{i}"));
            if (DispatchNeedsList(name, argc)) parameters.Add("OwnList* list");
            if (DispatchHasCaller(name)) parameters.Add("uint32_t caller");
            return $"FIRE_MAYBE_UNUSED {(prefix == "call_" ? HelperStorage : "static ")}Value {CallFn(name, argc, prefix)}(" + string.Join(", ", parameters) + ")";   // (direct calls can leave a dispatcher unused)
        }

        /// <summary>The program has actors: a call of a method on one is a message (SPEC / THREADING_DESIGN 2). The dispatcher that runs the method is then
        /// `direct_...`, and `call_...` queues a message for an actor and calls `direct_...` for everything else; the message runs `direct_...` later.</summary>
        private bool WrapDispatchers => _usesActors || _usesThreads;

        private string ActorWrapper(string name, int argc)
        {
            string direct = CallFn(name, argc, "direct_"), msg = CallFn(name, argc, "msg_");
            var sb = new StringBuilder();
            string argList = string.Concat(Enumerable.Range(0, argc).Select(i => $", a{i}"));
            string tail = (DispatchNeedsList(name, argc) ? ", list" : "") + (DispatchHasCaller(name) ? ", caller" : "");
            sb.AppendLine(DispatcherSignature(name, argc));
            sb.AppendLine("{");
            if (_usesActors)
            {
                sb.AppendLine("    if (self.kind == K_Class && FIRE_LIKELY(leafAlive(self)) && (asObj(self)->h.flags & 8)) {");
                sb.AppendLine($"        Value av[{Math.Max(1, argc)}] = {{{string.Join(", ", Enumerable.Range(0, argc).Select(i => $"a{i}").DefaultIfEmpty("Undef()"))}}};");
                sb.AppendLine($"        actorSend(self, {msg}, av, {argc});");
                sb.AppendLine("        return Undef();");
                sb.AppendLine("    }");
            }
            if (_usesThreads)
            {
                // a call on an object of the shared domain is one section
                sb.AppendLine("    bool ss_ = sectionScopeEnterV(self);");
                sb.AppendLine($"    Value r_ = {direct}(self{argList}{tail});");
                sb.AppendLine("    sectionScopeExit(ss_);");
                sb.AppendLine("    return r_;");
            }
            else sb.AppendLine($"    return {direct}(self{argList}{tail});");
            sb.AppendLine("}");
            if (!_usesActors) return sb.ToString();
            string msgArgs = string.Concat(Enumerable.Range(0, argc).Select(i => $", a[{i}]")) + (DispatchNeedsList(name, argc) ? ", list" : "") + (DispatchHasCaller(name) ? ", asObj(self)->cls" : "");
            sb.AppendLine($"static Value {msg}(Value self, const Value* a, OwnList* list) {{ (void)a; (void)list; return {direct}(self{msgArgs}); }}");
            return sb.ToString();
        }

        /// <summary>A method call: picks the implementation by the class of the receiver - or, for a string, number, array and so on,
        /// by the extension of its base type.</summary>
        private static bool HasMethodNamed(RuntimeClass rc, string name)
        {
            for (var c = rc; c != null; c = c.Base)
                if (c.Methods.ContainsKey(name)) return true;
            return false;
        }

        /// <summary>The text of the VM for a call that finds no method (VM.DescribeMethodNotFound).</summary>
        private static string DescribeMethodNotFound(RuntimeClass rc, string methodName, int argCount)
        {
            var arities = new List<int>();
            for (var c = rc; c != null; c = c.Base)
                if (c.Methods.TryGetValue(methodName, out var overloads)) arities.AddRange(overloads.Select(p => p.ParamCount));
            if (arities.Count == 0) return $"Method '{methodName}' not found on '{rc.Name}'.";
            return $"Method '{methodName}' on '{rc.Name}' has no overload with {argCount} argument(s) (available: {string.Join(", ", arities.Distinct().OrderBy(x => x))} argument(s)).";
        }

        /// <summary>The text of the VM for a `new` that finds no constructor (VM.DescribeConstructorNotFound).</summary>
        private static string DescribeConstructorNotFound(RuntimeClass rc, int argCount) =>
            $"Class '{rc.Name}' has no constructor with {argCount} argument(s) (available: {string.Join(", ", rc.Constructors.Keys.OrderBy(x => x))} argument(s)).";

        private static string InterfaceDispatcherName(string iface, string name, int argc) => $"icall_{Mangle(iface)}_{Mangle(name)}_{argc}";
        private static string InterfaceDispatcherSignature(string iface, string name, int argc) =>
            $"FIRE_MAYBE_UNUSED static Value {InterfaceDispatcherName(iface, name, argc)}(Value self{string.Concat(Enumerable.Range(0, argc).Select(i => $", Value a{i}"))})";

        /// <summary>The classes of the program that implement <paramref name="iface"/>, each with its method `name(argc)` - when every one has exactly one,
        /// public, not static and without default values (then a call needs no access check and no scoring); null otherwise.</summary>
        private List<(ClassInfo Class, FunctionProto Proto)>? InterfaceImplementations(string iface, string name, int argc)
        {
            var result = new List<(ClassInfo, FunctionProto)>();
            foreach (var c in _classList.Where(c => ClassIs(c.Rc, iface)))
            {
                if (c.Rc.IsTypeOverloaded(name, argc)) return null;
                var found = c.Rc.FindMethodWithAccess(name, argc);
                if (found.Proto is not { IsStatic: false } m || found.Access != AccessModifier.Public || m.ParamCount != argc) return null;
                result.Add((c, m));
            }
            return result.Count > 0 ? result : null;
        }

        /// <summary>Is a dispatcher of the interface better than the one of the name: the name is private or protected somewhere (the general one takes the class
        /// of the caller and checks it), or a class outside the interface has the method too (the general one switches over it as well). Not for a name that
        /// means something for other values too (a built-in method, a base-type extension: the general dispatcher knows what undefined does with it).</summary>
        private bool InterfaceDispatchWorthwhile(string iface, string name, int argc) => !LibraryRole &&
            !DispatchNeedsList(name, argc) && !ExtensionMethods(name, argc).Any() && InterfaceImplementations(iface, name, argc) != null
            && (RestrictedMethodName(name) || _classList.Any(c => !ClassIs(c.Rc, iface) && c.Rc.FindMethodWithAccess(name, argc).Proto != null));

        /// <summary>`icall_I_name_argc`: the method of the object's class for the classes that implement the interface. The type is that of a value that passed
        /// the guard of a declaration with the interface (InterfaceType): an object of one of these classes or undefined (and a destroyed object, which reports
        /// itself) - like DirectMethod for a class, nothing else is looked for. (Not the general dispatcher as a fallback: it would be a second caller of each
        /// method, which then no longer goes inline into a call site that is its only one.)</summary>
        private string InterfaceDispatcher(string iface, string name, int argc)
        {
            // (an entry of an earlier round of the fixpoint that no call site uses any more: no cases)
            var impls = InterfaceImplementations(iface, name, argc) ?? new List<(ClassInfo Class, FunctionProto Proto)>();
            string args = string.Concat(Enumerable.Range(0, argc).Select(i => $", a{i}"));
            var sb = new StringBuilder();
            sb.AppendLine(InterfaceDispatcherSignature(iface, name, argc));
            sb.AppendLine("{");
            if (impls.Count == 0 && argc > 0) sb.AppendLine("    " + string.Concat(Enumerable.Range(0, argc).Select(i => $"(void)a{i}; ")).TrimEnd());
            sb.AppendLine("    if (FIRE_LIKELY(self.kind == K_Class)) {");
            sb.AppendLine("        if (FIRE_UNLIKELY(!leafAlive(self))) return destroyedError(self);");
            sb.AppendLine("        switch (asObj(self)->cls) {");
            foreach (var group in impls.GroupBy(t => t.Proto))
                sb.AppendLine($"            {string.Concat(group.Select(t => $"case {t.Class.Id}: "))}return {_funcByProto[group.Key].Name}(self{args});");
            sb.AppendLine("            default: break;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine($"    return missingMethod(self, {CString(name)}, {argc});");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string Dispatcher(string name, int argc)
        {
            var byFunc = new Dictionary<Func, List<int>>();
            // classes where the types of the arguments decide between overloads: grouped by their candidates
            var overloaded = new List<(FunctionProto[] Protos, RuntimeClass[] Declaring, List<ClassInfo> Classes)>();
            foreach (var cls in _classList)
                if (cls.Rc.IsTypeOverloaded(name, argc) && cls.Rc.Candidates(name, argc) is var set && set.Protos.Any(p => !p.IsStatic))
                {
                    int group = overloaded.FindIndex(o => o.Protos.SequenceEqual(set.Protos) && o.Declaring.SequenceEqual(set.Declaring));
                    if (group < 0) overloaded.Add((set.Protos, set.Declaring, new List<ClassInfo> { cls }));
                    else overloaded[group].Classes.Add(cls);
                }
                else if (cls.Rc.FindMethodWithAccess(name, argc).Proto is { IsStatic: false } method)
                {
                    var f = _funcByProto[method];
                    if (!byFunc.TryGetValue(f, out var ids)) byFunc[f] = ids = new List<int>();
                    ids.Add(cls.Id);
                }
            var extensions = ExtensionMethods(name, argc).ToList();
            bool enumeratorOfArray = name == "GetEnumerator" && argc == 0 && _classes.ContainsKey("ListEnumerator");
            var builtins = BuiltinMethods(name, argc).ToList();
            // no class has the method (e.g. `command.Execute(device)` of the devices library when the program defines no command): calling it is an error at run time, like in the VM

            // a call site that passes addresses: an implementation without `ref` for that position gets the value
            bool derefs = _refCallSites.Contains((name, argc));
            // a static overload the values pick on an object (the VM runs it, without using the object)
            string StaticImpl(FunctionProto proto)
            {
                var (pre, defaults) = DefaultArgs(proto, argc, "Undef()", "list");
                return $"{{ {pre}return {_funcByProto[proto].Name}({(ArgsFor(proto) + defaults).TrimStart(',', ' ')}); }}";
            }
            string ScoredArg(int i) => derefs ? $"derefArg(a{i})" : $"a{i}";   // (an address is scored by the value it points to)
            string ArgsFor(FunctionProto proto) => string.Concat(Enumerable.Range(0, argc).Select(i => derefs && (proto.RefMask >> i & 1) == 0 ? $", derefArg(a{i})" : $", a{i}"));
            // an implementation with default values: the missing arguments are evaluated first (in order), then it is called
            bool restricted = RestrictedMethodName(name);
            string CallImpl(string target, FunctionProto proto, RuntimeClass? sample = null, bool extension = false, RuntimeClass? declaringOverload = null)
            {
                var (pre, defaults) = DefaultArgs(proto, argc, "self", "list");
                string access = "";
                var found = declaringOverload != null ? (Proto: proto, DeclaringClass: declaringOverload, Access: proto.Access ?? AccessModifier.Public)
                    : sample?.FindMethodWithAccess(name, argc) ?? default;
                if (restricted && sample != null && found.Proto != null && found.Access != AccessModifier.Public)
                {
                    string cond = AllowedExpr(found.DeclaringClass!, found.Access);
                    string message = extension
                        ? $"Method '{name}' of the extension of '{sample.Name.Substring(1)}' is {DescribeAccess(found.Access)} and cannot be called from here."
                        : $"Method '{name}' of '{found.DeclaringClass!.Name}' is {DescribeAccess(found.Access)} and cannot be called from here.";
                    access = $"if (!{cond}) return accessDenied({CString(message)}); ";
                }
                return pre.Length == 0 ? $"{{ {access}return {target}(self{ArgsFor(proto)}); }}" : $"{{ {access}{pre}return {target}(self{ArgsFor(proto)}{defaults}); }}";
            }
            var sb = new StringBuilder();
            sb.AppendLine(DispatcherSignature(name, argc, WrapDispatchers ? "direct_" : "call_"));
            sb.AppendLine("{");
            if (DispatchNeedsList(name, argc)) sb.AppendLine("    (void)list;   // (a method that only works on numbers or text allocates nothing)");
            if (Modular) sb.AppendLine("    (void)caller;");
            sb.AppendLine("    switch (self.kind) {");
            foreach (var (cpp, proto, extensionRc) in extensions)
            {
                if (extensionRc.IsTypeOverloaded(name, argc))
                {
                    // a base-type extension with overloads by type: the values decide, like for objects
                    var set = extensionRc.Candidates(name, argc);
                    sb.AppendLine($"        case {cpp}: {{");
                    sb.Append(OverloadChoice(set.Protos, argc, "NULL", c => CallImpl(_funcByProto[set.Protos[c]].Name, set.Protos[c], extensionRc, true, set.Declaring[c]), "            ", ScoredArg));
                    sb.AppendLine("        }");
                }
                else sb.AppendLine($"        case {cpp}: {CallImpl(_funcByProto[proto].Name, proto, extensionRc, true)}");
            }
            foreach (var (kind, expr) in builtins)
                if (!extensions.Any(e => e.Cpp == kind)) sb.AppendLine($"        case {kind}: return {expr};");
            if (enumeratorOfArray)
            {
                var info = _classes["ListEnumerator"];
                var ctor = _funcByProto[info.Rc.FindConstructor(2)!];
                sb.AppendLine("        case K_Array: case K_Buffer: case K_String: {");   // (a string: the enumerator indexes it, `text[i]` is a char)
                sb.AppendLine("            bool ok;");
                sb.AppendLine("            Value count = lengthOf(self, &ok);");
                sb.AppendLine($"            Value e = newObject({info.Id}, {info.Fields.Count}, list);");
                sb.AppendLine($"            {Nested($"{ctor.Name}(e, self, count);")}");   // (the VM makes it nested)
                sb.AppendLine("            return e;");
                sb.AppendLine("        }");
            }
            if (byFunc.Count > 0 || overloaded.Count > 0)
            {
                sb.AppendLine("        case K_Class:");
                sb.AppendLine("            if (FIRE_UNLIKELY(!leafAlive(self))) return destroyedError(self);");
                if (overloaded.Count == 0 && byFunc.Count == 1 && byFunc.Values.First().Count == _classList.Count)
                    sb.AppendLine($"            asObj(self); {CallImpl(byFunc.Keys.First().Name, byFunc.Keys.First().Proto!, _classList.First().Rc)}");
                else
                {
                    sb.AppendLine("            switch (asObj(self)->cls) {");
                    foreach (var (f, ids) in byFunc)
                    {
                        foreach (int id in ids) sb.AppendLine($"                case {id}:");
                        sb.AppendLine($"                    {CallImpl(f.Name, f.Proto!, _classList.First(c => c.Id == ids[0]).Rc)}");
                    }
                    foreach (var (protos, declaring, classes) in overloaded)
                    {
                        foreach (var cls in classes) sb.AppendLine($"                case {cls.Id}:");
                        sb.AppendLine("                {");
                        var sample = classes[0].Rc;
                        sb.Append(OverloadChoice(protos, argc, "objAt(self)", c => protos[c].IsStatic ? StaticImpl(protos[c]) : CallImpl(_funcByProto[protos[c]].Name, protos[c], sample, false, declaring[c]), "                    ", ScoredArg));
                        sb.AppendLine("                }");
                    }
                    sb.AppendLine("                default: break;");
                    sb.AppendLine("            }");
                    sb.AppendLine("            break;");
                }
            }
            sb.AppendLine("        default: break;");
            sb.AppendLine("    }");
            if (OwnMethodId(name, argc) is { } ownId)
            {
                // the built-in ownership method for everything that has no method of this name
                int baseArgs = OwnMethodBaseArgs(ownId);
                string target = baseArgs == 1 ? "a0" : "Undef()";
                if (argc == baseArgs) sb.AppendLine($"    ownMethod({ownId}, self, {target}, list);");
                else sb.AppendLine($"    ownMethodT({ownId}, self, {target}, a{baseArgs}, list, g_scopeLists, g_scopeCount);   // (the call site with a `Takes` mode left the scopes of the caller in g_scopeLists)");
                sb.AppendLine("    return Undef();");
            }
            else
            {
                if (byFunc.Count == 0 && extensions.Count == 0 && builtins.Count == 0 && !enumeratorOfArray && argc > 0)
                    sb.AppendLine("    " + string.Concat(Enumerable.Range(0, argc).Select(i => $"(void)a{i}; ")).TrimEnd());   // no class has the method: nothing uses the arguments
                // a class that has the method with other numbers of arguments: an ArgumentException, otherwise a MissingMemberException (like the VM)
                var otherArity = _classList.Where(c => HasMethodNamed(c.Rc, name) && c.Rc.FindMethodWithAccess(name, argc).Proto == null).ToList();
                if (otherArity.Count > 0)
                {
                    sb.AppendLine("    if (self.kind == K_Class) switch (asObj(self)->cls) {");
                    foreach (var c in otherArity)
                        sb.AppendLine($"        case {c.Id}: return fireError(\"ArgumentException\", {CString(DescribeMethodNotFound(c.Rc, name, argc))});");
                    sb.AppendLine("        default: break;");
                    sb.AppendLine("    }");
                }
                sb.AppendLine($"    return missingMethod(self, {CString(name)}, {argc});");
            }
            sb.AppendLine("}");
            if (WrapDispatchers) sb.Append(ActorWrapper(name, argc));
            return sb.ToString();
        }

        /// <summary>`Takes.Children` of an object that implements IEnumerable takes its items (SPEC 2.2): this runs the enumerator (GetEnumerator, MoveNext, GetCurrent) and hands each item to the runtime.</summary>
        private string EnumerateItemsFunction()
        {
            var ids = _classList.Where(c => ClassIs(c.Rc, "IEnumerable")).Select(c => c.Id).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("static bool fire_enumerateItems(Value obj, OwnList* list, void (*sink)(Value, void*), void* ctx) {");
            if (ids.Count == 0) sb.AppendLine("    (void)obj; (void)list; (void)sink; (void)ctx;\n    return false;\n}");
            else
            {
                string unwound = _usesExceptions ? "    if (FIRE_UNLIKELY(g_unwind.active)) return false;\n" : "";
                sb.AppendLine("    if (obj.kind != K_Class || !leafAlive(obj)) return false;");
                if (Modular) sb.AppendLine("    if (!fireIsOf(asObj(obj)->cls, \"IEnumerable\")) return false;");
                else sb.AppendLine("    switch (asObj(obj)->cls) { " + string.Concat(ids.Select(id => $"case {id}: ")) + "break; default: return false; }");
                sb.AppendLine($"    Value e = call_GetEnumerator_0(obj{DispatchTail("GetEnumerator", 0, "list", "0xFFFFFFFFu")});");
                sb.AppendLine("    adopt(e, list);");
                sb.Append(unwound);
                sb.AppendLine("    for (;;) {");
                sb.AppendLine($"        Value more = call_MoveNext_0(e{DispatchTail("MoveNext", 0, "list", "0xFFFFFFFFu")});");
                sb.Append(unwound.Replace("    if", "        if"));
                sb.AppendLine("        if (!(more.kind == K_Bool && more.i)) break;");
                sb.AppendLine($"        Value cur = call_GetCurrent_0(e{DispatchTail("GetCurrent", 0, "list", "0xFFFFFFFFu")});");
                sb.AppendLine("        adopt(cur, list);");
                sb.Append(unwound.Replace("    if", "        if"));
                sb.AppendLine("        sink(cur, ctx);");
                sb.AppendLine("    }");
                sb.AppendLine("    return true;");
                sb.AppendLine("}");
            }
            _initCode.AppendLine("    g_enumerateItems = fire_enumerateItems;");
            return sb.ToString();
        }

        /// <summary>The arms of a switch over class ids, one per line: classes with the same code share one arm (`case 3: case 9: code`), in the order of their
        /// first class. Every code ends the arm itself (return, break, a call that does not return).</summary>
        private static string MergedCases(IEnumerable<(int Id, string Code)> arms, string indent = "        ")
        {
            var sb = new StringBuilder();
            foreach (var group in arms.GroupBy(a => a.Code, StringComparer.Ordinal))
                sb.Append(indent).Append(string.Concat(group.Select(a => $"case {a.Id}: "))).Append(group.Key).Append('\n');
            return sb.ToString();
        }

        /// <summary>Read and write access to the field `name`: the index of a field is the same in a class and all its subclasses,
        /// but not between unrelated classes that happen to use the same name. `Length`/`length` also work on strings, arrays and buffers.</summary>
        private string FieldHelpers(string name)
        {
            string text = FieldHelpersCore(name);
            return _usesProbes ? WrapProbeSetter(name, text) : text;
        }

        private string FieldHelpersCore(string name)
        {
            var indexByClass = new Dictionary<int, int>();
            foreach (var cls in _classList)
                if (cls.Rc.FieldIndex.TryGetValue(name, out int index)) indexByClass[cls.Id] = index;
            if (Modular || HasProperty(name) || RestrictedField(name)) return PropertyHelpers(name, indexByClass);
            bool isLength = name is "Length" or "length";
            string m = Mangle(name);
            string lengthCode = isLength ? "    { bool ok; Value n = lengthOf(v, &ok); if (ok) return n; }\n"
                : name == "littleEndian" ? "    if (v.kind == K_Buffer) return Bool(bufOf(v)->little != 0);\n" : "";
            var sb = new StringBuilder();
            if (indexByClass.Count == 0)
            {
                // no object of the program has such a field: only a built-in member (or dead code) can get here
                sb.AppendLine($"static inline Value gf_{m}(Value v) {{\n{lengthCode}    return missingField(v, {CString(name)});\n}}");
                sb.AppendLine($"static inline void sf_{m}(Value v, Value x) {{ (void)x; if (v.kind != K_Class) {{ notObjectAssign(v); return; }} fatal(\"Field '{name}' cannot be assigned.\"); }}");
                if (_fieldAddressTaken.Contains(name)) sb.AppendLine($"static inline Value fp_{m}(Value v) {{ (void)v; fatal(\"Field '{name}' not found on this object.\"); }}");
                return sb.ToString();
            }

            string indexCode;
            if (indexByClass.Count == _classList.Count && indexByClass.Values.Distinct().Count() == 1)
                indexCode = $"    const uint32_t idx = {indexByClass.Values.First()};";
            else
            {
                var cases = new StringBuilder();
                foreach (var group in indexByClass.GroupBy(kv => kv.Value))
                {
                    foreach (var kv in group) cases.Append($"        case {kv.Key}:\n");
                    cases.Append($"            idx = {group.Key}; break;\n");
                }
                indexCode = "    uint32_t idx = 0;\n    switch (o->cls) {\n" + cases + $"        default: missingField(v, {CString(name)}); FIELD_MISSING_RETURN\n    }}";
            }
            // a field declared with a unit (`int len : mm`) checks every assignment
            // and a field declared int/float/bool takes that kind or undefined (DeclaredKind)
            var unitArms = new List<(int, string)>();
            foreach (var cls in _classList)
            {
                if (!indexByClass.ContainsKey(cls.Id)) continue;
                string check = (cls.Rc.FindFieldRequiredUnit(name) is { } requiredUnit ? $"x = {AdaptUnitCall("x", requiredUnit)}; " : "")
                    + (cls.Rc.FindFieldRequiredKind(name) is var kind and not 0 ? FieldKindCheck(name, kind) + " " : "")
                    + (cls.Rc.FindFieldRequiredType(name) is { } requiredType ? ClassGuard("x", requiredType, $"Field '{name}'", "o") + " " : "");
                if (check.Length > 0) unitArms.Add((cls.Id, check + "break;"));
            }
            string unitCode = unitArms.Count == 0 ? "" : "    switch (o->cls) {\n" + MergedCases(unitArms) + "        default: break;\n    }\n" + (_usesExceptions ? "    if (FIRE_UNLIKELY(g_unwind.active)) return;\n" : "");
            // (a value that is not an object, or an object without the field: a MissingMemberException, like the VM)
            string valueIndex = indexCode.Replace("FIELD_MISSING_RETURN", "return Undef();"), voidIndex = indexCode.Replace("FIELD_MISSING_RETURN", "return;");
            string notObject = $"    if (FIRE_UNLIKELY(v.kind != K_Class)) return missingField(v, {CString(name)});\n";
            return $"static inline Value gf_{m}(Value v) {{\n{lengthCode}{notObject}    if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v);\n    Obj* o = asObj(v);\n{valueIndex}\n    return objFields(o)[idx];\n}}\n"
                 + (_usesThreads ? $"static inline void sfb_{m}(Value v, Value x);\nstatic inline void sf_{m}(Value v, Value x) {{ bool ss_ = sectionScopeEnterV(v); sfb_{m}(v, x); sectionScopeExit(ss_); }}\n" : "")   // (a change of an object of the shared domain is one section)
                 + $"static inline void {(_usesThreads ? "sfb_" : "sf_")}{m}(Value v, Value x) {{\n    if (FIRE_UNLIKELY(v.kind != K_Class)) {{ notObjectAssign(v); return; }}\n    if (FIRE_UNLIKELY(!leafAlive(v))) {{ destroyedError(v); return; }}\n    Obj* o = asObj(v);\n{voidIndex}\n{unitCode}    Value old = objFields(o)[idx];\n    objFields(o)[idx] = x;\n    retain(x);\n    release(old);\n}}\n"
                 + (!_fieldAddressTaken.Contains(name) ? "" : $"static inline Value fp_{m}(Value v) {{\n    if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v);\n    Obj* o = asObj(v);\n{valueIndex}\n    return PtrV(&objFields(o)[idx]);\n}}\n");
        }

        /// <summary>The check of a field declared int/float/bool in its setter (DeclaredKind, fire_rt guardKind).</summary>
        private string FieldKindCheck(string name, byte kind) => $"x = {GuardKindCall("x", kind, $"Field '{name}'")};";
        private static string KindConstant(byte declaredKind) => DeclaredKind.TagOf(declaredKind) switch { TypeTag.Int => "K_Int", TypeTag.Float => "K_Float", _ => "K_Bool" };
        private string GuardKindCall(string value, byte declared, string what) =>
            $"guardKind({value}, {KindConstant(declared)}, {(DeclaredKind.IsUnitless(declared) ? "true" : "false")}, {CString(what)}, {Constant(Value.MakeString(Unit.DemandDisplay("unitless")))})";
        private static byte KindOfDeclared(byte declared) => DeclaredKind.TagOf(declared) switch { TypeTag.Int => KindInt, TypeTag.Float => KindFloat, _ => KindBool };

        /// <summary>Does some class of the program declare a property of this name (`get_name`/`set_name`)? Then reading and writing it takes the scope list
        /// of the caller (the getter's result belongs to it).</summary>
        private bool HasProperty(string name) =>
            _program.Program.Classes.Values.Any(rc => rc.Methods.ContainsKey("get_" + name) || rc.Methods.ContainsKey("set_" + name));

        /// <summary>Access to `name` where some class has a property of that name (SPEC 8.8): a real field of the class wins, otherwise the getter/setter
        /// is called - decided per class, like the VM does by name at run time.</summary>
        /// <summary>The arms of the field helpers of `name` for these classes (by their numbers): read, write (the checks of the declaration, access) and the
        /// address - a field of the class, otherwise its property.</summary>
        private (List<(int, string)> Get, List<(int, string)> Set, List<(int, string)> Ptr) FieldArms(string name, IEnumerable<ClassInfo> classes)
        {
            var getArms = new List<(int, string)>();
            var setArms = new List<(int, string)>();
            var ptrArms = new List<(int, string)>();
            foreach (var cls in classes)
            {
                int label = cls.Id;
                if (cls.Rc.FieldIndex.TryGetValue(name, out int index))
                {
                    string fieldGuard = "", fieldGuardSet = "";
                    if (CheckAccess && cls.Rc.FindFieldAccess(name) is { } fieldAccess && fieldAccess.Access != AccessModifier.Public)
                    {
                        string text = CString($"Field '{name}' of '{fieldAccess.DeclaringClass.Name}' is {DescribeAccess(fieldAccess.Access)} and cannot be accessed from here.");
                        string cond = AllowedExpr(fieldAccess.DeclaringClass, fieldAccess.Access);
                        fieldGuard = $"if (!{cond}) return accessDenied({text}); ";
                        fieldGuardSet = $"if (!{cond}) {{ accessDenied({text}); return; }} ";
                    }
                    getArms.Add((label, $"{{ {fieldGuard}return objFields(o)[{index}]; }}"));
                    string unit = cls.Rc.FindFieldRequiredUnit(name) is { } requiredUnit
                        ? $"x = {AdaptUnitCall("x", requiredUnit)}; " + (_usesExceptions ? "if (FIRE_UNLIKELY(g_unwind.active)) return; " : "")
                        : "";
                    if (cls.Rc.FindFieldRequiredKind(name) is var kind and not 0)
                        unit += FieldKindCheck(name, kind) + (_usesExceptions ? " if (FIRE_UNLIKELY(g_unwind.active)) return; " : " ");
                    if (cls.Rc.FindFieldRequiredType(name) is { } requiredType)
                        unit += ClassGuard("x", requiredType, $"Field '{name}'", "o") + (_usesExceptions ? " if (FIRE_UNLIKELY(g_unwind.active)) return; " : " ");
                    setArms.Add((label, $"{{ {fieldGuardSet}{unit}Value old = objFields(o)[{index}]; objFields(o)[{index}] = x; retain(x); release(old); return; }}"));
                    ptrArms.Add((label, $"return PtrV(&objFields(o)[{index}]);"));
                    continue;
                }
                var (getter, getterDecl, getterAccess) = cls.Rc.FindMethodWithAccess("get_" + name, 0);
                var (setter, setterDecl, setterAccess) = cls.Rc.FindMethodWithAccess("set_" + name, 1);
                if (getter is { IsStatic: true }) getter = null;   // a static property is not a member of the object
                if (setter is { IsStatic: true }) setter = null;
                if (getter == null && setter == null) continue;
                string AccessorGuard(string method, RuntimeClass decl, AccessModifier access, bool isSetter)
                {
                    if (!CheckAccess || access == AccessModifier.Public) return "";
                    string text = CString($"'{method}' of '{decl.Name}' is {DescribeAccess(access)} and cannot be accessed from here.");
                    string cond = AllowedExpr(decl, access);
                    return isSetter ? $"if (!{cond}) {{ accessDenied({text}); return; }} " : $"if (!{cond}) return accessDenied({text}); ";
                }
                // (a class registered late in this round: its accessors are pulled in now and generated in the next round)
                if (getter != null) GetFunc(getter, FuncKind.Method);
                if (setter != null) GetFunc(setter, FuncKind.Method);
                if (getter != null) getArms.Add((label, $"{{ {AccessorGuard("get_" + name, getterDecl!, getterAccess, false)}Value r = {_funcByProto[getter].Name}(v); adopt(r, list); return r; }}"));
                else if (setter != null) getArms.Add((label, $"fatal(\"Property '{name}' of '{cls.Rc.Name}' has no getter (only 'set').\");"));
                if (setter != null) setArms.Add((label, $"{{ {AccessorGuard("set_" + name, setterDecl!, setterAccess, true)}Value r = {_funcByProto[setter].Name}(v, x); adopt(r, list); return; }}"));
                else if (getter != null) setArms.Add((label, $"fatal(\"Property '{name}' on '{cls.Rc.Name}' has no setter (only 'get').\");"));
            }
            return (getArms, setArms, ptrArms);
        }

        private string PropertyHelpers(string name, Dictionary<int, int> indexByClass)
        {
            string m = Mangle(name);
            bool isLength = name is "Length" or "length";
            string lengthCode = isLength ? "    { bool ok; Value n = lengthOf(v, &ok); if (ok) return n; }\n"
                : name == "littleEndian" ? "    if (v.kind == K_Buffer) return Bool(bufOf(v)->little != 0);\n" : "";
            var (getArms, setArms, ptrArms) = FieldArms(name, _classList);
            string get = MergedCases(getArms), set = MergedCases(setArms), ptr = MergedCases(ptrArms);
            var sb = new StringBuilder();
            string lp = (FieldHasList(name) ? ", OwnList* list" : "") + (FieldHasCaller(name) ? ", uint32_t caller" : "");
            string unusedLp = (FieldHasList(name) ? "(void)list; " : "") + (FieldHasCaller(name) ? "(void)caller; " : "");
            // (modules: a value of a class that has no such field, or no class at all, is a MissingMemberException - the program's list of classes is complete, but the name may come from a library)
            string getMissing = Modular ? $"return missingField(v, {CString(name)});" : $"fatal(\"Field '{name}' not found on this object.\");";
            string setMissing = !Modular ? $"fatal(\"Field '{name}' not found on this object.\");"
                : setArms.Count == 0 ? $"fatal(\"Field '{name}' cannot be assigned.\");" : $"missingField(v, {CString(name)}); return;";
            sb.AppendLine($"{HelperInline}Value gf_{m}(Value v{lp}) {{\n{lengthCode}    {unusedLp}\n    if (FIRE_UNLIKELY(v.kind != K_Class)) return missingField(v, {CString(name)});\n    if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v);\n    Obj* o = asObj(v);\n    switch (o->cls) {{\n{get}        default: {getMissing}\n    }}\n}}");
            string lpArgs = (FieldHasList(name) ? ", list" : "") + (FieldHasCaller(name) ? ", caller" : "");
            if (_usesThreads)   // (a change of an object of the shared domain is one section)
                sb.AppendLine($"static inline void sfb_{m}(Value v, Value x{lp});\n{HelperInline}void sf_{m}(Value v, Value x{lp}) {{ bool ss_ = sectionScopeEnterV(v); sfb_{m}(v, x{lpArgs}); sectionScopeExit(ss_); }}");
            sb.AppendLine($"{(_usesThreads ? "static inline " : HelperInline)}void {(_usesThreads ? "sfb_" : "sf_")}{m}(Value v, Value x{lp}) {{\n    (void)x; {unusedLp}\n    if (FIRE_UNLIKELY(v.kind != K_Class)) {{ notObjectAssign(v); return; }}\n    if (FIRE_UNLIKELY(!leafAlive(v))) {{ destroyedError(v); return; }}\n    Obj* o = asObj(v);\n    switch (o->cls) {{\n{set}        default: {setMissing}\n    }}\n}}");
            // (the address of a field, `ref obj.name`: only for the names whose address the program takes)
            if (_fieldAddressTaken.Contains(name))
                sb.AppendLine($"{HelperInline}Value fp_{m}(Value v) {{\n    if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v);\n    Obj* o = asObj(v);\n    switch (o->cls) {{\n{ptr}        default: fatal(\"The address of '{name}' cannot be taken (it is a property or does not exist).\");\n    }}\n}}");
            return sb.ToString();
        }

        /// <summary>`a[i]` and `a[i] = v`: arrays, buffers and strings directly, objects through their GetIndex/SetIndex methods.</summary>
        private string IndexHelpers()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{HelperInline}Value {Px}aget_g(Value a, Value i, OwnList* list) {{");
            if (_dispatchers.Contains(("GetIndex", 1)))
            {
                sb.AppendLine($"    if (a.kind == K_Class) {{ Value r = {CallFn("GetIndex", 1)}(a, i{DispatchTail("GetIndex", 1, "list", "0xFFFFFFFFu")}); adopt(r, list); return r; }}");
            }
            else sb.AppendLine("    (void)list;");
            sb.AppendLine("    return arrayGet(a, i);");
            sb.AppendLine("}");
            sb.AppendLine($"{HelperInline}void {Px}aset_g(Value a, Value i, Value v, OwnList* list) {{");
            string secEnd = _usesThreads ? " sectionScopeExit(ss_);" : "";
            if (_usesThreads) sb.AppendLine("    bool ss_ = sectionScopeEnterV(a);");
            if (_dispatchers.Contains(("SetIndex", 2)))
                sb.AppendLine($"    if (a.kind == K_Class) {{ Value r = {CallFn("SetIndex", 2)}(a, i, v{DispatchTail("SetIndex", 2, "list", "0xFFFFFFFFu")}); adopt(r, list);{secEnd} return; }}");
            else sb.AppendLine("    (void)list;");
            sb.AppendLine("    arraySet(a, i, v);" + secEnd);
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>fire::runDestructors (destructors of the class chain, derived class first) and fire::userToString (ToString()).</summary>
        private string RuntimeHooks()
        {
            var sb = new StringBuilder();
            sb.Append(ClassDescTable());
            if (_usesExceptions)
            {
                var indexClass = _classes.GetValueOrDefault("IndexOutOfBoundsException") ?? throw new NativeNotSupportedException("exceptions need the prelude class IndexOutOfBoundsException");
                var destroyedClass = _classes.GetValueOrDefault("DestroyedException") ?? throw new NativeNotSupportedException("exceptions need the prelude class DestroyedException");
                var divideClass = _classes.GetValueOrDefault("DivideByZeroException") ?? throw new NativeNotSupportedException("exceptions need the prelude class DivideByZeroException");
                sb.AppendLine("Value makeDivideError(Value message) {");
                sb.AppendLine($"    Value o = newObject({divideClass.Id}, {divideClass.Fields.Count}, g_globalOwn);");
                sb.AppendLine($"    {Nested($"{_funcByProto[divideClass.Rc.FindConstructor(1)!].Name}(o, message);")}");
                sb.AppendLine("    return o;");
                sb.AppendLine("}");
                sb.AppendLine("Value makeDestroyedError(Value message) {");
                sb.AppendLine($"    Value o = newObject({destroyedClass.Id}, {destroyedClass.Fields.Count}, g_globalOwn);");
                sb.AppendLine($"    {Nested($"{_funcByProto[destroyedClass.Rc.FindConstructor(1)!].Name}(o, message);")}");
                sb.AppendLine("    return o;");
                sb.AppendLine("}");
                if (_usesReflection && _classes.GetValueOrDefault("ReflectionException") is { } reflectClass)
                {
                    sb.AppendLine("Value makeReflectError(Value message) {");
                    sb.AppendLine($"    Value o = newObject({reflectClass.Id}, {reflectClass.Fields.Count}, g_globalOwn);");
                    sb.AppendLine($"    {Nested($"{_funcByProto[reflectClass.Rc.FindConstructor(1)!].Name}(o, message);")}");
                    sb.AppendLine("    return o;");
                    sb.AppendLine("}");
                }
                else if (_usesReflection)
                {
                    // (the runtime's reflection calls it; a program without the class ReflectionException ends with the message)
                    sb.AppendLine("Value makeReflectError(Value message) { char t[500]; strToUtf8(message, t, sizeof t); fatal(t); }");
                }
                var accessClass = _classes.GetValueOrDefault("AccessDeniedException") ?? throw new NativeNotSupportedException("exceptions need the prelude class AccessDeniedException");
                sb.AppendLine("Value makeAccessError(Value message) {");
                sb.AppendLine($"    Value o = newObject({accessClass.Id}, {accessClass.Fields.Count}, g_globalOwn);");
                sb.AppendLine($"    {Nested($"{_funcByProto[accessClass.Rc.FindConstructor(1)!].Name}(o, message);")}");
                sb.AppendLine("    return o;");
                sb.AppendLine("}");
                var unitClass = _classes.GetValueOrDefault("UnitMismatchException") ?? throw new NativeNotSupportedException("exceptions need the prelude class UnitMismatchException");
                sb.AppendLine("Value makeUnitError(Value message, Value expected, Value actual) {");
                sb.AppendLine($"    Value o = newObject({unitClass.Id}, {unitClass.Fields.Count}, g_globalOwn);");
                sb.AppendLine($"    {Nested($"{_funcByProto[unitClass.Rc.FindConstructor(3)!].Name}(o, message, expected, actual);")}");
                sb.AppendLine("    return o;");
                sb.AppendLine("}");
                sb.AppendLine("Value makeIndexError(Value message, int64_t index, int64_t length) {");
                sb.AppendLine($"    Value o = newObject({indexClass.Id}, {indexClass.Fields.Count}, g_globalOwn);");
                sb.AppendLine($"    {Nested($"{_funcByProto[indexClass.Rc.FindConstructor(3)!].Name}(o, message, Int(index), Int(length));")}");
                sb.AppendLine("    return o;");
                sb.AppendLine("}");
                // `fireError("Class", "text")` of the natives of packages: the exception object of the class of that name
                sb.AppendLine("Value makeClassError(const char* cls, Value message) {");
                sb.AppendLine("    (void)cls; (void)message;");
                foreach (var name in _errorClasses)
                {
                    var errorClass = _classes.GetValueOrDefault(name);
                    if (errorClass == null || errorClass.Rc.FindConstructor(1) is not { ParamCount: 1 } ctor) continue;
                    sb.AppendLine($"    if (strcmp(cls, {CString(name)}) == 0) {{");
                    sb.AppendLine($"        Value o = newObject({errorClass.Id}, {errorClass.Fields.Count}, g_globalOwn);");
                    sb.AppendLine($"        {Nested($"{_funcByProto[ctor].Name}(o, message);")}");
                    sb.AppendLine("        return o;");
                    sb.AppendLine("    }");
                }
                sb.AppendLine("    return Undef();");
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        /// <summary>The descriptors of the classes (FireClassDesc in fire_rt.h, one row per class id): what the runtime asks about a class - its name, the
        /// fields `message` and `ticks`, the destructors of its chain, ToString() and the exception types it is - without a switch over the class ids.</summary>
        private string ClassDescTable()
        {
            var sb = new StringBuilder();
            var rows = new List<string>();
            var byId = _classList.ToDictionary(c => c.Id);
            int count = _classList.Count == 0 ? 0 : _classList.Max(c => c.Id) + 1;
            // (modules: every class of the libraries has a row - its chain answers the access checks of its code)
            if (Modular) count = Math.Max(count, _progBase);
            var nameOfId = _fixedIds?.ToDictionary(kv => kv.Value, kv => kv.Key) ?? new Dictionary<int, string>();
            var columns = new StringBuilder();
            for (int id = 0; id < count; id++)
            {
                // (an id without a class: a class of a library module that nothing makes objects of)
                if (!byId.TryGetValue(id, out var cls))
                {
                    if (Modular && nameOfId.TryGetValue(id, out var unmade) && _program.Program.Classes.TryGetValue(unmade, out var unmadeRc))
                    {
                        var c = new StringBuilder();
                        for (var b = unmadeRc; b != null; b = b.Base) c.Append(CString(b.Name)).Append(", ");
                        columns.AppendLine($"static const char* const kIsa{id}[] = {{{c}NULL}};");
                        rows.Add($"{{{CString(unmade)}, {unmadeRc.FlattenedFieldNames.Count}, -1, -1, NULL, NULL, NULL, kIsa{id}, NULL, NULL, NULL}}");
                    }
                    else rows.Add(Modular ? "{\"?\", 0, -1, -1, NULL, NULL, NULL, NULL, NULL, NULL, NULL}" : "{\"?\", 0, -1, -1, NULL, NULL, NULL}");
                    continue;
                }
                var chain = new List<string>();
                for (var rc = cls.Rc; rc != null; rc = rc.Base)
                    if (rc.Destructor != null) chain.Add($"{_funcByProto[rc.Destructor].Name}(ObjV(o));");
                string destroy = "NULL";
                if (chain.Count > 0)
                {
                    destroy = $"fire_dtor_{cls.Id}";
                    sb.AppendLine($"static void {destroy}(Obj* o) {{ {string.Join(" ", chain)} }}");
                }
                string toString = cls.Rc.FindMethodWithAccess("ToString", 0).Proto is { ParamCount: 0 } m ? _funcByProto[m].Name : "NULL";
                int message = _usesExceptions && cls.Rc.FieldIndex.TryGetValue("message", out int mi) ? mi : -1;
                int ticks = _needsTimeObj && cls.Rc.FieldIndex.TryGetValue("ticks", out int ti) ? ti : -1;
                string excTypes = "NULL";
                if (_usesExceptions)
                {
                    var types = Enumerable.Range(1, _excTypes.Count - 1).Where(i => ClassIs(cls.Rc, _excTypes[i])).ToList();
                    if (types.Count > 0)
                    {
                        excTypes = $"kExcT{cls.Id}";
                        sb.AppendLine($"static const int32_t {excTypes}[] = {{{string.Join(", ", types)}, -1}};");
                    }
                }
                string modular = Modular ? ModularDescColumns(cls.Rc, columns, id) : "";
                rows.Add($"{{{CString(cls.Rc.Name)}, {cls.Fields.Count}, {message}, {ticks}, {destroy}, {toString}, {excTypes}{modular}}}");
            }
            sb.Append(_libraryTablesCode);
            sb.Append(columns);
            if (rows.Count > 0)
            {
                sb.AppendLine($"static const FireClassDesc kClassDescs[] = {{\n    {string.Join(",\n    ", rows)}\n}};");
                _initCode.Insert(0, $"    g_classDescs = kClassDescs; g_classDescCount = {rows.Count};\n");
            }
            return sb.ToString();
        }

        // -------------------------------------------------------------------------------------------------------------
        // Flow state: stack depth, the static scope chain (identity of the scope + number of declared slots) and which stack
        // slots may hold a reference value
        // -------------------------------------------------------------------------------------------------------------
        private const int FunctionScope = -1;
        private const int GlobalScope = -2;

        private sealed class Flow
        {
            public int Depth;
            public List<(int Id, int Declared)> Scopes = new();
            /// <summary>Bit k: stack slot k may hold a string/array/buffer (slots beyond 63 are assumed to).</summary>
            public ulong Refs;
            /// <summary>The static type of each stack slot (see TypeOf; slots beyond 63 are unknown).</summary>
            public int[] Types = new int[64];
            /// <summary>The `try` statements that are active here, outermost first.</summary>
            public List<HEntry> Handlers = new();
            /// <summary>The catch lambda this code belongs to (null: the function itself).</summary>
            public Region? Region;
            /// <summary>The `finally` blocks that this code is inside of (their entry addresses), outermost first.</summary>
            public List<int> Fins = new();

            public Flow Clone() => new() { Depth = Depth, Scopes = new List<(int, int)>(Scopes), Refs = Refs, Types = (int[])Types.Clone(), Handlers = new List<HEntry>(Handlers), Region = Region, Fins = new List<int>(Fins) };

            /// <summary>Forgets the types of the slots from <paramref name="from"/> on.</summary>
            public void ClearTypes(int from)
            {
                from = Math.Max(from, 0);
                if (from < Types.Length) Array.Clear(Types, from, Types.Length - from);
            }

            public bool SameShape(Flow o) => Depth == o.Depth && ReferenceEquals(Region, o.Region) && Scopes.SequenceEqual(o.Scopes)
                && Handlers.SequenceEqual(o.Handlers) && Fins.SequenceEqual(o.Fins);
        }

        /// <summary>An active `try`: the region of its template, and whether only its `finally` is still armed (while a catch block runs).</summary>
        private readonly record struct HEntry(Region Region, bool FinOnly);

        /// <summary>The code of a function or catch lambda, and what belongs at its end: shared unwinding exits and the landing sites of its handlers.</summary>
        private sealed class Out
        {
            public readonly StringBuilder Code = new();
            /// <summary>The `#line` directive of the last instruction emitted into <see cref="Code"/> that has a line.</summary>
            public string? LineDir;
            /// <summary>Debug builds: the scope state the code emitted so far left in the frame (see DebugBeforeInstruction).</summary>
            public int DbgLv = int.MinValue;
            public readonly StringBuilder Tail = new();
            public readonly Dictionary<string, int> Exits = new();
            public int MaxDepth;
        }

        /// <summary>One `try` statement (a handler template): its catch blocks are a C++ lambda that runs on top of the throw site, so that
        /// `resume` can continue there. Slots of the operand stack that the catch code uses are its own (<c>c{template}_{k}</c>).</summary>
        private sealed class Region
        {
            public required int Template;
            /// <summary>The region the `try` statement is in (null: the function).</summary>
            public Region? Outer;
            /// <summary>The state in front of the RegisterHandler instruction.</summary>
            public required Flow Reg;
            public int ScopeBase => Reg.Scopes.Count;
            public int DepthBase => Reg.Depth;
            public int CatchScopeId;
            public bool HasFinally;
            public int? FinallyAddr;
            public List<(string? Type, int Addr)> Catches = new();
            public int Start = -1, End = -1;
            /// <summary>Jump targets outside of the catch blocks that the catch code leaves to.</summary>
            public readonly SortedSet<int> ExitLabels = new();
            /// <summary>The catch block leaves its own scope on the way out (jump): its objects die at the landing site, after the scopes of the throw site (like in the VM).</summary>
            public bool DeferCatchRelease;
            public bool ReturnExit;
            public readonly Out Out = new();
        }

        private void UseExceptions()
        {
            if (_usesExceptions && _usesGlobalOwn) return;
            _usesExceptions = true;
            _usesGlobalOwn = true;
            _version++;
        }

        /// <summary>The parts of the platform layer in C that the packages in C include (`#include FIRE_PLATFORM_NET_C_HEADER`): the file system (io), the sockets (net), TLS (tls), the devices (dev, gpio, i2c, spi, usb, wifi), sound (audio), the window (display).</summary>
        public static readonly string[] CPlatformLayers = { "fs", "net", "tls", "dev", "gpio", "i2c", "spi", "usb", "wifi", "audio", "display" };

        /// <summary>`#define FIRE_PLATFORM_<PART>_C_HEADER "platform/<platform>/fire_<part>.h"` for every part of <see cref="CPlatformLayers"/>.</summary>
        public static IEnumerable<string> CPlatformHeaderDefines(string platform) =>
            CPlatformLayers.Select(part => $"#define FIRE_PLATFORM_{part.ToUpperInvariant()}_C_HEADER \"platform/{platform}/fire_{part}.h\"");

        private void NeedTimeObj()
        {
            if (_needsTimeObj) return;
            _needsTimeObj = true;
            _version++;
        }

        /// <summary>`#import "graphics"`: the bridge needs the exceptions (a destroyed buffer or array).</summary>
        private void UseGraphics()
        {
            if (_usesGraphics) return;
            _usesGraphics = true;
            _usesGlobalOwn = true;   // what the slicer hands out belongs to the global scope
            UseExceptions();
            _version++;
        }

        /// <summary>`#import "windows"`: the window shows a framebuffer of the graphics bridge; on the desktop it needs SDL3 or SDL2 (the build links it, see `fire-link:`).</summary>
        private void UseWindows()
        {
            if (_usesWindows) return;
            UseGraphics();
            _usesWindows = true;
            _version++;
        }

        private void UseThreads()
        {
            if (!_target.Native.SupportsThreads) throw new NativeNotSupportedException($"fire threads are not supported by the platform '{_target.Native.Platform}'");
            if (!_usesThreads) { _usesThreads = true; _version++; }
            UseExceptions();   // leave and terminate unwind like an exception
        }

        /// <summary>The C++ variable of a global slot in the code that is being generated: the globals of the program are shared; a fire body
        /// keeps its own slots (behind the shared ones) in variables of the thread.</summary>
        /// <summary>A write to a global of the main program or a static field from a fire thread is a section (see the shared domain in the runtime).</summary>
        private string SectionFor(string variable) => _usesThreads && (variable.StartsWith("G", StringComparison.Ordinal) || variable.StartsWith("SF", StringComparison.Ordinal)) ? "bool ss_ = sectionScopeEnter(true); " : "";
        /// <summary>The end of the section that <see cref="SectionFor"/> began (before the end of its block).</summary>
        private string SectionEnd(string variable) => SectionFor(variable).Length > 0 ? "sectionScopeExit(ss_); " : "";

        private string GlobalVar(int slot, ISet<string> locals)
        {
            var f = _cur;
            if (LibraryRole) throw new NativeNotSupportedException($"the library module '{_self!.Name}' uses a global variable of the program");
            if (f.Kind != FuncKind.FireBody || slot < f.GlobalSlotCount) { _globals.Add(slot); return $"G{slot}"; }
            f.ThreadVars.Add(slot);
            locals.Add($"T{slot}");
            return $"T{slot}";
        }

        private Out OutOf(Func f, Region? region) => region?.Out ?? f.Root;

        /// <summary>The innermost region (starting at <paramref name="from"/>) that contains the address; null: the function.</summary>
        private static Region? RegionAt(Region? from, int addr)
        {
            for (var r = from; r != null; r = r.Outer)
                if (r.Start <= addr && addr < r.End) return r;
            return null;
        }

        /// <summary>The name of an operand stack slot in the code of <paramref name="region"/>.</summary>
        private static string SlotName(Region? region, int k)
        {
            for (var r = region; r != null; r = r.Outer)
                if (k >= r.DepthBase) return $"c{r.Template}_{k}";
            return $"s{k}";
        }

        private int ExcTypeId(string? typeName)
        {
            if (typeName == null) return -1;
            int index = _excTypes.IndexOf(typeName);
            if (index < 0) { _excTypes.Add(typeName); index = _excTypes.Count - 1; }
            return index;
        }

        private static string ListName(int scopeId) => scopeId == FunctionScope ? "OP" : scopeId == GlobalScope ? "OG" : $"O{scopeId}";

        private void GenerateChunk(Func f)
        {
            if (f.External) return;   // (a member of another module: that module generates it)
            var chunk = f.Chunk;
            _cur = f;
            string name = f.Name;
            var code = OpInfo.Decode(chunk.Code);
            var indexOfAddr = new Dictionary<int, int>();
            for (int i = 0; i < code.Count; i++) indexOfAddr[code[i].Addr] = i;
            f.Decoded = code;
            f.IndexOf = indexOfAddr;
            f.HasCallTArgs = code.Any(c => c.Op == OpCode.TakeCallTypeArgs);
            _pendingNewTArgs = null;
            _pendingLambdaTArgs = null;
            CollectTArgTexts(f);
            f.Regions = new Dictionary<int, Region>();
            f.Root = new Out();
            // which scopes need a list is found again in every round (pass 1 below sees every instruction before pass 2 emits): a scope that needed one
            // only for a value whose type was not known yet loses it once the type is known
            f.NeedsList.Clear();
            f.Hoists.Clear();
            var finAddrs = chunk.Handlers.Where(t => t.FinallyAddr != null).Select(t => t.FinallyAddr!.Value).ToHashSet();

            var entry = new Flow?[code.Count];
            f.Entry = entry;
            var start = new Flow();
            start.Scopes.Add(f.IsTop ? (GlobalScope, f.Kind == FuncKind.FireBody ? f.Proto!.ParamCount : 0) : (FunctionScope, f.ParamLike));
            entry[0] = start;
            // Parameters can be anything: assume they may be references (a call-site analysis could narrow this). A parameter declared int/float/bool
            // has its type from the GuardLocal of the prologue.
            f.GuardAddr = code.Where(c => c.Op is OpCode.GuardLocal or OpCode.GuardLocalType).ToDictionary(c => c.A[0], c => c.Addr);
            for (int i = 0; i < f.ParamLike; i++)
            {
                MarkVarRef(VarKey(f, $"P{i}"));
                if (!f.GuardAddr.ContainsKey(i)) JoinVarType(VarKey(f, $"P{i}"), TypeUnknown);
            }
            if (f.IsTop && (_usesGlobalOwn || f.Kind == FuncKind.FireBody)) f.NeedsList.Add(GlobalScope);   // thrown exceptions, TakeGlobal and TakeUpwards need the list of the global scope

            var targets = new HashSet<int>();
            var work = new Stack<int>();
            work.Push(0);
            var locals = new SortedSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < f.ThreadArgs; i++) { locals.Add($"T{f.GlobalSlotCount + i}"); f.ThreadVars.Add(f.GlobalSlotCount + i); MarkVarRef(VarKey(f, $"T{f.GlobalSlotCount + i}")); JoinVarType(VarKey(f, $"T{f.GlobalSlotCount + i}"), TypeUnknown); }

            void Propagate(int addr, Flow state, int from)
            {
                if (!indexOfAddr.TryGetValue(addr, out int target))
                    throw new NativeNotSupportedException($"jump to {addr} inside an instruction ({name})");
                if (finAddrs.Contains(addr)) { state = state.Clone(); state.Fins.Add(addr); }
                if (entry[target] == null) { entry[target] = state.Clone(); work.Push(target); return; }
                var existing = entry[target]!;
                if (!existing.SameShape(state))
                    throw new NativeNotSupportedException($"inconsistent stack/scope state at {addr} ({name}), reached from {from}");
                if ((existing.Refs | state.Refs) != existing.Refs) { existing.Refs |= state.Refs; work.Push(target); }
                // a slot keeps its kind only if every way here agrees
                bool typesChanged = false;
                for (int k = 0; k < existing.Types.Length; k++)
                {
                    int joined = JoinTypes(existing.Types[k], state.Types[k]);
                    if (joined != existing.Types[k]) { existing.Types[k] = joined; typesChanged = true; }
                }
                if (typesChanged) work.Push(target);
            }

            // Pass 1: propagate states (and find out which scopes can own objects); pass 2: emit with the states found.
            // Which jumps and returns run through a `finally` is only known once the code in front of it was seen, so repeat until that settles.
            while (true)
            {
                int signature = f.FinSignature;
                while (work.Count > 0)
                {
                    int idx = work.Pop();
                    var state = entry[idx]!.Clone();
                    var ins = code[idx];
                    var step = Step(f, chunk, ins, state, null, locals);
                    var o = OutOf(f, entry[idx]!.Region);
                    o.MaxDepth = Math.Max(o.MaxDepth, Math.Max(entry[idx]!.Depth, state.Depth));
                    if (step.JumpTarget is int jt) { targets.Add(jt); Propagate(jt, state, ins.Addr); }
                    if (step.Extra != null)
                        foreach (var (addr, edgeState) in step.Extra) { targets.Add(addr); Propagate(addr, edgeState, ins.Addr); }
                    if (step.FallsThrough)
                    {
                        if (idx + 1 >= code.Count) throw new NativeNotSupportedException($"code of {name} runs past its end");
                        Propagate(code[idx + 1].Addr, state, ins.Addr);
                    }
                }
                if (f.FinSignature == signature) break;
                for (int i = 0; i < code.Count; i++) if (entry[i] != null) work.Push(i);
            }
            // a hoist needs the list of its target only when a scope it hoists from can own something
            foreach (var (target, sources) in f.Hoists)
                if (sources.Any(f.NeedsList.Contains)) f.NeedsList.Add(target);

            var dbg = DebugBuild ? DebugBegin(f, code, entry) : null;
            for (int i = 0; i < code.Count; i++)
            {
                if (entry[i] == null) continue; // unreachable
                var ins = code[i];
                var o = OutOf(f, entry[i]!.Region);
                if (targets.Contains(ins.Addr)) o.Code.AppendLine($"L{ins.Addr}:;");
                if (dbg != null) DebugBeforeInstruction(f, dbg, chunk, ins, i, entry[i]!, o, targets.Contains(ins.Addr), locals);
                string comment = $"// {ins.Addr}: {ins.Op}{(ins.A.Length > 0 ? " " + string.Join(",", ins.A) : "")}";
                // `#line`: the C of the instruction is the fire line it was compiled from (an instruction without a line keeps the one before it)
                _lineDir = null;
                if (_target.Native.LineDirectives)
                {
                    var (src, line) = chunk.GetLocation(ins.Addr);
                    if (line > 0) o.LineDir = $"#line {line} {LineFileLiteral(src)}";
                    _lineDir = o.LineDir;
                }
                if (_lineDir != null) { o.Code.AppendLine(_lineDir + " " + comment); _lineFresh = true; }
                else o.Code.AppendLine("    " + comment);
                var state = entry[i]!.Clone();
                Step(f, chunk, ins, state, o.Code, locals);
                if (dbg != null) DebugAfterInstruction(f, ins, entry[i]!, o);
                _lineDir = null;
            }
            foreach (var region in f.Regions.Values.OrderBy(r => r.Template)) Landing(f, region, locals);

            // A function with catch blocks keeps its variables in a frame (a struct): the catch blocks are functions of their own that run on top of the
            // throw site (so that `resume` can continue there) and reach the variables through the frame. In the code the names stay the same - a macro
            // per variable turns `s0` into `(FR->s0)`, in the function and in its catch blocks.
            var catchRegions = f.Regions.Values.Where(r => r.Catches.Count > 0).OrderBy(r => r.Template).ToList();
            bool framed = catchRegions.Count > 0;
            var members = new List<(string Decl, string Name, string? Init)>();   // (declaration in the frame, name, initial value)
            void Var(string type, string varName, string? init) => members.Add(($"{type} {varName}", varName, init));
            // the function proper is `name_b` when a wrapper counts the call depth (`try x.Take...`)
            string defName = Wrapped && (f.Kind != FuncKind.Main || DebugBuild) ? f.Name + "_b" : f.Name;
            if (framed)
            {
                foreach (var p in f.ParamNames) Var("Value", p, p + "_in");
            }
            // a debug build: the frame of the function on the shadow stack (its wrapper pushed it; the catch blocks reach it through the frame struct)
            if (dbg != null && framed) Var("DbgFrame*", "DF", "g_dbgTop");

            var pre = new StringBuilder();     // what comes before the declarations
            var decl = new StringBuilder();    // the declarations (not framed) - or nothing, the frame holds them
            var post = new StringBuilder();    // the rest of the start of the function
            if (_usesTryDepth && f.Kind == FuncKind.Main) pre.AppendLine("    ++g_callDepth;");
            pre.Append(Trampoline(f));
            if (f.HasSelf) pre.AppendLine("    (void)self;");
            if (f.Kind == FuncKind.FireBody) pre.AppendLine("    (void)args;");
            if (!f.IsTop && _usesThreads) pre.AppendLine("    if (FIRE_UNLIKELY(pollSignals())) return Undef();");
            if (f.Kind == FuncKind.Lambda)
            {
                if (framed) Var("Value", "self", "lamOn(lam)"); else decl.AppendLine("    Value self = lamOn(lam); (void)self;");
                for (int i = 0; i < f.CaptureCount; i++)
                    if (framed) Var("Value", $"P{f.ParamCount + i}", $"lamCapture(lam, {i})"); else decl.AppendLine($"    Value P{f.ParamCount + i} = lamCapture(lam, {i}); (void)P{f.ParamCount + i};");
            }
            for (int i = 0; i < f.ParamCount; i++) pre.AppendLine($"    (void)P{i};");
            if (dbg != null && !framed) pre.AppendLine("    DbgFrame* const DF = g_dbgTop;");
            if (f.Root.MaxDepth > 0)
            {
                if (framed) foreach (int i in Enumerable.Range(0, f.Root.MaxDepth)) Var("Value", $"s{i}", null);
                else decl.AppendLine("    Value " + string.Join(", ", Enumerable.Range(0, f.Root.MaxDepth).Select(i => $"s{i}")) + ";");
            }
            var localsToDeclare = locals.Where(l => !(l.StartsWith("P") && int.Parse(l.AsSpan(1)) < f.ParamLike)).ToList();
            if (localsToDeclare.Count > 0)
            {
                if (framed) foreach (var l in localsToDeclare) Var("Value", l, "Undef()");
                else decl.AppendLine("    Value " + string.Join(", ", localsToDeclare.Select(l => l + " = Undef()")) + ";");
            }
            string ListInit(int i) => i < 0 ? "{NULL, NULL, poolMark(), 0, NULL, NULL}" : i >= f.Chunk.Code.Count ? "{NULL, NULL, 0xFFFFFFFFu, 0, NULL, NULL}" : "{NULL, NULL, 0, 0, NULL, NULL}";   // (the list of a catch block that never ran: leaving it is a no-op)
            if (f.NeedsList.Count > 0)
            {
                if (framed) foreach (int i in f.NeedsList.OrderBy(i => i)) Var("OwnList", ListName(i), "(OwnList)" + ListInit(i));
                else decl.AppendLine("    OwnList " + string.Join(", ", f.NeedsList.OrderBy(i => i).Select(i => ListName(i) + " = " + ListInit(i))) + ";");
            }
            if (f.NeedsArgList)
            {
                if (framed) Var("OwnList", "AL", "(OwnList){NULL, NULL, 0, 0, NULL, NULL}"); else decl.AppendLine("    OwnList AL = {NULL, NULL, 0, 0, NULL, NULL};");
            }
            // the handlers of the `try` statements, and the operand stack slots of the catch blocks
            foreach (var region in f.Regions.Values.OrderBy(r => r.Template))
            {
                if (framed) Var("Handler", $"H{region.Template}", null); else decl.AppendLine($"    Handler H{region.Template};");
                if (region.Catches.Count > 0)
                    for (int k = region.DepthBase; k < region.Out.MaxDepth; k++) Var("Value", $"c{region.Template}_{k}", null);
            }
            if (f.Kind == FuncKind.Main && _usesThreads) post.AppendLine("    gilAcquire();\n    g_domainRoot = &OG;");
            if (f.IsTop && (_usesGlobalOwn || f.Kind == FuncKind.FireBody)) post.AppendLine("    g_globalOwn = &OG;");
            if (f.Kind == FuncKind.FireBody)
            {
                post.AppendLine("    adoptTravel(&OG);");
                for (int i = 0; i < f.ThreadArgs; i++)
                    post.AppendLine(VarRef(VarKey(f, $"T{f.GlobalSlotCount + i}")) ? $"    T{f.GlobalSlotCount + i} = args[{i}]; retain(T{f.GlobalSlotCount + i});" : $"    T{f.GlobalSlotCount + i} = args[{i}];");
            }
            if (!f.IsTop && _usesTakeUpwards && f.NeedsList.Contains(FunctionScope)) post.AppendLine("    OP.parent = g_globalOwn;   // the parent scope of a function scope is the global scope");
            // Parameters are variables: they hold what was passed.
            for (int i = 0; i < f.ParamLike; i++)
                if (VarRef(VarKey(f, $"P{i}"))) post.AppendLine($"    retain(P{i});");
            if (dbg != null) post.Append(DebugPrologue(f, dbg));

            var sb = new StringBuilder();
            if (dbg != null) sb.Append(DebugTables(f, dbg));
            foreach (var region in f.Regions.Values.OrderBy(r => r.Template))
            {
                var types = region.Catches.Select(c => ExcTypeId(c.Type).ToString(CultureInfo.InvariantCulture)).ToList();
                sb.AppendLine($"static const int32_t HT_{name}_{region.Template}[] = {{{(types.Count == 0 ? "0" : string.Join(", ", types))}}};");
                sb.AppendLine($"static const HandlerInfo HI_{name}_{region.Template} = {{{region.Catches.Count}, HT_{name}_{region.Template}, {(region.HasFinally ? 1 : 0)}, {(LibraryRole ? $"fire_exct_{_self!.Id}" : "NULL")}}};");
            }
            string frame = $"FR_{f.Name}";
            // the catch blocks: one function each, with the frame of the function as its context
            var catchCode = new StringBuilder();
            foreach (var region in catchRegions)
            {
                catchCode.AppendLine($"static void {CatchName(f, region)}{(dbg != null ? "_b" : "")}(void* ctx_, uint32_t ci, Value e)");
                catchCode.AppendLine("{");
                catchCode.AppendLine($"    {frame}* FR = ({frame}*)ctx_;");
                catchCode.AppendLine("    switch (ci) {");
                string catchVar = $"B{region.CatchScopeId}_0";
                for (int i = 0; i < region.Catches.Count; i++)
                {
                    string init = (locals.Contains(catchVar) ? $"{catchVar} = e; " : "(void)e; ") + (f.NeedsList.Contains(region.CatchScopeId) ? $"{ListName(region.CatchScopeId)}.mark = poolMark(); " + (_usesTakeUpwards ? $"{ListName(region.CatchScopeId)}.parent = &{ListName(region.Reg.Scopes[^1].Id)}; " : "") : "");
                    catchCode.AppendLine($"    case {i}: {{ {init}goto L{region.Catches[i].Addr}; }}");
                }
                catchCode.AppendLine("    default: break;");
                catchCode.AppendLine("    }");
                catchCode.Append(region.Out.Code);
                catchCode.Append(region.Out.Tail);
                catchCode.AppendLine("    fatal(\"control flow fell off the end of a catch block\");");
                catchCode.AppendLine("}");
            }
            // Only what the catch blocks use lives in the frame (and the handlers, which the frame gives to the runtime); everything else stays a plain
            // variable of the function, which the C compiler can keep in a register.
            var plain = new List<(string Decl, string Name, string? Init)>();
            if (framed && dbg == null)   // (a debug build keeps them all in the frame: the debugger reads them from the catch blocks too)
            {
                var used = new HashSet<string>(System.Text.RegularExpressions.Regex.Matches(catchCode.ToString(), @"\b[A-Za-z_][A-Za-z0-9_]*\b").Select(m => m.Value), StringComparer.Ordinal);
                plain = members.Where(m => !used.Contains(m.Name) && !m.Decl.StartsWith("Handler ", StringComparison.Ordinal)).ToList();
                members = members.Except(plain).ToList();
            }
            var inFrame = new HashSet<string>(members.Select(m => m.Name), StringComparer.Ordinal);
            string defSignature = f.SignatureOf(defName, p => framed && inFrame.Contains(p) ? "_in" : "");
            if (framed)
            {
                sb.AppendLine($"typedef struct {frame} {{ {string.Join(" ", members.Select(m => m.Decl + ";"))} }} {frame};");
                foreach (var region in catchRegions) sb.AppendLine($"static void {CatchName(f, region)}(void* ctx_, uint32_t ci, Value e);");
                if (dbg != null) foreach (var region in catchRegions) sb.AppendLine($"static void {CatchName(f, region)}_b(void* ctx_, uint32_t ci, Value e);");
                foreach (var m in members) sb.AppendLine($"#define {m.Name} (FR->{m.Name})");
            }
            sb.AppendLine(defSignature);
            sb.AppendLine("{");
            if (framed)
            {
                sb.AppendLine($"    {frame} FRv; {frame}* FR = &FRv;");
                foreach (var m in members.Where(m => m.Init != null)) sb.AppendLine($"    {m.Name} = {m.Init};");
                // the variables of the function that no catch block uses (parameters stay the parameters)
                foreach (var m in plain.Where(m => !f.ParamNames.Contains(m.Name)))
                    sb.AppendLine((m.Init == null ? $"    {m.Decl};" : $"    {m.Decl} = {m.Init};") + (m.Decl.StartsWith("Value ", StringComparison.Ordinal) ? $" (void){m.Name};" : ""));
            }
            sb.Append(pre);
            sb.Append(decl);
            sb.Append(post);
            sb.Append(f.Root.Code);
            sb.Append(f.Root.Tail);
            if (!f.IsTop) sb.AppendLine("    fatal(\"control flow fell off the end of a function\");");
            sb.AppendLine("}");
            if (_target.Native.LineDirectives) sb.AppendLine(LineResetMarker);
            sb.Append(catchCode);
            if (_target.Native.LineDirectives && catchCode.Length > 0) sb.AppendLine(LineResetMarker);
            if (framed) foreach (var m in members) sb.AppendLine($"#undef {m.Name}");
            if (dbg != null) foreach (var region in catchRegions) sb.AppendLine(DebugCatchWrapper(f, CatchName(f, region)));
            // `try x.Take...`: every function counts the call depth (the callee knows which argument list is its own); a debug build keeps its frame
            if (defName != f.Name)
            {
                string call = $"{defName}({string.Join(", ", (f.Kind == FuncKind.FireBody ? new[] { "args" } : Array.Empty<string>()).Concat(f.ParamNames))})";
                sb.AppendLine(f.Signature);
                if (dbg != null) sb.AppendLine(DebugWrapper(f, call));
                else sb.AppendLine(f.Kind == FuncKind.FireBody
                    ? $"{{ ++g_callDepth; {call}; --g_callDepth; }}"
                    : $"{{ ++g_callDepth; Value r_ = {call}; --g_callDepth; return r_; }}");
            }
            f.Code = sb.ToString();
        }

        /// <summary>The function of the catch blocks of a `try` statement.</summary>
        private static string CatchName(Func f, Region region) => $"C_{f.Name}_{region.Template}";

        // `#line` (docs/NATIVE_BACKEND.md "Zeilen des Programms im C"): the instructions name their fire line; after the code of a function the C file names itself
        // again (the final pass replaces the marker with `#line <its own line> "fire_program.c"`).
        private string? _lineDir;
        private bool _lineFresh;
        private const string LineResetMarker = "//@@fire-line-reset@@";
        /// <summary>The name of the C file in `#line` directives that return to the generated code.</summary>
        public const string ProgramFileName = "fire_program.c";

        /// <summary>The file name of a source in `#line`: its path; `fire:prelude/N` for the prelude and the preludes of imports, `fire:source/N` for a source without a file.</summary>
        private string LineFileLiteral(int sourceIndex)
        {
            if (_lineFiles.TryGetValue(sourceIndex, out var literal)) return literal;
            string? file = _program.SourceFiles is { } files && sourceIndex < files.Count ? files[sourceIndex] : null;
            string name = file ?? (sourceIndex < _program.FirstUserSource ? $"fire:prelude/{sourceIndex}" : $"fire:source/{sourceIndex}");
            literal = "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            _lineFiles[sourceIndex] = literal;
            return literal;
        }
        private readonly Dictionary<int, string> _lineFiles = new();

        /// <summary>Replaces the markers after the functions with `#line` directives that name the C file and its own line again.</summary>
        private static string ResolveLineResets(string text)
        {
            if (!text.Contains(LineResetMarker, StringComparison.Ordinal)) return text;
            var result = new StringBuilder(text.Length + 4096);
            int lineNo = 1, start = 0;
            while (start < text.Length)
            {
                int end = text.IndexOf('\n', start);
                int next = end < 0 ? text.Length : end + 1;
                if (string.CompareOrdinal(text, start, LineResetMarker, 0, LineResetMarker.Length) == 0)
                    result.Append("#line ").Append(lineNo + 1).Append(" \"" + ProgramFileName + "\"\n");
                else result.Append(text, start, next - start);
                lineNo++;
                start = next;
            }
            return result.ToString();
        }

        // -------------------------------------------------------------------------------------------------------------
        // Exceptions: unwinding, landing sites, return through `finally`
        // -------------------------------------------------------------------------------------------------------------
        private Region GetRegion(Func f, int template, Flow st)
        {
            if (f.Regions.TryGetValue(template, out var region)) return region;
            var t = f.Chunk.Handlers[template];
            region = new Region
            {
                Template = template, Outer = st.Region, Reg = st.Clone(), CatchScopeId = f.Chunk.Code.Count + template,
                HasFinally = t.FinallyAddr != null, FinallyAddr = t.FinallyAddr,
            };
            foreach (var (type, addr) in t.Catches) region.Catches.Add((type, addr));
            if (region.Catches.Count > 0)
            {
                region.Start = region.Catches[0].Addr;
                var before = f.Decoded[f.IndexOf[region.Start] - 1];
                if (before.Op != OpCode.Jump) throw new NativeNotSupportedException($"unexpected layout of a try statement in {f.Name}");
                region.End = before.A[0];
            }
            f.Regions[template] = region;
            return region;
        }

        /// <summary>The state while a catch block runs: the registers of the `try` are there, only its `finally` is still armed.</summary>
        private static Flow LandingFlow(Region region)
        {
            var flow = region.Reg.Clone();
            if (region.HasFinally) flow.Handlers.Add(new HEntry(region, true));
            return flow;
        }

        /// <summary>Leaves the scopes <c>[downTo, fromCount)</c> of <paramref name="st"/> (innermost first): what they own is destroyed, what they hold is released.
        /// With <paramref name="transfer"/> an object that is returned does not die with them.</summary>
        private void ReleaseScopes(Func f, Flow st, int fromCount, int downTo, bool reset, ISet<string> locals, Action<string> emit, string? transfer)
        {
            if (transfer != null)
            {
                // a returned value that belongs to one of the scopes that are left goes to the caller, with what hangs on it and belongs to them too (SPEC 2.3)
                var leaving = new List<string>();
                for (int i = fromCount - 1; i >= downTo; i--)
                    if (f.NeedsList.Contains(st.Scopes[i].Id)) leaving.Add("&" + ListName(st.Scopes[i].Id));
                if (leaving.Count > 0) emit($"{{ OwnList* const T_[] = {{{string.Join(", ", leaving)}}}; transferTree({transfer}, T_, {leaving.Count}); }}");
            }
            for (int i = fromCount - 1; i >= downTo; i--)
            {
                int id = st.Scopes[i].Id;
                // the scope of a catch block that is left (jump, return, exception): the landing site of the `try` leaves it, after the scopes of the throw site (like in the VM)
                if (st.Region != null && id == st.Region.CatchScopeId && i == st.Region.ScopeBase) { st.Region.DeferCatchRelease = true; continue; }
                if (f.NeedsList.Contains(id)) emit($"leave(&{ListName(id)});");
                foreach (var v in ScopeVariables(f, id, locals))
                    if (VarRef(VarKey(f, v))) emit(reset ? $"release({v}); {v} = Undef();" : $"release({v});");
            }
        }

        /// <summary>The end of the program: the global scope is released like any other (destructors run), then what the globals hold.</summary>
        private void HaltCode(Func f, Action<string> emit)
        {
            if (f.Kind == FuncKind.FireBody)
            {
                // the end of a thread: it destroys what it created itself (the copies of `taking` skip their destructors); after an unhandled
                // exception its global scope lives on, the exception belongs to it and is on its way to the main program
                emit("g_leaving = 1;");
                emit("if (g_threadFailed) graveyardAdd(&OG); else leave(&OG);");
                foreach (var t in f.ThreadVars)
                    if (VarRef(VarKey(f, $"T{t}"))) emit($"release(T{t});");
                return;
            }
            if (_usesThreads) emit("mainFinish();");
            if (f.NeedsList.Contains(GlobalScope)) emit("leave(&OG);");
            foreach (var g in _globals.OrderBy(x => x))
                if (VarRef($"G{g}")) emit($"release(G{g});");
            foreach (var (key, index) in _staticFields.OrderBy(kv => kv.Value))
                if (StaticFieldVar(key.Class, key.Field) is var sfv && VarRef(sfv)) emit($"release({sfv});");
        }

        /// <summary>The code that runs when something is unwinding through this point: the scopes up to the innermost active `try` of this
        /// function (or catch lambda) are left, then control goes to the landing site of that `try`, or out of the function. Returns the label.</summary>
        private string ExitLabel(Func f, Flow st, ISet<string> locals)
        {
            var o = OutOf(f, st.Region);
            var body = new StringBuilder();
            void E(string text) => body.Append("    ").AppendLine(text);
            HEntry? local = st.Handlers.Count > 0 && st.Handlers[^1].Region.Outer == st.Region ? st.Handlers[^1] : null;
            int baseCount = st.Region?.ScopeBase ?? (f.IsTop ? 1 : 0);
            ReleaseScopes(f, st, st.Scopes.Count, local?.Region.ScopeBase ?? baseCount, local != null || st.Region != null, locals, E, null);
            if (local != null) E($"goto LAND{local.Value.Region.Template};");
            else if (st.Region != null) E("return;");
            else if (f.IsTop) { HaltCode(f, E); E("return;"); }
            else E("return Undef();");
            string text = body.ToString();
            if (!o.Exits.TryGetValue(text, out int id))
            {
                id = o.Exits.Count;
                o.Exits[text] = id;
                o.Tail.AppendLine($"U{id}:;").Append(text);
            }
            return $"U{id}";
        }

        /// <summary>A `return` of the C++ variable <c>r</c> from the code described by <paramref name="st"/>. A `try` with a `finally` that is still active
        /// in this region gets to run it first (completion kind 2); a catch lambda hands the return to the landing site of its `try`. Returns the edges
        /// of the flow graph (the entries of the finally blocks).</summary>
        private List<(int Addr, Flow State)> ReturnSeq(Func f, Flow st, bool rIsRef, bool retain, Action<string>? emit, ISet<string> locals)
        {
            var edges = new List<(int, Flow)>();
            void E(string text) => emit?.Invoke(text);
            bool isCtor = f.Kind == FuncKind.Ctor;
            string? transfer = isCtor ? null : "r";
            if (retain && rIsRef && !isCtor) E("retain(r);");
            for (int hi = st.Handlers.Count - 1; hi >= 0 && st.Handlers[hi].Region.Outer == st.Region; hi--)
            {
                var region = st.Handlers[hi].Region;
                if (!st.Handlers[hi].FinOnly) E("popHandler();");   // (the armed finally of a catch block was already taken off the stack)
                if (!region.HasFinally) continue;
                int fin = region.FinallyAddr!.Value;
                ReleaseScopes(f, st, st.Scopes.Count, region.ScopeBase, true, locals, E, transfer);
                E($"{SlotName(st.Region, region.DepthBase)} = r; {SlotName(st.Region, region.DepthBase + 1)} = Int(2); goto L{fin};");
                f.FinReturn.Add(fin);
                ulong below = region.DepthBase >= 64 ? ulong.MaxValue : (1UL << region.DepthBase) - 1;
                edges.Add((fin, new Flow
                {
                    Depth = region.DepthBase + 2, Scopes = st.Scopes.Take(region.ScopeBase).ToList(), Handlers = st.Handlers.Take(hi).ToList(),
                    Region = st.Region, Fins = new List<int>(region.Reg.Fins), Refs = (st.Refs & below) | (rIsRef && region.DepthBase < 64 ? 1UL << region.DepthBase : 0),
                }));
                return edges;
            }
            if (st.Region == null)
            {
                ReleaseScopes(f, st, st.Scopes.Count, 0, false, locals, E, transfer);
                E(isCtor ? "return self;" : "return r;");
            }
            else
            {
                ReleaseScopes(f, st, st.Scopes.Count, st.Region.ScopeBase, true, locals, E, transfer);
                E($"exitReturn(&H{st.Region.Template}, r); return;");
                st.Region.ReturnExit = true;
                edges.AddRange(ReturnSeq(f, LandingFlow(st.Region), true, false, null, locals));
            }
            return edges;
        }

        /// <summary>Where unwinding arrives at the function that owns a `try` (reached from <c>U</c> exits): what a catch block left through
        /// (jump, return), an exception that this `try` did not catch (it runs the `finally` and throws on), or something passing by.</summary>
        private void Landing(Func f, Region r, ISet<string> locals)
        {
            var o = OutOf(f, r.Outer);
            int t = r.Template;
            var lt = new StringBuilder();
            void E(string text) => lt.Append("    ").AppendLine(text);
            var reg = r.Reg;
            lt.AppendLine($"LAND{t}: FIRE_UNUSED_LABEL;");
            if (r.DeferCatchRelease)
            {
                // the scope of the catch block (left by a jump, a return or an exception) dies here, after the scopes of the throw site (the VM destroys in this order)
                if (f.NeedsList.Contains(r.CatchScopeId)) E($"leave(&{ListName(r.CatchScopeId)});");
                foreach (var v in ScopeVariables(f, r.CatchScopeId, locals))
                    if (VarRef(VarKey(f, v))) E($"release({v}); {v} = Undef();");
            }
            if (r.ExitLabels.Count > 0)
            {
                E($"if (g_unwind.kind == UW_JUMP && g_unwind.target == &H{t}) {{");
                E("    uint32_t lab = g_unwind.label; clearUnwind();");
                E("    switch (lab) {");
                foreach (int target in r.ExitLabels)
                {
                    var te = f.Entry[f.IndexOf[target]] ?? throw new NativeNotSupportedException($"catch block leaves to unreachable code ({f.Name})");
                    bool here = ReferenceEquals(te.Region, r.Outer);
                    E($"    case {target}: {{");
                    ReleaseScopes(f, reg, reg.Scopes.Count, here ? te.Scopes.Count : r.Outer?.ScopeBase ?? 0, true, locals, x => E("        " + x), null);
                    if (here)
                    {
                        for (int k = r.DepthBase; k < te.Depth; k++) E($"        {SlotName(r.Outer, k)} = g_unwind.regs[{k - r.DepthBase}];");
                        E($"        goto L{target};");
                    }
                    else E($"        exitJump(&H{r.Outer!.Template}, {target}); return;");
                    E("    }");
                }
                E("    default: fatal(\"internal error: unknown landing label\");");
                E("    }");
                E("}");
            }
            if (r.ReturnExit)
            {
                E($"if (g_unwind.kind == UW_RETURN && g_unwind.target == &H{t}) {{");
                E("    Value r = g_unwind.value; (void)r; clearUnwind();");
                ReturnSeq(f, LandingFlow(r), true, false, x => E("    " + x), locals);
                E("}");
            }
            string exit = ExitLabel(f, reg, locals);
            if (_usesThreads)
            {
                // leave/terminate pass through: the `finally` runs (completion 4), no catch block matches
                E("if (g_unwind.kind == UW_LEAVE) {");
                E($"    g_handlers = H{t}.prev;");
                if (r.HasFinally) E($"    clearUnwind(); {SlotName(r.Outer, r.DepthBase)} = Undef(); {SlotName(r.Outer, r.DepthBase + 1)} = Int(4); goto L{r.FinallyAddr};");
                else E($"    goto {exit};");
                E("}");
            }
            E($"if (g_unwind.kind == UW_RETHROW && g_unwind.target == &H{t}) {{");
            E("    Value x = g_unwind.value; clearUnwind();");
            if (r.HasFinally) E($"    {SlotName(r.Outer, r.DepthBase)} = x; {SlotName(r.Outer, r.DepthBase + 1)} = Int(1); goto L{r.FinallyAddr};");
            else { E("    throwValue(x, false);"); E($"    goto {exit};"); }
            E("}");
            E($"goto {exit};");
            o.Tail.Append(lt);
        }

        private readonly record struct StepResult(bool FallsThrough, int? JumpTarget, List<(int Addr, Flow State)>? Extra = null);

        private string Var(Flow st, int depth, int slot, ISet<string> locals)
        {
            int scopeIndex = st.Scopes.Count - 1 - depth;
            if (scopeIndex < 0) throw new NativeNotSupportedException("access to a variable outside of the function (closures are not supported yet)");
            int id = st.Scopes[scopeIndex].Id;
            if (id == GlobalScope) return GlobalVar(slot, locals);
            string v = id == FunctionScope ? $"P{slot}" : $"B{id}_{slot}";
            locals.Add(v);
            return v;
        }

        /// <summary>The variables that belong to a scope of the function (parameters and base-scope locals for the function scope).</summary>
        private static IEnumerable<string> ScopeVariables(Func f, int scopeId, ISet<string> locals)
        {
            if (scopeId == GlobalScope) return Enumerable.Empty<string>();
            if (scopeId == FunctionScope)
                return Enumerable.Range(0, f.ParamLike).Select(i => $"P{i}").Concat(locals.Where(l => l.StartsWith("P") && int.Parse(l.AsSpan(1)) >= f.ParamLike));
            return locals.Where(l => l.StartsWith($"B{scopeId}_", StringComparison.Ordinal));
        }

        /// <summary>Applies the effect of one instruction to <paramref name="st"/>; with a builder it also emits the C++.</summary>
        /// <summary>Set by the instructions that keep the kinds of the stack slots up to date (StepCore); after any other one the slots it may have written
        /// lose their kinds - an instruction that is not taught about kinds can never make the generator believe a wrong one.</summary>
        private bool _kindsKept;

        private StepResult Step(Func f, Chunk chunk, Instr ins, Flow st, StringBuilder? sb, ISet<string> locals)
        {
            int before = st.Depth;
            _kindsKept = false;
            // an operand that has no value yet (TypeNone) gives a result without one
            bool fromNone = false;
            for (int k = Math.Max(0, before - 3); k < Math.Min(before, 64); k++) fromNone |= st.Types[k] == TypeNone;
            var result = StepCore(f, chunk, ins, st, sb, locals);
            if (!_kindsKept)
            {
                int from = st.Depth > before ? before : st.Depth - 1;
                st.ClearTypes(from);
                if (fromNone) for (int k = Math.Max(0, from); k < Math.Min(st.Depth, 64); k++) st.Types[k] = TypeNone;
            }
            if (result.Extra != null) foreach (var (_, edge) in result.Extra) edge.ClearTypes(0);
            return result;
        }

        private StepResult StepCore(Func f, Chunk chunk, Instr ins, Flow st, StringBuilder? sb, ISet<string> locals)
        {
            int d = st.Depth;
            string fn = f.Name;
            string S(int k) => SlotName(st.Region, k);
            // After an operation that can throw: if an exception is unwinding, leave (to the handler of this function, or out of it).
            void Check()
            {
                if (!_usesExceptions || sb == null) return;
                E($"if (FIRE_UNLIKELY(g_unwind.active)) goto {ExitLabel(f, st, locals)};");
            }
            // After an operation that throws only on its slow path: `failed` is a cheap test that holds whenever it may have thrown (an arithmetic result
            // is never undefined, a condition that threw is false) - only then the unwinding flag is read.
            void CheckIf(string failed)
            {
                if (!_usesExceptions || sb == null) return;
                E($"if (FIRE_UNLIKELY({failed}) && FIRE_UNLIKELY(g_unwind.active)) goto {ExitLabel(f, st, locals)};");
            }
            // A condition: a value that is no bool throws an ArgumentException (truthy gives false then) - with exceptions the false path checks first.
            void JumpOnCondition(string value, bool jumpIfTrue, int target)
            {
                if (!_usesExceptions || sb == null) { E($"if ({(jumpIfTrue ? "" : "!")}truthy({value})) goto L{target};"); return; }
                string exit = ExitLabel(f, st, locals);
                if (jumpIfTrue) E($"if (truthy({value})) {{ goto L{target}; }} if (FIRE_UNLIKELY(g_unwind.active)) goto {exit};");
                else E($"if (!truthy({value})) {{ if (FIRE_UNLIKELY(g_unwind.active)) goto {exit}; goto L{target}; }}");
            }
            void E(string text)
            {
                if (sb == null) return;
                // (every further line of the instruction names its fire line again: the instruction comment line in between would shift the count)
                if (_lineDir != null && !_lineFresh) sb.AppendLine(_lineDir);
                _lineFresh = false;
                sb.Append("    ").AppendLine(text);
            }
            void Need(int n)
            {
                if (d < n) throw new NativeNotSupportedException($"stack underflow at {ins.Addr} in {fn}");
            }
            StepResult Next() { st.Depth = d; return new StepResult(true, null); }
            string Str(int constIdx) => chunk.Constants[constIdx].AsString();
            // The innermost scope owns what is created or handed back at this point.
            string OwnerList()
            {
                f.NeedsList.Add(st.Scopes[^1].Id);
                return ListName(st.Scopes[^1].Id);
            }
            // A member that the code of this function may not use: the access is an AccessDeniedException (the code after it is dead when it is thrown)
            void AccessCheck(RuntimeClass declaring, AccessModifier access, string message)
            {
                if (!CheckAccess || AccessAllowed(f.Chunk.OwnerClass, declaring, access)) return;
                E($"accessDenied({CString(message)});");
                Check();
            }
            void RequireSelf() { if (!f.HasSelf && f.Kind != FuncKind.Lambda) throw new NativeNotSupportedException($"'this' outside of an instance member ({fn} at {ins.Addr})"); }
            // overloads by type where the target is known up to the overload: the selector scores the arguments (access: per candidate, by the class of this code)
            StepResult OverloadedCall(RuntimeClass rc, string method, int argc, FuncKind kind, int slot, string self, string what, bool owned = false, ClassInfo? newClass = null)
            {
                long argMask = ArgMask();
                uint addressBits = 0;
                for (int i = 0; i < Math.Min(argc, 16); i++) if ((argMask >> (4 * i) & 15) == 3) addressBits |= 1u << i;
                var selector = OverloadSelectorFor(rc, method, argc, kind, (declaring, access) =>
                    !CheckAccess || AccessAllowed(f.Chunk.OwnerClass, declaring, access) ? null
                    : kind == FuncKind.Ctor ? $"Constructor of '{rc.Name}' is {DescribeAccess(access)} and cannot be called from here."
                    : $"{what} '{method}' of '{declaring.Name}' is {DescribeAccess(access)} and cannot be called from here.", addressBits, HintBefore(f, ins));
                long mask = ArgMask();
                int first = d - argc;
                ArgsBefore(first, argc, mask);
                string args = Args(first, argc);
                if (newClass != null)
                {
                    string ownerExpr = owned ? $"&asObj({S(slot)})->owned" : "&" + OwnerList();
                    string typeArgsSet = _pendingNewTArgs != null ? $"asObj(o)->targs = {_pendingNewTArgs}; " : "";
                    _pendingNewTArgs = null;
                    E($"{{ Value o = newObject({ClassIdExpr(newClass)}, {newClass.Fields.Count}, {ownerExpr}); {typeArgsSet}{(newClass.Rc.IsActor ? "markActor(o); " : "")}{selector.Name}(o{args}, &{OwnerList()}); {S(slot)} = o; }}");
                    ArgsAfter(argc, mask, S(slot));
                    Check();
                    SetR(slot, false);
                    SetT(slot, ObjType(newClass.Rc)); _kindsKept = true;
                }
                else
                {
                    E($"{S(slot)} = {selector.Name}({self}{args}, &{OwnerList()});");
                    ArgsAfter(argc, mask, kind == FuncKind.Ctor ? "Undef()" : S(slot));
                    Check();
                    if (kind == FuncKind.Ctor) { E($"{S(slot)} = Undef();"); SetR(slot, false); }
                    else AdoptResult(slot, SelectorReturnType(selector));
                }
                d = slot + 1; return Next();
            }
            string Args(int first, int count) => string.Concat(Enumerable.Range(first, count).Select(i => ", " + S(i)));
            // `ref` arguments (SPEC 5.4.2): the prefix CopyArgs in front of the call marks the arguments that carry an address (bits 3); `flat`/`copy` are not translated yet.
            long ArgMask()
            {
                int at = f.IndexOf[ins.Addr];
                if (at > 0 && f.Decoded[at - 1].Op == OpCode.ArgNames) at--;
                if (at == 0 || f.Decoded[at - 1].Op != OpCode.CopyArgs) return 0;
                long mask = (long)f.Decoded[at - 1].A[0] | ((long)f.Decoded[at - 1].A[1] << 16) | ((long)f.Decoded[at - 1].A[2] << 32) | ((long)f.Decoded[at - 1].A[3] << 48);
                return mask;
            }
            // `f(1, size: 2)`: the names of the last arguments (OpCode.ArgNames right in front of the call), null without.
            string[]? NamesBefore()
            {
                int at = f.IndexOf[ins.Addr];
                return at > 0 && f.Decoded[at - 1] is { Op: OpCode.ArgNames } named ? Str(named.A[0]).Split('|') : null;
            }
            // A call with named arguments of a callee that is known (docs/language/08-classes.md): every parameter gets its argument - the positional
            // ones in order, a named one by its name - or its default value (`Pre` evaluates those). Null when the names do not fit the callee.
            (string Pre, string Args)? NamedCallArgs(FunctionProto proto, int first, int argc, string[] names, long mask, string self)
            {
                if (proto.ParamNames is not { } paramNames) return null;
                int positional = argc - names.Length;
                if (positional > proto.ParamCount) return null;
                var argOf = Enumerable.Repeat(-1, proto.ParamCount).ToArray();
                for (int i = 0; i < positional; i++) argOf[i] = i;
                for (int k = 0; k < names.Length; k++)
                {
                    int at = Array.IndexOf(paramNames, names[k]);
                    if (at < 0 || argOf[at] >= 0) return null;
                    argOf[at] = positional + k;
                }
                var pre = new StringBuilder();
                var args = new StringBuilder();
                for (int p = 0; p < proto.ParamCount; p++)
                {
                    int j = argOf[p];
                    if (j >= 0)
                    {
                        args.Append(", ").Append((mask >> (4 * j) & 15) == 3 && (proto.RefMask >> p & 1) == 0 ? $"ptrRead({S(first + j)})" : S(first + j));
                        continue;
                    }
                    var defaultProto = p < proto.ParamDefaults.Count ? proto.ParamDefaults[p] : null;
                    if (defaultProto == null) return null;
                    pre.Append($"Value dv{p} = adoptV({GetFunc(defaultProto, FuncKind.Init).Name}({self}), &{OwnerList()}); ");
                    args.Append($", dv{p}");
                }
                return (pre.ToString(), args.ToString());
            }
            // The ArgumentException of VM.FillNamedArgs when the names do not fit the callee.
            static string NamedMismatch(FunctionProto proto, int argc, string[] names)
            {
                var paramNames = proto.ParamNames ?? Array.Empty<string>();
                int positional = argc - names.Length;
                if (positional > proto.ParamCount) return $"Wrong number of arguments: expected {proto.ParamCount}, got {argc}.";
                var given = new bool[proto.ParamCount];
                for (int i = 0; i < positional; i++) given[i] = true;
                foreach (var name in names)
                {
                    int at = Array.IndexOf(paramNames, name);
                    if (at < 0) return $"There is no parameter '{name}'.";
                    if (given[at]) return $"The parameter '{name}' is given twice.";
                    given[at] = true;
                }
                for (int i = 0; i < proto.ParamCount; i++)
                    if (!given[i] && (i >= proto.ParamDefaults.Count || proto.ParamDefaults[i] == null)) return $"The parameter '{paramNames[i]}' has no value (and no default value).";
                return "Named arguments do not fit.";
            }
            (string Pre, string Args) NamedCallArgsOrFail(FunctionProto proto, int first, int argc, string[] names, long mask, string self, string what) =>
                NamedCallArgs(proto, first, argc, names, mask, self)
                ?? throw new NativeNotSupportedException($"the named arguments {string.Join(", ", names)} do not fit {what}");
            // The arguments for a callee that is known: an address goes to a `ref` parameter, the others get the value.
            string CallArgs(int first, int count, long mask, uint calleeRefs) => string.Concat(Enumerable.Range(0, count).Select(i =>
                ", " + ((mask >> (4 * i) & 15) == 3 && (calleeRefs >> i & 1) == 0 ? $"ptrRead({S(first + i)})" : S(first + i))));
            // `f(g())` (SPEC 2.1): the result of `g` that is still the caller's belongs to the callee - it travels in the argument list of the call
            // and is destroyed after the call unless the callee kept it (field, TakeTo, return value). The destructor therefore runs after the
            // callee's own locals, not before them as in the VM.
            void ArgsBefore(int first, int argc, long mask)
            {
                for (int i = 0; i < Math.Min(argc, 16); i++)
                {
                    int bits = (int)(mask >> (4 * i) & 15);
                    if (bits == 4)
                    {
                        f.NeedsArgList = true;
                        E($"reownArg({S(first + i)}, &{OwnerList()}, &AL);");
                    }
                    else if (bits == 5)
                    {
                        // `f(take x)` (SPEC 2.2): x belongs to the callee, whoever owned it - it travels in the argument list like a returned value
                        f.NeedsArgList = true;
                        E($"if (takeAlive({S(first + i)})) takeArg({S(first + i)}, &{OwnerList()}, &AL);");
                        Check();
                    }
                    else if (bits is 1 or 2)
                    {
                        // `f(flat x)` / `f(copy x)`: the copy belongs to the callee - it travels in the argument list like a returned value
                        f.NeedsArgList = true;
                        E($"{S(first + i)} = copyArg({S(first + i)}, {(bits == 2 ? "true" : "false")}, &{OwnerList()}, &AL);");
                        Check();
                    }
                }
            }
            void ArgsAfter(int argc, long mask, string result)
            {
                for (int i = 0; i < Math.Min(argc, 16); i++)
                    if ((mask >> (4 * i) & 15) is 1 or 2 or 4 or 5) { E($"finishArgs({result}, &AL);"); return; }
            }
            bool R(int k) => k >= 64 || (st.Refs >> k & 1UL) != 0;
            void SetR(int k, bool value)
            {
                if (k >= 64) return;
                if (value) st.Refs |= 1UL << k; else st.Refs &= ~(1UL << k);
            }
            // The static types of the stack slots (Flow.Types): their kind (or KindUnknown) and the unit of a number.
            int Ty(int k) => k < 0 || k >= 64 ? TypeUnknown : st.Types[k];
            byte K(int k) => KindOfType(Ty(k));
            // An object of a known class, or undefined when the type is nullable (a parameter or field declared with the class).
            bool ObjSlot(int k) => BaseKindOfType(Ty(k)) == KindObj && (Ty(k) & TypeNone) == 0 && InterfaceOfType(Ty(k)) == null;
            int U(int k) => UnitOfType(Ty(k));
            void SetT(int k, int type) { if (k >= 0 && k < 64) st.Types[k] = type; }
            bool IsNum(byte kind) => kind is KindInt or KindFloat;
            // A number of known kind (or undefined, see the guards of TypedArith) as a C++ Real / double.
            byte BK(int k) => BaseKindOfType(Ty(k));
            string Num(int k) => BK(k) == KindFloat ? $"{S(k)}.f" : $"(Real){S(k)}.i";
            string Dbl(int k) => BK(k) == KindFloat ? $"(double){S(k)}.f" : $"(double){S(k)}.i";
            // A parameter in the prologue before its GuardLocal still holds what the caller passed.
            bool RawParam(string variable) => variable[0] == 'P' && f.GuardAddr.TryGetValue(int.Parse(variable.AsSpan(1)), out int guardAt) && ins.Addr < guardAt;
            // What a load of a variable certainly gives. A global is only trusted in the main program: another function can run before its declaration.
            int LoadType(string variable)
            {
                if (variable[0] == 'G' && f.Kind != FuncKind.Main) return TypeUnknown;
                return _varType.TryGetValue(VarKey(f, variable), out int type) ? type : TypeUnknown;
            }
            // Two different static units that convert by a factor (alignUnits, unitFactor): the C++ expression of the factor from `from` to `to`.
            string? LinearFactor(int from, int to)
            {
                Unit a = UnitById(from), b = UnitById(to);
                if (from == 0 || to == 0 || !a.IsLinear || !b.IsLinear || !a.IsCompatibleWith(b)) return null;
                _unitFactorCaches.Add((from, to));
                return $"unitFactorCached(&{Px}kUF_{from}_{to}, {UnitC(from)}, {UnitC(to)})";
            }
            // After a call: whatever came back belongs to the innermost scope now - unless it is certainly a number or a bool (the static return type).
            void AdoptResult(int slot, int type = TypeUnknown)
            {
                if (IsPlainType(type)) { SetR(slot, false); SetT(slot, type); _kindsKept = true; return; }
                E($"adopt({S(slot)}, &{OwnerList()});"); SetR(slot, true);
                if (type != TypeUnknown) { SetT(slot, type); _kindsKept = true; }   // (an object of a known class)
            }
            // The list for the default values of a call that leaves out arguments (only then the scope needs one).
            string DefaultsList(FunctionProto proto, int argc) => argc < proto.ParamCount ? "&" + OwnerList() : "";
            // Stores into a variable: count what it now holds, let go of what it held.
            void StoreVar(string variable, int slot)
            {
                string key = VarKey(f, variable);
                bool valueRef = R(slot);
                if (valueRef) MarkVarRef(key);
                bool varRef = VarRef(key);
                string sec = SectionFor(variable);
                string secEnd = SectionEnd(variable);
                if (valueRef) E($"{{ {sec}Value n = {S(slot)}; Value o = {variable}; {variable} = n; retain(n); release(o); {secEnd}}}");
                else if (varRef) E($"{{ {sec}Value o = {variable}; {variable} = {S(slot)}; release(o); {secEnd}}}");
                else E(sec.Length > 0 ? $"{{ {sec}{variable} = {S(slot)}; {secEnd}}}" : $"{variable} = {S(slot)};");
            }
            // The variables of a scope let go of their values when the scope is left.
            void ReleaseScopeVariables(int scopeId, bool reset)
            {
                foreach (var v in ScopeVariables(f, scopeId, locals))
                    if (VarRef(VarKey(f, v))) E(reset ? $"release({v}); {v} = Undef();" : $"release({v});");
            }

            // a safe point: a jump back (a loop) lets the other threads run now and then, and looks at the signals (leave, terminate)
            if (_usesThreads && sb != null && ins.A.Length > 0 && ins.A[0] <= ins.Addr
                && ins.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfFalsePeek or OpCode.JumpIfTruePeek
                    or OpCode.JumpIfNotLt or OpCode.JumpIfNotLtEq or OpCode.JumpIfNotGt or OpCode.JumpIfNotGtEq or OpCode.JumpIfNotEq or OpCode.JumpIfNotNotEq)
                E($"if (FIRE_UNLIKELY(pollSignals())) goto {ExitLabel(f, st, locals)};");

            switch (ins.Op)
            {
                case OpCode.LoadConst:
                {
                    var c = chunk.Constants[ins.A[0]];
                    E($"{S(d)} = {Constant(c)};");
                    SetR(d, c.Kind == ValueKind.String);
                    SetT(d, TypeOfConstant(c)); _kindsKept = true;
                    d++; return Next();
                }
                case OpCode.Pop:
                    Need(1); d--; _kindsKept = true; return Next();
                case OpCode.Dup:
                    Need(1); E($"{S(d)} = {S(d - 1)};"); SetR(d, R(d - 1)); SetT(d, Ty(d - 1)); _kindsKept = true; d++; return Next();
                case OpCode.Swap:
                {
                    Need(2); E($"{{ Value t = {S(d - 1)}; {S(d - 1)} = {S(d - 2)}; {S(d - 2)} = t; }}");
                    bool a = R(d - 1), b = R(d - 2); SetR(d - 1, b); SetR(d - 2, a);
                    int ta = Ty(d - 1), tb = Ty(d - 2); SetT(d - 1, tb); SetT(d - 2, ta); _kindsKept = true;
                    return Next();
                }

                case OpCode.DeclareLocal:
                {
                    Need(1);
                    var (id, declared) = st.Scopes[^1];
                    string v;
                    if (id == GlobalScope) v = GlobalVar(declared, locals);
                    else
                    {
                        v = id == FunctionScope ? $"P{declared}" : $"B{id}_{declared}";
                        locals.Add(v);
                    }
                    string key = VarKey(f, v);
                    if (R(d - 1)) { MarkVarRef(key); E($"{v} = {S(d - 1)}; retain({v});"); }
                    else E($"{v} = {S(d - 1)};");
                    JoinVarType(key, Ty(d - 1));
                    st.Scopes[^1] = (id, declared + 1);
                    _kindsKept = true;
                    d--; return Next();
                }
                case OpCode.LoadLocal:
                {
                    string v = Var(st, ins.A[0], ins.A[1], locals);
                    E($"{S(d)} = {v};");
                    SetR(d, VarRef(VarKey(f, v)));
                    SetT(d, RawParam(v) ? TypeUnknown : LoadType(v)); _kindsKept = true;
                    d++; return Next();
                }
                case OpCode.StoreLocal:
                case OpCode.StoreLocalPop:
                {
                    Need(1);
                    string v = Var(st, ins.A[0], ins.A[1], locals);
                    StoreVar(v, d - 1);
                    if (!RawParam(v)) JoinVarType(VarKey(f, v), Ty(d - 1));
                    _kindsKept = true;
                    if (ins.Op == OpCode.StoreLocalPop) d--;
                    return Next();
                }
                case OpCode.LoadGlobal:
                {
                    string gv = GlobalVar(ins.A[0], locals);
                    E($"{S(d)} = {gv};"); SetR(d, VarRef(VarKey(f, gv))); SetT(d, LoadType(gv)); _kindsKept = true; d++; return Next();
                }
                case OpCode.StoreGlobal:
                case OpCode.StoreGlobalPop:
                {
                    Need(1);
                    string gv = GlobalVar(ins.A[0], locals);
                    StoreVar(gv, d - 1);
                    JoinVarType(VarKey(f, gv), Ty(d - 1)); _kindsKept = true;
                    if (ins.Op == OpCode.StoreGlobalPop) d--;
                    return Next();
                }
                case OpCode.ArithLocalConstPop:
                case OpCode.ArithGlobalConstPop:
                {
                    bool local = ins.Op == OpCode.ArithLocalConstPop;
                    string v;
                    Value constant;
                    bool subtract;
                    int mode;
                    if (local) { v = Var(st, ins.A[0], ins.A[1], locals); constant = chunk.Constants[ins.A[2]]; mode = ins.A[3]; }
                    else { v = GlobalVar(ins.A[0], locals); constant = chunk.Constants[ins.A[1]]; mode = ins.A[2]; }
                    subtract = (mode & 1) != 0;
                    bool stepMode = (mode & 2) != 0;
                    string key = VarKey(f, v);
                    if (constant.Kind == ValueKind.String) MarkVarRef(key);
                    _kindsKept = true;   // (the stack is untouched)
                    int varType = LoadType(v), constType = TypeOfConstant(constant);
                    byte varKind = KindOfType(varType), constKind = KindOfType(constType);
                    int varUnit = UnitOfType(varType);
                    // `x++` (step mode) counts in the unit of the variable (stepOf)
                    int constUnit = stepMode && varUnit != 0 ? varUnit : UnitOfType(constType);
                    if (IsNum(varKind) && IsNum(constKind) && varUnit == constUnit)
                    {
                        string op = subtract ? "-" : "+";
                        string sec = SectionFor(v);
                        string unitArg = varUnit == 0 ? "" : $", {varUnit}";
                        string kc = Constant(constant);
                        bool ints = varKind == KindInt && constKind == KindInt;
                        if (ints)
                            E($"{(sec.Length > 0 ? "{ " + sec : "")}{v} = Int((int64_t)((uint64_t){v}.i {op} (uint64_t){kc}.i){unitArg});{(sec.Length > 0 ? " " + SectionEnd(v) + "}" : "")}");
                        else
                        {
                            string x = varKind == KindFloat ? $"{v}.f" : $"(Real){v}.i", y = constKind == KindFloat ? $"{kc}.f" : $"(Real){kc}.i";
                            E($"{(sec.Length > 0 ? "{ " + sec : "")}{v} = Float({x} {op} {y}{unitArg});{(sec.Length > 0 ? " " + SectionEnd(v) + "}" : "")}");
                        }
                        JoinVarType(key, TypeOf(ints ? KindInt : KindFloat, varUnit));
                        return Next();
                    }
                    JoinVarType(key, TypeUnknown);
                    string k = Constant(constant);
                    if (stepMode) k = $"stepOf({v}, {k})";
                    if (!subtract && VarRef(key))
                    {
                        E($"{{ {SectionFor(v)}Value o = {v}; {v} = addR(o, {k}, &{OwnerList()}); retain({v}); release(o); {SectionEnd(v)}}}");
                        Check();
                    }
                    else
                    {
                        E(SectionFor(v).Length > 0 ? $"{{ {SectionFor(v)}{v} = {(subtract ? "sub" : "add")}({v}, {k}); {SectionEnd(v)}}}" : $"{v} = {(subtract ? "sub" : "add")}({v}, {k});");
                        CheckIf($"{v}.kind == K_Undefined");
                    }
                    return Next();
                }

                case OpCode.StepAdd:
                case OpCode.Add:
                {
                    Need(2);
                    if (TypedArith("+")) return Next();
                    if (ins.Op == OpCode.StepAdd) E($"{S(d - 1)} = stepOf({S(d - 2)}, {S(d - 1)});");
                    if (UseOperator("operator+"))
                    {
                        E($"{S(d - 2)} = {OperatorWrapper("operator+")}({S(d - 2)}, {S(d - 1)}, &{OwnerList()});");
                        Check();
                        d--; SetR(d - 1, true);
                    }
                    else if (R(d - 2) || R(d - 1))
                    {
                        E($"{S(d - 2)} = addR({S(d - 2)}, {S(d - 1)}, &{OwnerList()});");
                        Check();
                        d--; SetR(d - 1, true);
                    }
                    else { E($"{S(d - 2)} = add({S(d - 2)}, {S(d - 1)});"); CheckIf($"{S(d - 2)}.kind == K_Undefined"); d--; SetR(d - 1, false); }
                    return Next();
                }
                case OpCode.Power: return Binary("power", "operator^");
                case OpCode.StepSub:
                case OpCode.Sub:
                    Need(2);
                    if (TypedArith("-")) return Next();
                    if (ins.Op == OpCode.StepSub) { Need(2); E($"{S(d - 1)} = stepOf({S(d - 2)}, {S(d - 1)});"); }
                    return Binary("sub", "operator-");

                case OpCode.Mul: Need(2); return TypedArith("*") ? Next() : Binary("mul", "operator*");
                case OpCode.Div: Need(2); return TypedArith("/") ? Next() : Binary("divide", "operator/");
                case OpCode.Mod: Need(2); return TypedArith("%") ? Next() : Binary("modulo", "operator%");
                case OpCode.BitAnd: return Binary("bitAnd", "operator&");
                case OpCode.BitOr: return Binary("bitOr", "operator|");
                case OpCode.BitXor: return Binary("bitXor", "operator#");
                case OpCode.ShiftLeft: return Binary("shl", "operator<<");
                case OpCode.ShiftRight: return Binary("shr", "operator>>");
                case OpCode.RotateUnderTop:
                {
                    Need(3);
                    E($"{{ Value t = {S(d - 3)}; {S(d - 3)} = {S(d - 2)}; {S(d - 2)} = t; }}");
                    bool lower = R(d - 3), middle = R(d - 2);
                    SetR(d - 3, middle); SetR(d - 2, lower);
                    int tl = Ty(d - 3), tm = Ty(d - 2); SetT(d - 3, tm); SetT(d - 2, tl); _kindsKept = true;
                    return Next();
                }
                case OpCode.CoerceUnit:
                {
                    Need(1);
                    int target = UnitId(chunk.Units[ins.A[0]]);
                    byte kind = K(d - 1);
                    int from = U(d - 1);
                    if (IsNum(kind) && from == target) { _kindsKept = true; return Next(); }   // already in that unit
                    E($"{S(d - 1)} = coerceUnit({S(d - 1)}, {UnitC(target)});");
                    // a float stays a float; an int converted within its dimension stays an int (into the base unit it may become a float)
                    Unit fromUnit = UnitById(from), toUnit = UnitById(target);
                    bool sameDimension = fromUnit.IsCompatibleWith(toUnit) && !(target == 0 && from != 0);
                    if (kind == KindFloat || kind == KindInt && sameDimension) { SetT(d - 1, TypeOf(kind, target)); _kindsKept = true; }
                    return Next();
                }
                case OpCode.RelabelUnitDynamic:
                    Need(2); E($"{S(d - 1)} = relabelUnit({S(d - 1)}, unitOf({S(d - 2)}));"); return Next();
                case OpCode.RelabelUnit:
                {
                    Need(1);
                    int target = UnitId(chunk.Units[ins.A[0]]);
                    if (IsNum(K(d - 1)))
                    {
                        // the same number with another unit
                        E($"{S(d - 1)}.unit = {UnitC(target)};");
                        SetT(d - 1, TypeOf(K(d - 1), target)); _kindsKept = true;
                        return Next();
                    }
                    E($"{S(d - 1)} = relabelUnit({S(d - 1)}, {UnitC(target)});");
                    return Next();
                }
                case OpCode.CoerceUnitDynamic:
                    Need(2); E($"{S(d - 1)} = coerceUnit({S(d - 1)}, unitOf({S(d - 2)}));"); return Next();
                case OpCode.CoerceType:
                    Need(1);
                    E($"{S(d - 1)} = coerceType({S(d - 1)}, {(TypeTag)ins.A[0] switch { TypeTag.Bool => "K_Bool", TypeTag.Int => "K_Int", TypeTag.Float => "K_Float", TypeTag.Char => "K_Char", _ => "K_String" }});");
                    Check();
                    return Next();
                case OpCode.CastTo:
                {
                    // `x!Dog`: the same value, `undefined` passes, anything else that is not `is of` the type is an ArgumentException
                    Need(1);
                    string castName = chunk.Constants[ins.A[0]].AsString();
                    string castTest = castName switch
                    {
                        "bool" => "K_Bool", "int" => "K_Int", "float" => "K_Float", "char" => "K_Char", "string" => "K_String", "undefined" => "K_Undefined", "class" => "K_Class", _ => "",
                    };
                    string test = castTest == "K_Int" ? $"{S(d - 1)}.kind == K_Int && {S(d - 1)}.reserved == 0"
                        : castTest.Length > 0 ? $"{S(d - 1)}.kind == {castTest}" : $"{IsOfFn(castName)}({S(d - 1)})";
                    if (castTest.Length == 0) _isOfTypes.Add(castName);
                    E($"if (FIRE_UNLIKELY({S(d - 1)}.kind != K_Undefined && !({test}))) castFailed({S(d - 1)}, {CString(castName)});");
                    Check();
                    return Next();
                }
                case OpCode.RebindLambda:
                    Need(2);
                    E($"{S(d - 2)} = rebindLam({S(d - 2)}, {S(d - 1)}, &{OwnerList()});");
                    Check();
                    d--; SetR(d - 1, true); return Next();
                case OpCode.ConvertWidth:
                    Need(1);
                    if ((ins.A[1] & 4) != 0)
                    {
                        // implicit (an assignment to a declared width): only a number of the declared kind is cut - this cannot fail
                        E($"if ({S(d - 1)}.kind == {((ins.A[1] & 1) != 0 ? "K_Float" : "K_Int")}) {S(d - 1)} = convertWidth({S(d - 1)}, {ins.A[0]}, {((ins.A[1] & 1) != 0 ? "true" : "false")}, {((ins.A[1] & 2) != 0 ? "true" : "false")});");
                        return Next();
                    }
                    E($"{S(d - 1)} = convertWidth({S(d - 1)}, {ins.A[0]}, {((ins.A[1] & 1) != 0 ? "true" : "false")}, {((ins.A[1] & 2) != 0 ? "true" : "false")});");
                    Check();
                    return Next();
                case OpCode.CoerceTypeDynamic:
                    Need(2); E($"{S(d - 1)} = coerceType({S(d - 1)}, {S(d - 2)}.kind);"); return Next();
                case OpCode.IsInUnit:
                    Need(1); E($"{S(d - 1)} = Bool(unitCompatible(unitOf({S(d - 1)}), {UnitC(UnitId(chunk.Units[ins.A[0]]))}));"); SetR(d - 1, false); return Next();
                case OpCode.ToEnum:
                {
                    // `x!Color`: the number as a value of the enum
                    Need(1);
                    E($"{S(d - 1)} = toEnum({S(d - 1)}, {EnumC(EnumIndex(chunk.Constants[ins.A[0]].Enum!))});");
                    Check();
                    SetR(d - 1, false); return Next();
                }
                case OpCode.IsOfType:
                {
                    Need(1);
                    string typeName = chunk.Constants[ins.A[0]].AsString();
                    string test = typeName switch
                    {
                        "bool" => "K_Bool", "int" => "K_Int", "float" => "K_Float", "char" => "K_Char", "string" => "K_String", "undefined" => "K_Undefined", "class" => "K_Class", _ => "",
                    };
                    if (test == "K_Int") E($"{S(d - 1)} = Bool({S(d - 1)}.kind == K_Int && {S(d - 1)}.reserved == 0);");   // (an enum is not an int)
                    else if (test.Length > 0) E($"{S(d - 1)} = Bool({S(d - 1)}.kind == {test});");
                    else { _isOfTypes.Add(typeName); E($"{S(d - 1)} = Bool({IsOfFn(typeName)}({S(d - 1)}));"); }
                    SetR(d - 1, false); return Next();
                }
                case OpCode.IsFrom:
                    Need(2); E($"{S(d - 2)} = Bool(isFrom({S(d - 2)}, {S(d - 1)}, {(ins.A[0] != 0 ? "true" : "false")}));"); d--; SetR(d - 1, false); return Next();
                case OpCode.Neg:
                    Need(1);
                    if (K(d - 1) == KindInt) { E($"{S(d - 1)} = Int((int64_t)(0 - (uint64_t){S(d - 1)}.i){(U(d - 1) == 0 ? "" : $", {UnitC(U(d - 1))}")});"); _kindsKept = true; return Next(); }
                    if (K(d - 1) == KindFloat) { E($"{S(d - 1)} = Float(-{S(d - 1)}.f{(U(d - 1) == 0 ? "" : $", {UnitC(U(d - 1))}")});"); _kindsKept = true; return Next(); }
                    return Unary("negate");
                case OpCode.LogicalNot:
                    Need(1);
                    if (K(d - 1) == KindBool) { E($"{S(d - 1)} = Bool({S(d - 1)}.i == 0);"); _kindsKept = true; return Next(); }
                    return Unary("lnot");
                case OpCode.BitNot: return Unary("bitNot");

                case OpCode.Eq: Need(2); return TypedCompare("==") ? Next() : Compare("opEq", false, "operator==");
                case OpCode.NotEq: Need(2); return TypedCompare("!=") ? Next() : Compare("opEq", true, "operator!=");
                case OpCode.Lt: Need(2); return TypedCompare("<") ? Next() : Compare("lt", false, "operator<");
                case OpCode.LtEq: Need(2); return TypedCompare("<=") ? Next() : Compare("le", false, "operator<=");
                case OpCode.Gt: Need(2); return TypedCompare(">") ? Next() : Compare("gt", false, "operator>");
                case OpCode.GtEq: Need(2); return TypedCompare(">=") ? Next() : Compare("ge", false, "operator>=");

                case OpCode.Jump:
                {
                    int target = ins.A[0];
                    var targetRegion = RegionAt(st.Region, target);
                    if (ReferenceEquals(targetRegion, st.Region)) { E($"goto L{target};"); _kindsKept = true; return new StepResult(false, target); }
                    // a jump out of a catch block: the catch lambda ends, and the function that owns the `try` continues at the target
                    for (var r = st.Region; r != targetRegion; r = r!.Outer) r!.ExitLabels.Add(target);
                    // the operand slots of the catch code travel in registers of the unwinding state: the frames that unwind first overwrite the
                    // slots of the function that owns the `try` (the result of the call that threw), the landing site puts them back
                    var outermost = st.Region!;
                    while (!ReferenceEquals(outermost.Outer, targetRegion)) outermost = outermost.Outer!;
                    if (d - outermost.DepthBase > 4) throw new NativeNotSupportedException($"a jump out of a catch block with {d - outermost.DepthBase} operands ({fn} at {ins.Addr})");
                    for (int k = outermost.DepthBase; k < d; k++) E($"g_unwind.regs[{k - outermost.DepthBase}] = {SlotName(st.Region, k)};");
                    E($"exitJump(&H{st.Region!.Template}, {target}); return;");
                    var exitState = st.Clone();
                    exitState.Region = targetRegion;
                    return new StepResult(false, null, new List<(int, Flow)> { (target, exitState) });
                }
                case OpCode.JumpIfFalse:
                    Need(1);
                    if (K(d - 1) == KindBool) E($"if (!{S(d - 1)}.i) goto L{ins.A[0]};");
                    else JumpOnCondition(S(d - 1), false, ins.A[0]);
                    d--; st.Depth = d; _kindsKept = true;
                    return new StepResult(true, ins.A[0]);
                case OpCode.JumpIfFalsePeek:
                    Need(1);
                    if (K(d - 1) == KindBool) E($"if (!{S(d - 1)}.i) goto L{ins.A[0]};");
                    else JumpOnCondition(S(d - 1), false, ins.A[0]);
                    _kindsKept = true;
                    return new StepResult(true, ins.A[0]);
                case OpCode.JumpIfTruePeek:
                    Need(1);
                    if (K(d - 1) == KindBool) E($"if ({S(d - 1)}.i) goto L{ins.A[0]};");
                    else JumpOnCondition(S(d - 1), true, ins.A[0]);
                    _kindsKept = true;
                    return new StepResult(true, ins.A[0]);
                case OpCode.JumpIfNotLt: return TypedJumpIfNot("<") ?? JumpIfNot("lt", false, "operator<");
                case OpCode.JumpIfNotLtEq: return TypedJumpIfNot("<=") ?? JumpIfNot("le", false, "operator<=");
                case OpCode.JumpIfNotGt: return TypedJumpIfNot(">") ?? JumpIfNot("gt", false, "operator>");
                case OpCode.JumpIfNotGtEq: return TypedJumpIfNot(">=") ?? JumpIfNot("ge", false, "operator>=");
                case OpCode.JumpIfNotEq: return TypedJumpIfNot("==") ?? JumpIfNot("opEq", false, "operator==");
                case OpCode.JumpIfNotNotEq: return TypedJumpIfNot("!=") ?? JumpIfNot("opEq", true, "operator!=");

                case OpCode.EnterScope:
                {
                    int parentId = st.Scopes[^1].Id;
                    if (_usesTakeUpwards)
                    {
                        // TakeUpwards moves a value to the parent scope: both scopes need a list, the child knows the parent
                        f.NeedsList.Add(ins.Addr);
                        f.NeedsList.Add(parentId);
                    }
                    if (f.NeedsList.Contains(ins.Addr))
                    {
                        E($"{ListName(ins.Addr)}.mark = poolMark();");
                        if (_usesTakeUpwards) E($"{ListName(ins.Addr)}.parent = &{ListName(parentId)};");
                    }
                    st.Scopes.Add((ins.Addr, 0)); _kindsKept = true; return Next();
                }
                case OpCode.ExitScope:
                {
                    if (st.Scopes.Count <= 1) throw new NativeNotSupportedException($"ExitScope without scope at {ins.Addr} in {fn}");
                    int id = st.Scopes[^1].Id;
                    // the scope of the catch block itself: its objects die after the scopes of the throw site, so the landing site leaves it (see Landing)
                    if (st.Region != null && id == st.Region.CatchScopeId && st.Scopes.Count - 1 == st.Region.ScopeBase) st.Region.DeferCatchRelease = true;
                    // a catch block that leaves scopes of its function (break/continue): the function that owns the `try` leaves them, after the unwinding
                    else if (st.Scopes.Count - 1 >= (st.Region?.ScopeBase ?? 0))
                    {
                        if (f.NeedsList.Contains(id)) E($"leave(&{ListName(id)});");
                        ReleaseScopeVariables(id, reset: true);
                    }
                    st.Scopes.RemoveAt(st.Scopes.Count - 1);
                    _kindsKept = true;
                    return Next();
                }

                case OpCode.CallNative:
                {
                    int argc = ins.A[1];
                    Need(argc);
                    string native = ins.A[0] < _natives.Count ? _natives[ins.A[0]] : "?";
                    if (native == "print" && argc == 1)
                    {
                        E($"{S(d - 1)} = print({S(d - 1)}, &{OwnerList()});");
                        Check();
                        SetR(d - 1, false);
                        return Next();
                    }
                    if (native == StringMethods.NativeName && argc is >= 2 and <= 4)
                    {
                        int first = d - argc;
                        _usesStringCall = true;
                        string a0 = argc > 2 ? S(first + 2) : "Undef()", a1 = argc > 3 ? S(first + 3) : "Undef()";
                        E($"{S(first)} = stringCall({S(first)}.i, {S(first + 1)}, {argc - 2}, {a0}, {a1}, &{OwnerList()});");
                        Check();
                        d = first + 1; SetR(first, true); return Next();
                    }
                    if (native == ResourceMethods.NativeName && argc == 2)
                    {
                        int first = d - 2;
                        E($"{S(first)} = resourceCall({S(first)}.i, {S(first + 1)}, &{OwnerList()});");
                        Check();
                        d = first + 1; SetR(first, true); return Next();
                    }
                    if (native == EnvironmentMethods.NativeName && argc is >= 1 and <= 3)
                    {
                        // Environment (fire_rt.h: envCall; the system's part in the platform layer)
                        int first = d - argc;
                        E($"{S(first)} = envCall({S(first)}.i, {(argc > 1 ? S(first + 1) : "Undef()")}, {(argc > 2 ? S(first + 2) : "Undef()")}, &{OwnerList()});");
                        Check();
                        d = first + 1; SetR(first, true); return Next();
                    }
                    if (native == MathMethods.NativeName && argc is 2 or 3)
                    {
                        int first = d - argc;
                        E($"{S(first)} = mathCall({S(first)}.i, {S(first + 1)}, {(argc == 3 ? S(first + 2) : "Undef()")});");
                        Check();
                        d = first + 1; SetR(first, false); return Next();
                    }
                    if (native == JsonMethods.NativeName && argc is 2 or 3)
                    {
                        UseJson();
                        _callsJsonEngine = true;
                        int first = d - argc;
                        E($"{S(first)} = fire_jsonCall({argc}, {S(first)}, {S(first + 1)}, {(argc == 3 ? S(first + 2) : "Undef()")}, {JsonCallType(f)}, &{OwnerList()});");
                        Check();
                        d = first + 1; SetR(first, true); return Next();
                    }
                    if (native == CharMethods.NativeName && argc == 2)
                    {
                        int first = d - 2;
                        E($"{S(first)} = charCall({S(first)}.i, {S(first + 1)}, &{OwnerList()});");
                        d = first + 1; SetR(first, true); return Next();
                    }
                    if (native.StartsWith("__refl_") && ReflectionNatives.Contains(native))
                    {
                        UseReflection();
                        UseReflectionLevel(native); _reflNativesUsed.Add(native);
                        if (native is "__refl_probe" or "__refl_silence" or "__refl_silence_handle") UseProbes();
                        int first = d - argc;
                        string callerArg = "g_reflCaller";
                        string list = "&" + OwnerList();
                        string call = native switch
                        {
                            "__refl_class_name" when argc == 1 => $"rf_class_name({S(first)}, {list})",
                            "__refl_class_info" when argc == 1 => $"rf_class_info({S(first)}, {list})",
                            "__refl_members" when argc == 1 => $"rf_members({S(first)}, {list})",
                            "__refl_classes" when argc == 0 => $"rf_classes({list})",
                            "__refl_is_sub" when argc == 2 => $"rf_is_sub({S(first)}, {S(first + 1)})",
                            "__refl_get" when argc == 2 => $"rf_get({S(first)}, {S(first + 1)}, {list}, {callerArg})",
                            "__refl_set" when argc == 3 => $"rf_set({S(first)}, {S(first + 1)}, {S(first + 2)}, {list}, {callerArg})",
                            "__refl_call" when argc == 3 => $"rf_call({S(first)}, {S(first + 1)}, {S(first + 2)}, {list}, {callerArg})",
                            "__refl_new" when argc == 2 => $"rf_new({S(first)}, {S(first + 1)}, {list}, {callerArg})",
                            "__refl_has" when argc == 2 => $"rf_has({S(first)}, {S(first + 1)})",
                            "__refl_selector_path" when argc == 1 => $"rf_selector_path({S(first)}, {list})",
                            "__refl_member_kind" when argc == 2 => $"rf_member_kind({S(first)}, {S(first + 1)}, {list})",
                            "__refl_probe" when argc == 4 => $"rf_probe({S(first)}, {S(first + 1)}, {S(first + 2)}, {S(first + 3)}, {list}, {callerArg})",
                            "__refl_silence" when argc == 2 => $"rf_silence({S(first)}, {S(first + 1)})",
                            "__refl_silence_handle" when argc == 1 => $"rf_silence_handle({S(first)})",
                            "__refl_enum_name" when argc == 1 => $"rf_enum_name({S(first)}, {list})",
                            "__refl_enum_of" when argc == 2 => $"rf_enum_of({S(first)}, {S(first + 1)})",
                            _ => throw new NativeNotSupportedException($"native function '{native}' with {argc} argument(s) (called in {fn})"),
                        };
                        E($"{S(first)} = {call};");
                        Check();
                        d = first + 1; SetR(first, true); return Next();
                    }
                    if (native.StartsWith("__GRPHWin", StringComparison.Ordinal) && WindowsBridgeNatives.TryGetValue(native.Substring(9), out var winNative) && winNative.Argc == argc)
                    {
                        UseWindows(); _nativeImportsUsed.Add(NativeImports.Windows);
                        string name = native.Substring(6);
                        int first = d - argc;
                        if (name == "WinMgrStart")
                        {
                            // the thread of a window manager runs the program's cycles at its safe points: fire threads (the GIL, host services); a platform
                            // without threads has no such thread - Start says false and the program uses Run/Tick
                            if (!_target.Native.SupportsThreads)
                            {
                                E($"{S(first)} = Bool(false);");
                                d = first + 1; SetR(first, false); return Next();
                            }
                            UseThreads();
                        }
                        string args = string.Join(", ", Enumerable.Range(first, argc).Select(S).Concat(name is "WinNextEvent" or "WinSystemTheme" or "WinClipboardGetText" or "WinClipboardGetImage" or "WinClipboardGetFiles" ? new[] { "&" + OwnerList() } : Array.Empty<string>()));
                        E($"{S(first)} = {SharedNative($"fire_win_{name}", argc, args.EndsWith("&" + OwnerList(), StringComparison.Ordinal), NativeImports.Windows)}({args});");   // (fire_bridge_windows.h, part of this file)
                        Check();
                        d = first + 1; SetR(first, winNative.Reference); return Next();
                    }
                    if (native.StartsWith("__GRPH", StringComparison.Ordinal) && GraphicsBridgeNatives.TryGetValue(native.Substring(6), out var grphNative) && grphNative.Argc == argc)
                    {
                        UseGraphics(); _nativeImportsUsed.Add(NativeImports.Graphics);
                        string name = native.Substring(6);
                        int first = d - argc;
                        string args = string.Join(", ", Enumerable.Range(first, argc).Select(S).Concat(GraphicsNeedsList.Contains(name) ? new[] { "&" + OwnerList() } : Array.Empty<string>()));
                        E($"{S(first)} = {SharedNative($"fire_gfx_{name}", argc, GraphicsNeedsList.Contains(name), NativeImports.Graphics)}({args});");   // (fire_bridge_graphics.h, part of this file)
                        Check();
                        d = first + 1; SetR(first, grphNative.Reference); return Next();
                    }
                    if (_packageNatives.TryGetValue(native, out var packageNative) && packageNative.Argc == argc)
                    {
                        if (_nativeImportOf.TryGetValue(native, out var nativeImport)) _nativeImportsUsed.Add(nativeImport);
                        // a native of a package (C++ in the generated file): `Value cpp(Value a, ...)`, with the list of the scope as the last argument if it asks for it
                        int first = d - argc;
                        string args = string.Join(", ", Enumerable.Range(first, argc).Select(S).Concat(packageNative.NeedsList ? new[] { "&" + OwnerList() } : Array.Empty<string>()));
                        if (packageNative.Throws) UseExceptions();   // its exception classes (`fireError`) need the exceptions of the runtime
                        if (packageNative.Host) NeedTimeObj();        // a function the VM runs itself waits and takes times: a TimeSpan is read from its `ticks`
                        // (a package in C: its function is part of this file; in C++: through the thunk into fire_natives.cpp)
                        E($"{S(first)} = {(packageNative.IsC ? SharedNative(packageNative.Function, argc, packageNative.NeedsList, _nativeImportOf.GetValueOrDefault(native) ?? "") : NativeThunk(packageNative.Function, argc, packageNative.NeedsList))}({args});");
                        Check();
                        // a function of the host (`Sleep`) ends at terminate: leave now, not at the next loop
                        if (packageNative.Host && _usesThreads && sb != null) E($"if (FIRE_UNLIKELY(pollSignals())) goto {ExitLabel(f, st, locals)};");
                        d = first + 1; SetR(first, packageNative.ReturnsReference); return Next();
                    }
                    throw new NativeNotSupportedException($"native function '{native}' (called in {fn})");
                }
                case OpCode.CallExtern:
                {
                    // a C function of a library (SPEC 8.1.1): declared with its symbol name, called with the arguments converted by their declared types
                    string externName = chunk.Constants[ins.A[0]].AsString();
                    int argc = ins.A[1];
                    Need(argc);
                    if (!_program.Program.ExternSignatures.TryGetValue(externName, out var sig) || sig.LibName == null)
                        throw new NativeNotSupportedException($"extern function '{externName}' is not linked to a library with #extern \"lib\" ({fn})");
                    if (sig.ParamTypes.Count != argc) throw new NativeNotSupportedException($"extern function '{externName}': {sig.ParamTypes.Count} parameter(s) declared, called with {argc}");
                    int first = d - argc;
                    var locals2 = new StringBuilder();
                    var frees = new List<string>();   // (after the call, in reverse order: a pointer writes its value back, a string is freed)
                    var args2 = new List<string>();
                    var ctypes = new List<string>();
                    for (int i = 0; i < argc; i++)
                    {
                        var t = sig.ParamTypes[i] ?? throw new NativeNotSupportedException($"extern function '{externName}': a parameter type is missing");
                        string ctype = ExternCType(t, externName);
                        ctypes.Add(ctype);
                        string v = S(first + i);
                        if (t.PointerDepth > 0) { locals2.Append($"ExternPtr p{i}; externPtrInit(&p{i}, {v}); "); args2.Add($"p{i}.raw"); frees.Insert(0, $"externPtrFree(&p{i}); "); }
                        else if (t.BaseName == "string") { locals2.Append($"ExternString p{i}; externStringInit(&p{i}, {v}); "); args2.Add($"p{i}.p"); frees.Insert(0, $"externStringFree(&p{i}); "); }
                        else if (t.BaseName == "float") args2.Add($"({ctype})externFloat({v})");
                        else args2.Add($"({ctype})externInt({v})");
                    }
                    string retC = sig.ReturnType == null ? "void" : ExternCType(sig.ReturnType, externName);
                    _externDecls[externName] = $"extern {retC} fire_ext_{Mangle(externName)}({string.Join(", ", ctypes)}) __asm__(FIRE_SYM({CString(externName)}));   // library: {sig.LibName}";
                    string call = $"fire_ext_{Mangle(externName)}({string.Join(", ", args2)})";
                    string result;
                    string freeAll = string.Concat(frees);
                    if (sig.ReturnType == null) { E($"{{ {locals2}{call}; {freeAll}}}"); result = "Undef()"; }
                    else if (sig.ReturnType.PointerDepth > 0) { E($"{{ {locals2}{S(first)} = Int((int64_t)(intptr_t){call}); {freeAll}}}"); result = ""; }
                    else
                    {
                        string conv = sig.ReturnType.BaseName switch
                        {
                            "bool" => $"Bool({call} != 0)",
                            "float" => $"Float((Real){call})",
                            "char" => $"Char((uint32_t){call})",
                            "string" => $"externResultString({call}, &{OwnerList()})",
                            _ => $"Int((int64_t){call})",
                        };
                        E($"{{ {locals2}{S(first)} = {conv}; {freeAll}}}");
                        result = "";
                    }
                    if (result.Length > 0) E($"{S(first)} = {result};");
                    d = first + 1; SetR(first, sig.ReturnType?.BaseName == "string"); return Next();
                }
                case OpCode.CallStaticMethod:
                {
                    int argc = ins.A[2];
                    Need(argc);
                    string cls = Str(ins.A[0]), method = Str(ins.A[1]);
                    var rc = FindClass(cls);
                    if (ReflectionPrelude.HelperClasses.Contains(cls)) NoteReflectionLiterals(f);   // (`Reflect.New("Dog")`: the names stay, stripping)
                    var staticNames = NamesBefore();
                    if (rc.IsTypeOverloaded(method, argc))
                    {
                        if (staticNames != null) throw new NativeNotSupportedException($"named arguments for {cls}.{method}, which is overloaded by type");
                        if (ReflectionPrelude.HelperClasses.Contains(cls)) E(ReflectionCallerAssign(f).TrimEnd());
                        return OverloadedCall(rc, method, argc, FuncKind.Static, d - argc, "Undef()", "Static method");
                    }
                    var proto = rc.FindMethodWithAccess(method, argc).Proto
                        ?? throw new NativeNotSupportedException($"{cls}.{method} with {argc} argument(s) not found");
                    if (!proto.IsStatic) throw new NativeNotSupportedException($"{cls}.{method} is not static");
                    if (rc.FindMethodWithAccess(method, argc) is { } staticFound)
                        AccessCheck(staticFound.DeclaringClass!, staticFound.Access, $"Static method '{method}' of '{staticFound.DeclaringClass!.Name}' is {DescribeAccess(staticFound.Access)} and cannot be called from here.");
                    var target = GetFunc(proto, FuncKind.Static);
                    var (callee, specCondition) = staticNames != null ? (target, null) : Specialize(f, ins, target, d - argc, argc, S, Ty);
                    if (ReflectionPrelude.HelperClasses.Contains(cls)) E(ReflectionCallerAssign(f).TrimEnd());
                    long mask = ArgMask();
                    string specTest = specCondition != null ? $"({specCondition}) ? {callee.Name}(" : "";
                    ArgsBefore(d - argc, argc, mask);
                    var (dpre, dargs) = staticNames != null ? ("", "") : DefaultArgs(proto, argc, "Undef()", DefaultsList(proto, argc));
                    string staticArgs = (CallArgs(d - argc, argc, mask, proto.RefMask) + dargs).TrimStart(',', ' ');
                    if (staticNames != null)
                    {
                        (dpre, staticArgs) = NamedCallArgsOrFail(proto, d - argc, argc, staticNames, mask, "Undef()", $"{cls}.{method}");
                        staticArgs = staticArgs.TrimStart(',', ' ');
                    }
                    string staticCall = specCondition != null ? $"{specTest}{staticArgs}) : {target.Name}({staticArgs})" : $"{callee.Name}({staticArgs})";
                    E($"{(dpre.Length > 0 ? "{ " + dpre : "")}{S(d - argc)} = {staticCall};{(dpre.Length > 0 ? " }" : "")}");
                    ArgsAfter(argc, mask, S(d - argc));
                    Check();
                    AdoptResult(d - argc, specCondition == null ? ReturnType(callee) : JoinTypes(ReturnType(callee), ReturnType(target)));
                    d = d - argc + 1; return Next();
                }
                case OpCode.Return:
                {
                    Need(1);
                    if (f.IsTop) throw new NativeNotSupportedException("return in top-level code");
                    bool isCtor = f.Kind == FuncKind.Ctor;
                    if (!isCtor) JoinVarType(ReturnKey(f), Ty(d - 1));
                    bool anything = st.Scopes.Any(sc => f.NeedsList.Contains(sc.Id) || ScopeVariables(f, sc.Id, locals).Any(v => VarRef(VarKey(f, v))));
                    bool tryActive = st.Region != null || st.Handlers.Count > 0;
                    if (!isCtor && !tryActive && !R(d - 1) && !anything) { E($"return {S(d - 1)};"); return new StepResult(false, null); }
                    // the value is on its way to the caller: it keeps a count, and an object that the leaving scopes owned goes along
                    E($"{{ Value r = {S(d - 1)}; (void)r;");
                    var edges = ReturnSeq(f, st, R(d - 1), true, sb == null ? null : x => E("  " + x), locals);
                    E("}");
                    return new StepResult(false, null, edges);
                }
                case OpCode.Halt:
                    if (!f.IsTop) throw new NativeNotSupportedException("Halt in a function");
                    HaltCode(f, E);
                    E("return;");
                    return new StepResult(false, null);
                case OpCode.SetTimeout:
                case OpCode.SetAutoSync:
                    // `#timeout` sets the wait time of the waiting functions of the devices; `#nosync` only matters for programs with threads
                    if (ins.Op == OpCode.SetTimeout) { Need(1); E($"setDefaultTimeout({S(d - 1)});"); NeedTimeObj(); d--; }
                    else if (_usesThreads && ins.A[0] == 0) E("g_autoSync = false; g_attnMask &= ~ATTN_SECT;");
                    return Next();

                // ---------------------------------------------------------------------------------------------------
                // Objects
                // ---------------------------------------------------------------------------------------------------
                case OpCode.LoadThis:
                    RequireSelf();
                    if (f.Kind == FuncKind.Lambda)
                    {
                        // a lambda has no `this` of its own (SPEC 4.2), only an `on` target: without one it is an ArgumentException, like in the VM
                        E($"{S(d)} = FIRE_UNLIKELY(self.kind == K_Undefined) ? fireError(\"ArgumentException\", \"'this' is not bound at this point.\") : self;");
                        Check();
                    }
                    else E($"{S(d)} = self;");
                    SetR(d, IsExtensionMethod(f.Proto));
                    if (f.HasSelf && !IsExtensionMethod(f.Proto) && f.Chunk.OwnerClass is { } thisClass) { SetT(d, ObjType(thisClass)); _kindsKept = true; }
                    d++; return Next();
                case OpCode.GetField:
                {
                    Need(1);
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    if (ObjSlot(d - 1) && DirectFieldIndex(ObjClass(Ty(d - 1)), field, f) is int fieldIndex)
                    {
                        // an object of a known class: the field by its index (a destroyed object throws, like gf_; undefined goes to gf_, which reports it)
                        if (IsNullable(Ty(d - 1))) E($"{S(d - 1)} = FIRE_LIKELY({S(d - 1)}.kind == K_Class && leafAlive({S(d - 1)})) ? objFields(objAt({S(d - 1)}))[{fieldIndex}] : {FieldFn("gf", field)}({S(d - 1)}{FieldTail(field, f, () => "NULL")});");
                        else E($"{S(d - 1)} = FIRE_LIKELY(leafAlive({S(d - 1)})) ? objFields(objAt({S(d - 1)}))[{fieldIndex}] : destroyedError({S(d - 1)});");
                        Check();
                    }
                    else if (ObjSlot(d - 1) && !IsNullable(Ty(d - 1)) && DirectAccessor(ObjClass(Ty(d - 1)), field, false, f) is { } getter)
                    {
                        // a property of an object of a known class: its getter itself (the access is known to be allowed), like the case of gf_
                        string getterName = GetFunc(getter, FuncKind.Method).Name;
                        E($"if (FIRE_UNLIKELY(!leafAlive({S(d - 1)}))) {S(d - 1)} = destroyedError({S(d - 1)}); else {{ Value r_ = {getterName}({S(d - 1)}); adopt(r_, &{OwnerList()}); {S(d - 1)} = r_; }}");
                        Check();
                    }
                    else if (HasProperty(field) || RestrictedField(field)) { E($"{S(d - 1)} = {FieldFn("gf", field)}({S(d - 1)}{FieldTail(field, f, () => "&" + OwnerList())});"); Check(); }
                    else { E($"{S(d - 1)} = {FieldFn("gf", field)}({S(d - 1)}{(Modular ? FieldTail(field, f, () => "&" + OwnerList()) : "")});"); Check(); }   // (a destroyed object throws)
                    int fieldType = FieldType(field);
                    if (fieldType == TypeUnknown && SpecFieldType(f, ins, field) is { } specType) fieldType = specType;
                    if (fieldType != TypeUnknown) { SetR(d - 1, false); SetT(d - 1, fieldType); _kindsKept = true; }
                    else SetR(d - 1, true);
                    return Next();
                }
                case OpCode.SetField:
                {
                    Need(2);
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    if (ObjSlot(d - 2) && DirectSetField(ObjClass(Ty(d - 2)), field, S(d - 2), S(d - 1), Ty(d - 1), f, IsNullable(Ty(d - 2))) is { } direct) E(direct);
                    else if (ObjSlot(d - 2) && !IsNullable(Ty(d - 2)) && DirectAccessor(ObjClass(Ty(d - 2)), field, true, f) is { } setter)
                        E($"if (FIRE_UNLIKELY(!leafAlive({S(d - 2)}))) destroyedError({S(d - 2)}); else {{ Value r_ = {GetFunc(setter, FuncKind.Method).Name}({S(d - 2)}, {S(d - 1)}); adopt(r_, &{OwnerList()}); }}");
                    else E($"{FieldFn("sf", field)}({S(d - 2)}, {S(d - 1)}{FieldTail(field, f, () => "&" + OwnerList())});");
                    Check();
                    E($"{S(d - 2)} = {S(d - 1)};");
                    SetR(d - 2, R(d - 1));
                    d--; return Next();
                }
                case OpCode.SetFieldOnThis:
                {
                    Need(1); RequireSelf();
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    if (f.HasSelf && !IsExtensionMethod(f.Proto) && f.Chunk.OwnerClass is { } selfClass && DirectSetField(selfClass, field, "self", S(d - 1), Ty(d - 1), f) is { } direct) E(direct);
                    else if (f.HasSelf && !IsExtensionMethod(f.Proto) && f.Chunk.OwnerClass is { } selfClass2 && DirectAccessor(selfClass2, field, true, f) is { } selfSetter)
                        E($"if (FIRE_UNLIKELY(!leafAlive(self))) destroyedError(self); else {{ Value r_ = {GetFunc(selfSetter, FuncKind.Method).Name}(self, {S(d - 1)}); adopt(r_, &{OwnerList()}); }}");
                    else E($"{FieldFn("sf", field)}(self, {S(d - 1)}{FieldTail(field, f, () => "&" + OwnerList())});");
                    Check();
                    d--; return Next();
                }
                case OpCode.NewObject:
                case OpCode.NewObjectOwned:
                {
                    int argc = ins.A[1];
                    bool owned = ins.Op == OpCode.NewObjectOwned;
                    Need(argc + (owned ? 1 : 0));
                    var cls = RegisterClass(Str(ins.A[0]));
                    var newNames = NamesBefore();
                    if (newNames != null && cls.Rc.IsConstructorTypeOverloaded(argc)) throw new NativeNotSupportedException($"named arguments for the constructors of {cls.Rc.Name}, which are overloaded by type");
                    if (cls.Rc.IsConstructorTypeOverloaded(argc)) return OverloadedCall(cls.Rc, "new", argc, FuncKind.Ctor, d - argc - (owned ? 1 : 0), "", "", owned, cls);
                    var ctor = cls.Rc.FindConstructor(argc);
                    if (ctor == null)
                    {
                        // no constructor takes that many arguments: an ArgumentException at run time (like the VM)
                        int errorSlot = d - argc - (owned ? 1 : 0);
                        E($"{S(errorSlot)} = fireError(\"ArgumentException\", {CString(DescribeConstructorNotFound(cls.Rc, argc))});");
                        Check();
                        SetR(errorSlot, false);
                        d = errorSlot + 1; return Next();
                    }
                    AccessCheck(cls.Rc, ctor.Access ?? AccessModifier.Public, $"Constructor of '{cls.Rc.Name}' is {DescribeAccess(ctor.Access ?? AccessModifier.Public)} and cannot be called from here.");
                    var target = GetFunc(ctor, FuncKind.Ctor);
                    int slot = d - argc - (owned ? 1 : 0);
                    string ownerExpr = owned ? $"&asObj({S(slot)})->owned" : "&" + OwnerList();
                    long mask = ArgMask();
                    ArgsBefore(d - argc, argc, mask);
                    var (dpre, dargs) = newNames != null ? NamedCallArgsOrFail(ctor, d - argc, argc, newNames, mask, "o", "new " + cls.Rc.Name) : DefaultArgs(ctor, argc, "o", "&" + OwnerList());
                    string ctorArgs = newNames != null ? "" : CallArgs(d - argc, argc, mask, ctor.RefMask);
                    string typeArgsSet = _pendingNewTArgs != null ? $"asObj(o)->targs = {_pendingNewTArgs}; " : "";
                    _pendingNewTArgs = null;
                    E($"{{ Value o = newObject({ClassIdExpr(cls)}, {cls.Fields.Count}, {ownerExpr}); {typeArgsSet}{(cls.Rc.IsActor ? "markActor(o); " : "")}{dpre}{target.Name}(o{ctorArgs}{dargs}); {S(slot)} = o; }}");
                    ArgsAfter(argc, mask, S(slot));
                    Check();
                    SetR(slot, false);
                    SetT(slot, ObjType(cls.Rc)); _kindsKept = true;
                    d = slot + 1; return Next();
                }
                case OpCode.ConstructBase:
                {
                    int argc = ins.A[1];
                    Need(argc); RequireSelf();
                    var rc = FindClass(Str(ins.A[0]));
                    if (rc.IsConstructorTypeOverloaded(argc)) return OverloadedCall(rc, "new", argc, FuncKind.Ctor, d - argc, "self", "");
                    var ctor = rc.FindConstructor(argc) ?? throw new NativeNotSupportedException($"class {rc.Name} has no constructor with {argc} argument(s)");
                    var target = GetFunc(ctor, FuncKind.Ctor);
                    OwnerList();
                    long mask = ArgMask();
                    ArgsBefore(d - argc, argc, mask);
                    var (dpre, dargs) = DefaultArgs(ctor, argc, "self", "&" + OwnerList());
                    E($"{(dpre.Length > 0 ? "{ " + dpre : "")}{target.Name}(self{CallArgs(d - argc, argc, mask, ctor.RefMask)}{dargs});{(dpre.Length > 0 ? " }" : "")}");
                    ArgsAfter(argc, mask, "Undef()");
                    Check();
                    E($"{S(d - argc)} = Undef();");
                    SetR(d - argc, false);
                    d = d - argc + 1; return Next();
                }
                case OpCode.CallProtoWithThis:
                {
                    int argc = ins.A[1];
                    Need(argc + 1);
                    var proto = chunk.Functions[ins.A[0]];
                    if (proto.ParamCount != argc) throw new NativeNotSupportedException($"initializer with {proto.ParamCount} parameters called with {argc}");
                    var target = GetFunc(proto, FuncKind.Init);
                    int slot = d - argc - 1;
                    E($"{S(slot)} = {target.Name}({S(slot)}{Args(d - argc, argc)});");
                    Check();
                    AdoptResult(slot, ReturnType(target));
                    d = slot + 1; return Next();
                }
                case OpCode.CallMethod:
                {
                    int argc = ins.A[1];
                    Need(argc + 1);
                    string method = Str(ins.A[0]);
                    int slot = d - argc - 1;
                    // (`t.Find("Bark")` on a Type, Member, ...: the names stay, stripping)
                    if (_usesReflection && ReflectionMethodNames.Contains(method) && ObjSlot(slot) && IsReflectionHelperClass(ObjClass(Ty(slot)))) NoteReflectionLiterals(f);
                    if (NamesBefore() is { } methodNames)
                    {
                        // named arguments: per class of the receiver its method with the arguments in the order of its parameters (no dispatcher - its
                        // arguments are positional); a class whose method does not have these parameters, and any other receiver, is an ArgumentException
                        var classes = (ObjSlot(slot) ? InstancesOf(ObjClass(Ty(slot))) : _classList).ToList();
                        long nmask = ArgMask();
                        ArgsBefore(d - argc, argc, nmask);
                        var line = new StringBuilder($"if ({S(slot)}.kind == K_Class && leafAlive({S(slot)})) {{ switch (objAt({S(slot)})->cls) {{ ");
                        foreach (var c in classes)
                        {
                            if (c.Rc.IsTypeOverloaded(method, argc) || c.Rc.FindMethodWithAccess(method, argc) is not { Proto: { IsStatic: false } proto } namedFound) continue;
                            if (CheckAccess && !AccessAllowed(f.Chunk.OwnerClass, namedFound.DeclaringClass!, namedFound.Access))
                            {
                                // (known at compile time: this code may not call the method of that class)
                                line.Append($"case {c.Id}: {S(slot)} = accessDenied({CString($"Method '{method}' of '{namedFound.DeclaringClass!.Name}' is {DescribeAccess(namedFound.Access)} and cannot be called from here.")}); break; ");
                                continue;
                            }
                            if (NamedCallArgs(proto, d - argc, argc, methodNames, nmask, S(slot)) is not { } fitting) continue;
                            var (npre, nargs) = fitting;
                            line.Append($"case {c.Id}: {{ {npre}{ClearCallTArgs(proto)}{S(slot)} = {GetFunc(proto, FuncKind.Method).Name}({S(slot)}{nargs}); break; }} ");
                        }
                        var anyProto = classes.Select(c => c.Rc.FindMethodWithAccess(method, argc).Proto).FirstOrDefault(p => p is { IsStatic: false });
                        string noFit = CString(anyProto != null ? NamedMismatch(anyProto, argc, methodNames) : $"Named arguments do not fit '{method}'.");
                        string notObject = CString($"Named arguments are not possible for the built-in method '{method}'.");
                        line.Append($"default: {S(slot)} = fireError(\"ArgumentException\", {noFit}); break; }} }} else if ({S(slot)}.kind == K_Class) {S(slot)} = destroyedError({S(slot)}); else ");
                        // a base-type extension (`class extends string`): its method by the kind of the receiver
                        foreach (var (kindCpp, extProto, extRc) in ExtensionMethods(method, argc))
                        {
                            if (extRc.IsTypeOverloaded(method, argc)) continue;
                            string fits = NamedCallArgs(extProto, d - argc, argc, methodNames, nmask, S(slot)) is { } extArgs
                                ? $"{{ {extArgs.Pre}{S(slot)} = {GetFunc(extProto, FuncKind.Method).Name}({S(slot)}{extArgs.Args}); }}"
                                : $"{S(slot)} = fireError(\"ArgumentException\", {CString(NamedMismatch(extProto, argc, methodNames))});";
                            line.Append($"if ({S(slot)}.kind == {kindCpp}) {fits} else ");
                        }
                        line.Append($"{S(slot)} = fireError(\"ArgumentException\", {notObject});");
                        E(line.ToString());
                        ArgsAfter(argc, nmask, S(slot));
                        Check();
                        AdoptResult(slot);
                        d = slot + 1; return Next();
                    }
                    if (OwnMethodId(method, argc) is { } ownMethodId && !AnyClassHasMethod(method, argc))
                    {
                        string owner = OwnerList();
                        // the built-in ownership methods of objects, arrays and buffers (a class that declares one itself takes precedence)
                        if (ownMethodId == "OM_TakeUpwards" && !_usesTakeUpwards) { _usesTakeUpwards = true; _usesGlobalOwn = true; _version++; }
                        if (ownMethodId == "OM_TakeGlobal" && !_usesGlobalOwn) { _usesGlobalOwn = true; _version++; }
                        int baseArgs = OwnMethodBaseArgs(ownMethodId);
                        string targetArg = baseArgs == 1 ? S(slot + 1) : "Undef()";
                        bool isTry = OwnMethodIsTry(method);
                        if (isTry && !_usesTryDepth) { _usesTryDepth = true; _version++; }
                        if (argc == baseArgs && !isTry) E($"ownMethod({ownMethodId}, {S(slot)}, {targetArg}, &{owner});");
                        else
                        {
                            // with a `Takes` mode (the scopes of the running call tell what is "local") or with `try` (only the owner moves it)
                            var scopeLists = st.Scopes.Where(sc => f.NeedsList.Contains(sc.Id)).Select(sc => "&" + ListName(sc.Id)).ToList();
                            string localsDecl = scopeLists.Count == 0 ? "" : $"OwnList* const L_[] = {{{string.Join(", ", scopeLists)}}}; ";
                            string modeArg = argc > baseArgs ? S(slot + 1 + baseArgs) : "Int(0)";
                            if (argc > baseArgs && !_usesTakeEnumerate) { _usesTakeEnumerate = true; _version++; }
                            string call = isTry
                                ? $"{S(slot)} = Bool(ownMethodTry({ownMethodId}, {S(slot)}, {targetArg}, {modeArg}, &{owner}, {(scopeLists.Count == 0 ? "NULL" : "L_")}, {scopeLists.Count}, {(f.HasSelf ? "self" : "Undef()")}));"
                                : $"ownMethodT({ownMethodId}, {S(slot)}, {targetArg}, {modeArg}, &{owner}, {(scopeLists.Count == 0 ? "NULL" : "L_")}, {scopeLists.Count});";
                            E($"{{ {localsDecl}{call} }}");
                            if (isTry) { SetR(slot, false); d = slot + 1; return Next(); }
                        }
                        E($"{S(slot)} = Undef();");
                        SetR(slot, false);
                        d = slot + 1; return Next();
                    }
                    bool passesAddresses = Enumerable.Range(0, Math.Min(argc, 16)).Any(i => (ArgMask() >> (4 * i) & 15) == 3);
                    if (ObjSlot(slot) && !passesAddresses && !(_usesReflection && ReflectionMethodNames.Contains(method))
                        && DirectMethod(ObjClass(Ty(slot)), method, argc, f) is { } directProto)
                    {
                        // an object of a known class whose method no derived class overrides: the method itself, not the dispatcher (a destroyed object throws, like there)
                        var target = GetFunc(directProto, FuncKind.Method);
                        var (callee, specCondition) = Specialize(f, ins, target, d - argc, argc, S, Ty);
                        long mask = ArgMask();
                        string specTest = specCondition != null ? $"({specCondition}) ? {callee.Name}(" : "";
                        ArgsBefore(d - argc, argc, mask);
                        var (dpre, dargs) = DefaultArgs(directProto, argc, S(slot), DefaultsList(directProto, argc));
                        // (a receiver that may be undefined: the MissingMemberException of the dispatcher, which has nothing for undefined)
                        string notObject = IsNullable(Ty(slot)) ? $"if (FIRE_UNLIKELY({S(slot)}.kind != K_Class)) {S(slot)} = missingMethod({S(slot)}, {CString(method)}, {argc}); else " : "";
                        string methodArgs = $"{S(slot)}{CallArgs(d - argc, argc, mask, directProto.RefMask)}{dargs}";
                        string methodCall = specCondition != null ? $"{specTest}{methodArgs}) : {target.Name}({methodArgs})" : $"{callee.Name}({methodArgs})";
                        E($"{{ {dpre}{notObject}if (FIRE_UNLIKELY(!leafAlive({S(slot)}))) {S(slot)} = destroyedError({S(slot)}); else {S(slot)} = {methodCall}; }}");
                        ArgsAfter(argc, mask, S(slot));
                        Check();
                        AdoptResult(slot, specCondition == null ? ReturnType(callee) : JoinTypes(ReturnType(callee), ReturnType(target)));
                        d = slot + 1; return Next();
                    }
                    if (!passesAddresses && !WrapDispatchers && OwnMethodId(method, argc) == null && !(_usesReflection && ReflectionMethodNames.Contains(method))
                        && InterfaceOfType(Ty(slot)) is { } receiverInterface && InterfaceDispatchWorthwhile(receiverInterface, method, argc))
                    {
                        // a value of an interface type: its classes implement the method publicly (E4063) - their own dispatcher, no access check, no caller
                        _interfaceDispatchers.Add((receiverInterface, method, argc));
                        long imask = ArgMask();
                        ArgsBefore(d - argc, argc, imask);
                        E($"{S(slot)} = {InterfaceDispatcherName(receiverInterface, method, argc)}({S(slot)}{Args(d - argc, argc)});");
                        ArgsAfter(argc, imask, S(slot));
                        Check();
                        AdoptResult(slot, DispatchResultType(method, argc));
                        d = slot + 1; return Next();
                    }
                    if (!passesAddresses && !WrapDispatchers && OwnMethodId(method, argc) == null
                        && !(_usesReflection && ReflectionMethodNames.Contains(method))
                        && HintBefore(f, ins) is { IsConstructor: false } callHint && callHint.Method == method)
                    {
                        // the overload the type checker chose (OverloadHint): when the arguments are what it assumed, the method of the object's class runs
                        // directly - no scoring; otherwise (and for any other receiver) the dispatcher
                        var classes = ObjSlot(slot) ? InstancesOf(ObjClass(Ty(slot))).ToList()
                            : _classList.Where(c => { for (var r = c.Rc; r != null; r = r.Base) if (r.Name == callHint.DeclaringClass) return true; return false; }).ToList();
                        // (a class whose method this code may not call goes to the dispatcher, which says so)
                        var hinted = classes.Where(c => MethodAllowedOn(c.Rc, method, argc, f)).Select(c => (c.Id, Proto: HintTarget(callHint, c.Rc))).Where(t => t.Proto is { IsStatic: false }).ToList();
                        if (hinted.Count > 0)
                        {
                            _dispatchers.Add((method, argc));
                            string hextra = DispatchTail(method, argc, DispatchNeedsList(method, argc) ? "&" + OwnerList() : "", CallerId(f));
                            long hmask = ArgMask();
                            ArgsBefore(d - argc, argc, hmask);
                            string hargs = Args(d - argc, argc);
                            string dispatch = $"{CallFn(method, argc)}({S(slot)}{hargs}{hextra})";
                            var line = new StringBuilder($"if ({S(slot)}.kind == K_Class && leafAlive({S(slot)}) && {HintHolds(callHint, i => S(d - argc + i))}) {{ g_explicitTArgs = 0; switch (objAt({S(slot)})->cls) {{ ");
                            foreach (var group in hinted.GroupBy(t => t.Proto!))
                            {
                                var target = GetFunc(group.Key, FuncKind.Method);
                                var (hpre, hdefaults) = DefaultArgs(group.Key, argc, S(slot), DefaultsList(group.Key, argc));
                                line.Append(string.Concat(group.Select(t => $"case {t.Id}: "))).Append($"{{ {hpre}{ClearCallTArgs(group.Key)}{S(slot)} = {target.Name}({S(slot)}{hargs}{hdefaults}); break; }} ");
                            }
                            line.Append($"default: {S(slot)} = {dispatch}; break; }} }} else {S(slot)} = {dispatch};");
                            E(line.ToString());
                            ArgsAfter(argc, hmask, S(slot));
                            Check();
                            AdoptResult(slot, DispatchResultType(method, argc));
                            d = slot + 1; return Next();
                        }
                    }
                    _dispatchers.Add((method, argc));
                    if (passesAddresses) _refCallSites.Add((method, argc));
                    int resultType = DispatchResultType(method, argc);
                    string extra = DispatchTail(method, argc, DispatchNeedsList(method, argc) ? "&" + OwnerList() : "", CallerId(f));
                    long dmask = ArgMask();
                    if (_usesReflection && ReflectionMethodNames.Contains(method)) E(ReflectionCallerAssign(f).TrimEnd());
                    ArgsBefore(d - argc, argc, dmask);
                    if (OwnMethodId(method, argc) is { } dispatchedOwn && argc > OwnMethodBaseArgs(dispatchedOwn))
                    {
                        // an ownership method with a `Takes` mode that goes through the dispatcher (some class declares a method of this name): the built-in fallback there needs the scopes of this call
                        var dispatchScopes = st.Scopes.Where(sc => f.NeedsList.Contains(sc.Id)).Select(sc => "&" + ListName(sc.Id)).ToList();
                        if (dispatchScopes.Count > 0)
                            E($"{{ OwnList* const L_[] = {{{string.Join(", ", dispatchScopes)}}}; g_scopeLists = L_; g_scopeCount = {dispatchScopes.Count};");
                        E($"{S(slot)} = {CallFn(method, argc)}({S(slot)}{Args(d - argc, argc)}{extra});");
                        if (dispatchScopes.Count > 0) E("g_scopeLists = NULL; g_scopeCount = 0; }");
                    }
                    else
                    E($"{S(slot)} = {CallFn(method, argc)}({S(slot)}{Args(d - argc, argc)}{extra});");
                    ArgsAfter(argc, dmask, S(slot));
                    Check();
                    AdoptResult(slot, resultType);
                    d = slot + 1; return Next();
                }
                case OpCode.CallBaseMethod:
                {
                    int argc = ins.A[2];
                    Need(argc); RequireSelf();
                    var rc = FindClass(Str(ins.A[0]));
                    string method = Str(ins.A[1]);
                    var baseNames = NamesBefore();
                    if (baseNames != null && rc.IsTypeOverloaded(method, argc)) throw new NativeNotSupportedException($"named arguments for base.{method}, which is overloaded by type");
                    if (rc.IsTypeOverloaded(method, argc)) return OverloadedCall(rc, method, argc, FuncKind.Method, d - argc, "self", "Method");
                    var proto = rc.FindMethodWithAccess(method, argc).Proto ?? throw new NativeNotSupportedException($"{rc.Name}.{method} with {argc} argument(s) not found");
                    if (rc.FindMethodWithAccess(method, argc) is { Proto: not null } baseFound)
                        AccessCheck(baseFound.DeclaringClass!, baseFound.Access, $"Method '{method}' of '{baseFound.DeclaringClass!.Name}' is {DescribeAccess(baseFound.Access)} and cannot be called from here.");
                    var target = GetFunc(proto, FuncKind.Method);
                    long mask = ArgMask();
                    ArgsBefore(d - argc, argc, mask);
                    var (dpre, dargs) = baseNames != null ? NamedCallArgsOrFail(proto, d - argc, argc, baseNames, mask, "self", "base." + method) : DefaultArgs(proto, argc, "self", DefaultsList(proto, argc));
                    string baseArgs = baseNames != null ? "" : CallArgs(d - argc, argc, mask, proto.RefMask);
                    E($"{(dpre.Length > 0 ? "{ " + dpre : "")}{S(d - argc)} = {target.Name}(self{baseArgs}{dargs});{(dpre.Length > 0 ? " }" : "")}");
                    ArgsAfter(argc, mask, S(d - argc));
                    Check();
                    AdoptResult(d - argc, ReturnType(target));
                    d = d - argc + 1; return Next();
                }
                case OpCode.GetStaticField:
                {
                    // no static field of that name: a static property (`get_Name`, SPEC 8.8)
                    if (FindClass(Str(ins.A[0])) is { } getRc && getRc.FindStaticFieldOwner(Str(ins.A[1])) == null
                        && getRc.FindMethodWithAccess("get_" + Str(ins.A[1]), 0) is { Proto: { IsStatic: true } staticGetter } getterFound)
                    {
                        AccessCheck(getterFound.DeclaringClass!, getterFound.Access, $"'get_{Str(ins.A[1])}' of '{getterFound.DeclaringClass!.Name}' is {DescribeAccess(getterFound.Access)} and cannot be accessed from here.");
                        E($"{S(d)} = {GetFunc(staticGetter, FuncKind.Static).Name}();");
                        Check();
                        AdoptResult(d);
                        d++; return Next();
                    }
                    if (FindClass(Str(ins.A[0])).FindFieldAccess(Str(ins.A[1])) is { } getAccess)
                        AccessCheck(getAccess.DeclaringClass, getAccess.Access, $"Static field '{Str(ins.A[1])}' of '{getAccess.DeclaringClass.Name}' is {DescribeAccess(getAccess.Access)} and cannot be accessed from here.");
                    string sfVar = StaticFieldVar(Str(ins.A[0]), Str(ins.A[1]));
                    E($"{S(d)} = {sfVar};");
                    SetR(d, VarRef(sfVar));
                    d++; return Next();
                }
                case OpCode.SetStaticField:
                case OpCode.SetStaticFieldOnInit:
                {
                    Need(1);
                    if (FindClass(Str(ins.A[0])) is { } setRc && setRc.FindStaticFieldOwner(Str(ins.A[1])) == null
                        && setRc.FindMethodWithAccess("set_" + Str(ins.A[1]), 1) is { Proto: { IsStatic: true } staticSetter } setterFound)
                    {
                        if (ins.Op == OpCode.SetStaticField)
                            AccessCheck(setterFound.DeclaringClass!, setterFound.Access, $"'set_{Str(ins.A[1])}' of '{setterFound.DeclaringClass!.Name}' is {DescribeAccess(setterFound.Access)} and cannot be accessed from here.");
                        E($"{{ Value r = {GetFunc(staticSetter, FuncKind.Static).Name}({S(d - 1)}); adopt(r, &{OwnerList()}); }}");
                        Check();
                        return Next();
                    }
                    // (the initializer of a static field runs for its class, whatever the access)
                    if (ins.Op == OpCode.SetStaticField && FindClass(Str(ins.A[0])).FindFieldAccess(Str(ins.A[1])) is { } setAccess)
                        AccessCheck(setAccess.DeclaringClass, setAccess.Access, $"Static field '{Str(ins.A[1])}' of '{setAccess.DeclaringClass.Name}' is {DescribeAccess(setAccess.Access)} and cannot be accessed from here.");
                    string sfVar = StaticFieldVar(Str(ins.A[0]), Str(ins.A[1]));
                    if (FindClass(Str(ins.A[0])).FindStaticFieldOwner(Str(ins.A[1]))?.FindFieldRequiredKind(Str(ins.A[1])) is { } staticKind and not 0)
                    {
                        E($"{S(d - 1)} = {GuardKindCall(S(d - 1), staticKind, $"Field '{Str(ins.A[1])}'")};");
                        Check();
                    }
                    if (FindClass(Str(ins.A[0])).FindStaticFieldOwner(Str(ins.A[1]))?.FindFieldRequiredType(Str(ins.A[1])) is { } staticClass && !FitsClassType(Ty(d - 1), staticClass))
                    {
                        E(ClassGuard(S(d - 1), staticClass, $"Field '{Str(ins.A[1])}'"));
                        Check();
                    }
                    StoreVar(sfVar, d - 1);
                    return Next();
                }

                // ---------------------------------------------------------------------------------------------------
                // Lambdas (capture by value, SPEC 4.2.1)
                // ---------------------------------------------------------------------------------------------------
                case OpCode.MakeLambda:
                case OpCode.MakeLambdaCapturing:
                {
                    bool capturing = ins.Op == OpCode.MakeLambdaCapturing;
                    bool hasOn = ins.A[1] != 0;
                    int captures = capturing ? ins.A[2] : 0;
                    Need(captures + (hasOn ? 1 : 0));
                    var proto = chunk.Functions[ins.A[0]];
                    var target = GetFunc(proto, FuncKind.Lambda, captures);
                    string defaultsInit = "";
                    if (proto.SelectorPath is { } selectorPath)
                    {
                        UseReflection();
                        if (!_selectorPaths.TryGetValue(selectorPath, out var selName)) { selName = $"{Px}kSel{_selectorPaths.Count}"; _selectorPaths[selectorPath] = selName; _version++; }
                        defaultsInit = $" l->sel = {selName}; l->nsel = {selectorPath.Length};";
                    }
                    if (proto.ParamDefaults.Any(p => p != null))
                    {
                        // the default values are functions of their own (with the `on` target as `this`); the lambda knows how to reach them
                        foreach (var dp in proto.ParamDefaults) if (dp != null) GetFunc(dp, FuncKind.Init);
                        int required = 0;
                        while (required < proto.ParamCount && (required >= proto.ParamDefaults.Count || proto.ParamDefaults[required] == null)) required++;
                        defaultsInit += $" l->nreq = {required}; l->dflt = {target.Name}_d;";
                        _lambdaDefaults.Add(target);
                    }
                    int first = d - captures - (hasOn ? 1 : 0);
                    var sbCaps = new StringBuilder();
                    for (int i = 0; i < captures; i++) sbCaps.Append($" lamCaps(l)[{i}] = {S(first + i)}; retain({S(first + i)});");
                    string lambdaTArgs = _pendingLambdaTArgs != null ? $" l->targs = {_pendingLambdaTArgs};" : "";
                    _pendingLambdaTArgs = null;
                    E($"{{ Lam* l = allocLam({proto.ParamCount}, {captures}, {target.Name}_t, &{OwnerList()}); l->on = {(hasOn ? S(d - 1) : "Undef()")};{defaultsInit}{lambdaTArgs}{sbCaps} {S(first)} = LamV(l); }}");
                    d = first + 1; SetR(first, true); return Next();
                }
                case OpCode.Call:
                {
                    int argc = ins.A[0];
                    Need(argc + 1);
                    int slot = d - argc - 1;
                    string owner = OwnerList();
                    var argList = argc == 0 ? "Undef()" : string.Join(", ", Enumerable.Range(slot + 1, argc).Select(S));
                    E($"{{ Value args[{Math.Max(argc, 1)}] = {{{argList}}}; {S(slot)} = {(DebugBuild ? "callLamV" : "callLam")}({S(slot)}, {argc}, args, &{owner}); }}");
                    Check();
                    AdoptResult(slot);
                    d = slot + 1; return Next();
                }
                case OpCode.GuardLocal:
                {
                    // the end of the prologue: the parameter holds its declared kind or undefined, in the unit that was checked before it
                    string v = Var(st, 0, ins.A[0], locals);
                    byte declared = (byte)((ins.A[1] & 0x7F) + 1 | ins.A[1] & DeclaredKind.UnitlessBit);
                    E($"{v} = {GuardKindCall(v, declared, Str(ins.A[2]))};");
                    Check();
                    int unit = ins.A[3] == 0xFFFF ? -1 : DemandedUnitId(Str(ins.A[3]));
                    byte kind = KindOfDeclared(declared);
                    JoinVarType(VarKey(f, v), kind == KindBool ? TypeOf(KindBool, 0) | NullableBit : unit < 0 ? TypeUnknown : TypeOf(kind, unit) | NullableBit);
                    _kindsKept = true;
                    return Next();
                }
                case OpCode.SetTypeArgs:
                {
                    // the type arguments of the next `new` (the object keeps them) or of the next call of a generic method (it takes them: TakeCallTypeArgs)
                    string typeArgs = TArgsExpr(f, Str(ins.A[0]));
                    if (ins.A[1] == 1) E($"g_callTArgs = {typeArgs};");
                    else if (ins.A[1] == 3) E($"g_callTArgs = {typeArgs}; g_explicitTArgs = {Str(ins.A[0]).Split('|').Length};");   // (written type arguments: only generic overloads count)
                    else if (ins.A[1] == 2) _pendingLambdaTArgs = typeArgs;
                    else _pendingNewTArgs = typeArgs;
                    _kindsKept = true;
                    return Next();
                }
                case OpCode.OverloadHint:
                    // (read by the call it stands in front of: OverloadHintBefore)
                    return Next();
                case OpCode.ArgNames:
                    // (read by the call it stands in front of: NamesBefore)
                    return Next();
                case OpCode.TakeCallTypeArgs:
                    // (a specialised copy knows its type arguments; the caller set them for the generic method all the same)
                    if (f.Kind == FuncKind.Lambda) E("TArgs ctargs = lamOf(lam)->targs; (void)ctargs;");   // (what the lambda took along)
                    else E($"TArgs ctargs = {(f.SpecTArgs != null ? TArgsExpr(f, f.SpecTArgs) : "((TArgs)g_callTArgs)")}; g_callTArgs = NULL; g_explicitTArgs = 0; (void)ctargs;");
                    _kindsKept = true;
                    return Next();
                case OpCode.GuardTop:
                {
                    // a value going into a local whose declaration the type checker could not prove: its kind (a whole number for a float is converted)
                    Need(1);
                    byte declared = (byte)(ins.A[0] + 1);
                    E($"{S(d - 1)} = {GuardKindCall(S(d - 1), declared, Str(ins.A[1]))};");
                    Check();
                    byte kind = KindOfDeclared(declared);
                    if (KindOfType(Ty(d - 1)) != kind) SetT(d - 1, kind == KindBool ? TypeOf(KindBool, 0) | NullableBit : TypeUnknown);
                    _kindsKept = true;
                    return Next();
                }
                case OpCode.GuardTopType:
                {
                    Need(1);
                    E(ClassGuard(S(d - 1), Str(ins.A[0]), Str(ins.A[1]), SelfObj(f), f.HasCallTArgs ? "ctargs" : null));
                    Check();
                    _kindsKept = true;
                    return Next();
                }
                case OpCode.GuardLocalType:
                {
                    // the end of the prologue: the parameter holds undefined or what its declaration says (DeclaredType) - with a class, an object of it
                    string v = Var(st, 0, ins.A[0], locals);
                    string spec = Str(ins.A[1]);
                    if (f.SpecParams?.FirstOrDefault(p => p.Slot == ins.A[0]) is { Kind: not 0 } specParam)
                    {
                        // a specialised copy: the call site made sure the argument is a number of that kind in that unit (or a bool)
                        JoinVarType(VarKey(f, v), TypeOf(specParam.Kind, specParam.Unit));
                        _kindsKept = true;
                        return Next();
                    }
                    E(ClassGuard(v, spec, Str(ins.A[2]), SelfObj(f), f.HasCallTArgs ? "ctargs" : null));
                    Check();
                    JoinVarType(VarKey(f, v), DeclaredClass(spec) is { } guardedRc ? ObjType(guardedRc) | NullableBit : InterfaceType(spec) ?? TypeUnknown);
                    _kindsKept = true;
                    return Next();
                }
                case OpCode.CheckLambdaSignature:
                    Need(1); E($"checkLambda({S(d - 1)}, {ins.A[0]});"); Check(); return Next();   // (a mismatch throws an ArgumentException)
                case OpCode.CheckUnit:
                {
                    Need(1);
                    string unitText = Str(ins.A[0]);
                    if (IsNum(K(d - 1)) && TypedAdaptUnit(unitText)) return Next();
                    E($"{S(d - 1)} = {AdaptUnitCall(S(d - 1), unitText)};");
                    Check();
                    return Next();
                }

                // ---------------------------------------------------------------------------------------------------
                // Strings, arrays, buffers
                // ---------------------------------------------------------------------------------------------------
                case OpCode.FormatValue:
                {
                    Need(1);
                    string spec = Str(ins.A[0]);
                    if (spec.Length > 0 && "XDBFExdbfe".IndexOf(spec[0]) < 0)
                        throw new NativeNotSupportedException($"format specifier '{spec}' (supported: X, D, B, F, E)");
                    E($"{S(d - 1)} = formatValue({S(d - 1)}, {Constant(chunk.Constants[ins.A[0]])}, &{OwnerList()});");
                    Check();
                    SetR(d - 1, true);
                    return Next();
                }
                case OpCode.NewArray:
                    Need(1); E($"{S(d - 1)} = newArray({S(d - 1)}, &{OwnerList()});"); SetR(d - 1, true); return Next();
                case OpCode.MakeBuffer:
                    Need(1); E($"{S(d - 1)} = newBuffer({S(d - 1)}, &{OwnerList()});"); SetR(d - 1, true); return Next();
                case OpCode.MakeArrayLiteral:
                {
                    int count = ins.A[0];
                    Need(count);
                    int first = d - count;
                    var sbItems = new StringBuilder();
                    for (int i = 0; i < count; i++) sbItems.Append($" arrItems(a)[{i}] = {S(first + i)}; retain({S(first + i)});");
                    E($"{{ Arr* a = allocArr({count}, &{OwnerList()});{sbItems} {S(first)} = ArrV(a); }}");
                    d = first + 1; SetR(first, true); return Next();
                }
                case OpCode.MakeArrayLiteralParts:
                {
                    int count = ins.A[0];
                    uint mask = (uint)ins.A[1] | ((uint)ins.A[2] << 16);
                    Need(count);
                    int first = d - count;
                    var sbItems = new StringBuilder();
                    for (int i = 0; i < count; i++) sbItems.Append($" arrItems(a)[{i}] = {S(first + i)}; retain({S(first + i)});");
                    // an array made inside the literal belongs to the outer array
                    var parts = new StringBuilder();
                    for (int i = 0; i < count; i++)
                        if ((mask >> i & 1) != 0) parts.Append($" attachPart(ArrV(a), arrItems(a)[{i}]);");
                    E($"{{ Arr* a = allocArr({count}, &{OwnerList()});{sbItems}{parts} {S(first)} = ArrV(a); }}");
                    d = first + 1; SetR(first, true); return Next();
                }
                case OpCode.NewJagged:
                {
                    int ranks = ins.A[0];
                    Need(ranks);
                    int first = d - ranks;
                    var sizes = string.Join(", ", Enumerable.Range(first, ranks).Select(i => $"sizeArg({S(i)})"));
                    E($"{{ const int64_t sz[{ranks}] = {{{sizes}}}; {S(first)} = newJagged(sz, {ranks}, &{OwnerList()}); }}");
                    d = first + 1; SetR(first, true); return Next();
                }
                case OpCode.HoistValue:
                {
                    Need(1);
                    _kindsKept = true;
                    if (IsPlainType(Ty(d - 1))) return Next();   // a number or a bool belongs to no scope
                    string target = ListName(st.Scopes[0].Id);
                    // only a scope that has a list for something else can own the value (a scope without one owns nothing: nothing links into it);
                    // pass 1 has seen every instruction when pass 2 emits, so a loop body that creates nothing keeps no bookkeeping for the hoist
                    if (sb == null) f.Hoists.Add((st.Scopes[0].Id, st.Scopes.Skip(1).Select(sc => sc.Id).ToArray()));
                    for (int i = 1; i < st.Scopes.Count; i++)
                        if (f.NeedsList.Contains(st.Scopes[i].Id))
                            E($"hoistFrom({S(d - 1)}, &{ListName(st.Scopes[i].Id)}, &{target});");
                    return Next();
                }
                case OpCode.TakeToScope:
                {
                    // `var a = take x` / `v = take x` (SPEC 2.2): x belongs to the scope that holds the variable
                    Need(1);
                    int depth = ins.A[0];
                    string list;
                    if (depth == 0xFFFF) list = "g_globalOwn";
                    else
                    {
                        var sc = st.Scopes[Math.Max(0, st.Scopes.Count - 1 - depth)];
                        f.NeedsList.Add(sc.Id);
                        list = "&" + ListName(sc.Id);
                    }
                    E($"if (takeAlive({S(d - 1)})) takeToList({S(d - 1)}, {list});");
                    Check();
                    return Next();
                }
                case OpCode.TakeCheck:
                    return Next();   // the check is part of `takeArg` in the argument list of the call
                case OpCode.TakeToObject:
                    Need(2);
                    E($"if (takeAlive({S(d - 1)})) takeToHolder({S(d - 2)}, {S(d - 1)});");
                    Check();
                    return Next();
                case OpCode.TakeToArray:
                    Need(3);
                    E($"if (takeAlive({S(d - 1)})) takeToHolder({S(d - 3)}, {S(d - 1)});");
                    Check();
                    return Next();
                case OpCode.OwnValue:
                    Need(2);
                    foreach (var sc in st.Scopes) f.NeedsList.Add(sc.Id);
                    E(st.Scopes.Count == 0 ? $"{S(d - 2)} = ownValueN({S(d - 2)}, {S(d - 1)}, NULL, 0);"
                        : $"{S(d - 2)} = ownValueN({S(d - 2)}, {S(d - 1)}, (OwnList* const[]){{{string.Join(", ", st.Scopes.Select(sc => "&" + ListName(sc.Id)))}}}, {st.Scopes.Count});");
                    SetR(d - 2, true);
                    d--; return Next();
                case OpCode.Delete:
                    Need(1);
                    E($"deleteValue({S(d - 1)});");
                    d--; return Next();
                case OpCode.ArrayGet:
                    Need(2); E($"{S(d - 2)} = {Px}aget_g({S(d - 2)}, {S(d - 1)}, &{OwnerList()});"); Check(); d--; SetR(d - 1, true); return Next();
                case OpCode.ArraySet:
                    Need(3); E($"{Px}aset_g({S(d - 3)}, {S(d - 2)}, {S(d - 1)}, &{OwnerList()});"); Check(); E($"{S(d - 3)} = {S(d - 1)};"); SetR(d - 3, R(d - 1)); d -= 2; return Next();
                case OpCode.IncDecIndex:
                {
                    Need(2);
                    bool increment = ins.A[0] != 0, prefix = ins.A[1] != 0;
                    string list = "&" + OwnerList();
                    string guard = _usesExceptions ? $"if (FIRE_UNLIKELY(g_unwind.active)) goto {(sb == null ? "X" : ExitLabel(f, st, locals))}; " : "";
                    E($"{{ Value o = {Px}aget_g({S(d - 2)}, {S(d - 1)}, {list}); {guard}Value n = {(increment ? "add" : "sub")}(o, stepOf(o, Int(1))); {Px}aset_g({S(d - 2)}, {S(d - 1)}, n, {list}); {S(d - 2)} = {(prefix ? "n" : "o")}; }}");
                    Check();
                    d--; SetR(d - 1, false); return Next();
                }

                // ---------------------------------------------------------------------------------------------------
                // `ref` parameters and pointers to variables, fields and elements (SPEC 5.4.2)
                // ---------------------------------------------------------------------------------------------------
                case OpCode.Probe:
                {
                    Need(2); UseProbes();
                    string member = (ins.A[1] & 2) != 0 ? "NULL" : CString(Str(ins.A[0]));
                    E($"{{ char err[500]; int64_t id = rf_probeAdd({S(d - 2)}, {member}, {((ins.A[1] & 1) != 0 ? "true" : "false")}, {S(d - 1)}, err, sizeof err); if (id == -2) {{ }} else if (id < 0) {{ fireError(strstr(err, \"has no member\") ? \"MissingMemberException\" : \"ArgumentException\", err); }} else {S(d - 2)} = Int(id); }}");
                    Check();
                    d--; SetR(d - 1, false); return Next();
                }
                case OpCode.SilenceMember:
                {
                    Need(1); UseProbes();
                    string member = ins.A[1] != 0 ? "NULL" : CString(Str(ins.A[0]));
                    E($"{{ char err[500]; if (!rf_silenceCore({S(d - 1)}, {member}, err, sizeof err)) fireError(\"ArgumentException\", err); }}");
                    Check();
                    d--; return Next();
                }
                case OpCode.SilenceValue:
                    Need(1); UseProbes();
                    E($"{{ char err[500]; if (!rf_silenceValue({S(d - 1)}, err, sizeof err)) fireError(\"ArgumentException\", err); }}");
                    Check();
                    d--; return Next();
                case OpCode.CopyValue:
                    Need(1);
                    E($"{S(d - 1)} = copyValue({S(d - 1)}, {((ins.A[0] & 1) != 0 ? "true" : "false")}, &{OwnerList()});");
                    Check();
                    return Next();
                case OpCode.CopyValueOwned:
                    Need(2);
                    E($"{S(d - 2)} = copyOwned({S(d - 2)}, {S(d - 1)}, {((ins.A[0] & 1) != 0 ? "true" : "false")});");
                    Check();
                    d--; SetR(d - 1, R(d)); return Next();
                case OpCode.CopyArgs:
                    return Next();
                case OpCode.AddressOfLocal:
                {
                    string v = Var(st, ins.A[0], ins.A[1], locals);
                    MarkVarRef(VarKey(f, v));   // what is written through the pointer is counted by the variable
                    JoinVarType(VarKey(f, v), TypeUnknown);
                    E($"{S(d)} = PtrV(&{v});");
                    SetR(d, false); d++; return Next();
                }
                case OpCode.AddressOfGlobal:
                {
                    string gv = GlobalVar(ins.A[0], locals);
                    MarkVarRef(VarKey(f, gv));
                    JoinVarType(VarKey(f, gv), TypeUnknown);
                    E($"{S(d)} = PtrV(&{gv});");
                    SetR(d, false); d++; return Next();
                }
                case OpCode.AddressOfField:
                {
                    Need(1);
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    if (_fieldAddressTaken.Add(field)) _version++;   // (what is written through the pointer is not checked: the field has no static type)
                    E($"{S(d - 1)} = {FieldFn("fp", field)}({S(d - 1)});");
                    SetR(d - 1, false);
                    return Next();
                }
                case OpCode.AddressOfIndex:
                    if (!_usesElementPointers) { _usesElementPointers = true; _version++; }
                    Need(2); E($"{S(d - 2)} = addressOfIndex({S(d - 2)}, {S(d - 1)});"); Check(); d--; SetR(d - 1, false); return Next();
                case OpCode.PtrRead:
                    Need(1); E($"{S(d - 1)} = ptrRead({S(d - 1)});"); Check(); SetR(d - 1, true); return Next();
                case OpCode.PtrWrite:
                    Need(2);
                    E($"ptrWrite({S(d - 2)}, {S(d - 1)});");
                    Check();
                    E($"{S(d - 2)} = {S(d - 1)};");
                    SetR(d - 2, R(d - 1));
                    d--; return Next();
                case OpCode.RequireRefParam:
                    E($"requireRef({Var(st, 0, ins.A[0], locals)}, {CString(chunk.Constants[ins.A[1]].AsString())});");
                    return Next();

                // ---------------------------------------------------------------------------------------------------
                // Exceptions
                // ---------------------------------------------------------------------------------------------------
                case OpCode.RegisterHandler:
                {
                    UseExceptions();
                    int t = ins.A[0];
                    var region = GetRegion(f, t, st);
                    if (_usesTakeUpwards && region.Catches.Count > 0) { f.NeedsList.Add(region.CatchScopeId); f.NeedsList.Add(st.Scopes[^1].Id); }
                    E(region.Catches.Count > 0 ? $"H{t}.fn = {CatchName(f, region)}; H{t}.ctx = FR;" : $"H{t}.fn = NULL; H{t}.ctx = NULL;");
                    E($"H{t}.info = &HI_{fn}_{t}; pushHandler(&H{t});");
                    var edges = new List<(int, Flow)>();
                    foreach (var (_, addr) in region.Catches)
                    {
                        var catchFlow = st.Clone();
                        catchFlow.Region = region;
                        if (region.HasFinally) catchFlow.Handlers.Add(new HEntry(region, true));
                        catchFlow.Scopes.Add((region.CatchScopeId, 1));
                        edges.Add((addr, catchFlow));
                    }
                    if (region.HasFinally)
                    {
                        // an exception that this `try` does not catch arrives at its `finally`
                        var finallyFlow = st.Clone();
                        finallyFlow.Depth = d + 2;
                        edges.Add((region.FinallyAddr!.Value, finallyFlow));
                    }
                    st.Handlers.Add(new HEntry(region, false));
                    st.Depth = d;
                    return new StepResult(true, null, edges);
                }
                case OpCode.UnregisterHandler:
                    if (st.Handlers.Count == 0) throw new NativeNotSupportedException($"UnregisterHandler without handler at {ins.Addr} in {fn}");
                    st.Handlers.RemoveAt(st.Handlers.Count - 1);
                    E("popHandler();");
                    return Next();
                case OpCode.Throw:
                    UseExceptions(); Need(1);
                    E($"{S(d - 1)} = throwValue({S(d - 1)});");
                    Check();
                    E($"adopt({S(d - 1)}, &{OwnerList()});");
                    SetR(d - 1, true);
                    return Next();
                case OpCode.ResumeException:
                    UseExceptions(); Need(2);
                    E($"resumeThrow({S(d - 2)}, {S(d - 1)});");
                    if (sb != null) E($"goto {ExitLabel(f, st, locals)};");
                    E($"{S(d - 2)} = Undef();");
                    d--; SetR(d - 1, false); return Next();
                case OpCode.ClearPendingResume:
                    Need(1); E($"clearPending({S(d - 1)});"); d--; return Next();
                case OpCode.EnterFinallyNormal:
                    E($"{S(d)} = Undef(); {S(d + 1)} = Int(0);");
                    SetR(d, false); SetR(d + 1, false);
                    d += 2; return Next();
                case OpCode.PushJump:
                {
                    var next = f.Decoded[f.IndexOf[ins.Addr] + 1];
                    if (next.Op != OpCode.Jump) throw new NativeNotSupportedException($"PushJump without Jump at {ins.Addr} in {fn}");
                    if (!f.FinJumps.TryGetValue(next.A[0], out var jumps)) f.FinJumps[next.A[0]] = jumps = new SortedSet<int>();
                    jumps.Add(ins.A[0]);
                    E($"{S(d)} = Int({ins.A[0]}); {S(d + 1)} = Int(3);");
                    SetR(d, false); SetR(d + 1, false);
                    d += 2; return Next();
                }
                case OpCode.EndFinally:
                {
                    Need(2);
                    if (st.Fins.Count == 0) throw new NativeNotSupportedException($"EndFinally outside of a finally block at {ins.Addr} in {fn}");
                    int fin = st.Fins[^1];
                    string payload = S(d - 2), kind = S(d - 1);
                    bool payloadRef = R(d - 2);
                    var after = st.Clone();
                    after.Fins.RemoveAt(after.Fins.Count - 1);
                    after.Depth = d - 2;
                    var edges = new List<(int, Flow)>();
                    E($"switch ({kind}.i) {{");
                    E("case 0: break;");
                    E($"case 1: throwValue({payload}, false);");
                    if (sb != null && _usesExceptions) E($"    goto {ExitLabel(f, after, locals)};");
                    else E("    break;");
                    if (f.FinReturn.Contains(fin))
                    {
                        E("case 2: {");
                        E($"    Value r = {payload}; (void)r;");
                        edges.AddRange(ReturnSeq(f, after, payloadRef, false, sb == null ? null : x => E("    " + x), locals));
                        E("}");
                    }
                    if (f.FinJumps.TryGetValue(fin, out var targets))
                    {
                        E($"case 3: switch ({payload}.i) {{");
                        foreach (int target in targets)
                        {
                            E($"    case {target}: goto L{target};");
                            edges.Add((target, after));
                        }
                        E("    default: break;");
                        E("    }");
                        E("    break;");
                    }
                    if (_usesThreads)
                    {
                        E("case 4:");
                        E("    startLeave();");
                        if (sb != null) E($"    goto {ExitLabel(f, after, locals)};");
                        else E("    break;");
                    }
                    E("default: break;");
                    E("}");
                    st.Fins.RemoveAt(st.Fins.Count - 1);
                    d -= 2;
                    st.Depth = d;
                    return new StepResult(true, null, edges);
                }

                // ---------------------------------------------------------------------------------------------------
                // Fire threads
                // ---------------------------------------------------------------------------------------------------
                case OpCode.Fire:
                {
                    UseThreads();
                    int takingCount = ins.A[2];
                    bool hasWith = ins.A[3] != 0;
                    int argc = takingCount + (hasWith ? 1 : 0);
                    Need(argc);
                    var body = GetFunc(chunk.Functions[ins.A[0]], FuncKind.FireBody);
                    body.GlobalSlotCount = ins.A[1];
                    int first = d - argc;
                    var sbf = new StringBuilder("{ OwnList* travel = newTravel(); Value av[" + Math.Max(1, argc) + "] = {}; ");
                    for (int i = 0; i < takingCount; i++) sbf.Append($"av[{i}] = takeCopy({S(first + i)}, travel); ");
                    if (hasWith) sbf.Append($"av[{takingCount}] = {S(first + takingCount)}; ");
                    sbf.Append($"ensureDomain(); fireThread({body.Name}, av, {argc}, travel); }}");
                    E(sbf.ToString());
                    d = first;
                    return Next();
                }
                case OpCode.Sync:
                    UseThreads(); Need(1);
                    E($"{S(d - 1)} = syncValue({S(d - 1)}, {((ins.A[0] & 2) != 0 ? "true" : "false")});");
                    Check();
                    SetR(d - 1, false);
                    return Next();
                case OpCode.Process:
                    UseThreads(); Need(1);
                    E($"processMsg({S(d - 1)}, true, &{OwnerList()});");
                    Check();
                    d--; return Next();
                case OpCode.TryProcess:
                    UseThreads(); Need(1);
                    E($"{S(d - 1)} = Bool(processMsg({S(d - 1)}, false, &{OwnerList()}) > 0);");
                    Check();
                    SetR(d - 1, false); return Next();
                case OpCode.Leave:
                    UseThreads();
                    E("leaveNow();");
                    if (sb != null) E($"goto {ExitLabel(f, st, locals)};");
                    return new StepResult(false, null);
                case OpCode.Terminate:
                    UseThreads(); Need(1);
                    E($"terminateNow({S(d - 1)});");
                    if (sb != null) E($"goto {ExitLabel(f, st, locals)};");
                    d--; st.Depth = d;
                    return new StepResult(false, null);
                case OpCode.RegisterThreadsCatch:
                case OpCode.RegisterTerminateCatch:
                {
                    UseThreads();
                    var handler = GetFunc(chunk.Functions[ins.A[0]], FuncKind.Static);
                    // (a function the runtime calls with the exception or the value of terminate)
                    string lambda = $"{Px}cb_{handler.Name}";
                    _catchCallbacks[lambda] = handler.ParamCount >= 1 ? $"return {handler.Name}(e);" : $"(void)e; return {handler.Name}();";
                    if (ins.Op == OpCode.RegisterThreadsCatch) E($"registerThreadsCatch({(ins.A[1] == 0 ? "-1" : LibraryRole ? $"fire_exct_{_self!.Id}[{ExcTypeId(Str(ins.A[2]))}]" : ExcTypeId(Str(ins.A[2])).ToString(CultureInfo.InvariantCulture))}, {lambda});");
                    else E($"registerTerminateCatch({lambda});");
                    return Next();
                }
                case OpCode.SyncGlobals:
                    UseThreads();
                    E($"{S(d)} = Int(syncGlobals());");
                    Check();
                    SetR(d, false); d++; return Next();
                case OpCode.SectionEnter:
                    UseThreads();
                    E("sectionEnterOp();");
                    return Next();
                case OpCode.SectionExit:
                    UseThreads();
                    E("sectionExitOp();");
                    return Next();
                case OpCode.PostGlobal:
                {
                    UseThreads();
                    int argc = ins.A[0];
                    Need(argc + 1);
                    int first = d - argc - 1;
                    E($"{{ Value av[{Math.Max(1, argc)}] = {{{string.Join(", ", Enumerable.Range(first, argc).Select(S).DefaultIfEmpty("Undef()"))}}}; postJob({S(d - 1)}, av, {argc}); }}");
                    d = first;
                    return Next();
                }

                default:
                    throw new NativeNotSupportedException($"opcode {ins.Op} (in {fn} at {ins.Addr})");
            }

            StepResult Binary(string fnName, string opMethod)
            {
                Need(2);
                if (UseOperator(opMethod))
                {
                    E($"{S(d - 2)} = {OperatorWrapper(opMethod)}({S(d - 2)}, {S(d - 1)}, &{OwnerList()});");
                    Check(); d--; SetR(d - 1, true); return Next();
                }
                E($"{S(d - 2)} = {fnName}({S(d - 2)}, {S(d - 1)});"); CheckIf($"{S(d - 2)}.kind == K_Undefined"); d--; SetR(d - 1, false); return Next();
            }
            StepResult Unary(string fnName)
            {
                Need(1); E($"{S(d - 1)} = {fnName}({S(d - 1)});"); CheckIf($"{S(d - 1)}.kind == K_Undefined"); SetR(d - 1, false); return Next();
            }
            StepResult Compare(string fnName, bool negate, string opMethod)
            {
                Need(2);
                if (UseOperator(opMethod))
                {
                    E($"{S(d - 2)} = {OperatorWrapper(opMethod)}({S(d - 2)}, {S(d - 1)}, &{OwnerList()});");
                    Check(); d--; SetR(d - 1, true); return Next();
                }
                E($"{S(d - 2)} = Bool({(negate ? "!" : "")}{fnName}({S(d - 2)}, {S(d - 1)}));"); CheckIf(negate ? "true" : $"{S(d - 2)}.i == 0"); d--; SetR(d - 1, false); return Next();
            }
            StepResult JumpIfNot(string fnName, bool negate, string opMethod)
            {
                Need(2);
                if (UseOperator(opMethod))
                {
                    // the overload can return anything: a value that is true or false decides
                    E($"{{ Value t = {OperatorWrapper(opMethod)}({S(d - 2)}, {S(d - 1)}, &{OwnerList()});");
                    if (_usesExceptions && sb != null) E($"  if (FIRE_UNLIKELY(g_unwind.active)) goto {ExitLabel(f, st, locals)};");
                    E($"  if (!truthy(t)) goto L{ins.A[0]}; }}");
                    d -= 2; st.Depth = d;
                    return new StepResult(true, ins.A[0]);
                }
                if (_usesExceptions && sb != null && !negate)
                    E($"if (!({fnName}({S(d - 2)}, {S(d - 1)}))) {{ if (FIRE_UNLIKELY(g_unwind.active)) goto {ExitLabel(f, st, locals)}; goto L{ins.A[0]}; }}");
                else E($"if (!({(negate ? "!" : "")}{fnName}({S(d - 2)}, {S(d - 1)}))) goto L{ins.A[0]};");
                d -= 2; st.Depth = d;
                return new StepResult(true, ins.A[0]);
            }

            // `a op b` on two numbers of known kind and unit (no bit width): the C++ operation itself, with the semantics of add/sub/mul/divide/modulo
            // and their unit algebra (ints wrap; an int division by 0 throws, by -1 negates; `%` by -1 is 0; `+ - %` keep the common unit, `* /` with
            // a number without a unit keep the other unit; a float plus a number of another unit of the same dimension converts the right one by the
            // factor of alignUnits). An operand that may be undefined (a parameter or field, DeclaredKind) is tested at run time: when it is undefined
            // the generic function runs (and throws). Leaves the result in S(d - 2) and pops one slot; false: not known well enough, the generic code is used.
            bool TypedArith(string op)
            {
                byte ka = BK(d - 2), kb = BK(d - 1);
                if (!IsNum(ka) || !IsNum(kb)) return false;
                int ua = U(d - 2), ub = U(d - 1);
                string a = S(d - 2), b = S(d - 1);
                string guard = KindGuard(d - 2, d - 1);
                string generic = op switch { "+" => "add", "-" => "sub", "*" => "mul", "/" => "divide", _ => "modulo" };
                // the fast expression, run only when the guard holds (the generic function otherwise)
                void Emit(string fast)
                {
                    if (guard.Length == 0) { E($"{a} = {fast};"); return; }
                    E($"{a} = FIRE_LIKELY({guard}) ? {fast} : {generic}({a}, {b});");
                    CheckIf($"{a}.kind == K_Undefined");
                }
                bool ints = ka == KindInt && kb == KindInt;
                int unit;
                switch (op)
                {
                    case "+" or "-" or "%" when ua == ub: unit = ua; break;
                    case "+" or "-" when !ints && LinearFactor(ub, ua) is { } factor:
                        // alignUnits: the right operand goes into the unit of the left one as a float
                        Emit($"Float({Num(d - 2)} {op} (Real)({Dbl(d - 1)} * {factor}), {UnitC(ua)})");
                        d--;
                        SetR(d - 1, false); SetT(d - 1, TypeOf(KindFloat, ua)); _kindsKept = true;
                        return true;
                    case "*" when ua == 0 || ub == 0: unit = ua == 0 ? ub : ua; break;
                    case "/" when ub == 0: unit = ua; break;
                    default: return false;
                }
                string unitArg = unit == 0 ? "" : $", {UnitC(unit)}";
                if (ints)
                {
                    switch (op)
                    {
                        case "/": E($"{a} = divide({a}, {b});"); CheckIf($"{a}.kind == K_Undefined"); break;   // (undefined throws there, too)
                        case "%":
                            E($"{a} = FIRE_LIKELY({(guard.Length > 0 ? guard + " && " : "")}{b}.i > 0) ? Int({a}.i % {b}.i{unitArg}) : modulo({a}, {b});");
                            CheckIf($"{a}.kind == K_Undefined");
                            break;
                        default: Emit($"Int((int64_t)((uint64_t){a}.i {op} (uint64_t){b}.i){unitArg})"); break;
                    }
                }
                else if (op == "%") Emit($"Float(fmod({Num(d - 2)}, {Num(d - 1)}){unitArg})");
                else Emit($"Float({Num(d - 2)} {op} {Num(d - 1)}{unitArg})");
                d--;
                SetR(d - 1, false); SetT(d - 1, TypeOf(ints ? KindInt : KindFloat, unit)); _kindsKept = true;
                return true;
            }
            // The run-time test that the operands that may be undefined are not ("" when none may be).
            string KindGuard(params int[] slots) => string.Join(" && ", slots.Where(k => IsNullable(Ty(k)))
                .Select(k => $"{S(k)}.kind == {(BK(k) switch { KindInt => "K_Int", KindFloat => "K_Float", _ => "K_Bool" })}"));
            // The C++ condition of `a op b` for two slots of known kind (numbers of the same unit, or two bools for == and !=), null when the kinds
            // are not known. With a float they compare like compareNumeric (NaN sorts below everything, like double.CompareTo in the VM). An operand
            // that may be undefined is tested; when it is, the generic comparison decides (`==` is false, `<` throws). `mayThrow`: the generic one can throw.
            string? TypedCondition(string op, int ia, int ib, out bool mayThrow)
            {
                mayThrow = false;
                byte ka = BK(ia), kb = BK(ib);
                string a = S(ia), b = S(ib);
                // `x == undefined` where x is a number, a bool or an object of a known class: its kind decides (unless a class overloads the operator)
                if (op is "==" or "!=" && (Ty(ia) == TypeUndefined) != (Ty(ib) == TypeUndefined)
                    && (Ty(ia) == TypeUndefined ? Ty(ib) : Ty(ia)) is var other && (other & TypeNone) == 0 && BaseKindOfType(other) != KindUnknown
                    && !_classList.Any(c => c.Rc.FindMethodWithAccess("operator" + op, 1).Proto != null))
                    return $"{(Ty(ia) == TypeUndefined ? b : a)}.kind {op} K_Undefined";
                string? fast;
                if (ka == KindBool && kb == KindBool) fast = op is "==" or "!=" ? $"{a}.i {op} {b}.i" : null;
                else if (!IsNum(ka) || !IsNum(kb) || U(ia) != U(ib)) fast = null;
                else if (ka == KindInt && kb == KindInt) fast = $"{a}.i {op} {b}.i";
                else if (op is "==" or "!=") fast = $"{Num(ia)} {op} {Num(ib)}";
                else fast = $"compareNumeric({a}, {b}) {op} 0";
                if (fast == null) return null;
                string guard = KindGuard(ia, ib);
                if (guard.Length == 0) return fast;
                mayThrow = op is not ("==" or "!=");
                string generic = op switch { "<" => "lt", "<=" => "le", ">" => "gt", ">=" => "ge", "==" => "opEq", _ => "!opEq" };
                return $"(FIRE_LIKELY({guard}) ? ({fast}) : {generic}({a}, {b}))";
            }
            bool TypedCompare(string op)
            {
                string? cond = TypedCondition(op, d - 2, d - 1, out bool mayThrow);
                if (cond == null) return false;
                E($"{S(d - 2)} = Bool({cond});");
                if (mayThrow) CheckIf($"{S(d - 2)}.i == 0");   // (a comparison that threw is false)
                d--;
                SetR(d - 1, false); SetT(d - 1, TypeOf(KindBool, 0)); _kindsKept = true;
                return true;
            }
            // CheckUnit (adaptUnit) on a number of known kind and unit: nothing to do when it already has the unit of the declaration (a float
            // declaration makes an int a float); a conversion within the dimension keeps the generic call, but the result is known.
            bool TypedAdaptUnit(string demand)
            {
                bool toFloat = demand.StartsWith('#');
                string unitText = demand.TrimStart('#');
                byte kind = K(d - 1);
                int from = U(d - 1);
                byte resultKind = toFloat ? KindFloat : kind;
                string toFloatCode = toFloat && kind == KindInt ? $"{S(d - 1)} = Float((Real){S(d - 1)}.i{(from == 0 ? "" : $", {UnitC(from)}")});" : "";
                int resultUnit;
                if (unitText == "any") resultUnit = from;
                else
                {
                    bool dimensionOnly = unitText.StartsWith("any ", StringComparison.Ordinal);
                    Unit demanded = DemandUnit(unitText);
                    int target = UnitId(demanded);
                    if (!UnitById(from).IsCompatibleWith(demanded)) return false;   // an UnitMismatchException
                    if (dimensionOnly || from == target) resultUnit = from;
                    else
                    {
                        E($"{S(d - 1)} = {AdaptUnitCall(S(d - 1), demand)};");
                        Check();
                        SetT(d - 1, TypeOf(resultKind, target)); _kindsKept = true;
                        return true;
                    }
                }
                if (toFloatCode.Length > 0) E(toFloatCode);
                SetT(d - 1, TypeOf(resultKind, resultUnit)); _kindsKept = true;
                return true;
            }
            StepResult? TypedJumpIfNot(string op)
            {
                Need(2);
                string? cond = TypedCondition(op, d - 2, d - 1, out bool mayThrow);
                if (cond == null) return null;
                if (mayThrow && _usesExceptions && sb != null)
                    E($"if (!({cond})) {{ if (FIRE_UNLIKELY(g_unwind.active)) goto {ExitLabel(f, st, locals)}; goto L{ins.A[0]}; }}");
                else E($"if (!({cond})) goto L{ins.A[0]};");
                d -= 2; st.Depth = d; _kindsKept = true;
                return new StepResult(true, ins.A[0]);
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // Constants
        // -------------------------------------------------------------------------------------------------------------
        private int UnitId(Unit? unit)
        {
            if (unit == null || unit.IsUnitless) return 0;
            for (int i = 1; i < _units.Count; i++) if (_units[i].Equals(unit) && _units[i].DisplaySymbol == unit.DisplaySymbol) return i;   // (`l` and `dm^3` are equal units with other names)
            _units.Add(unit);
            if ((unit.Converter ?? unit.UnitlessConverter) is { } converter && !_unitConverters.ContainsKey(converter))
            {
                // the conversion functions of the unit (static To and From of its hidden class) are called by the runtime: they are generated
                var rc = FindClass(converter);
                Func Conv(string method) => GetFunc(rc.FindMethodWithAccess(method, 1).Proto ?? throw new NativeNotSupportedException($"the conversion function {converter}.{method} is missing"), FuncKind.Static);
                _unitConverters[converter] = (_unitConverters.Count, Conv("To"), Conv("From"));
            }
            return _units.Count - 1;
        }

        private string Constant(Value v)
        {
            switch (v.Kind)
            {
                case ValueKind.Int:
                {
                    long i = v.AsInt();
                    string lit = i == long.MinValue ? "INT64_MIN" : i.ToString(CultureInfo.InvariantCulture) + "LL";
                    if (v.Enum is { } enumType) return $"EnumV({lit}, {EnumC(EnumIndex(enumType))})";   // (a value of an enum carries its type)
                    int unit = UnitId(v.Unit);
                    return unit == 0 ? $"Int({lit})" : $"Int({lit}, {UnitC(unit)})";
                }
                case ValueKind.Float:
                {
                    int unit = UnitId(v.Unit);
                    string lit = DoubleLiteral(v.AsFloat(), _program.FloatWidth == 32);
                    return unit == 0 ? $"Float({lit})" : $"Float({lit}, {UnitC(unit)})";
                }
                case ValueKind.Bool: return v.AsBool() ? "Bool(true)" : "Bool(false)";
                case ValueKind.Char: return $"Char({(int)v.AsChar()})";
                case ValueKind.Undefined: { int unit = UnitId(v.Unit); return unit == 0 ? "Undef()" : $"Undef({UnitC(unit)})"; }
                case ValueKind.String:
                {
                    string s = v.AsString();
                    if (!_stringIndex.TryGetValue(s, out int index)) { index = _strings.Count; _strings.Add(s); _stringIndex[s] = index; _version++; }
                    return $"StrV(&{Px}K{index})";
                }
                default:
                    throw new NativeNotSupportedException($"constant of kind {v.Kind}");
            }
        }

        private static string DoubleLiteral(double d, bool single)
        {
            if (double.IsNaN(d)) return single ? "nanf(\"\")" : "nan(\"\")";
            if (double.IsPositiveInfinity(d)) return single ? "HUGE_VALF" : "HUGE_VAL";
            if (double.IsNegativeInfinity(d)) return single ? "-HUGE_VALF" : "-HUGE_VAL";
            if (d == 0 && double.IsNegative(d)) return single ? "-0.0f" : "-0.0";
            string text = single ? ((float)d).ToString("R", CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);
            if (!(text.Contains('.') || text.Contains('E') || text.Contains('e'))) text += ".0";
            return single ? text + "f" : text;
        }

        /// <summary>The C of an import the compiler knows itself (`graphics`, `windows`): its natives in the virtual machine are C#, its C is the source of the standard package
        /// (`Source/graphics/native/`, docs/PACKAGES.md), put into the generated file like that of any package.</summary>
        private readonly HashSet<string> _standardNativesWritten = new(StringComparer.OrdinalIgnoreCase);

        private void AppendStandardNatives(StringBuilder sb, string import)
        {
            _standardNativesWritten.Add(import);   // (the project of the library itself has the same C as its own natives: it is written once)
            var found = PackageStore.Default.Installed().SelectMany(p => p.Manifest.Imports.Select(i => new InstalledImport(p, i)))
                .FirstOrDefault(i => string.Equals(i.Name, import, StringComparison.OrdinalIgnoreCase) && i.Import.Native != null)
                ?? throw new NativeNotSupportedException($"the standard package 'fire-{import}' with the C of `#import \"{import}\"` is not installed (start ember or spark once)");
            foreach (var (name, text) in found.ReadNativeSources(new[] { _target.Native.Platform, _target.Name }))
            {
                sb.AppendLine($"// ---- {found.Package.Name} {found.Package.Version}, import \"{import}\": {name}");
                sb.AppendLine(text);
                sb.AppendLine($"// ---- end of {name}");
            }
        }

        private static string CString(string s) => CString(Encoding.UTF8.GetBytes(s));

        /// <summary>A C string literal of the bytes (octal escapes for everything outside printable ASCII).</summary>
        private static string CString(byte[] bytes)
        {
            var sb = new StringBuilder("\"");
            foreach (byte b in bytes)
            {
                if (b == '"' || b == '\\') sb.Append('\\').Append((char)b);
                else if (b >= 0x20 && b < 0x7F && b != '?') sb.Append((char)b); // '?' avoids trigraphs
                else sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            }
            return sb.Append('"').ToString();
        }
    }
}
