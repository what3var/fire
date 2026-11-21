using System.Collections.Generic;

namespace fire.Bytecode
{
    /// <summary>
    /// The name of a function in a stack trace (docs/spec/STACKTRACE.md section 2), by its chunk - the same rules in the VM in C# (here), the VM in C
    /// (native/vm vm_load.c `vmTraceNames`) and the native build: the top level `&lt;main&gt;`; a method `Class.Name` (a property's accessor by its method,
    /// `Class.get_X`), a constructor `Class.construct`, a destructor `Class.destruct`, the initializer of a field or the default value of a parameter
    /// `Class.&lt;init&gt;`, each specialization of a generic method under the name of the method; a generic class without its type parameters (`List.Add`).
    /// Every other function is a lambda (`&lt;lambda&gt;`); the body of a thread (`fire { }`) is `&lt;fire&gt;` - the engine that starts the thread knows it.
    /// The classes are visited in the order of the program's table, the first name a chunk gets is its name.
    /// </summary>
    public static class TraceNames
    {
        public const string Main = "<main>", Lambda = "<lambda>", Fire = "<fire>";

        public static Dictionary<Chunk, string> Build(Chunk topLevel, IEnumerable<RuntimeClass> classes)
        {
            var names = new Dictionary<Chunk, string>(ReferenceEqualityComparer.Instance);
            names.TryAdd(topLevel, Main);
            foreach (var rc in classes)
            {
                string cls = ClassDisplay(rc.Name);
                foreach (var (name, protos) in rc.Methods)
                    foreach (var p in protos) Add(names, p, cls + "." + name);
                foreach (var p in rc.Constructors.Values) Add(names, p, cls + ".construct");
                foreach (var p in rc.ExtraConstructors) Add(names, p, cls + ".construct");
                if (rc.Destructor != null) Add(names, rc.Destructor, cls + ".destruct");
            }
            // (the initializers after every member: a member that is also a default value keeps its own name)
            foreach (var rc in classes)
            {
                string init = ClassDisplay(rc.Name) + ".<init>";
                foreach (var (_, p) in rc.Fields) if (p != null) Add(names, p, init);
                foreach (var (_, p) in rc.StaticFields) if (p != null) Add(names, p, init);
                foreach (var (_, protos) in rc.Methods) foreach (var p in protos) Defaults(names, p, init);
                foreach (var p in rc.Constructors.Values) Defaults(names, p, init);
                foreach (var p in rc.ExtraConstructors) Defaults(names, p, init);
            }
            return names;
        }

        private static void Add(Dictionary<Chunk, string> names, FunctionProto p, string name)
        {
            names.TryAdd(p.Chunk, name);
            if (p.Specializations != null) foreach (var s in p.Specializations) if (s != null) names.TryAdd(s.Chunk, name);
        }

        private static void Defaults(Dictionary<Chunk, string> names, FunctionProto p, string init)
        {
            foreach (var d in p.ParamDefaults) if (d != null) names.TryAdd(d.Chunk, init);
        }

        /// <summary>A class as a trace shows it: without the arity of a generic class (`List`1` is `List`) or type arguments.</summary>
        public static string ClassDisplay(string name)
        {
            int cut = name.IndexOfAny(new[] { '`', '<' });
            return cut > 0 ? name.Substring(0, cut) : name;
        }
    }
}
