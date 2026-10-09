using System.Collections.Generic;
using MemoryPack;

namespace fire.Bytecode
{
    /// <summary>A declared member of a class for reflection (docs/DESIGN_LAMBDA_REFLECTION_PROBE.md): what is otherwise lost at runtime
    /// - the declared type names and parameter names. Names and access are known to the runtime even without it.</summary>
    [MemoryPackable]
    public sealed partial class MemberMeta
    {
        public string Name { get; set; } = "";

        /// <summary>"field", "property", "method" or "constructor".</summary>
        public string Kind { get; set; } = "field";

        /// <summary>Declared type (field/property) or return type (method) as in the source, "" if none was given.</summary>
        public string TypeName { get; set; } = "";

        /// <summary>"public", "private" or "protected".</summary>
        public string Access { get; set; } = "public";

        public bool IsStatic { get; set; }
        public bool IsReadonly { get; set; }
        public bool CanRead { get; set; } = true;
        public bool CanWrite { get; set; } = true;

        /// <summary>Required unit (`: mm`), "" if none.</summary>
        public string Unit { get; set; } = "";

        public List<string> ParamNames { get; set; } = new();
        public List<string> ParamTypes { get; set; } = new();
    }

    /// <summary>The members declared in a class ITSELF (inheritance goes via <see cref="RuntimeClass.Base"/>) and their base names.</summary>
    [MemoryPackable]
    public sealed partial class ClassMeta
    {
        public List<MemberMeta> Members { get; set; } = new();

        /// <summary>All names from `class X : A, B` (base class and interfaces) as written.</summary>
        public List<string> BaseNames { get; set; } = new();
    }
}
