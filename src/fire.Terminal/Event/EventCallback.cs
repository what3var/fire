using fire.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Terminal.Event
{
    public class EventCallback
    {
        public LambdaValue Callback { get; init; }

        public int WindowHandle { get; init; }

        public EventType EventType { get; init; }

        public static bool CheckParameters(LambdaValue callback, EventType type)
        {
            switch (type)
            {
                case EventType.KeyDown:
                case EventType.KeyUp:
                case EventType.MouseScroll:
                    return callback.Proto.ParamCount == 4;
                case EventType.MouseMoveRelative:
                case EventType.MouseMove:
                case EventType.MouseUp:
                case EventType.MouseDown:
                    return callback.Proto.ParamCount == 3;
                case EventType.TextInput:
                    return callback.Proto.ParamCount == 1;
                case EventType.Resize:
                case EventType.JoystickButtonDown:
                case EventType.JoystickButtonUp:
                    return callback.Proto.ParamCount == 2;
                case EventType.TouchDown:
                case EventType.TouchMove:
                case EventType.TouchUp:
                    return callback.Proto.ParamCount == 4;
                case EventType.JoystickAxis:
                case EventType.JoystickHat:
                    return callback.Proto.ParamCount == 3;
                case EventType.JoystickAdded:
                case EventType.JoystickRemoved:
                    return callback.Proto.ParamCount == 1;
                default:
                    return callback.Proto.ParamCount == 0;
            }
        }
    }
}
