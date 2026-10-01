using fire.Bytecode;
using fire.Runtime;
using fire.Terminal;
using fire.Terminal.Event;
using fire.Terminal.Windows;
using fire.Values;
using System.Collections.Generic;
using System.Net.WebSockets;

namespace fire.Terminal.Bridge
{
    /// <summary>
    /// Die Brücke zwischen fire und der Grafik-API (siehe docs/
    /// CONSOLE.md): registriert FramebufferManager/ConsoleManager/
    /// WindowManager als native Funktionen (über NativeRegistry.
    /// RegisterGroup, jeweils mit eigenem Namens-Präfix) und liefert dazu
    /// passenden fire-Quelltext (<see cref="PreludeSource"/>), der
    /// diese nativen Funktionen hinter drei gewöhnlichen Klassen
    /// (Framebuffer/Console/Window) versteckt - Skript-Code sieht nie eine
    /// rohe ID, nur normale Objekte mit normalen Methoden.
    ///
    /// Bewusst "grob" gehalten (siehe Anfrage) - nicht jede Manager-Methode
    /// ist abgebildet, nur eine repräsentative Auswahl (Erzeugen/Zerstören
    /// plus die gängigsten Operationen je Ressourcenart). Weitere Methoden
    /// lassen sich nach demselben Muster ergänzen: native Funktion in der
    /// passenden Build*Functions-Methode registrieren, passende fire-
    /// Methode in PreludeSource hinzufügen, die `this.id` automatisch
    /// mitgibt.
    /// </summary>
    public static class GraphicsBridge
    {
        public const string FramebufferPrefix = "__GRPHFb";
        public const string ConsolePrefix = "__GRPHCon";
        public const string WindowPrefix = "__GRPHWin";

        /// <summary>Ungültige/fehlgeschlagene Erzeugung - IdManager vergibt
        /// echte IDs immer ab 1 aufwärts (siehe dort), -1 ist deshalb als
        /// Sentinel sicher von jeder echten ID unterscheidbar. Die
        /// Create-Wrapper unten fangen JEDE Exception aus dem jeweiligen
        /// Manager ab und liefern diesen Sentinel statt die Exception roh
        /// durch die VM durchschlagen zu lassen - PreludeSource prüft
        /// darauf und wirft dafür eine echte, per `try`/`catch` fangbare
        /// HandleUnavailableException (siehe dort).</summary>
        public const int InvalidHandle = -1;

        public static void RegisterAll(
            NativeRegistry natives, FramebufferManager framebuffers, ConsoleManager consoles, WindowManager windows)
        {
            natives.RegisterGroup(FramebufferPrefix, BuildFramebufferFunctions(framebuffers));
            natives.RegisterGroup(ConsolePrefix, BuildConsoleFunctions(consoles));
            natives.RegisterGroup(WindowPrefix, BuildWindowFunctions(windows));
        }

        public static void RegisterStubs(
                    NativeRegistry natives)
        {
            natives.RegisterGroup(FramebufferPrefix, BuildFramebufferFunctionStubs());
            natives.RegisterGroup(ConsolePrefix, BuildConsoleFunctionStubs());
            natives.RegisterGroup(WindowPrefix, BuildWindowFunctionStubs());
        }

