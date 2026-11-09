using System.Diagnostics;
using System.Text;
using fire.Native;
using fire.Runtime;

namespace fire.Compiler
{
    public static partial class NativeBuilder
    {
        /// <summary>The file next to a program with linkage "shared" that describes it for the plugins built for it (`--host`).</summary>
        public static string HostFileOf(string exe) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, Path.GetFileNameWithoutExtension(exe) + PluginHostInfo.Extension);

        /// <summary>The extension of a plugin's library on the target.</summary>
        public static string PluginExtension(TargetProfile target) => target.Native.Platform == "windows" ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";

        /// <summary>The natives in C++ of a plugin for the VM (its project's, its packages'): their libraries for this machine (C ABI, PackageLibrary - built now when needed)
        /// copied next to its bytecode, and which native is in which (the VM registers those the program does not have). One that cannot be built is left out (the
        /// VM says the native is missing when it links the plugin).</summary>
        private static List<(string Native, string Library)> PluginVmNatives(fire.Runtime.LinkedProgram program, string directory, StringBuilder log)
        {
            var result = new List<(string, string)>();
            if (program.PackageNatives is not { Count: > 0 } names || program.PackageNativeLibraries is not { } files) return result;
            var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var imports = PackageImports.LibraryImports(program.NativeImports).Where(i => i.Import.Native is { IsC: false }).ToList();
            var count = PackageLibrary.CountBuilds(imports);
            foreach (var import in imports)
            {
                try
                {
                    string built = PackageLibrary.Ensure(import, m => log.AppendLine(m), count);
                    string file = PackageLibrary.FileNameFor(import);
                    File.Copy(built, Path.Combine(directory, file), true);
                    copied.Add(file);
                }
                catch (Exception ex) when (ex is fire.Package.Manager.PackageException or IOException or UnauthorizedAccessException)
                {
                    log.AppendLine($"note: the natives in C++ of '{import.Name}' are not built for the VM ({ex.Message})");
                }
            }
            for (int i = 0; i < names.Count && i < files.Count; i++)
                if (copied.Contains(files[i])) result.Add((names[i], files[i]));
            return result;
        }

