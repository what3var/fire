using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia.Interactivity;
using Avalonia.Threading;
using fire.Native;

namespace fire.Editor
{
    // The build window (docs: the help page of spark, "Builds"): every build spark starts - Run with the VM when it compiles, a native build, a native run or debug session,
    // the ESP32 (build and flash), Build, packing a library - is a build job. When it takes longer than half a second its window appears: the phase with its progress, the
    // log as it runs, Hide and Cancel. Hidden, the status bar shows the progress; a click there brings the window back. Cancel ends the compilers with their process trees
    // (BuildProcess), the in-process parts stop at their next checkpoint (BuildMonitor), what was half written is deleted; "Build cancelled" goes into the Output.
    public partial class MainWindow
    {
        /// <summary>A build that runs: its monitor (progress, log, cancellation) and its window.</summary>
        private sealed class BuildJob
        {
            public BuildJob(string what) => What = what;
            public string What { get; }
            public readonly CancellationTokenSource Cancellation = new();
            public BuildMonitor Monitor = null!;
            public readonly Stopwatch Watch = Stopwatch.StartNew();
            /// <summary>The lines of the log that the window has not shown yet (from any thread).</summary>
            public readonly ConcurrentQueue<string> Pending = new();
            /// <summary>Every line (the window gets them all when it appears late).</summary>
            public readonly List<string> Lines = new();
            public volatile object? Progress;   // (a boxed BuildProgress: written by the build's threads)
            public BuildProgressWindow? Window;
            public DispatcherTimer? Timer;
            public bool Hidden;
            public bool Ended;
            public CancellationToken Token => Cancellation.Token;
            public bool Cancelling => Cancellation.IsCancellationRequested;
            public BuildProgress? Last => Progress is BuildProgress p ? p : null;
        }

        /// <summary>How long a build runs before its window appears (a quick compile for the VM shows none).</summary>
        private static readonly TimeSpan BuildWindowDelay = TimeSpan.FromMilliseconds(500);

        private BuildJob? _buildJob;

        /// <summary>spark was closed while a build ran: it closes once the cancelled build has stopped.</summary>
        private bool _closeAfterBuild;

        /// <summary>Is a build running? (Then a second one does not start: the status bar says so.)</summary>
        private bool BuildRunning(bool tell = true)
        {
            if (_buildJob == null) return false;
            if (tell) UpdateStatus($"{_buildJob.What} is being built - wait for it or cancel it (Ctrl+Break, Shift+F5).");
            return true;
        }

        /// <summary>A build begins: its monitor, the timer that shows its window after <see cref="BuildWindowDelay"/> and refreshes it and the status bar.</summary>
        private BuildJob BeginBuildJob(string what)
        {
            var job = new BuildJob(what);
            job.Monitor = new BuildMonitor(job.Token, p => job.Progress = p, line => job.Pending.Enqueue(line));
            job.Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            job.Timer.Tick += (_, _) => RefreshBuildJob(job);
            job.Timer.Start();
            _buildJob = job;
            return job;
        }

        /// <summary>The timer of a build: the new lines of the log into the window, the progress into the window or the status bar; the window appears once the build runs long enough.</summary>
        private void RefreshBuildJob(BuildJob job)
        {
            if (job.Ended) return;
            var lines = new List<string>();
            while (job.Pending.TryDequeue(out var line)) lines.Add(line);
            job.Lines.AddRange(lines);
            if (job.Window == null && !job.Hidden && job.Watch.Elapsed >= BuildWindowDelay) ShowBuildWindow(job);
            else if (job.Window != null && lines.Count > 0) job.Window.AppendLog(lines);
            job.Window?.ShowProgress(job.Last, job.Watch.Elapsed, job.Cancelling);
            UpdateBuildStatusBar(job);
        }

        /// <summary>Shows the window of the build (made the first time, with the log so far).</summary>
        private void ShowBuildWindow(BuildJob job)
        {
            job.Hidden = false;
            if (job.Window == null)
            {
                var window = new BuildProgressWindow(job.What);
                window.HideRequested += () => HideBuildWindow(job);
                window.CancelRequested += () => CancelBuild(job);
                window.Dismissed += () => { if (job.Window == window) job.Window = null; };
                window.AppendLog(job.Lines);
                window.ShowProgress(job.Last, job.Watch.Elapsed, job.Cancelling);
                job.Window = window;
            }
            if (!job.Window.IsVisible)
            {
                try { job.Window.Show(this); }
                catch (InvalidOperationException) { job.Window.Show(); }
            }
            job.Window.Activate();
            UpdateBuildStatusBar(job);
        }

