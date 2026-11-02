using SDL3;

namespace fire.Terminal.Sdl
{
    /// <summary>The clipboard of the desktop through SDL3 (text). The video part of SDL is started for it once (on X11 SDL needs a window of its own for the
    /// clipboard); the calls hold <see cref="SdlGate.Lock"/>, which the pumping of the windows holds as well - on X11 SDL waits for the answer of the
    /// clipboard's owner by pumping events itself. A desktop that cannot be reached (no SDL, no display) gives the program a clipboard of its own instead.</summary>
    public sealed class SdlClipboard : IClipboard
    {
        private readonly MemoryClipboard _fallback = new();
        private bool _tried;
        private bool _ready;

        private bool Ready()
        {
            if (_tried) return _ready;
            _tried = true;
            try { _ready = SDL.InitSubSystem(SDL.InitFlags.Video); }
            catch (System.Exception) { _ready = false; }   // (no SDL library on this machine)
            return _ready;
        }

        public string? GetText()
        {
            lock (SdlGate.Lock)
            {
                if (!Ready()) return _fallback.GetText();
                if (!SDL.HasClipboardText()) return null;
                string? text = SDL.GetClipboardText();
                return string.IsNullOrEmpty(text) ? null : text;
            }
        }

        public bool SetText(string text)
        {
            lock (SdlGate.Lock)
            {
                if (!Ready()) return _fallback.SetText(text);
                return SDL.SetClipboardText(text);
            }
        }

        public bool HasText
        {
            get
            {
                lock (SdlGate.Lock)
                {
                    if (!Ready()) return _fallback.HasText;
                    return SDL.HasClipboardText();
                }
            }
        }
    }

    /// <summary>One lock for everything that touches SDL's events: the pumping of the windows (from the threads of the window managers) and the clipboard.</summary>
    public static class SdlGate
    {
        public static readonly object Lock = new();
    }
}
