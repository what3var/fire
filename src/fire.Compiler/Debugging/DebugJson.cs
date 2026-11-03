using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace fire.Debugging
{
    /// <summary>
    /// The JSON of the native debug protocol (docs/NATIVE_DEBUG_PROTOCOL.md): one message per line. A small reader and writer of its own: the program writes
    /// fire strings as UTF-16 units in `\uXXXX`, also single surrogates, which System.Text.Json refuses - here they become the string as it is.
    /// Values: <see cref="Dictionary{TKey,TValue}"/> (object), <see cref="List{T}"/> (array), string, long or double (number), bool, null.
    /// </summary>
    public static class DebugJson
    {
        public static object? Parse(string text)
        {
            int i = 0;
            var value = ReadValue(text, ref i);
            SkipBlanks(text, ref i);
            if (i != text.Length) throw new FormatException($"unexpected '{text[i]}' at {i}");
            return value;
        }

        public static Dictionary<string, object?> ParseObject(string text) =>
            Parse(text) as Dictionary<string, object?> ?? throw new FormatException("not a JSON object");

        private static void SkipBlanks(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static object? ReadValue(string s, ref int i)
        {
            SkipBlanks(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            switch (c)
            {
                case '{':
                {
                    i++;
                    var obj = new Dictionary<string, object?>();
                    SkipBlanks(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; return obj; }
                    while (true)
                    {
                        SkipBlanks(s, ref i);
                        string key = ReadString(s, ref i);
                        SkipBlanks(s, ref i);
                        Expect(s, ref i, ':');
                        obj[key] = ReadValue(s, ref i);
                        SkipBlanks(s, ref i);
                        if (i < s.Length && s[i] == ',') { i++; continue; }
                        Expect(s, ref i, '}');
                        return obj;
                    }
                }
                case '[':
                {
                    i++;
                    var list = new List<object?>();
                    SkipBlanks(s, ref i);
                    if (i < s.Length && s[i] == ']') { i++; return list; }
                    while (true)
                    {
                        list.Add(ReadValue(s, ref i));
                        SkipBlanks(s, ref i);
                        if (i < s.Length && s[i] == ',') { i++; continue; }
                        Expect(s, ref i, ']');
                        return list;
                    }
                }
                case '"': return ReadString(s, ref i);
                case 't': ExpectWord(s, ref i, "true"); return true;
                case 'f': ExpectWord(s, ref i, "false"); return false;
                case 'n': ExpectWord(s, ref i, "null"); return null;
                default:
                {
                    int start = i;
                    if (s[i] == '-') i++;
                    bool isFloat = false;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '.' or 'e' or 'E' or '+' or '-'))
                    {
                        if (s[i] is '.' or 'e' or 'E') isFloat = true;
                        i++;
                    }
                    string number = s[start..i];
                    if (number.Length == 0 || number == "-") throw new FormatException($"unexpected '{c}' at {start}");
                    if (!isFloat && long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
                    return double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture);
                }
            }
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException($"expected '{c}' at {i}");
            i++;
        }

        private static void ExpectWord(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException($"expected '{word}' at {i}");
            i += word.Length;
        }

        private static string ReadString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("unterminated escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("short \\u escape");
                        sb.Append((char)int.Parse(s.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException($"unknown escape \\{e}");
                }
            }
        }

        /// <summary>One line of JSON (no line break inside): everything outside ASCII as `\uXXXX`.</summary>
        public static string Write(object? value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object? value)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string str: WriteString(sb, str); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case long n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object?> obj:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var (k, v) in obj)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, k);
                        sb.Append(':');
                        WriteValue(sb, v);
                    }
                    sb.Append('}');
                    break;
                }
                case System.Collections.IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var v in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteValue(sb, v);
                    }
                    sb.Append(']');
                    break;
                }
                default: throw new ArgumentException($"cannot write {value.GetType().Name} as JSON");
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------------------------------------------------------
        // Reading fields
        // ---------------------------------------------------------------

        public static string? Str(this Dictionary<string, object?> o, string key) => o.TryGetValue(key, out var v) ? v as string : null;
        public static long? Num(this Dictionary<string, object?> o, string key) => o.TryGetValue(key, out var v) ? v switch { long l => l, double d => (long)d, _ => null } : null;
        public static bool Flag(this Dictionary<string, object?> o, string key) => o.TryGetValue(key, out var v) && v is true;
        public static List<object?> Arr(this Dictionary<string, object?> o, string key) => o.TryGetValue(key, out var v) && v is List<object?> l ? l : new List<object?>();
        public static Dictionary<string, object?>? Obj(this Dictionary<string, object?> o, string key) => o.TryGetValue(key, out var v) ? v as Dictionary<string, object?> : null;
    }
}
