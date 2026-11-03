using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using fire.Native;
using fire.Runtime;

namespace fire.Compiler
{
    /// <summary>The result of a native build: where the program (or, for a toolchain that only writes files, the folder) is, what the tools said, and the errors and warnings of the compile
    /// (<see cref="Diagnostic"/>, with codes; <see cref="NativeBuilder.BuildSafe"/> fills them).</summary>
    public sealed record NativeBuildResult(bool Ok, string Output, string Log, IReadOnlyList<Diagnostic>? Diagnostics = null);

    /// <summary>
    /// Builds a program natively: translate it to C++ (<see cref="CppGenerator"/>) for a target, write the runtime and the platform package of the target next to it,
    /// and let the toolchain of the configuration (<see cref="NativeConfig"/>) turn it into a program - or only write the files (and build files) of a project.
    /// </summary>
    public static class NativeBuilder
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
        /// <param name="chooseSdl">Which SDL a window is built with when the target does not say (<see cref="ChooseSdl"/>; null: SDL2).</param>
        public static NativeProgram GenerateLinked(IReadOnlyList<string> sources, TargetProfile target, VmExecutionMode? mode = null, int? floatWidth = null, string? basePath = null, IReadOnlyList<string>? defines = null, fire.Projects.BuildPlan? plan = null, IReadOnlyList<string?>? sourcePaths = null,
            Func<int>? chooseSdl = null, TrimMode? trim = null)
        {
            var linked = new Linker { BasePath = basePath, Engine = "native", Defines = defines, Plan = plan, SourcePaths = plan?.SourcePaths.Cast<string?>().ToList() ?? sourcePaths }.CompileAndLink(sources, null, null, mode, floatWidth, target);
            var generated = CppGenerator.GenerateSources(linked, target, chooseSdl, trim);
            return new NativeProgram(generated.Program, generated.Natives, linked.GuiSubsystem, linked.VersionInfo, linked.IconPath);
        }

        /// <summary>The generated C (and the natives in C++, null: none) and what the executable needs besides: the subsystem, the version info and the icon.</summary>
        public sealed record NativeProgram(string C, string? Natives, bool Gui, fire.Utilities.PeVersionInfo? VersionInfo, string? IconPath);

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

