namespace fire.Standard
{
    /// <summary>
    /// `#import "spi"`: the SPI bus as a controller (docs/NETWORK.md, "SPI"). The natives are C++ (native/bridges/fire_bridge_spi.hpp over plat::spi: /dev/spidevB.C of Linux - Raspberry Pi and other boards -,
    /// the SPI master driver of ESP-IDF on an ESP32); this fire source wraps them in classes in `namespace Spi`.
    ///
    ///   var chip = new Spi.Device("0.0", 0, 1000000)              // /dev/spidev0.0, mode 0, 1 MHz; Spi.Board.Devices() lists the devices
    ///   var answer = chip.Transfer(command)                       // send the bytes and receive as many at the same time (a byte[])
    ///   var id = chip.WriteRead(command, 3)                       // send the command, then read 3 bytes (the chip select stays low in between)
    ///   chip.Write(data)                                          // Write(data, offset, count); Read(count) sends zeros and returns what comes in
    ///   chip.Mode = 3                                             // clock polarity and phase (0..3); Speed (Hz) and LsbFirst can be changed too
    ///
    /// A device is a bus with one chip select; the chip select is held low during a transfer. Every platform has the simulated device "sim", a loopback by default (MISO tied to MOSI: what is sent comes back);
    /// `Spi.Sim.Reply(bytes)` turns it into a device that answers with those bytes, `Spi.Sim.Sent()` shows what the program sent. A program tried on the PC with "sim" runs on the board with the real device.
    /// Errors are exceptions: `Spi.SpiException` (with a `code`) and its subclasses.
    /// </summary>
    public static class SpiPrelude
    {
        public const string Source = """
            namespace Spi {
                class SpiException : Exception {
                    string message
                    int code

                    construct(string message, int code = 7) {
                        this.message = message
                        this.code = code
                    }
                }

                // No such device (code 3).
                class NotFoundException : SpiException {
                    construct(string message) : base(message, 3) { }
                }

                // The device or its bus is used by somebody else (code 4).
                class BusyException : SpiException {
                    construct(string message) : base(message, 4) { }
                }

                // No permission to use the device (code 5; on Linux the user must be in the group spi).
                class PermissionException : SpiException {
                    construct(string message) : base(message, 5) { }
                }

                // This platform cannot do it (code 6).
                class UnsupportedException : SpiException {
                    construct(string message) : base(message, 6) { }
                }

                // The transfer took too long (code 8).
                class TimeoutException : SpiException {
                    construct(string message) : base(message, 8) { }
                }

                class Errors {
                    // Throws the exception that fits the last error of a native function.
                    static Throw() {
                        var code = __SpiLastError()
                        var message = __SpiLastErrorMessage()
                        if (code == 3) { throw new NotFoundException(message) }
                        if (code == 4) { throw new BusyException(message) }
                        if (code == 5) { throw new PermissionException(message) }
                        if (code == 6) { throw new UnsupportedException(message) }
                        if (code == 8) { throw new TimeoutException(message) }
                        throw new SpiException(message, code)
                    }

                    static int Handle(int handle) {
                        if (handle < 0) { Throw() }
                        return handle
                    }
                }

                // The devices of this machine.
                class Board {
                    // true if this machine has SPI hardware (the device "sim" is always there).
                    static bool Available() { return __SpiSupported() == 1 }

                    // The names of the devices: "sim" first, then the real ones ("spidev0.0", ... on Linux, "spi-2" and "spi-3" on an ESP32).
                    static Devices() {
                        var all = __SpiDevices()
                        if (all == undefined) { Spi.Errors.Throw() }
                        var list = new List()
                        for (var i = 0; i < all.length; i++) { list.Add(all[i]) }
                        return list
                    }
                }

                // One device: a bus with a chip select.
                class Device {
                    int handle
                    bool closed
                    string name
                    int mode
                    int speed
                    bool lsbFirst

                    // `device`: a name ("spidev0.0", "spi-2", "spi-2.5" - the number after the dot is the chip select pin on an ESP32, "sim") or the end of a Linux name ("0.0" is "spidev0.0"); `mode` 0..3 is the clock
                    // polarity and phase, `speed` the clock in Hz, `lsbFirst` the bit order.
                    construct(device, int mode = 0, int speed = 1000000, bool lsbFirst = false) {
                        this.handle = -1
                        this.closed = false
                        this.mode = mode
                        this.speed = speed
                        this.lsbFirst = lsbFirst
                        var text = "" + device
                        if (text != "sim" && !text.StartsWith("spi")) { text = "spidev" + text }
                        this.name = text
                        var lsb = 0
                        if (lsbFirst) { lsb = 1 }
                        this.handle = Spi.Errors.Handle(__SpiOpen(text, mode, speed, lsb))
                    }

                    destruct() { try { this.Close() } catch (SpiException e) { } }

                    string Name { get { return this.name } }
                    bool IsClosed { get { return this.closed } }

                    Check() {
                        if (this.closed) { throw new SpiException("The device is closed.", 2) }
                    }

                    // Changes the setup of the open device.
                    Configure(int mode, int speed, bool lsbFirst = false) {
                        this.Check()
                        var lsb = 0
                        if (lsbFirst) { lsb = 1 }
                        if (!__SpiConfigure(this.handle, mode, speed, lsb)) { Spi.Errors.Throw() }
                        this.mode = mode
                        this.speed = speed
                        this.lsbFirst = lsbFirst
                    }

                    int Mode {
                        get { return this.mode }
                        set { this.Configure(value, this.speed, this.lsbFirst) }
                    }

                    int Speed {
                        get { return this.speed }
                        set { this.Configure(this.mode, value, this.lsbFirst) }
                    }

                    bool LsbFirst {
                        get { return this.lsbFirst }
                        set { this.Configure(this.mode, this.speed, value) }
                    }

                    // Sends the bytes and receives as many at the same time; returns the received bytes (a byte[]).
                    Transfer(data) {
                        this.Check()
                        var back = new byte[data.length]
                        if (__SpiTransfer(this.handle, data, 0, back, 0, data.length) < 0) { Spi.Errors.Throw() }
                        return back
                    }

                    // The general form: `count` bytes from `out` (from `outOffset`) are sent, the bytes that come in go to `into` (from `inOffset`).
                    TransferInto(out, int outOffset, into, int inOffset, int count) {
                        this.Check()
                        if (__SpiTransfer(this.handle, out, outOffset, into, inOffset, count) < 0) { Spi.Errors.Throw() }
                    }

                    // Sends `count` bytes of the buffer (all from `offset` on without a count); what comes in is dropped.
                    Write(data, int offset = 0, int count = -1) {
                        this.Check()
                        if (count < 0) { count = data.length - offset }
                        if (__SpiWrite(this.handle, data, offset, count) < 0) { Spi.Errors.Throw() }
                    }

                    // Receives `count` bytes (zeros are sent meanwhile); returns them.
                    Read(int count) {
                        this.Check()
                        var data = new byte[count]
                        if (__SpiRead(this.handle, data, 0, count) < 0) { Spi.Errors.Throw() }
                        return data
                    }

                    // Sends the bytes, then receives `inCount` bytes in the same transfer (the chip select stays low); returns the received ones.
                    WriteRead(data, int inCount) {
                        this.Check()
                        var total = data.length + inCount
                        var out = new byte[total]
                        for (var i = 0; i < data.length; i++) { out[i] = data[i] }
                        var back = new byte[total]
                        if (__SpiTransfer(this.handle, out, 0, back, 0, total) < 0) { Spi.Errors.Throw() }
                        var answer = new byte[inCount]
                        for (var i = 0; i < inCount; i++) { answer[i] = back[data.length + i] }
                        return answer
                    }

                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.handle >= 0) { __SpiClose(this.handle) }
                    }
                }

                // The simulated device "sim" from the outside (for tests, and to try a program without the hardware).
                class Sim {
                    // The device answers with these bytes (one per byte sent, starting with the first - the answer to a command byte comes in the bytes after it, so queue a leading 0 -, then zeros) instead of looping back what was sent.
                    static Reply(data) {
                        if (!__SpiSimReply(data, 0, data.length)) { Spi.Errors.Throw() }
                    }

                    // MISO is tied to MOSI again (what is sent comes back); queued answers are dropped.
                    static Loopback() { __SpiSimLoopback() }

                    // How many bytes the program sent to the device (the first 65536 are kept).
                    static int SentCount() { return __SpiSimSentCount() }

                    // The bytes the program sent to the device since the start or the last Clear.
                    static Sent() {
                        var n = __SpiSimSentCount()
                        var data = new byte[n]
                        for (var i = 0; i < n; i++) { data[i] = __SpiSimSentByte(i) }
                        return data
                    }

                    // The mode, speed and bit order that were set last, and the number of transfers: what a chip would have seen.
                    static int Mode() { return __SpiSimInfo(0) }
                    static int Speed() { return __SpiSimInfo(1) }
                    static bool LsbFirst() { return __SpiSimInfo(2) == 1 }
                    static int Transfers() { return __SpiSimInfo(3) }

                    // Forgets what was sent and the queued answers.
                    static Clear() { __SpiSimClear() }
                }
            }
            """;
    }
}
