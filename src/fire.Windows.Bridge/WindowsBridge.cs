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
    /// The window bridge (`#import "windows"`): the SDL window in which a framebuffer of the graphics bridge (`#import "graphics"`) is shown -
    /// separate from `graphics`, so that a program for a platform without a window (embedded) imports only `graphics` and plugs in another
    /// output device (e.g. a display) instead. Registers the <see cref="WindowManager"/> as native functions (prefix <see cref="WindowPrefix"/>)
    /// and supplies the classes `Window` and `EventType` as fire source (<see cref="PreludeSource"/>). Requires `graphics`
    /// (<c>HandleUnavailableException</c> and `Framebuffer` come from there).
    /// </summary>
    public static class WindowsBridge
    {
        public const string WindowPrefix = "__GRPHWin";

        /// <summary>Invalid/failed creation (like <c>GraphicsBridge.InvalidHandle</c>).</summary>
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
                // IMPORTANT: always add new functions AT THE END, in BuildWindowFunctionStubs in the same order (index = position).
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
                ["SetTouchMouse"] = args =>
                {
                    mgr.SetTouchMouse((int)args[0].AsInt(), args[1].AsBool());
                    return Value.MakeUndefined();
                },
                ["GetTouchMouse"] = args => Value.MakeBool(mgr.GetTouchMouse((int)args[0].AsInt())),
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
                ["SetTouchMouse"] = args => Value.MakeUndefined() /*STUB*/,
                ["GetTouchMouse"] = args => Value.MakeUndefined() /*STUB*/,
            };
        }

        /// <summary>fire source of the classes `Window` and `EventType` - to be placed behind the prelude of `graphics` (see ImportedPreludes).</summary>
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

                // Fetches the events and shows the current content of the framebuffer. With VSync (the default) every tick waits for the
                // monitor's refresh (60 Hz = 16.7 ms): ideal for animations and wait loops (`while (win.Tick()) { ... }`),
                // but a drawing loop with one tick per pass then takes 256 x 16.7 ms = 4.3 s. Whoever draws a lot and only wants to
                // show now and then calls Tick less often or sets `win.VSync = false` (Tick then returns immediately).
                bool Tick() { return __GRPHWinTick(this.id) }

                bool VSync {
                    get { return __GRPHWinGetVSync(this.id) }
                    set { __GRPHWinSetVSync(this.id, value) }
                }

                // true: if the user drags the window to another size, the framebuffer gets exactly that size (as long as it is valid, see Framebuffer.Resize) - instead of its content
                // being stretched to the window (default: false, as before). The program receives the event EventType.Resize and redraws at the new size; the mouse positions
                // are then pixels of the framebuffer 1:1. (A UI.Root switches it on: `root.autoResize`.)
                bool AutoResize {
                    get { return __GRPHWinGetAutoResize(this.id) }
                    set { __GRPHWinSetAutoResize(this.id, value) }
                }

                // true (default): a finger on the touchscreen ALSO triggers mouse events (as SDL does by itself), so a program that only listens to the mouse can be operated with a finger.
                // false: only the touch events (EventType.TouchDown/TouchMove/TouchUp). A UI.Root switches it off and evaluates the fingers itself.
                bool TouchMouse {
                    get { return __GRPHWinGetTouchMouse(this.id) }
                    set { __GRPHWinSetTouchMouse(this.id, value) }
                }

                // Polling style instead of callbacks: EnableEvents() switches a queue on, after which, following each tick,
                // NextEvent() fetches one event after the other (undefined when none is left). The event is an
                // array: e[0] is the type (see EventType), the rest depends on the type, positions are whole pixels of the
                // framebuffer: MouseDown/MouseUp [type, button, x, y], MouseMove [type, x, y, buttons], MouseScroll [type, scrollX,
                // scrollY, x, y], KeyDown/KeyUp [type, keycode, scancode, modifier, repeated], TextInput [type, text], Resize [type, width, height] (the size of the window; with AutoResize the
                // framebuffer already has it if it is valid), Close [type]. Touchscreen: TouchDown/TouchMove/TouchUp [type, finger, x, y, pressure] (pixels of the framebuffer; finger
                // distinguishes several fingers; pressure 0 to 1). Joystick: JoystickAxis [type, joystick, axis, position] (-1 to 1), JoystickButtonDown/JoystickButtonUp [type, joystick, button],
                // JoystickHat [type, joystick, hat, directions] (bit mask: 1 up, 2 right, 4 down, 8 left; 0 centre), JoystickAdded/JoystickRemoved [type, joystick] (plugged in/unplugged).
                // Processing thus happens in the main program - with the real global variables, not the isolated copy of a
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

                // Touchscreen: (finger, x, y, druck)
                bool RegisterTouchDown(lambda<int,float,float,float> fn) { return __GRPHWinRegisterEvent(this.id, EventType.TouchDown!, fn); }
                bool RegisterTouchMove(lambda<int,float,float,float> fn) { return __GRPHWinRegisterEvent(this.id, EventType.TouchMove!, fn); }
                bool RegisterTouchUp(lambda<int,float,float,float> fn) { return __GRPHWinRegisterEvent(this.id, EventType.TouchUp!, fn); }

                // Joystick: (joystick, axis, position), (joystick, button), (joystick, hat, directions), (joystick)
                bool RegisterJoystickAxis(lambda<int,int,float> fn) { return __GRPHWinRegisterEvent(this.id, EventType.JoystickAxis!, fn); }
                bool RegisterJoystickButtonDown(lambda<int,int> fn) { return __GRPHWinRegisterEvent(this.id, EventType.JoystickButtonDown!, fn); }
                bool RegisterJoystickButtonUp(lambda<int,int> fn) { return __GRPHWinRegisterEvent(this.id, EventType.JoystickButtonUp!, fn); }
                bool RegisterJoystickHat(lambda<int,int,int> fn) { return __GRPHWinRegisterEvent(this.id, EventType.JoystickHat!, fn); }
                bool RegisterJoystickAdded(lambda<int> fn) { return __GRPHWinRegisterEvent(this.id, EventType.JoystickAdded!, fn); }
                bool RegisterJoystickRemoved(lambda<int> fn) { return __GRPHWinRegisterEvent(this.id, EventType.JoystickRemoved!, fn); }

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
                TouchDown = 16,
                TouchMove = 17,
                TouchUp = 18,
                MouseDown = 8,
                MouseMove = 9,
                MouseMoveRelative = 10,
                MouseUp = 11,
                MouseScroll = 12,
                //MouseEnter = 13,
                //MouseLeave = 14,
                KeyDown = 24,
                KeyUp = 25,
                JoystickAxis = 32,
                JoystickButtonDown = 33,
                JoystickButtonUp = 34,
                JoystickHat = 35,
                JoystickAdded = 36,
                JoystickRemoved = 37
            }
            """;
    }
}
