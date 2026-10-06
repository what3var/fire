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

        private readonly Func<IFramebufferRenderer>? _rendererFactory;

        // Ereignis-Warteschlangen der Fenster, die sie per EnableEventQueue angefordert haben (Abfrage-Stil statt Callback, siehe
        // NextEvent). Nur gefüllt, solange eine Warteschlange angefordert ist, damit ein reines Callback-Fenster nichts ansammelt.
        private readonly Dictionary<int, Queue<IEvent>> _eventQueues = new();
        private const int MaxQueuedEvents = 4096;

        /// <param name="rendererFactory">Erzeugt den Renderer je Fenster (Vorgabe: SDL). Für Tests und andere Backends austauschbar.</param>
        public WindowManager(FramebufferManager framebuffers, Action<LambdaValue, Value[]> callbackRunner,
            Func<IFramebufferRenderer>? rendererFactory = null)
        {
            _framebuffers = framebuffers ?? throw new ArgumentNullException(nameof(framebuffers));
            _callbacks = new List<EventCallback>();
            _rendererFactory = rendererFactory;

            Callback = callbackRunner;
        }

        /// <summary>Fordert für das Fenster eine Ereignis-Warteschlange an: ab jetzt landet jedes Ereignis, das Tick holt, auch dort
        /// und lässt sich mit <see cref="NextEvent"/> abfragen - ohne Callback, also auch ohne dessen isolierte Kopie der globalen
        /// Variablen (siehe SPEC 8.1.4). Callbacks laufen daneben unverändert weiter.</summary>
        public void EnableEventQueue(int id)
        {
            _windows.Get(id);
            if (!_eventQueues.ContainsKey(id)) _eventQueues[id] = new Queue<IEvent>();
        }

        /// <summary>Das nächste Ereignis des Fensters als Werte-Array (siehe <see cref="EncodeEvent"/>), oder undefined, wenn keins
        /// ansteht (oder keine Warteschlange angefordert wurde).</summary>
        public Value NextEvent(int id)
        {
            if (!_eventQueues.TryGetValue(id, out var queue) || queue.Count == 0) return Value.MakeUndefined();
            return EncodeEvent(queue.Dequeue());
        }

        /// <summary>Kodiert ein Ereignis als Array: [0] ist der Typ (siehe EventType), der Rest hängt vom Typ ab - Positionen sind
        /// ganze Pixel (nach unten gerundet, in Framebuffer-Koordinaten):
        /// MouseDown/MouseUp [typ, taste, x, y]; MouseMove [typ, x, y, tastenzustand]; MouseMoveRelative [typ, dx, dy, tastenzustand];
        /// MouseScroll [typ, scrollX, scrollY, x, y] (Scrollwerte als Fließkommazahl); KeyDown/KeyUp [typ, keycode, scancode, modifier,
        /// wiederholt]; TextInput [typ, text]; Close/CloseRequest [typ].</summary>
        public static Value EncodeEvent(IEvent evnt)
        {
            static long Px(float v) => (long)Math.Floor(v);
            Value[] items;
            switch (evnt)
            {
                case KeyEvent key:
                    items = new[] { Value.MakeInt((int)key.Type), Value.MakeInt(key.KeyCode), Value.MakeInt(key.ScanCode), Value.MakeInt(key.Modifier), Value.MakeBool(key.IsKeyRepeat) };
                    break;
                case ClickEvent click:
                    items = new[] { Value.MakeInt((int)click.Type), Value.MakeInt(click.Button), Value.MakeInt(Px(click.X)), Value.MakeInt(Px(click.Y)) };
                    break;
                case MotionEvent motion when motion.Type == Event.EventType.MouseMoveRelative:
                    items = new[] { Value.MakeInt((int)motion.Type), Value.MakeInt(Px(motion.Xrel)), Value.MakeInt(Px(motion.Yrel)), Value.MakeInt(motion.ButtonState) };
                    break;
                case MotionEvent motion:
                    items = new[] { Value.MakeInt((int)motion.Type), Value.MakeInt(Px(motion.X)), Value.MakeInt(Px(motion.Y)), Value.MakeInt(motion.ButtonState) };
                    break;
                case ScrollEvent scroll:
                    items = new[] { Value.MakeInt((int)scroll.Type), Value.MakeFloat(scroll.ScrollX), Value.MakeFloat(scroll.ScrollY), Value.MakeInt(Px(scroll.X)), Value.MakeInt(Px(scroll.Y)) };
                    break;
                case TextEvent text:
                    items = new[] { Value.MakeInt((int)text.Type), text.Text == null ? Value.MakeUndefined() : Value.MakeString(text.Text) };
                    break;
                default:
                    items = new[] { Value.MakeInt((int)evnt.Type) };
                    break;
            }
            var array = new ScriptArray(items.Length);
            for (int i = 0; i < items.Length; i++) array.Items[i] = items[i];
            return Value.MakeArray(array);
        }

        /// <summary>Erzeugt UND öffnet sofort ein neues Fenster für den
        /// Framebuffer mit der ID `framebufferId` - anders als
        /// FramebufferManager.CreateFramebuffer/RendererManager.CreateRenderer
        /// (die nur das C#-Objekt anlegen) macht das hier auch gleich das
        /// eigentliche OS-Fenster sichtbar, da ein unsichtbar erzeugtes
        /// Fenster für den Aufrufer keinen Sinn ergäbe.</summary>
        public int CreateWindow(int framebufferId, string title = "fire Konsole")
        {
            var fb = _framebuffers.GetFramebuffer(framebufferId);
            var window = new ConsoleWindow(fb, _rendererFactory?.Invoke());
            
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
            _eventQueues.Remove(id);
            window.Dispose();
            return _windows.Destroy(id);
        }

        /// <summary>Für C#-seitige Weiterverwendung - kein Teil des rein-
        /// ID-basierten Oberflächen-APIs.</summary>
        public ConsoleWindow GetWindow(int id) => _windows.Get(id);

        public bool GetVSync(int id) => _windows.Get(id).VSync;

        public void SetVSync(int id, bool enabled) => _windows.Get(id).VSync = enabled;

        /// <summary>Ein einzelner Zyklus für EIN Fenster (siehe
        /// ConsoleWindow.Tick) - liefert false, wenn das Fenster vom Nutzer
        /// geschlossen wurde (der Aufrufer sollte dann üblicherweise
        /// DestroyWindow(id) nachziehen).</summary>
        public bool Tick(int id)
        {
            var result = _windows.Get(id).Tick();

            if (result?.Events != null)
            {
                if (_eventQueues.TryGetValue(id, out var queue))
                    foreach (var queued in result.Events)
                        if (queue.Count < MaxQueuedEvents) queue.Enqueue(queued);

                foreach (var evnt in result.Events)
                {
                    // ToList: ein Callback darf selbst Ereignisse an- oder abmelden, ohne die Schleife zu stören
                    foreach (var hndlr in _callbacks.Where(c => c.WindowHandle == id && c.EventType == evnt.Type).ToList())
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
