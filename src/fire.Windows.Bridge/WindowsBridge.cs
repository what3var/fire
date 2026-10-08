using fire.Bytecode;
using fire.Runtime;
using fire.Terminal;
using fire.Terminal.Event;
using fire.Terminal.Windows;
using fire.Values;
using System;
using System.Collections.Generic;

namespace fire.Windows.Bridge
{
    /// <summary>
    /// Die Fenster-Brücke (`#import "windows"`): das SDL-Fenster, in dem ein Framebuffer der Grafik-Brücke (`#import "graphics"`) angezeigt wird -
    /// getrennt von `graphics`, damit ein Programm für eine Plattform ohne Fenster (Embedded) nur `graphics` importiert und stattdessen ein anderes
    /// Ausgabegerät (z.B. ein Display) einbindet. Registriert den <see cref="WindowManager"/> als native Funktionen (Präfix <see cref="WindowPrefix"/>)
    /// und liefert die Klassen `Window` und `EventType` als fire-Quelltext (<see cref="PreludeSource"/>). Setzt `graphics` voraus
    /// (<c>HandleUnavailableException</c> und `Framebuffer` stammen von dort).
    /// </summary>
    public static class WindowsBridge
    {
        public const string WindowPrefix = "__GRPHWin";

        /// <summary>Ungültige/fehlgeschlagene Erzeugung (wie <c>GraphicsBridge.InvalidHandle</c>).</summary>
        public const int InvalidHandle = -1;

        public static void RegisterAll(NativeRegistry natives, WindowManager windows) =>
            natives.RegisterGroup(WindowPrefix, BuildWindowFunctions(windows));

        public static void RegisterStubs(NativeRegistry natives) =>
            natives.RegisterGroup(WindowPrefix, BuildWindowFunctionStubs());

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
                },
                // WICHTIG: neue Funktionen immer ANS ENDE, in BuildWindowFunctionStubs in derselben Reihenfolge (Index = Position).
                ["SetVSync"] = args =>
                {
                    mgr.SetVSync((int)args[0].AsInt(), args[1].AsBool());
                    return Value.MakeUndefined();
                },
                ["GetVSync"] = args => Value.MakeBool(mgr.GetVSync((int)args[0].AsInt())),
                ["SetAutoResize"] = args =>
                {
                    mgr.SetAutoResize((int)args[0].AsInt(), args[1].AsBool());
                    return Value.MakeUndefined();
                },
                ["GetAutoResize"] = args => Value.MakeBool(mgr.GetAutoResize((int)args[0].AsInt())),
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
                ["SetVSync"] = args => Value.MakeUndefined() /*STUB*/,
                ["GetVSync"] = args => Value.MakeUndefined() /*STUB*/,
                ["SetAutoResize"] = args => Value.MakeUndefined() /*STUB*/,
                ["GetAutoResize"] = args => Value.MakeUndefined() /*STUB*/,
            };
        }

        /// <summary>fire-Quelltext der Klassen `Window` und `EventType` - hinter die Prelude von `graphics` zu setzen (siehe ImportedPreludes).</summary>
        public const string PreludeSource = """
            class Window {
                int id

                construct(Framebuffer framebuffer, string title) {
                    this.id = __GRPHWinCreate(framebuffer.id, title)
                    if (this.id == -1) {
                        throw new HandleUnavailableException("The window could not be created.")
                    }
                }

                destruct() {
                    __GRPHWinDestroy(this.id)
                }

                // Holt die Ereignisse ab und zeigt den aktuellen Inhalt des Framebuffers. Mit VSync (Vorgabe) wartet jedes Tick auf die
                // Bildwiederholung des Monitors (60 Hz = 16,7 ms): ideal für Animationen und Warteschleifen (`while (win.Tick()) { ... }`),
                // aber eine Zeichenschleife mit einem Tick je Durchlauf braucht dann 256 x 16,7 ms = 4,3 s. Wer viel zeichnet und nur
                // gelegentlich anzeigen will, ruft Tick seltener auf oder setzt `win.VSync = false` (Tick kehrt dann sofort zurück).
                bool Tick() { return __GRPHWinTick(this.id) }

                bool VSync {
                    get { return __GRPHWinGetVSync(this.id) }
                    set { __GRPHWinSetVSync(this.id, value) }
                }

                // true: zieht der Nutzer das Fenster auf eine andere Größe, bekommt der Framebuffer genau diese Größe (sofern sie gültig ist, siehe Framebuffer.Resize) - statt dass sein Inhalt
                // auf das Fenster gestreckt wird (Vorgabe: false, wie bisher). Das Programm bekommt das Ereignis EventType.Resize und zeichnet in der neuen Größe neu; die Mauspositionen
                // sind dann Pixel des Framebuffers 1:1. (Ein UI.Root schaltet es ein: `root.autoResize`.)
                bool AutoResize {
                    get { return __GRPHWinGetAutoResize(this.id) }
                    set { __GRPHWinSetAutoResize(this.id, value) }
                }

                // Abfrage-Stil statt Callbacks: EnableEvents() schaltet eine Warteschlange ein, danach holt man nach jedem Tick
                // mit NextEvent() ein Ereignis nach dem anderen ab (undefined, wenn keins mehr ansteht). Das Ereignis ist ein
                // Array: e[0] ist der Typ (siehe EventType), der Rest hängt vom Typ ab, Positionen sind ganze Pixel des
                // Framebuffers: MouseDown/MouseUp [typ, taste, x, y], MouseMove [typ, x, y, tasten], MouseScroll [typ, scrollX,
                // scrollY, x, y], KeyDown/KeyUp [typ, keycode, scancode, modifier, wiederholt], TextInput [typ, text], Resize [typ, breite, höhe] (die Größe des Fensters; mit AutoResize hat der
                // Framebuffer sie schon, wenn sie gültig ist), Close [typ].
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
            
                bool RegisterResize(lambda<int,int> fn)
                {
                    return __GRPHWinRegisterEvent(this.id, EventType.Resize!, fn);
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
                Resize = 4,
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
