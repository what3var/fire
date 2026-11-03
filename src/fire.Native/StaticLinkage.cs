using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using fire.Bytecode;
using fire.Runtime;
using fire.Values;
using MemoryPack;
using fire.Package.Manager;

namespace fire.Native
{
    /// <summary>A library module of a build with linkage "static": its static library in the user-wide cache (<see cref="NativeLibraryCache"/>), the C file it is built from
    /// (written when the library is not in the cache yet) and its interface file.</summary>
    public sealed record StaticLibrary(string Module, string Key, string Directory, string CFile, string LibraryName, string InterfaceFile, bool Cached)
    {
        public string LibraryFile(string extension) => Path.Combine(Directory, LibraryName + extension);
    }

    /// <summary>The program of a build with linkage "static": its own C (it defines the runtime's state, the helpers and the tables every module needs), the natives in C++
    /// and the libraries, in dependency order (`core` first).</summary>
    public sealed record StaticBuildPlan(string ProgramC, string? Natives, IReadOnlyList<StaticLibrary> Libraries, string Log);

    public sealed partial class CppGenerator
    {
        /// <summary>The C files of the library modules (linkage "static"): every module its own translation unit.</summary>
        private bool _separate;
        /// <summary>The program, linkage "static": the head of a library module's file (the program's configuration of the runtime, without its state).</summary>
        private string? _libraryHead;
        /// <summary>Units of at most this many dimensions with linkage "static" (the size of the unit tables is the same in every module).</summary>
        private const int SeparateDims = 16;

        /// <summary>The format of the interface files (ROADMAP 9.7): a changed format is a different key.</summary>
        public const int InterfaceFormat = 1;

        /// <summary>The library module whose import the natives in C belong to (linkage "static": they live in its file, ROADMAP 9 B2) - null: the program has them (no module
        /// of that name, or one file).</summary>
        /// (Only natives that stand alone: the C of an import that uses no other import's natives in C and whose natives no other import's C uses - the bridge of windows
        /// uses the framebuffers of graphics, scene3d those too: such natives stay together in the program's file.)
        private ModuleSpec? NativesOwner(string import)
        {
            if (!_separate) return null;
            var module = _moduleOfSource.Values.Distinct().FirstOrDefault(m => string.Equals(m.Name, import, StringComparison.OrdinalIgnoreCase));
            return module != null && NativesStandAlone(_program, module.Name, _target) ? module : null;
        }

