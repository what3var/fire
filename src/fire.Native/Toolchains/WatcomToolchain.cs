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
            environment["INCLUDE"] = Path.Combine(root, "h");
            environment["EDPATH"] = Path.Combine(root, "eddat");
        }

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
