using fire.Runtime;

namespace fire.Native
{
    /// <summary>
    /// Open Watcom (ROADMAP 13): `owcc`, its POSIX-like driver, compiles and links in one step - for DOS in real mode (`wcc`: -bdos, large model), DOS with DPMI
    /// (`wcc386`: -bdos4g), Windows 3.x and the old 32-bit Windows. What the target needs (system, memory model, processor, stack) comes from the target's
    /// compile arguments; this driver adds what every build needs: C99, every function in its own segment (a segment of the real mode holds 64 KB, the runtime
    /// is more), the optimization, the include folders.
    /// The compiler is found on the PATH, in %WATCOM%\binl64 (binnt64, binl, binnt) or in Toolchain\watcom next to the compiler.
    /// </summary>
    public sealed class WatcomToolchain : IToolchainDriver
    {
        public string Kind => "watcom";

        public ToolchainDef Default { get; } = new() { Kind = "watcom", Compiler = "owcc", CCompiler = "owcc", CStd = "c99", Optimization = "-Os" };

        public bool CompilesCpp => false;   // (the programs for these targets are C; packages with natives in C++ are not for them)

        public (string Executable, string Arguments) Command(CompileRequest r)
        {
            var args = new List<string>();
            args.AddRange(r.Target.Native.CompileArgs);
            args.Add("-std=" + (r.Toolchain.CStd ?? "c99"));
            args.Add("-ffunction-sections");
            args.Add(r.DebugBuild ? "-g" : r.Toolchain.Optimization is "size" or null ? "-Os" : r.Toolchain.Optimization);
            if (r.Toolchain.Args != null) args.AddRange(r.Toolchain.Args);
            if (r.CompileOnly) args.Add("-c");
            args.AddRange(StartupSources(r.Target, r.IncludeDir).Select(f => Quote(ObjectOf(f))));   // (the startup code of the platform first: its segments set the order)
            args.Add(Quote(r.SourceFile));
            args.AddRange(r.ExtraInputs.Select(Quote));
            args.Add("-I" + Quote(r.IncludeDir));
            args.AddRange(r.IncludeDirs.Select(d => "-I" + Quote(d)));
            args.Add("-o");
            args.Add(Quote(r.Output));
            if (!r.CompileOnly) args.AddRange(r.Libraries.Where(l => l != "-lm"));   // (the math functions are in Watcom's C library)
            return (Locate(r.Toolchain) ?? r.Toolchain.Compiler ?? "owcc", string.Join(" ", args));
        }

        public string? Locate(ToolchainDef toolchain)
        {
            string name = toolchain.Compiler ?? "owcc";
            if (ToolchainSetup.FindProgram(name) is { } onPath) return onPath;
            foreach (string root in Roots())
                foreach (string bin in new[] { "binl64", "binnt64", "binl", "binnt" })
                {
                    string candidate = Path.Combine(root, bin, OperatingSystem.IsWindows() ? name + ".exe" : name);
                    if (File.Exists(candidate)) return candidate;
                }
            return null;
        }

        public void PrepareEnvironment(string executable, IDictionary<string, string?> environment)
        {
            string? bin = Path.GetDirectoryName(executable);
            string? root = bin != null ? Path.GetDirectoryName(bin) : null;
            if (root == null || !Directory.Exists(Path.Combine(root, "h")) || !File.Exists(Path.Combine(root, "h", "stdio.h"))) return;   // (not a Watcom tree)
            environment["WATCOM"] = root;
            environment["INCLUDE"] = Path.Combine(root, "h") + Path.PathSeparator + Path.Combine(root, "h", "win");   // (h/win: windows.h for win16)
            environment["EDPATH"] = Path.Combine(root, "eddat");
            // (the linker finds the stubs of the DOS extenders - dos32a.exe for dos32 - in binw, through the PATH)
            string binw = Path.Combine(root, "binw");
            string path = environment.TryGetValue("PATH", out var p) && p != null ? p : Environment.GetEnvironmentVariable("PATH") ?? "";
            if (Directory.Exists(binw) && !path.Split(Path.PathSeparator).Contains(binw)) environment["PATH"] = bin + Path.PathSeparator + binw + Path.PathSeparator + path;
        }

        /// <summary>The startup code of a platform without an operating system (bios16: fire_bios16_start.asm), assembled with wasm before the program is compiled - owcc
        /// would hand the compiler's options to the assembler too.</summary>
        public IReadOnlyList<(string Executable, string Arguments)> PreCommands(ToolchainDef toolchain, TargetProfile target, string includeDir)
        {
            var sources = StartupSources(target, includeDir);
            if (sources.Count == 0) return Array.Empty<(string, string)>();
            string? compiler = Locate(toolchain);
            string wasm = compiler != null ? Path.Combine(Path.GetDirectoryName(compiler)!, OperatingSystem.IsWindows() ? "wasm.exe" : "wasm") : "wasm";
            string cpu = target.Native.CompileArgs.Contains("-march=i86") ? "-0" : "-3";
            return sources.Select(f => (wasm, $"-q {cpu} {Quote(f)} -fo={Quote(ObjectOf(f))}")).ToList();
        }

        /// <summary>A target without an operating system gets its disk image next to the program (bios16: <c>&lt;name&gt;.img</c> - the boot sector, then the program from
        /// sector 1 on; a floppy of 1.44 MB when it fits, else a hard disk).</summary>
        public string? AfterLink(ToolchainDef toolchain, TargetProfile target, string exe)
        {
            if (target.Native.Platform != "bios16") return null;
            byte[] program = File.ReadAllBytes(exe);
            long size = 512 + program.LongLength;
            long floppy = 1474560, length = size <= floppy ? floppy : (size + 512 * 63 * 16 - 1) / (512 * 63 * 16) * (512 * 63 * 16);
            string image = Path.ChangeExtension(exe, ".img");
            using (var file = File.Create(image))
            {
                file.Write(BiosBootSectors.Bios16);
                file.Write(program);
                file.SetLength(length);
            }
            return $"boot image: {image} ({(length == floppy ? "a floppy of 1.44 MB" : $"a hard disk of {length / 1048576} MB")}, the program {program.Length} bytes from sector 1 on)";
        }

        private static IReadOnlyList<string> StartupSources(TargetProfile target, string includeDir)
        {
            string dir = Path.Combine(includeDir, "platform", target.Native.Platform);
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*_start.asm").OrderBy(f => f, StringComparer.Ordinal).ToList() : new List<string>();
        }

        private static string ObjectOf(string asm) => Path.ChangeExtension(asm, ".obj");

        /// <summary>Where an Open Watcom tree may be: %WATCOM%, Toolchain/watcom next to the compiler, /opt/watcom, C:\WATCOM.</summary>
        private static IEnumerable<string> Roots()
        {
            if (Environment.GetEnvironmentVariable("WATCOM") is { Length: > 0 } env) yield return env;
            yield return Path.Combine(AppContext.BaseDirectory, "Toolchain", "watcom");
            yield return OperatingSystem.IsWindows() ? @"C:\WATCOM" : "/opt/watcom";
        }

        private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
    }
}
