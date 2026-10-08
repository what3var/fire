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
        /// <summary>Die Größe des Fensters hat sich geändert (der Nutzer zieht am Rand, Maximieren).</summary>
        Resize = 4,
        /// <summary>Ein Finger berührt den Touchscreen / bewegt sich / hebt ab (Position in Framebuffer-Pixeln, siehe <see cref="TouchEvent"/>).</summary>
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
        /// <summary>Joystick (Steuerknüppel, Gamepad): eine Achse bewegt sich, ein Knopf wird gedrückt/losgelassen, ein Coolie-Hat (Steuerkreuz) ändert sich, ein Gerät wird angesteckt/abgezogen (siehe <see cref="JoystickEvent"/>).</summary>
        JoystickAxis = 32,
        JoystickButtonDown = 33,
        JoystickButtonUp = 34,
        JoystickHat = 35,
        JoystickAdded = 36,
        JoystickRemoved = 37,
    }

}
