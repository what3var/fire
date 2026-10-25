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

        /// <summary>A unit with an offset (`unit Celsius: C { coerce K => value + 273.15 }`): value_in_base = value * Scale + Offset. 0 for the units of
        /// fire and every unit that is only a factor.</summary>
        public double Offset { get; }

        /// <summary>A unit converted by functions (`coerce to W => 10 ^ (value / 10)` and `coerce from W => ...`): the name of the hidden class whose static
        /// methods `To` and `From` convert a value into the reference unit and back; Scale and Offset are those of the reference unit. Null for every
        /// other unit.</summary>
        public string? Converter { get; }

        /// <summary>A unit of its own dimension that becomes a plain number by functions (`unit Decibel: dB { coerce to unitless => 10 ^ (value / 20);
        /// coerce from unitless => 20 * Math.Log10(value) }`): the name of the hidden class with To and From. Such a value is converted into its number when
        /// it is multiplied or divided, stored without a unit or coerced to unitless; added to and compared with its own unit it stays as it is. Null for
        /// every other unit.</summary>
        public string? UnitlessConverter { get; }

        [MemoryPackConstructor]
        private Unit(IReadOnlyDictionary<string, int> dimensions, double scale, string? displaySymbol, double offset, string? converter, string? unitlessConverter)
        {
            Dimensions = dimensions;
            Scale = scale;
            _displaySymbol = displaySymbol;
            Offset = offset;
            Converter = converter;
            UnitlessConverter = unitlessConverter;
        }

        private Unit(IReadOnlyDictionary<string, int> dimensions, double scale, string? displaySymbol, double offset, string? converter)
            : this(dimensions, scale, displaySymbol, offset, converter, null) { }

        private Unit(IReadOnlyDictionary<string, int> dimensions, double scale, string? displaySymbol) : this(dimensions, scale, displaySymbol, 0, null, null) { }

        /// <summary>The plain number of a value of a unit with UnitlessConverter (`6dB` is 1.995...).</summary>
        public double ToUnitless(double value) => UnitlessConverter != null ? CallConverter(UnitlessConverter, "To", value) : value;

        /// <summary>A plain number as a value of a unit with UnitlessConverter (2 is 6.02dB).</summary>
        public double FromUnitless(double value) => UnitlessConverter != null ? CallConverter(UnitlessConverter, "From", value) : value;

        /// <summary>A unit that is only a factor of its base unit (all units of fire, `unit Inch: in { coerce m => 0.0254 }`) - not one with an offset or
        /// conversion functions.</summary>
        [MemoryPackIgnore]
        public bool IsLinear => Offset == 0 && Converter == null;

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
            ['µ'] = 1e-6,   // micro sign (U+00B5)
            ['μ'] = 1e-6,   // Greek small mu (U+03BC), what many keyboards type
            ['u'] = 1e-6,   // the ASCII spelling: 10uF, 5us
            ['m'] = 1e-3,
            ['c'] = 1e-2,
            ['k'] = 1e3,
            ['M'] = 1e6,
            ['G'] = 1e9,
            ['n'] = 1e-9,
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
            if (string.IsNullOrEmpty(symbol) || symbol == "unitless")
                return Unitless;

            if (symbol[0] == DemandMark) return DecodeDemand(symbol);
            var custom = _customUnits;

            // A combined unit of a declaration: `m/s`, `kg*m/s^2`, `m^2` - factors joined by `*` and `/` (everything after a `/` divides), each with an optional power
            if (symbol.IndexOfAny(CombiningChars) >= 0)
                return ParseCombined(symbol);

            // 1) Exact hit on a named (possibly non-prefixable) unit name first,
            //    so that e.g. "min" is not wrongly split into prefix 'm' + base "in".
            if (NamedUnits.TryGetValue(symbol, out var named))
            {
                var dims = new Dictionary<string, int> { [named.baseSymbol] = 1 };
                return new Unit(dims, named.scale, symbol);
            }

            // 1b) A unit of the program (`unit Inch: in { ... }`), by its symbol or its name
            if (custom.Count > 0 && custom.TryGetValue(symbol, out var own))
                return new Unit(own.DimensionTable, own.Scale, own.Symbol, own.Offset, own.Converter, own.UnitlessConverter);

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

            // 2b) Prefix + a `prefixed unit` of the program (`mV` of `prefixed unit Volt: V`) - by its symbol only
            if (symbol.Length >= 2 && custom.Count > 0 && Prefixes.TryGetValue(symbol[0], out var ownPrefix)
                && custom.TryGetValue(symbol.Substring(1), out var prefixable) && prefixable.Prefixed && prefixable.Symbol == symbol.Substring(1))
                return new Unit(prefixable.DimensionTable, ownPrefix * prefixable.Scale, symbol);

            // 3) Unknown -> atomic unit, compatible only with itself.
            var atomicDims = new Dictionary<string, int> { [symbol] = 1 };
            return new Unit(atomicDims, 1.0, symbol);
        }

        // ---------------------------------------------------------------
        // Units of the program (`unit Inch: in { coerce m => 0.0254 }`, SPEC "Own units")
        // ---------------------------------------------------------------
        // Per thread: a compilation reads the declarations and uses them on its thread (several programs may be compiled at the same time); a running
        // program needs none of them (its declarations carry their units, see DemandText).
        [ThreadStatic] private static Dictionary<string, CustomUnit>? _customUnitTable;
        [ThreadStatic] private static List<CustomUnit>? _customUnitsOfThread;
        private static Dictionary<string, CustomUnit> _customUnits => _customUnitTable ??= new();
        private static List<CustomUnit> _customUnitList => _customUnitsOfThread ??= new();

        /// <summary>The units the program declares (in the order of their declarations).</summary>
        public static IReadOnlyList<CustomUnit> CustomUnits => _customUnitList;

        /// <summary>Makes <paramref name="units"/> the units of the program (the compiler after reading the declarations, the VM before it runs a
        /// compiled program): from now on `Parse` knows their symbols, names and - for a prefixed one - the prefixed symbols. Replaces the units of the
        /// program before.</summary>
        public static void UseCustomUnits(IReadOnlyList<CustomUnit>? units)
        {
            units ??= Array.Empty<CustomUnit>();
            if (units.Count == 0 && _customUnitList.Count == 0) return;
            if (units.SequenceEqual(_customUnitList)) return;
            var table = new Dictionary<string, CustomUnit>();
            foreach (var u in units)
            {
                table[u.Symbol] = u;
                if (u.Name != u.Symbol) table.TryAdd(u.Name, u);
            }
            _customUnitsOfThread = units.ToList();
            _customUnitTable = table;
        }

        private const char DemandMark = '§';

        /// <summary>The text of a unit that a declaration demands, as the compiled program keeps it (TypeRef.UnitDemand): unchanged for the units of fire;
        /// a unit of the program becomes a text that says its symbol, dimensions and factor (`§in§m:1§0.0254`), so that the program does not need the
        /// declarations of its units when it runs (another program compiled in the meantime may have other ones).</summary>
        public static string DemandText(string unitText)
        {
            if (unitText.StartsWith("any ", StringComparison.Ordinal)) return "any " + DemandText(unitText.Substring(4));
            if (_customUnitList.Count == 0 || unitText.Length == 0 || unitText[0] == DemandMark) return unitText;
            if (!unitText.Split(CombiningChars).Any(IsCustomUnitWord)) return unitText;
            var u = Parse(unitText);
            string dims = string.Join(",", u.Dimensions.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => d.Key + ":" + d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return $"{DemandMark}{u}{DemandMark}{dims}{DemandMark}{u.Scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}{DemandMark}{u.Offset.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}{DemandMark}{u.Converter}{DemandMark}{u.UnitlessConverter}";
        }

        /// <summary>The unit of a demand as the user wrote it (for messages): `#§in§m:1§0.0254` is `in`, `any §in§...` is `any in`.</summary>
        public static string DemandDisplay(string demand)
        {
            demand = demand.TrimStart('#');
            if (demand.StartsWith("any ", StringComparison.Ordinal)) return "any " + DemandDisplay(demand.Substring(4));
            return demand.Length > 0 && demand[0] == DemandMark ? demand.Split(DemandMark)[1] : demand;
        }

        private static Unit DecodeDemand(string text)
        {
            var parts = text.Split(DemandMark);   // "", symbol, dimensions, scale, offset, converter, unitless converter
            var dims = new Dictionary<string, int>();
            foreach (var entry in parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = entry.LastIndexOf(':');
                dims[entry.Substring(0, colon)] = int.Parse(entry.Substring(colon + 1), System.Globalization.CultureInfo.InvariantCulture);
            }
            return new Unit(dims, double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture), parts[1],
                double.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture), parts[5].Length == 0 ? null : parts[5],
                parts.Length > 6 && parts[6].Length > 0 ? parts[6] : null);
        }

        /// <summary>A unit symbol that only a unit of the program defines (a word the lexer reads as a keyword, `in`, can still be a unit then).</summary>
        public static bool IsCustomUnitWord(string word) =>
            _customUnits.ContainsKey(word)
            || word.Length >= 2 && Prefixes.ContainsKey(word[0]) && _customUnits.TryGetValue(word.Substring(1), out var u) && u.Prefixed && u.Symbol == word.Substring(1);

        /// <summary>What `Parse` makes of a word without the units of the program: null for an unknown word (an atomic unit), else the built-in unit
        /// (`m`, `km`, `min`) - a unit of the program must not take its place.</summary>
        public static string? BuiltInMeaning(string word)
        {
            if (NamedUnits.ContainsKey(word)) return word;
            if (word.Length >= 2 && Prefixes.ContainsKey(word[0]) && NamedUnits.ContainsKey(word.Substring(1)) && PrefixableBaseUnits.Contains(word.Substring(1)))
                return $"the prefix '{word[0]}' in front of '{word.Substring(1)}'";
            return null;
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
            if (!IsLinear || !target.IsLinear)
                throw new InvalidOperationException($"'{this}' and '{target}' are not converted by a factor (one of them has an offset or conversion functions).");
            if (ReferenceEquals(target, this)) return 1.0;
            var cache = _factorCache;
            if (cache != null && ReferenceEquals(cache.Target, target)) return cache.Factor;
            double factor = NiceFactor(Scale / target.Scale);
            _factorCache = new FactorCache(target, factor);
            return factor;
        }

        private sealed record FactorCache(Unit Target, double Factor, double Shift = 0);

        /// <summary>Calls the conversion function `method` (`To` or `From`) of the hidden class of a unit (see Converter) - set by the VM.</summary>
        public static Func<string, string, double, double>? ConverterCall;

        /// <summary>A number in this unit converted into <paramref name="target"/> (the same dimension): by the factor for units that are only one; with an
        /// offset (`20C` is `68F`) and conversion functions it goes through the reference units: to(value) * scale + offset is the base value, and
        /// back. The factor and the shift between two units have no rounding error of their own (NiceFactor).</summary>
        public double Convert(double value, Unit target)
        {
            if (ReferenceEquals(target, this)) return value;
            if (IsLinear && target.IsLinear) return value * ConversionFactorTo(target);
            if (!IsCompatibleWith(target)) throw new UnitMismatchException(this, target);
            var cache = _factorCache;
            if (cache == null || !ReferenceEquals(cache.Target, target))
            {
                double shift = (Offset - target.Offset) / target.Scale;
                cache = new FactorCache(target, NiceFactor(Scale / target.Scale), shift == 0 ? 0 : NiceFactor(shift));
                _factorCache = cache;
            }
            double x = Converter != null ? CallConverter(Converter, "To", value) : value;
            double scaled = x * cache.Factor, y = scaled + cache.Shift;
            if (Math.Abs(y) <= 1e-12 * Math.Max(Math.Abs(scaled), Math.Abs(cache.Shift))) y = 0;   // (32F is 0C, not 5.7E-14C)
            return target.Converter != null ? CallConverter(target.Converter, "From", y) : y;
        }

        /// <summary>The number in the base unit (`5mm` is 0.005, `20C` is 293.15) - for `x:unitless`.</summary>
        public double ToBase(double value) => UnitlessConverter != null ? CallConverter(UnitlessConverter, "To", value)
            : (Converter != null ? CallConverter(Converter, "To", value) : value) * Scale + Offset;

        private static double CallConverter(string converter, string method, double value) =>
            (ConverterCall ?? throw new InvalidOperationException("The units of this program need the VM to be converted."))(converter, method, value);

        /// <summary>The unit that the product with another unit has (`20C * 2s`, `10dB / 1m`): a unit with an offset or conversion functions counts as its
        /// reference unit there (a difference: the offset and the functions do not take part).</summary>
        [MemoryPackIgnore]
        public Unit LinearPart => IsLinear ? this : new Unit(Dimensions, Scale, null);
        [MemoryPackIgnore] private FactorCache? _factorCache;

        /// <summary>The factor between two units without the rounding error of the scales: they are binary approximations of decimal numbers, so `0.3048 /
        /// 0.0254` is 12.000000000000002 and `0.001 / 0.01` 0.09999999999999999. A factor a few units in the last place away from a number with 12
        /// significant digits is that number (12, 0.1); any other one (1/60, 1/0.0254) stays as it is. fire_rt.hpp unitNiceFactor does the same.</summary>
        public static double NiceFactor(double factor)
        {
            double nice = double.Parse(factor.ToString("G12", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
            return Math.Abs(nice - factor) <= 8 * 2.220446049250313e-16 * Math.Abs(factor) ? nice : factor;
        }

        private static readonly char[] CombiningChars = { '/', '*', '^' };

        private static Unit ParseCombined(string symbol)
        {
            var result = Unitless;
            bool divide = false;
            int i = 0;
            while (i < symbol.Length)
            {
                int start = i;
                while (i < symbol.Length && symbol[i] is not ('/' or '*' or '^')) i++;
                var factor = Parse(symbol.Substring(start, i - start));
                int power = 1;
                if (i < symbol.Length && symbol[i] == '^')
                {
                    int numberStart = ++i;
                    if (i < symbol.Length && symbol[i] == '-') i++;
                    while (i < symbol.Length && char.IsDigit(symbol[i])) i++;
                    power = int.Parse(symbol.Substring(numberStart, i - numberStart), System.Globalization.CultureInfo.InvariantCulture);
                }
                for (int p = 0; p < Math.Abs(power); p++)
                    result = divide ^ power < 0 ? Divide(result, factor) : Multiply(result, factor);
                if (i < symbol.Length)
                {
                    if (symbol[i] == '/') divide = true;
                    i++;
                }
            }
            return new Unit(new Dictionary<string, int>(result.Dimensions), result.Scale, symbol);
        }

        /// <summary>The square root of the unit (`m^2` → `m`, `cm^2` → `cm`, `m^2/s^2` → `m/s`); null when an exponent is odd (`m` has no root). A display
        /// symbol that ends in `^2` loses it, otherwise the root has none.</summary>
        public Unit? Sqrt()
        {
            if (IsUnitless) return this;
            if (!IsLinear) return LinearPart.Sqrt();
            var dims = new Dictionary<string, int>();
            foreach (var (key, exp) in Dimensions)
            {
                if (exp % 2 != 0) return null;
                dims[key] = exp / 2;
            }
            string? display = _displaySymbol is { } d && d.EndsWith("^2", StringComparison.Ordinal) && d.IndexOfAny(CombiningChars, 0, d.Length - 2) < 0 ? d[..^2] : null;
            return new Unit(dims, Math.Sqrt(Scale), display);
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
            // a unit that becomes a plain number (dB) takes part as that number
            if (a.UnitlessConverter != null) a = Unitless;
            if (b.UnitlessConverter != null) b = Unitless;
            if (b.IsUnitless) return a;
            if (a.IsUnitless) return b;
            a = a.LinearPart;
            b = b.LinearPart;

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
            if (a.UnitlessConverter != null) a = Unitless;
            if (b.UnitlessConverter != null) b = Unitless;
            if (b.IsUnitless) return a;
            a = a.LinearPart;
            b = b.LinearPart;

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
            return Math.Abs(Scale - other.Scale) < 1e-12 && Offset == other.Offset && Converter == other.Converter && UnitlessConverter == other.UnitlessConverter;
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

    /// <summary>A unit the program declares: `prefixed unit Volt: V` (a new dimension `V`, `mV`, `kV`, ...) or `unit Inch: in { coerce m => 0.0254 }`
    /// (the dimension of `m`, 1in = 0.0254m). <see cref="Dimensions"/> and <see cref="Scale"/> are those of a parsed unit (value_in_base = value * Scale).</summary>
    [MemoryPackable]
    public sealed partial record CustomUnit(string Name, string Symbol, bool Prefixed, Dictionary<string, int> Dimensions, double Scale, double Offset = 0, string? Converter = null,
        string? UnitlessConverter = null)
    {
        [MemoryPackIgnore]
        internal IReadOnlyDictionary<string, int> DimensionTable => Dimensions;

        public bool Equals(CustomUnit? other) =>
            other is not null && Name == other.Name && Symbol == other.Symbol && Prefixed == other.Prefixed && Scale.Equals(other.Scale) && Offset.Equals(other.Offset) && Converter == other.Converter
            && UnitlessConverter == other.UnitlessConverter && Dimensions.Count == other.Dimensions.Count && Dimensions.All(d => other.Dimensions.TryGetValue(d.Key, out int e) && e == d.Value);

        public override int GetHashCode() => HashCode.Combine(Name, Symbol, Prefixed, Scale);
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
