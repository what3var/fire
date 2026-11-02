namespace fire.Terminal
{
    /// <summary>The clipboard of the desktop (text) as `Clipboard` of `#import "windows"` sees it. The VM uses SDL's (fire.Terminal.Sdl.SdlClipboard);
    /// <see cref="MemoryClipboard"/> keeps the text in the program (tests, a host without a desktop).</summary>
    public interface IClipboard
    {
        /// <summary>The text on the clipboard, null when there is none.</summary>
        string? GetText();

        /// <summary>Puts text on the clipboard; false when the desktop refuses.</summary>
        bool SetText(string text);

        bool HasText { get; }
    }

    /// <summary>A clipboard of the program's own.</summary>
    public sealed class MemoryClipboard : IClipboard
    {
        private readonly object _gate = new();
        private string? _text;

        public string? GetText() { lock (_gate) return string.IsNullOrEmpty(_text) ? null : _text; }

        public bool SetText(string text) { lock (_gate) _text = text; return true; }

        public bool HasText { get { lock (_gate) return !string.IsNullOrEmpty(_text); } }
    }
}
