using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace fire.Editor
{
    /// <summary>
    /// The assistant of spark (docs/ai/assistant.md): a fire program (Assistant/assistant.fire, package `assistant`) that spark starts with forge when it is switched
    /// on (Tools > Assistant, or the unchecked task of the setup) and stops when it is switched off or spark closes. It talks to spark over the MCP server, which
    /// runs while the assistant is on.
    /// </summary>
    public partial class MainWindow
    {
        private Process? _assistant;

        /// <summary>At the start: the choice of the setup (once), then the assistant when it is on.</summary>
        private void StartAssistantIfWanted()
        {
            if (!_settings.AssistantSetupSeen)
            {
                _settings.AssistantSetupSeen = true;
                if (SetupWantsAssistant()) _settings.Assistant = true;
                _settings.Save();
            }
            if (mnuAssistant != null) mnuAssistant.IsChecked = _settings.Assistant;
            if (_settings.Assistant) StartAssistant();
        }

        /// <summary>The task "The assistant" of the setup (Windows: HKCU or HKLM Software\fire\spark, Assistant = 1).</summary>
        private static bool SetupWantsAssistant()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                {
                    using var key = root.OpenSubKey(@"Software\fire\spark");
                    if (key?.GetValue("Assistant") is int v && v == 1) return true;
                    if (key?.GetValue("Assistant") is string s && s == "1") return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        private void Assistant_Click(object? sender, RoutedEventArgs e)
        {
            _settings.Assistant = !_settings.Assistant;
            _settings.Save();
            if (_settings.Assistant) StartAssistant();
            else
            {
                StopAssistant();
                if (!_settings.McpServer) StopMcpServer();
                UpdateStatus("The assistant is off.");
            }
            if (sender is Avalonia.Controls.MenuItem item) item.IsChecked = _settings.Assistant;
        }

        private void StartAssistant()
        {
            if (_assistant is { HasExited: false }) return;
            StartMcpServer();
            string script = Path.Combine(AppContext.BaseDirectory, "Assistant", "assistant.fire");
            string forge = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "forge.exe" : "forge");
            if (!File.Exists(script) || !File.Exists(forge))
            {
                UpdateStatus("The assistant is not installed with this spark (Assistant/assistant.fire, forge).");
                return;
            }
            try
            {
                var info = new ProcessStartInfo(forge) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                info.Environment[fire.Native.BackgroundWork.LinesVariable] = "1";   // (its packages are built at the first start: the status bar shows it)
                info.ArgumentList.Add("run");
                info.ArgumentList.Add(script);
                var p = Process.Start(info);
                if (p == null) return;
                p.EnableRaisingEvents = true;
                // what it writes goes into the log of the status bar only when it fails
                var output = new System.Text.StringBuilder();
                p.OutputDataReceived += (_, a) => { if (a.Data != null) lock (output) output.AppendLine(a.Data); };
                p.ErrorDataReceived += (_, a) => { if (a.Data != null && !ChildWorkLine(a.Data)) lock (output) output.AppendLine(a.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    ChildWorkEnded();
                    if (_assistant != p) return;
                    _assistant = null;
                    string text;
                    lock (output) text = output.ToString().Trim();
                    if (p.ExitCode != 0 && text.Length > 0)
                    {
                        // (all of it into the Output: the last line alone rarely says why)
                        _output.Append($"The assistant ended (exit code {p.ExitCode}):{Environment.NewLine}{text}{Environment.NewLine}");
                        UpdateStatus("The assistant ended: " + text.Split('\n')[^1].Trim() + " (see the Output)");
                    }
                });
                _assistant = p;
                UpdateStatus("The assistant starts...");
            }
            catch (Exception ex) { UpdateStatus("The assistant could not start: " + ex.Message); }
        }

        /// <summary>Ctrl+^: the assistant opens its question line (an MCP notification, {"event": "poke"}); when it is off, the status bar says how to switch it on.</summary>
        private void PokeAssistant()
        {
            if (!_settings.Assistant || _mcp == null)
            {
                UpdateStatus("The assistant is off (Tools > Assistant).");
                return;
            }
            if (_assistant is not { HasExited: false }) StartAssistant();
            _mcp.Log("info", new System.Text.Json.Nodes.JsonObject { ["event"] = "poke" }, "spark");
        }

        private void StopAssistant()
        {
            var p = _assistant;
            _assistant = null;
            if (p == null) return;
            // it ends by itself when spark is gone; to be sure it is ended here
            _mcp?.Log("info", new System.Text.Json.Nodes.JsonObject { ["event"] = "closing" }, "spark");
            try { if (!p.WaitForExit(300) && !p.HasExited) p.Kill(true); } catch (Exception) { }
            p.Dispose();
        }
    }
}