        private static Dictionary<string, NativeFunction> BuildFramebufferFunctionStubs()
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args => Value.MakeUndefined() /*STUB*/,
                ["Destroy"] = args => Value.MakeUndefined() /*STUB*/,
                ["Width"] = args => Value.MakeUndefined() /*STUB*/,
                ["Height"] = args => Value.MakeUndefined() /*STUB*/,
                ["ReadByte"] = args => Value.MakeUndefined() /*STUB*/,
                ["WriteByte"] = args => Value.MakeUndefined() /*STUB*/,
            };
        }

        private static Dictionary<string, NativeFunction> BuildFramebufferFunctions(FramebufferManager mgr)
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args =>
                {
                    try { return Value.MakeInt(mgr.CreateFramebuffer((int)args[0].AsInt(), (int)args[1].AsInt())); }
                    catch { return Value.MakeInt(InvalidHandle); }
                },
                ["Destroy"] = args => Value.MakeBool(mgr.DestroyFramebuffer((int)args[0].AsInt())),
                ["Width"] = args => Value.MakeInt(mgr.GetWidth((int)args[0].AsInt())),
                ["Height"] = args => Value.MakeInt(mgr.GetHeight((int)args[0].AsInt())),
                ["ReadByte"] = args => Value.MakeInt(mgr.ReadByte((int)args[0].AsInt(), (int)args[1].AsInt())),
                ["WriteByte"] = args =>
                {
                    mgr.WriteByte((int)args[0].AsInt(), (int)args[1].AsInt(), (byte)args[2].AsInt());
                    return Value.MakeUndefined();
                },
            };
        }

        private static Dictionary<string, NativeFunction> BuildConsoleFunctionStubs()
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args => Value.MakeUndefined() /*STUB*/,
                ["Destroy"] = args => Value.MakeUndefined() /*STUB*/,
                ["Print"] = args => Value.MakeUndefined() /*STUB*/,
                ["Locate"] = args => Value.MakeUndefined() /*STUB*/,
                ["Clear"] = args => Value.MakeUndefined() /*STUB*/,
                ["SetColor"] = args => Value.MakeUndefined() /*STUB*/,
                ["SetPixel"] = args => Value.MakeUndefined() /*STUB*/,
                ["GetPixel"] = args => Value.MakeUndefined() /*STUB*/,
                ["FillRect"] = args => Value.MakeUndefined() /*STUB*/,
                ["DrawRect"] = args => Value.MakeUndefined() /*STUB*/,
                ["DrawLine"] = args => Value.MakeUndefined() /*STUB*/,
                ["DrawText"] = args => Value.MakeUndefined() /*STUB*/,
                ["CellWidth"] = args => Value.MakeUndefined() /*STUB*/,
                ["CellHeight"] = args => Value.MakeUndefined() /*STUB*/,
            };
        }

        private static Dictionary<string, NativeFunction> BuildConsoleFunctions(ConsoleManager mgr)
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args =>
                {
                    try { return Value.MakeInt(mgr.CreateConsole((int)args[0].AsInt())); }
                    catch { return Value.MakeInt(InvalidHandle); }
                },
                ["Destroy"] = args => Value.MakeBool(mgr.DestroyConsole((int)args[0].AsInt())),
                ["Print"] = args =>
                {
                    mgr.Print((int)args[0].AsInt(), args[1].AsString());
                    return Value.MakeUndefined();
                },
                ["Locate"] = args =>
                {
                    mgr.Locate((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt());
                    return Value.MakeUndefined();
                },
                ["Clear"] = args =>
                {
                    mgr.Clear((int)args[0].AsInt());
                    return Value.MakeUndefined();
                },
                ["SetColor"] = args =>
                {
                    mgr.SetColor((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt());
                    return Value.MakeUndefined();
                },
                ["SetPixel"] = args =>
                {
                    mgr.SetPixel((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt(), (int)args[3].AsInt());
                    return Value.MakeUndefined();
                },
                ["GetPixel"] = args => Value.MakeInt(mgr.GetPixel((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt())),
                // Die folgenden nehmen ROHE Farbwerte (R im niedrigsten Byte, Alpha im höchsten), keine Palette-Indizes.
                ["FillRect"] = args =>
                {
                    mgr.FillRect((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt(), (int)args[3].AsInt(), (int)args[4].AsInt(), (int)args[5].AsInt());
                    return Value.MakeUndefined();
                },
                ["DrawRect"] = args =>
                {
                    mgr.DrawRect((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt(), (int)args[3].AsInt(), (int)args[4].AsInt(), (int)args[5].AsInt());
                    return Value.MakeUndefined();
                },
                ["DrawLine"] = args =>
                {
                    mgr.DrawLine((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt(), (int)args[3].AsInt(), (int)args[4].AsInt(), (int)args[5].AsInt());
                    return Value.MakeUndefined();
                },
                ["DrawText"] = args =>
                {
                    mgr.DrawText((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt(), args[3].AsString(), (int)args[4].AsInt(), (int)args[5].AsInt());
                    return Value.MakeUndefined();
                },
                ["CellWidth"] = args => Value.MakeInt(mgr.GetCellWidth((int)args[0].AsInt())),
                ["CellHeight"] = args => Value.MakeInt(mgr.GetCellHeight((int)args[0].AsInt())),
            };
        }

        private static Dictionary<string, NativeFunction> BuildWindowFunctions(WindowManager mgr)
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args =>
                {
                    try { return Value.MakeInt(mgr.CreateWindow((int)args[0].AsInt(), args[1].AsString())); }
                    catch { return Value.MakeInt(InvalidHandle); }
                },
                ["Destroy"] = args => Value.MakeBool(mgr.DestroyWindow((int)args[0].AsInt())),
                ["Tick"] = args => Value.MakeBool(mgr.Tick((int)args[0].AsInt())),
                ["EnableEvents"] = args =>
                {
                    mgr.EnableEventQueue((int)args[0].AsInt());
                    return Value.MakeBool(true);
                },
                ["NextEvent"] = args => mgr.NextEvent((int)args[0].AsInt()),
                ["RegisterEvent"] = args =>
                {
                    if (args.Count() != 3)
                        return Value.MakeBool(false);
                    
                    EventType eventType = (EventType)args[1].AsInt();
                    var winId = (int)args[0].AsInt();
                    var callback = (LambdaValue)args[2].AsLambda();

                    if (!EventCallback.CheckParameters(callback, eventType))
                        return Value.MakeBool(false);

                    mgr.RegisterCallback(winId, eventType, callback);

                    return Value.MakeBool(true);
                }
            };
        }
        
        private static Dictionary<string, NativeFunction> BuildWindowFunctionStubs()
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args => Value.MakeUndefined() /*STUB*/,
                ["Destroy"] = args => Value.MakeUndefined() /*STUB*/,
                ["Tick"] = args => Value.MakeUndefined() /*STUB*/,
                ["EnableEvents"] = args => Value.MakeUndefined() /*STUB*/,
                ["NextEvent"] = args => Value.MakeUndefined() /*STUB*/,
                ["RegisterEvent"] = args => Value.MakeUndefined() /*STUB*/,
            };
        }

        /// <summary>fire-Quelltext, der die per <see cref="RegisterAll"/>
        /// registrierten nativen Funktionen hinter drei gewöhnlichen Klassen
        /// versteckt - VOR das eigentliche Nutzer-Skript zu setzen (analog
        /// zu Standard.Prelude.Source, siehe Parser.ParseWithPrelude für das
        /// allgemeine Muster). Jede Klasse merkt sich ihre ID als Feld und
        /// gibt sie bei jeder Methode automatisch als erstes Argument an die
        /// zugehörige native Funktion mit - Skript-Code selbst sieht nie
        /// eine rohe ID.</summary>
        public const string PreludeSource = """
            class HandleUnavailableException : Exception {
                string message

                construct(string message) {
                    this.message = message
                }
            }

            class Framebuffer {
                int id

                construct(int width, int height) {
                    this.id = __GRPHFbCreate(width, height)
                    if (this.id == -1) {
                        throw new HandleUnavailableException("Framebuffer konnte nicht erstellt werden.")
                    }
                }

                destruct() {
                    __GRPHFbDestroy(this.id)
                }

                int Width() { return __GRPHFbWidth(this.id) }
                int Height() { return __GRPHFbHeight(this.id) }
                int ReadByte(int offset) { return __GRPHFbReadByte(this.id, offset) }
                WriteByte(int offset, int value) { __GRPHFbWriteByte(this.id, offset, value) }
            }

            class Console {
                int id

                construct(Framebuffer framebuffer) {
                    this.id = __GRPHConCreate(framebuffer.id)
                    if (this.id == -1) {
                        throw new HandleUnavailableException("Konsole konnte nicht erstellt werden.")
                    }
                }

                destruct() {
                    __GRPHConDestroy(this.id)
                }

                Print(string text) { __GRPHConPrint(this.id, text) }
                Locate(int row, int column) { __GRPHConLocate(this.id, row, column) }
                Clear() { __GRPHConClear(this.id) }
                SetColor(int foreground, int background) { __GRPHConSetColor(this.id, foreground, background) }
                SetPixel(int x, int y, int color) { __GRPHConSetPixel(this.id, x, y, color) }
                int GetPixel(int x, int y) { return __GRPHConGetPixel(this.id, x, y) }

                // Farben der folgenden Methoden sind ROHE Werte: r + g*256 + b*65536 + a*16777216 (a = 255 deckend; siehe
                // UI.Color.Rgb) - keine Palette-Indizes wie bei SetColor. Positionen/Größen in PIXELN.
                FillRect(int x, int y, int w, int h, int color) { __GRPHConFillRect(this.id, x, y, w, h, color) }
                DrawRect(int x, int y, int w, int h, int color) { __GRPHConDrawRect(this.id, x, y, w, h, color) }
                DrawLine(int x0, int y0, int x1, int y1, int color) { __GRPHConDrawLine(this.id, x0, y0, x1, y1, color) }
                // background 0 (Alpha 0) = transparent: nur die Zeichen-Pixel werden geschrieben
                DrawText(int x, int y, string text, int color, int background) { __GRPHConDrawText(this.id, x, y, text, color, background) }
                int CellWidth() { return __GRPHConCellWidth(this.id) }
                int CellHeight() { return __GRPHConCellHeight(this.id) }
            }

            class Window {
                int id

                construct(Framebuffer framebuffer, string title) {
                    this.id = __GRPHWinCreate(framebuffer.id, title)
                    if (this.id == -1) {
                        throw new HandleUnavailableException("Fenster konnte nicht erstellt werden.")
                    }
                }

                destruct() {
                    __GRPHWinDestroy(this.id)
                }

                bool Tick() { return __GRPHWinTick(this.id) }

                // Abfrage-Stil statt Callbacks: EnableEvents() schaltet eine Warteschlange ein, danach holt man nach jedem Tick
                // mit NextEvent() ein Ereignis nach dem anderen ab (undefined, wenn keins mehr ansteht). Das Ereignis ist ein
                // Array: e[0] ist der Typ (siehe EventType), der Rest hängt vom Typ ab, Positionen sind ganze Pixel des
                // Framebuffers: MouseDown/MouseUp [typ, taste, x, y], MouseMove [typ, x, y, tasten], MouseScroll [typ, scrollX,
                // scrollY, x, y], KeyDown/KeyUp [typ, keycode, scancode, modifier, wiederholt], TextInput [typ, text], Close [typ].
                // Die Verarbeitung läuft so im Hauptprogramm - mit den echten globalen Variablen, nicht der isolierten Kopie eines
                // Callbacks.
                bool EnableEvents() { return __GRPHWinEnableEvents(this.id) }
                NextEvent() { return __GRPHWinNextEvent(this.id) }
            
            
                bool RegisterMouseDown(lambda<int,float,float> fn)
                {
                    return __GRPHWinRegisterEvent(this.id, EventType.MouseDown!, fn);
                }
            
                bool RegisterMouseUp(lambda<int,float,float> fn)
                {
                    return __GRPHWinRegisterEvent(this.id, EventType.MouseUp!, fn);
                }
            
                bool RegisterMouseMove(lambda<float,float,int> fn)
                {
                    return __GRPHWinRegisterEvent(this.id, EventType.MouseMove!, fn);
                }
            
                bool RegisterMouseMoveRelative(lambda<float,float,int> fn)
                {
                    return __GRPHWinRegisterEvent(this.id, EventType.MouseMoveRelative!, fn);
                }
            
                bool RegisterMouseScroll(lambda<float,float,float,float> fn)
                {
                    return __GRPHWinRegisterEvent(this.id, EventType.MouseScroll!, fn);
                }
            
                bool RegisterKeyDown(lambda<int,int,int,bool> fn)
                {
                    return __GRPHWinRegisterEvent(this.id, EventType.KeyDown!, fn);
                }
            
                bool RegisterKeyUp(lambda<int,int,int,bool> fn)
                {
                    return __GRPHWinRegisterEvent(this.id, EventType.KeyUp!, fn);
                }
            
                bool RegisterTextInput(lambda<string> fn)
                {
                    return __GRPHWinRegisterEvent(this.id, EventType.TextInput!, fn);
                }
            
                bool RegisterClose(lambda fn)
                {
                    return __GRPHWinRegisterEvent(this.id, EventType.Close!, fn);
                }
            
                bool RegisterCloseRequest(lambda fn)
                {
                    return __GRPHWinRegisterEvent(this.id, EventType.CloseRequest!, fn);
                }
            
            }

            enum EventType
            {
                Unknown = 0,
                Close = 1,
                CloseRequest = 2,
                TextInput = 3,
                MouseDown = 8,
                MouseMove = 9,
                MouseMoveRelative = 10,
                MouseUp = 11,
                MouseScroll = 12,
                //MouseEnter = 13,
                //MouseLeave = 14,
                KeyDown = 24,
                KeyUp = 25
            }
            """;
    }
}
