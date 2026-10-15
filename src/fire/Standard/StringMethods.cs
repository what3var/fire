using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using fire.Bytecode;
using fire.Values;

namespace fire.Standard
{
    /// <summary>The methods of `string` (SPEC 8.12). The numbers are the IDs through which the prelude
    /// addresses the native function <see cref="StringMethods.NativeName"/> - deliberately fixed values
    /// (never renumber) and NO names: dispatching via string comparisons would be too slow in the
    /// VM.</summary>
    public enum StringMethod
    {
        IndexOf = 1,
        LastIndexOf = 2,
        Substring = 3,
        CharAt = 4,
        Contains = 5,
        StartsWith = 6,
        EndsWith = 7,
        ToUpper = 8,
        ToLower = 9,
        Trim = 10,
        TrimStart = 11,
        TrimEnd = 12,
        Replace = 13,
        Split = 14,
        PadLeft = 15,
        PadRight = 16,
        ToInt = 17,
        ToFloat = 18,
        TryToInt = 19,
        TryToFloat = 20,
        CompareTo = 21,
        Join = 22,
    }

    /// <summary>
    /// `string` methods as an extension of the base type (`class extends string`, SPEC 5.5.1) in the prelude,
    /// all via ONE native function: `__StringCall(id, text, arguments...)`. All comparisons are
    /// ORDINAL (character by character, without culture), indices count `char`s (16-bit code units).
    /// A value that may be a string OR a character (`IndexOf("ab")`, `IndexOf('a')`),
    /// is read via <see cref="Text"/>. An invalid index throws
    /// <see cref="NativeIndexOutOfRangeException"/> (the VM turns this into a catchable
    /// `IndexOutOfBoundsException`).
    /// </summary>
    public static class StringMethods
    {
        public const string NativeName = "__StringCall";

        /// <summary>Prelude signatures: (Id, return type, name, parameters). One line per overload -
        /// the prelude passes the parameters through unchanged to the native function, whose argument count
        /// then determines the overload.</summary>
        private static readonly (StringMethod Id, string Returns, string Name, string Params)[] Signatures =
        {
            (StringMethod.IndexOf, "int", "IndexOf", "any value"),
            (StringMethod.IndexOf, "int", "IndexOf", "any value, int start"),
            (StringMethod.LastIndexOf, "int", "LastIndexOf", "any value"),
            (StringMethod.LastIndexOf, "int", "LastIndexOf", "any value, int start"),
            (StringMethod.Substring, "string", "Substring", "int start"),
            (StringMethod.Substring, "string", "Substring", "int start, int count"),
            (StringMethod.CharAt, "char", "CharAt", "int index"),
            (StringMethod.Contains, "bool", "Contains", "any value"),
            (StringMethod.StartsWith, "bool", "StartsWith", "any value"),
            (StringMethod.EndsWith, "bool", "EndsWith", "any value"),
            (StringMethod.ToUpper, "string", "ToUpper", ""),
            (StringMethod.ToLower, "string", "ToLower", ""),
            (StringMethod.Trim, "string", "Trim", ""),
            (StringMethod.TrimStart, "string", "TrimStart", ""),
            (StringMethod.TrimEnd, "string", "TrimEnd", ""),
            (StringMethod.Replace, "string", "Replace", "any oldValue, any newValue"),
            (StringMethod.Split, "string[]", "Split", "any separator"),
            (StringMethod.PadLeft, "string", "PadLeft", "int width"),
            (StringMethod.PadLeft, "string", "PadLeft", "int width, any fill"),
            (StringMethod.PadRight, "string", "PadRight", "int width"),
            (StringMethod.PadRight, "string", "PadRight", "int width, any fill"),
            (StringMethod.ToInt, "int", "ToInt", ""),
            (StringMethod.ToFloat, "float", "ToFloat", ""),
            (StringMethod.TryToInt, "any", "TryToInt", ""),
            (StringMethod.TryToFloat, "any", "TryToFloat", ""),
            (StringMethod.CompareTo, "int", "CompareTo", "any other"),
        };

        /// <summary>The fire source `class extends string { ... }` for the prelude, generated from
        /// <see cref="Signatures"/> - so the IDs appear in only ONE place.</summary>
        public static string PreludeSource { get; } = BuildPreludeSource();

