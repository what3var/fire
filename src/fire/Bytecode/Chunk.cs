using System.Collections.Generic;
using fire.Values;
using MemoryPack;

namespace fire.Bytecode
{
    /// <summary>Ein kompiliertes Programm: Instruktions-Bytes plus die Pools, auf
    /// die per Index verwiesen wird (Konstanten, Einheiten), sowie optionale
    /// Debug-Informationen (Zeilennummern-Tabelle, lokale Variablennamen) für
    /// Werkzeuge wie den Step-Debugger im Editor-Unterprojekt - die eigentliche
    /// VM braucht davon nichts, das ist rein für externe Inspektion.</summary>
    [MemoryPackable]
    public sealed partial class Chunk
    {
        public List<byte> Code { get; }
        public List<Value> Constants { get; }
        public List<Unit> Units { get; }
        public List<FunctionProto> Functions { get; }
        public List<HandlerTemplate> Handlers { get; }

        /// <summary>Die Klasse, deren Methode/Konstruktor/Property-Accessor
        /// dieser Chunk ist - `null` für Top-Level-Code, freie Lambdas und
        /// alles andere außerhalb einer Klasse. Vom Compiler gesetzt (siehe
        /// CompileMethodProto/CompileConstructorProto), von der VM für die
        /// Zugriffsmodifikator-Prüfung gelesen (siehe VM.
        /// IsMemberAccessAllowed): das ist die Klasse, deren CODE gerade
        /// tatsächlich ausführt - bewusst NICHT dasselbe wie die konkrete
        /// Klasse von `this` (VM._currentThis)! Ruft z.B. eine `Derived`-
        /// Instanz eine geerbte, nicht überschriebene `Base`-Methode auf,
        /// oder läuft `Base`s eigener Konstruktor als Teil einer `Derived`-
        /// Konstruktion (siehe ConstructBase), ist `this` konkret eine
        /// `Derived`-Instanz, obwohl gerade `Base`s eigener Code läuft -
        /// für "darf dieser Code auf Base's privates Mitglied zugreifen"
        /// zählt die Klasse des AUSFÜHRENDEN CODES (Base), nicht die
        /// konkrete Instanzklasse (Derived).
        ///
        /// SERIALISIERUNG: erzeugt einen echten Zyklus (RuntimeClass ->
        /// Methode/Feld-Initialisierer/Konstruktor -> dieser Chunk ->
        /// OwnerClass -> dieselbe RuntimeClass) - MemoryPack verfolgt keine
        /// Objekt-Identität/Zyklen (siehe CompiledProgram.
        /// RelinkAfterDeserialize-Doku für dieselbe Begründung bei
        /// RuntimeClass.Base), würde also endlos rekursieren ("reached depth
        /// limit"). Deshalb ausgenommen und nach dem Deserialisieren über
        /// RelinkAfterDeserialize wiederhergestellt.</summary>
        [MemoryPackIgnore]
        public RuntimeClass? OwnerClass { get; set; }

        // Zeilennummern-Tabelle: statt PRO Instruktion eine Zeile zu speichern
        // (viel Redundanz, aufeinanderfolgende Instruktionen gehören fast immer
        // zur selben Quelltextzeile), nur die STELLEN, an denen sich die Zeile
        // ändert (Run-Length-artig) - (Byte-Offset im Code, Quell-Index, Zeile).
        // GetLocation sucht die letzte Stelle mit Offset <= gefragtem Offset.
        //
        // Quell-Index (SourceIndex): Position der jeweiligen Quelle in der
        // `sources`-Liste, die an Parser.ParseMultiple ging (0 = üblicherweise
        // die Prelude) - siehe Ast.ClassDecl.SourceIndex/Compiler.
        // CurrentSourceIndex. Nötig, seit ein Programm aus MEHREREN Dateien
        // bestehen kann (SPEC "Mehrere Quelldateien"): eine nackte Zeilenzahl
        // allein ist dann mehrdeutig (Zeile 5 in Datei A und Zeile 5 in
        // Datei B sind unterschiedliche Stellen) - Werkzeuge wie der
        // Step-Debugger im Editor-Unterprojekt brauchen BEIDES, um die
        // richtige Datei/Zeile anzuzeigen und Haltepunkte korrekt zu treffen.
        private readonly List<(int Offset, int SourceIndex, int Line)> _lineTable = new();
        private int _lastMarkedSourceIndex = -1;
        private int _lastMarkedLine = -1;

        /// <summary>Vom Compiler aufgerufen, bevor die Instruktionen für ein
        /// neues Statement emittiert werden (siehe Compiler.CompileStmt) -
        /// legt einen neuen Zeilentabellen-Eintrag an, aber NUR wenn sich
        /// Quelle+Zeile gegenüber der zuletzt markierten Stelle tatsächlich
        /// geändert haben (mehrere Instruktionen derselben Stelle teilen sich
        /// einen Eintrag).</summary>
        public void MarkLine(int sourceIndex, int line)
        {
            if (sourceIndex == _lastMarkedSourceIndex && line == _lastMarkedLine) return;
            _lineTable.Add((Code.Count, sourceIndex, line));
            _lastMarkedSourceIndex = sourceIndex;
            _lastMarkedLine = line;
        }

