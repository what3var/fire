using System;
using fire.Values;

namespace fire.Standard
{
    /// <summary>The functions of the class `Math` that need the host (IDs for <see cref="MathMethods.NativeName"/>, fixed values - never renumber).</summary>
    public enum MathMethod
    {
        Sqrt = 1,
        Round = 2,
        Floor = 3,
        Ceiling = 4,
        Sin = 5,
        Cos = 6,
        Tan = 7,
        Asin = 8,
        Acos = 9,
        Atan = 10,
        Atan2 = 11,
        Log = 12,
        Log10 = 13,
        Exp = 14,
    }

    /// <summary>
    /// The class `Math` of the prelude (always there): `Math.Sqrt(x)`, `Math.Abs(x)`, `Math.Min(a, b)`, ..., `Math.PI`. Functions that keep the number as it is
    /// (`Abs`, `Min`, `Max`, `Clamp`, `Sign`) are written in fire; the others go through ONE native function `__MathCall(id, x[, y])`, which the native backend
    /// implements alike (fire_rt.hpp: mathCall).
    ///
    /// Units: `Sqrt` takes the root of the unit too (`Math.Sqrt(9m * 1m)` is `3m`; a unit without a root - `m` - is an `ArgumentException`), `Round`/`Floor`/
    /// `Ceiling` keep it (`Math.Round(2.6m)` is `3m`), an int stays an int. The angle and exponential functions take a number without unit (radians) and give a
    /// float; `Atan2(y, x)` takes two values of the same dimension (`y` is converted into the unit of `x`). A wrong argument is an InvalidOperationException,
    /// which the VM throws as a catchable `ArgumentException`.
    /// </summary>
    public static class MathMethods
    {
        public const string NativeName = "__MathCall";

        public static string PreludeSource { get; } = $$"""

            /// Mathematical functions and constants (always available).
            class Math {
                /// The circle number π.
                static float PI = 3.141592653589793
                /// Euler's number e.
                static float E = 2.718281828459045
                /// The square root; of a value with a unit also the root of the unit (`Math.Sqrt(16m * 1m)` is `4m`).
                static any Sqrt(any x) { return {{NativeName}}({{(int)MathMethod.Sqrt}}, x) }
                /// The value without its sign (the unit stays).
                static any Abs(any x) { if (x < x - x) { return -x } return x }
                /// The smaller of two values (units are converted for the comparison).
                static any Min(any a, any b) { if (b < a) { return b } return a }
                /// The larger of two values.
                static any Max(any a, any b) { if (b > a) { return b } return a }
                /// `x` limited to `low`...`high`.
                static any Clamp(any x, any low, any high) { if (x < low) { return low } if (x > high) { return high } return x }
                /// -1, 0 or 1 for a negative value, zero or a positive value.
                static int Sign(any x) { if (x < x - x) { return -1 } if (x > x - x) { return 1 } return 0 }
                /// Rounded to the nearest whole number (halves away from zero); the unit stays, an int stays an int.
                static any Round(any x) { return {{NativeName}}({{(int)MathMethod.Round}}, x) }
                /// The largest whole number that is not larger.
                static any Floor(any x) { return {{NativeName}}({{(int)MathMethod.Floor}}, x) }
                /// The smallest whole number that is not smaller.
                static any Ceiling(any x) { return {{NativeName}}({{(int)MathMethod.Ceiling}}, x) }
                /// The sine of an angle in radians.
                static float Sin(any x) { return {{NativeName}}({{(int)MathMethod.Sin}}, x) }
                /// The cosine of an angle in radians.
                static float Cos(any x) { return {{NativeName}}({{(int)MathMethod.Cos}}, x) }
                /// The tangent of an angle in radians.
                static float Tan(any x) { return {{NativeName}}({{(int)MathMethod.Tan}}, x) }
                /// The angle (radians) whose sine is `x`.
                static float Asin(any x) { return {{NativeName}}({{(int)MathMethod.Asin}}, x) }
                /// The angle (radians) whose cosine is `x`.
                static float Acos(any x) { return {{NativeName}}({{(int)MathMethod.Acos}}, x) }
                /// The angle (radians) whose tangent is `x`.
                static float Atan(any x) { return {{NativeName}}({{(int)MathMethod.Atan}}, x) }
                /// The angle (radians) of the point (x, y): -π ... π.
                static float Atan2(any y, any x) { return {{NativeName}}({{(int)MathMethod.Atan2}}, y, x) }
                /// The natural logarithm.
                static float Log(any x) { return {{NativeName}}({{(int)MathMethod.Log}}, x) }
                /// The logarithm to base 10.
                static float Log10(any x) { return {{NativeName}}({{(int)MathMethod.Log10}}, x) }
                /// e to the power of `x`.
                static float Exp(any x) { return {{NativeName}}({{(int)MathMethod.Exp}}, x) }
            }

            """;

