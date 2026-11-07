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
        // the choice of targets: groups, display names, the toolchains a target allows (gcc chosen for dos16 builds with watcom)
        var nativeConfig = new fire.Native.NativeConfig();
        CheckNat("Ziele: Gruppen und Klarnamen (BIOS16 (+IO) unter Native, DOS unter OS, ESP32 unter Embedded)",
            TargetProfile.Bios16Dos.Title == "BIOS16 (+IO)" && TargetProfile.Bios16Dos.Category == TargetCategory.Native && TargetProfile.Dos16.Category == TargetCategory.Os
            && TargetProfile.TryGet("esp32", out var esp) && esp.Category == TargetCategory.Embedded);
        CheckNat("Ziele: eine Toolchain, die das Ziel nicht baut, wird nicht genommen (dos16 mit gcc -> watcom; linux mit gcc bleibt gcc; files immer)",
            nativeConfig.ResolveToolchain(TargetProfile.Dos16, "gcc").EffectiveKind == "watcom" && nativeConfig.ResolveToolchain(TargetProfile.Linux, "gcc").EffectiveKind == "gcc"
            && nativeConfig.ResolveToolchain(TargetProfile.Dos16, "files").EffectiveKind == "files");
        // Open Watcom portable: the archive is unpacked (the WATCOM folder found inside it, the compilers executable); without Open Watcom a target of it asks for it
        // (download / change / cancel) instead of taking another compiler
        {
            string owDir = Path.Combine(workDir, "owzip");
            Directory.CreateDirectory(owDir);
            string zip = Path.Combine(owDir, "ow.zip");
            using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                z.CreateEntry("ow2/h/stdio.h").Open().Dispose();
                z.CreateEntry("ow2/binl64/owcc").Open().Dispose();
                z.CreateEntry("ow2/binnt64/owcc.exe").Open().Dispose();
                z.CreateEntry("ow2/lib286/dos/clibl.lib").Open().Dispose();
            }
            string target = Path.Combine(owDir, "watcom");
            string? unpacked = null, error = null;
            try { unpacked = fire.Native.WatcomSetup.Unpack(zip, null, target); } catch (InvalidOperationException e) { error = e.Message; }
            CheckNat("Open Watcom portable: das Archiv wird entpackt (der WATCOM-Ordner darin wird Toolchain/watcom, h und lib286 dabei)",
                unpacked == target && File.Exists(Path.Combine(target, "h", "stdio.h")) && File.Exists(Path.Combine(target, "lib286", "dos", "clibl.lib"))
                && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(Path.Combine(target, "binl64", "owcc")) & UnixFileMode.UserExecute) != 0), error);
            var missing = new fire.Native.ToolchainDef { Kind = "watcom", Compiler = Path.Combine(owDir, "nowhere", "owcc") };
            fire.Native.ToolchainRequest? asked = null;
            var oldAsk = fire.Native.ToolchainProvider.Ask;
            fire.Native.ToolchainProvider.Ask = r => { asked = r; return fire.Native.ToolchainChoice.Cancel; };
            fire.Native.ToolchainDef? got;
            try { got = fire.Native.ToolchainProvider.Require(missing, "test"); }
            finally { fire.Native.ToolchainProvider.Ask = oldAsk; }
            CheckNat("Open Watcom fehlt: der Benutzer wird gefragt (Open Watcom laden), kein anderer Compiler springt ein",
                got == null && asked is { IsWatcom: true } && asked.InstallDirectory == fire.Native.WatcomSetup.Directory, asked?.Message);
        }
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
        // the boot image is a FAT12 floppy: FIRE.EXE first and in one piece, folders, 8.3 names; fsck.fat and mtools (when they are there) read it
        {
            var exe = new byte[3000];
            for (int i = 0; i < exe.Length; i++) exe[i] = (byte)i;
            var files = new List<fire.Native.Fat12Image.Entry>
            {
                new() { Name = "README.TXT", Data = System.Text.Encoding.ASCII.GetBytes("Hallo") },
                new() { Name = "DATA", Children = new() { new() { Name = "BIG.BIN", Data = new byte[5000] }, new() { Name = "EMPTY" , Data = Array.Empty<byte>() } } },
            };
            byte[] img = fire.Native.Fat12Image.Create(fire.Native.BiosBootSectors.Bios16, exe, files);
            int root = 19 * 512;
            bool exeFirst = System.Text.Encoding.ASCII.GetString(img, root, 11) == "FIRE    EXE" && BitConverter.ToUInt16(img, root + 26) == 2
                && BitConverter.ToInt32(img, root + 28) == 3000 && img.AsSpan(33 * 512, 3000).SequenceEqual(exe);
            string? badName = null;
            try { fire.Native.Fat12Image.Create(fire.Native.BiosBootSectors.Bios16, exe, new List<fire.Native.Fat12Image.Entry> { new() { Name = "LONGNAME123.TXT", Data = new byte[1] } }); }
            catch (InvalidOperationException e) { badName = e.Message; }
            CheckNat("bios16: das Boot-Image ist eine FAT12-Diskette (1,44 MB) - FIRE.EXE zuerst und am Stueck ab Cluster 2, Ordner, ein langer Name ist ein Fehler",
                img.Length == 1474560 && exeFirst && badName != null && badName.Contains("8.3"), badName);
            string? fsck = fire.Native.ToolchainSetup.FindProgram("fsck.fat"), mtype = fire.Native.ToolchainSetup.FindProgram("mtype");
            string fatDir = Path.Combine(workDir, "bios16fat");
            Directory.CreateDirectory(fatDir);
            string imgFile = Path.Combine(fatDir, "disk.img");
            File.WriteAllBytes(imgFile, img);
            if (fsck == null || mtype == null) Console.WriteLine("SKIP: bios16 FAT12 mit fsck.fat/mtools pruefen - dosfstools oder mtools fehlen");
            else
            {
                var check = TestProcess.Run(fsck, $"-n \"{imgFile}\"", fatDir);
                var typed = TestProcess.Run(mtype, $"-i \"{imgFile}\" ::/README.TXT", fatDir, null, new Dictionary<string, string?> { ["MTOOLS_SKIP_CHECK"] = "1" });
                CheckNat("bios16: fsck.fat findet nichts, mtype liest die Datei", check.Exit == 0 && !check.Stdout.Contains("Auto-") && typed.Stdout == "Hallo",
                    $"  fsck.fat ({check.Exit}):\n{check.Stdout}{check.Stderr}\n  mtype: {typed.Stdout}{typed.Stderr}");
            }
        }
        var biosWatcom = new fire.Native.WatcomToolchain();
        if (biosWatcom.Locate(fire.Native.ToolchainDef.BuiltIn["watcom"]) == null || biosQemu == null)
            Console.WriteLine("SKIP: bios16 bauen und in QEMU booten - Open Watcom (owcc) oder qemu-system-i386 fehlt");
        else
        {
            // builds the program and its boot image (PROG.img: boot sector + program, a floppy of 1.44 MB) and boots it in QEMU (at most 2 minutes)
            // (the files of `disk` go into the folder disk next to the program: onto the floppy)
            (bool Ok, string Text) RunBios16(string source, string folder, TargetProfile? target = null, IReadOnlyDictionary<string, string>? disk = null)
            {
                string dir = Path.Combine(workDir, folder);
                Directory.CreateDirectory(dir);
                if (disk != null)
                    foreach (var (name, content) in disk)
                    {
                        string file = Path.Combine(dir, "disk", name);
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        File.WriteAllText(file, content);
                    }
                var built = fire.Compiler.NativeBuilder.Build(new[] { source }, new fire.Native.NativeConfig(), target ?? bios16, fire.Native.ToolchainDef.BuiltIn["watcom"], Path.Combine(dir, "PROG.EXE"), VmExecutionMode.Release);
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

            // ---- bios16-dos: the mini-DOS - files read from the floppy (io/filesystem with the paths of DOS), the folder, the current directory, writing
            // refused; the memory and the version of DOS through the package dos
            var bios16Dos = TargetProfile.Bios16Dos;
            CheckNat("bios16-dos: Zielprofil (bios16 mit FIRE_BIOS_MINIDOS, dazu io, filesystem und dos)", bios16Dos.Native.Platform == "bios16"
                && bios16Dos.Native.Defines.Contains("FIRE_BIOS_MINIDOS=1") && bios16Dos.Imports.Contains("io") && bios16Dos.Imports.Contains("dos") && bios16Dos.MacroName == "BIOS16_DOS");
            const string miniDos = """
                #import "io"
                #import "filesystem"
                #import "dos"
                print("exists " + IO.File.Exists("README.TXT") + " " + IO.File.Exists("A:\\DATA\\ZAHLEN.TXT") + " " + IO.File.Exists("nope.txt"))
                print(IO.File.ReadAllText("readme.txt"))
                var lines = IO.File.ReadAllLines("data/zahlen.txt")
                print("lines " + lines.Count + " " + lines[999])
                var names = IO.Directory.GetFiles("A:\\")
                foreach (n in names) { print(n) }
                print("dirs " + IO.Directory.GetDirectories("\\").Count)
                IO.Directory.SetCurrentDirectory("DATA")
                print("cwd " + IO.Directory.GetCurrentDirectory() + " " + IO.File.Exists("ZAHLEN.TXT"))
                IO.Directory.SetCurrentDirectory("..")
                try { IO.File.WriteAllText("x.txt", "a") } catch (e) { print("write: " + e.message) }
                var v = Dos.Version()
                print("dos " + v[0] + "." + v[1])
                var seg = Dos.Alloc(100)
                print("alloc " + (seg > 0) + " " + (Dos.LargestFree() > 1000))
                Dos.Free(seg)
                try { Dos.Alloc(65000) } catch (e) { print("too big") }
                """;
            var numbers = string.Concat(Enumerable.Range(1, 1000).Select(i => $"Zahl {i}\r\n"));
            var dosRun = RunBios16(miniDos, "bios16dos", bios16Dos, new Dictionary<string, string> { ["README.TXT"] = "Hallo vom Boot-Image", ["DATA/ZAHLEN.TXT"] = numbers });
            const string miniDosExpected = "exists True True False\nHallo vom Boot-Image\nlines 1000 Zahl 1000\nA:\\FIRE.EXE\nA:\\README.TXT\ndirs 1\ncwd A:\\DATA True\n"
                + "write: Access to the path 'A:\\X.TXT' is denied.\ndos 5.0\nalloc True True\ntoo big\n";
            CheckNat("bios16-dos: das Mini-DOS - Dateien von der Diskette (ueber mehrere Cluster, Unterordner, Muster, aktueller Ordner), Schreiben verweigert, Speicher und Version von DOS",
                dosRun.Ok && dosRun.Text == miniDosExpected, $"  erwartet:\n{miniDosExpected}\n  QEMU:\n{dosRun.Text}");
        }
    }
}
