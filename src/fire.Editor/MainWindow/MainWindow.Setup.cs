using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Threading;
using fire.Editor.Setup;

namespace fire.Editor
{
    // The setup wizard (? > Setup Wizard; by itself at the first start of spark, settings.SetupWizardSeen): toolchains, the model of the assistant, the first build of the standard
    // packages - then a progress window (SetupProgressWindow) that can go to the background; the status bar shows it then and a click there brings it back.
    public partial class MainWindow
    {
        private SetupRun? _setupRun;
        private SetupProgressWindow? _setupWindow;
        private bool _setupHidden;
        private DispatcherTimer? _setupStatusTimer;
        private IDisposable? _setupStatusWork;
        private string? _setupStatusText;
        private bool _setupHooked;

        /// <summary>Does the wizard come at this start (the first one; FIRE_SETUP_WIZARD=off never, e.g. for automated runs)?</summary>
        private bool SetupWizardDue => !_settings.SetupWizardSeen && Environment.GetEnvironmentVariable("FIRE_SETUP_WIZARD") != "off";

        private async void SetupWizard_Click(object? sender, RoutedEventArgs e) => await ShowSetupWizard();

        /// <summary>The wizard (a setup that runs: its window instead). True when the user started a setup.</summary>
        private async Task<bool> ShowSetupWizard(bool firstStart = false)
        {
            if (_setupRun is { Running: true }) { ShowSetupProgress(); return false; }
            var catalog = ModelCatalog.Load();
            var env = SetupEnvironment.Current(_settings.Assistant);
            var model = new SetupWizardModel(env, catalog, fire.Native.MachineToolchains.Load());
            var wizard = new SetupWizardWindow(model);
            if (Environment.GetEnvironmentVariable("FIRE_SETUP_WIZARD_SHOTS") is { Length: > 0 } shots) { await SaveSetupShots(shots); return false; }
            await wizard.ShowDialog<bool?>(this);
            if (firstStart || !_settings.SetupWizardSeen)
            {
                _settings.SetupWizardSeen = true;
                _settings.Save();
            }
            if (wizard.Plan == null) return false;
            // the compilers for Build and Publish (toolchain.json; the downloads of the plan bring the missing ones)
            try { model.Compilers.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { UpdateStatus("The choice of the compilers could not be saved: " + ex.Message); }
            // (Start must never do nothing: a plan without steps is said, a failure to start is shown)
            try
            {
                var run = SetupWizardModel.Launch(wizard.Plan, plan => SetupSteps.For(plan, catalog, startup: SetupSteps.StartupInstalled));
                if (run == null)
                {
                    UpdateStatus("The setup has nothing to do: everything chosen is there already.");
                    if (wizard.OpenOptions) await ShowOptions();
                    return false;
                }
                StartSetup(run);
            }
            catch (Exception ex)
            {
                await Dialogs.Message(this, "The setup could not start: " + ex.Message, "spark setup");
                return false;
            }
            // at the first start the welcome window waits until the progress is closed or goes to the background - opened at once, it covered the progress
            // (a modal window over it: Start seemed to do nothing)
            if (firstStart || wizard.OpenOptions) await _setupDismissed!.Task;
            if (wizard.OpenOptions) await ShowOptions();
            return true;
        }

        /// <summary>Done when the progress window is closed or goes to the background.</summary>
        private TaskCompletionSource? _setupDismissed;

        /// <summary>At the start (after the splash): the wizard when it is due; the assistant waits for it (its model may come with it).</summary>
        private async Task RunFirstStartSetup()
        {
            if (Environment.GetEnvironmentVariable("FIRE_OPTIONS_SHOTS") is { Length: > 0 } optionShots) { await SaveOptionsShots(optionShots); return; }
            if (!SetupWizardDue && Environment.GetEnvironmentVariable("FIRE_SETUP_WIZARD_SHOTS") is not { Length: > 0 }) return;
            bool started = await ShowSetupWizard(firstStart: true);
            if (!started) StartAssistantAfterSetup();
        }

        private void StartAssistantAfterSetup()
        {
            if (_settings.Assistant && _assistant is not { HasExited: false }) StartAssistant();
        }

        /// <summary>A run that was just started: its window, its line in the status bar.</summary>
        private void StartSetup(SetupRun run)
        {
            HookSetupStatus();
            _setupRun = run;
            _setupDismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            run.StatusChanged += () => Dispatcher.UIThread.Post(() => SetupChanged(run));
            _setupWindow?.CloseForGood();
            _setupWindow = null;
            _setupHidden = false;
            ShowSetupProgress();
            _setupStatusTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => UpdateSetupStatus());
            _setupStatusTimer.Start();
        }

        private void ShowSetupProgress()
        {
            if (_setupRun == null) return;
            _setupHidden = false;
            if (_setupWindow == null)
            {
                var window = new SetupProgressWindow(_setupRun);
                window.HideRequested += HideSetupProgress;
                window.Closed += (_, _) => { if (_setupWindow == window) _setupWindow = null; _setupDismissed?.TrySetResult(); };
                _setupWindow = window;
            }
            if (!_setupWindow.IsVisible)
            {
                try { _setupWindow.Show(this); }
                catch (InvalidOperationException) { _setupWindow.Show(); }
            }
            _setupWindow.Activate();
            UpdateSetupStatus();
        }

