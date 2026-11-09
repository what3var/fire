using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace fire.Editor
{
    /// <summary>The Runtime panel (docked on the right, shown while a program runs - like the diagnostic tools of Visual Studio): how busy and how big the program is
    /// over the last minute (CPU and memory as curves), a few numbers (CPU now and on average, memory now and at its peak, collections of the .NET heap for a run in
    /// the VM, threads, lines of output) and what happened (start, pauses, errors, new threads, the end with its exit code and duration). A native program is its
    /// own process; the virtual machine runs inside spark, so for it the numbers are those of spark's process.</summary>
    public sealed class RuntimePanelControl : UserControl
    {
        private const int Samples = 120;          // 60 s at 2 samples a second
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

        private readonly TextBlock _state = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _source = new() { Opacity = 0.65, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        private readonly Graph _cpu = new(Color.FromRgb(0x60, 0xA5, 0xFA), 100);
        private readonly Graph _memory = new(Color.FromRgb(0xF9, 0x73, 0x16), 0);
        private readonly TextBlock _cpuText = new() { FontSize = 11 };
        private readonly TextBlock _memoryText = new() { FontSize = 11 };
        private readonly TextBlock _numbers = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        private readonly ObservableCollection<string> _events = new();
        private readonly DispatcherTimer _timer;
        private readonly ScrollViewer _eventsScroll;

        private Process? _process;            // the native program; null: the VM in spark's own process
        private bool _remote;                 // a program on a board (debugged over its serial line): no process here to measure
        private Stopwatch _clock = new();
        private TimeSpan _lastCpu;
        private DateTime _lastSample;
        private double _cpuSum;
        private int _cpuCount;
        private long _peak;
        private int[] _gcStart = new int[3];
        private int _lastGen2;
        private int _outputLines;
        private int _threads;

        /// <summary>A run is being watched (between Start and Stop).</summary>
        public bool IsWatching { get; private set; }

        public RuntimePanelControl()
        {
            _timer = new DispatcherTimer { Interval = Interval };
            _timer.Tick += (_, _) => Sample();
            var list = new ItemsControl
            {
                ItemsSource = _events,
                ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((text, _) =>
                    new SelectableTextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 1) }),
            };
            _eventsScroll = new ScrollViewer { Content = list, MinHeight = 60 };
            var events = _eventsScroll;
            var top = new StackPanel
            {
                Spacing = 4, Margin = new Thickness(8, 6, 8, 4),
                Children =
                {
                    _state, _source,
                    Caption("CPU"), _cpu, _cpuText,
                    Caption("Memory"), _memory, _memoryText,
                    _numbers,
                    Caption("Events"),
                },
            };
            var dock = new DockPanel();
            DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
            dock.Children.Add(top);
            dock.Children.Add(events);
            Content = dock;
            ShowIdle();
        }

        private static TextBlock Caption(string text) => new() { Text = text, Opacity = 0.6, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

        private void ShowIdle()
        {
            _state.Text = "No program runs.";
            _source.Text = "Start a program (F5): this panel shows how busy it is, how much memory it takes and what happens.";
        }

        /// <summary>A run begins: `process` is the native program (null: the virtual machine, which runs in spark), `what` says which (shown at the top).</summary>
        public void Start(Process? process, string what, bool remote = false)
        {
            _process = process;
            _remote = remote;
            _clock = Stopwatch.StartNew();
            _events.Clear();
            _cpu.Clear(); _memory.Clear();
            _cpuSum = 0; _cpuCount = 0; _peak = 0; _outputLines = 0; _threads = 1;
            _gcStart = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            _lastGen2 = _gcStart[2];
            _lastCpu = CpuTime();
            _lastSample = DateTime.UtcNow;
            _state.Text = $"Running: {what}";
            _source.Text = remote ? "the program runs on a board: no CPU or memory of it here, only what happens"
                : process != null ? $"the process of the program (pid {SafePid(process)})" : "the virtual machine runs inside spark: the numbers are those of spark's process";
            IsWatching = true;
            Event($"started ({what})");
            _timer.Start();
            Sample();
        }

        /// <summary>The run has ended (`exitCode` of a native program, if known).</summary>
        public void Stop(int? exitCode = null)
        {
            if (!IsWatching) return;
            Sample();
            _timer.Stop();
            IsWatching = false;
            string duration = Duration(_clock.Elapsed);
            _state.Text = $"Ended after {duration}" + (exitCode is { } code ? $" (exit code {code})" : "");
            Event(exitCode is { } c ? $"ended, exit code {c}, after {duration}" : $"ended after {duration}");
            _process = null;
        }

        /// <summary>Something happened during the run (a pause, an error, a thread).</summary>
        public void Event(string text)
        {
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Event(text)); return; }
            _events.Add($"{Duration(_clock.Elapsed),8}  {text}");
            if (_events.Count > 500) _events.RemoveAt(0);
            Dispatcher.UIThread.Post(() => _eventsScroll.ScrollToEnd(), DispatcherPriority.Background);
        }

        /// <summary>A line of output of the program (counted).</summary>
        public void OutputLine() => _outputLines++;

        /// <summary>A new fire thread of the program.</summary>
        public void ThreadStarted(string name) { _threads++; Event($"thread started: {name}"); }

        /// <summary>The state line while the program is paused or runs again.</summary>
        public void SetPaused(bool paused, string? where = null)
        {
            if (!IsWatching) return;
            _state.Text = paused ? $"Paused{(where != null ? " " + where : "")}" : _state.Text.StartsWith("Paused", StringComparison.Ordinal) ? "Running" : _state.Text;
        }

        private TimeSpan CpuTime()
        {
            try
            {
                if (_process != null) { _process.Refresh(); return _process.HasExited ? _lastCpu : _process.TotalProcessorTime; }
                return Process.GetCurrentProcess().TotalProcessorTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return _lastCpu; }
        }

        private long MemoryBytes()
        {
            try
            {
                if (_process != null) return _process.HasExited ? 0 : _process.WorkingSet64;
                return Process.GetCurrentProcess().WorkingSet64;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return 0; }
        }

        private void Sample()
        {
            if (_remote)
            {
                _cpuText.Text = _memoryText.Text = "-";
                _numbers.Text = $"running {Duration(_clock.Elapsed)}  ·  {_threads} thread{(_threads == 1 ? "" : "s")}  ·  {_outputLines} line{(_outputLines == 1 ? "" : "s")} of output";
                return;
            }
            var now = DateTime.UtcNow;
            var cpu = CpuTime();
            double wall = (now - _lastSample).TotalMilliseconds;
            double percent = wall <= 0 ? 0 : Math.Clamp((cpu - _lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100, 0, 100);
            _lastCpu = cpu;
            _lastSample = now;
            long memory = MemoryBytes();
            if (memory > _peak) _peak = memory;
            _cpu.Add(percent);
            _memory.Add(memory / (1024.0 * 1024.0));
            _cpuSum += percent; _cpuCount++;
            _cpuText.Text = $"now {percent:0}%   average {(_cpuSum / Math.Max(1, _cpuCount)):0}%   ({Environment.ProcessorCount} cores = 100%)";
            _memoryText.Text = $"now {Mb(memory)}   peak {Mb(_peak)}";
            var lines = new List<string> { $"running {Duration(_clock.Elapsed)}", $"{_threads} thread{(_threads == 1 ? "" : "s")}", $"{_outputLines} line{(_outputLines == 1 ? "" : "s")} of output" };
            if (_process == null)
            {
                int g0 = GC.CollectionCount(0) - _gcStart[0], g1 = GC.CollectionCount(1) - _gcStart[1], g2 = GC.CollectionCount(2) - _gcStart[2];
                lines.Add($".NET heap {Mb(GC.GetTotalMemory(false))}, collections {g0}/{g1}/{g2} (gen 0/1/2)");
                int total2 = GC.CollectionCount(2);
                if (total2 != _lastGen2) { Event("full garbage collection (gen 2)"); _lastGen2 = total2; }
            }
            else
            {
                try { if (!_process.HasExited) lines.Add($"{_process.Threads.Count} OS threads"); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
            _numbers.Text = string.Join("  ·  ", lines);
        }

        private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.CurrentCulture) + " MB";

        private static string Duration(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00} min" : t.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture) + " s";

        private static string SafePid(Process p)
        {
            try { return p.Id.ToString(CultureInfo.InvariantCulture); }
            catch (InvalidOperationException) { return "?"; }
        }

        /// <summary>A small curve of the last <see cref="Samples"/> values: filled below the line; the scale is `max` (0: the largest value seen).</summary>
        private sealed class Graph : Control
        {
            private readonly Queue<double> _values = new();
            private readonly IBrush _fill;
            private readonly IPen _line;
            private readonly double _max;

            public Graph(Color color, double max)
            {
                _fill = new SolidColorBrush(color, 0.25);
                _line = new Pen(new SolidColorBrush(color), 1.5);
                _max = max;
                Height = 48;
                HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            public void Add(double v)
            {
                _values.Enqueue(v);
                while (_values.Count > Samples) _values.Dequeue();
                InvalidateVisual();
            }

            public void Clear() { _values.Clear(); InvalidateVisual(); }

            public override void Render(DrawingContext context)
            {
                var b = Bounds;
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), null, new Rect(0, 0, b.Width, b.Height), 3, 3);
                if (_values.Count < 2) return;
                double max = _max > 0 ? _max : Math.Max(1e-9, _values.Max() * 1.15);
                double step = b.Width / (Samples - 1);
                double x0 = b.Width - step * (_values.Count - 1);
                var points = _values.Select((v, i) => new Point(x0 + i * step, b.Height - Math.Clamp(v / max, 0, 1) * (b.Height - 2) - 1)).ToList();
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(points[0].X, b.Height), true);
                    foreach (var p in points) g.LineTo(p);
                    g.LineTo(new Point(points[^1].X, b.Height));
                    g.EndFigure(true);
                }
                context.DrawGeometry(_fill, null, area);
                var line = new StreamGeometry();
                using (var g = line.Open())
                {
                    g.BeginFigure(points[0], false);
                    foreach (var p in points.Skip(1)) g.LineTo(p);
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, _line, line);
            }
        }
    }
}
