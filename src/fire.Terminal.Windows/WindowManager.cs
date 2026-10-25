using fire.Runtime;
using fire.Terminal;
using fire.Terminal.Event;
using fire.Values;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static SDL3.SDL;

namespace fire.Terminal.Windows
{
    /// <summary>
    /// Manages console windows (ConsoleWindow instances) via ascending,
    /// unique IDs (see IdManager) - each window shows ONE
    /// framebuffer (by ID, see FramebufferManager). Deliberately lives in
    /// this Windows project (not in the platform-independent core), since
    /// ConsoleWindow itself requires SDL/window handling.
    /// </summary>
    public sealed class WindowManager
    {
        private readonly IdManager<ConsoleWindow> _windows = new();
        private readonly FramebufferManager _framebuffers;

        private readonly List<EventCallback> _callbacks;

        private Action<LambdaValue, Value[]> Callback;

        private readonly Func<IFramebufferRenderer>? _rendererFactory;

        private readonly Action<int>? _hold;

        // Event queues of the windows that requested them via EnableEventQueue (polling style instead of a callback, see
        // NextEvent). Only filled as long as a queue is requested, so that a pure callback window accumulates nothing.
        private readonly Dictionary<int, Queue<IEvent>> _eventQueues = new();
        private const int MaxQueuedEvents = 4096;

        // Windows whose input is blocked (Window.Blocked: a modal window of their manager is open): their input events are dropped, only a resize and the
        // closing of the program still come through.
        private readonly HashSet<int> _blocked = new();

        /// <summary>Blocks the input of the window (a modal window is open above it) or lets it through again.</summary>
        public void SetBlocked(int id, bool blocked)
        {
            _windows.Get(id);
            if (blocked) _blocked.Add(id); else _blocked.Remove(id);
        }

        public bool GetBlocked(int id) => _blocked.Contains(id);

        // Window.AutoClose: the windows that close themselves when the user asks for it (the frame's close button, Alt+F4) - all, except those in
        // _manualClose; a window that got such a request counts as closed from then on (Tick/Pump say false).
        private readonly HashSet<int> _manualClose = new();
        private readonly HashSet<int> _closeRequested = new();

        public bool GetAutoClose(int id) { _windows.Get(id); return !_manualClose.Contains(id); }

        public void SetAutoClose(int id, bool enabled)
        {
            _windows.Get(id);
            if (enabled) _manualClose.Remove(id); else _manualClose.Add(id);
        }

        /// <summary>Has the window been closed by a close request (AutoClose)?</summary>
        public bool CloseRequested(int id) => _closeRequested.Contains(id);

        /// <summary>Started managers only give the beat, the windows stay on the program's thread (it opens and pumps them at its safe points): macOS,
        /// where windows may only live on the main thread. FIRE_WINDOW_COOPERATIVE=1 switches it on elsewhere too (tests).</summary>
        public static bool CooperativeManagers { get; set; } =
            OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("FIRE_WINDOW_COOPERATIVE") == "1";

        /// <param name="rendererFactory">Creates the renderer per window (default: SDL). Replaceable for tests and other backends.</param>
        /// <param name="hold">Adds holds on the program end (fire.Runtime's FireRuntime.AddHostHold): the windows that a manager's thread shows keep the program alive.</param>
        public WindowManager(FramebufferManager framebuffers, Action<LambdaValue, Value[]> callbackRunner,
            Func<IFramebufferRenderer>? rendererFactory = null, Action<int>? hold = null)
        {
            _hold = hold;
            _framebuffers = framebuffers ?? throw new ArgumentNullException(nameof(framebuffers));
            _callbacks = new List<EventCallback>();
            _rendererFactory = rendererFactory;

            Callback = callbackRunner;
        }

        /// <summary>Requests an event queue for the window: from now on every event that Tick fetches also lands there
        /// and can be polled with <see cref="NextEvent"/> - without a callback, so also without its isolated copy of the global
        /// variables (see SPEC 8.1.4). Callbacks keep running alongside, unchanged.</summary>
        public void EnableEventQueue(int id)
        {
            _windows.Get(id);
            if (!_eventQueues.ContainsKey(id)) _eventQueues[id] = new Queue<IEvent>();
        }

