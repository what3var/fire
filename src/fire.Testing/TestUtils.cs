using fire.Runtime;
using System;

sealed class LoggingDestructRunner : IDestructRunner
{
    public void RunDestructor(ObjectInstance instance) =>
        Console.WriteLine($"  destruct() aufgerufen für Instanz von '{instance.ClassName}'");
}

sealed class RaceDemoRunner : IDestructRunner
{
    private readonly ObjectInstance _watchFor;
    private readonly ObjectInstance _transferSource;
    private readonly IDestructRunner _inner;

    public RaceDemoRunner(ObjectInstance watchFor, ObjectInstance transferSource, IDestructRunner inner)
    {
        _watchFor = watchFor;
        _transferSource = transferSource;
        _inner = inner;
    }

    public void RunDestructor(ObjectInstance instance)
    {
        _inner.RunDestructor(instance);
        if (ReferenceEquals(instance, _watchFor))
        {
            Console.WriteLine("  (simuliert: waehrend destruct() wird TakeTo auf das gerade zerstoerte Objekt aufgerufen)");
            _transferSource.TakeTo(_watchFor, this);
        }
    }
}

/// <summary>Builds a generated native program for the tests: the program (C) with the C compiler that belongs to <c>cxx</c>, and - when it has natives in C++ -
/// those with <c>cxx</c>, linked together. The files are <c>name.c</c> and <c>name_natives.cpp</c> in <c>dir</c> (next to the runtime files).</summary>
static class NativeTestBuild
{
    /// <summary>ccache when it is on this machine (FIRE_TEST_CCACHE=0 switches it off): the programs of an unchanged case are not compiled again. It only caches
    /// compiling (not linking), keys on the preprocessed text and the flags - the paths below the temporary folder are made relative (CCACHE_BASEDIR), the
    /// file of the program is named after its content (a filter that numbers the cases differently still finds them).</summary>
    public static readonly string? Ccache = FindCcache();

    /// <summary>The optimization of the test builds (FIRE_TEST_OPT, default -O1): with the sanitizers -O1 compiles in 20-30 % less time than -O2 and finds the
    /// same memory errors (a few warnings of gcc come only with -O2: FIRE_TEST_OPT=-O2); the checks that are about -O2 itself name it themselves.</summary>
    public static readonly string Opt = Environment.GetEnvironmentVariable("FIRE_TEST_OPT") is { Length: > 0 } opt ? opt.Trim() : "-O1";

