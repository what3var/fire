using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using fire.Compiler;

namespace fire.Editor
{
    /// <summary>The diagnostics `forge serve` pushes for a document (docs/spec/FORGE_SERVE.md "Diagnosen"): the version they belong to, whether a newer version was
    /// asked for while they were computed (`Stale`), and the time of the analysis.</summary>
    public sealed record ServeDiagnostics(string Path, int Version, bool Stale, int Milliseconds, List<Diagnostic> Items);

    /// <summary>A file the server knows besides a document: its path and text (null: the server reads the file).</summary>
    public sealed record ServeFile(string Path, string? Text);

    /// <summary>A library of a project for the analysis (`ProjectLibraries`): the names `#import` finds it by, its import name and namespace, what it needs, its files.</summary>
    public sealed record ServeLibrary(IReadOnlyList<string> Names, string Import, string Name, string? Namespace, IReadOnlyList<string> Requires, IReadOnlyList<ServeFile> Files);

    /// <summary>
    /// The client of the language services as a process (docs/spec/FORGE_SERVE.md, docs/ROADMAP_C_PORT.md L4 and Phase C step 6): starts `forge-c serve`, keeps the open
    /// documents and the projects up to date (`open`/`change`/`close`/`project`), asks for the completion and the other answers, and raises <see cref="Diagnostics"/> for what
    /// the server pushes. One JSON message per line over stdio; requests wait for their answer (the server answers them in order, on its main thread - the completion is a few
    /// milliseconds), the diagnostics come from the server's analysis thread.
    ///
    /// The switch: <c>FIRE_SPARK_LANG=inproc</c> keeps the language services in spark's own process (ScriptSymbolIndex, CompletionEngine, LiveDiagnostics) until M2; without it
    /// they go over the protocol, and back to the own process when the server cannot be started or has died.
    /// </summary>
    public sealed class ServeClient : IDisposable
    {
        /// <summary>The language services run in spark's own process (<c>FIRE_SPARK_LANG=inproc</c>).</summary>
        public static bool InProcess => string.Equals(Environment.GetEnvironmentVariable("FIRE_SPARK_LANG"), "inproc", StringComparison.OrdinalIgnoreCase);

        private readonly Process _process;
        private readonly object _writeLock = new();
        private readonly Dictionary<long, TaskCompletionSource<JsonNode?>> _waiting = new();
        private long _nextId;
        private volatile bool _dead;
        private readonly StringBuilder _stderr = new();

        /// <summary>A push of the server: the diagnostics of a document. Raised on the reader thread of the client.</summary>
        public event Action<ServeDiagnostics>? Diagnostics;

        /// <summary>The server asks for the whole text of a document again (an edit did not fit its text).</summary>
        public event Action<string>? Resync;

        /// <summary>What the server said it can do (`initialize`).</summary>
        public IReadOnlyList<string> Capabilities { get; private set; } = Array.Empty<string>();

        public bool IsAlive => !_dead && !_process.HasExited;

        /// <summary>What the server wrote to stderr (for a report when it died).</summary>
        public string ErrorOutput { get { lock (_stderr) return _stderr.ToString(); } }

