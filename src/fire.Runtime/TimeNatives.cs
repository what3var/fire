using System;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// What the virtual machine does itself for `#import "time"`: the command `Sleep` and the unit of a time value (`__time_unit_ticks`: the units do not cross the package ABI). `Sleep` has to wait the way the VM waits (not deaf: a terminate or an abort ends it), so it is not part of the
    /// C++ library of the package (the `host` functions of the manifest, see <see cref="HostNatives"/>). All the rest of the time functions (DateTime, TimeSpan) are the C++ of
    /// native/bridges/fire_bridge_time.hpp. Time is counted in ticks (100 ns, like in .NET).
    /// </summary>
    public static class TimeNatives
    {
        public const long TicksPerMillisecond = 10_000;
        public const long TicksPerSecond = 10_000_000;

        private static double Num(Value v) => v.Kind == ValueKind.Float ? v.AsFloat() : v.AsInt();

        /// <summary>A value with a time unit (`500ms`, `2s`, `1.5min`) in ticks, otherwise undefined.</summary>
        internal static Value UnitTicks(Value[] a)
        {
            var v = a[0];
            if (v.Kind is not (ValueKind.Int or ValueKind.Float) || v.Unit is not { IsUnitless: false } unit) return Value.MakeUndefined();
            double perSecond;
            try { perSecond = unit.ConversionFactorTo(Unit.Parse("s")); }
            catch (Exception) { return VM.CurrentThreadVm is { } vm ? vm.NativeFail("TimeException", $"'{unit}' is not a unit of time.") : throw new InvalidOperationException($"'{unit}' is not a unit of time."); }
            return Value.MakeInt((long)Math.Round(Num(v) * perSecond * TicksPerSecond));
        }

        /// <summary>A time specification as ticks, as `Sleep` understands it: a `TimeSpan`, a value with a time unit (`500ms`, `2s`) or a number in milliseconds.
        /// `false` with an error text if it is none of these.</summary>
        internal static bool TryTimeTicks(Value arg, out long ticks, out string error)
        {
            ticks = 0;
            error = "";
            if (arg.Kind == ValueKind.Class)
            {
                var obj = (ObjectInstance)arg.AsObjectRef();
                if (!obj.TryGetFieldLocked("ticks", out var field) || field.Kind != ValueKind.Int) { error = $"expects a TimeSpan, got: {obj.ClassName}."; return false; }
                ticks = field.AsInt();
                return true;
            }
            if (arg.Kind is ValueKind.Int or ValueKind.Float)
            {
                if (arg.Unit is { IsUnitless: false } unit)
                {
                    try { ticks = (long)Math.Round(Num(arg) * unit.ConversionFactorTo(Unit.Parse("s")) * TicksPerSecond); return true; }
                    catch (Exception) { error = $"'{unit}' is not a unit of time."; return false; }
                }
                ticks = (long)Math.Round(Num(arg) * TicksPerMillisecond); // a number without a unit: milliseconds
                return true;
            }
            error = $"expects a TimeSpan, a time value or milliseconds, got: {arg.Kind}.";
            return false;
        }

        /// <summary>`Sleep(time)`: `time` is a `TimeSpan`, a value with a time unit (`Sleep(500ms)`) or a number (milliseconds).</summary>
        internal static Value Sleep(Value[] a)
        {
            string? problem = null;
            long ticks = 0;
            if (a.Length != 1) problem = "Sleep expects exactly one argument (TimeSpan, a time value like 500ms, or milliseconds).";
            else if (!TryTimeTicks(a[0], out ticks, out var error)) problem = error.StartsWith("expects", StringComparison.Ordinal) ? "Sleep " + error : error;
            if (problem != null)
                return VM.CurrentThreadVm is { } failing ? failing.NativeFail("TimeException", problem) : throw new InvalidOperationException(problem);

            if (VM.CurrentThreadVm is { } vm) vm.SleepTicks(ticks);
            else if (ticks > 0) System.Threading.Thread.Sleep(TimeSpan.FromTicks(ticks));
            return Value.MakeUndefined();
        }
    }

    /// <summary>The natives that the host (the virtual machine) runs itself although a package declares them (`host` in the manifest): by name.</summary>
    public static class HostNatives
    {
        public static NativeFunction? Find(string name) => name switch
        {
            "Sleep" => TimeNatives.Sleep,
            "__time_unit_ticks" => TimeNatives.UnitTicks,
            "__DEVWaitForString" => DeviceHostNatives.WaitForString,
            "__DEVWaitFor" => DeviceHostNatives.WaitFor,
            _ => null,
        };
    }
}
