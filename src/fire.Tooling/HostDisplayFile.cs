using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace fire.Compiler
{
    /// <summary>
    /// The host's side of the display of a program in another process (docs/ROADMAP_C_PORT.md, phase B step 9; the program's side: native/abi/fire_gfx_host.h,
    /// FIRE_DISPLAY_SHM): one file that both map - the program writes its pictures (a sequence number that is odd while it writes) and its mouse pointer into it, the
    /// host writes its events into a ring in it. No pipe and no lock between the processes: the host looks at the sequence number (<see cref="TryCopyFrame"/>).
    /// The layout is in fire_gfx_host.h; the file is sparse (a picture of 4K uses its pages only when it is drawn).
    /// </summary>
    public sealed class HostDisplayFile : IDisposable
    {
        public const int HeaderSize = 4096;
        public const int SlotSize = 128;
        public const string EnvironmentVariable = "FIRE_DISPLAY_SHM";

        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private readonly int _slots;
        private readonly long _capacity;
        private readonly long _frameOffset;
        private readonly object _writeLock = new();
        private long _eventWrite;
        private long _lastSeq;
        private bool _disposed;

        /// <summary>The file (FIRE_DISPLAY_SHM of the program).</summary>
        public string Path { get; }

        private HostDisplayFile(string path, int slots, long capacity)
        {
            Path = path;
            _slots = slots;
            _capacity = capacity;
            _frameOffset = HeaderSize + (long)slots * SlotSize;
            long total = _frameOffset + capacity;
            // (Windows: the program maps the same file - CreateFileMapping/MapViewOfFile, its views and these are coherent; the program opens it with FILE_SHARE_DELETE,
            // so the file goes when the last handle closes - also when spark ends without Dispose; File.Delete would fail while the program still maps it)
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | (OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None),
                4096, OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None);
            stream.SetLength(total);
            _file = MemoryMappedFile.CreateFromFile(stream, null, total, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            _view = _file.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);
            _view.WriteArray(0, Encoding.ASCII.GetBytes("FIRESHM1"), 0, 8);
            _view.Write(8, 1);
            _view.Write(12, HeaderSize);
            _view.Write(16, slots);
            _view.Write(20, SlotSize);
            _view.Write(24, capacity);
            _view.Flush();
        }

        /// <summary>A new file in <paramref name="folder"/> (default: /dev/shm when there is one, else the temporary folder) with a ring of <paramref name="slots"/> events and room
        /// for a picture of <paramref name="capacity"/> bytes (default: 3840 x 2160).</summary>
        public static HostDisplayFile Create(string? folder = null, int slots = 1024, long capacity = 3840L * 2160 * 4)
        {
            folder ??= OperatingSystem.IsLinux() && Directory.Exists("/dev/shm") ? "/dev/shm" : System.IO.Path.GetTempPath();
            string path = System.IO.Path.Combine(folder, $"fire-display-{Environment.ProcessId}-{Guid.NewGuid():N}.shm");
            return new HostDisplayFile(path, slots, capacity);
        }

        /// <summary>An event for the program's windows; false when the ring is full (the program does not take its events).</summary>
        public bool Post(in HostDisplayEvent e)
        {
            lock (_writeLock)
            {
                if (_disposed) return false;
                long read = _view.ReadInt64(40);
                if (_eventWrite - read >= _slots) return false;
                long o = HeaderSize + (_eventWrite % _slots) * SlotSize;
                _view.Write(o, e.Type); _view.Write(o + 4, e.Button); _view.Write(o + 8, e.Buttons); _view.Write(o + 12, e.KeyCode); _view.Write(o + 16, e.Modifiers);
                _view.Write(o + 20, e.Width); _view.Write(o + 24, e.Height); _view.Write(o + 28, e.Joystick); _view.Write(o + 32, e.Index);
                _view.Write(o + 36, e.X); _view.Write(o + 40, e.Y); _view.Write(o + 44, e.Dx); _view.Write(o + 48, e.Dy); _view.Write(o + 52, e.Value); _view.Write(o + 56, e.Pressure);
                _view.Write(o + 60, 0); _view.Write(o + 64, e.Finger);
                var text = new byte[32];
                if (e.Text is { Length: > 0 } t)
                {
                    int n = 0;
                    foreach (var rune in t.EnumerateRunes())
                    {
                        if (n + rune.Utf8SequenceLength > 31) break;
                        n += rune.EncodeToUtf8(text.AsSpan(n));
                    }
                }
                _view.WriteArray(o + 72, text, 0, 32);
                Thread.MemoryBarrier();
                _eventWrite++;
                _view.Write(32, _eventWrite);
                return true;
            }
        }

        /// <summary>The newest picture of the program into <paramref name="into"/> (grown when needed) when there is one the host has not taken yet and it is not being
        /// written right now; its sequence number, else null.</summary>
        public long? TryCopyFrame(ref uint[] into, out int width, out int height, out int window)
        {
            width = height = window = 0;
            if (_disposed) return null;
            long seq = _view.ReadInt64(48);
            if (seq == _lastSeq || (seq & 1) != 0) return null;
            Thread.MemoryBarrier();
            width = _view.ReadInt32(56);
            height = _view.ReadInt32(60);
            window = _view.ReadInt32(64);
            long n = (long)width * height;
            if (width <= 0 || height <= 0 || n * 4 > _capacity) return null;
            if (into.Length != n) into = new uint[n];
            _view.ReadArray(_frameOffset, into, 0, (int)n);
            Thread.MemoryBarrier();
            if (_view.ReadInt64(48) != seq) return null;   // (written meanwhile: the next look takes the new one)
            _lastSeq = seq;
            return seq;
        }

        /// <summary>The mouse pointer the program wants (UI.Cursor).</summary>
        public int Cursor => _disposed ? 0 : _view.ReadInt32(68);
        /// <summary>The process of the program, once it has mapped the file (0 before).</summary>
        public int ProgramPid => _disposed ? 0 : _view.ReadInt32(72);
        /// <summary>Pictures the program could not hand over (bigger than the room for one).</summary>
        public int DroppedFrames => _disposed ? 0 : _view.ReadInt32(76);
        /// <summary>Events the program has taken.</summary>
        public long EventsTaken => _disposed ? 0 : _view.ReadInt64(40);

        public void Dispose()
        {
            lock (_writeLock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _view.Dispose();
            _file.Dispose();
            try { File.Delete(Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