        private static string BuildPreludeSource()
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("// string extended as a base type (SPEC 5.5.1, 8.12): each method calls the ONE native function");
            sb.AppendLine("// " + NativeName + "(id, text, arguments...) - the method is selected by its ID, not by its name.");
            sb.AppendLine("class extends string {");
            foreach (var (id, returns, name, parms) in Signatures)
            {
                string names = string.Join(", ", parms.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Split(' ')[^1]));
                string call = $"{NativeName}({(int)id}, this{(names.Length == 0 ? "" : ", " + names)})";
                sb.AppendLine($"    {(returns.Length == 0 ? "" : returns + " ")}{name}({parms}) {{ return {call} }}");
            }
            // `", ".Join(items)`: the elements of a List or an array as text (like `"" + x`), joined by this text - in one piece, natively
            sb.AppendLine("    string Join(any items) {");
            sb.AppendLine("        int n = 0");
            sb.AppendLine("        if (items is of List) { n = items.count } else { n = items.length }");
            sb.AppendLine("        var texts = new string[n]");
            sb.AppendLine("        for (int i = 0; i < n; i++) { texts[i] = \"\" + items[i] }");
            sb.AppendLine($"        return {NativeName}({(int)StringMethod.Join}, this, texts)");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine(StringBuilderSource);
            return sb.ToString();
        }

        /// <summary>`StringBuilder`: collects pieces of text and joins them once at the end (`+=` in a loop copies the whole text every time).</summary>
        private const string StringBuilderSource = """

            /// Collects pieces of text and joins them at the end: `Append` is cheap, `ToString()` builds the text once.
            class StringBuilder {
                List<string> parts
                int size = 0
                construct() { this.parts = new List<string>() }
                /// Appends a value as text (like `"" + value`).
                undefined Append(any value) {
                    string t = "" + value
                    this.parts.Add(t)
                    this.size = this.size + t.Length
                }
                /// Appends a value as text and a line break.
                undefined AppendLine(any value) {
                    this.Append(value)
                    this.Append("\n")
                }
                /// The number of characters collected so far.
                int Length { get { return this.size } }
                /// Removes everything collected.
                undefined Clear() {
                    this.parts.Clear()
                    this.size = 0
                }
                /// The collected text.
                string ToString() { return "".Join(this.parts) }
            }
            """;

        /// <summary>The native function `__StringCall(id, text, arguments...)`.</summary>
        public static Value Call(Value[] args)
        {
            if (args.Length < 2 || args[1].Kind != ValueKind.String)
                throw new InvalidOperationException($"{NativeName}(id, text, ...) expects the string as the second argument.");

            var method = (StringMethod)args[0].AsInt();
            string s = args[1].AsString();
            int argc = args.Length - 2;
            Value a0 = argc > 0 ? args[2] : default;
            Value a1 = argc > 1 ? args[3] : default;

            switch (method)
            {
                case StringMethod.IndexOf:
                {
                    Arity(method, argc, 1, 2);
                    long start = argc == 2 ? a1.AsInt() : 0;
                    if (start < 0 || start > s.Length) throw Out(start, s.Length);
                    return Value.MakeInt(s.IndexOf(Text(a0), (int)start, StringComparison.Ordinal));
                }

                // Searches BACKWARDS from `start` (default: the last character) - a hit must lie
                // entirely within the range [0 .. start] (like string.LastIndexOf in .NET).
                case StringMethod.LastIndexOf:
                {
                    Arity(method, argc, 1, 2);
                    string value = Text(a0);
                    long start = argc == 2 ? a1.AsInt() : s.Length - 1;
                    if (s.Length == 0) return Value.MakeInt(value.Length == 0 ? 0 : -1);
                    if (start < 0 || start >= s.Length) throw Out(start, s.Length);
                    return Value.MakeInt(s.LastIndexOf(value, (int)start, StringComparison.Ordinal));
                }

                case StringMethod.Substring:
                {
                    Arity(method, argc, 1, 2);
                    long start = a0.AsInt();
                    if (start < 0 || start > s.Length) throw Out(start, s.Length);
                    long count = argc == 2 ? a1.AsInt() : s.Length - start;
                    if (count < 0 || count > s.Length - start) throw Out(start + count, s.Length);
                    return Value.MakeString(s.Substring((int)start, (int)count));
                }

                case StringMethod.CharAt:
                {
                    Arity(method, argc, 1, 1);
                    long index = a0.AsInt();
                    if (index < 0 || index >= s.Length) throw Out(index, s.Length);
                    return Value.MakeChar(s[(int)index]);
                }

                case StringMethod.Contains:
                    Arity(method, argc, 1, 1);
                    return Value.MakeBool(s.Contains(Text(a0), StringComparison.Ordinal));
                case StringMethod.StartsWith:
                    Arity(method, argc, 1, 1);
                    return Value.MakeBool(s.StartsWith(Text(a0), StringComparison.Ordinal));
                case StringMethod.EndsWith:
                    Arity(method, argc, 1, 1);
                    return Value.MakeBool(s.EndsWith(Text(a0), StringComparison.Ordinal));

                case StringMethod.ToUpper:
                    Arity(method, argc, 0, 0);
                    return Value.MakeString(s.ToUpperInvariant());
                case StringMethod.ToLower:
                    Arity(method, argc, 0, 0);
                    return Value.MakeString(s.ToLowerInvariant());

                case StringMethod.Trim:
                    Arity(method, argc, 0, 0);
                    return Value.MakeString(s.Trim());
                case StringMethod.TrimStart:
                    Arity(method, argc, 0, 0);
                    return Value.MakeString(s.TrimStart());
                case StringMethod.TrimEnd:
                    Arity(method, argc, 0, 0);
                    return Value.MakeString(s.TrimEnd());

                // Replaces ALL occurrences; an empty `old` changes nothing.
                case StringMethod.Replace:
                {
                    Arity(method, argc, 2, 2);
                    string old = Text(a0);
                    return Value.MakeString(old.Length == 0 ? s : s.Replace(old, Text(a1), StringComparison.Ordinal));
                }

                // Splits at every occurrence of the separator (empty parts are kept);
                // an empty separator returns the whole string as the single element.
                case StringMethod.Split:
                {
                    Arity(method, argc, 1, 1);
                    string separator = Text(a0);
                    string[] parts = separator.Length == 0 ? new[] { s } : s.Split(separator, StringSplitOptions.None);
                    var array = new ScriptArray(parts.Length);
                    for (int i = 0; i < parts.Length; i++)
                        array.Items[i] = Value.MakeString(parts[i]);
                    return Value.MakeArray(array);
                }

                case StringMethod.PadLeft:
                case StringMethod.PadRight:
                {
                    Arity(method, argc, 1, 2);
                    long width = a0.AsInt();
                    if (width < 0 || width > int.MaxValue) throw Out(width, s.Length);
                    char fill = argc == 2 ? Text(a1) is { Length: > 0 } f ? f[0] : ' ' : ' ';
                    return Value.MakeString(method == StringMethod.PadLeft ? s.PadLeft((int)width, fill) : s.PadRight((int)width, fill));
                }

                // numbers from text (SPEC 8.12): spaces around are ignored; an int may have a sign, `0x`/`0b` and `_` between digits, a float
                // also a fraction and an exponent. ToInt/ToFloat throw (ArgumentException in the VM), TryToInt/TryToFloat give undefined.
                case StringMethod.ToInt:
                case StringMethod.TryToInt:
                    Arity(method, argc, 0, 0);
                    if (ParseInt(s, out long whole)) return Value.MakeInt(whole);
                    if (method == StringMethod.TryToInt) return Value.MakeUndefined();
                    throw new InvalidOperationException($"'{s}' is not a whole number.");

                case StringMethod.ToFloat:
                case StringMethod.TryToFloat:
                    Arity(method, argc, 0, 0);
                    if (ParseFloat(s, out double number)) return Value.MakeFloat(number);
                    if (method == StringMethod.TryToFloat) return Value.MakeUndefined();
                    throw new InvalidOperationException($"'{s}' is not a number.");

                case StringMethod.Join:
                {
                    Arity(method, argc, 1, 1);
                    if (a0.Kind != ValueKind.Array) throw new InvalidOperationException("Join expects a List or an array.");
                    var items = a0.AsArray();
                    var parts = new string[items.Length];
                    for (int i = 0; i < parts.Length; i++) parts[i] = items.Items[i].AsString();
                    return Value.MakeString(string.Join(s, parts));
                }

                case StringMethod.CompareTo:
                    Arity(method, argc, 1, 1);
                    return Value.MakeInt(Math.Sign(string.CompareOrdinal(s, Text(a0))));

                default:
                    throw new InvalidOperationException($"{NativeName}: unknown method ID {(int)method}.");
            }
        }

        private static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n';

        /// <summary>The text between the spaces at both ends, and whether a sign stood in front (consumed).</summary>
        private static (int Start, int End, bool Negative) Body(string s)
        {
            int start = 0, end = s.Length;
            while (start < end && IsSpace(s[start])) start++;
            while (end > start && IsSpace(s[end - 1])) end--;
            bool negative = false;
            if (start < end && s[start] is '+' or '-') { negative = s[start] == '-'; start++; }
            return (start, end, negative);
        }

        /// <summary>Digits of `radix` with `_` only between two digits; at least one digit. Must be identical to fire_rt.hpp: parseIntText.</summary>
        public static bool ParseInt(string s, out long value)
        {
            value = 0;
            var (i, end, negative) = Body(s);
            int radix = 10;
            if (end - i > 2 && s[i] == '0' && s[i + 1] is 'x' or 'X') { radix = 16; i += 2; }
            else if (end - i > 2 && s[i] == '0' && s[i + 1] is 'b' or 'B') { radix = 2; i += 2; }
            if (i >= end) return false;
            ulong magnitude = 0;
            ulong limit = negative ? 9223372036854775808UL : 9223372036854775807UL;
            for (int k = i; k < end; k++)
            {
                char c = s[k];
                if (c == '_' && k > i && k + 1 < end && s[k + 1] != '_') continue;
                int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : 99;
                if (digit >= radix) return false;
                if (magnitude > (limit - (ulong)digit) / (ulong)radix) return false;
                magnitude = magnitude * (ulong)radix + (ulong)digit;
            }
            value = negative ? (long)(0UL - magnitude) : (long)magnitude;
            return true;
        }

        /// <summary>Digits with `_` between them, an optional fraction (`2.5`, `.5`, `2.`) and exponent (`1e3`, `2.5E-2`). Must be identical to fire_rt.hpp: parseFloatText.</summary>
        public static bool ParseFloat(string s, out double value)
        {
            value = 0;
            var (i, end, negative) = Body(s);
            var clean = new StringBuilder();
            int digits = 0;
            bool Digits(ref int k)
            {
                int start = k;
                while (k < end && (char.IsAsciiDigit(s[k]) || (s[k] == '_' && k > start && k + 1 < end && char.IsAsciiDigit(s[k + 1]))))
                {
                    if (s[k] != '_') { clean.Append(s[k]); digits++; }
                    k++;
                }
                return k > start;
            }
            int p = i;
            Digits(ref p);
            if (p < end && s[p] == '.') { clean.Append('.'); p++; Digits(ref p); }
            if (digits == 0) return false;
            if (p < end && s[p] is 'e' or 'E')
            {
                clean.Append('e'); p++;
                if (p < end && s[p] is '+' or '-') clean.Append(s[p++]);
                int before = digits;
                Digits(ref p);
                if (digits == before) return false;
            }
            if (p != end) return false;
            value = double.Parse(clean.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
            if (negative) value = -value;
            return true;
        }

        /// <summary>String or character as a string.</summary>
        private static string Text(Value v) => v.Kind == ValueKind.Char ? v.AsChar().ToString() : v.AsString();

        private static NativeIndexOutOfRangeException Out(long index, int length) => new(index, length, "String index");

        private static void Arity(StringMethod method, int argc, int min, int max)
        {
            if (argc < min || argc > max)
                throw new InvalidOperationException($"{NativeName}: {method} expects {(min == max ? min.ToString() : $"{min}..{max}")} argument(s), got {argc}.");
        }
    }
}
