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

        // Event queues of the windows that requested them via EnableEventQueue (polling style instead of a callback, see
        // NextEvent). Only filled as long as a queue is requested, so that a pure callback window accumulates nothing.
        private readonly Dictionary<int, Queue<IEvent>> _eventQueues = new();
        private const int MaxQueuedEvents = 4096;

        /// <param name="rendererFactory">Creates the renderer per window (default: SDL). Replaceable for tests and other backends.</param>
        public WindowManager(FramebufferManager framebuffers, Action<LambdaValue, Value[]> callbackRunner,
            Func<IFramebufferRenderer>? rendererFactory = null)
        {
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
            window.Dispose();
            return _windows.Destroy(id);
        }

        /// <summary>For continued use on the C# side - not part of the purely
        /// ID-based surface API.</summary>
        public ConsoleWindow GetWindow(int id) => _windows.Get(id);

        public bool GetAutoResize(int id) => _windows.Get(id).AutoResize;

        public void SetAutoResize(int id, bool enabled) => _windows.Get(id).AutoResize = enabled;

        public bool GetTouchMouse(int id) => _windows.Get(id).TouchMouse;

        public void SetTouchMouse(int id, bool enabled) => _windows.Get(id).TouchMouse = enabled;

        public bool GetVSync(int id) => _windows.Get(id).VSync;

        public void SetVSync(int id, bool enabled) => _windows.Get(id).VSync = enabled;

        /// <summary>A single cycle for ONE window (see
        /// ConsoleWindow.Tick) - returns false if the window was closed
        /// by the user (the caller should then usually
        /// follow up with DestroyWindow(id)).</summary>
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

            return result?.StillOpen == true;
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
