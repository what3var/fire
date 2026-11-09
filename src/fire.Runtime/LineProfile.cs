using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using fire.Bytecode;

namespace fire.Runtime
{
    /// <summary>The profiler of a debug run (spark's Profile switch, docs/tools/spark.md): per source line how often a thread came into it and how many instructions
    /// of the VM ran in it - which code is used how much, and with <see cref="ExecutableLines"/> which never ran. The VM counts while it runs on to a breakpoint
    /// or to the end (VM.RunUntilBreakpoint) as long as a profile is <see cref="Active"/>; every thread of the run adds to the same profile.</summary>
    public sealed class LineProfile
    {
        private sealed class Counts { public long Hits; public long Instructions; }
        private readonly ConcurrentDictionary<(int SourceIndex, int Line), Counts> _lines = new();

        /// <summary>The profile the VMs count into (null: no profiling).</summary>
        public static LineProfile? Active { get; set; }

        /// <summary>The run came into `line` `hits` times and ran `instructions` instructions in it.</summary>
        public void Add((int SourceIndex, int Line) line, long hits, long instructions)
        {
            if (line.Line <= 0) return;
            var c = _lines.GetOrAdd(line, _ => new Counts());
            if (hits != 0) Interlocked.Add(ref c.Hits, hits);
            if (instructions != 0) Interlocked.Add(ref c.Instructions, instructions);
        }

        /// <summary>What was counted: line -> (hits, instructions).</summary>
        public Dictionary<(int SourceIndex, int Line), (long Hits, long Instructions)> Snapshot()
        {
            var result = new Dictionary<(int, int), (long, long)>();
            foreach (var (k, v) in _lines) result[k] = (Interlocked.Read(ref v.Hits), Interlocked.Read(ref v.Instructions));
            return result;
        }

        /// <summary>Every line the program has code for: its top level, the functions in it, the methods, constructors, destructors, field initialisers and
        /// default values of its classes, and the lambdas inside them.</summary>
        public static HashSet<(int SourceIndex, int Line)> ExecutableLines(CompiledProgram program)
        {
            var lines = new HashSet<(int, int)>();
            var seen = new HashSet<Chunk>(ReferenceEqualityComparer.Instance);
            void Walk(Chunk? chunk)
            {
                if (chunk == null || !seen.Add(chunk)) return;
                foreach (var l in chunk.Lines) lines.Add(l);
                foreach (var f in chunk.Functions) Proto(f);
            }
            void Proto(FunctionProto? p)
            {
                if (p == null) return;
                Walk(p.Chunk);
                foreach (var d in p.ParamDefaults) Proto(d);
            }
            Walk(program.TopLevel);
            foreach (var rc in program.Classes.Values)
            {
                foreach (var list in rc.Methods.Values) foreach (var m in list) Proto(m);
                foreach (var c in rc.AllConstructors) Proto(c);
                Proto(rc.Destructor);
                foreach (var (_, init) in rc.Fields) Proto(init);
                foreach (var (_, init) in rc.StaticFields) Proto(init);
            }
            return lines;
        }
    }
}
