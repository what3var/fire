using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using fire.Ast;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// fire plugins in the VM (ROADMAP 9 phase D b, decision 9.8: the VM loads bytecode; docs/NATIVE_BACKEND.md "Plugins"). `Library.Load` of a `.fireplugin` file (or of
    /// the native library next to which it lies) reads the plugin's bytecode; `__LibCall(5, handle)` links its classes into the running program: the interfaces of the
    /// modules it was compiled against must be the program's (ROADMAP 9.6, the hashes only - <see cref="ModuleInterface"/>), the native functions its code calls are
    /// renumbered by name, its base classes are the program's, its static initialisers run. The errors are those of the native runtime (fire_rt.h fireRegisterPlugin).
    /// </summary>
    public sealed partial class VM
    {
        /// <summary>The linked program a set of classes belongs to (the sessions register it): the interfaces of its modules for the check of a plugin.</summary>
        private static readonly ConditionalWeakTable<object, LinkedProgram> ProgramsOfClasses = new();

        /// <summary>The sessions: the linked program of a VM's classes (its modules are the ones plugins are checked against).</summary>
        public static void RegisterProgram(LinkedProgram program) => ProgramsOfClasses.AddOrUpdate(program.Program.Classes, program);

        /// <summary>The plugins this process read, by their handles (below every native handle: negative).</summary>
        private static readonly ConcurrentDictionary<long, (string Path, PluginBytecode? Plugin, string? Error)> PluginFiles = new();
        private static long s_nextPluginHandle = -1000;
        /// <summary>The plugins linked into a set of classes (a program): by their name.</summary>
        private static readonly ConditionalWeakTable<object, Dictionary<string, PluginBytecode>> LinkedPlugins = new();

        /// <summary>The bytecode of a fire plugin that `path` names (the `.fireplugin` file, or a native library with one next to it); null: none.</summary>
        private static string? PluginFileFor(string path)
        {
            try
            {
                if (path.EndsWith(".fireplugin", StringComparison.OrdinalIgnoreCase)) return File.Exists(path) ? path : null;
                foreach (var ext in new[] { ".so", ".dll", ".dylib" })
                    if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && Path.ChangeExtension(path, ".fireplugin") is var bc && File.Exists(bc)) return bc;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException) { }
            return null;
        }

        /// <summary>`Library.Load` of a fire plugin: its handle (0: no plugin there).</summary>
        private static long LoadPluginFile(string path)
        {
            if (PluginFileFor(path) is not { } file) return 0;
            PluginBytecode? plugin = null;
            string? error = null;
            try { plugin = PluginBytecode.Read(file); if (plugin == null) error = "The file '" + path + "' is not a fire plugin."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MemoryPack.MemoryPackSerializationException or System.Text.Json.JsonException or ArgumentException)
            {
                error = $"The plugin '{path}' cannot be read: {ex.Message}";
            }
            long handle = System.Threading.Interlocked.Decrement(ref s_nextPluginHandle);
            PluginFiles[handle] = (path, plugin, error);
            return handle;
        }

        private static Value Names(IReadOnlyList<string> names)
        {
            var arr = new ScriptArray(names.Count);
            for (int i = 0; i < names.Count; i++) arr.Items[i] = Value.MakeString(names[i]);
            return Value.MakeArray(arr);
        }

        /// <summary>`__LibCall(5, handle)`: the classes of the fire plugin loaded with this handle, linked into the program (an array of their names); undefined: a C library;
        /// a string: why it does not fit.</summary>
        private Value LibraryPlugin(long handle)
        {
            if (!PluginFiles.TryGetValue(handle, out var entry)) return Value.MakeUndefined();
            if (entry.Plugin is not { } plugin) return Value.MakeString(entry.Error ?? "The library is no fire plugin.");
            string? why;
            lock (LinkedPlugins)
                why = LinkPlugin(plugin);
            return why != null ? Value.MakeString($"The plugin '{plugin.Name}' cannot be loaded: {why}") : Names(plugin.Classes.Where(c => !c.StartsWith("__", StringComparison.Ordinal)).ToList());
        }

        /// <summary>Links the classes of a plugin into the classes of this program; null: done (or done before), else why not.</summary>
        private string? LinkPlugin(PluginBytecode plugin)
        {
            var linked = LinkedPlugins.GetOrCreateValue(_classes);
            if (linked.TryGetValue(plugin.Name, out var known)) return ReferenceEquals(known, plugin) || known.Classes.SequenceEqual(plugin.Classes) ? null : "another plugin of that name is loaded already.";
            if (plugin.Format != ModuleInterface.Format) return "it is a fire plugin of another version of fire - build it again.";
            if (_classes is not Dictionary<string, RuntimeClass> classes) return "this program cannot load fire plugins.";
            var program = plugin.Program!;
            // the interfaces of the modules it was compiled against: the program's (a program that does not carry its modules - a packed one - is not checked)
            if (ProgramsOfClasses.TryGetValue(_classes, out var host) && ModuleInterface.Modules(host) is { } modules)
                foreach (var dep in plugin.Deps)
                {
                    if (!modules.TryGetValue(dep[0], out var module)) return $"it needs the module '{dep[0]}', which this program does not have.";
                    string has = ModuleInterface.Text(host, module.Classes, module.Sources);
                    if (has != dep[1]) return $"it was built against another interface of the module '{dep[0]}' ({Short(dep[1])}, the program has {Short(has)}) - build it again against the libraries of this program.";
                }
            foreach (var name in plugin.Classes)
                if (classes.ContainsKey(name)) return $"the program has a class '{name}' already.";
            var own = plugin.Classes.Select(n => program.Program.Classes[n]).ToList();
            // its base classes are the program's (the copies it was compiled with have the same interface)
            foreach (var rc in own)
                if (rc.Base is { } b && !plugin.Classes.Contains(b.Name))
                {
                    if (!classes.TryGetValue(b.Name, out var hostBase)) return $"the program has no class '{b.Name}'.";
                    rc.Base = hostBase;
                }
            // the natives its code calls: the program's numbers (by name)
            var seen = new HashSet<Chunk>(ReferenceEqualityComparer.Instance);
            string? problem = null;
            void Visit(FunctionProto? proto)
            {
                if (proto == null || problem != null || !seen.Add(proto.Chunk)) return;
                problem = Renumber(proto.Chunk, plugin.Natives);
                foreach (var d in proto.ParamDefaults) Visit(d);
                foreach (var f in proto.Chunk.Functions) Visit(f);
            }
            foreach (var rc in own)
            {
                foreach (var (_, init) in rc.Fields) Visit(init);
                foreach (var (_, init) in rc.StaticFields) Visit(init);
                foreach (var overloads in rc.Methods.Values) foreach (var m in overloads) Visit(m);
                foreach (var ctor in rc.AllConstructors) Visit(ctor);
                Visit(rc.Destructor);
            }
            if (problem != null) return problem;
            foreach (var rc in own) classes[rc.Name] = rc;
            linked[plugin.Name] = plugin;
            // its units: a unit of the VM carries its dimensions by name (and its conversion functions by the name of its hidden class, linked above) - only what a text
            // may name (`"5in".ToFloat()`) needs the program to know them
            if (host != null && program.Program.CustomUnits.Count > 0) Unit.AddCustomUnits(host.Program.CustomUnits, program.Program.CustomUnits);
            // the static initialisers of its classes, in the order of their declarations (as the top level of a program runs them first); what they make belongs to the
            // global scope (like the values of the top level)
            foreach (var rc in own)
                foreach (var (field, init) in rc.StaticFields)
                {
                    if (CallInline(new LambdaValue(init, null), Array.Empty<Value>(), out var value) is { } error) return $"the initialiser of '{rc.Name}.{field}' threw {error.ClassName}.";
                    if (value.Kind == ValueKind.Class && value.AsObjectRef() is ObjectInstance obj) obj.TakeGlobal(_globalScope);
                    else if (LeafOf(value) is { } leaf) LeafOwnership.Reparent(leaf, _globalScope);
                    rc.StaticFieldValues[field] = value;
                }
            return null;
        }

        private static string Short(string abi) => abi.Length > 14 ? abi.Substring(0, 14) : abi;

        /// <summary>The native calls of a chunk of a plugin: the program's numbers for the plugin's (by name); what a plugin cannot do in the VM yet.</summary>
        private string? Renumber(Chunk chunk, IReadOnlyList<string> natives)
        {
            string? problem = null;
            var code = chunk.Code;
            OperandSizes.Walk(code, (at, op, operands) =>
            {
                if (problem != null) return;
                switch (op)
                {
                    case OpCode.CallNative:
                    {
                        string? name = operands[0] < natives.Count ? natives[operands[0]] : null;
                        int index = name != null && _natives.Has(name) ? _natives.IndexOf(name) : -1;
                        if (index < 0) { problem = $"it calls the native function '{name ?? operands[0].ToString()}', which this program does not have (an import the program lacks)."; return; }
                        code[at + 1] = (byte)(index & 0xFF);
                        code[at + 2] = (byte)(index >> 8);
                        break;
                    }
                    case OpCode.CallTryableNative or OpCode.CallExtern:
                        problem = "its code calls natives that a plugin cannot call in the VM yet (tryable or extern functions).";
                        break;
                    case OpCode.LoadGlobal or OpCode.StoreGlobal or OpCode.StoreGlobalPop or OpCode.AddressOfGlobal or OpCode.ArithGlobalConstPop:
                        problem = "its classes use variables of the top level of its file (a plugin has no top level).";
                        break;
                }
            });
            return problem;
        }
    }
}
