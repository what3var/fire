using fire.Bytecode;
using MemoryPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Runtime
{
    /// <summary>Ergebnis eines Compiler-Laufs: der Top-Level-Chunk plus die
    /// kompilierten Klassen (Name -> RuntimeClass), die die VM für `new`/
    /// Methodenaufrufe braucht, plus die Signaturen aller `extern`-
    /// Deklarationen (für dynamisches Linking gegen eine per `#extern
    /// "libName"` benannte native Bibliothek, siehe VM.CallExtern).</summary>
    [MemoryPackable]
    public sealed partial class CompiledProgram
    {
        public required Chunk TopLevel { get; init; }
        public required IReadOnlyDictionary<string, RuntimeClass> Classes { get; init; }
        public required IReadOnlyDictionary<string, ExternSignature> ExternSignatures { get; init; }

        /// <summary>MUSS nach jedem Deserialisieren EINMAL aufgerufen werden,
        /// bevor das Programm ausgeführt wird (siehe SPEC "Programm-
        /// Serialisierung") - MemoryPack verfolgt anders als z.B.
        /// BinaryFormatter KEINE Objekt-Identität über mehrere Vorkommen
        /// hinweg: `RuntimeClass.Base` einer abgeleiteten Klasse und der
        /// entsprechende Eintrag in `Classes` würden nach dem Deserialisieren
        /// sonst zu ZWEI SEPARATEN, unabhängigen Objektinstanzen (mit
        /// identischem Inhalt, aber verschiedener Identität).
        ///
        /// Das ist für die meiste Nutzung (Basisklassen-Kette für Methoden-/
        /// Feldauflösung - FindMethod/FindFieldAccess laufen ja nur über
        /// NAMEN+DATEN, nicht Objekt-Identität) unschädlich, bricht aber die
        /// GETEILTE Speicherstelle statischer Felder (RuntimeClass.
        /// StaticFieldValues, siehe FindStaticFieldOwner-Doku): `Derived.X`
        /// und `Base.X` müssten dieselbe Dictionary-INSTANZ sein, wären nach
        /// dem Deserialisieren ohne diesen Schritt aber zwei unabhängige
        /// (beide leere) Kopien.
        ///
        /// Setzt für jede Klasse mit einer (jetzt möglicherweise
        /// verwaisten) Base-Referenz stattdessen die KANONISCHE Instanz aus
        /// `Classes` (nach Name) - exakt dieselbe Verknüpfung, die der
        /// Compiler beim ERSTEN Kompilieren auch macht (siehe Compiler.
        /// CompileClasses).</summary>
        public void RelinkAfterDeserialize()
        {
            foreach (var rc in Classes.Values)
            {
                if (rc.Base != null)
                {
                    if (!Classes.TryGetValue(rc.Base.Name, out var canonicalBase))
                        throw new InvalidOperationException(
                            $"Basisklasse '{rc.Base.Name}' von '{rc.Name}' fehlt in Classes - beschädigter/" +
                            "unvollständiger Programm-Cache?");
                    rc.Base = canonicalBase;
                }

                // Chunk.OwnerClass wiederherstellen (siehe dortige Doku - beim
                // Serialisieren ausgenommen, da ein echter Zyklus zurück auf
                // `rc` selbst). JEDER Chunk, der zu DIESER Klasse gehört
                // (Feld-Initialisierer, statische Feld-Initialisierer,
                // Methoden inkl. aller Überladungen, Konstruktoren, Destruktor)
                // bekommt seinen OwnerClass zurück - rekursiv auch für jede
                // darin verschachtelte Lambda (übernimmt beim Kompilieren
                // dieselbe umschließende Klasse, siehe Compiler.CompileLambda)
                // und jeden Parameter-Standardwert-Proto.
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