        /// <summary>A fire plugin for the VM only (no native library): the classes of `sources` compiled for the VM (`ownImports`: imports of packages whose prelude these sources
        /// are - their natives are the plugin's), as <see cref="PluginBytecode"/> at `path`. `packageNatives` picks the natives the VM binds when it links the plugin (name,
        /// library file - relative to the plugin's folder, or a full path). Compile errors are thrown.</summary>
        public static LinkedProgram WriteVmPlugin(string path, string name, IReadOnlyList<string> sources, IReadOnlyList<string?>? sourcePaths, VmExecutionMode mode,
            IReadOnlyCollection<string>? ownImports = null, Func<LinkedProgram, IEnumerable<(string Native, string Library)>>? packageNatives = null)
        {
            var linked = new Linker { ClassMeta = true, Engine = "vm", SourcePaths = sourcePaths, OwnImports = ownImports }.CompileAndLink(sources, null, null, mode);
            var deps = (ModuleInterface.Modules(linked) ?? new()).OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => (m.Key, ModuleInterface.Text(linked, m.Value.Classes, m.Value.Sources)));
            PluginBytecode.Write(path, name, linked, deps, packageNatives?.Invoke(linked));
            return linked;
        }

        /// <summary>`--linkage plugin` (ROADMAP 9 phase D b, docs/spec/NATIVE_BACKEND.md "Plugins"): the classes of the program's own sources as a fire plugin - a shared library
        /// (`<output>.so`/`.dll`) that a program with linkage "shared" loads with `Library.Load`, and its bytecode for the VM (`<output>.fireplugin`). With `--host <program>`
        /// it is built for that program (its switches, the files of its libraries - on Windows needed: the DLL imports from them).</summary>
        private static NativeBuildResult BuildPlugin(IReadOnlyList<string> sources, NativeConfig config, TargetProfile target, ToolchainDef toolchain, string output,
            VmExecutionMode? mode, int? floatWidth, bool keepSources, string? basePath, IReadOnlyList<string>? defines, fire.Projects.BuildPlan? plan, IReadOnlyList<string?>? sourcePaths)
        {
            var log = new StringBuilder();
            NativeBuildResult Fail(string text) => new(false, output, log + text, new[] { new Diagnostic(0, text, DiagnosticSeverity.Error, ErrorCatalog.NativeConfiguration) });
            bool dll = target.Native.Platform == "windows";
            bool can = toolchain.EffectiveKind is "gcc" or "clang" && (dll || target.Native.Platform == "posix" && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && target.Name == TargetProfile.Host.Name);
            if (!can) return Fail($"A fire plugin is a shared library: Linux and macOS with gcc or clang, Windows with MinGW (not the target '{target.Name}' with '{toolchain.EffectiveKind}').");
            if (ToolchainDetector.Find(toolchain) == null) return Fail($"The compiler '{toolchain.EffectiveCompiler}' was not found.");
            string ext = PluginExtension(target);
            string full = Path.GetFullPath(output);
            foreach (var known in new[] { ext, ".fireplugin", ".so", ".dll", ".dylib" })
                if (full.EndsWith(known, StringComparison.OrdinalIgnoreCase)) { full = full.Substring(0, full.Length - known.Length); break; }
            string library = full + ext, bytecode = full + ".fireplugin";
            PluginHostInfo? host = null;
            string? hostDir = null;
            if (config.Host is { Length: > 0 } hostPath)
            {
                string file = hostPath.EndsWith(PluginHostInfo.Extension, StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(hostPath) : HostFileOf(hostPath);
                host = PluginHostInfo.Read(file);
                if (host == null) return Fail($"The program '{hostPath}' has no description for plugins ({Path.GetFileName(file)}): build it with linkage \"shared\".");
                hostDir = Path.GetDirectoryName(file);
            }
            else if (dll) return Fail("A plugin for Windows is built for a program (--host <program>): its DLL imports from the DLLs of the program's libraries.");
            var linked = new Linker { ClassMeta = true, BasePath = basePath, Engine = "native", ToolchainKind = toolchain.EffectiveKind, Defines = defines, Plan = plan, SourcePaths = plan?.SourcePaths.Cast<string?>().ToList() ?? sourcePaths, GlobalJobsDefault = config.GlobalJobsValue, Optimize = OptimizeOf(config, plan, target) }.CompileAndLink(sources, null, null, mode, floatWidth, target);
            string name = plan?.Project.Name ?? Path.GetFileName(full);
            var watch = Stopwatch.StartNew();
            BuildMonitor.Step(BuildPhase.GenerateC, "Generating the C of the plugin");
            var pluginPlan = CppGenerator.GeneratePlugin(linked, target, name, host, StrippingOf(config, plan), ToolchainStamp(toolchain));
            log.Append(pluginPlan.Log);
            log.AppendLine($"generated in {watch.ElapsedMilliseconds} ms");
            string? configDirectory = config.Path == null ? null : Path.GetDirectoryName(config.Path);
            string workDir = keepSources ? full + ".src" : Path.Combine(Path.GetTempPath(), "fire-plugin-" + Guid.NewGuid().ToString("N"));
            try
            {
                string cFile = WriteFiles(workDir, pluginPlan.C, target, "fire_plugin.c", configDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(library)!);
                bool ok;
                string text;
                // its natives in C++: a file of their own, compiled with the C++ compiler into the same library (Linux: hidden - they are the plugin's own copy)
                string? natObj = null;
                if (pluginPlan.Natives != null)
                {
                    string natFile = Path.Combine(workDir, "fire_plugin_natives.cpp");
                    File.WriteAllText(natFile, pluginPlan.Natives);
                    natObj = Path.Combine(workDir, "fire_plugin_natives.o");
                    var natCompile = CompilerCommand(toolchain, target, natFile, workDir, natObj, sharedLibrary: !dll, language: "c++", compileOnly: true);
                    string natArgs = natCompile.Arguments + (dll ? "" : " -fvisibility=hidden");
                    log.AppendLine($"{natCompile.Executable} {natArgs}");
                    (ok, text) = Run(natCompile.Executable, natArgs, workDir);
                    log.Append(text);
                    if (!ok) return Fail("The C++ compiler reported errors in the natives of the plugin (see the build log).");
                }
                if (dll)
                {
                    // a DLL imports by file name: from the runtime's DLL and the DLLs of the modules of the program (next to it)
                    var compile = CompilerCommand(toolchain, target, cFile, workDir, Path.Combine(workDir, "fire_plugin.o"), language: "c", compileOnly: true);
                    log.AppendLine($"{compile.Executable} {compile.Arguments}");
                    BuildMonitor.Step(BuildPhase.CompileC, "Compiling the plugin (C)", 0, 2);
                    (ok, text) = Run(compile.Executable, compile.Arguments, workDir);
                    log.Append(text);
                    if (!ok) return Fail("The C compiler reported errors in the plugin (see the build log).");
                    var imports = new List<string>();
                    if (host!.Runtime != null) imports.Add(Path.Combine(hostDir!, host.Runtime));
                    foreach (var (module, _) in pluginPlan.Deps)
                        if (host.Modules.FirstOrDefault(m => m.Name == module) is { Library.Length: > 0 } m) imports.Add(Path.Combine(hostDir!, m.Library));
                    var missing = imports.FirstOrDefault(f => !File.Exists(f));
                    if (missing != null) return Fail($"The library '{Path.GetFileName(missing)}' of the program is not next to it ({hostDir}).");
                    // (with natives in C++: linked by the C++ compiler, its runtime in the DLL)
                    string cc = natObj != null ? ToolchainDetector.Find(toolchain) ?? toolchain.EffectiveCompiler : ToolchainDetector.FindC(toolchain) ?? toolchain.EffectiveCCompiler;
                    var libs = (toolchain.Libs ?? new()).Concat(target.Native.LinkLibs).ToList();
                    string objects = $"\"{Path.Combine(workDir, "fire_plugin.o")}\"" + (natObj != null ? $" \"{natObj}\"" : "");
                    string arguments = $"-shared -static-libgcc{(natObj != null ? " -static-libstdc++" : "")} -o \"{library}\" {objects}{string.Concat(imports.Select(i => $" \"{i}\""))} -Wl,--export-all-symbols -s {string.Join(" ", libs)}";
                    log.AppendLine($"{cc} {arguments}");
                    BuildMonitor.Step(BuildPhase.Link, "Linking the plugin's DLL", 1, 2);
                    (ok, text) = Run(cc, arguments, workDir);
                    log.Append(text);
                }
                else
                {
                    // (what it uses of the program and the libraries is found when it is loaded: the program exports it)
                    var link = CompilerCommand(toolchain, target, cFile, workDir, library, sharedLibrary: true, language: "c", extraInputs: natObj != null ? new[] { natObj } : null);
                    string extra = (OperatingSystem.IsMacOS() ? " -undefined dynamic_lookup" : "") + (natObj != null ? " -lstdc++" : "");
                    log.AppendLine($"{link.Executable} {link.Arguments}{extra} -lm");
                    BuildMonitor.Step(BuildPhase.Link, "Compiling and linking the plugin (C)");
                    (ok, text) = Run(link.Executable, link.Arguments + extra + " -lm", workDir);
                    log.Append(text);
                }
                bool built = ok && File.Exists(library);
                if (built)
                {
                    // the bytecode for the VM: compiled for the VM (`#if vm`), with the interfaces of the modules as the VM sees them
                    BuildMonitor.Step(BuildPhase.CompileFire, "Compiling the plugin's bytecode for the VM");
                    var vmLinked = new Linker { ClassMeta = true, BasePath = basePath, Engine = "vm", Defines = defines, Plan = plan, SourcePaths = plan?.SourcePaths.Cast<string?>().ToList() ?? sourcePaths }.CompileAndLink(sources, null, null, mode, floatWidth);
                    var vmDeps = (ModuleInterface.Modules(vmLinked) ?? new()).OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => (m.Key, ModuleInterface.Text(vmLinked, m.Value.Classes, m.Value.Sources)));
                    PluginBytecode.Write(bytecode, name, vmLinked, vmDeps, PluginVmNatives(vmLinked, Path.GetDirectoryName(bytecode)!, log));
                    log.AppendLine($"plugin {name}: {library}, {Path.GetFileName(bytecode)} (the VM)");
                }
                return new NativeBuildResult(built, library, log.ToString(), built ? null : new[] { new Diagnostic(0, "The C compiler reported errors in the plugin (see the build log).", DiagnosticSeverity.Error, ErrorCatalog.NativeToolchain) });
            }
            finally
            {
                if (!keepSources) try { Directory.Delete(workDir, true); } catch (IOException) { }
            }
        }
    }
}
