using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Ergebnis von <see cref="StringMethods.TryCall"/>.</summary>
    internal enum StringCallStatus
    {
        /// <summary>Kein Methodenname/keine Argumentanzahl dieser Klasse (die VM
        /// probiert dann die übrigen eingebauten Methoden, z.B. ToBytes).</summary>
        NotFound,
        Ok,
        /// <summary>Ein Index/eine Länge lag außerhalb der Zeichenkette - die VM
        /// macht daraus eine fangbare `IndexOutOfBoundsException`.</summary>
        IndexOutOfRange,
    }

    /// <summary>
    /// Die eingebauten Methoden von `string` (SPEC 8.12): `IndexOf`, `LastIndexOf`,
    /// `Substring`, `CharAt`, `Contains`, `StartsWith`, `EndsWith`, `ToUpper`, `ToLower`,
    /// `Trim`/`TrimStart`/`TrimEnd`, `Replace`, `Split`, `PadLeft`, `PadRight`. Alle
    /// vergleichen ORDINAL (Zeichen für Zeichen, ohne Kultur), Indizes zählen `char`s
    /// (16-Bit-Codeeinheiten, wie der Typ `char` dieser Sprache). Ein Wert, der eine
    /// Zeichenkette ODER ein Zeichen sein darf (`IndexOf("ab")`, `IndexOf('a')`), wird
    /// über <see cref="Text"/> gelesen. Kein Zugriff auf VM-Zustand - eine
    /// Skript-Exception wirft die VM anhand von <see cref="StringCallStatus.IndexOutOfRange"/>.
    /// </summary>
    internal static class StringMethods
    {
        /// <summary>Zeichenkette oder Zeichen als Zeichenkette.</summary>
        private static string Text(Value v) => v.Kind == ValueKind.Char ? v.AsChar().ToString() : v.AsString();

        public static StringCallStatus TryCall(
            string s, string name, Value[] args, out Value result, out long badIndex, out int length)
        {
            result = default;
            badIndex = 0;
            length = s.Length;

            switch (name)
            {
                case "IndexOf" when args.Length is 1 or 2:
                {
                    long start = args.Length == 2 ? args[1].AsInt() : 0;
                    if (start < 0 || start > s.Length) return Out(start, out badIndex);
                    result = Value.MakeInt(s.IndexOf(Text(args[0]), (int)start, StringComparison.Ordinal));
                    return StringCallStatus.Ok;
                }

                // Sucht RÜCKWÄRTS ab `start` (Vorgabe: dem letzten Zeichen) - ein Treffer muss
                // ganz im Bereich [0 .. start] liegen (wie string.LastIndexOf in .NET).
                case "LastIndexOf" when args.Length is 1 or 2:
                {
                    string value = Text(args[0]);
                    long start = args.Length == 2 ? args[1].AsInt() : s.Length - 1;
                    if (s.Length == 0)
                    {
                        result = Value.MakeInt(value.Length == 0 ? 0 : -1);
                        return StringCallStatus.Ok;
                    }
                    if (start < 0 || start >= s.Length) return Out(start, out badIndex);
                    result = Value.MakeInt(s.LastIndexOf(value, (int)start, StringComparison.Ordinal));
                    return StringCallStatus.Ok;
                }

                case "Substring" when args.Length is 1 or 2:
                {
                    long start = args[0].AsInt();
                    if (start < 0 || start > s.Length) return Out(start, out badIndex);
                    long count = args.Length == 2 ? args[1].AsInt() : s.Length - start;
                    if (count < 0 || count > s.Length - start) return Out(start + count, out badIndex);
                    result = Value.MakeString(s.Substring((int)start, (int)count));
                    return StringCallStatus.Ok;
                }

                case "CharAt" when args.Length == 1:
                {
                    long index = args[0].AsInt();
                    if (index < 0 || index >= s.Length) return Out(index, out badIndex);
                    result = Value.MakeChar(s[(int)index]);
                    return StringCallStatus.Ok;
                }

                case "Contains" when args.Length == 1:
                    result = Value.MakeBool(s.Contains(Text(args[0]), StringComparison.Ordinal));
                    return StringCallStatus.Ok;
                case "StartsWith" when args.Length == 1:
                    result = Value.MakeBool(s.StartsWith(Text(args[0]), StringComparison.Ordinal));
                    return StringCallStatus.Ok;
                case "EndsWith" when args.Length == 1:
                    result = Value.MakeBool(s.EndsWith(Text(args[0]), StringComparison.Ordinal));
                    return StringCallStatus.Ok;

                case "ToUpper" when args.Length == 0:
                    result = Value.MakeString(s.ToUpperInvariant());
                    return StringCallStatus.Ok;
                case "ToLower" when args.Length == 0:
                    result = Value.MakeString(s.ToLowerInvariant());
                    return StringCallStatus.Ok;

                case "Trim" when args.Length == 0:
                    result = Value.MakeString(s.Trim());
                    return StringCallStatus.Ok;
                case "TrimStart" when args.Length == 0:
                    result = Value.MakeString(s.TrimStart());
                    return StringCallStatus.Ok;
                case "TrimEnd" when args.Length == 0:
                    result = Value.MakeString(s.TrimEnd());
                    return StringCallStatus.Ok;

                // Ersetzt ALLE Vorkommen; ein leeres `old` ändert nichts.
                case "Replace" when args.Length == 2:
                {
                    string old = Text(args[0]);
                    result = Value.MakeString(old.Length == 0 ? s : s.Replace(old, Text(args[1]), StringComparison.Ordinal));
                    return StringCallStatus.Ok;
                }

                // Zerlegt an jedem Vorkommen des Trenners (leere Teile bleiben erhalten);
                // ein leerer Trenner liefert die ganze Zeichenkette als einziges Element.
                case "Split" when args.Length == 1:
                {
                    string separator = Text(args[0]);
                    string[] parts = separator.Length == 0 ? new[] { s } : s.Split(separator, StringSplitOptions.None);
                    var array = new ScriptArray(parts.Length);
                    for (int i = 0; i < parts.Length; i++)
                        array.Items[i] = Value.MakeString(parts[i]);
                    result = Value.MakeArray(array);
                    return StringCallStatus.Ok;
                }

                case "PadLeft" when args.Length is 1 or 2:
                case "PadRight" when args.Length is 1 or 2:
                {
                    long width = args[0].AsInt();
                    if (width < 0 || width > int.MaxValue) return Out(width, out badIndex);
                    char fill = args.Length == 2 ? Text(args[1]) is { Length: > 0 } f ? f[0] : ' ' : ' ';
                    result = Value.MakeString(name == "PadLeft" ? s.PadLeft((int)width, fill) : s.PadRight((int)width, fill));
                    return StringCallStatus.Ok;
                }

                default:
                    return StringCallStatus.NotFound;
            }

            static StringCallStatus Out(long index, out long bad)
            {
                bad = index;
                return StringCallStatus.IndexOutOfRange;
            }
        }
    }
}
