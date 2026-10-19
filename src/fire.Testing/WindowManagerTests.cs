using fire.Bytecode;
using fire.Compiler;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Values;

/// <summary>fire's `WindowManager` in the virtual machine (docs/UI.md): windows cycled on the program's thread (`Run`, `Tick`) or shown by a thread of the
/// manager (`Show`, `Start`), whose cycles the program runs at its safe points and at its end. The OS window is a fake that closes after a number of pumps
/// and remembers the thread that opened and pumped it.</summary>
static class WindowManagerTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Fenster-Manager: {title}" : $"FEHLER: Fenster-Manager: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    /// <summary>An OS window for the tests: thread-safe, closes after `ClosesAfter` pumps (0: never), and can be given events from the script.</summary>
    private sealed class ThreadedFake : fire.Terminal.IFramebufferRenderer
    {
        private readonly object _gate = new();
        private readonly List<fire.Terminal.Event.IEvent> _pending = new();
        public int ClosesAfter;
        public int Pumps;
        public int Presents;
        public int? OpenedOn;
        public readonly HashSet<int> PumpedOn = new();
        public string Title = "";
        public bool Disposed;
        public bool VSync { get; set; } = true;
        public bool TouchMouse { get; set; } = true;

        public void Initialize(string title, int initialWidth, int initialHeight, int internalHandle)
        {
            lock (_gate) { Title = title; OpenedOn = Environment.CurrentManagedThreadId; }
        }

        public void Present(fire.Terminal.Framebuffer framebuffer) { lock (_gate) Presents++; }

        public void Dispose() { lock (_gate) Disposed = true; }

        public fire.Terminal.WindowPumpResult PumpEvents()
        {
            lock (_gate)
            {
                Pumps++;
                PumpedOn.Add(Environment.CurrentManagedThreadId);
                var events = _pending.ToArray();
                _pending.Clear();
                bool open = ClosesAfter == 0 || Pumps < ClosesAfter;
                return new fire.Terminal.WindowPumpResult { StillOpen = open, Events = events };
            }
        }

        public void Key(int code)
        {
            lock (_gate) _pending.Add(new fire.Terminal.Event.KeyEvent { Type = fire.Terminal.Event.EventType.KeyDown, KeyCode = code });
        }

        public void Click(float x, float y)
        {
            lock (_gate)
            {
                _pending.Add(new fire.Terminal.Event.ClickEvent { Type = fire.Terminal.Event.EventType.MouseDown, Button = 1, X = x, Y = y });
                _pending.Add(new fire.Terminal.Event.ClickEvent { Type = fire.Terminal.Event.EventType.MouseUp, Button = 1, X = x, Y = y });
            }
        }
    }

    private sealed record Result(List<string> Lines, List<ThreadedFake> Windows, int MainThread, string? Unhandled, int HoldsAfter);

    private static string StdPrelude(string import) => fire.Package.Manager.PackageStore.Default.FindImport(import)!.ReadPrelude()!;

    /// <summary>Runs a script with graphics, windows, reflection and ui; `closesAfter[i]` is how many pumps the i-th window stays open.</summary>
    private static Result Run(string script, params int[] closesAfter)
    {
        var lines = new List<string>();
        var fakes = new List<ThreadedFake>();
        var sources = new[] { fire.Standard.Prelude.Source, StdPrelude("graphics"), StdPrelude("windows"), StdPrelude("reflection"), StdPrelude("ui"), script };
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var program = Parser.ParseMultiple(sources.Select(src => Preprocessor.Process(src, Directory.GetCurrentDirectory(), included)).ToList());
        var natives = new NativeRegistry();
        natives.Register("print", args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();

        VM? vm = null;
        IReadOnlyDictionary<string, RuntimeClass>? classes = null;
        var fbManager = new fire.Terminal.FramebufferManager();
        var conManager = new fire.Terminal.RendererManager(fbManager, new fire.Terminal.IntegratedGlyphFont());
        var winManager = new fire.Terminal.Windows.WindowManager(fbManager,
            (l, v) => FireRuntime.RunCallback(l, v, natives, classes, () => vm!.SnapshotGlobals(), message => { lock (lines) lines.Add("CB: " + message); }, VmExecutionMode.Debug, vm),
            () =>
            {
                lock (fakes)
                {
                    var fake = new ThreadedFake { ClosesAfter = fakes.Count < closesAfter.Length ? closesAfter[fakes.Count] : 0 };
                    fakes.Add(fake);
                    return fake;
                }
            },
            FireRuntime.AddHostHold);
        fire.Terminal.Bridge.GraphicsBridge.RegisterAll(natives, fbManager, conManager);
        fire.Windows.Bridge.WindowsBridge.RegisterAll(natives, winManager);
        ReflectionNatives.Register(natives);
        natives.Register("__TestKey", args => { lock (fakes) fakes[(int)args[0].AsInt()].Key((int)args[1].AsInt()); return Value.MakeUndefined(); });
        natives.Register("__TestClick", args => { lock (fakes) fakes[(int)args[0].AsInt()].Click((float)args[1].AsFloat(), (float)args[2].AsFloat()); return Value.MakeUndefined(); });
        natives.Register("__TestWait", args => { Thread.Sleep((int)args[0].AsInt()); return Value.MakeUndefined(); });

        var resolved = Resolver.Resolve(program, natives.Names);
        var compiled = Compiler.Compile(program, resolved, natives);
        classes = compiled.Classes;
        VM.ResetTerminateForTests();
        vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, isMainThreadVm: true, executionMode: VmExecutionMode.Debug);
        // the program runs on a thread of its own (as in the editor): its id is "the program's thread"
        int runnerId = 0;
        var runner = new Thread(() => { runnerId = Environment.CurrentManagedThreadId; try { vm.Run(); } finally { winManager.StopManagers(); } }, 16 * 1024 * 1024);
        runner.Start();
        if (!runner.Join(TimeSpan.FromSeconds(20)))
        {
            lines.Add("TIMEOUT");
            VM.RequestTerminate(Value.MakeUndefined());
            runner.Join(5000);
        }
        VM.ResetTerminateForTests();
        string? unhandled = vm.UnhandledException != null ? new UncaughtScriptException(vm.UnhandledException).ToString() : null;
        lock (fakes) return new Result(lines, fakes.ToList(), runnerId, unhandled, FireRuntime.HostHolds);
    }

    public static void Run()
    {
        Console.WriteLine("=== WindowManager ===");
        try
        {
            CurrentThread();
            Started();
            Managers();
            UiRoot();
        }
        catch (Exception ex)
        {
            Check("ohne Ausnahme durchgelaufen", false, ex.ToString());
        }
        Console.WriteLine(_failures == 0 ? "Alle Fenster-Manager-Pruefungen bestanden." : $"FEHLER: {_failures} Fenster-Manager-Pruefung(en) fehlgeschlagen.");
    }

    private static string Show(Result r) => string.Join("\n", r.Lines) + (r.Unhandled != null ? "\nUNHANDLED: " + r.Unhandled : "");

    // Run(): the manager on the program's thread, blocking until the windows are closed
    private static void CurrentThread()
    {
        var r = Run("""
            #import "windows"
            var fb = new Framebuffer(32, 24)
            var a = new Window(fb, "A")
            var b = new Window(fb, "B")
            WindowManager.Default.Attach(a)
            var cycles = 0
            a.onCycle = func () => { cycles++ }
            a.RegisterKeyDown(func (int k, int s, int m, bool rep) => { print("key " + k) })
            WindowManager.Default.Attach(b)
            print("b vsync " + b.VSync + ", manager " + (b.Manager == WindowManager.Default))
            __TestKey(0, 65)
            a.Run()
            print("cycles " + cycles + ", left " + WindowManager.Default.Count + ", a closed " + a.IsClosed)
            """, 3, 5);
        Check("Run: blockiert auf dem Programm-Thread, bis alle Fenster zu sind; Callbacks und onCycle laufen", r.Unhandled == null
            && r.Lines.Contains("key 65") && r.Lines.Contains("cycles 3, left 0, a closed True") && r.Lines.Contains("b vsync False, manager True"), Show(r));
        Check("Run: die Fenster oeffnen und pumpen auf dem Programm-Thread (kein eigener Thread)", r.Windows.Count == 2
            && r.Windows.All(w => w.OpenedOn == r.MainThread && w.PumpedOn.SetEquals(new[] { r.MainThread })), string.Join("; ", r.Windows.Select(w => $"{w.OpenedOn} {string.Join(",", w.PumpedOn)} main {r.MainThread}")));
        Check("Run: zweites Fenster laeuft weiter, bis es selbst zu ist (5 Zyklen)", r.Windows.Count == 2 && r.Windows[1].Pumps == 5, Show(r));
    }

    // Show(): the manager's thread shows the window; the program goes on, runs the cycles at its safe points and waits at its end
    private static void Started()
    {
        var r = Run("""
            #import "windows"
            var fb = new Framebuffer(32, 24)
            var w = new Window(fb, "Shown")
            var cycles = 0
            w.onCycle = func () => { cycles++ }
            w.RegisterKeyDown(func (int k, int s, int m, bool rep) => { print("key " + k + " after " + (cycles > 0)) })
            w.Show()
            print("goes on, started " + w.Manager.IsStarted)
            __TestKey(0, 66)
            var spin = 0
            while (cycles < 3) { spin++; __TestWait(2) }
            print("the program ran cycles while it worked")
            """, 12);
        Check("Show: das Programm laeuft weiter, die Zyklen laufen an seinen sicheren Punkten (Schleife)", r.Unhandled == null
            && r.Lines.FirstOrDefault() == "goes on, started True" && r.Lines.Contains("the program ran cycles while it worked"), Show(r));
        Check("Show: Callbacks laufen im Programm", r.Lines.Any(l => l.StartsWith("key 66")), Show(r));
        Check("Show: der Thread des Managers oeffnet und pumpt das Fenster (nicht der Programm-Thread)", r.Windows.Count == 1
            && r.Windows[0].OpenedOn != null && r.Windows[0].OpenedOn != r.MainThread && !r.Windows[0].PumpedOn.Contains(r.MainThread), string.Join("; ", r.Windows.Select(w => $"{w.OpenedOn} {string.Join(",", w.PumpedOn)} main {r.MainThread}")));
        Check("Show: das Programmende wartet, bis das Fenster zu ist, und gibt dann frei", r.Windows.Count == 1 && r.Windows[0].Pumps >= 12 && r.Windows[0].Disposed && r.HoldsAfter == 0 && !r.Lines.Contains("TIMEOUT"),
            $"pumps {r.Windows.FirstOrDefault()?.Pumps} disposed {r.Windows.FirstOrDefault()?.Disposed} holds {r.HoldsAfter}\n" + Show(r));
        Check("Show: Bilder werden auf dem Thread gezeigt", r.Windows.Count == 1 && r.Windows[0].Presents > 0, Show(r));

        // Run() on a started manager: waits while the thread shows the windows
        r = Run("""
            #import "windows"
            var fb = new Framebuffer(32, 24)
            var w = new Window(fb, "Shown")
            var cycles = 0
            w.onCycle = func () => { cycles++ }
            w.Show()
            w.Run()
            print("after run: closed " + w.IsClosed + ", cycles > 2 " + (cycles > 2))
            """, 6);
        Check("Show + Run: Run wartet auf dem Programm-Thread, bis der Thread des Managers das Fenster zu meldet", r.Unhandled == null && r.Lines.Contains("after run: closed True, cycles > 2 True"), Show(r));
    }

    // several managers, the manager as a constructor argument, Attach moves a window
    private static void Managers()
    {
        var r = Run("""
            #import "windows"
            var fb = new Framebuffer(32, 24)
            var mine = new WindowManager()
            var a = new Window(fb, "A", mine)
            var b = new Window(fb, "B")
            print("a: " + (a.Manager == mine) + ", b: " + (b.Manager == undefined) + ", default: " + (WindowManager.Default != mine))
            mine.Attach(b)
            print("b: " + (b.Manager == mine) + ", count " + mine.Count)
            WindowManager.Default.Attach(b)
            print("moved: " + (b.Manager == WindowManager.Default) + ", mine " + mine.Count + ", default " + WindowManager.Default.Count)
            mine.Detach(a)
            print("detached: " + (a.Manager == undefined) + ", mine " + mine.Count)
            var t = 0
            while (WindowManager.Default.Tick()) { t++ }
            print("ticks " + t)
            """, 0, 4);
        Check("Mehrere Manager: Konstruktor-Parameter haengt an, Attach verschiebt, Detach loest, Tick zaehlt", r.Unhandled == null
            && r.Lines.SequenceEqual(new[] { "a: True, b: True, default: True", "b: True, count 2", "moved: True, mine 1, default 1", "detached: True, mine 0", "ticks 3" }), Show(r));
    }

    // UI.Root with Show: the interface is drawn and its clicks are handled in the program while the manager's thread shows the window
    private static void UiRoot()
    {
        var r = Run("""
            #import "ui"
            var fb = new Framebuffer(200, 100)
            var win = new Window(fb, "UI")
            var ui = new UI.Root(fb, win)
            var clicks = 0
            var button = new UI.Button("Click", 10, 10)
            button.onClick = func () => { clicks++; print("clicked " + clicks) }
            ui.Add(button)
            ui.Show()
            __TestClick(0, 15.0, 15.0)
            """, 10);
        Check("UI.Root.Show: Klicks werden im Programm behandelt, das Programmende wartet aufs Schliessen", r.Unhandled == null && r.Lines.Contains("clicked 1") && !r.Lines.Contains("TIMEOUT"), Show(r));

        r = Run("""
            #import "ui"
            var fb = new Framebuffer(200, 100)
            var one = new Window(fb, "One")
            var ui = new UI.Root(fb, one)
            var fb2 = new Framebuffer(100, 50)
            var two = new Window(fb2, "Two")
            var other = new UI.Root(fb2, two)
            var ticks = 0
            other.onTick = func () => { ticks++ }
            ui.Run(other)
            print("both done, other ticks " + ticks + ", same manager " + (other.window.Manager == undefined))
            """, 3, 4);
        Check("UI.Root.Run(other): beide Fenster laufen ueber den Manager, bis beide zu sind", r.Unhandled == null && r.Lines.Contains("both done, other ticks 4, same manager True"), Show(r));
    }
}
