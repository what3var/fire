using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Threading;
using fire.Projects;
using fire.Projects.Mcp;

namespace fire.Editor
{
    /// <summary>
    /// spark as an MCP server (docs/tools/spark-mcp.md): the editor's state, its documents, the errors, the output and the commands Check, Run and Stop as tools
    /// for an AI client - the assistant of spark (docs/ai/assistant.md), or any other MCP client. Off by default (Tools > MCP Server); it listens on 127.0.0.1
    /// only and needs the token of the connection file.
    /// </summary>
    public partial class MainWindow
    {
        private McpServer? _mcp;

        private const string StateUri = "spark://state";
        private const string OutputUri = "spark://output";

        private void StartMcpServerIfWanted()
        {
            if (mnuMcpServer != null) mnuMcpServer.IsChecked = _settings.McpServer;
            if (_settings.McpServer || _settings.Assistant) StartMcpServer();
        }

        private void StartMcpServer()
        {
            if (_mcp != null) return;
            try
            {
                var server = new McpServer("spark", typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "1.0")
                {
                    Instructions = "spark is the editor of the fire language. The tools read the documents of the editor and its errors, open files, change the " +
                                   "selected text, check (compile) the program, run and stop it. search_docs and read_doc find answers in the documentation of fire, " +
                                   "list_samples, list_sample_files and read_sample_file show sample programs. Paths are full paths or paths relative to the solution.",
                };
                RegisterMcpTools(server);
                KnowledgeTools.Register(server, Path.Combine(AppContext.BaseDirectory, "Help", "Docs"), SampleCatalog.BuiltinRoot);
                server.Activity += text => Dispatcher.UIThread.Post(() => UpdateStatus(text));
                server.Start();
                _mcp = server;
                UpdateStatus($"MCP server: {server.Url} (token in {server.ConnectionFile}).");
            }
            catch (Exception ex) { UpdateStatus("The MCP server could not start: " + ex.Message); }
        }

        private void StopMcpServer()
        {
            _mcp?.Stop();
            _mcp = null;
        }

        private void McpServer_Click(object? sender, RoutedEventArgs e)
        {
            _settings.McpServer = !_settings.McpServer;
            _settings.Save();
            if (_settings.McpServer || _settings.Assistant) StartMcpServer();
            else { StopMcpServer(); UpdateStatus("MCP server stopped."); }
            if (sender is Avalonia.Controls.MenuItem item) item.IsChecked = _settings.McpServer;
        }

        // ---- the events the clients hear about ----

        /// <summary>The active document or the run changed: the clients that subscribed to spark://state are told.</summary>
        private void McpStateChanged() => _mcp?.ResourceUpdated(StateUri);

        private void McpBuildFinished(string what, bool ok, int errors, int warnings)
        {
            if (_mcp == null) return;
            _mcp.Log(ok ? "info" : "warning", new JsonObject { ["event"] = "build", ["what"] = what, ["ok"] = ok, ["errors"] = errors, ["warnings"] = warnings }, "spark");
            _mcp.ResourceUpdated(StateUri);
            _checkDone?.TrySetResult(ok);
        }

        private void McpRunChanged(bool running)
        {
            if (_mcp == null) return;
            _mcp.Log("info", new JsonObject { ["event"] = running ? "run started" : "run ended" }, "spark");
            _mcp.ResourceUpdated(StateUri);
            if (!running) _mcp.ResourceUpdated(OutputUri);
        }

        private TaskCompletionSource<bool>? _checkDone;
        /// <summary>A compile of a tool (check): no dialog on an error - the tool gives the errors back.</summary>
        private bool _quietCompile;

        // ---- the tools ----

        private static Task<T> OnUi<T>(Func<T> f) => Dispatcher.UIThread.InvokeAsync(f).GetTask();
        private static Task<T> OnUi<T>(Func<Task<T>> f) => Dispatcher.UIThread.InvokeAsync(f);

        private static string? Str(JsonElement args, string name) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

        /// <summary>A number of the arguments - also from a text that begins with one ("12," of a small model).</summary>
        private static int? Int(JsonElement args, string name) =>
            System.Text.RegularExpressions.Regex.Match(Str(args, name) ?? "", @"^\s*-?\d+") is { Success: true } m && int.TryParse(m.Value, out int n) ? n : null;

