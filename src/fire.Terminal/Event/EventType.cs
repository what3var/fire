using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Terminal.Event
{
    public enum EventType
    {
        Unknown = 0,
        Close = 1,
        TextInput = 2,
        MouseDown = 8,
        MouseMove = 9,
        MouseMoveRelative = 10,
        MouseUp = 11,
        MouseScroll = 12,
        //MouseEnter = 13,
        //MouseLeave = 14,
        KeyDown = 24,
        KeyUp = 25,
    }

}
