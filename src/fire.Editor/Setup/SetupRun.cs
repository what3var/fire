using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace fire.Editor.Setup
{
    /// <summary>A piece of the setup: a toolchain, a model, the first build of the standard packages. <see cref="Run"/> runs on a worker thread and reports through the context;
    /// it throws when it failed (the message is shown) and stops at the context's token.</summary>
    public interface ISetupStep
    {
        string Title { get; }
        void Run(SetupStepContext ctx);
    }

    /// <summary>A step from a function (the tests, small steps).</summary>
    public sealed class SetupStep(string title, Action<SetupStepContext> run) : ISetupStep
    {
        public string Title { get; } = title;
        public void Run(SetupStepContext ctx) => run(ctx);
    }

    public enum SetupItemState { Waiting, Running, Done, Failed, Cancelled }

    /// <summary>What a step's progress counts: nothing (indeterminate), bytes (a download, a check) or steps (packages).</summary>
    public enum SetupUnit { None, Bytes, Count }

    /// <summary>What a step gets while it runs: its token, and where it says what it does.</summary>
    public sealed class SetupStepContext
    {
        private readonly SetupItem _item;
        internal SetupStepContext(SetupItem item, CancellationToken token) { _item = item; Token = token; }
        public CancellationToken Token { get; }
        /// <summary>What it does now ("Downloading", "Checking the SHA-256", "Unpacking"): the progress starts again (indeterminate until bytes or a count come).</summary>
        public void Phase(string text) => _item.SetPhase(text);
        public void Bytes(long done, long total) => _item.SetProgress(SetupUnit.Bytes, done, total);
        /// <summary>A count (packages); <paramref name="phase"/> also says what it is at, without starting again.</summary>
        public void Count(long done, long total, string? phase = null) => _item.SetProgress(SetupUnit.Count, done, total, phase);
        public void Log(string line) => _item.AddLog(line);
    }

    /// <summary>One line of the progress window: its state, phase, progress, speed, error and log. Written by the worker, read by the window (a snapshot under the lock).</summary>
    public sealed class SetupItem
    {
        private readonly object _lock = new();
        private readonly List<string> _log = new();
        private readonly SpeedMeter _speed;

        public SetupItem(ISetupStep step, Func<double> clock)
        {
            Step = step;
            _speed = new SpeedMeter(clock);
        }

        public ISetupStep Step { get; }
        public string Title => Step.Title;
        public SetupItemState State { get { lock (_lock) return _state; } }
        public string Phase { get { lock (_lock) return _phase; } }
        public string? Error { get { lock (_lock) return _error; } }
        public SetupUnit Unit { get { lock (_lock) return _unit; } }
        public long Done { get { lock (_lock) return _done; } }
        public long Total { get { lock (_lock) return _total; } }
        public IReadOnlyList<string> Log { get { lock (_lock) return _log.ToArray(); } }
        /// <summary>Bytes a second over the last seconds (0: none yet, or not bytes).</summary>
        public double Speed { get { lock (_lock) return _unit == SetupUnit.Bytes ? _speed.BytesPerSecond : 0; } }
        /// <summary>A change since the window last looked (it redraws only then).</summary>
        public int Version { get { lock (_lock) return _version; } }

        private SetupItemState _state = SetupItemState.Waiting;
        private string _phase = "Waiting";
        private string? _error;
        private SetupUnit _unit;
        private long _done, _total;
        private int _version;

        internal void Start()
        {
            lock (_lock) { _state = SetupItemState.Running; _phase = "Starting"; _error = null; _unit = SetupUnit.None; _done = _total = 0; _speed.Reset(); _version++; }
        }

        internal void SetPhase(string text)
        {
            lock (_lock) { _phase = text; _unit = SetupUnit.None; _done = _total = 0; _speed.Reset(); _version++; }
        }

        internal void SetProgress(SetupUnit unit, long done, long total, string? phase = null)
        {
            lock (_lock)
            {
                if (phase != null) _phase = phase;
                if (unit == SetupUnit.Bytes && _unit != SetupUnit.Bytes) _speed.Reset();
                _unit = unit; _done = done; _total = total;
                if (unit == SetupUnit.Bytes) _speed.Add(done);
                _version++;
            }
        }

        internal void Reset()
        {
            lock (_lock) { _state = SetupItemState.Waiting; _phase = "Waiting"; _error = null; _version++; }
        }

        internal void AddLog(string line) { lock (_lock) { _log.Add(line); _version++; } }

        internal void Finish(SetupItemState state, string? error = null)
        {
            lock (_lock)
            {
                _state = state;
                _error = error;
                _phase = state switch { SetupItemState.Done => "Done", SetupItemState.Failed => "Failed", SetupItemState.Cancelled => "Cancelled", _ => _phase };
                if (state == SetupItemState.Done && _total > 0) _done = _total;
                _version++;
            }
        }

        /// <summary>0..1, or null while it cannot be said (indeterminate).</summary>
        public double? Fraction
        {
            get
            {
                lock (_lock)
                {
                    if (_state == SetupItemState.Done) return 1;
                    if (_state != SetupItemState.Running || _total <= 0) return null;
                    return Math.Clamp((double)_done / _total, 0, 1);
                }
            }
        }

        /// <summary>The line under the title: "Downloading - 120.5 MB of 507 MB, 4.2 MB/s, about 1 min left", "Installing the package io - 3 of 40", "Failed: ...".</summary>
        public string StatusText
        {
            get
            {
                lock (_lock)
                {
                    switch (_state)
                    {
                        case SetupItemState.Waiting: return "Waiting";
                        case SetupItemState.Done: return "Done";
                        case SetupItemState.Cancelled: return "Cancelled" + (_unit == SetupUnit.Bytes && _done > 0 ? $" at {SetupFormat.Size(_done)} (a retry goes on from there)" : "");
                        case SetupItemState.Failed: return "Failed: " + (_error ?? "see the log");
                    }
                    if (_unit == SetupUnit.Bytes)
                    {
                        string text = $"{_phase} - {SetupFormat.Size(_done)}" + (_total > 0 ? $" of {SetupFormat.Size(_total)}" : "");
                        double speed = _speed.BytesPerSecond;
                        if (speed > 0) text += ", " + SetupFormat.Speed(speed);
                        if (speed > 0 && _total > _done) text += ", " + SetupFormat.Remaining((_total - _done) / speed);
                        return text;
                    }
                    if (_unit == SetupUnit.Count && _total > 0) return $"{_phase} - {_done} of {_total}";
                    return _phase + "...";
                }
            }
        }
    }

    /// <summary>
    /// The progress of the setup (spark's progress window after the setup wizard): the steps one after the other on a worker thread - a step that fails does not stop the others;
    /// Cancel stops the one that runs and leaves the rest; a failed or cancelled step can be tried again (<see cref="Retry"/>; a download goes on from its .part file). The window only
    /// shows the items; hidden, the status bar shows <see cref="Summary"/> (<see cref="StatusChanged"/>).
    /// </summary>
    public sealed class SetupRun
    {
        private readonly object _lock = new();
        private CancellationTokenSource? _cancel;
        private Task? _worker;

        public SetupRun(IEnumerable<ISetupStep> steps, Func<double>? clock = null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            clock ??= () => watch.Elapsed.TotalSeconds;
            Items = steps.Select(s => new SetupItem(s, clock)).ToList();
        }

        public IReadOnlyList<SetupItem> Items { get; }

        /// <summary>Is a step running (or about to)?</summary>
        public bool Running { get { lock (_lock) return _worker is { IsCompleted: false }; } }

        public bool Finished => !Running && Items.All(i => i.State is SetupItemState.Done or SetupItemState.Failed or SetupItemState.Cancelled);
        public bool Succeeded => Finished && Items.All(i => i.State == SetupItemState.Done);
        public bool CanRetry => !Running && Items.Any(i => i.State is SetupItemState.Failed or SetupItemState.Cancelled);

        /// <summary>A step began or ended, or the whole run did (on the worker thread).</summary>
        public event Action? StatusChanged;

        /// <summary>Runs the steps that wait (all of them the first time). The task ends when they are through.</summary>
        public Task Start()
        {
            lock (_lock)
            {
                if (_worker is { IsCompleted: false }) return _worker;
                _cancel = new CancellationTokenSource();
                var token = _cancel.Token;
                _worker = Task.Run(() => Work(token));
                return _worker;
            }
        }

        private void Work(CancellationToken token)
        {
            foreach (var item in Items)
            {
                if (item.State != SetupItemState.Waiting) continue;
                if (token.IsCancellationRequested) { item.Finish(SetupItemState.Cancelled); continue; }
                item.Start();
                Raise();
                try
                {
                    item.Step.Run(new SetupStepContext(item, token));
                    item.Finish(token.IsCancellationRequested ? SetupItemState.Cancelled : SetupItemState.Done);
                }
                catch (Exception) when (token.IsCancellationRequested) { item.Finish(SetupItemState.Cancelled); }
                catch (OperationCanceledException) { item.Finish(SetupItemState.Cancelled); }
                catch (Exception ex)
                {
                    item.AddLog("error: " + ex.Message);
                    item.Finish(SetupItemState.Failed, ex.Message);
                }
                Raise();
            }
            Raise();
        }

        /// <summary>Stops the step that runs (it ends as Cancelled) and the ones that wait.</summary>
        public void Cancel()
        {
            lock (_lock) _cancel?.Cancel();
        }

        /// <summary>Tries the failed and cancelled steps again (<paramref name="item"/>: only that one), once the run is through.</summary>
        public Task? Retry(SetupItem? item = null)
        {
            if (Running) return null;
            foreach (var i in item != null ? new[] { item } : Items.ToArray())
                if (i.State is SetupItemState.Failed or SetupItemState.Cancelled) i.Reset();
            return Start();
        }

        private void Raise()
        {
            try { StatusChanged?.Invoke(); } catch (Exception) { }
        }

        /// <summary>The line of the status bar: "Setup: 2 of 3 - Downloading Qwen3.5 0.8B 45%", "Setup done", "Setup: 1 failed".</summary>
        public string Summary
        {
            get
            {
                int done = Items.Count(i => i.State == SetupItemState.Done);
                var running = Items.FirstOrDefault(i => i.State == SetupItemState.Running);
                if (running != null)
                {
                    double? f = running.Fraction;
                    return $"Setup {Items.IndexOf(running) + 1}/{Items.Count}: {running.Title}" + (f is { } x ? $" {(int)(x * 100)}%" : "");
                }
                int failed = Items.Count(i => i.State == SetupItemState.Failed);
                int cancelled = Items.Count(i => i.State == SetupItemState.Cancelled);
                if (failed > 0) return $"Setup: {failed} of {Items.Count} failed";
                if (cancelled > 0) return $"Setup: {cancelled} of {Items.Count} cancelled";
                return done == Items.Count ? "Setup done" : $"Setup: {done} of {Items.Count} done";
            }
        }

        /// <summary>The progress of the whole run (0..1): every step counts the same, the one that runs with its fraction.</summary>
        public double Fraction
        {
            get
            {
                if (Items.Count == 0) return 1;
                double sum = 0;
                foreach (var i in Items)
                    sum += i.State switch { SetupItemState.Done or SetupItemState.Failed or SetupItemState.Cancelled => 1, SetupItemState.Running => i.Fraction ?? 0, _ => 0 };
                return sum / Items.Count;
            }
        }
    }

    internal static class ListExtensions
    {
        public static int IndexOf<T>(this IReadOnlyList<T> list, T item)
        {
            for (int i = 0; i < list.Count; i++) if (EqualityComparer<T>.Default.Equals(list[i], item)) return i;
            return -1;
        }
    }

    /// <summary>The speed of a download over the last seconds (a window of samples, so that a pause shows soon); the clock is given (the tests).</summary>
    public sealed class SpeedMeter(Func<double> clock, double window = 3.0)
    {
        private readonly Queue<(double Time, long Bytes)> _samples = new();

        public void Reset() => _samples.Clear();

        public void Add(long bytes)
        {
            double now = clock();
            _samples.Enqueue((now, bytes));
            while (_samples.Count > 2 && now - _samples.Peek().Time > window) _samples.Dequeue();
        }

        public double BytesPerSecond
        {
            get
            {
                if (_samples.Count < 2) return 0;
                var first = _samples.Peek();
                var last = _samples.Last();
                double seconds = last.Time - first.Time;
                return seconds <= 0.2 ? 0 : Math.Max(0, (last.Bytes - first.Bytes) / seconds);
            }
        }
    }

    /// <summary>Sizes, speeds and times as the setup shows them.</summary>
    public static class SetupFormat
    {
        /// <summary>"0 B", "812 KB", "507 MB", "1.0 GB" (decimal units, as ai.models' SizeText for the models).</summary>
        public static string Size(long bytes)
        {
            if (bytes < 1000) return $"{bytes} B";
            if (bytes < 1_000_000) return $"{bytes / 1000} KB";
            if (bytes < 1_000_000_000) return bytes < 10_000_000 ? $"{bytes / 1_000_000.0:0.0} MB" : $"{bytes / 1_000_000} MB";
            return $"{bytes / 1_000_000_000.0:0.0} GB";
        }

        public static string Speed(double bytesPerSecond) => Size((long)bytesPerSecond) + "/s";

        /// <summary>"about 12 s left", "about 3 min left", "about 1 h 5 min left".</summary>
        public static string Remaining(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "";
            if (seconds < 60) return $"about {Math.Max(1, (int)Math.Ceiling(seconds))} s left";
            if (seconds < 3600) return $"about {(int)Math.Ceiling(seconds / 60)} min left";
            int h = (int)(seconds / 3600);
            return $"about {h} h {(int)Math.Ceiling((seconds - h * 3600) / 60)} min left";
        }
    }
}
