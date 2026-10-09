using System.Text;

namespace fire.IO.Bridge
{
    /// <summary>
    /// Where `IO.Stdio` (standard input/output/error of a script) leads -
    /// decided by the HOST: a console program the real console
    /// (<see cref="SystemConsole"/>, default), the editor e.g. its output window
    /// (<see cref="Custom"/>). Every open returns the same stream for the
    /// whole session (see IoBridge.IoHost).
    /// </summary>
    public abstract class IoStdio
    {
        public abstract Stream OpenInput();
        public abstract Stream OpenOutput();
        public abstract Stream OpenError();

        /// <summary>The real standard input/output/error of the process.</summary>
        public static IoStdio SystemConsole { get; } = new SystemConsoleStdio();

        /// <summary>Output and error line by line to callback functions (without
        /// the line break); an incomplete last line stays until the
        /// next break or `IO.Stdio.Flush()`. `error` without a value
        /// likewise goes to `output`. `input`: without a value an empty stream
        /// (ends immediately).</summary>
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

        /// <summary>A write-only stream that decodes UTF-8 bytes to text
        /// (also across chunk boundaries in the middle of a
        /// multi-byte character) and passes it on line by line.</summary>
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
