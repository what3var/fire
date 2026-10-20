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
    /// and supplies the classes `Window` and `EventType` as fire source (the prelude of the standard package). Requires `graphics`
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
                // the two halves of a cycle (fire's WindowManager): events in, frame out
                ["Pump"] = args => Value.MakeBool(mgr.Pump((int)args[0].AsInt())),
                ["Present"] = args => { mgr.Present((int)args[0].AsInt()); return Value.MakeUndefined(); },
                // window managers
                ["MgrCreate"] = args => Value.MakeInt(mgr.CreateManager()),
                ["MgrAttach"] = args => Value.MakeBool(mgr.AttachToManager((int)args[0].AsInt(), (int)args[1].AsInt())),
                ["MgrDetach"] = args => Value.MakeBool(mgr.DetachFromManager((int)args[0].AsInt(), (int)args[1].AsInt())),
                ["MgrStart"] = args => args[1].Kind == ValueKind.Lambda && mgr.StartManager((int)args[0].AsInt(), (LambdaValue)args[1].AsLambda()) ? Value.MakeBool(true) : Value.MakeBool(false),
                ["MgrWait"] = args => { mgr.ManagerWait((int)args[0].AsInt(), (int)args[1].AsInt()); return Value.MakeUndefined(); },
                // a modal window of its manager is open: the input of this window is dropped (Window.Show(true))
                ["SetBlocked"] = args => { mgr.SetBlocked((int)args[0].AsInt(), args[1].AsBool()); return Value.MakeUndefined(); },
                // the program runs the cycle its manager's thread asked for: the next one may be asked for
                ["MgrServed"] = args => { mgr.ManagerServed((int)args[0].AsInt()); return Value.MakeUndefined(); },
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
                ["Pump"] = args => Value.MakeUndefined() /*STUB*/,
                ["Present"] = args => Value.MakeUndefined() /*STUB*/,
                ["MgrCreate"] = args => Value.MakeUndefined() /*STUB*/,
                ["MgrAttach"] = args => Value.MakeUndefined() /*STUB*/,
                ["MgrDetach"] = args => Value.MakeUndefined() /*STUB*/,
                ["MgrStart"] = args => Value.MakeUndefined() /*STUB*/,
                ["MgrWait"] = args => Value.MakeUndefined() /*STUB*/,
                ["SetBlocked"] = args => Value.MakeUndefined() /*STUB*/,
                ["MgrServed"] = args => Value.MakeUndefined() /*STUB*/,
            };
        }

    }
}
