using System.Runtime.InteropServices;
using System.Text;
using SDL3;

namespace fire.Terminal.Sdl
{
    /// <summary>The clipboard of the desktop through SDL3: text, pictures (image/png, image/bmp - SDL3 puts image/bmp on the Windows clipboard as CF_DIB) and files
    /// (text/uri-list and x-special/gnome-copied-files; on Windows SDL3 has no CF_HDROP, so files stay within the program there). The video part of SDL is started for it
    /// once (on X11 SDL needs a window of its own for the clipboard); the calls hold <see cref="SdlGate.Lock"/>, which the pumping of the windows holds as well - on X11
    /// SDL waits for the answer of the clipboard's owner by pumping events itself. A desktop that cannot be reached (no SDL, no display) gives the program a clipboard of its
    /// own instead. The same kinds as the native display (native/platform/std/fire_display_sdl3.h).</summary>
    public sealed class SdlClipboard : IClipboard
    {
        private readonly MemoryClipboard _fallback = new();
        private bool _tried;
        private bool _ready;

        private bool Ready()
        {
            if (!_tried)
            {
                _tried = true;
                try { _ready = SDL.InitSubSystem(SDL.InitFlags.Video); }
                catch (System.Exception) { _ready = false; }   // (no SDL library on this machine)
                if (_ready)
                {
                    SDL.PumpEvents();
                    try { X11Refresh.Run(); }
                    catch (System.Exception) { }   // (no libX11: nothing to ask)
                }
            }
            // what other programs did with the clipboard arrives as events (X11: the owner changed, then the kinds it offers): taken in before the clipboard is read
            if (_ready) SDL.PumpEvents();
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

        // ---- what the program offers: SDL asks the callback for each kind when someone wants it; the data lives in unmanaged memory until SDL cleans it up ----

        private sealed class Offer
        {
            public readonly Dictionary<string, (IntPtr Data, int Size)> Kinds = new();
        }

        private static readonly object OffersGate = new();
        private static readonly Dictionary<IntPtr, Offer> Offers = new();
        private static long _nextOffer;
        // (the delegates live as long as the program: SDL keeps the function pointers)
        private static readonly SDL.ClipboardDataCallback DataCallback = OfferData;
        private static readonly SDL.ClipboardCleanupCallback CleanupCallback = OfferCleanup;

        private static IntPtr OfferData(IntPtr userdata, string mimeType, out UIntPtr size)
        {
            size = UIntPtr.Zero;
            lock (OffersGate)
            {
                if (!Offers.TryGetValue(userdata, out var offer) || !offer.Kinds.TryGetValue(mimeType, out var kind)) return IntPtr.Zero;
                size = (UIntPtr)kind.Size;
                return kind.Data;
            }
        }

        private static void OfferCleanup(IntPtr userdata)
        {
            lock (OffersGate)
            {
                if (!Offers.Remove(userdata, out var offer)) return;
                foreach (var kind in offer.Kinds.Values) Marshal.FreeHGlobal(kind.Data);
            }
        }

        /// <summary>Offers these kinds on the clipboard (the caller holds the lock and SDL is ready).</summary>
        private static bool SetData(IReadOnlyList<(string Mime, byte[] Data)> kinds)
        {
            var offer = new Offer();
            foreach (var (mime, data) in kinds)
            {
                IntPtr p = Marshal.AllocHGlobal(Math.Max(1, data.Length));
                Marshal.Copy(data, 0, p, data.Length);
                offer.Kinds[mime] = (p, data.Length);
            }
            IntPtr key;
            lock (OffersGate)
            {
                key = (IntPtr)(++_nextOffer);
                Offers[key] = offer;
            }
            string[] mimes = kinds.Select(k => k.Mime).ToArray();
            // (when SDL fails it has already called the cleanup)
            return SDL.SetClipboardData(DataCallback, CleanupCallback, key, mimes, (UIntPtr)mimes.Length);
        }

        /// <summary>A kind from the clipboard, null when it is not there (or empty). The caller holds the lock.</summary>
        private static byte[]? GetData(string mime)
        {
            if (!SDL.HasClipboardData(mime)) return null;
            IntPtr p = SDL.GetClipboardData(mime, out UIntPtr size);
            if (p == IntPtr.Zero) return null;
            try
            {
                int n = (int)size;
                if (n <= 0) return null;
                var bytes = new byte[n];
                Marshal.Copy(p, bytes, 0, n);
                return bytes;
            }
            finally { SDL.Free(p); }
        }

        public byte[]? GetImage()
        {
            lock (SdlGate.Lock)
            {
                if (!Ready()) return _fallback.GetImage();
                return GetData("image/png") ?? GetData("image/bmp");
            }
        }

        public bool SetImage(byte[] png)
        {
            if (png == null || png.Length == 0) return false;
            // the desktops that want a BMP (Windows: CF_DIB) get one made from the picture
            byte[]? bmp = null;
            try { bmp = ImageEncoder.EncodeBmp(ImageDecoder.Decode(png)); }
            catch (ImageFormatException) { return false; }
            lock (SdlGate.Lock)
            {
                if (!Ready()) return _fallback.SetImage(png);
                return SetData(new[] { ("image/png", png), ("image/bmp", bmp) });
            }
        }

        public bool HasImage
        {
            get
            {
                lock (SdlGate.Lock)
                {
                    if (!Ready()) return _fallback.HasImage;
                    return SDL.HasClipboardData("image/png") || SDL.HasClipboardData("image/bmp");
                }
            }
        }

        public string[] GetFiles()
        {
            lock (SdlGate.Lock)
            {
                if (!Ready()) return _fallback.GetFiles();
                var list = GetData("text/uri-list") ?? GetData("x-special/gnome-copied-files");
                return list == null ? Array.Empty<string>() : PathsFromUriList(Encoding.UTF8.GetString(list));
            }
        }

        public bool SetFiles(string[] paths)
        {
            var kept = Array.FindAll(paths ?? Array.Empty<string>(), p => !string.IsNullOrEmpty(p) && !p.Contains('\n'));
            if (kept.Length == 0) return false;
            string uris = UriListFromPaths(kept);
            lock (SdlGate.Lock)
            {
                if (!Ready()) return _fallback.SetFiles(kept);
                return SetData(new[] { ("text/uri-list", Encoding.UTF8.GetBytes(uris)), ("x-special/gnome-copied-files", Encoding.UTF8.GetBytes("copy\n" + uris)) });
            }
        }

        public bool HasFiles => GetFiles().Length > 0;

        /// <summary>The paths as a text/uri-list: `file://` URIs, each line ending in CR LF, the bytes a URI may not hold as %XX (C:\a\b becomes file:///C:/a/b).</summary>
        public static string UriListFromPaths(IEnumerable<string> paths)
        {
            var sb = new StringBuilder();
            foreach (string path in paths)
            {
                if (path.Length == 0) continue;
                sb.Append("file://");
                if (path.Length >= 2 && path[1] == ':') sb.Append('/');
                foreach (byte b in Encoding.UTF8.GetBytes(path))
                {
                    char c = b == '\\' ? '/' : (char)b;
                    if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || "/-_.~:!$&'()*+,;=@".IndexOf(c) >= 0)) sb.Append(c);
                    else sb.Append('%').Append(((int)b).ToString("X2"));
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>The paths of a text/uri-list (or of x-special/gnome-copied-files): the file:// URIs; comments and other URIs are left out.</summary>
        public static string[] PathsFromUriList(string list)
        {
            var paths = new List<string>();
            foreach (string raw in list.Split('\n', '\r', '\0'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("file://", StringComparison.Ordinal) || line.Length <= 7) continue;
                int at = line.IndexOf('/', 7);
                if (at < 0) continue;
                string path = line.Substring(at);
                if (OperatingSystem.IsWindows() && path.Length >= 3 && path[2] == ':') path = path.Substring(1);
                var bytes = new List<byte>();
                for (int i = 0; i < path.Length; i++)
                {
                    if (path[i] == '%' && i + 2 < path.Length && Uri.IsHexDigit(path[i + 1]) && Uri.IsHexDigit(path[i + 2]))
                    {
                        bytes.Add((byte)Convert.ToInt32(path.Substring(i + 1, 2), 16));
                        i += 2;
                    }
                    else bytes.AddRange(Encoding.UTF8.GetBytes(path[i].ToString()));
                }
                string decoded = Encoding.UTF8.GetString(bytes.ToArray());
                paths.Add(OperatingSystem.IsWindows() ? decoded.Replace('/', '\\') : decoded);
            }
            return paths.ToArray();
        }
    }

    /// <summary>SDL 3.2 on X11 learns what the clipboard offers only when its owner changes (XFixes tells it, then it asks the owner for TARGETS): what another
    /// program copied before this one started stays invisible - SDL refuses every kind it was not told of. So once, when the clipboard is first used: if SDL
    /// knows no kinds but somebody owns CLIPBOARD, SDL is told that the owner changed (an XFixesSelectionNotify sent through SDL's own connection to a hidden
    /// window of SDL - delivered back to SDL, which then asks for TARGETS itself), and its events are pumped until it has the kinds (at most half a second).
    /// The same as fire_display_x11ClipboardRefresh of native/platform/std/fire_display_sdl3.h. The caller holds <see cref="SdlGate.Lock"/>.</summary>
    internal static unsafe class X11Refresh
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct XFixesNotify { public int type; public nuint serial; public int send_event; public IntPtr display; public nuint window; public int subtype; public nuint owner, selection, time, selectionTime; }
        [StructLayout(LayoutKind.Sequential)]
        private struct XEvent { public fixed long pad[24]; }

        private static int KindCount()
        {
            object? kinds = SDL.GetClipboardMimeTypes(out UIntPtr n);
            if (kinds is IntPtr p && p != IntPtr.Zero) SDL.Free(p);
            return (int)n;
        }

        public static void Run()
        {
            if (!(OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) || SDL.GetCurrentVideoDriver() != "x11" || KindCount() > 0) return;
            if (!NativeLibrary.TryLoad("libX11.so.6", out IntPtr lib) && !NativeLibrary.TryLoad("libX11.so", out lib)) return;
            var queryExtension = (delegate* unmanaged<IntPtr, byte*, int*, int*, int*, int>)NativeLibrary.GetExport(lib, "XQueryExtension");
            var internAtom = (delegate* unmanaged<IntPtr, byte*, int, nuint>)NativeLibrary.GetExport(lib, "XInternAtom");
            var getSelectionOwner = (delegate* unmanaged<IntPtr, nuint, nuint>)NativeLibrary.GetExport(lib, "XGetSelectionOwner");
            var sendEvent = (delegate* unmanaged<IntPtr, nuint, int, nint, XEvent*, int>)NativeLibrary.GetExport(lib, "XSendEvent");
            var flush = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(lib, "XFlush");
            IntPtr hidden = SDL.CreateWindow("", 1, 1, SDL.WindowFlags.Hidden);
            if (hidden == IntPtr.Zero) return;
            bool sent = false;
            try
            {
                uint props = SDL.GetWindowProperties(hidden);
                IntPtr display = SDL.GetPointerProperty(props, SDL.Props.WindowX11DisplayPointer, IntPtr.Zero);
                nuint xwindow = (nuint)SDL.GetNumberProperty(props, SDL.Props.WindowX11WindowNumber, 0);
                int opcode, eventBase, errorBase;
                byte[] xfixes = Encoding.ASCII.GetBytes("XFIXES\0"), clipboardName = Encoding.ASCII.GetBytes("CLIPBOARD\0");
                fixed (byte* xf = xfixes)
                fixed (byte* cn = clipboardName)
                {
                    if (display != IntPtr.Zero && xwindow != 0 && queryExtension(display, xf, &opcode, &eventBase, &errorBase) != 0)
                    {
                        nuint clipboard = internAtom(display, cn, 0);
                        nuint owner = getSelectionOwner(display, clipboard);
                        if (owner != 0)
                        {
                            XEvent e = default;
                            var f = (XFixesNotify*)&e;
                            f->type = eventBase;   // + XFixesSelectionNotify (0)
                            f->display = display;
                            f->window = xwindow;
                            f->subtype = 0;        // XFixesSetSelectionOwnerNotify
                            f->owner = owner;
                            f->selection = clipboard;
                            sent = sendEvent(display, xwindow, 0, 0, &e) != 0;   // (no mask: to the client that made the window - SDL)
                            flush(display);
                        }
                    }
                }
            }
            finally { SDL.DestroyWindow(hidden); }
            for (int i = 0; sent && i < 100; i++)
            {
                SDL.PumpEvents();
                if (KindCount() > 0) break;
                SDL.Delay(5);
            }
        }
    }

    /// <summary>One lock for everything that touches SDL's events: the pumping of the windows (from the threads of the window managers) and the clipboard.</summary>
    public static class SdlGate
    {
        public static readonly object Lock = new();
    }
}
