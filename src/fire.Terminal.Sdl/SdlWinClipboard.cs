using System.Runtime.InteropServices;
using System.Text;

namespace fire.Terminal.Sdl
{
    /// <summary>The DROPFILES block of CF_HDROP (files on the Windows clipboard, in a drop): a header of 20 bytes - pFiles (where the paths begin), pt, fNC, fWide -
    /// then the paths, each ending in 0, the list ending in another 0 (UTF-16 when fWide, else the ANSI code page). Platform-independent, so that the tests can check
    /// it anywhere; the clipboard itself is <see cref="WinClipboard"/>. The same layout as fire_display_winGetFiles/SetFiles of native/platform/std/fire_display_sdl3.h.</summary>
    public static class DropFiles
    {
        public const int HeaderSize = 20;

        /// <summary>The block for these paths (UTF-16, fWide set).</summary>
        public static byte[] Encode(IReadOnlyList<string> paths)
        {
            var sb = new StringBuilder();
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || path.Contains('\0')) continue;
                sb.Append(path).Append('\0');
            }
            sb.Append('\0');
            if (sb.Length == 1) sb.Append('\0');   // (an empty list still ends in two zeros)
            byte[] chars = Encoding.Unicode.GetBytes(sb.ToString());
            var block = new byte[HeaderSize + chars.Length];
            BitConverter.TryWriteBytes(block.AsSpan(0, 4), (uint)HeaderSize);   // pFiles
            BitConverter.TryWriteBytes(block.AsSpan(16, 4), 1);                 // fWide
            chars.CopyTo(block, HeaderSize);
            return block;
        }

        /// <summary>The paths of a block (an empty array when it is not one). `ansi` decodes the bytes of a path of an old block without fWide (default: the ANSI
        /// code page of the system - on Windows; elsewhere UTF-8).</summary>
        public static string[] Decode(ReadOnlySpan<byte> block, Func<byte[], string>? ansi = null)
        {
            if (block.Length < HeaderSize) return Array.Empty<string>();
            uint offset = BitConverter.ToUInt32(block.Slice(0, 4));
            bool wide = BitConverter.ToInt32(block.Slice(16, 4)) != 0;
            if (offset < HeaderSize || offset >= (uint)block.Length) return Array.Empty<string>();
            var paths = new List<string>();
            var rest = block.Slice((int)offset);
            if (wide)
            {
                int i = 0;
                while (i + 1 < rest.Length)
                {
                    int start = i;
                    while (i + 1 < rest.Length && (rest[i] != 0 || rest[i + 1] != 0)) i += 2;
                    if (i == start) break;   // (the second zero: the end of the list)
                    paths.Add(Encoding.Unicode.GetString(rest.Slice(start, i - start)));
                    i += 2;
                }
            }
            else
            {
                ansi ??= AnsiString;
                int i = 0;
                while (i < rest.Length)
                {
                    int start = i;
                    while (i < rest.Length && rest[i] != 0) i++;
                    if (i == start) break;
                    paths.Add(ansi(rest.Slice(start, i - start).ToArray()));
                    i++;
                }
            }
            return paths.ToArray();
        }

        private static unsafe string AnsiString(byte[] bytes)
        {
            if (!OperatingSystem.IsWindows()) return Encoding.UTF8.GetString(bytes);
            fixed (byte* p = bytes) return Marshal.PtrToStringAnsi((IntPtr)p, bytes.Length);
        }
    }

    /// <summary>Files on the Windows clipboard as CF_HDROP (SDL3 has only text and CF_DIB there): read with GetClipboardData, written with SetClipboardData. The owner
    /// that SetClipboardData needs is a message-only window on a thread of its own that takes its messages (the clipboard sends the owner WM_DESTROYCLIPBOARD when
    /// another program empties it). Only called on Windows, with SDL's video driver "windows", under <see cref="SdlGate.Lock"/>.</summary>
    internal static class WinClipboard
    {
        private const uint CF_HDROP = 15, GMEM_MOVEABLE = 0x0002, GMEM_ZEROINIT = 0x0040;
        private static readonly IntPtr HWND_MESSAGE = new(-3);

        [DllImport("user32.dll")] private static extern int OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern int CloseClipboard();
        [DllImport("user32.dll")] private static extern int EmptyClipboard();
        [DllImport("user32.dll")] private static extern int IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern int TranslateMessage(ref Msg msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg msg);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] private static extern int GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr mem);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg { public IntPtr hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x, y; public uint priv; }

        private static IntPtr _owner;
        private static bool _ownerTried;

        /// <summary>The owner window (made once, on its own thread, which then takes its messages for good); zero when it could not be made.</summary>
        private static IntPtr Owner()
        {
            if (_ownerTried) return _owner;
            _ownerTried = true;
            using var made = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                _owner = CreateWindowExW(0, "STATIC", "fire clipboard", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                made.Set();
                if (_owner == IntPtr.Zero) return;
                while (GetMessageW(out Msg msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
            }) { IsBackground = true, Name = "fire clipboard" };
            thread.Start();
            made.Wait(2000);
            return _owner;
        }

        private static bool Open(IntPtr owner)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (OpenClipboard(owner) != 0) return true;
                Thread.Sleep(10);   // (another program has it open just now)
            }
            return false;
        }

        public static bool HasFiles => IsClipboardFormatAvailable(CF_HDROP) != 0;

        /// <summary>The paths of CF_HDROP; null when there is none.</summary>
        public static unsafe string[]? GetFiles()
        {
            if (!HasFiles || !Open(IntPtr.Zero)) return null;
            try
            {
                IntPtr mem = GetClipboardData(CF_HDROP);
                if (mem == IntPtr.Zero) return null;
                IntPtr p = GlobalLock(mem);
                if (p == IntPtr.Zero) return null;
                try { return DropFiles.Decode(new ReadOnlySpan<byte>((void*)p, checked((int)GlobalSize(mem)))); }
                finally { GlobalUnlock(mem); }
            }
            finally { CloseClipboard(); }
        }

        /// <summary>Puts the paths on the clipboard as CF_HDROP (and nothing else: what was there goes).</summary>
        public static bool SetFiles(IReadOnlyList<string> paths)
        {
            byte[] block = DropFiles.Encode(paths);
            IntPtr owner = Owner();
            if (owner == IntPtr.Zero) return false;
            IntPtr mem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (nuint)block.Length);
            if (mem == IntPtr.Zero) return false;
            IntPtr p = GlobalLock(mem);
            if (p == IntPtr.Zero) { GlobalFree(mem); return false; }
            Marshal.Copy(block, 0, p, block.Length);
            GlobalUnlock(mem);
            bool ok = false;
            if (Open(owner))
            {
                EmptyClipboard();
                ok = SetClipboardData(CF_HDROP, mem) != IntPtr.Zero;   // (from here on the memory belongs to the system)
                CloseClipboard();
            }
            if (!ok) GlobalFree(mem);
            return ok;
        }
    }
}