        /// <summary>A path of a client: full, or relative to the solution (or to the folder of the active document).</summary>
        private string? Resolve(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            string? baseDir = _workspace.FilePath != null ? Path.GetDirectoryName(_workspace.FilePath) : FullPathOf(ActiveDocument) is { } a ? Path.GetDirectoryName(a) : null;
            return baseDir != null ? Path.GetFullPath(Path.Combine(baseDir, path)) : Path.GetFullPath(path);
        }

        private OpenDocument? DocumentOf(string? full) =>
            full == null ? ActiveDocument : _documents.FirstOrDefault(d => FullPathOf(d) is { } p && ProjectFiles.PathComparer.Equals(p, full));

        private bool McpRunning() =>
            _nativeRun != null || (_session.HasProgram && (_session.IsStarted || _isBusyValue) && !_session.IsFinished);

        private JsonObject StateJson()
        {
            var docs = new JsonArray();
            foreach (var d in _documents)
                docs.Add(new JsonObject { ["name"] = d.DisplayName, ["path"] = FullPathOf(d), ["kind"] = d.Kind.ToString(), ["modified"] = d.View.IsModified });
            var active = ActiveDocument;
            JsonObject? a = null;
            if (active != null)
            {
                var (first, last) = active.View.GetSelectionLines();
                a = new JsonObject
                {
                    ["name"] = active.DisplayName, ["path"] = FullPathOf(active), ["kind"] = active.Kind.ToString(), ["modified"] = active.View.IsModified,
                    ["caretLine"] = active.View.GetCaretLine(), ["caretColumn"] = active.View.GetCaretColumn(), ["lineCount"] = active.View.LineCount,
                    ["selectionFirstLine"] = first, ["selectionLastLine"] = last, ["hasSelection"] = active.View.HasSelection,
                };
            }
            return new JsonObject
            {
                ["solution"] = _workspace.FilePath,
                ["solutionName"] = _workspace.Name,
                ["startupProject"] = _workspace.Startup?.Name,
                ["activeDocument"] = a,
                ["openDocuments"] = docs,
                ["running"] = McpRunning(),
                ["building"] = _buildJob != null,
                ["errors"] = CurrentErrors().Count(i => i.Severity == "Error"),
                ["warnings"] = CurrentErrors().Count(i => i.Severity == "Warning"),
            };
        }

        /// <summary>What the error list shows: the last build, else the live analysis of the active script.</summary>
        private IReadOnlyList<ErrorListItem> CurrentErrors()
        {
            if (_buildItems != null) return _buildItems;
            var doc = ActiveDocument;
            var diagnostics = doc?.Script?.Diagnostics ?? (IReadOnlyList<fire.Compiler.Diagnostic>)Array.Empty<fire.Compiler.Diagnostic>();
            return diagnostics.Select(d => ToItem(d, doc?.DisplayName, FullPathOf(doc))).ToList();
        }

