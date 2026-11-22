using System.Globalization;
using System.Text;
using fire.Bytecode;
using fire.Runtime;

namespace fire.Native
{
    /// <summary>
    /// Stack traces of exceptions in the native build (docs/spec/STACKTRACE.md, section 13): a fire function that can throw keeps a frame on the C stack -
    /// `FireTFrame TF` (fire_rt.h: the caller's frame, its descriptor, its line) - which it pushes at its start and takes off at every `return`; the code
    /// stores the line where a line of the line table begins and at every jump target (the places of FIRE_DBG_L/FIRE_DBG_J in a debug build), and the
    /// descriptor where a function spans files (the top level of a program of several sources). The descriptors (`kFI_fN`: the name of the function as
    /// TraceNames gives it, the file as LinkedProgram.TraceFiles shows it) are static data of the module that has the function. A catch block makes the
    /// frame of its function the top while it runs (FIRE_TF_CATCH). A function that cannot throw - its body calls only functions without frames and nothing
    /// of the runtime (a C body of pure arithmetic: `M.Fib`) - has none. The setting `off` generates none of it; `calls` no lines.
    /// The C port does the same in native/forge/gen/trace.c.
    /// </summary>
    public sealed partial class CppGenerator
    {
        /// <summary>The setting of the program (`#stacktrace`, the project, `--stacktrace`, fire.native.json, the mode: LinkedProgram.StackTrace).</summary>
        private StackTraceMode TraceMode => _program.StackTrace;

        /// <summary>The functions get frames.</summary>
        private bool TraceOn => TraceMode != StackTraceMode.Off;

        /// <summary>... and store their lines.</summary>
        private bool TraceLines => TraceMode == StackTraceMode.Lines;

        /// <summary>What the last round found for each function: it has no frame (it cannot throw). A change bumps the version (another round).</summary>
        private readonly Dictionary<string, bool> _traceFrameless = new(StringComparer.Ordinal);
        /// <summary>The C names of the functions (`fN`, `fN_c`) -> the function's name.</summary>
        private Dictionary<string, string>? _traceCallees;
        private int _traceCalleesCount;
        private Dictionary<Chunk, string>? _traceNames;

        /// <summary>The name of a function in a trace (TraceNames; a fire body that is no member `&lt;fire&gt;`).</summary>
        private string TraceNameOf(Func f)
        {
            if (f.Kind == FuncKind.Main) return TraceNames.Main;
            _traceNames ??= TraceNames.Build(_program.Program.TopLevel, _program.Program.Classes.Values);
            if (_traceNames.TryGetValue(f.Chunk, out var name)) return name;
            return f.Kind == FuncKind.FireBody ? TraceNames.Fire : TraceNames.Lambda;
        }

        /// <summary>The file of a source as a trace shows it (null: the prelude).</summary>
        private string? TraceFileOf(int src) => _program.TraceFiles is { } files && src >= 0 && src < files.Count ? files[src] : null;

        /// <summary>The descriptors of a function: one per file its instructions are in, in the order of the code.</summary>
        private List<string?> TraceFilesOf(Chunk chunk, List<Instr> code)
        {
            var files = new List<string?>();
            foreach (var ins in code)
            {
                string? file = TraceFileOf(chunk.GetLocation(ins.Addr).SourceIndex);
                if (!files.Contains(file)) files.Add(file);
            }
            if (files.Count == 0) files.Add(null);
            return files;
        }

        /// <summary>Before instruction <paramref name="i"/> (pass 2): where a line begins or a jump lands, the line (and the file, if the function has several).</summary>
        private void TraceBeforeInstruction(Func f, Chunk chunk, Instr ins, int i, Out o, bool isTarget, List<string?> files)
        {
            var (src, line) = chunk.GetLocationRange(ins.Addr, out int start, out _);
            if (!(i == 0 || start == ins.Addr || isTarget)) return;
            string? file = TraceFileOf(src);
            var text = new StringBuilder();
            if (files.Count > 1) text.Append($"TF.fn = &kFI_{f.Name}[{files.IndexOf(file)}]; ");
            if (TraceLines && file != null) text.Append($"TF.line = {line}; ");
            if (text.Length > 0) o.Code.Append("    ").AppendLine(text.ToString().TrimEnd());
        }

        /// <summary>The descriptors of a function (before it).</summary>
        private string TraceTable(Func f, List<string?> files)
        {
            string name = CString(TraceNameOf(f));
            return $"static const FireFnInfo kFI_{f.Name}[] = {{{string.Join(", ", files.Select(file => $"{{{name}, {(file == null ? "NULL" : CString(file))}}}"))}}};\n";
        }

        /// <summary>The lines that store a position (TraceBeforeInstruction) out of the code of a function without a frame.</summary>
        private static string TraceStripLines(string code)
        {
            if (!code.Contains("    TF.", StringComparison.Ordinal)) return code;
            var sb = new StringBuilder(code.Length);
            foreach (var line in code.Split('\n'))
            {
                if (line.StartsWith("    TF.", StringComparison.Ordinal)) continue;
                sb.Append(line).Append('\n');
            }
            sb.Length--;   // (the last piece had no line feed)
            return sb.ToString();
        }

        /// <summary>The functions the code calls that are no function of this program and nothing that cannot throw: a function without them and with
        /// callees without frames cannot throw (its frame would never be read).</summary>
        private static readonly HashSet<string> TracePureCalls = new(StringComparer.Ordinal)
        {
            "if", "while", "for", "switch", "return", "sizeof", "Int", "Float", "Bool", "Undef", "FIRE_LIKELY", "FIRE_UNLIKELY",
        };

