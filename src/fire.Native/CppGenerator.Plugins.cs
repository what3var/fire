using System.Globalization;
using System.Text;
using System.Text.Json;
using fire.Bytecode;
using fire.Runtime;
using fire.Values;
using fire.Ast;
using fire.Package.Manager;

namespace fire.Native
{
    /// <summary>What a program with linkage "shared" says about itself for the plugins built for it (`<program>.firehost` next to it; `--host`): the switches and
    /// the configuration of the runtime, its library modules with their interfaces and the files of their libraries.</summary>
    public sealed class PluginHostInfo
    {
        public const string Extension = ".firehost";
        public int Format { get; set; } = CppGenerator.PluginFormat;
        public string Target { get; set; } = "";
        public string Flags { get; set; } = "";
        public int? Sdl { get; set; }
        public string Head { get; set; } = "";
        /// <summary>Windows: the file of the runtime's DLL.</summary>
        public string? Runtime { get; set; }
        public List<PluginHostModule> Modules { get; set; } = new();

        public static PluginHostInfo? Read(string path)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<PluginHostInfo>(File.ReadAllText(path)) : null; }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }

        public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    }

    public sealed class PluginHostModule
    {
        public string Name { get; set; } = "";
        public string Abi { get; set; } = "";
        /// <summary>The file of its shared library (DLL) next to the program.</summary>
        public string Library { get; set; } = "";
    }

    /// <summary>A plugin as generated: its C (one file), its module name, the interfaces of the modules it needs (the host must have the same), the log.</summary>
    public sealed record PluginBuildPlan(string C, string Name, IReadOnlyList<(string Module, string Abi)> Deps, string Log);

    /// <summary>
    /// fire plugins (ROADMAP 9, phase D b; docs/NATIVE_BACKEND.md "Plugins"): the classes of a program built as a library module of their own (`--linkage plugin`), a
    /// shared library that a program with linkage "shared" loads while it runs (`Library.Load`) - the runtime registers its classes (fire_rt.h fireRegisterPlugin).
    ///
    /// The plugin is a library module (its generator has the library role): it numbers its classes from a base the runtime gives it, calls the other modules by their
    /// exported names and serves its own classes; what the program defines for a library (the base, the tables of units, enums and catch types) it defines itself,
    /// filled when it is registered (by name). It writes the descriptors of its classes, its reflection rows and `fire_module_info`.
    ///
    /// A program that can load plugins (linkage "shared") is an open world: no class of its libraries or of itself is the last of its kind (no devirtualisation
    /// without a guard, the interface dispatchers fall back to the runtime, `is of` asks the runtime for classes after its own), every class of the libraries has its
    /// full descriptor and the tables of its own classes name every member (a plugin may call any by name).
    /// </summary>
    public sealed partial class CppGenerator
    {
        public const int PluginFormat = 1;

        /// <summary>The program: plugins may add classes (linkage "shared").</summary>
        private bool _openWorld;
        /// <summary>A library module that is a plugin.</summary>
        private bool _plugin;
        /// <summary>A plugin: the modules it needs with their interfaces (the runtime compares them with the program's).</summary>
        private List<(string Module, string Abi)> _pluginDeps = new();
        private const string PluginHeadMark = "@FIRE_PLUGIN_HEAD@";

        private static string InsertAfterRuntime(string text, string insert)
        {
            const string include = "#include \"fire_rt.h\"\n";
            int at = text.IndexOf(include, StringComparison.Ordinal);
            return at < 0 ? insert + text : text.Insert(at + include.Length, insert);
        }

        /// <summary>The key of the configuration of the runtime a plugin must be built for: the generator, the switches of the program, the head of its files.</summary>
        internal static string PluginHeadKey(string head, string flags) =>
            Hash($"fire-plugin {PluginFormat} {typeof(CppGenerator).Assembly.ManifestModule.ModuleVersionId}\nflags {flags}\n{head}").Substring(0, 40);

        /// <summary>The program (linkage "shared"): what the runtime checks a plugin against (g_pluginHost).</summary>
        private string PluginHostCode()
        {
            if (!_openWorld || _sharedAbis == null || _libraryHead == null) return "";
            var sb = new StringBuilder();
            sb.AppendLine("/* ---- fire plugins (ROADMAP 9 phase D b): what the runtime checks a plugin against - the configuration of the runtime, the interfaces of the modules ---- */");
            sb.AppendLine("static const char* fire_moduleAbi(const char* name) {");
            foreach (var m in _sharedAbis.Keys) sb.AppendLine($"    if (strcmp(name, {CString(m.Name)}) == 0) return fire_abi_{m.Id};");
            sb.AppendLine("    return NULL;");
            sb.AppendLine("}");
            sb.AppendLine($"static const char* const kPluginExcNames[] = {{{string.Join(", ", _excTypes.Select(CString))}}};");
            string flags = (_forced ?? CurrentFlags).ToString();
            sb.AppendLine($"static const FirePluginHost kPluginHost = {{{CString(PluginHeadKey(_libraryHead, flags))}, fire_moduleAbi, kPluginExcNames, {_excTypes.Count}u, {(_dllModules != null ? "fire_hostFind" : "NULL")}}};");
            _initCode.AppendLine("    g_pluginHost = &kPluginHost;   // (it can load fire plugins)");
            return sb.ToString();
        }

        /// <summary>The program (linkage "shared"): what the plugins built for it need to know (written next to it as `.firehost`).</summary>
        internal PluginHostInfo? HostInfo(IReadOnlyDictionary<ModuleSpec, string> libraryFiles, string? runtimeFile)
        {
            if (!_openWorld || _sharedAbis == null || _libraryHead == null) return null;
            string flags = (_forced ?? CurrentFlags).ToString();
            return new PluginHostInfo
            {
                Target = _target.Name, Flags = flags, Sdl = _sdlChosen ?? _target.Native.Sdl, Head = PluginHeadKey(_libraryHead, flags), Runtime = runtimeFile,
                Modules = _sharedAbis.Select(kv => new PluginHostModule { Name = kv.Key.Name, Abi = kv.Value, Library = libraryFiles.TryGetValue(kv.Key, out var f) ? f : "" }).ToList(),
            };
        }

        // ---- the plugin (a library module) ----------------------------------------------------------------------------------------------------------

        /// <summary>A plugin: the tables a program defines for a library - here its own, filled by the runtime when it registers the plugin (by name).</summary>
        private string PluginDeclarations()
        {
            var sb = new StringBuilder();
            string id = _self!.Id;
            if (_units.Count > 1) throw new NativeNotSupportedException($"the plugin uses units ({string.Join(", ", _units.Skip(1).Take(3).Select(u => u.ToString()))}): not possible in a plugin yet");
            sb.AppendLine($"/* ---- plugin {_self.Name} (ROADMAP 9 phase D b): the tables a program defines for a library module - its own, filled when it is registered ---- */");
            sb.AppendLine($"static uint32_t fire_mb_{id} = 0;");
            foreach (var m in _usedModules.OrderBy(m => m.Index)) sb.Append(HostDecl($"fire_mb_{m.Id}", $"extern const uint32_t fire_mb_{m.Id};"));
            sb.AppendLine($"FIRE_MAYBE_UNUSED static const uint16_t fire_units_{id}[] = {{0}};");
            sb.AppendLine($"static uint8_t fire_enums_{id}[{_enumList.Count + 1}];");
            sb.AppendLine($"static int32_t fire_exct_{id}[{Math.Max(1, _excTypes.Count)}];");
            sb.AppendLine($"FIRE_MAYBE_UNUSED static const int32_t fire_dbgsrc_{id}[{Math.Max(1, _self.Sources.Count)}] = {{0}};");
            return sb.ToString();
        }

        /// <summary>A plugin, while it is generated: what its descriptors call (the destructors and ToString of the chains of its classes) and the function with the
        /// static initialisers of its classes.</summary>
        private void RegisterPluginTargets()
        {
            if (!_plugin) return;
            foreach (var name in _self!.Classes)
            {
                var rc = FindClass(name);
                for (var c = rc; c != null; c = c.Base) if (c.Destructor != null) GetFunc(c.Destructor, FuncKind.Dtor);
                if (rc.FindMethodWithAccess("ToString", 0).Proto is { ParamCount: 0, IsStatic: false } toString) GetFunc(toString, FuncKind.Method);
            }
            // the static initialisers of its classes (the compiler puts them at the start of a program's top level): what they make belongs to the global scope
            _pluginStaticInits.Clear();
            foreach (var rc in _program.Program.Classes.Values.Where(c => _self.Classes.Contains(c.Name)))
                foreach (var (field, init) in rc.StaticFields) _pluginStaticInits.Add((rc.Name, field, GetFunc(init, FuncKind.Init)));
        }
        private readonly List<(string Class, string Field, Func Init)> _pluginStaticInits = new();

        /// <summary>A plugin: the end of its file - the descriptors of its classes, its enums and catch types by name, its reflection rows and `fire_module_info`.</summary>
        private string PluginTrailer()
        {
            if (!_plugin) return "";
            var sb = new StringBuilder();
            string id = _self!.Id;
            sb.AppendLine($"/* ---- plugin {_self.Name}: what the runtime registers (fire_module_info, fire_rt.h FirePlugin) ---- */");
            var columns = new StringBuilder();
            var rows = new List<string>();
            var rowCode = new StringBuilder();
            for (int k = 0; k < _self.Classes.Count; k++) rows.Add(DescRow(RegisterClass(_self.Classes[k]), k, rowCode, columns, excTypes: false));
            sb.Append(_libraryTablesCode);
            sb.Append(rowCode);
            sb.Append(columns);
            sb.AppendLine($"static const FireClassDesc kPlDescs[] = {{\n    {string.Join(",\n    ", rows)}\n}};");
            sb.AppendLine("static void fire_pl_fill(FireClassDesc* rows) { memcpy(rows, kPlDescs, sizeof kPlDescs); }");
            sb.AppendLine($"static const char* const kPlClasses[] = {{{string.Join(", ", _self.Classes.Select(CString))}}};");
            string depNames = "NULL", depAbis = "NULL";
            if (_pluginDeps.Count > 0)
            {
                depNames = "kPlDepNames"; depAbis = "kPlDepAbis";
                sb.AppendLine($"static const char* const kPlDepNames[] = {{{string.Join(", ", _pluginDeps.Select(d => CString(d.Module)))}}};");
                sb.AppendLine($"static const char* const kPlDepAbis[] = {{{string.Join(", ", _pluginDeps.Select(d => CString(d.Abi)))}}};");
            }
            sb.AppendLine($"static const char* const kPlExc[] = {{{string.Join(", ", _excTypes.Select(CString))}}};");
            string enums = "NULL";
            if (_enumList.Count > 0)
            {
                enums = "kPlEnums";
                for (int i = 0; i < _enumList.Count; i++)
                    sb.AppendLine($"static const FireEnumMember kPlEnumM{i}[] = {{{string.Join(", ", _enumList[i].Members.Select(m => $"{{{CString(m.Name)}, {(m.Value == long.MinValue ? "INT64_MIN" : m.Value.ToString(CultureInfo.InvariantCulture) + "LL")}}}").DefaultIfEmpty("{\"\", 0}"))}}};");
                sb.AppendLine($"static const FireEnum kPlEnums[] = {{{string.Join(", ", _enumList.Select((e, i) => $"{{{CString(e.Name)}, {(e.IsFlags ? 1 : 0)}, kPlEnumM{i}, {e.Members.Count}}}"))}}};");
            }
            string rf = "NULL";
            if (_usesReflection)
            {
                rf = "kPlRf";
                sb.AppendLine($"static const void* const kPlRf[] = {{{string.Join(", ", _self.Classes.Select(n => "&" + RfRowName(ModId, FindClass(n))))}}};");
            }
            sb.AppendLine("static void fire_pl_init(void) {");
            foreach (var (cls, field, init) in _pluginStaticInits)
                sb.AppendLine($"    {{ Value r = {init.Name}(Undef()); {(_usesExceptions ? "if (g_unwind.active) return; " : "")}adopt(r, g_globalOwn); {StaticFieldVar(cls, field)} = r; retain(r); }}   // {cls}.{field}");
            sb.AppendLine("}");
            string link = "NULL";
            if (_dll)
            {
                link = $"fire_link_{id}";
                sb.AppendLine($"__declspec(dllexport) const char* fire_link_{id}(const void* (*find)(const char*));");
            }
            sb.AppendLine($"{(_dll ? "__declspec(dllexport) " : "")}extern const FirePlugin fire_module_info;");
            sb.AppendLine($"{(_dll ? "__declspec(dllexport) " : "")}const FirePlugin fire_module_info = {{FIRE_PLUGIN_FORMAT, {CString(_self.Name)}, \"{PluginHeadMark}\", {_pluginDeps.Count}u, {depNames}, {depAbis}, "
                + $"{_self.Classes.Count}u, kPlClasses, &fire_mb_{id}, fire_pl_fill, {_enumList.Count}u, {enums}, fire_enums_{id}, {_excTypes.Count}u, kPlExc, fire_exct_{id}, {rf}, fire_pl_init, {link}}};");
            return sb.ToString();
        }

        /// <summary>A plugin (`--linkage plugin`, ROADMAP 9 phase D b): the classes of the program's own sources as a library module of their own, against the library
        /// modules of the program (core, the imports, the libraries of the project) - the program that loads it must have these with the same interfaces. The switches
        /// are those of that program (<paramref name="host"/>, its `.firehost` file), else those of the plugin and its modules.</summary>
        public static PluginBuildPlan GeneratePlugin(LinkedProgram program, TargetProfile target, string name, PluginHostInfo? host, StrippingLevel? stripping, Func<int>? chooseSdl,
            string toolchainStamp, string? cacheRoot = null)
        {
            bool dll = target.Native.Platform == "windows";
            var log = new StringBuilder();
            var specs = ModulesOf(program);
            var abi = specs.ToDictionary(s => s, s => AbiHash(program, s));
            var modules = program.SourceModules ?? throw new NativeNotSupportedException("the program does not carry the modules of its sources");
            string cName = ModuleCName(name).Substring(1);
            var self = new ModuleSpec { Name = name, Id = "p" + cName, Index = specs.Count };
            if (specs.Any(s => s.Name == name)) throw new NativeNotSupportedException($"the plugin has the name of a module of the program: '{name}'");
            for (int i = 0; i < modules.Count; i++) if (modules[i] == null) self.Sources.Add(i);
            self.Deps.AddRange(specs);
            foreach (var rc in program.Program.Classes.Values)
                if (rc.Decl != null && self.Sources.Contains(rc.Decl.Source)) self.Classes.Add(rc.Name);
            self.Classes.Sort(StringComparer.Ordinal);
            if (self.Classes.Count == 0) throw new NativeNotSupportedException("the plugin declares no classes");
            var all = specs.Append(self).ToList();
            ModuleFlags? flags = host != null ? ParseFlags(host.Flags) : null;
            if (host != null && host.Target != target.Name) throw new NativeNotSupportedException($"the program '{host.Target}' is built for another target than the plugin ('{target.Name}')");
            if (host != null)
                foreach (var dep in specs)
                {
                    var their = host.Modules.FirstOrDefault(m => m.Name == dep.Name);
                    if (their == null) throw new NativeNotSupportedException($"the program has no module '{dep.Name}' (the plugin imports it)");
                    if (their.Abi != $"{InterfaceFormat}:{abi[dep]}") log.AppendLine($"note: the module '{dep.Name}' of the program has another interface than the one the plugin sees - it will not load there");
                }
            if (flags == null)
            {
                // (no program named: the switches of the plugin's modules - each module's own, from its interface in the cache or generated)
                var union = default(ModuleFlags);
                string root = Path.Combine(cacheRoot ?? NativeLibraryCache.Root, target.Name);
                foreach (var spec in specs)
                {
                    string depKey = LibraryKey(program, spec, abi, target, toolchainStamp + "|shared" + (dll ? "|dll" : ""), null, target.Native.Sdl, LibraryStripping(program, stripping));
                    union = union.Or(ReadInterface(Path.Combine(root, depKey, spec.Id + NativeLibraryCache.InterfaceExtension), depKey) is { } known ? ParseFlags(known.Flags)
                        : GenerateLibrary(program, spec, specs, target, stripping, null, separate: true, dll: dll).Flags);
                }
                flags = union;
            }
            ModuleOutput output;
            for (int round = 0; ; round++)
            {
                output = GenerateLibrary(program, self, all, target, stripping, flags, separate: true, dll: dll, plugin: specs.Select(s => (s.Name, $"{InterfaceFormat}:{abi[s]}")).ToList());
                var union = flags.Value.Or(output.Flags);
                if (union.Equals(flags.Value)) break;
                if (host != null) throw new NativeNotSupportedException($"the plugin needs switches the program was not built with ({DescribeFlags(union, flags.Value)}) - the program must use them too");
                if (round > 4) throw new NativeNotSupportedException("the plugin and its modules do not agree on the switches");
                flags = union;
            }
            if (output.NativeThunks.Length > 0) throw new NativeNotSupportedException("the plugin calls natives in C++: not possible in a plugin yet");
            // the head of its file: the configuration of the runtime of the program (the same switches), the key the runtime compares
            var headGen = new CppGenerator(program, target) { _role = ModuleRole.Program, _forced = flags, _separate = true, _dll = dll, _chooseSdl = chooseSdl, _strip = LibraryStripping(program, stripping) };
            headGen.CollectPackages();
            headGen.ApplyForcedFlags();
            if (headGen._usesWindows && target.Native.Platform is "posix" or "windows") headGen._sdlChosen = target.Native.Sdl ?? host?.Sdl ?? chooseSdl?.Invoke() ?? 2;
            string head = headGen.LibraryHeadOf(headGen.HeadDefines() + headGen.HeadTail(0));
            string key = PluginHeadKey(head, flags.Value.ToString());
            if (output.JsonEngine) head = head.Replace("#include \"fire_json.h\"", "#define FIRE_JSON_IMPLEMENTATION 1   // (the JSON engine: in the file of the module that calls it)\n#include \"fire_json.h\"");
            if (host != null && key != host.Head) log.AppendLine($"note: the configuration of the runtime differs from the program's ({key.Substring(0, 12)}, the program {host.Head.Substring(0, Math.Min(12, host.Head.Length))})");
            string c = ResolveLineResets(head + output.NativesC + output.Body.Replace(PluginHeadMark, key) + output.LinkC);
            log.AppendLine($"plugin {name}: {self.Classes.Count} classes, switches {flags.Value}, runtime {key.Substring(0, 12)}");
            return new PluginBuildPlan(c, name, specs.Select(s => (s.Name, $"{InterfaceFormat}:{abi[s]}")).ToList(), log.ToString());
        }

        private static string DescribeFlags(ModuleFlags wanted, ModuleFlags has)
        {
            string[] names = { "exceptions", "threads", "actors", "reflection", "probes", "element pointers", "try depth", "global ownership", "take upwards", "take enumerate", "json", "time objects", "string calls", "graphics", "windows" };
            string w = wanted.ToString(), h = has.ToString();
            return string.Join(", ", Enumerable.Range(0, Math.Min(w.Length, names.Length)).Where(i => w[i] == '1' && (i >= h.Length || h[i] != '1')).Select(i => names[i]));
        }
    }
}
