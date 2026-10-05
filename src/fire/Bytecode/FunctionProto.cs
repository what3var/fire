using fire.Ast;
using MemoryPack;
using System;
using System.Collections.Generic;

namespace fire.Bytecode
{
    /// <summary>
    /// Kompilierter Funktionskörper: ein eigener Chunk (die Lambda sieht ja
    /// ohnehin nur ihren eigenen Scope + global, braucht also keine Upvalues/
    /// eingefangenen Variablen - ein simpler eigener Adressraum reicht) plus die
    /// Anzahl erwarteter Parameter. Wird einmal beim Kompilieren einer LambdaExpr
    /// erzeugt; jede Auswertung dieser LambdaExpr zur Laufzeit (z.B. in einer
    /// Schleife) erzeugt dagegen einen neuen LambdaValue, der auf denselben
    /// (wiederverwendeten) FunctionProto verweist.
    ///
    /// ParamDefaults: parallel zu den Parametern, ein eigener 0-Arg-Proto pro
    /// optionalem Parameter (null für Pflichtparameter) - wird von VM.
    /// FillDefaultArgs ausgewertet, wenn ein Aufruf weniger Argumente liefert
    /// als ParamCount (siehe Ast.LambdaParam.DefaultValue-Doku).
    /// </summary>
    [MemoryPackable]
    public sealed partial class FunctionProto
    {
        public Chunk Chunk { get; }
        public int ParamCount { get; }
        public IReadOnlyList<FunctionProto?> ParamDefaults { get; }

        public AccessModifier? Access { get; set; }

        /// <summary>SPEC "Statische Mitglieder" - `true` für eine `static`
        /// deklarierte Methode/Property-Accessor (Konstruktoren sind nie
        /// statisch). Bestimmt beim Aufruf, ob VM.CallMethod ein Objekt
        /// erwartet (Instanzmethode) oder VM.CallStaticMethod ohne
        /// gebundenes 'this' läuft.</summary>
        public bool IsStatic { get; set; }

        /// <summary>Bit i: der Parameter i ist `ref` deklariert (SPEC 5.4.2) - sein Slot haelt einen Zeiger auf die Variable des Aufrufers.</summary>
        public uint RefMask { get; set; }

        /// <summary>Besteht der Körper einer Lambda mit genau einem Parameter nur aus einer Mitgliedskette auf diesem Parameter
        /// (`c => c.radius`, `p => p.address.city`), die Namen von außen nach innen - sonst null. Grundlage des Lambda-Typs `selector`.</summary>
        public string[]? SelectorPath { get; set; }

        private NativeForwarder? _forwarder;
        private bool _forwarderChecked;

        /// <summary>Ist die Methode eine reine Weiterleitung an eine native Funktion (siehe NativeForwarder), sonst null.
        /// Wird beim ersten Bedarf aus dem Bytecode ermittelt (nicht serialisiert).</summary>
        [MemoryPackIgnore]
        public NativeForwarder? Forwarder
        {
            get
            {
                if (!_forwarderChecked)
                {
                    _forwarder = NativeForwarder.TryCreate(Chunk, ParamCount);
                    _forwarderChecked = true;
                }
                return _forwarder;
            }
        }

        public FunctionProto(Chunk chunk, int paramCount, AccessModifier? access, IReadOnlyList<FunctionProto?>? paramDefaults = null, bool isStatic = false)
        {
            Chunk = chunk;
            ParamCount = paramCount;
            ParamDefaults = paramDefaults ?? Array.Empty<FunctionProto?>();
            Access = access;
            IsStatic = isStatic;
        }
    }
}
