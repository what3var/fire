namespace fire.Standard
{
    /// <summary>
    /// `#import "i2c"`: the I2C bus as a controller (docs/NETWORK.md, "I2C"). The natives are C++ (native/bridges/fire_bridge_i2c.hpp over plat::i2c: /dev/i2c-N of Linux - Raspberry Pi and other boards -,
    /// the master driver of ESP-IDF on an ESP32); this fire source wraps them in classes in `namespace I2c`.
    ///
    ///   var bus = new I2c.Bus(1)                                  // "i2c-1" (a Raspberry Pi's pins 3 and 5); I2c.Board.Buses() lists them
    ///   print(bus.Scan())                                         // the addresses that answer
    ///   var id = bus.ReadRegister(0x76, 0xD0)                     // write the register number, read one byte back (repeated start)
    ///   bus.WriteRegister(0x76, 0xF4, 0x27)
    ///   var data = bus.ReadRegisters(0x76, 0xF7, 6)               // 6 bytes from register 0xF7 on
    ///   bus.Write(0x3C, buffer)                                   // plain transfers: Write(address, buffer, offset, count), Read(address, count), WriteRead(address, out, inCount)
    ///
    /// Addresses are the 7 bit addresses (0x00 .. 0x7F). A transfer is done in the call (a few bytes take a millisecond) and throws `I2c.NoAckException` when no device answers.
    /// Every platform has the simulated bus "sim": `I2c.Sim.Add(0x50)` puts a device on it whose 256 registers behave like a typical sensor chip (the first byte written is the register, more bytes are stored
    /// from there on, a read counts up from the register), `I2c.Sim.SetRegister(0x50, 0, 42)` changes what it "measures". A program tried on the PC with "sim" runs on the board with the real bus.
    /// Errors are exceptions: `I2c.I2cException` (with a `code`) and its subclasses.
    /// </summary>
    public static class I2cPrelude
    {
        public const string Source = """
            namespace I2c {
                class I2cException : Exception {
                    string message
                    int code

                    construct(string message, int code = 7) {
                        this.message = message
                        this.code = code
                    }
                }

                // No such bus (code 3).
                class NotFoundException : I2cException {
                    construct(string message) : base(message, 3) { }
                }

                // The bus is used by somebody else (code 4).
                class BusyException : I2cException {
                    construct(string message) : base(message, 4) { }
                }

                // No permission to use the bus (code 5; on Linux the user must be in the group i2c).
                class PermissionException : I2cException {
                    construct(string message) : base(message, 5) { }
                }

                // This platform cannot do it (code 6).
                class UnsupportedException : I2cException {
                    construct(string message) : base(message, 6) { }
                }

                // Nobody answered at the address (code 8): no such device, not powered, wrong wiring.
                class NoAckException : I2cException {
                    construct(string message) : base(message, 8) { }
                }

                // The transfer took too long (code 9): a device holds the clock line low.
                class TimeoutException : I2cException {
                    construct(string message) : base(message, 9) { }
                }

                class Errors {
                    // Throws the exception that fits the last error of a native function.
                    static Throw() {
                        var code = __I2cLastError()
                        var message = __I2cLastErrorMessage()
                        if (code == 3) { throw new NotFoundException(message) }
                        if (code == 4) { throw new BusyException(message) }
                        if (code == 5) { throw new PermissionException(message) }
                        if (code == 6) { throw new UnsupportedException(message) }
                        if (code == 8) { throw new NoAckException(message) }
                        if (code == 9) { throw new TimeoutException(message) }
                        throw new I2cException(message, code)
                    }

                    static int Handle(int handle) {
                        if (handle < 0) { Throw() }
                        return handle
                    }
                }

                // The buses of this machine.
                class Board {
                    // true if this machine has I2C hardware (the bus "sim" is always there).
                    static bool Available() { return __I2cSupported() == 1 }

                    // The names of the buses: "sim" first, then the real ones ("i2c-1", ... on Linux, "i2c-0" on an ESP32).
                    static Buses() {
                        var all = __I2cBuses()
                        if (all == undefined) { I2c.Errors.Throw() }
                        var list = new List()
                        for (var i = 0; i < all.length; i++) { list.Add(all[i]) }
                        return list
                    }
                }

                // One bus.
                class Bus {
                    int handle
                    bool closed
                    string name
                    int speed

                    // `bus`: a number (1 is "i2c-1"), a name ("i2c-1", "sim") or the text of a number; `speed` in Hz (the ESP32 uses it; Linux leaves it to the device tree).
                    construct(bus, int speed = 100000) {
                        this.handle = -1
                        this.closed = false
                        this.speed = speed
                        var text = "" + bus
                        if (text != "sim" && !text.StartsWith("i2c-")) { text = "i2c-" + text }
                        this.name = text
                        this.handle = I2c.Errors.Handle(__I2cOpen(text, speed))
                    }

                    destruct() { try { this.Close() } catch (I2cException e) { } }

                    string Name { get { return this.name } }
                    bool IsClosed { get { return this.closed } }

                    int Speed {
                        get { return this.speed }
                        set {
                            this.Check()
                            if (!__I2cSetSpeed(this.handle, value)) { I2c.Errors.Throw() }
                            this.speed = value
                        }
                    }

                    Check() {
                        if (this.closed) { throw new I2cException("The bus is closed.", 2) }
                    }

                    // Writes `count` bytes of the buffer (all of it from `offset` on without a count) to the device.
                    Write(int address, buffer, int offset = 0, int count = -1) {
                        this.Check()
                        if (count < 0) { count = buffer.length - offset }
                        if (__I2cWrite(this.handle, address, buffer, offset, count) < 0) { I2c.Errors.Throw() }
                    }

                    WriteByte(int address, int value) {
                        var b = new byte[1]
                        b[0] = value & 255
                        this.Write(address, b)
                    }

                    // Reads `count` bytes from the device into the buffer.
                    ReadInto(int address, buffer, int offset, int count) {
                        this.Check()
                        if (__I2cRead(this.handle, address, buffer, offset, count) < 0) { I2c.Errors.Throw() }
                    }

                    // Reads `count` bytes from the device.
                    Read(int address, int count) {
                        var data = new byte[count]
                        this.ReadInto(address, data, 0, count)
                        return data
                    }

                    // Writes `outCount` bytes (without: all), then reads `inCount` bytes with a repeated start in between (no other master can step in); returns the bytes read.
                    WriteRead(int address, buffer, int inCount, int outCount = -1) {
                        this.Check()
                        if (outCount < 0) { outCount = buffer.length }
                        var data = new byte[inCount]
                        if (__I2cWriteRead(this.handle, address, buffer, 0, outCount, data, 0, inCount) < 0) { I2c.Errors.Throw() }
                        return data
                    }

                    // One byte of a register: writes the register number, reads the value.
                    int ReadRegister(int address, int register) {
                        var r = new byte[1]
                        r[0] = register & 255
                        var data = this.WriteRead(address, r, 1)
                        return data[0]
                    }

                    // `count` bytes from the register on (the device counts the register up).
                    ReadRegisters(int address, int register, int count) {
                        var r = new byte[1]
                        r[0] = register & 255
                        return this.WriteRead(address, r, count)
                    }

                    WriteRegister(int address, int register, int value) {
                        var b = new byte[2]
                        b[0] = register & 255
                        b[1] = value & 255
                        this.Write(address, b)
                    }

                    // Writes the bytes into the registers from `register` on.
                    WriteRegisters(int address, int register, data) {
                        var b = new byte[data.length + 1]
                        b[0] = register & 255
                        for (var i = 0; i < data.length; i++) { b[i + 1] = data[i] }
                        this.Write(address, b)
                    }

                    // true if a device answers at the address.
                    bool Probe(int address) {
                        this.Check()
                        var r = __I2cProbe(this.handle, address)
                        if (r < 0) { I2c.Errors.Throw() }
                        return r == 1
                    }

                    // The addresses (a list of numbers) that answer: 0x08 .. 0x77 are the addresses of devices, the rest is reserved.
                    Scan() {
                        var found = new List()
                        for (var a = 8; a < 120; a++) {
                            if (this.Probe(a)) { found.Add(a) }
                        }
                        return found
                    }

                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.handle >= 0) { __I2cClose(this.handle) }
                    }
                }

                // The simulated bus "sim" from the outside: the devices on it (for tests, and to try a program without the hardware).
                class Sim {
                    // Puts a device with 256 registers (all 0) at the address; an existing one is replaced.
                    static Add(int address) {
                        if (!__I2cSimAdd(address)) { I2c.Errors.Throw() }
                    }

                    static Remove(int address) {
                        if (!__I2cSimRemove(address)) { I2c.Errors.Throw() }
                    }

                    // Sets a register of a device (what a sensor measures).
                    static SetRegister(int address, int register, int value) {
                        if (!__I2cSimSetRegister(address, register, value)) { I2c.Errors.Throw() }
                    }

                    // A register of a device (what a program wrote there).
                    static int GetRegister(int address, int register) {
                        var v = __I2cSimGetRegister(address, register)
                        if (v < 0) { I2c.Errors.Throw() }
                        return v
                    }

                    // Takes all devices off the bus.
                    static Reset() { __I2cSimReset() }
                }
            }
            """;
    }
}
