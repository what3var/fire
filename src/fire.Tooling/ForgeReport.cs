using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using fire.Tooling;
namespace fire.Tooling
{
    /// <summary>
    /// The report of `forge build|publish --report json` (docs/tools/forge.md, "The report for tools"): what a tool that starts forge - spark - needs to show a build, as JSON lines on
    /// stdout, one object per line, each with a "type": <c>start</c>, <c>progress</c> (the phase, its text and count), <c>background</c> (work without a build window: the standard
    /// packages, the natives of a package), <c>log</c> (a line of the build log), <c>diagnostic</c> (an error or warning with file, line, column, code), <c>output</c> (what was
    /// written), <c>project</c> (one project of a publish or of `--all`) and, last, <c>end</c> (ok, the exit code, cancelled, the summary, the first error). The writer is
    /// <see cref="ForgeReportWriter"/>, the reader <see cref="ForgeReport.TryParse"/>; a reader ignores types and fields it does not know (the format only grows).
    /// </summary>
    public static class ForgeReport
    {
        /// <summary>The version of the format (in <c>start</c>): raised only when a reader of the old one would misread the new one.</summary>
        public const int Version = 1;

        /// <summary>The line spark writes to forge's input to cancel the build (on every system; on Linux and macOS SIGTERM does the same).</summary>
        public const string CancelLine = "cancel";

        /// <summary>The exit code of a cancelled build.</summary>
        public const int ExitCancelled = 3;

        /// <summary>The names of the phases in the report (BuildPhase).</summary>
        public static string PhaseName(BuildPhase phase) => phase switch
        {
            BuildPhase.CompileFire => "compile-fire",
            BuildPhase.PackageNatives => "package-natives",
            BuildPhase.GenerateC => "generate-c",
            BuildPhase.CompileC => "compile-c",
            BuildPhase.Link => "link",
            BuildPhase.Flash => "flash",
            BuildPhase.Pack => "pack",
            _ => phase.ToString().ToLowerInvariant(),
        };

        public static BuildPhase? ParsePhase(string? name) => name switch
        {
            "compile-fire" => BuildPhase.CompileFire,
            "package-natives" => BuildPhase.PackageNatives,
            "generate-c" => BuildPhase.GenerateC,
            "compile-c" => BuildPhase.CompileC,
            "link" => BuildPhase.Link,
            "flash" => BuildPhase.Flash,
            "pack" => BuildPhase.Pack,
            _ => null,
        };

        /// <summary>Reads one line of the report: false when it is no object of the report (no JSON, no "type") - a reader shows such a line as a line of the log.</summary>
        public static bool TryParse(string line, out ForgeEvent ev)
        {
            ev = null!;
            string text = line.Trim();
            if (text.Length < 2 || text[0] != '{') return false;
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return false;
                string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                int Int(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
                bool? Bool(string name) => root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
                string type = t.GetString()!;
                Diagnostic? diagnostic = null;
                if (type == "diagnostic")
                    diagnostic = new Diagnostic(Int("line"), Str("message") ?? "", Str("severity") == "warning" ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, Str("code") ?? "", Str("file"), Int("column"));
                ev = new ForgeEvent(type)
                {
                    Text = Str("text"),
                    Phase = ParsePhase(Str("phase")),
                    Done = Int("done"),
                    Total = Int("total"),
                    Diagnostic = diagnostic,
                    Kind = Str("kind"),
                    Path = Str("path"),
                    Project = Str("project") ?? (type == "project" ? Str("name") : null),
                    Ok = Bool("ok") ?? false,
                    Skipped = Bool("skipped") ?? false,
                    UpToDate = Bool("upToDate") ?? false,
                    Cancelled = Bool("cancelled") ?? false,
                    ExitCode = Int("exitCode"),
                    Summary = Str("summary"),
                    Error = Str("error"),
                    Command = Str("command"),
                    FormatVersion = Int("version"),
                };
                return true;
            }
            catch (JsonException) { return false; }
        }
    }

