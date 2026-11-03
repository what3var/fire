using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using fire.Native;
using fire.Runtime;

namespace fire.Debugging
{
    /// <summary>
    /// Running a native build from spark (ROADMAP 8.7): a plain run (<see cref="Start"/>: stdout/stderr line by line to a callback) and a run under the
    /// debugger (<see cref="LaunchDebug"/>: listen, start the program with FIRE_DEBUG, accept its connection). <see cref="DebugToolchain"/> makes the
    /// toolchain of a debug build (-O0 -g, symbols kept).
    /// </summary>
    public static class NativeLaunch
    {
        /// <summary>Whether a program built for <paramref name="target"/> with <paramref name="toolchain"/> runs on this machine: a desktop target of this
        /// system and a toolchain that makes a program (not only files, not the browser).</summary>
        public static bool CanRunHere(TargetProfile target, ToolchainDef toolchain, out string reason)
        {
            reason = "";
            if (toolchain.EffectiveKind is "files" or "custom" or "emscripten")
            {
                reason = $"the toolchain of target '{target.Name}' ({toolchain.EffectiveKind}) does not make a program that runs here";
                return false;
            }
            string platform = target.Native.Platform;
            bool here = OperatingSystem.IsWindows() ? platform == "windows" : platform == "posix";
            if (!here)
            {
                reason = $"a program for target '{target.Name}' (platform {platform}) does not run on this machine";
                return false;
            }
            return true;
        }

        /// <summary>The toolchain of a debug build: like <paramref name="toolchain"/>, but without optimisation and with debug information
        /// (gcc/clang/tcc: <c>-O0 -g</c>, MSVC: <c>/Od /Zi</c>), and the symbols are not stripped.</summary>
        public static ToolchainDef DebugToolchain(ToolchainDef toolchain)
        {
            var copy = JsonSerializer.Deserialize<ToolchainDef>(JsonSerializer.Serialize(toolchain))!;
            copy.Optimization = toolchain.EffectiveKind == "msvc" ? "/Od /Zi" : "-O0 -g";
            copy.Strip = false;
            return copy;
        }

        /// <summary>Starts a program with stdout and stderr redirected: each line goes to <paramref name="output"/> (on a thread of the process reader),
        /// <paramref name="exited"/> gets the exit code. Throws what Process.Start throws.</summary>
        public static Process Start(string program, IEnumerable<string> arguments, string? workingDirectory, Action<string> output, Action<int>? exited = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            var process = new Process { StartInfo = StartInfo(program, arguments, workingDirectory, environment), EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) output(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) output(e.Data); };
            if (exited != null) process.Exited += (_, _) => { process.WaitForExit(); exited(process.ExitCode); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        private static ProcessStartInfo StartInfo(string program, IEnumerable<string> arguments, string? workingDirectory, IReadOnlyDictionary<string, string>? environment)
        {
            var psi = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,   // (input is empty: the program reads end of input, like print/IO.Stdio in spark's VM run)
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            if (environment != null) foreach (var (k, v) in environment) psi.Environment[k] = v;
            return psi;
        }

        /// <summary>Starts a native debug build under the debugger: listens on 127.0.0.1, starts the program with FIRE_DEBUG, accepts its connection (the token
        /// must match) and waits until its main thread stands at its entry. The program's output goes to the target's Output. Throws
        /// <see cref="NativeDebugException"/> (with what the program wrote, when it ended without connecting).</summary>
        public static NativeDebugTarget LaunchDebug(string program, IEnumerable<string> arguments, string? workingDirectory, TimeSpan connectTimeout,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            using var listener = NativeDebugListener.Start();
            var env = environment?.ToDictionary(e => e.Key, e => e.Value) ?? new Dictionary<string, string>();
            env[NativeDebugListener.EnvironmentVariable] = listener.EnvironmentValue;
            var early = new List<string>();
            NativeDebugTarget? target = null;
            var gate = new object();
            void OnLine(string line)
            {
                lock (gate)
                {
                    if (target == null) { early.Add(line); return; }
                }
                target.WriteOutput(line);
            }
            var process = Start(program, arguments, workingDirectory, OnLine, null, env);
            process.StandardInput.Close();
            try
            {
                var accepted = listener.Accept(connectTimeout, () =>
                {
                    if (!process.HasExited) return null;
                    process.WaitForExit();   // (also until the output is read to its end)
                    string said;
                    lock (gate) said = string.Join(Environment.NewLine, early.TakeLast(20));
                    return $"The program ended (exit code {process.ExitCode}) without connecting to the debugger." + (said.Length > 0 ? Environment.NewLine + said : "");
                });
                lock (gate)
                {
                    target = accepted;
                    foreach (var line in early) accepted.WriteOutput(line);
                    early.Clear();
                }
                accepted.AttachProcess(process, readOutput: false);
                return accepted;
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
        }
    }
}
