using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using fire.Editor.Setup;
using fire.Native;

namespace fire.Editor
{
    // Tools > Options (OptionsDialog): the options of spark for this machine - General (start, the engine of a script, the assistant, the MCP server, the ESP-IDF folder) and
    // Compilers (the compiler for Build and the one for Publish, toolchain.json; the downloads run in the progress window of the setup).
    public partial class MainWindow
    {
        private async void Options_Click(object? sender, RoutedEventArgs e) => await ShowOptions();

        /// <summary>The options (on <paramref name="page"/>: general, compilers); Apply saves them, applies what changed and downloads the missing compilers.</summary>
        private async Task ShowOptions(string? page = null)
        {
            bool assistantBefore = _settings.Assistant, mcpBefore = _settings.McpServer;
            var env = await Task.Run(() => SetupEnvironment.Current(_settings.Assistant));
            var compilers = new CompilerChoiceModel(env, MachineToolchains.Load());
            var dialog = new OptionsDialog(_settings, compilers, env.AssistantAvailable);
            if (page != null) dialog.SelectPage(page);
            bool applied = await dialog.ShowDialog<bool?>(this) == true;
            if (dialog.RunSetupWizard) { await ShowSetupWizard(); return; }
            if (!applied) return;

            _settings.Save();
            try { compilers.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Dialogs.Message(this, $"The choice of the compilers could not be saved to {MachineToolchains.DefaultFile}: {ex.Message}", "Options");
            }
            if (_settings.Assistant != assistantBefore)
            {
                if (_settings.Assistant) StartAssistant();
                else { StopAssistant(); if (!_settings.McpServer) StopMcpServer(); }
                mnuAssistant.IsChecked = _settings.Assistant;
            }
            if (_settings.McpServer != mcpBefore)
            {
                if (_settings.McpServer || _settings.Assistant) StartMcpServer(); else StopMcpServer();
                mnuMcpServer.IsChecked = _settings.McpServer;
            }
            RefreshEngineMenu();

            var plan = compilers.PlanItems();
            string summary = $"{compilers.Summary(ToolchainPurpose.Build)}; {compilers.Summary(ToolchainPurpose.Publish)}";
            if (plan.Count == 0) { UpdateStatus(summary); return; }
            if (_setupRun is { Running: true })
            {
                await Dialogs.Message(this, "A setup is running: the compilers are downloaded when it is through - open Tools > Options again then.", "Options");
                return;
            }
            try
            {
                var run = SetupWizardModel.Launch(plan, p => SetupSteps.For(p, ModelCatalog.Load()));
                if (run != null) StartSetup(run);
                UpdateStatus(summary);
            }
            catch (Exception ex)
            {
                await Dialogs.Message(this, "The download could not start: " + ex.Message, "Options");
            }
        }

        /// <summary>Pictures of the pages of the options (FIRE_OPTIONS_SHOTS=&lt;folder&gt;; FIRE_SETUP_WIZARD_AS=windows: as on Windows without compilers).</summary>
        private async Task SaveOptionsShots(string folder)
        {
            Directory.CreateDirectory(folder);
            var env = SetupEnvironment.Current(_settings.Assistant);
            if (Environment.GetEnvironmentVariable("FIRE_SETUP_WIZARD_AS") == "windows")
                env = env with { IsWindows = true, IsMacOS = false, IsX64 = true, Detected = Array.Empty<DetectedToolchain>(), W64devkitThere = false, WatcomThere = false, TinyccThere = false, TinyccPinned = true };
            var dialog = new OptionsDialog(new fire.Projects.EditorSettings { Assistant = _settings.Assistant, Engine = _settings.Engine }, new CompilerChoiceModel(env, MachineToolchains.Load()), env.AssistantAvailable);
            dialog.Show(this);
            await Task.Delay(800);
            try
            {
                foreach (string page in new[] { "compilers", "general" })
                {
                    dialog.SelectPage(page);
                    await Task.Delay(300);
                    SetupWizardWindow.SaveShot(dialog, Path.Combine(folder, $"options-{page}.png"));
                }
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString()); }
            dialog.Close();
            File.WriteAllText(Path.Combine(folder, "done.txt"), "ok");
        }
    }
}