    static string? FindCcache()
    {
        if (Environment.GetEnvironmentVariable("FIRE_TEST_CCACHE") == "0") return null;
        string? found = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, "ccache")).FirstOrDefault(File.Exists);
        if (found == null) return null;
        if (Environment.GetEnvironmentVariable("CCACHE_BASEDIR") == null) Environment.SetEnvironmentVariable("CCACHE_BASEDIR", Path.GetTempPath());
        Environment.SetEnvironmentVariable("CCACHE_NOHASHDIR", "1");
        return found;
    }

    static string ContentName(string text, string flags)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(flags + "\n" + text));
        return "p" + Convert.ToHexString(hash, 0, 10).ToLowerInvariant();
    }

    /// <summary>(writes a file atomically: two cases with the same program write the same file at the same time)</summary>
    static void WriteAtomically(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, true);
    }

    /// <summary>Writes the sources and returns the command lines, in order (each: the program, its arguments).</summary>
    public static List<(string Tool, string Arguments)> Commands(string cxx, fire.Native.NativeSources sources, string dir, string name, string flags, string exe, string tail = "")
    {
        string cc = fire.Native.ToolchainDef.CCompilerFor(cxx);
        var commands = new List<(string, string)>();
        if (Ccache != null)
        {
            // with the cache: every unit compiled on its own (-c, through ccache), then linked
            string cName = ContentName(sources.Program, cc + " " + flags);
            string cFile = Path.Combine(dir, cName + ".c");
            WriteAtomically(cFile, sources.Program);
            string cObj = Path.Combine(dir, name + ".o");
            commands.Add((Ccache, $"{cc} -std=c11 {flags} -c \"{cFile}\" -I\"{dir}\" -o \"{cObj}\""));
            if (sources.Natives == null)
            {
                commands.Add((cc, $"{flags} \"{cObj}\" -o \"{exe}\"{tail} -lm"));
                return commands;
            }
            string nName = ContentName(sources.Natives, cxx + " " + flags);
            string nFile = Path.Combine(dir, nName + "_natives.cpp");
            WriteAtomically(nFile, sources.Natives);
            string nObj = Path.Combine(dir, name + "_natives.o");
            commands.Add((Ccache, $"{cxx} -std=c++17 {flags} -c \"{nFile}\" -I\"{dir}\" -o \"{nObj}\""));
            commands.Add((cxx, $"{flags} \"{nObj}\" \"{cObj}\" -o \"{exe}\"{tail} -lm"));
            return commands;
        }
        {
            string cFile = Path.Combine(dir, name + ".c");
            File.WriteAllText(cFile, sources.Program);
            if (sources.Natives == null)
            {
                commands.Add((cc, $"-std=c11 {flags} \"{cFile}\" -I\"{dir}\" -o \"{exe}\"{tail} -lm"));
                return commands;
            }
            string nativesFile = Path.Combine(dir, name + "_natives.cpp");
            File.WriteAllText(nativesFile, sources.Natives);
            string obj = Path.Combine(dir, name + ".o");
            commands.Add((cc, $"-std=c11 {flags} -c \"{cFile}\" -I\"{dir}\" -o \"{obj}\""));
            commands.Add((cxx, $"-std=c++17 {flags} \"{nativesFile}\" \"{obj}\" -I\"{dir}\" -o \"{exe}\"{tail}"));
            return commands;
        }
    }

    /// <summary>Runs the commands of <see cref="Commands"/> with <paramref name="run"/> (tool, arguments -> output, exit code); the output of all, the exit code of the first that failed.
    /// (each command as one of the compiler processes that <see cref="TestJobs.Compiler"/> allows at a time)</summary>
    public static string Build(Func<string, string, (string Output, int Exit)> run, string cxx, fire.Native.NativeSources sources, string dir, string name, string flags, string exe, out int exit, string tail = "")
    {
        var output = new System.Text.StringBuilder();
        exit = 0;
        foreach (var (tool, arguments) in Commands(cxx, sources, dir, name, flags, exe, tail))
        {
            var (text, code) = TestJobs.Compiler(() => run(tool, arguments));
            output.Append(text);
            if ((exit = code) != 0) break;
        }
        return output.ToString();
    }

    /// <summary>A program for <see cref="Build"/>: runs the tool in <paramref name="dir"/>.</summary>
    public static Func<string, string, (string, int)> In(string dir) => (tool, arguments) =>
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool, arguments)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir })!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (output + err.Result, p.ExitCode);
    };
}

/// <summary>Runs a program of a test and waits for it: its output, its errors and its exit code. Variables for the program go in <paramref name="environment"/>
/// (null removes one) - never through Environment.SetEnvironmentVariable, which other cases running at the same time would see.</summary>
static class TestProcess
{
    public static (string Stdout, string Stderr, int Exit) Run(string tool, string arguments, string dir, string? input = null,
        IReadOnlyDictionary<string, string?>? environment = null, int timeoutMs = -1)
    {
        var info = new System.Diagnostics.ProcessStartInfo(tool, arguments)
        { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null, UseShellExecute = false, WorkingDirectory = dir };
        if (environment != null)
            foreach (var (name, value) in environment)
                if (value == null) info.Environment.Remove(name); else info.Environment[name] = value;
        using var p = System.Diagnostics.Process.Start(info)!;
        if (input != null) { p.StandardInput.Write(input); p.StandardInput.Close(); }
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch (Exception) { }
            p.WaitForExit();
            return (output.Result, err.Result + "(Zeitueberschreitung)", -1);
        }
        p.WaitForExit();
        return (output.Result, err.Result, p.ExitCode);
    }
}

