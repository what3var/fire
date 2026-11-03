using System.Diagnostics;
using System.Text;
using System.Text.Json;
using fire.Compiler;
using fire.Native;
using fire.Package.Manager;
using fire.Projects;
using fire.Runtime;
using fire.Values;

/// <summary>Linkage "static" (ROADMAP 9, docs/NATIVE_BACKEND.md "Getrennte Übersetzung"): every library module a static library in the user-wide cache - built once, then
/// reused; the program's output the same as with linkage "source" (and the VM); a library that changes is built again, the others not; a debug build stops in a library.
/// `FIRE_TESTS_ONLY=linkage`.</summary>
static class LinkageTests
{
    private static int _failures;
    private static string? _winePrefix;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Linkage: {title}" : $"FEHLER: Linkage: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static string Write(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private static string Vm(IReadOnlyList<string> sources, BuildPlan? plan = null)
    {
        var output = new StringBuilder();
        var session = RuntimeSession.Build(sources, null, args => { output.AppendLine(args.Length > 0 ? args[0].ToString() : ""); return Value.MakeUndefined(); }, plan: plan);
        session.Run();
        Value.SingleFloats = false;
        return output.ToString().Replace("\r\n", "\n");
    }

    private static string Exec(string exe, string? launcher = null)
    {
        var psi = new ProcessStartInfo(launcher ?? exe, launcher != null ? $"\"{exe}\"" : "") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (launcher != null) { psi.Environment["WINEDEBUG"] = "-all"; psi.Environment["LANG"] = "C.UTF-8"; if (_winePrefix != null) psi.Environment["WINEPREFIX"] = _winePrefix; }
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(120000)) { try { p.Kill(true); } catch (Exception) { } return "(timeout)"; }
        return output.Replace("\r\n", "\n") + (p.ExitCode != 0 ? $"(exit {p.ExitCode}) {err.Result}" : "");
    }

    private sealed record Built(bool Ok, string Exe, string Log, double Seconds, long Size);

    private static Built Build(IReadOnlyList<string> sources, string linkage, string exe, ToolchainDef? toolchain = null, TargetProfile? target = null, BuildPlan? plan = null, VmExecutionMode? mode = null,
        StrippingLevel? stripping = null)
    {
        var config = new NativeConfig { Linkage = linkage, Stripping = stripping };
        target ??= TargetProfile.Host;
        toolchain ??= config.ResolveToolchain(target, "gcc");
        var watch = Stopwatch.StartNew();
        var result = NativeBuilder.BuildSafe(sources, config, target, toolchain, exe, mode, plan: plan);
        return new Built(result.Ok, exe, result.Log, watch.Elapsed.TotalSeconds, File.Exists(exe) ? new FileInfo(exe).Length : 0);
    }

    private static List<string> ModuleLines(string log, string what) =>
        log.Split('\n').Where(l => l.StartsWith("module ", StringComparison.Ordinal) && l.Contains(what)).Select(l => l.Substring(7, l.IndexOf(": ", StringComparison.Ordinal) - 7)).ToList();