        /// <summary>The function cannot throw: what its code calls is in <see cref="TracePureCalls"/>, itself, or functions without frames.</summary>
        private bool TraceCannotThrow(Func f, string code)
        {
            if (_traceCallees == null || _traceCalleesCount != _funcs.Count)
            {
                _traceCallees = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var g in _funcs) { _traceCallees.TryAdd(g.Name, g.Name); _traceCallees.TryAdd(g.Name + "_c", g.Name); }
                _traceCalleesCount = _funcs.Count;
            }
            int i = 0, n = code.Length;
            while (i < n)
            {
                char c = code[i];
                if (c == '"' || c == '\'')
                {
                    for (i++; i < n && code[i] != c; i++) if (code[i] == '\\') i++;
                    i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && code[i + 1] == '/') { while (i < n && code[i] != '\n') i++; continue; }
                if (c == '#' && (i == 0 || code[i - 1] == '\n')) { while (i < n && code[i] != '\n') i++; continue; }
                if (!(char.IsAsciiLetter(c) || c == '_')) { i++; continue; }
                int s = i;
                while (i < n && (char.IsAsciiLetterOrDigit(code[i]) || code[i] == '_')) i++;
                int j = i;
                while (j < n && code[j] == ' ') j++;
                if (j >= n || code[j] != '(') continue;
                string id = code.Substring(s, i - s);
                if (TracePureCalls.Contains(id)) continue;
                if (!_traceCallees.TryGetValue(id, out var callee)) return false;
                if (callee == f.Name) continue;
                if (!_traceFrameless.TryGetValue(callee, out bool frameless) || !frameless) return false;
            }
            return true;
        }

        /// <summary>Notes whether the function has a frame; a change is another round.</summary>
        private void TraceNoteFrameless(Func f, bool frameless)
        {
            bool before = _traceFrameless.TryGetValue(f.Name, out bool known) && known;
            _traceFrameless[f.Name] = frameless;
            if (before != frameless) _version++;
        }

        private static readonly System.Text.RegularExpressions.Regex TraceSimpleReturn = new(
            @"^\s*(?:[A-Za-z_][A-Za-z0-9_]*|Undef\(\)|-?[0-9][0-9A-Za-z.]*|\([A-Za-z_][A-Za-z0-9_ ]*\)\s*-?[0-9][0-9A-Za-z.]*)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Every `return x;` of <paramref name="code"/> takes the frame off first: `{ leave; return x; }` - a value that is no variable or constant
        /// is computed before (`{ type r_tf = x; leave; return r_tf; }`: a call in it still has the frame below it).</summary>
        private static string TraceReturns(string code, string leave, string? retType)
        {
            if (!code.Contains("return", StringComparison.Ordinal)) return code;
            var sb = new StringBuilder(code.Length + 256);
            int i = 0, n = code.Length, copied = 0;
            while (i < n)
            {
                char c = code[i];
                if (c == '"' || c == '\'')
                {
                    for (i++; i < n && code[i] != c; i++) if (code[i] == '\\') i++;
                    i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && code[i + 1] == '/') { while (i < n && code[i] != '\n') i++; continue; }
                if (c == '#' && (i == 0 || code[i - 1] == '\n')) { while (i < n && code[i] != '\n') i++; continue; }
                if (!(char.IsAsciiLetter(c) || c == '_')) { i++; continue; }
                int s = i;
                while (i < n && (char.IsAsciiLetterOrDigit(code[i]) || code[i] == '_')) i++;
                if (i - s != 6 || string.CompareOrdinal(code, s, "return", 0, 6) != 0) continue;
                // the end of the statement: the `;` outside of brackets and literals
                int j = i, depth = 0;
                while (j < n)
                {
                    char d = code[j];
                    if (d == '"' || d == '\'')
                    {
                        for (j++; j < n && code[j] != d; j++) if (code[j] == '\\') j++;
                        j++;
                        continue;
                    }
                    if (d == '(' || d == '[' || d == '{') depth++;
                    else if (d == ')' || d == ']' || d == '}') depth--;
                    else if (d == ';' && depth <= 0) break;
                    j++;
                }
                if (j >= n) break;
                string expr = code.Substring(i, j - i);
                sb.Append(code, copied, s - copied);
                if (expr.Trim().Length == 0) sb.Append($"{{ {leave}; return; }}");
                else if (TraceSimpleReturn.IsMatch(expr) || retType == null) sb.Append($"{{ {leave}; return{expr}; }}");
                else sb.Append($"{{ {retType} r_tf ={expr}; {leave}; return r_tf; }}");
                copied = j + 1;
                i = j + 1;
            }
            sb.Append(code, copied, n - copied);
            return sb.ToString();
        }

        /// <summary>`fire`: where it stands, as the line of the thread names it (`main.fire:40`; `prelude`; without the line with `calls`).</summary>
        private string TraceStartAt(Chunk chunk, Instr ins)
        {
            if (!TraceOn) return "";
            var (src, line) = chunk.GetLocation(ins.Addr);
            string? file = TraceFileOf(src);
            string where = file == null ? "prelude" : TraceLines && line > 0 ? file + ":" + line.ToString(CultureInfo.InvariantCulture) : file;
            return $"g_traceStartAt = {CString(where)}; ";
        }
    }
}
