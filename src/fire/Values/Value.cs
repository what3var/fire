using System;
using System.Globalization;

namespace fire.Values
{
    /// <summary>
    /// Ein Laufzeitwert. bool/char/string sind reine Value-Types ohne Einheit.
    /// int/float/undefined tragen zusätzlich eine Unit (Default: Unitless).
    /// class ist eine Referenz auf eine Objektinstanz (Ownership-Modell,
    /// siehe Runtime-Schicht - noch nicht Teil dieses Meilensteins).
    /// </summary>
    public readonly struct Value : IEquatable<Value>
    {
        public ValueKind Kind { get; }
        public Unit? Unit { get; }

        /// <summary>Nur für Int/Float relevant (SPEC "APIs & Bitbreiten"). Default
        /// ist immer die höchste Genauigkeit (W64).</summary>
        public NumericWidth Width { get; }

        private readonly long _intValue;
        private readonly double _floatValue;
        private readonly bool _boolValue;
        private readonly char _charValue;
        private readonly string? _stringValue;
        private readonly object? _objectRef; // Referenz auf ObjectInstance (Runtime-Schicht)

        private Value(ValueKind kind, Unit? unit, long i, double f, bool b, char c, string? s, object? obj,
            NumericWidth width = NumericWidth.W64)
        {
            Kind = kind;
            Unit = unit;
            _intValue = i;
            _floatValue = f;
            _boolValue = b;
            _charValue = c;
            _stringValue = s;
            _objectRef = obj;
            Width = width;
        }

        // ---------------------------------------------------------------
        // Factories
        // ---------------------------------------------------------------
        public static Value MakeBool(bool value) =>
            new(ValueKind.Bool, null, 0, 0, value, '\0', null, null);

        public static Value MakeInt(long value, Unit? unit = null, NumericWidth width = NumericWidth.W64) =>
            new(ValueKind.Int, unit ?? Values.Unit.Unitless, value, 0, false, '\0', null, null, width);

        public static Value MakeFloat(double value, Unit? unit = null, NumericWidth width = NumericWidth.W64) =>
            new(ValueKind.Float, unit ?? Values.Unit.Unitless, 0, value, false, '\0', null, null, width);

        public static Value MakeChar(char value) =>
            new(ValueKind.Char, null, 0, 0, false, value, null, null);

        public static Value MakeString(string value) =>
            new(ValueKind.String, null, 0, 0, false, '\0', value, null);

        public static Value MakeUndefined(Unit? unit = null) =>
            new(ValueKind.Undefined, unit ?? Values.Unit.Unitless, 0, 0, false, '\0', null, null);

        public static Value MakeClassRef(object objectInstance) =>
            new(ValueKind.Class, null, 0, 0, false, '\0', null, objectInstance);

        // Lose typisiert (object) aus demselben Grund wie MakeClassRef: Values
        // bleibt unabhängig von der Runtime-Schicht (Runtime.LambdaValue), die
        // Runtime-Schicht hängt von Values ab, nicht umgekehrt.
        public static Value MakeLambda(object lambdaValue) =>
            new(ValueKind.Lambda, null, 0, 0, false, '\0', null, lambdaValue);

        public static Value MakePointer(PointerTarget target) =>
            new(ValueKind.Pointer, null, 0, 0, false, '\0', null, target);

        public static Value MakeArray(ScriptArray array) =>
            new(ValueKind.Array, null, 0, 0, false, '\0', null, array);

        public static Value MakeBuffer(ByteBuffer buffer) =>
            new(ValueKind.Buffer, null, 0, 0, false, '\0', null, buffer);

        // ---------------------------------------------------------------
        // Accessors (werfen bei falschem Kind)
        // ---------------------------------------------------------------
        public bool AsBool()
        {
            RequireKind(ValueKind.Bool);
            return _boolValue;
        }

        public long AsInt()
        {
            RequireKind(ValueKind.Int);
            return _intValue;
        }

        public double AsFloat()
        {
            RequireKind(ValueKind.Float);
            return _floatValue;
        }

        public char AsChar()
        {
            RequireKind(ValueKind.Char);
            return _charValue;
        }

        public string AsString()
        {
            RequireKind(ValueKind.String);
            return _stringValue!;
        }

        public object AsObjectRef()
        {
            RequireKind(ValueKind.Class);
            return _objectRef!;
        }

        public object AsLambda()
        {
            RequireKind(ValueKind.Lambda);
            return _objectRef!;
        }

        public PointerTarget AsPointer()
        {
            RequireKind(ValueKind.Pointer);
            return (PointerTarget)_objectRef!;
        }

        public ScriptArray AsArray()
        {
            RequireKind(ValueKind.Array);
            return (ScriptArray)_objectRef!;
        }

        public ByteBuffer AsBuffer()
        {
            RequireKind(ValueKind.Buffer);
            return (ByteBuffer)_objectRef!;
        }

        /// <summary>Interne Invariante, keine behandelbare Laufzeitbedingung
        /// (siehe VmInvariantViolationException-Doku - Zugriff mit falschem
        /// Kind bedeutet "der Compiler/Resolver hat einen Bug", nicht
        /// "das Skript hat einen ungültigen Zustand erreicht").</summary>
        private void RequireKind(ValueKind expected)
        {
            if (Kind != expected)
                throw new VmInvariantViolationException($"Value ist vom Typ {Kind}, nicht {expected}.");
        }

        // ---------------------------------------------------------------
        // Coercion
        // ---------------------------------------------------------------

        /// <summary>Rechnet einen Int/Float/Undefined-Wert in eine andere (kompatible)
        /// Einheit um. Wirft UnitMismatchException bei inkompatibler Dimension.</summary>
        public Value CoerceUnit(Unit targetUnit)
        {
            if (Kind != ValueKind.Int && Kind != ValueKind.Float && Kind != ValueKind.Undefined)
                throw new InvalidOperationException($"Typ {Kind} trägt keine Einheit und kann nicht umgerechnet werden.");

            var currentUnit = Unit ?? Values.Unit.Unitless;
            if (currentUnit.Equals(targetUnit))
                return this;

            double factor = currentUnit.ConversionFactorTo(targetUnit);

            return Kind switch
            {
                ValueKind.Int => MakeInt((long)Math.Round(_intValue * factor), targetUnit),
                ValueKind.Float => MakeFloat(_floatValue * factor, targetUnit),
                ValueKind.Undefined => MakeUndefined(targetUnit),
                _ => throw new InvalidOperationException("unreachable"),
            };
        }

        /// <summary>Erzwingt den Zieltyp (aktuell: int/float untereinander, sowie
        /// triviale Identität). Weitere Typkombinationen folgen mit dem Evaluator.</summary>
        public Value CoerceType(ValueKind targetKind)
        {
            if (Kind == targetKind) return this;

            switch (Kind, targetKind)
            {
                case (ValueKind.Int, ValueKind.Float):
                    return MakeFloat(_intValue, Unit);
                case (ValueKind.Float, ValueKind.Int):
                    return MakeInt((long)Math.Round(_floatValue), Unit);
                case (ValueKind.Undefined, ValueKind.Int):
                    return MakeInt(0, Unit);
                case (ValueKind.Undefined, ValueKind.Float):
                    return MakeFloat(0, Unit);
                default:
                    throw new InvalidOperationException(
                        $"Kann Typ {Kind} nicht nach {targetKind} coercen.");
            }
        }

        // ---------------------------------------------------------------
        // Arithmetik (einfache Fälle mit exakt gleicher Einheit; die
        // "Anker-Regel" für automatische Ziel-Einheiten bei gemischten
        // Operanden lebt im Evaluator, da sie Syntax-Info (":"/"!" im
        // Ausdruck) benötigt, die dem Value selbst nicht vorliegt.)
        // ---------------------------------------------------------------
        public static Value Add(Value a, Value b)
        {
            if (a.Kind == ValueKind.Pointer && b.Kind == ValueKind.Int)
                return a.OffsetPointer(b._intValue);

            // String-Konkatenation: sobald EINE Seite ein String ist, wird die
            // ANDERE Seite über ihre normale ToString()-Darstellung angehängt
            // (deckt also auch "text " + 42 oder 42 + " text" ab, nicht nur
            // String + String) - das ist das erwartete Verhalten für '+' in
            // einer Skriptsprache und entspricht ToString()'s bereits
            // vorhandener Kind-übergreifender Darstellung.
            if (a.Kind == ValueKind.String || b.Kind == ValueKind.String)
                return MakeString(a.ToString() + b.ToString());

            RequireNumeric(a); RequireNumeric(b);
            RequireSameUnit(a, b);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() + b.ToDouble(), a.Unit);
            return MakeInt(a._intValue + b._intValue, a.Unit);
        }