        private ServeClient(Process process)
        {
            _process = process;
            var reader = new Thread(ReadLoop) { IsBackground = true, Name = "forge serve reader" };
            reader.Start();
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) { if (_stderr.Length < 64 * 1024) _stderr.AppendLine(e.Data); } };
            _process.BeginErrorReadLine();
        }

        /// <summary>Starts `exe serve` and initializes it: the prelude of this program (written into a folder of tables), the installed packages, the debounce.</summary>
        public static ServeClient Start(string exe, string? packages = null, int debounceMs = 300, string? log = null)
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            info.ArgumentList.Add("serve");
            info.ArgumentList.Add("--tables");
            info.ArgumentList.Add(TablesFolder());
            info.ArgumentList.Add("--packages");
            info.ArgumentList.Add(packages ?? fire.Package.Manager.PackageStore.Default.Root);
            if (log != null) { info.ArgumentList.Add("--log"); info.ArgumentList.Add(log); }
            var process = Process.Start(info) ?? throw new IOException("forge serve did not start.");
            var client = new ServeClient(process);
            var result = client.Request("initialize", new JsonObject { ["client"] = "spark", ["debounce"] = debounceMs }, 20000);
            client.Capabilities = result?["capabilities"]?.AsArray().Select(c => c!.GetValue<string>()).ToList() ?? new List<string>();
            return client;
        }

        /// <summary>A folder with the prelude of this program (`prelude.fire`, as `forge tables` writes it): the server reads the same standard library spark compiles with.</summary>
        private static string TablesFolder()
        {
            string prelude = fire.Standard.Prelude.Source.Replace("\r\n", "\n");
            string folder = Path.Combine(Path.GetTempPath(), "fire-serve-tables-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(prelude)))[..16]);
            string file = Path.Combine(folder, "prelude.fire");
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(folder);
                string temp = file + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(temp, prelude, new UTF8Encoding(false));
                try { File.Move(temp, file, true); } catch (IOException) { /* another process wrote it */ }
            }
            return folder;
        }

        // ---- the program ----------------------------------------------------------------------------------------------------------------------------------

        private static readonly object FindLock = new();
        private static string? _found;
        private static string? _findError;

        /// <summary>The program `forge-c` (native/forge): <c>FIRE_FORGE_C</c>, next to spark, else built from native/forge/forge.c of the repository with the C compiler of
        /// the machine (once, into a cache keyed by the sources). Null with a reason when there is none.</summary>
        public static string? FindExecutable(out string error)
        {
            lock (FindLock)
            {
                if (_found != null || _findError != null) { error = _findError ?? ""; return _found; }
                _found = Find(out string why);
                _findError = _found == null ? why : null;
                error = why;
                return _found;
            }
        }

        private static string? Find(out string error)
        {
            error = "";
            string exeName = OperatingSystem.IsWindows() ? "forge-c.exe" : "forge-c";
            if (Environment.GetEnvironmentVariable("FIRE_FORGE_C") is { Length: > 0 } env)
            {
                if (File.Exists(env)) return env;
                error = $"FIRE_FORGE_C={env} does not exist";
                return null;
            }
            string beside = Path.Combine(AppContext.BaseDirectory, exeName);
            if (File.Exists(beside)) return beside;
            string? source = null;
            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
                for (var dir = new DirectoryInfo(start); dir != null && source == null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "native", "forge", "forge.c"))) source = Path.Combine(dir.FullName, "native", "forge", "forge.c");
            if (source == null) { error = "neither forge-c nor native/forge/forge.c found"; return null; }
            // the cache: the sources' names, sizes and times
            string forgeDir = Path.GetDirectoryName(source)!;
            var stamp = new StringBuilder();
            foreach (var f in Directory.GetFiles(forgeDir).Concat(Directory.GetFiles(Path.Combine(forgeDir, "..", "tables"))).Concat(Directory.GetFiles(Path.Combine(forgeDir, "..", "base"))).OrderBy(f => f, StringComparer.Ordinal))
            {
                var fi = new FileInfo(f);
                stamp.Append(fi.Name).Append(fi.Length).Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            }
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString())))[..16];
            string cache = Path.Combine(Path.GetTempPath(), "fire-forge-c-" + hash);
            string exe = Path.Combine(cache, exeName);
            if (File.Exists(exe)) return exe;
            Directory.CreateDirectory(cache);
            foreach (var cc in OperatingSystem.IsWindows() ? new[] { "gcc", "clang" } : new[] { "cc", "gcc", "clang" })
            {
                try
                {
                    var info = new ProcessStartInfo(cc) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                    string temp = exe + "." + Environment.ProcessId + ".tmp";
                    foreach (var a in new[] { "-O2", source, "-o", temp, "-lm" }) info.ArgumentList.Add(a);
                    if (!OperatingSystem.IsWindows()) info.ArgumentList.Add("-lpthread");
                    using var p = Process.Start(info);
                    if (p == null) continue;
                    var err = p.StandardError.ReadToEndAsync();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(600000)) { try { p.Kill(); } catch (Exception) { } error = cc + ": timeout"; continue; }
                    if (p.ExitCode != 0) { error = cc + ": " + err.Result; continue; }
                    try { File.Move(temp, exe, true); } catch (IOException) { /* another process built it */ }
                    return exe;
                }
                catch (System.ComponentModel.Win32Exception) { error = "no C compiler (cc, gcc, clang)"; }
            }
            return null;
        }

        private static ServeClient? _shared;
        private static readonly object SharedLock = new();
        private static bool _sharedFailed;

        /// <summary>The server of this process, started on first use; null in the in-process mode, or when it cannot be started (then the own process serves).</summary>
        public static ServeClient? Shared
        {
            get
            {
                if (InProcess) return null;
                lock (SharedLock)
                {
                    if (_shared != null && _shared.IsAlive) return _shared;
                    if (_shared != null) { _shared = null; _sharedFailed = false; }   // it died: once more
                    if (_sharedFailed) return null;
                    try
                    {
                        var exe = FindExecutable(out _);
                        if (exe == null) { _sharedFailed = true; return null; }
                        _shared = Start(exe, log: Environment.GetEnvironmentVariable("FIRE_SERVE_LOG"));
                        return _shared;
                    }
                    catch (Exception) { _sharedFailed = true; return null; }
                }
            }
        }

        // ---- messages ---------------------------------------------------------------------------------------------------------------------------------------

        private void Write(JsonObject message)
        {
            string line = message.ToJsonString();
            lock (_writeLock)
            {
                if (_dead) throw new IOException("forge serve has ended.");
                try
                {
                    _process.StandardInput.Write(line);
                    _process.StandardInput.Write('\n');
                    _process.StandardInput.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { _dead = true; throw new IOException("forge serve has ended.", ex); }
            }
        }

        /// <summary>A message without an answer.</summary>
        public void Notify(string method, JsonObject? parameters = null)
        {
            var message = new JsonObject { ["method"] = method };
            if (parameters != null) message["params"] = parameters;
            Write(message);
        }

        /// <summary>A request and its result (null for `null`); throws <see cref="ServeException"/> for an error of the server, IOException when the server ended or did not answer
        /// within `timeoutMs`.</summary>
        public JsonNode? Request(string method, JsonObject? parameters = null, int timeoutMs = 10000)
        {
            long id = Interlocked.Increment(ref _nextId);
            var waiting = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiting) _waiting[id] = waiting;
            var message = new JsonObject { ["id"] = id, ["method"] = method };
            if (parameters != null) message["params"] = parameters;
            try
            {
                Write(message);
                if (!waiting.Task.Wait(timeoutMs))
                {
                    try { Notify("cancel", new JsonObject { ["id"] = id }); } catch (IOException) { }
                    throw new IOException($"forge serve did not answer '{method}' within {timeoutMs} ms.");
                }
                return waiting.Task.Result;
            }
            catch (AggregateException ex) when (ex.InnerException != null) { throw ex.InnerException; }
            finally { lock (_waiting) _waiting.Remove(id); }
        }

        private void ReadLoop()
        {
            try
            {
                string? line;
                while ((line = _process.StandardOutput.ReadLine()) != null)
                {
                    JsonNode? node;
                    try { node = JsonNode.Parse(line); }
                    catch (System.Text.Json.JsonException) { continue; }
                    if (node is not JsonObject message) continue;
                    if (message["id"] is { } idNode && message["method"] == null)
                    {
                        long id = idNode.GetValue<long>();
                        TaskCompletionSource<JsonNode?>? waiting;
                        lock (_waiting) _waiting.TryGetValue(id, out waiting);
                        if (waiting == null) continue;
                        if (message["error"] is JsonObject error)
                            waiting.TrySetException(new ServeException(error["code"]?.GetValue<string>() ?? "", error["message"]?.GetValue<string>() ?? ""));
                        else waiting.TrySetResult(message["result"]?.DeepClone());
                        continue;
                    }
                    switch (message["method"]?.GetValue<string>())
                    {
                        case "diagnostics":
                            if (message["params"] is JsonObject p && Diagnostics is { } handler)
                            {
                                var result = new ServeDiagnostics(p["path"]!.GetValue<string>(), p["version"]!.GetValue<int>(), p["stale"]?.GetValue<bool>() ?? false,
                                    p["ms"]?.GetValue<int>() ?? 0, DiagnosticsOf(p["items"]));
                                try { handler(result); } catch (Exception) { /* a handler's problem is not the protocol's */ }
                            }
                            break;
                        case "resync":
                            if (message["params"]?["path"]?.GetValue<string>() is { } path) Resync?.Invoke(path);
                            break;
                    }
                }
            }
            catch (Exception) { }
            _dead = true;
            lock (_waiting)
                foreach (var waiting in _waiting.Values) waiting.TrySetException(new IOException("forge serve has ended. " + ErrorOutput));
        }

        // ---- documents and projects -------------------------------------------------------------------------------------------------------------------------

        private static JsonArray FilesJson(IEnumerable<ServeFile>? files)
        {
            var array = new JsonArray();
            foreach (var f in files ?? Array.Empty<ServeFile>())
            {
                var o = new JsonObject { ["path"] = f.Path };
                if (f.Text != null) o["text"] = f.Text;
                array.Add(o);
            }
            return array;
        }

        /// <summary>Opens (or replaces) a document: its text and version; `project`: the key of its project (the context of its analysis), `includes`: what the symbol index knows
        /// besides it (the other files of the project, the script of an `#include`d markup file), `kind` `fxml` for a markup file.</summary>
        public void Open(string path, int version, string text, string? project = null, IEnumerable<ServeFile>? includes = null, string? kind = null, string? baseDirectory = null)
        {
            var p = new JsonObject { ["path"] = path, ["version"] = version, ["text"] = text };
            if (project != null) p["project"] = project;
            if (includes != null) p["includes"] = FilesJson(includes);
            if (kind != null) p["kind"] = kind;
            if (baseDirectory != null) p["base"] = baseDirectory;
            Notify("open", p);
        }

        /// <summary>The new text of a document (the whole text).</summary>
        public void ChangeText(string path, int version, string text, IEnumerable<ServeFile>? includes = null) =>
            Notify("change", WithIncludes(new JsonObject { ["path"] = path, ["version"] = version, ["text"] = text }, includes));

        /// <summary>The changes of a document since the last version (the editor's TextEdits with the inserted text), applied in order.</summary>
        public void ChangeEdits(string path, int version, IEnumerable<(int Offset, int Removed, string Inserted)> edits, IEnumerable<ServeFile>? includes = null)
        {
            var array = new JsonArray();
            foreach (var (offset, removed, inserted) in edits) array.Add(new JsonObject { ["offset"] = offset, ["removed"] = removed, ["text"] = inserted });
            Notify("change", WithIncludes(new JsonObject { ["path"] = path, ["version"] = version, ["edits"] = array }, includes));
        }

        private static JsonObject WithIncludes(JsonObject p, IEnumerable<ServeFile>? includes)
        {
            if (includes != null) p["includes"] = FilesJson(includes);
            return p;
        }

        public void Close(string path) => Notify("close", new JsonObject { ["path"] = path });

        /// <summary>The context of the analyses of the documents of a project (what BuildPlan knows): its files, its libraries, the type checking, its own import name when it is a
        /// library.</summary>
        public void Project(string key, IEnumerable<ServeFile> files, IEnumerable<ServeLibrary>? libraries = null, string? typeChecking = null, string? ownImport = null)
        {
            var libs = new JsonArray();
            foreach (var l in libraries ?? Array.Empty<ServeLibrary>())
                libs.Add(new JsonObject
                {
                    ["names"] = new JsonArray(l.Names.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                    ["import"] = l.Import,
                    ["name"] = l.Name,
                    ["namespace"] = l.Namespace,
                    ["requires"] = new JsonArray(l.Requires.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                    ["files"] = FilesJson(l.Files),
                });
            var p = new JsonObject { ["key"] = key, ["files"] = FilesJson(files), ["libraries"] = libs };
            if (typeChecking != null) p["typeChecking"] = typeChecking;
            if (ownImport != null) { p["library"] = true; p["projectImport"] = ownImport; }
            Notify("project", p);
        }

        public void DropProject(string key) => Notify("dropProject", new JsonObject { ["key"] = key });

        /// <summary>The installed packages changed (ScriptSymbolIndex.ForgetImports, LiveDiagnostics.Invalidate on the server's side).</summary>
        public void Invalidate() => Notify("invalidate");

        // ---- questions --------------------------------------------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, CompletionKind> Kinds = new()
        {
            ["keyword"] = CompletionKind.Keyword, ["typeKeyword"] = CompletionKind.TypeKeyword, ["class"] = CompletionKind.ClassName, ["enum"] = CompletionKind.EnumName,
            ["enumMember"] = CompletionKind.EnumMember, ["namespace"] = CompletionKind.Namespace, ["field"] = CompletionKind.Field, ["method"] = CompletionKind.Method,
            ["property"] = CompletionKind.Property, ["variable"] = CompletionKind.Variable, ["parameter"] = CompletionKind.Parameter,
        };

        /// <summary>The suggestions at `offset`, the best first (CompletionEngine.GetSuggestions ordered by score), and where the word at the caret starts.</summary>
        public List<CompletionItem> Completion(string path, int offset, out int replaceStart)
        {
            var result = Request("completion", new JsonObject { ["path"] = path, ["offset"] = offset });
            replaceStart = result?["replaceStart"]?.GetValue<int>() ?? offset;
            var items = new List<CompletionItem>();
            foreach (var node in result?["items"]?.AsArray() ?? new JsonArray())
            {
                if (node is not JsonObject o) continue;
                var kind = Kinds.TryGetValue(o["kind"]?.GetValue<string>() ?? "", out var k) ? k : CompletionKind.Variable;
                string? doc = o["doc"]?.GetValue<string>();
                items.Add(new CompletionItem(o["text"]!.GetValue<string>(), kind, o["score"]?.GetValue<float>() ?? 0f, o["detail"]?.GetValue<string>())
                {
                    Documentation = doc == null ? null : DocComments.Parse(doc),
                });
            }
            return items;
        }

        /// <summary>The analysis of a document right now (without the debounce): its version and its diagnostics.</summary>
        public (int Version, List<Diagnostic> Items) Diagnose(string path, int timeoutMs = 60000)
        {
            var result = Request("diagnose", new JsonObject { ["path"] = path }, timeoutMs);
            return (result?["version"]?.GetValue<int>() ?? 0, DiagnosticsOf(result?["items"]));
        }

        private static List<Diagnostic> DiagnosticsOf(JsonNode? items)
        {
            var list = new List<Diagnostic>();
            foreach (var node in items?.AsArray() ?? new JsonArray())
            {
                if (node is not JsonObject d) continue;
                list.Add(new Diagnostic(d["line"]?.GetValue<int>() ?? 0, d["message"]?.GetValue<string>() ?? "",
                    d["severity"]?.GetValue<string>() == "warning" ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
                    d["code"]?.GetValue<string>() ?? "", d["file"]?.GetValue<string>(), d["column"]?.GetValue<int>() ?? 0));
            }
            return list;
        }

        public void Dispose()
        {
            try
            {
                if (IsAlive)
                {
                    Request("shutdown", null, 2000);
                    Notify("exit");
                }
            }
            catch (Exception) { }
            try { if (!_process.WaitForExit(2000)) _process.Kill(); } catch (Exception) { }
            _dead = true;
            _process.Dispose();
        }
    }

    /// <summary>An error the server answered a request with (`code`: parse, method, params, document, cancelled, internal).</summary>
    public sealed class ServeException : Exception
    {
        public string Code { get; }
        public ServeException(string code, string message) : base($"forge serve: {code}: {message}") => Code = code;
    }

    /// <summary>
    /// What the editors of spark ask the shared server (<see cref="ServeClient.Shared"/>): it keeps the server's copy of each document in step with the editor's text (one edit
    /// per sync: the changed middle between the common start and end), the context of the symbol index (the other files of the project, `#include`d scripts) and the projects of
    /// the analyses (a BuildPlan's files and libraries, sent again only when one of them changed). Every method returns null when the language services run in this process
    /// (<c>FIRE_SPARK_LANG=inproc</c>) or the server is not available - the caller then does it itself (ScriptSymbolIndex, CompletionEngine, LiveDiagnostics).
    /// </summary>
    public static class ServeDocuments
    {
        private sealed class Doc
        {
            public int Version;
            public string? Text;
            public object? Includes;     // (the list of context files last sent: by reference)
            public string? Project;
            public ServeClient? Client;  // (the server it was opened on: a new server knows nothing of it)
        }

        private static readonly Dictionary<string, Doc> Docs = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, (long Hash, ServeClient Client)> Projects = new(StringComparer.Ordinal);

        /// <summary>The path a document has on the server: its file, or a name of its own for an unsaved one.</summary>
        public static string PathOf(string? filePath, object owner) =>
            filePath != null ? Path.GetFullPath(filePath) : Path.Combine(Path.GetTempPath(), $"untitled-{Environment.ProcessId}-{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(owner)}.fire");

        /// <summary>The server has the document with this text (and these context files, this project).</summary>
        private static ServeClient? Sync(string path, string text, IReadOnlyList<(string Path, string Text)>? includes, string? project, string? baseDirectory)
        {
            var client = ServeClient.Shared;
            if (client == null) return null;
            lock (Docs)
            {
                if (!Docs.TryGetValue(path, out var doc) || doc.Client != client) { doc = new Doc { Client = client }; Docs[path] = doc; }
                IEnumerable<ServeFile>? files = includes != null && !ReferenceEquals(includes, doc.Includes) ? includes.Select(f => new ServeFile(f.Path, f.Text)).ToList() : null;
                if (doc.Text == null || (project != null && project != doc.Project))
                {
                    doc.Version++;
                    client.Open(path, doc.Version, text, project ?? doc.Project, files ?? (doc.Includes as IReadOnlyList<(string Path, string Text)>)?.Select(f => new ServeFile(f.Path, f.Text)), baseDirectory: baseDirectory);
                }
                else if (!string.Equals(doc.Text, text, StringComparison.Ordinal) || files != null)
                {
                    doc.Version++;
                    string old = doc.Text;
                    int start = 0, max = Math.Min(old.Length, text.Length);
                    while (start < max && old[start] == text[start]) start++;
                    int end = 0;
                    while (end < max - start && old[old.Length - 1 - end] == text[text.Length - 1 - end]) end++;
                    client.ChangeEdits(path, doc.Version, new[] { (start, old.Length - start - end, text.Substring(start, text.Length - start - end)) }, files);
                }
                doc.Text = text;
                if (includes != null) doc.Includes = includes;
                if (project != null) doc.Project = project;
                return client;
            }
        }

        /// <summary>The suggestions at `offset` over the protocol, the best first; null: not available (do it in this process).</summary>
        public static List<CompletionItem>? Complete(string path, string text, int offset, IReadOnlyList<(string Path, string Text)> context)
        {
            try { return Sync(path, text, context, null, null)?.Completion(path, offset, out _); }
            catch (Exception ex) when (ex is IOException or ServeException) { return null; }
        }

        /// <summary>The diagnostics of a document alone over the protocol; null: not available.</summary>
        public static List<Diagnostic>? Analyze(string path, string text, string? baseDirectory)
        {
            try { return Sync(path, text, null, "", baseDirectory)?.Diagnose(path).Items; }
            catch (Exception ex) when (ex is IOException or ServeException) { return null; }
        }

        /// <summary>The diagnostics of a document of a project over the protocol (the context of the plan: its other files and libraries); null: not available.</summary>
        public static List<Diagnostic>? AnalyzeInProject(string path, string text, string? baseDirectory, fire.Projects.BuildPlan plan, IReadOnlyList<(string Path, string Text)> others)
        {
            var client = ServeClient.Shared;
            if (client == null) return null;
            try
            {
                string key = "document:" + path;
                var libraries = plan.Libraries.Values.Distinct().Select(l => new ServeLibrary(
                    plan.Libraries.Where(e => ReferenceEquals(e.Value, l)).Select(e => e.Key).ToList(), l.ImportName, l.Name,
                    l.Project.Project.Standard ? null : l.ImportName, l.Requires, l.Sources.Select(f => new ServeFile(f.Path, f.Text)).ToList())).ToList();
                var files = others.Select(f => new ServeFile(f.Path, f.Text)).ToList();
                string? ownImport = plan.Type == fire.Projects.OutputType.Library ? plan.Project.Project.ImportName : null;
                long hash = 17;
                foreach (var f in files) hash = hash * 31 + f.Path.GetHashCode() * 7 + (f.Text?.GetHashCode() ?? 0);
                foreach (var l in libraries) { hash = hash * 31 + l.Import.GetHashCode(); foreach (var f in l.Files) hash = hash * 31 + (f.Text?.GetHashCode() ?? 0); }
                hash = hash * 31 + (plan.Settings?.TypeChecking ?? "").GetHashCode() + (ownImport ?? "").GetHashCode();
                lock (Projects)
                {
                    if (!Projects.TryGetValue(key, out var known) || known.Hash != hash || known.Client != client)
                    {
                        client.Project(key, files, libraries, plan.Settings?.TypeChecking, ownImport);
                        Projects[key] = (hash, client);
                    }
                }
                return Sync(path, text, null, key, baseDirectory)?.Diagnose(path).Items;
            }
            catch (Exception ex) when (ex is IOException or ServeException) { return null; }
        }

        /// <summary>The editor of a document closed.</summary>
        public static void Close(string path)
        {
            lock (Docs)
            {
                if (!Docs.Remove(path, out var doc) || doc.Client == null || !doc.Client.IsAlive) return;
                try { doc.Client.Close(path); } catch (IOException) { }
            }
        }

        /// <summary>The installed packages or the workspace changed.</summary>
        public static void Invalidate()
        {
            if (ServeClient.InProcess) return;
            try { ServeClient.Shared?.Invalidate(); } catch (IOException) { }
            lock (Projects) Projects.Clear();
        }
    }
}
