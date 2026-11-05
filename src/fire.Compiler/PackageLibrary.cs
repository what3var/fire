using System.Runtime.InteropServices;
using System.Text;
using fire.Native;
using fire.Package.Manager;
using fire.Runtime;

namespace fire.Compiler
{
    /// <summary>
    /// The natives of a package for the virtual machine (docs/PACKAGE_NATIVES.md): the C++ source of an import is built into a shared library with the C ABI of native/abi/fire_pkg_abi.h
    /// - a generated wrapper around the functions, compiled together with the runtime and the sources, for the platform of this machine (`platformSources` of the package for it are
    /// used) - and the VM calls it. A package may bring a prebuilt library for the machine (<c>native.libraries</c>), which is used as it is. The built libraries are kept in the
    /// folder of the package (<c>lib/&lt;runtime identifier&gt;/</c>; a folder in the temporary files when that cannot be written) and are built again when a source is newer.
    /// </summary>
    public static class PackageLibrary
    {
        /// <summary>The runtime identifier of this machine: win-x64, linux-x64, linux-arm64, osx-arm64, ...</summary>
        public static string Rid
        {
            get
            {
                string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
                string arch = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", Architecture.X86 => "x86", Architecture.Arm => "arm", _ => "other" };
                return $"{os}-{arch}";
            }
        }

        /// <summary>The keys that `platformSources` and `platforms` of a package are matched with for this machine: the platform package and the target name.</summary>
        public static IReadOnlyList<string> HostPlatformKeys => new[] { TargetProfile.Host.Native.Platform, TargetProfile.Host.Name };

        private static string Extension => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib" : ".so";
        private static string Prefix => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "" : "lib";

        private static string Safe(string text) => new string(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());

        /// <summary>The file name of the library of an import (the same on every machine of one operating system: a packed program names it in its payload).</summary>
        public static string FileNameFor(InstalledImport import) => $"{Prefix}fire_pkg_{Safe(import.Package.Name)}_{Safe(import.Name)}{Extension}";

        private static string? Prebuilt(InstalledImport import)
        {
            if (import.Import.Native == null) return null;
            foreach (var (rid, path) in import.Import.Native.Libraries)
                if (string.Equals(rid, Rid, StringComparison.OrdinalIgnoreCase) && File.Exists(import.FullPath(path))) return import.FullPath(path);
            return null;
        }

        private static IEnumerable<string> BuiltDirectories(InstalledImport import)
        {
            if (import.Package.BuildDirectory != null) yield return Path.Combine(import.Package.BuildDirectory, Rid);
            else yield return Path.Combine(import.Package.Directory, "lib", Rid);
            yield return Path.Combine(Path.GetTempPath(), "fire-packages", Safe(import.Package.Name) + "-" + Safe(import.Package.Version), Rid);
        }

        /// <summary>When the runtime that libraries are built against last changed (the fire.Native assembly; MinValue in a single-file build without a location).</summary>
        private static DateTime RuntimeStamp() => typeof(NativeRuntimeFiles).Assembly.Location is { Length: > 0 } location && File.Exists(location) ? File.GetLastWriteTimeUtc(location) : DateTime.MinValue;

        private static DateTime NewestSource(InstalledImport import) =>
            import.Import.Native!.SourcesFor(HostPlatformKeys).Select(s => File.GetLastWriteTimeUtc(import.FullPath(s)))
                .Concat(import.Package.DescriptionFile is { } d && File.Exists(d) ? new[] { File.GetLastWriteTimeUtc(d) } : Array.Empty<DateTime>())
                // the runtime the library is built against (fire_rt.h, the ABI wrapper - inside fire.Native): a new runtime rebuilds the library like a new source
                .Append(RuntimeStamp()).DefaultIfEmpty(DateTime.MinValue).Max();

        /// <summary>The library of an import on this machine if there is one (prebuilt, or built earlier and not older than the sources); otherwise null.</summary>
        public static string? Locate(InstalledImport import)
        {
            if (import.Import.Native == null) return null;
            if (Prebuilt(import) is { } prebuilt) return prebuilt;
            DateTime newest = NewestSource(import);
            foreach (string dir in BuiltDirectories(import))
            {
                string path = Path.Combine(dir, FileNameFor(import));
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= newest) return path;
            }
            return null;
        }

