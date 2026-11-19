using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace fire.Debugging
{
    /// <summary>
    /// spark on the VM in C (docs/ROADMAP_C_PORT.md, phase B step 9, L4): a program of the VM runs as a child process `fire run x.fbc [-- args]` - its output line by line,
    /// its input through the pipe, its exit code; under the debugger with FIRE_DEBUG (TCP; the input still through the pipe), debugged by
    /// <see cref="NativeDebugTarget"/> like a native program (its hello says engine "vm"). The libraries of its packages are where forge built them: their folders go
    /// into FIRE_PKG_PATH. <see cref="UseDotnet"/> (FIRE_SPARK_VM=dotnet) keeps the C# VM in spark's process until M1.
    /// </summary>
    public static class CVmLaunch
    {
        /// <summary>The switch of spark: FIRE_SPARK_VM=dotnet runs programs of the VM in the C# VM inside spark (RuntimeSession, VmDebugTarget, LiveSession in the process) as before.</summary>
        public const string SwitchVariable = "FIRE_SPARK_VM";

        /// <summary>For the tests: overrides <see cref="SwitchVariable"/> (null: the variable decides).</summary>
        public static bool? DotnetOverride { get; set; }

        /// <summary>spark runs the VM of C# in its own process (FIRE_SPARK_VM=dotnet), not `fire` as a child process.</summary>
        public static bool UseDotnet => DotnetOverride ?? System.Environment.GetEnvironmentVariable(SwitchVariable)?.Trim().ToLowerInvariant() is "dotnet" or ".net" or "net" or "cs";

        /// <summary>The command line of `fire` for a program file and the program's arguments.</summary>
        public static List<string> Arguments(string programFile, IEnumerable<string> arguments)
        {
            var list = new List<string> { "run", programFile };
            var rest = arguments.ToList();
            if (rest.Count > 0) { list.Add("--"); list.AddRange(rest); }
            return list;
        }

        /// <summary>The environment of `fire`: FIRE_PKG_PATH with the folders of <paramref name="libraries"/> before what it was, FIRE_STDOUT_LINES (and <paramref name="extra"/>).</summary>
        public static Dictionary<string, string> Environment(IEnumerable<string> libraries, IReadOnlyDictionary<string, string>? extra = null)
        {
            // (its stdout line by line also into the pipe: the Output panel, the messages of the live view, as they come)
            var env = new Dictionary<string, string> { ["FIRE_STDOUT_LINES"] = "1" };
            var folders = libraries.Select(l => Path.GetDirectoryName(Path.GetFullPath(l))!).Where(d => d.Length > 0).Distinct().ToList();
            if (System.Environment.GetEnvironmentVariable("FIRE_PKG_PATH") is { Length: > 0 } before) folders.Add(before);
            if (folders.Count > 0) env["FIRE_PKG_PATH"] = string.Join(Path.PathSeparator, folders);
            if (extra != null) foreach (var (k, v) in extra) env[k] = v;
            return env;
        }

        /// <summary>A plain run: `fire run x.fbc` with its stdout and stderr line by line to <paramref name="output"/>, its stdin open (<see cref="WriteInput"/>),
        /// <paramref name="exited"/> with the exit code.</summary>
        public static Process Start(string fire, string programFile, IEnumerable<string> arguments, string? workingDirectory, Action<string> output, Action<int>? exited,
            IReadOnlyDictionary<string, string>? environment) =>
            NativeLaunch.Start(fire, Arguments(programFile, arguments), workingDirectory, output, exited, environment);

        /// <summary>A line for the standard input of a plain run. false: it has ended or its input is closed.</summary>
        public static bool WriteInput(Process process, string line)
        {
            try
            {
                if (process.HasExited) return false;
                process.StandardInput.WriteLine(line);
                process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { return false; }
        }

        /// <summary>A run under the debugger: `fire run x.fbc` with FIRE_DEBUG over TCP (127.0.0.1, a token), connected and at its entry; its stdin stays open for
        /// <see cref="WriteInput"/> (target.Process). Over TCP and not framed over stdio (<see cref="NativeLaunch.LaunchDebugStdio"/>, <paramref name="framed"/>): the natives of
        /// the packages (io's ReadLine) read stdin themselves and would take the frames (docs/spec/VM_C.md, "Debugger", open points).</summary>
        public static NativeDebugTarget LaunchDebug(string fire, string programFile, IEnumerable<string> arguments, string? workingDirectory, TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment, bool framed = false) =>
            framed ? NativeLaunch.LaunchDebugStdio(fire, Arguments(programFile, arguments), workingDirectory, timeout, environment)
                   : NativeLaunch.LaunchDebug(fire, Arguments(programFile, arguments), workingDirectory, timeout, environment, keepInput: true);
    }
}
