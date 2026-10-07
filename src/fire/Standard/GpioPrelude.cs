namespace fire.Standard
{
    /// <summary>
    /// `#import "gpio"`: digital pins (docs/NETWORK.md, "Hardware"). The natives are C++ (native/bridges/fire_bridge_gpio.hpp over plat::gpio: the GPIO character device of Linux - Raspberry Pi and other
    /// boards -, ESP-IDF on an ESP32); this fire source wraps them in classes in `namespace Gpio`. It needs `time` (the clock of the time limits).
    ///
    ///   var led = new Gpio.Pin(17).Output()                       // line 17 of the first real chip (Linux: gpiochip0), as an output, low
    ///   led.Write(true)
    ///   led.Toggle()
    ///
    ///   var button = new Gpio.Pin(27).Input(Gpio.Pull.Up, Gpio.Edge.Falling)
    ///   if (button.WaitEdge(5s) != Gpio.Edge.None) { print("pressed at " + button.EdgeTime + " us") }
    ///   while (button.TakeEdge() != Gpio.Edge.None) { }           // the edges that are waiting, one by one
    ///
    ///   var pin = new Gpio.Pin("gpiochip1", 4)                    // a line of another chip
    ///
    /// Every platform has the simulated chip "sim" (lines 0..31) that needs no hardware: `Gpio.Sim.Wire(1, 2)` connects two lines, `Gpio.Sim.Drive(5, 1)` drives a line from outside (like a button).
    /// A program tried on the PC with "sim" runs on the board with the name of the real chip. `Gpio.Board.Chips()` lists the chips ("sim" first); `new Gpio.Pin(line)` takes the first that is not "sim"
    /// and falls back to "sim" when the machine has none.
    /// Edges are collected (by the sim, the driver or an interrupt) and handed out by `TakeEdge`/`WaitEdge`/`Poll`: nothing blocks (waiting asks and sleeps a moment, the program stays abortable).
    /// Errors are exceptions: `Gpio.GpioException` (with a `code`) and its subclasses.
    /// </summary>
    public static class GpioPrelude
    {
        public const string Source = """
            namespace Gpio {
                enum Pull { None, Up, Down }
                enum Edge { None, Rising, Falling, Both }

                class GpioException : Exception {
                    string message
                    int code

                    construct(string message, int code = 7) {
                        this.message = message
                        this.code = code
                    }
                }

                // No such chip or line (code 3).
                class NotFoundException : GpioException {
                    construct(string message) : base(message, 3) { }
                }

                // The line is used by somebody else (code 4).
                class BusyException : GpioException {
                    construct(string message) : base(message, 4) { }
                }

                // No permission to use the GPIO device (code 5; on Linux: the user must be in the group gpio).
                class PermissionException : GpioException {
                    construct(string message) : base(message, 5) { }
                }

                // This platform cannot do it (code 6).
                class UnsupportedException : GpioException {
                    construct(string message) : base(message, 6) { }
                }

                class Errors {
                    // Throws the exception that fits the last error of a native function.
                    static Throw() {
                        var code = __GpioLastError()
                        var message = __GpioLastErrorMessage()
                        if (code == 3) { throw new NotFoundException(message) }
                        if (code == 4) { throw new BusyException(message) }
                        if (code == 5) { throw new PermissionException(message) }
                        if (code == 6) { throw new UnsupportedException(message) }
                        throw new GpioException(message, code)
                    }

                    static int Handle(int handle) {
                        if (handle < 0) { Throw() }
                        return handle
                    }
                }

                class Clock {
                    static int Millis(limit) {
                        if (limit == undefined) { return -1 }
                        var span = TimeSpan.Of(limit)
                        var ms = span.Ticks / 10000
                        if (ms < 0) { return 0 }
                        return ms
                    }

                    static int Now() { return DateTime.UtcNow().Ticks }
                }

                // The chips of this machine.
                class Board {
                    // true if this machine has GPIO hardware (the chip "sim" is always there).
                    static bool Available() { return __GpioSupported() == 1 }

                    // The names of the chips: "sim" first, then the real ones ("gpiochip0", ... on Linux, "gpio" on an ESP32).
                    static Chips() {
                        var all = __GpioChips()
                        if (all == undefined) { Gpio.Errors.Throw() }
                        var list = new List()
                        for (var i = 0; i < all.length; i++) { list.Add(all[i]) }
                        return list
                    }

                    // The first real chip, or "sim" when there is none.
                    static string DefaultChip() {
                        var all = __GpioChips()
                        if (all == undefined) { Gpio.Errors.Throw() }
                        if (all.length > 1) { return all[1] }
                        return "sim"
                    }
                }

                // One line of a chip. Input or Output claims it (a second Pin on the same line then throws BusyException); Close gives it back.
                class Pin {
                    int handle
                    bool closed
                    string chip
                    int line
                    int direction
                    var onEdge

                    // The line `line` of the default chip (Board.DefaultChip()).
                    construct(int line) {
                        this.Open(Gpio.Board.DefaultChip(), line)
                    }

                    construct(string chip, int line) {
                        this.Open(chip, line)
                    }

                    Open(string chip, int line) {
                        this.handle = -1
                        this.closed = false
                        this.chip = chip
                        this.line = line
                        this.direction = -1
                        this.onEdge = undefined
                        this.handle = Gpio.Errors.Handle(__GpioOpen(chip, line))
                    }

                    destruct() { try { this.Close() } catch (GpioException e) { } }

                    string Chip { get { return this.chip } }
                    int Line { get { return this.line } }
                    bool IsClosed { get { return this.closed } }
                    bool IsInput { get { return this.direction == 0 } }
                    bool IsOutput { get { return this.direction == 1 } }

                    Check() {
                        if (this.closed) { throw new GpioException("The pin is closed.", 2) }
                    }

                    // Sets the line up as an input: `pull` is Gpio.Pull.None/Up/Down, `edge` which changes are reported (Gpio.Edge.None/Rising/Falling/Both). Returns the pin.
                    Input(int pull = 0, int edge = 0) {
                        this.Check()
                        if (!__GpioConfigure(this.handle, 0, pull, edge, 0)) { Gpio.Errors.Throw() }
                        this.direction = 0
                        return this
                    }

                    // Sets the line up as an output that starts at `initial` (true: high). Returns the pin.
                    Output(bool initial = false) {
                        this.Check()
                        var v = 0
                        if (initial) { v = 1 }
                        if (!__GpioConfigure(this.handle, 1, 0, 0, v)) { Gpio.Errors.Throw() }
                        this.direction = 1
                        return this
                    }

                    // The level: true is high.
                    bool Read() {
                        this.Check()
                        var v = __GpioRead(this.handle)
                        if (v < 0) { Gpio.Errors.Throw() }
                        return v == 1
                    }

                    // The level as 0 or 1.
                    int ReadInt() {
                        if (this.Read()) { return 1 }
                        return 0
                    }

                    Write(bool value) {
                        this.Check()
                        var v = 0
                        if (value) { v = 1 }
                        if (!__GpioWrite(this.handle, v)) { Gpio.Errors.Throw() }
                    }

                    // Flips an output; returns the new level.
                    bool Toggle() {
                        var now = !this.Read()
                        this.Write(now)
                        return now
                    }

                    // The next edge that is waiting, without waiting: Gpio.Edge.Rising/Falling, or Gpio.Edge.None if there is none. EdgeTime says when it happened.
                    int TakeEdge() {
                        this.Check()
                        var e = __GpioPollEdge(this.handle)
                        if (e < 0) { Gpio.Errors.Throw() }
                        return e
                    }

                    // When the last edge that TakeEdge handed out happened: microseconds on a clock that only goes forward.
                    int EdgeTime {
                        get {
                            this.Check()
                            return __GpioEdgeTime(this.handle)
                        }
                    }

                    // Waits for the next edge up to the time limit (a TimeSpan, a time value like 500ms or milliseconds; undefined: for ever): the edge, or Gpio.Edge.None when the time ran out.
                    int WaitEdge(timeout = undefined) {
                        var limit = Gpio.Clock.Millis(timeout)
                        var deadline = -1
                        if (limit >= 0) { deadline = Gpio.Clock.Now() + limit * 10000 }
                        var tries = 0
                        while (true) {
                            var e = this.TakeEdge()
                            if (e != 0) { return e }
                            var ms = 1
                            if (tries >= 5) { ms = 2 }
                            if (tries >= 50) { ms = 5 }
                            if (deadline >= 0) {
                                var left = (deadline - Gpio.Clock.Now()) / 10000
                                if (left <= 0) { return 0 }
                                if (left < ms) { ms = left }
                            }
                            Sleep(ms)
                            tries = tries + 1
                        }
                    }

                    // Hands every waiting edge to `onEdge` (a function of the edge and its time in microseconds); returns how many there were. Call it from the loop of the program.
                    int Poll() {
                        var n = 0
                        while (true) {
                            var e = this.TakeEdge()
                            if (e == 0) { return n }
                            n = n + 1
                            var handler = this.onEdge
                            if (handler != undefined) { handler(e, this.EdgeTime) }
                        }
                    }

                    // Gives the line back.
                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.handle >= 0) { __GpioClose(this.handle) }
                    }
                }

                // The simulated chip "sim" from the outside: wires between lines and levels that something outside drives (for tests, and to try a program without the board).
                class Sim {
                    // Number of lines of the chip.
                    static int Lines() { return 32 }

                    // Connects two lines: what one drives, the other sees.
                    static Wire(int a, int b) {
                        if (!__GpioSimWire(a, b)) { Gpio.Errors.Throw() }
                    }

                    static Unwire(int a, int b) {
                        if (!__GpioSimUnwire(a, b)) { Gpio.Errors.Throw() }
                    }

                    // Drives a line from outside (a button, a sensor): true high, false low. A pin that is an output fights with it (low wins).
                    static Drive(int line, bool level) {
                        var v = 0
                        if (level) { v = 1 }
                        if (!__GpioSimDrive(line, v)) { Gpio.Errors.Throw() }
                    }

                    // Lets go of a line that was driven from outside.
                    static Release(int line) {
                        if (!__GpioSimDrive(line, -1)) { Gpio.Errors.Throw() }
                    }

                    // The level on a line, whoever drives it.
                    static bool Level(int line) {
                        var v = __GpioSimLevel(line)
                        if (v < 0) { Gpio.Errors.Throw() }
                        return v == 1
                    }

                    // Removes all wires and drives (the open pins stay open).
                    static Reset() { __GpioSimReset() }
                }
            }
            """;
    }
}
