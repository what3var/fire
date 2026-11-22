using System.Diagnostics;
using System.Text;
using fire.Runtime;

namespace fire.Compiler
{
    /// <summary>
    /// A program of the VM in `fire` (the VM in C) as a child process: `forge run`, `forge test` and `forge data` of the C# forge (docs/ROADMAP_C_PORT.md, M1: the
    /// C# VM is not needed for them any more), the same way forge-c does it (native/forge/tools/commands.c tcRunInVm, testrunner.c trRunInVm, tcRunDriver).
    /// The program is linked with the libraries of its packages (<see cref="Linker.PackageLibraries"/>), written as a program file with its line tables into a
    /// folder of its own in the temp folder (`fire-run-&lt;guid&gt;`, `fire-test-&lt;guid&gt;`, ...), and run as `fire run &lt;file&gt; [-- &lt;arguments&gt;]` with the
    /// VM of its widths (<see cref="CVm.Locate"/>); the folders of its libraries go into FIRE_PKG_PATH. The folder goes when the program has ended.
    /// </summary>
    public static class CVmRun
    {
        /// <summary>A folder of its own in the temp folder: `&lt;temp&gt;/&lt;prefix&gt;&lt;guid&gt;`.</summary>
        public static string NewFolder(string prefix)
        {
            string folder = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>The start of `fire run &lt;programFile&gt; [-- arguments]` for <paramref name="linked"/>: the VM of its widths, FIRE_PKG_PATH with the folders of its libraries
        /// (and what it was), <paramref name="extra"/> in the environment. Redirected: stdout and stderr (UTF-8) and stdin.</summary>
        public static ProcessStartInfo StartInfo(LinkedProgram linked, string programFile, IEnumerable<string> arguments, bool redirect, IReadOnlyDictionary<string, string>? extra = null,
            string? workingDirectory = null)
        {
            string vm = CVm.Locate(linked.IntWidth, linked.FloatWidth, line => Console.Error.WriteLine(line));
            var info = new ProcessStartInfo(vm) { UseShellExecute = false, WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory() };
            if (redirect)
            {
                info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
                info.StandardOutputEncoding = info.StandardErrorEncoding = new UTF8Encoding(false);
                info.CreateNoWindow = true;
            }
            foreach (var a in fire.Debugging.CVmLaunch.Arguments(programFile, arguments)) info.ArgumentList.Add(a);
            var folders = (linked.PackageLibraryFiles ?? new List<string>()).Select(l => Path.GetDirectoryName(Path.GetFullPath(l))!).Where(d => d.Length > 0).Distinct().ToList();
            if (Environment.GetEnvironmentVariable("FIRE_PKG_PATH") is { Length: > 0 } before) folders.Add(before);
            if (folders.Count > 0) info.Environment["FIRE_PKG_PATH"] = string.Join(Path.PathSeparator, folders);
            if (extra != null) foreach (var (k, v) in extra) info.Environment[k] = v;
            return info;
        }

        /// <summary>Writes the program file of <paramref name="linked"/> (with line tables, as forge-c writes it) into a new folder: (the folder, the file).</summary>
        public static (string Folder, string File) Write(LinkedProgram linked, string prefix, string name = "program.fbc")
        {
            string folder = NewFolder(prefix);
            string file = Path.Combine(folder, name);
            File.WriteAllBytes(file, fire.Bytecode.ProgramFile.Write(linked));
            return (folder, file);
        }

        /// <summary>`forge run`: the program with the stdin, stdout and stderr of this process; the exit code of `fire` (an exception nobody caught: its report on stderr and 1,
        /// `terminate(n)`: n).</summary>
        public static int RunInherited(LinkedProgram linked, IEnumerable<string> arguments)
        {
            var (folder, file) = Write(linked, "fire-run-");
            try
            {
                Console.Out.Flush();
                Console.Error.Flush();
                using var process = Process.Start(StartInfo(linked, file, arguments, redirect: false))!;
                process.WaitForExit();
                return process.ExitCode;
            }
            finally { RemoveFolder(folder); }
        }

        /// <summary>`forge run` called with writers of its own (the tests, a host that is not the console): the program with the stdin of this process, its stdout and stderr
        /// into <paramref name="stdout"/> and <paramref name="stderr"/> as they come; the exit code of `fire`.</summary>
        public static int RunToWriters(LinkedProgram linked, IEnumerable<string> arguments, TextWriter stdout, TextWriter stderr)
        {
            var (folder, file) = Write(linked, "fire-run-");
            try
            {
                var info = StartInfo(linked, file, arguments, redirect: true);
                info.RedirectStandardInput = false;   // (it reads what this process reads)
                using var process = Process.Start(info)!;
                var errors = System.Threading.Tasks.Task.Run(() => Copy(process.StandardError, stderr));
                Copy(process.StandardOutput, stdout);
                errors.Wait();
                process.WaitForExit();
                return process.ExitCode;
            }
            finally { RemoveFolder(folder); }
        }

        private static void Copy(StreamReader from, TextWriter to)
        {
            var buffer = new char[8192];
            int n;
            while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
                lock (to) { to.Write(buffer, 0, n); to.Flush(); }
        }

        /// <summary>What a program run to its end printed (stdout, line by line to <paramref name="line"/> as it comes, when given), its stderr and its exit code.
        /// <paramref name="started"/> gets the process (to end it from another thread); <paramref name="extra"/>: more of the environment (FIRE_STDOUT_LINES, ...).</summary>
        public static (string Output, string Errors, int ExitCode) RunCaptured(LinkedProgram linked, string prefix, Action<string>? line = null, Action<Process?>? started = null,
            IReadOnlyDictionary<string, string>? extra = null, IEnumerable<string>? arguments = null, string? workingDirectory = null)
        {
            var (folder, file) = Write(linked, prefix);
            try
            {
                using var process = new Process { StartInfo = StartInfo(linked, file, arguments ?? Array.Empty<string>(), redirect: true, extra, workingDirectory) };
                var output = new StringBuilder();
                var errors = new StringBuilder();
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.Append(e.Data).Append('\n'); };
                process.Start();
                started?.Invoke(process);
                try
                {
                    process.StandardInput.Close();
                    process.BeginErrorReadLine();
                    string? text;
                    while ((text = process.StandardOutput.ReadLine()) != null)
                    {
                        output.Append(text).Append('\n');
                        line?.Invoke(text);
                    }
                    process.WaitForExit();
                }
                finally { started?.Invoke(null); }
                lock (errors) return (output.ToString(), errors.ToString(), process.ExitCode);
            }
            finally { RemoveFolder(folder); }
        }

        /// <summary>The line of an exception nobody caught in what `fire` wrote to stderr - "Unhandled exception of type 'X': message", as UncaughtScriptException says it -,
        /// else null.</summary>
        public static string? UnhandledLine(string errors)
        {
            int at = errors.IndexOf("Unhandled exception of type ", StringComparison.Ordinal);
            if (at < 0) return null;
            int end = errors.IndexOf('\n', at);
            return end < 0 ? errors.Substring(at) : errors.Substring(at, end - at);
        }

        private static void RemoveFolder(string folder)
        {
            try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
