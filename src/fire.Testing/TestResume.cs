using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Resuming and splitting a run (docs/tools/testing.md "Resume and shards"):
/// <list type="bullet">
/// <item><c>FIRE_TESTS_RESUME=&lt;dir&gt;</c> (<c>1</c>: ~/.cache/fire-tests/resume): every check line ("OK: ..." / "FEHLER: ...") is appended to a file of this run in the folder as
/// soon as it is printed, flushed per line - together with the <b>unit</b> it belongs to (a case, a debugger case, a window case, a group of checks) and the
/// <b>key</b> of that unit: what its result depends on (the source and the expected files of a case, a hash of the built test program and its libraries, the
/// packages and native sources of the repository, the variables that change a build, the compilers). A unit whose last finished run with the same key had only
/// OK checks is not run again: its checks are printed again as <c>OK (fortgesetzt): title</c>. A unit that failed, changed or did not finish runs again.</item>
/// <item><c>FIRE_TESTS_SHARD=i/n</c>: only the units whose title hash falls into part i of n run (the checks outside units run in every part).</item>
/// </list>
/// A unit is opened where the work of a check happens (<see cref="Decide"/> before the work, <see cref="Run"/>/<see cref="Unit"/> around the printing of its
/// checks); the check lines themselves are seen by <see cref="TestProgress"/>'s writer (<see cref="Observe"/>), so every helper that prints "OK:"/"FEHLER:"
/// is recorded without knowing of this.
/// </summary>
static class TestResume
{
    public enum Decision { Run, Resumed, OtherShard }

    /// <summary>A recorded check: OK or FEHLER, its title, for a FEHLER the lines after it (shortened).</summary>
    public sealed record Check(bool Ok, string Title, string? Detail);

    /// <summary>The folder of the records, null: FIRE_TESTS_RESUME is not set.</summary>
    public static readonly string? Folder = FolderFrom(Environment.GetEnvironmentVariable("FIRE_TESTS_RESUME"));
    /// <summary>FIRE_TESTS_SHARD=i/n (1-based); (1, 1) without.</summary>
    public static readonly (int Index, int Count) Shard = ParseShard(Environment.GetEnvironmentVariable("FIRE_TESTS_SHARD"));

    public static bool Active => Folder != null || Shard.Count > 1;

    static readonly string _run = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId;
    static readonly object _gate = new();
    static Dictionary<(string Unit, string Key), List<Check>>? _resumable;
    static StreamWriter? _writer;
    static bool _writeFailed;
    static readonly Dictionary<(string Unit, string Key), (int Checks, bool Failed)> _thisRun = new();
    static readonly Dictionary<string, Decision> _decided = new();
    static int _resumedChecks;
    static readonly AsyncLocal<Scope?> _current = new();

