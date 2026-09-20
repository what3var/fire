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

string testScript = """
    #import ""graphics""
    var fb = new Framebuffer(320, 240)
    print("Framebuffer erstellt, id=" + fb.id)
    print("Breite: " + fb.Width() + ", Hoehe: " + fb.Height())

    var con = new Console(fb)
    print("Console erstellt, id=" + con.id)
    var win = new Window(fb, "Bridge-Test")
    print("Fenster erstellt, id=" + win.id)
    
    con.SetColor(0xFFFFFF, 0)
    con.Clear()
    con.Locate(0, 0)
    con.Print("Hallo von der Bruecke!")
    con.SetPixel(5, 5, 0xFF0000)
    print("Pixel(5,5) = " + con.GetPixel(5, 5))
    
    fb.WriteByte(0, 123)
    print("Byte 0 = " + fb.ReadByte(0))
    
    
    
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

try
{
    var context = RuntimeSession.Build(new string[] { testScript }, VmExecutionMode.Debug);
    context.VirtualMachine.Run();
}
catch (System.Exception ex)
{
    System.Console.WriteLine($"HALT! {ex.Message}");
}
