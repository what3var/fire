using fire.Bytecode;
using fire.Runtime;
using fire.Terminal;
using fire.Terminal.Event;
using fire.Values;
using System;
using System.Collections.Generic;
using System.Threading;

namespace fire.Compiler
{
    /// <summary>
    /// A fire program with a user interface running INSIDE a host application (the circuit editor of spark shows the CircuitView of the package circuits.ui this way):
    /// the program runs on a thread of its own; its window is not a window of the system but <see cref="HostWindow"/> - the host gets every picture
    /// (<see cref="FrameReady"/>, <see cref="CopyFrame"/>) and hands in the mouse, the keys and the size of the area it shows the picture in. The program talks to the host
    /// with `print` (every line arrives as <see cref="Message"/>) and gets the host's commands from the function `extern string __HostCommand()` (the next command, "" when
    /// there is none).
    /// </summary>
    public sealed class LiveSession : IDisposable
    {
        /// <summary>The window of the program (<see cref="IHostDisplay"/>: the C library's, or <see cref="HostRenderer"/> over the C# one): events from the host, pictures to the
        /// host, about 60 per second.</summary>
        private sealed class HostWindow : IHostDisplay
        {
            private readonly LiveSession _owner;
            private long _lastPresent;
            public HostWindow(LiveSession owner) => _owner = owner;

            public bool NextEvent(int window, out HostDisplayEvent e)
            {
                lock (_owner._lock)
                {
                    if (_owner._events.Count == 0) { e = default; return false; }
                    e = _owner._events[0];
                    _owner._events.RemoveAt(0);
                    return true;
                }
            }

            public void Present(int window, ReadOnlySpan<uint> source, int width, int height, bool vsync)
            {
                bool changed;
                lock (_owner._lock)
                {
                    int n = width * height;
                    // the same picture as last time (a paused simulation, an idle editor): nothing to hand over - the host neither copies nor uploads it again
                    changed = _owner._pixels.Length != n || _owner._width != width || _owner._height != height || !source.SequenceEqual(_owner._pixels.AsSpan(0, source.Length));
                    if (changed)
                    {
                        if (_owner._pixels.Length != n) _owner._pixels = new uint[n];
                        source.CopyTo(_owner._pixels);
                        _owner._width = width;
                        _owner._height = height;
                        _owner._frame++;
                    }
                }
                if (changed) _owner.FrameReady?.Invoke();
                // about 60 pictures per second: the program waits like for the vertical blank of a screen
                long now = Environment.TickCount64;
                long wait = 16 - (now - _lastPresent);
                if (wait > 0 && vsync) Thread.Sleep((int)wait);
                _lastPresent = Environment.TickCount64;
            }

            /// <summary>Window.Cursor of the program: the host shows that pointer over the picture.</summary>
            public void Cursor(int window, int cursor)
            {
                if (cursor == _owner.Cursor) return;
                _owner.Cursor = cursor;
                _owner.CursorChanged?.Invoke(cursor);
            }
        }

        private readonly object _lock = new();
        private readonly List<HostDisplayEvent> _events = new();
        private readonly Queue<string> _commands = new();
        private uint[] _pixels = Array.Empty<uint>();
        private int _width, _height;
        private long _frame;
        private volatile bool _stopping;
        private Thread? _thread;
        private RuntimeSession? _session;

        /// <summary>A line the program printed (on the thread of the program).</summary>
        public event Action<string>? Message;
        /// <summary>A new picture is there (on the thread of the program): take it with <see cref="CopyFrame"/>.</summary>
        public event Action? FrameReady;
        /// <summary>The program wants another mouse pointer over its picture (SDL numbering: 0 arrow, 1 text, ... 11 hand, see UI.Cursor); on the thread of the program.</summary>
        public event Action<int>? CursorChanged;

        /// <summary>The mouse pointer the program wants now.</summary>
        public int Cursor { get; private set; }

        /// <summary>The program ended (null) or failed (the message); on the thread of the program.</summary>
        public event Action<string?>? Ended;

        public bool IsRunning => _thread is { IsAlive: true };

