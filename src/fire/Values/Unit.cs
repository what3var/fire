using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ScriptLang.Values
{
    /// <summary>
    /// Repräsentiert eine Einheit als Dimensionsvektor (Basissymbol -> Exponent)
    /// plus einen Skalierungsfaktor relativ zur reinen Basisdimension.
    ///
    /// Beispiel: "mm" hat Dimension {m:1} und Scale 0.001 (1mm = 0.001 * 1m).
    /// "mm*mm" (m^2-Dimension) hat Dimension {m:2} und Scale 0.001*0.001 = 1e-6.
    ///
    /// Zwei Units sind dimensional kompatibel, wenn ihre Dimensionsvektoren
    /// (nach Entfernen von Exponent-0-Einträgen) identisch sind. Die Umrechnung
    /// zwischen kompatiblen Units erfolgt über das Verhältnis der Scale-Faktoren.
    /// </summary>
    public sealed class Unit : IEquatable<Unit>
    {
        // Basissymbol -> Exponent. Basissymbole sind entweder eine der
        // fest eingebauten Basisdimensionen ("m","g","s","b") oder ein
        // atomarer, frei erfundener Einheitenname (z.B. "apples").
        public IReadOnlyDictionary<string, int> Dimensions { get; }

        // Faktor, um einen Zahlenwert in dieser Unit in die reine
        // Basisdimension umzurechnen: wert_in_basis = wert * Scale
        public double Scale { get; }

        // Für hübsches ToString() bei nicht-zusammengesetzten Einheiten
        // (z.B. "mm" statt nur der Dimension "m"). Null bei zusammengesetzten
        // / abgeleiteten Einheiten (z.B. Ergebnis einer Multiplikation).
        private readonly string? _displaySymbol;

        public static readonly Unit Unitless = new Unit(new Dictionary<string, int>(), 1.0, null);

        private Unit(IReadOnlyDictionary<string, int> dimensions, double scale, string? displaySymbol)
        {
            Dimensions = dimensions;
            Scale = scale;
            _displaySymbol = displaySymbol;
        }

        public bool IsUnitless => Dimensions.Count == 0;

        // ---------------------------------------------------------------
        // Eingebaute Präfixe (dezimal)
        // ---------------------------------------------------------------
        private static readonly Dictionary<char, double> Prefixes = new()
        {
            ['f'] = 1e-15,
            ['p'] = 1e-12,
            ['µ'] = 1e-6,
            ['m'] = 1e-3,
            ['c'] = 1e-2,
            ['k'] = 1e3,
            ['M'] = 1e6,
            ['G'] = 1e9,
        };

        // Präfixfähige Basiseinheiten: Symbol -> kanonisches Basissymbol
        // (bei diesen ist Basissymbol == Symbol, aber explizit gehalten für Klarheit)
        private static readonly HashSet<string> PrefixableBaseUnits = new() { "m", "g", "s", "b", "B" };

        // "B" (Byte) ist selbst schon eine benannte, präfixfähige Einheit,
        // deren kanonische Basisdimension "b" (Bit) ist, mit Skalierungsfaktor 8.
        // Nicht-präfixfähige, aber zu "s" kompatible Zeiteinheiten mit festem Faktor.
        private static readonly Dictionary<string, (string baseSymbol, double scale)> NamedUnits = new()
        {
            ["m"] = ("m", 1.0),
            ["g"] = ("g", 1.0),
            ["s"] = ("s", 1.0),
            ["b"] = ("b", 1.0),
            ["B"] = ("b", 8.0),
            ["min"] = ("s", 60.0),
            ["h"] = ("s", 3600.0),
            ["d"] = ("s", 86400.0),
        };

        // Einheiten, die selbst keinen Präfix vor sich zulassen (auch wenn ihre
        // Basisdimension prinzipiell präfixfähig ist, z.B. "s" via "ms").
        private static readonly HashSet<string> NonPrefixable = new() { "min", "h", "d" };

        /// <summary>
        /// Parst eine Einheiten-Suffix-Zeichenfolge, wie sie direkt an einem
        /// Zahlenliteral steht (z.B. "mm", "km", "min", "apples").
        /// Leere Zeichenfolge -> Unitless.
        /// </summary>
        public static Unit Parse(string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
                return Unitless;

            // 1) Exakter Treffer auf einen benannten (ggf. nicht-präfixfähigen) Unit-Namen zuerst,
            //    damit z.B. "min" nicht fälschlich als Präfix 'm' + Basis "in" zerlegt wird.
            if (NamedUnits.TryGetValue(symbol, out var named))
            {
                var dims = new Dictionary<string, int> { [named.baseSymbol] = 1 };
                return new Unit(dims, named.scale, symbol);
            }

            // 2) Präfix + präfixfähige Basiseinheit, z.B. "mm" = 'm'(milli) + "m"(Meter)
            if (symbol.Length >= 2)
            {
                char prefixChar = symbol[0];
                string rest = symbol.Substring(1);
                if (Prefixes.TryGetValue(prefixChar, out var prefixFactor)
                    && NamedUnits.TryGetValue(rest, out var baseUnit)
                    && PrefixableBaseUnits.Contains(rest)
                    && !NonPrefixable.Contains(rest))
                {
                    var dims = new Dictionary<string, int> { [baseUnit.baseSymbol] = 1 };
                    return new Unit(dims, prefixFactor * baseUnit.scale, symbol);
                }
            }

            // 3) Unbekannt -> atomare Einheit, kompatibel nur zu sich selbst.
            var atomicDims = new Dictionary<string, int> { [symbol] = 1 };
            return new Unit(atomicDims, 1.0, symbol);
        }

        public bool IsCompatibleWith(Unit other)
        {
            if (Dimensions.Count != other.Dimensions.Count) return false;
            foreach (var (key, exp) in Dimensions)
            {
                if (!other.Dimensions.TryGetValue(key, out var otherExp) || otherExp != exp)
                    return false;
            }
            return true;
        }

        /// <summary>Faktor, mit dem ein Zahlenwert in dieser Unit multipliziert werden muss,
        /// um den äquivalenten Wert in <paramref name="target"/> zu erhalten.</summary>
        public double ConversionFactorTo(Unit target)
        {
            if (!IsCompatibleWith(target))
                throw new UnitMismatchException(this, target);
            return Scale / target.Scale;
        }

        public static Unit Multiply(Unit a, Unit b)
        {
            // Häufigster Fall: Skalierung mit einer reinen Zahl (z.B.
            // `20mm / 2`, `3 * 5km`) - Ergebnis soll GENAU die Einheit des
            // anderen Faktors sein (inkl. dessen Anzeige-Symbol), nicht eine
            // neu konstruierte, unbenannte Einheit. Ohne das würde z.B.
            // `20mm / 2` zwar korrekt intern als 10 * Scale(0.001) berechnet,
            // aber ohne Anzeige-Symbol als "10m" statt "10mm" dargestellt
            // (ToString() zeigt für unbenannte Einheiten nur die nackte
            // Basisdimension, ignoriert dabei aber deren Scale-Faktor - siehe
            // ToString()-Kommentar unten).
            if (b.IsUnitless) return a;
            if (a.IsUnitless) return b;

            var dims = new Dictionary<string, int>(a.Dimensions);
            foreach (var (key, exp) in b.Dimensions)
            {
                dims[key] = dims.TryGetValue(key, out var existing) ? existing + exp : exp;
                if (dims[key] == 0) dims.Remove(key);
            }

            // Zweithäufigster Fall: dieselbe benannte Einheit mit sich selbst
            // multipliziert (z.B. `radius * radius` für eine Fläche) - dafür
            // einen sinnvollen Anzeige-Namen synthetisieren ("mm^2" statt nur
            // "m^2", das den Scale-Faktor sonst optisch verschluckt). Deckt
            // bewusst nur das direkte Quadrat ab (a*a), nicht verkettete
            // höhere Potenzen (a*a*a für Volumen) - danach ist das Zwischen-
            // ergebnis schon "mm^2" benannt und matcht "mm" nicht mehr, fällt
            // also auf die (dann wieder scale-blinde) Dimensions-Anzeige
            // zurück. Für wirklich gemischte Einheiten (z.B. m*s) bleibt es
            // ebenfalls bei der reinen Dimensions-Anzeige - dort ist Scale in
            // der Praxis meist 1.0, das Problem tritt also seltener auf.
            string? display = a._displaySymbol != null && a._displaySymbol == b._displaySymbol
                ? $"{a._displaySymbol}^2"
                : null;

            return new Unit(dims, a.Scale * b.Scale, display);
        }

        public static Unit Divide(Unit a, Unit b)
        {
            // Siehe Multiply() - derselbe Skalierungs-Sonderfall, hier nur für
            // den Nenner sinnvoll (b unitless): `a` bleibt unverändert. Ist
            // dagegen `a` unitless (z.B. `2 / 20mm`), ist das Ergebnis
            // dimensional etwas GENUIN NEUES (1/Länge) - dafür bewusst KEIN
            // Sonderfall, das muss durch die normale Dimensions-Konstruktion.
            if (b.IsUnitless) return a;

            var dims = new Dictionary<string, int>(a.Dimensions);
            foreach (var (key, exp) in b.Dimensions)
            {
                dims[key] = dims.TryGetValue(key, out var existing) ? existing - exp : -exp;
                if (dims[key] == 0) dims.Remove(key);
            }
            return new Unit(dims, a.Scale / b.Scale, null);
        }

        public bool Equals(Unit? other)
        {
            if (other is null) return false;
            if (!IsCompatibleWith(other)) return false;
            return Math.Abs(Scale - other.Scale) < 1e-12;
        }

        public override bool Equals(object? obj) => obj is Unit u && Equals(u);

        public override int GetHashCode()
        {
            int hash = 17;
            foreach (var kv in Dimensions.OrderBy(k => k.Key))
                hash = HashCode.Combine(hash, kv.Key, kv.Value);
            return hash;
        }

        public override string ToString()
        {
            if (IsUnitless) return "unitless";
            if (_displaySymbol != null) return _displaySymbol;

            // Positive Exponenten -> Zähler, negative -> Nenner, dargestellt als
            // Bruch (z.B. "m/s" statt "m*s^-1", "m/s^2" statt "m*s^-2").
            var numerator = new List<string>();
            var denominator = new List<string>();
            foreach (var (key, exp) in Dimensions.OrderBy(k => k.Key))
            {
                if (exp > 0)
                    numerator.Add(exp == 1 ? key : $"{key}^{exp}");
                else
                    denominator.Add(exp == -1 ? key : $"{key}^{-exp}");
            }

            string num = numerator.Count > 0 ? string.Join("*", numerator) : "1";
            string dimPart = num;
            if (denominator.Count > 0)
            {
                string den = string.Join("*", denominator);
                if (denominator.Count > 1) den = $"({den})";
                dimPart = $"{num}/{den}";
            }

            // Ohne eigenes Anzeige-Symbol (z.B. verkettete Multiplikation wie
            // `a*a*a` für ein Volumen, oder wirklich gemischte Einheiten mit
            // krummem Skalierungsfaktor) zeigt die reine Dimensions-Anzeige
            // oben nur die BASIS-Dimension (z.B. "m^3"), ignoriert dabei aber
            // einen von 1.0 abweichenden Scale-Faktor - der Zahlenwert selbst
            // ist trotzdem korrekt (siehe Multiply/Divide), nur die Anzeige
            // würde ihn sonst STILLSCHWEIGEND falsch interpretierbar machen
            // (z.B. "10" bei tatsächlich 10 Kubik-Millimetern als "10 m^3"
            // gelesen). Deshalb den Faktor explizit ausweisen, statt ihn zu
            // verschlucken - kein hübscher Einheitenname, aber wenigstens
            // nicht irreführend.
            if (Math.Abs(Scale - 1.0) > 1e-12)
                return $"{dimPart}(×{Scale.ToString("G", System.Globalization.CultureInfo.InvariantCulture)})";

            return dimPart;
        }
    }

    public sealed class UnitMismatchException : Exception
    {
        public Unit From { get; }
        public Unit To { get; }

        public UnitMismatchException(Unit from, Unit to)
            : base($"Einheiten inkompatibel: '{from}' kann nicht nach '{to}' umgerechnet werden.")
        {
            From = from;
            To = to;
        }
    }
}
