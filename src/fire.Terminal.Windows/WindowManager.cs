using fire.Runtime;
using fire.Terminal;
using fire.Terminal.Event;
using fire.Values;
using System;
using System.Collections.Generic;
using static SDL3.SDL;

namespace fire.Terminal.Windows
{
    /// <summary>
    /// Verwaltet Konsolenfenster (ConsoleWindow-Instanzen) über aufsteigende,
    /// eindeutige IDs (siehe IdManager) - jedes Fenster zeigt EINEN
    /// Framebuffer (per ID, siehe FramebufferManager) an. Lebt bewusst in
    /// diesem Windows-Projekt (nicht im plattformunabhängigen Kern), da
    /// ConsoleWindow selbst SDL/Fenster-Handling voraussetzt.
    /// </summary>
    public sealed class WindowManager
    {
        private readonly IdManager<ConsoleWindow> _windows = new();
        private readonly FramebufferManager _framebuffers;

        private readonly List<EventCallback> _callbacks;

        private Action<LambdaValue, Value[]> Callback;

        public WindowManager(FramebufferManager framebuffers, Action<LambdaValue, Value[]> callbackRunner)
        {
            _framebuffers = framebuffers ?? throw new ArgumentNullException(nameof(framebuffers));
            _callbacks = new List<EventCallback>();

            Callback = callbackRunner;
        }

        /// <summary>Erzeugt UND öffnet sofort ein neues Fenster für den
        /// Framebuffer mit der ID `framebufferId` - anders als
        /// FramebufferManager.CreateFramebuffer/ConsoleManager.CreateConsole
        /// (die nur das C#-Objekt anlegen) macht das hier auch gleich das
        /// eigentliche OS-Fenster sichtbar, da ein unsichtbar erzeugtes
        /// Fenster für den Aufrufer keinen Sinn ergäbe.</summary>
        public int CreateWindow(int framebufferId, string title = "fire Konsole")
        {
            var fb = _framebuffers.GetFramebuffer(framebufferId);
            var window = new ConsoleWindow(fb);
            
            var handle = _windows.Create(window);
            
            window.Open(handle, title);
            
            return handle;
        }

        public void RegisterCallback(int id, Event.EventType eventType, LambdaValue callback)
        {
            _callbacks.Add(new EventCallback() { Callback = callback, EventType = eventType, WindowHandle = id });
        }

        public void UnregisterCallbacks(int id)
        {
            _callbacks.RemoveAll(c => c.WindowHandle == id);
        }

        public void UnregisterCallbacks(int id, Event.EventType eventType)
        {
            _callbacks.RemoveAll(c => c.WindowHandle == id && c.EventType == eventType);
        }

        /// <summary>Schließt und zerstört das Fenster - bereits zerstörte/
        /// unbekannte IDs sind KEIN Fehler (siehe IdManager.Destroy-Doku).</summary>
        public bool DestroyWindow(int id)
        {
            if (!_windows.TryGet(id, out var window) || window == null) return false;
            window.Dispose();
            return _windows.Destroy(id);
        }

        /// <summary>Für C#-seitige Weiterverwendung - kein Teil des rein-
        /// ID-basierten Oberflächen-APIs.</summary>
        public ConsoleWindow GetWindow(int id) => _windows.Get(id);