        /// <summary>The native function `__MathCall(id, x[, y])`.</summary>
        public static Value Call(Value[] args)
        {
            if (args.Length is < 2 or > 3) throw new InvalidOperationException($"{NativeName}(id, x[, y]) expects one or two values.");
            var method = (MathMethod)args[0].AsInt();
            var x = args[1];
            if (x.Kind is not (ValueKind.Int or ValueKind.Float))
                throw new InvalidOperationException($"Math.{method} expects a number, not a value of type '{x.Kind}'.");
            double v = x.Kind == ValueKind.Int ? x.AsInt() : x.AsFloat();
            var unit = x.Unit ?? Unit.Unitless;
            switch (method)
            {
                case MathMethod.Sqrt:
                {
                    var root = unit.Sqrt() ?? throw new InvalidOperationException($"Math.Sqrt: the unit '{unit}' has no square root.");
                    return Value.MakeFloat(Math.Sqrt(v), root.IsUnitless ? null : root);
                }
                case MathMethod.Round: case MathMethod.Floor: case MathMethod.Ceiling:
                {
                    if (x.Kind == ValueKind.Int) return x;
                    double r = method == MathMethod.Round ? Math.Round(v, MidpointRounding.AwayFromZero) : method == MathMethod.Floor ? Math.Floor(v) : Math.Ceiling(v);
                    return Value.MakeFloat(r, x.Unit);
                }
                case MathMethod.Atan2:
                {
                    var other = args.Length == 3 ? args[2] : Value.MakeUndefined();
                    if (other.Kind is not (ValueKind.Int or ValueKind.Float))
                        throw new InvalidOperationException($"Math.Atan2 expects two numbers, not a value of type '{other.Kind}'.");
                    var otherUnit = other.Unit ?? Unit.Unitless;
                    if (!unit.IsCompatibleWith(otherUnit))
                        throw new InvalidOperationException($"Math.Atan2 expects two values of the same dimension, not '{unit}' and '{otherUnit}'.");
                    double xv = other.Kind == ValueKind.Int ? other.AsInt() : other.AsFloat();
                    double yv = unit.Convert(v, otherUnit);
                    return Value.MakeFloat(Math.Atan2(yv, xv));
                }
            }
            if (!unit.IsUnitless)
                throw new InvalidOperationException($"Math.{method} expects a number without unit, not one in '{unit}'.");
            return Value.MakeFloat(method switch
            {
                MathMethod.Sin => Math.Sin(v),
                MathMethod.Cos => Math.Cos(v),
                MathMethod.Tan => Math.Tan(v),
                MathMethod.Asin => Math.Asin(v),
                MathMethod.Acos => Math.Acos(v),
                MathMethod.Atan => Math.Atan(v),
                MathMethod.Log => Math.Log(v),
                MathMethod.Log10 => Math.Log10(v),
                MathMethod.Exp => Math.Exp(v),
                _ => throw new InvalidOperationException($"{NativeName}: unknown function {(int)method}."),
            });
        }
    }
}