        /// <summary>The imports of <paramref name="imports"/> whose library has to be built now (not prebuilt, not built earlier, no recent failure): the count of the phase
        /// <see cref="BuildPhase.PackageNatives"/> when a program needs several (pass the result to <see cref="Ensure"/>).</summary>
        public static BuildCount CountBuilds(IEnumerable<InstalledImport> imports) =>
            new(imports.Count(i => i.Import.Native != null && Locate(i) == null && RecentFailure(i) == null && i.Import.Native.SupportsAny(HostPlatformKeys)));

        /// <summary>How many libraries of packages one program builds (the steps of the phase <see cref="BuildPhase.PackageNatives"/>) and how many have begun.</summary>
        public sealed class BuildCount(int total)
        {
            public int Total { get; } = total;
            public int Started { get; private set; }
            internal int Next() => Math.Min(++Started, Math.Max(Total, 1));
        }

        /// <summary>The library of an import: located, or built now with a C++ compiler of this machine. A failure is a <see cref="PackageException"/> with what the compiler said.
        /// A build that runs under a <see cref="BuildMonitor"/> is its phase <see cref="BuildPhase.PackageNatives"/> ("Building natives of package X", step n of
        /// <paramref name="count"/>); cancelled, the compiler ends with its process tree, <see cref="OperationCanceledException"/> comes out and no library is left behind
        /// (it is built in a folder of its own and moved into the cache in one step).</summary>
        public static string Ensure(InstalledImport import, Action<string>? log = null, BuildCount? count = null)
        {
            if (import.Import.Native == null) throw new PackageException($"The import '{import.Name}' has no natives.");
            if (Locate(import) is { } done) return done;
            if (RecentFailure(import) is { } earlier) throw new PackageException(earlier);
            if (!import.Import.Native.SupportsAny(HostPlatformKeys))
                throw new PackageException($"The package '{import.Package.Name}' (import '{import.Name}') has native code for {string.Join(", ", import.Import.Native.Platforms)}, not for this machine ({string.Join(", ", HostPlatformKeys)}).");
            int step = count?.Next() ?? 1, steps = Math.Max(count?.Total ?? 1, step);
            // (a project with natives stands for a package "project:<name>", see ProjectNativeOverlay)
            string owner = import.Package.Name.StartsWith("project:", StringComparison.Ordinal) ? "project " + import.Package.Name.Substring(8) : "package " + import.Package.Name;
            string phaseText = $"Building natives of {owner}" + (import.Name != import.Package.Name && "project " + import.Name != owner ? $" (import {import.Name})" : "") + (steps > 1 ? $" ({step}/{steps})" : "");
            BuildMonitor.Step(BuildPhase.PackageNatives, phaseText, step - 1, steps);
            log?.Invoke($"Building the native part of the package '{import.Package.Name}' (import '{import.Name}') for {Rid}...");

            var config = new NativeConfig();
            var target = TargetProfile.Host;
            // a toolchain of this machine; none: the host offers to provide one (install w64devkit, change the toolchain, cancel)
            ToolchainDef? toolchain = ToolchainProvider.Require(config.ResolveToolchain(target),
                $"The package '{import.Package.Name}' contains native code ({(import.Import.Native.IsC ? "C" : "C++")}) that has to be compiled on this machine, for the virtual machine.");
            if (toolchain == null)
                throw new PackageException($"The native part of the package '{import.Package.Name}' has to be built with a {(import.Import.Native.IsC ? "C compiler (gcc, clang, tcc or cl)" : "C++ compiler (g++, clang++ or cl)")}, and none was found on this machine. " +
                    "Install one, or use a native build, or install a package that brings a prebuilt library for " + Rid + ".");

            if (!import.Import.Native.IsC && !toolchain.CompilesCpp)
                throw new PackageException($"The native part of the package '{import.Package.Name}' is C++, and the toolchain '{toolchain.EffectiveKind}' compiles only C. Choose gcc, clang or msvc for this machine.");
            string work = Path.Combine(Path.GetTempPath(), "fire-pkgbuild-" + Guid.NewGuid().ToString("N"));
            try
            {
                NativeRuntimeFiles.WriteTo(work, target.Native.Platform);
                bool isC = import.Import.Native.IsC;
                string cpp = Path.Combine(work, isC ? "wrapper.c" : "wrapper.cpp");
                File.WriteAllText(cpp, isC ? GenerateCWrapper(import) : GenerateWrapper(import));
                if (ToolchainProvider.RequireLibraries(toolchain, cpp) is { } missingLibrary)
                {
                    string message = $"Building the native part of the package '{import.Package.Name}' needs a library that is missing:\n{missingLibrary}";
                    RememberFailure(import, message);
                    throw new PackageException(message);
                }
                string outFile = Path.Combine(work, FileNameFor(import));
                var (exe, args) = NativeBuilder.CompilerCommand(toolchain, target, cpp, work, outFile, sharedLibrary: true, extraIncludeDirs: new[] { Path.Combine(work, "abi") }, language: isC ? "c" : "c++");
                var (ok, text) = NativeBuilder.Run(exe, args, work);   // (cancelled: OperationCanceledException - nothing is remembered as a failure, the folder goes)
                BuildMonitor.Checkpoint();
                if (!ok || !File.Exists(outFile))
                {
                    string message = $"Building the native part of the package '{import.Package.Name}' failed:\n{exe} {args}\n{text}";
                    RememberFailure(import, message);
                    throw new PackageException(message);
                }
                foreach (string dir in BuiltDirectories(import))
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        string dest = Path.Combine(dir, FileNameFor(import));
                        // (copied next to it first and then renamed: a library in the cache is always whole - Locate takes any file there)
                        string partial = dest + "." + Guid.NewGuid().ToString("N") + ".partial";
                        try
                        {
                            File.Copy(outFile, partial, overwrite: true);
                            BuildMonitor.Checkpoint();
                            File.Move(partial, dest, overwrite: true);
                        }
                        finally { try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
                        try { File.Delete(FailureFile(import, dir)); } catch (IOException) { }
                        BuildMonitor.Current?.Report(new BuildProgress(BuildPhase.PackageNatives, phaseText, step, steps));
                        return dest;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not writable: the next place */ }
                }
                throw new PackageException("The built library of the package could not be stored.");
            }
            finally
            {
                // FIRE_PKGBUILD_KEEP: the folder of the build stays (the generated wrapper, the runtime) - to build the natives into a program of one's own
                if (Environment.GetEnvironmentVariable("FIRE_PKGBUILD_KEEP") is { Length: > 0 }) log?.Invoke("The build of the package stays in " + work);
                else try { Directory.Delete(work, true); } catch (IOException) { }
            }
        }

        // A package whose library cannot be built on this machine (a missing development library, say) would be tried again by every program that imports it - a compiler run of some seconds
        // that fails again. The failure is remembered for a few minutes (next to where the library would be); after installing what was missing wait that long or delete the `.failed` file.
        private static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(5);

        private static string FailureFile(InstalledImport import, string dir) => Path.Combine(dir, FileNameFor(import) + ".failed");

        private static string? RecentFailure(InstalledImport import)
        {
            foreach (string dir in BuiltDirectories(import))
            {
                try
                {
                    string file = FailureFile(import, dir);
                    if (!File.Exists(file)) continue;
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > FailureMemory || File.GetLastWriteTimeUtc(file) < NewestSource(import)) continue;
                    return File.ReadAllText(file) + $"\n(This was the result of an earlier attempt, less than {FailureMemory.TotalMinutes:0} minutes ago; delete '{file}' to try again.)";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return null;
        }

        private static void RememberFailure(InstalledImport import, string message)
        {
            foreach (string dir in BuiltDirectories(import))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(FailureFile(import, dir), message);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>The C of the wrapper of a package in C ("language": "c"): the runtime in library mode (fire_rt.h as C), the sources of the import for this machine, and the entry
        /// points of the ABI around its functions (native/abi/fire_pkg_wrapper.h) - a C compiler is enough (TinyCC).</summary>
        public static string GenerateCWrapper(InstalledImport import)
        {
            var native = import.Import.Native ?? throw new PackageException($"The import '{import.Name}' has no natives.");
            var target = TargetProfile.Host;
            var sb = new StringBuilder();
            sb.AppendLine($"/* Generated by fire.Compiler: the C ABI (native/abi/fire_pkg_abi.h) around the natives in C of the package {import.Package.Name} {import.Package.Version}, import \"{import.Name}\". */");
            foreach (string lib in native.LinkLibrariesFor(HostPlatformKeys)) sb.AppendLine($"// fire-link: {lib}");
            sb.AppendLine("#define FIRE_LIBRARY 1");
            sb.AppendLine($"#define FIRE_TARGET \"{target.Name}\"");
            sb.AppendLine($"#define FIRE_TARGET_{target.Name.ToUpperInvariant()} 1");
            sb.AppendLine($"#define FIRE_HAL_{target.HalPackage.ToUpperInvariant()} 1");
            sb.AppendLine("#define FIRE_NDIMS 1");
            sb.AppendLine($"#define FIRE_PLATFORM_HEADER \"platform/{target.Native.Platform}/fire_platform.h\"");
            foreach (string line in CppGenerator.CPlatformHeaderDefines(target.Native.Platform)) sb.AppendLine(line);
            sb.AppendLine("#define FIRE_RT_IMPLEMENTATION 1   /* the library has a runtime of its own (its state lives here) */");
            sb.AppendLine("#include \"fire_rt.h\"");
            foreach (var (name, text) in import.ReadNativeSources(HostPlatformKeys))
            {
                sb.AppendLine($"/* ---- {name} */");
                sb.AppendLine(text);
            }
            sb.AppendLine("#include \"fire_pkg_wrapper.h\"");
            var functions = native.Functions.Where(f => !f.Host).ToList();
            for (int i = 0; i < functions.Count; i++)
            {
                var fn = functions[i];
                var args = Enumerable.Range(0, fn.Arguments).Select(k => $"a[{k}]").Concat(fn.NeedsList ? new[] { "list" } : Array.Empty<string>());
                sb.AppendLine($"static Value thunk_{i}(const Value* a, OwnList* list) {{ (void)a; (void)list; return {fn.Symbol}({string.Join(", ", args)}); }}");
            }
            sb.AppendLine("static const FirePkgFn kEntries[] = {");
            for (int i = 0; i < functions.Count; i++)
                sb.AppendLine($"    {{\"{functions[i].Name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\", {functions[i].Arguments}, thunk_{i}}},");
            sb.AppendLine("    {NULL, 0, NULL}");
            sb.AppendLine("};");
            sb.AppendLine($"static const int kCount = {functions.Count};");
            sb.AppendLine("FIRE_PKG_EXPORT void fire_pkg_set_host(const fire_host* host) { g_libraryHost = host; }");
            sb.AppendLine(string.IsNullOrWhiteSpace(native.Reset)
                ? "FIRE_PKG_EXPORT void fire_pkg_reset(void) { }"
                : $"FIRE_PKG_EXPORT void fire_pkg_reset(void) {{ fire_pkgLock(); {native.Reset}(); fire_pkgUnlock(); }}");
            sb.AppendLine("FIRE_PKG_EXPORT int fire_pkg_abi_version(void) { return FIRE_PKG_ABI_VERSION; }");
            sb.AppendLine("FIRE_PKG_EXPORT int fire_pkg_function_count(void) { return kCount; }");
            sb.AppendLine("FIRE_PKG_EXPORT const char* fire_pkg_function_name(int i) { return i >= 0 && i < kCount ? kEntries[i].name : NULL; }");
            sb.AppendLine("FIRE_PKG_EXPORT int fire_pkg_function_arity(int i) { return i >= 0 && i < kCount ? kEntries[i].arity : -1; }");
            sb.AppendLine("FIRE_PKG_EXPORT int fire_pkg_call(int i, const fire_val* a, int n, fire_val* r, char* e, int es) { return fire_pkgCallEntry(kEntries, kCount, i, a, n, r, e, es); }");
            return sb.ToString();
        }

        /// <summary>The file name of the library of graphics and windows for the VM (one library: the windows show the framebuffers of the graphics).</summary>
        public static string GraphicsFileName => $"{Prefix}fire_gfx{Extension}";

        /// <summary>The library of graphics and windows for the VM (<see cref="GenerateGraphicsWrapper"/>): built once per machine (again when a source of the two packages is newer),
        /// kept with the graphics package. A failure is a <see cref="PackageException"/> with what the compiler said.</summary>
        public static string EnsureGraphics(InstalledImport graphics, InstalledImport windows, Action<string>? log = null)
        {
            DateTime newest = new[] { NewestSource(graphics), NewestSource(windows), File.GetLastWriteTimeUtc(typeof(NativeRuntimeFiles).Assembly.Location) }.Max();
            foreach (string dir in BuiltDirectories(graphics))
            {
                string path = Path.Combine(dir, GraphicsFileName);
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= newest) return path;
            }
            log?.Invoke($"Building the native part of graphics and windows for {Rid}...");
            var target = TargetProfile.Host;
            ToolchainDef? toolchain = ToolchainProvider.Require(new NativeConfig().ResolveToolchain(target), "The windows and the graphics of fire are C that has to be compiled on this machine, for the virtual machine.");
            if (toolchain == null)
                throw new PackageException("The windows and the graphics of fire have to be built with a C compiler (gcc, clang, tcc or cl), and none was found on this machine.");
            string work = Path.Combine(Path.GetTempPath(), "fire-pkgbuild-" + Guid.NewGuid().ToString("N"));
            try
            {
                NativeRuntimeFiles.WriteTo(work, target.Native.Platform);
                string c = Path.Combine(work, "wrapper.c");
                File.WriteAllText(c, GenerateGraphicsWrapper(graphics, windows));
                string outFile = Path.Combine(work, GraphicsFileName);
                var (exe, args) = NativeBuilder.CompilerCommand(toolchain, target, c, work, outFile, sharedLibrary: true, extraIncludeDirs: new[] { Path.Combine(work, "abi") }, language: "c");
                var (ok, text) = NativeBuilder.Run(exe, args, work);
                if (!ok || !File.Exists(outFile)) throw new PackageException($"Building the native part of graphics and windows failed:\n{exe} {args}\n{text}");
                foreach (string dir in BuiltDirectories(graphics))
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        string dest = Path.Combine(dir, GraphicsFileName);
                        File.Copy(outFile, dest, overwrite: true);
                        return dest;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not writable: the next place */ }
                }
                throw new PackageException("The built library of graphics and windows could not be stored.");
            }
            finally
            {
                if (Environment.GetEnvironmentVariable("FIRE_PKGBUILD_KEEP") is { Length: > 0 }) log?.Invoke("The build of graphics and windows stays in " + work);
                else try { Directory.Delete(work, true); } catch (IOException) { }
            }
        }

        /// <summary>The C of the library of `graphics` and `windows` for the VM: the runtime in library mode, the sources of both imports (the bridges, the display of the platform
        /// - fire's own, without SDL), the entry points of the ABI around every `__GRPH...` native the native backend knows (CppGenerator's tables: the same names, arguments and
        /// lists), and the host's own functions (the pixels of a framebuffer, a presenter for scene3d, the windows without a screen for the editor).</summary>
        /// <param name="target">The machine the library is for (default: this one; the tests generate the one of Windows to build it with MinGW).</param>
        public static string GenerateGraphicsWrapper(InstalledImport graphics, InstalledImport windows, TargetProfile? target = null)
        {
            target ??= TargetProfile.Host;
            var platformKeys = new[] { target.Native.Platform, target.Name };
            var sb = new StringBuilder();
            sb.AppendLine("/* Generated by fire.Compiler: the C ABI (native/abi/fire_pkg_abi.h) around the natives of graphics and windows, for the virtual machine. */");
            foreach (string lib in CppGenerator.DisplayLibraries(target)) sb.AppendLine($"// fire-link: {lib}");
            sb.AppendLine("#define FIRE_LIBRARY 1");
            sb.AppendLine($"#define FIRE_TARGET \"{target.Name}\"");
            sb.AppendLine($"#define FIRE_TARGET_{target.Name.ToUpperInvariant()} 1");
            sb.AppendLine($"#define FIRE_HAL_{target.HalPackage.ToUpperInvariant()} 1");
            sb.AppendLine("#define FIRE_NDIMS 1");
            sb.AppendLine($"#define FIRE_PLATFORM_HEADER \"platform/{target.Native.Platform}/fire_platform.h\"");
            foreach (string line in CppGenerator.CPlatformHeaderDefines(target.Native.Platform)) sb.AppendLine(line);
            sb.AppendLine("#define FIRE_RT_IMPLEMENTATION 1   /* the library has a runtime of its own (its state lives here) */");
            sb.AppendLine("#include \"fire_rt.h\"");
            foreach (var import in new[] { graphics, windows })
                foreach (var (name, text) in import.ReadNativeSources(platformKeys))
                {
                    sb.AppendLine($"/* ---- {import.Name}: {name} */");
                    sb.AppendLine(text);
                }
            sb.AppendLine("#include \"fire_pkg_wrapper.h\"");
            var entries = new List<(string Name, int Argc, string Call)>();
            foreach (var (key, info) in CppGenerator.GraphicsBridgeNatives)
                entries.Add(("__GRPH" + key, info.Argc, $"fire_gfx_{key}({string.Join(", ", Enumerable.Range(0, info.Argc).Select(k => $"a[{k}]").Concat(CppGenerator.GraphicsNeedsList.Contains(key) ? new[] { "list" } : Array.Empty<string>()))})"));
            foreach (var (key, info) in CppGenerator.WindowsBridgeNatives)
            {
                string name = "Win" + key;
                entries.Add(("__GRPHWin" + key, info.Argc, $"fire_win_{name}({string.Join(", ", Enumerable.Range(0, info.Argc).Select(k => $"a[{k}]").Concat(CppGenerator.WindowsNeedsList.Contains(name) ? new[] { "list" } : Array.Empty<string>()))})"));
            }
            for (int i = 0; i < entries.Count; i++)
                sb.AppendLine($"static Value thunk_{i}(const Value* a, OwnList* list) {{ (void)a; (void)list; return {entries[i].Call}; }}");
            sb.AppendLine("static const FirePkgFn kEntries[] = {");
            foreach (var (e, i) in entries.Select((e, i) => (e, i))) sb.AppendLine($"    {{\"{e.Name}\", {e.Argc}, thunk_{i}}},");
            sb.AppendLine("    {NULL, 0, NULL}");
            sb.AppendLine("};");
            sb.AppendLine($"static const int kCount = {entries.Count};");
            sb.AppendLine("FIRE_PKG_EXPORT void fire_pkg_set_host(const fire_host* host) { g_libraryHost = host; }");
            sb.AppendLine("#include \"fire_gfx_host.h\"");
            sb.AppendLine("FIRE_PKG_EXPORT void fire_pkg_reset(void) { fire_pkgLock(); fire_gfx_host_reset(); fire_pkgUnlock(); }");
            sb.AppendLine("FIRE_PKG_EXPORT void fire_pkg_service(void) { fire_pkgService(); }");
            sb.AppendLine("FIRE_PKG_EXPORT int fire_pkg_abi_version(void) { return FIRE_PKG_ABI_VERSION; }");
            sb.AppendLine("FIRE_PKG_EXPORT int fire_pkg_function_count(void) { return kCount; }");
            sb.AppendLine("FIRE_PKG_EXPORT const char* fire_pkg_function_name(int i) { return i >= 0 && i < kCount ? kEntries[i].name : NULL; }");
            sb.AppendLine("FIRE_PKG_EXPORT int fire_pkg_function_arity(int i) { return i >= 0 && i < kCount ? kEntries[i].arity : -1; }");
            sb.AppendLine("FIRE_PKG_EXPORT int fire_pkg_call(int i, const fire_val* a, int n, fire_val* r, char* e, int es) { return fire_pkgCallEntry(kEntries, kCount, i, a, n, r, e, es); }");
            return sb.ToString();
        }

        /// <summary>The C++ of the wrapper: the runtime in library mode (fire_rt.h as C++, fire_rt_cpp.hpp), the sources of the import for this machine, and the entry points of the ABI around its functions.</summary>
        public static string GenerateWrapper(InstalledImport import)
        {
            var native = import.Import.Native ?? throw new PackageException($"The import '{import.Name}' has no natives.");
            var target = TargetProfile.Host;
            var sb = new StringBuilder();
            sb.AppendLine($"// Generated by fire.Compiler: the C ABI (native/abi/fire_pkg_abi.h) around the natives of the package {import.Package.Name} {import.Package.Version}, import \"{import.Name}\".");
            foreach (string lib in native.LinkLibrariesFor(HostPlatformKeys)) sb.AppendLine($"// fire-link: {lib}");   // (`linkLibraries` of the package: the build links them)
            sb.AppendLine("#define FIRE_LIBRARY 1");
            sb.AppendLine($"#define FIRE_TARGET \"{target.Name}\"");
            sb.AppendLine($"#define FIRE_TARGET_{target.Name.ToUpperInvariant()} 1");
            sb.AppendLine($"#define FIRE_HAL_{target.HalPackage.ToUpperInvariant()} 1");
            sb.AppendLine("#define FIRE_NDIMS 1");
            sb.AppendLine($"#define FIRE_PLATFORM_HEADER \"platform/{target.Native.Platform}/fire_platform.h\"");
            sb.AppendLine($"#define FIRE_PLATFORM_CPP_HEADER \"platform/{target.Native.Platform}/fire_platform.hpp\"");
            foreach (string line in CppGenerator.CPlatformHeaderDefines(target.Native.Platform)) sb.AppendLine(line);   // (the platform layers in C: packages in C++ use them as well)
            sb.AppendLine("#define FIRE_RT_IMPLEMENTATION 1   // the library has a runtime of its own (its state lives here)");
            sb.AppendLine("#include \"fire_rt_cpp.hpp\"");
            foreach (var (name, text) in import.ReadNativeSources(HostPlatformKeys))
            {
                sb.AppendLine($"// ---- {name}");
                sb.AppendLine(text);
            }
            sb.AppendLine("#include \"fire_pkg_wrapper.hpp\"");
            sb.AppendLine("using namespace fire;");
            // the functions of the host (`host` in the manifest) are run by the VM itself: they are not part of the library
            var functions = native.Functions.Where(f => !f.Host).ToList();
            for (int i = 0; i < functions.Count; i++)
            {
                var fn = functions[i];
                var args = Enumerable.Range(0, fn.Arguments).Select(k => $"a[{k}]").Concat(fn.NeedsList ? new[] { "list" } : Array.Empty<string>());
                sb.AppendLine($"static Value thunk_{i}(const Value* a, OwnList* list) {{ (void)a; (void)list; return {fn.Cpp}({string.Join(", ", args)}); }}");
            }
            sb.AppendLine("static const pkgabi::FnEntry kEntries[] = {");
            for (int i = 0; i < functions.Count; i++)
                sb.AppendLine($"    {{\"{functions[i].Name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\", {functions[i].Arguments}, thunk_{i}}},");
            sb.AppendLine("    {nullptr, 0, nullptr}");
            sb.AppendLine("};");
            sb.AppendLine($"static const int kCount = {functions.Count};");
            sb.AppendLine("FIRE_PKG_EXPORT void fire_pkg_set_host(const fire_host* host) { libraryHost() = host; }");
            sb.AppendLine(string.IsNullOrWhiteSpace(native.Reset)
                ? "FIRE_PKG_EXPORT void fire_pkg_reset(void) { }"
                : $"FIRE_PKG_EXPORT void fire_pkg_reset(void) {{ std::lock_guard<std::mutex> lock(pkgabi::callLock()); {native.Reset}(); }}");
            sb.AppendLine("FIRE_PKG_EXPORT int fire_pkg_abi_version(void) { return FIRE_PKG_ABI_VERSION; }");
            sb.AppendLine("FIRE_PKG_EXPORT int fire_pkg_function_count(void) { return kCount; }");
            sb.AppendLine("FIRE_PKG_EXPORT const char* fire_pkg_function_name(int i) { return i >= 0 && i < kCount ? kEntries[i].name : nullptr; }");
            sb.AppendLine("FIRE_PKG_EXPORT int fire_pkg_function_arity(int i) { return i >= 0 && i < kCount ? kEntries[i].arity : -1; }");
            sb.AppendLine("FIRE_PKG_EXPORT int fire_pkg_call(int i, const fire_val* a, int n, fire_val* r, char* e, int es) { return pkgabi::callEntry(kEntries, kCount, i, a, n, r, e, es); }");
            return sb.ToString();
        }
    }
}