        private void RegisterMcpTools(McpServer s)
        {
            const string none = "";
            s.AddTool("spark_state", "The state of the editor: the solution, the active document (path, caret line and column, selection), the open documents, " +
                                     "whether a program runs or a build is going on, the number of errors and warnings.", none,
                async _ => McpToolResult.Ok((await OnUi(StateJson)).ToJsonString(new JsonSerializerOptions { WriteIndented = true })));

            s.AddTool("read_document", "The text of a document: the open one as it is in the editor (also unsaved changes), else the file. Without a path the active document. " +
                                       "firstLine/lastLine (1-based) give only those lines; each line starts with its number.",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"the file (full or relative to the solution); empty: the active document\"}," +
                "\"firstLine\":{\"type\":\"integer\"},\"lastLine\":{\"type\":\"integer\"}}}",
                async args =>
                {
                    string? full = Resolve(Str(args, "path"));
                    string? text = await OnUi(() => DocumentOf(full)?.View.GetText());
                    if (text == null)
                    {
                        if (full == null) return McpToolResult.Error("No document is open.");
                        if (!File.Exists(full)) return McpToolResult.Error($"There is no file {full}.");
                        text = await File.ReadAllTextAsync(full);
                    }
                    var lines = text.Replace("\r\n", "\n").Split('\n');
                    int first = Math.Max(1, Int(args, "firstLine") ?? 1), last = Math.Min(lines.Length, Int(args, "lastLine") ?? lines.Length);
                    var sb = new StringBuilder();
                    for (int i = first; i <= last; i++) sb.Append(i).Append(": ").Append(lines[i - 1]).Append('\n');
                    return McpToolResult.Ok(sb.ToString());
                });

            s.AddTool("get_selection", "The selected text of the active document, with its file and lines (empty when nothing is selected).", none,
                async _ => McpToolResult.Ok((await OnUi(() =>
                {
                    var d = ActiveDocument;
                    if (d == null) return new JsonObject { ["text"] = "" };
                    var (first, last) = d.View.GetSelectionLines();
                    return new JsonObject { ["path"] = FullPathOf(d), ["firstLine"] = first, ["lastLine"] = last, ["text"] = d.View.GetSelectedText() };
                })).ToJsonString()));

            s.AddTool("get_diagnostics", "The errors and warnings of the error list (the last check or build, else the live analysis of the active script): one a line, " +
                                         "severity code file:line message.", none,
                async _ =>
                {
                    var items = await OnUi(CurrentErrors);
                    if (items.Count == 0) return McpToolResult.Ok("No errors and no warnings.");
                    return McpToolResult.Ok(string.Join("\n", items.Select(i => $"{i.Severity} {i.Code} {(i.Path ?? i.File)}:{i.Line} {i.Description}")));
                });

            s.AddTool("open_file", "Opens a file in the editor (or shows it when it is open) and puts the caret on the line.",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"the file (full or relative to the solution)\"}," +
                "\"line\":{\"type\":\"integer\",\"description\":\"the line (1-based)\"}},\"required\":[\"path\"]}",
                async args =>
                {
                    string? full = Resolve(Str(args, "path"));
                    if (full == null || !File.Exists(full)) return McpToolResult.Error($"There is no file {Str(args, "path")}.");
                    int line = Int(args, "line") ?? 1;
                    return await OnUi(() =>
                    {
                        var d = DocumentOf(full) ?? OpenFile(full);
                        if (d == null) return McpToolResult.Error($"{full} could not be opened.");
                        Activate(d);
                        if (line >= 1) d.View.GoToLine(Math.Min(line, d.View.LineCount));
                        return McpToolResult.Ok($"Opened {full} at line {line}.");
                    });
                });

            s.AddTool("replace_selection", "Replaces the selected text of the active document by `text` (with nothing selected: puts it in at the caret). " +
                                           "The user sees the change and can undo it (Ctrl+Z).",
                "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"the new text\"}},\"required\":[\"text\"]}",
                async args =>
                {
                    string text = Str(args, "text") ?? "";
                    return await OnUi(() =>
                    {
                        var d = ActiveDocument;
                        if (d == null) return McpToolResult.Error("No document is open.");
                        if (!d.View.ReplaceSelection(text)) return McpToolResult.Error($"{d.DisplayName} cannot be changed here (read-only, or no text).");
                        return McpToolResult.Ok($"Changed {d.DisplayName} at line {d.View.GetCaretLine()}.");
                    });
                });

            s.AddTool("replace_lines", "Replaces the lines firstLine..lastLine (1-based) of a document by `text` (several lines with \\n; empty: the lines go). " +
                                       "lastLine = firstLine - 1 puts the text in before firstLine. The file is opened when it is not; the user sees the change and can undo it.",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"the file; empty: the active document\"}," +
                "\"firstLine\":{\"type\":\"integer\"},\"lastLine\":{\"type\":\"integer\"},\"text\":{\"type\":\"string\"}},\"required\":[\"firstLine\",\"lastLine\",\"text\"]}",
                async args =>
                {
                    string? full = Resolve(Str(args, "path"));
                    int? first = Int(args, "firstLine"), last = Int(args, "lastLine");
                    if (first == null || last == null) return McpToolResult.Error("firstLine and lastLine are numbers of lines.");
                    string text = Str(args, "text") ?? "";
                    if (text != "" && !text.EndsWith("\n")) text += "\n";
                    return await OnUi(() =>
                    {
                        var d = DocumentOf(full);
                        if (d == null && full != null && File.Exists(full)) d = OpenFile(full);
                        if (d == null) return McpToolResult.Error(full == null ? "No document is open." : $"There is no file {full}.");
                        Activate(d);
                        if (!d.View.ReplaceLines(first.Value, last.Value, text))
                            return McpToolResult.Error($"The lines {first}..{last} are not in {d.DisplayName} ({d.View.LineCount} lines), or it cannot be changed here.");
                        return McpToolResult.Ok($"Changed {d.DisplayName}: lines {first}..{last}.");
                    });
                });

            s.AddTool("list_files", "The files of the solution: each project with its source files (in compile order) and its other files.", none,
                async _ => McpToolResult.Ok(await OnUi(() =>
                {
                    if (!_workspace.IsOpen) return "No solution or project is open." + (FullPathOf(ActiveDocument) is { } a ? " The active document: " + a : "");
                    var sb = new StringBuilder();
                    foreach (var p in _workspace.AllProjects)
                    {
                        sb.Append("Project ").Append(p.Name).Append(" (").Append(p.FilePath).Append(")\n");
                        foreach (var f in p.Files) sb.Append("  ").Append(f).Append('\n');
                        foreach (var f in p.ContentFiles) sb.Append("  (other) ").Append(Path.Combine(p.Directory, f)).Append('\n');
                    }
                    return sb.ToString();
                })));

            s.AddTool("check", "Compiles the project (or the active script) like Start does, without running it; the result and the errors and warnings.", none,
                async _ =>
                {
                    var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    bool started = await OnUi(async () =>
                    {
                        if (BuildRunning(false) || McpRunning()) return false;
                        _checkDone = done;
                        _quietCompile = true;
                        bool ok;
                        try { ok = await CompileAndPrepareAsync(null); }
                        finally { _quietCompile = false; }
                        done.TrySetResult(ok);
                        return true;
                    });
                    if (!started) return McpToolResult.Error("A build or a program is running - wait for it, or stop it (tool stop).");
                    bool result = await done.Task;
                    var items = await OnUi(CurrentErrors);
                    string list = items.Count == 0 ? "No errors and no warnings." : string.Join("\n", items.Select(i => $"{i.Severity} {i.Code} {(i.Path ?? i.File)}:{i.Line} {i.Description}"));
                    return McpToolResult.Ok((result ? "The check succeeded." : "The check failed.") + "\n" + list);
                });

            s.AddTool("run", "Starts the program (like F5): the project, or the active script. Its output: the tool get_output (or the resource spark://output).", none,
                async _ => await OnUi(() =>
                {
                    if (BuildRunning(false) || McpRunning()) return McpToolResult.Error("Something runs already.");
                    Run_Click(this, new RoutedEventArgs());
                    return McpToolResult.Ok("Started.");
                }));

            s.AddTool("stop", "Stops the program that runs (and a build).", none,
                async _ => await OnUi(() =>
                {
                    Stop_Click(this, new RoutedEventArgs());
                    return McpToolResult.Ok("Stopped.");
                }));

            s.AddTool("get_output", "What the running (or the last) program wrote. `lastLines`: only the end.",
                "{\"type\":\"object\",\"properties\":{\"lastLines\":{\"type\":\"integer\"}}}",
                async args =>
                {
                    string text = await OnUi(() => _output.Text);
                    if (Int(args, "lastLines") is int n && n > 0)
                    {
                        var lines = text.Split('\n');
                        text = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - n)));
                    }
                    return McpToolResult.Ok(text == "" ? "(no output)" : text);
                });

            s.AddResource(StateUri, "spark state", "The state of the editor as JSON (subscribe: it changes with the active document, a build, a run).", "application/json",
                async () => (await OnUi(StateJson)).ToJsonString());
            s.AddResource(OutputUri, "program output", "What the program wrote.", "text/plain", async () => await OnUi(() => _output.Text));
        }
    }
}
