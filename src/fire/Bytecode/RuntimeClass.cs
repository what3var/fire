using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Values;
using MemoryPack;

namespace fire.Bytecode
{
    /// <summary>
    /// Compiled counterpart to a ClassDecl: fields (as 0-arg protos that
    /// are evaluated with a bound 'this'), methods (name -> list of
    /// protos, ONE per overloaded parameter count - see FindMethod), for
    /// virtual resolution via the base-class chain), constructor (ALWAYS
    /// present - synthesised if the class declares none of its own)
    /// and optionally a destructor.
    ///
    /// Destructor NOTE: The destructor proto is compiled, but is currently
    /// NOT yet executed by the VM (see VM.RunDestructor) - the
    /// cascade deletion itself (SPEC 2.3) already works via the
    /// runtime layer, only the actual destruct() method body does not run yet
    /// because during an ongoing scope teardown that would need a renewed
    /// nesting of the interpreter loop, which is deliberately not yet built
    /// here (reentrancy risk, see BYTECODE.md).
    /// </summary>
    /// 

    [MemoryPackable]
    public sealed partial class FieldInfo
    {
        public string? RequiredUnit { get; set; }

        public AccessModifier AccessModifier { get; set; }

        /// <summary>SPEC "Static members" - `true` for a `static`
        /// declared field: ONE shared storage location per class
        /// (RuntimeClass.StaticFieldValues/StaticFields), NOT per instance
        /// (ObjectInstance.Fields, see RuntimeClass.Fields).</summary>
        public bool IsStatic { get; set; }
    }

    [MemoryPackable]
    public sealed partial class RuntimeClass
    {
        public string Name { get; }

        /// <summary>Needed only by the compiler (base-class resolution,
        /// SourceIndex for multi-file debugging) - NO longer by the VM at
        /// runtime (ObjectInstance carries only the class name, see
        /// the docs there, precisely so that the complete Stmt/Expr AST hierarchy
        /// does not hang off every runtime object). Therefore excluded from the planned
        /// program serialisation - a program loaded from the cache
        /// is already fully compiled, needs the source AST
        /// no more.</summary>
        [MemoryPackIgnore]
        public ClassDecl Decl { get; }
        public RuntimeClass? Base { get; set; }

        /// <summary>`actor Name { ... }` instead of `class Name { ... }` (see
        /// Ast.ClassDecl.IsActor) - each instance of this class gets a mailbox on
        /// `new` (see VM.NewObject/Runtime.ObjectInstance.
        /// Mailbox). Runs up the base-class chain: a class that inherits from
        /// an actor is itself also an actor (inheriting from a
        /// NON-actor base by an actor class, on the other hand, is not
        /// sensibly possible, since a normal class knows no mailbox semantics
        /// - not checked separately at this stage).
        ///
        /// Used to be derived via Decl.IsActor - but Decl is now
        /// [MemoryPackIgnore] (see there), so it would no longer be available
        /// after deserialisation. A real field of its own instead of a
        /// computed property, so that the value survives the leap over
        /// serialisation - set once by the compiler when creating the
        /// RuntimeClass (see Compiler.CompileClasses).</summary>
        public bool IsActor { get; set; }

        /// <summary>Declared types/signatures for reflection (only if the program uses `#import "reflection"`), otherwise null.</summary>
        public ClassMeta? Meta { get; set; }

        /// <summary>The interfaces this class names in `class X : Base, IFoo` (for `value is of IFoo`; inherited ones come via the base-class chain).</summary>
        public List<string> Interfaces { get; set; } = new();

        /// <summary>Class of the reflection library (`Reflect`, `Type`, `Member`, `Selector`): for the access check the caller BEFORE it counts.</summary>
        public bool IsReflectionHelper { get; set; }

        public List<(string Name, FunctionProto Init)> Fields { get; }

        /// <summary>Access modifier of each field declared in THIS class
        /// itself (not inherited) - see FindFieldAccess for
        /// the base-class chain. Filled directly by the compiler (see
        /// CompileClass), default when an entry is missing is `Public`
        /// (see FindFieldAccess).</summary>
        public Dictionary<string, FieldInfo> OwnFieldInfo { get; }