/// <summary>Parallel work of the native group: how many at a time (FIRE_TEST_JOBS, default: the cores; 1 = one after the other) and the worker pool that keeps
/// the order of the output.</summary>
static class TestJobs
{
    /// <summary>FIRE_TEST_JOBS=n: at most n compiler processes at a time and n workers; 1 runs everything one after the other (default: the cores of the machine).</summary>
    public static readonly int Count = int.TryParse(Environment.GetEnvironmentVariable("FIRE_TEST_JOBS"), out int n) && n > 0 ? n : Environment.ProcessorCount;

    static readonly SemaphoreSlim _compilers = new(Count);

    /// <summary>Held by whatever runs Wine: the cases share one prefix (workDir/wineprefix), whose first use sets it up.</summary>
    public static readonly object Wine = new();

    static int _nextServer = 300 + Environment.ProcessId % 50 * 40;
    static readonly object _serverGate = new();

    /// <summary>The argument of xvfb-run for an X server of its own ("-n N"): `-a` looks for a free number and then starts the server - two runs at the same
    /// time (here, or of another test run on the machine) can take the same one, and one of them fails. The numbers start far from those of `-a` (99) and
    /// differ per process; a number whose lock file or socket is there is skipped.</summary>
    public static string XvfbServer()
    {
        lock (_serverGate)
            for (; ; _nextServer++)
                if (!File.Exists($"/tmp/.X{_nextServer}-lock") && !File.Exists($"/tmp/.X11-unix/X{_nextServer}"))
                    return $"-n {_nextServer++}";
    }

    /// <summary>Runs <paramref name="run"/> while no compiler of this run runs (it takes all places of <see cref="Compiler"/>): for a test that drives windows
    /// on a clock (the drag cases: an X program presses, moves and lets go), which a machine busy with compilers upsets.</summary>
    public static T Alone<T>(Func<T> run)
    {
        lock (_alone)
        {
            int taken = 0;
            try
            {
                for (; taken < Count; taken++) _compilers.Wait();
                return run();
            }
            finally { if (taken > 0) _compilers.Release(taken); }
        }
    }

    static readonly object _alone = new();

    /// <summary>Runs <paramref name="run"/> as one of at most <see cref="Count"/> compiler processes at a time (C with AddressSanitizer: hundreds of MB each).</summary>
    public static T Compiler<T>(Func<T> run)
    {
        _compilers.Wait();
        try { return run(); }
        finally { _compilers.Release(); }
    }

    /// <summary>Runs <paramref name="work"/> for 0..count-1 on <paramref name="degree"/> workers (default <see cref="Count"/>) and hands each result to
    /// <paramref name="done"/> on the calling thread, in the order of the indexes, as soon as it and all before it are there - the output is the same as one
    /// after the other. The workers are threads of their own, not the thread pool: they wait for processes, and the thread pool would hold back the
    /// completions of their reads behind the queued work (minutes). An exception of <paramref name="work"/> is thrown again by this call.
    /// With <paramref name="what"/> a line "[ .. ] what done/count" every 10 s.</summary>
    public static void Ordered<T>(int count, Func<int, T> work, Action<int, T> done, int? degree = null, string? what = null)
    {
        int workers = Math.Min(degree ?? Count, count);
        if (workers <= 1)
        {
            for (int i = 0; i < count; i++) done(i, work(i));
            return;
        }
        var results = new T[count];
        var errors = new System.Runtime.ExceptionServices.ExceptionDispatchInfo?[count];
        var ready = new bool[count];
        int next = -1, finished = 0;
        var gate = new object();
        var threads = Enumerable.Range(0, workers).Select(_ => new Thread(() =>
        {
            for (int i; (i = Interlocked.Increment(ref next)) < count;)
            {
                T result = default!;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
                try { result = work(i); }
                catch (Exception ex) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
                lock (gate)
                {
                    results[i] = result;
                    errors[i] = error;
                    ready[i] = true;
                    finished++;
                    Monitor.PulseAll(gate);
                }
            }
        }, 16 << 20) { IsBackground = true, Name = "fire-test-worker" }).ToList();
        foreach (var t in threads) t.Start();
        using (what == null ? null : TestProgress.Heartbeat(what, () => Volatile.Read(ref finished), count))
            for (int i = 0; i < count; i++)
            {
                lock (gate) while (!ready[i]) Monitor.Wait(gate);
                errors[i]?.Throw();
                done(i, results[i]);
                results[i] = default!;
            }
        foreach (var t in threads) t.Join();
    }

