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

        /// <summary>The files embedded by `new Resource("path")` (docs/RESOURCES.md); the id of a resource is its index. Set by the linker after compiling.</summary>
        public List<ResourceEntry> Resources { get; set; } = new();

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
                foreach (var proto in rc.Constructors.Values) RelinkProtoOwner(proto, rc);
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
