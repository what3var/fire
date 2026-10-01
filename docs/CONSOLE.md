# ScriptLang.Terminal – eigenständige SDL3-Konsole

Vier eigene Projekte, komplett unabhängig von `ScriptLang.Editor` (referenziert
ihn nicht, wird nicht von ihm referenziert) - gedacht zur direkten
Wiederverwendung in einer späteren, eigenständigen Laufzeit (Runtime).

## Projekt-Aufteilung

```
ScriptLang.Terminal            (net8.0, PLATTFORMUNABHÄNGIG)
  PixelColor, Palette, Framebuffer, TerminalCanvas,
  IGlyphFont, IFramebufferRenderer, IdManager,
  FramebufferManager, ConsoleManager

ScriptLang.Terminal.Sdl        (net8.0, referenziert nur .Terminal)
  SdlFramebufferRenderer         – IFramebufferRenderer über SDL3-CS

ScriptLang.Terminal.Windows    (net8.0-windows, referenziert .Terminal + .Sdl)
  GdiGlyphFont                    – IGlyphFont über System.Drawing/GDI+
  ConsoleWindow                   – zeigt EINEN Framebuffer in einem Fenster an
  WindowManager                   – verwaltet ConsoleWindow-Instanzen über IDs

ScriptLang.Terminal.Demo       (net8.0-windows, referenziert .Windows)
  Program.cs                      – Standalone-Demo (rohes C#-Manager-API)

ScriptLang.Terminal.Bridge     (net8.0-windows, referenziert ScriptLang + .Terminal + .Windows)
  GraphicsBridge.cs                – native Funktionsregistrierung + ScriptLang-Prelude

ScriptLang.Terminal.Bridge.Test (net8.0-windows, referenziert .Bridge)
  Program.cs                      – End-to-End-Test: Skript ruft die Grafik-API auf
```

**Warum diese Trennung**: `ScriptLang.Terminal` selbst enthält KEINE Windows-/
SDL-/GDI-Abhängigkeit und zielt bewusst auf `net8.0` statt `net8.0-windows`.
Genau das ist die Schicht, die später möglichst 1:1 portierbar bleiben soll
(z.B. nach C++) bzw. an die eine ScriptLang-Brücke direkt andocken kann.

## Die Brücke zur Skriptsprache (`ScriptLang.Terminal.Bridge`)

`GraphicsBridge.RegisterAll(natives, framebuffers, consoles, windows)`
registriert die drei Manager als native Funktionen - über
`NativeRegistry.RegisterGroup(prefix, functions)` (SPEC 8.1.6): jede
Funktionsgruppe bekommt ein eigenes Namens-Präfix
(`__GRPHFb`/`__GRPHCon`/`__GRPHWin`), der tatsächlich registrierte Name ist
`prefix + suffix` (z.B. `__GRPHFbCreate`). Bewusst "grob" gehalten - nicht
jede Manager-Methode ist abgebildet, nur Erzeugen/Zerstören plus eine
repräsentative Auswahl der gängigsten Operationen je Ressourcenart.

`GraphicsBridge.PreludeSource` ist ScriptLang-Quelltext (nach demselben
Muster wie `Standard.Prelude.Source`, siehe Parser.ParseWithPrelude), der
diese nativen Funktionen hinter drei gewöhnlichen Klassen versteckt:

```
class Framebuffer {
    int id
    construct(int width, int height) {
        this.id = __GRPHFbCreate(width, height)
        if (this.id == -1) {
            throw new HandleUnavailableException("Framebuffer konnte nicht erstellt werden.")
        }
    }
    destruct() { __GRPHFbDestroy(this.id) }
    int Width() { return __GRPHFbWidth(this.id) }
    ...
}
```