    /// <summary>Work in two steps whose cost is known only after the first: <paramref name="prepare"/> (in this process: generating C) runs for the indexes
    /// in <paramref name="order"/> on <see cref="Count"/> threads, <paramref name="execute"/> (processes: build and run) on <see cref="Count"/> other threads,
    /// which always take the prepared item with the largest <paramref name="cost"/> - the longest builds start first and do not end the run alone.
    /// Returns at once; <see cref="Pipeline.Join"/> waits for everything. With FIRE_TEST_JOBS=1 both steps of one item after the other, on one thread.</summary>
    public static Pipeline StartPipeline<TPrepared>(IReadOnlyList<int> order, Func<int, TPrepared> prepare, Func<TPrepared, long> cost, Action<int, TPrepared> execute)
    {
        var pipeline = new Pipeline();
        if (Count <= 1)
        {
            var single = new Thread(() => { foreach (int index in order) execute(index, prepare(index)); }, 16 << 20) { IsBackground = true };
            pipeline.Threads.Add(single);
            single.Start();
            return pipeline;
        }
        var ready = new List<(int Index, TPrepared Item, long Cost)>();
        int next = -1;
        var gate = new object();
        for (int t = 0; t < Count; t++)
            pipeline.Threads.Add(new Thread(() =>
            {
                for (int k; (k = Interlocked.Increment(ref next)) < order.Count;)
                {
                    var item = prepare(order[k]);
                    lock (gate)
                    {
                        ready.Add((order[k], item, cost(item)));
                        Monitor.PulseAll(gate);
                    }
                }
            }, 16 << 20) { IsBackground = true, Name = "fire-test-prepare" });
        int taken = 0;
        for (int t = 0; t < Count; t++)
            pipeline.Threads.Add(new Thread(() =>
            {
                while (true)
                {
                    (int Index, TPrepared Item, long Cost) item;
                    lock (gate)
                    {
                        while (ready.Count == 0 && taken < order.Count) Monitor.Wait(gate);
                        if (ready.Count == 0) return;
                        int best = 0;
                        for (int i = 1; i < ready.Count; i++) if (ready[i].Cost > ready[best].Cost) best = i;
                        item = ready[best];
                        ready.RemoveAt(best);
                        if (++taken == order.Count) Monitor.PulseAll(gate);
                    }
                    execute(item.Index, item.Item);
                }
            }, 16 << 20) { IsBackground = true, Name = "fire-test-worker" });
        foreach (var thread in pipeline.Threads) thread.Start();
        return pipeline;
    }

    public sealed class Pipeline
    {
        internal readonly List<Thread> Threads = new();
        public void Join() { foreach (var thread in Threads) thread.Join(); }
    }

    /// <summary><see cref="Ordered"/> for a list: the results of <paramref name="work"/> (item, index) in the order of the items, once all are there.</summary>
    public static TResult[] Map<TItem, TResult>(IReadOnlyList<TItem> items, Func<TItem, int, TResult> work)
    {
        var results = new TResult[items.Count];
        Ordered(items.Count, i => work(items[i], i), (i, result) => results[i] = result);
        return results;
    }

