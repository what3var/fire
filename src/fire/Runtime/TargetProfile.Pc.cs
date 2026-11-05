namespace fire.Runtime
{
    /// <summary>The targets of the old PC (ROADMAP 13): DOS in real mode and with DPMI, without an operating system (BIOS, UEFI), Windows 3.x and the old
    /// 32-bit Windows. Each one is a platform package of its own (native/platform/&lt;name&gt;/) built with its toolchain; none of them has fire threads.</summary>
    public sealed partial record TargetProfile
    {
        /// <summary>DOS in real mode from the 8086 on (ROADMAP 13.3): Open Watcom, large model (far pointers: 32 bits, so with `int` and `float` of 32 bits a
        /// value takes 8 bytes), the machine word has 16 bits. No fire threads, no windows; the console is DOS's.</summary>
        public static readonly TargetProfile Dos16 = new()
        {
            Name = "dos16", Symbols = new[] { "dos16", "dos" }, HalPackage = "dos16",
            FloatWidth = 32, IntWidth = 32, WordWidth = 16, DefaultStackBytes = 4096,
            Imports = new[] { NativeImports.Print, "time", NativeImports.Reflection, NativeImports.Linq },
            Native = new NativeTarget
            {
                Platform = "dos16", SupportsThreads = false, Toolchain = "watcom",
                // (owcc: a DOS program of the large model, for the 8086; 32 KB of stack - the default of 2 KB is too small for the generated code; inlining
                // limited - a function of the real mode must fit one segment of 64 KB, and a long `fire_main` with every fast path inlined does not)
                CompileArgs = new[] { "-bdos", "-mcmodel=l", "-march=i86", "-mstack-size=32768", "-Wc,-oe=20" },
            },
        };

        private static TargetProfile[] PcTargets() => new[] { Dos16 };
    }
}
