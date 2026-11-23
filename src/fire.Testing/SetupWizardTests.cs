using System.Net;
using System.Security.Cryptography;
using fire.Editor.Assistant;
using fire.Editor.Setup;
using fire.Tooling;
/// <summary>spark's setup wizard without a window (src/fire.Editor/Setup): the catalog of ai.models read from its source, the choice for a machine, the pages and the plan for
/// Windows and Linux, the progress of a run (states, bytes and speed, cancel, retry, a failure that does not stop the others) and the download of a model against a small HTTP
/// server (a .part continued with Range, the SHA-256 check, a wrong file removed). FIRE_TESTS_ONLY=setupwizard; FIRE_TESTS_FILTER narrows to the checks whose title has the text.</summary>
static class SetupWizardTests
{
    private static int _failures;

    private static bool Wanted(string title)
    {
        string? filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER");
        return string.IsNullOrWhiteSpace(filter) || filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(f => title.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private static void Check(string title, Func<bool> ok, Func<string>? detail = null)
    {
        if (!Wanted(title)) return;
        bool result;
        string? why = null;
        try { result = ok(); if (!result) why = detail?.Invoke(); }
        catch (Exception ex) { result = false; why = ex.ToString(); }
        if (!result) _failures++;
        Console.WriteLine(result ? $"OK: Setup-Wizard: {title}" : $"FEHLER: Setup-Wizard: {title}" + (why == null ? "" : $"\n{why}"));
    }

    private static readonly SetupModelInfo Small = new("qwen2.5-0.5b", "Qwen2.5 0.5B", "smallest", "Qwen/x", "small.gguf", 428730208, "aa", "Apache 2.0", "", 1024, 2048);

    private static string CatalogSource()
    {
        foreach (string dir in new[] { Path.Combine(AppContext.BaseDirectory, "Source"), Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "packages") })
        {
            string f = Path.Combine(dir, "ai.models", "models.fire");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("models.fire");
    }

    private static SetupEnvironment Windows(bool cpp = false, bool assistant = true, long memory = 16384) =>
        new(IsWindows: true, IsMacOS: false, IsX64: true, new MachineInfo(memory, 8, true), AssistantAvailable: true, AssistantEnabled: assistant,
            cpp ? new[] { new DetectedToolchain("gcc", "GCC (g++)", @"C:\msys64\ucrt64\bin\g++.exe") } : Array.Empty<DetectedToolchain>(), ModelFolder: EmptyFolder());

    private static SetupEnvironment Linux(bool cpp = true) =>
        new(IsWindows: false, IsMacOS: false, IsX64: true, new MachineInfo(8192, 4, true), AssistantAvailable: true, AssistantEnabled: false,
            cpp ? new[] { new DetectedToolchain("gcc", "GCC (g++)", "/usr/bin/g++") } : Array.Empty<DetectedToolchain>(), ModelFolder: EmptyFolder());

    /// <summary>spark's native build of the assistant (src/fire.Editor/Assistant/AssistantNative.cs): the packages it is made of, its key, the stamps, a build.</summary>
    private static void NativeAssistant()
    {
        string packages = Path.GetDirectoryName(Path.GetDirectoryName(CatalogSource())!)!;
        string root = EmptyFolder();
        string script = Path.Combine(root, "assistant.fire");
        File.WriteAllText(script, "#import \"assistant\"\nAssistant.App.Run(\"\", true)\n");
        Check("Assistent nativ: die Pakete des Assistenten - mit ai, ui, windows, mcp, ohne scene3d", () =>
        {
            var names = AssistantNative.PackageFolders(script, packages).Select(Path.GetFileName).ToList();
            return names.Contains("assistant") && names.Contains("ai") && names.Contains("ai.agent") && names.Contains("ui") && names.Contains("windows") && names.Contains("mcp")
                && !names.Contains("scene3d") && !names.Contains("data.mysql");
        }, () => string.Join(", ", AssistantNative.PackageFolders(script, packages).Select(Path.GetFileName)));
        // a small tree of packages: app uses lib (whose natives use base); other is not used
        string tree = Path.Combine(root, "packages");
        void Package(string name, string source, string uses = "")
        {
            Directory.CreateDirectory(Path.Combine(tree, name));
            File.WriteAllText(Path.Combine(tree, name, name + ".fireproj"),
                "{ \"name\": \"fire-" + name + "\", \"import\": \"" + name + "\"" + (uses.Length > 0 ? ", \"native\": { \"uses\": [\"" + uses + "\"] }" : "") + " }");
            File.WriteAllText(Path.Combine(tree, name, name + ".fire"), source);
        }
        Package("app", "#import \"lib\"\n");
        Package("lib", "// lib\n", uses: "base");
        Package("base", "// base\n");
        Package("other", "// other\n");
        string compiler = Path.Combine(root, "forge.dll");
        File.WriteAllText(compiler, "v1");
        string main = Path.Combine(root, "main.fire");
        File.WriteAllText(main, "#import \"app\"\n");
        string Key() => AssistantNative.Key(main, tree, new[] { compiler });
        string k1 = Key();
        Check("Assistent nativ: Pakete ueber #import und uses gefunden", () => string.Join(",", AssistantNative.PackageFolders(main, tree).Select(Path.GetFileName)) == "app,base,lib",
            () => string.Join(",", AssistantNative.PackageFolders(main, tree).Select(Path.GetFileName)));
        Check("Assistent nativ: der Schluessel bleibt - ein unbenutztes Paket und bin/ aendern ihn nicht", () =>
        {
            File.WriteAllText(Path.Combine(tree, "other", "other.fire"), "// changed\n");
            Directory.CreateDirectory(Path.Combine(tree, "lib", "bin"));
            File.WriteAllText(Path.Combine(tree, "lib", "bin", "x.o"), "build output");
            return Key() == k1;
        });
        Check("Assistent nativ: ein benutztes Paket, das Skript und der Compiler aendern ihn", () =>
        {
            File.WriteAllText(Path.Combine(tree, "base", "base.fire"), "// changed\n");
            string k2 = Key();
            File.WriteAllText(main, "#import \"app\"\nprint(1)\n");
            string k3 = Key();
            File.WriteAllText(compiler, "v2");
            string k4 = Key();
            return k2 != k1 && k3 != k2 && k4 != k3;
        });
        Check("Assistent nativ: Stempel - kein Binary, dann das passende; ein Fehlschlag gilt nur fuer seinen Schluessel", () =>
        {
            string folder = Path.Combine(root, "native");
            Directory.CreateDirectory(folder);
            bool none = AssistantNative.Current(folder, "K") == null;
            File.WriteAllText(AssistantNative.BinaryIn(folder), "binary");
            File.WriteAllText(Path.Combine(folder, "assistant.key"), "K");
            File.WriteAllText(Path.Combine(folder, "assistant.failed"), "F");
            return none && AssistantNative.Current(folder, "K") != null && AssistantNative.Current(folder, "L") == null
                && AssistantNative.FailedBefore(folder, "F") && !AssistantNative.FailedBefore(folder, "K");
        });
        // the compiler: GCC without optimisation (the engine of the model optimises itself), else Clang/MSVC as for publishing, TinyCC alone: the machine's
        Check("Assistent nativ: der Compiler - GCC mit -O0, sonst Clang/MSVC wie zum Veroeffentlichen, nur TinyCC: der Build-Compiler der Maschine", () =>
        {
            var machine = new fire.Tooling.MachineToolchains("tcc", "gcc");
            Func<fire.Tooling.ToolchainDef, string?> Only(params string[] kinds) => d => kinds.Contains(d.EffectiveKind) ? "/usr/bin/" + d.EffectiveKind : null;
            var gcc = AssistantNative.BuildToolchain(machine, Only("tcc", "gcc", "clang"));
            var clang = AssistantNative.BuildToolchain(machine, Only("tcc", "clang"));
            var tcc = AssistantNative.BuildToolchain(machine, Only("tcc"));
            return gcc is { EffectiveKind: "gcc", Optimization: "-O0" } && gcc.Args!.SequenceEqual(new[] { "-w" })
                && clang is { EffectiveKind: "clang", Optimization: "-O2" } && tcc == null
                && AssistantNative.Describe(gcc) != AssistantNative.Describe(clang) && AssistantNative.Describe(null) != AssistantNative.Describe(gcc)
                && AssistantNative.Key(main, tree, new[] { compiler }, AssistantNative.Describe(gcc)) != AssistantNative.Key(main, tree, new[] { compiler }, AssistantNative.Describe(clang));
        });
        if (OperatingSystem.IsWindows()) return;   // (the stand-ins of forge below are shell scripts)
        Check("Assistent nativ: ein Bau, der scheitert, wird fuer seinen Schluessel gemerkt", () =>
        {
            string folder = Path.Combine(root, "failing");
            string fake = Path.Combine(root, "fake-forge.sh");
            File.WriteAllText(fake, "#!/bin/sh\necho 'no C compiler' >&2\nexit 1\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var r = AssistantNative.Build(fake, main, folder, "X");
            return r.Binary == null && !r.Cancelled && r.Output.Contains("no C compiler") && AssistantNative.FailedBefore(folder, "X");
        });
        Check("Assistent nativ: ein Bau, der gelingt, steht an seinem Platz mit seinem Schluessel (ohne die Fortschrittszeilen)", () =>
        {
            string folder = Path.Combine(root, "working");
            string fake = Path.Combine(root, "fake-forge-ok.sh");
            // (forge build <script> --engine native -o <out>: the sixth argument is the output)
            File.WriteAllText(fake, "#!/bin/sh\necho '##fire-progress 0 0 Building' >&2\necho built > \"$6\"\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var r = AssistantNative.Build(fake, main, folder, "Y");
            return r.Binary != null && File.ReadAllText(r.Binary).Trim() == "built" && AssistantNative.Current(folder, "Y") == r.Binary
                && !r.Output.Contains("##fire-progress") && Directory.GetFiles(folder, "build-*").Length == 0;
        });
        Check("Assistent nativ: der Compiler geht als Toolchain einer eigenen Konfiguration an forge (--config, --toolchain assistant)", () =>
        {
            string folder = Path.Combine(root, "configured");
            string fake = Path.Combine(root, "fake-forge-args.sh");
            File.WriteAllText(fake, "#!/bin/sh\necho \"$7 $9 ${10}\" > \"$6\"\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var gcc = AssistantNative.BuildToolchain(new fire.Tooling.MachineToolchains("tcc", "auto"), d => d.EffectiveKind == "gcc" ? "/usr/bin/gcc" : null)!;
            var r = AssistantNative.Build(fake, main, folder, "C", toolchain: gcc);
            string config = Path.Combine(folder, "assistant.native.json");
            var loaded = File.Exists(config) ? fire.Tooling.NativeConfig.Load(config).Toolchains.GetValueOrDefault(AssistantNative.ToolchainName) : null;
            return r.Binary != null && File.ReadAllText(r.Binary).Trim() == "--config --toolchain " + AssistantNative.ToolchainName
                && loaded is { EffectiveKind: "gcc", Optimization: "-O0" };
        }, () => File.Exists(Path.Combine(root, "configured", "assistant")) ? File.ReadAllText(Path.Combine(root, "configured", "assistant")) : "no binary");
        Check("Assistent nativ: abgebrochen - kein Binary, kein Fehlschlag gemerkt", () =>
        {
            string folder = Path.Combine(root, "cancelled");
            string fake = Path.Combine(root, "fake-forge-slow.sh");
            File.WriteAllText(fake, "#!/bin/sh\nsleep 30\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var r = AssistantNative.Build(fake, main, folder, "Z", token: cancel.Token);
            return r.Cancelled && r.Binary == null && !AssistantNative.FailedBefore(folder, "Z") && watch.Elapsed < TimeSpan.FromSeconds(10);
        });
    }

    private static string EmptyFolder()
    {
        string f = Path.Combine(Path.GetTempPath(), "fire-setup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(f);
        Cleanup.Add(f);
        return f;
    }

    private static readonly List<string> Cleanup = new();

    public static void Run()
    {
        Console.WriteLine("=== Setup-Wizard: catalog, pages, plan, progress, download ===");
        _failures = 0;
        try
        {
            Catalog();
            Pages();
            StartAndTinycc();
            Compilers();
            Progress();
            Download();
            NativeAssistant();
        }
        finally
        {
            foreach (string f in Cleanup) try { Directory.Delete(f, true); } catch (Exception) { }
        }
        Console.WriteLine(_failures == 0 ? "Alle Setup-Wizard-Pruefungen bestanden." : $"FEHLER: {_failures} Setup-Wizard-Pruefung(en) fehlgeschlagen.");
    }

    private static void Catalog()
    {
        var catalog = ModelCatalog.Load(CatalogSource());
        Check("Katalog: aus models.fire gelesen (vier Modelle, der Standard zuerst)", () => catalog.Count == 4 && catalog[0].Id == "qwen3.5-0.8b",
            () => string.Join(", ", catalog.Select(m => m.Id)));
        Check("Katalog: Groesse, SHA-256, Speicher, Lizenz mit Klammern", () =>
        {
            var lfm = ModelCatalog.Find(catalog, "lfm3-1.7b")!;
            return lfm.Size == 1049615680 && lfm.Sha256.Length == 64 && lfm.MinMemoryMB == 4096 && lfm.GoodMemoryMB == 8192 && lfm.License.Contains("(free for companies")
                && ModelCatalog.Find(catalog, "Qwen3.5-0.8B-Q4_0.gguf")?.Id == "qwen3.5-0.8b" && catalog[0].SizeText == "507 MB" && lfm.SizeText == "1.0 GB";
        });
        Check("Katalog: fehlende Datei -> leer", () => ModelCatalog.Load(Path.Combine(EmptyFolder(), "none.fire")).Count == 0);
        Check("Katalog: Ausdruck statt Literal wird ausgelassen", () => ModelCatalog.Parse("all.Add(new AI.ModelInfo(\"a\", x + \"b\", \"c\"))\nall.Add(new AI.ModelInfo(\"i\",\"n\",\"d\",\"r\",\"f\",5,\"s\",\"l\",\"u\",1,2))").Count == 1);
        Check("Wahl: 16 GB, 8 Threads, SIMD -> Qwen3.5 0.8B, staerker LFM3", () =>
            ModelCatalog.Recommended(catalog, new MachineInfo(16384, 8, true))?.Id == "qwen3.5-0.8b" && ModelCatalog.Stronger(catalog, new MachineInfo(16384, 8, true))?.Id == "lfm3-1.7b");
        Check("Wahl: 2 GB -> das kleinste, kein staerkeres", () =>
            ModelCatalog.Recommended(catalog, new MachineInfo(2048, 8, true))?.Id == "qwen2.5-0.5b" && ModelCatalog.Stronger(catalog, new MachineInfo(2048, 8, true)) == null);
        Check("Wahl: ohne SIMD mit 2 Threads -> das kleinste", () => ModelCatalog.Recommended(catalog, new MachineInfo(8192, 2, false))?.Id == "qwen2.5-0.5b");
        Check("Wahl: Fits nach Mindestspeicher (unbekannt passt)", () =>
            !ModelCatalog.Fits(ModelCatalog.Find(catalog, "qwen3.5-2b")!, new MachineInfo(4096, 8, true)) && ModelCatalog.Fits(ModelCatalog.Find(catalog, "qwen3.5-2b")!, new MachineInfo(-1, 8, true)));
        Check("Adresse: Mirror mit und ohne Schraegstrich", () =>
            ModelCatalog.UrlOf(Small, "http://m") == "http://m/Qwen/x/resolve/main/small.gguf" && ModelCatalog.UrlOf(Small, "") == "https://huggingface.co/Qwen/x/resolve/main/small.gguf");
        Check("Format: Groessen, Geschwindigkeit, Restzeit", () =>
            SetupFormat.Size(999) == "999 B" && SetupFormat.Size(507154688) == "507 MB" && SetupFormat.Size(4_200_000) == "4.2 MB" && SetupFormat.Size(1_214_873_856) == "1.2 GB"
            && SetupFormat.Speed(2_500_000) == "2.5 MB/s" && SetupFormat.Remaining(12.2) == "about 13 s left" && SetupFormat.Remaining(150) == "about 3 min left" && SetupFormat.Remaining(3900) == "about 1 h 5 min left");
    }

    private static void Pages()
    {
        var catalog = ModelCatalog.Load(CatalogSource());
        var win = new SetupWizardModel(Windows(), catalog);
        Check("Seiten: Windows mit Assistent -> Willkommen, Toolchains, Modell, Zusammenfassung", () =>
            win.Pages.SequenceEqual(new[] { SetupPage.Welcome, SetupPage.Toolchains, SetupPage.Model, SetupPage.Summary }));
        Check("Seiten: Assistent aus -> ohne Modellseite", () => !new SetupWizardModel(Windows(assistant: false), catalog).Pages.Contains(SetupPage.Model));
        Check("Seiten: Assistent an, aber nicht installiert -> ohne Modellseite", () => !new SetupWizardModel(Windows() with { AssistantAvailable = false }, catalog).Pages.Contains(SetupPage.Model));
        Check("Seiten: Next/Back, Schritttext, letzte Seite", () =>
        {
            var m = new SetupWizardModel(Windows(), catalog);
            bool ok = !m.CanBack && m.StepText == "Step 1 of 4" && m.Next() && m.Page == SetupPage.Toolchains && m.Next() && m.Next() && m.IsLast && !m.Next() && m.Back() && m.Page == SetupPage.Model;
            return ok;
        });
        Check("Toolchains Windows ohne C++: GCC (w64devkit) zum Herunterladen, fuer Publish empfohlen und im Plan; Watcom nicht angehakt", () =>
            win.Compilers.Row("gcc") is { Installable: true, DownloadId: "w64devkit", Name: "GCC (w64devkit)" } && win.Compilers.Recommended(ToolchainPurpose.Publish) == "gcc"
            && win.Compilers.Downloads().Select(r => r.Kind).SequenceEqual(new[] { "gcc" }) && win.Watcom is { Installable: true, Selected: false });
        Check("Toolchains Windows mit g++: nichts herunterzuladen, Hinweis 'ist da'", () =>
        {
            var m = new SetupWizardModel(Windows(cpp: true), catalog);
            return m.Compilers.Downloads().Count == 0 && m.Plan().All(p => p.Kind != SetupPlanKind.Toolchain) && m.ToolchainHints[0].Contains("A C++ compiler is there");
        });
        Check("Toolchains Windows: w64devkit schon da -> nicht noch einmal", () =>
        {
            var m = new SetupWizardModel(Windows() with { W64devkitThere = true }, catalog);
            return m.Compilers.Row("gcc") is { Installed: true, Installable: false } && m.Plan().All(p => p.Kind != SetupPlanKind.Toolchain);
        });
        Check("Toolchains Linux: nur Hinweise, nichts zum Herunterladen", () =>
        {
            var m = new SetupWizardModel(Linux(cpp: false), catalog);
            return !m.OffersToolchains && m.ToolchainHints.Any(h => h.Contains("apt install g++")) && m.Plan().All(p => p.Kind != SetupPlanKind.Toolchain);
        }, () => string.Join("\n", new SetupWizardModel(Linux(cpp: false), catalog).ToolchainHints));
        Check("Toolchains macOS: xcode-select", () => new SetupWizardModel(Linux(cpp: false) with { IsMacOS = true }, catalog).ToolchainHints.Any(h => h.Contains("xcode-select --install")));
        Check("Modell: empfohlenes angehakt, staerkeres markiert, zu grosses gesperrt", () =>
        {
            var m = new SetupWizardModel(Windows(memory: 8192), catalog);
            m.SelectedModelId = "qwen3.5-2b";   // (needs 6 GB: fits; 2B would not on 4 GB)
            var small = new SetupWizardModel(Windows(memory: 4096), catalog);
            small.SelectedModelId = "qwen3.5-2b";
            return win.SelectedModelId == "qwen3.5-0.8b" && win.Models.Single(o => o.Info.Id == "lfm3-1.7b").Stronger && m.SelectedModelId == "qwen3.5-2b"
                && small.SelectedModelId == "qwen3.5-0.8b" && !small.Models.Single(o => o.Info.Id == "qwen3.5-2b").Fits
                && win.Models[0].MemoryText == "needs 2 GB memory, good with 4 GB";
        });
        Check("Modell: ein installiertes wird vorgewaehlt (nichts herunterzuladen)", () =>
        {
            var env = Windows();
            var lfm = ModelCatalog.Find(catalog, "lfm3-1.7b")!;
            using (var f = File.Create(Path.Combine(env.ModelFolder!, lfm.File))) f.SetLength(lfm.Size);
            var m = new SetupWizardModel(env, catalog);
            return m.SelectedModelId == "lfm3-1.7b" && m.SelectedModel!.Installed && m.Plan().All(p => p.Kind != SetupPlanKind.Model) && m.SummaryLines.Any(l => l.Contains("is there already"));
        });
        Check("Plan: w64devkit, Modell, Pakete; Summe der Downloads", () =>
        {
            var plan = win.Plan();
            return plan.Select(p => p.Kind).SequenceEqual(new[] { SetupPlanKind.Toolchain, SetupPlanKind.Model, SetupPlanKind.Packages }) && plan[1].Id == "qwen3.5-0.8b"
                && win.TotalDownloadBytes == 80_000_000 + 507154688 && win.SummaryLines.Last() == "Downloads together: about 587 MB.";
        }, () => string.Join("\n", win.SummaryLines));
        Check("Plan: kein Modell ('none') -> nur Toolchain und Pakete", () =>
        {
            var m = new SetupWizardModel(Windows(), catalog) { SelectedModelId = "" };
            return m.Plan().Count == 2 && m.SummaryLines.Any(l => l.StartsWith("Assistant: no model"));
        });
        Check("Plan: Linux mit g++ -> nur die Pakete, nichts herunterzuladen", () =>
        {
            var m = new SetupWizardModel(Linux(), catalog);
            return m.Plan().Count == 1 && m.Plan()[0].Kind == SetupPlanKind.Packages && m.SummaryLines.Last() == "Nothing is downloaded.";
        });
        Check("Schritte: aus dem Plan (Titel in Reihenfolge)", () =>
            SetupSteps.For(win.Plan(), catalog).Select(s => s.Title).SequenceEqual(win.Plan().Select(p => p.Title)));
    }

    private static void StartAndTinycc()
    {
        var catalog = ModelCatalog.Load(CatalogSource());
        Check("Start: von der Zusammenfassung an startet der Lauf (Finish -> Launch), vorher nicht", () =>
        {
            var m = new SetupWizardModel(Windows(), catalog);
            bool early = m.Finish() == null;   // (Welcome: Start is Next there)
            while (m.Next()) { }
            var plan = m.Finish();
            var ran = new List<string>();
            var run = SetupWizardModel.Launch(plan, p => p.Select(i => (ISetupStep)new SetupStep(i.Title, _ => { lock (ran) ran.Add(i.Id); })).ToList());
            bool started = run != null && (run.Running || run.Finished);
            SpinWait.SpinUntil(() => run!.Finished, 5000);
            return early && m.Page == SetupPage.Summary && plan!.Count == 3 && started && run!.Succeeded
                && string.Join(",", ran) == "w64devkit,qwen3.5-0.8b,standard" && run.Items.Select(i => i.Title).SequenceEqual(plan.Select(i => i.Title));
        });
        Check("Start: die echten Schritte des Plans starten (Linux: die Pakete, ohne zu warten)", () =>
        {
            var m = new SetupWizardModel(Linux(), catalog);
            while (m.Next()) { }
            var installed = new ManualResetEventSlim();
            var run = SetupWizardModel.Launch(m.Finish(), p => p.Select(i => SetupSteps.Packages(i.Title, log => { installed.Set(); return Array.Empty<string>(); })).ToList());
            return run != null && installed.Wait(5000) && SpinWait.SpinUntil(() => run.Succeeded, 5000);
        });
        Check("Start: ein leerer Plan startet nichts (null: der Aufrufer sagt es)", () =>
            SetupWizardModel.Launch(Array.Empty<SetupPlanItem>(), _ => Array.Empty<ISetupStep>()) == null && SetupWizardModel.Launch(null, _ => Array.Empty<ISetupStep>()) == null);
        Check("Pakete: warten auf die Installation des Starts, Cancel beendet das Warten", () =>
        {
            using var startup = new ManualResetEvent(false);
            bool calledEarly = false, called = false;
            var run = new SetupRun(new[] { SetupSteps.Packages("p", log => { called = true; return Array.Empty<string>(); }, startup) });
            var t = run.Start();
            Thread.Sleep(200);
            calledEarly = called;
            string phase = run.Items[0].Phase;
            startup.Set();
            t.Wait(5000);
            var run2 = new SetupRun(new[] { SetupSteps.Packages("p", log => Array.Empty<string>(), new ManualResetEvent(false)) });
            var t2 = run2.Start();
            Thread.Sleep(100);
            run2.Cancel();
            return !calledEarly && phase == "Waiting for the installation that spark started" && called && run.Succeeded && t2.Wait(5000) && run2.Items[0].State == SetupItemState.Cancelled;
        });
        Check("TinyCC: Windows mit gepinntem Hash -> angeboten (C only, Groesse), fuer Build empfohlen", () =>
        {
            var m = new SetupWizardModel(Windows() with { TinyccPinned = true }, catalog);
            var t = m.Compilers.Row("tcc")!;
            return t is { Installable: true, DownloadId: "tcc", CompilesCpp: false } && t.Traits.Contains("C only") && t.SizeText == "about 1.0 MB" && t.StateText == "not installed - download (about 1.0 MB)"
                && m.Compilers.Recommended(ToolchainPurpose.Build) == "tcc";
        });
        Check("TinyCC: ohne gepinnten Hash nicht angeboten, Hinweis statt dessen; gepinnt -> Schritt im Plan", () =>
        {
            var m = new SetupWizardModel(Windows(), catalog);
            var p = new SetupWizardModel(Windows() with { TinyccPinned = true }, catalog);
            return !m.Compilers.Row("tcc")!.Installable && m.ToolchainHints.Any(h => h.Contains("TinyCC"))
                && p.Plan().Any(i => i.Kind == SetupPlanKind.Toolchain && i.Id == "tcc") && SetupSteps.For(p.Plan(), catalog).Count == p.Plan().Count;
        });
        Check("TinyCC: Linux/macOS nur Hinweis (apt / brew), da -> kein Hinweis", () =>
            new SetupWizardModel(Linux(cpp: false), catalog).ToolchainHints.Any(h => h.Contains("sudo apt install tcc"))
            && new SetupWizardModel(Linux(cpp: false) with { IsMacOS = true }, catalog).ToolchainHints.Any(h => h.Contains("brew install tcc"))
            && !new SetupWizardModel(Linux() with { Detected = new[] { new DetectedToolchain("tcc", "TinyCC", "/usr/bin/tcc") } }, catalog).ToolchainHints.Any(h => h.Contains("TinyCC"))
            && !new SetupWizardModel(Linux(), catalog).Compilers.Row("tcc")!.Installable);
    }

    /// <summary>The compilers for Build and Publish (Tools > Options > Compilers, the wizard's grid; fire.Native MachineToolchains): toolchain.json read and written, the choice
    /// resolved (project > IDE > automatic), the C++ fallback of TinyCC, the grid's downloads with the checkbox. FIRE_TESTS_FILTER=Compiler narrows to them.</summary>
    private static void Compilers()
    {
        var catalog = ModelCatalog.Load(CatalogSource());
        Func<ToolchainDef, string?> Finder(params string[] kinds) => def => kinds.Contains(def.EffectiveKind) ? "/x/" + def.EffectiveKind : null;

        Check("Compiler: toolchain.json - build/publish hin und zurueck, der Toolchain der Maschine bleibt", () =>
        {
            string file = Path.Combine(EmptyFolder(), "toolchain.json");
            bool none = MachineToolchains.Load(file) == MachineToolchains.Default;
            File.WriteAllText(file, """{ "Extends": "gcc", "Kind": "gcc", "Compiler": "/opt/kit/bin/g++" }""");
            var old = MachineToolchains.Load(file);   // (an older fire: its toolchain is the choice for both)
            new MachineToolchains("tcc", "gcc").Save(file);
            var back = MachineToolchains.Load(file);
            new MachineToolchains(MachineToolchains.Automatic, "msvc").Save(file);
            var auto = MachineToolchains.Load(file);
            File.WriteAllText(file, """{ "build": "turbo-c", "publish": "W64DEVKIT" }""");
            var odd = MachineToolchains.Load(file);
            return none && old.Build == "gcc" && old.Publish == "gcc" && old.Legacy?.Compiler == "/opt/kit/bin/g++"
                && back.Build == "tcc" && back.Publish == "gcc" && back.Legacy?.Compiler == "/opt/kit/bin/g++"
                && auto.Build == MachineToolchains.Automatic && auto.Publish == "msvc" && odd.Build == MachineToolchains.Automatic && odd.Publish == "gcc";
        });
        Check("Compiler: SaveMachineToolchain (w64devkit) laesst build/publish stehen", () =>
        {
            string file = MachineToolchains.DefaultFile;
            string? before = File.Exists(file) ? File.ReadAllText(file) : null;
            try
            {
                new MachineToolchains("tcc", "clang").Save();
                ToolchainSetup.SaveMachineToolchain(new ToolchainDef { Extends = "gcc", Kind = "gcc", Compiler = "/opt/w64/g++" });
                var m = MachineToolchains.Load();
                return m.Build == "tcc" && m.Publish == "clang" && ToolchainSetup.LoadMachineToolchain()?.Compiler == "/opt/w64/g++";
            }
            finally { if (before != null) File.WriteAllText(file, before); else File.Delete(file); }
        });
        Check("Compiler: automatisch - Build TinyCC, Publish GCC (empfohlen); sonst der beste da; ein gewaehlter, der fehlt -> automatisch", () =>
        {
            var auto = MachineToolchains.Default;
            var b = auto.Resolve(ToolchainPurpose.Build, Finder("tcc", "gcc"), windows: false);
            var p = auto.Resolve(ToolchainPurpose.Publish, Finder("tcc", "gcc"), windows: false);
            var onlyGcc = auto.Resolve(ToolchainPurpose.Build, Finder("gcc"), windows: false);
            var winPublish = auto.Resolve(ToolchainPurpose.Publish, Finder("tcc", "msvc", "clang"), windows: true);
            var missing = new MachineToolchains("clang", "clang").Resolve(ToolchainPurpose.Build, Finder("tcc", "gcc"), windows: false);
            var chosen = new MachineToolchains("gcc", "tcc").Resolve(ToolchainPurpose.Build, Finder("tcc", "gcc"), windows: false);
            var nothing = auto.Resolve(ToolchainPurpose.Build, Finder(), windows: false);
            return b.Toolchain?.EffectiveKind == "tcc" && b.Source == "automatic, recommended" && b.Toolchain.MachineChoice
                && p.Toolchain?.EffectiveKind == "gcc" && onlyGcc.Toolchain?.EffectiveKind == "gcc" && onlyGcc.Source == "automatic"
                && winPublish.Toolchain?.EffectiveKind == "msvc" && missing.Toolchain?.EffectiveKind == "tcc" && missing.Source.Contains("clang is not installed")
                && chosen.Toolchain?.EffectiveKind == "gcc" && chosen.Source == "chosen in the IDE options" && nothing.Toolchain == null
                && !ToolchainDef.BuiltIn["tcc"].MachineChoice;   // (a copy is marked, the built-in one stays)
        });
        Check("Compiler: der Toolchain der Maschine gibt den Pfad seiner Art (w64devkit-g++)", () =>
        {
            var m = new MachineToolchains(MachineToolchains.Automatic, MachineToolchains.Automatic, new ToolchainDef { Kind = "gcc", Compiler = @"C:\fire\Toolchain\w64devkit\bin\g++.exe" });
            return m.Resolve(ToolchainPurpose.Publish, Finder("gcc"), windows: true).Toolchain?.Compiler == @"C:\fire\Toolchain\w64devkit\bin\g++.exe"
                && m.DefFor("tcc").Compiler == "tcc";
        });
        Check("Compiler: Vorrang - Projekt (Name, fire.native.json) vor der Wahl der IDE vor automatisch", () =>
        {
            string file = MachineToolchains.DefaultFile;
            string? before = File.Exists(file) ? File.ReadAllText(file) : null;
            try
            {
                var host = fire.Tooling.TargetProfile.Host;
                new MachineToolchains("gcc", "gcc").Save();
                var config = new NativeConfig();
                bool gccHere = ToolchainDetector.Find(ToolchainDef.BuiltIn["gcc"]) != null;
                var ide = config.ResolveToolchain(host, null, ToolchainPurpose.Build);
                var named = config.ResolveToolchain(host, "clang", ToolchainPurpose.Build);
                var fromFile = new NativeConfig { Toolchain = "tcc" }.ResolveToolchain(host, null, ToolchainPurpose.Publish);
                var legacy = config.ResolveToolchain(host, null);   // (no purpose: the machine's toolchain or the first one found, as before)
                return (!gccHere || ide.EffectiveKind == "gcc" && ide.MachineChoice) && named.EffectiveKind == "clang" && !named.MachineChoice
                    && fromFile.EffectiveKind == "tcc" && !fromFile.MachineChoice && !legacy.MachineChoice
                    && config.NamesToolchain(host, "x") && !config.NamesToolchain(host) && new NativeConfig { Toolchain = "tcc" }.NamesToolchain(host);
            }
            finally { if (before != null) File.WriteAllText(file, before); else File.Delete(file); }
        });
        Check("Compiler: Raster unter Windows - Automatisch, TinyCC und w64devkit zum Herunterladen, der Haken an", () =>
        {
            var m = new CompilerChoiceModel(Windows() with { TinyccPinned = true });
            return m.Build == MachineToolchains.Automatic && m.Publish == MachineToolchains.Automatic && m.DownloadMissing && m.OffersDownloads
                && m.Rows.Select(r => r.Kind).SequenceEqual(new[] { "tcc", "gcc" })
                && m.Downloads().Select(r => r.DownloadId).SequenceEqual(new[] { "tcc", "w64devkit" })
                && m.Summary(ToolchainPurpose.Build) == "Build: TinyCC (automatic, recommended, downloaded first)"
                && m.Summary(ToolchainPurpose.Publish).StartsWith("Publish: GCC (w64devkit) (automatic, recommended");
        }, () => string.Join("\n", new CompilerChoiceModel(Windows() with { TinyccPinned = true }).Rows.Select(r => r.Kind)));
        Check("Compiler: Raster - eine Wahl je Spalte, der Haken aus -> nichts herunterzuladen, gewaehlt wird gespeichert", () =>
        {
            var m = new CompilerChoiceModel(Windows() with { TinyccPinned = true });
            m.Choose(ToolchainPurpose.Build, "gcc");
            m.Choose(ToolchainPurpose.Publish, "clang");   // (no row: ignored)
            bool onlyGcc = m.Downloads().Select(r => r.DownloadId).SequenceEqual(new[] { "w64devkit" });
            m.DownloadMissing = false;
            bool nothing = m.Downloads().Count == 0 && m.PlanItems().Count == 0 && m.Summary(ToolchainPurpose.Build).StartsWith("Build: no compiler");
            string file = Path.Combine(EmptyFolder(), "toolchain.json");
            m.Save(file);
            var saved = MachineToolchains.Load(file);
            return onlyGcc && nothing && m.Publish == MachineToolchains.Automatic && saved.Build == "gcc" && saved.Publish == MachineToolchains.Automatic;
        });
        Check("Compiler: Raster unter Linux - gefunden mit Pfad und Version, nichts herunterzuladen; MSVC empfohlen ohne GCC unter Windows", () =>
        {
            var env = Linux() with { Detected = new[] { new DetectedToolchain("gcc", "GCC (g++)", "/usr/bin/g++", "g++ (Debian 14.2.0) 14.2.0"), new DetectedToolchain("tcc", "TinyCC", "/usr/bin/tcc", "tcc version 0.9.27") } };
            var m = new CompilerChoiceModel(env, new MachineToolchains("tcc", "gcc"));
            var win = new CompilerChoiceModel(Windows() with { IsX64 = false, Detected = new[] { new DetectedToolchain("msvc", "Visual C++ (cl)", @"C:\VS\cl.exe") } });
            return !m.OffersDownloads && m.Build == "tcc" && m.Row("gcc")!.StateText == "g++ (Debian 14.2.0) 14.2.0 - /usr/bin/g++" && m.Effective(ToolchainPurpose.Build) == ("tcc", "chosen in the IDE options")
                && new CompilerChoiceModel(Linux(cpp: false)).Row("gcc")!.StateText.Contains("sudo apt install g++")
                && win.Recommended(ToolchainPurpose.Publish) == "msvc" && win.Effective(ToolchainPurpose.Publish).Kind == "msvc";
        });
        Check("Compiler: Wizard-Plan mit dem Haken - TinyCC und w64devkit; ohne Haken keine Toolchain, die Zusammenfassung nennt Build und Publish", () =>
        {
            var m = new SetupWizardModel(Windows() with { TinyccPinned = true }, catalog) { SelectedModelId = "" };
            var with = m.Plan().Where(p => p.Kind == SetupPlanKind.Toolchain).Select(p => p.Id).ToList();
            bool lines = m.SummaryLines.Any(l => l.StartsWith("Build: TinyCC")) && m.SummaryLines.Any(l => l.StartsWith("Publish: GCC (w64devkit)"));
            m.Compilers.DownloadMissing = false;
            var without = m.Plan();
            var watcom = new SetupWizardModel(Windows(), catalog) { SelectedModelId = "" };
            watcom.Watcom.Selected = true;
            return with.SequenceEqual(new[] { "tcc", "w64devkit" }) && lines && without.All(p => p.Kind != SetupPlanKind.Toolchain) && without.Count == 1
                && watcom.Plan().Select(p => p.Id).SequenceEqual(new[] { "w64devkit", "watcom", "standard" });
        }, () => string.Join("\n", new SetupWizardModel(Windows() with { TinyccPinned = true }, catalog).SummaryLines));
    }

    private static void Progress()
    {
        Check("Lauf: Schritte nacheinander, Fehler haelt die anderen nicht auf, Retry", () =>
        {
            int attempts = 0;
            var order = new List<string>();
            var run = new SetupRun(new ISetupStep[]
            {
                new SetupStep("a", ctx => { lock (order) order.Add("a"); ctx.Count(1, 1, "one"); }),
                new SetupStep("b", ctx => { lock (order) order.Add("b"); if (++attempts == 1) throw new IOException("network gone"); }),
                new SetupStep("c", ctx => { lock (order) order.Add("c"); }),
            });
            run.Start().Wait();
            bool first = run.Finished && !run.Succeeded && run.Items[1].State == SetupItemState.Failed && run.Items[1].StatusText == "Failed: network gone"
                && run.Items[2].State == SetupItemState.Done && run.CanRetry && run.Summary == "Setup: 1 of 3 failed" && run.Items[1].Log.Last() == "error: network gone";
            run.Retry()!.Wait();
            return first && run.Succeeded && run.Summary == "Setup done" && string.Join("", order) == "abcb" && run.Fraction == 1;
        });
        Check("Lauf: Cancel stoppt den laufenden und die wartenden; Retry setzt fort", () =>
        {
            var started = new ManualResetEventSlim();
            int runs = 0;
            var run = new SetupRun(new ISetupStep[]
            {
                new SetupStep("long", ctx => { Interlocked.Increment(ref runs); ctx.Bytes(100, 1000); started.Set(); if (runs == 1) ctx.Token.WaitHandle.WaitOne(10000); ctx.Token.ThrowIfCancellationRequested(); }),
                new SetupStep("after", ctx => { }),
            });
            var task = run.Start();
            started.Wait(5000);
            bool running = run.Running && run.Items[0].State == SetupItemState.Running && run.Summary.StartsWith("Setup 1/2: long 10%");
            run.Cancel();
            task.Wait(5000);
            bool cancelled = run.Items.All(i => i.State == SetupItemState.Cancelled) && run.Items[0].StatusText.StartsWith("Cancelled at 100 B");
            run.Retry(run.Items[0])!.Wait(5000);
            bool one = run.Items[0].State == SetupItemState.Done && run.Items[1].State == SetupItemState.Cancelled;
            run.Retry()!.Wait(5000);
            return running && cancelled && one && run.Succeeded;
        });
        string detail = "";
        Check("Fortschritt: Bytes mit Geschwindigkeit und Restzeit (Uhr vorgegeben)", () =>
        {
            double now = 0;
            var item = new SetupRun(new[] { new SetupStep("x", _ => { }) }, () => now).Items[0];
            var ctx = new SetupStepContext(item, CancellationToken.None);   // (internal: the files of the wizard are compiled into the tests)
            item.Start();
            ctx.Phase("Downloading");
            ctx.Bytes(0, 100_000_000);
            now = 1; ctx.Bytes(2_000_000, 100_000_000);
            now = 2; ctx.Bytes(4_000_000, 100_000_000);
            string text = item.StatusText;
            double? fraction = item.Fraction;
            ctx.Phase("Checking the SHA-256");
            detail = $"{text} / {fraction} / {item.StatusText}";
            return text == "Downloading - 4.0 MB of 100 MB, 2.0 MB/s, about 48 s left" && fraction is > 0.039 and < 0.041 && item.StatusText == "Checking the SHA-256...";
        }, () => detail);
        Check("Fortschritt: Zaehler mit Phase (Pakete)", () =>
        {
            var gate = new ManualResetEventSlim();
            var run = new SetupRun(new[] { new SetupStep("p", ctx => { ctx.Count(3, 40, "Installing the package io"); gate.Wait(5000); }) });
            var t = run.Start();
            SpinWait.SpinUntil(() => run.Items[0].Done == 3, 5000);
            string text = run.Items[0].StatusText;
            gate.Set();
            t.Wait(5000);
            return text == "Installing the package io - 3 of 40";
        });
        Check("Pakete: Fortschritt aus BackgroundWork, Fehler der Installation -> Schritt fehlgeschlagen", () =>
        {
            var step = SetupSteps.Packages("Build the standard packages", log =>
            {
                using (fire.Tooling.BackgroundWork.Begin("Installing the package io (2/5)", 1, 5)) Thread.Sleep(50);
                log("The standard package ai could not be installed: no compiler");
                return new[] { "io" };
            });
            var run = new SetupRun(new[] { step });
            var t = run.Start();
            SpinWait.SpinUntil(() => run.Items[0].Total == 5 || t.IsCompleted, 5000);
            string seen = run.Items[0].Phase;
            t.Wait(5000);
            return seen == "Installing the package io" && run.Items[0].State == SetupItemState.Failed && run.Items[0].Error!.Contains("ai could not be installed");
        });
        Check("Assistent: assistant.json bekommt das Modell, andere Schluessel bleiben", () =>
        {
            string folder = EmptyFolder();
            string file = Path.Combine(folder, "assistant.json");
            File.WriteAllText(file, "{\"character\":\"flint\",\"model\":\"\",\"muted\":\"x\"}");
            SetupSteps.RememberModel(Small, folder, file, new[] { Small });
            string text = File.ReadAllText(file);
            string none = Path.Combine(folder, "none.json");
            SetupSteps.RememberModel(Small, folder, none, new[] { Small });   // (no file, no other model: not written - the assistant's introduction stays)
            return text.Contains("\"model\":\"qwen2.5-0.5b\"") && text.Contains("\"character\":\"flint\"") && text.Contains("\"muted\":\"x\"") && !File.Exists(none);
        });
    }

    private static void Download()
    {
        var data = new byte[2_500_000];
        new Random(11).NextBytes(data);
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        int ranges = 0;
        using var listener = new HttpListener();
        int port = 0;
        for (int p = 47411; p < 47500; p++)
        {
            try { listener.Prefixes.Clear(); listener.Prefixes.Add($"http://127.0.0.1:{p}/"); listener.Start(); port = p; break; }
            catch (HttpListenerException) { }
        }
        _ = Task.Run(() =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { break; }
                try
                {
                    string path = ctx.Request.Url!.AbsolutePath;
                    byte[] body = path.EndsWith("/good.gguf") ? data : path.EndsWith("/bad.gguf") ? data.Reverse().ToArray() : Array.Empty<byte>();
                    if (body.Length == 0) { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
                    int from = 0;
                    string? range = ctx.Request.Headers["Range"];
                    if (range != null && range.StartsWith("bytes="))
                    {
                        from = int.Parse(range.Substring(6).TrimEnd('-'));
                        Interlocked.Increment(ref ranges);
                        if (from >= body.Length) { ctx.Response.StatusCode = 416; ctx.Response.Close(); continue; }
                        ctx.Response.StatusCode = 206;
                        ctx.Response.Headers["Content-Range"] = $"bytes {from}-{body.Length - 1}/{body.Length}";
                    }
                    ctx.Response.ContentLength64 = body.Length - from;
                    ctx.Response.OutputStream.Write(body, from, body.Length - from);
                    ctx.Response.Close();
                }
                catch (Exception) { try { ctx.Response.Abort(); } catch { } }
            }
        });
        string mirror = $"http://127.0.0.1:{port}/";
        var good = new SetupModelInfo("t", "Test", "", "repo/x", "good.gguf", data.Length, sha, "", "", 1, 1);
        var bad = good with { File = "bad.gguf" };

        Check("Download: ganz, SHA-256 geprueft, Fortschritt in Bytes", () =>
        {
            string folder = EmptyFolder();
            var phases = new List<string>();
            var run = new SetupRun(new[] { new SetupStep("m", ctx => ModelDownload.Run(good, ctx, folder, mirror)) });
            var t = run.Start();
            while (!t.IsCompleted) { string ph = run.Items[0].Phase; if (phases.LastOrDefault() != ph) phases.Add(ph); Thread.Sleep(1); }
            return run.Succeeded && phases.Contains("Checking the SHA-256") && ModelCatalog.Installed(good, folder) && !File.Exists(Path.Combine(folder, "good.gguf.part")) && run.Items[0].Log.Any(l => l.Contains("SHA-256") && l.Contains("correct"));
        });
        Check("Download: ein .part wird mit Range fortgesetzt", () =>
        {
            string folder = EmptyFolder();
            File.WriteAllBytes(Path.Combine(folder, "good.gguf.part"), data.Take(1_000_000).ToArray());
            int before = ranges;
            var run = new SetupRun(new[] { new SetupStep("m", ctx => ModelDownload.Run(good, ctx, folder, mirror)) });
            run.Start().Wait(20000);
            return run.Succeeded && ranges == before + 1 && SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "good.gguf"))).SequenceEqual(SHA256.HashData(data));
        });
        Check("Download: falsche Datei -> entfernt, Fehler mit SHA-256", () =>
        {
            string folder = EmptyFolder();
            var run = new SetupRun(new[] { new SetupStep("m", ctx => ModelDownload.Run(bad, ctx, folder, mirror)) });
            run.Start().Wait(20000);
            return run.Items[0].State == SetupItemState.Failed && run.Items[0].Error!.Contains("SHA-256") && !File.Exists(Path.Combine(folder, "bad.gguf"));
        });
        Check("Download: nicht da (404) -> fehlgeschlagen nach den Versuchen", () =>
        {
            string folder = EmptyFolder();
            var missing = good with { File = "missing.gguf" };
            var run = new SetupRun(new[] { new SetupStep("m", ctx => ModelDownload.Run(missing, ctx, folder, mirror, attempts: 2)) });
            run.Start().Wait(30000);
            return run.Items[0].State == SetupItemState.Failed && run.Items[0].Error!.Contains("404");
        }, () => "");
        Check("Download: Cancel waehrend des Wartens auf den naechsten Versuch", () =>
        {
            string folder = EmptyFolder();
            var missing = good with { File = "missing.gguf" };
            var run = new SetupRun(new[] { new SetupStep("m", ctx => ModelDownload.Run(missing, ctx, folder, mirror, attempts: 5)) });
            var t = run.Start();
            SpinWait.SpinUntil(() => run.Items[0].Log.Any(l => l.Contains("again in a moment")), 10000);
            run.Cancel();
            return t.Wait(5000) && run.Items[0].State == SetupItemState.Cancelled;
        });
        Check("Download: ein vorhandenes Modell wird nur geprueft", () =>
        {
            string folder = EmptyFolder();
            File.WriteAllBytes(Path.Combine(folder, "good.gguf"), data);
            var run = new SetupRun(new[] { new SetupStep("m", ctx => ModelDownload.Run(good, ctx, folder, "http://127.0.0.1:1/")) });
            run.Start().Wait(10000);
            return run.Succeeded;
        });
        listener.Stop();
    }
}
