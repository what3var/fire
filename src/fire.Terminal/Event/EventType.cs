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
        CloseRequest = 2,
        TextInput = 3,
        /// <summary>The size of the window has changed (the user drags the edge, maximise).</summary>
        Resize = 4,
        /// <summary>A finger touches the touchscreen / moves / lifts off (position in framebuffer pixels, see <see cref="TouchEvent"/>).</summary>
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
        /// <summary>Joystick (control stick, gamepad): an axis moves, a button is pressed/released, a hat (D-pad) changes, a device is plugged in/unplugged (see <see cref="JoystickEvent"/>).</summary>
        JoystickAxis = 32,
        JoystickButtonDown = 33,
        JoystickButtonUp = 34,
        JoystickHat = 35,
        JoystickAdded = 36,
        JoystickRemoved = 37,
    }

}