        /// <summary>Liefert Quell-Index und Quelltextzeile, zu denen der
        /// Byte-Offset `ip` gehört ((0, 0), wenn keine Zeileninformation
        /// vorhanden ist, z.B. für programmatisch/ohne Compiler gebaute
        /// Chunks).</summary>
        public (int SourceIndex, int Line) GetLocation(int ip)
        {
            int resultSource = 0, resultLine = 0;
            foreach (var (offset, sourceIndex, line) in _lineTable)
            {
                if (offset > ip) break;
                resultSource = sourceIndex;
                resultLine = line;
            }
            return (resultSource, resultLine);
        }

        // Debug-Namen für lokale Variablen: (Tiefe relativ zur jeweiligen
        // Deklarationsstelle zur KOMPILIERZEIT, Slot) -> Name. "Tiefe" hier
        // bedeutet dieselbe Zählweise wie beim LoadLocal-Opcode (0 = die Scope,
        // in der die Variable deklariert wurde) - ein Debugger, der zur
        // Laufzeit an einer bestimmten Stelle pausiert, muss diese Tiefe daher
        // relativ zur AKTUELLEN Scope-Tiefe an dieser Stelle interpretieren
        // (siehe VM.DescribeLocals). Bewusst ein Best-Effort-Register, keine
        // perfekte 1:1-Abbildung für jeden denkbaren Verschachtelungsfall -
        // reicht aber für die allermeisten Fälle (Parameter, top-level lokale
        // Variablen einer Funktion/Methode/eines Lambdas).
        public Dictionary<(int Depth, int Slot), string> DebugLocalNames { get; }

        /// <summary>Normale Verwendung (Compiler baut den Chunk schrittweise
        /// per EmitByte/AddConstant/... auf) - alle Sammlungen leer.</summary>
        public Chunk()
        {
            Code = new();
            Constants = new();
            Units = new();
            Functions = new();
            Handlers = new();
            DebugLocalNames = new();
        }

        /// <summary>Für MemoryPack (siehe Klassendoku "SERIALISIERUNG") - OHNE
        /// eigenen Konstruktor mit diesen Parametern hätte der generierte
        /// Deserialisierer keine Möglichkeit, die aus dem Stream gelesenen
        /// Werte irgendwo unterzubringen (die Properties haben bewusst KEINEN
        /// Setter, siehe oben) - er würde sie schlicht VERWERFEN und
        /// stattdessen `new Chunk()` mit lauter leeren Sammlungen anlegen,
        /// ohne jede Fehlermeldung. Namen der Parameter müssen (Groß-/
        /// Kleinschreibung ignoriert) zu den Property-Namen passen, das ist
        /// die Konvention, an der MemoryPack Konstruktor-Parameter zu
        /// Properties zuordnet.</summary>
        [MemoryPackConstructor]
        public Chunk(List<byte> code, List<Value> constants, List<Unit> units, List<FunctionProto> functions,
            List<HandlerTemplate> handlers, Dictionary<(int Depth, int Slot), string> debugLocalNames)
        {
            Code = code;
            Constants = constants;
            Units = units;
            Functions = functions;
            Handlers = handlers;
            DebugLocalNames = debugLocalNames;
        }

        public void MarkLocalName(int depth, int slot, string name) => DebugLocalNames[(depth, slot)] = name;

        public int AddConstant(Value v)
        {
            Constants.Add(v);
            return Constants.Count - 1;
        }

        public int AddUnit(Unit u)
        {
            Units.Add(u);
            return Units.Count - 1;
        }

        public int AddFunctionProto(FunctionProto proto)
        {
            Functions.Add(proto);
            return Functions.Count - 1;
        }

        public int AddHandlerTemplate(HandlerTemplate template)
        {
            Handlers.Add(template);
            return Handlers.Count - 1;
        }

        public void EmitByte(byte b) => Code.Add(b);
        public void EmitOp(OpCode op) => EmitByte((byte)op);

        public void EmitU16(int value)
        {
            Code.Add((byte)(value & 0xFF));
            Code.Add((byte)((value >> 8) & 0xFF));
        }

        /// <summary>Schreibt einen u16-Wert an eine bereits emittierte Stelle
        /// zurück (Backpatching für Sprungziele, die erst nach dem Kompilieren
        /// des übersprungenen Codes bekannt sind).</summary>
        public void PatchU16(int at, int value)
        {
            Code[at] = (byte)(value & 0xFF);
            Code[at + 1] = (byte)((value >> 8) & 0xFF);
        }

        /// <summary>Aktuelle Schreibposition - als Sprungziel oder als Ausgangspunkt
        /// für ein späteres PatchU16 nützlich.</summary>
        [MemoryPackIgnore]
        public int Here => Code.Count;
    }
}
