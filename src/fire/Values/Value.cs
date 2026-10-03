using System;
using System.Globalization;
using System.Runtime.CompilerServices;

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
        // Speicherlayout: 24 Byte (Kind 4 + Width 4 + Bits 8 + Referenz 8) - ein Value wird bei JEDEM
        // Stack-Zugriff kopiert, die frühere Fassung mit je einem eigenen Feld pro Werteart (long, double,
        // bool, char, string, object + Unit) war 64 Byte groß.
        //   _bits: Int = der Wert, Float = die IEEE-754-Bits des double, Bool = 0/1, Char = der Zeichencode.
        //   _ref:  String = die Zeichenkette, Class/Lambda/Pointer/Array/Buffer = das Objekt,
        //          Int/Float/Undefined = die Unit (nie beides gleichzeitig - eine Zahl hat keine Objekt-
        //          referenz und ein Objekt keine Einheit).
        public ValueKind Kind { get; }

        /// <summary>Nur für Int/Float relevant (SPEC "APIs & Bitbreiten"). Default
        /// ist immer die höchste Genauigkeit (W64).</summary>
        public NumericWidth Width { get; }

        private readonly long _bits;
        private readonly object? _ref;

        /// <summary>Die Einheit - nur Int/Float/Undefined tragen eine (sonst <c>null</c>).</summary>
        public Unit? Unit => Kind is ValueKind.Int or ValueKind.Float or ValueKind.Undefined
            ? Unsafe.As<Unit?>(_ref)
            : null;

        private long _intValue => _bits;
        private double _floatValue => BitConverter.Int64BitsToDouble(_bits);
        private bool _boolValue => _bits != 0;
        private char _charValue => (char)_bits;

        private Value(ValueKind kind, long bits, object? reference, NumericWidth width = NumericWidth.W64)
        {
            Kind = kind;
            Width = width;
            _bits = bits;
            _ref = reference;
        }

        // ---------------------------------------------------------------
        // Factories
        // ---------------------------------------------------------------
        public static Value MakeBool(bool value) =>
            new(ValueKind.Bool, value ? 1 : 0, null);

        public static Value MakeInt(long value, Unit? unit = null, NumericWidth width = NumericWidth.W64) =>
            new(ValueKind.Int, value, unit ?? Values.Unit.Unitless, width);

        public static Value MakeFloat(double value, Unit? unit = null, NumericWidth width = NumericWidth.W64) =>
            new(ValueKind.Float, BitConverter.DoubleToInt64Bits(value), unit ?? Values.Unit.Unitless, width);

        public static Value MakeChar(char value) =>
            new(ValueKind.Char, value, null);

        public static Value MakeString(string value) =>
            new(ValueKind.String, 0, value);

        public static Value MakeUndefined(Unit? unit = null) =>
            new(ValueKind.Undefined, 0, unit ?? Values.Unit.Unitless);

        public static Value MakeClassRef(object objectInstance) =>
            new(ValueKind.Class, 0, objectInstance);

        // Lose typisiert (object) aus demselben Grund wie MakeClassRef: Values
        // bleibt unabhängig von der Runtime-Schicht (Runtime.LambdaValue), die
        // Runtime-Schicht hängt von Values ab, nicht umgekehrt.
        public static Value MakeLambda(object lambdaValue) =>
            new(ValueKind.Lambda, 0, lambdaValue);

        public static Value MakePointer(PointerTarget target) =>
            new(ValueKind.Pointer, 0, target);

        public static Value MakeArray(ScriptArray array) =>
            new(ValueKind.Array, 0, array);

        public static Value MakeBuffer(ByteBuffer buffer) =>
            new(ValueKind.Buffer, 0, buffer);

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
            return Unsafe.As<string>(_ref)!;
        }

        public object AsObjectRef()
        {
            RequireKind(ValueKind.Class);
            return _ref!;
        }

        public object AsLambda()
        {
            RequireKind(ValueKind.Lambda);
            return _ref!;
        }

        public PointerTarget AsPointer()
        {
            RequireKind(ValueKind.Pointer);
            return Unsafe.As<PointerTarget>(_ref)!;
        }

        public ScriptArray AsArray()
        {
            RequireKind(ValueKind.Array);
            return Unsafe.As<ScriptArray>(_ref)!;
        }

        public ByteBuffer AsBuffer()
        {
            RequireKind(ValueKind.Buffer);
            return Unsafe.As<ByteBuffer>(_ref)!;
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
        // Schnellpfad der Grundrechenarten: beide Operanden Zahlen mit DERSELBEN Einheit-Instanz (der Normalfall:
        // `Unit.Unitless`) - dann entfallen Einheitenvergleich und Kindprüfungen, das Ergebnis ist mit dem des
        // allgemeinen Pfads identisch (Einheit des linken Operanden, Breite W64).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool BothNumericSameUnit(in Value a, in Value b) =>
            a.Kind is ValueKind.Int or ValueKind.Float && b.Kind is ValueKind.Int or ValueKind.Float
            && ReferenceEquals(a._ref, b._ref);

        // "In place"-Varianten der Schnellpfade für die VM (siehe VM.Step): das Ergebnis überschreibt den linken
        // Operanden direkt im Stack, ohne Value-Kopien durch Argumente und Rückgabewert. Liefern false, wenn der
        // Schnellpfad nicht zutrifft (andere Einheit/Art, Division durch 0, ...) - dann rechnet der Aufrufer über
        // den allgemeinen Weg und bekommt dessen Ergebnis bzw. dessen Ausnahme.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryAddInPlace(ref Value a, in Value b)
        {
            if (!BothNumericSameUnit(a, b)) return false;
            a = a.Kind == ValueKind.Int && b.Kind == ValueKind.Int
                ? new Value(ValueKind.Int, a._bits + b._bits, a._ref)
                : new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(a.ToDouble() + b.ToDouble()), a._ref);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TrySubtractInPlace(ref Value a, in Value b)
        {
            if (!BothNumericSameUnit(a, b)) return false;
            a = a.Kind == ValueKind.Int && b.Kind == ValueKind.Int
                ? new Value(ValueKind.Int, a._bits - b._bits, a._ref)
                : new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(a.ToDouble() - b.ToDouble()), a._ref);
            return true;
        }

        /// <summary>Nur für zwei Werte OHNE Einheit (dieselbe `Unitless`-Instanz) - sonst entsteht eine Produkt-Einheit.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryMultiplyInPlace(ref Value a, in Value b)
        {
            if (!BothNumericSameUnit(a, b) || !ReferenceEquals(a._ref, Values.Unit.Unitless)) return false;
            a = a.Kind == ValueKind.Int && b.Kind == ValueKind.Int
                ? new Value(ValueKind.Int, a._bits * b._bits, a._ref)
                : new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(a.ToDouble() * b.ToDouble()), a._ref);
            return true;
        }

        /// <summary>Nur int % int mit Divisor != 0 (sonst wirft der allgemeine Weg wie bisher).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryModuloInPlace(ref Value a, in Value b)
        {
            if (a.Kind != ValueKind.Int || b.Kind != ValueKind.Int || !ReferenceEquals(a._ref, b._ref) || b._bits == 0 || b._bits == -1)
                return false;
            a = new Value(ValueKind.Int, a._bits % b._bits, a._ref);
            return true;
        }

        /// <summary>Vergleich zweier Zahlen mit derselben Einheit-Instanz: `kind` 0 = `&lt;`, 1 = `&lt;=`, 2 = `&gt;`, 3 = `&gt;=`.
        /// Verglichen wird wie Compare() als double.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryCompareInPlace(ref Value a, in Value b, int kind)
        {
            if (!BothNumericSameUnit(a, b)) return false;
            int c = a.ToDouble().CompareTo(b.ToDouble());
            bool result = kind switch { 0 => c < 0, 1 => c <= 0, 2 => c > 0, _ => c >= 0 };
            a = MakeBool(result);
            return true;
        }

        /// <summary>Schnellpfad der verschmolzenen Vergleichssprünge (VM: JumpIfNotLt usw.): vergleicht zwei Zahlen gleicher Einheit
        /// (`kind`: 0 &lt;, 1 &lt;=, 2 &gt;, 3 &gt;=) und liefert false, wenn der Schnellpfad nicht zutrifft. Zwei Ganzzahlen werden als
        /// Ganzzahlen verglichen, alles andere wie <see cref="Compare"/> als double.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryCompareFast(in Value a, in Value b, int kind, out bool result)
        {
            if (!BothNumericSameUnit(a, b)) { result = false; return false; }
            int c = a.Kind == ValueKind.Int && b.Kind == ValueKind.Int
                ? a._bits.CompareTo(b._bits)
                : a.ToDouble().CompareTo(b.ToDouble());
            result = kind switch { 0 => c < 0, 1 => c <= 0, 2 => c > 0, _ => c >= 0 };
            return true;
        }

        public static Value Add(Value a, Value b)
        {
            if (BothNumericSameUnit(a, b))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits + b._bits, a._ref);
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(a.ToDouble() + b.ToDouble()), a._ref);
            }

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
            if (BothNumericSameUnit(a, b))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits - b._bits, a._ref);
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(a.ToDouble() - b.ToDouble()), a._ref);
            }

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
            if (BothNumericSameUnit(a, b))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits % b._bits, a._ref);
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(a.ToDouble() % b.ToDouble()), a._ref);
            }

            RequireNumeric(a); RequireNumeric(b);
            RequireSameUnit(a, b);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() % b.ToDouble(), a.Unit);
            return MakeInt(a._intValue % b._intValue, a.Unit);
        }

        public static Value Divide(Value a, Value b)
        {
            // Beide ohne Einheit (dieselbe `Unitless`-Instanz): Ergebnis ohne Einheit, wie Unit.Divide.
            if (BothNumericSameUnit(a, b) && ReferenceEquals(a._ref, Values.Unit.Unitless))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits / b._bits, a._ref);
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(a.ToDouble() / b.ToDouble()), a._ref);
            }

            RequireNumeric(a); RequireNumeric(b);
            var resultUnit = Values.Unit.Divide(a.Unit ?? Values.Unit.Unitless, b.Unit ?? Values.Unit.Unitless);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() / b.ToDouble(), resultUnit);
            return MakeInt(a._intValue / b._intValue, resultUnit);
        }

        public static Value Multiply(Value a, Value b)
        {
            // Beide ohne Einheit (dieselbe `Unitless`-Instanz): Ergebnis ohne Einheit, wie Unit.Multiply.
            if (BothNumericSameUnit(a, b) && ReferenceEquals(a._ref, Values.Unit.Unitless))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits * b._bits, a._ref);
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(a.ToDouble() * b.ToDouble()), a._ref);
            }

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
            if (BothNumericSameUnit(a, b))
                return a.ToDouble().CompareTo(b.ToDouble());

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
            if (ReferenceEquals(a._ref, b._ref) && a.Kind is ValueKind.Int or ValueKind.Float) return;
            var ua = a.Unit ?? Values.Unit.Unitless;
            var ub = b.Unit ?? Values.Unit.Unitless;
            if (!ua.Equals(ub))
                throw new UnitMismatchException(ua, ub);
        }

        // ---------------------------------------------------------------
        // Gleichheit ignoriert Einheit und Bitbreite (wie bisher) und vergleicht je Werteart nur das, was
        // sie tatsächlich hält.
        public bool Equals(Value other)
        {
            if (Kind != other.Kind) return false;
            switch (Kind)
            {
                case ValueKind.Int:
                case ValueKind.Bool:
                case ValueKind.Char:
                    return _bits == other._bits;
                case ValueKind.Float:
                    return _floatValue.Equals(other._floatValue);
                case ValueKind.Undefined:
                    return true;
                case ValueKind.String:
                    return (string?)_ref == (string?)other._ref;
                default:
                    return Equals(_ref, other._ref);
            }
        }

        public override bool Equals(object? obj) => obj is Value v && Equals(v);

        public override int GetHashCode() => Kind switch
        {
            ValueKind.Int or ValueKind.Bool or ValueKind.Char => HashCode.Combine(Kind, _bits),
            ValueKind.Float => HashCode.Combine(Kind, _floatValue),
            ValueKind.Undefined => HashCode.Combine(Kind),
            _ => HashCode.Combine(Kind, _ref),
        };

        public override string ToString() => Kind switch
        {
            ValueKind.Bool => _boolValue.ToString(),
            ValueKind.Int => Unit is { IsUnitless: false } u ? $"{_intValue}{u}" : _intValue.ToString(),
            ValueKind.Float => Unit is { IsUnitless: false } u2 ? $"{_floatValue}{u2}" : _floatValue.ToString(),
            ValueKind.Char => _charValue.ToString(),
            ValueKind.String => Unsafe.As<string>(_ref) ?? "",
            ValueKind.Class => $"<object {_ref}>",
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
