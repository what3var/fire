using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using fire.Ast;
using fire.Bytecode;
using fire.Standard;
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
        /// <summary>The interfaces of the modules of a program (a set of classes) as it was built, by module.</summary>
        private static readonly ConditionalWeakTable<object, Dictionary<string, string>> HostInterfaces = new();

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
                    // (as the program was built: what plugins added to its classes - `class extends` - is not part of the interface; computed before the first one did)
                    var built = HostInterfaces.GetOrCreateValue(_classes);
                    if (!built.TryGetValue(dep[0], out var has)) built[dep[0]] = has = ModuleInterface.Text(host, module.Classes, module.Sources);
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
            // its natives in C++ the program does not have: from the libraries next to its bytecode (C ABI, like those of a package)
            if (plugin.PackageNatives.Count > 0)
                PackageNativeBinding.Register(_natives, plugin.PackageNatives, plugin.PackageLibraries,
                    file => Path.Combine(plugin.Directory, file) is var path && File.Exists(path) ? path : null);
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
            // its `class extends` of classes of the program, of libraries and of base types: the methods its own sources add (no fields - the build refuses them)
            var extensions = new List<(RuntimeClass Target, RuntimeClass Copy, List<(string Name, FunctionProto Proto)> Added)>();
            foreach (var group in plugin.Extensions.Where(e => e.Length == 3).GroupBy(e => e[0]).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (plugin.Classes.Contains(group.Key) || !program.Program.Classes.TryGetValue(group.Key, out var rc)) continue;   // (`$int`: never in Classes)
                var added = new List<(string, FunctionProto)>();
                foreach (var e in group)
                    if (rc.Methods.TryGetValue(e[1], out var overloads) && overloads.FirstOrDefault(p => p.ParamCount.ToString(System.Globalization.CultureInfo.InvariantCulture) == e[2]) is { } proto)
                        added.Add((e[1], proto));
                if (added.Count == 0) continue;
                classes.TryGetValue(rc.Name, out var target);
                foreach (var (name, proto) in added)
                    if (target != null && target.Methods.TryGetValue(name, out var has) && has.Any(h => h.ParamCount == proto.ParamCount))
                        return $"it adds the method '{name}' ({proto.ParamCount} arguments) to '{(BaseTypeExtensions.TryGetTypeName(rc.Name, out var bt) ? bt : rc.Name)}', which has it already (another plugin added it, or the class of the program has it).";
                // (a base type the program does not extend at all: the plugin's collecting class itself - all of its methods)
                foreach (var (_, proto) in target == null ? rc.Methods.SelectMany(m => m.Value.Select(p => (m.Key, p))).ToList() : added) Visit(proto);
                extensions.Add((target!, rc, added));
            }
            if (problem != null) return problem;
            LinkPluginSources(plugin, host);
            foreach (var rc in own) classes[rc.Name] = rc;
            foreach (var (target, copy, added) in extensions)
            {
                if (target == null)
                {
                    classes[copy.Name] = copy;
                    foreach (var kind in Enum.GetValues<ValueKind>())
                        if (BaseTypeExtensions.ClassNameFor(kind) == copy.Name) _baseTypeClasses[(int)kind] = copy;
                    continue;
                }
                foreach (var (name, proto) in added)
                {
                    if (!target.Methods.TryGetValue(name, out var list)) target.Methods[name] = list = new List<FunctionProto>();
                    list.Add(proto);
                }
            }
            if (extensions.Count > 0) foreach (var rc in classes.Values) rc.ForgetFoundMethods();
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

        /// <summary>A plugin's sources got the next source indexes of the program (its lines are told apart from the program's): the classes of the program, the first new
        /// index, the files and the hashes of the plugin's sources. The debugger (VmDebugTarget) grows its list of sources; raised on the thread that loads the plugin.</summary>
        public static event Action<object, int, IReadOnlyList<string?>, IReadOnlyList<string?>>? PluginSourcesLinked;

        /// <summary>The sources the plugins added to a program (a set of classes), after the program's own.</summary>
        private static readonly ConditionalWeakTable<object, StrongBox<int>> PluginSourceCounts = new();

        /// <summary>The line tables of a plugin's code: its own sources become the next indexes after the program's (and the plugins' before), the sources of its
        /// libraries 0 (their code is the program's); a program that does not know its sources (a packed one) keeps the plugin's numbers.</summary>
        private void LinkPluginSources(PluginBytecode plugin, LinkedProgram? host)
        {
            var program = plugin.Program!;
            var own = plugin.Sources.Where(s => s.Length == 3 && int.TryParse(s[0], out _)).ToList();
            if (host?.SourceFiles is not { } hostFiles || own.Count == 0) return;
            var added = PluginSourceCounts.GetOrCreateValue(_classes);
            int first = hostFiles.Count + added.Value;
            added.Value += own.Count;
            var map = new Dictionary<int, int>();
            for (int k = 0; k < own.Count; k++) map[int.Parse(own[k][0]!, System.Globalization.CultureInfo.InvariantCulture)] = first + k;
            foreach (var chunk in PluginBytecode.Chunks(program.Program)) chunk.RemapSources(s => map.TryGetValue(s, out var to) ? to : 0);
            PluginSourcesLinked?.Invoke(_classes, first, own.Select(s => s[1]).ToList(), own.Select(s => s[2]).ToList());
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