    /// <summary>One object of the report (<see cref="ForgeReport"/>): <see cref="Type"/> says which of the fields mean something.</summary>
    public sealed record ForgeEvent(string Type)
    {
        /// <summary>progress, background, log: the text (background without a text: the work is over).</summary>
        public string? Text { get; init; }
        public BuildPhase? Phase { get; init; }
        public int Done { get; init; }
        public int Total { get; init; }
        public Diagnostic? Diagnostic { get; init; }
        /// <summary>output: program, folder (a toolchain that only writes files, a publish folder), package.</summary>
        public string? Kind { get; init; }
        public string? Path { get; init; }
        /// <summary>output, diagnostic: the project it belongs to; project: its name.</summary>
        public string? Project { get; init; }
        public bool Ok { get; init; }
        public bool Skipped { get; init; }
        public bool UpToDate { get; init; }
        public bool Cancelled { get; init; }
        public int ExitCode { get; init; }
        public string? Summary { get; init; }
        public string? Error { get; init; }
        public string? Command { get; init; }
        public int FormatVersion { get; init; }

        public BuildProgress? Progress => Type == "progress" && Phase is { } phase ? new BuildProgress(phase, Text ?? "", Done, Total) : null;
    }

    /// <summary>Where a build that reports (`--report json`, `--all`) says what happens: as the report for tools (<see cref="ForgeReportWriter"/>) or as text for people
    /// (<see cref="ForgeTextReporter"/>).</summary>
    public abstract class ForgeReporter
    {
        /// <summary>The project the following events belong to (`--all`, publish); null: none.</summary>
        public string? Project { get; set; }
        public abstract void Start(string command, string? what);
        public abstract void Progress(BuildProgress p);
        public abstract void Background(BackgroundWork.Item? item);
        /// <summary>A line of the build log.</summary>
        public abstract void Log(string line);
        /// <summary>The whole log of a step written when it is done (NativeBuildResult.Log): only its lines that have not come as they happened.</summary>
        public abstract void LogBlock(string? log);
        public abstract void Diagnostic(Diagnostic d);
        /// <summary>What the build wrote: <paramref name="kind"/> program, folder, package or publish.</summary>
        public abstract void Output(string kind, string path);
        public abstract void ProjectResult(string name, bool ok, bool skipped = false, bool upToDate = false, string? error = null);
        /// <summary>The end: nothing is reported after it.</summary>
        public abstract void End(bool ok, int exitCode, bool cancelled = false, string? summary = null, string? error = null);
    }

    /// <summary>The text for people (`forge build --all` without `--report`): the log on stdout, the errors and warnings on stderr.</summary>
    public sealed class ForgeTextReporter(TextWriter stdout, TextWriter stderr) : ForgeReporter
    {
        public override void Start(string command, string? what) { }
        public override void Progress(BuildProgress p) { }
        public override void Background(BackgroundWork.Item? item) { }
        public override void Log(string line) { lock (stdout) stdout.WriteLine(line); }
        public override void LogBlock(string? log)
        {
            if (string.IsNullOrEmpty(log)) return;
            lock (stdout) stdout.Write(log.EndsWith('\n') ? log : log + Environment.NewLine);
        }
        public override void Diagnostic(Diagnostic d) { lock (stderr) stderr.WriteLine(d.ToLogLine()); }
        public override void Output(string kind, string path) { }
        public override void ProjectResult(string name, bool ok, bool skipped = false, bool upToDate = false, string? error = null) { }
        public override void End(bool ok, int exitCode, bool cancelled = false, string? summary = null, string? error = null)
        {
            if (summary != null) Log(summary);
            if (!ok && error != null) lock (stderr) stderr.WriteLine(error);
        }
    }

    /// <summary>Writes the report (<see cref="ForgeReport"/>) - from any thread, each object as one line, flushed at once (the reader shows the build as it goes).</summary>
    public sealed class ForgeReportWriter : ForgeReporter
    {
        private readonly TextWriter _out;
        private readonly object _lock = new();
        /// <summary>The lines of the log written so far (a block of lines written at the end, the log of a native build, leaves out what came already).</summary>
        private readonly Dictionary<string, int> _logged = new(StringComparer.Ordinal);
        private bool _ended;

        public ForgeReportWriter(TextWriter output) => _out = output;

        private void Write(Action<Utf8JsonWriter> fields)
        {
            var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                json.WriteStartObject();
                fields(json);
                json.WriteEndObject();
            }
            string line = Encoding.UTF8.GetString(buffer.ToArray());
            lock (_lock)
            {
                if (_ended) return;
                _out.WriteLine(line);
                _out.Flush();
            }
        }

