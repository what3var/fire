using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using fire.Native;
using fire.Runtime;

namespace fire.Compiler
{
    /// <summary>The result of a native build: where the program (or, for a toolchain that only writes files, the folder) is, what the tools said, and the errors and warnings of the compile
    /// (<see cref="Diagnostic"/>, with codes; <see cref="NativeBuilder.BuildSafe"/> fills them).</summary>
    public sealed record NativeBuildResult(bool Ok, string Output, string Log, IReadOnlyList<Diagnostic>? Diagnostics = null)
    {
        /// <summary>The build was cancelled (<see cref="BuildMonitor.Token"/>): its tools were ended, what it had half written is gone.</summary>
        public bool Cancelled { get; init; }
    }

    /// <summary>
    /// Builds a program natively: translate it to C++ (<see cref="CppGenerator"/>) for a target, write the runtime and the platform package of the target next to it,
    /// and let the toolchain of the configuration (<see cref="NativeConfig"/>) turn it into a program - or only write the files (and build files) of a project.
    /// </summary>
    public static partial class NativeBuilder
    {
        /// <summary>The program (C).</summary>
        public const string SourceName = "fire_program.c";
        /// <summary>The natives of the program in C++ (when it has some).</summary>
        public const string NativesName = CppGenerator.NativesFileName;

        /// <summary>The program of <paramref name="sources"/> as C++ for <paramref name="target"/>. Compile errors of the script and
        /// <see cref="NativeNotSupportedException"/> propagate.</summary>
        public static string Generate(IReadOnlyList<string> sources, TargetProfile target, VmExecutionMode? mode = null, int? floatWidth = null, string? basePath = null, IReadOnlyList<string>? defines = null, fire.Projects.BuildPlan? plan = null)
        {
            return GenerateLinked(sources, target, mode, floatWidth, basePath, defines, plan).C;
        }

        /// <summary>Like <see cref="Generate"/>, and whether the program asked for the GUI subsystem (`#noconsole`, the project's subsystem).</summary>
        public static NativeProgram GenerateLinked(IReadOnlyList<string> sources, TargetProfile target, VmExecutionMode? mode = null, int? floatWidth = null, string? basePath = null, IReadOnlyList<string>? defines = null, fire.Projects.BuildPlan? plan = null, IReadOnlyList<string?>? sourcePaths = null,
            StrippingLevel? stripping = null, fire.Runtime.GlobalJobsMode? globalJobs = null, string? toolchainKind = null, fire.Runtime.OptimizeFor? optimize = null, bool stats = false)
        {
            var linked = new Linker { BasePath = basePath, Engine = "native", ToolchainKind = toolchainKind, Defines = defines, Plan = plan, SourcePaths = plan?.SourcePaths.Cast<string?>().ToList() ?? sourcePaths, GlobalJobsDefault = globalJobs,
                Optimize = optimize ?? OptimizeOf(null, plan, target) }.CompileAndLink(sources, null, null, mode, floatWidth, target);
            BuildMonitor.Step(BuildPhase.GenerateC, "Generating the C of the program");
            var generated = CppGenerator.GenerateSources(linked, target, stripping, stats);
            return new NativeProgram(generated.Program, generated.Natives, linked.GuiSubsystem, linked.VersionInfo, linked.IconPath) { Funcs = generated.Funcs };
        }

        /// <summary>`--stats`: the report of the build (NativeStats) into the log and next to the program (.stats.txt, .stats.json).</summary>
        private static void WriteStats(TargetProfile target, string exe, string workDir, string cFile, string? mapFile, NativeProgram program, StringBuilder log)
        {
            if (mapFile == null || !File.Exists(mapFile)) { log.AppendLine("--stats: this toolchain writes no map of the program (Open Watcom, gcc and clang do)."); return; }
            var report = NativeStats.Analyze(target.Name, exe, workDir, cFile, mapFile, program.Funcs ?? Array.Empty<FuncStat>());
            string text = NativeStats.Text(report);
            File.WriteAllText(exe + ".stats.txt", text);
            File.WriteAllText(exe + ".stats.json", NativeStats.Json(report));
            log.Append(text);
            log.AppendLine($"(the report: {exe}.stats.txt and .stats.json, the map: {mapFile})");
        }

        /// <summary>The generated C (and the natives in C++, null: none) and what the executable needs besides: the subsystem, the version info and the icon.</summary>
        public sealed record NativeProgram(string C, string? Natives, bool Gui, fire.Utilities.PeVersionInfo? VersionInfo, string? IconPath)
        {
            /// <summary>`--stats`: what the generator made of each fire function.</summary>
            public IReadOnlyList<FuncStat>? Funcs { get; init; }
        }

        /// <summary>Writes version info and icon into a Windows executable the toolchain has made (the Win32 resource API; any other platform leaves the file alone).
        /// A missing icon file is reported in the log instead of failing the build.</summary>
        public static void ApplyPeMetadata(string exe, NativeProgram program, TargetProfile target, StringBuilder log)
        {
            if (target.Name != "windows" || !OperatingSystem.IsWindows() || !File.Exists(exe)) return;
            try
            {
                if (program.VersionInfo != null) fire.Utilities.PeResourceEditor.SetVersionInfo(exe, program.VersionInfo);
                if (!string.IsNullOrEmpty(program.IconPath))
                {
                    if (File.Exists(program.IconPath)) fire.Utilities.PeResourceEditor.SetIcon(exe, program.IconPath);
                    else log.AppendLine($"Icon '{program.IconPath}' not found: the program keeps the default icon.");
                }
                log.AppendLine("Version info and icon written into the program.");
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                log.AppendLine("Could not write version info/icon into the program: " + ex.Message);
            }
        }

