using fire.Bytecode;
using MemoryPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Runtime
{
    /// <summary>Result of a compiler run: the top-level chunk plus the
    /// compiled classes (name -> RuntimeClass) that the VM needs for `new`/
    /// method calls, plus the signatures of all `extern`
    /// declarations (for dynamic linking against a native library named by an `#extern
    /// "libName"`, see VM.CallExtern).</summary>
    [MemoryPackable]
    public sealed partial class CompiledProgram
    {
        public required Chunk TopLevel { get; init; }
        public required IReadOnlyDictionary<string, RuntimeClass> Classes { get; init; }
        public required IReadOnlyDictionary<string, ExternSignature> ExternSignatures { get; init; }

        /// <summary>The files embedded by `new Resource("path")` (docs/stdlib/resources.md); the id of a resource is its index. Set by the linker after compiling.</summary>
        public List<ResourceEntry> Resources { get; set; } = new();

        /// <summary>The enums the program declares, by their full name: what JSON reads into a field of an enum (ProgramEnums, the VM's IJsonHost.FindEnum),
        /// and the enum of a field for the C++ generator - always this program's, never another one's of the same name in the process.</summary>
        [MemoryPack.MemoryPackIgnore]
        public IReadOnlyDictionary<string, fire.Values.EnumType>? Enums
        {
            get => _enums;
            set
            {
                _enums = value;
                EnumDecls = value?.Values.Select(e => new EnumDecl { Name = e.Name, Flags = e.IsFlags, Members = e.Members.Select(m => m.Name).ToList(), Values = e.Members.Select(m => m.Value).ToList() }).ToList() ?? new();
                if (value != null && Classes != null) ProgramEnums.Register(Classes, value);
            }
        }
        private IReadOnlyDictionary<string, fire.Values.EnumType>? _enums;

        /// <summary>The enums as data (what a program cache keeps; RelinkAfterDeserialize makes Enums from it).</summary>
        public List<EnumDecl> EnumDecls { get; set; } = new();

        [MemoryPackable]
        public sealed partial class EnumDecl
        {
            public string Name { get; set; } = "";
            public bool Flags { get; set; }
            public List<string> Members { get; set; } = new();
            public List<long> Values { get; set; } = new();
        }

        /// <summary>The units the program declares (`unit Inch: in { ... }`); the VM makes them known before it runs the program (Unit.UseCustomUnits).</summary>
        public List<fire.Values.CustomUnit> CustomUnits { get; set; } = new();

        /// <summary>MUST be called ONCE after every deserialisation,
        /// before the program is executed (see SPEC "Program
        /// serialisation") - unlike e.g. BinaryFormatter, MemoryPack does NOT track
        /// object identity across multiple occurrences:
        /// `RuntimeClass.Base` of a derived class and the
        /// corresponding entry in `Classes` would, after deserialisation,
        /// otherwise become TWO SEPARATE, independent object instances (with
        /// identical content, but different identity).
        ///
        /// That is harmless for most uses (base-class chain for method/
        /// field resolution - FindMethod/FindFieldAccess work only on
        /// NAMES+DATA, not object identity), but breaks the
        /// SHARED storage location of static fields (RuntimeClass.
        /// StaticFieldValues, see FindStaticFieldOwner docs): `Derived.X`
        /// and `Base.X` would have to be the same dictionary INSTANCE, but would be
        /// two independent (both empty) copies after deserialisation
        /// without this step.
        ///
        /// Instead sets, for every class with a (now possibly
        /// orphaned) Base reference, the CANONICAL instance from
        /// `Classes` (by name) - exactly the same linking the
        /// compiler does on the FIRST compile (see Compiler.
        /// CompileClasses).</summary>
        public void RelinkAfterDeserialize()
        {
            // (the enums of the program again, from their data: the same types as before - EnumType.Intern - and known to its VMs)
            if (_enums == null && EnumDecls.Count > 0)
            {
                var decls = EnumDecls;
                Enums = decls.ToDictionary(d => d.Name, d => fire.Values.EnumType.Intern(d.Name, d.Flags, d.Members.Zip(d.Values).Select(m => (m.First, m.Second)).ToList()), StringComparer.Ordinal);
            }
            foreach (var rc in Classes.Values)
            {
                if (rc.Base != null)
                {
                    if (!Classes.TryGetValue(rc.Base.Name, out var canonicalBase))
                        throw new InvalidOperationException(
                            $"Base class '{rc.Base.Name}' of '{rc.Name}' is missing in Classes - corrupt/" +
                            "incomplete program cache?");
                    rc.Base = canonicalBase;
                }

                // Restore Chunk.OwnerClass (see the docs there - excluded
                // on serialisation, since it is a real cycle back to
                // `rc` itself). EVERY chunk belonging to THIS class
                // (field initialisers, static field initialisers,
                // methods including all overloads, constructors, destructor)
                // gets its OwnerClass back - recursively also for every
                // lambda nested in it (takes over, when compiling,
                // the same enclosing class, see Compiler.CompileLambda)
                // and every parameter default-value proto.
                foreach (var (_, proto) in rc.Fields) RelinkProtoOwner(proto, rc);
                foreach (var (_, proto) in rc.StaticFields) RelinkProtoOwner(proto, rc);
                foreach (var overloads in rc.Methods.Values)
                    foreach (var proto in overloads) RelinkProtoOwner(proto, rc);
                foreach (var proto in rc.AllConstructors) RelinkProtoOwner(proto, rc);
                if (rc.Destructor != null) RelinkProtoOwner(rc.Destructor, rc);
            }
        }

        private static void RelinkProtoOwner(FunctionProto proto, RuntimeClass owner)
        {
            proto.Chunk.OwnerClass = owner;
            foreach (var nested in proto.Chunk.Functions)
                RelinkProtoOwner(nested, owner);
            foreach (var def in proto.ParamDefaults)
                if (def != null) RelinkProtoOwner(def, owner);
        }
    }



}
