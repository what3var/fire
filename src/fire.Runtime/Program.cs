using fire.Bytecode;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Terminal;
using fire.Terminal.Windows;
using fire.Terminal.Bridge;

// Grober End-to-End-Test der Grafik-Bruecke (siehe docs/CONSOLE.md):
// Manager deklarieren, alle Funktionen ueber GraphicsBridge registrieren,
// fire-Prelude + Testskript zusammen kompilieren und ausfuehren.
// 'Skriptseite': drei Klassen (Framebuffer/Console/Window) registrieren
// sich im Konstruktor selbst beim jeweiligen Manager, reichen ihre ID bei
// jeder Methode automatisch durch, und werfen im Fehlerfall eine
// HandleUnavailableException - siehe GraphicsBridge.PreludeSource.

using var font = new GdiGlyphFont();
var fbManager = new FramebufferManager();
var consoleManager = new ConsoleManager(fbManager, font);
var windowManager = new WindowManager(fbManager);

var natives = NativeRegistry.CreateDefault();
var registeredNames = new System.Collections.Generic.List<string>();
GraphicsBridge.RegisterAll(natives, fbManager, consoleManager, windowManager);
foreach (var name in natives.Names)
    registeredNames.Add(name);

System.Console.WriteLine($"Registrierte native Funktionen ({registeredNames.Count}):");
foreach (var name in registeredNames)
    System.Console.WriteLine("  " + name);

string testScript = """
    var fb = new Framebuffer(320, 240)
    print("Framebuffer erstellt, id=" + fb.id)
    print("Breite: " + fb.Width() + ", Hoehe: " + fb.Height())

    var con = new Console(fb)
    print("Console erstellt, id=" + con.id)
    con.SetColor(0xFFFFFF, 0)
    con.Clear()
    con.Locate(0, 0)
    con.Print("Hallo von der Bruecke!")
    con.SetPixel(5, 5, 0xFF0000)
    print("Pixel(5,5) = " + con.GetPixel(5, 5))

    fb.WriteByte(0, 123)
    print("Byte 0 = " + fb.ReadByte(0))

    var win = new Window(fb, "Bridge-Test")
    print("Fenster erstellt, id=" + win.id)
    var stillOpen = win.Tick()
    print("Fenster nach einem Tick offen: " + stillOpen)

    for (var i = 0; i < 255; i = i + 1) {
        con.Print("Test " + i);
        con.SetColor(i, 0)
        win.Tick()
    }

    try {
        var badFb = new Framebuffer(0, 0)
        print("FEHLER: haette werfen sollen")
    } catch (e : HandleUnavailableException) {
        print("Erwarteter Fehler gefangen: " + e.message)
    }
    """;

string combinedSource = fire.Standard.Prelude.Source + "\n" + GraphicsBridge.PreludeSource + "\n" + testScript;

System.Console.WriteLine();
System.Console.WriteLine("=== Skript-Ausgabe ===");
try
{
    var program = Parser.Parse(combinedSource);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var globalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, globalScope, natives, compiled.Classes);
    vm.Run();

    System.Console.WriteLine();
    System.Console.WriteLine("Test abgeschlossen ohne unbehandelten Fehler.");
}
catch (System.Exception ex)
{
    System.Console.WriteLine($"FEHLER: {ex.Message}");
}
