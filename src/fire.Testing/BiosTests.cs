using fire.Compiler;
using fire.Runtime;
using fire.Values;

/// <summary>bios16 (ROADMAP 13.5): a PC without an operating system in real mode - the boot sector loads the program (a DOS executable) from the floppy, the minimal
/// C library of the platform answers int 21h itself; run in QEMU, the output over COM1, the end through the isa-debug-exit port. Part of the native tests, and alone
/// with FIRE_TESTS_ONLY=bios16.</summary>
static class BiosTests
{
    /// <summary>The program of the dos16 test (objects, strings, units, exceptions, word, a fire thread) - the same here.</summary>
    public const string DosSource = """
        class Animal { string name
            construct(string n) { this.name = n }
            string Speak() { return this.name + " says hi" } }
        var a = new Animal("Rex")
        print(a.Speak())
        int big = 100000
        print(big * 3 + " " + (7.5 / 2) + " " + 5cm + 3mm)
        var list = [3, 1, 2]
        var sum = 0
        foreach (x in list) { sum += x }
        print(sum + " " + list.length)
        try { throw new Exception("boom") } catch (e) { print("caught " + e.message) }
        word w = 65535
        print(w + 1)
        var done = 0
        fire { var t = 0
          for (var i = 0; i < 2000; i++) { t = t + i }
          sync global { done = t } }
        while (done == 0) { sync globals }
        print("thread " + done)
        """;

