using fire.Runtime;

namespace fire.Native
{
    /// <summary>
    /// Open Watcom (ROADMAP 13): `owcc`, its POSIX-like driver, compiles and links in one step - for DOS in real mode (`wcc`: -bdos, large model), DOS with DPMI
    /// (`wcc386`: -bdos4g), Windows 3.x and the old 32-bit Windows. What the target needs (system, memory model, processor, stack) comes from the target's
    /// compile arguments; this driver adds what every build needs: C99, every function in its own segment (a segment of the real mode holds 64 KB, the runtime
    /// is more), the optimization, the include folders.
    /// The compiler is found on the PATH, in %WATCOM% or in Toolchain\watcom next to the compiler - in binnt64/binnt (Windows) or binl64/binl (Linux) of that tree.
    /// </summary>
    public sealed class WatcomToolchain : IToolchainDriver
    {
        public string Kind => "watcom";

        public ToolchainDef Default { get; } = new() { Kind = "watcom", Compiler = "owcc", CCompiler = "owcc", CStd = "c99" };   // (the direction - -Os or -Ot - comes from the build: the PC targets are optimised for size, TargetProfile.Optimize)

        public bool CompilesCpp => false;   // (the programs for these targets are C; packages with natives in C++ are not for them)

        public (string Executable, string Arguments) Command(CompileRequest r)
        {
            var args = new List<string>();
            args.AddRange(r.Target.Native.CompileArgs);
            args.Add("-std=" + (r.Toolchain.CStd ?? "c99"));
            args.Add("-ffunction-sections");
            args.Add(r.DebugBuild ? "-g" : r.Toolchain.Optimization is "size" or null ? "-Os" : r.Toolchain.Optimization);
            // (optimised for size: no inlining at all - the targets inline up to 20 quads (-oe=20, TargetProfile.Pc); for the generated code that is the
            // fast paths of the runtime (leafAlive, the checks) in every function: measured on dos16, the sample Scene3D Physics 716 -> 686 KB)
            if (!r.DebugBuild && r.Toolchain.Optimization is "size" or null) args.Add("-Wc,-oe=0");
            if (r.Toolchain.Args != null) args.AddRange(r.Toolchain.Args);
            if (r.CompileOnly) args.Add("-c");
            args.AddRange(StartupSources(r.Target, r.IncludeDir).Select(f => Quote(ObjectOf(f))));   // (the startup code of the platform first: its segments set the order)
            args.Add(Quote(r.SourceFile));
            args.AddRange(r.ExtraInputs.Select(Quote));
            args.Add("-I" + Quote(r.IncludeDir));
            args.AddRange(r.IncludeDirs.Select(d => "-I" + Quote(d)));
            args.Add("-o");
            args.Add(Quote(r.Output));
            // (the linker leaves out every segment - with -ffunction-sections every function - that nothing reaches: the C of a package that the program does not
            // call, e.g. scene3d's physics without scene3d.physics; measured on dos16: a scene3d program 477 -> 425 KB, hello 88 -> 84 KB)
            if (!r.CompileOnly) { args.Add("-Wl,option"); args.Add("-Wl,eliminate"); }
            if (!r.CompileOnly) args.AddRange(r.Libraries.Where(l => l != "-lm"));   // (the math functions are in Watcom's C library)
            return (Locate(r.Toolchain) ?? r.Toolchain.Compiler ?? "owcc", string.Join(" ", args));
        }

        public string? Locate(ToolchainDef toolchain)
        {
            string name = toolchain.Compiler ?? "owcc";
            if (ToolchainSetup.FindProgram(name) is { } onPath) return onPath;
            foreach (string root in Roots())
                if (CompilerIn(root, name) is { } found) return found;
            return null;
        }

        /// <summary>The folders of an Open Watcom tree that hold the compilers for this machine, in the order they are tried: the 64-bit ones first
        /// (Windows: binnt64, binnt; Linux: binl64, binl). The portable archive brings only the 32-bit ones (binnt, binl).</summary>
        public static IReadOnlyList<string> CompilerFolders { get; } = OperatingSystem.IsWindows() ? new[] { "binnt64", "binnt" } : new[] { "binl64", "binl" };

        /// <summary>The compiler <paramref name="name"/> (default owcc) in the Open Watcom tree <paramref name="root"/>, or null.</summary>
        public static string? CompilerIn(string root, string name = "owcc")
        {
            foreach (string bin in CompilerFolders)
            {
                string candidate = Path.Combine(root, bin, OperatingSystem.IsWindows() ? name + ".exe" : name);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>Where <see cref="Locate"/> looks for an Open Watcom tree (for a message that says where nothing was found).</summary>
        public static IEnumerable<string> SearchedFolders() => Roots().SelectMany(r => CompilerFolders.Select(b => Path.Combine(r, b)));

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

        /// <summary>A target without an operating system gets its disk image next to the program (bios16, bios16-dos: <c>&lt;name&gt;.img</c>, a FAT12 floppy of
        /// 1.44 MB - the boot sector, FIRE.EXE, and the files and folders of a folder <c>disk</c> next to the program: the mini-DOS of bios16-dos reads them).</summary>
        public string? AfterLink(ToolchainDef toolchain, TargetProfile target, string exe)
        {
            if (target.Native.Platform != "bios16") return null;
            string image = Path.ChangeExtension(exe, ".img");
            string disk = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "disk");
            var files = Directory.Exists(disk) ? Fat12Image.FromFolder(disk) : new List<Fat12Image.Entry>();
            byte[] program = File.ReadAllBytes(exe);
            try { File.WriteAllBytes(image, Fat12Image.Create(BiosBootSectors.Bios16, program, files)); }
            catch (InvalidOperationException e) { return $"boot image: not written - {e.Message}"; }
            return $"boot image: {image} (a FAT12 floppy of 1.44 MB: FIRE.EXE, {program.Length} bytes" + (files.Count > 0 ? $", and {files.Count} entries of {disk}" : "") + ")";
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
