# Eigene Plattformschicht statt SDL, Effekte und ein modernerer Stil

Status: **Teil 1 umgesetzt**, **Teil 2 zum großen Teil** (Branch `ui-platform`, siehe [Stand Teil 1](#stand-teil-1) und [Stand Teil 2](#stand-teil-2)); Teile 3-4 sind Entwurf. Dieses Dokument legt fest, wie fire Fenster, Eingaben und Grafik ohne SDL auf allen Zielen
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
* **Neu in der HAL** (wir erweitern die bestehende API, keine zweite): Skalierung (DPI) als Ereignis, `fire_display_wake`,
  `fire_display_setBackdrop(w, art)` -> erreichte Art (siehe unten), rahmenlose Fenster mit eigener Titelleiste (später).
* **IME ist Pflicht** (Teil 1, alle Desktop-Backends und Web): Komposition als eigenes Ereignis (`FIRE_EV_TEXT_EDITING`: Text, Cursor,
  Auswahl), fertiger Text wie bisher als `FIRE_EV_TEXT_INPUT`; das Programm meldet die Position des Eingabecursors
  (`fire_display_setTextInputRect`), damit das Kandidatenfenster dort erscheint. Win32: IMM32 (`WM_IME_COMPOSITION`), X11: XIM/XIC mit
  Preedit-Callbacks, Wayland: `text-input-v3`, macOS: `NSTextInputClient`, Web: `compositionstart/update/end`.
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
  (Größe, Radius, Weichheit). Nur ganz fein und kontrastarm: Popups 4-6 px, Fenster 8-10 px, Deckkraft 8-15 % (dunkles Thema etwas mehr),
  kaum Versatz nach unten.
* **Blur:** Hintergrund auf 1/2 -> 1/4 verkleinern, Box-Blur mit gleitender Summe (Kosten unabhängig vom Radius), zwei Durchgänge, bilinear zurück.
  Ergebnis im Cache, bis das Dirty-Rechteck den Bereich berührt.
* **Dirty-Rechtecke:** eine Liste je Framebuffer statt eines Flags; Präsentation und Blur arbeiten nur dort.

## Materialien und Stil

* **Glas** (an Aero/macOS angelehnt, nicht an Raven): Blur mit kleinem Radius (6-8 px bei voller Auflösung), Sättigung leicht angehoben (~1,2),
  dezente Tönung (10-20 % der Fläche), eine 1-px-Haarlinie oben/am Rand, kein Leuchtrand, kein Farbversatz. Lichtbrechung höchstens als
  feiner, leichter Rand - praktisch wie die Haarlinie: 1-2 px, in denen der Hintergrund um 1 px nach innen versetzt und etwas aufgehellt wird.
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
| 1 | Plattformschicht: headless, Win32, X11, Wayland (eigene Titelleiste ohne Server-Deko), Cocoa, Web; HAL-Erweiterungen inkl. IME; Joysticks; Audio macOS/wasm ohne SDL. Das SDL-Backend bleibt zum Vergleich wählbar. | native Programme ohne SDL |
| 2 | VM über C: `windows`/`graphics` als C-Bibliotheken in der VM, `UiPreview`/`LiveSession`, Tests auf headless; SDL3-CS und SDL-Backends entfernen | SDL ist raus, eine Implementierung |
| 3 | Helfer und Effekte (Software), Materialien, Schalter, Fenster-Hintergrund, Stil-Überarbeitung von `ui`, Raven in der About-Box | Glas/Acryl, modernerer Stil |
| 4 | GPU-2D mit Vulkan, Rücklesen nur bei Bedarf | Beschleunigung |

## Stand Teil 1

Umgesetzt - native Programme brauchen kein SDL mehr (SDL bleibt mit `"sdl": 2|3` oder `FIRE_SDL` wählbar):

| Teil | Datei | Geprüft |
|---|---|---|
| HAL-Erweiterungen | `fire_display_c.h` (TextEditing, Scale, Enter/Leave, Focus, Backdrop), `fire_display_keys.h`, `fire_display_common.h` | alle Backends |
| ohne Bildschirm | `fire_display_headless.h` | Testsuite (Fenster-Fälle, UI nativ == VM) |
| X11 | `fire_display_x11.h` | Xvfb: Fenster-Fälle, Pixel, Tasten/Maus/Resize, Zwischenablage (auch zwischen zwei Programmen, INCR), Drag-out und XDND-Drop zwischen zwei fire-Programmen; gcc, clang, tcc |
| Wayland | `fire_display_wayland.h` | weston headless: Fenster-Fälle, eigene Titelleiste, keine Lecks (ASan) |
| Win32 | `fire_display_win32.h` | MinGW + Wine: echtes Fenster (Tasten, Maus, Text), Zwischenablage (Text, Bild, Dateien), Drops |
| Cocoa | `fire_display_cocoa.h` | nur Syntax (gcc, clang) - **auf einem Mac noch nicht gelaufen** |
| Web | `fire_display_web.h` | Chromium headless: Pixel, Klick, Tasten, Text, IME-Komposition, Rad, Datei-Drop |
| Joysticks | `fire_display_joystick.h` (Linux js, XInput, GameController, Gamepad-API) | nur übersetzt (kein Gerät hier) |
| Audio ohne SDL | `fire_audio_coreaudio.h` (macOS), `fire_audio_web.h` (Browser) | Web: Chromium spielt ab; macOS: nur Syntax |

Offen in Teil 1:

* macOS auf echter Hardware prüfen (Cocoa, AudioQueue, GameController); Ziehen aus dem Fenster gibt es dort noch nicht (0).
* Wayland: Drag-out (`fire_dragout.h`) hört auf einer Verbindung - mit mehreren Fenstern auf verschiedenen Threads nur für das erste.
* Win32: während der Nutzer das Fenster zieht oder seine Größe ändert (modale Schleife von Windows), zeichnet das Programm nicht nach.
* Mit dem Emscripten 3.1.6 von Ubuntu laufen die wasm-Prüfungen der Suite nicht (schon ein Programm ohne Fenster startet nicht) - mit einem aktuellen emsdk prüfen.
* Die VM benutzte noch SDL3-CS - in Teil 2 entfernt.

## Stand Teil 2

Die VM benutzt für `graphics` und `windows` die C-Implementierung (Standard; `FIRE_VM_GRAPHICS=cs` nimmt die C#-Implementierung):

* **Paket-ABI erweitert** (`native/abi/fire_pkg_abi.h`, abwärtskompatibel): ein Lambda geht als Handle hinüber (`FIRE_LAMBDA`); die Bibliothek ruft es mit
  `fire_host.call_lambda` zurück - auf dem Thread der VM sofort (verschachtelt, mit den echten Globals), von einem anderen Thread am nächsten Safe Point.
  `request_service` lässt die VM `fire_pkg_service` der Bibliothek auf ihrem Thread aufrufen (die Zyklen der Fenster-Manager), `hold` hält das Programmende
  auf. Die Sperre der Aufrufe ist auf ihrem Thread wiedereintrittsfähig (ein Callback darf wieder Natives derselben Bibliothek rufen).
* **Eine Bibliothek für beide** (`PackageLibrary.EnsureGraphics`, `libfire_gfx`): die Bridges von graphics und windows und die eigene Anzeige, die Natives aus
  den Tabellen des Generators, Host-Funktionen in `native/abi/fire_gfx_host.h` (Pixel eines Framebuffers, Presenter für scene3d, Zurücksetzen am Programmende).
  Die Fenster-Manager haben ihre Threads auch in der Bibliothek (die Thread-Dienste der Runtime kommen im Bibliotheksmodus vom Host).
* **Gepackte Programme** (`forge build`) tragen die Bibliothek in ihrer Payload; SDL3 und die C#-Grafik kommen nicht mehr mit (ein Fensterprogramm: ~0,8 MB).
* Was der Host an Umgebungsvariablen für das Programm setzt (`FIRE_*`, `DISPLAY`, ...), bekommt auch die C-Laufzeit des Prozesses.

* **Editor-Werkzeuge auf der C-Schicht:** der UI-Designer (`UiPreview`) und der Circuit-Editor (`LiveSession`) lassen ihre Programme auf einer **Kopie** der
  Bibliothek laufen (`GraphicsLibrary.Instance`, ein Pool: eine geladene Bibliothek hat einen Zustand je Prozess - ihre Framebuffer, Fenster, die Anzeige -, so
  bleibt ein Programm daneben auf dem Desktop). Die Kopie bekommt eine **Anzeige, die der Host steuert** (`fire_gfx_host_display`, `FireDisplayHost` in
  `fire_display_headless.h`): die Ereignisse holt das Fenster beim Pumpen vom Host, Bilder und Mauszeiger gehen an den Host (`IHostDisplay`); ohne Host ein
  Fenster ohne Bildschirm (der Designer liest das Bild aus dem Framebuffer).
* **Je Programm statt je Prozess:** der `fire_host` einer Bibliothek mit eigenen Diensten schickt `request_service` an die VM des Programms, das *diese*
  Bibliothek benutzt; am Programmende werden nur die Bibliotheken des Programms zurückgesetzt (`PackageHost.Begin(..., libraries)`), Lambdas laufender Programme
  bleiben gültig. **Holds gehören dem Programm** (`VM.AddHostHold` seiner Haupt-VM; die C-Bibliothek meldet sie über ihren eigenen `fire_host.hold`
  an die VM, die sie benutzt): ein Fenster der Live-Ansicht hält kein Programm daneben auf, und nach dem Ende eines Programms hält nichts mehr.
* **SDL3-CS ist entfernt** (`fire.Terminal.Sdl`, `SdlFramebufferRenderer`, `SdlClipboard`): die Fenster des Desktops zeigt in der VM nur noch die C-Bibliothek. Die
  C#-Implementierung (`fire.Terminal`, `fire.Terminal.Windows`) bleibt als Rückfall, wo die Bibliothek nicht gebaut werden kann (kein C-Compiler) oder
  `FIRE_VM_GRAPHICS=cs` sie wählt: Grafik geht, Fenster nur mit einem Renderer des Hosts (die Tests, der Editor über `HostRenderer`); sonst meldet das Fenster,
  dass es keine Anzeige gibt.

Noch offen:

* Die Tests mit gefälschten Renderern (`FakeRenderer`, `ScriptedWindow` u. a.) und die Unit-Tests der C#-Grafik prüfen weiter die C#-Implementierung.
* Der C#-Zwilling der Grafik für Programme fällt weg, sobald die Bibliothek vorgebaut mitgeliefert wird (dann braucht die VM keinen C-Compiler mehr dafür).

## Stand Teil 3

* **Zeichnen in C** (`packages/graphics/native/fire_gfx_fx.h`): abgerundete Rechtecke mit Antialiasing (Füllung und Umriss), feine Schatten (Maske auf 1/2 bzw. 1/4 der Größe,
  weichgezeichnet, im Cache, nur der Ring um das Rechteck), Blur (verkleinern, Box-Blur mit gleitender Summe zweimal, bilinear zurück), Materialien Glas/Acryl/Tönung mit Haarlinie
  und feinem Lichtrand, Schalter für Blur und Transparenz. Halbtransparente Spannen mischen zwei Kanäle je Wort (bitgleich zu `fire_gfx_mix`). Am `Renderer`: `FillRoundRect`,
  `DrawRoundRect`, `DrawShadow`, `Blur`, `DrawMaterial` (`MaterialKind`, `MaterialOptions`); die C#-Rückfall-Implementierung zeichnet die einfachen Formen.
* **Stil von `ui`:** Radien 4/8 px, weichere Ränder, Fokus als 2-px-Akzentrand, Auswahl als abgerundete Pille, Scrollbalken als Pillen, Menüs auf Acryl mit feinem Schatten (pro
  Popup im Cache, neu nur wenn der Hintergrund samt Rand neu gezeichnet wurde), Schatten für alle schwebenden Elemente.
* **Schalter:** `UI.Effects.Glass/Transparency`, `root.Glass/Transparency`; Standard nach `SystemTheme.transparency` (neu: Windows `EnableTransparency`, macOS
  `reduceTransparency`, Browser `prefers-reduced-transparency`; Board: aus).
* **Fenster-Hintergrund:** `root.WindowBackdrop` fordert ihn an; was der Desktop gibt, macht die Grundfläche durchscheinend.
* **Nur Geändertes präsentieren:** `Window.PresentArea(x, y, w, h)` (optional in der HAL: `fire_display_presentArea`, `FIRE_DISPLAY_PRESENT_AREA`); X11 rechnet nur den
  Bereich um und gibt nur ihn an den X-Server (`XShmPutImage`), Win32 rechnet nur ihn um. Mehrere Bereiche vor einem Bild werden vereinigt, im Thread des Managers auch über
  mehrere veröffentlichte Bilder; ein Bild anderer Größe oder ohne Bereich geht ganz. `UI.Root` meldet sein Dirty-Rechteck selbst (nach vollem Neuzeichnen alles, im Leerlauf
  nichts); `root.PartialPresent = false` für Programme, die zusätzlich selbst in den Framebuffer zeichnen.
* **Tooltips:** `Element.ToolTip`, nach `root.toolTipDelay` unter dem Zeiger, ohne Eingaben.
* **Holds** gehören dem Programm (siehe Teil 2), Raven steht in der About-Box.

Noch offen:

* macOS präsentiert noch das ganze Bild (ungetestet ohne Mac); das Web ist nur gebaut, nicht im Browser geprüft (das Emscripten im Container ist zu alt). Blur-Cache auch für eigene Programme.

## Stand Teil 4

Stufen (alle umgesetzt): **4.1** Grundgerüst, **4.2** Text über einen Glyphen-Atlas und Blits über Texturen, **4.3** Schatten, Blur und Materialien auf der GPU, **4.4** Präsentieren
ohne Zurücklesen (direkt ins Fenster, wie scene3d DirectDrawing).

4.1 (`packages/graphics/native/fire_gfx_gpu.h`):

* **Vulkan zur Laufzeit** (`dlopen`/`LoadLibrary`, nichts gelinkt); die Deklarationen erzeugt `tools/make_gfx_vk.py` aus `vulkan_core.h` (`fire_gfx_vk.h`), die Shader
  `shaders/build.sh` (`fire_gfx_shaders.h`, SPIR-V). Ein Gerät (GPU vor CPU-Gerät; `FIRE_GFX_GPU=cpu` nimmt auch lavapipe), ein Bild je beschleunigtem Framebuffer.
* **Zwei Kopien, ein Abgleich:** `fire_gfx_fbCpu(fb, writes)` steht vor jedem Lesen und Schreiben der CPU (Surface, Blit, GetPixel, Bytes, PNG, Fenster). Hat die GPU seit dem
  letzten Abgleich gezeichnet, holt er die Pixel zurück (das Dirty-Flag); schreibt die CPU, lädt der nächste GPU-Befehl ihr Bild vorher hoch. Ohne Zwilling kostet der Haken
  einen Vergleich.
* **Auf der GPU:** `FillRect`, `FillRoundRect`, `DrawRoundRect`, `DrawRect` (Stift 1 px) - ein Instanz-Quad je Form, im Fragment-Shader dieselbe Abstandsfunktion wie
  `fire_gfx_fx.h` (Abdeckung `clamp(0.5 - d)`), mit Blending wie `fire_gfx_mix` oder kopierend (ohne Blending). Die Befehle sammeln sich bis zum nächsten Abgleich.
* **Schalter:** `Framebuffer.Accelerated` (`Framebuffer.GpuError()` sagt, warum nicht), `FIRE_GFX_GPU=1|cpu` beschleunigt jeden RGBA-Framebuffer. Auf Boards, im Browser und
  ohne Vulkan bleibt alles Software; die C#-Rückfall-Implementierung hat keine GPU.
* **Tests:** GPU gegen Software auf lavapipe (höchstens 2 Farbstufen Abweichung, gemessen 1), und die ganze UI-Gruppe läuft mit `FIRE_GFX_GPU=cpu` grün.

4.2 (Text und Blits):

* **Glyphen-Atlas:** eine Textur mit einem Byte Abdeckung je Pixel (2048×2048, in Regalen gefüllt, Schlüssel je Schrift, Größe und Glyphe); TrueType-Glyphen bringen ihr
  Abdeckungsbild aus dem Cache der Schrift mit, Bitmap-Glyphen (Konsolenschrift, 8x8, 8x14) werden zu 0/255. Geändertes geht beim nächsten Lauf hoch; ist er voll, wird
  erst gezeichnet, was ihn noch braucht, dann fängt er neu an. Hintergründe von Text sind Rechtecke.
* **Blits:** eine RGBA-Quelle bekommt selbst einen Zwilling; beim Einreihen des Blits wird sie hochgeladen bzw. ihr eigenes Zeichnen ausgeführt - der Blit zeichnet, was sie
  da war. Der Shader wählt das Quellpixel wie `fire_gfx_blit` (nächster Nachbar, ganzzahlig `s + (2f+1)·sw/(2|dw|)`, gespiegelt bei negativer Breite/Höhe); Kopie,
  transparent und Blend. Palettenquellen und ein Framebuffer in sich selbst bleiben Software, ebenso die Konsolenausgabe (`Print`, Scrollen).
* **Reihenfolge über mehrere Framebuffer:** Zwillinge mit offener Arbeit stehen in einer Liste in der Reihenfolge ihres ersten Befehls; ein Lauf führt erst die vor ihm
  aus (sie lesen ihn womöglich noch im alten Zustand), die nach ihm warten (sie können neuere Befehle tragen).
* **Tests:** Formen, Text (drei Schriftarten, Hintergründe, Clip) und Blits (alle Modi, skaliert, gespiegelt, Quelle danach geändert) gegen Software: höchstens 1 Stufe;
  die ganze UI-Gruppe läuft auch komplett auf der GPU.

4.3 (Schatten, Blur, Materialien):

* **Schatten:** die weichgezeichnete Maske macht weiter die Software einmal (`fire_gfx_fxShadowMask`, mit ihrem Cache); sie kommt mit eigenem Schlüssel in den Atlas
  (bis 1024 hoch). Ein Bild-Quad mit Modus 4 streckt sie bilinear in 16.16 wie `fire_gfx_fxSample`, lässt das Rechteck selbst aus und färbt mit der Deckkraft - eine Maske,
  die nicht in den Atlas passt, bleibt Software.
* **Blur und Materialien:** dieselben Schritte wie `fire_gfx_fxBlurMaterial`, ganzzahlig im Shader (`shaders/gfx_effect.*`): verkleinern (Mittel aus f×f), zweimal Box
  längs x, zweimal längs y (Ping-Pong zwischen zwei Hilfsbildern, die mitwachsen), zurück bilinear mit Sättigung, Tönung, Korn und Lichtrand, Abdeckung der Ecken als
  Alpha. Ein Effekt ist ein eigener Batch-Typ: der Render-Pass endet vor ihm, er liest, was bis dahin gezeichnet ist, und der Pass danach zeichnet darüber weiter.
  `DrawMaterial` geht dieselben Zweige wie in Software (deckend, Tönung ohne Blur, Material) samt Haarlinie als Formen.
* **Tests:** Schatten (auch negativ versetzt), Blur (Radius 3 bis 30), Glas, Acryl, Tönung, mit Clip und ohne Blending, nach einer Größenänderung - gegen Software
  höchstens 1 Stufe.

4.4 (direkt ins Fenster):

* **Ohne Zurücklesen:** zeigt ein Fenster einen beschleunigten Framebuffer, kopiert das Veröffentlichen eines Frames (`Tick`, `Present`) das Bild des Zwillings auf der
  GPU in das Bild seines Presenters; der Fenster-Thread blittet es über eine Swapchain ins Fenster (`VK_KHR_swapchain` auf einer Fläche für Win32, X11, Wayland oder
  einen CAMetalLayer) - derselbe Presenter-Weg der Fensterbrücke wie scene3d DirectDrawing (`fire_present_fn`), ohne Overlay. Die ersten Frames gehen zusätzlich als
  Pixel mit (das Fenster nimmt den Presenter vielleicht nicht, z. B. die Host-Anzeige des Editors); sobald ein Fenster einen gezeigt hat, bleiben die Frames auf der GPU.
  Ein Presenter, der scheitert, gibt das Fenster zurück, die Frames werden wieder zurückgelesen.
* **Transparenter Hintergrund (Backdrop):** der Frame wird beim Kopieren auf der GPU vormultipliziert (ein Pass des Effekt-Shaders, Modus 4 - dieselbe Rechnung
  `c·a/255` wie die Anzeigen in Software), die Swapchain mischt mit `VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR`; sie wird beim Wechsel neu gemacht. Eine Fläche, die so
  nicht mischen kann, bekommt diese Frames als Pixel (Windows: siehe DirectComposition). X11: ein
  32-Bit-Fenster ohne `PRE_MULTIPLIED` (z. B. NVIDIA) nimmt `INHERIT` - das vormultiplizierte Bild geht so ins Visual, der X-Kompositor mischt es wie das der
  Anzeige; ein 24-Bit-Fenster kann nicht durchscheinen (ohne Kompositor gibt die Anzeige ohnehin keinen transparenten Backdrop). Macht die Anzeige ihr Fenster dafür neu (X11: anderes Visual), lässt
  der Presenter das alte vorher los.
* **Grenzen:** zeichnet die Wayland-Anzeige ihre eigene Titelleiste (der Kompositor zeichnet keine: GNOME, weston), gibt sie das Fenster keinem Presenter - die Leiste
  liegt im Puffer der Fläche (`FIRE_WAYLAND_CSD=0` lässt sie weg: Tests); das gilt auch für scene3d DirectDrawing. Zeigen zwei Fenster denselben Framebuffer, bekommt
  das zweite die Pixel. Der Presenter zeigt einen neuen Frame sofort, den alten nur ab und zu wieder (nicht in jedem Zyklus). VSync übernimmt die Swapchain (FIFO) -
  lavapipe wartet dabei nicht auf den Bildschirm.
* **Windows: DirectComposition als Brücke** (`native/platform/std/fire_display_dcomp.h`): ist sie da (Windows 8 und später; `FIRE_DCOMP=0` schaltet sie ab),
  bekommt jedes Fenster beim Anlegen `WS_EX_NOREDIRECTIONBITMAP` und zeigt *alle* Frames über genau ein Visual mit einer Composition-Swapchain (R8G8B8A8; Alpha
  ignoriert bei deckendem, vormultipliziert bei transparentem Hintergrund - DWM mischt, auch Mica und Acryl; Blur-Behind ist nicht mehr nötig). Die UI merkt
  davon nichts: die Pixel der CPU gehen nur im geänderten Bereich in das Bild des Fensters (eine D3D11-Textur, `UpdateSubresource`), das pro Frame in den
  Back Buffer kopiert und präsentiert wird (VSync über `Present`). Der GPU-Presenter bekommt das Fenster als `FIRE_NATIVE_DCOMP` (`fire_dcomp_bridge` in
  `fire_pkg_abi.h`): Vulkan importiert die geteilte Textur (`VK_KHR_external_memory_win32`, Handle der Art D3D11_TEXTURE_KMT, gleicher Adapter über die LUID),
  kopiert den Frame hinein und lässt die Anzeige präsentieren - nichts kommt zurück, eine Vulkan-Swapchain gibt es unter Windows dann nicht. Rückfälle: ohne
  DirectComposition (Windows 7, Wine) oder wenn sie für ein Fenster scheitert, wird es ohne den Stil (neu) angelegt - GDI wie bisher; geht das Device verloren,
  ist sie für den Prozess aus und die Fenster werden neu angelegt; ohne Interop gehen die GPU-Frames als Pixel (nur das Gezeichnete zurückgelesen). scene3d
  DirectDrawing kennt `FIRE_NATIVE_DCOMP` noch nicht und liest dort zurück. COM in C über Vtable-Plätze; `tools/check_dcomp.sh` (und ein Test unter Wine)
  vergleicht Plätze, GUIDs und Strukturen mit dem SDK von MinGW. `Window.PresentPath()` sagt, welcher Weg es war; `docs/checks/windows_present.fire` ist die
  Prüfung für ein echtes Windows (unter Wine ist DirectComposition ein Stub: dort ist nur der Rückfall getestet).
* **Zurücklesen nur des Gezeichneten:** jeder Zwilling führt das Rechteck mit, das die GPU seit dem letzten Gleichstand gezeichnet hat (Formen, Bilder, Glyphen,
  Schatten, Effekte); ein Abgleich holt nur dieses zurück - der Rest der CPU-Pixel stimmt noch. Das macht jeden Pixelweg billig (Windows mit Transparenz, `GetPixel`,
  PNG, die Host-Anzeige), wenn sich wenig ändert.
* **Ein Schloss:** Programm-Thread (Zeichnen, Kopieren) und Fenster-Threads (Präsentieren) teilen sich die eine Queue; jeder Presenter hat seinen eigenen Command-Pool.
* **Tests:** unter Xvfb mit lavapipe: das Bild im Fenster (Bildschirmfoto) stimmt, und nach den ersten Frames liest nichts mehr zurück (`Framebuffer.GpuReadbacks()`);
  auf Wayland (weston) ein transparentes Fenster: dasselbe gemischte Bild wie in Software, 0 Readbacks.