    /// <summary>Runs the parts at the same time (each on a thread of its own; with FIRE_TEST_JOBS=1 one after the other) - the output stays the same as one after
    /// the other: a part writes through while all parts before it are done, else what it writes is held back until then. The parts must not share state:
    /// files of their own, no Environment.SetEnvironmentVariable, no VM. An exception of a part is thrown again (the first, in the order of the parts).</summary>
    public static void Concurrently(params Action[] parts)
    {
        if (Count <= 1 || parts.Length <= 1)
        {
            foreach (var part in parts) part();
            return;
        }
        var parent = TestProgress.Current;
        var sections = parts.Select(_ => new TestProgress.Section { Parent = parent }).ToArray();
        var errors = new System.Runtime.ExceptionServices.ExceptionDispatchInfo?[parts.Length];
        TestProgress.MakeLive(sections[0]);
        var threads = parts.Select((part, i) => new Thread(() =>
        {
            TestProgress.Enter(sections[i]);
            try { part(); }
            catch (Exception ex) { errors[i] = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            finally { Console.Out.Flush(); }
        }, 16 << 20) { IsBackground = true, Name = "fire-test-part" }).ToList();
        foreach (var t in threads) t.Start();
        for (int i = 0; i < threads.Count; i++)
        {
            threads[i].Join();
            if (i + 1 < threads.Count) TestProgress.MakeLive(sections[i + 1]);
        }
        foreach (var error in errors) error?.Throw();
    }
}

/// <summary>Progress of a run, for a log that is watched while it grows: every check line ("OK: ..." / "FEHLER: ...") gets its number and the number of
/// checks of the last run of the same group and filter - <c>OK: [123/530] title</c> (the status word stays first: what looks for "^OK:" or "FEHLER" keeps
/// working). The totals are kept per group and filter in ~/.cache/fire-tests/totals.json; "?" while there is none (the first run). Every line goes out at
/// once. <see cref="Heartbeat"/> prints a line now and then while a long phase prints no check.</summary>
static class TestProgress
{
    static int _count;
    static string _total = "?";
    static string _key = "";
    static string TotalsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "fire-tests", "totals.json");

    /// <summary>The checks counted so far.</summary>
    public static int Count => Volatile.Read(ref _count);