        public static Value Subtract(Value a, Value b)
        {
            if (a.Kind == ValueKind.Pointer && b.Kind == ValueKind.Int)
                return a.OffsetPointer(-b._intValue);
            if (a.Kind == ValueKind.Pointer && b.Kind == ValueKind.Pointer)
                throw new InvalidOperationException(
                    "Pointer-Differenz ('ptr1 - ptr2') wird aktuell nicht unterstützt.");

            RequireNumeric(a); RequireNumeric(b);
            RequireSameUnit(a, b);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() - b.ToDouble(), a.Unit);
            return MakeInt(a._intValue - b._intValue, a.Unit);
        }

        /// <summary>Pointer-Arithmetik: "N Elemente weiter" - siehe
        /// PointerTarget.Advance (bei Scope-Slots ein logischer Schritt in der
        /// zusammenhängenden Slot-Liste, bei Objekt-Feldern nur bei Offset 0
        /// gültig).</summary>
        private Value OffsetPointer(long elementOffset)
        {
            var target = AsPointer();
            var moved = target.Advance(elementOffset)
                ?? throw new InvalidOperationException("Pointer-Arithmetik außerhalb eines gültigen Bereichs.");
            return MakePointer(moved);
        }

        public static Value Modulo(Value a, Value b)
        {
            RequireNumeric(a); RequireNumeric(b);
            RequireSameUnit(a, b);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() % b.ToDouble(), a.Unit);
            return MakeInt(a._intValue % b._intValue, a.Unit);
        }

        public static Value Divide(Value a, Value b)
        {
            RequireNumeric(a); RequireNumeric(b);
            var resultUnit = Values.Unit.Divide(a.Unit ?? Values.Unit.Unitless, b.Unit ?? Values.Unit.Unitless);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() / b.ToDouble(), resultUnit);
            return MakeInt(a._intValue / b._intValue, resultUnit);
        }

        public static Value Multiply(Value a, Value b)
        {
            RequireNumeric(a); RequireNumeric(b);
            var resultUnit = Values.Unit.Multiply(a.Unit ?? Values.Unit.Unitless, b.Unit ?? Values.Unit.Unitless);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() * b.ToDouble(), resultUnit);
            return MakeInt(a._intValue * b._intValue, resultUnit);
        }

        private double ToDouble() => Kind == ValueKind.Float ? _floatValue : _intValue;

        public static Value Negate(Value v)
        {
            RequireNumeric(v);
            return v.Kind == ValueKind.Float ? MakeFloat(-v._floatValue, v.Unit) : MakeInt(-v._intValue, v.Unit);
        }

        public static Value LogicalNot(Value v)
        {
            if (v.Kind != ValueKind.Bool)
                throw new InvalidOperationException($"'!' (Negation) erwartet bool, nicht {v.Kind}.");
            return MakeBool(!v._boolValue);
        }

        public static Value BitNot(Value v)
        {
            if (v.Kind != ValueKind.Int)
                throw new InvalidOperationException($"'~' erwartet int, nicht {v.Kind}.");
            return MakeInt(~v._intValue, v.Unit);
        }

        private static void RequireInt(Value v, string opSymbol)
        {
            if (v.Kind != ValueKind.Int)
                throw new InvalidOperationException($"'{opSymbol}' erwartet int, nicht {v.Kind}.");
        }

        public static Value BitAnd(Value a, Value b)
        {
            RequireInt(a, "&"); RequireInt(b, "&");
            RequireSameUnit(a, b);
            return MakeInt(a._intValue & b._intValue, a.Unit);
        }

        public static Value BitOr(Value a, Value b)
        {
            RequireInt(a, "|"); RequireInt(b, "|");
            RequireSameUnit(a, b);
            return MakeInt(a._intValue | b._intValue, a.Unit);
        }

        public static Value BitXor(Value a, Value b)
        {
            RequireInt(a, "#"); RequireInt(b, "#");
            RequireSameUnit(a, b);
            return MakeInt(a._intValue ^ b._intValue, a.Unit);
        }

        /// <summary>`<<`/`>>` - der RECHTE Operand (die Schiebeweite) ist bewusst
        /// von JEDER Einheiten-Prüfung ausgenommen (anders als bei `&`/`|`/`#`,
        /// die `RequireSameUnit` durchsetzen): eine Schiebeweite ist eine reine
        /// Zählgröße ("um wie viele Bits"), keine Größe, die sinnvoll eine
        /// eigene Einheit tragen könnte - das Ergebnis übernimmt die Einheit
        /// des LINKEN Operanden unverändert, wie bei `~`.</summary>
        public static Value ShiftLeft(Value a, Value b)
        {
            RequireInt(a, "<<"); RequireInt(b, "<<");
            return MakeInt(a._intValue << (int)b._intValue, a.Unit);
        }

        public static Value ShiftRight(Value a, Value b)
        {
            RequireInt(a, ">>"); RequireInt(b, ">>");
            return MakeInt(a._intValue >> (int)b._intValue, a.Unit);
        }

        /// <summary>`^` (Potenz, NICHT bitweises XOR - das ist `#`, siehe BitXor).
        /// Ganzzahlig-schnelle Exponentiation (wiederholte Multiplikation) für
        /// `int^int` mit nicht-negativem Exponenten (Ergebnis bleibt `int`,
        /// wie bei `+`/`-`/`*`); sobald EIN Operand `float` ist oder der
        /// Exponent negativ ist, wird über `Math.Pow` (float-Ergebnis)
        /// gerechnet - ein negativer Exponent ergibt bei `int^int` sonst nur
        /// 0 (Ganzzahl-Rundung von Werten &lt;1), was kaum je gemeint ist.</summary>
        public static Value Power(Value a, Value b)
        {
            RequireNumeric(a); RequireNumeric(b);
            RequireSameUnit(a, b);

            bool useFloat = a.Kind == ValueKind.Float || b.Kind == ValueKind.Float
                || (b.Kind == ValueKind.Int && b._intValue < 0);
            if (useFloat)
                return MakeFloat(System.Math.Pow(a.ToDouble(), b.ToDouble()), a.Unit);

            long result = 1;
            long baseValue = a._intValue;
            long exponent = b._intValue;
            for (long i = 0; i < exponent; i++)
                result *= baseValue;
            return MakeInt(result, a.Unit);
        }

        /// <summary>Schneidet einen Int/Float-Wert auf die angegebene Bitbreite
        /// zu ("beim Kopieren von groß nach klein abgeschnitten"). Der interne
        /// Speicher bleibt immer long/double (volle Breite); TruncateTo wendet
        /// nur den durch die Zielbreite erzwungenen Wertebereich/die Präzision
        /// an und markiert das Ergebnis mit der neuen Width.</summary>
        public Value TruncateTo(NumericWidth width)
        {
            if (Kind == ValueKind.Int)
            {
                long truncated = width switch
                {
                    NumericWidth.W8 => (sbyte)_intValue,
                    NumericWidth.W16 => (short)_intValue,
                    NumericWidth.W32 => (int)_intValue,
                    _ => _intValue,
                };
                return MakeInt(truncated, Unit, width);
            }

            if (Kind == ValueKind.Float)
            {
                double truncated = width switch
                {
                    NumericWidth.W64 => _floatValue,
                    NumericWidth.W32 => (float)_floatValue,
                    NumericWidth.W16 => (double)(Half)_floatValue,
                    NumericWidth.W8 => Minifloat8ToDouble(Minifloat8FromDouble(_floatValue)),
                    _ => _floatValue,
                };
                return MakeFloat(truncated, Unit, width);
            }

            throw new InvalidOperationException($"TruncateTo ist nur für int/float gültig, nicht {Kind}.");
        }

        // 8-Bit-Minifloat: 1 Vorzeichen- + 4 Exponenten- (Bias 7) + 3 Mantissenbits
        // ("E4M3"-artig). Bewusst simple, dokumentierte Wahl - siehe NumericWidth.
        private static byte Minifloat8FromDouble(double value)
        {
            if (double.IsNaN(value)) return 0x7F;
            int sign = value < 0 ? 1 : 0;
            double abs = Math.Abs(value);
            if (abs == 0) return (byte)(sign << 7);

            int exp = (int)Math.Floor(Math.Log2(abs));
            double mantissaF = abs / Math.Pow(2, exp) - 1.0;
            exp += 7; // Bias
            if (exp <= 0) return (byte)(sign << 7); // Unterlauf -> 0
            if (exp >= 15) return (byte)((sign << 7) | (0xF << 3) | 0x7); // Überlauf -> größter Wert

            int mantissa = (int)Math.Round(mantissaF * 8) & 0x7;
            return (byte)((sign << 7) | (exp << 3) | mantissa);
        }

        private static double Minifloat8ToDouble(byte bits)
        {
            int sign = (bits >> 7) & 1;
            int exp = (bits >> 3) & 0xF;
            int mantissa = bits & 0x7;
            if (exp == 0 && mantissa == 0) return sign == 1 ? -0.0 : 0.0;
            double value = (1.0 + mantissa / 8.0) * Math.Pow(2, exp - 7);
            return sign == 1 ? -value : value;
        }

        /// <summary>Strukturelle Gleichheit für '=='/'!=' - funktioniert kindübergreifend
        /// (z.B. Bool vs Int liefert einfach false, kein Fehler).</summary>
        public static bool ValuesEqual(Value a, Value b) => a.Equals(b);

        /// <summary>Für '&lt;'/'&lt;='/'&gt;'/'&gt;=' - nur für numerische Werte mit
        /// übereinstimmender Einheit (wie Add/Subtract).</summary>
        public static int Compare(Value a, Value b)
        {
            RequireNumeric(a); RequireNumeric(b);
            RequireSameUnit(a, b);
            return a.ToDouble().CompareTo(b.ToDouble());
        }

        private static void RequireNumeric(Value v)
        {
            if (v.Kind != ValueKind.Int && v.Kind != ValueKind.Float)
                throw new InvalidOperationException($"Typ {v.Kind} ist nicht numerisch.");
        }

        private static void RequireSameUnit(Value a, Value b)
        {
            var ua = a.Unit ?? Values.Unit.Unitless;
            var ub = b.Unit ?? Values.Unit.Unitless;
            if (!ua.Equals(ub))
                throw new UnitMismatchException(ua, ub);
        }

        // ---------------------------------------------------------------
        public bool Equals(Value other) => Kind == other.Kind
            && _intValue == other._intValue
            && _floatValue.Equals(other._floatValue)
            && _boolValue == other._boolValue
            && _charValue == other._charValue
            && _stringValue == other._stringValue
            && Equals(_objectRef, other._objectRef);

        public override bool Equals(object? obj) => obj is Value v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(Kind, _intValue, _floatValue, _boolValue, _charValue, _stringValue, _objectRef);

        public override string ToString() => Kind switch
        {
            ValueKind.Bool => _boolValue.ToString(),
            ValueKind.Int => Unit is { IsUnitless: false } u ? $"{_intValue}{u}" : _intValue.ToString(),
            ValueKind.Float => Unit is { IsUnitless: false } u2 ? $"{_floatValue}{u2}" : _floatValue.ToString(),
            ValueKind.Char => _charValue.ToString(),
            ValueKind.String => _stringValue ?? "",
            ValueKind.Class => $"<object {_objectRef}>",
            ValueKind.Lambda => "<lambda>",
            ValueKind.Pointer => "<pointer>",
            ValueKind.Array => "<array>",
            ValueKind.Buffer => $"<buffer {AsBuffer().Length} bytes>",
            ValueKind.Undefined => Unit is { IsUnitless: false } u3 ? $"undefined:{u3}" : "undefined",
            _ => "?",
        };

        /// <summary>Format-Spezifizierer für `$"...{ausdruck:SPEC}..."` (siehe
        /// Compiler/OpCode.FormatValue). Ein leerer Spezifizierer verhält
        /// sich wie ToString(). Der ERSTE Buchstabe wählt das Format (Groß-/
        /// Kleinschreibung bei X/x unterschieden, sonst wie geschrieben
        /// übernommen), alles danach ist eine optionale Nachkommastellen-/
        /// Breitenangabe - bis auf 'B' 1:1 an .NETs eingebaute
        /// Zahlenformat-Strings durchgereicht (Standard Numeric Format
        /// Strings, z.B. "X4", "F2", "D5"):
        ///   X/x - hexadezimal, nur int. 'X4' padded auf mind. 4 Stellen.
        ///   D   - dezimal, nullgepolstert, nur int. 'D5' padded auf mind. 5.
        ///   F   - Festkomma, int/float. 'F2' = 2 Nachkommastellen.
        ///   E   - wissenschaftliche Notation, int/float.
        ///   B   - binär, nur int. 'B8' padded auf mind. 8 Stellen (von Hand
        ///         gebaut, da .NET kein natives 'B'-Zahlenformat kennt).
        /// Ein unbekannter erster Buchstabe oder ein Typ-Mismatch (z.B. 'X'
        /// auf einem float) wirft eine klare Exception statt still einen
        /// falschen/verwirrenden String zu produzieren.</summary>
        public string Format(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return ToString();

            string rest = spec.Length > 1 ? spec.Substring(1) : "";

            switch (char.ToUpperInvariant(spec[0]))
            {
                case 'X':
                case 'D':
                    RequireFormatKind(spec, ValueKind.Int);
                    return _intValue.ToString(spec, CultureInfo.InvariantCulture);

                case 'B':
                {
                    RequireFormatKind(spec, ValueKind.Int);
                    string bin = System.Convert.ToString(_intValue, 2);
                    return rest.Length > 0 && int.TryParse(rest, out int width)
                        ? bin.PadLeft(width, '0')
                        : bin;
                }

                case 'F':
                case 'E':
                    RequireFormatKind(spec, ValueKind.Int, ValueKind.Float);
                    return ToDouble().ToString(spec, CultureInfo.InvariantCulture);

                default:
                    throw new InvalidOperationException(
                        $"Unbekannter Format-Spezifizierer '{spec}' (bekannt: X/x, B, D, F, E, jeweils mit " +
                        "optionaler Nachkommastellen-/Breitenangabe wie 'F2'/'X4'/'D5').");
            }
        }

        private void RequireFormatKind(string spec, params ValueKind[] allowed)
        {
            if (Array.IndexOf(allowed, Kind) < 0)
                throw new InvalidOperationException(
                    $"Format-Spezifizierer '{spec}' erwartet {string.Join("/", allowed)}, nicht {Kind}.");
        }
    }
}
