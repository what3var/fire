namespace fire.Standard
{
    /// <summary>
    /// `#import "time"`: <c>TimeSpan</c> (a duration) and <c>DateTime</c> (a point in time), written in fire over a few native functions
    /// (the C++ of native/bridges/fire_bridge_time.hpp; the VM runs it in a shared library through the package ABI), plus the command <c>Sleep(time)</c>. Both count in ticks of 100 ns as in .NET (`DateTime` from 0001-01-01).
    /// See SPEC 8.15.
    /// </summary>
    public static class TimePrelude
    {
        public const string Source = """
            class TimeException : Exception {
                string message
                construct(string message) { this.message = message }
            }

            // A duration. Calculate as with numbers: `a + b`, `a - b`, `a * 2`, `a / 2`, comparisons, `a == b`.
            class TimeSpan {
                int ticks

                // new TimeSpan(ticks) / (h, m, s) / (d, h, m, s) / (d, h, m, s, ms)
                construct(int ticks) { this.ticks = ticks }
                construct(int h, int m, int s) { this.ticks = h * 36000000000 + m * 600000000 + s * 10000000 }
                construct(int d, int h, int m, int s) { this.ticks = d * 864000000000 + h * 36000000000 + m * 600000000 + s * 10000000 }
                construct(int d, int h, int m, int s, int ms) { this.ticks = d * 864000000000 + h * 36000000000 + m * 600000000 + s * 10000000 + ms * 10000 }

                static Zero() { return new TimeSpan(0) }
                static FromTicks(int t) { return new TimeSpan(t) }
                static FromMilliseconds(class x) { return new TimeSpan(__time_to_ticks(x, 10000)) }
                static FromSeconds(class x) { return new TimeSpan(__time_to_ticks(x, 10000000)) }
                static FromMinutes(class x) { return new TimeSpan(__time_to_ticks(x, 600000000)) }
                static FromHours(class x) { return new TimeSpan(__time_to_ticks(x, 36000000000)) }
                static FromDays(class x) { return new TimeSpan(__time_to_ticks(x, 864000000000)) }

                // From a value with a time unit (`TimeSpan.Of(1.5s)`, `Of(250ms)`), a number (milliseconds) or a TimeSpan
                static Of(class v) {
                    if (v is of TimeSpan) { return v }
                    var t = __time_unit_ticks(v)
                    if (t == undefined) { return new TimeSpan(__time_to_ticks(v, 10000)) }
                    return new TimeSpan(t)
                }

                int Ticks { get { return this.ticks } }
                int Days { get { return this.ticks / 864000000000 } }
                int Hours { get { return (this.ticks / 36000000000) % 24 } }
                int Minutes { get { return (this.ticks / 600000000) % 60 } }
                int Seconds { get { return (this.ticks / 10000000) % 60 } }
                int Milliseconds { get { return (this.ticks / 10000) % 1000 } }
                float TotalDays { get { return this.ticks / 864000000000.0 } }
                float TotalHours { get { return this.ticks / 36000000000.0 } }
                float TotalMinutes { get { return this.ticks / 600000000.0 } }
                float TotalSeconds { get { return this.ticks / 10000000.0 } }
                float TotalMilliseconds { get { return this.ticks / 10000.0 } }

                Add(class o) { return new TimeSpan(this.ticks + o.ticks) }
                Subtract(class o) { return new TimeSpan(this.ticks - o.ticks) }
                Multiply(class f) { return new TimeSpan(__time_to_ticks(this.ticks, f)) }
                Divide(class f) { return new TimeSpan(__time_to_ticks(this.ticks, 1.0 / f)) }
                Negate() { return new TimeSpan(0 - this.ticks) }
                Abs() { if (this.ticks < 0) { return new TimeSpan(0 - this.ticks) } return new TimeSpan(this.ticks) }
                CompareTo(class o) {
                    if (this.ticks < o.ticks) { return -1 }
                    if (this.ticks > o.ticks) { return 1 }
                    return 0
                }
                Equals(class o) { return (o is of TimeSpan) && this.ticks == o.ticks }

                // `ts + "text"` is a string concatenation (with ToString()), `ts + other` a sum
                operator+(class o) {
                    if (o is of string) { return this.ToString() + o }
                    return new TimeSpan(this.ticks + o.ticks)
                }
                operator-(class o) { return new TimeSpan(this.ticks - o.ticks) }
                operator*(class f) { return new TimeSpan(__time_to_ticks(this.ticks, f)) }
                operator/(class f) { return new TimeSpan(__time_to_ticks(this.ticks, 1.0 / f)) }
                operator<(class o) { return this.ticks < o.ticks }
                operator>(class o) { return this.ticks > o.ticks }
                operator<=(class o) { return this.ticks <= o.ticks }
                operator>=(class o) { return this.ticks >= o.ticks }
                operator==(class o) { return (o is of TimeSpan) && this.ticks == o.ticks }
                operator!=(class o) { return !((o is of TimeSpan) && this.ticks == o.ticks) }

                // `[-][d.]hh:mm:ss[.fffffff]`
                ToString() { return __time_span_text(this.ticks) }
            }

            // A point in time (calendar date and time of day). `kind` is "local" or "utc"; comparisons and differences assume the same kind (ToUtc()/ToLocal() convert).
            class DateTime {
                int ticks
                string kind

                // new DateTime(y, m, d) / (y, m, d, h, mi, s) / (y, m, d, h, mi, s, ms) - Ortszeit
                construct(int y, int m, int d) { this.ticks = __time_make(y, m, d, 0, 0, 0, 0); this.kind = "local" }
                construct(int y, int m, int d, int h, int mi, int s) { this.ticks = __time_make(y, m, d, h, mi, s, 0); this.kind = "local" }
                construct(int y, int m, int d, int h, int mi, int s, int ms) { this.ticks = __time_make(y, m, d, h, mi, s, ms); this.kind = "local" }

                static FromTicks(int ticks, string kind) {
                    var d = new DateTime(1, 1, 1)
                    d.ticks = ticks
                    d.kind = kind
                    return d
                }
                static UtcNow() { return DateTime.FromTicks(__time_now(), "utc") }
                static Now() {
                    var utc = __time_now()
                    return DateTime.FromTicks(utc + __time_local_offset(utc), "local")
                }
                static Today() { return DateTime.Now().Date() }
                static DaysInMonth(int year, int month) { return __time_days_in_month(year, month) }
                static IsLeapYear(int year) { return __time_days_in_month(year, 2) == 29 }

                // Text -> point in time (ISO and common forms, "2024-03-15 14:30:00"); Parse throws a TimeException, TryParse returns undefined
                static TryParse(string text) {
                    var t = __time_parse(text)
                    if (t == undefined) { return undefined }
                    return DateTime.FromTicks(t, "local")
                }
                static Parse(string text) {
                    var d = DateTime.TryParse(text)
                    if (d == undefined) { throw new TimeException("Not a valid date: '" + text + "'") }
                    return d
                }
                static FromUnixSeconds(class s) { return DateTime.FromTicks(621355968000000000 + __time_to_ticks(s, 10000000), "utc") }

                int Ticks { get { return this.ticks } }
                string Kind { get { return this.kind } }
                int Year { get { return __time_parts(this.ticks)[0] } }
                int Month { get { return __time_parts(this.ticks)[1] } }
                int Day { get { return __time_parts(this.ticks)[2] } }
                int Hour { get { return __time_parts(this.ticks)[3] } }
                int Minute { get { return __time_parts(this.ticks)[4] } }
                int Second { get { return __time_parts(this.ticks)[5] } }
                int Millisecond { get { return __time_parts(this.ticks)[6] } }
                // 0 = Sonntag ... 6 = Samstag
                int DayOfWeek { get { return __time_parts(this.ticks)[7] } }
                int DayOfYear { get { return __time_parts(this.ticks)[8] } }

                DayName() { return __time_format(this.ticks, "dddd") }
                MonthName() { return __time_format(this.ticks, "MMMM") }

                // Midnight of this day and the time of day as a duration
                Date() { return DateTime.FromTicks(this.ticks - this.ticks % 864000000000, this.kind) }
                TimeOfDay() { return new TimeSpan(this.ticks % 864000000000) }

                Add(class ts) { return DateTime.FromTicks(this.ticks + ts.ticks, this.kind) }
                // a TimeSpan -> DateTime, a DateTime -> TimeSpan
                Subtract(class o) {
                    if (o is of TimeSpan) { return DateTime.FromTicks(this.ticks - o.ticks, this.kind) }
                    return new TimeSpan(this.ticks - o.ticks)
                }
                AddTicks(int t) { return DateTime.FromTicks(this.ticks + t, this.kind) }
                AddMilliseconds(class x) { return DateTime.FromTicks(this.ticks + __time_to_ticks(x, 10000), this.kind) }
                AddSeconds(class x) { return DateTime.FromTicks(this.ticks + __time_to_ticks(x, 10000000), this.kind) }
                AddMinutes(class x) { return DateTime.FromTicks(this.ticks + __time_to_ticks(x, 600000000), this.kind) }
                AddHours(class x) { return DateTime.FromTicks(this.ticks + __time_to_ticks(x, 36000000000), this.kind) }
                AddDays(class x) { return DateTime.FromTicks(this.ticks + __time_to_ticks(x, 864000000000), this.kind) }
                AddMonths(int n) { return DateTime.FromTicks(__time_add_months(this.ticks, n), this.kind) }
                AddYears(int n) { return DateTime.FromTicks(__time_add_months(this.ticks, n * 12), this.kind) }

                ToUtc() {
                    if (this.kind == "utc") { return this }
                    // the offset of local time relative to UTC at this point in time (approximated from local time)
                    return DateTime.FromTicks(this.ticks - __time_local_offset(this.ticks), "utc")
                }
                ToLocal() {
                    if (this.kind == "local") { return this }
                    return DateTime.FromTicks(this.ticks + __time_local_offset(this.ticks), "local")
                }
                ToUnixSeconds() { return (this.ToUtc().ticks - 621355968000000000) / 10000000 }

                CompareTo(class o) {
                    if (this.ticks < o.ticks) { return -1 }
                    if (this.ticks > o.ticks) { return 1 }
                    return 0
                }
                Equals(class o) { return (o is of DateTime) && this.ticks == o.ticks }

                // `dt + "text"` is a string concatenation (with ToString()), `dt + timespan` a later point in time
                operator+(class ts) {
                    if (ts is of string) { return this.ToString() + ts }
                    return DateTime.FromTicks(this.ticks + ts.ticks, this.kind)
                }
                operator-(class o) {
                    if (o is of TimeSpan) { return DateTime.FromTicks(this.ticks - o.ticks, this.kind) }
                    return new TimeSpan(this.ticks - o.ticks)
                }
                operator<(class o) { return this.ticks < o.ticks }
                operator>(class o) { return this.ticks > o.ticks }
                operator<=(class o) { return this.ticks <= o.ticks }
                operator>=(class o) { return this.ticks >= o.ticks }
                operator==(class o) { return (o is of DateTime) && this.ticks == o.ticks }
                operator!=(class o) { return !((o is of DateTime) && this.ticks == o.ticks) }

                // "yyyy-MM-dd HH:mm:ss"; ToString(format) with the .NET time formats (e.g. "dd.MM.yyyy", "HH:mm", "o")
                ToString() { return __time_format(this.ticks, "yyyy-MM-dd HH:mm:ss") }
                ToString(string format) { return __time_format(this.ticks, format) }
            }
            """;
    }
}