        private static void Opt(Utf8JsonWriter json, string name, string? value)
        {
            if (value != null) json.WriteString(name, value);
        }

        public override void Start(string command, string? what) => Write(j =>
        {
            j.WriteString("type", "start");
            j.WriteNumber("version", ForgeReport.Version);
            j.WriteString("command", command);
            Opt(j, "text", what);
            j.WriteNumber("pid", Environment.ProcessId);
        });

        public override void Progress(BuildProgress p) => Write(j =>
        {
            j.WriteString("type", "progress");
            j.WriteString("phase", ForgeReport.PhaseName(p.Phase));
            j.WriteString("text", p.Text);
            j.WriteNumber("done", p.Done);
            j.WriteNumber("total", p.Total);
        });

        public override void Background(BackgroundWork.Item? item) => Write(j =>
        {
            j.WriteString("type", "background");
            if (item is { } i)
            {
                j.WriteString("text", i.Text);
                j.WriteNumber("done", i.Done);
                j.WriteNumber("total", i.Total);
            }
        });

        public override void Log(string line)
        {
            line = line.TrimEnd('\r', '\n');
            lock (_lock) _logged[line.TrimEnd()] = _logged.TryGetValue(line.TrimEnd(), out int n) ? n + 1 : 1;
            Write(j => { j.WriteString("type", "log"); j.WriteString("text", line); });
        }

        public override void LogBlock(string? log)
        {
            if (string.IsNullOrEmpty(log)) return;
            foreach (string raw in log.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
            {
                string key = raw.TrimEnd();
                bool seen;
                lock (_lock)
                {
                    seen = _logged.TryGetValue(key, out int n) && n > 0;
                    if (seen) _logged[key] = n - 1;
                }
                if (!seen) Write(j => { j.WriteString("type", "log"); j.WriteString("text", raw.TrimEnd('\r')); });
            }
        }

        public override void Diagnostic(Diagnostic d) => Write(j =>
        {
            j.WriteString("type", "diagnostic");
            j.WriteString("severity", d.IsWarning ? "warning" : "error");
            j.WriteString("code", d.Code);
            if (d.File != null) j.WriteString("file", d.File); else j.WriteNull("file");
            j.WriteNumber("line", d.Line);
            j.WriteNumber("column", d.Column);
            j.WriteString("message", d.Message);
            Opt(j, "project", Project);
        });

        public override void Output(string kind, string path) => Write(j =>
        {
            j.WriteString("type", "output");
            j.WriteString("kind", kind);
            j.WriteString("path", path);
            Opt(j, "project", Project);
        });

        public override void ProjectResult(string name, bool ok, bool skipped = false, bool upToDate = false, string? error = null) => Write(j =>
        {
            j.WriteString("type", "project");
            j.WriteString("name", name);
            j.WriteBoolean("ok", ok);
            j.WriteBoolean("skipped", skipped);
            j.WriteBoolean("upToDate", upToDate);
            Opt(j, "error", error);
        });

        public override void End(bool ok, int exitCode, bool cancelled = false, string? summary = null, string? error = null)
        {
            Write(j =>
            {
                j.WriteString("type", "end");
                j.WriteBoolean("ok", ok);
                j.WriteNumber("exitCode", exitCode);
                j.WriteBoolean("cancelled", cancelled);
                Opt(j, "summary", summary);
                Opt(j, "error", error);
            });
            lock (_lock) _ended = true;
        }

        /// <summary>A writer whose lines become lines of the log (Console.Out while the report is written: nothing else gets onto stdout).</summary>
        public TextWriter LogWriter() => new LineWriter(this);

        private sealed class LineWriter(ForgeReportWriter report) : TextWriter
        {
            private readonly StringBuilder _line = new();
            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char value)
            {
                lock (_line)
                {
                    if (value == '\n')
                    {
                        string text = _line.ToString();
                        _line.Clear();
                        report.Log(text);
                    }
                    else _line.Append(value);
                }
            }
            /// <summary>(A flush in the middle of a line does not end it; <see cref="Close"/> writes what is left.)</summary>
            public override void Flush() { }
            public override void Close()
            {
                lock (_line)
                {
                    if (_line.Length == 0) return;
                    string text = _line.ToString();
                    _line.Clear();
                    report.Log(text);
                }
            }
        }
    }
}
