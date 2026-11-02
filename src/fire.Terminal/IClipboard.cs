namespace fire.Terminal
{
    /// <summary>The clipboard of the desktop (text, pictures, files) as `Clipboard` of `#import "windows"` sees it. The VM uses SDL's (fire.Terminal.Sdl.SdlClipboard);
    /// <see cref="MemoryClipboard"/> keeps everything in the program (tests, a host without a desktop). It holds one thing at a time, as a desktop does: putting a picture
    /// there takes the text away.</summary>
    public interface IClipboard
    {
        /// <summary>The text on the clipboard, null when there is none.</summary>
        string? GetText();

        /// <summary>Puts text on the clipboard; false when the desktop refuses.</summary>
        bool SetText(string text);

        bool HasText { get; }

        /// <summary>The picture on the clipboard: the bytes of a PNG (or BMP) file, null when there is none.</summary>
        byte[]? GetImage();

        /// <summary>Puts a picture there (the bytes of a PNG file); false when the desktop refuses.</summary>
        bool SetImage(byte[] png);

        bool HasImage { get; }

        /// <summary>The paths of the files on the clipboard (empty: none).</summary>
        string[] GetFiles();

        /// <summary>Puts paths there; false when the desktop refuses.</summary>
        bool SetFiles(string[] paths);

        bool HasFiles { get; }
    }

    /// <summary>A clipboard of the program's own: all kinds, one at a time.</summary>
    public sealed class MemoryClipboard : IClipboard
    {
        private readonly object _gate = new();
        private string? _text;
        private byte[]? _image;
        private string[]? _files;

        private void Clear() { _text = null; _image = null; _files = null; }

        public string? GetText() { lock (_gate) return string.IsNullOrEmpty(_text) ? null : _text; }

        public bool SetText(string text) { lock (_gate) { Clear(); _text = text; } return true; }

        public bool HasText { get { lock (_gate) return !string.IsNullOrEmpty(_text); } }

        public byte[]? GetImage() { lock (_gate) return _image == null ? null : (byte[])_image.Clone(); }

        public bool SetImage(byte[] png)
        {
            if (png == null || png.Length == 0) return false;
            lock (_gate) { Clear(); _image = (byte[])png.Clone(); }
            return true;
        }

        public bool HasImage { get { lock (_gate) return _image != null; } }

        public string[] GetFiles() { lock (_gate) return _files == null ? System.Array.Empty<string>() : (string[])_files.Clone(); }

        public bool SetFiles(string[] paths)
        {
            var kept = System.Array.FindAll(paths ?? System.Array.Empty<string>(), p => !string.IsNullOrEmpty(p) && !p.Contains('\n'));
            if (kept.Length == 0) return false;
            lock (_gate) { Clear(); _files = kept; }
            return true;
        }

        public bool HasFiles { get { lock (_gate) return _files != null; } }
    }
}
