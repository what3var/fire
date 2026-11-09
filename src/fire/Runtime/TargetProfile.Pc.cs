namespace fire.Runtime
{
    /// <summary>The targets of the old PC (ROADMAP 13): DOS in real mode and with DPMI, without an operating system (BIOS, UEFI), Windows 3.x and the old
    /// 32-bit Windows. Each one is a platform package of its own (native/platform/&lt;name&gt;/) built with its toolchain; none of them has fire threads.</summary>
    public sealed partial record TargetProfile
    {
        /// <summary>The targets of Open Watcom: only it builds for them.</summary>
        private static readonly string[] Watcom = { "watcom" };

        /// <summary>DOS in real mode from the 8086 on (ROADMAP 13.3): Open Watcom, large model (far pointers: 32 bits), `int` and `float` of 32 bits, the
        /// machine word has 16 bits. fire threads are tasks of fire's own scheduler (std/fire_sched.h, switched at the safepoints, the timer
        /// interrupt asks for it); no windows; the console is DOS's.</summary>
        public static readonly TargetProfile Dos16 = new()
        {
            Name = "dos16", Symbols = new[] { "dos16", "dos" }, HalPackage = "dos16", ProgramExtension = ".exe",
            DisplayName = "DOS (16 bit, real mode)", Category = TargetCategory.Os, Toolchains = Watcom, Optimize = OptimizeFor.Size,
            FloatWidth = 32, IntWidth = 32, WordWidth = 16, DefaultStackBytes = 16384,
            Imports = new[] { NativeImports.Print, "io", "filesystem", NativeImports.Devices, "time", "console", "machine", "dos", "xms", "x86", "graphics", "windows", NativeImports.Reflection, NativeImports.Linq },
            Native = new NativeTarget
            {
                Platform = "dos16", Toolchain = "watcom",
                // (owcc: a DOS program of the huge model - an array or a buffer may hold more than 64 KB (fire_cc_watcom.h) -, for the 8086; 32 KB of stack - the default of 2 KB is too small for the generated code; inlining
                // limited - a function of the real mode must fit one segment of 64 KB, and a long `fire_main` with every fast path inlined does not; the stacks of
                // fire threads lie outside DGROUP: SS is not DS (-zu) and no stack checks - they compare with the program's own stack.
                // Linked with the C library of the large model: the code of the library lies in one segment `_TEXT` of at most 64 KB, and the one of the huge model
                // does not fit (it is about 30 % larger); the calling convention is the same, and the runtime never hands the library a block that passes the end
                // of a segment (fire_cc_watcom.h copies and clears big blocks piece by piece); -zt=16 as on win16: data from 16 bytes on gets far segments of its
                // own - DGROUP (64 KB) holds the stack and the small data, and a program with scene3d had more)
                CompileArgs = new[] { "-bdos", "-mcmodel=h", "-march=i86", "-mstack-size=32768", "-Wc,-oe=20", "-Wc,-zt=16", "-Wc,-zu", "-fno-stack-check",
                    "-Wl,option", "-Wl,stack=32768", "-Wl,option", "-Wl,nodefaultlibs", "-Wl,library", "-Wl,clibl", "-Wl,library", "-Wl,math87l", "-Wl,library", "-Wl,emu87" },
            },
        };

        /// <summary>DOS with a DOS extender from the 386 on (ROADMAP 13.4): Open Watcom (wcc386), flat 32-bit memory - every pointer 32 bits, blocks of any size -, the
        /// extender DOS/32A in the program (`-bdos32a`: one .exe, it switches into the protected mode and serves DPMI). Integers and floats of 64 bits as on the
        /// desktop (the 386 has the room; the results are the ones of the VM). fire threads are tasks of fire's own scheduler as on dos16 (std/sched/fire_ctx_x86_32.h).</summary>
        public static readonly TargetProfile Dos32 = new()
        {
            Name = "dos32", Symbols = new[] { "dos32", "dos" }, HalPackage = "dos32", ProgramExtension = ".exe",
            DisplayName = "DOS (32 bit, DOS/32A)", Category = TargetCategory.Os, Toolchains = Watcom, Optimize = OptimizeFor.Size,
            FloatWidth = 64, IntWidth = 64, WordWidth = 32, DefaultStackBytes = 65536,
            Imports = new[] { NativeImports.Print, "io", "filesystem", NativeImports.Devices, "time", "console", "machine", "dos", "xms", "x86", "graphics", "windows", NativeImports.Reflection, NativeImports.Linq },
            Native = new NativeTarget
            {
                Platform = "dos32", Toolchain = "watcom",
                // (owcc: the 32-bit compiler for the system dos32a - its stub is the whole extender; a stack of 256 KB)
                CompileArgs = new[] { "-bdos32a", "-march=i386", "-mstack-size=262144" },
            },
        };

        /// <summary>Windows 3.x (ROADMAP 13.7): Open Watcom (wcc, -bwindows), the huge model of the 16-bit protected mode as dos16 (arrays over 64 KB), the widths of
        /// dos16. The program starts in WinMain (std/fire_platform_win16.h) and calls the generated main; sleep keeps the messages flowing (the multitasking of
        /// Windows 3.x is cooperative). fire threads are tasks of fire's own scheduler (std/sched/fire_ctx_win16.h: a stack segment each, -zu).</summary>
        public static readonly TargetProfile Win16 = new()
        {
            Name = "win16", Symbols = new[] { "win16", "windows3" }, HalPackage = "win16", ProgramExtension = ".exe",
            DisplayName = "Windows 3.x", Category = TargetCategory.Os, Toolchains = Watcom, Optimize = OptimizeFor.Size,
            FloatWidth = 32, IntWidth = 32, WordWidth = 16, DefaultStackBytes = 16384,
            Imports = new[] { NativeImports.Print, "io", "filesystem", "time", "random", "hash", "json", "compression", "graphics", "windows", "ui", "dialogs",
                NativeImports.Reflection, NativeImports.Linq },
            Native = new NativeTarget
            {
                Platform = "win16", Toolchain = "watcom",
                Entry = new EntryPoint { Name = "fire_win16_main" },
                // (as dos16: the huge model linked with the C library of the large model - here the one of Windows; the 286 is the least Windows 3.x runs on;
                // -zt=16: data from 16 bytes on gets far segments of its own - the stack and the small data share DGROUP, 64 KB, and the UI library has much of it;
                // the stack of 32 KB is wlink's `option stack` - `-mstack-size` does not reach the header of a Windows program, which kept 8 KB: the layout of the UI
                // overflowed it, and the check of the stack then waited in a message box nobody saw)
                CompileArgs = new[] { "-bwindows", "-mcmodel=h", "-march=i286", "-Wc,-oe=20", "-Wc,-zt=16", "-Wc,-zu", "-fno-stack-check",
                    "-Wl,option", "-Wl,stack=32768", "-Wl,option", "-Wl,nodefaultlibs", "-Wl,library", "-Wl,clibl", "-Wl,library", "-Wl,math87l", "-Wl,library", "-Wl,emu87", "-Wl,library", "-Wl,windows", "-Wl,library", "-Wl,shell" },
            },
        };

        /// <summary>A PC without an operating system, started by the BIOS, in real mode from the 8086 on (ROADMAP 13.5): the widths and the huge model of dos16, the
        /// C library of Open Watcom with what it would ask DOS for served by the program itself (platform/bios16: the console of the BIOS and COM1, the conventional
        /// memory as the heap, int 21h for the C library). The build writes a disk image next to the program (`.img`: a FAT12 floppy with FIRE.EXE). No files (those: bios16-dos),
        /// no network, no sound; fire threads over fire's own scheduler as on dos16.</summary>
        public static readonly TargetProfile Bios16 = new()
        {
            Name = "bios16", Symbols = new[] { "bios16", "bios" }, HalPackage = "bios16",
            DisplayName = "BIOS16", Category = TargetCategory.Native, Toolchains = Watcom, Optimize = OptimizeFor.Size,
            FloatWidth = 32, IntWidth = 32, WordWidth = 16, DefaultStackBytes = 16384,
            Imports = new[] { NativeImports.Print, "time", "console", "machine", "graphics", "windows", NativeImports.Devices, NativeImports.Reflection, NativeImports.Linq },
            Native = new NativeTarget
            {
                Platform = "bios16", Toolchain = "watcom",
                // (as dos16 - but the start is the platform's (fire_bios16_start.asm: `_cstart_`, its stack of 32 KB), so no stack size here; floats are calls of
                // the math library (-fpc, mathl): they run with and without an 8087 and change no code - the emulator of DOS (emu87) patches the program's code at
                // run time, and that went wrong without DOS). `nofarcalls`: wlink otherwise turns a far call into the same segment into `push cs; call near`; a near
                // call whose target lies below IP 0 (it wraps around within the 64 KB of CS) is correct on an x86, but QEMU (8.2, TCG with chained blocks) does not
                // wrap the IP and lands 64 KB too low - and QEMU is where the boot images are tested.
                CompileArgs = new[] { "-bdos", "-mcmodel=h", "-march=i86", "-Wc,-fpc", "-Wc,-oe=20", "-Wc,-zu", "-fno-stack-check",
                    "-Wl,option", "-Wl,nodefaultlibs", "-Wl,option", "-Wl,start=_cstart_", "-Wl,option", "-Wl,nofarcalls",
                    "-Wl,library", "-Wl,clibl", "-Wl,library", "-Wl,mathl" },
            },
        };

        /// <summary>bios16 with the mini-DOS (ROADMAP 13.5, the second stage the user chooses): the same platform, and int 21h also serves files - read only, from
        /// the boot floppy (FAT12: FIRE.EXE and the folder `disk` next to the program, Fat12Image) - and the memory of DOS, so io, filesystem and dos run as on
        /// dos16 (platform/bios16/fire_bios16_dos.h).</summary>
        public static readonly TargetProfile Bios16Dos = Bios16 with
        {
            Name = "bios16-dos", Symbols = new[] { "bios16", "bios", "bios16dos" }, DisplayName = "BIOS16 (+IO)",
            Imports = Bios16.Imports.Concat(new[] { "io", "filesystem", "dos" }).ToArray(),
            Native = Bios16.Native with { Defines = new[] { "FIRE_BIOS_MINIDOS=1" } },
        };

        private static TargetProfile[] PcTargets() => new[] { Dos16, Dos32, Win16, Bios16, Bios16Dos };
    }
}
