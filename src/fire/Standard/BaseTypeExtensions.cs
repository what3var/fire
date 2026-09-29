using System.Diagnostics.CodeAnalysis;
using fire.Values;

namespace fire.Standard
{
    /// <summary>
    /// Erweiterungen von Basistypen (`class extends string { ... }`, SPEC 5.5.1).
    ///
    /// Ein Basistyp (`string`, `char`, `int`, `float`, `bool`) hat keine Klassendeklaration, die
    /// ein `class extends` erweitern könnte. Der Parser sammelt deshalb alle Erweiterungen eines
    /// Basistyps in EINE synthetische Klasse mit dem internen Namen <see cref="ClassName"/>
    /// (`$string`, ...) - kein gültiger Bezeichner, kollidiert also nie mit einer Nutzerklasse. Für
    /// Resolver und Compiler ist das eine gewöhnliche Klasse; die VM ruft ihre Methoden auf, wenn ein
    /// Methodenaufruf auf einem Wert dieses <see cref="ValueKind"/> keine Objektinstanz trifft, mit
    /// dem Wert selbst als `this`.
    ///
    /// Erlaubt sind NUR Methoden: ein Basiswert hat keinen Speicher, in dem ein Feld oder eine
    /// Property leben könnte (und `int`/`string` werden nicht per Referenz gehalten).
    /// </summary>
    public static class BaseTypeExtensions
    {
        /// <summary>Alle Basistypen, die sich erweitern lassen (Schlüsselwort → Werteart).
        /// `byte` fehlt bewusst: ein `byte` ist zur Laufzeit ein `int` (nur mit anderer Breite) -
        /// wer ihn erweitern will, erweitert `int`.</summary>
        public static bool TryGetKind(string typeName, out ValueKind kind)
        {
            switch (typeName)
            {
                case "string": kind = ValueKind.String; return true;
                case "char": kind = ValueKind.Char; return true;
                case "int": kind = ValueKind.Int; return true;
                case "float": kind = ValueKind.Float; return true;
                case "bool": kind = ValueKind.Bool; return true;
                default: kind = default; return false;
            }
        }

        public static bool IsExtendable(string typeName) => TryGetKind(typeName, out _);

        /// <summary>Interner Klassenname der Sammelklasse für `typeName` (z.B. `$string`).</summary>
        public static string ClassName(string typeName) => "$" + typeName;

        /// <summary>Die Sammelklasse für einen Basiswert dieser Art - <c>null</c>, wenn die Art gar
        /// nicht erweiterbar ist.</summary>
        public static string? ClassNameFor(ValueKind kind) => kind switch
        {
            ValueKind.String => "$string",
            ValueKind.Char => "$char",
            ValueKind.Int => "$int",
            ValueKind.Float => "$float",
            ValueKind.Bool => "$bool",
            _ => null,
        };

        /// <summary>Der Typname hinter einem Sammelklassennamen (`$string` → `string`), sonst
        /// <c>null</c>.</summary>
        public static bool TryGetTypeName(string className, [NotNullWhen(true)] out string? typeName)
        {
            typeName = null;
            if (className.Length < 2 || className[0] != '$') return false;
            string candidate = className.Substring(1);
            if (!IsExtendable(candidate)) return false;
            typeName = candidate;
            return true;
        }
    }
}
