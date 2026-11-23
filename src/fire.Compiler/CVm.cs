using System.Text;
using fire.Native;
using fire.Runtime;

namespace fire.Compiler
{
    /// <summary>The VM in C for a program is not there and cannot be built (FIRE_VM_EXE names nothing, no C compiler): its message says what to do.</summary>
    public sealed class VmUnavailableException : InvalidOperationException
    {
        public VmUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// The VM in C as the engine of `forge build --engine vm` (docs/ROADMAP_C_PORT.md phase B step 8, docs/spec/VM_C.md "Gepackte Programme"): the program
    /// `fire` (native/vm/fire.c) with the program file and the libraries of its packages behind it (<see cref="CVmPayload"/>) instead of the .NET runtime.
    ///
    /// Where `fire` comes from: FIRE_VM_EXE (a file, for one's own builds); else next to forge - the build of forge, spark and the tests compiles it there
    /// (`forge build-vm`, src/FireVm.targets) and the publish folder of spark (setup.iss) carries it; else it is built now with a C compiler of this machine from
    /// the sources forge carries (embedded: native/vm, native/base, native/tables; the runtime of fire.Native) into the cache of the user, once per version of the
    /// sources. A `fire` next to forge counts when its stamp (`fire.key`) names the sources this forge carries - a stale one from an older build is not taken.
    ///
    /// `#intwidth`/`#floatwidth`: one VM per pair of widths, the same sources with FIRE_INT32/FIRE_INT16/FIRE_FLOAT32 (`fire-i32`, `fire-f32`, `fire-i32-f32`,
    /// `fire-i16`, `fire-i16-f32`), found and built like `fire`.
    ///
    /// The VM in C is the only engine of a packed program (M1: the .NET packer and FIRE_VM_ENGINE=dotnet are gone).
    /// </summary>
    public static class CVm
    {
        private static string Extension => OperatingSystem.IsWindows() ? ".exe" : "";

        /// <summary>The name of the VM for a pair of widths: fire, fire-i32, fire-f32, fire-i32-f32, fire-i16, fire-i16-f32 (without .exe).</summary>
        public static string VariantName(int intWidth, int floatWidth) => "fire" + (intWidth == 64 ? "" : "-i" + intWidth) + (floatWidth == 64 ? "" : "-f32");

        /// <summary>The widths of a variant named like `i32-f32` (or `fire-i32-f32`, `default`); null when it names none.</summary>
        public static (int IntWidth, int FloatWidth)? ParseVariant(string text)
        {
            string t = text.Trim().ToLowerInvariant();
            if (t.StartsWith("fire")) t = t.Substring(4);
            t = t.Trim('-');
            if (t is "" or "default") return (64, 64);
            int iw = 64, fw = 64;
            foreach (string part in t.Split('-'))
                switch (part)
                {
                    case "i32": iw = 32; break;
                    case "i16": iw = 16; break;
                    case "f32": fw = 32; break;
                    default: return null;
                }
            return (iw, fw);
        }

        public static readonly (int IntWidth, int FloatWidth)[] AllVariants = { (64, 64), (32, 64), (64, 32), (32, 32), (16, 64), (16, 32) };

        private static readonly System.Reflection.Assembly Asm = typeof(CVm).Assembly;

        /// <summary>The sources of the VM this forge carries: their path under native/ ("vm/fire.c", "base/fire_base.h", "tables/opcodes.def") and their resource.</summary>
        private static IEnumerable<(string Path, string Resource)> Sources() =>
            Asm.GetManifestResourceNames().Where(n => n.StartsWith("cvm/", StringComparison.Ordinal)).Select(n => (n.Substring(4).Replace('\\', '/'), n)).OrderBy(s => s.Item1, StringComparer.Ordinal);

        private static string? _key;

        /// <summary>A hash of what `fire` is built from: the sources of the VM, the runtime with its platform package (NativeRuntimeFiles) - a VM built from other
        /// sources is not taken.</summary>
        public static string SourceKey()
        {
            if (_key != null) return _key;
            using var sha = System.Security.Cryptography.SHA256.Create();
            foreach (var (path, resource) in Sources())
            {
                byte[] title = Encoding.UTF8.GetBytes(path + "\n");
                sha.TransformBlock(title, 0, title.Length, null, 0);
                using var stream = Asm.GetManifestResourceStream(resource)!;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                byte[] body = ms.ToArray();
                sha.TransformBlock(body, 0, body.Length, null, 0);
            }
            byte[] runtime = Encoding.UTF8.GetBytes("runtime " + NativeRuntimeFiles.Fingerprint(TargetProfile.Host.Native.Platform));
            sha.TransformFinalBlock(runtime, 0, runtime.Length);
            return _key = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        private static string KeyOf(int intWidth, int floatWidth) => SourceKey() + " " + VariantName(intWidth, floatWidth);

        private static bool Current(string exe, int intWidth, int floatWidth)
        {
            try { return File.Exists(exe) && File.Exists(exe + ".key") && File.ReadAllText(exe + ".key").Trim() == KeyOf(intWidth, floatWidth); }
            catch (IOException) { return false; }
        }

        /// <summary>The cache of the VMs built on demand: &lt;LocalApplicationData&gt;/fire/vm/&lt;rid&gt;.</summary>
        public static string CacheFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create) is { Length: > 0 } local ? local : Path.GetTempPath(), "fire", "vm", PackageLibrary.Rid);

