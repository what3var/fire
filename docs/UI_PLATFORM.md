# Eigene Plattformschicht statt SDL, Effekte und ein modernerer Stil

Status: **Entwurf** (Branch `ui-platform`). Dieses Dokument legt fest, wie fire Fenster, Eingaben und Grafik ohne SDL auf allen Zielen
betreibt, wie Glas und Acryl hineinkommen und in welcher Reihenfolge das gebaut wird.

Inspiration: [Raven](https://github.com/oscar-soirey/Raven) (Apache-2.0) - wir haben uns dort die Aufteilung der Plattformschichten,
das Laden optionaler Bibliotheken zur Laufzeit und die Blur-Pyramide angesehen. **Code wird nicht übernommen**; die Umsetzung ist eigene
und folgt unserer Struktur (die HAL `fire_display_c.h`, Plattformpakete unter `native/platform/`). Raven wird in der About-Box genannt.

## Ausgangslage

* **Nativ (C):** `packages/windows/native/fire_bridge_windows.h` spricht nur mit der HAL `native/platform/std/fire_display_c.h`
  (~25 Funktionen, ein Ereignis-Struct mit 24 Typen). Dahinter steht heute SDL2 oder SDL3 (`fire_display_sdl.h`, `fire_display_sdl3.h`),
  gelinkt beim Bau (`// fire-link: SDL2|SDL3`), auf wasm Emscriptens SDL2. Ohne Bildschirm: `fire_display_none.h`.
* **VM (C#):** eine zweite Implementierung - `WindowsBridge` -> `WindowManager` -> `ConsoleWindow` -> `SdlFramebufferRenderer`
  (SDL3-CS) - und ein C#-Zwilling des Grafik-Pakets (`src/fire.Terminal`, ~6600 Zeilen), der pixelgleich zu `fire_gfx*.h` sein muss.
* **Gezeichnet wird überall in Software** in einen RGBA-Framebuffer; SDL lädt ihn nur als Textur hoch. `ui` zeichnet mit `FillRect`/`DrawRect`,
  ohne Antialiasing, Rundungen, Schatten oder Blur. scene3d hat schon ein Vulkan-Backend (zur Laufzeit geladen) mit Rücklesen in den
  Framebuffer oder eigenem Swapchain über `fire_display_native`.
* SDL bleibt sonst nur für Audio unter macOS und wasm (`fire_audio_sdl.h`). Systemthema, DPI und Drag-out kommen schon ohne SDL aus.
* Öffentliche Nummern stammen von SDL: Tasten- und Scancodes, Modifier-Bits, Maustasten, System-Cursor 0..11 (`UI.Keys`).

## Leitlinien

1. **Eine Implementierung, in C.** Fenster, Eingaben, Präsentation und Grafik gibt es nur einmal. Die VM benutzt dieselben C-Bibliotheken
   wie ein natives Programm (wie die anderen C-Packages: Shared Library über `wrapper.c`). VM und nativ verhalten sich damit zwangsläufig gleich.
2. **Minimale Abhängigkeiten.** Gelinkt wird nur, was das Betriebssystem immer hat (user32/gdi32; Cocoa/QuartzCore; libc).
   Alles andere wird zur Laufzeit geladen (dwmapi, libX11/libXext, libwayland-client, libxkbcommon, Vulkan) - ein Programm startet auch,
   wenn es fehlt, und fällt zurück.
3. **Software ist die Referenz.** Der Software-Renderer bleibt vollständig und läuft auf dem ESP32. Ein GPU-Backend ist eine
   Beschleunigung mit demselben Verhalten; kleine Pixelabweichungen sind erlaubt.
4. **Sparsam von Grund auf.** Der Grundstil ist flach und billig. Glas und Acryl sind eine Schicht darüber, die sich abschalten lässt;
   ohne sie sieht die Oberfläche fertig aus, nicht kaputt.
5. **Die Nummern bleiben.** Tasten, Modifier, Maustasten und Cursor behalten ihre Werte (jedes Backend übersetzt); `UI.Keys` ändert sich nicht.

## Die Plattformschicht

Die HAL `fire_display_c.h` bleibt die Grenze. Neue Backends in `native/platform/std/`:

| Datei | Ziel | Fenster und Eingabe | Framebuffer zeigen | GPU-Oberfläche | Gelinkt / zur Laufzeit |
|---|---|---|---|---|---|
| `fire_display_win32.h` | Windows | `CreateWindowExW`, Per-Monitor-DPI v2, `WM_CHAR`/IME | `SetDIBitsToDevice` / `StretchDIBits` | HWND | user32, gdi32 / dwmapi, shcore |
| `fire_display_x11.h` | Linux, BSD | Xlib, XInput2 für Touch, XIM | MIT-SHM (`XShmPutImage`), sonst `XPutImage` | Xlib-Window | - / libX11, libXext, libXi, libXcursor |
| `fire_display_wayland.h` | Linux | xdg-shell, xdg-decoration, fractional-scale, text-input-v3 | `wl_shm` (doppelt gepuffert) | `wl_surface` | - / libwayland-client, libxkbcommon |
| `fire_display_cocoa.h` | macOS | NSApplication/NSWindow/NSView über die objc-Runtime **aus C** (`objc_msgSend`), NSTextInputClient | `CALayer.contents` = CGImage | CAMetalLayer | Cocoa, QuartzCore (System) |
| `fire_display_web.h` | wasm | ein `<canvas>` (das aktuelle oder ein benanntes), Pointer-/Keyboard-Events | `putImageData` auf eine Sicht des wasm-Speichers | später WebGPU | Emscripten-JS-Bibliothek |
| `fire_display_headless.h` | Tests, Server | Fenster sind nur Größen, Ereignisse werden eingespeist | Pixel bleiben abrufbar | - | - |
| `fire_display_none.h` | ESP32 ohne Bildschirm | wie heute | | | |

* **Linux:** `fire_display_posix.h` wählt zur Laufzeit: Wayland, wenn `WAYLAND_DISPLAY` gesetzt ist und die Bibliothek lädt, sonst X11.
  Die Wayland-Protokolle (xdg-shell, xdg-decoration, ...) liegen als kleine, selbst geschriebene Tabellen vor (keine Abhängigkeit von
  wayland-scanner beim Bau). Ohne serverseitige Dekoration (GNOME) zeichnen wir eine eigene, schlichte Titelleiste.
* **macOS:** Cocoa will den Hauptthread. In spark gehört er Avalonia; das Backend reicht seine Aufrufe deshalb per `dispatch_sync` an die
  Haupt-Queue weiter, statt selbst eine Ereignisschleife zu besitzen. Ein natives Programm ohne Avalonia startet `NSApp` selbst.
* **wasm:** Standard ist das Canvas mit der Id `fire` oder - falls es fehlt - eines, das wir anlegen; `new Window(...)` kann ein
  vorhandenes Canvas per Name bekommen (`Window.Canvas = "#app"`).
* **Ereignisschleife:** `pump(timeout)` und ein thread-sicheres `wake()`, damit Manager-Threads nicht pollen müssen.
* **Neu in der HAL:** Skalierung (DPI) als Ereignis, `fire_display_wake`, `fire_display_setBackdrop(w, art)` -> erreichte Art (siehe unten),
  Texteingabe mit Komposition (IME), rahmenlose Fenster mit eigener Titelleiste (später).
* **Was SDL sonst noch liefert und ersetzt werden muss:**
  * Joysticks: evdev (Linux), XInput + Raw Input (Windows), GameController (macOS), Gamepad-API (Web).
  * Audio unter macOS (AudioQueue) und wasm (WebAudio) - danach ist SDL ganz raus.
  * System-Cursor, Clipboard (Text, Bild, Dateien) und Drop-Ereignisse je Backend; Drag-out (`fire_dragout.h`) gibt es schon ohne SDL.

## Die VM über C

Heute hat die VM eine eigene C#-Kette für Fenster und Grafik. Künftig benutzt sie die C-Bibliotheken von `windows` und `graphics`:

* `windows` und `graphics` werden in der VM wie die übrigen C-Packages als Shared Library gebaut; die `__GRPH*`-Natives kommen von dort.
* Damit entfallen `fire.Terminal.Sdl` (SDL3-CS), `SdlFramebufferRenderer`, die SDL-Bibliothek im Payload und - für Programme - der
  C#-Zwilling in `fire.Terminal`. Die Tests "VM == nativ" für Pixel werden zur Selbstverständlichkeit.
* **Was in C# bleibt oder umzieht:**
  * `UiPreview` (UI-Designer) und `LiveSession` (Circuit-Editor) zeichnen heute mit dem C#-Renderer in Avalonia. Sie bekommen den Framebuffer
    der C-Bibliothek (ein Zeiger auf die Pixel) und zeigen ihn in Avalonia; gezeichnet wird in C.
  * Die VM-Tests mit Fake-Renderern laufen auf `fire_display_headless.h` (Ereignisse einspeisen, Pixel abfragen).
* **Aufwand:** etwa so groß wie die Portierung eines großen Packages nach C. Der eigentliche Code existiert in C schon; die Arbeit steckt in der
  Umstellung der VM-Seite (Ressourcen-IDs, Ereignisse, Threads), in `UiPreview`/`LiveSession` und den Tests.
* **Risiken:**
  * Fein granulare Aufrufe (`SetPixel` in einer Schleife) kosten über die Bibliotheksgrenze mehr als heute in C#; wird gemessen.
  * Fenster laufen in spark im selben Prozess wie Avalonia (siehe macOS oben).

Lohnt sich: Jeder Effekt (Blur, Schatten, Rundungen, GPU) wird nur einmal geschrieben. Deshalb kommt dieser Schritt **vor** den Effekten.

## Zeichnen: Helfer und Effekte (Software)

Neu in `packages/graphics/native/` (C, für VM und nativ):

* **Schnelle Blöcke:** Kopieren zeilenweise mit `memcpy`; Alpha-Blending ganzzahlig, zwei Kanäle je 32-Bit-Wort (SWAR, auf x86 SSE2 / ARM NEON,
  wo vorhanden); deckende Quellen werden erkannt und nur kopiert.
* **Antialiasing** für Linien, Kreise und **abgerundete Rechtecke** (Abdeckung analytisch je Pixel, nur an den Kanten).
* **Schatten:** die Maske eines abgerundeten Rechtecks auf 1/4 oder 1/8 Auflösung weichgezeichnet, bilinear hochskaliert, im Cache nach
  (Größe, Radius, Weichheit). Klein: Popups 6-8 px, Fenster 12 px, geringe Deckkraft.
* **Blur:** Hintergrund auf 1/2 -> 1/4 verkleinern, Box-Blur mit gleitender Summe (Kosten unabhängig vom Radius), zwei Durchgänge, bilinear zurück.
  Ergebnis im Cache, bis das Dirty-Rechteck den Bereich berührt.
* **Dirty-Rechtecke:** eine Liste je Framebuffer statt eines Flags; Präsentation und Blur arbeiten nur dort.

## Materialien und Stil

* **Glas** (an Aero/macOS angelehnt, nicht an Raven): Blur mit kleinem Radius (6-8 px bei voller Auflösung), Sättigung leicht angehoben (~1,2),
  dezente Tönung (10-20 % der Fläche), eine 1-px-Haarlinie oben/am Rand, kein Leuchtrand, keine Lichtbrechung, kein Farbversatz.
* **Acryl:** wie Glas, stärkere Tönung (40-60 %) und ein festes, feines Rauschen (eine kleine Kachel, keine Berechnung pro Bild).
* **Schalter** (in `UI`, pro Root und global):
  * `Effects.Glass` - aus: kein Blur; Glas wird zu halbtransparenter Tönung, Acryl zur deckenderen Tönung.
  * `Effects.Transparency` - aus: keine Halbtransparenz; Flächen werden deckend (die Farbe, die die Tönung über dem Grundton ergäbe).
  * Standard folgt dem System (Windows "Transparenzeffekte", macOS "Transparenz reduzieren", GNOME/KDE wo lesbar) und der Leistung des Ziels
    (ESP32: beides aus).
* **Fenster-Hintergrund** (`fire_display_setBackdrop`): Windows 11 Mica/Acryl (`DWMWA_SYSTEMBACKDROP_TYPE`), Windows 10 Acryl (Accent-Policy),
  macOS `NSVisualEffectView`, KDE `_KDE_NET_WM_BLUR_BEHIND_REGION` / Wayland `ext-background-effect`. Die Plattform meldet, was sie erreicht hat;
  ohne Unterstützung bleibt das Fenster deckend (hinter das Fenster können wir selbst nicht sehen).
* **Stil allgemein:** einheitliche Eckenradien (4 px Steuerelemente, 8 px Popups/Fenster), 1-px-Ränder in niedriger Deckkraft, ruhigere
  Hover-/Pressed-Töne, Fokus als Akzent-Ring, Abstände aus wenigen Stufen (4/8/12/16). Metriken wandern aus den Steuerelementen in das Thema.

## GPU-2D (später)

* Der Renderer sammelt Zeichenbefehle (Rechteck, abgerundetes Rechteck, Linie, Glyphenfolge, Bild, Clip, Backdrop) in einer Liste.
  Das Software-Backend führt sie direkt aus (die Referenz); das Vulkan-Backend (Loader gemeinsam mit scene3d) auf der GPU.
* **Framebuffer nur bei Bedarf:** Wer Pixel lesen will (`GetPixel`, Speichern, Tests), bekommt sie von der GPU zurück - aber nur, wenn
  seit dem letzten Rücklesen gezeichnet wurde (Dirty-Flag).
* Gleiches Verhalten; Tests vergleichen GPU gegen Software mit Toleranz.

## Reihenfolge

| Teil | Inhalt | Ergebnis |
|---|---|---|
| 1 | Plattformschicht: headless, Win32, X11, Wayland, Cocoa, Web; HAL-Erweiterungen; Joysticks; Audio macOS/wasm ohne SDL. Das SDL-Backend bleibt zum Vergleich wählbar. | native Programme ohne SDL |
| 2 | VM über C: `windows`/`graphics` als C-Bibliotheken in der VM, `UiPreview`/`LiveSession`, Tests auf headless; SDL3-CS und SDL-Backends entfernen | SDL ist raus, eine Implementierung |
| 3 | Helfer und Effekte (Software), Materialien, Schalter, Fenster-Hintergrund, Stil-Überarbeitung von `ui`, Raven in der About-Box | Glas/Acryl, modernerer Stil |
| 4 | GPU-2D mit Vulkan, Rücklesen nur bei Bedarf | Beschleunigung |
