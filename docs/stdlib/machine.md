# The hardware of the PC (`#import "machine"`)

The package `fire-machine` reaches the hardware of the PC directly: I/O ports, physical memory, the processor and hardware interrupts - and, built on
them, the CMOS clock, the PC speaker and the configuration space of PCI devices. It works where a program owns the machine: on `dos16` (DOS in real
mode); `dos32` and `bios-x86` will follow. Everywhere else (an operating system, a board, the virtual machine) every call is a `Machine.MachineException`
that says so.

```
#import "machine"

print(Machine.Cpu.Level() + " " + Machine.Cpu.Vendor())        // 586 GenuineIntel
print(Machine.Memory.Peek16(0x400))                              // the port of COM1 from the BIOS data area
Machine.Memory.Poke16(0xB8000, 0x1E41)                           // a yellow 'A' on blue, top left of the text screen
var c = Machine.Cmos.Clock()                                     // [year, month, day, hour, minute, second]
Machine.Speaker.Tone(440)

var com1 = new Machine.Irq(4)                                    // IRQ 4: the serial port
fire {
    while (true) {
        com1.Wait()                                              // waits for the next interrupt; other fire threads go on
        print(Machine.Port.In8(0x3F8))                           // serve the device: read its data
    }
}
```

## Reference

| Member | Meaning |
|---|---|
| `Machine.Port.In8/In16/In32(port)`, `Out8/Out16/Out32(port, value)` | I/O ports 0..65535; 32 bits need a 386 or later |
| `Machine.Port.In(port, bits)`, `Out(port, bits, value)` | the same with the width as an argument (8, 16, 32) |
| `Machine.Memory.Peek8/16/32(address)`, `Poke8/16/32(address, value)`, `Peek/Poke(address, bits, ...)` | physical memory, little endian; in real mode the first megabyte and the 64 KB after it (the HMA, when the A20 line is on) |
| `Machine.Memory.Read(address, count)`, `Write(address, bytes)` | a range as a byte buffer |
| `Machine.Cpu.Level()` | 86 (8086/8088), 286, 386, 486, or 586 for a processor with CPUID |
| `Machine.Cpu.HasCpuid()`, `Machine.Cpu.Id(leaf, subleaf = 0)`, `Machine.Cpu.Vendor()` | CPUID: `[EAX, EBX, ECX, EDX]`; the vendor string ("GenuineIntel", "AuthenticAMD"; undefined without CPUID) |
| `Machine.Cpu.DisableInterrupts()`, `EnableInterrupts()` | CLI / STI - off only briefly: the clock and the keyboard stop meanwhile |
| `new Machine.Irq(line, chain = false)` | a hardware interrupt line 0..15: its interrupts are counted from now on and the line is unmasked; `chain`: the handler that was there before runs as well and acknowledges the interrupt (a line someone else serves too, like IRQ 0 of the clock); without it this one acknowledges the interrupt controller |
| `irq.Count()`, `irq.Wait(timeoutMs = -1)` | the interrupts since the last call; Wait waits for at least one (0: none came in the time) |
| `irq.Close()` | the handler and the mask as they were (also when the object ends, and for every line at the end of the program) |
| `Machine.Cmos.Read(register)`, `Write(register, value)`, `Machine.Cmos.Clock()` | the CMOS memory (0..127); the battery clock as `[year, month, day, hour, minute, second]` (BCD or binary, as the clock keeps it) |
| `Machine.Speaker.Tone(hz)`, `Machine.Speaker.Off()` | the PC speaker through channel 2 of the timer chip |
| `Machine.Pci.Read32/Write32(bus, device, function, offset)`, `Present(bus, device, function)`, `Address(...)` | the configuration space of PCI devices (mechanism 1: ports CF8h/CFCh) |
| `Machine.MachineException` | no access to the hardware here, an address out of reach, an IRQ that is open already, 32 bits on a 286 |

**Interrupts.** fire code never runs inside an interrupt: the handler of the package only counts and acknowledges, and the program serves the device when
it asks (`Count`, `Wait` - typically in a fire thread of its own). A device that keeps its line active until it is served raises no new interrupt
meanwhile; read its status when `Wait` returns. The vectors and masks are put back when the program ends, also after an exception.

The hardware is a part of the platform layer (`native/platform/<platform>/fire_machine.h`, the interface in `native/platform/std/fire_machine_c.h`;
`std/fire_machine_x86_16.h` for the real mode).
