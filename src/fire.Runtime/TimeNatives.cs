using System;
using System.Collections.Generic;
using System.Globalization;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Die nativen Funktionen hinter `#import "time"` (fire-Seite: <c>fire.Standard.TimePrelude</c> mit `DateTime` und `TimeSpan`) und der Befehl
    /// `Sleep`. Zeit wird in **Ticks** (100 ns, wie in .NET) gerechnet; `DateTime` zählt ab 0001-01-01.
    /// </summary>
    public static class TimeNatives
    {
        public const long TicksPerMillisecond = 10_000;
        public const long TicksPerSecond = 10_000_000;

        public static void Register(NativeRegistry natives)
        {
            natives.Register("__time_now", a => Value.MakeInt(DateTime.UtcNow.Ticks));
            natives.Register("__time_local_offset", a => Value.MakeInt(LocalOffset(a[0].AsInt())));
            natives.Register("__time_parts", a => Parts(a[0].AsInt()));
            natives.Register("__time_make", a => Make(a));
            natives.Register("__time_parse", a => Parse(a[0].AsString()));
            natives.Register("__time_format", a => Format(a[0].AsInt(), a[1].AsString()));
            natives.Register("__time_add_months", a => AddMonths(a[0].AsInt(), a[1].AsInt()));
            natives.Register("__time_days_in_month", a => DaysInMonth(a[0].AsInt(), a[1].AsInt()));
            natives.Register("__time_to_ticks", a => Value.MakeInt((long)Math.Round(Num(a[0]) * Num(a[1]))));
            natives.Register("__time_unit_ticks", a => UnitTicks(a[0]));
            natives.Register("__time_span_text", a => Value.MakeString(SpanText(a[0].AsInt())));
            natives.Register("Sleep", a => Sleep(a));
        }

        private static double Num(Value v) => v.Kind == ValueKind.Float ? v.AsFloat() : v.AsInt();

        private static Value Fail(string message) =>
            VM.CurrentThreadVm is { } vm ? vm.NativeFail("TimeException", message) : throw new InvalidOperationException(message);

        private static Value Array(params long[] items)
        {
            var arr = new ScriptArray(items.Length);
            for (int i = 0; i < items.Length; i++) arr.Items[i] = Value.MakeInt(items[i]);
            return Value.MakeArray(arr);
        }

        private static long LocalOffset(long utcTicks)
        {
            var utc = new DateTime(Math.Clamp(utcTicks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc);
            return TimeZoneInfo.Local.GetUtcOffset(utc).Ticks;
        }

        /// <summary>[Jahr, Monat, Tag, Stunde, Minute, Sekunde, Millisekunde, Wochentag (0 = Sonntag), Tag im Jahr].</summary>
        private static Value Parts(long ticks)
        {
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return Fail($"Zeitpunkt außerhalb des gültigen Bereichs ({ticks} Ticks).");
            var d = new DateTime(ticks);
            return Array(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second, d.Millisecond, (int)d.DayOfWeek, d.DayOfYear);
        }

        private static Value Make(Value[] a)
        {
            try
            {
                var d = new DateTime((int)a[0].AsInt(), (int)a[1].AsInt(), (int)a[2].AsInt(), (int)a[3].AsInt(), (int)a[4].AsInt(), (int)a[5].AsInt(), (int)a[6].AsInt());
                return Value.MakeInt(d.Ticks);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Fail($"Ungültiges Datum/ungültige Zeit: {a[0].AsInt()}-{a[1].AsInt()}-{a[2].AsInt()} {a[3].AsInt()}:{a[4].AsInt()}:{a[5].AsInt()}.{a[6].AsInt()}");
            }
        }

        private static Value Parse(string text) =>
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                ? Value.MakeInt(d.Ticks)
                : Value.MakeUndefined();

        private static Value Format(long ticks, string format)
        {
            try { return Value.MakeString(new DateTime(ticks).ToString(format, CultureInfo.InvariantCulture)); }
            catch (FormatException) { return Fail($"Ungültiges Zeitformat '{format}'."); }
            catch (ArgumentOutOfRangeException) { return Fail($"Zeitpunkt außerhalb des gültigen Bereichs ({ticks} Ticks)."); }
        }

        private static Value AddMonths(long ticks, long months)
        {
            try { return Value.MakeInt(new DateTime(ticks).AddMonths((int)months).Ticks); }
            catch (ArgumentOutOfRangeException) { return Fail("Das Ergebnis liegt außerhalb des gültigen Zeitbereichs."); }
        }

        private static Value DaysInMonth(long year, long month)
        {
            if (year < 1 || year > 9999 || month < 1 || month > 12) return Fail($"Ungültiger Monat {year}-{month}.");
            return Value.MakeInt(DateTime.DaysInMonth((int)year, (int)month));
        }

        /// <summary>Ein Wert mit Zeiteinheit (`500ms`, `2s`, `1.5min`) in Ticks, sonst undefined.</summary>
        private static Value UnitTicks(Value v)
        {
            if (v.Kind is not (ValueKind.Int or ValueKind.Float) || v.Unit is not { IsUnitless: false } unit) return Value.MakeUndefined();
            double perSecond;
            try { perSecond = unit.ConversionFactorTo(Unit.Parse("s")); }
            catch (Exception) { return Fail($"'{unit}' ist keine Zeiteinheit."); }
            return Value.MakeInt((long)Math.Round(Num(v) * perSecond * TicksPerSecond));
        }

        /// <summary>`[-][d.]hh:mm:ss[.fffffff]` wie .NET.</summary>
        private static string SpanText(long ticks) => TimeSpan.FromTicks(ticks).ToString("c", CultureInfo.InvariantCulture);

        /// <summary>`Sleep(zeit)`: `zeit` ist eine `TimeSpan`, ein Wert mit Zeiteinheit (`Sleep(500ms)`) oder eine Zahl (Millisekunden).</summary>
        private static Value Sleep(Value[] a)
        {
            if (a.Length != 1) return Fail("Sleep erwartet genau ein Argument (TimeSpan, Zeitwert wie 500ms oder Millisekunden).");
            long ticks;
            var arg = a[0];
            if (arg.Kind == ValueKind.Class)
            {
                var obj = (ObjectInstance)arg.AsObjectRef();
                if (!obj.TryGetFieldLocked("ticks", out var field) || field.Kind != ValueKind.Int) return Fail($"Sleep erwartet eine TimeSpan, erhalten: {obj.ClassName}.");
                ticks = field.AsInt();
            }
            else if (arg.Kind is ValueKind.Int or ValueKind.Float)
            {
                var unitTicks = UnitTicks(arg);
                if (unitTicks.Kind == ValueKind.Int) ticks = unitTicks.AsInt();
                else if (unitTicks.Kind == ValueKind.Undefined) ticks = (long)Math.Round(Num(arg) * TicksPerMillisecond); // eine Zahl ohne Einheit: Millisekunden
                else return unitTicks;
            }
            else return Fail($"Sleep erwartet eine TimeSpan, einen Zeitwert oder Millisekunden, erhalten: {arg.Kind}.");

            if (VM.CurrentThreadVm is { } vm) vm.SleepTicks(ticks);
            else if (ticks > 0) System.Threading.Thread.Sleep(TimeSpan.FromTicks(ticks));
            return Value.MakeUndefined();
        }
    }
}
