using System;
using System.Collections.Generic;
using System.Linq;
using fire.Values;

namespace fire.Standard
{
    /// <summary>The functions of the class `Environment` (IDs for <see cref="EnvironmentMethods.NativeName"/>, fixed values - never renumber).</summary>
    public enum EnvironmentMethod
    {
        Args = 1,
        ProgramPath = 2,
        Get = 3,
        Set = 4,
        Variables = 5,
        NewLine = 6,
        Platform = 7,
        Memory = 8,
    }

    /// <summary>What a program gets from whoever starts it: its arguments (without the program's name) and its path. The environment variables are the
    /// process's; what the program sets stays in this object (<see cref="Overrides"/>) - a program that runs in the IDE does not change the IDE's environment.</summary>
    public sealed class ProgramEnvironment
    {
        public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

        /// <summary>The path of the program: the executable, or for a program the VM runs from its source the main file ("" when there is none).</summary>
        public string ProgramPath { get; init; } = "";

        /// <summary>The variables the program set (null: removed), over the ones of the process.</summary>
        public Dictionary<string, string?> Overrides { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        /// <summary>The environment of a program that runs as a process of its own (a packed program): the arguments and the path of this process.</summary>
        public static ProgramEnvironment OfProcess() => new()
        {
            Args = System.Environment.GetCommandLineArgs().Skip(1).ToArray(),
            ProgramPath = System.Environment.ProcessPath ?? "",
        };

        public string? Get(string name) =>
            Overrides.TryGetValue(name, out var set) ? set : System.Environment.GetEnvironmentVariable(name);

        /// <summary>All variables: name and value, sorted by name.</summary>
        public List<KeyValuePair<string, string>> All()
        {
            var all = new Dictionary<string, string>(Overrides.Comparer);
            foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
                if (e.Key is string k && e.Value is string v) all[k] = v;
            foreach (var (k, v) in Overrides)
                if (v == null) all.Remove(k); else all[k] = v;
            return all.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// The class `Environment` of the prelude (always there, docs/language/19-standard-library.md): the arguments of the program, its path, the environment
    /// variables, the end of the program with an exit code, the line break and the name of the platform. One native function `__EnvCall(id[, a[, b]])`, which
    /// the native backend implements alike (fire_rt.h: envCall; the operating system's part in the platform layer).
    /// </summary>
    public static class EnvironmentMethods
    {
        public const string NativeName = "__EnvCall";

        public static string PreludeSource { get; } = $$"""

            /// The program's process: its arguments, its path, the environment variables, its end (always available).
            class Environment {
                /// The arguments of the program, without its name (`forge run app.fire -- a b`: ["a", "b"]).
                static string[] Args { get { return {{NativeName}}({{(int)EnvironmentMethod.Args}}) } }
                /// The path of the running program: the executable, or the main source file when the VM runs it from its source.
                static string ProgramPath { get { return {{NativeName}}({{(int)EnvironmentMethod.ProgramPath}}) } }
                /// The value of an environment variable; undefined if it is not set.
                static string Get(string name) { return {{NativeName}}({{(int)EnvironmentMethod.Get}}, name) }
                /// Sets an environment variable of this program (and of the processes it starts); `undefined` removes it.
                static undefined Set(string name, string value) { {{NativeName}}({{(int)EnvironmentMethod.Set}}, name, value) }
                /// All environment variables by name (sorted by name).
                static Dictionary<string, string> Variables {
                    get {
                        any raw = {{NativeName}}({{(int)EnvironmentMethod.Variables}})
                        var all = new Dictionary<string, string>()
                        for (var i = 0; i + 1 < raw.length; i = i + 2) { all[raw[i]] = raw[i + 1] }
                        return all
                    }
                }
                /// Ends the program with an exit code (like `terminate(code)`).
                static undefined Exit(int code = 0) { terminate(code) }
                /// The line break of the platform: "\r\n" on Windows, "\n" elsewhere.
                static string NewLine { get { return {{NativeName}}({{(int)EnvironmentMethod.NewLine}}) } }
                /// The platform the program runs on: "windows", "linux", "macos", "wasm", "esp32", "freertos", ...
                static string Platform { get { return {{NativeName}}({{(int)EnvironmentMethod.Platform}}) } }
                /// The memory of the machine in bytes (the physical memory; on a board the heap); undefined where the system does not tell.
                static int TotalMemory { get { return {{NativeName}}({{(int)EnvironmentMethod.Memory}}, 1) } }
                /// The memory programs can still get, in bytes (Linux: MemAvailable, Windows: the available physical memory, macOS: free and inactive pages; on
                /// a board the free heap); undefined where the system does not tell.
                static int FreeMemory { get { return {{NativeName}}({{(int)EnvironmentMethod.Memory}}, 2) } }
                /// The memory this program takes, in bytes (its resident set / working set; on a board the heap in use); undefined where the system does not tell.
                static int ProcessMemory { get { return {{NativeName}}({{(int)EnvironmentMethod.Memory}}, 3) } }
            }

            """;

        /// <summary>Environment.TotalMemory / FreeMemory / ProcessMemory in the VM (1, 2, 3): the machine's (as the native runtime asks the system - Linux
        /// /proc/meminfo, Windows GlobalMemoryStatusEx, macOS sysctl and vm_statistics through `vm_stat`, else what .NET knows) and this process's working set; -1:
        /// not known.</summary>
        internal static long MemoryOf(long which)
        {
            try
            {
                switch (which)
                {
                    case 3: return System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
                    case 1 or 2 when OperatingSystem.IsLinux() && System.IO.File.Exists("/proc/meminfo"):
                    {
                        string key = which == 1 ? "MemTotal:" : "MemAvailable:";
                        foreach (var line in System.IO.File.ReadLines("/proc/meminfo"))
                            if (line.StartsWith(key, StringComparison.Ordinal) && long.TryParse(line.Substring(key.Length).Trim().Split(' ')[0], out long kb)) return kb * 1024;
                        return -1;
                    }
                    case 1 or 2 when OperatingSystem.IsWindows():
                    {
                        var m = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
                        return GlobalMemoryStatusEx(ref m) ? (long)(which == 1 ? m.TotalPhys : m.AvailPhys) : -1;
                    }
                    case 1:
                        // (macOS and the rest: what .NET knows of the machine - the physical memory, or the limit of a container)
                        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                    case 2 when OperatingSystem.IsMacOS():
                        return MacFreeBytes();
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException
                or EntryPointNotFoundException or DllNotFoundException) { }
            return -1;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        /// <summary>macOS: the free, inactive and purgeable pages times the page size, from `vm_stat` (as the native runtime counts them with host_statistics64).</summary>
        private static long MacFreeBytes()
        {
            var psi = new System.Diagnostics.ProcessStartInfo("vm_stat") { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return -1;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            long page = 4096, pages = 0;
            var size = System.Text.RegularExpressions.Regex.Match(text, @"page size of (\d+) bytes");
            if (size.Success) page = long.Parse(size.Groups[1].Value);
            foreach (var name in new[] { "Pages free", "Pages inactive", "Pages purgeable" })
            {
                var m = System.Text.RegularExpressions.Regex.Match(text, name + @":\s+(\d+)");
                if (m.Success) pages += long.Parse(m.Groups[1].Value);
            }
            return pages > 0 ? pages * page : -1;
        }

        /// <summary>The native function `__EnvCall(id[, a[, b]])` for the program that `env` describes.</summary>
        public static Value Call(Value[] args, ProgramEnvironment env)
        {
            if (args.Length is < 1 or > 3) throw new InvalidOperationException($"{NativeName}(id[, a[, b]]) expects one to three values.");
            var method = (EnvironmentMethod)args[0].AsInt();
            string? Text(int i) => i < args.Length && args[i].Kind == ValueKind.String ? args[i].AsString() : null;
            string Name()
            {
                var name = Text(1);
                if (string.IsNullOrEmpty(name) || name.Contains('=') || name.Contains('\0'))
                    throw new InvalidOperationException($"Environment.{method}: '{name}' is not a name of an environment variable.");
                return name;
            }
            switch (method)
            {
                case EnvironmentMethod.Args:
                {
                    var array = new ScriptArray(env.Args.Count);
                    for (int i = 0; i < env.Args.Count; i++) array.Items[i] = Value.MakeString(env.Args[i]);
                    return Value.MakeArray(array);
                }
                case EnvironmentMethod.ProgramPath: return Value.MakeString(env.ProgramPath);
                case EnvironmentMethod.Get: return env.Get(Name()) is { } found ? Value.MakeString(found) : Value.MakeUndefined();
                case EnvironmentMethod.Set:
                {
                    var name = Name();
                    var value = Text(2);
                    if (value == null && args.Length > 2 && args[2].Kind != ValueKind.Undefined)
                        throw new InvalidOperationException("Environment.Set: the value must be a string (or undefined to remove the variable).");
                    env.Overrides[name] = value;
                    return Value.MakeUndefined();
                }
                case EnvironmentMethod.Variables:
                {
                    var all = env.All();
                    var array = new ScriptArray(all.Count * 2);
                    for (int i = 0; i < all.Count; i++)
                    {
                        array.Items[2 * i] = Value.MakeString(all[i].Key);
                        array.Items[2 * i + 1] = Value.MakeString(all[i].Value);
                    }
                    return Value.MakeArray(array);
                }
                case EnvironmentMethod.NewLine: return Value.MakeString(OperatingSystem.IsWindows() ? "\r\n" : "\n");
                case EnvironmentMethod.Memory:
                {
                    long m = args.Length > 1 && args[1].Kind == ValueKind.Int ? MemoryOf(args[1].AsInt()) : -1;
                    return m < 0 ? Value.MakeUndefined() : Value.MakeInt(m);
                }
                case EnvironmentMethod.Platform:
                    return Value.MakeString(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux"
                        : OperatingSystem.IsBrowser() ? "wasm" : "unknown");
                default: throw new InvalidOperationException($"{NativeName}: unknown function {(int)method}.");
            }
        }
    }
}