    /// <summary>Puts the counting writer in front of the console and saves the count when the process ends.</summary>
    public static void Install()
    {
        _key = (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") is { Length: > 0 } only ? only : "all")
            + "|" + (Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER") ?? "").Trim()
            + (Environment.GetEnvironmentVariable("FIRE_TEST_NO_NATIVE") == "1" ? "|no-native" : "");
        if (ReadTotals().TryGetValue(_key, out int known) && known > 0) _total = known.ToString();
        Console.SetOut(new ProgressWriter(Console.Out));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Console.Out.Flush();
            _out.Flush();
            if (Count == 0) return;
            try
            {
                var now = ReadTotals();
                now[_key] = Count;
                Directory.CreateDirectory(Path.GetDirectoryName(TotalsFile)!);
                string temp = TotalsFile + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(now, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, TotalsFile, true);
            }
            catch (Exception) { }   // (only a convenience: a run does not fail for it)
        };
    }

    static Dictionary<string, int> ReadTotals()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(TotalsFile)) ?? new(); }
        catch (Exception) { return new(); }
    }

    /// <summary>The line with its number, or null when it is no check: "OK: ..." and "FEHLER: ..." count - not the summary of a group
    /// ("FEHLER: 3 ...-Pruefung(en) fehlgeschlagen.").</summary>
    static string? Numbered(string line)
    {
        string status;
        if (line.StartsWith("OK: ", StringComparison.Ordinal)) status = "OK: ";
        else if (line.StartsWith("FEHLER: ", StringComparison.Ordinal)) status = "FEHLER: ";
        else return null;
        if (status == "FEHLER: " && line.Length > 8 && char.IsDigit(line[8]) && line.EndsWith("fehlgeschlagen.", StringComparison.Ordinal)) return null;
        int n = Interlocked.Increment(ref _count);
        return $"{status}[{n}/{_total}] {line.Substring(status.Length)}";
    }

    /// <summary>The output of a part of the run that runs at the same time as others (<see cref="TestJobs.Concurrently"/>): held back until the parts before it
    /// are done, then written through (Live) - to the part it runs in (<see cref="Parent"/>, when parts run inside a part), else to the console. Threads and
    /// tasks started inside the part write into it, too (AsyncLocal).</summary>
    internal sealed class Section
    {
        public readonly System.Text.StringBuilder Line = new();
        public readonly List<string> Held = new();
        public Section? Parent;
        public bool Live;
    }

    static readonly AsyncLocal<Section?> _section = new();
    static readonly object _gate = new();
    static TextWriter _out = Console.Out;

    /// <summary>The part the calling thread runs in (null: none).</summary>
    internal static Section? Current => _section.Value;

    /// <summary>The output of this thread (and what it starts) goes into <paramref name="section"/>.</summary>
    internal static void Enter(Section section) => _section.Value = section;

    /// <summary>A whole line of <paramref name="section"/>: held back by the first part on the way out that is not live, else written (with its number). Under _gate.</summary>
    static void Emit(Section? section, string line)
    {
        while (section != null && section.Live) section = section.Parent;
        if (section != null) { section.Held.Add(line); return; }
        _out.Write((Numbered(line) ?? line) + "\n");
        _out.Flush();
    }

    /// <summary>Writes what <paramref name="section"/> held back and lets it write through from now on.</summary>
    internal static void MakeLive(Section section)
    {
        lock (_gate)
        {
            section.Live = true;
            foreach (string line in section.Held) Emit(section, line);
            section.Held.Clear();
        }
    }

    /// <summary>A line straight to the output - not held back by a part, not counted (the heartbeat).</summary>
    public static void Note(string line)
    {
        lock (_gate) { _out.Write(line + "\n"); _out.Flush(); }
    }

    /// <summary>Numbers the check lines and writes every line through at once (a line is held only until its end, or - in a part that runs at the same time as
    /// others - until the parts before it are done).</summary>
    sealed class ProgressWriter : TextWriter
    {
        readonly System.Text.StringBuilder _line = new();
        public ProgressWriter(TextWriter inner) { _out = inner; }
        public override System.Text.Encoding Encoding => _out.Encoding;
        public override void Write(char value) { lock (_gate) Put(value); }
        public override void Write(string? value) { if (value == null) return; lock (_gate) foreach (char c in value) Put(c); }
        public override void Write(char[] buffer, int index, int count) { lock (_gate) for (int i = 0; i < count; i++) Put(buffer[index + i]); }
        public override void WriteLine(string? value) { lock (_gate) { if (value != null) foreach (char c in value) Put(c); Put('\n'); } }
        public override void WriteLine() { lock (_gate) Put('\n'); }
        public override void Flush() { lock (_gate) { if (_section.Value == null && _line.Length > 0) { _out.Write(_line.ToString()); _line.Clear(); } _out.Flush(); } }
        void Put(char c)
        {
            var section = _section.Value;
            var line = section?.Line ?? _line;
            if (c != '\n') { line.Append(c); return; }
            string text = line.ToString();
            line.Clear();
            Emit(section, text);
        }
    }

    /// <summary>Until the result is disposed: every 10 s a line "[ .. ] what done/total (seconds)" - a phase that builds in parallel prints no check for
    /// minutes. (Nothing when the phase ends within the first 10 s.) <paramref name="current"/>: what runs now (a step that takes long shows its name).</summary>
    public static IDisposable Heartbeat(string what, Func<int> done, int total, Func<string?>? current = null)
    {
        var started = DateTime.UtcNow;
        return new Timer(_ => Note($"[ .. ] {what} {done()}/{total} ({(int)(DateTime.UtcNow - started).TotalSeconds} s)" + (current?.Invoke() is { } now ? ": " + now : "")),
            null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }
}
