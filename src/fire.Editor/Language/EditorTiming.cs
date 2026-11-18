using System;
using System.Diagnostics;
using Avalonia.Threading;

namespace fire.Editor
{
    /// <summary>FIRE_EDITOR_TIMING=1: what the editor does on the UI thread and in the background while typing goes to stderr (to measure big solutions): every step that takes
    /// longer than a millisecond (highlighting, the symbol index, the analysis, the error list), the time from a key to the frame that shows it, and every stall of the UI thread
    /// (a timer that should tick every 50 ms and came late). Off: nothing is measured.</summary>
    internal static class EditorTiming
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("FIRE_EDITOR_TIMING") == "1";

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static double Now => Clock.Elapsed.TotalMilliseconds;

        public static void Log(string text)
        {
            if (On) Console.Error.WriteLine($"[editor {DateTime.Now:HH:mm:ss.fff}] {text}");
        }

        /// <summary>Measures the step until the result is disposed and logs it when it took at least `minimumMs` (nothing when timing is off).</summary>
        public static Scope Measure(string what, double minimumMs = 1) => new(On ? what : null, minimumMs);

        public readonly struct Scope : IDisposable
        {
            private readonly string? _what;
            private readonly double _start, _minimum;

            public Scope(string? what, double minimum)
            {
                _what = what;
                _minimum = minimum;
                _start = what == null ? 0 : Now;
            }

            public void Dispose()
            {
                if (_what == null) return;
                double ms = Now - _start;
                if (ms >= _minimum) Log($"{_what}: {ms:F1} ms{(Dispatcher.UIThread.CheckAccess() ? " (UI)" : "")}");
            }
        }

        private static DispatcherTimer? _watchdog;
        private static double _lastTick, _pause;
        private static int _gen2;

        /// <summary>Starts the stall watchdog of the UI thread (once; only when timing is on).</summary>
        public static void StartWatchdog()
        {
            if (!On || _watchdog != null) return;
            _lastTick = Now;
            _watchdog = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, (_, _) =>
            {
                double now = Now, late = now - _lastTick - 50;
                _lastTick = now;
                int gen2 = GC.CollectionCount(2);
                double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
                if (late >= 30) Log($"UI stall: {late + 50:F0} ms between ticks (GC: {gen2 - _gen2} full, {pause - _pause:F0} ms paused)");
                _gen2 = gen2;
                _pause = pause;
            });
            _watchdog.Start();
        }

        /// <summary>A key went in: the time until the next frame is drawn is logged ("key -> frame").</summary>
        public static void KeyIn(Avalonia.Controls.TopLevel? top)
        {
            if (!On || top == null) return;
            double start = Now;
            top.RequestAnimationFrame(_ => Log($"key -> frame: {Now - start:F1} ms"));
        }
    }
}