    public static void Run()
    {
        Console.WriteLine("=== Linkage \"static\": Bibliotheken im Cache ===");
        string root = Path.Combine(Path.GetTempPath(), "fire-linkage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? before = Environment.GetEnvironmentVariable("FIRE_NATIVE_CACHE");
        Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", Path.Combine(root, "cache"));
        try { RunIn(root); }
        catch (Exception ex) { Check("ohne Ausnahme", false, ex.ToString()); }
        finally
        {
            Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Console.WriteLine(_failures == 0 ? "Alle Linkage-Pruefungen bestanden." : $"FEHLER: {_failures} Linkage-Pruefung(en) fehlgeschlagen.");
    }

    private const string Cli = """
        #import "json"
        #import "linq"
        #import "hash"
        #import "compression"
        class Item {
            string name
            int count
            construct(string name, int count) { this.name = name; this.count = count }
        }
        class Store {
            List<Item> items = new List<Item>()
            construct() { }
        }
        var s = new Store()
        for (var i = 0; i < 5; i++) { s.items.Add(new Item("item" + i, i * 3)) }
        string text = Json.Serialize(s)
        print(text)
        var back = Json.Deserialize<Store>(text)
        print(back.items.Count + " " + back.items[4].name)
        print("big " + back.items.Where(x => x.count > 5).ToList().Count)
        print(Hash.Sha256.HexOf(text))
        var packed = Compression.Deflate.Compress(text)
        print("deflate " + (packed.Length < text.Length) + " " + Compression.Deflate.DecompressText(packed).Length)
        try { Json.Deserialize<Store>("{") } catch (JsonException e) { print("bad: " + e.message) }
        """;

    /// <summary>The classes of the program and of the libraries meet (ROADMAP 9 B2): a library calls members of objects of the program (OrderBy, Reflect.Get/Set/Call/New,
    /// probes), the program extends a generic List of core, overrides an operator, throws its own subclass of an exception of core, calls by name what a class
    /// lacks.</summary>
    private const string Meet = """
        #import "json"
        #import "linq"
        #import "reflection"
        class Pt {
            int x
            int y
            construct(int x, int y) { this.x = x; this.y = y }
            int CompareTo(Pt o) { return this.x - o.x }
            string ToString() { return "(" + x + "," + y + ")" }
            Pt operator+(Pt o) { return new Pt(x + o.x, y + o.y) }
            int Sum { get { return x + y } }
        }
        class MyError : ArgumentException {
            int code
            construct(string m, int c) : base(m) { code = c }
        }
        class Bag<T> : List<T> {
            string label = "bag"
            construct() : base() { }
            int Twice() { return Count * 2 }
        }
        var pts = new List<Pt>()
        pts.Add(new Pt(3, 1)); pts.Add(new Pt(1, 5)); pts.Add(new Pt(2, 2))
        pts = pts.OrderBy((Pt p) => p.x).ToList()
        print(pts[0].ToString() + " " + pts[2])
        print("sum " + (pts[0] + pts[1]))
        var big = pts.Where(p => p.Sum > 3).Select(p => p.x).ToList()
        print("big " + big.Count + " " + big[0])
        print("get " + Reflect.Get(pts[0], "y") + " " + Reflect.Get(pts[1], "Sum"))
        Reflect.Set(pts[0], "y", 42)
        print("set " + pts[0].y)
        print("call " + Reflect.Call(pts[1], "CompareTo", [pts[2]]))
        var b = new Bag<int>()
        b.Add(4); b.Add(5)
        print("bag " + b.Count + " " + b.Twice() + " " + b.label + " " + (b is of List) + " " + (b is of IEnumerable))
        var any = b
        var total = 0
        foreach (var v in any) { total += v }
        print("total " + total)
        try { throw new MyError("bad", 7) } catch (ArgumentException e) { print("caught " + e.message) }
        try { b.Add(Untyped.Text()) } catch (Exception e) { print("type " + e.message) }
        var o = pts[1]
        var holder = new List()
        holder.Add(o)
        var d = holder[0]
        print("dyn " + d.x + " " + d.Sum)
        try { d.Nope() } catch (Exception e) { print("miss " + e.message) }
        try { d.CompareTo() } catch (Exception e) { print("arity " + e.message) }
        print(Json.Serialize(pts[2]))
        print(Type.Of(pts[0]).Name)
        var np = Reflect.New("Pt", [7, 8])
        print("new " + np)
        var h = Reflect.Probe(np, "x", "changed", (any ob, any n, any ov, any nv) => print("probe " + n + " " + ov + "->" + nv))
        np.x = 9
        Reflect.Set(np, "x", 10)
        class Untyped {
            static any Text() { return "x" }
        }
        """;

    /// <summary>Stripping (docs/NATIVE_BACKEND.md "Größe des Programms") with libraries: the program leaves out like with "source" (a method of its own by a name
    /// made at run time, a class it never makes); a library keeps its API at every level (List.IndexOf by a name made at run time).</summary>
    private const string Strip = """
        #import "reflection"
        class Dog {
            string Bark() { return "wuff" }
            string Wag() { return "wag" }
        }
        class Ghost {
            int b
            construct() { this.b = 2 }
        }
        class N { static string Of(string a, string b) { return a + b } }
        var d = new Dog()
        print(Reflect.Call(d, "Bark", []))
        try { print(Reflect.Call(d, N.Of("Wa", "g"), [])) } catch (ReflectionException e) { print("call: " + e.message) }
        try { any g = Reflect.New(N.Of("Gho", "st"), []); print("ghost " + g.b) } catch (ReflectionException e) { print("new: " + e.message) }
        var l = new List()
        l.Add(1); l.Add(2)
        print("lib " + Reflect.Call(l, N.Of("Index", "Of"), [2]))
        """;

    private static void RunIn(string root)
    {
        // ---- a program with several packages: the same output as "source" and the VM; the second build takes every library from the cache ----
        string vm = Vm(new[] { Cli });
        var source = Build(new[] { Cli }, "source", Path.Combine(root, "cli-source"));
        Check("CLI: linkage source baut", source.Ok, source.Log);
        var first = Build(new[] { Cli }, "static", Path.Combine(root, "cli-static1"));
        string firstOut = first.Ok ? Exec(first.Exe) : "";
        Check("CLI: linkage static baut (jedes Modul eine Bibliothek: core, json, linq, hash, compression, ...)", first.Ok && ModuleLines(first.Log, "built in").Count >= 5 && !first.Log.Contains("not possible"), first.Log);
        Check("CLI: die Ausgabe ist die der VM und die von linkage source", first.Ok && firstOut == vm && (!source.Ok || Exec(source.Exe) == vm), $"VM:\n{vm}\nstatic:\n{firstOut}");
        var second = Build(new[] { Cli }, "static", Path.Combine(root, "cli-static2"));
        Check("CLI: der zweite Bau nimmt jede Bibliothek aus dem Cache (kein Modul wird neu erzeugt oder uebersetzt)",
            second.Ok && ModuleLines(second.Log, "new").Count == 0 && ModuleLines(second.Log, "built in").Count == 0 && ModuleLines(second.Log, "from the cache").Count >= 5 && Exec(second.Exe) == vm, second.Log);
        Console.WriteLine($"  (Messung CLI: source {source.Seconds:F1} s, {source.Size / 1024} KB; static erster Bau {first.Seconds:F1} s, zweiter {second.Seconds:F1} s, {second.Size / 1024} KB)");

        // ---- the interface files (ROADMAP 9.7) and the cache (9.2) ----
        var ifaces = Directory.EnumerateFiles(NativeLibraryCache.Root, "*" + NativeLibraryCache.InterfaceExtension, SearchOption.AllDirectories).ToList();
        bool ifaceOk = ifaces.Count > 0 && ifaces.All(f =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var r = doc.RootElement;
            return r.GetProperty("Format").GetInt32() == CppGenerator.InterfaceFormat && r.GetProperty("Abi").GetString()!.Length == 64 && r.GetProperty("Key").GetString()!.Length > 0;
        });
        Check("Schnittstelle: jede Bibliothek hat eine Datei mit Formatversion, ABI-Hash und Schluessel", ifaceOk, string.Join("\n", ifaces));
        Check("Cache: die Bibliotheken liegen im Cache des Benutzers (FIRE_NATIVE_CACHE)", NativeLibraryCache.Size().Libraries >= 5, NativeLibraryCache.Root);

        // ---- another program with some of the same packages shares their libraries ----
        // (the same packages - the same switches of the program, ROADMAP 9.4: a program that needs other switches gets variants of its own)
        string other = Cli.Replace("print(text)", "print(\"other \" + text.Length)");
        var shared = Build(new[] { other }, "static", Path.Combine(root, "other"));
        string sharedOut = shared.Ok ? Exec(shared.Exe) : "", sharedVm = Vm(new[] { other });
        Check("Teilen: ein anderes Programm mit denselben Packages nimmt alle Bibliotheken aus dem Cache (der Text eines Moduls haengt nicht vom Programm ab)",
            shared.Ok && ModuleLines(shared.Log, "new").Count == 0 && ModuleLines(shared.Log, "from the cache").Contains("core") && ModuleLines(shared.Log, "from the cache").Contains("pkg:json") && sharedOut == sharedVm,
            $"{shared.Log}\nnativ:\n{sharedOut}\nVM:\n{sharedVm}");

        // ---- every module serves its own classes (ROADMAP 9 B2): the program's C has no helpers for the classes of the libraries - the libraries reach the objects of
        // the program, and the program those of the libraries it does not know by type, through the tables of the members in the class descriptors ----
        string meetVm = Vm(new[] { Meet });
        var meet = Build(new[] { Meet }, "static", Path.Combine(root, "meet"));
        string meetOut = meet.Ok ? Exec(meet.Exe) : "";
        Check("Module: Klassen des Programms und der Libraries treffen sich (OrderBy, Reflect.Get/Set/Call/New, Probe, Operator, List<T> erweitert, Ausnahme, fehlende Methode) wie die VM",
            meet.Ok && meetOut == meetVm && !meet.Log.Contains("not possible"), $"{meet.Log}\nnativ:\n{meetOut}\nVM:\n{meetVm}");
        // ---- stripping: the program like with "source", a library keeps its API ----
        string stripVm = Vm(new[] { Strip });
        Check("Stripping: die VM", stripVm == "wuff\nwag\nghost 2\nlib 1\n", stripVm);
        foreach (var level in new[] { StrippingLevel.Methods, StrippingLevel.All })
        {
            var built = Build(new[] { Strip }, "static", Path.Combine(root, $"strip{(int)level}"), stripping: level);
            string got = built.Ok ? Exec(built.Exe) : "";
            int n = (int)level;
            string expected = $"wuff\ncall: Member 'Dog.Wag' was left out of the build (stripping {n}). Add [Keep] to 'Dog.Wag' or use stripping <= 1.\n"
                + (level == StrippingLevel.All ? "new: Class 'Ghost' was left out of the build (stripping 3). Add [Keep] to 'Ghost' or use stripping <= 2.\n" : "ghost 2\n") + "lib 1\n";
            Check($"Stripping {n} mit static: das Programm laesst weg wie mit source (klare Fehler), eine Library behaelt ihre Schnittstelle (List.IndexOf per Laufzeitname)",
                built.Ok && got == expected && !built.Log.Contains("not possible"), $"{built.Log}\nnativ:\n{got}\nerwartet:\n{expected}");
        }

        var linkedCli = new Linker { Engine = "native" }.CompileAndLink(new[] { Cli });
        string sourceC = CppGenerator.Generate(linkedCli);
        var cliPlan = CppGenerator.GenerateStatic(linkedCli, TargetProfile.Host, "linkage-test", cacheRoot: Path.Combine(root, "plan-cache"));
        Check("Module: das C des Programms ist kleiner als zwei Drittel von dem mit source (es bedient nur seine eigenen Klassen; Natives in C und die JSON-Engine in den Bibliotheken)",
            cliPlan.ProgramC.Length * 3 < sourceC.Length * 2 && !cliPlan.ProgramC.Contains("FIRE_JSON_IMPLEMENTATION 1") && !cliPlan.ProgramC.Contains("fire_bridge_hash.h"),
            $"Programm {cliPlan.ProgramC.Length} Zeichen, source {sourceC.Length}");

        // ---- a project with two libraries: a change of the code of a library builds only it, a change of its interface also the libraries that need it ----
        string s = Path.Combine(root, "sln");
        string libFile = Write(s, "Lib/Lib.fireproj", """{ "name": "Lib", "type": "library" }""");
        string libSource = Write(s, "Lib/lib.script", "namespace Lib {\n    class Counter {\n        int n = 0\n        construct() { }\n        Add(int k) {\n            this.n = this.n + k\n        }\n        int Get() { return this.n }\n        static string Version() { return \"v1\" }\n    }\n}\n");
        Write(s, "Lib2/Lib2.fireproj", """{ "name": "Lib2", "type": "library", "references": [ { "project": "../Lib/Lib.fireproj" } ] }""");
        Write(s, "Lib2/lib2.script", "#import \"Lib\"\nnamespace Lib2 {\n    class Wrap {\n        static int Twice(int x) { var c = new Lib.Counter(); c.Add(x); c.Add(x); return c.Get() }\n    }\n}\n");
        Write(s, "App/App.fireproj", """{ "name": "App", "references": [ { "project": "../Lib2/Lib2.fireproj" }, { "project": "../Lib/Lib.fireproj" } ], "settings": { "linkage": "static" } }""");
        Write(s, "App/main.script", "#import \"Lib2\"\n#import \"Lib\"\nclass Mine : Lib.Counter { construct() : base() { } }\nvar m = new Mine()\nm.Add(5)\nprint(Lib2.Wrap.Twice(21) + \" \" + m.Get() + \" \" + Lib.Counter.Version())\n");
        string sln = Write(s, "All.firesln", """{ "name": "All", "projects": ["Lib/Lib.fireproj", "Lib2/Lib2.fireproj", "App/App.fireproj"], "startup": "App" }""");
        BuildPlan Plan() { var ws = Workspace.Open(sln); return BuildPlan.Create(ws, ws.FindByName("App")!); }
        var plan = Plan();
        Check("Projekt: die Einstellung linkage des Projekts", plan.Settings.Linkage == "static", plan.Settings.Linkage);
        var p1 = Build(plan.SourceTexts, plan.Settings.Linkage!, Path.Combine(root, "app1"), plan: plan);
        string appVm = Vm(plan.SourceTexts, plan);
        Check("Projekt: Programm mit zwei Bibliotheken des Projekts (eine Klasse des Programms erbt von einer der Bibliothek) wie die VM",
            p1.Ok && Exec(p1.Exe) == appVm && ModuleLines(p1.Log, "built in").Contains("Lib") && ModuleLines(p1.Log, "built in").Contains("Lib2"), $"VM: {appVm}\n{p1.Log}");
        File.WriteAllText(libSource, File.ReadAllText(libSource).Replace("\"v1\"", "\"v2\""));
        plan = Plan();
        var p2 = Build(plan.SourceTexts, "static", Path.Combine(root, "app2"), plan: plan);
        Check("Projekt: der Code einer Bibliothek aendert sich - nur sie wird neu gebaut (Lib2 und core aus dem Cache)",
            p2.Ok && Exec(p2.Exe).Contains("v2") && ModuleLines(p2.Log, "built in").SequenceEqual(new[] { "Lib" }) && ModuleLines(p2.Log, "from the cache").Contains("Lib2"), p2.Log);
        File.WriteAllText(libSource, File.ReadAllText(libSource).Replace("        int Get() { return this.n }", "        int Get() { return this.n }\n        int Twice() { return this.n * 2 }"));
        plan = Plan();
        var p3 = Build(plan.SourceTexts, "static", Path.Combine(root, "app3"), plan: plan);
        Check("Projekt: die Schnittstelle einer Bibliothek aendert sich - auch die Bibliothek, die sie braucht, wird neu gebaut",
            p3.Ok && ModuleLines(p3.Log, "built in").Contains("Lib") && ModuleLines(p3.Log, "built in").Contains("Lib2") && Exec(p3.Exe) == Vm(plan.SourceTexts, plan), p3.Log);

        // ---- linkage "shared" (phase C, Linux/macOS): the libraries as shared libraries next to the program; two programs share them; a library that does not fit
        // the program (another interface) is a readable error when the program starts ----
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            plan = Plan();
            string shDir1 = Path.Combine(root, "shared1");
            Directory.CreateDirectory(shDir1);
            var sh1 = Build(plan.SourceTexts, "shared", Path.Combine(shDir1, "app"), plan: plan);
            string sh1Vm = Vm(plan.SourceTexts, plan);
            var soFiles = Directory.Exists(shDir1) ? Directory.GetFiles(shDir1, "libfire_*").Select(Path.GetFileName).ToList() : new List<string?>();
            Check("Shared: Programm mit Bibliotheken des Projekts als Shared Libraries neben dem Programm, wie die VM",
                sh1.Ok && Exec(sh1.Exe) == sh1Vm && soFiles.Any(f => f!.StartsWith("libfire_mLib_")) && soFiles.Any(f => f!.StartsWith("libfire_mcore_")) && !sh1.Log.Contains("only available"),
                $"VM: {sh1Vm}\n{string.Join(" ", soFiles)}\n{sh1.Log}");
            foreach (var dir in new[] { "sharedA", "sharedB", "shared2" }) Directory.CreateDirectory(Path.Combine(root, dir));
            const string smallA = "#import \"json\"\nclass Q { int x = 4 }\nprint(Json.Serialize(new Q()))\n";
            const string smallB = "#import \"json\"\nclass R { string t = \"r\" }\nprint(Json.Serialize(new R()) + \"!\")\n";
            var shA = Build(new[] { smallA }, "shared", Path.Combine(root, "sharedA", "a"));
            var shB = Build(new[] { smallB }, "shared", Path.Combine(root, "sharedB", "b"));
            Check("Shared: zwei Programme teilen sich die Bibliotheken (das zweite baut keine), beide wie die VM",
                shA.Ok && shB.Ok && ModuleLines(shB.Log, "built in").Count == 0 && ModuleLines(shB.Log, "from the cache").Contains("pkg:json")
                && Exec(shA.Exe) == Vm(new[] { smallA }) && Exec(shB.Exe) == Vm(new[] { smallB }), shA.Log + "\n" + shB.Log);
            // another layout of Lib (a field more): its library put in place of the one the first program was built with
            string before = File.ReadAllText(libSource);
            File.WriteAllText(libSource, before.Replace("        int n = 0\n", "        int n = 0\n        int extra = 1\n"));
            plan = Plan();
            var sh2 = Build(plan.SourceTexts, "shared", Path.Combine(root, "shared2", "app"), plan: plan);
            string? newLib = sh2.Ok ? Directory.GetFiles(Path.Combine(root, "shared2"), "libfire_mLib_*").FirstOrDefault() : null;
            string? oldLib = Directory.Exists(shDir1) ? Directory.GetFiles(shDir1, "libfire_mLib_*").FirstOrDefault() : null;
            string mismatch = "";
            if (newLib != null && oldLib != null) { File.Copy(newLib, oldLib, true); mismatch = Exec(sh1.Exe); }
            Check("Shared: eine Bibliothek mit einer anderen Schnittstelle neben dem Programm ist beim Start ein lesbarer Fehler (kein Absturz)",
                mismatch.Contains("does not fit this program") && mismatch.Contains("(exit 70)") && mismatch.Contains("'Lib'"), mismatch + "\n" + sh2.Log);
            File.WriteAllText(libSource, before);
        }

        // ---- a debug build: a breakpoint inside a static library ----
        DebugInLibrary(root, Plan(), libSource);

        // ---- a program that cannot be split (it declares a class of the prelude anew): linkage source, with a note ----
        const string own = "class Exception { string message; construct(string message = \"\") { this.message = message } }\ntry { throw new Exception(\"x\") } catch (e) { print(\"caught \" + e.message) }\n";
        var fallback = Build(new[] { own }, "static", Path.Combine(root, "own"));
        Check("Rueckfall: ein Programm, das sich nicht in Module teilen laesst, wird mit linkage source gebaut und sagt es", fallback.Ok && fallback.Log.Contains("not possible") && Exec(fallback.Exe) == Vm(new[] { own }), fallback.Log);

        // ---- other toolchains: TinyCC (tcc -ar), MinGW under Wine ----
        const string small = "#import \"json\"\nclass Q { int x = 4; string t = \"q\" }\nvar q = new Q()\nprint(Json.Serialize(q))\nprint(q.x * 10)\n";
        string smallVm = Vm(new[] { small });
        var config = new NativeConfig();
        if (ToolchainDetector.Find(ToolchainDef.BuiltIn["tcc"]) != null)
        {
            var tcc = Build(new[] { small }, "static", Path.Combine(root, "tcc"), ToolchainDef.BuiltIn["tcc"]);
            Check("TinyCC: linkage static (Bibliotheken mit tcc -ar)", tcc.Ok && Exec(tcc.Exe) == smallVm, tcc.Log);
            var tccShared = Build(new[] { small }, "shared", Path.Combine(root, "tcc-shared"), ToolchainDef.BuiltIn["tcc"]);
            Check("TinyCC: linkage shared gibt es dort nicht - gebaut wird static, das Protokoll sagt es", tccShared.Ok && tccShared.Log.Contains("only available") && Exec(tccShared.Exe) == smallVm, tccShared.Log);
        }
        else Console.WriteLine("(TinyCC: uebersprungen - tcc fehlt)");
        var mingw = new ToolchainDef { Kind = "gcc", Compiler = "x86_64-w64-mingw32-g++", CCompiler = "x86_64-w64-mingw32-gcc", Std = "c++17", CStd = "c11", Optimization = "-O2" };
        // (Debian/Ubuntu's wine64 package puts the loader into /usr/lib/wine without a `wine` on the PATH)
        string? wine = new[] { "/usr/lib/wine/wine64", "/usr/bin/wine64", "/usr/bin/wine" }.FirstOrDefault(File.Exists) ?? (HasProgram("wine") ? "wine" : null);
        if (ToolchainDetector.Find(mingw) != null && wine != null)
        {
            var win = Build(new[] { small }, "static", Path.Combine(root, "win.exe"), mingw, TargetProfile.Windows);
            _winePrefix = Path.Combine(root, "wineprefix");
            Check("MinGW: linkage static fuer Windows (x86_64-w64-mingw32-ar), unter Wine wie die VM", win.Ok && Exec(win.Exe, wine) == smallVm, win.Log);
        }
        else Console.WriteLine("(MinGW/Wine: uebersprungen)");

        // ---- ember cache clean ----
        var (libraries, _) = NativeLibraryCache.Clean();
        Check("ember cache clean: leert den Cache", libraries > 0 && !Directory.Exists(NativeLibraryCache.Root));
        var again = Build(new[] { other }, "static", Path.Combine(root, "other2"));
        Check("Nach dem Leeren: die Bibliotheken werden neu gebaut", again.Ok && ModuleLines(again.Log, "built in").Contains("core"), again.Log);
    }

