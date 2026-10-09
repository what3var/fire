using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MemoryPack;

namespace fire.Values
{
    /// <summary>
    /// Represents a unit as a dimension vector (base symbol -> exponent)
    /// plus a scale factor relative to the pure base dimension.
    ///
    /// Example: "mm" has dimension {m:1} and scale 0.001 (1mm = 0.001 * 1m).
    /// "mm*mm" (m^2 dimension) has dimension {m:2} and scale 0.001*0.001 = 1e-6.
    ///
    /// Two units are dimensionally compatible if their dimension vectors
    /// (after removing exponent-0 entries) are identical. Conversion
    /// between compatible units is done via the ratio of the scale factors.
    /// </summary>
    [MemoryPackable]
    public sealed partial class Unit : IEquatable<Unit>
    {
        // Base symbol -> exponent. Base symbols are either one of the
        // built-in base dimensions ("m","g","s","b") or an
        // atomic, freely invented unit name (e.g. "apples").
        public IReadOnlyDictionary<string, int> Dimensions { get; }

        // Factor to convert a numeric value in this unit into the pure
        // base dimension: value_in_base = value * Scale
        public double Scale { get; }

        // For pretty ToString() on non-composite units
        // (e.g. "mm" instead of just the dimension "m"). Null for composite
        // / derived units (e.g. the result of a multiplication).
        //
        // Private, but serialised nevertheless via [MemoryPackInclude] (see
        // MemoryPack docs: private members NOT included by default,
        // explicitly necessary) - needs `partial` on the class for that, otherwise
        // the generated formatter code would have no access to it.
        [MemoryPackInclude]
        private readonly string? _displaySymbol;

        public static readonly Unit Unitless = new Unit(new Dictionary<string, int>(), 1.0, null);

        [MemoryPackConstructor]
        private Unit(IReadOnlyDictionary<string, int> dimensions, double scale, string? displaySymbol)
        {
            Dimensions = dimensions;
            Scale = scale;
            _displaySymbol = displaySymbol;
        }

        public bool IsUnitless => Dimensions.Count == 0;

        /// <summary>The symbol of a named unit (`mm`), null for a derived one (the native backend writes it into its unit table).</summary>
        [MemoryPackIgnore]
        public string? DisplaySymbol => _displaySymbol;

        // ---------------------------------------------------------------
        // Built-in prefixes (decimal)
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

        // Prefixable base units: symbol -> canonical base symbol
        // (for these, base symbol == symbol, but kept explicit for clarity)
        private static readonly HashSet<string> PrefixableBaseUnits = new() { "m", "g", "s", "b", "B" };

        // "B" (byte) is itself already a named, prefixable unit,
        // whose canonical base dimension is "b" (bit), with scale factor 8.
        // Non-prefixable but "s"-compatible time units with a fixed factor.
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

        // Units that themselves allow no prefix in front of them (even if their
        // base dimension is in principle prefixable, e.g. "s" via "ms").
        private static readonly HashSet<string> NonPrefixable = new() { "min", "h", "d" };

        /// <summary>
        /// Parses a unit-suffix string as it stands directly on a
        /// number literal (e.g. "mm", "km", "min", "apples").
        /// Empty string -> Unitless.
        /// </summary>
        public static Unit Parse(string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
                return Unitless;

            // 1) Exact hit on a named (possibly non-prefixable) unit name first,
            //    so that e.g. "min" is not wrongly split into prefix 'm' + base "in".
            if (NamedUnits.TryGetValue(symbol, out var named))
            {
                var dims = new Dictionary<string, int> { [named.baseSymbol] = 1 };
                return new Unit(dims, named.scale, symbol);
            }

            // 2) Prefix + prefixable base unit, e.g. "mm" = 'm'(milli) + "m"(metre)
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

            // 3) Unknown -> atomic unit, compatible only with itself.
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

        /// <summary>Factor by which a numeric value in this unit must be multiplied
        /// to obtain the equivalent value in <paramref name="target"/>.</summary>
        public double ConversionFactorTo(Unit target)
        {
            if (!IsCompatibleWith(target))
                throw new UnitMismatchException(this, target);
            return Scale / target.Scale;
        }

        public static Unit Multiply(Unit a, Unit b)
        {
            // Most common case: scaling with a pure number (e.g.
            // `20mm / 2`, `3 * 5km`) - the result should be EXACTLY the unit of the
            // other factor (including its display symbol), not a
            // newly constructed, unnamed unit. Without this, e.g.
            // `20mm / 2` would indeed be computed correctly internally as 10 * Scale(0.001),
            // but displayed without a display symbol as "10m" instead of "10mm"
            // (ToString() shows only the bare base dimension for unnamed units,
            // while ignoring their scale factor - see
            // ToString() comment below).
            if (b.IsUnitless) return a;
            if (a.IsUnitless) return b;

            var dims = new Dictionary<string, int>(a.Dimensions);
            foreach (var (key, exp) in b.Dimensions)
            {
                dims[key] = dims.TryGetValue(key, out var existing) ? existing + exp : exp;
                if (dims[key] == 0) dims.Remove(key);
            }

            // Second most common case: the same named unit multiplied
            // by itself (e.g. `radius * radius` for an area) - synthesise
            // a sensible display name for it ("mm^2" instead of just
            // "m^2", which otherwise visually swallows the scale factor). Deliberately
            // covers only the direct square (a*a), not chained
            // higher powers (a*a*a for volume) - after that the intermediate
            // result is already named "mm^2" and no longer matches "mm", so it falls
            // back to the (then again scale-blind) dimension display.
            // For truly mixed units (e.g. m*s) it likewise stays
            // with the pure dimension display - there scale is
            // usually 1.0 in practice, so the problem occurs less often.
            string? display = a._displaySymbol != null && a._displaySymbol == b._displaySymbol
                ? $"{a._displaySymbol}^2"
                : null;

            return new Unit(dims, a.Scale * b.Scale, display);
        }

        public static Unit Divide(Unit a, Unit b)
        {
            // See Multiply() - the same scaling special case, here sensible only for
            // the denominator (b unitless): `a` stays unchanged. If, on the other hand,
            // `a` is unitless (e.g. `2 / 20mm`), the result is
            // dimensionally something GENUINELY NEW (1/length) - deliberately NO
            // special case for that, it must go through the normal dimension construction.
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
            if (ReferenceEquals(this, other)) return true; // the most common case: the same unit (usually `Unitless`)
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

            // Positive exponents -> numerator, negative -> denominator, displayed as a
            // fraction (e.g. "m/s" instead of "m*s^-1", "m/s^2" instead of "m*s^-2").
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

            // Without a display symbol of its own (e.g. chained multiplication such as
            // `a*a*a` for a volume, or truly mixed units with an
            // odd scale factor) the pure dimension display
            // above shows only the BASE dimension (e.g. "m^3"), while ignoring
            // a scale factor differing from 1.0 - the numeric value itself
            // is nevertheless correct (see Multiply/Divide), only the display
            // would otherwise make it SILENTLY open to misinterpretation
            // (e.g. "10" for actually 10 cubic millimetres read
            // as "10 m^3"). Therefore state the factor explicitly instead of
            // swallowing it - not a pretty unit name, but at least
            // not misleading.
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
            : base($"Incompatible units: '{from}' cannot be converted to '{to}'.")
        {
            From = from;
            To = to;
        }
    }
}