        /// <summary>The window's next event as a value array (see <see cref="EncodeEvent"/>), or undefined if none
        /// is pending (or no queue was requested).</summary>
        public Value NextEvent(int id)
        {
            if (!_eventQueues.TryGetValue(id, out var queue) || queue.Count == 0) return Value.MakeUndefined();
            return EncodeEvent(queue.Dequeue());
        }

        /// <summary>Encodes an event as an array: [0] is the type (see EventType), the rest depends on the type - positions are
        /// whole pixels (rounded down, in framebuffer coordinates):
        /// MouseDown/MouseUp [type, button, x, y]; MouseMove [type, x, y, button state]; MouseMoveRelative [type, dx, dy, button state];
        /// MouseScroll [type, scrollX, scrollY, x, y] (scroll values as floating point); KeyDown/KeyUp [type, keycode, scancode, modifier,
        /// repeated]; TextInput [type, text]; Resize [type, width, height] (the size of the window); Close/CloseRequest [type];
        /// TouchDown/TouchMove/TouchUp [type, finger, x, y, pressure] (pixels of the framebuffer, pressure 0 to 1 as floating point);
        /// JoystickAxis [type, joystick, axis, position] (position -1 to 1 as floating point); JoystickButtonDown/Up [type, joystick, button];
        /// JoystickHat [type, joystick, hat, directions] (bit mask: 1 up, 2 right, 4 down, 8 left; 0 centre); JoystickAdded/Removed [type, joystick].</summary>
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
                case ResizeEvent resize:
                    items = new[] { Value.MakeInt((int)resize.Type), Value.MakeInt(resize.Width), Value.MakeInt(resize.Height) };
                    break;
                case TouchEvent touch:
                    items = new[] { Value.MakeInt((int)touch.Type), Value.MakeInt(touch.Finger), Value.MakeInt(Px(touch.X)), Value.MakeInt(Px(touch.Y)), Value.MakeFloat(touch.Pressure) };
                    break;
                case JoystickEvent joy when joy.Type == Event.EventType.JoystickAxis:
                    items = new[] { Value.MakeInt((int)joy.Type), Value.MakeInt(joy.Joystick), Value.MakeInt(joy.Index), Value.MakeFloat(joy.Value) };
                    break;
                case JoystickEvent joy when joy.Type == Event.EventType.JoystickHat:
                    items = new[] { Value.MakeInt((int)joy.Type), Value.MakeInt(joy.Joystick), Value.MakeInt(joy.Index), Value.MakeInt((long)joy.Value) };
                    break;
                case JoystickEvent joy when joy.Type is Event.EventType.JoystickButtonDown or Event.EventType.JoystickButtonUp:
                    items = new[] { Value.MakeInt((int)joy.Type), Value.MakeInt(joy.Joystick), Value.MakeInt(joy.Index) };
                    break;
                case JoystickEvent joy:
                    items = new[] { Value.MakeInt((int)joy.Type), Value.MakeInt(joy.Joystick) };
                    break;
                default:
                    items = new[] { Value.MakeInt((int)evnt.Type) };
                    break;
            }
            var array = new ScriptArray(items.Length);
            for (int i = 0; i < items.Length; i++) array.Items[i] = items[i];
            return Value.MakeArray(array);
        }

        /// <summary>Creates AND immediately opens a new window for the
        /// framebuffer with the ID `framebufferId` - unlike
        /// FramebufferManager.CreateFramebuffer/RendererManager.CreateRenderer
        /// (which only create the C# object), this also makes
        /// the actual OS window visible, since a window created
        /// invisibly would make no sense to the caller.</summary>
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

        /// <summary>Closes and destroys the window - already destroyed/
        /// unknown IDs are NOT an error (see the IdManager.Destroy documentation).</summary>
        public bool DestroyWindow(int id)
        {
            if (!_windows.TryGet(id, out var window) || window == null) return false;
            _eventQueues.Remove(id);
            _blocked.Remove(id);
            _manualClose.Remove(id);
            _closeRequested.Remove(id);
            bool threadOwned = false;
            lock (_managersGate)
                if (_managed.TryGetValue(id, out var entry))
                {
                    threadOwned = entry.Manager.OwnsWindows;
                    DetachLocked(entry.Manager, entry.State);   // a manager's thread closes its OS window itself
                }
            if (!threadOwned) window.Dispose();
            return _windows.Destroy(id);
        }

        /// <summary>For continued use on the C# side - not part of the purely
        /// ID-based surface API.</summary>
        public ConsoleWindow GetWindow(int id) => _windows.Get(id);

        public bool GetAutoResize(int id) => _windows.Get(id).AutoResize;

        public void SetAutoResize(int id, bool enabled) => _windows.Get(id).AutoResize = enabled;

        public bool GetTouchMouse(int id) => _windows.Get(id).TouchMouse;

        public void SetTouchMouse(int id, bool enabled) { _windows.Get(id); OnOwner(id, w => w.TouchMouse = enabled); }

        public bool GetVSync(int id) => _windows.Get(id).VSync;

        public void SetVSync(int id, bool enabled) { _windows.Get(id); OnOwner(id, w => w.VSync = enabled); }

        /// <summary>Window.SetSize (on the thread that owns the OS window).</summary>
        public void SetSize(int id, int width, int height) { _windows.Get(id); OnOwner(id, w => w.SetSize(width, height)); }

        /// <summary>A single cycle for ONE window (see
        /// ConsoleWindow.Tick) - returns false if the window was closed
        /// by the user (the caller should then usually
        /// follow up with DestroyWindow(id)).</summary>
        public bool Tick(int id)
        {
            var window = _windows.Get(id);
            if (ManagedStarted(id)) { bool open = Pump(id); Present(id); return open; }
            var result = window.Tick();
            Deliver(id, result?.Events);
            return result?.StillOpen == true && !_closeRequested.Contains(id);
        }

        /// <summary>Puts the events of a window into its queue (EnableEventQueue) and runs its callbacks - on the calling thread (the program's).</summary>
        private void Deliver(int id, IEnumerable<IEvent>? events)
        {
            if (events == null) return;
            if (_blocked.Contains(id)) events = events.Where(e => e.Type == Event.EventType.Resize || e.Type == Event.EventType.Close).ToList();
            // AutoClose: the request closes the window (its handlers still hear of it)
            if (!_manualClose.Contains(id) && events.Any(e => e.Type == Event.EventType.CloseRequest)) _closeRequested.Add(id);
            var result = new WindowPumpResult { StillOpen = true, Events = events };
            if (result?.Events != null)
            {
                if (_eventQueues.TryGetValue(id, out var queue))
                    foreach (var queued in result.Events)
                        if (queue.Count < MaxQueuedEvents) queue.Enqueue(queued);

                foreach (var evnt in result.Events)
                {
                    // ToList: a callback may itself register or unregister events without disturbing the loop
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
                            case Event.EventType.Resize:
                                var resizeevent = (ResizeEvent)evnt;
                                Callback(hndlr.Callback, new[] { Value.MakeInt(resizeevent.Width), Value.MakeInt(resizeevent.Height) });
                                break;
                            case Event.EventType.TouchDown:
                            case Event.EventType.TouchMove:
                            case Event.EventType.TouchUp:
                                var touchevent = (TouchEvent)evnt;
                                Callback(hndlr.Callback, new[] { Value.MakeInt(touchevent.Finger), Value.MakeFloat(touchevent.X), Value.MakeFloat(touchevent.Y), Value.MakeFloat(touchevent.Pressure) });
                                break;
                            case Event.EventType.JoystickAxis:
                                var axisevent = (JoystickEvent)evnt;
                                Callback(hndlr.Callback, new[] { Value.MakeInt(axisevent.Joystick), Value.MakeInt(axisevent.Index), Value.MakeFloat(axisevent.Value) });
                                break;
                            case Event.EventType.JoystickHat:
                                var hatevent = (JoystickEvent)evnt;
                                Callback(hndlr.Callback, new[] { Value.MakeInt(hatevent.Joystick), Value.MakeInt(hatevent.Index), Value.MakeInt((long)hatevent.Value) });
                                break;
                            case Event.EventType.JoystickButtonDown:
                            case Event.EventType.JoystickButtonUp:
                                var buttonevent = (JoystickEvent)evnt;
                                Callback(hndlr.Callback, new[] { Value.MakeInt(buttonevent.Joystick), Value.MakeInt(buttonevent.Index) });
                                break;
                            case Event.EventType.JoystickAdded:
                            case Event.EventType.JoystickRemoved:
                                Callback(hndlr.Callback, new[] { Value.MakeInt(((JoystickEvent)evnt).Joystick) });
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

        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------
        // Window managers (fire's `WindowManager`, docs/UI.md): a manager cycles the windows attached to it - on the program's thread (Tick/Run), or,
        // once started, with a thread of its own that owns the OS windows: it opens them, fetches their events and shows the last frame the program
        // published, about 60 times a second. The program's side of a cycle (callbacks, the queue, drawing) still runs on the program's thread: the
        // thread posts the manager's service lambda to it (see the callback runner), which the main program runs at its next safe point.
        // ---------------------------------------------------------------------------------------------------------------------------------------------

        private sealed class ManagedWindow
        {
            public int Id;
            public ConsoleWindow Window = null!;
            public readonly System.Collections.Concurrent.ConcurrentQueue<IEvent> Inbox = new();
            public volatile bool Closed;
            public Framebuffer? Front;          // the frame the program published last (under the manager's gate)
            public bool FrontNew;
            public bool Release;                // the thread closes the OS window and forgets it
            public readonly System.Collections.Concurrent.ConcurrentQueue<Action<ConsoleWindow>> Todo = new();   // settings for the thread
        }

        private sealed class Manager
        {
            public int Id;
            public readonly object Gate = new();
            public readonly List<ManagedWindow> Windows = new();
            public Thread? Thread;
            public volatile bool Stop;
            public LambdaValue? Service;
            public int ServicePending;
            public long Cycles;
            public bool Cooperative;            // its thread only gives the beat (CooperativeManagers): the windows stay on the program's thread
            public bool Started => Thread != null;
            public bool OwnsWindows => Thread != null && !Cooperative;
        }

        private readonly Dictionary<int, Manager> _managers = new();
        private readonly Dictionary<int, (Manager Manager, ManagedWindow State)> _managed = new();
        private int _nextManager = 1;
        private readonly object _managersGate = new();

        public int CreateManager()
        {
            lock (_managersGate)
            {
                int id = _nextManager++;
                _managers[id] = new Manager { Id = id };
                return id;
            }
        }

        private Manager ManagerOf(int id)
        {
            lock (_managersGate)
                return _managers.TryGetValue(id, out var m) ? m : throw new InvalidOperationException($"No window manager with ID {id}.");
        }

        private bool ManagedStarted(int windowId)
        {
            lock (_managersGate) return _managed.TryGetValue(windowId, out var entry) && entry.Manager.OwnsWindows;
        }

        /// <summary>Attaches the window to the manager (taking it from another manager first).</summary>
        public bool AttachToManager(int managerId, int windowId)
        {
            var manager = ManagerOf(managerId);
            var window = _windows.Get(windowId);
            lock (_managersGate)
            {
                if (_managed.TryGetValue(windowId, out var old))
                {
                    if (old.Manager == manager) return true;
                    DetachLocked(old.Manager, old.State);
                }
                var state = new ManagedWindow { Id = windowId, Window = window };
                lock (manager.Gate) manager.Windows.Add(state);
                _managed[windowId] = (manager, state);
                if (manager.Started) _hold?.Invoke(1);
                return true;
            }
        }

        public bool DetachFromManager(int managerId, int windowId)
        {
            var manager = ManagerOf(managerId);
            lock (_managersGate)
            {
                if (!_managed.TryGetValue(windowId, out var entry) || entry.Manager != manager) return false;
                DetachLocked(manager, entry.State);
                return true;
            }
        }

        private void DetachLocked(Manager manager, ManagedWindow state)
        {
            _managed.Remove(state.Id);
            if (manager.OwnsWindows)
            {
                // the OS window belongs to the manager's thread: it closes it there
                lock (manager.Gate) state.Release = true;
                _hold?.Invoke(-1);
            }
            else
            {
                lock (manager.Gate) manager.Windows.Remove(state);
                if (manager.Started) _hold?.Invoke(-1);
            }
        }

        /// <summary>Starts the manager's thread. `service` is the fire lambda that the program runs for each cycle (it is posted to the program through the
        /// callback runner). false when it already runs.</summary>
        public bool StartManager(int managerId, LambdaValue service)
        {
            var manager = ManagerOf(managerId);
            lock (_managersGate)
            {
                if (manager.Started) return false;
                manager.Service = service;
                manager.Cooperative = CooperativeManagers;
                lock (manager.Gate) _hold?.Invoke(manager.Windows.Count(w => !w.Release));
                manager.Thread = manager.Cooperative
                    ? new Thread(() => BeatThread(manager)) { IsBackground = true, Name = "fire window manager " + managerId + " (beat)" }
                    : new Thread(() => PumpThread(manager)) { IsBackground = true, Name = "fire window manager " + managerId };
            }
            manager.Thread.Start();
            return true;
        }

        public bool ManagerStarted(int managerId) => ManagerOf(managerId).Started;

        /// <summary>The program runs the cycle that the manager's thread asked for (its service lambda): the next one may be asked for - also when that cycle
        /// does nothing (a modal window runs the cycles itself meanwhile).</summary>
        public void ManagerServed(int managerId) => Interlocked.Exchange(ref ManagerOf(managerId).ServicePending, 0);

        /// <summary>Waits (on the program's thread) up to `milliseconds` for the next cycle of the manager's thread.</summary>
        public void ManagerWait(int managerId, int milliseconds)
        {
            var manager = ManagerOf(managerId);
            lock (manager.Gate)
            {
                long seen = manager.Cycles;
                if (manager.Started && manager.Cycles == seen) Monitor.Wait(manager.Gate, Math.Max(1, milliseconds));
            }
        }

        /// <summary>The thread of a cooperative manager: about 60 times a second it asks the program for a cycle; the program opens, pumps and shows the
        /// windows itself (Pump/Present as without a thread).</summary>
        private void BeatThread(Manager manager)
        {
            while (!manager.Stop)
            {
                lock (manager.Gate) { manager.Cycles++; Monitor.PulseAll(manager.Gate); }
                if (manager.Service != null && Interlocked.CompareExchange(ref manager.ServicePending, 1, 0) == 0)
                    Callback(manager.Service, Array.Empty<Value>());
                Thread.Sleep(16);
            }
        }

        private void PumpThread(Manager manager)
        {
            var events = new List<IEvent>();
            while (!manager.Stop)
            {
                long start = Environment.TickCount64;
                ManagedWindow[] windows;
                lock (manager.Gate) windows = manager.Windows.ToArray();
                foreach (var w in windows)
                {
                    if (w.Release)
                    {
                        lock (manager.Gate) manager.Windows.Remove(w);
                        try { w.Window.Dispose(); } catch (Exception) { }
                        continue;
                    }
                    if (w.Closed) continue;
                    try
                    {
                        while (w.Todo.TryDequeue(out var todo)) todo(w.Window);
                        var result = w.Window.PumpRaw();
                        if (result?.Events != null) foreach (var e in result.Events) w.Inbox.Enqueue(e);
                        if (result != null && !result.StillOpen) w.Closed = true;
                        Framebuffer? frame;
                        lock (manager.Gate) { frame = w.Front; w.FrontNew = false; }
                        if (frame != null) lock (frame) w.Window.Present(frame);
                    }
                    catch (Exception)
                    {
                        w.Closed = true;   // a window that cannot be shown counts as closed
                    }
                }
                lock (manager.Gate) { manager.Cycles++; Monitor.PulseAll(manager.Gate); }
                if (manager.Service != null && Interlocked.CompareExchange(ref manager.ServicePending, 1, 0) == 0)
                    Callback(manager.Service, Array.Empty<Value>());
                // about 60 cycles a second: with VSync the presenting has already waited for the screen, otherwise (or with nothing to show) the rest of the period
                long rest = 16 - (Environment.TickCount64 - start);
                if (rest > 0) Thread.Sleep((int)rest);
            }
            ManagedWindow[] left;
            lock (manager.Gate) { left = manager.Windows.ToArray(); manager.Windows.Clear(); }
            foreach (var w in left) { try { w.Window.Dispose(); } catch (Exception) { } }
        }

        /// <summary>The program's side of a cycle, first half: the events of the window (from its manager's thread, or fetched here when nobody else
        /// shows it) go into its queue and to its callbacks; with AutoResize a resize gives the framebuffer the new size. false once the window is closed.</summary>
        public bool Pump(int id)
        {
            var window = _windows.Get(id);
            ManagedWindow? state = null;
            Manager? beat = null;
            lock (_managersGate)
                if (_managed.TryGetValue(id, out var entry))
                {
                    if (entry.Manager.OwnsWindows) state = entry.State;
                    else if (entry.Manager.Started) beat = entry.Manager;
                }
            if (state == null)
            {
                if (beat != null) Interlocked.Exchange(ref beat.ServicePending, 0);   // the program runs a cycle: the next one may be asked for
                var result = window.Pump();
                Deliver(id, result?.Events);
                return result?.StillOpen == true && !_closeRequested.Contains(id);
            }
            Manager? owner;
            lock (_managersGate) owner = _managed.TryGetValue(id, out var current) ? current.Manager : null;
            if (owner != null) Interlocked.Exchange(ref owner.ServicePending, 0);   // the program runs a cycle: the next one may be posted
            var events = new List<IEvent>();
            while (state.Inbox.TryDequeue(out var e)) events.Add(e);
            if (window.AutoResize)
                foreach (var e in events)
                    if (e is ResizeEvent resize && Framebuffer.IsValidSize(resize.Width, resize.Height))
                        window.Framebuffer.Resize(resize.Width, resize.Height);
            Deliver(id, events);
            return !state.Closed && !_closeRequested.Contains(id);
        }

        /// <summary>The program's side of a cycle, second half: shows the framebuffer - here, or (a started manager) hands a copy to the manager's thread.</summary>
        public void Present(int id)
        {
            var window = _windows.Get(id);
            Manager? manager = null;
            ManagedWindow? state = null;
            lock (_managersGate) if (_managed.TryGetValue(id, out var entry) && entry.Manager.OwnsWindows) { manager = entry.Manager; state = entry.State; }
            if (state == null || manager == null) { window.Present(window.Framebuffer); return; }
            var fb = window.Framebuffer;
            fb.Resolve();
            Framebuffer? front;
            lock (manager.Gate) front = state.Front;
            if (front == null || front.Width != fb.Width || front.Height != fb.Height) front = new Framebuffer(fb.Width, fb.Height);
            lock (front) Array.Copy(fb.Pixels, front.Pixels, Math.Min(fb.Pixels.Length, front.Pixels.Length));
            lock (manager.Gate) { state.Front = front; state.FrontNew = true; }
        }

        /// <summary>A setting of the OS window: right away, or - when a manager's thread owns the window - on that thread.</summary>
        private void OnOwner(int id, Action<ConsoleWindow> change)
        {
            var window = _windows.Get(id);
            ManagedWindow? state = null;
            lock (_managersGate) if (_managed.TryGetValue(id, out var entry) && entry.Manager.OwnsWindows) state = entry.State;
            if (state == null) change(window);
            else state.Todo.Enqueue(change);
        }

        /// <summary>Stops the threads of all managers (the end of the program run) and closes their windows.</summary>
        public void StopManagers()
        {
            Manager[] all;
            lock (_managersGate) { all = _managers.Values.ToArray(); }
            foreach (var m in all)
            {
                if (!m.Started) continue;
                lock (_managersGate)
                    foreach (var entry in _managed.Where(e => e.Value.Manager == m).ToList()) { _managed.Remove(entry.Key); _hold?.Invoke(-1); }
                m.Stop = true;
                m.Thread!.Join(2000);
            }
            lock (_managersGate) _managers.Clear();
        }

        /// <summary>Convenience method: ticks ALL currently managed
        /// windows once and automatically cleans up every window
        /// that the user closed in the meantime (no
        /// manual follow-up with DestroyWindow needed). For a host
        /// with ONE central loop that, alongside the script VM, keeps any
        /// number of console windows open at the same time.</summary>
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