        // ------------------------------------------------------------
        // Static members (SPEC "Static members") - ONE shared
        // storage location per CLASS instead of per instance. Access modifier/
        // required unit of a static field deliberately go through
        // the SAME OwnFieldInfo entries as instance fields (FieldInfo.
        // IsStatic differs only in WHERE the actual VALUE lives) - a
        // name is unique per class anyway, whether static or not.
        // ------------------------------------------------------------

        /// <summary>Static field values - lives HERE directly on the
        /// RuntimeClass (not like normal fields in ObjectInstance.Fields),
        /// because there is only EXACTLY ONE storage location per class, none
        /// per instance. Initialised once at program start (see
        /// VM.RunStaticInitializers), then read/written quite normally via GetStaticField/
        /// SetStaticField.</summary>
        public Dictionary<string, Value> StaticFieldValues { get; }

        /// <summary>Static field initialisers of THIS class (name -> 0-arg
        /// proto) - like Fields, but kept separate: do NOT run like
        /// Fields on EVERY `new` construction, but EXACTLY ONCE at
        /// program start (see VM.RunStaticInitializers), in
        /// declaration order.</summary>
        public List<(string Name, FunctionProto Init)> StaticFields { get; }

        /// <summary>Like FindFieldAccess, but for static fields: the
        /// class that declares `name` as a static field ITSELF
        /// (base-class chain, own class first) - `null` if no
        /// class in the chain has a static field of this name. A
        /// derived class WITHOUT its own static field of the same name
        /// shares the storage location of the base class (`Derived.X` and
        /// `Base.X` are then the SAME field, the same StaticFieldValues
        /// instance) - if Derived itself declares one with the same name,
        /// it is a SEPARATE, independent storage location (hides that
        /// of the base, not possible with instance fields, but common
        /// for statics in most OO languages).</summary>
        public RuntimeClass? FindStaticFieldOwner(string name)
        {
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.OwnFieldInfo.TryGetValue(name, out var info) && info.IsStatic)
                    return rc;
            return null;
        }