        /// <summary>Compiles and starts the program (`basePath`: where its `#include`s are looked for). Compile errors are thrown here.</summary>
        public void Start(string script, string? basePath = null)
        {
            var externs = new ExternRegistry();
            externs.Register("__HostCommand", _ =>
            {
                lock (_lock) return _commands.Count > 0 ? _commands.Dequeue() : "";
            });
            _session = RuntimeSession.Build(new[] { script }, VmExecutionMode.Release, args =>
            {
                if (args.Length > 0) Message?.Invoke(args[0].ToString() ?? "");
                return Value.MakeUndefined();
            }, basePath: basePath, externs: externs, hostWindows: new HostWindows(new HostWindow(this)));
            var session = _session;
            _thread = new Thread(() =>
            {
                string? error = null;
                try
                {
                    session.Run();
                    if (session.VirtualMachine?.UnhandledException != null) error = new UncaughtScriptException(session.VirtualMachine.UnhandledException).Message;
                }
                catch (Exception ex) { error = CompileErrors.Describe(ex); }
                finally
                {
                    try { session.CloseHostResources(); } catch (Exception) { }
                }
                Ended?.Invoke(error);
            }) { IsBackground = true, Name = "fire live session" };
            _thread.Start();
        }

        /// <summary>A command for the program (it reads it with `__HostCommand()`).</summary>
        public void Send(string command)
        {
            lock (_lock) _commands.Enqueue(command);
        }

        private void Post(HostDisplayEvent e)
        {
            lock (_lock)
            {
                // a move right after a move replaces it: the program only needs the last position (a fast pointer would flood it)
                if (e.Type == (int)EventType.MouseMove && _events.Count > 0 && _events[^1].Type == (int)EventType.MouseMove) _events[^1] = e;
                else _events.Add(e);
            }
        }

        /// <summary>Mouse button pressed / released at (x, y) in pixels of the picture (1 left, 2 middle, 3 right).</summary>
        public void MouseButton(int button, bool down, double x, double y) =>
            Post(new HostDisplayEvent { Type = (int)(down ? EventType.MouseDown : EventType.MouseUp), Button = button, X = (float)x, Y = (float)y });

        public void MouseMove(double x, double y, int buttons) => Post(new HostDisplayEvent { Type = (int)EventType.MouseMove, X = (float)x, Y = (float)y, Buttons = buttons });

        /// <summary>The wheel (dy > 0: up) at (x, y).</summary>
        public void Wheel(double dx, double dy, double x, double y) => Post(new HostDisplayEvent { Type = (int)EventType.MouseScroll, Dx = (float)dx, Dy = (float)dy, X = (float)x, Y = (float)y });

        /// <summary>A key with the codes of SDL (letters are their lower case character, see UI.Keys) and its modifiers (1/2 shift, 64/128 ctrl, 256/512 alt).</summary>
        public void Key(int keyCode, int modifiers, bool down) =>
            Post(new HostDisplayEvent { Type = (int)(down ? EventType.KeyDown : EventType.KeyUp), KeyCode = keyCode, Modifiers = modifiers });

        /// <summary>Typed text (in pieces that fit an event of the display: up to 31 bytes of UTF-8 each).</summary>
        public void Text(string text)
        {
            var piece = new System.Text.StringBuilder();
            int bytes = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 31) { Post(new HostDisplayEvent { Type = (int)EventType.TextInput, Text = piece.ToString() }); piece.Clear(); bytes = 0; }
                piece.Append(rune.ToString());
                bytes += rune.Utf8SequenceLength;
            }
            if (piece.Length > 0) Post(new HostDisplayEvent { Type = (int)EventType.TextInput, Text = piece.ToString() });
        }

        /// <summary>The area that shows the picture has a new size: the program's framebuffer follows (with AutoResize) and it lays itself out again.</summary>
        public void Resize(int width, int height) => Post(new HostDisplayEvent { Type = (int)EventType.Resize, Width = width, Height = height });

        /// <summary>The last picture (RGBA per pixel, row by row) into `into` (grown when needed); its number (counts up with every picture) and size.</summary>
        public long CopyFrame(ref uint[] into, out int width, out int height)
        {
            lock (_lock)
            {
                if (into.Length != _pixels.Length) into = new uint[_pixels.Length];
                Array.Copy(_pixels, into, _pixels.Length);
                width = _width;
                height = _height;
                return _frame;
            }
        }

        /// <summary>Stops the program (its window is closed) and waits a moment for it.</summary>
        public void Stop()
        {
            if (!_stopping) Post(new HostDisplayEvent { Type = (int)EventType.Close });   // (the window closes: the program's loop ends)
            _stopping = true;
            if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(2000);
        }

        public void Dispose() => Stop();
    }
}
