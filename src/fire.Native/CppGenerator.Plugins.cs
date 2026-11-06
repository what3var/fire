using System.Globalization;
using System.Text;
using System.Text.Json;
using fire.Bytecode;
using fire.Runtime;
using fire.Values;
using fire.Ast;
using fire.Package.Manager;
using fire.Standard;

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

    /// <summary>A plugin as generated: its C (one file), its natives in C++ (null: none - a file of their own, compiled with the C++ compiler into the same library), its
    /// module name, the interfaces of the modules it needs (the host must have the same), the log.</summary>
    public sealed record PluginBuildPlan(string C, string? Natives, string Name, IReadOnlyList<(string Module, string Abi)> Deps, string Log);

    /// <summary>
    /// fire plugins (ROADMAP 9, phase D b; docs/spec/NATIVE_BACKEND.md "Plugins"): the classes of a program built as a library module of their own (`--linkage plugin`), a
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
            sb.AppendLine($"/* ---- plugin {_self.Name} (ROADMAP 9 phase D b): the tables a program defines for a library module - its own, filled when it is registered ---- */");
            sb.AppendLine($"static uint32_t fire_mb_{id} = 0;");
            foreach (var m in _usedModules.OrderBy(m => m.Index)) sb.Append(HostDecl($"fire_mb_{m.Id}", $"extern const uint32_t fire_mb_{m.Id};"));
            sb.AppendLine($"static uint16_t fire_units_{id}[{Math.Max(1, _units.Count)}];");
            sb.AppendLine($"static uint8_t fire_enums_{id}[{_enumList.Count + 1}];");
            sb.AppendLine($"static int32_t fire_exct_{id}[{Math.Max(1, _excTypes.Count)}];");
            // (a debug build: the indexes of its sources among the program's, given when it is registered - fire_dbgPlugin)
            sb.AppendLine(DebugBuild ? $"static int32_t fire_dbgsrc_{id}[{Math.Max(1, _self.Sources.Count)}];"
                : $"FIRE_MAYBE_UNUSED static const int32_t fire_dbgsrc_{id}[{Math.Max(1, _self.Sources.Count)}] = {{0}};");
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
            // (the program has JSON: the units and type arguments of the fields of its classes are in its tables)
            if (_usesJson)
                foreach (var name in _self.Classes)
                    foreach (var field in JsonMethods.JsonFieldsOf(FindClass(name)))
                    {
                        UnitId(field.Unit);
                        foreach (var arg in JsonTypeArgTexts(field.Type)) TArgRef(arg);
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
            string units = PluginUnits(sb);
            string rf = "NULL";
            if (_usesReflection)
            {
                rf = "kPlRf";
                sb.AppendLine($"static const void* const kPlRf[] = {{{string.Join(", ", _self.Classes.Select(n => "&" + RfRowName(ModId, FindClass(n))))}}};");
            }
            var jsonFixups = new List<string>();
            string json = PluginJson(sb, jsonFixups);
            sb.AppendLine("static void fire_pl_init(void) {");
            foreach (var fixup in jsonFixups) sb.AppendLine("    " + fixup);   // (the units of the fields JSON reads: the program's, known now)
            foreach (var (cls, field, init) in _pluginStaticInits)
                sb.AppendLine($"    {{ Value r = {init.Name}(Undef()); {(_usesExceptions ? "if (g_unwind.active) return; " : "")}adopt(r, g_globalOwn); {StaticFieldVar(cls, field)} = r; retain(r); }}   // {cls}.{field}");
            sb.AppendLine("}");
            // its `class extends` of classes it does not declare (of the program, of libraries, base types): their tables by the name of the class (the runtime adds
            // them to what an object of such a class finds by name - fireFindMethod)
            // (`class extends int` where nothing else extends int: `$int` is a class of the plugin - its table is an extension all the same)
            string exts = "0u, NULL, NULL";
            var extended = ExtServed.Select(c => c.Rc).Concat(_self.Classes.Where(n => n.StartsWith('$')).Select(FindClass)).ToList();
            if (extended.Count > 0)
            {
                sb.AppendLine($"static const char* const kPlExtClasses[] = {{{string.Join(", ", extended.Select(c => CString(c.Name)))}}};");
                sb.AppendLine($"static const FireMembers* const kPlExtTables[] = {{{string.Join(", ", extended.Select(c => "&" + MembersName(ModId, c)))}}};");
                exts = $"{extended.Count}u, kPlExtClasses, kPlExtTables";
            }
            string debug = PluginDebugTables(sb);
            string link = "NULL";
            if (_dll)
            {
                link = $"fire_link_{id}";
                sb.AppendLine($"__declspec(dllexport) const char* fire_link_{id}(const void* (*find)(const char*));");
            }
            sb.AppendLine($"{(_dll ? "__declspec(dllexport) " : "")}extern const FirePlugin fire_module_info;");
            sb.AppendLine($"{(_dll ? "__declspec(dllexport) " : "")}const FirePlugin fire_module_info = {{FIRE_PLUGIN_FORMAT, {CString(_self.Name)}, \"{PluginHeadMark}\", {_pluginDeps.Count}u, {depNames}, {depAbis}, "
                + $"{_self.Classes.Count}u, kPlClasses, &fire_mb_{id}, fire_pl_fill, {_enumList.Count}u, {enums}, fire_enums_{id}, {_excTypes.Count}u, kPlExc, fire_exct_{id}, {rf}, fire_pl_init, {link}, {units}, {json}, {exts}, {debug}}};");
            return sb.ToString();
        }

        /// <summary>A debug plugin (fire_rt.h FirePlugin, fire_debug.h fire_dbgPlugin): its sources - file and hash, in the order of fire_dbgsrc_&lt;p&gt; -, the table the
        /// runtime fills with their indexes among the program's sources, and its classes for the debugger (the names of their fields). Not a debug build: none.</summary>
        private string PluginDebugTables(StringBuilder sb)
        {
            if (!DebugBuild) return "0u, NULL, NULL, NULL, NULL";
            var files = _program.SourceFiles ?? Array.Empty<string?>();
            var hashes = _program.SourceHashes ?? Array.Empty<string?>();
            var sources = _self!.Sources.OrderBy(s => s).ToList();
            string At(IReadOnlyList<string?> list, int src) => src < list.Count && list[src] != null ? CString(list[src]!) : "NULL";
            sb.AppendLine($"static const char* const kPlSrcFiles[] = {{{string.Join(", ", sources.Select(s => At(files, s)).DefaultIfEmpty("NULL"))}}};");
            sb.AppendLine($"static const char* const kPlSrcHashes[] = {{{string.Join(", ", sources.Select(s => At(hashes, s)).DefaultIfEmpty("NULL"))}}};");
            var classes = _self.Classes.Select(RegisterClass).ToList();
            for (int k = 0; k < classes.Count; k++)
                if (classes[k].Fields.Count > 0) sb.AppendLine($"static const char* const kPlDbgFields{k}[] = {{{string.Join(", ", classes[k].Fields.Select(CString))}}};");
            sb.AppendLine("static const DbgClass kPlDbgClasses[] = {" + string.Join(", ", classes.Select((c, k) => $"{{{CString(c.Rc.Name)}, {c.Fields.Count}, {(c.Fields.Count > 0 ? $"kPlDbgFields{k}" : "NULL")}}}").DefaultIfEmpty("{\"\", 0, NULL}")) + "};");
            sb.AppendLine($"static const void* fire_pl_dbgClass(uint32_t k) {{ return k < {classes.Count}u ? (const void*)&kPlDbgClasses[k] : NULL; }}");
            return $"{sources.Count}u, kPlSrcFiles, kPlSrcHashes, fire_dbgsrc_{_self.Id}, fire_pl_dbgClass";
        }

        /// <summary>A plugin: the tables of its units (the program's numbers for them are found by dimensions, scale and symbol when it is registered, fire_rt.h
        /// firePluginUnits) - the dimensions by name, a row per unit over them, the conversion functions and the units a text may name. The fields of FirePlugin from
        /// `nunits` on.</summary>
        private string PluginUnits(StringBuilder sb)
        {
            string id = _self!.Id;
            var dimNames = _units.SelectMany(u => u.Dimensions.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (dimNames.Count > SeparateDims) throw new NativeNotSupportedException($"the plugin uses {dimNames.Count} dimensions of units; at most {SeparateDims}");
            string dims = "NULL", conv = "0u, NULL, NULL, NULL", text = "0u, NULL";
            if (dimNames.Count > 0)
            {
                dims = "kPlDims";
                sb.AppendLine($"static const char* const kPlDims[] = {{{string.Join(", ", dimNames.Select(CString))}}};");
            }
            sb.AppendLine("static const UnitInit kPlUnits[] = {");
            foreach (var u in _units)
            {
                string exps = dimNames.Count == 0 ? "0" : string.Join(", ", dimNames.Select(n => u.Dimensions.TryGetValue(n, out int e) ? e.ToString(CultureInfo.InvariantCulture) : "0"));
                string display = u.DisplaySymbol is { } symbol ? CString(symbol) : "NULL";
                int c = u.Converter is { } converter ? _unitConverters[converter].Index : -1;
                int ul = u.UnitlessConverter is { } plainConverter ? _unitConverters[plainConverter].Index : -1;
                sb.AppendLine($"    {{{{{exps}}}, {DoubleLiteral(u.Scale, false)}, {display}, {DoubleLiteral(u.Offset, false)}, {c}, {ul}}},");
            }
            sb.AppendLine("};");
            if (_unitConverters.Count > 0)
            {
                var converters = _unitConverters.OrderBy(c => c.Value.Index).ToList();
                sb.AppendLine($"static const UnitConvFn kPlUnitTo[] = {{{string.Join(", ", converters.Select(c => c.Value.To.Name))}}};");
                sb.AppendLine($"static const UnitConvFn kPlUnitFrom[] = {{{string.Join(", ", converters.Select(c => c.Value.From.Name))}}};");
                // (the hidden class of a unit the plugin declares is its own: the unit is new in the program - one of a library is the program's when it has it)
                sb.AppendLine($"static const uint8_t kPlUnitOwn[] = {{{string.Join(", ", converters.Select(c => _self.Classes.Contains(c.Key) ? "1" : "0"))}}};");
                conv = $"{converters.Count}u, kPlUnitTo, kPlUnitFrom, kPlUnitOwn";
            }
            if (_usesStringCall || _program.Program.CustomUnits.Count > 0)
            {
                // the units a text may name (as the program's kTextUnits): those of fire, then those of the plugin and its libraries by symbol and name
                var rows = new List<string>();
                foreach (var (symbol, prefixable) in Unit.BuiltInSymbols) rows.Add($"{{{CString(symbol)}, {UnitId(Unit.Parse(symbol))}, {(prefixable ? "true" : "false")}}}");
                foreach (var own in _program.Program.CustomUnits)
                {
                    int u = UnitId(Unit.OfCustom(own));
                    rows.Add($"{{{CString(own.Symbol)}, {u}, {(own.Prefixed ? "true" : "false")}}}");
                    if (own.Name != own.Symbol) rows.Add($"{{{CString(own.Name)}, {u}, false}}");
                }
                sb.AppendLine($"static const TextUnit kPlTextUnits[] = {{\n    {string.Join(",\n    ", rows)}\n}};");
                text = $"{rows.Count}u, kPlTextUnits";
            }
            return $"{_units.Count}u, kPlUnits, fire_units_{id}, {dimNames.Count}u, {dims}, {conv}, {text}";
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
            if (PluginExtensionProblem(program, self.Sources) is { } extensionProblem) throw new NativeNotSupportedException(extensionProblem);
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
            string? natives = output.NativeThunks.Length > 0 ? headGen.PluginNativesCpp(output) : null;
            log.AppendLine($"plugin {name}: {self.Classes.Count} classes, switches {flags.Value}, runtime {key.Substring(0, 12)}{(natives != null ? $", natives in C++ ({output.CppNatives.Count})" : "")}");
            return new PluginBuildPlan(c, natives, name, specs.Select(s => (s.Name, $"{InterfaceFormat}:{abi[s]}")).ToList(), log.ToString());
        }

        /// <summary>A plugin's `class extends` of a class of the program or a library (or of a base type): it may add methods and properties with bodies - the
        /// objects of that class are made by the program, they have no room for a field the plugin adds (nor does the class a constructor, destructor, static field
        /// or operator the program would have to know). Null: fine; else what is wrong (the text of the build error).</summary>
        public static string? PluginExtensionProblem(LinkedProgram program, IReadOnlySet<int> pluginSources)
        {
            foreach (var rc in program.Program.Classes.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                if (rc.Decl == null || pluginSources.Contains(rc.Decl.Source)) continue;
                string target = BaseTypeExtensions.TryGetTypeName(rc.Name, out var baseType) ? baseType : rc.Name;
                foreach (var member in rc.Decl.Members)
                {
                    if (!pluginSources.Contains(member.Source)) continue;
                    string? what = member switch
                    {
                        FieldDecl f => $"the field '{f.Name}'",
                        ConstructorDecl => "a constructor",
                        DestructorDecl => "a destructor",
                        MethodDecl m when m.Name.StartsWith("operator", StringComparison.Ordinal) || m.Name is "GetIndex" or "SetIndex" => $"the operator '{m.Name}'",
                        _ => null,
                    };
                    if (what != null)
                        return $"'class extends {target}' in a plugin (line {member.Line}): {what} cannot be added to a class of the program or a library - its objects are made by the program; a plugin adds methods and properties with get/set bodies only.";
                }
            }
            return null;
        }

        /// <summary>A plugin's natives in C++ (this generator: the plugin's program, its packages collected): the configuration of the runtime of its C file, fire_rt_cpp.hpp,
        /// the C++ of the packages whose natives its code calls and its thunks (`fire_nat_<p>_k`). They are the plugin's own copy - their state is not the program's,
        /// also when the program imports the same package (Linux: compiled with hidden visibility, as a DLL on Windows is anyway). On Windows the pointers to the
        /// program are those of its C file (FIRE_DLL_LINKED_ELSEWHERE).</summary>
        private string PluginNativesCpp(ModuleOutput output)
        {
            var called = new HashSet<string>(output.CppNatives, StringComparer.Ordinal);
            var nat = new StringBuilder();
            nat.Append(HeadDefines().Replace("// Generated by fire.Native from the bytecode - do not edit.", "// Generated by fire.Native: the natives in C++ of a fire plugin - do not edit."));
            nat.Append(HeadTail(0));
            if (_dll) { nat.Append(DllHead()); nat.AppendLine("#define FIRE_DLL_LINKED_ELSEWHERE 1   // (the pointers to the program are those of the plugin's C file)"); }
            nat.AppendLine($"#define FIRE_PLATFORM_HEADER \"platform/{_target.Native.Platform}/fire_platform.h\"");
            nat.AppendLine($"#define FIRE_PLATFORM_CPP_HEADER \"platform/{_target.Native.Platform}/fire_platform.hpp\"");
            nat.AppendLine("#include \"fire_rt_cpp.hpp\"   // the runtime (fire_rt.h, the program has its state) and what C++ code expects of it");
            foreach (var (import, _) in _packageImports.Where(p => p.Import.Import.Native is { IsC: false } n && n.Functions.Any(f => called.Contains(f.Symbol))))
                foreach (var (file, text) in import.ReadNativeSources(new[] { _target.Native.Platform, _target.Name }))
                {
                    nat.AppendLine($"// ---- package {import.Package.Name} {import.Package.Version}, import \"{import.Name}\": {file}");
                    nat.AppendLine(text);
                    nat.AppendLine($"// ---- end of {file}");
                }
            nat.AppendLine();
            nat.Append(output.NativeThunks);
            return nat.ToString();
        }

        private static string DescribeFlags(ModuleFlags wanted, ModuleFlags has)
        {
            string[] names = { "exceptions", "threads", "actors", "reflection", "probes", "element pointers", "try depth", "global ownership", "take upwards", "take enumerate", "json", "time objects", "string calls", "graphics", "windows" };
            string w = wanted.ToString(), h = has.ToString();
            return string.Join(", ", Enumerable.Range(0, Math.Min(w.Length, names.Length)).Where(i => w[i] == '1' && (i >= h.Length || h[i] != '1')).Select(i => names[i]));
        }
    }
}