    public static string? FolderFrom(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "0") return null;
        if (value.Trim() == "1") return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "fire-tests", "resume");
        return Path.GetFullPath(value.Trim());
    }

    /// <summary>"i/n" with 1 &lt;= i &lt;= n; anything else: (1, 1) - no shards (a wrong value is reported once at the start).</summary>
    public static (int Index, int Count) ParseShard(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (1, 1);
        var parts = value.Trim().Split('/');
        if (parts.Length == 2 && int.TryParse(parts[0], out int i) && int.TryParse(parts[1], out int n) && n >= 1 && i >= 1 && i <= n) return (i, n);
        return (1, 1);
    }

    /// <summary>The part (1..count) a unit belongs to: a stable hash of its title (SHA-256, not string.GetHashCode, which changes from process to process).</summary>
    public static int ShardOf(string unit, int count)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(unit));
        return (int)(BitConverter.ToUInt64(hash, 0) % (ulong)count) + 1;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------
    // the key

    static string? _global;

    /// <summary>What every unit depends on: the built test program and what is next to it (the fire assemblies, forge, the standard packages as Source),
    /// the packages and native sources of the repository, the variables that change what a build makes, and the compilers on the PATH.</summary>
    public static string Global
    {
        get
        {
            lock (_gate) return _global ??= ComputeGlobal();
        }
    }

    static string? _cases;
    /// <summary>The files of tests/cases (what the groups read at run time).</summary>
    public static string CasesHash { get { lock (_gate) return _cases ??= HashTree(TestCases.Root(), _ => true); } }

    static readonly string[] KeyVariables =
    {
        "FIRE_TESTS_ONLY", "FIRE_SPLIT", "FIRE_SPLIT_UNIT_KB", "FIRE_MODULES", "FIRE_TEST_OPT", "FIRE_TEST_SANITIZE", "FIRE_TEST_NO_NATIVE", "FIRE_TEST_GIL",
        "FIRE_S3D_GPU", "FIRE_GFX_GPU", "FIRE_TEST_WIN_FORGE", "IDF_PATH", "CC", "CXX",
    };

    static string ComputeGlobal()
    {
        var text = new StringBuilder();
        text.Append("assembly ").Append(typeof(TestResume).Assembly.ManifestModule.ModuleVersionId).Append('\n');
        text.Append("bin ").Append(HashTree(AppContext.BaseDirectory, BuildFile)).Append('\n');
        string repo = RepoRoot();
        foreach (var part in new[] { "native", "packages" }) text.Append(part).Append(' ').Append(HashTree(Path.Combine(repo, part), RepoFile)).Append('\n');
        foreach (var name in KeyVariables) text.Append(name).Append('=').Append(Environment.GetEnvironmentVariable(name) ?? "(unset)").Append('\n');
        // the compilers: the file each name on the PATH resolves to, its size and time (no process: cheap)
        foreach (var tool in new[] { "cc", "gcc", "g++", "c++", "clang", "clang++", "tcc", "x86_64-w64-mingw32-gcc", "wine", "ccache" })
        {
            string? found = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, tool)).FirstOrDefault(File.Exists);
            if (found == null) { text.Append(tool).Append(" -\n"); continue; }
            try
            {
                var info = new FileInfo(found);
                string real = info.ResolveLinkTarget(true)?.FullName ?? found;
                var target = new FileInfo(real);
                text.Append(tool).Append(' ').Append(real).Append(' ').Append(target.Length).Append(' ').Append(target.LastWriteTimeUtc.Ticks).Append('\n');
            }
            catch (Exception) { text.Append(tool).Append(" ?\n"); }
        }
        return Hash(text.ToString());
    }

    /// <summary>The repository the test program was built from (src/fire.Testing/../..).</summary>
    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>The files of the build output that make a difference: not the .NET runtime (it comes with the SDK; its own version is in the assemblies' references).</summary>
    static bool BuildFile(string relative)
    {
        string name = Path.GetFileName(relative);
        if (relative.Contains(Path.DirectorySeparatorChar)) return !name.EndsWith(".pdb", StringComparison.Ordinal);
        if (name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)) return false;
        if (name is "mscorlib.dll" or "netstandard.dll" or "WindowsBase.dll" or "createdump") return false;
        if (name.StartsWith("lib", StringComparison.Ordinal) && name.EndsWith(".so", StringComparison.Ordinal) && !name.Contains("fire", StringComparison.OrdinalIgnoreCase)) return false;
        return !name.EndsWith(".pdb", StringComparison.Ordinal);
    }

    static bool RepoFile(string relative) =>
        !relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "build" || p.StartsWith('.')) && !relative.EndsWith(".o", StringComparison.Ordinal);

    /// <summary>SHA-256 over the relative paths and contents of the files below <paramref name="root"/> that <paramref name="take"/> keeps (sorted: the same on every machine).</summary>
    public static string HashTree(string root, Func<string, bool> take)
    {
        if (!Directory.Exists(root)) return "-";
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).Where(take).OrderBy(f => f, StringComparer.Ordinal);
        var buffer = new byte[1 << 16];
        foreach (var file in files)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(file.Replace('\\', '/') + "\0"));
            try
            {
                using var stream = File.OpenRead(Path.Combine(root, file));
                for (int n; (n = stream.Read(buffer, 0, buffer.Length)) > 0;) sha.AppendData(buffer, 0, n);
            }
            catch (IOException) { sha.AppendData(Encoding.UTF8.GetBytes("(unreadable)")); }
            catch (UnauthorizedAccessException) { sha.AppendData(Encoding.UTF8.GetBytes("(unreadable)")); }
            sha.AppendData(new byte[] { 0 });
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).ToLowerInvariant();

    /// <summary>The key of a unit: <see cref="Global"/> and what the unit itself depends on (its source, its expected output, ...; null counts as a value of its own).</summary>
    public static string Key(params string?[] parts) => KeyWith(Global, parts);

    /// <summary><see cref="Key"/> with a given global part (for the unit tests).</summary>
    public static string KeyWith(string global, params string?[] parts)
    {
        var text = new StringBuilder(global).Append('\n');
        // (every part with its length: "ab" + "c" is not "a" + "bc")
        foreach (var part in parts) text.Append(part == null ? "-1:" : part.Length + ":" + part).Append('\n');
        return Hash(text.ToString());
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------
    // the records: one JSON object per line, in a file per run (several runs on the same folder at the same time - shards - never write into one file)
    //   {"run":"...","t":<ticks>,"unit":"...","key":"...","ok":true|false,"title":"...","detail":"..."}    a check
    //   {"run":"...","t":<ticks>,"unit":"...","key":"...","done":true,"ok":true|false,"n":<checks>}        the unit has finished

    /// <summary>The units whose last finished run (with that key) had only OK checks, with those checks - from the lines of all record files. A line that is no
    /// JSON (the last line of a run that was killed while writing) is left out.</summary>
    public static Dictionary<(string Unit, string Key), List<Check>> Parse(IEnumerable<string> lines)
    {
        var checks = new Dictionary<(string Unit, string Key, string Run), List<Check>>();
        var done = new Dictionary<(string Unit, string Key), (long Time, string Run, bool Ok, int Count)>();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string unit = root.GetProperty("unit").GetString()!, key = root.GetProperty("key").GetString()!, run = root.GetProperty("run").GetString()!;
                if (unit.Length == 0) continue;   // (a check outside units: only for the reader of the file)
                bool ok = root.GetProperty("ok").GetBoolean();
                if (root.TryGetProperty("done", out var d) && d.GetBoolean())
                {
                    long time = root.GetProperty("t").GetInt64();
                    if (!done.TryGetValue((unit, key), out var known) || known.Time <= time) done[(unit, key)] = (time, run, ok, root.GetProperty("n").GetInt32());
                    continue;
                }
                if (!checks.TryGetValue((unit, key, run), out var list)) checks[(unit, key, run)] = list = new();
                list.Add(new Check(ok, root.GetProperty("title").GetString()!, root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        var resumable = new Dictionary<(string Unit, string Key), List<Check>>();
        foreach (var ((unit, key), last) in done)
        {
            if (!last.Ok || last.Count < 1) continue;
            if (!checks.TryGetValue((unit, key, last.Run), out var list) || list.Count != last.Count || list.Any(c => !c.Ok)) continue;
            resumable[(unit, key)] = list;
        }
        return resumable;
    }

    /// <summary>One record as a line (see above).</summary>
    public static string Format(string run, long time, string unit, string key, bool ok, string? title = null, string? detail = null, int? doneCount = null)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("run", run);
            json.WriteNumber("t", time);
            json.WriteString("unit", unit);
            json.WriteString("key", key);
            if (doneCount != null) { json.WriteBoolean("done", true); json.WriteBoolean("ok", ok); json.WriteNumber("n", doneCount.Value); }
            else
            {
                json.WriteBoolean("ok", ok);
                json.WriteString("title", title ?? "");
                if (detail != null) json.WriteString("detail", detail);
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static Dictionary<(string Unit, string Key), List<Check>> Resumable
    {
        get
        {
            lock (_gate)
            {
                if (_resumable != null) return _resumable;
                if (Folder == null || !Directory.Exists(Folder)) return _resumable = new();
                var lines = new List<string>();
                foreach (var file in Directory.EnumerateFiles(Folder, "*.jsonl"))
                    try { lines.AddRange(File.ReadAllLines(file)); } catch (IOException) { }
                return _resumable = Parse(lines);
            }
        }
    }

    static long _lastTime;
    /// <summary>(strictly increasing within the run: two records in the same tick keep their order)</summary>
    static long Now() { long t = DateTime.UtcNow.Ticks; lock (_gate) return _lastTime = Math.Max(t, _lastTime + 1); }

    static void Write(string line, bool durable)
    {
        if (Folder == null) return;
        lock (_gate)
        {
            if (_writeFailed) return;
            try
            {
                if (_writer == null)
                {
                    Directory.CreateDirectory(Folder);
                    var stream = new FileStream(Path.Combine(Folder, _run + ".jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
                }
                _writer.Write(line);
                _writer.Write('\n');
                _writer.Flush();
                if (durable) ((FileStream)_writer.BaseStream).Flush(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // (the disk is full or the folder gone: the run goes on, it only cannot be resumed from here on)
                _writeFailed = true;
                // (not here, under the lock: the writer of the output may wait for it)
                string message = $"(FIRE_TESTS_RESUME: die Aufzeichnung in {Folder} ist abgebrochen: {ex.Message})";
                ThreadPool.QueueUserWorkItem(_ => TestProgress.Note(message));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------
    // units

    /// <summary>Whether a unit runs here: <see cref="Decision.OtherShard"/> when its title belongs to another part (FIRE_TESTS_SHARD; not for a unit inside a
    /// unit: that runs with the one around it), <see cref="Decision.Resumed"/> when its last finished run with this key had only OK checks
    /// (FIRE_TESTS_RESUME) - then <see cref="Reprint"/> prints them -, else <see cref="Decision.Run"/>.</summary>
    public static Decision Decide(string unit, string key)
    {
        if (!Active) return Decision.Run;
        Decision decision;
        if (Shard.Count > 1 && _current.Value == null && ShardOf(unit, Shard.Count) != Shard.Index) decision = Decision.OtherShard;
        else if (Folder != null && Environment.GetEnvironmentVariable("FIRE_TESTS_UPDATE") != "1" && Resumable.ContainsKey((unit, key))) decision = Decision.Resumed;
        else decision = Decision.Run;
        lock (_gate) _decided[unit] = decision;
        return decision;
    }

    /// <summary><see cref="Decide"/>; when the unit was resumed its checks are printed again at once. True: the unit runs here.</summary>
    public static bool Pending(string unit, string key)
    {
        var decision = Decide(unit, key);
        if (decision == Decision.Resumed) Reprint(unit, key);
        return decision == Decision.Run;
    }

    /// <summary>Prints the recorded checks of a resumed unit: "OK (fortgesetzt): title" (counted like "OK:"; recorded again for a unit around it).</summary>
    public static void Reprint(string unit, string key)
    {
        if (!Resumable.TryGetValue((unit, key), out var checks)) return;
        foreach (var check in checks) Console.WriteLine(ResumedPrefix + check.Title);
        Interlocked.Add(ref _resumedChecks, checks.Count);
    }

    public const string ResumedPrefix = "OK (fortgesetzt): ";

    /// <summary>Runs <paramref name="body"/> - where the checks of the unit are printed - as the unit, and marks it finished (only when it returns: a unit
    /// whose body throws runs again next time).</summary>
    public static void Run(string unit, string key, Action body)
    {
        using var scope = Unit(unit, key);
        body();
        scope.Done();
    }

    /// <summary>The checks printed until Dispose (on this thread and what it starts) belong to the unit; <see cref="Scope.Done"/> marks it finished. A unit may be
    /// opened several times before it is done (a case whose checks are printed in two places).</summary>
    public static Scope Unit(string unit, string key)
    {
        var scope = new Scope(unit, key, _current.Value);
        _current.Value = scope;
        return scope;
    }

    /// <summary>Records the last checks of a unit and marks it finished, without printing them (a worker that has the result before its line is printed in
    /// the order of the cases: the line is printed later under <see cref="Unrecorded"/>).</summary>
    public static void Complete(string unit, string key, params Check[] checks)
    {
        if (Folder == null) return;
        lock (_gate)
        {
            var (count, failed) = _thisRun.GetValueOrDefault((unit, key));
            foreach (var check in checks)
            {
                count++;
                failed |= !check.Ok;
                string? detail = check.Detail is { Length: > 4000 } long_ ? long_.Substring(0, 4000) + " ..." : check.Detail;
                Write(Format(_run, Now(), unit, key, check.Ok, check.Title, check.Ok ? null : detail), durable: false);
            }
            _thisRun[(unit, key)] = (count, failed);
            Write(Format(_run, Now(), unit, key, !failed, doneCount: count), durable: true);
        }
    }

    /// <summary>The checks printed until Dispose are not recorded (they were, by <see cref="Complete"/>).</summary>
    public static Scope Unrecorded()
    {
        var scope = new Scope("", "", _current.Value) { Recorded = false };
        _current.Value = scope;
        return scope;
    }

    /// <summary>A group of checks as one unit: <paramref name="run"/> is not called when the group belongs to another part or was resumed.</summary>
    public static T Group<T>(string name, Func<T> run, T skipped)
    {
        if (!Active) return run();
        // (the groups read the cases of tests/cases at run time: a changed case file changes the key of every group)
        string key = Key("group", name, CasesHash);
        if (!Pending("Gruppe: " + name, key)) return skipped;
        using var scope = Unit("Gruppe: " + name, key);
        var result = run();
        // (a group that returns its failures: one that failed is not finished - it runs again)
        if (result is not int failures || failures == 0) scope.Done();
        return result;
    }

    public static void Group(string name, Action run) => Group(name, () => { run(); return 0; }, 0);

    public sealed class Scope : IDisposable
    {
        public readonly string Name, Key;
        public readonly Scope? Parent;
        internal bool Recorded = true;
        internal Check? PendingFailure;
        internal StringBuilder? PendingDetail;
        bool _disposed;

        internal Scope(string name, string key, Scope? parent) { Name = name; Key = key; Parent = parent; }

        /// <summary>The unit has finished: its checks so far are its result.</summary>
        public void Done()
        {
            lock (_gate)
            {
                FlushFailure();
                var (checks, failed) = _thisRun.GetValueOrDefault((Name, Key));
                Write(Format(_run, Now(), Name, Key, !failed, doneCount: checks), durable: true);
            }
        }

        internal void FlushFailure()
        {
            if (PendingFailure == null) return;
            string? detail = PendingDetail?.ToString().TrimEnd('\n');
            if (detail != null && detail.Length > 4000) detail = detail.Substring(0, 4000) + " ...";
            Write(Format(_run, Now(), Name, Key, false, PendingFailure.Title, string.IsNullOrEmpty(detail) ? null : detail), durable: false);
            PendingFailure = null;
            PendingDetail = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_gate) FlushFailure();
            if (_current.Value == this) _current.Value = Parent;
        }
    }

    /// <summary>Every whole line of the output, on the thread that wrote it (TestProgress's writer, under its lock): a check line is recorded for the units
    /// the thread is in (or, outside units, under the unit "" - only for the reader of the file); the lines after a FEHLER are its detail.</summary>
    internal static void Observe(string line)
    {
        if (!Active) return;
        bool? ok = null;
        string title = "";
        bool resumed = false;
        if (line.StartsWith("OK: ", StringComparison.Ordinal)) { ok = true; title = line.Substring(4); }
        else if (line.StartsWith(ResumedPrefix, StringComparison.Ordinal)) { ok = true; title = line.Substring(ResumedPrefix.Length); resumed = true; }
        else if (line.StartsWith("FEHLER: ", StringComparison.Ordinal) && !(line.Length > 8 && char.IsDigit(line[8]) && line.EndsWith("fehlgeschlagen.", StringComparison.Ordinal)))
        { ok = false; title = line.Substring(8); }
        var scope = _current.Value;
        if (scope is { Recorded: false }) return;
        if (ok == null && scope != null && line.StartsWith("FEHLER: ", StringComparison.Ordinal))
        {
            // the summary of a group ("FEHLER: 3 ...-Pruefung(en) fehlgeschlagen."): no check of its own, but the units it stands in failed
            lock (_gate)
                for (var s = scope; s != null; s = s.Parent)
                    _thisRun[(s.Name, s.Key)] = (_thisRun.GetValueOrDefault((s.Name, s.Key)).Checks, true);
            return;
        }
        lock (_gate)
        {
            if (ok == null)
            {
                for (var s = scope; s != null; s = s.Parent) s.PendingDetail?.Append(line).Append('\n');
                return;
            }
            if (scope == null)
            {
                if (!resumed) Write(Format(_run, Now(), "", "", ok.Value, title), durable: false);
                return;
            }
            for (var s = scope; s != null; s = s.Parent)
            {
                s.FlushFailure();
                var (checks, failed) = _thisRun.GetValueOrDefault((s.Name, s.Key));
                _thisRun[(s.Name, s.Key)] = (checks + 1, failed || !ok.Value);
                if (ok.Value) Write(Format(_run, Now(), s.Name, s.Key, true, title), durable: false);
                else { s.PendingFailure = new Check(false, title, null); s.PendingDetail = new StringBuilder(); }
            }
        }
    }

    /// <summary>At the start: what is on (a line), a wrong FIRE_TESTS_SHARD.</summary>
    public static void Announce()
    {
        string? shard = Environment.GetEnvironmentVariable("FIRE_TESTS_SHARD");
        if (!string.IsNullOrWhiteSpace(shard) && Shard.Count == 1 && shard.Trim() != "1/1")
            Console.WriteLine($"(FIRE_TESTS_SHARD={shard}: kein Teil i/n mit 1 <= i <= n - es laeuft alles)");
        if (!Active) return;
        var started = DateTime.UtcNow;
        _ = Global;   // (the hash of the build: once, now)
        if (Folder != null) Console.WriteLine($"(FIRE_TESTS_RESUME: Aufzeichnung in {Path.Combine(Folder, _run + ".jsonl")}; {Resumable.Count} Einheit(en) zum Fortsetzen bekannt; Schluessel des Builds {Global} in {(DateTime.UtcNow - started).TotalSeconds:0.0} s)");
        if (Shard.Count > 1) Console.WriteLine($"(FIRE_TESTS_SHARD: Teil {Shard.Index} von {Shard.Count})");
    }

    /// <summary>The lines at the end of a run: how many units ran, were resumed, belong to other parts (the count of every part).</summary>
    public static IEnumerable<string> Summary()
    {
        if (!Active) yield break;
        Dictionary<string, Decision> decided;
        lock (_gate) decided = new(_decided);
        int ran = decided.Values.Count(d => d == Decision.Run), resumed = decided.Values.Count(d => d == Decision.Resumed), other = decided.Values.Count(d => d == Decision.OtherShard);
        if (Folder != null)
            yield return $"[ resume ] {resumed} Einheit(en) fortgesetzt ({Volatile.Read(ref _resumedChecks)} Pruefung(en) 'OK (fortgesetzt)'), {ran} gelaufen; Aufzeichnung: {Path.Combine(Folder, _run + ".jsonl")}";
        if (Shard.Count > 1)
        {
            var perShard = Enumerable.Range(1, Shard.Count).Select(i => $"{i}/{Shard.Count}: {decided.Keys.Count(u => ShardOf(u, Shard.Count) == i)}");
            yield return $"[ shard ] Teil {Shard.Index}/{Shard.Count}: {ran + resumed} Einheit(en) hier, {other} in den anderen Teilen (Einheiten je Teil - {string.Join(", ", perShard)}); Pruefungen ausserhalb von Einheiten laufen in jedem Teil";
        }
    }
}
