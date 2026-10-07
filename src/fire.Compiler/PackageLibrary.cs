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

        private static DateTime NewestSource(InstalledImport import) =>
            import.Import.Native!.SourcesFor(HostPlatformKeys).Select(s => File.GetLastWriteTimeUtc(import.FullPath(s))).DefaultIfEmpty(DateTime.MinValue).Max();

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

        /// <summary>The library of an import: located, or built now with a C++ compiler of this machine. A failure is a <see cref="PackageException"/> with what the compiler said.</summary>
        public static string Ensure(InstalledImport import, Action<string>? log = null)
        {
            if (import.Import.Native == null) throw new PackageException($"The import '{import.Name}' has no natives.");
            if (Locate(import) is { } done) return done;
            if (RecentFailure(import) is { } earlier) throw new PackageException(earlier);
            if (!import.Import.Native.SupportsAny(HostPlatformKeys))
                throw new PackageException($"The package '{import.Package.Name}' (import '{import.Name}') has native code for {string.Join(", ", import.Import.Native.Platforms)}, not for this machine ({string.Join(", ", HostPlatformKeys)}).");
            log?.Invoke($"Building the native part of the package '{import.Package.Name}' (import '{import.Name}') for {Rid}...");

            var config = new NativeConfig();
            var target = TargetProfile.Host;
            // a toolchain of this machine; none: the host offers to provide one (install w64devkit, change the toolchain, cancel)
            ToolchainDef? toolchain = ToolchainProvider.Require(config.ResolveToolchain(target),
                $"The package '{import.Package.Name}' contains native code (C++) that has to be compiled on this machine, for the virtual machine.");
            if (toolchain == null)
                throw new PackageException($"The native part of the package '{import.Package.Name}' has to be built with a C++ compiler (g++, clang++ or cl), and none was found on this machine. " +
                    "Install one, or use a native build, or install a package that brings a prebuilt library for " + Rid + ".");

            string work = Path.Combine(Path.GetTempPath(), "fire-pkgbuild-" + Guid.NewGuid().ToString("N"));
            try
            {
                NativeRuntimeFiles.WriteTo(work, target.Native.Platform);
                string cpp = Path.Combine(work, "wrapper.cpp");
                File.WriteAllText(cpp, GenerateWrapper(import));
                string outFile = Path.Combine(work, FileNameFor(import));
                var (exe, args) = NativeBuilder.CompilerCommand(toolchain, target, cpp, work, outFile, sharedLibrary: true, extraIncludeDirs: new[] { Path.Combine(work, "abi") });
                var (ok, text) = NativeBuilder.Run(exe, args, work);
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
                        File.Copy(outFile, dest, overwrite: true);
                        try { File.Delete(FailureFile(import, dir)); } catch (IOException) { }
                        return dest;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not writable: the next place */ }
                }
                throw new PackageException("The built library of the package could not be stored.");
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { }
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

        /// <summary>The C++ of the wrapper: the runtime in library mode, the sources of the import for this machine, and the entry points of the ABI around its functions.</summary>
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
            sb.AppendLine($"#define FIRE_PLATFORM_HEADER \"platform/{target.Native.Platform}/fire_platform.hpp\"");
            sb.AppendLine($"#define FIRE_PLATFORM_FS_HEADER \"platform/{target.Native.Platform}/fire_fs.hpp\"");
            sb.AppendLine($"#define FIRE_PLATFORM_DEV_HEADER \"platform/{target.Native.Platform}/fire_dev.hpp\"");
            sb.AppendLine($"#define FIRE_PLATFORM_NET_HEADER \"platform/{target.Native.Platform}/fire_net.hpp\"");
            sb.AppendLine($"#define FIRE_PLATFORM_TLS_HEADER \"platform/{target.Native.Platform}/fire_tls.hpp\"");
            sb.AppendLine($"#define FIRE_PLATFORM_GPIO_HEADER \"platform/{target.Native.Platform}/fire_gpio.hpp\"");
            sb.AppendLine($"#define FIRE_PLATFORM_I2C_HEADER \"platform/{target.Native.Platform}/fire_i2c.hpp\"");
            sb.AppendLine($"#define FIRE_PLATFORM_SPI_HEADER \"platform/{target.Native.Platform}/fire_spi.hpp\"");
            sb.AppendLine($"#define FIRE_PLATFORM_WIFI_HEADER \"platform/{target.Native.Platform}/fire_wifi.hpp\"");
            sb.AppendLine("#include \"fire_rt.hpp\"");
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