Jede Klasse (`Framebuffer`/`Console`/`Window`) registriert sich in ihrem
Konstruktor selbst beim passenden Manager (über die native `Create`-
Funktion) und merkt sich die zurückgelieferte ID als Feld - jede weitere
Methode reicht `this.id` automatisch an die zugehörige native Funktion
weiter, Skript-Code selbst sieht nie eine rohe ID. Schlägt die Erzeugung
fehl (die `Create`-Wrapper in `GraphicsBridge` fangen dafür JEDE Exception
aus dem jeweiligen Manager ab und liefern `-1` als Sentinel statt sie roh
durch die VM durchschlagen zu lassen), wirft der Konstruktor eine neue,
per `try`/`catch` fangbare `HandleUnavailableException` - eine gewöhnliche,
in `PreludeSource` selbst definierte `Exception`-Unterklasse, nach exakt
demselben Muster wie die eingebaute `IndexOutOfBoundsException` (SPEC 8.5).
`destruct()` gibt die Manager-Ressource automatisch frei, wenn das Skript-
Objekt vom Ownership-Modell zerstört wird.

Ein kombiniertes Programm besteht damit aus drei Teilen, konkateniert und
zusammen geparst (`Standard.Prelude.Source + GraphicsBridge.PreludeSource
+ nutzerSkript`, dann `Parser.Parse(...)`): die Standardbibliothek, die
Grafik-Brücke, und das eigentliche Nutzer-Skript.

### Zeichnen mit Pixel-Koordinaten, Text, Mausposition, Ereignis-Warteschlange

- **Text schnell:** `TerminalCanvas.DrawGlyph`/`Print` schreiben ein Zeichen zeilenweise (eine Schrift mit `IBitmapGlyphFont`, z.B. `IntegratedGlyphFont`): pro Bitmap-Zeile
  ein Tabellenzugriff (`GlyphMasks`, Masken für vier Pixel je `Vector128`) und ein `ConditionalSelect` statt einer Abfrage je Pixel - ca. 30 ns statt 370 ns pro Zeichen (80x30 Zeichen: ~0,07 ms
  statt ~0,9 ms). Liegt die Zelle nicht vollständig im Framebuffer oder hat die Schrift keine Bitmap-Zeilen, bleibt der pixelweise Weg (`IsPixelSet` + `SetPixel` mit Clipping). Zeichen
  außerhalb der 256 der Tabelle werden als `?` gezeichnet. `Framebuffer.FillRect` füllt zeilenweise per `Span.Fill`.
- **`Console`-Methoden mit Pixel-Koordinaten** (Bridge): `FillRect`, `DrawRect`, `DrawLine`, `DrawText(x, y, text, color, background)`, `CellWidth()`, `CellHeight()`. Die Farben dieser Methoden sind
  ROHE Werte (`r + g*256 + b*65536 + a*16777216`, Alpha 255 = deckend), keine Palette-Indizes wie bei `SetColor`; ein Hintergrund mit Alpha 0 (z.B. `0`) ist transparent. `GetPixel` liefert den Wert als
  32-Bit-Zahl MIT Vorzeichen (deckende Farben also negativ).
- **Mausposition in Framebuffer-Pixeln:** SDL meldet Fenster-Koordinaten, das Fenster darf aber skaliert werden (der Framebuffer wird gestreckt); `SdlFramebufferRenderer` rechnet Position und Bewegung
  auf Framebuffer-Pixel um. Texteingabe (`SDL.StartTextInput`) ist eingeschaltet.
- **Ereignisse abfragen statt Callback:** Ein `Window`-Callback läuft auf einer eigenen VM mit einer ISOLIERTEN KOPIE der globalen Variablen (SPEC 8.1.4) - Objekte mit Lambdas oder Verweisen auf
  fremde Objekte lassen sich dabei nicht kopieren, und Änderungen am Original gehen verloren. Deshalb gibt es daneben `Window.EnableEvents()` und `Window.NextEvent()`: die Ereignisse, die `Tick`
  abholt, landen zusätzlich in einer Warteschlange (`WindowManager.EnableEventQueue`/`NextEvent`, höchstens 4096), und das Skript holt sie im Hauptprogramm ab. Ein Ereignis ist ein Array: `[0]` der
  Typ (`EventType`), der Rest je Typ - `MouseDown`/`MouseUp` `[typ, taste, x, y]`, `MouseMove` `[typ, x, y, tasten]`, `MouseScroll` `[typ, scrollX, scrollY, x, y]`, `KeyDown`/`KeyUp`
  `[typ, keycode, scancode, modifier, wiederholt]`, `TextInput` `[typ, text]`, `Close` `[typ]`; Positionen sind ganze Pixel. `WindowManager` nimmt dafür optional eine Renderer-Fabrik entgegen
  (`Func<IFramebufferRenderer>`, Vorgabe SDL) - so lassen sich Fenster ohne SDL testen.
