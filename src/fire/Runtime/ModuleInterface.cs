using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using fire.Bytecode;
using MemoryPack;

namespace fire.Runtime
{
    /// <summary>
    /// The interface of a library module (ROADMAP 9.7, docs/spec/NATIVE_BACKEND.md "Cache und Schnittstelle"): the hash of what other modules see of it - its classes with their
    /// bases, interfaces, fields (layout, declared kinds, units, types, access) and members (parameters, types, access, defaults). A module that only changes the code of its
    /// methods keeps it. The native backend names its libraries by it; a fire plugin is checked against the program by it (ROADMAP 9.6), natively and in the VM.
    /// </summary>
    public static class ModuleInterface
    {
        /// <summary>The format of the interface (a changed format is another hash).</summary>
        public const int Format = 1;

        /// <summary>The hash of the module made of <paramref name="classes"/> (sorted by name) whose code is in <paramref name="sources"/>.</summary>
        public static string Hash(LinkedProgram program, IEnumerable<string> classes, ICollection<int> sources)
        {
            var sb = new StringBuilder();
            sb.Append("fire-abi ").Append(Format).Append('\n');
            foreach (var name in classes)
            {
                var rc = program.Program.Classes[name];
                sb.Append("class ").Append(rc.Name).Append(" : ").Append(rc.Base?.Name).Append(" [").Append(string.Join(",", rc.Interfaces)).Append("] <")
                    .Append(string.Join(",", rc.BaseTypeArgs ?? Array.Empty<string>())).Append('>').Append(rc.IsActor ? " actor" : "").Append(rc.NotSerializable ? " ns" : "").Append(rc.NoCopy ? " nc" : "").Append('\n');
                sb.Append(" layout ").Append(string.Join(",", rc.FlattenedFieldNames)).Append('\n');
                foreach (var (field, info) in rc.OwnFieldInfo.OrderBy(f => f.Key, StringComparer.Ordinal))
                    sb.Append(" field ").Append(field).Append(' ').Append(info.IsStatic).Append(' ').Append(info.AccessModifier).Append(' ').Append(info.RequiredKind).Append(' ')
                        .Append(info.RequiredUnit).Append(' ').Append(info.RequiredType).Append(' ').Append(info.TypeText).Append('\n');
                void Proto(string what, FunctionProto p) =>
                    sb.Append(' ').Append(what).Append(" (").Append(p.ParamCount).Append(") ").Append(p.Access).Append(p.IsStatic ? " static" : "").Append(" t").Append(p.TypeParamCount)
                        .Append(" r").Append(p.RefMask).Append(" [").Append(string.Join(",", (p.ParamTypes ?? Array.Empty<string?>()).Select(t => t ?? "?"))).Append("] d")
                        .Append(string.Concat(p.ParamDefaults.Select(d => d == null ? '0' : '1'))).Append('\n');
                foreach (var (method, protos) in rc.Methods.OrderBy(m => m.Key, StringComparer.Ordinal))
                    foreach (var p in protos.Where(p => p.Chunk.FirstSourceIndex is not int src || sources.Contains(src))) Proto("method " + method, p);
                foreach (var (argc, p) in rc.Constructors.OrderBy(c => c.Key)) Proto("ctor" + argc, p);
                foreach (var p in rc.ExtraConstructors) Proto("xctor", p);
                if (rc.Destructor != null) sb.Append(" dtor\n");
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        }

        /// <summary>The library modules of a linked program (null: it does not carry the modules of its sources - a packed program): name -> its classes and sources.</summary>
        public static Dictionary<string, (List<string> Classes, HashSet<int> Sources)>? Modules(LinkedProgram program)
        {
            if (program.SourceModules is not { } modules) return null;
            var result = new Dictionary<string, (List<string> Classes, HashSet<int> Sources)>(StringComparer.Ordinal);
            for (int i = 0; i < modules.Count; i++)
                if (modules[i] is { } m)
                {
                    if (!result.TryGetValue(m, out var entry)) result[m] = entry = (new List<string>(), new HashSet<int>());
                    entry.Sources.Add(i);
                }
            foreach (var rc in program.Program.Classes.Values)
                if (rc.Decl != null && rc.Decl.Source < modules.Count && modules[rc.Decl.Source] is { } m) result[m].Classes.Add(rc.Name);
            foreach (var entry in result.Values) entry.Classes.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>The interface of a module as plugins name it ("format:hash").</summary>
        public static string Text(LinkedProgram program, List<string> classes, HashSet<int> sources) => $"{Format}:{Hash(program, classes, sources)}";
    }

    /// <summary>The bytecode of a fire plugin for the VM (`<name>.fireplugin`, next to its native library; ROADMAP 9.8): a header (its module, the interfaces of the modules
    /// it needs) and the linked program it was compiled as (MemoryPack). The VM links its classes into the running program (VM.Plugins.cs).</summary>
    public sealed class PluginBytecode
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FIREPLG1");

        public string Name { get; set; } = "";
        public int Format { get; set; } = ModuleInterface.Format;
        public List<string[]> Deps { get; set; } = new();
        public List<string> Classes { get; set; } = new();
        /// <summary>The native functions of its code by their numbers (CallNative): the VM renumbers them by name.</summary>
        public List<string> Natives { get; set; } = new();
        /// <summary>Its natives in C++ (a package or the project of the plugin) and the file names of the libraries for the VM next to it that hold them (C ABI,
        /// PackageNativeBinding): the VM registers those the program does not have.</summary>
        public List<string> PackageNatives { get; set; } = new();
        public List<string> PackageLibraries { get; set; } = new();
        /// <summary>The methods its `class extends` adds to classes it does not declare (of the program, of libraries, base types `$string`, ...): class, method,
        /// number of parameters (properties: `get_x`/`set_x`).</summary>
        public List<string[]> Extensions { get; set; } = new();
        /// <summary>Its own sources (the debugger: the VM gives them the next source indexes of the program): index in its bytecode, file, hash (null: none).</summary>
        public List<string?[]> Sources { get; set; } = new();
        [System.Text.Json.Serialization.JsonIgnore]
        public LinkedProgram? Program { get; set; }
        /// <summary>The folder of its file (the libraries of its natives lie there).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Directory { get; set; } = "";

        public static void Write(string path, string name, LinkedProgram program, IEnumerable<(string Module, string Abi)> deps, IEnumerable<(string Native, string Library)>? packageNatives = null)
        {
            var modules = program.SourceModules;
            var header = new PluginBytecode { Name = name, Deps = deps.Select(d => new[] { d.Module, d.Abi }).ToList(), Natives = program.NativeNames?.ToList() ?? new List<string>() };
            foreach (var (native, library) in packageNatives ?? Enumerable.Empty<(string, string)>()) { header.PackageNatives.Add(native); header.PackageLibraries.Add(library); }
            if (modules != null)
                foreach (var rc in program.Program.Classes.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
                    if (rc.Decl != null && rc.Decl.Source < modules.Count && (modules[rc.Decl.Source] != null || rc.Name.StartsWith('$')))
                        foreach (var member in rc.Decl.Members)
                        {
                            if (member.Source >= modules.Count || modules[member.Source] != null) continue;
                            if (member is Ast.MethodDecl m) header.Extensions.Add(new[] { rc.Name, m.Name, m.Params.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                            else if (member is Ast.PropertyDecl prop)
                            {
                                if (prop.Getter != null) header.Extensions.Add(new[] { rc.Name, "get_" + prop.Name, "0" });
                                if (prop.Setter != null) header.Extensions.Add(new[] { rc.Name, "set_" + prop.Name, "1" });
                            }
                        }
            foreach (var rc in program.Program.Classes.Values)
                if (rc.Decl != null && modules != null && rc.Decl.Source < modules.Count && modules[rc.Decl.Source] == null && !rc.Name.StartsWith('$')) header.Classes.Add(rc.Name);
            header.Classes.Sort(StringComparer.Ordinal);
            if (modules != null)
                for (int i = 1; i < modules.Count; i++)
                    if (modules[i] == null)
                        header.Sources.Add(new[] { i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            program.SourceFiles is { } f && i < f.Count ? f[i] : null, program.SourceHashes is { } h && i < h.Count ? h[i] : null });
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(header);
            // (the line tables go along: the debugger shows the plugin's lines - Chunk.KeepLineTable)
            foreach (var chunk in Chunks(program.Program)) chunk.KeepLineTable();
            byte[] body = MemoryPackSerializer.Serialize(program);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write(Magic);
            fs.Write(BitConverter.GetBytes(json.Length));
            fs.Write(json);
            fs.Write(body);
        }

        /// <summary>The plugin of a file (null: not a fire plugin).</summary>
        public static PluginBytecode? Read(string path)
        {
            byte[] all = File.ReadAllBytes(path);
            if (all.Length < Magic.Length + 4 || !all.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
            int length = BitConverter.ToInt32(all, Magic.Length);
            var header = JsonSerializer.Deserialize<PluginBytecode>(all.AsSpan(Magic.Length + 4, length));
            if (header == null) return null;
            var program = MemoryPackSerializer.Deserialize<LinkedProgram>(all.AsSpan(Magic.Length + 4 + length));
            if (program == null) return null;
            program.Program.RelinkAfterDeserialize();
            foreach (var chunk in Chunks(program.Program)) chunk.RestoreLineTable();
            header.Program = program;
            header.Directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            return header;
        }

        /// <summary>The chunks of a program: its top level, the members of its classes, the functions inside them (each once).</summary>
        public static List<Chunk> Chunks(CompiledProgram program)
        {
            var seen = new HashSet<Chunk>(ReferenceEqualityComparer.Instance);
            var list = new List<Chunk>();
            void VisitChunk(Chunk chunk)
            {
                if (!seen.Add(chunk)) return;
                list.Add(chunk);
                foreach (var f in chunk.Functions) Visit(f);
            }
            void Visit(FunctionProto? proto)
            {
                if (proto == null) return;
                VisitChunk(proto.Chunk);
                foreach (var d in proto.ParamDefaults) Visit(d);
            }
            VisitChunk(program.TopLevel);
            foreach (var rc in program.Classes.Values)
            {
                foreach (var (_, init) in rc.Fields) Visit(init);
                foreach (var (_, init) in rc.StaticFields) Visit(init);
                foreach (var overloads in rc.Methods.Values) foreach (var m in overloads) Visit(m);
                foreach (var ctor in rc.AllConstructors) Visit(ctor);
                Visit(rc.Destructor);
            }
            return list;
        }
    }
}