        private void HideBuildWindow(BuildJob job)
        {
            job.Hidden = true;
            job.Window?.Hide();
            UpdateBuildStatusBar(job);
            Activate();
        }

        /// <summary>Cancel: the build is asked to stop (its tools end, the rest stops at its next checkpoint); the window says "Cancelling..." until it has.</summary>
        private void CancelBuild(BuildJob? job = null)
        {
            job ??= _buildJob;
            if (job == null || job.Ended || job.Cancelling) return;
            job.Cancellation.Cancel();
            UpdateStatus($"Cancelling the build of {job.What}...");
            RefreshBuildJob(job);
        }

        /// <summary>The status bar while a build runs and its window is not shown: "Building... 3/9 modules" with a small bar; a click shows the window again.</summary>
        private void UpdateBuildStatusBar(BuildJob? job)
        {
            // (not in the first half second either: a quick build shows nothing at all)
            bool show = job is { Ended: false } && (job.Window == null || !job.Window.IsVisible) && job.Watch.Elapsed >= BuildWindowDelay;
            BuildStatusButton.IsVisible = show;
            if (!show) return;
            var p = job!.Last;
            string count = p is { Indeterminate: false } q ? $" {q.Done}/{q.Total}" + (q.Phase == BuildPhase.CompileC && q.Text.Contains("module", StringComparison.Ordinal) ? " modules" : "") : "";
            BuildStatusText.Text = job.Cancelling ? "Cancelling the build..." : $"Building...{count}";
            BuildStatusBar.IsIndeterminate = p is not { Indeterminate: false } || job.Cancelling;
            if (p is { Indeterminate: false } r)
            {
                BuildStatusBar.Maximum = r.Total;
                BuildStatusBar.Value = r.Done;
            }
            Avalonia.Controls.ToolTip.SetTip(BuildStatusButton, $"{job.What}: {p?.Text ?? "starting"} - click to show the build window (Ctrl+Break cancels)");
        }

        private void BuildStatus_Click(object? sender, RoutedEventArgs e)
        {
            if (_buildJob is { Ended: false } job) ShowBuildWindow(job);
        }

        private void CancelBuild_Click(object? sender, RoutedEventArgs e)
        {
            if (_buildJob == null) UpdateStatus("No build is running.");
            else CancelBuild();
        }

        /// <summary>How a build ended.</summary>
        private enum BuildOutcome { Succeeded, Failed, Cancelled }

        /// <summary>The end of a build: succeeded - its window closes (the log stays in the build log); failed - a window that is shown stays open with the error; cancelled - the
        /// window closes and the Output, the build log and the status bar say so.</summary>
        private void EndBuildJob(BuildJob job, BuildOutcome outcome, string? error = null)
        {
            if (job.Ended) return;
            job.Timer?.Stop();
            var lines = new List<string>();
            while (job.Pending.TryDequeue(out var line)) lines.Add(line);
            job.Lines.AddRange(lines);
            job.Ended = true;
            if (_buildJob == job) _buildJob = null;
            UpdateBuildStatusBar(null);
            if (_closeAfterBuild) Dispatcher.UIThread.Post(Close);
            var window = job.Window;
            if (outcome == BuildOutcome.Cancelled)
            {
                _output.Append($"Build cancelled: {job.What} ({job.Watch.Elapsed.TotalSeconds:0.0} s).{Environment.NewLine}");
                _buildLog.AppendLine("========== Build cancelled ==========");
                UpdateStatus($"Build cancelled: {job.What}.");
            }
            if (window == null) return;
            if (outcome == BuildOutcome.Failed && window.IsVisible)
            {
                if (lines.Count > 0) window.AppendLog(lines);
                window.ShowFailure(error ?? "See the build log.");
                window.Activate();
                return;
            }
            window.CloseForGood();
            job.Window = null;
        }

        /// <summary>Is the build window of a failed build showing its error (then no second message box is needed)?</summary>
        private static bool FailureShown(BuildJob job) => job.Window is { IsVisible: true };
    }
}