        /// <summary>Writes <paramref name="cpp"/> as <paramref name="fileName"/> into <paramref name="directory"/> with everything it includes: the runtime and the platform package of
        /// the target (a package of your own is copied from the target's <c>platformPath</c>).</summary>
        public static string WriteFiles(string directory, string cpp, TargetProfile target, string fileName = SourceName, string? configDirectory = null, string? natives = null)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, cpp);
            string nativesPath = Path.Combine(directory, NativesName);
            if (natives != null) File.WriteAllText(nativesPath, natives);
            else if (File.Exists(nativesPath)) File.Delete(nativesPath);   // (of an earlier build into the same folder)
            NativeRuntimeFiles.WriteTo(directory, target.Native.Platform);
            if (target.Native.PlatformPath is { Length: > 0 } custom)
            {
                string source = Path.IsPathRooted(custom) || configDirectory == null ? custom : Path.Combine(configDirectory, custom);
                if (!Directory.Exists(source)) throw new NativeConfigException($"the platform folder '{source}' of target '{target.Name}' does not exist");
                CopyDirectory(source, Path.Combine(directory, "platform", target.Native.Platform));
            }
            return path;
        }

        /// <summary>The stripping level of a build (docs/spec/NATIVE_BACKEND.md "Größe des Programms"): the command line (`--stripping`, `--keep-all`), then the project's
        /// setting, then fire.native.json; null: the default (3; a debug build keeps everything).</summary>
        public static StrippingLevel? StrippingOf(NativeConfig config, fire.Projects.BuildPlan? plan) =>
            !config.StrippingFromCommandLine && Strippings.Parse(plan?.Settings.Stripping) is { } fromProject ? fromProject : config.Stripping;

        /// <summary>The direction of a build (OptimizeFor, docs/spec/NATIVE_BACKEND.md "Optimierungsrichtung"): `--optimize`, then the project's setting, then fire.native.json,
        /// then the target's (the PC targets of Open Watcom: size).</summary>
        public static fire.Runtime.OptimizeFor OptimizeOf(NativeConfig? config, fire.Projects.BuildPlan? plan, TargetProfile target) => OptimizeOf(config, plan?.Settings, target);

        /// <summary><see cref="OptimizeOf(NativeConfig?, fire.Projects.BuildPlan?, TargetProfile)"/> with the settings of the project.</summary>
        public static fire.Runtime.OptimizeFor OptimizeOf(NativeConfig? config, fire.Projects.ProjectSettings? settings, TargetProfile target)
        {
            if (config is { OptimizeFromCommandLine: true, OptimizeValue: { } forced }) return forced;
            return fire.Runtime.OptimizeFors.Parse(settings?.Optimize) ?? config?.OptimizeValue ?? target.Optimize;
        }

        /// <summary>The toolchain with the flags of the direction: size is the small-program profile ("size"); speed keeps a flag of its own (`-O3`) and otherwise is the
        /// default of the kind (Open Watcom: -Ot).</summary>
        public static ToolchainDef WithDirection(ToolchainDef toolchain, fire.Runtime.OptimizeFor direction)
        {
            if (toolchain.EffectiveKind is "files" or "custom") return toolchain;
            string? flags = direction == fire.Runtime.OptimizeFor.Size ? "size"
                : !IsSizeProfile(toolchain.Optimization) && toolchain.Optimization != null ? toolchain.Optimization
                : toolchain.EffectiveKind == "watcom" ? "-Ot" : null;
            if (flags == toolchain.Optimization) return toolchain;
            var copy = new ToolchainDef { Extends = toolchain.Extends }.WithBase(toolchain);   // (a copy: the definition of the machine stays as it is)
            copy.Optimization = flags;
            return copy;
        }

        /// <summary>Translates and builds. <paramref name="output"/> is the program, or the folder for a toolchain that only writes files. <paramref name="monitor"/> (else the
        /// <see cref="BuildMonitor.Current"/> one) gets the progress and the commands as they run; when it is cancelled the build throws <see cref="OperationCanceledException"/>
        /// (<see cref="BuildSafe"/> returns a result with <see cref="NativeBuildResult.Cancelled"/> instead).</summary>
        public static NativeBuildResult Build(IReadOnlyList<string> sources, NativeConfig config, TargetProfile target, ToolchainDef toolchain, string output,
            VmExecutionMode? mode = null, int? floatWidth = null, bool keepSources = false, string? basePath = null, IReadOnlyList<string>? defines = null, fire.Projects.BuildPlan? plan = null,
            IReadOnlyList<string?>? sourcePaths = null, BuildMonitor? monitor = null)
        {
            using var watching = BuildMonitor.Use(monitor);
            toolchain = WithDirection(toolchain, OptimizeOf(config, plan, target));
            // linkage "shared" (ROADMAP 9, phase C): Linux and macOS with gcc or clang, Windows with MinGW (gcc, clang; DLLs - also built on Linux); elsewhere (msvc, the
            // boards, wasm, TinyCC) it is "static" and the log says so
            if (string.Equals(config.Linkage, "shared", StringComparison.OrdinalIgnoreCase) && toolchain.EffectiveKind is not ("files" or "custom"))
            {
                bool can = toolchain.EffectiveKind is "gcc" or "clang" && (target.Native.Platform == "posix" && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && target.Name == TargetProfile.Host.Name
                    || target.Native.Platform == "windows");
                if (can) return BuildStatic(sources, config, target, toolchain, output, mode, floatWidth, keepSources, basePath, defines, plan, sourcePaths, shared: true);
                config.Linkage = "static";
                NativeBuildResult r;
                try { r = Build(sources, config, target, toolchain, output, mode, floatWidth, keepSources, basePath, defines, plan, sourcePaths); }
                finally { config.Linkage = "shared"; }
                return r with { Log = $"Linkage \"shared\" is only available on Linux and macOS with gcc or clang and on Windows with MinGW (not for the target '{target.Name}' with '{toolchain.EffectiveKind}'): built with linkage \"static\".\n" + r.Log };
            }
            if (string.Equals(config.Linkage, "plugin", StringComparison.OrdinalIgnoreCase))
                return BuildPlugin(sources, config, target, toolchain, output, mode, floatWidth, keepSources, basePath, defines, plan, sourcePaths);
            if (string.Equals(config.Linkage, "static", StringComparison.OrdinalIgnoreCase) && toolchain.EffectiveKind is not ("files" or "custom"))
                return BuildStatic(sources, config, target, toolchain, output, mode, floatWidth, keepSources, basePath, defines, plan, sourcePaths);
            if (string.Equals(config.Linkage, "static", StringComparison.OrdinalIgnoreCase) && toolchain.EffectiveKind == "files")
                return WriteStaticFiles(sources, config, target, toolchain, output, mode, floatWidth, basePath, defines, plan, sourcePaths);
            var program = GenerateLinked(sources, target, mode, floatWidth, basePath, defines, plan, sourcePaths, StrippingOf(config, plan), config.GlobalJobsValue, toolchain.EffectiveKind, OptimizeOf(config, plan, target), config.Stats);
            string cpp = program.C;
            bool gui = program.Gui;
            string? configDirectory = config.Path == null ? null : Path.GetDirectoryName(config.Path);
            var log = new StringBuilder();

            if (toolchain.EffectiveKind == "files")
            {
                string directory = Path.GetFullPath(output);
                WriteFiles(directory, cpp, target, SourceName, configDirectory, program.Natives);
                if (toolchain.Layout == "idf-component")
                    File.WriteAllText(Path.Combine(directory, "CMakeLists.txt"),
                        $"# Generated by fire.Native for target '{target.Name}': the program as an ESP-IDF component (call {target.Native.Entry.Name} from the application).\n" +
                        $"idf_component_register(SRCS \"{SourceName}\"{(program.Natives != null ? $" \"{NativesName}\"" : "")}\n                       INCLUDE_DIRS \".\")\n" +
                        "target_compile_options(${COMPONENT_LIB} PRIVATE $<$<COMPILE_LANGUAGE:C>:-std=gnu11> $<$<COMPILE_LANGUAGE:CXX>:-std=gnu++17>)\n");
                log.AppendLine($"{directory}: {SourceName}{(program.Natives != null ? ", " + NativesName : "")}, fire_rt.h and platform/{target.Native.Platform}/ for {target.Name}; the entry point is {target.Native.Entry.Name}");
                if (!string.IsNullOrWhiteSpace(toolchain.BuildCommand))
                {
                    var (ok, text) = RunCommand(toolchain.BuildCommand!, directory);
                    log.Append(text);
                    return new NativeBuildResult(ok, directory, log.ToString());
                }
                return new NativeBuildResult(true, directory, log.ToString());
            }

            string workDir = keepSources ? Path.GetFullPath(output) + ".src" : Path.Combine(Path.GetTempPath(), "fire-native-" + Guid.NewGuid().ToString("N"));
            try
            {
                string cppFile = WriteFiles(workDir, cpp, target, SourceName, configDirectory, program.Natives);
                string? nativesFile = program.Natives != null ? Path.Combine(workDir, NativesName) : null;
                string exe = Path.GetFullPath(output);
                if (toolchain.EffectiveKind != "custom" && ToolchainDetector.Find(toolchain) == null)
                {
                    // nothing to compile with: offer to provide a toolchain (the host asks the user: install, change, cancel)
                    var provided = ToolchainProvider.Require(toolchain, "A native build translates the program to C++ and compiles it with a C++ toolchain.");
                    if (provided == null)
                    {
                        string notFound = $"The compiler '{toolchain.EffectiveCompiler}' was not found. Install it, name another one in the toolchain of {NativeConfig.FileName}, or build with the toolchain 'files' and compile the sources yourself.";
                        return new NativeBuildResult(false, exe, notFound, new[] { new Diagnostic(0, notFound, DiagnosticSeverity.Error, ErrorCatalog.NativeConfiguration) });
                    }
                    toolchain = provided;
                }
                bool ok = true;
                var sourceCommands = BuildCommands(toolchain, target, cppFile, nativesFile, workDir, exe, gui).ToList();
                string? mapFile = null;
                if (config.Stats && sourceCommands.Count > 0 && NativeStats.MapOptions(toolchain.EffectiveKind, NativeStats.MapFileOf(exe)) is { } mapOptions)
                {
                    mapFile = NativeStats.MapFileOf(exe);
                    var (lastExe, lastArgs) = sourceCommands[^1];
                    sourceCommands[^1] = (lastExe, lastArgs + " " + mapOptions);
                }
                for (int c = 0; c < sourceCommands.Count; c++)
                {
                    var (exeName, arguments) = sourceCommands[c];
                    // (one command compiles and links the C; with natives in C++ the C is compiled first, the second command links)
                    if (c == sourceCommands.Count - 1) BuildMonitor.Step(BuildPhase.Link, sourceCommands.Count == 1 ? "Compiling and linking the program (C)" : "Compiling the natives (C++) and linking the program", c, sourceCommands.Count);
                    else BuildMonitor.Step(BuildPhase.CompileC, "Compiling the program (C)", c, sourceCommands.Count);
                    log.AppendLine($"{exeName} {arguments}");
                    var (stepOk, text) = Run(exeName, arguments, workDir);
                    log.Append(text);
                    if (!(ok = stepOk)) break;
                }
                bool built = ok && File.Exists(exe);
                if (built) ApplyPeMetadata(exe, program, target, log);
                if (built && config.Stats) WriteStats(target, exe, workDir, cppFile, mapFile, program, log);
                if (built && ToolchainDrivers.AfterLink(toolchain, target, exe) is { } after) log.AppendLine(after);   // (a target without an operating system: its disk image)
                return new NativeBuildResult(built, exe, log.ToString(), built ? null : new[] { new Diagnostic(0, "The C++ compiler of the native build reported errors (see the build log).", DiagnosticSeverity.Error, ErrorCatalog.NativeToolchain) });
            }
            finally
            {
                if (!keepSources) try { Directory.Delete(workDir, true); } catch (IOException) { }
            }
        }

        /// <summary>Linkage "static" (ROADMAP 9, docs/spec/NATIVE_BACKEND.md "Getrennte Übersetzung"): every library module (the prelude, each import, each library of the project) is a
        /// static library in the user-wide cache, built once for its content and the switches of the program; the program's own C is compiled and linked with them. A module
        /// that cannot be built on its own yet: the build falls back to linkage "source" and says so.</summary>
        private static NativeBuildResult BuildStatic(IReadOnlyList<string> sources, NativeConfig config, TargetProfile target, ToolchainDef toolchain, string output,
            VmExecutionMode? mode, int? floatWidth, bool keepSources, string? basePath, IReadOnlyList<string>? defines, fire.Projects.BuildPlan? plan, IReadOnlyList<string?>? sourcePaths,
            bool shared = false)
        {
            var log = new StringBuilder();
            NativeBuildResult AsSource(string? why)
            {
                string? linkage = config.Linkage;
                config.Linkage = "source";
                try
                {
                    var r = Build(sources, config, target, toolchain, output, mode, floatWidth, keepSources, basePath, defines, plan, sourcePaths);
                    return why == null ? r : r with { Log = why + "\n" + r.Log };
                }
                finally { config.Linkage = linkage; }
            }
            if (ToolchainDetector.Find(toolchain) == null) return AsSource(null);   // (no compiler: the normal build offers one)
            var linked = new Linker { BasePath = basePath, Engine = "native", ToolchainKind = toolchain.EffectiveKind, Defines = defines, Plan = plan, SourcePaths = plan?.SourcePaths.Cast<string?>().ToList() ?? sourcePaths, GlobalJobsDefault = config.GlobalJobsValue, Optimize = OptimizeOf(config, plan, target) }.CompileAndLink(sources, null, null, mode, floatWidth, target);
            StaticBuildPlan staticPlan;
            var watch = Stopwatch.StartNew();
            BuildMonitor.Step(BuildPhase.GenerateC, "Generating the C of the program and its library modules");
            try { staticPlan = CppGenerator.GenerateStatic(linked, target, ToolchainStamp(toolchain), StrippingOf(config, plan), shared: shared, stats: config.Stats && !shared); }
            catch (NativeNotSupportedException ex) { return AsSource($"Linkage \"static\" is not possible for this program ({ex.Message}): built with linkage \"source\"."); }
            log.Append(staticPlan.Log);
            log.AppendLine($"generated in {watch.ElapsedMilliseconds} ms");
            var program = new NativeProgram(staticPlan.ProgramC, staticPlan.Natives, linked.GuiSubsystem, linked.VersionInfo, linked.IconPath) { Funcs = staticPlan.Funcs };
            string? configDirectory = config.Path == null ? null : Path.GetDirectoryName(config.Path);
            string workDir = keepSources ? Path.GetFullPath(output) + ".src" : Path.Combine(Path.GetTempPath(), "fire-native-" + Guid.NewGuid().ToString("N"));
            try
            {
                string cFile = WriteFiles(workDir, program.C, target, SourceName, configDirectory, program.Natives);
                string? nativesFile = program.Natives != null ? Path.Combine(workDir, NativesName) : null;
                string exe = Path.GetFullPath(output);
                // (Windows: DLLs - the runtime one of them, see CppGenerator "linkage shared on Windows")
                bool dll = shared && target.Native.Platform == "windows";
                string ext = dll ? ".dll" : shared ? (OperatingSystem.IsMacOS() ? ".dylib" : ".so") : toolchain.EffectiveKind == "msvc" ? ".lib" : ".a";
                // the libraries that are not in the cache, side by side: each its C (the generator wrote it there) to an object file, that into the library, then its interface
                var missing = staticPlan.Libraries.Where(l => !l.Cached).ToList();
                var logs = new string[missing.Count];
                var failed = new string?[missing.Count];
                var objects = new string[missing.Count];
                var monitor = BuildMonitor.Current;
                string Modules(int done) => $"Compiling the library modules (C): {done}/{missing.Count}";
                if (missing.Count > 0) { monitor?.Token.ThrowIfCancellationRequested(); monitor?.BeginCount(BuildPhase.CompileC, Modules(0), missing.Count); }
                // (the temporary files a library is made in, next to where it goes in the cache: a cancelled or failed build leaves none of them behind)
                var temporaries = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
                Parallel.For(0, missing.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, i =>
                {
                    if (monitor?.IsCancellationRequested == true) return;
                    try { BuildLibrary(i); }
                    catch (OperationCanceledException) { logs[i] ??= ""; }
                    finally
                    {
                        if (temporaries.TryRemove(i, out var temp)) try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        if (monitor?.IsCancellationRequested == false && failed[i] == null) monitor.Count(BuildPhase.CompileC, missing.Count, Modules);
                    }
                });
                void BuildLibrary(int i)
                {
                    var library = missing[i];
                    var libraryLog = new StringBuilder();
                    var libraryWatch = Stopwatch.StartNew();
                    string obj = Path.Combine(workDir, library.LibraryName + (toolchain.EffectiveKind == "msvc" ? ".obj" : ".o"));
                    if (dll)
                    {
                        // a DLL: here only its object file - it is linked below, after the DLLs it imports from (in the order of the plan)
                        var compileDll = CompilerCommand(toolchain, target, library.CFile, workDir, obj, language: "c", compileOnly: true);
                        libraryLog.AppendLine($"{compileDll.Executable} {compileDll.Arguments}");
                        var (dllOk, dllText) = Run(compileDll.Executable, compileDll.Arguments, workDir);
                        libraryLog.Append(dllText);
                        if (!dllOk) failed[i] = $"The C compiler reported errors in the library module '{library.Module}' (see the build log).";
                        else { objects[i] = obj; libraryLog.AppendLine($"module {library.Module}: compiled in {libraryWatch.ElapsedMilliseconds} ms"); }
                        logs[i] = libraryLog.ToString();
                        return;
                    }
                    if (shared)
                    {
                        // a shared library: position-independent, its own name inside (the program asks for it by that name next to itself); what it needs of the program
                        // (the runtime's state, the tables) the program exports (-rdynamic)
                        string so = library.LibraryFile(ext);
                        string soTemp = Path.Combine(library.Directory, Guid.NewGuid().ToString("N") + ext);
                        temporaries[i] = soTemp;
                        var link = CompilerCommand(toolchain, target, library.CFile, workDir, soTemp, sharedLibrary: true, language: "c");
                        string soName = OperatingSystem.IsMacOS() ? $" -Wl,-install_name,@rpath/{Path.GetFileName(so)} -undefined dynamic_lookup" : $" -Wl,-soname,{Path.GetFileName(so)}";
                        libraryLog.AppendLine($"{link.Executable} {link.Arguments}{soName} -lm");
                        var (soOk, soText) = Run(link.Executable, link.Arguments + soName + " -lm", workDir);
                        libraryLog.Append(soText);
                        if (!soOk || !File.Exists(soTemp)) { failed[i] = $"The C compiler reported errors in the library module '{library.Module}' (see the build log)."; logs[i] = libraryLog.ToString(); return; }
                        File.Move(soTemp, so, true);
                        CppGenerator.CommitStatic(library);
                        libraryLog.AppendLine($"module {library.Module}: built in {libraryWatch.ElapsedMilliseconds} ms -> {so}");
                        logs[i] = libraryLog.ToString();
                        return;
                    }
                    var compile = CompilerCommand(toolchain, target, library.CFile, workDir, obj, language: "c", compileOnly: true, sections: true);
                    libraryLog.AppendLine($"{compile.Executable} {compile.Arguments}");
                    var (ok, text) = Run(compile.Executable, compile.Arguments, workDir);
                    libraryLog.Append(text);
                    if (!ok) { failed[i] = $"The C compiler reported errors in the library module '{library.Module}' (see the build log)."; logs[i] = libraryLog.ToString(); return; }
                    string lib = library.LibraryFile(ext);
                    string temp = Path.Combine(library.Directory, Guid.NewGuid().ToString("N") + ext);
                    temporaries[i] = temp;
                    var archive = ArchiveCommand(toolchain, temp, obj);
                    libraryLog.AppendLine($"{archive.Executable} {archive.Arguments}");
                    (ok, text) = Run(archive.Executable, archive.Arguments, workDir);
                    libraryLog.Append(text);
                    if (!ok || !File.Exists(temp)) { failed[i] = $"The library of the module '{library.Module}' could not be made (see the build log)."; logs[i] = libraryLog.ToString(); return; }
                    File.Move(temp, lib, true);
                    CppGenerator.CommitStatic(library);
                    libraryLog.AppendLine($"module {library.Module}: built in {libraryWatch.ElapsedMilliseconds} ms -> {lib}");
                    logs[i] = libraryLog.ToString();
                }
                monitor?.Token.ThrowIfCancellationRequested();
                foreach (var text in logs) log.Append(text);
                if (failed.FirstOrDefault(f => f != null) is { } failure)
                    return new NativeBuildResult(false, exe, log.ToString(), new[] { new Diagnostic(0, failure, DiagnosticSeverity.Error, ErrorCatalog.NativeToolchain) });
                if (dll && LinkDlls(toolchain, target, staticPlan.Libraries, missing, objects, cFile, workDir, log) is { } dllFailure)
                    return new NativeBuildResult(false, exe, log.ToString(), new[] { new Diagnostic(0, dllFailure, DiagnosticSeverity.Error, ErrorCatalog.NativeToolchain) });
                // (a library before the ones it needs: the linker resolves in that order)
                var libraries = staticPlan.Libraries.AsEnumerable().Reverse().Select(l => l.LibraryFile(ext)).ToList();
                string? statsMap = config.Stats && !shared && NativeStats.MapOptions(toolchain.EffectiveKind, "x") != null ? NativeStats.MapFileOf(exe) : null;
                bool linkedOk = true;
                watch.Restart();
                var commands = BuildCommands(toolchain, target, cFile, nativesFile, workDir, exe, program.Gui, libraries);
                for (int c = 0; c < commands.Count; c++)
                {
                    var (exeName, arguments) = commands[c];
                    BuildMonitor.Step(BuildPhase.Link, c == commands.Count - 1 ? "Compiling the program (C) and linking it with its libraries" : "Compiling the program (C)", c, commands.Count);
                    // (shared: the program exports what the libraries need of it, and finds them next to itself)
                    if (shared && !dll && c == commands.Count - 1) arguments += OperatingSystem.IsMacOS() ? " -Wl,-export_dynamic -Wl,-rpath,@executable_path" : " -rdynamic -Wl,-rpath,$ORIGIN";
                    // (`--stats`: the map of the program - the code of the library modules is counted by the library it comes from)
                    if (statsMap != null) arguments += c == commands.Count - 1 ? " " + NativeStats.MapOptions(toolchain.EffectiveKind, statsMap) : " -ffunction-sections";
                    log.AppendLine($"{exeName} {arguments}");
                    var (stepOk, text) = Run(exeName, arguments, workDir);
                    log.Append(text);
                    if (!(linkedOk = stepOk)) break;
                }
                log.AppendLine($"program built in {watch.ElapsedMilliseconds} ms");
                bool built = linkedOk && File.Exists(exe);
                if (built) ApplyPeMetadata(exe, program, target, log);
                if (built && config.Stats)
                {
                    if (shared) log.AppendLine("--stats: not with linkage \"shared\" (the libraries are not in the program) - use static or source.");
                    else WriteStats(target, exe, workDir, cFile, statsMap, program, log);
                }
                if (built && ToolchainDrivers.AfterLink(toolchain, target, exe) is { } after) log.AppendLine(after);
                // (shared: the libraries next to the program - it finds them there, wherever the cache is)
                if (built && shared)
                {
                    foreach (var library in staticPlan.Libraries)
                    {
                        string next = Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileName(library.LibraryFile(ext)));
                        File.Copy(library.LibraryFile(ext), next, true);
                        log.AppendLine($"{Path.GetFileName(next)} next to the program");
                    }
                    // (what a plugin built for this program needs to know: `--linkage plugin --host <program>`)
                    if (staticPlan.Host is { } host)
                    {
                        foreach (var m in host.Modules) m.Library += ext;
                        if (host.Runtime != null) host.Runtime += ext;
                        File.WriteAllText(HostFileOf(exe), host.ToJson());
                    }
                }
                return new NativeBuildResult(built, exe, log.ToString(), built ? null : new[] { new Diagnostic(0, "The C++ compiler of the native build reported errors (see the build log).", DiagnosticSeverity.Error, ErrorCatalog.NativeToolchain) });
            }
            finally
            {
                if (!keepSources) try { Directory.Delete(workDir, true); } catch (IOException) { }
            }
        }

        /// <summary>Linkage "shared" on Windows: links the DLLs that are not in the cache from their object files, in the order of the plan (the runtime first, a module after
        /// the modules it needs): each against the DLLs before it (a DLL imports by file name), exporting everything, with the system libraries of the program. Null: done.</summary>
        private static string? LinkDlls(ToolchainDef toolchain, TargetProfile target, IReadOnlyList<StaticLibrary> all, List<StaticLibrary> missing, string[] objects, string programFile, string workDir, StringBuilder log)
        {
            string cc = ToolchainDetector.FindC(toolchain) ?? toolchain.EffectiveCCompiler;
            var libs = (toolchain.Libs ?? new()).Concat(target.Native.LinkLibs).ToList();
            foreach (string line in File.ReadLines(programFile))
                if (line.StartsWith("// fire-link: ", StringComparison.Ordinal) && "-l" + line.Substring("// fire-link: ".Length).Trim() is var flag && !libs.Contains(flag)) libs.Add(flag);
            var before = new List<string>();
            foreach (var library in all)
            {
                string file = library.LibraryFile(".dll");
                int i = missing.IndexOf(library);
                if (i >= 0)
                {
                    var watch = Stopwatch.StartNew();
                    // (a DLL carries the name it is linked as: the others import it by that name - its own name in a folder of its own, then moved)
                    string tempDir = Path.Combine(library.Directory, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    string temp = Path.Combine(tempDir, Path.GetFileName(file));
                    string arguments = $"-shared -static-libgcc -o \"{temp}\" \"{objects[i]}\"{string.Concat(before.Select(b => $" \"{b}\""))} -Wl,--export-all-symbols -s {string.Join(" ", libs)}";
                    log.AppendLine($"{cc} {arguments}");
                    BuildMonitor.Step(BuildPhase.Link, $"Linking the DLL of the module {library.Module}", i, missing.Count);
                    bool ok;
                    string text;
                    try { (ok, text) = Run(cc, arguments, workDir); }
                    catch (OperationCanceledException) { try { Directory.Delete(tempDir, true); } catch (IOException) { } throw; }
                    log.Append(text);
                    if (!ok || !File.Exists(temp)) { try { Directory.Delete(tempDir, true); } catch (IOException) { } return $"The DLL of the module '{library.Module}' could not be linked (see the build log)."; }
                    File.Move(temp, file, true);
                    try { Directory.Delete(tempDir, true); } catch (IOException) { }
                    CppGenerator.CommitStatic(library);
                    log.AppendLine($"module {library.Module}: built in {watch.ElapsedMilliseconds} ms -> {file}");
                }
                before.Add(file);
            }
            return null;
        }

        /// <summary>Linkage "static" with a toolchain that only writes files (an ESP-IDF component, docs/spec/NATIVE_BACKEND.md): the program's C and the C of every library module
        /// (from the cache, generated once) as translation units of their own; the build system of the board (idf.py) compiles them into the component's library.</summary>
        private static NativeBuildResult WriteStaticFiles(IReadOnlyList<string> sources, NativeConfig config, TargetProfile target, ToolchainDef toolchain, string output,
            VmExecutionMode? mode, int? floatWidth, string? basePath, IReadOnlyList<string>? defines, fire.Projects.BuildPlan? plan, IReadOnlyList<string?>? sourcePaths)
        {
            var linked = new Linker { BasePath = basePath, Engine = "native", ToolchainKind = toolchain.EffectiveKind, Defines = defines, Plan = plan, SourcePaths = plan?.SourcePaths.Cast<string?>().ToList() ?? sourcePaths, GlobalJobsDefault = config.GlobalJobsValue, Optimize = OptimizeOf(config, plan, target) }.CompileAndLink(sources, null, null, mode, floatWidth, target);
            StaticBuildPlan staticPlan;
            BuildMonitor.Step(BuildPhase.GenerateC, "Generating the C of the program and its library modules");
            try { staticPlan = CppGenerator.GenerateStatic(linked, target, $"files|{toolchain.Layout}", StrippingOf(config, plan)); }
            catch (NativeNotSupportedException ex)
            {
                string? linkage = config.Linkage;
                config.Linkage = "source";
                try { var r = Build(sources, config, target, toolchain, output, mode, floatWidth, false, basePath, defines, plan, sourcePaths); return r with { Log = $"Linkage \"static\" is not possible for this program ({ex.Message}): written with linkage \"source\".\n" + r.Log }; }
                finally { config.Linkage = linkage; }
            }
            string directory = Path.GetFullPath(output);
            string? configDirectory = config.Path == null ? null : Path.GetDirectoryName(config.Path);
            WriteFiles(directory, staticPlan.ProgramC, target, SourceName, configDirectory, staticPlan.Natives);
            var files = new List<string> { SourceName };
            if (staticPlan.Natives != null) files.Add(NativesName);
            foreach (var library in staticPlan.Libraries)
            {
                string name = library.LibraryName + ".c";
                File.Copy(library.CFile, Path.Combine(directory, name), true);
                CppGenerator.CommitStatic(library);
                files.Add(name);
            }
            foreach (var stale in Directory.EnumerateFiles(directory, "fire_m*.c").Where(f => !files.Contains(Path.GetFileName(f)))) File.Delete(stale);
            var log = new StringBuilder(staticPlan.Log);
            if (toolchain.Layout == "idf-component")
                File.WriteAllText(Path.Combine(directory, "CMakeLists.txt"),
                    $"# Generated by fire.Native for target '{target.Name}' with linkage \"static\": the program and its library modules as an ESP-IDF component (call {target.Native.Entry.Name} from the application).\n" +
                    $"idf_component_register(SRCS {string.Join(" ", files.Select(f => $"\"{f}\""))}\n                       INCLUDE_DIRS \".\")\n" +
                    "target_compile_options(${COMPONENT_LIB} PRIVATE $<$<COMPILE_LANGUAGE:C>:-std=gnu11> $<$<COMPILE_LANGUAGE:CXX>:-std=gnu++17>)\n");
            log.AppendLine($"{directory}: {string.Join(", ", files)}, fire_rt.h and platform/{target.Native.Platform}/ for {target.Name}; the entry point is {target.Native.Entry.Name}");
            if (!string.IsNullOrWhiteSpace(toolchain.BuildCommand))
            {
                var (ok, text) = RunCommand(toolchain.BuildCommand!, directory);
                log.Append(text);
                return new NativeBuildResult(ok, directory, log.ToString());
            }
            return new NativeBuildResult(true, directory, log.ToString());
        }

        /// <summary>What identifies the compiler of a toolchain for the cache of static libraries: its kind, the program (path, size, time), the C standard, optimization and arguments.</summary>
        public static string ToolchainStamp(ToolchainDef toolchain)
        {
            string compiler = ToolchainDetector.FindC(toolchain) ?? toolchain.EffectiveCCompiler;
            string file = "";
            try { var info = new FileInfo(compiler); if (info.Exists) file = $"{info.Length}@{info.LastWriteTimeUtc.Ticks}"; } catch (Exception) { }
            return string.Join("|", "sections", toolchain.EffectiveKind, compiler, file, toolchain.CStd ?? "", toolchain.Optimization ?? "", string.Join(" ", toolchain.Args ?? new()), string.Join(" ", toolchain.IncludeDirs ?? new()));
        }

        /// <summary>The command that puts an object file into a static library: `ar rcs` next to the compiler (`x86_64-w64-mingw32-ar`, `emar`, `llvm-ar`), `tcc -ar`, `lib` of msvc.</summary>
        public static (string Executable, string Arguments) ArchiveCommand(ToolchainDef toolchain, string library, string obj)
        {
            string compiler = ToolchainDetector.FindC(toolchain) ?? toolchain.EffectiveCCompiler;
            switch (toolchain.EffectiveKind)
            {
                case "tcc": return (compiler, $"-ar rcs \"{library}\" \"{obj}\"");
                case "msvc": return (Path.Combine(Path.GetDirectoryName(compiler) ?? "", "lib.exe"), $"/nologo /OUT:\"{library}\" \"{obj}\"");
            }
            string dir = Path.GetDirectoryName(compiler) ?? "";
            string name = Path.GetFileName(compiler);
            string exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ".exe" : "";
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(name), @"^(.*?)(gcc|cc|clang|emcc)(-[0-9.]+)?$");
            var candidates = new List<string>();
            if (m.Success)
            {
                string prefix = m.Groups[1].Value;
                if (m.Groups[2].Value == "emcc") candidates.Add("emar");
                if (m.Groups[2].Value == "clang") candidates.Add(prefix + "llvm-ar" + m.Groups[3].Value);
                candidates.Add(prefix + "ar");
                candidates.Add(prefix + "gcc-ar");
            }
            candidates.Add("ar");
            foreach (var c in candidates)
            {
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, c + exe))) return (Path.Combine(dir, c + exe), $"rcs \"{library}\" \"{obj}\"");
                if (ToolchainSetup.FindProgram(c) is { } found) return (found, $"rcs \"{library}\" \"{obj}\"");
            }
            return ("ar", $"rcs \"{library}\" \"{obj}\"");
        }

        /// <summary>Like <see cref="Build"/>, but what goes wrong (an error in the script, something the native backend cannot translate yet, a bad configuration) is in the result's log.</summary>
        /// A cancelled build (<paramref name="monitor"/>) is a result with <see cref="NativeBuildResult.Cancelled"/>: its tools were ended, and a program it had begun to write is deleted
        /// (a half-linked file would look like a program).</summary>
        public static NativeBuildResult BuildSafe(IReadOnlyList<string> sources, NativeConfig config, TargetProfile target, ToolchainDef toolchain, string output,
            VmExecutionMode? mode = null, int? floatWidth = null, bool keepSources = false, string? basePath = null, IReadOnlyList<string>? defines = null, fire.Projects.BuildPlan? plan = null,
            BuildMonitor? monitor = null)
        {
            using var watching = BuildMonitor.Use(monitor);
            // the warnings of the code (`warn 123: "text"`) are collected while the parser reads it
            var warnings = new DiagnosticCollector();
            using var collecting = warnings.Begin();
            NativeBuildResult Failed(string log, IEnumerable<Diagnostic> errors) =>
                new(false, output, log, errors.Concat(warnings.Items).ToList());
            var started = DateTime.UtcNow.AddSeconds(-1);
            try
            {
                var result = Build(sources, config, target, toolchain, output, mode, floatWidth, keepSources, basePath, defines, plan);
                return result with { Diagnostics = (result.Diagnostics ?? Array.Empty<Diagnostic>()).Concat(warnings.Items).ToList() };
            }
            catch (OperationCanceledException)
            {
                string removed = DeletePartialOutput(output, started) ? $" ({Path.GetFileName(output)}, which it had begun to write, is deleted)" : "";
                return new NativeBuildResult(false, output, $"Build cancelled{removed}.\n") { Cancelled = true };
            }
            catch (NativeNotSupportedException ex) { return Failed("Not supported by the native backend yet: " + ex.Message, CompileDiagnostics.Of(ex)); }
            catch (NativeConfigException ex) { return Failed(ex.Message, new[] { new Diagnostic(0, ex.Message, DiagnosticSeverity.Error, ErrorCatalog.NativeConfiguration) }); }
            catch (Exception ex) when (CommandLineRunner.IsCompileError(ex)) { return Failed(CompileErrors.Describe(ex), CompileDiagnostics.Of(ex)); }
        }

        /// <summary>After a cancelled build: the program file when this build has written it (a linker that was stopped leaves a file that is not a program). A folder (the toolchain
        /// "files") stays.</summary>
        private static bool DeletePartialOutput(string output, DateTime started)
        {
            try
            {
                string path = Path.GetFullPath(output);
                if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < started) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
        }

        /// <summary>The commands that build the program: the C file alone (one call of the C compiler), or - with natives in C++ - the C file compiled to an object
        /// file with the C compiler, then the C++ file compiled with the C++ compiler and linked with it (the C++ compiler links, it brings its runtime library).</summary>
        public static IReadOnlyList<(string Executable, string Arguments)> BuildCommands(ToolchainDef toolchain, TargetProfile target, string cFile, string? nativesFile, string workDir, string exe, bool gui = false,
            IReadOnlyList<string>? libraries = null)
        {
            if (nativesFile == null || toolchain.EffectiveKind == "custom")
                return ToolchainDrivers.PreCommands(toolchain, target, workDir)   // (Open Watcom: the startup code of a target without an operating system)
                    .Append(CompilerCommand(toolchain, target, cFile, workDir, exe, gui: gui, language: "c", natives: nativesFile, extraInputs: libraries)).ToList();
            if (!toolchain.CompilesCpp)
                throw new NativeConfigException($"The program uses natives in C++ ({NativesInCpp(nativesFile)}): the toolchain '{toolchain.EffectiveKind}' compiles only C. Build it with gcc, clang or msvc (--toolchain).");
            string obj = Path.Combine(workDir, toolchain.EffectiveKind == "msvc" ? "fire_program.obj" : "fire_program.o");
            return new[]
            {
                CompilerCommand(toolchain, target, cFile, workDir, obj, gui: gui, language: "c", compileOnly: true),
                CompilerCommand(toolchain, target, nativesFile, workDir, exe, gui: gui, language: "c++", extraInputs: new[] { obj }.Concat(libraries ?? Array.Empty<string>()), programFile: cFile),
            };
        }

        /// <summary>What the natives in C++ of a program come from (the packages and bridges named in the file), for a message.</summary>
        private static string NativesInCpp(string nativesFile)
        {
            try
            {
                var names = File.ReadLines(nativesFile).Where(l => l.StartsWith("// ---- package ", StringComparison.Ordinal))
                    .Select(l => l.Substring("// ---- package ".Length).Split(' ')[0]).Distinct().ToList();
                return names.Count > 0 ? string.Join(", ", names) : "a bridge of the compiler";
            }
            catch (IOException) { return "a bridge of the compiler"; }
        }

        /// <summary>The compiler command line for the generated file. <paramref name="language"/>: "c++" (the C++ compiler; a library for the VM, the natives) or "c" (the C
        /// compiler, the program). <paramref name="compileOnly"/>: an object file. <paramref name="extraInputs"/>: object files that are linked too. The libraries the program asks
        /// for (`// fire-link: ...`) are read from <paramref name="programFile"/> (default: the compiled file).</summary>
        public static (string Executable, string Arguments) CompilerCommand(ToolchainDef toolchain, TargetProfile target, string cppFile, string includeDir, string exe, bool sharedLibrary = false, IEnumerable<string>? extraIncludeDirs = null, bool gui = false,
            string language = "c++", bool compileOnly = false, IEnumerable<string>? extraInputs = null, string? programFile = null, string? natives = null, bool sections = false)
        {
            string kind = toolchain.EffectiveKind;
            bool isC = language == "c";
            string std = isC ? toolchain.CStd ?? "c11" : toolchain.Std ?? "c++17";
            var libs = (toolchain.Libs ?? new()).Concat(target.Native.LinkLibs).ToList();
            // the generated program asks for libraries it needs (`// fire-link: dl`, those of the packages): -ldl, dl.lib with msvc
            string linkSource = programFile ?? cppFile;
            if (!compileOnly && File.Exists(linkSource))
                foreach (string line in File.ReadLines(linkSource))
                    if (line.StartsWith("// fire-link: ", StringComparison.Ordinal))
                    {
                        string lib = line.Substring("// fire-link: ".Length).Trim();
                        // (a framework of macOS: -framework Name, two arguments that stay together)
                        string flag = lib.StartsWith("framework:", StringComparison.Ordinal) ? "-framework " + lib.Substring("framework:".Length) : kind == "msvc" ? lib + ".lib" : "-l" + lib;
                        if (!libs.Contains(flag)) libs.Add(flag);
                    }
            // a debug build (mode debug, `#define FIRE_DEBUG 1` at the top of the program): no optimization, debug information, not stripped - for the debugger of spark
            // and for gdb/lldb (docs/spec/NATIVE_BACKEND.md "Debug-Builds") (a debug plugin too: its lines are debugged in the program)
            bool debugBuild = (!sharedLibrary || Path.GetFileName(linkSource) == "fire_plugin.c") && File.Exists(linkSource) && File.ReadLines(linkSource).Take(60).Any(l => l.StartsWith("#define FIRE_DEBUG 1", StringComparison.Ordinal));
            var includes = (toolchain.IncludeDirs ?? new()).Concat(extraIncludeDirs ?? Array.Empty<string>()).ToList();
            // a toolchain with a driver of its own (Toolchains/: Open Watcom, ...) builds its command line itself
            if (ToolchainDrivers.For(kind) is { } driver)
                return driver.Command(new CompileRequest(toolchain, target, cppFile, includeDir, exe, includes, libs, compileOnly, (extraInputs ?? Array.Empty<string>()).ToList(), debugBuild));
            var libDirs = new List<string>();
            if (kind == "custom")
            {
                string template = toolchain.Command ?? throw new NativeConfigException("a custom toolchain needs a command");
                string command = template.Replace("{cpp}", $"\"{cppFile}\"").Replace("{c}", $"\"{cppFile}\"").Replace("{natives}", natives != null ? $"\"{natives}\"" : "")
                    .Replace("{dir}", $"\"{includeDir}\"").Replace("{out}", $"\"{exe}\"")
                    .Replace("{args}", string.Join(" ", (toolchain.Args ?? new()).Concat(target.Native.CompileArgs)))
                    .Replace("{libs}", string.Join(" ", libs));
                int space = command.IndexOf(' ');
                return space < 0 ? (command, "") : (command.Substring(0, space), command.Substring(space + 1));
            }
            var args = new StringBuilder();
            if (kind == "msvc")
            {
                bool smallMsvc = IsSizeProfile(toolchain.Optimization);
                args.Append($"{(isC ? $"/std:{(std == "c11" ? "c11" : std)} /TC" : $"/std:{std}")} {(debugBuild ? "/Od /Zi" : smallMsvc ? "/O1 /Gy /Gw" : toolchain.Optimization ?? "/O2")} ");
                if (sharedLibrary) args.Append("/LD ");
                if (compileOnly) args.Append("/c ");
                foreach (string a in toolchain.Args ?? new()) args.Append(a).Append(' ');
                foreach (string a in target.Native.CompileArgs) args.Append(a).Append(' ');
                args.Append($"\"{cppFile}\" ");
                foreach (string input in extraInputs ?? Array.Empty<string>()) args.Append($"\"{input}\" ");
                args.Append($"/I\"{includeDir}\" ");
                foreach (string d in includes) args.Append($"/I\"{d}\" ");
                if (compileOnly) { args.Append($"/Fo:\"{exe}\""); return (ToolchainDetector.Find(toolchain) ?? toolchain.EffectiveCompiler, args.ToString()); }
                args.Append($"/Fe:\"{exe}\" /Fo:\"{Path.Combine(includeDir, isC ? "fire_program.obj" : "fire_natives.obj")}\"");
                foreach (string l in libs) args.Append(' ').Append(l);
                bool windowsGui = gui && !sharedLibrary && target.Name == "windows";
                if (smallMsvc && !sharedLibrary) args.Append(" /link /OPT:REF /OPT:ICF" + string.Concat(libDirs.Select(d => $" /LIBPATH:\"{d}\"")) + (windowsGui ? " /SUBSYSTEM:WINDOWS /ENTRY:mainCRTStartup" : ""));
                else if (libDirs.Count > 0 || windowsGui) args.Append(" /link").Append(string.Concat(libDirs.Select(d => $" /LIBPATH:\"{d}\"")));
                if (windowsGui) args.Append(" /SUBSYSTEM:WINDOWS /ENTRY:mainCRTStartup");   // no console window; the program still starts at main
            }
            else
            {
                bool tcc = kind == "tcc";
                bool small = IsSizeProfile(toolchain.Optimization) && !tcc && !debugBuild;   // (TinyCC keeps every function anyway: no sections to drop)
                args.Append($"-std={std} {(debugBuild ? "-O0 -g" : small ? "-Os -ffunction-sections -fdata-sections" : toolchain.Optimization ?? "-O2")} ");
                // a library of linkage "static" (ROADMAP 9): every function in its own section - the program's link drops what it does not reach
                if (sections && !small && !tcc && !debugBuild && kind != "emscripten") args.Append("-ffunction-sections -fdata-sections ");
                bool gcLibraries = !tcc && !debugBuild && (extraInputs ?? Array.Empty<string>()).Any(i => i.EndsWith(".a", StringComparison.OrdinalIgnoreCase));
                if (compileOnly) args.Append("-c ");
                // a library for the VM carries its C++ runtime (MinGW: no libgcc/libstdc++ DLLs next to it), so that it also runs on a machine without the toolchain
                if (sharedLibrary) args.Append(tcc ? "-shared " : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "-dynamiclib -fPIC " : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "-shared -static-libgcc -static-libstdc++ " : "-shared -fPIC ");
                foreach (string a in toolchain.Args ?? new()) args.Append(a).Append(' ');
                foreach (string a in target.Native.CompileArgs) args.Append(a).Append(' ');
                if (gui && !sharedLibrary && target.Name == "windows") args.Append(tcc ? "-Wl,-subsystem=windows " : "-mwindows ");   // no console window; the startup code still calls main
                args.Append($"\"{cppFile}\" ");
                foreach (string input in extraInputs ?? Array.Empty<string>()) args.Append($"\"{input}\" ");
                args.Append($"-I\"{includeDir}\" ");
                foreach (string d in includes) args.Append($"-I\"{d}\" ");
                args.Append($"-o \"{exe}\"");
                if (compileOnly && kind == "emscripten") foreach (string l in libs.Where(l => l.StartsWith("-s", StringComparison.Ordinal))) args.Append(' ').Append(l);
                if (compileOnly) return (isC ? ToolchainDetector.FindC(toolchain) ?? toolchain.EffectiveCCompiler : ToolchainDetector.Find(toolchain) ?? toolchain.EffectiveCompiler, args.ToString());
                // the C library of the program: libm (the math functions of C are in a library of their own on some systems)
                if (isC && !sharedLibrary && kind != "emscripten" && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !libs.Contains("-lm")) libs.Add("-lm");
                foreach (string d in libDirs) args.Append($" -L\"{d}\"");
                foreach (string l in libs) args.Append(' ').Append(l);
                // a program without symbols and debug information is about half the size; "size" lets the linker drop what nothing uses
                if (!sharedLibrary && kind != "emscripten")
                {
                    bool apple = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
                    if (small || gcLibraries) args.Append(apple ? " -Wl,-dead_strip" : " -Wl,--gc-sections");
                    if (toolchain.Strip != false && !debugBuild) args.Append(apple ? " -Wl,-x" : " -s");
                }
            }
            string exeName = isC && kind != "msvc" ? ToolchainDetector.FindC(toolchain) ?? toolchain.EffectiveCCompiler : ToolchainDetector.Find(toolchain) ?? toolchain.EffectiveCompiler;
            return (exeName, args.ToString());
        }

        /// <summary>Whether the optimisation setting asks for the small-program profile ("size", or -Os).</summary>
        private static bool IsSizeProfile(string? optimization) => optimization is not null && (optimization.Equals("size", StringComparison.OrdinalIgnoreCase) || optimization == "-Os");

        private static (bool Ok, string Text) RunCommand(string command, string workDir)
        {
            int space = command.IndexOf(' ');
            return Run(space < 0 ? command : command.Substring(0, space), space < 0 ? "" : command.Substring(space + 1), workDir);
        }

        /// <summary>Runs a tool of the build. With a <see cref="BuildMonitor.Current"/> monitor the command and every line of its output go to its log as they come, and a
        /// cancelled build ends the tool's whole process tree (<see cref="OperationCanceledException"/>).</summary>
        internal static (bool Ok, string Text) Run(string exe, string arguments, string workDir)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir };   // (CreateNoWindow: the console window of the compiler stays away)
                // a portable toolchain (w64devkit) finds its other programs through the PATH: its folder comes first
                if (Path.IsPathRooted(exe) && Path.GetDirectoryName(exe) is { Length: > 0 } toolDir)
                    psi.Environment["PATH"] = toolDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                ToolchainDrivers.PrepareEnvironment(exe, psi.Environment);   // (Open Watcom: WATCOM, INCLUDE)
                if (BuildMonitor.Current is { } monitor)
                {
                    monitor.Line($"{exe} {arguments}");
                    var (code, stdout, stderr) = BuildProcess.Run(psi, monitor.Line, monitor.Token);
                    return (code == 0, stdout + stderr);
                }
                using var p = Process.Start(psi)!;
                var err = p.StandardError.ReadToEndAsync();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode == 0, output + err.Result);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                return (false, $"Could not run '{exe}': {ex.Message}\n");
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (string dir in Directory.GetDirectories(source)) CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        /// <summary>Tries a toolchain: compiles a small C++ program (with the standard library and threads) for the host and runs it. The log says what was run and what came out.</summary>
        public static NativeBuildResult TestToolchain(ToolchainDef toolchain)
        {
            var log = new StringBuilder();
            if (toolchain.EffectiveKind == "files") return new NativeBuildResult(true, "", "The toolchain 'files' only writes the sources; there is nothing to test.");
            string work = Path.Combine(Path.GetTempPath(), "fire-toolchain-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(work);
                string cpp = Path.Combine(work, "test.cpp");
                File.WriteAllText(cpp, "#include <cstdio>\n#include <cmath>\n#include <string>\n#include <thread>\nint main() { std::string s = \"toolchain \"; std::thread t([&] { s += \"ok\"; }); t.join(); std::printf(\"%s %g\\n\", s.c_str(), std::sqrt(16.0)); return 0; }\n");
                string exe = Path.Combine(work, OperatingSystem.IsWindows() ? "test.exe" : "test");
                var (exeName, arguments) = CompilerCommand(toolchain, TargetProfile.Host, cpp, work, exe);
                if (toolchain.EffectiveKind != "custom" && ToolchainDetector.Find(toolchain) == null)
                    return new NativeBuildResult(false, exe, $"The compiler '{toolchain.EffectiveCompiler}' was not found.");
                log.AppendLine($"{exeName} {arguments}");
                var (ok, text) = Run(exeName, arguments, work);
                log.Append(text);
                if (!ok || !File.Exists(exe)) return new NativeBuildResult(false, exe, log.ToString());
                var (ran, output) = Run(exe, "", work);
                log.Append(output);
                return new NativeBuildResult(ran && output.Contains("toolchain ok 4"), exe, log.ToString());
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { }
            }
        }

        /// <summary>The name of the program that a native build produces by default (<c>out</c>, <c>out.exe</c> on Windows).</summary>
        public static string DefaultOutput(TargetProfile target) => "out" + target.ProgramExtension;
    }
}
