using System.Collections.Generic;
using MemoryPack;

namespace fire.Bytecode
{
    /// <summary>Ein deklariertes Mitglied einer Klasse für die Reflection (docs/DESIGN_LAMBDA_REFLECTION_PROBE.md): was zur Laufzeit sonst
    /// verloren geht - die deklarierten Typnamen und Parameternamen. Namen und Zugriff kennt die Laufzeit auch ohne.</summary>
    [MemoryPackable]
    public sealed partial class MemberMeta
    {
        public string Name { get; set; } = "";

        /// <summary>"field", "property", "method" oder "constructor".</summary>
        public string Kind { get; set; } = "field";

        /// <summary>Deklarierter Typ (Feld/Property) bzw. Rückgabetyp (Methode) wie im Quelltext, "" wenn keiner angegeben war.</summary>
        public string TypeName { get; set; } = "";

        /// <summary>"public", "private" oder "protected".</summary>
        public string Access { get; set; } = "public";

        public bool IsStatic { get; set; }
        public bool IsReadonly { get; set; }
        public bool CanRead { get; set; } = true;
        public bool CanWrite { get; set; } = true;

        /// <summary>Geforderte Einheit (`: mm`), "" wenn keine.</summary>
        public string Unit { get; set; } = "";

        public List<string> ParamNames { get; set; } = new();
        public List<string> ParamTypes { get; set; } = new();
    }

    /// <summary>Die in einer Klasse SELBST deklarierten Mitglieder (Vererbung läuft über <see cref="RuntimeClass.Base"/>) und ihre Basisnamen.</summary>
    [MemoryPackable]
    public sealed partial class ClassMeta
    {
        public List<MemberMeta> Members { get; set; } = new();

        /// <summary>Alle Namen aus `class X : A, B` (Basisklasse und Interfaces) wie geschrieben.</summary>
        public List<string> BaseNames { get; set; } = new();
    }
}
