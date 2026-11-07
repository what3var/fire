namespace fire.Runtime
{
    /// <summary>The targets of the old PC (ROADMAP 13): DOS in real mode and with DPMI, without an operating system (BIOS, UEFI), Windows 3.x and the old
    /// 32-bit Windows. Each one is a platform package of its own (native/platform/&lt;name&gt;/) built with its toolchain; none of them has fire threads.</summary>
    public sealed partial record TargetProfile
    {
        /// <summary>DOS in real mode from the 8086 on (ROADMAP 13.3): Open Watcom, large model (far pointers: 32 bits, so with `int` and `float` of 32 bits a
        /// value takes 8 bytes), the machine word has 16 bits. fire threads are tasks of fire's own scheduler (std/fire_sched.h, switched at the safepoints, the timer
        /// interrupt asks for it); no windows; the console is DOS's.</summary>
        public static readonly TargetProfile Dos16 = new()
        {
            Name = "dos16", Symbols = new[] { "dos16", "dos" }, HalPackage = "dos16",
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
                // of a segment (fire_cc_watcom.h copies and clears big blocks piece by piece))
                CompileArgs = new[] { "-bdos", "-mcmodel=h", "-march=i86", "-mstack-size=32768", "-Wc,-oe=20", "-Wc,-zu", "-fno-stack-check",
                    "-Wl,option", "-Wl,nodefaultlibs", "-Wl,library", "-Wl,clibl", "-Wl,library", "-Wl,math87l", "-Wl,library", "-Wl,emu87" },
            },
        };

        /// <summary>DOS with a DOS extender from the 386 on (ROADMAP 13.4): Open Watcom (wcc386), flat 32-bit memory - every pointer 32 bits, blocks of any size -, the
        /// extender DOS/32A in the program (`-bdos32a`: one .exe, it switches into the protected mode and serves DPMI). Integers and floats of 64 bits as on the
        /// desktop (the 386 has the room; the results are the ones of the VM); no fire threads yet.</summary>
        public static readonly TargetProfile Dos32 = new()
        {
            Name = "dos32", Symbols = new[] { "dos32", "dos" }, HalPackage = "dos32",
            FloatWidth = 64, IntWidth = 64, WordWidth = 32, DefaultStackBytes = 65536,
            Imports = new[] { NativeImports.Print, "io", "filesystem", NativeImports.Devices, "time", "console", "machine", "dos", "xms", "x86", "graphics", "windows", NativeImports.Reflection, NativeImports.Linq },
            Native = new NativeTarget
            {
                Platform = "dos32", Toolchain = "watcom", SupportsThreads = false,
                // (owcc: the 32-bit compiler for the system dos32a - its stub is the whole extender; a stack of 256 KB)
                CompileArgs = new[] { "-bdos32a", "-march=i386", "-mstack-size=262144" },
            },
        };

        private static TargetProfile[] PcTargets() => new[] { Dos16, Dos32 };
    }
}
