using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Threading;
using fire.Editor.Assistant;

namespace fire.Editor
{
    /// <summary>
    /// The assistant of spark (docs/ai/assistant.md): a fire program (Assistant/assistant.fire, package `assistant`) that spark starts when it is switched on
    /// (Tools > Assistant, or the unchecked task of the setup) - as a native program once it is built (Assistant/AssistantNative.cs), else with `forge run` in
    /// the virtual machine - and stops when it is switched off or spark closes. It talks to spark over the MCP server, which runs while the assistant is on.
    /// </summary>
    public partial class MainWindow
    {
        private Process? _assistant;
        /// <summary>The assistant is being started (its native build is looked for): a second start meanwhile does nothing.</summary>
        private bool _assistantStarting;
        /// <summary>The native build of the assistant that runs in the background: forge's process, ended when the assistant is switched off or spark closes.</summary>
        private Process? _assistantBuild;
        private CancellationTokenSource? _assistantBuildCancel;

        /// <summary>FIRE_ASSISTANT_ENGINE=vm: the assistant always in the virtual machine (no native build).</summary>
        private static bool AssistantWantsVm => string.Equals(Environment.GetEnvironmentVariable("FIRE_ASSISTANT_ENGINE"), "vm", StringComparison.OrdinalIgnoreCase);

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
            if (_settings.Assistant && !SetupWizardDue) StartAssistant();   // (else after the setup wizard: its model may come with it)
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

        /// <summary>The process of the assistant that one of the open sparks started (its id in assistant.pid beside the connection files): one assistant serves
        /// all sparks - it connects to each of them (their files in spark-mcp/).</summary>
        private static string AssistantPidFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fire", "assistant.pid");

        private static bool OtherAssistantRuns()
        {
            try
            {
                if (!File.Exists(AssistantPidFile) || !int.TryParse(File.ReadAllText(AssistantPidFile).Trim(), out int pid)) return false;
                using var other = Process.GetProcessById(pid);
                return !other.HasExited;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Other sparks that are open (their connection files, their processes alive) - the assistant stays for them when this one closes.</summary>
        private static bool OtherSparksOpen()
        {
            try
            {
                string folder = fire.Projects.Mcp.McpServer.InstanceFolderOf("spark");
                if (!Directory.Exists(folder)) return false;
                foreach (string file in Directory.GetFiles(folder, "*.json"))
                {
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out int pid) || pid == Environment.ProcessId) continue;
                    try { using var p = Process.GetProcessById(pid); if (!p.HasExited) return true; }
                    catch (Exception) { try { File.Delete(file); } catch (Exception) { } }   // (a spark that ended without removing its file)
                }
            }
            catch (Exception) { }
            return false;
        }

        private void StartAssistant() => _ = StartAssistantAsync();

        /// <summary>Starts the assistant: the native binary when its build is there and up to date (<see cref="AssistantNative"/>), else `forge run` in the virtual
        /// machine - and then the native build in the background (the status bar shows it), for the next start. Without a C toolchain, or when that build failed,
        /// it stays in the VM (the Output says why).</summary>
        private async Task StartAssistantAsync()
        {
            if (_assistant is { HasExited: false } || _assistantStarting) return;
            StartMcpServer();
            // another spark's assistant runs: it finds this spark by itself (its connection file)
            if (OtherAssistantRuns())
            {
                UpdateStatus("The assistant runs already - it works with this spark too (Ctrl+^ calls it here).");
                return;
            }
            string script = Path.Combine(AppContext.BaseDirectory, "Assistant", "assistant.fire");
            string forge = Forge.Locate(out _) ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "forge.exe" : "forge");
            if (!File.Exists(script) || !File.Exists(forge))
            {
                UpdateStatus("The assistant is not installed with this spark (Assistant/assistant.fire, forge).");
                return;
            }
            _assistantStarting = true;
            try
            {
                string folder = AssistantNative.Folder;
                var native = AssistantWantsVm ? default : await Task.Run(() => AssistantNative.Look(script, AppContext.BaseDirectory, folder));
                if (!_settings.Assistant || _assistant is { HasExited: false }) return;   // (switched off meanwhile)
                if (native.Binary != null && LaunchAssistant(native.Binary, Array.Empty<string>(), true)) return;
                // the native build is due: build it now and start the assistant natively when it is done (the VM is far too slow for it) - with GCC
                // about a minute (AssistantNative.BuildToolchain); only when no native build is possible does it run in the VM
                if (!AssistantWantsVm && native.Error == null && !native.FailedBefore && native.Toolchain && native.Key != null)
                {
                    BuildAssistantNatively(forge, script, folder, native.Key, startAfter: true);
                    return;
                }
                if (!LaunchAssistant(forge, new[] { "run", script }, false) || AssistantWantsVm) return;
                if (native.Error != null) _output.Append($"The assistant runs in the virtual machine: its native build could not be checked ({native.Error}).{Environment.NewLine}");
                else if (native.FailedBefore) _output.Append($"The assistant runs in the virtual machine: its native build failed before - it is tried again when spark or its packages change ({folder}).{Environment.NewLine}");
                else if (!native.Toolchain) _output.Append($"The assistant runs in the virtual machine: there is no C toolchain for its native build (Tools > Toolchain Settings, or Help > Setup Wizard).{Environment.NewLine}");
            }
            finally { _assistantStarting = false; }
        }

