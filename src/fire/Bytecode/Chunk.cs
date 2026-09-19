using System.Collections.Generic;
using ScriptLang.Values;

namespace ScriptLang.Bytecode
{
    /// <summary>Ein kompiliertes Programm: Instruktions-Bytes plus die Pools, auf
    /// die per Index verwiesen wird (Konstanten, Einheiten), sowie optionale
    /// Debug-Informationen (Zeilennummern-Tabelle, lokale Variablennamen) für
    /// Werkzeuge wie den Step-Debugger im Editor-Unterprojekt - die eigentliche
    /// VM braucht davon nichts, das ist rein für externe Inspektion.</summary>
    public sealed class Chunk
    {
        public List<byte> Code { get; } = new();
        public List<Value> Constants { get; } = new();
        public List<Unit> Units { get; } = new();
        public List<FunctionProto> Functions { get; } = new();
        public List<HandlerTemplate> Handlers { get; } = new();

        // Zeilennummern-Tabelle: statt PRO Instruktion eine Zeile zu speichern
        // (viel Redundanz, aufeinanderfolgende Instruktionen gehören fast immer
        // zur selben Quelltextzeile), nur die STELLEN, an denen sich die Zeile
        // ändert (Run-Length-artig) - (Byte-Offset im Code, Zeile). GetLine
        // sucht die letzte Stelle mit Offset <= gefragtem Offset.
        private readonly List<(int Offset, int Line)> _lineTable = new();
        private int _lastMarkedLine = -1;

        /// <summary>Vom Compiler aufgerufen, bevor die Instruktionen für ein
        /// neues Statement emittiert werden (siehe Compiler.CompileStmt) -
        /// legt einen neuen Zeilentabellen-Eintrag an, aber NUR wenn sich die
        /// Zeile gegenüber der zuletzt markierten tatsächlich geändert hat
        /// (mehrere Instruktionen derselben Zeile teilen sich einen Eintrag).</summary>
        public void MarkLine(int line)
        {
            if (line == _lastMarkedLine) return;
            _lineTable.Add((Code.Count, line));
            _lastMarkedLine = line;
        }

        /// <summary>Liefert die Quelltextzeile, zu der der Byte-Offset `ip`
        /// gehört (0, wenn keine Zeileninformation vorhanden ist, z.B. für
        /// programmatisch/ohne Compiler gebaute Chunks).</summary>
        public int GetLine(int ip)
        {
            int result = 0;
            foreach (var (offset, line) in _lineTable)
            {
                if (offset > ip) break;
                result = line;
            }
            return result;
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
        public Dictionary<(int Depth, int Slot), string> DebugLocalNames { get; } = new();

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
        public int Here => Code.Count;
    }
}
