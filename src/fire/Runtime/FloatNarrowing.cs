using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// `#floatwidth 32`: rounds every float constant of a compiled program to the nearest 32-bit float, so that the program
    /// starts from the same values a float32 CPU (or a native build with `float`) has. The arithmetic itself is rounded by
    /// <see cref="Value.SingleFloats"/> at run time.
    /// </summary>
    public static class FloatNarrowing
    {
        public static void Apply(CompiledProgram program)
        {
            var seen = new HashSet<Chunk>(ReferenceEqualityComparer.Instance);
            void Visit(FunctionProto? proto)
            {
                if (proto == null || !seen.Add(proto.Chunk)) return;
                VisitChunk(proto.Chunk);
                foreach (var d in proto.ParamDefaults) Visit(d);
            }
            void VisitChunk(Chunk chunk)
            {
                for (int i = 0; i < chunk.Constants.Count; i++)
                {
                    var c = chunk.Constants[i];
                    if (c.Kind == ValueKind.Float)
                        chunk.ReplaceConstant(i, Value.MakeFloat((double)(float)c.AsFloat(), c.Unit, c.Width));
                }
                foreach (var f in chunk.Functions) Visit(f);
            }

            if (seen.Add(program.TopLevel)) VisitChunk(program.TopLevel);
            foreach (var rc in program.Classes.Values)
            {
                foreach (var (_, init) in rc.Fields) Visit(init);
                foreach (var (_, init) in rc.StaticFields) Visit(init);
                foreach (var overloads in rc.Methods.Values) foreach (var m in overloads) Visit(m);
                foreach (var ctor in rc.Constructors.Values) Visit(ctor);
                Visit(rc.Destructor);
            }
        }
    }
}
