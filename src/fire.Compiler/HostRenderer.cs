using fire.Terminal;
using fire.Terminal.Event;

namespace fire.Compiler
{
    /// <summary>The windows of a host (<see cref="IHostDisplay"/>) for the C# implementation of graphics and windows: where the C library cannot be had (no C compiler,
    /// FIRE_VM_GRAPHICS=cs), the editor's live and design views run on it the same way. Every window of the program is window 1 of the host.</summary>
    internal sealed class HostRenderer : IFramebufferRenderer
    {
        private readonly IHostDisplay? _display;
        private int _buttons;
        private bool _closed;
        private int _cursor;

        public HostRenderer(IHostDisplay? display) => _display = display;

        public bool VSync { get; set; } = true;
        public void Initialize(string title, int initialWidth, int initialHeight, int internalHandle) { }

        public WindowPumpResult PumpEvents()
        {
            var events = new List<IEvent>();
            while (_display != null && !_closed && _display.NextEvent(1, out var e))
            {
                switch ((EventType)e.Type)
                {
                    case EventType.MouseDown:
                    case EventType.MouseUp:
                        bool down = e.Type == (int)EventType.MouseDown;
                        if (e.Button is >= 1 and <= 31) _buttons = down ? _buttons | (1 << (e.Button - 1)) : _buttons & ~(1 << (e.Button - 1));
                        events.Add(new ClickEvent { Type = (EventType)e.Type, Button = e.Button, X = e.X, Y = e.Y, IsButtonDown = down });
                        break;
                    case EventType.MouseMove: events.Add(new MotionEvent { Type = EventType.MouseMove, X = e.X, Y = e.Y, ButtonState = _buttons }); break;
                    case EventType.MouseScroll: events.Add(new ScrollEvent { Type = EventType.MouseScroll, ScrollX = e.Dx, ScrollY = e.Dy, X = e.X, Y = e.Y }); break;
                    case EventType.KeyDown:
                    case EventType.KeyUp:
                        events.Add(new KeyEvent { Type = (EventType)e.Type, KeyCode = e.KeyCode, Modifier = e.Modifiers, IsButtonDown = e.Type == (int)EventType.KeyDown });
                        break;
                    case EventType.TextInput: if (!string.IsNullOrEmpty(e.Text)) events.Add(new TextEvent { Type = EventType.TextInput, Text = e.Text }); break;
                    case EventType.Resize: if (e.Width > 0 && e.Height > 0) events.Add(new ResizeEvent { Type = EventType.Resize, Width = e.Width, Height = e.Height }); break;
                    case EventType.Close: _closed = true; break;
                }
            }
            return new WindowPumpResult { StillOpen = !_closed, Events = events };
        }

        public void Present(Framebuffer framebuffer)
        {
            int n = Math.Min(framebuffer.Width * framebuffer.Height, framebuffer.Pixels.Length);
            _display?.Present(1, framebuffer.Pixels.AsSpan(0, n), framebuffer.Width, framebuffer.Height, VSync);
        }

        public void SetCursor(int cursor)
        {
            if (cursor == _cursor) return;
            _cursor = cursor;
            _display?.Cursor(1, cursor);
        }

        public void Dispose() { }
    }
}