- Die Oberflächen-Bibliothek `#import "ui"` baut darauf auf, siehe `docs/UI.md`.

## Drei unabhängig verwaltete Ressourcenarten, jede über eine eigene ID

Der zentrale Architekturpunkt dieser Ausbaustufe: **Framebuffer**, **Konsolen**
(`TerminalCanvas`) und **Fenster** (`ConsoleWindow`) sind DREI GETRENNTE,
jeweils über einen eigenen Manager mit aufsteigenden IDs verwaltete
Ressourcenarten - kein Objekt "besitzt" mehr ein anderes automatisch:

```
FramebufferManager  .CreateFramebuffer(w, h)      -> int fbId
                     .DestroyFramebuffer(fbId)     -> bool
ConsoleManager       .CreateConsole(fbId)          -> int consoleId   (an EINEN Framebuffer gebunden)
                     .DestroyConsole(consoleId)    -> bool
WindowManager        .CreateWindow(fbId)           -> int windowId    (zeigt EINEN Framebuffer an)
                     .DestroyWindow(windowId)      -> bool
```

IDs sind pro Manager aufsteigend und werden NIE wiederverwendet (siehe
`IdManager<T>`) - eine zu spät weitergereichte, bereits zerstörte ID zeigt
verlässlich ins Leere statt versehentlich auf ein neues, andersartiges
Objekt. Alle drei Manager-Klassen bilden zusammen das **rein funktionale
API**: praktisch jede Operation (Print/Color/Locate/SetPixel/DrawLine/...)
ist eine Methode, die nur eine Ressourcen-ID plus Primitive (int/byte/string)
nimmt, NIE eine Objektreferenz - genau die Form, die sich später 1:1 an eine
Skriptsprachen-Bridge (`NativeRegistry`-Funktionen) weiterreichen lässt.
`GetFramebuffer(id)`/`GetConsole(id)`/`GetWindow(id)` sind die einzigen
Ausnahmen (liefern die echte C#-Instanz) - für C#-seitige Weiterverwendung,
kein Teil der eigentlichen Oberfläche.

**Warum ein Fenster keinen eigenen Framebuffer mehr besitzt**: `ConsoleWindow`
ist jetzt eine dünne Hülle, die NUR das Rendering eines EXTERN übergebenen
Framebuffers übernimmt. Das erlaubt z.B., einen Framebuffer OHNE Fenster zu
betreiben (reines Offscreen-Rendering), oder ihn später an ein anderes
Fenster umzuhängen, ohne die Konsole/den Inhalt neu aufzubauen.

## Farben: `PixelColor`, `Palette`, direkter Wert ODER Palette-Index

- **`PixelColor` konvertiert implizit zu `int`** (ein 32-Bit-Farbwert, R,G,B,A
  in fester Byte-Reihenfolge, siehe unten) - jede Funktion, die einen
  `int color`-Parameter nimmt, akzeptiert deshalb auch direkt eine
  `PixelColor`-Konstante wie `PixelColor.Yellow`, ohne Umweg.
- **`Palette`**: eine anpassbare 256-Farben-Palette (Index 0-255), jeder
  Eintrag per `SetColor(byte index, int color)` frei überschreibbar
  (klassisches VGA-Palettenregister-Verhalten - der Index bleibt, die
  dahinterliegende Farbe kann sich ändern). Default-Belegung im
  xterm-256-Stil: Index 0-15 die klassischen 16 CGA/QBasic-Farben, 16-231
  ein 6x6x6-Farbwürfel (216 Farben), 232-255 ein 24-stufiger Graukeil -
  bewusst NICHT der exakte historische VGA-Standard-DAC (dessen genaue Werte
  sich ohne Testmöglichkeit hier nicht verlässlich reproduzieren ließen),
  sondern eine nachvollziehbare, garantiert korrekte Formel; da die Palette
  ohnehin frei überschreibbar ist, zählt das für die Defaults mehr als
  historische Exaktheit.
- Jede `TerminalCanvas` (und damit jede "Konsole") hat ihre **eigene**
  Palette (wie Cursor/Farben, nicht geteilt).
- **Zwei Überladungen** für Color/SetPixel/DrawLine/DrawRect/FillRect: eine
  nimmt einen direkten `PixelColor`/`int`-Wert, eine einen `byte`-
  Palette-Index (schlägt intern in der Palette nach). Über `ConsoleManager`
  heißen die Index-Varianten `...ByIndex` (z.B. `SetPixelByIndex`), da C#
  hier keine reine Überladung nach Rückgabetyp/ID-Signatur zulässt.

## Byteweiser Framebuffer-Zugriff

`FramebufferManager` bietet sowohl byteweisen als auch blockweisen Zugriff
auf die rohen Pixel-Daten:

- `ReadByte(id, offset)` / `WriteByte(id, offset, value)` - IMMER korrekt
  (manuell pro Kanal geschoben/maskiert, unabhängig von der Host-Endianness).
  Byte-Offset 0 = R des ersten Pixels, 1 = G, 2 = B, 3 = A, 4 = R des
  zweiten Pixels, usw. (feste Reihenfolge, siehe `PixelColor`).
- `ReadBytes(id)` / `WriteBytes(id, data)` - NICHT zwingend, aber aus
  Convenience-/Geschwindigkeitsgründen: kopiert den GESAMTEN Puffer per
  `Buffer.BlockCopy` (roher Speicherzugriff, schneller als eine Schleife) -
  das ergibt exakt dieselbe R,G,B,A-Byte-Reihenfolge, WEIL .NET auf allen
  realistischen Zielplattformen (x86/x64/ARM im Normalbetrieb) little-endian
  ist und `PixelColor`s Speicher-Layout genau dafür ausgelegt ist (siehe
  unten) - anders als `ReadByte`/`WriteByte` verlässt sich das hier also
  bewusst auf die Host-Endianness, für den Geschwindigkeitsgewinn.

## Kernideen (weiterhin gültig)

- **Ein Framebuffer ist die einzige Wahrheit.** `TerminalCanvas` schreibt
  ausschließlich in einen `Framebuffer` (nie direkt ins Fenster); ein
  `IFramebufferRenderer` liest den fertigen Inhalt nur noch aus.
- **`PixelColor`: feste Byte-Reihenfolge, ohne Overhead abrufbar.** `R`, `G`,
  `B`, `A` liegen IMMER in genau dieser Reihenfolge im Speicher (Byte 0-3),
  unabhängig von der Host-Endianness. Über
  `[StructLayout(LayoutKind.Explicit)]` liegt zusätzlich ein `Packed`-Feld
  (uint) auf DENSELBEN 4 Bytes - beide Sichten sind buchstäblich derselbe
  Speicher, keine Umrechnung, kein zusätzlicher Speicherverbrauch.
- **"Wählbarer Framebuffer".** `TerminalCanvas.Target` (bzw.
  `ConsoleManager.SetTargetFramebuffer`) ist jederzeit umschaltbar – Cursor-
  Position, Farben UND Palette gehören dem `TerminalCanvas`-Objekt selbst,
  nicht dem jeweiligen Puffer.
- **Terminal-Emulation ist QBasic-artig.** `Print`/`Color`/`Locate` verändern
  Text, Vorder-/Hintergrundfarbe und Cursor-Position. Jedes geschriebene
  Zeichen übermalt immer die **gesamte Zelle** (außer `Background == null`,
  "optional transparent").
- **Kein Scrollback.** Erreicht der Cursor das Bildschirmende, verschiebt
  `Framebuffer.ScrollUp` den gesamten Inhalt eine Zellenhöhe nach oben; was
  oben herausfällt, ist unwiderruflich verloren.
- **Fenstergröße ist unabhängig vom Zeichenraster.** `SdlFramebufferRenderer`
  zeichnet den Framebuffer immer auf die gesamte aktuelle Fenstergröße
  gestreckt/gestaucht.
- **Kein eigenes Alpha-Blending.** `Framebuffer.SetPixel`/`FillRect`
  schreiben ein Zielpixel immer vollständig, es wird nichts gemischt.
- **SDL-Texturformat passend zu `PixelColor`.** `SDL.PixelFormat.RGBA8888`
  (in SDL3 der Name für das gepackte 32-Bit-RGBA-Format) - "R,G,B,A in genau
  dieser Byte-Reihenfolge im Speicher", passend zu `PixelColor.Packed`.

## Zwei Nutzungsarten von `ConsoleWindow`/`WindowManager`

- `ConsoleWindow.Run()` – blockierende Standalone-Schleife für EIN Fenster.
- `WindowManager.Tick(id)` / `WindowManager.TickAll()` – ein einzelner Zyklus
  (Events abholen + zeichnen) für einen Host mit eigener Schleife -
  `TickAll()` bedient dabei beliebig viele gleichzeitig offene Fenster und
  räumt automatisch jedes auf, das der Nutzer währenddessen geschlossen hat.

## Bewusst noch NICHT Teil dieser Ausbaustufe

- **Keine Anbindung an ScriptLang selbst** (keine neuen `NativeRegistry`-
  Funktionen). Das bestehende `print` in `ScriptLang`/
  `NativeRegistry.CreateDefault()` bleibt unverändert. Die drei Manager-
  Klassen sind aber genau so geschnitten (flache, ID-basierte Methoden mit
  Primitive-Parametern), dass eine spätere Anbindung im Wesentlichen nur noch
  aus "jede Manager-Methode als native Funktion registrieren" bestehen
  dürfte.
- **Kein Puffer-Swapping/Double-Buffering-Komfort.**
- **Keine eingebettete Bitmap-Schrift.** `GdiGlyphFont` nutzt eine
  installierte Systemschrift (Default: "Consolas") über GDI+ – funktioniert
  nur unter Windows.

## Bekannter Vorbehalt: SDL3-CS ungetestet

Diese Sandbox hat keinen Netzwerkzugriff (kein NuGet-Restore, kein Kompilieren
möglich). `SdlFramebufferRenderer.cs` ist gegen die tatsächliche SDL3-CS-
Namenskonvention geschrieben (verifiziert über die offiziellen Beispiele in
github.com/edwardgushchin/SDL3-CS): kein doppeltes `SDL_`-Präfix mehr (`SDL.
Init` statt `SDL_Init`), Konstanten-Gruppen als echte C#-Enums (`SDL.
EventType.Quit`, `SDL.PixelFormat.RGBA8888`), `SDL.CreateWindowAndRenderer`
als kombinierter Aufruf. Trotzdem noch NICHT selbst gegen das tatsächliche
Paket kompiliert worden - am unsichersten bleibt die genaue Schreibweise von
`SDL.TextureAccess.Streaming` sowie ob `SDL.Event.Type` bereits
`SDL.EventType` ist oder ein roher `uint`-Wert (dafür steht im Code bewusst
die per Cast robuste Variante). Bitte beim ersten lokalen Build per
IntelliSense gegenprüfen. Sollte sich die tatsächliche API leicht
unterscheiden, ist nur `ScriptLang.Terminal.Sdl` betroffen.
