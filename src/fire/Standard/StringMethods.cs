using System;
using System.Collections.Generic;
using System.Text;
using fire.Bytecode;
using fire.Values;

namespace fire.Standard
{
    /// <summary>Die Methoden von `string` (SPEC 8.12). Die Zahlen sind die IDs, über die der Prelude
    /// die native Funktion <see cref="StringMethods.NativeName"/> anspricht - bewusst feste Werte
    /// (nie umnummerieren) und KEINE Namen: das Dispatchen über Zeichenkettenvergleiche wäre in der
    /// VM zu langsam.</summary>
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
    /// `string`-Methoden als Erweiterung des Basistyps (`class extends string`, SPEC 5.5.1) im Prelude,
    /// alle über EINE native Funktion: `__StringCall(id, text, argumente...)`. Alle Vergleiche sind
    /// ORDINAL (Zeichen für Zeichen, ohne Kultur), Indizes zählen `char`s (16-Bit-Codeeinheiten).
    /// Ein Wert, der eine Zeichenkette ODER ein Zeichen sein darf (`IndexOf("ab")`, `IndexOf('a')`),
    /// wird über <see cref="Text"/> gelesen. Ein ungültiger Index wirft
    /// <see cref="NativeIndexOutOfRangeException"/> (die VM macht daraus eine fangbare
    /// `IndexOutOfBoundsException`).
    /// </summary>
    public static class StringMethods
    {
        public const string NativeName = "__StringCall";

        /// <summary>Signaturen des Prelude: (Id, Rückgabetyp, Name, Parameter). Pro Überladung eine Zeile -
        /// der Prelude reicht die Parameter unverändert an die native Funktion durch, deren Argumentzahl
        /// dann die Überladung bestimmt.</summary>
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

        /// <summary>Der fire-Quelltext `class extends string { ... }` für den Prelude, aus
        /// <see cref="Signatures"/> erzeugt - die IDs stehen so nur an EINER Stelle.</summary>
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

        /// <summary>Die native Funktion `__StringCall(id, text, argumente...)`.</summary>
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

                // Sucht RÜCKWÄRTS ab `start` (Vorgabe: dem letzten Zeichen) - ein Treffer muss
                // ganz im Bereich [0 .. start] liegen (wie string.LastIndexOf in .NET).
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

                // Ersetzt ALLE Vorkommen; ein leeres `old` ändert nichts.
                case StringMethod.Replace:
                {
                    Arity(method, argc, 2, 2);
                    string old = Text(a0);
                    return Value.MakeString(old.Length == 0 ? s : s.Replace(old, Text(a1), StringComparison.Ordinal));
                }

                // Zerlegt an jedem Vorkommen des Trenners (leere Teile bleiben erhalten);
                // ein leerer Trenner liefert die ganze Zeichenkette als einziges Element.
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

        /// <summary>Zeichenkette oder Zeichen als Zeichenkette.</summary>
        private static string Text(Value v) => v.Kind == ValueKind.Char ? v.AsChar().ToString() : v.AsString();

        private static NativeIndexOutOfRangeException Out(long index, int length) => new(index, length, "String index");

        private static void Arity(StringMethod method, int argc, int min, int max)
        {
            if (argc < min || argc > max)
                throw new InvalidOperationException($"{NativeName}: {method} expects {(min == max ? min.ToString() : $"{min}..{max}")} argument(s), got {argc}.");
        }
    }
}
