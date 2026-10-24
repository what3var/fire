using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Runtime;
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

        /// <summary>The kind a field declared `int`/`float`/`bool` holds (DeclaredKind: TypeTag + 1, 0: none) - every assignment is checked.</summary>
        public byte RequiredKind { get; set; }

        /// <summary>What a field declared with a class, an interface or a constrained type parameter holds (or `undefined`): every assignment is checked
        /// (DeclaredType, its encoded form); null: none.</summary>
        public string? RequiredType { get; set; }

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

        /// <summary>The type arguments this class gives its base class (`class IntBox : Box&lt;int&gt;`, `class MyList&lt;U&gt; : List&lt;U&gt;`), as
        /// DeclaredType texts - `$k` for the k-th type parameter of this class; null when the base is not generic (DeclaredType.TypeArgOf).</summary>
        public string[]? BaseTypeArgs { get; set; }

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

        /// <summary>Like FindFieldRequiredUnit, for the declared class or interface (FieldInfo.RequiredType, null: none).</summary>
        public string? FindFieldRequiredType(string name)
        {
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.OwnFieldInfo.TryGetValue(name, out var field))
                    return field.RequiredType;
            return null;
        }

        /// <summary>Like FindFieldRequiredUnit, for the kind (FieldInfo.RequiredKind, 0: none).</summary>
        public byte FindFieldRequiredKind(string name)
        {
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.OwnFieldInfo.TryGetValue(name, out var field))
                    return field.RequiredKind;
            return 0;
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
            if (Constructors.TryAdd(proto.ParamCount, proto)) return;
            // overloading by type: another constructor with as many parameters, of other types
            if (Overloads.SameSignature(Constructors[proto.ParamCount], proto) || ExtraConstructors.Any(c => Overloads.SameSignature(c, proto)))
                throw new System.InvalidOperationException(
                    $"Internal error: a constructor with {proto.ParamCount} parameters was registered twice.");
            ExtraConstructors.Add(proto);
        }

        /// <summary>Constructors with the same number of parameters as one in Constructors, but other parameter types (overloading by type, see Overloads).</summary>
        public List<FunctionProto> ExtraConstructors { get; set; } = new();

        /// <summary>All constructors (Constructors and ExtraConstructors).</summary>
        [MemoryPackIgnore]
        public IEnumerable<FunctionProto> AllConstructors => Constructors.Values.Concat(ExtraConstructors);

        public FunctionProto? Destructor { get; set; }

        /// <summary>For each method and property getter of this class (key: the method name, `get_Name` for a getter) the members of `this` it reads
        /// (`this.x`, `x` without `this.`, `this.M()`); filled by the compiler. The base of probes on computed properties (SPEC 8.14).</summary>
        public Dictionary<string, string[]>? ThisReads { get; set; }

        /// <summary>A computed property (a getter, no setter, no field of that name): the fields and settable properties of `this` its value
        /// depends on - what its getter reads, followed through the getters and methods of the class it calls. null when `name` is no computed property.</summary>
        public List<string>? ComputedSources(string name)
        {
            if (FieldIndex.ContainsKey(name) || FindMethod("set_" + name, 1) != null || FindMethod("get_" + name, 0) == null) return null;
            var sources = new List<string>();
            var seen = new HashSet<string>();
            var pending = new Queue<string>();
            pending.Enqueue("get_" + name);
            seen.Add("get_" + name);
            while (pending.Count > 0)
            {
                string key = pending.Dequeue();
                string[]? reads = null;
                for (var c = this; c != null && reads == null; c = c.Base) c.ThisReads?.TryGetValue(key, out reads);
                if (reads == null) continue;
                foreach (var read in reads)
                {
                    bool isField = FieldIndex.ContainsKey(read), hasSetter = FindMethod("set_" + read, 1) != null;
                    if ((isField || hasSetter) && !sources.Contains(read)) sources.Add(read);
                    if (!isField && FindMethod("get_" + read, 0) != null && seen.Add("get_" + read)) pending.Enqueue("get_" + read);
                    else if (!isField && Methods.Keys.Concat(BaseMethodNames()).Contains(read) && seen.Add(read)) pending.Enqueue(read);
                }
            }
            return sources;
        }

        private IEnumerable<string> BaseMethodNames()
        {
            for (var c = Base; c != null; c = c.Base)
                foreach (var name in c.Methods.Keys) yield return name;
        }

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

            // the same number of parameters is an overload by type (Overloads) - unless the types are the same as well
            if (overloads.Any(p => Overloads.SameSignature(p, proto)))
                throw new System.InvalidOperationException(
                    $"Internal error: method '{name}' with {proto.ParamCount} parameters was registered twice.");

            overloads.Add(proto);
        }

        /// <summary>The methods a call `name` with `argCount` arguments may reach, in the order of preference for a tie: along the base-class chain (the
        /// own class first), in a class an exact number of parameters before one that fills in default values (fewer parameters first), then in the
        /// order of declaration. A method of a base class that a method of a derived class overrides (Overloads.Overrides) is left out. The first
        /// entry is what a call without overloading by type reaches. `RuntimeChoice`: the types of the arguments decide between them.</summary>
        public sealed class MethodCandidates
        {
            public static readonly MethodCandidates Empty = new(Array.Empty<FunctionProto>(), Array.Empty<RuntimeClass>(), false);
            public FunctionProto[] Protos { get; }
            public RuntimeClass[] Declaring { get; }
            public bool RuntimeChoice { get; }
            public int Count => Protos.Length;

            public MethodCandidates(FunctionProto[] protos, RuntimeClass[] declaring, bool runtimeChoice)
            {
                Protos = protos;
                Declaring = declaring;
                RuntimeChoice = runtimeChoice;
            }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Name, int ArgCount), MethodCandidates> _candidateCache = new();

        public MethodCandidates Candidates(string name, int argCount) => _candidateCache.GetOrAdd((name, argCount), key => ComputeCandidates(key.Name, key.ArgCount));

        private MethodCandidates ComputeCandidates(string name, int argCount)
        {
            var protos = new List<FunctionProto>();
            var declaring = new List<RuntimeClass>();
            var seen = new List<FunctionProto>();   // every method of a more derived class (also of other arities: it may override)
            for (var rc = this; rc != null; rc = rc.Base)
            {
                if (!rc.Methods.TryGetValue(name, out var overloads)) continue;
                var fitting = overloads
                    .Where(p => p.ParamCount == argCount || p.ParamCount > argCount && AllTrailingHaveDefaults(p, argCount))
                    .Where(p => !seen.Any(d => Overloads.Overrides(d, p)))
                    .OrderBy(p => p.ParamCount == argCount ? 0 : 1).ThenBy(p => p.ParamCount)
                    .ToList();
                foreach (var p in fitting)
                {
                    protos.Add(p);
                    declaring.Add(rc);
                }
                seen.AddRange(overloads);
            }
            return protos.Count == 0 ? MethodCandidates.Empty : new MethodCandidates(protos.ToArray(), declaring.ToArray(), NeedsRuntimeChoice(protos, argCount));
        }

        /// <summary>Several candidates of which one has a typed parameter (or is generic) among the given arguments: the values decide. Otherwise the
        /// first candidate is always the one (untyped overloads differ only by their number of parameters).</summary>
        private static bool NeedsRuntimeChoice(IReadOnlyList<FunctionProto> protos, int argCount)
        {
            if (protos.Count < 2) return false;
            foreach (var p in protos)
            {
                if (p.TypeParamCount > 0) return true;
                for (int i = 0; i < argCount && i < p.ParamCount; i++)
                    if (!string.IsNullOrEmpty(Overloads.TypeAt(p, i))) return true;
            }
            return false;
        }

        /// <summary>Like FindMethodWithAccess(name, argCount), choosing among overloads by type with the values of the arguments (Overloads.Select).
        /// `owner`: the object whose type parameters a `T` parameter means; `callTypeArgs`: the type arguments of the call (of a generic method);
        /// `typeArgCount` &gt; 0: the call names type arguments (`M&lt;string&gt;(x)`), so a generic method is meant.</summary>
        public (FunctionProto? Proto, RuntimeClass? DeclaringClass, AccessModifier Access) FindMethodWithAccess(string name, ReadOnlySpan<Value> args, ObjectInstance? owner,
            string[]? callTypeArgs, int typeArgCount = 0)
        {
            var set = Candidates(name, args.Length);
            if (set.Count == 0) return (null, null, AccessModifier.Public);
            int index = 0;
            if (set.RuntimeChoice || typeArgCount > 0)
            {
                index = Overloads.Select(set.Protos, args, owner, callTypeArgs, typeArgCount);
                if (index < 0)
                {
                    // nothing fits: the first candidate (of the named generic ones) - its parameter checks report what does not fit
                    index = 0;
                    if (typeArgCount > 0)
                        for (int i = 0; i < set.Count; i++)
                            if (set.Protos[i].TypeParamCount == typeArgCount) { index = i; break; }
                }
            }
            var proto = set.Protos[index];
            return (proto, set.Declaring[index], proto.Access ?? AccessModifier.Public);
        }

        /// <summary>Does the choice among the methods `name` for `argCount` arguments depend on the types of the arguments (no inline cache then)?</summary>
        public bool IsTypeOverloaded(string name, int argCount) => Candidates(name, argCount).RuntimeChoice;

        /// <summary>Does the choice of the constructor for `argCount` arguments depend on the types of the arguments?</summary>
        public bool IsConstructorTypeOverloaded(int argCount) => ConstructorCandidates(argCount).Count > 1 && ExtraConstructors.Count > 0;

        public List<FunctionProto> ConstructorCandidates(int argCount) =>
            AllConstructors.Where(p => p.ParamCount == argCount || p.ParamCount > argCount && AllTrailingHaveDefaults(p, argCount))
                .OrderBy(p => p.ParamCount == argCount ? 0 : 1).ThenBy(p => p.ParamCount).ToList();

        /// <summary>The constructor for these arguments: among overloads by type the one that fits them most closely (see Overloads).</summary>
        public FunctionProto? FindConstructor(ReadOnlySpan<Value> args, ObjectInstance? instance)
        {
            if (ExtraConstructors.Count == 0) return FindConstructor(args.Length);
            var candidates = ConstructorCandidates(args.Length);
            if (candidates.Count == 0) return null;
            int index = Overloads.Select(candidates, args, instance, null);
            return candidates[index < 0 ? 0 : index];
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
        // (concurrent: the fire threads of a program share the classes and fill the cache at the same time)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Name, int ArgCount), (FunctionProto? Proto, RuntimeClass? DeclaringClass, AccessModifier Access)> _methodCache = new();

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

            // the first candidate: the own class first, an exact number of parameters before default values (see Candidates)
            var set = Candidates(name, argCount);
            (FunctionProto? Proto, RuntimeClass? DeclaringClass, AccessModifier Access) result = set.Count == 0
                ? (null, null, AccessModifier.Public)
                : (set.Protos[0], set.Declaring[0], set.Protos[0].Access ?? AccessModifier.Public);
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