        /// <summary>Ein einzelner Zyklus für EIN Fenster (siehe
        /// ConsoleWindow.Tick) - liefert false, wenn das Fenster vom Nutzer
        /// geschlossen wurde (der Aufrufer sollte dann üblicherweise
        /// DestroyWindow(id) nachziehen).</summary>
        public bool Tick(int id)
        {
            var result = _windows.Get(id).Tick();

            if (result?.Events != null)
            {
                foreach (var evnt in result.Events)
                {
                    foreach (var hndlr in _callbacks.Where(c => c.WindowHandle == id && c.EventType == evnt.Type))
                    {
                        switch(evnt.Type)
                        {
                            case Event.EventType.KeyDown:
                            case Event.EventType.KeyUp:
                                var keyevent = (KeyEvent)evnt;
                                Callback(hndlr.Callback,
                                    new[]
                                    {
                                        //Value.MakeInt(keyevent.SourceHandle),
                                        //Value.MakeInt((int)keyevent.Type),
                                        Value.MakeInt(keyevent.KeyCode),
                                        Value.MakeInt(keyevent.ScanCode),
                                        Value.MakeInt(keyevent.Modifier),
                                        //Value.MakeBool(keyevent.IsButtonDown),
                                        Value.MakeBool(keyevent.IsKeyRepeat)
                                    });
                                break;
                            case Event.EventType.Close:

                                var closeevent = (Event.Event)evnt;
                                Callback(hndlr.Callback,
                                    new Value[0]);
                                break;
                            case Event.EventType.CloseRequest:

                                var closerqevent = (Event.Event)evnt;
                                Callback(hndlr.Callback,
                                    new Value[0]);
                                break;
                            case Event.EventType.MouseDown:
                            case Event.EventType.MouseUp:
                                var clickevent = (ClickEvent)evnt;
                                Callback(hndlr.Callback,
                                    new[]
                                    {
                                        //Value.MakeInt(clickevent.SourceHandle),
                                        //Value.MakeInt((int)clickevent.Type),
                                        Value.MakeInt(clickevent.Button),
                                        Value.MakeFloat(clickevent.X),
                                        Value.MakeFloat(clickevent.Y),
//                                        Value.MakeBool(clickevent.IsButtonDown)
                                    });
                                break;
                            case Event.EventType.MouseMove:
                                var moveevent = (MotionEvent)evnt;
                                Callback(hndlr.Callback,
                                    new[]
                                    {
                                        //Value.MakeInt(moveevent.SourceHandle),
                                        //Value.MakeInt((int)moveevent.Type),
                                        Value.MakeFloat(moveevent.X),
                                        Value.MakeFloat(moveevent.Y),
                                        //Value.MakeFloat(moveevent.Xrel),
                                        //Value.MakeFloat(moveevent.Yrel),
                                        Value.MakeInt(moveevent.ButtonState)
                                    });
                                break;
                            case Event.EventType.MouseMoveRelative:
                                var relevent = (MotionEvent)evnt;
                                Callback(hndlr.Callback,
                                    new[]
                                    {
                                        //Value.MakeInt(moveevent.SourceHandle),
                                        //Value.MakeInt((int)moveevent.Type),
                                        Value.MakeFloat(relevent.Xrel),
                                        Value.MakeFloat(relevent.Yrel),
                                        //Value.MakeFloat(moveevent.Xrel),
                                        //Value.MakeFloat(moveevent.Yrel),
                                        Value.MakeInt(relevent.ButtonState)
                                    });
                                break;
                            case Event.EventType.MouseScroll:
                                var scrollevent = (ScrollEvent)evnt;
                                Callback(hndlr.Callback,
                                    new[]
                                    {
                                        //Value.MakeInt(scrollevent.SourceHandle),
                                        //Value.MakeInt((int)scrollevent.Type),
                                        Value.MakeFloat(scrollevent.ScrollX),
                                        Value.MakeFloat(scrollevent.ScrollY),
                                        Value.MakeFloat(scrollevent.X),
                                        Value.MakeFloat(scrollevent.Y)
                                    });
                                break;
                            case Event.EventType.TextInput:
                                var textevent = (TextEvent)evnt;
                                Callback(hndlr.Callback,
                                    new[]
                                    {
                                        //Value.MakeInt(textevent.SourceHandle),
                                        //Value.MakeInt((int)textevent.Type),
                                        textevent.Text == null ? Value.MakeUndefined() : Value.MakeString(textevent.Text)
                                    }); 
                                break;
                        }
                    }

                }
            }

            return result?.StillOpen == true;
        }

        /// <summary>Bequemlichkeitsmethode: tickt ALLE aktuell verwalteten
        /// Fenster einmal durch und räumt dabei automatisch jedes Fenster
        /// auf, das der Nutzer währenddessen geschlossen hat (kein
        /// manuelles Nachziehen von DestroyWindow nötig). Für einen Host
        /// mit EINER zentralen Schleife, die neben der Skript-VM beliebig
        /// viele Konsolenfenster gleichzeitig offen hält.</summary>
        public void TickAll()
        {
            List<int>? closed = null;
            foreach (int id in _windows.Ids)
            {
                if (_windows.Get(id).Tick()?.StillOpen == false)
                {
                    closed ??= new List<int>();
                    closed.Add(id);
                }
            }
            if (closed == null) return;
            foreach (int id in closed)
                DestroyWindow(id);
        }
    }
}
