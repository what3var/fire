using System.Text;

namespace fire.IO.Bridge
{
    /// <summary>
    /// Wohin `IO.Stdio` (Standardein-/-ausgabe/-fehler eines Skripts) führt -
    /// entscheidet der HOST: ein Konsolenprogramm die echte Konsole
    /// (<see cref="SystemConsole"/>, Vorgabe), der Editor z.B. sein Ausgabefenster
    /// (<see cref="Custom"/>). Jedes Öffnen liefert denselben Stream für die
    /// ganze Sitzung (siehe IoBridge.IoHost).
    /// </summary>
    public abstract class IoStdio
    {
        public abstract Stream OpenInput();
        public abstract Stream OpenOutput();
        public abstract Stream OpenError();

        /// <summary>Die echte Standardein-/-ausgabe/-fehler des Prozesses.</summary>
        public static IoStdio SystemConsole { get; } = new SystemConsoleStdio();

        /// <summary>Ausgabe und Fehler zeilenweise an Rückruffunktionen (ohne
        /// den Zeilenumbruch); eine unvollständige letzte Zeile bleibt bis zum
        /// nächsten Umbruch oder `IO.Stdio.Flush()` liegen. `error` ohne Angabe
        /// geht ebenfalls an `output`. `input`: ohne Angabe ein leerer Stream
        /// (sofort Ende).</summary>
        public static IoStdio Custom(Action<string> output, Action<string>? error = null, Stream? input = null) =>
            new CustomStdio(output, error ?? output, input ?? Stream.Null);

        private sealed class SystemConsoleStdio : IoStdio
        {
            public override Stream OpenInput() => Console.OpenStandardInput();
            public override Stream OpenOutput() => Console.OpenStandardOutput();
            public override Stream OpenError() => Console.OpenStandardError();
        }

        private sealed class CustomStdio : IoStdio
        {
            private readonly Action<string> _output;
            private readonly Action<string> _error;
            private readonly Stream _input;

            public CustomStdio(Action<string> output, Action<string> error, Stream input)
            {
                _output = output;
                _error = error;
                _input = input;
            }

            public override Stream OpenInput() => _input;
            public override Stream OpenOutput() => new LineCallbackStream(_output);
            public override Stream OpenError() => new LineCallbackStream(_error);
        }

        /// <summary>Ein nur schreibbarer Stream, der UTF-8-Bytes zu Text
        /// dekodiert (auch über Chunk-Grenzen hinweg mitten in einem
        /// Mehrbyte-Zeichen) und ihn zeilenweise weitergibt.</summary>
        private sealed class LineCallbackStream : Stream
        {
            private readonly Action<string> _callback;
            private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
            private readonly StringBuilder _line = new();

            public LineCallbackStream(Action<string> callback) => _callback = callback;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                var chars = new char[_decoder.GetCharCount(buffer, offset, count)];
                int n = _decoder.GetChars(buffer, offset, count, chars, 0);
                for (int i = 0; i < n; i++)
                {
                    char c = chars[i];
                    if (c == '\n')
                    {
                        if (_line.Length > 0 && _line[_line.Length - 1] == '\r') _line.Length--;
                        _callback(_line.ToString());
                        _line.Clear();
                    }
                    else
                    {
                        _line.Append(c);
                    }
                }
            }

            public override void Flush()
            {
                if (_line.Length == 0) return;
                _callback(_line.ToString());
                _line.Clear();
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
