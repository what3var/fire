using System;
using System.Collections.Generic;
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
            (StringMethod.IndexOf, "int", "IndexOf", "value"),
            (StringMethod.IndexOf, "int", "IndexOf", "value, start"),
            (StringMethod.LastIndexOf, "int", "LastIndexOf", "value"),
            (StringMethod.LastIndexOf, "int", "LastIndexOf", "value, start"),
            (StringMethod.Substring, "string", "Substring", "start"),
            (StringMethod.Substring, "string", "Substring", "start, count"),
            (StringMethod.CharAt, "char", "CharAt", "index"),
            (StringMethod.Contains, "bool", "Contains", "value"),
            (StringMethod.StartsWith, "bool", "StartsWith", "value"),
            (StringMethod.EndsWith, "bool", "EndsWith", "value"),
            (StringMethod.ToUpper, "string", "ToUpper", ""),
            (StringMethod.ToLower, "string", "ToLower", ""),
            (StringMethod.Trim, "string", "Trim", ""),
            (StringMethod.TrimStart, "string", "TrimStart", ""),
            (StringMethod.TrimEnd, "string", "TrimEnd", ""),
            (StringMethod.Replace, "string", "Replace", "oldValue, newValue"),
            (StringMethod.Split, "string[]", "Split", "separator"),
            (StringMethod.PadLeft, "string", "PadLeft", "width"),
            (StringMethod.PadLeft, "string", "PadLeft", "width, fill"),
            (StringMethod.PadRight, "string", "PadRight", "width"),
            (StringMethod.PadRight, "string", "PadRight", "width, fill"),
        };

        /// <summary>The fire source `class extends string { ... }` for the prelude, generated from
        /// <see cref="Signatures"/> - so the IDs appear in only ONE place.</summary>
        public static string PreludeSource { get; } = BuildPreludeSource();

        private static string BuildPreludeSource()
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("// string als Basistyp erweitert (SPEC 5.5.1, 8.12): jede Methode ruft die EINE native Funktion");
            sb.AppendLine("// " + NativeName + "(id, text, argumente...) - die Methode wird über ihre ID gewählt, nicht über den Namen.");
            sb.AppendLine("class extends string {");
            foreach (var (id, returns, name, parms) in Signatures)
            {
                string call = $"{NativeName}({(int)id}, this{(parms.Length == 0 ? "" : ", " + parms)})";
                sb.AppendLine($"    {(returns.Length == 0 ? "" : returns + " ")}{name}({parms}) {{ return {call} }}");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

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

                default:
                    throw new InvalidOperationException($"{NativeName}: unknown method ID {(int)method}.");
            }
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