        /// <summary>Translates and builds. <paramref name="output"/> is the program, or the folder for a toolchain that only writes files.</summary>
        public static NativeBuildResult Build(IReadOnlyList<string> sources, NativeConfig config, TargetProfile target, ToolchainDef toolchain, string output,
            VmExecutionMode? mode = null, int? floatWidth = null, bool keepSources = false, string? basePath = null, IReadOnlyList<string>? defines = null, fire.Projects.BuildPlan? plan = null,
            IReadOnlyList<string?>? sourcePaths = null)
        {
            // the SDL of a window: what the environment, the target or the toolchain asks for, else SDL3 when the toolchain can build it (asked only for a program with a window)
            target = WithRequestedSdl(target, toolchain);
            Func<int>? chooseSdl = toolchain.EffectiveKind is "files" or "custom" or "emscripten" ? null : () => ChooseSdl(toolchain, target);
            if (string.Equals(config.Linkage, "static", StringComparison.OrdinalIgnoreCase) && toolchain.EffectiveKind is not ("files" or "custom"))
                return BuildStatic(sources, config, target, toolchain, output, mode, floatWidth, keepSources, basePath, defines, plan, sourcePaths, chooseSdl);
            if (string.Equals(config.Linkage, "static", StringComparison.OrdinalIgnoreCase) && toolchain.EffectiveKind == "files")
                return WriteStaticFiles(sources, config, target, toolchain, output, mode, floatWidth, basePath, defines, plan, sourcePaths);
            var program = GenerateLinked(sources, target, mode, floatWidth, basePath, defines, plan, sourcePaths, chooseSdl, config.Trim);
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
                // a window needs the development files of SDL (2 or 3): look for them, offer to download them (Windows)
                if (ToolchainProvider.RequireLibraries(toolchain, cppFile) is { } missingLibrary)
                    return new NativeBuildResult(false, exe, missingLibrary, new[] { new Diagnostic(0, missingLibrary, DiagnosticSeverity.Error, ErrorCatalog.NativeConfiguration) });
                bool ok = true;
                foreach (var (exeName, arguments) in BuildCommands(toolchain, target, cppFile, nativesFile, workDir, exe, gui))
                {
                    log.AppendLine($"{exeName} {arguments}");
                    var (stepOk, text) = Run(exeName, arguments, workDir);
                    log.Append(text);
                    if (!(ok = stepOk)) break;
                }
                bool built = ok && File.Exists(exe);
                if (built) ApplyPeMetadata(exe, program, target, log);
                // a Windows program with a window needs SDL2.dll (SDL3.dll) next to it
                int sdlVersion = SdlSetup.RequiredVersion(cppFile);
                if (built && toolchain.EffectiveKind != "emscripten" && sdlVersion > 0 && SdlSetup.CopyRuntimeNextTo(exe, SdlSetup.Locate(toolchain, sdlVersion))) log.AppendLine($"SDL{sdlVersion}.dll copied next to the program.");
                return new NativeBuildResult(built, exe, log.ToString(), built ? null : new[] { new Diagnostic(0, "The C++ compiler of the native build reported errors (see the build log).", DiagnosticSeverity.Error, ErrorCatalog.NativeToolchain) });
            }
            finally
            {
                if (!keepSources) try { Directory.Delete(workDir, true); } catch (IOException) { }
            }
        }

        /// <summary>Linkage "static" (ROADMAP 9, docs/NATIVE_BACKEND.md "Getrennte Übersetzung"): every library module (the prelude, each import, each library of the project) is a
        /// static library in the user-wide cache, built once for its content and the switches of the program; the program's own C is compiled and linked with them. A module
        /// that cannot be built on its own yet: the build falls back to linkage "source" and says so.</summary>
        private static NativeBuildResult BuildStatic(IReadOnlyList<string> sources, NativeConfig config, TargetProfile target, ToolchainDef toolchain, string output,
            VmExecutionMode? mode, int? floatWidth, bool keepSources, string? basePath, IReadOnlyList<string>? defines, fire.Projects.BuildPlan? plan, IReadOnlyList<string?>? sourcePaths, Func<int>? chooseSdl)
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
            var linked = new Linker { BasePath = basePath, Engine = "native", Defines = defines, Plan = plan, SourcePaths = plan?.SourcePaths.Cast<string?>().ToList() ?? sourcePaths }.CompileAndLink(sources, null, null, mode, floatWidth, target);
            StaticBuildPlan staticPlan;
            var watch = Stopwatch.StartNew();
            try { staticPlan = CppGenerator.GenerateStatic(linked, target, ToolchainStamp(toolchain), chooseSdl, config.Trim); }
            catch (NativeNotSupportedException ex) { return AsSource($"Linkage \"static\" is not possible for this program ({ex.Message}): built with linkage \"source\"."); }
            log.Append(staticPlan.Log);
            log.AppendLine($"generated in {watch.ElapsedMilliseconds} ms");
            var program = new NativeProgram(staticPlan.ProgramC, staticPlan.Natives, linked.GuiSubsystem, linked.VersionInfo, linked.IconPath);
            string? configDirectory = config.Path == null ? null : Path.GetDirectoryName(config.Path);
            string workDir = keepSources ? Path.GetFullPath(output) + ".src" : Path.Combine(Path.GetTempPath(), "fire-native-" + Guid.NewGuid().ToString("N"));
            try
            {
                string cFile = WriteFiles(workDir, program.C, target, SourceName, configDirectory, program.Natives);
                string? nativesFile = program.Natives != null ? Path.Combine(workDir, NativesName) : null;
                string exe = Path.GetFullPath(output);
                if (ToolchainProvider.RequireLibraries(toolchain, cFile) is { } missingLibrary)
                    return new NativeBuildResult(false, exe, missingLibrary, new[] { new Diagnostic(0, missingLibrary, DiagnosticSeverity.Error, ErrorCatalog.NativeConfiguration) });
                string ext = toolchain.EffectiveKind == "msvc" ? ".lib" : ".a";
                // the libraries that are not in the cache, side by side: each its C (the generator wrote it there) to an object file, that into the library, then its interface
                var missing = staticPlan.Libraries.Where(l => !l.Cached).ToList();
                var logs = new string[missing.Count];
                var failed = new string?[missing.Count];
                Parallel.For(0, missing.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, i =>
                {
                    var library = missing[i];
                    var libraryLog = new StringBuilder();
                    var libraryWatch = Stopwatch.StartNew();
                    string obj = Path.Combine(workDir, library.LibraryName + (toolchain.EffectiveKind == "msvc" ? ".obj" : ".o"));
                    var compile = CompilerCommand(toolchain, target, library.CFile, workDir, obj, language: "c", compileOnly: true, sections: true);
                    libraryLog.AppendLine($"{compile.Executable} {compile.Arguments}");
                    var (ok, text) = Run(compile.Executable, compile.Arguments, workDir);
                    libraryLog.Append(text);
                    if (!ok) { failed[i] = $"The C compiler reported errors in the library module '{library.Module}' (see the build log)."; logs[i] = libraryLog.ToString(); return; }
                    string lib = library.LibraryFile(ext);
                    string temp = Path.Combine(library.Directory, Guid.NewGuid().ToString("N") + ext);
                    var archive = ArchiveCommand(toolchain, temp, obj);
                    libraryLog.AppendLine($"{archive.Executable} {archive.Arguments}");
                    (ok, text) = Run(archive.Executable, archive.Arguments, workDir);
                    libraryLog.Append(text);
                    if (!ok || !File.Exists(temp)) { failed[i] = $"The library of the module '{library.Module}' could not be made (see the build log)."; logs[i] = libraryLog.ToString(); return; }
                    File.Move(temp, lib, true);
                    CppGenerator.CommitStatic(library);
                    libraryLog.AppendLine($"module {library.Module}: built in {libraryWatch.ElapsedMilliseconds} ms -> {lib}");
                    logs[i] = libraryLog.ToString();
                });
                foreach (var text in logs) log.Append(text);
                if (failed.FirstOrDefault(f => f != null) is { } failure)
                    return new NativeBuildResult(false, exe, log.ToString(), new[] { new Diagnostic(0, failure, DiagnosticSeverity.Error, ErrorCatalog.NativeToolchain) });
                // (a library before the ones it needs: the linker resolves in that order)
                var libraries = staticPlan.Libraries.AsEnumerable().Reverse().Select(l => l.LibraryFile(ext)).ToList();
                bool linkedOk = true;
                watch.Restart();
                foreach (var (exeName, arguments) in BuildCommands(toolchain, target, cFile, nativesFile, workDir, exe, program.Gui, libraries))
                {
                    log.AppendLine($"{exeName} {arguments}");
                    var (stepOk, text) = Run(exeName, arguments, workDir);
                    log.Append(text);
                    if (!(linkedOk = stepOk)) break;
                }
                log.AppendLine($"program built in {watch.ElapsedMilliseconds} ms");
                bool built = linkedOk && File.Exists(exe);
                if (built) ApplyPeMetadata(exe, program, target, log);
                int sdlVersion = SdlSetup.RequiredVersion(cFile);
                if (built && toolchain.EffectiveKind != "emscripten" && sdlVersion > 0 && SdlSetup.CopyRuntimeNextTo(exe, SdlSetup.Locate(toolchain, sdlVersion))) log.AppendLine($"SDL{sdlVersion}.dll copied next to the program.");
                return new NativeBuildResult(built, exe, log.ToString(), built ? null : new[] { new Diagnostic(0, "The C++ compiler of the native build reported errors (see the build log).", DiagnosticSeverity.Error, ErrorCatalog.NativeToolchain) });
            }
            finally
            {
                if (!keepSources) try { Directory.Delete(workDir, true); } catch (IOException) { }
            }
        }

        /// <summary>Linkage "static" with a toolchain that only writes files (an ESP-IDF component, docs/NATIVE_BACKEND.md): the program's C and the C of every library module
        /// (from the cache, generated once) as translation units of their own; the build system of the board (idf.py) compiles them into the component's library.</summary>
        private static NativeBuildResult WriteStaticFiles(IReadOnlyList<string> sources, NativeConfig config, TargetProfile target, ToolchainDef toolchain, string output,
            VmExecutionMode? mode, int? floatWidth, string? basePath, IReadOnlyList<string>? defines, fire.Projects.BuildPlan? plan, IReadOnlyList<string?>? sourcePaths)
        {
            var linked = new Linker { BasePath = basePath, Engine = "native", Defines = defines, Plan = plan, SourcePaths = plan?.SourcePaths.Cast<string?>().ToList() ?? sourcePaths }.CompileAndLink(sources, null, null, mode, floatWidth, target);
            StaticBuildPlan staticPlan;
            try { staticPlan = CppGenerator.GenerateStatic(linked, target, $"files|{toolchain.Layout}", null, config.Trim); }
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
        public static NativeBuildResult BuildSafe(IReadOnlyList<string> sources, NativeConfig config, TargetProfile target, ToolchainDef toolchain, string output,
            VmExecutionMode? mode = null, int? floatWidth = null, bool keepSources = false, string? basePath = null, IReadOnlyList<string>? defines = null, fire.Projects.BuildPlan? plan = null)
        {
            // the warnings of the code (`warn 123: "text"`) are collected while the parser reads it
            var warnings = new DiagnosticCollector();
            using var collecting = warnings.Begin();
            NativeBuildResult Failed(string log, IEnumerable<Diagnostic> errors) =>
                new(false, output, log, errors.Concat(warnings.Items).ToList());
            try
            {
                var result = Build(sources, config, target, toolchain, output, mode, floatWidth, keepSources, basePath, defines, plan);
                return result with { Diagnostics = (result.Diagnostics ?? Array.Empty<Diagnostic>()).Concat(warnings.Items).ToList() };
            }
            catch (NativeNotSupportedException ex) { return Failed("Not supported by the native backend yet: " + ex.Message, CompileDiagnostics.Of(ex)); }
            catch (NativeConfigException ex) { return Failed(ex.Message, new[] { new Diagnostic(0, ex.Message, DiagnosticSeverity.Error, ErrorCatalog.NativeConfiguration) }); }
            catch (Exception ex) when (CommandLineRunner.IsCompileError(ex)) { return Failed(CompileErrors.Describe(ex), CompileDiagnostics.Of(ex)); }
        }

        /// <summary>The commands that build the program: the C file alone (one call of the C compiler), or - with natives in C++ - the C file compiled to an object
        /// file with the C compiler, then the C++ file compiled with the C++ compiler and linked with it (the C++ compiler links, it brings its runtime library).</summary>
        public static IReadOnlyList<(string Executable, string Arguments)> BuildCommands(ToolchainDef toolchain, TargetProfile target, string cFile, string? nativesFile, string workDir, string exe, bool gui = false,
            IReadOnlyList<string>? libraries = null)
        {
            if (nativesFile == null || toolchain.EffectiveKind == "custom")
                return new[] { CompilerCommand(toolchain, target, cFile, workDir, exe, gui: gui, language: "c", natives: nativesFile, extraInputs: libraries) };
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
            // the generated program asks for libraries it needs (`// fire-link: SDL3` or `SDL2` for a window): -lSDL3, SDL3.lib with msvc
            string linkSource = programFile ?? cppFile;
            // (Emscripten needs -sUSE_SDL=2 also when it only compiles: it picks the headers of its SDL2 port)
            if ((!compileOnly || kind == "emscripten") && File.Exists(linkSource))
                foreach (string line in File.ReadLines(linkSource))
                    if (line.StartsWith("// fire-link: ", StringComparison.Ordinal))
                    {
                        string lib = line.Substring("// fire-link: ".Length).Trim();
                        string flag = kind == "msvc" ? lib + ".lib" : "-l" + lib;
                        if (!libs.Contains(flag)) libs.Add(flag);
                    }
            // a debug build (mode debug, `#define FIRE_DEBUG 1` at the top of the program): no optimization, debug information, not stripped - for the debugger of spark
            // and for gdb/lldb (docs/NATIVE_BACKEND.md "Debug-Builds")
            bool debugBuild = !sharedLibrary && File.Exists(linkSource) && File.ReadLines(linkSource).Take(60).Any(l => l.StartsWith("#define FIRE_DEBUG 1", StringComparison.Ordinal));
            var includes = (toolchain.IncludeDirs ?? new()).Concat(extraIncludeDirs ?? Array.Empty<string>()).ToList();
            var libDirs = new List<string>();
            // SDL: where its headers and import library are (Toolchain\SDL3, SDL3_DIR, the prefix of the compiler, the system; the same for SDL2), see SdlSetup
            if (kind == "emscripten") libs = libs.Select(l => l == "-lSDL2" ? "-sUSE_SDL=2" : l).ToList();   // Emscripten brings SDL2 itself
            else if (kind != "custom")
                foreach (int version in new[] { 3, 2 })
                    if (libs.Any(l => l == $"-lSDL{version}" || l == $"SDL{version}.lib") && SdlSetup.Locate(toolchain, version) is { } sdl)
                    {
                        includes.AddRange(sdl.IncludeDirs.Where(d => !includes.Contains(d)));
                        libDirs.AddRange(sdl.LibDirs.Where(d => !libDirs.Contains(d)));
                        // TinyCC resolves the libraries that libSDL*.so needs itself and does not follow their RUNPATH (libpulse.so.0 -> pulseaudio/libpulsecommon-*.so): it is told the folder
                        if (kind == "tcc" && OperatingSystem.IsLinux()) libDirs.AddRange(SdlSetup.TccRunpathDirs().Where(d => !libDirs.Contains(d)));
                    }
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

        internal static (bool Ok, string Text) Run(string exe, string arguments, string workDir)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir };   // (CreateNoWindow: the console window of the compiler stays away)
                // a portable toolchain (w64devkit) finds its other programs through the PATH: its folder comes first
                if (Path.IsPathRooted(exe) && Path.GetDirectoryName(exe) is { Length: > 0 } toolDir)
                    psi.Environment["PATH"] = toolDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
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

        /// <summary>The SDL that is asked for, before the toolchain is probed: the environment variable FIRE_SDL (2 or 3), else the target's <c>sdl</c>, else the toolchain's; null:
        /// the build decides (<see cref="ChooseSdl"/>).</summary>
        public static int? RequestedSdl(TargetProfile target, ToolchainDef? toolchain)
        {
            string? env = Environment.GetEnvironmentVariable("FIRE_SDL")?.Trim();
            if (env == "2" || env == "3") return env[0] - '0';
            return target.Native.Sdl ?? toolchain?.Sdl;
        }

        /// <summary>The target with the SDL that is asked for (<see cref="RequestedSdl"/>) written into it.</summary>
        public static TargetProfile WithRequestedSdl(TargetProfile target, ToolchainDef? toolchain) =>
            RequestedSdl(target, toolchain) is int sdl && sdl != target.Native.Sdl ? target with { Native = target.Native with { Sdl = sdl } } : target;

        /// <summary>The SDL a window is built with: 3 when the toolchain can compile and link SDL3 for the target (<see cref="CanBuildSdl3"/>), else 2.</summary>
        public static int ChooseSdl(ToolchainDef toolchain, TargetProfile target) => CanBuildSdl3(toolchain, target) ? 3 : 2;

        /// <summary>Can the toolchain compile and link a program against SDL3 for this target? A tiny program (`#include &lt;SDL3/SDL.h&gt;`, SDL_Init) is built - once: the
        /// answer is remembered (<see cref="NativeProbeCache"/>) under the compiler, the target and where SDL3 was found. Not where SDL3 is not found at all.</summary>
        public static bool CanBuildSdl3(ToolchainDef toolchain, TargetProfile target)
        {
            string kind = toolchain.EffectiveKind;
            if (kind is "files" or "custom" or "emscripten" || target.Native.Platform is not ("posix" or "windows")) return false;
            string? compiler = kind == "msvc" ? ToolchainDetector.Find(toolchain) : ToolchainDetector.FindC(toolchain);
            if (compiler == null || SdlSetup.Locate(toolchain, 3) is not { } location) return false;
            string? header = location.IncludeDirs.Concat(new[] { "/usr/include", "/usr/local/include" }).Select(d => Path.Combine(d, "SDL3", "SDL.h")).FirstOrDefault(File.Exists);
            string key = string.Join("|", "sdl3", kind, NativeProbeCache.Stamp(compiler), target.Name, target.Native.Platform, string.Join(" ", toolchain.Args ?? new()),
                string.Join(" ", target.Native.CompileArgs), string.Join(";", location.IncludeDirs), string.Join(";", location.LibDirs), NativeProbeCache.Stamp(header), location.Source);
            if (NativeProbeCache.Get(key) is bool known) return known;
            string work = Path.Combine(Path.GetTempPath(), "fire-sdl3-probe-" + Guid.NewGuid().ToString("N"));
            bool ok = false;
            try
            {
                Directory.CreateDirectory(work);
                string c = Path.Combine(work, "probe.c");
                File.WriteAllText(c, "// fire-link: SDL3\n#include <SDL3/SDL.h>\nint main(void) { return SDL_Init(0) ? 0 : 1; }\n");
                string exe = Path.Combine(work, target.Name == "windows" ? "probe.exe" : "probe");
                var (exeName, arguments) = CompilerCommand(toolchain, target, c, work, exe, language: "c");
                ok = Run(exeName, arguments, work).Ok && File.Exists(exe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NativeConfigException) { ok = false; }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { }
            }
            NativeProbeCache.Set(key, ok);
            return ok;
        }

        /// <summary>The name of the program that a native build produces by default (<c>out</c>, <c>out.exe</c> on Windows).</summary>
        public static string DefaultOutput(TargetProfile target) => target.Name == "windows" ? "out.exe" : target.Name == "wasm" ? "out.html" : target.IsEmbedded ? "out" : "out";
    }
}
