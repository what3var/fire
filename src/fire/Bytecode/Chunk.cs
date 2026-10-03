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
        public (int SourceIndex, int Line) GetLocation(int ip) => GetLocationRange(ip, out _, out _);

        /// <summary>Wie <see cref="GetLocation"/>, liefert aber zusätzlich den Byte-Bereich [start, endExclusive), in dem
        /// dieselbe Stelle gilt - wer viele aufeinanderfolgende Offsets abfragt (der Debugger beim Weiterlaufen), muss
        /// nur beim Verlassen dieses Bereichs neu nachschlagen. Die Tabelle ist nach Offset sortiert (MarkLine hängt
        /// immer am aktuellen Code-Ende an): binäre Suche statt eines Durchlaufs vom Anfang bei jeder Abfrage.</summary>
        public (int SourceIndex, int Line) GetLocationRange(int ip, out int start, out int endExclusive)
        {
            var table = _lineTable;
            int lo = 0, hi = table.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (table[mid].Offset <= ip) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            start = found < 0 ? 0 : table[found].Offset;
            endExclusive = found + 1 < table.Count ? table[found + 1].Offset : int.MaxValue;
            return found < 0 ? (0, 0) : (table[found].SourceIndex, table[found].Line);
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

        // Array-Kopien von Code und Konstanten für die VM (siehe VM.ReadByte/LoadConst): ein Array-Zugriff ist
        // deutlich billiger als der Indexer einer List<T>, und der Code wird pro Instruktion 1-3 Mal gelesen.
        // Beim Bauen (Emit*/Patch*/AddConstant) wird die Kopie verworfen und beim nächsten Lesen neu angelegt;
        // nach dem Kompilieren ändert sich nichts mehr. Zwei Threads dürfen gleichzeitig anlegen (gleicher Inhalt).
        [MemoryPackIgnore] private byte[]? _codeArray;
        [MemoryPackIgnore] private Value[]? _constantsArray;

        [MemoryPackIgnore]
        public byte[] CodeArray => _codeArray ??= Code.ToArray();

        /// <summary>Inline-Caches der Aufrufstellen dieses Chunks, indiziert mit dem Byte-Offset des Opcodes
        /// (siehe SiteCache). Erst beim ersten Bedarf angelegt (nach dem Kompilieren ist die Codelänge fest).</summary>
        [MemoryPackIgnore]
        public SiteCache?[]? SiteCaches;

        public SiteCache?[] EnsureSiteCaches() => SiteCaches ??= new SiteCache?[Code.Count + 1];

        [MemoryPackIgnore]
        public Value[] ConstantsArray => _constantsArray ??= Constants.ToArray();

        public int AddConstant(Value v)
        {
            _constantsArray = null;
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

        public void EmitByte(byte b)
        {
            _codeArray = null;
            Code.Add(b);
        }
        public void EmitOp(OpCode op)
        {
            _lastOpStart = Code.Count;
            EmitByte((byte)op);
        }

        // Für das Verschmelzen von Instruktionen beim Übersetzen (siehe EndsWithOp): wo der zuletzt emittierte Opcode beginnt
        // und an welcher Stelle zuletzt ein Sprungziel/Patch-Punkt abgefragt wurde (jede Marke entsteht über `Here`).
        [MemoryPackIgnore] private int _lastOpStart = -1;
        [MemoryPackIgnore] private int _lastHere = -1;

        /// <summary>Ist `op` (mit `operandBytes` Operandenbytes) die zuletzt emittierte Instruktion, und zeigt KEIN Sprungziel hinter sie?
        /// Nur dann darf der Compiler sie zusammen mit der nächsten zu einer verschmolzenen Instruktion machen: eine Marke hinter ihr
        /// würde nach dem Verschmelzen mitten in die neue Instruktion zeigen.</summary>
        public bool EndsWithOp(OpCode op, int operandBytes) =>
            _lastOpStart >= 0 && _lastOpStart == Code.Count - 1 - operandBytes && Code[_lastOpStart] == (byte)op && _lastHere != Code.Count;

        /// <summary>Ersetzt den Opcode der zuletzt emittierten Instruktion (gleiche Operanden) - nach einem erfolgreichen EndsWithOp.</summary>
        public void ReplaceLastOp(OpCode op)
        {
            _codeArray = null;
            Code[_lastOpStart] = (byte)op;
        }

        public void EmitU16(int value)
        {
            _codeArray = null;
            Code.Add((byte)(value & 0xFF));
            Code.Add((byte)((value >> 8) & 0xFF));
        }

        /// <summary>Schreibt einen u16-Wert an eine bereits emittierte Stelle
        /// zurück (Backpatching für Sprungziele, die erst nach dem Kompilieren
        /// des übersprungenen Codes bekannt sind).</summary>
        public void PatchU16(int at, int value)
        {
            _codeArray = null;
            Code[at] = (byte)(value & 0xFF);
            Code[at + 1] = (byte)((value >> 8) & 0xFF);
        }

        /// <summary>Aktuelle Schreibposition - als Sprungziel oder als Ausgangspunkt
        /// für ein späteres PatchU16 nützlich.</summary>
        [MemoryPackIgnore]
        public int Here
        {
            get
            {
                _lastHere = Code.Count;
                return Code.Count;
            }
        }
    }
}