        /// <summary>The executable of the VM for the widths: FIRE_VM_EXE, next to forge, in the cache, or built now (see the class). A failure is an
        /// <see cref="InvalidOperationException"/> that says what to do.</summary>
        public static string Locate(int intWidth, int floatWidth, Action<string>? log = null)
        {
            string name = VariantName(intWidth, floatWidth) + Extension;
            if (Environment.GetEnvironmentVariable("FIRE_VM_EXE") is { Length: > 0 } given)
            {
                // a file: the VM of 64-bit widths; its variants next to it
                string path = intWidth == 64 && floatWidth == 64 ? given : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(given))!, name);
                if (File.Exists(path)) return path;
                if (intWidth == 64 && floatWidth == 64) throw new VmUnavailableException($"FIRE_VM_EXE names '{given}', which does not exist.");
            }
            string beside = Path.Combine(AppContext.BaseDirectory, name);
            if (Current(beside, intWidth, floatWidth)) return beside;
            string cached = Path.Combine(CacheFolder, SourceKey().Substring(0, 16), name);
            if (File.Exists(cached)) return cached;
            var (ok, text) = Build(cached, intWidth, floatWidth, log);
            if (!ok)
                throw new VmUnavailableException($"The VM in C ({VariantName(intWidth, floatWidth)}) for the program could not be built on this machine:\n{text}\n" +
                    "Install a C compiler (gcc, clang, tcc or cl), or set FIRE_VM_EXE to a `fire` built elsewhere, or build it with --engine native.");
            return cached;
        }

        /// <summary>Writes the sources of the VM in the layout of the repository (vm/, base/, tables/, runtime/, platform/, abi/) into <paramref name="folder"/> and a
        /// file that includes vm/fire.c with the defines of the widths; returns that file.</summary>
        public static string WriteSources(string folder, int intWidth = 64, int floatWidth = 64, TargetProfile? target = null)
        {
            // (another machine's: the tests build the VM of Windows with MinGW - its platform package and its libraries)
            target ??= TargetProfile.Host;
            bool windows = target.Native.Platform == TargetProfile.Windows.Native.Platform;
            Directory.CreateDirectory(folder);
            foreach (var (path, resource) in Sources())
            {
                string written = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(written)!);
                using var stream = Asm.GetManifestResourceStream(resource)!;
                using var file = File.Create(written);
                stream.CopyTo(file);
            }
            string runtime = Path.Combine(folder, "runtime");
            NativeRuntimeFiles.WriteTo(runtime, target.Native.Platform);
            foreach (string sub in new[] { "platform", "abi" })
            {
                string from = Path.Combine(runtime, sub), to = Path.Combine(folder, sub);
                if (!Directory.Exists(from)) continue;
                if (Directory.Exists(to)) Directory.Delete(to, true);
                Directory.Move(from, to);
            }
            var sb = new StringBuilder();
            sb.AppendLine("/* The program fire (native/vm/fire.c), built by forge (src/fire.Compiler/CVm.cs). */");
            // (the libraries the VM links: CompilerCommand reads them from here)
            if (windows) sb.AppendLine("// fire-link: ws2_32");
            else
            {
                sb.AppendLine("// fire-link: pthread");
                if (!OperatingSystem.IsMacOS()) sb.AppendLine("// fire-link: dl");
            }
            if (intWidth == 16) sb.AppendLine("#define FIRE_INT16 1");
            else if (intWidth == 32) sb.AppendLine("#define FIRE_INT32 1");
            if (floatWidth == 32) sb.AppendLine("#define FIRE_FLOAT32 1");
            sb.AppendLine("#include \"vm/fire.c\"");
            string main = Path.Combine(folder, "fire_vm.c");
            File.WriteAllText(main, sb.ToString());
            return main;
        }

        /// <summary>Builds the VM for the widths into <paramref name="exe"/> with the optimising C compiler of this machine (with its stamp `.key`); the
        /// compiler's output on a failure.</summary>
        public static (bool Ok, string Log) Build(string exe, int intWidth = 64, int floatWidth = 64, Action<string>? log = null)
        {
            var target = TargetProfile.Host;
            ToolchainDef? toolchain;
            try
            {
                toolchain = ToolchainProvider.Require(PackageLibrary.OptimizingToolchain(new NativeConfig(), target),
                    "Programs of the virtual machine are packed with the VM in C (the program fire), which is compiled once on this machine.");
            }
            catch (Exception ex) when (ex is NativeConfigException or IOException or InvalidOperationException) { return (false, ex.Message); }
            if (toolchain == null) return (false, "No C compiler was found on this machine.");
            string work = Path.Combine(Path.GetTempPath(), "fire-vmbuild-" + Guid.NewGuid().ToString("N"));
            try
            {
                log?.Invoke($"Building the VM in C ({VariantName(intWidth, floatWidth)}) for {PackageLibrary.Rid} with {toolchain.EffectiveKind}...");
                string main = WriteSources(work, intWidth, floatWidth);
                string built = Path.Combine(work, VariantName(intWidth, floatWidth) + Extension);
                var (compiler, args) = NativeBuilder.CompilerCommand(toolchain, target, main, work, built, language: "c");
                var (ok, text) = NativeBuilder.Run(compiler, args, work);
                if (!ok || !File.Exists(built)) return (false, $"{compiler} {args}\n{text}");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(exe))!);
                // (copied next to it and renamed: a VM in its place is always whole)
                string partial = exe + "." + Guid.NewGuid().ToString("N") + ".partial";
                File.Copy(built, partial, true);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(partial, File.GetUnixFileMode(partial) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                File.Move(partial, exe, true);
                File.WriteAllText(exe + ".key", KeyOf(intWidth, floatWidth));
                return (true, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (false, ex.Message); }
            finally { try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }

        /// <summary>`forge build-vm &lt;folder&gt; [--variant v]... [--all] [--rid r]`: the VM next to forge (the build of forge, spark and the tests: src/FireVm.targets),
        /// only when it is not current. --rid: a publish for another machine builds nothing (its VM is built there on demand).</summary>
        public static int BuildCommand(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
        {
            string? folder = null, rid = null;
            var variants = new List<(int, int)>();
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == "--all") variants.AddRange(AllVariants);
                else if (args[i] == "--variant" && i + 1 < args.Count)
                {
                    if (ParseVariant(args[++i]) is not { } v) { stderr.WriteLine($"'{args[i]}' names no variant of the VM (default, i32, f32, i32-f32, i16, i16-f32)."); return 2; }
                    variants.Add(v);
                }
                else if (args[i] == "--rid" && i + 1 < args.Count) rid = args[++i];
                else folder ??= args[i];
            }
            if (folder == null) { stderr.WriteLine("build-vm <folder> [--variant <v>]... [--all] [--rid <rid>]"); return 2; }
            if (!string.IsNullOrWhiteSpace(rid) && !string.Equals(rid.Trim(), PackageLibrary.Rid, StringComparison.OrdinalIgnoreCase))
            {
                stdout.WriteLine($"The VM in C is not built for {rid} on {PackageLibrary.Rid}: forge builds it on the machine of the program when it packs one.");
                return 0;
            }
            if (variants.Count == 0) variants.Add((64, 64));
            int failed = 0;
            foreach (var (iw, fw) in variants.Distinct())
            {
                string exe = Path.Combine(folder, VariantName(iw, fw) + Extension);
                if (Current(exe, iw, fw)) { stdout.WriteLine($"{exe} (up to date)"); continue; }
                var (ok, text) = Build(exe, iw, fw, stderr.WriteLine);
                if (ok) stdout.WriteLine(exe);
                else { failed++; stderr.WriteLine($"The VM in C ({VariantName(iw, fw)}) could not be built: {text}"); }
            }
            return failed == 0 ? 0 : 1;
        }

        /// <summary>Packs <paramref name="program"/> with the VM of its widths into <paramref name="outName"/>: the VM, then the program file (with line tables only for a
        /// stack trace) and the libraries of its packages (<see cref="LinkedProgram.PackageLibraryFiles"/>, with graphics and windows). `customizeExe`
        /// gets the copy of the VM before the payload goes behind it (subsystem, icon, version of a Windows program).</summary>
        public static void Pack(LinkedProgram program, string outName, Action<string>? customizeExe = null, Action<string>? log = null)
        {
            string vm = Locate(program.IntWidth, program.FloatWidth, log ?? (line => Console.Error.WriteLine(line)));
            string temp = outName + "." + Guid.NewGuid().ToString("N") + ".vm";
            try
            {
                File.Copy(vm, temp, true);
                customizeExe?.Invoke(temp);
                var items = new List<(byte, string, byte[])> { (CVmPayload.ProgramKind, "program.fbc", fire.Bytecode.ProgramFile.Write(program, lineTables: program.StackTrace != fire.Runtime.StackTraceMode.Off)) };   // (a stack trace needs the line tables: docs/spec/STACKTRACE.md)
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string library in program.PackageLibraryFiles ?? new List<string>())
                    if (names.Add(Path.GetFileName(library)))
                        items.Add((CVmPayload.LibraryKind, Path.GetFileName(library), File.ReadAllBytes(library)));
                using (var fs = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    CVmPayload.Append(fs, items);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(temp) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                File.Move(temp, outName, true);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
        }
    }
}
