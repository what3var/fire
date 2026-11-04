using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace fire.Debugging
{
    /// <summary>
    /// The wire of the native debug protocol (docs/NATIVE_DEBUG_PROTOCOL.md "Transport"): messages (one JSON object each) in both directions. TCP carries
    /// only messages, one per line (<see cref="TcpDebugTransport"/>); a framed transport (<see cref="FramedDebugTransport"/>: stdio of a process, the
    /// console UART of a board) carries the program's console as well, the messages are frames in it.
    /// </summary>
    public interface INativeDebugTransport : IDisposable
    {
        /// <summary>The next message (blocks); null: the connection ended.</summary>
        string? ReadMessage();

        /// <summary>Sends a message (one JSON object without a line break). Throws IOException when the connection is gone.</summary>
        void WriteMessage(string json);

        /// <summary>The program's console comes over this transport too (and its input goes back over it): a framed transport.</summary>
        bool CarriesConsole { get; }

        /// <summary>Where the lines of the program's console go (a framed transport; null: they are kept until there is a receiver).</summary>
        Action<string>? Console { get; set; }

        /// <summary>A line of input for the program (its standard input; a framed transport: it goes over the wire as it is). false: not possible here.</summary>
        bool WriteInput(string line);
    }

    /// <summary>TCP: one JSON message per line (the desktop, FIRE_DEBUG=tcp:...). The program's console is the output of its process.</summary>
    public sealed class TcpDebugTransport : INativeDebugTransport
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly object _writeLock = new();

        public TcpDebugTransport(TcpClient client, StreamReader? reader = null)
        {
            _client = client;
            _reader = reader ?? new StreamReader(client.GetStream(), new UTF8Encoding(false));
            _writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        }

        public string? ReadMessage()
        {
            while (true)
            {
                string? line = _reader.ReadLine();
                if (line == null || line.Length > 0) return line;
            }
        }

        public void WriteMessage(string json)
        {
            try { lock (_writeLock) _writer.WriteLine(json); }
            catch (SocketException ex) { throw new IOException(ex.Message, ex); }
        }

        public bool CarriesConsole => false;
        public Action<string>? Console { get; set; }
        public bool WriteInput(string line) => false;

        public void Dispose()
        {
            try { _client.Dispose(); } catch (SocketException) { }
        }
    }

    /// <summary>
    /// A framed transport: one byte stream carries the program's console (its output; the boot log of a board) and the messages, each a frame of its own line:
    /// <c>\x1E FD: json * crc16 \n</c> (CRC-16/CCITT-FALSE of the JSON as 4 hex digits; the program writes pure ASCII). Frames go to <see cref="ReadMessage"/>,
    /// everything else to <see cref="Console"/> line by line (a frame in the middle of a line of the console is cut out of it); a frame whose CRC does not
    /// match (garbled on the wire) is dropped. The other direction: frames, and plain lines that are the program's standard input.
    /// </summary>
    public class FramedDebugTransport : INativeDebugTransport
    {
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly object _writeLock = new();
        private readonly object _consoleLock = new();
        private readonly byte[] _chunk = new byte[4096];
        private int _pos, _len;
        private readonly List<byte> _console = new();
        private readonly List<byte> _frame = new();
        private bool _inFrame;
        private readonly List<string> _pendingConsole = new();
        private Action<string>? _consoleSink;
        private bool _ended;

        public FramedDebugTransport(Stream input, Stream output)
        {
            _input = input;
            _output = output;
        }

        /// <summary>Frames dropped because their CRC or their form was wrong.</summary>
        public int DroppedFrames { get; private set; }

        public bool CarriesConsole => true;

        public Action<string>? Console
        {
            get { lock (_consoleLock) return _consoleSink; }
            set
            {
                List<string> pending;
                lock (_consoleLock)
                {
                    _consoleSink = value;
                    if (value == null) return;
                    pending = new List<string>(_pendingConsole);
                    _pendingConsole.Clear();
                }
                foreach (var line in pending) value(line);
            }
        }

        private void EmitConsole(bool force)
        {
            if (_console.Count == 0 && !force) return;
            int n = _console.Count;
            if (n > 0 && _console[n - 1] == '\r') n--;
            string line = Encoding.UTF8.GetString(_console.GetRange(0, n).ToArray());
            _console.Clear();
            Action<string>? sink;
            lock (_consoleLock)
            {
                sink = _consoleSink;
                if (sink == null) { _pendingConsole.Add(line); return; }
            }
            sink(line);
        }

        /// <summary>The next message; the console that comes before it goes to <see cref="Console"/>. null: the stream ended.</summary>
        public string? ReadMessage()
        {
            while (true)
            {
                if (_pos == _len)
                {
                    if (_ended) return null;
                    int n;
                    try { n = _input.Read(_chunk, 0, _chunk.Length); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { n = 0; }
                    if (n <= 0)
                    {
                        _ended = true;
                        if (_console.Count > 0) EmitConsole(false);   // (the last line without a line break)
                        return null;
                    }
                    _pos = 0;
                    _len = n;
                }
                byte b = _chunk[_pos++];
                if (_inFrame)
                {
                    if (b == (byte)'\n')
                    {
                        _inFrame = false;
                        if (Unframe(_frame) is { } message) { _frame.Clear(); return message; }
                        _frame.Clear();
                    }
                    else if (b != (byte)'\r') _frame.Add(b);
                    continue;
                }
                if (b == 0x1E) { _inFrame = true; _frame.Clear(); continue; }
                if (b == (byte)'\n') EmitConsole(true);
                else _console.Add(b);
            }
        }

        /// <summary>The JSON of a frame (what follows \x1E up to the line break), null when it is no good frame.</summary>
        private string? Unframe(List<byte> frame)
        {
            int star = frame.LastIndexOf((byte)'*');
            if (frame.Count < 9 || frame[0] != 'F' || frame[1] != 'D' || frame[2] != ':' || star < 3 || frame.Count - star - 1 != 4) { DroppedFrames++; return null; }
            var json = frame.GetRange(3, star - 3).ToArray();
            string crcText = Encoding.ASCII.GetString(frame.GetRange(star + 1, 4).ToArray());
            if (!int.TryParse(crcText, System.Globalization.NumberStyles.HexNumber, null, out int crc) || crc != Crc16(json)) { DroppedFrames++; return null; }
            return Encoding.UTF8.GetString(json);
        }

        /// <summary>CRC-16/CCITT-FALSE: polynomial 0x1021, start 0xFFFF, no reflection, no final xor (the agent's dbgCrc16).</summary>
        public static int Crc16(ReadOnlySpan<byte> data)
        {
            int crc = 0xFFFF;
            foreach (byte x in data)
            {
                crc ^= x << 8;
                for (int k = 0; k < 8; k++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
            return crc;
        }

        /// <summary>A message as a frame (bytes on the wire). The JSON is made pure ASCII first (\u escapes) - the CRC counts bytes.</summary>
        public static byte[] Frame(string json)
        {
            var ascii = new StringBuilder(json.Length);
            foreach (char c in json) if (c > 0x7E) ascii.Append("\\u").Append(((int)c).ToString("x4")); else ascii.Append(c);
            byte[] body = Encoding.ASCII.GetBytes(ascii.ToString());
            byte[] tail = Encoding.ASCII.GetBytes($"*{Crc16(body):x4}\n");
            var frame = new byte[4 + body.Length + tail.Length];
            frame[0] = 0x1E; frame[1] = (byte)'F'; frame[2] = (byte)'D'; frame[3] = (byte)':';
            body.CopyTo(frame, 4);
            tail.CopyTo(frame, 4 + body.Length);
            return frame;
        }

        public void WriteMessage(string json) => Write(Frame(json));

        /// <summary>A line of standard input for the program (a line break is added; a \x1E at its start would make it a frame - it is dropped).</summary>
        public bool WriteInput(string line)
        {
            try { Write(Encoding.UTF8.GetBytes(line.TrimStart('\x1E').Replace("\r", "").Replace("\n", " ") + "\n")); return true; }
            catch (IOException) { return false; }
        }

        private void Write(byte[] bytes)
        {
            try
            {
                lock (_writeLock)
                {
                    _output.Write(bytes, 0, bytes.Length);
                    _output.Flush();
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or TimeoutException) { throw new IOException(ex.Message, ex); }
        }

        public virtual void Dispose()
        {
            try { _input.Dispose(); } catch (IOException) { }
            if (!ReferenceEquals(_input, _output)) try { _output.Dispose(); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The console UART of a board (ESP32: UART0 behind its USB-serial chip) as a framed transport, through System.IO.Ports. <see cref="Open"/> opens the
    /// port without touching DTR/RTS and can reset the board the way `idf.py monitor` does (RTS pulls EN low with DTR high: a normal boot, not the
    /// bootloader), so that the program starts again while spark listens. The constructor over a stream is for tests (a fake device, QEMU's serial socket).
    /// </summary>
    public sealed class SerialFramedTransport : FramedDebugTransport
    {
        private readonly SerialPort? _port;

        public SerialFramedTransport(Stream stream) : base(stream, stream) { }

        private SerialFramedTransport(SerialPort port) : base(port.BaseStream, port.BaseStream) { _port = port; }

        /// <summary>The port it reads (null: a stream).</summary>
        public string? PortName => _port?.PortName;

        /// <summary>Opens <paramref name="portName"/> (8N1, no handshake); <paramref name="reset"/>: restart the board (RTS) after opening.</summary>
        public static SerialFramedTransport Open(string portName, int baud, bool reset)
        {
            var port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One) { Handshake = Handshake.None, DtrEnable = false, RtsEnable = false, ReadTimeout = SerialPort.InfiniteTimeout, WriteTimeout = 5000 };
            port.Open();
            if (reset)
            {
                // EN low (RTS), IO0 high (DTR off): the chip restarts and boots the program
                port.DtrEnable = false;
                port.RtsEnable = true;
                Thread.Sleep(100);
                port.RtsEnable = false;
            }
            return new SerialFramedTransport(port);
        }

        public override void Dispose()
        {
            if (_port != null)
            {
                try { _port.Close(); } catch (IOException) { }
                _port.Dispose();
                return;
            }
            base.Dispose();
        }

        /// <summary>The USB-serial ports of this machine that a board can be on (Linux: /dev/serial/by-id, /dev/ttyUSB*, /dev/ttyACM*; macOS: /dev/cu.usb*,
        /// /dev/cu.SLAB*, /dev/cu.wchusb*; Windows: the COM ports).</summary>
        public static IReadOnlyList<string> CandidatePorts()
        {
            var ports = new List<string>();
            try
            {
                if (OperatingSystem.IsWindows()) ports.AddRange(SerialPort.GetPortNames());
                else if (OperatingSystem.IsMacOS())
                {
                    foreach (var f in Directory.GetFiles("/dev", "cu.*"))
                        if (Regex.IsMatch(Path.GetFileName(f), @"^cu\.(usbserial|usbmodem|SLAB_USBtoUART|wchusbserial)")) ports.Add(f);
                }
                else
                {
                    foreach (var f in Directory.GetFiles("/dev"))
                        if (Regex.IsMatch(Path.GetFileName(f), @"^tty(USB|ACM)\d+$")) ports.Add(f);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            ports.Sort(StringComparer.Ordinal);
            return ports;
        }

        /// <summary>The port esptool used, from the output of `idf.py flash` ("Serial port /dev/ttyUSB0"), null when it does not say.</summary>
        public static string? PortFromFlashLog(string log)
        {
            string? found = null;
            foreach (Match m in Regex.Matches(log, @"Serial port (\S+)")) found = m.Groups[1].Value.TrimEnd(':', '.');
            return found;
        }
    }
}