        private void HideSetupProgress()
        {
            _setupHidden = true;
            _setupWindow?.Hide();
            _setupDismissed?.TrySetResult();
            UpdateSetupStatus();
            Activate();
        }

        /// <summary>A step began or ended: at the end of the run the assistant starts (with its model), the status bar says how it went.</summary>
        private void SetupChanged(SetupRun run)
        {
            if (run != _setupRun) return;
            _setupWindow?.Refresh();
            if (run.Running) return;
            UpdateSetupStatus();
            if (!run.Finished) return;
            UpdateStatus(run.Succeeded ? "The setup is done." : run.Summary + (_setupHidden ? " - ? > Setup Wizard shows what happened (and Retry)." : "."));
            if (_setupHidden && !run.Succeeded) ShowSetupProgress();   // (an error is not left in the background)
            StartAssistantAfterSetup();
        }

        /// <summary>While the window is hidden, the setup is in the status bar (as the work of BackgroundWork) - except while the packages are built: they show themselves.</summary>
        private void UpdateSetupStatus()
        {
            var run = _setupRun;
            bool packages = run?.Items.Any(i => i.State == SetupItemState.Running && i.Title == "Build the standard packages") == true;
            string? text = run is { Running: true } && _setupHidden && !packages ? run.Summary : null;
            if (text == _setupStatusText) return;
            _setupStatusText = text;
            var old = _setupStatusWork;
            _setupStatusWork = text != null ? fire.Native.BackgroundWork.Begin(text, (int)(run!.Fraction * 100), 100) : null;
            old?.Dispose();
            if (run is not { Running: true }) _setupStatusTimer?.Stop();
        }

        /// <summary>A click on the work of the status bar brings back the hidden setup; spark closing cancels it.</summary>
        private void HookSetupStatus()
        {
            if (_setupHooked) return;
            _setupHooked = true;
            WorkStatus.Background = Avalonia.Media.Brushes.Transparent;   // (hit-testable between the text and the bar)
            WorkStatus.PointerPressed += (_, _) => { if (_setupRun != null && _setupHidden) ShowSetupProgress(); };
            Closed += (_, _) => { _setupRun?.Cancel(); _setupStatusWork?.Dispose(); };
        }

        // ---- pictures of the pages (FIRE_SETUP_WIZARD_SHOTS=<folder>; FIRE_SETUP_WIZARD_AS=windows shows the pages of Windows with the assistant on) ----

        private async Task SaveSetupShots(string folder)
        {
            var catalog = ModelCatalog.Load();
            var env = SetupEnvironment.Current(assistantEnabled: true) with { AssistantAvailable = true };
            if (Environment.GetEnvironmentVariable("FIRE_SETUP_WIZARD_AS") == "windows")
                env = env with { IsWindows = true, IsMacOS = false, IsX64 = true, Detected = Array.Empty<DetectedToolchain>(), W64devkitThere = false, WatcomThere = false, TinyccThere = false, TinyccPinned = true };
            var wizard = new SetupWizardWindow(new SetupWizardModel(env, catalog, fire.Native.MachineToolchains.Load()));
            wizard.Show(this);
            await Task.Delay(800);
            try { wizard.SaveShots(folder); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString()); }
            wizard.Close();

            // the progress window with a run of fake steps: a download half done, a failure, one waiting
            var gate = new System.Threading.ManualResetEventSlim();
            var steps = new ISetupStep[]
            {
                new SetupStep("Install w64devkit (GCC for Windows)", ctx => { ctx.Log("Downloading https://github.com/skeeto/w64devkit/releases/... "); ctx.Log("Unpacking ..."); }),
                new SetupStep("Download the model Qwen3.5 0.8B", ctx =>
                {
                    ctx.Phase("Downloading");
                    for (int i = 0; i <= 10; i++) { ctx.Bytes(i * 25_000_000L, 507_154_688); System.Threading.Thread.Sleep(100); }
                    gate.Wait(TimeSpan.FromSeconds(10));
                }),
                new SetupStep("Build the standard packages", ctx => throw new InvalidOperationException("The standard package ai could not be installed: (example)")),
            };
            var run = new SetupRun(steps);
            var progress = new SetupProgressWindow(run);
            _ = run.Start();
            progress.Show(this);
            await Task.Delay(1600);
            progress.Refresh();
            try { SetupWizardWindow.SaveShot(progress, Path.Combine(folder, "setup-progress-running.png")); } catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "error2.txt"), ex.ToString()); }
            gate.Set();
            await Task.Delay(800);
            progress.Refresh();
            try { SetupWizardWindow.SaveShot(progress, Path.Combine(folder, "setup-progress-failed.png")); } catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "error3.txt"), ex.ToString()); }
            progress.CloseForGood();
            File.WriteAllText(Path.Combine(folder, "done.txt"), "ok");
        }
    }
}