        /// <summary>Like FindFieldAccess, but for the required unit -
        /// searches along the base-class chain (own class first) for the
        /// class that actually declares `name` ITSELF with a unit.
        /// `null` if no class in the chain prescribes a fixed
        /// unit for this field.</summary>
        public string? FindFieldRequiredUnit(string name)
        {
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.OwnFieldInfo.TryGetValue(name, out var field))
                    return field.RequiredUnit;
            return null;
        }

        /// <summary>Like FindMethod, but for fields: searches along the
        /// base-class chain (own class first) for the class that
        /// actually declares `name` ITSELF, together with its
        /// access modifier - `null` if no field of this name is
        /// declared anywhere in the chain (e.g. a field set dynamically via
        /// the dictionary fallback, see Runtime.
        /// FieldStore - there is no access check for it, see VM.
        /// CheckFieldAccess).</summary>
        public (RuntimeClass DeclaringClass, AccessModifier Access)? FindFieldAccess(string name)
        {
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.OwnFieldInfo.TryGetValue(name, out var field))
                    return (rc, field.AccessModifier);
            return null;
        }

        /// <summary>All field names of this class INCLUDING all inherited ones
        /// (base first, recursively, then its own, each in
        /// declaration order) - exactly the order in which
        /// ConstructBase + the own field initialisers actually set them at runtime
        /// (see Compiler.CompileConstructorProto).
        /// Basis for FieldIndex/Runtime.FieldStore (SPEC optimisation:
        /// field access via a fixed array slot instead of a dictionary
        /// lookup per access, see docs/BYTECODE.md). Computed once
        /// and cached - as with FindMethod (see there) the same holds: Fields/Base
        /// are filled ONLY during the one-time compilation, never
        /// changed afterwards at runtime, so the cache is permanently
        /// valid.</summary>

        // RE MESSAGEPACK: IGNORE FOR NOW, IT WILL BE REBUILT ANYWAY IF IN DOUBT

        [MemoryPackIgnore]
        public IReadOnlyList<string> FlattenedFieldNames => _flattenedFieldNames ??= ComputeFlattenedFieldNames();
        
        private List<string>? _flattenedFieldNames;

        private List<string> ComputeFlattenedFieldNames()
        {
            var names = Base != null ? new List<string>(Base.FlattenedFieldNames) : new List<string>();
            foreach (var (name, _) in Fields) names.Add(name);
            return names;
        }

        /// <summary>Field name -> fixed slot index in Runtime.FieldStore, for
        /// O(1) field access instead of a dictionary lookup per instance and
        /// access (see FlattenedFieldNames docs). If a
        /// derived class re-declares a field with the same name as a
        /// base class (unusual, but not forbidden), the LATER (own) index automatically wins
        /// here - the inherited slot
        /// thereby becomes unused (a little memory wasted, but
        /// functionally harmless: just as with the old dictionary-based
        /// behaviour the last write access under the
        /// same name wins in the end anyway).</summary>

        // RE MESSAGEPACK: IGNORE FOR NOW!

        [MemoryPackIgnore]
        public IReadOnlyDictionary<string, int> FieldIndex => _fieldIndex ??= ComputeFieldIndex();
        private Dictionary<string, int>? _fieldIndex;

        private Dictionary<string, int> ComputeFieldIndex()
        {
            var names = FlattenedFieldNames;
            var index = new Dictionary<string, int>(names.Count);
            for (int i = 0; i < names.Count; i++) index[names[i]] = i;
            return index;
        }

        /// <summary>Method name -> all overloads of this name in THIS
        /// class (each with a different parameter count - see
        /// FindMethod for resolution by call argument count). Properties
        /// (get_X/set_X, see PropertyDecl docs) also end up here, as a
        /// list with exactly one entry (no overloading for properties).</summary>
        public Dictionary<string, List<FunctionProto>> Methods { get; }

        /// <summary>Constructor overloads of this class, by
        /// parameter count - unlike methods WITHOUT a base-class chain:
        /// `new Derived(...)` uses only Derived's OWN constructors,
        /// never those of the base class (those are called at most via `: base(...)`
        /// FROM an own constructor). Always at least
        /// one entry (arity 0) - synthesised if the class
        /// declares no `construct` of its own.</summary>
        public Dictionary<int, FunctionProto> Constructors { get; }

        /// <summary>Access modifier of each constructor, by
        /// parameter count (parallel to Constructors) - a private
        /// constructor prevents `new X(...)` from outside the class
        /// (classic singleton/factory-method pattern), see VM.
        /// CheckConstructorAccess.</summary>
        public void AddConstructor(FunctionProto proto)
        {
            if (!Constructors.TryAdd(proto.ParamCount, proto))
                throw new System.InvalidOperationException(
                    $"Internal error: a constructor with {proto.ParamCount} parameters was registered twice.");
        }

        public FunctionProto? Destructor { get; set; }

        /// <summary>Does this class or one of its base classes have a destructor? Without one there is nothing to execute when destroying an object
        /// (see ObjectInstance.Destroy) - the chain is short, a query costs only a few pointer accesses.</summary>
        public bool HasDestructorInChain()
        {
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.Destructor != null) return true;
            return false;
        }

        public RuntimeClass(string name, ClassDecl decl)
        {
            Name = name;
            Decl = decl;
            Fields = new();
            OwnFieldInfo = new();
            StaticFieldValues = new();
            StaticFields = new();
            Methods = new();
            Constructors = new();
        }

        /// <summary>For MemoryPack (see Decl docs and Chunk.Chunk(List&lt;byte&gt;,...)
        /// docs for the detailed reasoning) - Decl stays `null!`
        /// (not needed, never read after compiling). The six
        /// collection parameters are mandatory because Fields/OwnFieldInfo/
        /// StaticFieldValues/StaticFields/Methods/Constructors have NO setter
        /// (deliberately, see their docs) - without a constructor that
        /// accepts them, the generated deserialiser would have no
        /// way to put the values read from the stream anywhere,
        /// and would silently discard them (Base/
        /// IsActor/Destructor are NOT affected, they have normal setters).</summary>
        [MemoryPackConstructor]
        public RuntimeClass(string name, List<(string Name, FunctionProto Init)> fields,
            Dictionary<string, FieldInfo> ownFieldInfo, Dictionary<string, Value> staticFieldValues,
            List<(string Name, FunctionProto Init)> staticFields, Dictionary<string, List<FunctionProto>> methods,
            Dictionary<int, FunctionProto> constructors)
        {
            Name = name;
            Decl = null!;
            Fields = fields;
            OwnFieldInfo = ownFieldInfo;
            StaticFieldValues = staticFieldValues;
            StaticFields = staticFields;
            Methods = methods;
            Constructors = constructors;
        }

        /// <summary>Registers an (overloaded) method under its name -
        /// throws if in THIS class (not base classes - there
        /// overloading again with the same arity in a derived
        /// class is allowed as an "override", see FindMethod) an
        /// overload with EXACTLY the same parameter count already exists -
        /// actually already caught by the resolver (see Resolver.
        /// ResolveClass), here as an additional safety net at
        /// compiler level. `access` applies to ALL overloads of this name
        /// together (not per individual arity) - a deliberate
        /// simplification: different modifiers on overloads
        /// of the same name are a rare, not particularly sensible
        /// case, the last compiled call wins.</summary>
        public void AddMethod(string name, FunctionProto proto)
        {
            if (!Methods.TryGetValue(name, out var overloads))
            {
                overloads = new List<FunctionProto>();
                Methods[name] = overloads;
            }

            if (overloads.Any(p => p.ParamCount == proto.ParamCount))
                throw new System.InvalidOperationException(
                    $"Internal error: method '{name}' with {proto.ParamCount} parameters was registered twice.");

            overloads.Add(proto);
        }

        /// <summary>Searches for a method along the base-class chain (own
        /// class first) by name AND argument count - basis of the
        /// virtual resolution for `obj.Method(...)`. Since the language is
        /// dynamically typed, the argument count is the only
        /// distinguishing feature between
        /// overloads that is reliably known at call time (an overload by TYPE could not be generally
        /// checked). It searches the ENTIRE chain for a
        /// matching arity, not only in the FIRST class that knows the name
        /// at all - so a derived class can add a method
        /// of the same name with a DIFFERENT arity without
        /// hiding the inherited overloads of the base class. Also accepts
        /// an overload with MORE parameters than `argCount` if the
        /// missing (always TRAILING) parameters have default values (see
        /// FindBestMatch) - an exact match always takes precedence.</summary>
        /// <summary>Memoises FindMethod results by (name, argument count) -
        /// the underlying data (Methods/Base) is filled ONLY during the
        /// ONE-TIME compilation (see Compiler.CompileClass),
        /// never changed afterwards at runtime - the cache is therefore valid
        /// forever from the first hit, no invalidation needed.
        /// Without it, EVERY single `obj.Method(...)` call at
        /// runtime would walk the complete base-class chain again via
        /// dictionary lookup + linear overload scan, even
        /// if the result (with an unchanged call site/the same
        /// class) is always the same.</summary>
        /// <summary>Memoises FindMethod results by (name, argument count) -
        /// the underlying data (Methods/Base) is filled ONLY during the
        /// ONE-TIME compilation (see Compiler.CompileClass),
        /// never changed afterwards at runtime - the cache is therefore valid
        /// forever from the first hit, no invalidation needed.
        /// Without it, EVERY single `obj.Method(...)` call at
        /// runtime would walk the complete base-class chain again via
        /// dictionary lookup + linear overload scan, even
        /// if the result (with an unchanged call site/the same
        /// class) is always the same. Additionally carries, along with the proto, the
        /// DECLARING class and its access modifier (see
        /// FindMethodWithAccess) - costs nothing extra, since the walk
        /// already knows the declaring level as soon as it has
        /// found it.</summary>
        private readonly Dictionary<(string Name, int ArgCount), (FunctionProto? Proto, RuntimeClass? DeclaringClass, AccessModifier Access)> _methodCache = new();

        public FunctionProto? FindMethod(string name, int argCount) => FindMethodWithAccess(name, argCount).Proto;

        /// <summary>Like FindMethod, additionally returns the class that
        /// actually declares `name` ITSELF (relevant for the base-class chain
        /// with inheritance/overrides) together with its access modifier -
        /// see VM.CheckMethodAccess. DeclaringClass/Access are meaningless
        /// if Proto is `null` (no matching method found).</summary>
        public (FunctionProto? Proto, RuntimeClass? DeclaringClass, AccessModifier Access) FindMethodWithAccess(string name, int argCount)
        {
            var key = (name, argCount);
            if (_methodCache.TryGetValue(key, out var cached))
                return cached;

            (FunctionProto? Proto, RuntimeClass? DeclaringClass, AccessModifier Access) result = (null, null, AccessModifier.Public);
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.Methods.TryGetValue(name, out var overloads))
                {
                    var match = FindBestMatch(overloads, argCount);
                    if (match != null)
                    {
                        var access = match.Access ?? AccessModifier.Public;
                        result = (match, rc, access);
                        break;
                    }
                }

            _methodCache[key] = result;
            return result;
        }

        /// <summary>Searches for the constructor with this argument count - see
        /// Constructors docs (no base-class chain, unlike
        /// FindMethod). Like FindMethod, also accepts an overload with
        /// more parameters if the missing ones have default values.
        /// Exact match first via direct O(1) dictionary access (instead of
        /// via the general linear FindBestMatch scan, which for the
        /// most common case - class has exactly one constructor with exactly
        /// matching arity - would be unnecessary) - `Constructors` is, after all, already
        /// indexed by arity.</summary>
        public FunctionProto? FindConstructor(int argCount) =>
            Constructors.TryGetValue(argCount, out var exact) ? exact : FindBestMatch(Constructors.Values, argCount);

        /// <summary>Shared resolution for FindMethod/FindConstructor: an
        /// exact arity match always wins; otherwise the overload with the
        /// FEWEST parameters among all that (a) have more parameters than
        /// `argCount` AND (b) have a default value for every extra
        /// (trailing) parameter (see
        /// AllTrailingHaveDefaults) - the "narrowest" matching overload.</summary>
        private static FunctionProto? FindBestMatch(IEnumerable<FunctionProto> overloads, int argCount)
        {
            FunctionProto? exact = null;
            FunctionProto? bestWithDefaults = null;

            foreach (var proto in overloads)
            {
                if (proto.ParamCount == argCount)
                {
                    exact = proto;
                    continue;
                }
                if (proto.ParamCount > argCount && AllTrailingHaveDefaults(proto, argCount))
                    if (bestWithDefaults == null || proto.ParamCount < bestWithDefaults.ParamCount)
                        bestWithDefaults = proto;
            }

            return exact ?? bestWithDefaults;
        }

        /// <summary>Do all parameters of `proto` from index `suppliedCount` on
        /// (i.e. all that would be MISSING in a call with `suppliedCount` arguments)
        /// have a default value? Public, since VM.
        /// CheckArity/FillDefaultArgs also need this for call validation and the
        /// actual filling in (not only the overload
        /// resolution here in FindBestMatch).</summary>
        public static bool AllTrailingHaveDefaults(FunctionProto proto, int suppliedCount)
        {
            for (int i = suppliedCount; i < proto.ParamCount; i++)
                if (i >= proto.ParamDefaults.Count || proto.ParamDefaults[i] == null)
                    return false;
            return true;
        }

        /// <summary>Does ANY method of this name exist (regardless of
        /// argument count), via the base-class chain? For
        /// naming-convention checks that (still) have no fixed arity -
        /// currently unused, since GetIndex/SetIndex/get_/set_ each have a
        /// FIXED, known arity and use FindMethod(name, arity) directly;
        /// kept as a helper method for future cases.</summary>
        public bool HasMethod(string name)
        {
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.Methods.ContainsKey(name))
                    return true;
            return false;
        }

        /// <summary>`true` if `name` (via the base-class chain, like
        /// FindMethod) is a static method/property accessor - for
        /// the error case "ClassName.Method()" on a NON-static
        /// method (see VM.CallStaticMethod) or vice versa "instance.
        /// Method()" on a static one. `argCount` as with FindMethod,
        /// since overloads of different arity could theoretically be
        /// static to differing degrees (the SPEC makes no provision for this, but
        /// FunctionProto.IsStatic is stored per overload anyway -
        /// no reason to restrict this artificially here).</summary>
        public bool IsStaticMethod(string name, int argCount) => FindMethod(name, argCount)?.IsStatic ?? false;
    }
}
