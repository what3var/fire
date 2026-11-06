# The services of DOS (`#import "dos"`) and extended memory (`#import "xms"`)

Two small packages for programs that run under DOS (`dos16`; `dos32` will follow): `fire-dos` calls any interrupt with the registers of the 8086 - the
functions of DOS (`int 21h`) and of the BIOS (`int 10h`, `13h`, `16h`, `1Ah`, ...) - and offers the memory and the interrupt vectors of DOS on top of it.
`fire-xms` reaches the memory above 1 MB through the XMS driver (HIMEM.SYS; DOSBox has one). Elsewhere every call is a `DosException` / `XmsException`.
For the hardware itself (ports, physical memory, IRQs) see [the machine package](machine.md).

```
#import "dos"
#import "xms"

var r = Dos.Interrupt(0x1A, new DosRegisters(0))   // BIOS: the ticks since midnight in CX:DX
print(r.cx * 65536 + r.dx)

var name = new byte[64]                           // DOS 47h: the current directory into DS:SI
var q = new DosRegisters(0x4700)
q.ds = Dos.SegmentOf(name)
q.si = Dos.OffsetOf(name)
Dos.Call(q)
if (q.Carry) { print("error " + q.ax) }

var block = new XmsBlock(1024)                    // 1 MB above the first megabyte
block.Write(0, bigBuffer)
var part = block.Read(4096, 512)
block.Close()
```

## `dos`

| Member | Meaning |
|---|---|
| `new DosRegisters()`, `new DosRegisters(ax)` | `ax`, `bx`, `cx`, `dx`, `si`, `di`, `ds`, `es` (0: the program's own data segment), `flags` after the call; `Carry`, and the halves `Ah`, `Al`, `Bh`, `Bl`, `Ch`, `Cl`, `Dh`, `Dl` |
| `Dos.Interrupt(number, registers)` | INT `number` (0..255); the registers hold what the function gave back (and are returned) |
| `Dos.Call(registers)` | INT 21h |
| `Dos.Version()` | `[major, minor]` |
| `Dos.Alloc(paragraphs)`, `Dos.Free(segment)`, `Dos.LargestFree()` | blocks of DOS memory (16-byte paragraphs) |
| `Dos.GetVector(number)`, `Dos.SetVector(number, address)` | an interrupt vector as segment * 65536 + offset (set only addresses that stay valid; a handler of your own: `Machine.Irq`) |
| `Dos.FarOf(buffer, offset = 0)`, `Dos.SegmentOf(...)`, `Dos.OffsetOf(...)` | where the bytes of a buffer are, for a function that wants a buffer in DS:DX, ES:DI, ... (valid while the buffer lives) |
| `DosException` | no DOS here; `Free` of a segment that is no block; `Alloc` of more than there is (the message says how much there is) |

## `xms`

| Member | Meaning |
|---|---|
| `Xms.Available()`, `Xms.Version()`, `Xms.FreeKb()`, `Xms.LargestKb()` | the driver: there?, its version (`[3, 0]`), the free memory and the largest block in KB |
| `new XmsBlock(kb)` | a block of 1..65535 KB (`XmsException` when there is not that much) |
| `block.Write(offset, data, at = 0, count = -1)` | bytes of a buffer into the block |
| `block.Read(offset, count)`, `block.ReadInto(offset, data, at = 0, count = -1)` | bytes of the block into a new buffer / into a buffer |
| `block.Kb`, `block.Size` | the size in KB / bytes |
| `block.Close()` | gives the block back (also when the object ends, and for every block at the end of the program - else DOS would lose it until it starts again) |
| `XmsException` | no driver; the error of the driver (`code`, and its text) |

A move may be of any length and at any offset (the driver moves an even number of bytes: an odd last byte goes through a word read from the block and
written back), and a buffer may be larger than 64 KB.

Both are parts of the platform layer (`native/platform/<platform>/fire_dos.h`, the interface in `native/platform/std/fire_dos_c.h`; `std/fire_dos_real.h`
for the real mode).