    public static void RunAlone()
    {
        int failures = 0;
        string workDir = Path.Combine(Path.GetTempPath(), "fire-bios-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        Run((title, ok, detail) =>
        {
            if (!ok) failures++;
            Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}" + (detail == null ? "" : $"\n{detail}"));
        }, workDir, DosSource);
        try { Directory.Delete(workDir, true); } catch (IOException) { }
        Console.WriteLine(failures == 0 ? "Alle bios16-Pruefungen bestanden." : $"FEHLER: {failures} bios16-Pruefung(en) fehlgeschlagen.");
        if (failures > 0) Environment.ExitCode = 1;
    }

    public delegate void Checker(string title, bool ok, string? detail = null);

    public static void Run(Checker CheckNat, string workDir, string dosSource)
    {
        var bios16 = TargetProfile.Bios16;
        CheckNat("bios16: Zielprofil (Plattform bios16, Toolchain watcom, eigener Start, int/float 32, word 16, Threads ueber den eigenen Scheduler, nofarcalls)",
            bios16.Native.Platform == "bios16" && bios16.Native.Toolchain == "watcom" && bios16.IntWidth == 32 && bios16.FloatWidth == 32 && bios16.WordWidth == 16
            && bios16.Native.SupportsThreads && bios16.Native.CompileArgs.Contains("-Wl,start=_cstart_") && bios16.Native.CompileArgs.Contains("-Wl,nofarcalls"));
        string? biosDir = null;
        for (var dirInfo = new DirectoryInfo(AppContext.BaseDirectory); dirInfo != null && biosDir == null; dirInfo = dirInfo.Parent)
            if (File.Exists(Path.Combine(dirInfo.FullName, "native", "platform", "bios16", "fire_bios16_boot.asm"))) biosDir = Path.Combine(dirInfo.FullName, "native", "platform", "bios16");
        string? biosNasm = fire.Native.ToolchainSetup.FindProgram("nasm"), biosQemu = fire.Native.ToolchainSetup.FindProgram("qemu-system-i386");
        CheckNat("bios16: der Boot-Sektor ist 512 Bytes lang und endet mit 55 AA", fire.Native.BiosBootSectors.Bios16.Length == 512
            && fire.Native.BiosBootSectors.Bios16[510] == 0x55 && fire.Native.BiosBootSectors.Bios16[511] == 0xAA);
        if (biosNasm == null || biosDir == null) Console.WriteLine("SKIP: bios16 Boot-Sektor gegen die Quelle - nasm oder native/platform/bios16 fehlt");
        else
        {
            string bootDir = Path.Combine(workDir, "bios16boot");
            Directory.CreateDirectory(bootDir);
            var asm = TestProcess.Run(biosNasm, $"-f bin \"{Path.Combine(biosDir, "fire_bios16_boot.asm")}\" -o \"{Path.Combine(bootDir, "boot.bin")}\"", bootDir);
            byte[] assembled = File.Exists(Path.Combine(bootDir, "boot.bin")) ? File.ReadAllBytes(Path.Combine(bootDir, "boot.bin")) : Array.Empty<byte>();
            CheckNat("bios16: der eingebaute Boot-Sektor ist die Ausgabe von nasm fuer fire_bios16_boot.asm", assembled.SequenceEqual(fire.Native.BiosBootSectors.Bios16),
                $"  nasm: {asm.Exit} {asm.Stderr}");
        }
        var biosWatcom = new fire.Native.WatcomToolchain();
        if (biosWatcom.Locate(fire.Native.ToolchainDef.BuiltIn["watcom"]) == null || biosQemu == null)
            Console.WriteLine("SKIP: bios16 bauen und in QEMU booten - Open Watcom (owcc) oder qemu-system-i386 fehlt");
        else
        {
            // builds the program and its boot image (PROG.img: boot sector + program, a floppy of 1.44 MB) and boots it in QEMU (at most 2 minutes)
            (bool Ok, string Text) RunBios16(string source, string folder)
            {
                string dir = Path.Combine(workDir, folder);
                Directory.CreateDirectory(dir);
                var built = fire.Compiler.NativeBuilder.Build(new[] { source }, new fire.Native.NativeConfig(), bios16, fire.Native.ToolchainDef.BuiltIn["watcom"], Path.Combine(dir, "PROG.EXE"), VmExecutionMode.Release);
                if (!built.Ok) return (false, built.Log);
                string image = Path.Combine(dir, "PROG.img"), serial = Path.Combine(dir, "COM1.TXT");
                if (!File.Exists(image)) return (false, "kein Boot-Image\n" + built.Log);
                var run = TestProcess.Run(biosQemu, $"-display none -serial file:\"{serial}\" -device isa-debug-exit,iobase=0xf4,iosize=1 -drive file=\"{image}\",format=raw,if=floppy -boot a -no-reboot",
                    dir, null, null, 120_000);
                string text = File.Exists(serial) ? File.ReadAllText(serial).Replace("\r", "") : "(keine Ausgabe)";
                // (isa-debug-exit: QEMU ends with (code << 1) | 1 - 1 is the end with code 0)
                return (true, run.Exit == -1 ? text + "(QEMU nach 2 Minuten beendet)" : run.Exit != 1 ? text + $"(QEMU-Ende {run.Exit})" : text);
            }
            string BiosVm(string source)
            {
                var linked = new Linker().CompileAndLink(new[] { source }, null, null, VmExecutionMode.Release, null, bios16);
                var lines = new List<string>();
                try { fire.Runtime.Session.Build(linked, VmExecutionMode.Release, a => { lines.Add(a[0].ToString()!); return Value.MakeUndefined(); }).Run(); }
                finally { Value.WordBits = 64; Value.Ints32 = false; Value.Ints16 = false; Value.SingleFloats = false; }
                return string.Concat(lines.Select(l => l + "\n"));
            }
            string biosVm = BiosVm(dosSource);
            var biosRun = RunBios16(dosSource, "bios16");
            CheckNat("bios16: mit Open Watcom gebaut, von der Diskette in QEMU gebootet - dieselbe Ausgabe wie die VM (Objekte, Einheiten, Ausnahmen, word, ein fire-Thread)",
                biosRun.Ok && biosRun.Text == biosVm && biosVm.Length > 0, $"  VM:\n{biosVm}\n  QEMU:\n{biosRun.Text}");
            // the timer of the BIOS (INT 1Ch) takes the turn from a thread that never waits; Sleep; int of 32 bits that overflows as in the VM
            const string biosThreads = """
                #import "time"
                print("start")
                fire { var t = 0
                  for (var i = 0; i < 200000; i++) { t = t + i }
                  print("thread " + t) }
                var n = 0
                for (var k = 0; k < 300000; k++) { n = n + 1 }
                print("main " + n)
                Sleep(300ms)
                print("ende")
                """;
            var biosThr = RunBios16(biosThreads, "bios16thr");
            var thrLines = biosThr.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            CheckNat("bios16: ein Thread neben dem Hauptprogramm (Zeitscheiben ueber INT 1Ch), Sleep, int von 32 Bit",
                biosThr.Ok && thrLines.Length == 4 && thrLines[0] == "start" && thrLines[3] == "ende" && thrLines.Contains("thread -1474936480") && thrLines.Contains("main 300000"),
                $"  QEMU:\n{biosThr.Text}");
        }
    }
}