        /// <summary>Is the import one with natives in C whose C neither uses nor is used by the C of another import of the program (it can live in the file of its module)?
        /// Its C uses another's when it names a function of it (`fire_gfx_` of graphics in the bridge of windows). The same answer for every module (part of the key).</summary>
        internal static bool NativesStandAlone(LinkedProgram program, string import, TargetProfile target)
        {
            var imports = NativeCImports(program);
            if (!imports.Contains(import, StringComparer.OrdinalIgnoreCase)) return false;
            string own = NativeCText(import, target), ownPrefix = NativePrefix(import);
            foreach (var other in imports.Where(i => !string.Equals(i, import, StringComparison.OrdinalIgnoreCase)))
            {
                string prefix = NativePrefix(other);
                if (prefix.Length > 0 && own.Contains(prefix, StringComparison.Ordinal)) return false;
                if (ownPrefix.Length > 0 && NativeCText(other, target).Contains(ownPrefix, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>The imports of the program with natives in C: graphics and windows (their modules), the packages in C.</summary>
        private static List<string> NativeCImports(LinkedProgram program)
        {
            var result = new List<string>();
            var modules = program.SourceModules?.Where(m => m != null).Select(m => m!).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            foreach (var standard in new[] { NativeImports.Graphics, NativeImports.Windows })
                if (modules.Contains(standard, StringComparer.OrdinalIgnoreCase) || program.NativeImports.Contains(standard)) result.Add(standard);
            foreach (var key in program.NativeImports.Where(k => k.StartsWith(PackageStore.KeyPrefix, StringComparison.Ordinal)))
                if (PackageStore.Default.FindKey(key)?.Import.Native is { IsC: true }) result.Add(key);
            return result;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), string> NativeCTexts = new();

        /// <summary>The C of the natives of an import (empty: none).</summary>
        private static string NativeCText(string import, TargetProfile target) => NativeCTexts.GetOrAdd((import.ToLowerInvariant(), target.Name), _ =>
        {
            InstalledImport? found = import.StartsWith(PackageStore.KeyPrefix, StringComparison.Ordinal) ? PackageStore.Default.FindKey(import)
                : PackageStore.Default.Installed().SelectMany(p => p.Manifest.Imports.Select(i => new InstalledImport(p, i)))
                    .FirstOrDefault(i => string.Equals(i.Name, import, StringComparison.OrdinalIgnoreCase) && i.Import.Native != null);
            if (found?.Import.Native == null) return "";
            try { return string.Concat(found.ReadNativeSources(new[] { target.Native.Platform, target.Name }).Select(s => s.Text + "\n")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
        });

        /// <summary>The prefix of the C functions of an import's natives (`fire_hash_`, `fire_gfx_`, `fire_win_`).</summary>
        private static string NativePrefix(string import)
        {
            if (string.Equals(import, NativeImports.Graphics, StringComparison.OrdinalIgnoreCase)) return "fire_gfx_";
            if (string.Equals(import, NativeImports.Windows, StringComparison.OrdinalIgnoreCase)) return "fire_win_";
            var symbol = PackageStore.Default.FindKey(import)?.Import.Native?.Functions.Select(f => f.Symbol).FirstOrDefault(s => s.StartsWith("fire_", StringComparison.Ordinal));
            if (symbol == null) return "";
            int second = symbol.IndexOf('_', 5);
            return second > 5 ? symbol.Substring(0, second + 1) : "";
        }

        /// <summary>The natives in C of an import with their signatures (the C function, arguments, the scope list): those of a package from its manifest, of graphics
        /// and windows from the generator's tables.</summary>
        private IEnumerable<(string Function, int Argc, bool NeedsList)> NativeFunctionsOf(string import)
        {
            if (string.Equals(import, NativeImports.Graphics, StringComparison.OrdinalIgnoreCase))
                return GraphicsBridgeNatives.Select(n => ($"fire_gfx_{n.Key}", n.Value.Argc, GraphicsNeedsList.Contains(n.Key)));
            if (string.Equals(import, NativeImports.Windows, StringComparison.OrdinalIgnoreCase))
                return WindowsBridgeNatives.Select(n => ($"fire_win_Win{n.Key}", n.Value.Argc, n.Value.Reference));
            var package = _packageImports.FirstOrDefault(p => string.Equals(p.Key, import, StringComparison.Ordinal)).Import;
            return package?.Import.Native is { IsC: true } native ? native.Functions.Select(fn => (fn.Symbol, fn.Arguments, fn.NeedsList)) : Enumerable.Empty<(string, int, bool)>();
        }

        /// <summary>A library module, linkage "static": the natives in C of its own import (graphics, windows, a package in C) - they live in its file (their state once),
        /// every other module and the program call them through its `fire_cn_` functions - and the declarations of those of other modules it calls.</summary>
        private string LibraryNativesC()
        {
            var sb = new StringBuilder();
            foreach (var (function, (argc, needsList, _)) in _sharedNatives.OrderBy(n => n.Key, StringComparer.Ordinal))
                sb.AppendLine($"Value fire_cn_{function}({ThunkParams(argc, needsList)});");
            string own = _self!.Name;
            if (!ReferenceEquals(NativesOwner(own), _self)) return sb.ToString();
            bool standard = string.Equals(own, NativeImports.Graphics, StringComparison.OrdinalIgnoreCase) || string.Equals(own, NativeImports.Windows, StringComparison.OrdinalIgnoreCase);
            var package = _packageImports.FirstOrDefault(p => string.Equals(p.Key, own, StringComparison.Ordinal)).Import;
            if (standard) AppendStandardNatives(sb, own.ToLowerInvariant());
            else if (package?.Import.Native is { IsC: true })
                foreach (var (name, text) in package.ReadNativeSources(new[] { _target.Native.Platform, _target.Name }))
                {
                    sb.AppendLine($"// ---- package {package.Package.Name} {package.Package.Version}, import \"{package.Name}\" (C): {name}");
                    sb.AppendLine(text);
                    sb.AppendLine($"// ---- end of {name}");
                }
            else return sb.ToString();
            foreach (var (function, argc, needsList) in NativeFunctionsOf(own))
            {
                string args = string.Join(", ", Enumerable.Range(0, argc).Select(i => $"a{i}").Concat(needsList ? new[] { "list" } : Array.Empty<string>()));
                sb.AppendLine($"Value fire_cn_{function}({ThunkParams(argc, needsList)}) {{ return {function}({args}); }}");
            }
            return sb.ToString();
        }

        /// <summary>Linkage "static": the natives in C of other files this generator calls (function -> arguments, scope list, import) - the `fire_cn_` functions of their
        /// module (or of the program).</summary>
        private readonly SortedDictionary<string, (int Argc, bool NeedsList, string Import)> _sharedNatives = new(StringComparer.Ordinal);

        /// <summary>The C function a call of a native in C goes to: itself when it is part of this file, otherwise (linkage "static") the `fire_cn_` function of the module
        /// whose import it belongs to, or of the program.</summary>
        private string SharedNative(string function, int argc, bool needsList, string import)
        {
            if (!_separate) return function;
            var owner = NativesOwner(import);
            if (owner == null ? !LibraryRole : ReferenceEquals(owner, _self)) return function;
            _sharedNatives[function] = (argc, needsList, import);
            return "fire_cn_" + function;
        }

        /// <summary>The program, linkage "static": the `fire_cn_` functions of its natives in C that the library modules call, and the declarations of those of the
        /// modules it calls.</summary>
        private string SharedNativeThunks()
        {
            if (!_separate || _libraries == null) return "";
            var all = new SortedDictionary<string, (int Argc, bool NeedsList, string Import)>(StringComparer.Ordinal);
            foreach (var lib in _libraries) foreach (var (f, sig) in lib.Needs.SharedNatives) all[f] = sig;
            var sb = new StringBuilder();
            foreach (var (function, (argc, needsList, import)) in all)
            {
                if (NativesOwner(import) != null) continue;   // (in the file of their module)
                string args = string.Join(", ", Enumerable.Range(0, argc).Select(i => $"a{i}").Concat(needsList ? new[] { "list" } : Array.Empty<string>()));
                sb.AppendLine($"Value fire_cn_{function}({ThunkParams(argc, needsList)}) {{ return {function}({args}); }}");
            }
            foreach (var (function, (argc, needsList, _)) in _sharedNatives) sb.AppendLine($"Value fire_cn_{function}({ThunkParams(argc, needsList)});");
            return sb.ToString();
        }

        /// <summary>The imports whose natives in C the code of this generator calls (a library module's file gets their C).</summary>
        private readonly HashSet<string> _nativeImportsUsed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _nativeImportOf = new(StringComparer.Ordinal);

        /// <summary>The hash of what other modules see of a module (ROADMAP 9.7): its classes with their bases, interfaces, fields (layout, declared kinds, units, types,
        /// access) and members (parameters, types, access, defaults). A module that only changes the code of its methods keeps it - the modules that need it are not built again.</summary>
        internal static string AbiHash(LinkedProgram program, ModuleSpec spec)
        {
            var sb = new StringBuilder();
            sb.Append("fire-abi ").Append(InterfaceFormat).Append('\n');
            foreach (var name in spec.Classes)
            {
                var rc = program.Program.Classes[name];
                sb.Append("class ").Append(rc.Name).Append(" : ").Append(rc.Base?.Name).Append(" [").Append(string.Join(",", rc.Interfaces)).Append("] <")
                    .Append(string.Join(",", rc.BaseTypeArgs ?? Array.Empty<string>())).Append('>').Append(rc.IsActor ? " actor" : "").Append(rc.NotSerializable ? " ns" : "").Append('\n');
                sb.Append(" layout ").Append(string.Join(",", rc.FlattenedFieldNames)).Append('\n');
                foreach (var (field, info) in rc.OwnFieldInfo.OrderBy(f => f.Key, StringComparer.Ordinal))
                    sb.Append(" field ").Append(field).Append(' ').Append(info.IsStatic).Append(' ').Append(info.AccessModifier).Append(' ').Append(info.RequiredKind).Append(' ')
                        .Append(info.RequiredUnit).Append(' ').Append(info.RequiredType).Append(' ').Append(info.TypeText).Append('\n');
                void Proto(string what, FunctionProto p) =>
                    sb.Append(' ').Append(what).Append(" (").Append(p.ParamCount).Append(") ").Append(p.Access).Append(p.IsStatic ? " static" : "").Append(" t").Append(p.TypeParamCount)
                        .Append(" r").Append(p.RefMask).Append(" [").Append(string.Join(",", (p.ParamTypes ?? Array.Empty<string?>()).Select(t => t ?? "?"))).Append("] d")
                        .Append(string.Concat(p.ParamDefaults.Select(d => d == null ? '0' : '1'))).Append('\n');
                foreach (var (method, protos) in rc.Methods.OrderBy(m => m.Key, StringComparer.Ordinal))
                    foreach (var p in protos.Where(p => p.Chunk.FirstSourceIndex is not int src || spec.Sources.Contains(src))) Proto("method " + method, p);
                foreach (var (argc, p) in rc.Constructors.OrderBy(c => c.Key)) Proto("ctor" + argc, p);
                foreach (var p in rc.ExtraConstructors) Proto("xctor", p);
                if (rc.Destructor != null) sb.Append(" dtor\n");
            }
            return Hash(sb.ToString());
        }

        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        /// <summary>The hash of the natives in C of an import (empty: none).</summary>
        private static string NativeSourcesHash(string module, TargetProfile target)
        {
            InstalledImport? import = module.StartsWith(PackageStore.KeyPrefix, StringComparison.Ordinal) ? PackageStore.Default.FindKey(module)
                : PackageStore.Default.Installed().SelectMany(p => p.Manifest.Imports.Select(i => new InstalledImport(p, i)))
                    .FirstOrDefault(i => string.Equals(i.Name, module, StringComparison.OrdinalIgnoreCase) && i.Import.Native != null);
            if (import?.Import.Native == null) return "";
            try { return Hash(string.Concat(import.ReadNativeSources(new[] { target.Native.Platform, target.Name }).Select(s => s.Name + "\n" + s.Text + "\n"))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "?"; }
        }

        /// <summary>The key of a module's library in the cache: the generator, the toolchain, the target and the mode, the module's sources, the interfaces of the modules
        /// it needs and the switches it is built with (ROADMAP 9.4: a variant per set; null - the module's own switches, only its interface is kept).</summary>
        internal static string LibraryKey(LinkedProgram program, ModuleSpec spec, IReadOnlyDictionary<ModuleSpec, string> abi, TargetProfile target, string toolchainStamp, ModuleFlags? flags, int? sdl)
        {
            var sb = new StringBuilder();
            sb.Append("fire-static ").Append(InterfaceFormat).Append(' ').Append(typeof(CppGenerator).Assembly.ManifestModule.ModuleVersionId).Append('\n');
            sb.Append("toolchain ").Append(toolchainStamp).Append('\n');
            sb.Append("target ").Append(target.Name).Append(' ').Append(JsonSerializer.Serialize(target.Native)).Append(' ').Append(target.FloatWidth).Append('\n');
            sb.Append("mode ").Append(program.ExecutionMode).Append(" float ").Append(program.FloatWidth).Append(" sdl ").Append(sdl).Append('\n');
            sb.Append("module ").Append(spec.Name).Append('\n');
            foreach (int s in spec.Sources.OrderBy(s => s)) sb.Append(" source ").Append(program.SourceTextHashes?[s]).Append('\n');
            foreach (var dep in spec.Deps) sb.Append(" needs ").Append(dep.Name).Append(' ').Append(abi[dep]).Append('\n');
            // (the natives in C of its import and of those it needs go into its file: a changed native is a new library)
            foreach (var m in spec.Deps.Append(spec)) sb.Append(" natives ").Append(m.Name).Append(' ').Append(NativeSourcesHash(m.Name, target)).Append('\n');
            // (whether its natives in C live in its file depends on the other imports of the program: a variant of its own)
            sb.Append("natives-here ").Append(NativesStandAlone(program, spec.Name, target)).Append('\n');
            sb.Append("flags ").Append(flags?.ToString() ?? "own").Append('\n');
            return Hash(sb.ToString()).Substring(0, 40);
        }

        // ---- the interface file -----------------------------------------------------------------------------------------------------------------
        private sealed class InterfaceFile
        {
            public int Format { get; set; }
            public string Module { get; set; } = "";
            public string Key { get; set; } = "";
            public string Abi { get; set; } = "";
            public string Flags { get; set; } = "";
            public List<string> Classes { get; set; } = new();
            public List<string> Exports { get; set; } = new();
            public List<string[]> Dispatchers { get; set; } = new();
            public List<string> Fields { get; set; } = new();
            public List<string> FieldAddresses { get; set; } = new();
            public List<string> IsOf { get; set; } = new();
            public List<string[]> TArgOfs { get; set; } = new();
            public List<string> Operators { get; set; } = new();
            public List<string> OverloadClasses { get; set; } = new();
            public List<string> ErrorClasses { get; set; } = new();
            public List<string[]> RefCallSites { get; set; } = new();
            public List<string[]> StaticFields { get; set; } = new();
            public List<string> Units { get; set; } = new();
            public List<EnumEntry> Enums { get; set; } = new();
            public List<string> ExcTypes { get; set; } = new();
            public List<string> ReflectionNatives { get; set; } = new();
            public List<string> Made { get; set; } = new();
            public List<string[]> SharedNatives { get; set; } = new();
            public string NativeThunks { get; set; } = "";
            public bool JsonEngine { get; set; }
        }
        private sealed class EnumEntry
        {
            public string Name { get; set; } = "";
            public bool Flags { get; set; }
            public List<string> Members { get; set; } = new();
            public List<long> Values { get; set; } = new();
        }

        private static ModuleFlags ParseFlags(string text)
        {
            bool B(int i) => i < text.Length && text[i] == '1';
            return new ModuleFlags(B(0), B(1), B(2), B(3), B(4), B(5), B(6), B(7), B(8), B(9), B(10), B(11), B(12), B(13), B(14));
        }

        private static InterfaceFile InterfaceOf(ModuleOutput output, string key, string abi, IEnumerable<string> exports)
        {
            var n = output.Needs;
            return new InterfaceFile
            {
                Format = InterfaceFormat, Module = output.Spec.Name, Key = key, Abi = abi, Flags = output.Flags.ToString(), Classes = output.Spec.Classes.ToList(),
                Exports = exports.OrderBy(e => e, StringComparer.Ordinal).ToList(),
                Dispatchers = n.Dispatchers.Select(d => new[] { d.Name, d.Argc.ToString(CultureInfo.InvariantCulture) }).ToList(),
                Fields = n.Fields.ToList(), FieldAddresses = n.FieldAddresses.ToList(), IsOf = n.IsOf.ToList(),
                TArgOfs = n.TArgOfs.Select(t => new[] { t.Cls, t.Index.ToString(CultureInfo.InvariantCulture) }).ToList(),
                Operators = n.Operators.ToList(), OverloadClasses = n.OverloadClasses.ToList(), ErrorClasses = n.ErrorClasses.ToList(),
                RefCallSites = n.RefCallSites.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Argc).Select(r => new[] { r.Name, r.Argc.ToString(CultureInfo.InvariantCulture) }).ToList(),
                StaticFields = n.StaticFields.Select(f => new[] { f.Class, f.Field }).ToList(),
                Units = n.Units.Select(u => Convert.ToBase64String(MemoryPackSerializer.Serialize(u))).ToList(),
                Enums = n.Enums.Select(e => new EnumEntry { Name = e.Name, Flags = e.IsFlags, Members = e.Members.Select(m => m.Name).ToList(), Values = e.Members.Select(m => m.Value).ToList() }).ToList(),
                ExcTypes = n.ExcTypes.ToList(), ReflectionNatives = n.ReflectionNatives.ToList(), Made = n.Classes.ToList(), NativeThunks = output.NativeThunks, JsonEngine = output.JsonEngine,
                SharedNatives = n.SharedNatives.Select(x => new[] { x.Key, x.Value.Argc.ToString(CultureInfo.InvariantCulture), x.Value.NeedsList ? "1" : "0", x.Value.Import }).ToList(),
            };
        }

        private static ModuleOutput OutputOf(InterfaceFile f, ModuleSpec spec)
        {
            var n = new ModuleNeeds();
            foreach (var d in f.Dispatchers) n.Dispatchers.Add((d[0], int.Parse(d[1], CultureInfo.InvariantCulture)));
            n.Fields.UnionWith(f.Fields);
            n.FieldAddresses.UnionWith(f.FieldAddresses);
            n.IsOf.UnionWith(f.IsOf);
            foreach (var t in f.TArgOfs) n.TArgOfs.Add((t[0], int.Parse(t[1], CultureInfo.InvariantCulture)));
            n.Operators.UnionWith(f.Operators);
            n.OverloadClasses.UnionWith(f.OverloadClasses);
            n.ErrorClasses.UnionWith(f.ErrorClasses);
            foreach (var r in f.RefCallSites) n.RefCallSites.Add((r[0], int.Parse(r[1], CultureInfo.InvariantCulture)));
            foreach (var s in f.StaticFields) n.StaticFields.Add((s[0], s[1]));
            n.Units.AddRange(f.Units.Select(u => MemoryPackSerializer.Deserialize<Unit>(Convert.FromBase64String(u))!));
            n.Enums.AddRange(f.Enums.Select(e => EnumType.Intern(e.Name, e.Flags, e.Members.Zip(e.Values).Select(m => (m.First, m.Second)).ToList())));
            n.ExcTypes.AddRange(f.ExcTypes);
            n.ReflectionNatives.UnionWith(f.ReflectionNatives);
            n.Classes.UnionWith(f.Made);
            foreach (var x in f.SharedNatives) n.SharedNatives[x[0]] = (int.Parse(x[1], CultureInfo.InvariantCulture), x[2] == "1", x.Length > 3 ? x[3] : "");
            return new ModuleOutput { Spec = spec, Body = "", NativeThunks = f.NativeThunks, Needs = n, Flags = ParseFlags(f.Flags), JsonEngine = f.JsonEngine };
        }

        private static InterfaceFile? ReadInterface(string path, string key)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var f = JsonSerializer.Deserialize<InterfaceFile>(File.ReadAllText(path));
                return f != null && f.Format == InterfaceFormat && f.Key == key ? f : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or MemoryPackSerializationException) { return null; }
        }

        private static void WriteAtomically(string path, string text)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, true);
        }

        /// <summary>Linkage "static" (ROADMAP 9): the program's C and, per library module, its static library in the cache - looked up by its key, or its C file
        /// written next to where the library goes (the caller compiles it and puts the library there). The switches every module is built with are those of all of
        /// them; a module whose interface for these switches is in the cache is not generated at all.</summary>
        /// <param name="toolchainStamp">What identifies the compiler (its kind, path, version, arguments): part of every key.</param>
        public static StaticBuildPlan GenerateStatic(LinkedProgram program, TargetProfile target, string toolchainStamp, Func<int>? chooseSdl = null, TrimMode? trim = null, string? cacheRoot = null)
        {
            var specs = ModulesOf(program);
            var abi = specs.ToDictionary(s => s, s => AbiHash(program, s));
            string root = Path.Combine(cacheRoot ?? NativeLibraryCache.Root, target.Name);
            var log = new StringBuilder();
            int? sdl = target.Native.Sdl;
            ModuleFlags? flags = null;
            for (int round = 0; ; round++)
            {
                var outputs = new List<ModuleOutput>();
                var fresh = new Dictionary<ModuleSpec, ModuleOutput>();
                var keys = new Dictionary<ModuleSpec, string>();
                var union = flags ?? default;
                foreach (var spec in specs)
                {
                    string key = LibraryKey(program, spec, abi, target, toolchainStamp, flags, sdl);
                    keys[spec] = key;
                    string iface = Path.Combine(root, key, spec.Id + NativeLibraryCache.InterfaceExtension);
                    ModuleOutput output;
                    if (ReadInterface(iface, key) is { } known)
                    {
                        // (the program declares a class of the module anew - `class Exception` of its own: the module's classes are not those its library was built with)
                        if (!known.Classes.SequenceEqual(spec.Classes))
                            throw new NativeNotSupportedException($"module '{spec.Name}': the program declares classes of the module anew");
                        output = OutputOf(known, spec);
                    }
                    else
                    {
                        output = GenerateLibrary(program, spec, specs, target, trim, flags, separate: true);
                        fresh[spec] = output;
                        if (flags == null)   // (the module's own switches: only its interface is kept)
                            WriteAtomically(iface, JsonSerializer.Serialize(InterfaceOf(output, key, abi[spec], output.Exports)));
                    }
                    outputs.Add(output);
                    union = union.Or(output.Flags);
                }
                var main = new CppGenerator(program, target)
                {
                    _chooseSdl = chooseSdl, _trim = EffectiveTrim(program, trim), _role = ModuleRole.Program, _forced = flags, _libraries = outputs, _separate = true,
                };
                main.SetModules(specs, null);
                string c = main.Run();
                union = union.Or(main.CurrentFlags);
                if (main._sdlChosen is int chosen && sdl == null) sdl = chosen;
                if (flags is { } final && final.Equals(union))
                {
                    var libraries = new List<StaticLibrary>();
                    foreach (var spec in specs)
                    {
                        string dir = Path.Combine(root, keys[spec]);
                        string name = "fire_" + spec.Id;
                        string cFile = Path.Combine(dir, spec.Id + ".c");
                        string ifaceFile = Path.Combine(dir, spec.Id + NativeLibraryCache.InterfaceExtension);
                        bool cached = !fresh.ContainsKey(spec);
                        if (fresh.TryGetValue(spec, out var output))
                        {
                            string head = main._libraryHead!;
                            if (output.JsonEngine) head = head.Replace("#include \"fire_json.h\"", "#define FIRE_JSON_IMPLEMENTATION 1   // (the JSON engine: in the file of the module that calls it)\n#include \"fire_json.h\"");
                            WriteAtomically(cFile, ResolveLineResets(head + output.NativesC + output.Body));
                            // (the interface last: it says the library is there - the caller writes it after building the library, see CommitStatic)
                            WriteAtomically(ifaceFile + ".new", JsonSerializer.Serialize(InterfaceOf(output, keys[spec], abi[spec], output.Exports)));
                            log.AppendLine($"module {spec.Name}: new ({keys[spec].Substring(0, 12)})");
                        }
                        else log.AppendLine($"module {spec.Name}: from the cache ({keys[spec].Substring(0, 12)})");
                        libraries.Add(new StaticLibrary(spec.Name, keys[spec], dir, cFile, name, ifaceFile, cached));
                    }
                    return new StaticBuildPlan(c, main._nativesCode, libraries, log.ToString());
                }
                if (round > 4) throw new NativeNotSupportedException("the modules do not agree on the switches of the program");
                flags = union;
            }
        }

        /// <summary>After the library of a module is built: its interface says it is in the cache.</summary>
        public static void CommitStatic(StaticLibrary library)
        {
            string pending = library.InterfaceFile + ".new";
            if (File.Exists(pending)) File.Move(pending, library.InterfaceFile, true);
        }
    }
}