    private static bool HasProgram(string name) => ToolchainSetup.FindProgram(name) != null;

    /// <summary>A debug build with linkage "static": the debugger sets a breakpoint in a file of a library of the project and the program stops there (ROADMAP 9.9: the
    /// tables of each module, the sources of the program).</summary>
    private static void DebugInLibrary(string root, BuildPlan plan, string libSource)
    {
        var dbg = Build(plan.SourceTexts, "static", Path.Combine(root, "app-debug"), plan: plan, mode: VmExecutionMode.Debug);
        if (!dbg.Ok || dbg.Log.Contains("not possible") || !ModuleLines(dbg.Log, "").Contains("Lib")) { Check("Debug: Debug-Build mit statischen Bibliotheken", false, dbg.Log); return; }
        try
        {
            using var c = NativeDebugClient.Launch(dbg.Exe, root);
            int main = c.Hello.GetProperty("mainThread").GetInt32();
            int libIndex = c.Hello.GetProperty("sources").EnumerateArray()
                .Where(x => x.GetProperty("file").ValueKind == JsonValueKind.String && Path.GetFullPath(x.GetProperty("file").GetString()!) == Path.GetFullPath(libSource))
                .Select(x => x.GetProperty("index").GetInt32()).DefaultIfEmpty(-1).First();
            c.NextEvent("stopped");
            c.Request("breakpoints", new() { ["breakpoints"] = new[] { new[] { libIndex, 6 } } });
            c.Request("continue", new() { ["thread"] = main });
            var stop = c.NextEventExcept("thread", "threadExited");
            bool atLib = stop.GetProperty("event").GetString() == "stopped" && stop.GetProperty("source").GetInt32() == libIndex && stop.GetProperty("line").GetInt32() == 6;
            var trace = atLib ? c.Request("trace", new() { ["thread"] = main }) : default;
            bool named = atLib && trace.GetProperty("frames").EnumerateArray().First().GetProperty("function").GetString() == "Lib.Counter.Add";
            Check("Debug: ein Haltepunkt in einer statischen Bibliothek des Projekts haelt dort (Quelle der Bibliothek, Zeile, Funktion)", libIndex >= 0 && atLib && named, $"lib source {libIndex}: {stop} {(atLib ? trace.ToString() : "")}");
            c.Request("breakpoints", new() { ["breakpoints"] = Array.Empty<int[]>() });
            c.Request("continue", new() { ["thread"] = main, ["breakpoints"] = false });
            while (c.NextEvent().GetProperty("event").GetString() != "exited") c.Request("continue", new() { ["thread"] = main, ["breakpoints"] = false });
        }
        catch (Exception ex) { Check("Debug: ein Haltepunkt in einer statischen Bibliothek", false, ex.ToString()); }
    }
}
