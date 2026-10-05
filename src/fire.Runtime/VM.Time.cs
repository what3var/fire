using System;
using System.Diagnostics;

namespace fire.Runtime
{
    public sealed partial class VM
    {
        /// <summary>Text eines Werts für Ausgabe und Textverkettung: ein Objekt mit einer parameterlosen `ToString()`-Methode liefert deren Ergebnis, alles andere
        /// seine gewohnte Darstellung. null, wenn `ToString()` eine Exception ausgelöst hat, die schon in einen Handler umgeleitet wurde (kein Ergebnis).</summary>
        internal string? StringifyForText(fire.Values.Value v)
        {
            if (v.Kind != fire.Values.ValueKind.Class) return v.ToString();
            var obj = (ObjectInstance)v.AsObjectRef();
            if (ResolveClass(obj.ClassName).FindMethod("ToString", 0) == null) return v.ToString();
            var result = CallMethodNested(obj, "ToString", Array.Empty<fire.Values.Value>());
            if (result == null) return null;
            return result.Value.Kind == fire.Values.ValueKind.String ? result.Value.AsString() : result.Value.ToString();
        }

        /// <summary>Für den Host-`print`: das erste Argument, wenn es ein Objekt mit `ToString()` ist, als Text (siehe <see cref="StringifyForText"/>). null, wenn
        /// die Umwandlung umgeleitet wurde - der native Aufruf liefert dann kein Ergebnis.</summary>
        public static fire.Values.Value[]? StringifyForPrint(fire.Values.Value[] args)
        {
            if (args.Length == 0 || args[0].Kind != fire.Values.ValueKind.Class || CurrentThreadVm is not { } vm) return args;
            var text = vm.StringifyForText(args[0]);
            if (text == null) { vm._nativeRedirected = true; return null; }
            return new[] { fire.Values.Value.MakeString(text) };
        }

        // Standard-Wartezeit der Warte-Funktionen (`#timeout`). Statisch, damit auch die Fire-Threads des Programms sie sehen; ein neues Hauptprogramm
        // beginnt wieder mit 30 Sekunden (siehe den VM-Konstruktor).
        private static long s_defaultTimeoutTicks = DefaultTimeoutTicks;
        private const long DefaultTimeoutTicks = 30L * 10_000_000;

        /// <summary>Die Wartezeit, die eine Warte-Funktion ohne eigene Zeitangabe nimmt: das `#timeout` des Programms, sonst 30 Sekunden.</summary>
        public static TimeSpan DefaultTimeout => TimeSpan.FromTicks(System.Threading.Volatile.Read(ref s_defaultTimeoutTicks));

        internal static void SetDefaultTimeout(TimeSpan timeout) => System.Threading.Volatile.Write(ref s_defaultTimeoutTicks, timeout.Ticks);
        internal static void ResetDefaultTimeout() => System.Threading.Volatile.Write(ref s_defaultTimeoutTicks, DefaultTimeoutTicks);

        /// <summary>Wartet, bis `condition` wahr wird, höchstens `timeout` (ein `undefined` = <see cref="DefaultTimeout"/>; sonst eine TimeSpan, ein Zeitwert wie `500ms`
        /// oder Millisekunden, wie bei `Sleep`). Kommt der Aufruf von einer VM (`CurrentThreadVm`), ist das Warten nicht taub (siehe <see cref="SleepTicks"/>):
        /// `leave`/`terminate` beenden es sofort (Ergebnis false), die Warteschlange des Hauptprogramms läuft weiter. Liefert true, sobald `condition` wahr war.
        /// `ArgumentException`, wenn `timeout` keine Zeitangabe ist.</summary>
        public static bool WaitUntil(Func<bool> condition, fire.Values.Value timeout)
        {
            long ticks;
            if (timeout.Kind == fire.Values.ValueKind.Undefined) ticks = System.Threading.Volatile.Read(ref s_defaultTimeoutTicks);
            else if (!TimeNatives.TryTimeTicks(timeout, out ticks, out var error)) throw new ArgumentException("Invalid wait time: " + error);

            if (CurrentThreadVm is { } vm) return vm.WaitUntilTicks(condition, ticks);

            long deadline = Stopwatch.GetTimestamp() + (long)(ticks * (Stopwatch.Frequency / 10_000_000.0));
            while (!condition())
            {
                if (Stopwatch.GetTimestamp() >= deadline) return false;
                System.Threading.Thread.Sleep(2);
            }
            return true;
        }

        private bool WaitUntilTicks(Func<bool> condition, long ticks)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)(ticks * (Stopwatch.Frequency / 10_000_000.0));
            while (true)
            {
                if (condition()) return true;
                PollSignalsAfterOp();
                if (_stopExecutionRequested) return false;

                long remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0) return false;
                int milliseconds = (int)Math.Clamp(remaining * 1000 / Stopwatch.Frequency, 1, 5);
                FireRuntime.WaitForWake(milliseconds);
            }
        }

        /// <summary>`Sleep`: legt diesen Thread für `ticks` (100 ns) schlafen - aber nicht taub. Zwischen den kurzen Schlafstücken läuft, was sonst an den sicheren
        /// Punkten läuft (<see cref="PollSignalsAfterOp"/>): `leave`/`terminate` beenden das Schlafen sofort, zugestellte Fire-Thread-Exceptions werden behandelt, und
        /// im Hauptprogramm arbeitet das automatische Abarbeiten der Warteschlange (Sektionen, `fire global`-Aufträge, Host-Callbacks; mit `#nosync` nicht).
        /// Ein Eintrag in der Warteschlange weckt das Schlafen früher auf.</summary>
        public void SleepTicks(long ticks)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)(ticks * (Stopwatch.Frequency / 10_000_000.0));
            while (true)
            {
                PollSignalsAfterOp();
                if (_stopExecutionRequested) return;

                long remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0) return;
                int milliseconds = (int)Math.Clamp(remaining * 1000 / Stopwatch.Frequency, 1, 20);
                FireRuntime.WaitForWake(milliseconds);
            }
        }
    }
}