        /// <summary>Builds the assistant natively in the background (once per version of spark and its packages); with <paramref name="startAfter"/> it is
        /// started natively as soon as the build is done - and in the VM only when the build failed.</summary>
        private void BuildAssistantNatively(string forge, string script, string folder, string key, bool startAfter = false)
        {
            if (_assistantBuildCancel != null) return;
            var cancel = new CancellationTokenSource();
            _assistantBuildCancel = cancel;
            Task.Run(() =>
            {
                using var work = fire.Tooling.BackgroundWork.Begin("Building the assistant as a native program - it starts when the build is done");
                return AssistantNative.Build(forge, script, folder, key, p => _assistantBuild = p, null, cancel.Token, AssistantNative.BuildToolchain());
            }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                if (_assistantBuildCancel == cancel) _assistantBuildCancel = null;
                _assistantBuild = null;
                cancel.Dispose();
                var result = t.IsCompletedSuccessfully ? t.Result : new AssistantNative.BuildResult(null, t.Exception?.GetBaseException().Message ?? "");
                if (result.Cancelled) return;
                bool wanted = startAfter && _settings.Assistant && _assistant is not { HasExited: false };
                if (result.Binary != null)
                {
                    _output.Append($"The assistant was built as a native program ({result.Binary}).{Environment.NewLine}");
                    if (wanted && LaunchAssistant(result.Binary, Array.Empty<string>(), true)) UpdateStatus("The assistant is built as a native program and started.");
                    else if (wanted) LaunchAssistant(forge, new[] { "run", script }, false);
                    else UpdateStatus("The assistant is built as a native program - it runs natively from its next start.");
                }
                else
                {
                    _output.Append($"The assistant could not be built as a native program - it runs in the virtual machine:{Environment.NewLine}{result.Output}{Environment.NewLine}");
                    UpdateStatus("The assistant could not be built natively - it runs in the virtual machine (see the Output).");
                    if (wanted) LaunchAssistant(forge, new[] { "run", script }, false);
                }
            }));
        }

        private void CancelAssistantBuild()
        {
            var cancel = _assistantBuildCancel;
            _assistantBuildCancel = null;
            try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
            // (the token ends forge and its compilers; here too, should the cancel come before forge's process was known)
            try { if (_assistantBuild is { HasExited: false } b) b.Kill(true); } catch (Exception) { }
        }

        /// <summary>Starts the assistant's process: the native binary, or forge with its arguments. false when it did not start.</summary>
        private bool LaunchAssistant(string file, string[] arguments, bool native)
        {
            try
            {
                var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                info.Environment[fire.Tooling.BackgroundWork.LinesVariable] = "1";   // (forge builds the packages at the first start: the status bar shows it)
                foreach (string a in arguments) info.ArgumentList.Add(a);
                var p = Process.Start(info);
                if (p == null) return false;
                fire.Debugging.ChildProcesses.Add(p);
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
                try { File.WriteAllText(AssistantPidFile, p.Id.ToString()); } catch (Exception) { }
                UpdateStatus(native ? "The assistant starts (native)..." : "The assistant starts...");
                return true;
            }
            catch (Exception ex)
            {
                if (native)
                {
                    // (a native build that does not start here: the VM, as without one)
                    _output.Append($"The native assistant could not start ({ex.Message}) - it runs in the virtual machine.{Environment.NewLine}");
                    return false;
                }
                UpdateStatus("The assistant could not start: " + ex.Message);
                return false;
            }
        }

        /// <summary>Ctrl+^: the assistant opens its question line (an MCP notification, {"event": "poke"}); when it is off, the status bar says how to switch it on.</summary>
        private void PokeAssistant()
        {
            if (!_settings.Assistant || _mcp == null)
            {
                UpdateStatus("The assistant is off (Tools > Assistant).");
                return;
            }
            if (_assistant is not { HasExited: false } && !OtherAssistantRuns()) StartAssistant();
            _mcp.Log("info", new System.Text.Json.Nodes.JsonObject { ["event"] = "poke" }, "spark");
        }

        private void StopAssistant()
        {
            // (a native build that runs ends too: its compilers would outlive spark)
            CancelAssistantBuild();
            // the assistant hears that this spark goes; it ends when no spark is left
            _mcp?.Log("info", new System.Text.Json.Nodes.JsonObject { ["event"] = "closing" }, "spark");
            var p = _assistant;
            _assistant = null;
            if (p == null) return;
            // (started by this spark: ended here - unless other sparks are open, which it serves too)
            if (!OtherSparksOpen())
            {
                try { if (!p.WaitForExit(300) && !p.HasExited) p.Kill(true); } catch (Exception) { }
                try { File.Delete(AssistantPidFile); } catch (Exception) { }
            }
            p.Dispose();
        }
    }
}
